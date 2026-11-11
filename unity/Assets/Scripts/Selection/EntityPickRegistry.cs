using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;
using Yixuan.World;
using Yixuan.SelectionLab;

namespace Yixuan.Pick
{
    /// <summary>One catalog entity resolved to real scene geometry.</summary>
    public sealed class EntityPickEntry
    {
        public string Id = "";
        public string Cls = "";
        public string Kind = "";
        public string Name = "";
        public string DisplayName = "";
        public string Capabilities = "";

        public int Code;
        public bool Selectable;
        public bool GeometryVerified;
        public string SourceKind = "none";      // dynamic_node / single_renderer / source_object_sidecar
        public string VerifiedBy = "";          // provenance description
        public string FailureReason = "";

        public bool Dynamic;
        public Vector3 CatalogPosition;
        public Vector3 CatalogBoundsMin;
        public Vector3 CatalogBoundsMax;
        public float Height;

        public readonly List<Renderer> Renderers = new List<Renderer>();
        public Mesh EntityMesh;                 // world-space source-object mesh (identity transform)
        public int TriangleCount;
        public string[] SourceObjects = new string[0];
        public int SourceObjectCount;

        public Vector3 LivePosition()
        {
            if (Dynamic && Renderers.Count > 0)
            {
                bool any = false;
                Bounds bounds = new Bounds();
                foreach (var renderer in Renderers)
                {
                    if (renderer == null) continue;
                    if (!any) { bounds = renderer.bounds; any = true; }
                    else bounds.Encapsulate(renderer.bounds);
                }
                if (any) return bounds.center;
            }
            return CatalogPosition;
        }

        public Bounds WorldBounds()
        {
            if (Dynamic && Renderers.Count > 0)
            {
                bool any = false;
                Bounds bounds = new Bounds();
                foreach (var renderer in Renderers)
                {
                    if (renderer == null) continue;
                    if (!any) { bounds = renderer.bounds; any = true; }
                    else bounds.Encapsulate(renderer.bounds);
                }
                if (any) return bounds;
            }
            if (EntityMesh != null) return EntityMesh.bounds;
            var center = (CatalogBoundsMin + CatalogBoundsMax) * 0.5f;
            var size = CatalogBoundsMax - CatalogBoundsMin;
            return new Bounds(center, size);
        }
    }

    // ---------------------------------------------------------------- sidecar
    [Serializable]
    public sealed class SidecarOwner
    {
        public string entity_id;
        public float distance_m;
        public string region;
    }

    [Serializable]
    public sealed class SidecarCounterexamples
    {
        public float[] ground_triangle_world_point;
        public SidecarOwner[] ground_triangle_owner_entities;
        public float[] metro_triangle_world_point;
        public SidecarOwner[] metro_triangle_owner_entities;
        public string note;
    }

    [Serializable]
    public sealed class SidecarStatusCounts
    {
        public int entity;
        public int excluded;
        public int unclassified;
    }

    [Serializable]
    public sealed class SidecarEntityInfo
    {
        public string entity_id;
        public string @class;
        public string[] objects;
        public int object_count;
        public int vertex_count;
        public int triangle_count;
    }

    [Serializable]
    public sealed class SidecarManifest
    {
        public string schema;
        public string generated_at_local;
        public string generator;
        public string generator_sha256;
        public string blender_version;
        public string blend_file;
        public string blend_sha256;
        public string source_object_map;
        public string source_object_map_sha256;
        public string coordinate_contract;
        public int source_rows;
        public SidecarStatusCounts status_counts;
        public int mapped_object_count;
        public int exported_object_count;
        public string[] excluded_classes;
        public int excluded_source_object_count;
        public string[] excluded_source_objects_sample;
        public int unclassified_source_object_count;
        public string[] unclassified_source_objects_sample;
        public int missing_source_object_count;
        public int entity_count;
        public int triangle_count;
        public SidecarEntityInfo[] entities;
        public SidecarCounterexamples review_counterexample_checks;
        public string sidecar_sha256;
        public int sidecar_bytes;
        public string[] warnings;
    }

    /// <summary>
    /// Generic entity pick registry for the existing SovietBlueHour map.
    ///
    /// Node A round 1 (Codex review R1): static geometry identity no longer comes
    /// from AABB centroid guessing. A read-only Blender export of the existing
    /// source .blend uses the existing source-object map
    /// (Maps/SovietBlueHour/entities/source_object_map.csv) to attribute every
    /// source object to its explicit entity id, and writes a hidden pick sidecar
    /// (Assets/Resources/FirstPersonSelection/static_pick.v1.bytes, 8.4MB,
    /// 258,924 triangles, 758 entities). Only source-object-provenanced geometry
    /// is selectable/GeometryVerified. Excluded/unclassified source objects stay
    /// as visible occluders (the scene renderers still write depth) but get no
    /// entity id. Buildings keep their existing per-building renderer, vehicles /
    /// tram / flame keep their existing DYN_entity_* live nodes.
    /// </summary>
    public static class EntityPickRegistry
    {
        public const string MapRootName = "SovietBlueHour_Map";
        public const string GeometryName = "SovietBlueHour_Geometry";
        public const string StaticPickMeshName = "FirstPersonSelection_StaticPick";
        public const string SidecarResource = "FirstPersonSelection/static_pick.v1";
        public const string SidecarManifestResource = "FirstPersonSelection/static_pick.v1.manifest";

        public static bool Ready { get; private set; }
        public static bool Building { get; private set; }
        public static string Error { get; private set; } = "";
        public static PickCoverageReport Report { get; private set; }
        public static Mesh StaticPickMesh { get; private set; }
        public static long BuildMs { get; private set; }
        public static SidecarManifest Manifest { get; private set; }

        static readonly List<EntityPickEntry> _entries = new List<EntityPickEntry>();
        static readonly Dictionary<string, EntityPickEntry> _byId = new Dictionary<string, EntityPickEntry>(StringComparer.Ordinal);
        static readonly Dictionary<int, EntityPickEntry> _byCode = new Dictionary<int, EntityPickEntry>();

        public static int Count { get { return _entries.Count; } }
        public static List<EntityPickEntry> Entries { get { return _entries; } }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetSession()
        {
            Ready = false;
            Building = false;
            Error = "";
            Report = null;
            StaticPickMesh = null;
            BuildMs = 0;
            Manifest = null;
            _entries.Clear();
            _byId.Clear();
            _byCode.Clear();
        }

        public static bool EnsureBuilt()
        {
            if (Ready) return true;
            if (Building) return false;
            Building = true;
            var stopwatch = Stopwatch.StartNew();
            try
            {
                BuildInternal();
                Ready = Report != null && Report.geometry_verified_entities > 0;
            }
            catch (Exception exception)
            {
                Error = "registry build failed: " + exception;
                Ready = false;
                UnityEngine.Debug.LogError("[FirstPersonSelection] " + Error);
            }
            finally
            {
                stopwatch.Stop();
                BuildMs = stopwatch.ElapsedMilliseconds;
                if (Report != null) Report.build_ms = BuildMs;
                Building = false;
            }
            return Ready;
        }

        public static EntityPickEntry Get(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            EntityPickEntry entry;
            return _byId.TryGetValue(id, out entry) ? entry : null;
        }

        public static EntityPickEntry GetByCode(int code)
        {
            if (code == 0) return null;
            EntityPickEntry entry;
            return _byCode.TryGetValue(code, out entry) ? entry : null;
        }

        public static Color32 EncodeCode(int code)
        {
            return new Color32((byte)(code & 0xFF), (byte)((code >> 8) & 0xFF), (byte)((code >> 16) & 0xFF), 255);
        }

        public static int DecodeCode(Color32 color)
        {
            return color.r | (color.g << 8) | (color.b << 16);
        }

        /// <summary>Reset the registry when the explicit scene source changes/rebinds.</summary>
        public static void ResetForExplicitSource()
        {
            Ready = false;
            Building = false;
            Error = "";
            Report = null;
            StaticPickMesh = null;
            BuildMs = 0;
            Manifest = null;
            _entries.Clear();
            _byId.Clear();
            _byCode.Clear();
        }

        static void BuildExplicitLab()
        {
            var scene = SelectionLabContext.Scene;
            var report = new PickCoverageReport
            {
                catalog_entities = scene != null ? scene.entities.Length : 0,
                built_at_local = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:sszzz"),
                geometry_source_policy = "explicit_scene_renderer",
                source_meshes = new[] { "SelectionLabScene explicit renderers" },
            };
            if (scene == null)
            {
                Error = "selection lab scene context missing";
                return;
            }
            for (int i = 0; i < scene.entities.Length; i++)
            {
                var def = scene.entities[i];
                if (def == null || string.IsNullOrEmpty(def.id) || def.renderer == null)
                {
                    report.missing_entities++;
                    continue;
                }
                var bounds = def.renderer.bounds;
                var entry = new EntityPickEntry
                {
                    Id = def.id,
                    Cls = string.IsNullOrEmpty(def.cls) ? def.shape : def.cls,
                    Kind = "selection_lab",
                    Name = def.display_name,
                    DisplayName = def.display_name,
                    Capabilities = def.capabilities,
                    Code = i + 1,
                    Selectable = true,
                    GeometryVerified = true,
                    SourceKind = "explicit_scene",
                    VerifiedBy = "SelectionLabScene entity definition + live renderer",
                    Dynamic = false,
                    CatalogPosition = bounds.center,
                    CatalogBoundsMin = bounds.min,
                    CatalogBoundsMax = bounds.max,
                    Height = bounds.size.y,
                };
                entry.Renderers.Add(def.renderer);
                entry.TriangleCount = CountRendererTriangles(entry.Renderers);
                _entries.Add(entry);
                _byId[entry.Id] = entry;
                _byCode[entry.Code] = entry;
                report.selectable_entities++;
                report.geometry_verified_entities++;
                report.assigned_triangles += entry.TriangleCount;
                report.entries.Add(new EntityCoverage
                {
                    entity_id = entry.Id,
                    cls = entry.Cls,
                    name = entry.DisplayName,
                    source_kind = entry.SourceKind,
                    selectable = true,
                    geometry_verified = true,
                    triangles = entry.TriangleCount,
                    renderers = 1,
                    pos = new[] { bounds.center.x, bounds.center.y, bounds.center.z },
                    bounds = new[] { bounds.min.x, bounds.min.z, bounds.max.x, bounds.max.z },
                    verified_by = entry.VerifiedBy,
                });
            }
            report.missing_entities = Mathf.Max(0, report.catalog_entities - report.selectable_entities);
            Report = report;
            Error = "";
            UnityEngine.Debug.Log("[FirstPersonSelection] lab registry entries=" + _entries.Count +
                      " selectable=" + report.selectable_entities);
        }

        // ------------------------------------------------------------- build
        static void BuildInternal()
        {
            Error = "";
            _entries.Clear();
            _byId.Clear();
            _byCode.Clear();
            StaticPickMesh = null;
            Manifest = null;

            if (SelectionLabContext.IsActive)
            {
                BuildExplicitLab();
                return;
            }

            if (!WorldEntityCatalog.EnsureLoaded() || WorldEntityCatalog.Data == null)
            {
                Error = "entity catalog unavailable: " + WorldEntityCatalog.Error;
                return;
            }
            var mapRoot = GameObject.Find(MapRootName);
            if (mapRoot == null)
            {
                Error = "找不到场景根节点 " + MapRootName + "（主场景未加载？）";
                return;
            }
            var geometry = mapRoot.transform.Find(GeometryName);
            if (geometry == null) geometry = mapRoot.transform;

            var catalog = WorldEntityCatalog.Data.entities;
            var nameMap = new Dictionary<string, Transform>(StringComparer.Ordinal);
            foreach (var transform in mapRoot.GetComponentsInChildren<Transform>(true))
            {
                if (transform == null || string.IsNullOrEmpty(transform.name)) continue;
                if (!nameMap.ContainsKey(transform.name)) nameMap[transform.name] = transform;
            }
            var directChildren = new Dictionary<string, Transform>(StringComparer.Ordinal);
            foreach (Transform child in geometry)
                if (child != null && !directChildren.ContainsKey(child.name)) directChildren[child.name] = child;

            var report = new PickCoverageReport();
            report.catalog_entities = catalog.Length;
            report.built_at_local = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:sszzz");
            for (int index = 0; index < catalog.Length; index++)
            {
                var entity = catalog[index];
                if (entity == null || string.IsNullOrEmpty(entity.id)) continue;
                var entry = new EntityPickEntry
                {
                    Id = entity.id,
                    Cls = entity.cls,
                    Kind = entity.kind,
                    Name = entity.name,
                    DisplayName = WorldEntityCatalog.DisplayName(entity),
                    Capabilities = CapabilitySummary(entity),
                    Code = index + 1,
                    Selectable = false,
                    Height = entity.height,
                };
                if (entity.pos != null && entity.pos.Length >= 3)
                    entry.CatalogPosition = new Vector3(entity.pos[0], entity.pos[1], entity.pos[2]);
                entry.CatalogBoundsMin = new Vector3(entry.CatalogPosition.x, entry.CatalogPosition.y - Mathf.Max(0.01f, entity.height) * 0.5f, entry.CatalogPosition.z);
                entry.CatalogBoundsMax = new Vector3(entry.CatalogPosition.x, entry.CatalogPosition.y + Mathf.Max(0.01f, entity.height) * 0.5f, entry.CatalogPosition.z);
                if (entity.bounds != null && entity.bounds.Length >= 4)
                {
                    entry.CatalogBoundsMin = new Vector3(entity.bounds[0], entry.CatalogBoundsMin.y, entity.bounds[1]);
                    entry.CatalogBoundsMax = new Vector3(entity.bounds[2], entry.CatalogBoundsMax.y, entity.bounds[3]);
                }
                _entries.Add(entry);
                _byId[entry.Id] = entry;
                _byCode[entry.Code] = entry;
            }

            // 1) dynamic nodes (vehicles / tram / flame fire): live transforms.
            foreach (var entry in _entries)
            {
                string suffix = SuffixOf(entry.Id);
                if (string.IsNullOrEmpty(suffix)) continue;
                Transform node;
                if (nameMap.TryGetValue("DYN_entity_" + suffix, out node) && node != null)
                {
                    var renderers = node.GetComponentsInChildren<Renderer>(true);
                    foreach (var renderer in renderers)
                    {
                        if (renderer == null) continue;
                        bool hasMesh = renderer.GetComponent<MeshFilter>() != null || renderer is SkinnedMeshRenderer;
                        if (hasMesh) entry.Renderers.Add(renderer);
                    }
                    if (entry.Renderers.Count > 0)
                    {
                        entry.SourceKind = "dynamic_node";
                        entry.VerifiedBy = "existing DYN_entity_* live node renderer";
                        entry.Dynamic = true;
                        entry.GeometryVerified = true;
                        entry.TriangleCount = CountRendererTriangles(entry.Renderers);
                    }
                }
            }

            // 2) buildings: one existing renderer per building entity.
            foreach (var entry in _entries)
            {
                if (entry.Renderers.Count > 0 || entry.Cls != "building") continue;
                string suffix = SuffixOf(entry.Id);
                if (string.IsNullOrEmpty(suffix)) continue;
                string wanted = suffix.ToUpperInvariant();
                foreach (var pair in directChildren)
                {
                    if (!pair.Key.StartsWith(wanted, StringComparison.OrdinalIgnoreCase)) continue;
                    var renderer = pair.Value.GetComponent<Renderer>();
                    if (renderer == null) continue;
                    entry.Renderers.Add(renderer);
                    entry.SourceKind = "single_renderer";
                    entry.VerifiedBy = "existing per-building BLD_* renderer";
                    entry.GeometryVerified = true;
                    entry.Dynamic = false;
                    entry.TriangleCount = CountRendererTriangles(entry.Renderers);
                    break;
                }
            }

            // 3) flame base renderer belongs to the eternal flame entity.
            foreach (var entry in _entries)
            {
                if (entry.Id != "entity/prop_eternal_flame" && entry.Cls != "eternal_flame") continue;
                foreach (var pair in directChildren)
                {
                    if (!pair.Key.StartsWith("PROP_ETERNAL_FLAME_BASE", StringComparison.OrdinalIgnoreCase)) continue;
                    var renderer = pair.Value.GetComponent<Renderer>();
                    if (renderer == null || entry.Renderers.Contains(renderer)) continue;
                    entry.Renderers.Add(renderer);
                    if (entry.SourceKind == "none") entry.SourceKind = "single_renderer";
                    if (string.IsNullOrEmpty(entry.VerifiedBy)) entry.VerifiedBy = "existing eternal flame base renderer";
                }
            }

            // 4) static source-object sidecar (reliable provenance from source_object_map.csv).
            LoadManifest();
            int sidecarEntities = 0;
            int sidecarTriangles = 0;
            int sidecarObjects = 0;
            int orphanSidecarEntities = 0;
            var staticVertices = new List<Vector3>(1 << 18);
            var staticColors = new List<Color32>(1 << 18);
            var staticIndices = new List<int>(1 << 19);
            var sidecarAsset = Resources.Load<TextAsset>(SidecarResource);
            if (sidecarAsset == null)
            {
                Error = "缺少静态 pick sidecar Resources/" + SidecarResource + ".bytes（先运行 Tools/FirstPersonSelection/export_static_pick_sidecar.py）";
            }
            else
            {
                try
                {
                    ParseSidecar(sidecarAsset.bytes, staticVertices, staticColors, staticIndices,
                        ref sidecarEntities, ref sidecarTriangles, ref sidecarObjects, ref orphanSidecarEntities);
                }
                catch (Exception exception)
                {
                    Error = "sidecar 解析失败：" + exception.Message;
                    UnityEngine.Debug.LogError("[FirstPersonSelection] " + Error);
                }
            }
            if (staticIndices.Count > 0)
            {
                var mesh = new Mesh { name = StaticPickMeshName, hideFlags = HideFlags.DontSave };
                mesh.indexFormat = staticVertices.Count > 65000
                    ? UnityEngine.Rendering.IndexFormat.UInt32
                    : UnityEngine.Rendering.IndexFormat.UInt16;
                mesh.SetVertices(staticVertices);
                mesh.SetColors(staticColors);
                mesh.SetTriangles(staticIndices, 0, true);
                mesh.RecalculateBounds();
                StaticPickMesh = mesh;
            }
            ApplyManifestDetails();

            // 5) coverage
            report.sidecar_path = "Assets/Resources/FirstPersonSelection/static_pick.v1.bytes";
            report.sidecar_manifest_path = "Assets/Resources/FirstPersonSelection/static_pick.v1.manifest.json";
            report.sidecar_entities = sidecarEntities;
            report.sidecar_triangles = sidecarTriangles;
            report.sidecar_objects = sidecarObjects;
            report.source_meshes = new[] { "static_pick.v1.bytes (source_object_map.csv provenance)" };
            report.merged_triangles = sidecarTriangles;
            report.assigned_triangles = sidecarTriangles;
            report.unassigned_triangles = 0;
            report.static_extracted_entities = sidecarEntities;
            if (sidecarAsset != null) report.sidecar_sha256 = Sha256(sidecarAsset.bytes);
            if (Manifest != null)
            {
                report.source_map_rows = Manifest.source_rows;
                report.source_map_mapped_objects = Manifest.mapped_object_count;
                report.excluded_source_objects = Manifest.excluded_source_object_count;
                report.unclassified_source_objects = Manifest.unclassified_source_object_count;
                report.excluded_source_objects_sample = Manifest.excluded_source_objects_sample ?? new string[0];
                report.unclassified_source_objects_sample = Manifest.unclassified_source_objects_sample ?? new string[0];
                report.unknown_source_objects_note = Manifest.excluded_source_object_count + Manifest.unclassified_source_object_count;
                report.source_object_map_sha256 = Manifest.source_object_map_sha256 ?? "";
                report.blend_sha256 = Manifest.blend_sha256 ?? "";
            }
            if (!string.IsNullOrEmpty(Error))
                UnityEngine.Debug.LogError("[FirstPersonSelection] sidecar error: " + Error);

            report.selectable_entities = 0;
            report.geometry_verified_entities = 0;
            report.missing_entities = 0;
            report.dynamic_entities = 0;
            report.static_single_renderer_entities = 0;
            foreach (var entry in _entries)
            {
                entry.Selectable = entry.GeometryVerified && (entry.Renderers.Count > 0 || entry.TriangleCount > 0);
                if (entry.SourceKind == "dynamic_node") report.dynamic_entities++;
                else if (entry.SourceKind == "single_renderer") report.static_single_renderer_entities++;
                if (entry.Selectable)
                {
                    report.selectable_entities++;
                    report.geometry_verified_entities++;
                    int old;
                    report.verified_by_class.TryGetValue(entry.Cls ?? "", out old);
                    report.verified_by_class[entry.Cls ?? ""] = old + 1;
                    report.verified_by_source.TryGetValue(entry.SourceKind ?? "", out old);
                    report.verified_by_source[entry.SourceKind ?? ""] = old + 1;
                }
                else
                {
                    report.missing_entities++;
                    if (string.IsNullOrEmpty(entry.FailureReason))
                        entry.FailureReason = entry.Renderers.Count == 0 && entry.TriangleCount == 0
                            ? "no_reliable_source_geometry_for_entity"
                            : "no_usable_geometry";
                    int old;
                    report.missing_by_class.TryGetValue(entry.Cls ?? "", out old);
                    report.missing_by_class[entry.Cls ?? ""] = old + 1;
                }
                var coverage = new EntityCoverage
                {
                    entity_id = entry.Id,
                    cls = entry.Cls,
                    name = entry.DisplayName,
                    source_kind = entry.SourceKind,
                    selectable = entry.Selectable,
                    geometry_verified = entry.GeometryVerified,
                    triangles = entry.TriangleCount,
                    renderers = entry.Renderers.Count,
                    reason = entry.FailureReason,
                    pos = new[] { entry.CatalogPosition.x, entry.CatalogPosition.y, entry.CatalogPosition.z },
                    bounds = new[]
                    {
                        entry.CatalogBoundsMin.x, entry.CatalogBoundsMin.z,
                        entry.CatalogBoundsMax.x, entry.CatalogBoundsMax.z
                    },
                    verified_by = entry.VerifiedBy,
                    source_object_count = entry.SourceObjectCount,
                    source_objects = entry.SourceObjects.Length > 8
                        ? new ArraySegment<string>(entry.SourceObjects, 0, 8).ToArray()
                        : entry.SourceObjects,
                };
                report.entries.Add(coverage);
            }
            if (orphanSidecarEntities > 0)
            {
                report.unknown_source_objects_note += orphanSidecarEntities;
                UnityEngine.Debug.LogWarning("[FirstPersonSelection] sidecar contained " + orphanSidecarEntities +
                                              " entity ids not present in the catalog; ignored");
            }
            Report = report;
            UnityEngine.Debug.Log("[FirstPersonSelection] registry ready=" + Ready +
                                  " entries=" + _entries.Count +
                                  " selectable=" + report.selectable_entities +
                                  " missing=" + report.missing_entities +
                                  " dynamic=" + report.dynamic_entities +
                                  " sidecarEntities=" + sidecarEntities +
                                  " sidecarTris=" + sidecarTriangles +
                                  " sidecarObjects=" + sidecarObjects +
                                  " error=" + Error);
        }

        static void ParseSidecar(byte[] bytes, List<Vector3> vertices, List<Color32> colors, List<int> indices,
                                 ref int entityCount, ref int triangleCount, ref int objectCount, ref int orphanEntities)
        {
            if (bytes == null || bytes.Length < 16) throw new InvalidOperationException("sidecar bytes too small");
            if (bytes[0] != 'F' || bytes[1] != 'P' || bytes[2] != 'S' || bytes[3] != 'P' ||
                bytes[4] != 'I' || bytes[5] != 'C' || bytes[6] != 'K' || bytes[7] != '1')
                throw new InvalidOperationException("bad sidecar magic");
            int offset = 8;
            int version = ReadInt32(bytes, ref offset);
            if (version != 1) throw new InvalidOperationException("unsupported sidecar version " + version);
            int count = ReadInt32(bytes, ref offset);
            for (int i = 0; i < count; i++)
            {
                int idLength = ReadUInt16(bytes, ref offset);
                string id = System.Text.Encoding.UTF8.GetString(bytes, offset, idLength);
                offset += idLength;
                int vertexCount = ReadInt32(bytes, ref offset);
                int triCount = ReadInt32(bytes, ref offset);
                int objectCountValue = ReadUInt16(bytes, ref offset);
                var localVertices = new Vector3[vertexCount];
                var localNormals = new Vector3[vertexCount];
                // Binary layout: ALL positions for the entity, then ALL normals,
                // then ALL indices (the exporter writes three contiguous arrays).
                for (int v = 0; v < vertexCount; v++)
                    localVertices[v] = new Vector3(ReadFloat(bytes, ref offset), ReadFloat(bytes, ref offset), ReadFloat(bytes, ref offset));
                for (int v = 0; v < vertexCount; v++)
                    localNormals[v] = new Vector3(ReadFloat(bytes, ref offset), ReadFloat(bytes, ref offset), ReadFloat(bytes, ref offset));
                var localIndices = new int[triCount * 3];
                for (int t = 0; t < triCount * 3; t++) localIndices[t] = ReadInt32(bytes, ref offset);
                var entry = Get(id);
                if (entry == null)
                {
                    orphanEntities++;
                    continue;
                }
                if (entry.Renderers.Count > 0)
                {
                    // Direct scene renderer has higher authority; the sidecar entry is
                    // non-authoritative coverage information and never duplicates pick geometry.
                    orphanEntities++;
                    continue;
                }
                var mesh = new Mesh { name = "PickSource_" + id.Replace('/', '_'), hideFlags = HideFlags.DontSave };
                mesh.indexFormat = vertexCount > 65000
                    ? UnityEngine.Rendering.IndexFormat.UInt32
                    : UnityEngine.Rendering.IndexFormat.UInt16;
                mesh.SetVertices(localVertices);
                mesh.SetNormals(localNormals);
                mesh.SetTriangles(localIndices, 0, true);
                mesh.RecalculateBounds();
                entry.EntityMesh = mesh;
                entry.SourceKind = "source_object_sidecar";
                entry.VerifiedBy = "source_object_map.csv -> static_pick.v1.bytes";
                entry.GeometryVerified = true;
                entry.TriangleCount = triCount;
                entry.SourceObjectCount = objectCountValue;
                entityCount++;
                triangleCount += triCount;
                objectCount += objectCountValue;
                Color32 color = EncodeCode(entry.Code);
                int baseIndex = vertices.Count;
                for (int v = 0; v < vertexCount; v++)
                {
                    vertices.Add(localVertices[v]);
                    colors.Add(color);
                }
                for (int t = 0; t < localIndices.Length; t++) indices.Add(baseIndex + localIndices[t]);
            }
        }

        static void LoadManifest()
        {
            var asset = Resources.Load<TextAsset>(SidecarManifestResource);
            if (asset == null)
            {
                UnityEngine.Debug.LogWarning("[FirstPersonSelection] missing sidecar manifest Resources/" + SidecarManifestResource);
                return;
            }
            try
            {
                Manifest = JsonUtility.FromJson<SidecarManifest>(asset.text);
            }
            catch (Exception exception)
            {
                UnityEngine.Debug.LogWarning("[FirstPersonSelection] sidecar manifest parse failed: " + exception.Message);
                Manifest = null;
            }
        }

        static void ApplyManifestDetails()
        {
            if (Manifest == null || Manifest.entities == null) return;
            foreach (var info in Manifest.entities)
            {
                if (info == null || string.IsNullOrEmpty(info.entity_id)) continue;
                var entry = Get(info.entity_id);
                if (entry == null) continue;
                entry.SourceObjectCount = info.object_count;
                entry.SourceObjects = info.objects ?? new string[0];
            }
        }

        static int ReadInt32(byte[] bytes, ref int offset)
        {
            int value = BitConverter.ToInt32(bytes, offset);
            offset += 4;
            return value;
        }

        static int ReadUInt16(byte[] bytes, ref int offset)
        {
            int value = BitConverter.ToUInt16(bytes, offset);
            offset += 2;
            return value;
        }

        static float ReadFloat(byte[] bytes, ref int offset)
        {
            float value = BitConverter.ToSingle(bytes, offset);
            offset += 4;
            return value;
        }

        public static string Sha256(byte[] bytes)
        {
            if (bytes == null) return "";
            using (var sha = System.Security.Cryptography.SHA256.Create())
            {
                var hash = sha.ComputeHash(bytes);
                var builder = new System.Text.StringBuilder(hash.Length * 2);
                foreach (byte value in hash) builder.Append(value.ToString("x2"));
                return builder.ToString();
            }
        }

        // ------------------------------------------------------------ helpers
        static string SuffixOf(string id)
        {
            if (string.IsNullOrEmpty(id)) return "";
            return id.StartsWith("entity/", StringComparison.Ordinal) ? id.Substring("entity/".Length) : id;
        }

        static string CapabilitySummary(WorldEntityCatalog.Entity entity)
        {
            if (entity == null || entity.capabilities == null || entity.capabilities.Length == 0) return "";
            var builder = new System.Text.StringBuilder();
            foreach (var capability in entity.capabilities)
            {
                if (capability == null) continue;
                if (builder.Length > 0) builder.Append(',');
                builder.Append(capability.verb);
                bool usable = capability.code_implemented && capability.geometry_verified;
                builder.Append(usable ? ":ready" : ":not_ready");
            }
            return builder.ToString();
        }

        static int CountRendererTriangles(List<Renderer> renderers)
        {
            int total = 0;
            foreach (var renderer in renderers)
            {
                if (renderer == null) continue;
                Mesh mesh; int submeshStart, submeshCount;
                GetRendererMeshRange(renderer, out mesh, out submeshStart, out submeshCount);
                if (mesh == null) continue;
                for (int submesh = submeshStart; submesh < submeshStart + submeshCount && submesh < mesh.subMeshCount; submesh++)
                {
                    try { total += (int)(mesh.GetIndexCount(submesh) / 3); }
                    catch { }
                }
            }
            return total;
        }

        /// <summary>
        /// Resolve the drawable mesh and submesh range for a scene renderer.
        /// Static batching (which Unity performs at play start for static-flagged
        /// map objects) replaces MeshFilter.sharedMesh with a combined mesh that
        /// spans many objects; drawing the whole combined mesh would paint other
        /// buildings/props at this renderer's draw calls. MeshRenderer.subMeshStartIndex
        /// plus the material count gives the renderer's real range.
        /// </summary>
        public static void GetRendererMeshRange(Renderer renderer, out Mesh mesh,
                                                out int submeshStart, out int submeshCount)
        {
            mesh = null;
            submeshStart = 0;
            submeshCount = 0;
            if (renderer == null) return;
            var filter = renderer.GetComponent<MeshFilter>();
            mesh = filter != null ? filter.sharedMesh : (renderer as SkinnedMeshRenderer)?.sharedMesh;
            if (mesh == null) return;
            var meshRenderer = renderer as MeshRenderer;
            submeshCount = mesh.subMeshCount;
            if (meshRenderer != null)
            {
                int start = meshRenderer.subMeshStartIndex;
                int count = renderer.sharedMaterials != null ? renderer.sharedMaterials.Length : mesh.subMeshCount;
                if (count <= 0) count = mesh.subMeshCount;
                if (start >= 0 && count > 0 && start + count <= mesh.subMeshCount)
                {
                    submeshStart = start;
                    submeshCount = count;
                }
            }
        }

        /// <summary>
        /// Draw matrix matching the mesh returned by GetRendererMeshRange.
        /// Statically batched meshes are already baked in world space, so they
        /// must be drawn with identity; live dynamic renderers use their transform.
        /// </summary>
        public static Matrix4x4 GetRendererDrawMatrix(Renderer renderer)
        {
            if (renderer == null) return Matrix4x4.identity;
            return renderer.isPartOfStaticBatch ? Matrix4x4.identity : renderer.transform.localToWorldMatrix;
        }

        /// <summary>All renderers that can occlude the eye view (used by the ID pass).</summary>
        public static List<Renderer> VisibleRenderers(Camera eye)
        {
            var result = new List<Renderer>(160);
            if (eye == null) return result;
            int mask = eye.cullingMask;
            var renderers = UnityEngine.Object.FindObjectsOfType<Renderer>();
            foreach (var renderer in renderers)
            {
                if (renderer == null || !renderer.enabled) continue;
                if (!renderer.gameObject.activeInHierarchy) continue;
                if ((mask & (1 << renderer.gameObject.layer)) == 0) continue;
                if (renderer is ParticleSystemRenderer || renderer is TrailRenderer || renderer is LineRenderer) continue;
                result.Add(renderer);
            }
            return result;
        }

        // ------------------------------------------------------- truth queries
        /// <summary>
        /// World-space mesh for a sidecar-backed static entity (identity transform).
        /// Returns null for entities driven by real scene renderers.
        /// </summary>
        public static Mesh GetEntityMesh(EntityPickEntry entry)
        {
            return entry != null ? entry.EntityMesh : null;
        }

        /// <summary>Exact 3D point-triangle distance (Ericson closest point on triangle).</summary>
        public static float PointTriangleDistance(Vector3 point, Vector3 a, Vector3 b, Vector3 c)
        {
            Vector3 ab = b - a, ac = c - a, ap = point - a;
            float d1 = Vector3.Dot(ab, ap), d2 = Vector3.Dot(ac, ap);
            if (d1 <= 0f && d2 <= 0f) return ap.magnitude;
            Vector3 bp = point - b;
            float d3 = Vector3.Dot(ab, bp), d4 = Vector3.Dot(ac, bp);
            if (d3 >= 0f && d4 <= d3) return bp.magnitude;
            float vc = d1 * d4 - d3 * d2;
            if (vc <= 0f && d1 >= 0f && d3 <= 0f)
            {
                float v = d1 / (d1 - d3);
                return (point - (a + v * ab)).magnitude;
            }
            Vector3 cp = point - c;
            float d5 = Vector3.Dot(ab, cp), d6 = Vector3.Dot(ac, cp);
            if (d6 >= 0f && d5 <= d6) return cp.magnitude;
            float vb = d5 * d2 - d1 * d6;
            if (vb <= 0f && d2 >= 0f && d6 <= 0f)
            {
                float w = d2 / (d2 - d6);
                return (point - (a + w * ac)).magnitude;
            }
            float va = d3 * d6 - d5 * d4;
            if (va <= 0f && (d4 - d3) >= 0f && (d5 - d6) >= 0f)
            {
                float w = (d4 - d3) / ((d4 - d3) + (d5 - d6));
                return (point - (b + w * (c - b))).magnitude;
            }
            Vector3 n = Vector3.Cross(ab, ac);
            float length = n.magnitude;
            if (length < 1e-12f) return float.MaxValue;
            return Mathf.Abs(Vector3.Dot(ap, n / length));
        }

        /// <summary>
        /// Find the sidecar entity whose source-object triangle is closest to a
        /// world point (tolerance in metres). Used by tests to prove the ground
        /// counterexample cannot be attributed to a tree. Geometry with no
        /// provenanced triangle is reported as unknown (null).
        /// </summary>
        public static EntityPickEntry FindTriangleOwner(Vector3 point, float tolerance)
        {
            EntityPickEntry best = null;
            float bestDistance = float.MaxValue;
            foreach (var entry in _entries)
            {
                var mesh = entry.EntityMesh;
                if (mesh == null) continue;
                var vertices = mesh.vertices;
                var triangles = mesh.triangles;
                for (int t = 0; t < triangles.Length; t += 3)
                {
                    float distance = PointTriangleDistance(point,
                        vertices[triangles[t]], vertices[triangles[t + 1]], vertices[triangles[t + 2]]);
                    if (distance < bestDistance)
                    {
                        bestDistance = distance;
                        best = entry;
                        if (distance <= 1e-5f) break;
                    }
                }
                if (bestDistance <= 1e-5f) break;
            }
            return bestDistance <= tolerance ? best : null;
        }

        /// <summary>Find an entity by one of its source object names (provenance query).</summary>
        public static EntityPickEntry FindBySourceObject(string sourceObject)
        {
            if (string.IsNullOrEmpty(sourceObject)) return null;
            foreach (var entry in _entries)
            {
                if (entry.SourceObjects == null) continue;
                foreach (var name in entry.SourceObjects)
                    if (name == sourceObject) return entry;
            }
            return null;
        }
    }
}
