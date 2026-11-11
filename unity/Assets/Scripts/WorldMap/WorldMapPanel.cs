using System;
using System.Collections.Generic;
using UnityEngine;
using Yixuan.AI.Tasks;
using Yixuan.Motion;
using Yixuan.Pick;
using Yixuan.World;

namespace Yixuan.WorldMap
{
    /// <summary>
    /// Native IMGUI fine map for the unified HUD map tab.
    ///
    /// The panel is deliberately read-mostly: it displays the generated basemap
    /// and entity inventory, but navigation always goes through
    /// CharacterLocomotion / WorldData. It does not teleport the character and
    /// never invents an anchor for arbitrary points.
    /// </summary>
    public static class WorldMapPanel
    {
        enum Mode { View, Go }

        static bool _initTried;
        static bool _ready;
        static string _loadError;
        static CharacterLocomotion _loco;
        static Yixuan.Core.YixuanCharacter _character;
        static Camera _eyeCamera;
        static MapWorldProfile _map;
        static GUIStyle _box, _title, _small, _label, _btn, _btnOn, _text, _field;
        static Texture2D _dot;
        static Vector2 _center = new Vector2(0f, 2f);
        static float _zoom = 1f;
        static bool _follow = true;
        static bool _dragging;
        static Vector2 _dragLast;
        static float _dragDistance;
        static Rect _mapRect;
        static Rect _panelRect;
        static Mode _mode = Mode.Go;
        static string _search = "";
        static WorldMapData.Entity _selected;
        static Vector2 _selectedPoint;
        static bool _hasSelectedPoint;
        static int _lastSharedSelectionVersion = -1;
        static bool _selectionFromShared;
        static readonly HashSet<string> _groups = new HashSet<string>(StringComparer.Ordinal);
        static readonly List<WorldMapData.Entity> _matches = new List<WorldMapData.Entity>();
        static List<Vector3> _route;
        static Vector3 _target;
        static bool _hasTarget;
        static CharacterLocomotion _navigationLoco;
        static ulong _navigationGeneration;
        static string _navigationOwner;
        static int _pointerControl;
        static Vector2 _searchScroll;
        public static bool Expanded { get; set; }
        static string _status = "点击地图任意可走地面前往；查看模式只查询不移动。";
        static string _statusKind = "idle";

        static bool _pointerVisible;
        public static bool CameraInputBlocked
        {
            get
            {
                if (Yixuan.UI.BigMapPanel.InputBlocked) return true;
                Vector3 pointer = Input.mousePosition;
                return _pointerVisible && Application.isFocused &&
                    (_dragging || _panelRect.Contains(new Vector2(pointer.x, Screen.height - pointer.y)));
            }
        }

        public static void CancelPointerCapture()
        {
            if (_pointerControl != 0 && GUIUtility.hotControl == _pointerControl) GUIUtility.hotControl = 0;
            _pointerControl = 0;
            _dragging = false;
            _dragDistance = 0f;
            _pointerVisible = false;
        }

        /// <summary>True while the map panel owns a real, currently moving target.</summary>
        public static bool OwnsNavigation(CharacterLocomotion loco)
        {
            return MatchesNavigation(loco) && loco.HasTarget;
        }

        static bool MatchesNavigation(CharacterLocomotion loco)
        {
            return _hasTarget && loco != null && loco == _navigationLoco &&
                   loco.NavigationGeneration == _navigationGeneration && loco.MotionOwner == _navigationOwner;
        }

        public static bool StopNavigation(string reason = "地图停止")
        {
            if (!OwnsNavigation(_navigationLoco) || !TaskAuthority.Allows(1)) return false;
            _navigationLoco.Stop(reason, "cancelled");
            _hasTarget = false;
            _route = null;
            _status = "已停止地图导航。"; _statusKind = "cancelled";
            return true;
        }

        public static void Tick() { UpdateNavStatus(); }

        // ---- shared navigation API for the big map (same model/guards) ----

        public static bool HasNavigation { get { return _hasTarget; } }
        public static Vector3 CurrentTarget { get { return _target; } }
        public static string StatusText { get { return _status; } }
        public static string StatusKind { get { return _statusKind; } }
        /// <summary>Read-only diagnostic: entity id currently selected on the minimap.</summary>
        public static string SelectedEntityIdForTest { get { return _selected != null ? _selected.id : ""; } }

        static void EnsureRefs()
        {
            if (_loco == null) _loco = UnityEngine.Object.FindObjectOfType<CharacterLocomotion>();
            if (_character == null) _character = UnityEngine.Object.FindObjectOfType<Yixuan.Core.YixuanCharacter>();
            if (_map == null) _map = UnityEngine.Object.FindObjectOfType<MapWorldProfile>();
            if (_navigationLoco == null && _loco != null && _loco.HasTarget) _navigationLoco = _loco;
        }

        /// <summary>Copy the remaining real route of the map-owned navigation, if any.</summary>
        public static bool CopyRemainingRoute(List<Vector3> buffer)
        {
            if (buffer == null) return false;
            buffer.Clear();
            EnsureRefs();
            if (!_hasTarget || _navigationLoco == null || _loco != _navigationLoco) return false;
            if (!_navigationLoco.HasTarget || !MatchesNavigation(_navigationLoco)) return false;
            _navigationLoco.CopyRemainingPath(buffer);
            return buffer.Count > 0;
        }

        /// <summary>Validate and plan a route without moving (big-map preview).</summary>
        public static bool PreviewPath(Vector3 from, Vector2 world, out List<Vector3> path, out string message)
        {
            EnsureRefs();
            path = null;
            message = "";
            if (world.x < WorldMapData.MinX || world.x > WorldMapData.MaxX ||
                world.y < WorldMapData.MinZ || world.y > WorldMapData.MaxZ)
            {
                message = "目标超出地图范围，未规划。";
                return false;
            }
            Vector3 target = new Vector3(world.x, WorldData.GroundHeightAt(new Vector3(world.x, 0f, world.y)), world.y);
            if (!WorldData.IsInsideBounds(target) || !WorldData.IsWalkable(target))
            {
                message = "目标不可走（障碍/建筑内）：" + target.ToString("F2");
                return false;
            }
            path = WorldData.FindPath(from, target);
            if (path == null || path.Count == 0)
            {
                message = "无法到达该点：" + (WorldData.LastError ?? "无路径");
                return false;
            }
            message = "已规划 " + path.Count + " 个路点，前往 (" + target.x.ToString("F1") +
                      ", " + target.z.ToString("F1") + ")";
            return true;
        }

        /// <summary>Big-map entry point: same guards/ownership as the debug map click.</summary>
        public static bool TryNavigateTo(Vector2 world, out string status, out string kind)
        {
            EnsureRefs();
            TryNavigate(world);
            status = _status;
            kind = _statusKind;
            return _statusKind == "moving";
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetSession() { Shutdown(); }

        public static void UpdatePointerBlock(Rect panel, bool visible)
        {
            if (!visible || !Application.isFocused) { CancelPointerCapture(); return; }
            _pointerVisible = true; _panelRect = panel;
        }

        static void EnsureStyles()
        {
            if (_box != null) return;
            _box = new GUIStyle(GUI.skin.box);
            _title = new GUIStyle(GUI.skin.label) { fontSize = 13, fontStyle = FontStyle.Bold };
            _title.normal.textColor = new Color(0.90f, 0.94f, 1f);
            _label = new GUIStyle(GUI.skin.label) { fontSize = 11, wordWrap = true };
            _label.normal.textColor = new Color(0.80f, 0.86f, 0.94f);
            _small = new GUIStyle(GUI.skin.label) { fontSize = 10, wordWrap = true };
            _small.normal.textColor = new Color(0.58f, 0.66f, 0.78f);
            _btn = new GUIStyle(GUI.skin.button) { fontSize = 10 };
            _btnOn = new GUIStyle(GUI.skin.button) { fontSize = 10 };
            _btnOn.normal.textColor = new Color(1f, 0.90f, 0.58f);
            _text = new GUIStyle(GUI.skin.textField) { fontSize = 11 };
            _field = new GUIStyle(GUI.skin.textField) { fontSize = 12 };
            _dot = DotTexture();
            _mapRect = new Rect(0, 0, 100, 100);
            _panelRect = _mapRect;
        }

        static Texture2D DotTexture()
        {
            var t = new Texture2D(7, 7, TextureFormat.RGBA32, false);
            for (int y = 0; y < 7; y++)
                for (int x = 0; x < 7; x++)
                {
                    float d = Vector2.Distance(new Vector2(x, y), new Vector2(3, 3));
                    t.SetPixel(x, y, d <= 3.2f ? Color.white : new Color(1, 1, 1, 0));
                }
            t.Apply(false, false);
            return t;
        }

        public static bool Load()
        {
            // A rejected lazy call in the explicit lab must not cache a false
            // result into _initTried; the main-scene panel would stay dead after
            // returning. Do not write any global init state here.
            if (Yixuan.SelectionLab.SelectionLabContext.IsActive)
            {
                _loadError = "explicit_scene_uses_lab_minimap";
                return false;
            }
            if (_initTried) return _ready;
            _initTried = true;
            _ready = WorldMapData.Load();
            _loadError = WorldMapData.Error;
            if (_ready)
            {
                _center = new Vector2((WorldMapData.MinX + WorldMapData.MaxX) * 0.5f,
                                      (WorldMapData.MinZ + WorldMapData.MaxZ) * 0.5f);
                foreach (var g in WorldMapData.Data.groups) _groups.Add(g.id);
            }
            return _ready;
        }

        public static void Shutdown()
        {
            CancelPointerCapture();
            if (_dot != null) UnityEngine.Object.Destroy(_dot);
            _dot = null;
            _box = _title = _small = _label = _btn = _btnOn = _text = _field = null;
            _loco = _navigationLoco = null; _character = null; _eyeCamera = null; _map = null;
            _selected = null; _hasSelectedPoint = false; _search = "";
            _matches.Clear(); _groups.Clear(); _searchScroll = Vector2.zero;
            _follow = true; _zoom = 1f; Expanded = false;
            _mode = Mode.Go;
            _status = "前往：点击地面行走；查看：点击物体查询。"; _statusKind = "idle";
            _initTried = false;
            _ready = false;
            _route = null;
            _hasTarget = false;
        }

        public static bool Draw(Rect content, CharacterLocomotion loco, Yixuan.Core.YixuanCharacter character, MapWorldProfile map)
        {
            _loco = loco;
            _character = character;
            _map = map;
            if (!Load())
            {
                GUI.Label(content, "精细地图数据未就绪：" + (_loadError ?? "unknown"), _label);
                return false;
            }
            EnsureStyles();
            _panelRect = content;
            _pointerVisible = true;
            SyncSharedSelection();

            // Toolbar
            float y = content.y;
            GUI.Label(new Rect(content.x, y, 90, 20), "精细世界地图", _title);
            if (GUI.Button(new Rect(content.x + 108, y, 58, 20), _mode == Mode.Go ? "前往" : "查看", _btnOn))
                _mode = _mode == Mode.Go ? Mode.View : Mode.Go;
            if (GUI.Button(new Rect(content.x + 170, y, 52, 20), "跟随", _follow ? _btnOn : _btn))
            {
                _follow = !_follow;
                if (_follow) _center = CharacterXZ();
            }
            if (GUI.Button(new Rect(content.x + 226, y, 48, 20), "复位", _btn))
            {
                _center = new Vector2((WorldMapData.MinX + WorldMapData.MaxX) * 0.5f,
                                      (WorldMapData.MinZ + WorldMapData.MaxZ) * 0.5f);
                _zoom = 1f;
                _follow = false;
            }
            if (GUI.Button(new Rect(content.x + 278, y, 46, 20), "−", _btn)) _zoom = Mathf.Max(1f, _zoom / 1.2f);
            GUI.Label(new Rect(content.x + 328, y, 38, 20), "×" + _zoom.ToString("0.0"), _small);
            if (GUI.Button(new Rect(content.x + 368, y, 46, 20), "+", _btn)) _zoom = Mathf.Min(12f, _zoom * 1.2f);
            y += 24;
            if (GUI.Button(new Rect(content.x, y, 76, 20), Expanded ? "收起大图" : "展开大图", _btn))
                Expanded = !Expanded;
            GUI.Label(new Rect(content.x + 82, y, content.width - 82, 20),
                _mode == Mode.Go ? "前往：直接点地面；拖动平移，滚轮缩放" : "查看：点标记或搜索名称 / ID", _small);
            y += 24;

            // Search + layer toggles
            GUI.Label(new Rect(content.x, y, 34, 20), "搜索", _small);
            _search = GUI.TextField(new Rect(content.x + 38, y, content.width - 84, 20), _search, 80, _field);
            if (GUI.Button(new Rect(content.x + content.width - 42, y, 38, 20), "清空", _btn))
            {
                _search = "";
                _selected = null;
                _matches.Clear();
            }
            y += 23;
            y += DrawGroupToggles(content.x, y, content.width);
            if (!string.IsNullOrWhiteSpace(_search))
            {
                UpdateMatches();
                float resultsH = Mathf.Min(72f, Mathf.Max(22f, content.height * 0.14f));
                GUI.Label(new Rect(content.x, y, content.width, 17), "找到 " + _matches.Count + " 项（仅定位，不行走）", _small);
                y += 18;
                var results = new Rect(content.x, y, content.width, resultsH);
                _searchScroll = GUI.BeginScrollView(results, _searchScroll, new Rect(0, 0, results.width - 18, Mathf.Max(resultsH, _matches.Count * 21f)));
                for (int i = 0; i < _matches.Count; i++)
                {
                    var match = _matches[i];
                    if (GUI.Button(new Rect(0, i * 21f, results.width - 20, 20), match.name + "  " + match.id, _btn))
                    {
                        _selected = match; _hasSelectedPoint = false;
                        _center = new Vector2(match.pos[0], match.pos[2]);
                        _groups.Add(match.group); _follow = false; _zoom = Mathf.Max(3f, _zoom);
                        _status = "已定位：" + Describe(match); _statusKind = "selected";
                    }
                }
                GUI.EndScrollView();
                y += resultsH + 3;
            }

            // Map rectangle: leave room for detail/status at bottom.
            if (_follow) _center = CharacterXZ();
            ClampCenter();
            float detailH = 100f;
            float available = Mathf.Max(40f, content.yMax - y - detailH);
            float mapH = Expanded ? available : Mathf.Min(content.width * 0.76f, available);
            _mapRect = new Rect(content.x + 2, y, content.width - 4, mapH);
            HandleMapInput();
            DrawMap();
            y = _mapRect.yMax + 4;

            DrawBottom(content, y);
            return true;
        }

        static float DrawGroupToggles(float x, float y, float width)
        {
            var groups = WorldMapData.Data.groups;
            int columns = Mathf.Max(1, Mathf.Min(groups.Length, Mathf.FloorToInt(width / 64f)));
            float bw = (width - (columns - 1) * 2f) / columns;
            for (int i = 0; i < groups.Length; i++)
            {
                var g = groups[i];
                bool on = _groups.Contains(g.id);
                if (GUI.Button(new Rect(x + (i % columns) * (bw + 2f), y + (i / columns) * 22f, bw, 19), g.name, on ? _btnOn : _btn))
                {
                    if (on) _groups.Remove(g.id); else _groups.Add(g.id);
                }
            }
            return Mathf.CeilToInt(groups.Length / (float)columns) * 22f;
        }

        static void HandleMapInput()
        {
            var e = Event.current;
            if (e == null) return;
            int control = GUIUtility.GetControlID("WorldMapDrag".GetHashCode(), FocusType.Passive);
            bool inside = _mapRect.Contains(e.mousePosition);
            if (e.rawType == EventType.MouseUp && e.button == 0 && _dragging)
            {
                _dragging = false;
                if (GUIUtility.hotControl == _pointerControl) GUIUtility.hotControl = 0;
                _pointerControl = 0;
                if (inside && _dragDistance < 5f) HandleMapClick(e.mousePosition - _mapRect.position);
                _dragDistance = 0f;
                e.Use();
                return;
            }
            if (!inside && !_dragging) return;
            Vector2 local = e.mousePosition - _mapRect.position;
            if (e.type == EventType.ScrollWheel && inside)
            {
                Vector2 before = MapToWorld(local);
                float factor = e.delta.y < 0f ? 1.2f : 1f / 1.2f;
                _zoom = Mathf.Clamp(_zoom * factor, 1f, 12f);
                Vector2 after = MapToWorld(local);
                _center += before - after;
                _follow = false;
                ClampCenter();
                e.Use();
                return;
            }
            if (e.type == EventType.MouseDown && e.button == 0 && GUIUtility.hotControl == 0)
            {
                _pointerControl = control; GUIUtility.hotControl = control;
                _dragging = true;
                _dragLast = local;
                _dragDistance = 0f;
                e.Use();
                return;
            }
            if (e.type == EventType.MouseDrag && _dragging)
            {
                Vector2 delta = local - _dragLast;
                _dragLast = local;
                _dragDistance += delta.magnitude;
                float scale = MapScale();
                _center -= new Vector2(delta.x / scale, -delta.y / scale);
                _follow = false;
                ClampCenter();
                e.Use();
                return;
            }
        }

        /// <summary>
        /// Minimap entity selection production entry. Publishes through the same
        /// shared selection interface as the big map and manual confirm; a new
        /// manual/map selection advances the shared generation (old AI token is
        /// invalidated). Source stays "map_click": this is not an image confirm.
        /// </summary>
        public static bool SelectEntityById(string id)
        {
            var entry = PickService.ConfirmEntity(id, "map_click");
            if (entry == null)
            {
                _status = "选择失败：" + (PickService.LastError ?? "publish_rejected");
                _statusKind = "fail";
                return false;
            }
            if (WorldMapData.Data != null)
            {
                foreach (var entity in WorldMapData.Data.entities)
                {
                    if (entity.id != id) continue;
                    _selected = entity;
                    _selectionFromShared = true;
                    _hasSelectedPoint = false;
                    _status = "已选中：" + Describe(entity);
                    _statusKind = "selected";
                    break;
                }
            }
            return true;
        }

        static void HandleMapClick(Vector2 local)
        {
            var hit = _mode == Mode.View ? NearestEntity(local) : null;
            if (hit != null)
            {
                _selected = hit;
                _hasSelectedPoint = false;
                _status = "查看：" + Describe(hit);
                _statusKind = "selected";
                // A map click is an explicit entity selection; publish it through
                // the same shared selection interface used by the crosshair confirm.
                if (!SelectEntityById(hit.id))
                {
                    _selected = null;
                    _selectionFromShared = false;
                }
                return;
            }
            Vector2 world = MapToWorld(local);
            if (_mode == Mode.View)
            {
                _selected = null;
                _selectedPoint = world;
                _hasSelectedPoint = true;
                _status = "查看地面：(" + world.x.ToString("F1") + ", " + world.y.ToString("F1") + ")";
                _statusKind = "selected";
                return;
            }
            _selected = null;
            _hasSelectedPoint = true;
            _selectedPoint = world;
            TryNavigate(world);
        }

        /// <summary>Consumer refresh entry used by Draw; exposed so a test can force the same sync deterministically.</summary>
        public static void SyncSharedSelectionNow()
        {
            SyncSharedSelection();
        }

        /// <summary>
        /// Minimap consumes the same confirmed selection record as the
        /// first-person highlight, big map and LLM/task facts.
        /// </summary>
        static void SyncSharedSelection()
        {
            if (SelectionBroker.Version == _lastSharedSelectionVersion) return;
            _lastSharedSelectionVersion = SelectionBroker.Version;
            var record = SelectionBroker.Current;
            if (record == null || !record.confirmed)
            {
                if (_selectionFromShared)
                {
                    _selected = null;
                    _selectionFromShared = false;
                    _hasSelectedPoint = false;
                    _statusKind = "idle";
                }
                return;
            }
            if (WorldMapData.Data == null) return;
            foreach (var entity in WorldMapData.Data.entities)
            {
                if (entity.id != record.entity_id) continue;
                _selected = entity;
                _selectionFromShared = true;
                _hasSelectedPoint = false;
                _status = "已选中：" + Describe(entity);
                _statusKind = "selected";
                return;
            }
        }

        static WorldMapData.Entity NearestEntity(Vector2 local)
        {
            if (WorldMapData.Data == null) return null;
            float best = 5f * 5f;
            WorldMapData.Entity found = null;
            foreach (var e in WorldMapData.Data.entities)
            {
                if (!_groups.Contains(e.group)) continue;
                Vector2 p = WorldToMap(new Vector2(e.pos[0], e.pos[2]));
                float d = (p - local).sqrMagnitude;
                if (d < best) { best = d; found = e; }
            }
            return found;
        }

        static void TryNavigate(Vector2 world)
        {
            if (_loco == null || _character == null) { _status = "导航不可用：缺少角色/移动控制器"; _statusKind = "fail"; return; }
            if (Stage4.Stage4SeatController.BlocksNormalWalking)
            {
                _status = "当前坐着，先起身再导航。"; _statusKind = "fail"; return;
            }
            if (!TaskAuthority.Allows(3) || !TaskAuthority.Allows(1))
            {
                _status = "身体/移动被其他任务占用，未启动地图导航。"; _statusKind = "fail"; return;
            }
            if (world.x < WorldMapData.MinX || world.x > WorldMapData.MaxX ||
                world.y < WorldMapData.MinZ || world.y > WorldMapData.MaxZ)
            {
                _status = "目标超出地图范围，未启动。"; _statusKind = "fail"; return;
            }
            Vector3 target = new Vector3(world.x, WorldData.GroundHeightAt(new Vector3(world.x, 0f, world.y)), world.y);
            if (!WorldData.IsInsideBounds(target))
            {
                _status = "目标超出导航可走范围。"; _statusKind = "fail"; return;
            }
            if (!WorldData.IsWalkable(target))
            {
                _status = "目标不可走（障碍/建筑内）：" + target.ToString("F2"); _statusKind = "fail"; return;
            }
            var path = WorldData.FindPath(_character.transform.position, target);
            if (path == null || path.Count == 0)
            {
                _status = "无法到达该点：" + (WorldData.LastError ?? "无路径"); _statusKind = "fail"; return;
            }
            if (!_loco.MoveTo(target))
            {
                _status = "启动失败：" + (_loco.LastError ?? _loco.LastStatus); _statusKind = "fail"; return;
            }
            _route = path;
            _navigationLoco = _loco;
            _navigationGeneration = _loco.NavigationGeneration;
            _navigationOwner = _loco.MotionOwner;
            _loco.CopyRemainingPath(_route);
            _target = target;
            _hasTarget = true;
            _statusKind = "moving";
            _status = "已规划 " + path.Count + " 个路点，前往 (" + target.x.ToString("F1") + ", " + target.z.ToString("F1") + ")";
        }

        static void DrawMap()
        {
            GUI.BeginGroup(_mapRect);
            Rect localRect = new Rect(0, 0, _mapRect.width, _mapRect.height);
            GUI.DrawTexture(localRect, Texture2D.blackTexture);
            var bmin = new Vector2(WorldMapData.MinX, WorldMapData.MinZ);
            var bmax = new Vector2(WorldMapData.MaxX, WorldMapData.MaxZ);
            var imgRect = WorldMapProjection.ImageRect(_mapRect.size, _center, bmin, bmax, _zoom);
            if (WorldMapData.Basemap != null)
                GUI.DrawTexture(imgRect, WorldMapData.Basemap, ScaleMode.StretchToFill, false);

            // Map edge
            DrawLineLocal(new Vector2(0, 0), new Vector2(localRect.width, 0), 1f, new Color(0.34f, 0.42f, 0.52f));
            DrawLineLocal(new Vector2(0, 0), new Vector2(0, localRect.height), 1f, new Color(0.34f, 0.42f, 0.52f));
            DrawLineLocal(new Vector2(localRect.width, 0), new Vector2(localRect.width, localRect.height), 1f, new Color(0.34f, 0.42f, 0.52f));
            DrawLineLocal(new Vector2(0, localRect.height), new Vector2(localRect.width, localRect.height), 1f, new Color(0.34f, 0.42f, 0.52f));

            // Entities
            int drawn = 0, labels = 0;
            bool showLabels = _zoom >= 2.0f;
            foreach (var ent in WorldMapData.Data.entities)
            {
                if (!_groups.Contains(ent.group)) continue;
                Vector2 p = WorldToMap(new Vector2(ent.pos[0], ent.pos[2]));
                if (p.x < -8 || p.y < -8 || p.x > localRect.width + 8 || p.y > localRect.height + 8) continue;
                bool selected = _selected != null && _selected.id == ent.id;
                float r = selected ? 5.5f : (ent.group == "building" ? 3.8f : 2.9f);
                Color c = GroupColor(ent.group);
                if (selected) c = new Color(1f, 0.95f, 0.55f, 1f);
                DrawCircleLocal(p, r, c, 0f);
                drawn++;
                if (selected || (showLabels && labels < 28 && (ent.group != "scatter" || _zoom >= 4f)))
                {
                    GUI.color = selected ? Color.white : new Color(0.85f, 0.9f, 1f, 0.9f);
                    GUI.Label(new Rect(p.x + 5, p.y - 8, 150, 16), Trim(ent.name, 16), _small);
                    GUI.color = Color.white;
                    labels++;
                }
            }

            // Player
            if (_character != null)
            {
                Vector2 p = WorldToMap(new Vector2(_character.transform.position.x, _character.transform.position.z));
                DrawArrowLocal(p, _character.transform.eulerAngles.y, 18f, new Color(0.3f, 0.9f, 1f, 1f));
                DrawCircleLocal(p, 3f, new Color(0.2f, 1f, 0.6f, 1f), 0f);
                if (_eyeCamera == null)
                {
                    var eyeObject = GameObject.Find("EyeCamera");
                    if (eyeObject != null) _eyeCamera = eyeObject.GetComponent<Camera>();
                }
                if (_eyeCamera != null)
                {
                    Vector3 f = _eyeCamera.transform.forward; f.y = 0f;
                    if (f.sqrMagnitude > 0.001f)
                    {
                        float eyeYaw = Mathf.Atan2(f.x, f.z) * Mathf.Rad2Deg;
                        DrawArrowLocal(p, eyeYaw, 12f, new Color(1f, 0.62f, 0.3f, 0.8f));
                    }
                }
            }
            // Draw the actual executed/planned route above entity dots so it is readable.
            if (_route != null && _route.Count > 0)
            {
                Vector2 prev = WorldToMap(new Vector2(_character != null ? _character.transform.position.x : _route[0].x,
                                                      _character != null ? _character.transform.position.z : _route[0].z));
                for (int i = 0; i < _route.Count; i++)
                {
                    Vector2 cur = WorldToMap(new Vector2(_route[i].x, _route[i].z));
                    DrawLineLocal(prev, cur, 3f, new Color(1f, 0.55f, 0.12f, 1f));
                    prev = cur;
                }
                Vector2 t = WorldToMap(new Vector2(_target.x, _target.z));
                DrawCircleLocal(t, 7f, new Color(1f, 0.35f, 0.15f, 1f), 0f);
            }
            GUI.Label(new Rect(5, 3, 240, 15), "实体 " + drawn + " · 北 ↑ · 青=身体 橙=眼睛", _small);
            GUI.EndGroup();
        }

        static void DrawBottom(Rect content, float y)
        {
            float h = content.yMax - y;
            if (h < 20f) return;
            if (_selected != null)
            {
                GUI.Label(new Rect(content.x, y, content.width, 18), _selected.name + "  [" + _selected.id + "]", _label);
                GUI.Label(new Rect(content.x, y + 17, content.width, 16),
                    "坐标 (" + _selected.pos[0].ToString("F1") + ", " + _selected.pos[2].ToString("F1") + ") · 类别 " + _selected.cls +
                    " · 高 " + _selected.height.ToString("F2") + "m", _small);
                if (GUI.Button(new Rect(content.x + content.width - 54, y, 50, 20), "前往", _btn))
                {
                    _hasSelectedPoint = true;
                    _selectedPoint = new Vector2(_selected.pos[0], _selected.pos[2]);
                    _mode = Mode.Go;
                    TryNavigate(_selectedPoint);
                }
                y += 38;
            }
            else if (_hasSelectedPoint)
            {
                GUI.Label(new Rect(content.x, y, content.width, 18), "查看地面点 (" + _selectedPoint.x.ToString("F1") + ", " + _selectedPoint.y.ToString("F1") + ")", _label);
                y += 20;
            }
            bool previousEnabled = GUI.enabled;
            GUI.enabled = previousEnabled && OwnsNavigation(_loco) && TaskAuthority.Allows(1);
            if (GUI.Button(new Rect(content.x, y, 86, 25), "停止地图导航", _btn))
            {
                StopNavigation();
            }
            GUI.enabled = previousEnabled;
            Color statusColor = _statusKind == "fail" ? new Color(1f, 0.62f, 0.42f) :
                                (_statusKind == "arrived" ? new Color(0.5f, 1f, 0.62f) :
                                 (_statusKind == "moving" ? new Color(1f, 0.86f, 0.45f) : new Color(0.8f, 0.86f, 0.94f)));
            GUI.color = statusColor;
            GUI.Label(new Rect(content.x + 92, y, content.width - 92, 34), _status, _small);
            GUI.color = Color.white;
            if (_hasTarget && _loco != null)
            {
                float d = Vector3.Distance(_character != null ? _character.transform.position : _target, _target);
                GUI.Label(new Rect(content.x, y + 34, content.width, 18),
                    "目标 (" + _target.x.ToString("F1") + ", " + _target.z.ToString("F1") + ")  距离 " + d.ToString("F2") + "m", _small);
            }
        }

        static void UpdateNavStatus()
        {
            if (!_hasTarget) return;
            var motor = _navigationLoco;
            if (!MatchesNavigation(motor))
            {
                _hasTarget = false; _route = null;
                _status = "地图路线已结束或被其他导航接管。"; _statusKind = "cancelled";
                return;
            }
            if (motor.HasTarget)
            {
                if (_route == null) _route = new List<Vector3>();
                motor.CopyRemainingPath(_route);
                if (_statusKind == "moving")
                    _status = motor.IsMoving ? "行进中：剩余路线 " + _route.Count + " 点" : "地图导航已暂停（走路关闭）。";
                return;
            }
            _hasTarget = false; _route = null;
            if (motor.LastStatus == "arrived")
            {
                Vector3 delta = motor.transform.position - _target; delta.y = 0;
                _status = "已到达，平面误差 " + delta.magnitude.ToString("F2") + "m"; _statusKind = "arrived";
            }
            else
            {
                _status = "地图导航结束：" + (motor.LastError ?? motor.LastStatus);
                _statusKind = motor.LastStatus == "cancelled" ? "cancelled" : "fail";
            }
        }

        static void UpdateMatches()
        {
            if (WorldMapData.Data == null) return;
            string q = _search.Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(q)) { _matches.Clear(); return; }
            _matches.Clear();
            foreach (var e in WorldMapData.Data.entities)
            {
                if (E(e.name).Contains(q) || E(e.id).Contains(q) || E(e.english).Contains(q) ||
                    E(e.kind).Contains(q) || AnyAlias(e, q))
                    _matches.Add(e);
            }
            _matches.Sort((a, b) => string.CompareOrdinal(a.name, b.name));
        }

        static bool AnyAlias(WorldMapData.Entity e, string q)
        {
            if (e.aliases == null) return false;
            foreach (var a in e.aliases) if (E(a).Contains(q)) return true;
            return false;
        }

        static string E(string s) { return (s ?? "").ToLowerInvariant(); }

        static Vector2 CharacterXZ()
        {
            return _character != null ? new Vector2(_character.transform.position.x, _character.transform.position.z)
                                      : new Vector2(0f, 2f);
        }

        static Vector2 WorldToMap(Vector2 world)
        {
            return WorldMapProjection.WorldToMap(world, _mapRect.size, _center, WorldSpan(), _zoom);
        }

        static Vector2 MapToWorld(Vector2 local)
        {
            return WorldMapProjection.MapToWorld(local, _mapRect.size, _center, WorldSpan(), _zoom);
        }

        static Vector2 WorldSpan()
        {
            return new Vector2(WorldMapData.MaxX - WorldMapData.MinX, WorldMapData.MaxZ - WorldMapData.MinZ);
        }

        static float MapScale()
        {
            return WorldMapProjection.Scale(_mapRect.size, WorldSpan(), _zoom);
        }

        static void ClampCenter()
        {
            _center.x = Mathf.Clamp(_center.x, WorldMapData.MinX, WorldMapData.MaxX);
            _center.y = Mathf.Clamp(_center.y, WorldMapData.MinZ, WorldMapData.MaxZ);
        }

        static void DrawCircleLocal(Vector2 p, float r, Color c, float ring)
        {
            GUI.color = c;
            GUI.DrawTexture(new Rect(p.x - r, p.y - r, r * 2, r * 2), _dot);
            GUI.color = Color.white;
        }

        static void DrawLineLocal(Vector2 a, Vector2 b, float width, Color c)
        {
            Vector2 d = b - a;
            if (d.sqrMagnitude < 0.01f) return;
            float len = d.magnitude;
            var old = GUI.matrix;
            GUI.color = c;
            GUIUtility.RotateAroundPivot(Mathf.Atan2(d.y, d.x) * Mathf.Rad2Deg, a);
            GUI.DrawTexture(new Rect(a.x, a.y - width * 0.5f, len, width), Texture2D.whiteTexture);
            GUI.matrix = old;
            GUI.color = Color.white;
        }

        static void DrawArrowLocal(Vector2 p, float yaw, float size, Color c)
        {
            float angle = yaw * Mathf.Deg2Rad;
            Vector2 forward = new Vector2(Mathf.Sin(angle), -Mathf.Cos(angle));
            Vector2 right = new Vector2(-forward.y, forward.x);
            Vector2 tip = p + forward * size;
            Vector2 a = p - forward * (size * 0.3f) + right * (size * 0.4f);
            Vector2 b = p - forward * (size * 0.3f) - right * (size * 0.4f);
            DrawLineLocal(a, tip, 2f, c); DrawLineLocal(tip, b, 2f, c); DrawLineLocal(b, a, 2f, c);
        }

        static Color GroupColor(string group)
        {
            switch (group)
            {
                case "building": return new Color(0.78f, 0.66f, 0.48f, 1f);
                case "bench": return new Color(0.84f, 0.55f, 0.26f, 1f);
                case "vehicle": return new Color(0.30f, 0.62f, 0.78f, 1f);
                case "green": return new Color(0.42f, 0.70f, 0.38f, 1f);
                case "lighting": return new Color(0.82f, 0.72f, 0.36f, 1f);
                case "leisure": return new Color(0.80f, 0.52f, 0.60f, 1f);
                case "scatter": return new Color(0.55f, 0.58f, 0.45f, 1f);
                default: return new Color(0.60f, 0.68f, 0.72f, 1f);
            }
        }

        static string Describe(WorldMapData.Entity e)
        {
            return e.name + " [" + e.id + "] (" + e.pos[0].ToString("F1") + ", " + e.pos[2].ToString("F1") + ")";
        }

        static string Trim(string s, int n)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Length <= n ? s : s.Substring(0, n) + "…";
        }
    }
}
