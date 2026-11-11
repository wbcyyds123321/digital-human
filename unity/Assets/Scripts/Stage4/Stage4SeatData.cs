using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;
using Yixuan.World;
using Yixuan.SelectionLab;

namespace Yixuan.Stage4
{
    [Serializable]
    public sealed class Stage4SeatFrame
    {
        public float seat_yaw_deg;
        public float[] front_unity;
        public float[] right_unity;
        public float[] up_unity;
    }

    [Serializable]
    public sealed class Stage4SeatSurface
    {
        public float[] surface_point_unity;
        public float surface_y_unity;
        public float ground_y_unity;
        public float height_above_ground_m;
        public float[] top_bounds_unity_xz_flat;
        public float top_width_m;
        public float top_depth_m;
    }

    [Serializable]
    public sealed class Stage4SeatApproach
    {
        public float[] position_unity;
        public float yaw_deg;
        public float ground_y_unity;
        public float clearance_m;
        public float agent_radius_m;
        public float safety_margin_m;
        public float navigation_padding_m;
        public string method;
    }

    [Serializable]
    public sealed class Stage4ApproachCandidate
    {
        public string side;
        public float[] position_unity;
        public float yaw_deg;
        public float clearance_m;
    }

    [Serializable]
    public sealed class Stage4SeatSource
    {
        public string source_group;
        public string[] source_objects;
        public string[] seat_objects;
    }

    [Serializable]
    public sealed class Stage4SeatNavInfo
    {
        public float[] blocking_bounds_unity;
        public bool nav_obstacle_covered;
    }

    [Serializable]
    public sealed class Stage4SeatEntry
    {
        public string entity_id;
        public string entity_class;
        public string display_name;
        public bool usable_for_interaction;
        public string geometry_status;
        public Stage4SeatSource source;
        public Stage4SeatFrame frame;
        public Stage4SeatSurface seat;
        public Stage4SeatApproach approach;
        public Stage4ApproachCandidate[] approach_candidates;
        public Stage4SeatNavInfo nav;

        public Vector3 SeatPoint { get { return ToVector(seat != null ? seat.surface_point_unity : null); } }
        public Vector3 ApproachPoint { get { return ToVector(approach != null ? approach.position_unity : null); } }
        public float SeatYaw { get { return frame != null ? frame.seat_yaw_deg : 180f; } }
        public float ApproachYaw { get { return approach != null ? approach.yaw_deg : 0f; } }
        public float Padding
        {
            get { return approach != null && approach.navigation_padding_m > 0f ? approach.navigation_padding_m : 0.05f; }
        }

        static Vector3 ToVector(float[] values)
        {
            return values != null && values.Length >= 3
                ? new Vector3(values[0], values[1], values[2])
                : Vector3.zero;
        }
    }

    [Serializable]
    public sealed class Stage4SeatExclusion
    {
        public string entity_id;
        public string status;
        public string[] errors;
        public string gate_status;
    }

    [Serializable]
    sealed class Stage4SeatDocument
    {
        public string schema;
        public string data_version;
        public string map_version;
        public string blend_sha256;
        public Stage4SeatEntry[] entries;
        public Stage4SeatExclusion[] excluded;
    }

    [Serializable]
    sealed class Stage4NavEntry
    {
        public string entity_id;
        public float[] blocking_bounds_unity;
        public bool nav_obstacle_covered;
    }

    [Serializable]
    sealed class Stage4NavDocument
    {
        public string schema;
        public string source_world_entities_sha256;
        public string blend_sha256;
        public Stage4NavEntry[] targets;
    }

    [Serializable]
    sealed class Stage4Manifest
    {
        public string schema;
        public string data_version;
        public string map_version;
        public string source_path;
        public string source_sha256;
        public string copy_path;
        public string copy_sha256;
        public string nav_path;
        public string nav_sha256;
        public string exclusions_path;
        public string exclusions_sha256;
        public string world_entities_sha256;
        public string blend_sha256;
    }

    [Serializable]
    public sealed class Stage4Calibration
    {
        public string schema;
        public string character;
        public string action_id;
        public string clip_name;
        public string pivot_bone;
        public float hips_above_seat_m;
        public float forward_offset_m;
        public float snap_seconds;
        public float approach_navigation_padding_m;
        public string source_note;
    }

    /// <summary>
    /// Universal Stage-4 seat data loader (seats_v1 package).
    ///
    /// The old loader only accepted a hard-coded two-bench sidecar and aborted
    /// the whole load if any entry was not verified. The v1 package is built from
    /// the repaired bench generator: every geometry-verified bench is usable, the
    /// per-bench exclusion list keeps the reasons for the rest, and bench AABBs
    /// already covered by the world_map obstacle layer are not registered twice.
    /// </summary>
    public static class Stage4SeatData
    {
        const string ResourceRoot = "SovietBlueHour/seats_v1/";
        const string CalibrationResource = "SovietBlueHour/stage4/seat_calibration";

        public static bool Loaded { get; private set; }
        public static string Error { get; private set; }
        public static Stage4SeatEntry[] Entries { get; private set; } = new Stage4SeatEntry[0];
        public static Stage4SeatEntry[] AllEntries { get; private set; } = new Stage4SeatEntry[0];
        public static Stage4SeatExclusion[] Excluded { get; private set; } = new Stage4SeatExclusion[0];
        public static Stage4Calibration Calibration { get; private set; }
        public static string LoadLog { get; private set; } = "";
        public static string MapVersion { get; private set; } = "";

        static readonly List<Bounds> BenchBoundsCache = new List<Bounds>();
        static bool _registerAllBenchBounds;
        public static int BenchBoundsRegistered { get; private set; }
        public static int BenchBoundsCoveredElsewhere { get; private set; }
        static readonly Dictionary<string, Stage4SeatEntry> ById =
            new Dictionary<string, Stage4SeatEntry>(StringComparer.Ordinal);

        /// <summary>
        /// Static seat data is a Soviet-map concern. Defer it until the active
        /// scene is known and never install it in the explicit whitebox lab.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void BootAfterScene()
        {
            if (SelectionLabContext.IsActive) return;
            EnsureLoaded();
            RegisterNavigationProvider();
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetSession()
        {
            Loaded = false;
            Error = null;
            Entries = new Stage4SeatEntry[0];
            AllEntries = new Stage4SeatEntry[0];
            Excluded = new Stage4SeatExclusion[0];
            Calibration = null;
            LoadLog = "";
            MapVersion = "";
            BenchBoundsCache.Clear();
            ById.Clear();
        }

        /// <summary>Clear static seat data when an explicit non-Soviet scene owns the runtime.</summary>
        public static void ResetForExplicitScene()
        {
            MapWorldProfile.UnregisterExternalObstacleProvider("stage4.bench_bounds");
            Loaded = false;
            Error = null;
            Entries = new Stage4SeatEntry[0];
            AllEntries = new Stage4SeatEntry[0];
            Excluded = new Stage4SeatExclusion[0];
            Calibration = null;
            LoadLog = "";
            MapVersion = "";
            BenchBoundsCache.Clear();
            ById.Clear();
            BenchBoundsRegistered = 0;
            BenchBoundsCoveredElsewhere = 0;
        }

        public static void EnsureLoaded()
        {
            if (Loaded) return;
            if (SelectionLabContext.IsActive)
            {
                Loaded = false;
                Error = "skipped_by_explicit_scene";
                return;
            }
            Error = null;
            LoadLog = "";
            try
            {
                var manifestAsset = Resources.Load<TextAsset>(ResourceRoot + "manifest");
                var dataAsset = Resources.Load<TextAsset>(ResourceRoot + "interaction_geometry.v1");
                var navAsset = Resources.Load<TextAsset>(ResourceRoot + "bench_navigation.v1");
                var exclusionsAsset = Resources.Load<TextAsset>(ResourceRoot + "exclusions.v1");
                var calibrationAsset = Resources.Load<TextAsset>(CalibrationResource);
                if (manifestAsset == null || dataAsset == null || navAsset == null)
                {
                    Error = "Stage-4 v1 Resources 数据缺失（manifest / interaction_geometry.v1 / bench_navigation.v1）";
                    return;
                }
                var manifest = JsonUtility.FromJson<Stage4Manifest>(manifestAsset.text);
                if (manifest == null) { Error = "Stage-4 v1 manifest 解析失败"; return; }
                string dataHash = Sha256(dataAsset.text);
                string navHash = Sha256(navAsset.text);
                if (!string.IsNullOrEmpty(manifest.copy_sha256) && dataHash != manifest.copy_sha256)
                {
                    Error = "Stage-4 v1 座位副本哈希不匹配:" + dataHash;
                    return;
                }
                if (!string.IsNullOrEmpty(manifest.nav_sha256) && navHash != manifest.nav_sha256)
                {
                    Error = "Stage-4 v1 导航数据哈希不匹配:" + navHash;
                    return;
                }
                if (!VerifySourceInEditor(manifest))
                {
                    return;
                }
                var doc = JsonUtility.FromJson<Stage4SeatDocument>(dataAsset.text);
                var nav = JsonUtility.FromJson<Stage4NavDocument>(navAsset.text);
                if (doc == null || doc.entries == null || doc.entries.Length == 0)
                {
                    Error = "Stage-4 v1 没有可用座位 entries";
                    return;
                }
                if (nav == null || nav.targets == null || nav.targets.Length != doc.entries.Length)
                {
                    Error = "Stage-4 v1 导航 target 数量与座位 entries 不一致";
                    return;
                }
                var navById = new Dictionary<string, Stage4NavEntry>(StringComparer.Ordinal);
                foreach (var target in nav.targets)
                {
                    if (target == null || string.IsNullOrEmpty(target.entity_id)) continue;
                    navById[target.entity_id] = target;
                }
                var usable = new List<Stage4SeatEntry>();
                var bounds = new List<Bounds>();
                ById.Clear();
                foreach (var entry in doc.entries)
                {
                    if (entry == null || string.IsNullOrEmpty(entry.entity_id))
                    {
                        Error = "Stage-4 v1 出现空 entry";
                        return;
                    }
                    if (!entry.usable_for_interaction || entry.geometry_status != "verified")
                    {
                        Error = "Stage-4 v1 包内出现未验证 entry：" + entry.entity_id;
                        return;
                    }
                    Stage4NavEntry target;
                    if (!navById.TryGetValue(entry.entity_id, out target))
                    {
                        Error = "Stage-4 v1 缺少导航 bbox：" + entry.entity_id;
                        return;
                    }
                    entry.nav = new Stage4SeatNavInfo
                    {
                        blocking_bounds_unity = target.blocking_bounds_unity,
                        nav_obstacle_covered = target.nav_obstacle_covered,
                    };
                    float[] b = target.blocking_bounds_unity;
                    if (b != null && b.Length >= 6)
                    {
                        bounds.Add(new Bounds(
                            new Vector3((b[0] + b[3]) * 0.5f, (b[1] + b[4]) * 0.5f, (b[2] + b[5]) * 0.5f),
                            new Vector3(Mathf.Abs(b[3] - b[0]), Mathf.Abs(b[4] - b[1]), Mathf.Abs(b[5] - b[2]))));
                    }
                    usable.Add(entry);
                    ById[entry.entity_id] = entry;
                }
                Entries = usable.ToArray();
                AllEntries = Entries;
                if (exclusionsAsset != null)
                {
                    var exclusions = JsonUtility.FromJson<ExclusionsDocument>(exclusionsAsset.text);
                    Excluded = exclusions != null && exclusions.entries != null
                        ? exclusions.entries : new Stage4SeatExclusion[0];
                }
                BenchBoundsCache.Clear();
                int covered = 0;
                // The world_map obstacle layer already carries per-entity bench
                // AABBs. Registering them a second time via the Stage-4 provider
                // inflates the same bench twice (1.0 m extra on every side). Keep
                // one owner: Stage-4 registers only benches the obstacle layer does
                // not cover; if that layer is unavailable, fall back to registering
                // all benches so walkability stays safe.
                _registerAllBenchBounds = !WorldMapObstacleLayerAvailable();
                for (int i = 0; i < AllEntries.Length && i < bounds.Count; i++)
                {
                    bool coveredElsewhere = !_registerAllBenchBounds &&
                                            AllEntries[i].nav != null &&
                                            AllEntries[i].nav.nav_obstacle_covered;
                    if (coveredElsewhere) { covered++; continue; }
                    BenchBoundsCache.Add(bounds[i]);
                }
                BenchBoundsCoveredElsewhere = covered;
                BenchBoundsRegistered = BenchBoundsCache.Count;
                Calibration = calibrationAsset != null ? JsonUtility.FromJson<Stage4Calibration>(calibrationAsset.text) : null;
                if (Calibration == null)
                {
                    Error = "Stage-4 缺少角色动作校准配置";
                    return;
                }
                MapVersion = doc.map_version;
                Loaded = true;
                LoadLog = "entries=" + Entries.Length + " excluded=" + Excluded.Length +
                          " map=" + doc.map_version + " data=" + dataHash.Substring(0, 12) +
                          " nav=" + navHash.Substring(0, 12) + " calibration=" +
                          Calibration.action_id + "/" + Calibration.clip_name +
                          " bench_bounds_registered=" + BenchBoundsRegistered +
                          " covered_elsewhere=" + BenchBoundsCoveredElsewhere +
                          " fallback_all=" + _registerAllBenchBounds;
                Debug.Log("[Stage4SeatData] 载入成功：" + LoadLog);
            }
            catch (Exception e)
            {
                Error = "Stage-4 v1 数据载入异常：" + e.Message;
                Debug.LogError("[Stage4SeatData] " + Error);
            }
        }

        [Serializable]
        sealed class ExclusionsDocument
        {
            public string schema;
            public Stage4SeatExclusion[] entries;
            public int count;
        }

        static bool WorldMapObstacleLayerAvailable()
        {
            return Resources.Load<UnityEngine.TextAsset>("SovietBlueHour/world_map/nav_obstacles") != null &&
                   Yixuan.WorldMap.WorldMapData.Load();
        }

        static bool VerifySourceInEditor(Stage4Manifest manifest)
        {
#if UNITY_EDITOR
            try
            {
                if (string.IsNullOrEmpty(manifest.source_path) || string.IsNullOrEmpty(manifest.source_sha256))
                    return true;
                string projectRoot = Path.GetDirectoryName(Application.dataPath);
                string source = Path.Combine(projectRoot ?? "", manifest.source_path ?? "");
                if (File.Exists(source))
                {
                    string sourceHash = SHA256(File.ReadAllBytes(source));
                    if (sourceHash != manifest.source_sha256)
                    {
                        Error = "Stage-4 v1 源参数文件哈希已变化：" + sourceHash;
                        return false;
                    }
                }
            }
            catch (Exception e)
            {
                Error = "Stage-4 v1 源参数校验异常：" + e.Message;
                return false;
            }
#endif
            return true;
        }

        public static Stage4SeatEntry Get(string id)
        {
            EnsureLoaded();
            if (string.IsNullOrEmpty(id)) return null;
            Stage4SeatEntry entry;
            return ById.TryGetValue(id, out entry) ? entry : null;
        }

        public static Stage4SeatExclusion GetExclusion(string id)
        {
            EnsureLoaded();
            if (Excluded == null) return null;
            foreach (var exclusion in Excluded)
            {
                if (exclusion != null && exclusion.entity_id == id) return exclusion;
            }
            return null;
        }

        public static List<Bounds> GetBenchBounds()
        {
            EnsureLoaded();
            return new List<Bounds>(BenchBoundsCache);
        }

        /// <summary>
        /// Resolve the approach point for one bench. Candidates are tried in
        /// front/right/left/back order; a candidate is accepted when its point is
        /// walkable and (optionally) reachable with a real path from `from`.
        /// This replaces the old single hard-coded front approach and lets benches
        /// whose front is blocked be used from a side without shrinking obstacles.
        /// </summary>
        public static bool ResolveApproach(Stage4SeatEntry entry, Vector3 from, bool requirePath,
                                           out Vector3 approach, out string reason)
        {
            approach = Vector3.zero;
            reason = "";
            if (entry == null) { reason = "null_entry"; return false; }
            float padding = entry.Padding;
            var tried = new List<string>();
            if (entry.approach_candidates != null)
            {
                foreach (var candidate in entry.approach_candidates)
                {
                    if (candidate == null || candidate.position_unity == null ||
                        candidate.position_unity.Length < 3) continue;
                    Vector3 point = new Vector3(candidate.position_unity[0],
                                                candidate.position_unity[1],
                                                candidate.position_unity[2]);
                    Vector3 outward = point - entry.SeatPoint;
                    outward.y = 0f;
                    if (outward.sqrMagnitude < 1e-6f) continue;
                    point += outward.normalized * padding;
                    point.y = WorldData.GroundHeightAt(point);
                    if (!WorldData.IsWalkable(point))
                    {
                        tried.Add(candidate.side + ":unwalkable");
                        continue;
                    }
                    if (requirePath)
                    {
                        var path = WorldData.FindPath(from, point);
                        if (path == null || path.Count == 0)
                        {
                            tried.Add(candidate.side + ":no_path(" + (WorldData.LastError ?? "?") + ")");
                            continue;
                        }
                    }
                    approach = point;
                    reason = "side=" + candidate.side + " padding=" + padding.ToString("F2");
                    return true;
                }
            }
            // legacy fallback: the package always writes candidates, but keep a
            // safe path for hand-edited data.
            Vector3 fallback = entry.ApproachPoint;
            if (fallback != Vector3.zero)
            {
                Vector3 outward = fallback - entry.SeatPoint;
                outward.y = 0f;
                if (outward.sqrMagnitude > 1e-6f)
                {
                    fallback += outward.normalized * padding;
                    fallback.y = WorldData.GroundHeightAt(fallback);
                    if (WorldData.IsWalkable(fallback))
                    {
                        if (!requirePath || (WorldData.FindPath(from, fallback) != null &&
                                             WorldData.FindPath(from, fallback).Count > 0))
                        {
                            approach = fallback;
                            reason = "legacy_front";
                            return true;
                        }
                    }
                }
            }
            reason = "no usable approach (" + string.Join(", ", tried.ToArray()) + ")";
            return false;
        }

        /// <summary>Runtime availability snapshot used by the big map / acceptance reports.</summary>
        public static bool TryCheckReachable(Stage4SeatEntry entry, Vector3 from, out string detail)
        {
            Vector3 approach;
            string reason;
            detail = "";
            if (ResolveApproach(entry, from, true, out approach, out reason))
            {
                detail = "reachable via " + reason;
                return true;
            }
            detail = reason;
            return false;
        }

        /// <summary>
        /// Called synchronously by MapWorldProfile before its first obstacle
        /// collection, so a cold main-scene build sees seat bounds even though
        /// the old BeforeSceneLoad boot has been removed.
        /// </summary>
        public static void PrepareForMapBuild()
        {
            if (SelectionLabContext.IsActive)
            {
                ResetForExplicitScene();
                return;
            }
            EnsureLoaded();
            RegisterNavigationProvider();
        }

        public static void RegisterNavigationProvider()
        {
            MapWorldProfile.RegisterExternalObstacleProvider("stage4.bench_bounds", GetBenchBounds);
            // Match collision.json avoidance: Expand adds this to full size, so
            // 2*(radius+margin) = 1.0 leaves 0.5 m clear on each side.
            MapWorldProfile.ExternalObstacleInflation = 2f * (WorldData.AgentRadius + WorldData.Margin);
        }

        static string Sha256(string text)
        {
            return SHA256(Encoding.UTF8.GetBytes(text ?? ""));
        }

        static string SHA256(byte[] bytes)
        {
            using (var sha = System.Security.Cryptography.SHA256.Create())
            {
                var hash = sha.ComputeHash(bytes);
                var sb = new StringBuilder(hash.Length * 2);
                foreach (byte value in hash) sb.Append(value.ToString("x2"));
                return sb.ToString();
            }
        }
    }
}
