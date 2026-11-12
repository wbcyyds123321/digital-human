using System;
using System.Collections.Generic;
using UnityEngine;

namespace Yixuan.Pick
{
    /// <summary>
    /// Shared data types for the first-person selection round (2026-11-10).
    /// Node A: generic entity resolution on the existing map, a real per-pixel
    /// entity ID image rendered from the current EyeCamera, a shared confirmed
    /// selection record and a single-entity highlight.
    ///
    /// Design rules:
    ///  - stable identity is always the world entity catalog id string
    ///    (entity/prop_bench_00 etc.); the 24-bit runtime code below is only a
    ///    per-session render encoding and must never be persisted as identity;
    ///  - candidates are not confirmed selections;
    ///  - every frame record carries pose / projection / viewport / frame id so
    ///    an old image can never be applied to a newer camera pose.
    /// </summary>
    public enum PickSourceKind
    {
        ManualClick = 0,
        AiConfirm = 1,
        MapClick = 2,
        Reuse = 3,
        InjectedTest = 4,
    }

    /// <summary>Frozen camera description shared by RGB / ID / depth / ray.</summary>
    [Serializable]
    public sealed class PickCameraSnapshot
    {
        public long frame_id;
        public int frame_count;
        public double time_since_startup;
        public string source = "character_eye";

        public Vector3 position;
        public Quaternion rotation = Quaternion.identity;
        public float fov = 60f;
        public float near = 0.05f;
        public float far = 900f;
        public float aspect = 1f;
        public int width;
        public int height;
        public float viewport_x;
        public float viewport_y;
        public float viewport_w = 1f;
        public float viewport_h = 1f;

        public Matrix4x4 view;
        public Matrix4x4 projection_gl;
        public Matrix4x4 projection_gpu;

        public Vector3 Forward { get { return rotation * Vector3.forward; } }

        public Vector3 ProjectWorldToViewport(Vector3 world, out float depth01)
        {
            depth01 = 0f;
            Vector4 clip = projection_gpu * (view * new Vector4(world.x, world.y, world.z, 1f));
            if (clip.w <= 1e-5f) return new Vector3(-1f, -1f, -1f);
            Vector3 ndc = new Vector3(clip.x / clip.w, clip.y / clip.w, clip.z / clip.w);
            // Frame pixels/UVs are TOP-origin: the ID/depth readback rows are
            // top-down (D3D render-target memory order), unlike the RGB clone
            // camera texture. CodeAt/DepthAt index rows directly, so u/v are
            // top-origin too.
            depth01 = Mathf.Clamp01((ndc.z + 1f) * 0.5f);
            return new Vector3(ndc.x * 0.5f + 0.5f, 0.5f - ndc.y * 0.5f, ndc.z);
        }
    }

    /// <summary>One resolved screen pixel -> entity hit.</summary>
    public sealed class PickHit
    {
        public EntityPickEntry entity;
        public int code;
        public int pixel_x;
        public int pixel_y;
        public float u;
        public float v;
        public float distance_m;
        public Vector3 surface_point;
        public Vector3 eye_position;
        public Vector3 eye_forward;
        public bool is_sky;
        public PickCameraSnapshot snapshot;

        public string EntityId { get { return entity != null ? entity.Id : ""; } }
        public string EntityName { get { return entity != null ? entity.DisplayName : ""; } }

        public bool Valid { get { return entity != null && !is_sky; } }
    }

    /// <summary>
    /// One captured frame: same-frame RGB + per-pixel entity code + linear depth.
    /// id_pixels[i] is the encoded Color32 of the runtime code; codes[i] is the
    /// decoded 24-bit code (0 = no selectable entity). Pixel index = y*width + x,
    /// (0,0) is bottom-left, matching Unity texture convention.
    /// </summary>
    public sealed class PickFrame
    {
        public PickCameraSnapshot snapshot;
        public int width;
        public int height;
        public Color32[] id_pixels;
        public int[] codes;
        public float[] depth01;
        /// <summary>Raw RGBA8 channel bytes of the encoded linear depth (diagnostics only).</summary>
        public Color32[] depth_rgba;
        public Color32[] rgb_pixels;
        public byte[] rgb_png;
        public string error = "";
        public long FrameId { get { return snapshot != null ? snapshot.frame_id : -1; } }
        public bool Ok { get { return string.IsNullOrEmpty(error) && codes != null; } }

        public int CodeAt(int x, int y)
        {
            if (codes == null || x < 0 || y < 0 || x >= width || y >= height) return 0;
            return codes[y * width + x];
        }

        public float DepthAt(int x, int y)
        {
            if (depth01 == null || x < 0 || y < 0 || x >= width || y >= height) return 0f;
            return depth01[y * width + x];
        }

        public int CodeAtUv(float u, float v)
        {
            int x = Mathf.Clamp(Mathf.FloorToInt(u * width), 0, width - 1);
            int y = Mathf.Clamp(Mathf.FloorToInt(v * height), 0, height - 1);
            return CodeAt(x, y);
        }

        public Vector3 WorldPointAt(float u, float v, out float depth01)
        {
            depth01 = DepthAtUv(u, v);
            if (depth01 <= 0f) return Vector3.zero;
            Vector3 origin;
            Vector3 direction;
            if (!ViewportRay(u, v, out origin, out direction)) return Vector3.zero;
            // PickId.shader encodes -viewZ (view-axis depth) linearly between
            // near and far. The world-space distance along the centre ray is
            // eyeDepth / cos(ray, forward); treating view-axis depth as a slant
            // range was the R2 off-axis bug (30deg / 10m -> 1.547m error).
            float eyeDepth = snapshot.near + depth01 * (snapshot.far - snapshot.near);
            float cosine = Vector3.Dot(direction, snapshot.Forward);
            if (cosine <= 1e-4f) return Vector3.zero;
            return origin + direction * (eyeDepth / cosine);
        }

        /// <summary>
        /// Frozen-snapshot viewport ray for a (u,v) pixel centre. u/v and the
        /// pixel rows are TOP-origin, matching the ID/depth readback row order
        /// (row 0 = top). A_r1 wrongly assumed bottom-left viewport rows, which
        /// only affected off-centre pixels (centre rows are invariant).
        /// </summary>
        public bool ViewportRay(float u, float v, out Vector3 origin, out Vector3 direction)
        {
            origin = snapshot != null ? snapshot.position : Vector3.zero;
            direction = snapshot != null ? snapshot.Forward : Vector3.forward;
            if (snapshot == null) return false;
            Matrix4x4 viewProjectionGpu = snapshot.projection_gpu * snapshot.view;
            if (Mathf.Abs(viewProjectionGpu.determinant) < 1e-12f) return false;
            Matrix4x4 inverse = viewProjectionGpu.inverse;
            var ndc = new Vector4(u * 2f - 1f, 1f - v * 2f, 1f, 1f);
            var farClip = inverse * ndc;
            if (Mathf.Abs(farClip.w) < 1e-8f) return false;
            Vector3 farPoint = new Vector3(farClip.x / farClip.w, farClip.y / farClip.w, farClip.z / farClip.w);
            Vector3 delta = farPoint - origin;
            if (delta.sqrMagnitude < 1e-12f) return false;
            direction = delta.normalized;
            return true;
        }

        public float DepthAtUv(float u, float v)
        {
            int x = Mathf.Clamp(Mathf.FloorToInt(u * width), 0, width - 1);
            int y = Mathf.Clamp(Mathf.FloorToInt(v * height), 0, height - 1);
            return DepthAt(x, y);
        }

        public bool ContainsCode(int code, out int count)
        {
            count = 0;
            if (codes == null || code == 0) return false;
            for (int i = 0; i < codes.Length; i++) if (codes[i] == code) count++;
            return count > 0;
        }

        public Dictionary<int, int> HistogramInBox(Rect uvBox)
        {
            var histogram = new Dictionary<int, int>();
            int x0 = Mathf.Clamp(Mathf.FloorToInt(uvBox.xMin * width), 0, width - 1);
            int x1 = Mathf.Clamp(Mathf.CeilToInt(uvBox.xMax * width) - 1, 0, width - 1);
            int y0 = Mathf.Clamp(Mathf.FloorToInt(uvBox.yMin * height), 0, height - 1);
            int y1 = Mathf.Clamp(Mathf.CeilToInt(uvBox.yMax * height) - 1, 0, height - 1);
            for (int y = y0; y <= y1; y++)
            {
                int row = y * width;
                for (int x = x0; x <= x1; x++)
                {
                    int code = codes[row + x];
                    if (code == 0) continue;
                    int old;
                    histogram.TryGetValue(code, out old);
                    histogram[code] = old + 1;
                }
            }
            return histogram;
        }

        /// <summary>Resolve a pixel to a hit using the frozen snapshot.</summary>
        public PickHit ResolveUv(float u, float v)
        {
            int x = Mathf.Clamp(Mathf.FloorToInt(u * width), 0, width - 1);
            int y = Mathf.Clamp(Mathf.FloorToInt(v * height), 0, height - 1);
            int code = CodeAt(x, y);
            float depth = depth01 != null ? depth01[y * width + x] : 0f;
            var hit = new PickHit
            {
                code = code,
                pixel_x = x,
                pixel_y = y,
                u = (x + 0.5f) / width,
                v = (y + 0.5f) / height,
                snapshot = snapshot,
                eye_position = snapshot != null ? snapshot.position : Vector3.zero,
                eye_forward = snapshot != null ? snapshot.Forward : Vector3.forward,
                is_sky = depth <= 0f,
                entity = code != 0 ? EntityPickRegistry.GetByCode(code) : null,
            };
            if (depth > 0f) hit.surface_point = WorldPointAt(hit.u, hit.v, out depth);
            hit.distance_m = depth > 0f
                ? Vector3.Distance(snapshot.position, hit.surface_point)
                : 0f;
            return hit;
        }
    }

    /// <summary>A candidate region from the ID image. Never a confirmed selection.</summary>
    public sealed class PickBoxCandidate
    {
        public string entity_id = "";
        public string name = "";
        public string cls = "";
        public int pixel_count;
        public float area_fraction;
        public Vector2 centroid_uv;
        public Vector3 surface_point;
        public float distance_m;
        // Round-1 reliability fields: the representative point is valid only when
        // the frozen frame at that exact pixel reports this same entity id with a
        // valid depth. A foreign/background pixel is never reported as this entity.
        public bool has_reliable_point;
        public int sample_pixel_x = -1;
        public int sample_pixel_y = -1;
        public int sample_code;
        public float sample_depth01;
        public string point_note = "";
    }

    public sealed class PickBoxResult
    {
        public bool ok;
        public bool ambiguous;
        public string note = "";
        public Rect uv_box;
        public int generation;
        public bool dominant_is_confirmed = false;   // box candidates never publish a ConfirmedSelection
        public readonly List<PickBoxCandidate> candidates = new List<PickBoxCandidate>();
        public PickBoxCandidate Dominant { get { return candidates.Count > 0 ? candidates[0] : null; } }
    }

    /// <summary>
    /// Live re-sampling of the current confirmed selection (node A round 1 R5).
    /// The SelectionRecord stays an immutable observation record; current facts
    /// (entity/character/eye) are recomputed on demand so a moved vehicle or a
    /// walking character cannot make the LLM/task facts stale.
    /// </summary>
    public sealed class SelectionLiveFacts
    {
        public bool available;
        public string reason = "";
        public string entity_id = "";
        public string display_name = "";
        public string cls = "";
        public string capabilities = "";
        public string source = "";
        public int generation;
        public long observed_frame_id;
        public double observed_time;
        public Vector3 observed_position;
        public Vector3 observed_surface_point;
        public Vector3 current_position;
        public bool dynamic;
        public string position_source = "";
        public Vector3 character_position;
        public float body_yaw_deg;
        public Vector3 eye_position;
        public Vector3 eye_forward;
        public float distance_m;
        public float bearing_deg;
        public float pitch_deg;
        public double sampled_time;
        public int sampled_frame;
        public float age_seconds;
        public float observed_to_current_m;
    }

    /// <summary>One shared, confirmed selection. Consumed by highlight/map/LLM/task.</summary>
    [Serializable]
    public sealed class SelectionRecord
    {
        public string entity_id = "";
        public string name = "";
        public string display_name = "";
        public string cls = "";
        public string kind = "";

        public bool confirmed = true;
        public string source = "manual_click";
        public int generation;

        public long frame_id;
        public int frame_count;
        public double time_since_startup;
        public string camera_source = "";
        public PickCameraSnapshot compute_snapshot;

        public Vector3 entity_position;
        public Vector3 surface_point;
        public Vector3 bounds_min;
        public Vector3 bounds_max;
        /// <summary>True when bounds/position were sampled from live renderers at publish time.</summary>
        public bool bounds_from_live_renderers;
        /// <summary>True when the surface point passed the live-bounds + epsilon check.</summary>
        public bool surface_inside_live_bounds;
        /// <summary>"ok" or the explicit rejection reason for the surface point.</summary>
        public string surface_validation_note = "";
        public bool dynamic;
        public string live_position_note = "";

        public Vector3 character_position;
        public float body_yaw_deg;
        public Vector3 eye_position;
        public Vector3 eye_forward;
        public float distance_m;
        public float bearing_deg;
        public float pitch_deg;
        public float surface_distance_from_eye_m;

        public string capabilities = "";
        public string note = "";

        public string EntityId { get { return entity_id; } }
    }

    /// <summary>Per-entity coverage entry of the selection registry.</summary>
    public sealed class EntityCoverage
    {
        public string entity_id = "";
        public string cls = "";
        public string name = "";
        public string source_kind = "none";
        public bool selectable;
        public bool geometry_verified;
        public int triangles;
        public int renderers;
        public string reason = "";
        public float[] pos;
        public float[] bounds;
        /// <summary>Provenance that proved this entity's geometry (dynamic_node / single_renderer / source_object_sidecar).</summary>
        public string verified_by = "";
        public int source_object_count;
        public string[] source_objects = new string[0];
        public int unknown_triangles;
    }

    public sealed class PickCoverageReport
    {
        public string schema = "firstperson-selection.pick-coverage.v2";
        public int catalog_entities;
        public int selectable_entities;
        public int geometry_verified_entities;
        public int missing_entities;
        public int dynamic_entities;
        public int static_extracted_entities;
        public int static_single_renderer_entities;
        public int merged_triangles;
        public int assigned_triangles;
        public int unassigned_triangles;
        public long build_ms;
        public string built_at_local = "";
        public string[] source_meshes = new string[0];
        // Reliability/provenance fields (node A round 1)
        public string geometry_source_policy = "reliable_source_objects_only";
        public string sidecar_path = "";
        public string sidecar_sha256 = "";
        public string sidecar_manifest_path = "";
        public int sidecar_entities;
        public int sidecar_triangles;
        public int sidecar_objects;
        public int source_map_rows;
        public int source_map_mapped_objects;
        public int excluded_source_objects;
        public int unclassified_source_objects;
        public string[] excluded_source_objects_sample = new string[0];
        public string[] unclassified_source_objects_sample = new string[0];
        public int unknown_source_objects_note;
        public string source_object_map_sha256 = "";
        public string blend_sha256 = "";
        public readonly List<EntityCoverage> entries = new List<EntityCoverage>();
        public readonly Dictionary<string, int> missing_by_class = new Dictionary<string, int>();
        public readonly Dictionary<string, int> verified_by_class = new Dictionary<string, int>();
        public readonly Dictionary<string, int> verified_by_source = new Dictionary<string, int>();

        public EntityCoverage Find(string id)
        {
            foreach (var entry in entries) if (entry.entity_id == id) return entry;
            return null;
        }
    }
}
