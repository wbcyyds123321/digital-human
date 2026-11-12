using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEngine;
using Yixuan.World;

namespace Yixuan.AI.Tasks
{
    public delegate bool TaskApproachResolver(Vector3 start, out Vector3 point, out string error);

    public sealed class TaskDestination
    {
        public string Id, Name, Path, Description;
        public bool SemanticObject;
        public string[] Aliases;
        public Transform Object, Approach, LookAt;
        public TaskInteractable Interaction;
        public bool IsAnchor;
        public Vector3 AnchorPosition;
        public Func<Vector3> LivePosition;
        public Func<bool> LiveAvailable;
        public TaskApproachResolver LiveApproach;
        public bool IsAvailable
        {
            get
            {
                if (LiveAvailable != null)
                    return LiveAvailable() && (LivePosition != null || IsAnchor || Object != null);
                return IsAnchor || Object != null;
            }
        }
        public Vector3 Position { get { return LivePosition != null ? LivePosition() : Object != null ? Object.position : AnchorPosition; } }
        public DestinationSnapshot Snapshot()
        { return new DestinationSnapshot { id = Id, name = Name, path = Path, aliases = Aliases, description = Description, position = Position, interactable = Interaction != null }; }
    }
    public sealed class TaskTargetCatalog
    {

        void AddSelectionTarget(Transform actor)
        {
            var selection = Yixuan.Pick.SelectionBroker.Current;
            if (selection == null || !selection.confirmed) return;
            AddEntityTarget(selection.entity_id, selection.display_name);
        }

        readonly System.Collections.Generic.List<string> _taskBoundIds = new System.Collections.Generic.List<string>();
        readonly System.Collections.Generic.Dictionary<string, string> _taskBoundLabels = new System.Collections.Generic.Dictionary<string, string>();

        /// <summary>
        /// Register an in-task confirmed visual target so Catalog.Refresh (called by
        /// other UI paths) re-adds it instead of losing the task's target A when the
        /// global single selection moved to B.
        /// </summary>
        public void RegisterTaskBoundTarget(string entityId, string label)
        {
            if (string.IsNullOrEmpty(entityId)) return;
            if (!_taskBoundIds.Contains(entityId)) _taskBoundIds.Add(entityId);
            _taskBoundLabels[entityId] = label ?? "";
            AddEntityTarget(entityId, label);
        }

        public void ClearTaskBoundTargets()
        {
            _taskBoundIds.Clear();
            _taskBoundLabels.Clear();
        }

        void ReAddTaskBoundTargets()
        {
            foreach (var entityId in _taskBoundIds.ToArray())
            {
                string label;
                if (!_taskBoundLabels.TryGetValue(entityId, out label)) label = "";
                AddEntityTarget(entityId, label);
            }
        }

        void AddEntityTarget(string entityId, string label)
        {
            if (string.IsNullOrEmpty(entityId)) return;
            var entry = Yixuan.Pick.EntityPickRegistry.Get(entityId);
            if (entry == null) return;
            var destination = new TaskDestination
            {
                Id = entry.Id,
                Name = string.IsNullOrEmpty(label) ? entry.DisplayName : label,
                Path = "selection/" + entry.Id,
                Description = "当前任务已验证确认的实体",
                SemanticObject = true,
                Aliases = new[] { entry.Id, string.IsNullOrEmpty(label) ? entry.DisplayName : label },
                LivePosition = entry.LivePosition,
                LiveAvailable = () => SelectionEntryAvailable(entry),
                LiveApproach = (Vector3 from, out Vector3 point, out string error) => TrySelectionApproach(entry, from, out point, out error),
            };
            bool merged = false;
            foreach (var existing in Targets)
            {
                if (existing == null || existing.Id != destination.Id) continue;
                existing.SemanticObject = true;
                if (existing.LivePosition == null) existing.LivePosition = destination.LivePosition;
                if (existing.LiveApproach == null) existing.LiveApproach = destination.LiveApproach;
                existing.LiveAvailable = destination.LiveAvailable;
                if (existing.Aliases == null || existing.Aliases.Length == 0) existing.Aliases = destination.Aliases;
                merged = true; break;
            }
            if (!merged) Targets.Add(destination);
        }

        static bool SelectionEntryAvailable(Yixuan.Pick.EntityPickEntry entry)
        {
            if (entry == null || !ReferenceEquals(Yixuan.Pick.EntityPickRegistry.Get(entry.Id), entry)) return false;
            if (entry.EntityMesh != null) return true;
            foreach (var renderer in entry.Renderers)
                if (renderer != null && renderer.enabled && renderer.gameObject.activeInHierarchy) return true;
            return false;
        }

        static bool TrySelectionApproach(Yixuan.Pick.EntityPickEntry entry, Vector3 from, out Vector3 point, out string error)
        {
            point = Vector3.zero; error = "";
            var bounds = entry.WorldBounds();
            Vector3 centre = bounds.center;
            Vector3 dir = from - centre; dir.y = 0f;
            if (dir.sqrMagnitude < 0.01f) dir = Vector3.forward;
            dir.Normalize();
            float radius = Mathf.Max(bounds.extents.x, bounds.extents.z) + 1.6f;
            float[] angles = { 0f, 35f, -35f, 70f, -70f, 110f, -110f, 180f };
            foreach (float angle in angles)
            {
                Vector3 d = Quaternion.Euler(0f, angle, 0f) * dir;
                Vector3 candidate = centre + d * radius;
                candidate.y = WorldData.GroundHeightAt(candidate);
                if (!WorldData.IsWalkable(candidate)) continue;
                var path = WorldData.FindPath(from, candidate);
                if (path == null || path.Count == 0) continue;
                point = candidate; error = ""; return true;
            }
            error = "no_walkable_approach"; return false;
        }

        public readonly List<TaskDestination> Targets = new List<TaskDestination>();
        public void Refresh(Transform actor)
        {
            WorldData.Refresh(); Targets.Clear();
            var mapProfile = WorldData.Map != null ? WorldData.Map : UnityEngine.Object.FindObjectOfType<MapWorldProfile>();
            var transforms = UnityEngine.Object.FindObjectsOfType<Transform>();
            foreach (var t in transforms)
            {
                if (actor != null && (t == actor || t.IsChildOf(actor))) continue;
                if (!t.gameObject.scene.IsValid() || !t.gameObject.activeInHierarchy) continue;
                // 大地图合并网格（BLD_* / PROPS / SITE / VEHICLES）不是可导航语义对象：
                // 由 MapWorldProfile 的锚点（中文地点名）提供语义目标，避免 LLM 目标表被 51 条英文网格 ID 污染。
                if (mapProfile != null && t != mapProfile.transform && t.IsChildOf(mapProfile.transform)) continue;
                // 天空罩/碰撞盒辅助对象不是场景目标
                if (t.name == "SovietBlueHourSky") continue;
                var marker = t.GetComponent<TaskDestinationMarker>();
                var renderers = t.GetComponentsInChildren<Renderer>();
                bool geometry = false;
                foreach (var r in renderers) if (r is MeshRenderer && r.enabled) { geometry = true; break; }
                if (!geometry && marker == null) continue;
                string path = ScenePath(t);
                var aliases = new List<string> { t.name, path };
                string id = "scene/" + path, label = t.name;
                if (marker != null)
                {
                    if (!string.IsNullOrWhiteSpace(marker.Id)) id = marker.Id;
                    if (!string.IsNullOrWhiteSpace(marker.DisplayName)) { label = marker.DisplayName; aliases.Add(label); }
                    aliases.AddRange((marker.Aliases ?? "").Split(new[] { ';', '；', ',', '，', '|' }, StringSplitOptions.RemoveEmptyEntries));
                }
                string semanticLabel, description;
                bool semantic = TaskObjectSemantics.Enrich(t, aliases, out semanticLabel, out description);
                if (marker == null && semantic) label = semanticLabel;
                if (t.name == "TrafficLight") aliases.Add("traffic_light_main");
                Targets.Add(new TaskDestination { Id = id, Name = label, Path = path, Description = description, SemanticObject = semantic || marker != null, Aliases = aliases.ToArray(), Object = t,
                    Approach = marker != null ? marker.ApproachPose : null, LookAt = marker != null ? marker.LookAtPose : null, Interaction = t.GetComponent<TaskInteractable>() });
            }
            foreach (var a in WorldData.Anchors)
            {
                if (a.Id == "start_point" || a.Id == "traffic_light_main") continue;
                Targets.Add(new TaskDestination { Id = "anchor/" + a.Id, Name = a.Label, Path = "WorldData/" + a.Id,
                    Aliases = a.Id == "store_door" ? new[] { a.Id, a.Label, "商店门口", "便利店门口", "商店入口", "便利店入口", "店门口" } : a.Id == "street_center" ? new[] { a.Id, a.Label, "道路中间", "道路中央", "路中间", "路中央", "马路中间", "马路中央", "街道中间" } : new[] { a.Id, a.Label }, IsAnchor = true, AnchorPosition = a.Position });

            }
            AddDynamicTargets(actor);
            AddSelectionTarget(actor);
            ReAddTaskBoundTargets();
        }

        static readonly System.Collections.Generic.Dictionary<string, string> DynamicClassAliases =
            new System.Collections.Generic.Dictionary<string, string>
        {
            { "sedan", "轿车" }, { "suv", "越野车" }, { "bus", "公交" },
            { "truck", "卡车" }, { "wreck", "残车" }, { "tram", "有轨电车" },
        };

        static string[] UniqueAliases(System.Collections.Generic.List<string> values)
        {
            var seen = new System.Collections.Generic.HashSet<string>();
            var result = new System.Collections.Generic.List<string>();
            foreach (var v in values)
            {
                if (string.IsNullOrWhiteSpace(v)) continue;
                string key = v.Trim();
                if (!seen.Add(key)) continue;
                result.Add(key);
            }
            return result.ToArray();
        }

        void AddDynamicTargets(Transform actor)
        {
            var world = Yixuan.Dynamic.DynamicWorld.Instance;
            if (world == null || !world.Ready) return;
            foreach (var v in world.Vehicles)
            {
                if (v == null || v.Spec == null || v.Spec.@class == "tram") continue;
                var spec = v.Spec;
                string clsCn = DynamicClassAliases.ContainsKey(spec.@class) ? DynamicClassAliases[spec.@class] : "车辆";
                var aliases = new System.Collections.Generic.List<string>
                    { spec.display_name, spec.name_zh, clsCn, "车", "车辆", spec.area };
                if (spec.color_names != null) aliases.AddRange(spec.color_names);
                foreach (var src in spec.source_objects ?? new string[0]) aliases.Add(src);
                string description = clsCn + " " + spec.display_name +
                    (spec.color_names != null && spec.color_names.Length > 0
                        ? "，颜色：" + string.Join("/", spec.color_names) : "，颜色未知") +
                    (spec.available ? "，可驾驶（走近后可上车/开车）" : "，不可驾驶：" + spec.unavailable_reason);
                Targets.Add(new TaskDestination
                {
                    Id = spec.entity_id,
                    Name = spec.display_name,
                    Path = "dynamic/" + spec.node,
                    Description = description,
                    SemanticObject = true,
                    Aliases = UniqueAliases(aliases),
                    IsAnchor = true,
                    AnchorPosition = v.Center,
                    LivePosition = () => v.Center,
                    LiveApproach = (Vector3 from, out Vector3 point, out string error) =>
                        TryVehicleApproach(v, from, out point, out error),
                });
            }
            if (world.Tram != null && world.Tram.Spec != null)
            {
                var tram = world.Tram;
                var aliases = new System.Collections.Generic.List<string>
                    { "有轨电车", "电车", tram.Spec.display_name, "TRAM" };
                Targets.Add(new TaskDestination
                {
                    Id = tram.Spec.entity_id,
                    Name = tram.Spec.display_name,
                    Path = "dynamic/" + tram.Spec.node,
                    Description = "有轨电车，沿真实轨道自动往返；本轮不支持上下客/驾驶，可观察或查询实时位置",
                    SemanticObject = true,
                    Aliases = aliases.ToArray(),
                    IsAnchor = true,
                    AnchorPosition = tram.Center,
                    LivePosition = () => tram.Center,
                    LiveApproach = (Vector3 from, out Vector3 point, out string error) =>
                        TryTramApproach(tram, from, out point, out error),
                });
            }
            if (world.Flame != null && world.Flame.Spec != null)
            {
                var flame = world.Flame;
                Vector3 flamePosition = new Vector3(flame.Spec.pos[0], flame.Spec.pos[1], flame.Spec.pos[2]);
                var aliases = new System.Collections.Generic.List<string>
                    { "圣火", "长明火", "火焰", "雕像前的火", "长明火火焰", "永恒之火" };
                Targets.Add(new TaskDestination
                {
                    Id = flame.Spec.entity_id,
                    Name = "圣火",
                    Path = "dynamic/" + flame.Spec.node,
                    Description = "祖国母亲雕像前圣火；需要走近操作点才能点燃/熄灭，实时状态读取query_dynamic或flame_known/flame_lit",
                    SemanticObject = true,
                    Aliases = aliases.ToArray(),
                    IsAnchor = true,
                    AnchorPosition = flamePosition,
                    LivePosition = () => flamePosition,
                    LiveApproach = (Vector3 from, out Vector3 point, out string error) =>
                        TryFlameApproach(flame, from, out point, out error),
                });
            }
        }

        static bool TryVehicleApproach(Yixuan.Dynamic.VehicleRuntime v, Vector3 start,
            out Vector3 point, out string error)
        {
            point = Vector3.zero;
            error = null;
            var actor = Yixuan.Dynamic.DynamicActorSystem.Instance;
            if (actor == null) { error = "车辆交互系统尚未就绪"; return false; }
            // Navigation and boarding must resolve the same point in the current vehicle pose.
            if (!actor.TryResolveBoardingPoint(v, start, out point, out var detail))
            { error = "找不到可达车辆靠近点：" + detail; return false; }
            return true;
        }

        static bool TryTramApproach(Yixuan.Dynamic.TramRuntime tram, Vector3 start,
            out Vector3 point, out string error)
        {
            point = Vector3.zero;
            error = null;
            if (tram == null) { error = "有轨电车已不存在"; return false; }
            Vector3 pivot = tram.CurrentPivot;
            for (int ring = 0; ring < 3; ring++)
            {
                float radius = 4.5f + ring * 1.5f;
                for (int i = 0; i < 8; i++)
                {
                    float angle = i * Mathf.PI * 2f / 8f;
                    Vector3 candidate = pivot + new Vector3(Mathf.Cos(angle) * radius, 0f, Mathf.Sin(angle) * radius);
                    candidate.y = WorldData.GroundHeightAt(candidate);
                    if (!WorldData.IsWalkable(candidate)) continue;
                    var path = WorldData.FindPath(start, candidate);
                    if (path == null || path.Count == 0) continue;
                    point = candidate;
                    return true;
                }
            }
            error = "找不到可达有轨电车观察点";
            return false;
        }

        static bool TryFlameApproach(Yixuan.Dynamic.FlameRuntime flame, Vector3 start,
            out Vector3 point, out string error)
        {
            point = Vector3.zero;
            error = null;
            if (flame == null || flame.Spec == null) { error = "圣火已不存在"; return false; }
            foreach (var p in flame.Spec.operation_points ?? new Yixuan.Dynamic.DynFlamePoint[0])
            {
                if (p == null || p.point == null || p.point.Length < 2) continue;
                Vector3 candidate = new Vector3(p.point[0], 0f, p.point[1]);
                candidate.y = WorldData.GroundHeightAt(candidate);
                if (!WorldData.IsWalkable(candidate)) continue;
                var path = WorldData.FindPath(start, candidate);
                if (path == null || path.Count == 0) continue;
                point = candidate;
                return true;
            }
            error = "找不到可达圣火操作点";
            return false;
        }

        public List<TaskDestination> SemanticTargets()
        { var list = Targets.FindAll(x => x.SemanticObject || x.IsAnchor); list.Sort((a,b)=>string.CompareOrdinal(a.Path,b.Path)); return list; }
        public TaskDestination Get(string id)
        { TaskDestination found = null; foreach (var t in Targets) if (t.Id == id) { if (found != null) return null; found = t; } return found; }
        public static string ScenePath(Transform t)
        {
            string p = t.name + "[" + t.GetSiblingIndex() + "]";
            while (t.parent != null) { t = t.parent; p = t.name + "[" + t.GetSiblingIndex() + "]/" + p; }
            return p;
        }
        public bool MatchStart(string text, out string id, out int consumed, out string error)
        {
            id = null; consumed = 0; error = null;
            var matched = new List<TaskDestination>();
            foreach (var t in Targets) foreach (string raw in t.Aliases)
            {
                string alias = raw.Trim();
                if (alias.Length == 0 || !text.StartsWith(alias, StringComparison.OrdinalIgnoreCase)) continue;
                // English identifiers must end at a word boundary.
                if (text.Length > alias.Length && char.IsLetterOrDigit(alias[alias.Length - 1]) && alias[alias.Length - 1] < 128 &&
                    (char.IsLetterOrDigit(text[alias.Length]) && text[alias.Length] < 128 || text[alias.Length] == '_')) continue;
                if (alias.Length > consumed) { consumed = alias.Length; matched.Clear(); }
                if (alias.Length == consumed && !matched.Contains(t)) matched.Add(t);
            }
            if (matched.Count == 0) { error = "找不到目标：" + text; return false; }
            if (matched.Count != 1)
            {
                var paths = new List<string>(); foreach (var t in matched) paths.Add(t.Path);
                error = "目标重名，请指定完整路径：" + string.Join("；", paths.ToArray()); return false;
            }
            if (Get(matched[0].Id) == null) { error = "目标 Id 重复，请修正 TaskDestinationMarker.Id：" + matched[0].Id; return false; }
            id = matched[0].Id; return true;
        }
        public bool FindApproach(TaskDestination t, Vector3 start, out Vector3 approach, out List<Vector3> route, out string error)
        {
            approach = Vector3.zero; route = null; error = null;
            if (t == null || !t.IsAvailable) { error = "目标已不存在"; return false; }
            if (t.LiveApproach != null || t.IsAnchor || t.Approach != null)
            {
                if (t.LiveApproach != null)
                {
                    if (!t.LiveApproach(start, out approach, out error)) return false;
                }
                else approach = t.IsAnchor ? t.AnchorPosition : t.Approach.position;
                approach.y = WorldData.GroundHeightAt(approach);
                route = WorldData.FindPath(start, approach);
                if (route == null || route.Count == 0) { error = "指定站立点不可达：" + t.Name+"；"+WorldData.LastError+"；起点="+start+"，指定点="+approach+"。不会自动替换目标或穿越障碍"; return false; }
                return true;
            }
            Bounds bounds = new Bounds(t.Position, Vector3.zero); bool hasBounds = false;
            foreach (var r in t.Object.GetComponentsInChildren<Renderer>())
            {
                if (!(r is MeshRenderer) || !r.enabled) continue;
                if (!hasBounds) { bounds = r.bounds; hasBounds = true; } else bounds.Encapsulate(r.bounds);
            }
            float best = float.MaxValue;
            // Stand outside the object's footprint and the navigation collision margin.
            for (int ring = 0; ring < 4; ring++)
            {
                float clearance = WorldData.AgentRadius + WorldData.Margin + 0.15f + ring * 0.4f;
                float rx = Mathf.Max(0.05f, bounds.extents.x) + clearance;
                float rz = Mathf.Max(0.05f, bounds.extents.z) + clearance;
                for (int n = 0; n < 24; n++)
                {
                    float angle = n * Mathf.PI * 2 / 24;
                    // Points lie on the expanded rectangle, including its edges, not an ellipse through corners.
                    float dx = Mathf.Sin(angle), dz = Mathf.Cos(angle);
                    float scale = 1f / Mathf.Max(Mathf.Abs(dx), Mathf.Abs(dz));
                    Vector3 p = new Vector3(bounds.center.x + dx * scale * rx, WorldData.GroundY, bounds.center.z + dz * scale * rz);
                    if (!WorldData.IsWalkable(p)) continue;
                    var path = WorldData.FindPath(start, p);
                    if (path == null || path.Count == 0) continue;
                    float length = 0; Vector3 prev = start;
                    foreach (var point in path) { length += FlatDistance(prev, point); prev = point; }
                    float score = length + ring * 0.7f;
                    if (score < best) { best = score; approach = p; route = path; }
                }
            }
            if (route == null) { error = "找不到可达靠近点：" + t.Name; return false; }
            return true;
        }
        public static float FlatDistance(Vector3 a, Vector3 b) { a.y = b.y = 0; return Vector3.Distance(a, b); }
    }
}
