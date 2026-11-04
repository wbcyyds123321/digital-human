using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEngine;
using Yixuan.World;

namespace Yixuan.AI.Tasks
{
    public sealed class TaskDestination
    {
        public string Id, Name, Path, Description;
        public bool SemanticObject;
        public string[] Aliases;
        public Transform Object, Approach, LookAt;
        public TaskInteractable Interaction;
        public bool IsAnchor;
        public Vector3 AnchorPosition;
        public Vector3 Position { get { return Object != null ? Object.position : AnchorPosition; } }
        public DestinationSnapshot Snapshot()
        { return new DestinationSnapshot { id = Id, name = Name, path = Path, aliases = Aliases, description = Description, position = Position, interactable = Interaction != null }; }
    }
    public sealed class TaskTargetCatalog
    {
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
            if (t == null || !t.IsAnchor && t.Object == null) { error = "目标已不存在"; return false; }
            if (t.IsAnchor || t.Approach != null)
            {
                approach = t.IsAnchor ? t.AnchorPosition : t.Approach.position; approach.y = WorldData.GroundY;
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
