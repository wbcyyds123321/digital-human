using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using Yixuan.Motion;
using Yixuan.Stage4;
using Yixuan.World;
using Yixuan.WorldMap;

namespace Yixuan.UI
{
    /// <summary>
    /// Large, usable world map layer. Builds its whole UI in code (uGUI Canvas +
    /// dedicated UI camera) so it does not require scene/prefab edits, can be
    /// captured to a RenderTexture in batchmode, and shares the exact same map
    /// model and navigation guards as the debug IMGUI map (WorldMapData /
    /// WorldMapProjection / WorldMapPanel.TryNavigateTo).
    ///
    /// Default layer = the generated simplified plan basemap + category icons.
    /// The entity-AABB layer is a separate, explicitly-labelled debug toggle.
    /// </summary>
    public sealed class BigMapPanel : MonoBehaviour
    {
        // ---------------- public state / API ----------------
        public static BigMapPanel Instance { get; private set; }
        public static bool IsOpen { get { return Instance != null && Instance._open; } }
        public static bool InputBlocked { get { return IsOpen; } }
        public static string LastStatus { get { return Instance != null ? Instance._status : "big_map_not_created"; } }
        public static string SelectedEntityId { get { return Instance != null ? (Instance._selectedId ?? "") : ""; } }
        public static int VisibleMarkerCount { get { return Instance != null ? Instance._visibleMarkers : 0; } }
        public static int RouteWaypointCount { get { return Instance != null && Instance._previewRoute != null ? Instance._previewRoute.Count : 0; } }
        public static bool DebugLayerOn { get { return Instance != null && Instance._debugLayer; } }
        public static bool SearchFocused
        {
            get { return Instance != null && Instance._searchField != null && Instance._searchField.isFocused; }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Bootstrap()
        {
            if (Yixuan.SelectionLab.SelectionLabContext.IsActive) return;
            if (UnityEngine.Object.FindObjectOfType<BigMapPanel>() != null) return;
            var go = new GameObject("UI_BigMap");
            go.AddComponent<BigMapPanel>();
        }

        public static BigMapPanel EnsureInstance()
        {
            if (Instance != null) return Instance;
            var existing = UnityEngine.Object.FindObjectOfType<BigMapPanel>();
            if (existing != null) return existing;
            var go = new GameObject("UI_BigMap");
            return go.AddComponent<BigMapPanel>();
        }

        public static void Open() { EnsureInstance().SetOpen(true); }
        public static void Close() { if (Instance != null) Instance.SetOpen(false); }
        public static void Toggle() { var p = EnsureInstance(); p.SetOpen(!p._open); }
        public static bool OpenForTest() { Open(); return IsOpen; }
        public static bool CloseForTest() { Close(); return !IsOpen; }

        /// <summary>
        /// Select an entity from the catalog by stable id (map click equivalent).
        /// Both big-map click and this API publish through the shared selection
        /// entry: a new manual/map selection advances the shared generation so an
        /// older pending AI token can no longer overwrite it. Source remains
        /// "map_click" (this is not claimed as an image/visual confirmation).
        /// </summary>
        public static bool SelectEntity(string id)
        {
            var panel = EnsureInstance();
            var entity = WorldEntityCatalog.Get(id);
            if (entity == null)
            {
                panel._status = "选择失败：实体目录没有 " + (id ?? "null");
                panel._statusKind = "fail";
                return false;
            }
            var record = Yixuan.Pick.PickService.ConfirmEntity(id, "map_click");
            if (record == null)
            {
                panel._status = "选择失败：" + (Yixuan.Pick.PickService.LastError ?? "publish_rejected");
                panel._statusKind = "fail";
                return false;
            }
            panel.SetOpen(true);
            panel._selectedId = id;
            panel._selectionFromShared = true;
            panel._hasGround = false;
            panel._previewRoute = null;
            panel._previewText = "";
            panel.CenterOn(WorldEntityCatalog.WorldPosition(entity));
            panel.RefreshInfo();
            panel._status = "已选中：" + WorldEntityCatalog.DisplayName(entity) + " [" + id + "]";
            panel._statusKind = "selected";
            panel._dirty = true;
            return true;
        }

        public static bool SelectGroundForTest(Vector2 worldXZ)
        {
            var panel = EnsureInstance();
            panel.SetOpen(true);
            panel._selectedId = null;
            panel._hasGround = true;
            panel._groundPoint = worldXZ;
            panel._previewRoute = null;
            panel._previewText = "";
            panel.CenterOn(new Vector3(worldXZ.x, 0f, worldXZ.y));
            panel.RefreshInfo();
            panel._status = "已选中地面点 (" + worldXZ.x.ToString("F1") + ", " + worldXZ.y.ToString("F1") + ")";
            panel._statusKind = "selected";
            panel._dirty = true;
            return true;
        }

        public static bool SetFilterForTest(string clsOrAnchor)
        {
            var panel = EnsureInstance();
            panel.SetFilter(clsOrAnchor);
            panel.RebuildMarkersNow();
            return panel._filterCls == (clsOrAnchor ?? "");
        }

        public static bool SetSearchForTest(string text)
        {
            var panel = EnsureInstance();
            panel.SetSearch(text);
            panel.RebuildMarkersNow();
            return panel._searchText == (text ?? "");
        }

        public void RebuildMarkersNow()
        {
            _dirty = false;
            RebuildMarkers();
        }

        /// <summary>Plan a real route for the current selection; sets preview state.</summary>
        /// <summary>Find a viewport-local point that maps to walkable ground far
        /// enough from every drawn icon, for deterministic click tests.</summary>
        public static string EmptyGroundLocalForTest()
        {
            var panel = EnsureInstance();
            if (panel._viewport == null || !WorldMapData.Loaded) return "";
            Vector2 size = panel._viewport.rect.size;
            for (int gy = 1; gy <= 6; gy++)
            {
                for (int gx = 1; gx <= 6; gx++)
                {
                    Vector2 local = new Vector2(size.x * gx / 7f, size.y * gy / 7f);
                    Vector2 world = panel.LocalToWorld(local);
                    if (world.x < WorldMapData.MinX || world.x > WorldMapData.MaxX ||
                        world.y < WorldMapData.MinZ || world.y > WorldMapData.MaxZ) continue;
                    Vector3 probe = new Vector3(world.x,
                        WorldData.GroundHeightAt(new Vector3(world.x, 0f, world.y)), world.y);
                    if (!WorldData.IsWalkable(probe)) continue;
                    bool tooClose = false;
                    foreach (var marker in panel._entityMarkers)
                        if ((marker.Local - local).sqrMagnitude < 24f * 24f) { tooClose = true; break; }
                    if (!tooClose)
                    {
                        foreach (var anchor in panel._anchorMarkers)
                            if ((anchor.Local - local).sqrMagnitude < 20f * 20f) { tooClose = true; break; }
                    }
                    if (tooClose) continue;
                    return local.x.ToString("F2") + "," + local.y.ToString("F2");
                }
            }
            return "";
        }

        public static void RebuildMarkersForTest()
        {
            var panel = EnsureInstance();
            panel.RebuildMarkersNow();
        }

        /// <summary>Node-2: dynamic vehicles/tram move; the icon layer must refresh.</summary>
        public static void MarkDynamicMarkersDirty()
        {
            if (Instance != null) Instance._dirty = true;
        }

        public static void RefreshOverlayForTest()
        {
            var panel = EnsureInstance();
            panel.UpdateOverlayGraphic();
        }

        public static bool PreviewRouteForTest()
        {
            var panel = EnsureInstance();
            bool ok = panel.PreviewRoute();
            panel.UpdateOverlayGraphic();
            return ok;
        }

        /// <summary>Execute the current selection: walk / sit / stand / report unsupported.</summary>
        public static bool ConfirmActionForTest()
        {
            var panel = EnsureInstance();
            return panel.ConfirmAction();
        }

        public static bool SitSelectedForTest()
        {
            var panel = EnsureInstance();
            return panel.SitSelected();
        }

        public static string DescribeSelectionForTest()
        {
            var panel = EnsureInstance();
            return panel.DescribeSelection();
        }

        public static string LayoutSnapshotForTest()
        {
            var panel = EnsureInstance();
            return panel.LayoutSnapshot();
        }

        /// <summary>Projection-level check for the drawn preview route: the rendered
        /// local points must equal the live world→map projection of the route.</summary>
        public static string RouteRenderSnapshotForTest()
        {
            var panel = EnsureInstance();
            return panel.RouteRenderSnapshot();
        }

        public static string PreviewTargetForTest()
        {
            var panel = EnsureInstance();
            if (!panel._lastPreviewTarget.HasValue) return "";
            Vector3 value = panel._lastPreviewTarget.Value;
            return value.x.ToString("F4") + "," + value.z.ToString("F4");
        }

        public static string ConfirmTargetForTest()
        {
            var panel = EnsureInstance();
            if (!panel._lastConfirmTarget.HasValue) return "";
            Vector3 value = panel._lastConfirmTarget.Value;
            return value.x.ToString("F4") + "," + value.z.ToString("F4");
        }

        public static string ResolvedTargetForTest()
        {
            var panel = EnsureInstance();
            return panel._lastResolvedTarget.x.ToString("F4") + "," +
                   panel._lastResolvedTarget.z.ToString("F4") + "|" + panel._lastResolvedReason;
        }

        public static string ActionNamesForTestPublic()
        {
            var panel = EnsureInstance();
            return string.Join("|", panel.ActionNamesForTest());
        }

        public static bool ClickActionForTest(string label)
        {
            var panel = EnsureInstance();
            var names = panel.ActionNamesForTest();
            for (int i = 0; i < names.Length && i < panel._actionButtons.Count; i++)
            {
                if (names[i] != label) continue;
                if (!panel._actionButtons[i].interactable) return false;
                panel._actionButtons[i].onClick.Invoke();
                return true;
            }
            return false;
        }

        public static bool ClickActionForTest(int index)
        {
            var panel = EnsureInstance();
            if (index < 0 || index >= panel._actionButtons.Count) return false;
            if (!panel._actionButtons[index].interactable) return false;
            panel._actionButtons[index].onClick.Invoke();
            return true;
        }

        public static int ActionCountForTest()
        {
            var panel = EnsureInstance();
            int count = 0;
            for (int i = 0; i < panel._actionButtons.Count; i++)
                if (panel._actionButtons[i].gameObject.activeSelf) count++;
            return count;
        }

        public static int ActionSlotCapacityForTest()
        {
            var panel = EnsureInstance();
            return panel._actionButtons.Count;
        }

        public static string GazeSourceForTest()
        {
            var panel = EnsureInstance();
            return panel._gazeSource;
        }

        /// <summary>
        /// Event-path reproduction of the real R4 scenario: one complete drag
        /// gesture (pointer down, move, release) followed by a *new* click gesture
        /// (pointer down, release without movement).  A single gesture that both
        /// dragged and released must not be treated as a click.
        /// </summary>
        public static float SimulateDragForTest(Vector2 startLocal, Vector2 endLocal, bool leftButton)
        {
            var panel = EnsureInstance();
            if (panel._inputHandler == null) return -1f;
            panel._inputHandler.BeginGestureForTest(startLocal, leftButton);
            panel._inputHandler.DragGestureForTest(endLocal);
            panel._lastGestureDragDistance = panel._inputHandler.DragDistanceForTest;
            panel._inputHandler.EndGestureForTest();
            return panel._lastGestureDragDistance;
        }

        public static bool SimulateClickAtForTest(Vector2 clickLocal, bool leftButton)
        {
            var panel = EnsureInstance();
            if (panel._inputHandler == null) return false;
            panel._inputHandler.BeginGestureForTest(clickLocal, leftButton);
            return panel._inputHandler.TryClickForTest(clickLocal);
        }

        public static bool SimulateDragClickForTest(Vector2 startLocal, Vector2 endLocal,
                                                    Vector2 clickLocal, bool leftButton)
        {
            var panel = EnsureInstance();
            if (panel._inputHandler == null) return false;
            SimulateDragForTest(startLocal, endLocal, leftButton);
            return SimulateClickAtForTest(clickLocal, leftButton);
        }

        public static float DragDistanceForTest()
        {
            var panel = EnsureInstance();
            return panel._lastGestureDragDistance;
        }

        public static void RefreshRouteProjectionForTest()
        {
            var panel = EnsureInstance();
            panel.UpdateOverlayGraphic();
        }

        /// <summary>Same check but WITHOUT refreshing the overlay, so a stale
        /// (non-reprojected) route after pan/zoom is detected instead of masked.</summary>
        public static float RouteProjectionErrorNoRefreshForTest()
        {
            var panel = EnsureInstance();
            if (panel._previewRoute == null || panel._previewRoute.Count == 0) return -1f;
            var character = panel.FindCharacter();
            int offset = character != null ? 1 : 0;
            float maxError = 0f;
            for (int i = 0; i < panel._previewRoute.Count; i++)
            {
                Vector2 projected = panel.ToLocal(panel._previewRoute[i]);
                int index = i + offset;
                if (index >= panel._lastPreviewLocal.Count) return -1f;
                maxError = Mathf.Max(maxError, Vector2.Distance(projected, panel._lastPreviewLocal[index]));
            }
            return maxError;
        }

        public static float RouteProjectionErrorForTest()
        {
            var panel = EnsureInstance();
            panel.UpdateOverlayGraphic();
            if (panel._previewRoute == null || panel._previewRoute.Count == 0) return -1f;
            var character = panel.FindCharacter();
            int offset = character != null ? 1 : 0;
            float maxError = 0f;
            for (int i = 0; i < panel._previewRoute.Count; i++)
            {
                Vector2 projected = panel.ToLocal(panel._previewRoute[i]);
                int index = i + offset;
                if (index >= panel._lastPreviewLocal.Count) return -1f;
                float error = Vector2.Distance(projected, panel._lastPreviewLocal[index]);
                if (error > maxError) maxError = error;
            }
            return maxError;
        }

        public static int PreviewRenderedPointCountForTest()
        {
            var panel = EnsureInstance();
            panel.UpdateOverlayGraphic();
            return panel._lastPreviewLocal != null ? panel._lastPreviewLocal.Count : 0;
        }

        public static string FirstRenderedPointForTest()
        {
            var panel = EnsureInstance();
            panel.UpdateOverlayGraphic();
            if (panel._lastPreviewLocal == null || panel._lastPreviewLocal.Count == 0) return "";
            return panel._lastPreviewLocal[0].x.ToString("F2") + "," +
                   panel._lastPreviewLocal[0].y.ToString("F2");
        }

        public static string FirstWorldRoutePointForTest()
        {
            var panel = EnsureInstance();
            if (panel._previewRoute == null || panel._previewRoute.Count == 0) return "";
            return panel._previewRoute[0].x.ToString("F2") + "," + panel._previewRoute[0].z.ToString("F2");
        }

        public static bool WalkToSelectedForTest()
        {
            var panel = EnsureInstance();
            var entity = WorldEntityCatalog.Get(panel._selectedId);
            if (entity != null) return panel.WalkToEntity(entity);
            return panel.ConfirmAction();
        }

        public static void ResetViewForTest()
        {
            var panel = EnsureInstance();
            panel.ResetView();
        }

        public static bool SelectBenchForCardTest()
        {
            var panel = EnsureInstance();
            var entity = panel.FindNearestUsableBench();
            if (entity == null) return false;
            return SelectEntity(entity.id);
        }

        public static string PanCenterForTest()
        {
            var panel = EnsureInstance();
            return panel._center.x.ToString("F4") + "," + panel._center.y.ToString("F4");
        }

        public static string LocalOfEntityForTest(string id)
        {
            var panel = EnsureInstance();
            var entity = WorldEntityCatalog.Get(id);
            if (entity == null) return "";
            Vector3 position = WorldEntityCatalog.WorldPosition(entity);
            Vector2 local = panel.ToLocal(position);
            return local.x.ToString("F2") + "," + local.y.ToString("F2");
        }

        public static string InfoTextForTest()
        {
            var panel = EnsureInstance();
            return panel._infoBody != null ? panel._infoBody.text : "";
        }

        public static string SelectedStateForTest()
        {
            var panel = EnsureInstance();
            return (panel._selectedId ?? "") + "|ground=" + panel._hasGround + "|" +
                   panel._groundPoint.x.ToString("F2") + "," + panel._groundPoint.y.ToString("F2");
        }

        public static string MapLocalForWorldForTest(float x, float z)
        {
            var panel = EnsureInstance();
            Vector2 local = panel.ToLocalXZ(new Vector2(x, z));
            return local.x.ToString("F2") + "," + local.y.ToString("F2");
        }

        public static void ZoomForTest(float scrollDelta)
        {
            var panel = EnsureInstance();
            if (panel._viewport == null) return;
            Vector2 size = panel._viewport.rect.size;
            panel.ZoomAt(new Vector2(size.x * 0.5f, size.y * 0.5f), scrollDelta);
        }

        public static void PanByForTest(float dx, float dy)
        {
            var panel = EnsureInstance();
            panel.PanBy(new Vector2(dx, dy));
        }

        public static bool ActionsWithinPanelForTest()
        {
            var panel = EnsureInstance();
            if (panel._actionButtons == null || panel._canvas == null) return false;
            var corners = new Vector3[4];
            foreach (var button in panel._actionButtons)
            {
                if (button == null || !button.gameObject.activeSelf) continue;
                var rt = (RectTransform)button.transform;
                rt.GetWorldCorners(corners);
                for (int i = 0; i < 4; i++)
                {
                    Vector3 viewport = panel._uiCamera.WorldToViewportPoint(corners[i]);
                    if (viewport.x < -0.01f || viewport.x > 1.01f || viewport.y < -0.01f || viewport.y > 1.01f)
                        return false;
                }
            }
            return true;
        }

        public static bool CapturePng(string path, out int width, out int height, out string error)
        {
            var panel = EnsureInstance();
            return panel.CaptureToPng(path, out width, out height, out error);
        }

        public static void SetDebugLayerForTest(bool on)
        {
            var panel = EnsureInstance();
            panel._debugLayer = on;
            panel._dirty = true;
        }

        // ---------------- panel state ----------------
        Canvas _canvas;
        CanvasScaler _scaler;
        Camera _uiCamera;
        RectTransform _root;
        RectTransform _viewport;
        RectTransform _labelsHost;
        BigMapInputHandler _inputHandler;
        RawImage _basemap;
        BigMapMarkerGraphic _markers;
        BigMapMarkerGraphic _overlay;
        InputField _searchField;
        Text _infoTitle;
        Text _infoBody;
        Text _statusText;
        Text _modeText;
        Text _legendText;
        readonly List<Button> _actionButtons = new List<Button>();
        readonly List<Text> _actionLabels = new List<Text>();
        readonly List<Text> _labels = new List<Text>();
        readonly List<Button> _filterButtons = new List<Button>();
        readonly List<Text> _filterLabels = new List<Text>();

        bool _open;
        bool _built;
        bool _hudWasVisible;
        string _filterCls = "";
        string _searchText = "";
        HashSet<string> _searchIds;
        string _selectedId;
        int _lastSharedSelectionVersion = -1;
        bool _selectionFromShared;
        bool _hasGround;
        Vector2 _groundPoint;
        string _status = "按 M 打开大地图";
        string _statusKind = "idle";
        string _previewText = "";
        List<Vector3> _previewRoute;
        Vector3? _lastPreviewTarget;
        Vector3? _lastConfirmTarget;
        Vector3 _lastResolvedTarget;
        string _lastResolvedReason = "";
        string _stateSignature = "";
        int _actionVersion;
        bool _debugLayer;
        int _visibleMarkers;
        Vector2 _center;
        float _zoom = 1f;
        bool _dirty = true;
        bool _labelsDirty = true;
        Vector2 _lastViewportSize;

        // marker data handed to the mesh graphic
        readonly List<MapMarker> _entityMarkers = new List<MapMarker>();
        readonly List<MapMarker> _anchorMarkers = new List<MapMarker>();
        Vector2 _characterLocal;
        float _characterYaw;
        bool _hasCharacter;
        readonly List<Vector3> _navRouteBuffer = new List<Vector3>();
        List<Vector2> _lastPreviewLocal = new List<Vector2>();
        List<Vector2> _lastNavigationLocal = new List<Vector2>();
        string _gazeSource = "none";
        float _lastGestureDragDistance;
        static readonly List<MapMarker> EmptyMarkers = new List<MapMarker>();

        // ---------------- lifecycle ----------------
        void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;
            DontDestroyOnLoad(gameObject);
            Build();
            SetOpen(false);
        }

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
            if (_uiCamera != null) Destroy(_uiCamera.gameObject);
        }

        void Update()
        {
            if (!_built) return;
            if (_open)
            {
                if (Input.GetKeyDown(KeyCode.Escape) && !TextFocusBlocking())
                {
                    SetOpen(false);
                    return;
                }
                if (_viewport != null)
                {
                    Vector2 size = _viewport.rect.size;
                    if (size != _lastViewportSize) { _lastViewportSize = size; _dirty = true; }
                }
            }
            if (_open && _dirty) RebuildMarkers();
            SyncSharedSelection();
        }

        /// <summary>Consumer refresh entry used by the per-frame Update path; exposed so a test can force the same sync deterministically.</summary>
        public static void SyncSharedSelectionNow()
        {
            if (Instance != null) Instance.SyncSharedSelection();
        }

        /// <summary>
        /// The big map consumes the same confirmed selection record as the
        /// first-person highlight, minimap, LLM and task facts. It never invents
        /// a confirmed selection from a candidate.
        /// </summary>
        void SyncSharedSelection()
        {
            if (Yixuan.Pick.SelectionBroker.Version == _lastSharedSelectionVersion) return;
            _lastSharedSelectionVersion = Yixuan.Pick.SelectionBroker.Version;
            var record = Yixuan.Pick.SelectionBroker.Current;
            if (record == null || !record.confirmed)
            {
                if (_selectionFromShared)
                {
                    _selectedId = null;
                    _selectionFromShared = false;
                    _statusKind = "idle";
                    _dirty = true;
                }
                return;
            }
            var entity = WorldEntityCatalog.Get(record.entity_id);
            if (entity == null) return;
            _selectedId = record.entity_id;
            _selectionFromShared = true;
            _hasGround = false;
            _status = "已选中：" + WorldEntityCatalog.DisplayName(entity) + " [" + record.entity_id + "]";
            _statusKind = "selected";
            _dirty = true;
            if (_open) RefreshInfo();
        }

        bool TextFocusBlocking()
        {
            if (_searchField != null && _searchField.isFocused) return true;
#if UNITY_EDITOR
            if (UnityEngine.EventSystems.EventSystem.current != null &&
                UnityEngine.EventSystems.EventSystem.current.currentSelectedGameObject != null)
            {
                var field = UnityEngine.EventSystems.EventSystem.current.currentSelectedGameObject
                    .GetComponent<InputField>();
                if (field != null && field.isFocused) return true;
            }
#endif
            return false;
        }

        /// <summary>Drop old-map selection/markers/zoom when an explicit lab scene owns the runtime.</summary>
        public static void ResetForExplicitScene()
        {
            var panel = Instance;
            if (panel == null) return;
            if (panel._open) panel.SetOpen(false);
            panel._selectedId = null;
            panel._previewRoute = null;
            panel._hasGround = false;
            panel._zoom = 1f;
            panel._lastSharedSelectionVersion = -1;
            panel._dirty = true;
            panel._status = "实验室模式：请使用实验室顶视小地图";
            panel._statusKind = "lab";
        }

        public void SetOpen(bool open)
        {
            if (!_built) Build();
            if (Yixuan.SelectionLab.SelectionLabContext.IsActive)
            {
                if (_open) { _open = false; if (_canvas != null) _canvas.gameObject.SetActive(false); if (_uiCamera != null) _uiCamera.enabled = false; }
                _selectedId = null;
                _previewRoute = null;
                _hasGround = false;
                _status = "实验室模式：请使用实验室顶视小地图";
                _statusKind = "lab";
                return;
            }
            if (open == _open) return;
            _open = open;
            if (_canvas != null) _canvas.gameObject.SetActive(open);
            if (_uiCamera != null) _uiCamera.enabled = open;
            if (open)
            {
                _hudWasVisible = Yixuan.UI.UnifiedHud.IsConsoleVisible;
                Yixuan.UI.UnifiedHud.SetConsoleVisible(false);
                if (WorldMapData.Load() == false)
                {
                    _status = "地图数据未就绪：" + (WorldMapData.Error ?? "unknown");
                    _statusKind = "fail";
                }
                else
                {
                    _status = "拖动平移 · 滚轮缩放 · 点击物体预览 · W/A/S/D 不生效（地图编辑中）";
                    _statusKind = "idle";
                    if (_selectedId == null && !_hasGround)
                    {
                        var character = FindCharacter();
                        if (character != null) CenterOn(character.position);
                    }
                }
                _lastViewportSize = Vector2.zero;
                _dirty = true;
                _labelsDirty = true;
                RefreshInfo();
            }
            else
            {
                Yixuan.UI.UnifiedHud.SetConsoleVisible(_hudWasVisible);
            }
            UpdateStatusLabel();
        }

        void CenterOn(Vector3 position)
        {
            _center = new Vector2(position.x, position.z);
            ClampView();
            _dirty = true;
        }

        // ---------------- build ----------------
        void Build()
        {
            if (_built) return;
            _built = true;
            EnsureEventSystem();
            EnsureFont();

            var canvasGo = new GameObject("BigMapCanvas", typeof(RectTransform));
            canvasGo.transform.SetParent(transform, false);
            canvasGo.layer = 5;
            _canvas = canvasGo.AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceCamera;
            _canvas.planeDistance = 5f;
            _canvas.sortingOrder = 100;
            _scaler = canvasGo.AddComponent<CanvasScaler>();
            _scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            _scaler.referenceResolution = new Vector2(1920f, 1080f);
            _scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            _scaler.matchWidthOrHeight = 0.5f;
            _scaler.referencePixelsPerUnit = 100f;
            canvasGo.AddComponent<GraphicRaycaster>();

            var cameraGo = new GameObject("BigMapUICamera", typeof(Camera));
            cameraGo.transform.SetParent(transform, false);
            _uiCamera = cameraGo.GetComponent<Camera>();
            _uiCamera.clearFlags = CameraClearFlags.Depth;
            _uiCamera.cullingMask = 1 << 5;
            _uiCamera.depth = 60f;
            _uiCamera.orthographic = true;
            _uiCamera.orthographicSize = 5f;
            _uiCamera.nearClipPlane = 0.01f;
            _uiCamera.farClipPlane = 50f;
            _uiCamera.allowHDR = false;
            _uiCamera.enabled = false;
            _canvas.worldCamera = _uiCamera;

            _root = NewRect("Root", canvasGo.transform, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
            _root.gameObject.layer = 5;
            var bg = NewImage("Bg", _root, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero,
                              new Color(0.016f, 0.022f, 0.034f, 0.985f));

            BuildHeader();
            BuildMapHost();
            BuildSidePanel();
            RebuildFilterButtons();
            _markers = CreateMarkers("Markers", _viewport);
            _overlay = CreateMarkers("Overlay", _viewport);
            _overlay.raycastTarget = false;
            _overlay.Markers = EmptyMarkers;
            _labelsHost.SetAsLastSibling();
            _canvas.gameObject.SetActive(false);
        }

        static void EnsureEventSystem()
        {
            if (UnityEngine.EventSystems.EventSystem.current != null) return;
            if (UnityEngine.Object.FindObjectOfType<UnityEngine.EventSystems.EventSystem>() != null) return;
            var go = new GameObject("UI_BigMapEventSystem");
            go.AddComponent<UnityEngine.EventSystems.EventSystem>();
            go.AddComponent<UnityEngine.EventSystems.StandaloneInputModule>();
        }

        static Font _font;

        static void EnsureFont()
        {
            if (_font != null) return;
            try
            {
                _font = Font.CreateDynamicFontFromOSFont(
                    new[] { "Microsoft YaHei UI", "Microsoft YaHei", "SimHei", "SimSun", "Arial" }, 16);
            }
            catch { _font = null; }
            if (_font == null)
            {
                try { _font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"); } catch { _font = null; }
            }
        }

        void BuildHeader()
        {
            var header = NewRect("Header", _root, new Vector2(0f, 1f), new Vector2(1f, 1f),
                                 new Vector2(16f, -62f), new Vector2(-16f, -8f));
            NewText("Title", header, new Vector2(0f, 1f), new Vector2(0f, 1f),
                    new Vector2(0f, -40f), new Vector2(420f, 0f), "大地图 · SovietBlueHour", 28,
                    TextAnchor.MiddleLeft, new Color(0.88f, 0.93f, 1f, 1f));
            _modeText = NewText("Mode", header, new Vector2(0f, 1f), new Vector2(1f, 1f),
                                new Vector2(430f, -38f), new Vector2(-360f, -4f),
                                "主体：人物 · 座面行走/点击预览", 16, TextAnchor.MiddleLeft,
                                new Color(0.62f, 0.72f, 0.86f, 1f));
            NewButton("Reset", header, new Vector2(1f, 1f), new Vector2(1f, 1f),
                      new Vector2(-340f, -46f), new Vector2(-224f, -8f), "复位视图",
                      new Color(0.16f, 0.22f, 0.30f, 0.95f), () => { ResetView(); });
            NewButton("Debug", header, new Vector2(1f, 1f), new Vector2(1f, 1f),
                      new Vector2(-218f, -46f), new Vector2(-108f, -8f), "调试网格",
                      new Color(0.16f, 0.22f, 0.30f, 0.95f), () =>
                      {
                          _debugLayer = !_debugLayer;
                          _status = _debugLayer ? "调试层：实体AABB（非真实轮廓）" : "调试层关闭";
                          _statusKind = "info";
                          _dirty = true;
                      });
            NewButton("Close", header, new Vector2(1f, 1f), new Vector2(1f, 1f),
                      new Vector2(-100f, -46f), new Vector2(0f, -8f), "关闭 (Esc)",
                      new Color(0.30f, 0.18f, 0.18f, 0.95f), () => SetOpen(false));
        }

        void BuildMapHost()
        {
            var host = NewRect("MapHost", _root, new Vector2(0f, 0f), new Vector2(0.775f, 1f),
                               new Vector2(16f, 16f), new Vector2(-8f, -76f));
            var frame = NewImage("MapFrame", host, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero,
                                 new Color(0.05f, 0.07f, 0.09f, 1f));
            _viewport = NewRect("MapViewport", frame.transform, Vector2.zero, Vector2.one,
                                new Vector2(2f, 2f), new Vector2(-2f, -2f));
            _viewport.pivot = new Vector2(0f, 0f);
            _viewport.anchorMin = Vector2.zero;
            _viewport.anchorMax = Vector2.one;
            _viewport.offsetMin = new Vector2(2f, 2f);
            _viewport.offsetMax = new Vector2(-2f, -2f);
            var mask = _viewport.gameObject.AddComponent<RectMask2D>();
            var raycast = NewImage("ViewportRaycast", _viewport, Vector2.zero, Vector2.one,
                                   Vector2.zero, Vector2.zero, new Color(0f, 0f, 0f, 0.004f));
            raycast.raycastTarget = true;

            var basemapGo = new GameObject("Basemap", typeof(RectTransform));
            basemapGo.transform.SetParent(_viewport, false);
            basemapGo.layer = 5;
            _basemap = basemapGo.AddComponent<RawImage>();
            _basemap.raycastTarget = false;
            var brt = _basemap.rectTransform;
            brt.anchorMin = new Vector2(0f, 1f);
            brt.anchorMax = new Vector2(0f, 1f);
            brt.pivot = new Vector2(0f, 1f);

            var labelsGo = new GameObject("Labels", typeof(RectTransform));
            labelsGo.transform.SetParent(_viewport, false);
            labelsGo.layer = 5;
            _labelsHost = labelsGo.GetComponent<RectTransform>();
            _labelsHost.pivot = new Vector2(0f, 0f);
            _labelsHost.anchorMin = Vector2.zero;
            _labelsHost.anchorMax = Vector2.one;
            _labelsHost.offsetMin = Vector2.zero;
            _labelsHost.offsetMax = Vector2.zero;

            var handler = _viewport.gameObject.AddComponent<BigMapInputHandler>();
            handler.Panel = this;
            _inputHandler = handler;

            _legendText = NewText("Legend", frame.transform, new Vector2(0f, 0f), new Vector2(1f, 0f),
                                  new Vector2(8f, 4f), new Vector2(-8f, 24f),
                                  "北 ↑   左键拖动平移 · 滚轮缩放 · 点击物体选中 · 青=长椅 橙=车辆 红=圣火 黄=地点 蓝=建筑 绿=绿化（调试网格=实体AABB，非真实轮廓）",
                                  14, TextAnchor.MiddleLeft, new Color(0.72f, 0.80f, 0.92f, 0.95f));
        }

        void BuildSidePanel()
        {
            var side = NewImage("Side", _root, new Vector2(0.775f, 0f), new Vector2(1f, 1f),
                                new Vector2(8f, 16f), new Vector2(-16f, -76f),
                                new Color(0.035f, 0.05f, 0.072f, 0.98f));

            var title = NewText("SideTitle", side.transform, new Vector2(0f, 1f), new Vector2(1f, 1f),
                                new Vector2(14f, -38f), new Vector2(-14f, -8f), "搜索与筛选", 20,
                                TextAnchor.MiddleLeft, new Color(0.85f, 0.90f, 1f, 1f));

            NewText("SearchLabel", side.transform, new Vector2(0f, 1f), new Vector2(1f, 1f),
                    new Vector2(14f, -62f), new Vector2(-14f, -40f), "搜索名称 / ID / 区域", 15,
                    TextAnchor.MiddleLeft, new Color(0.62f, 0.72f, 0.86f, 1f));
            _searchField = NewInput("Search", side.transform, new Vector2(0f, 1f), new Vector2(1f, 1f),
                                    new Vector2(14f, -98f), new Vector2(-14f, -64f), "");
            _searchField.onValueChanged.AddListener(value =>
            {
                _searchText = value ?? "";
                _dirty = true;
                RebuildSearchSet();
                _status = string.IsNullOrEmpty(_searchText) ? "搜索已清空" : "搜索：" + _searchText;
                _statusKind = "info";
                UpdateStatusLabel();
            });

            var filterRow = NewRect("FilterRow", side.transform, new Vector2(0f, 1f), new Vector2(1f, 1f),
                                    new Vector2(14f, -136f), new Vector2(-14f, -102f));
            for (int i = 0; i < FilterLabels.Length; i++)
            {
                int index = i;
                var button = NewButton("Filter" + i, filterRow, new Vector2(0f, 0f), new Vector2(0f, 1f),
                                       new Vector2(i * 62f, 0f), new Vector2(i * 62f + 58f, 0f),
                                       FilterLabels[i], new Color(0.14f, 0.20f, 0.28f, 0.95f),
                                       () => SetFilter(FilterCls[index]));
                _filterButtons.Add(button);
                _filterLabels.Add(button.GetComponentInChildren<Text>());
            }

            _infoTitle = NewText("InfoTitle", side.transform, new Vector2(0f, 1f), new Vector2(1f, 1f),
                                 new Vector2(14f, -172f), new Vector2(-14f, -140f), "未选中", 19,
                                 TextAnchor.MiddleLeft, new Color(0.92f, 0.95f, 1f, 1f));
            _infoBody = NewText("InfoBody", side.transform, new Vector2(0f, 0f), new Vector2(1f, 1f),
                                new Vector2(14f, 138f), new Vector2(-14f, -178f), "", 15,
                                TextAnchor.UpperLeft, new Color(0.80f, 0.87f, 0.97f, 1f));
            _infoBody.horizontalOverflow = HorizontalWrapMode.Wrap;
            _infoBody.verticalOverflow = VerticalWrapMode.Truncate;

            var actions = NewRect("Actions", side.transform, new Vector2(0f, 0f), new Vector2(1f, 0f),
                                  new Vector2(14f, 52f), new Vector2(-14f, 128f));
            for (int i = 0; i < 6; i++)
            {
                int index = i;
                int col = i % 3;
                int row = i / 3;
                float x0 = col / 3f;
                float x1 = (col + 1f) / 3f;
                float y0 = row == 0 ? 0.5f : 0f;
                float y1 = row == 0 ? 1f : 0.5f;
                var button = NewButton("Action" + i, actions, new Vector2(x0, y0),
                                       new Vector2(x1, y1), new Vector2(2f, 2f), new Vector2(-2f, -2f),
                                       "-", new Color(0.16f, 0.30f, 0.42f, 0.98f),
                                       () => OnActionClicked(index));
                button.gameObject.SetActive(false);
                _actionButtons.Add(button);
                _actionLabels.Add(button.GetComponentInChildren<Text>());
            }
            _statusText = NewText("Status", side.transform, new Vector2(0f, 0f), new Vector2(1f, 0f),
                                  new Vector2(14f, 10f), new Vector2(-14f, 50f), _status, 14,
                                  TextAnchor.UpperLeft, new Color(0.72f, 0.82f, 0.95f, 1f));
            _statusText.horizontalOverflow = HorizontalWrapMode.Wrap;
        }

        static readonly string[] FilterLabels = { "全部", "长椅", "车辆", "圣火", "地点", "建筑" };
        static readonly string[] FilterCls = { "", "bench", "vehicle", "eternal_flame", "anchor", "building" };

        int _actionCount;
        string[] _actionNames = new string[0];

        void RebuildFilterButtons()
        {
            for (int i = 0; i < _filterButtons.Count && i < FilterCls.Length; i++)
            {
                bool on = _filterCls == FilterCls[i];
                if (_filterLabels[i] != null)
                    _filterLabels[i].color = on ? new Color(1f, 0.88f, 0.42f, 1f)
                                                : new Color(0.74f, 0.82f, 0.94f, 1f);
            }
        }

        void SetFilter(string cls)
        {
            _filterCls = cls ?? "";
            _dirty = true;
            RebuildFilterButtons();
            _status = "筛选：" + FilterName(_filterCls);
            _statusKind = "info";
            RefreshInfo();
            UpdateStatusLabel();
        }

        static string FilterName(string cls)
        {
            switch (cls)
            {
                case "bench": return "长椅";
                case "vehicle": return "车辆";
                case "eternal_flame": return "圣火";
                case "anchor": return "地点";
                case "building": return "建筑";
                default: return "全部";
            }
        }

        void SetSearch(string text)
        {
            _searchText = text ?? "";
            if (_searchField != null) _searchField.text = _searchText;
            RebuildSearchSet();
            _dirty = true;
        }

        void RebuildSearchSet()
        {
            _searchIds = null;
            if (string.IsNullOrEmpty(_searchText)) return;
            _searchIds = new HashSet<string>(StringComparer.Ordinal);
            var entities = WorldEntityCatalog.Query(null, _searchText, 0);
            foreach (var entity in entities) _searchIds.Add(entity.id);
            foreach (var anchor in WorldEntityCatalog.Anchors)
            {
                if (anchor != null && anchor.label != null &&
                    anchor.label.ToLowerInvariant().Contains(_searchText.ToLowerInvariant()))
                    _searchIds.Add("anchor/" + anchor.id);
            }
        }

        // ---------------- info / actions ----------------
        void RefreshInfo()
        {
            if (_infoTitle == null) return;
            var entity = WorldEntityCatalog.Get(_selectedId);
            if (entity != null)
            {
                _infoTitle.text = WorldEntityCatalog.DisplayName(entity) + "  [" + entity.cls + "]";
                _infoBody.text = DescribeEntity(entity);
                ConfigureActionsForEntity(entity);
            }
            else if (_hasGround)
            {
                _infoTitle.text = "地面点";
                _infoBody.text = DescribeGround(_groundPoint);
                ConfigureGroundActions();
            }
            else
            {
                _infoTitle.text = "未选中";
                _infoBody.text = "点击地图上的物体，或使用搜索/筛选；\n点击空地可预览该坐标。\n\n" +
                                 "长椅：走近并坐下；\n车辆/圣火：本轮节点1尚未实现，会如实提示。";
                ConfigureActions(new[] { "关闭地图" }, new Action[] { () => SetOpen(false) });
            }
        }

        string DescribeEntity(WorldEntityCatalog.Entity entity)
        {
            var sb = new StringBuilder();
            sb.Append("ID：").Append(entity.id).Append('\n');
            sb.Append("区域：").Append(string.IsNullOrEmpty(entity.area) ? "全图" : entity.area)
              .Append(" · 类别：").Append(entity.cls).Append('\n');
            var character = FindCharacter();
            if (character != null)
            {
                Vector3 pos = WorldEntityCatalog.WorldPosition(entity);
                float distance = Vector3.Distance(new Vector3(character.position.x, 0f, character.position.z),
                                                  new Vector3(pos.x, 0f, pos.z));
                sb.Append("距离角色：").Append(distance.ToString("F1")).Append(" m\n");
            }
            if (entity.capabilities != null && entity.capabilities.Length > 0)
            {
                sb.Append("\n能力状态：\n");
                foreach (var capability in entity.capabilities)
                {
                    if (capability == null) continue;
                    sb.Append("· ").Append(VerbName(capability.verb)).Append("：");
                    if (!capability.code_implemented) sb.Append("本轮未实现");
                    else if (!capability.geometry_verified) sb.Append("几何未验证，暂不可用");
                    else sb.Append("已实现/几何已验证");
                    if (capability.code_implemented && capability.geometry_verified)
                        sb.Append(" · 可达性：运行时验证");
                    if (!capability.tested && capability.code_implemented)
                        sb.Append(" · 未实测");
                    if (!string.IsNullOrEmpty(capability.reason) && !capability.code_implemented)
                        sb.Append("（").Append(capability.reason).Append("）");
                    sb.Append('\n');
                }
            }
            if (entity.seat != null && entity.seat.usable)
            {
                sb.Append("\n座面高：")
                  .Append(entity.seat.seat_height_above_ground_m.ToString("F2")).Append(" m");
                sb.Append(" · 朝向：").Append(entity.seat.seat_yaw_deg.ToString("F0")).Append("°\n");
                int candidates = entity.seat.approach_candidates != null
                    ? entity.seat.approach_candidates.Length : 0;
                sb.Append("靠近点候选：").Append(candidates).Append(" 个（前/右/左/后）");
                sb.Append(" · 障碍层：").Append(entity.seat.nav_obstacle_covered ? "已登记" : "未登记").Append('\n');
            }
            else if (entity.cls == "bench")
            {
                sb.Append("\n不可坐下：该椅子几何/朝向未通过验证。\n");
            }
            var seatController = Stage4SeatController.Instance;
            if (seatController == null) seatController = UnityEngine.Object.FindObjectOfType<Stage4SeatController>();
            if (seatController != null)
            {
                sb.Append("\n座位控制器：").Append(seatController.State);
                if (seatController.State != Stage4SeatController.SeatState.Idle)
                {
                    sb.Append(" · ").Append(seatController.LastStatus ?? "");
                    if (!string.IsNullOrEmpty(seatController.LastError))
                        sb.Append(" · ").Append(seatController.LastError);
                    if (seatController.NavigationBudgetSeconds > 0f)
                    {
                        sb.Append("\n导航预算：").Append(seatController.NavigationBudgetSeconds.ToString("F0"))
                          .Append(" s / 路径 ").Append(seatController.NavigationPathMeters.ToString("F0"))
                          .Append(" m · 靠近点 side=").Append(seatController.ResolvedApproachSide ?? "");
                    }
                }
                sb.Append('\n');
            }
            if (!string.IsNullOrEmpty(_previewText))
            {
                sb.Append("\n路线预览：").Append(_previewText).Append('\n');
            }
            return sb.ToString();
        }

        string DescribeGround(Vector2 world)
        {
            var sb = new StringBuilder();
            Vector3 pos = new Vector3(world.x, WorldData.GroundHeightAt(new Vector3(world.x, 0f, world.y)), world.y);
            sb.Append("世界坐标：(").Append(world.x.ToString("F2")).Append(", ")
              .Append(world.y.ToString("F2")).Append(")\n");
            sb.Append("地面高度：").Append(pos.y.ToString("F2")).Append(" m\n");
            sb.Append("可走：").Append(WorldData.IsWalkable(pos) ? "是" : "否（障碍/建筑内）").Append('\n');
            try { sb.Append("区域：").Append(WorldData.ZoneAt(pos)).Append('\n'); } catch { }
            var character = FindCharacter();
            if (character != null)
            {
                float distance = Vector3.Distance(new Vector3(character.position.x, 0f, character.position.z),
                                                  new Vector3(world.x, 0f, world.y));
                sb.Append("距角色：").Append(distance.ToString("F1")).Append(" m\n");
            }
            if (!string.IsNullOrEmpty(_previewText)) sb.Append("\n路线预览：").Append(_previewText).Append('\n');
            return sb.ToString();
        }

        static string VerbName(string verb)
        {
            switch (verb)
            {
                case "sit": return "坐下";
                case "walk_to": return "走近";
                case "enter": return "上车";
                case "observe": return "观看";
                case "ride": return "乘车";
                case "light": return "点燃圣火";
                case "extinguish": return "熄灭圣火";
                default: return verb ?? "能力";
            }
        }

        void ConfigureActionsForEntity(WorldEntityCatalog.Entity entity)
        {
            var seat = WorldEntityCatalog.CapabilityOf(entity, "sit");
            bool seatUsable = seat != null && seat.code_implemented && seat.geometry_verified;
            var controller = Stage4SeatController.Instance;
            if (controller == null) controller = UnityEngine.Object.FindObjectOfType<Stage4SeatController>();
            Stage4SeatController.SeatState state = controller != null
                ? controller.State : Stage4SeatController.SeatState.Idle;
            bool seated = state == Stage4SeatController.SeatState.Seated;
            bool busy = state == Stage4SeatController.SeatState.WalkingToApproach ||
                        state == Stage4SeatController.SeatState.Aligning ||
                        state == Stage4SeatController.SeatState.Snapping ||
                        state == Stage4SeatController.SeatState.StandingUp ||
                        state == Stage4SeatController.SeatState.Cancelling;
            var names = new List<string>();
            var actions = new List<Action>();
            var enabled = new List<bool>();
            names.Add("预览路线"); actions.Add(() => PreviewRoute()); enabled.Add(!busy);
            if (seatUsable)
            {
                if (seated)
                {
                    names.Add("起身");
                    actions.Add(() => RequestStandForSelected());
                    enabled.Add(true);
                }
                else
                {
                    names.Add("走近并坐下");
                    actions.Add(() => SitSelected());
                    enabled.Add(!busy);
                    names.Add("只走近");
                    actions.Add(() => WalkToEntity(entity));
                    enabled.Add(!busy);
                }
            }
            else
            {
                names.Add("走近");
                actions.Add(() => WalkToEntity(entity));
                enabled.Add(!busy);
            }
            if (busy && controller != null)
            {
                names.Add("停止坐下");
                actions.Add(() => controller.RequestCancel());
                enabled.Add(true);
            }
            if (WorldMapPanel.HasNavigation)
            {
                names.Add("停止移动");
                actions.Add(() =>
                {
                    bool stopped = WorldMapPanel.StopNavigation("大地图停止");
                    _status = stopped ? "已停止地图导航" : "停止失败：没有地图导航所有权";
                    _statusKind = stopped ? "info" : "fail";
                    UpdateStatusLabel();
                });
                enabled.Add(true);
            }
            names.Add("关闭地图"); actions.Add(() => SetOpen(false)); enabled.Add(true);
            ConfigureActions(names.ToArray(), actions.ToArray(), enabled.ToArray());
        }

        void RequestStandForSelected()
        {
            var controller = Stage4SeatController.Instance;
            if (controller == null) controller = UnityEngine.Object.FindObjectOfType<Stage4SeatController>();
            if (controller == null) return;
            controller.RequestStand();
            _status = "已请求起身，状态实时更新";
            _statusKind = "info";
            UpdateStatusLabel();
        }

        /// <summary>Live signature: the info card/buttons must follow the actual seat
        /// controller and map navigation state without re-selecting the entity.</summary>
        string BuildStateSignature()
        {
            var controller = Stage4SeatController.Instance;
            string controllerState = controller != null
                ? (controller.State + "|" + (controller.CurrentEntity ?? "") + "|" + controller.LastStatus)
                : "no_controller";
            return (_selectedId ?? "ground:" + _hasGround + ":" + _groundPoint) +
                   "|" + controllerState + "|" + WorldMapPanel.HasNavigation + "|" +
                   WorldMapPanel.StatusKind + "|" + (_previewRoute != null ? _previewRoute.Count : 0) +
                   "|" + _filterCls + "|" + _searchText;
        }

        string[] ActionNamesForTest()
        {
            return _actionNames ?? new string[0];
        }

        void ConfigureGroundActions()
        {
            ConfigureActions(new[] { "预览路线", "确认前往", "关闭地图" }, new Action[]
            {
                () => PreviewRoute(),
                () => ConfirmAction(),
                () => SetOpen(false),
            });
        }

        void ConfigureActions(string[] names, Action[] actions)
        {
            var enabled = new bool[names.Length];
            for (int i = 0; i < enabled.Length; i++) enabled[i] = true;
            ConfigureActions(names, actions, enabled);
        }

        void ConfigureActions(string[] names, Action[] actions, bool[] enabled)
        {
            for (int i = 0; i < _actionButtons.Count; i++)
            {
                bool active = i < names.Length;
                _actionButtons[i].gameObject.SetActive(active);
                if (!active) continue;
                _actionLabels[i].text = names[i];
                Action callback = actions[i];
                _actionButtons[i].onClick.RemoveAllListeners();
                _actionButtons[i].onClick.AddListener(() => callback());
                bool isEnabled = enabled == null || i >= enabled.Length || enabled[i];
                _actionButtons[i].interactable = isEnabled;
                _actionLabels[i].color = isEnabled
                    ? new Color(0.93f, 0.96f, 1f, 1f)
                    : new Color(0.55f, 0.60f, 0.68f, 1f);
            }
            _actionCount = names.Length;
            _actionNames = names;
            _actionVersion++;
        }

        void OnActionClicked(int index)
        {
            // actions are bound through ConfigureActions; this is a safety net only
            if (index < 0 || index >= _actionCount) return;
        }

        bool PreviewRoute()
        {
            Vector3? target = ResolveSelectionTarget();
            if (target == null)
            {
                _status = "还没有选择目标：先点选物体或地图空地。";
                _statusKind = "fail";
                UpdateStatusLabel();
                return false;
            }
            var character = FindCharacter();
            if (character == null)
            {
                _status = "预览失败：缺少角色。";
                _statusKind = "fail";
                return false;
            }
            List<Vector3> path;
            string message;
            bool ok = WorldMapPanel.PreviewPath(character.position, new Vector2(target.Value.x, target.Value.z),
                                                out path, out message);
            _previewText = message;
            _status = message;
            _statusKind = ok ? "preview" : "fail";
            if (ok)
            {
                _previewRoute = path;
                _lastPreviewTarget = target;
                _dirty = true;
                _status = "路线预览成功：" + message;
                _statusKind = "preview";
            }
            else
            {
                _previewRoute = null;
                _lastPreviewTarget = null;
            }
            _infoBody.text = _selectedId != null && WorldEntityCatalog.Get(_selectedId) != null
                ? DescribeEntity(WorldEntityCatalog.Get(_selectedId)) : DescribeGround(_groundPoint);
            UpdateStatusLabel();
            return ok;
        }

        bool ConfirmAction()
        {
            var entity = WorldEntityCatalog.Get(_selectedId);
            var seat = entity != null ? WorldEntityCatalog.CapabilityOf(entity, "sit") : null;
            bool seatUsable = seat != null && seat.code_implemented && seat.geometry_verified;
            if (seatUsable) return SitSelected();
            Vector3? target = SelectionWorldPoint();
            if (target == null)
            {
                _status = "确认失败：没有可前往的目标。";
                _statusKind = "fail";
                UpdateStatusLabel();
                return false;
            }
            if (entity != null)
            {
                var enter = WorldEntityCatalog.CapabilityOf(entity, "enter");
                var light = WorldEntityCatalog.CapabilityOf(entity, "light");
                if ((enter != null && !enter.code_implemented) || (light != null && !light.code_implemented))
                {
                    _status = "「" + WorldEntityCatalog.DisplayName(entity) + "」的交互本轮节点1尚未实现，未执行。";
                    _statusKind = "fail";
                    UpdateStatusLabel();
                    return false;
                }
            }
            string status;
            string kind;
            bool ok = WorldMapPanel.TryNavigateTo(new Vector2(target.Value.x, target.Value.z), out status, out kind);
            _lastConfirmTarget = target;
            _lastResolvedTarget = target.Value;
            if (ok)
            {
                _status = "已确认前往：" + status;
                _statusKind = "moving";
                _previewRoute = null;
            }
            else
            {
                _status = status;
                _statusKind = kind == "idle" ? "fail" : kind;
            }
            UpdateStatusLabel();
            return ok;
        }

        bool WalkToEntity(WorldEntityCatalog.Entity entity)
        {
            if (entity == null) return false;
            Vector3 target;
            string kind;
            string reason;
            if (!ResolveEntityWalkTarget(entity, out target, out kind, out reason))
            {
                _status = "走近失败：" + reason;
                _statusKind = "fail";
                UpdateStatusLabel();
                return false;
            }
            // "只走近" uses exactly the previewed target, so preview == execution.
            _lastResolvedTarget = target;
            _lastResolvedReason = reason;
            string status;
            string statusKind;
            bool ok = WorldMapPanel.TryNavigateTo(new Vector2(target.x, target.z), out status, out statusKind);
            _status = ok ? "走近 " + WorldEntityCatalog.DisplayName(entity) + "：" + status : status;
            _statusKind = ok ? "moving" : statusKind;
            UpdateStatusLabel();
            return ok;
        }

        bool SitSelected()
        {
            var entity = WorldEntityCatalog.Get(_selectedId);
            if (entity == null)
            {
                _status = "坐下失败：未选中椅子。";
                _statusKind = "fail";
                UpdateStatusLabel();
                return false;
            }
            var controller = Stage4SeatController.Instance;
            if (controller == null) controller = UnityEngine.Object.FindObjectOfType<Stage4SeatController>();
            if (controller == null)
            {
                _status = "坐下失败：Stage4SeatController 未创建。";
                _statusKind = "fail";
                UpdateStatusLabel();
                return false;
            }
            if (controller.State != Stage4SeatController.SeatState.Idle &&
                controller.State != Stage4SeatController.SeatState.Failed)
            {
                _status = "坐下失败：座位控制器当前状态 " + controller.State;
                _statusKind = "fail";
                UpdateStatusLabel();
                return false;
            }
            bool started = controller.TrySit(entity.id);
            _status = started ? "开始走近并坐下：" + WorldEntityCatalog.DisplayName(entity) + "（状态实时更新）"
                              : "坐下启动失败：" + controller.LastError;
            _statusKind = started ? "moving" : "fail";
            UpdateStatusLabel();
            return started;
        }

        Vector3? SelectionWorldPoint()
        {
            Vector3 target;
            string kind;
            string reason;
            if (ResolveWalkTarget(out target, out kind, out reason))
            {
                _lastResolvedTarget = target;
                _lastResolvedReason = reason;
                return target;
            }
            return null;
        }

        /// <summary>
        /// Resolve the current selection to the exact walk target that preview and
        /// execution both use.  There is one resolution, not two: the preview route
        /// target is the target that "确认/走近" navigates to.
        /// </summary>
        bool ResolveWalkTarget(out Vector3 target, out string kind, out string reason)
        {
            target = Vector3.zero;
            kind = "";
            reason = "";
            var entity = WorldEntityCatalog.Get(_selectedId);
            if (entity != null)
                return ResolveEntityWalkTarget(entity, out target, out kind, out reason);
            if (_hasGround)
            {
                target = new Vector3(_groundPoint.x, WorldData.GroundHeightAt(
                    new Vector3(_groundPoint.x, 0f, _groundPoint.y)), _groundPoint.y);
                var character = FindCharacter();
                var path = WorldData.FindPath(character != null ? character.position : target, target);
                if (!WorldData.IsWalkable(target))
                {
                    kind = "ground";
                    reason = "目标地面不可走（障碍/建筑内）";
                    return false;
                }
                if (path == null || path.Count == 0)
                {
                    kind = "ground";
                    reason = "无法到达该点：" + (WorldData.LastError ?? "无路径");
                    return false;
                }
                kind = "ground";
                reason = "ground_point";
                return true;
            }
            reason = "没有选择目标";
            return false;
        }

        Vector3? ResolveSelectionTarget()
        {
            Vector3 target;
            string kind;
            string reason;
            if (!ResolveWalkTarget(out target, out kind, out reason)) return null;
            _lastResolvedTarget = target;
            _lastResolvedReason = reason;
            return target;
        }

        /// <summary>
        /// Non-ground entity walk target: seats use the seat controller's approach
        /// resolution (with a real path); other entities stand outside their real
        /// footprint by the AABB support distance along the direction to the
        /// character, then try rotated directions until a walkable, path-connected
        /// point is found.  The center of a large object is never used as an
        /// approach point.
        /// </summary>
        bool ResolveEntityWalkTarget(WorldEntityCatalog.Entity entity, out Vector3 target,
                                     out string kind, out string reason)
        {
            target = Vector3.zero;
            kind = "";
            reason = "";
            if (entity == null)
            {
                reason = "实体不存在";
                return false;
            }
            var character = FindCharacter();
            Vector3 from = character != null ? character.position : WorldEntityCatalog.Spawn;
            if (entity.seat != null && entity.seat.usable)
            {
                var entry = Stage4SeatData.Get(entity.id);
                if (entry != null)
                {
                    Vector3 approach;
                    string approachReason;
                    if (Stage4SeatData.ResolveApproach(entry, from, true, out approach, out approachReason))
                    {
                        target = approach;
                        kind = "seat_approach";
                        reason = approachReason;
                        return true;
                    }
                    kind = "seat_approach";
                    reason = "椅子没有可走且可寻路的靠近点：" + approachReason;
                    return false;
                }
            }
            Vector3 center;
            float halfX = 0.5f;
            float halfZ = 0.5f;
            if (entity.bounds != null && entity.bounds.Length >= 4)
            {
                center = new Vector3((entity.bounds[0] + entity.bounds[2]) * 0.5f, 0f,
                                     (entity.bounds[1] + entity.bounds[3]) * 0.5f);
                halfX = Mathf.Abs(entity.bounds[2] - entity.bounds[0]) * 0.5f;
                halfZ = Mathf.Abs(entity.bounds[3] - entity.bounds[1]) * 0.5f;
            }
            else
            {
                center = WorldEntityCatalog.WorldPosition(entity);
            }
            Vector3 baseDirection = from - center;
            baseDirection.y = 0f;
            if (baseDirection.sqrMagnitude < 0.01f) baseDirection = Vector3.forward;
            baseDirection.Normalize();
            float[] angles = { 0f, 30f, -30f, 60f, -60f, 90f, -90f, 150f, -150f, 180f };
            string firstError = "";
            for (int i = 0; i < angles.Length; i++)
            {
                Vector3 direction = Quaternion.Euler(0f, angles[i], 0f) * baseDirection;
                // AABB support distance along this direction + 1.2 m clearance
                float support = Mathf.Abs(direction.x) * halfX + Mathf.Abs(direction.z) * halfZ;
                Vector3 candidate = center + direction * (support + 1.2f);
                candidate.y = WorldData.GroundHeightAt(candidate);
                if (!WorldData.IsWalkable(candidate))
                {
                    if (string.IsNullOrEmpty(firstError)) firstError = "候选点不可走";
                    continue;
                }
                var path = WorldData.FindPath(from, candidate);
                if (path == null || path.Count == 0)
                {
                    if (string.IsNullOrEmpty(firstError))
                        firstError = "无法到达候选点：" + (WorldData.LastError ?? "无路径");
                    continue;
                }
                target = candidate;
                kind = "entity_footprint_standoff";
                reason = "direction=" + angles[i].ToString("F0") + "° support=" +
                         support.ToString("F2") + "m";
                return true;
            }
            kind = "entity_footprint_standoff";
            reason = "该物体周围没有可走且可寻路的靠近点（" + firstError + "）";
            return false;
        }

        string DescribeSelection()
        {
            var entity = WorldEntityCatalog.Get(_selectedId);
            if (entity != null) return DescribeEntity(entity);
            if (_hasGround) return DescribeGround(_groundPoint);
            return "no_selection";
        }

        WorldEntityCatalog.Entity FindNearestUsableBench()
        {
            var character = FindCharacter();
            Vector3 from = character != null ? character.position : WorldEntityCatalog.Spawn;
            WorldEntityCatalog.Entity best = null;
            float bestDistance = float.MaxValue;
            foreach (var candidate in WorldEntityCatalog.Query("bench", null, 0))
            {
                if (candidate.seat == null || !candidate.seat.usable) continue;
                Vector3 position = WorldEntityCatalog.WorldPosition(candidate);
                float distance = (position - from).sqrMagnitude;
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = candidate;
                }
            }
            return best;
        }

        string RouteRenderSnapshot()
        {
            var sb = new StringBuilder();
            sb.Append("{");
            var character = FindCharacter();
            Vector2 characterLocal = character != null ? ToLocal(character.position) : Vector2.zero;
            sb.Append("\"characterLocal\":[").Append(characterLocal.x.ToString("F2")).Append(',')
              .Append(characterLocal.y.ToString("F2")).Append("],");
            sb.Append("\"gazeSource\":\"").Append(_gazeSource ?? "none").Append("\",");
            var world = new List<string>();
            var rendered = new List<string>();
            var expected = new List<string>();
            float maxError = 0f;
            if (_previewRoute != null)
            {
                sb.Append("\"previewRouteWorld\":").Append(_previewRoute.Count).Append(',');
                for (int i = 0; i < _previewRoute.Count; i++)
                {
                    Vector3 p = _previewRoute[i];
                    Vector2 projected = ToLocal(p);
                    int localIndex = i + (character != null ? 1 : 0);
                    Vector2 actual = localIndex < _lastPreviewLocal.Count
                        ? _lastPreviewLocal[localIndex] : new Vector2(-9999f, -9999f);
                    float error = Vector2.Distance(projected, actual);
                    if (error > maxError) maxError = error;
                    world.Add(p.x.ToString("F2") + "," + p.z.ToString("F2"));
                    rendered.Add(actual.x.ToString("F2") + "," + actual.y.ToString("F2"));
                    expected.Add(projected.x.ToString("F2") + "," + projected.y.ToString("F2"));
                }
            }
            sb.Append("\"previewLocalCount\":").Append(_lastPreviewLocal.Count).Append(',');
            sb.Append("\"maxProjectionError\":").Append(maxError.ToString("F4")).Append(',');
            sb.Append("\"world\":[\"").Append(string.Join("\",\"", world.ToArray())).Append("\"],");
            sb.Append("\"rendered\":[\"").Append(string.Join("\",\"", rendered.ToArray())).Append("\"],");
            sb.Append("\"expected\":[\"").Append(string.Join("\",\"", expected.ToArray())).Append("\"],");
            sb.Append("\"previewTarget\":\"").Append(PreviewTargetForTest()).Append("\",");
            sb.Append("\"confirmTarget\":\"").Append(ConfirmTargetForTest()).Append("\",");
            sb.Append("\"resolvedTarget\":\"").Append(ResolvedTargetForTest()).Append("\"");
            sb.Append("}");
            return sb.ToString();
        }

        string LayoutSnapshot()
        {
            var sb = new StringBuilder();
            Vector2 viewport = _viewport != null ? _viewport.rect.size : Vector2.zero;
            Vector2 canvasSize = _canvas != null ? ((RectTransform)_canvas.transform).rect.size : Vector2.zero;
            sb.Append("{");
            sb.Append("\"open\":").Append(_open ? "true" : "false").Append(',');
            sb.Append("\"screen\":[").Append(Screen.width).Append(',').Append(Screen.height).Append("],");
            sb.Append("\"canvas\":[").Append(canvasSize.x.ToString("F1")).Append(',')
              .Append(canvasSize.y.ToString("F1")).Append("],");
            sb.Append("\"scaleFactor\":").Append((_canvas != null ? _canvas.scaleFactor : 1f).ToString("F3")).Append(',');
            sb.Append("\"viewport\":[").Append(viewport.x.ToString("F1")).Append(',')
              .Append(viewport.y.ToString("F1")).Append("],");
            sb.Append("\"dpi\":").Append(Screen.dpi.ToString("F1")).Append(',');
            sb.Append("\"zoom\":").Append(_zoom.ToString("F3")).Append(',');
            sb.Append("\"markers\":").Append(_visibleMarkers).Append(',');
            sb.Append("\"debugLayer\":").Append(_debugLayer ? "true" : "false").Append(',');
            sb.Append("\"selected\":\"").Append(_selectedId ?? "").Append("\",");
            sb.Append("\"routePoints\":").Append(_previewRoute != null ? _previewRoute.Count : 0).Append(',');
            sb.Append("\"filter\":\"").Append(_filterCls ?? "").Append("\",");
            sb.Append("\"search\":\"").Append((_searchText ?? "").Replace("\"", "'")).Append("\",");
            // world->map round trip sanity for the four map corners of interest
            if (WorldMapData.Loaded)
            {
                var span = new Vector2(WorldMapData.MaxX - WorldMapData.MinX, WorldMapData.MaxZ - WorldMapData.MinZ);
                Vector2 probe = new Vector2(-20.2857f, -67.0f);
                Vector2 local = WorldMapProjection.WorldToMap(probe, viewport, _center, span, _zoom);
                Vector2 back = WorldMapProjection.MapToWorld(local, viewport, _center, span, _zoom);
                sb.Append("\"projectionProbe\":{\"world\":[").Append(probe.x.ToString("F3")).Append(',')
                  .Append(probe.y.ToString("F3")).Append("],\"local\":[").Append(local.x.ToString("F2"))
                  .Append(',').Append(local.y.ToString("F2")).Append("],\"error\":")
                  .Append(Vector2.Distance(probe, back).ToString("F5")).Append("},");
            }
            sb.Append("\"status\":\"").Append((_status ?? "").Replace("\\", "/").Replace("\"", "'")).Append("\"}");
            return sb.ToString();
        }

        // ---------------- per-frame map drawing ----------------
        void LateUpdate()
        {
            if (!_open || !_built || _canvas == null || !_canvas.gameObject.activeSelf) return;
            if (WorldMapData.Loaded == false) return;
            // keep the info card/buttons in sync with the real seat controller and
            // map navigation state (坐下→坐稳→起身 updates without re-selecting)
            string signature = BuildStateSignature();
            if (signature != _stateSignature)
            {
                _stateSignature = signature;
                RefreshInfo();
            }
            DrawBasemap();
            if (_dirty) RebuildMarkers();
            UpdateOverlayGraphic();
            UpdateLabels();
            UpdateStatusLabel();
        }

        void DrawBasemap()
        {
            if (_basemap == null || _viewport == null) return;
            if (WorldMapData.Basemap == null)
            {
                _basemap.enabled = false;
                return;
            }
            _basemap.enabled = true;
            _basemap.texture = WorldMapData.Basemap;
            Vector2 size = _viewport.rect.size;
            if (size.x < 4f || size.y < 4f) return;
            var span = new Vector2(WorldMapData.MaxX - WorldMapData.MinX, WorldMapData.MaxZ - WorldMapData.MinZ);
            var bmin = new Vector2(WorldMapData.MinX, WorldMapData.MinZ);
            var bmax = new Vector2(WorldMapData.MaxX, WorldMapData.MaxZ);
            Rect rect = WorldMapProjection.ImageRect(size, _center, bmin, bmax, _zoom);
            _basemap.rectTransform.anchoredPosition = new Vector2(rect.x, -rect.y);
            _basemap.rectTransform.sizeDelta = new Vector2(rect.width, rect.height);
        }

        Vector2 ToLocal(Vector3 world)
        {
            if (_viewport == null) return Vector2.zero;
            Vector2 size = _viewport.rect.size;
            var span = new Vector2(WorldMapData.MaxX - WorldMapData.MinX, WorldMapData.MaxZ - WorldMapData.MinZ);
            Vector2 p = WorldMapProjection.WorldToMap(new Vector2(world.x, world.z), size, _center, span, _zoom);
            return new Vector2(p.x, size.y - p.y);
        }

        Vector2 ToLocalXZ(Vector2 worldXZ)
        {
            if (_viewport == null) return Vector2.zero;
            Vector2 size = _viewport.rect.size;
            var span = new Vector2(WorldMapData.MaxX - WorldMapData.MinX, WorldMapData.MaxZ - WorldMapData.MinZ);
            Vector2 p = WorldMapProjection.WorldToMap(worldXZ, size, _center, span, _zoom);
            return new Vector2(p.x, size.y - p.y);
        }

        Vector2 LocalToWorld(Vector2 local)
        {
            Vector2 size = _viewport.rect.size;
            var span = new Vector2(WorldMapData.MaxX - WorldMapData.MinX, WorldMapData.MaxZ - WorldMapData.MinZ);
            Vector2 yDown = new Vector2(local.x, size.y - local.y);
            return WorldMapProjection.MapToWorld(yDown, size, _center, span, _zoom);
        }

        void ClampView()
        {
            _center.x = Mathf.Clamp(_center.x, WorldMapData.MinX, WorldMapData.MaxX);
            _center.y = Mathf.Clamp(_center.y, WorldMapData.MinZ, WorldMapData.MaxZ);
        }

        void RebuildMarkers()
        {
            _dirty = false;
            _entityMarkers.Clear();
            _anchorMarkers.Clear();
            if (!WorldMapData.Loaded || _viewport == null) { ApplyMarkerData(); return; }
            string cls = _filterCls;
            var entities = WorldMapData.Data.entities;
            int count = 0;
            foreach (var entity in entities)
            {
                if (entity == null || entity.pos == null || entity.pos.Length < 3) continue;
                bool show;
                if (cls == "anchor") show = false;
                else if (string.IsNullOrEmpty(cls)) show = true;
                else show = entity.cls == cls;
                if (!show) continue;
                if (_searchIds != null && !_searchIds.Contains(entity.id)) continue;
                Vector3 position = new Vector3(entity.pos[0], entity.pos[1], entity.pos[2]);
                var dynamicWorld = Yixuan.Dynamic.DynamicWorld.Instance;
                if (dynamicWorld != null)
                {
                    var live = dynamicWorld.CurrentPositionOf(entity.id);
                    if (live.HasValue) position = live.Value;
                }
                Vector2 local = ToLocal(position);
                if (local.x < -20f || local.y < -20f || local.x > _viewport.rect.width + 20f ||
                    local.y > _viewport.rect.height + 20f) continue;
                _entityMarkers.Add(new MapMarker
                {
                    Local = local,
                    Color = ColorForClass(entity.cls),
                    Size = MarkerSize(entity.cls),
                    Selected = _selectedId == entity.id,
                    Kind = 0,
                });
                count++;
            }
            var anchors = WorldEntityCatalog.Anchors;
            if (anchors != null && (string.IsNullOrEmpty(cls) || cls == "anchor"))
            {
                foreach (var anchor in anchors)
                {
                    if (anchor == null || anchor.pos == null || anchor.pos.Length < 3) continue;
                    if (_searchIds != null && !_searchIds.Contains("anchor/" + anchor.id)) continue;
                    Vector2 local = ToLocal(new Vector3(anchor.pos[0], anchor.pos[1], anchor.pos[2]));
                    if (local.x < -20f || local.y < -20f || local.x > _viewport.rect.width + 20f ||
                        local.y > _viewport.rect.height + 20f) continue;
                    _anchorMarkers.Add(new MapMarker
                    {
                        Local = local,
                        Color = new Color(1f, 0.83f, 0.25f, 0.95f),
                        Size = 7f,
                        Kind = 1,
                    });
                    count++;
                }
            }
            _visibleMarkers = count;
            ApplyMarkerData();
            _labelsDirty = true;
            BuildLabels();
        }

        void ApplyMarkerData()
        {
            if (_markers != null)
            {
                _markers.Markers = _entityMarkers;
                _markers.RebuildMesh();
            }
        }

        static Color ColorForClass(string cls)
        {
            switch (cls)
            {
                case "bench": return new Color(0.32f, 0.86f, 0.72f, 0.95f);
                case "vehicle": return new Color(1f, 0.62f, 0.26f, 0.95f);
                case "eternal_flame": return new Color(1f, 0.30f, 0.28f, 1f);
                case "building": return new Color(0.42f, 0.60f, 0.92f, 0.75f);
                case "tree": case "hedge": case "planter": case "reeds":
                    return new Color(0.35f, 0.65f, 0.38f, 0.45f);
                case "street_lamp": return new Color(0.95f, 0.88f, 0.55f, 0.55f);
                default: return new Color(0.62f, 0.70f, 0.82f, 0.50f);
            }
        }

        static float MarkerSize(string cls)
        {
            switch (cls)
            {
                case "bench": return 6f;
                case "vehicle": return 7f;
                case "eternal_flame": return 9f;
                case "building": return 5f;
                default: return 3.2f;
            }
        }

        void UpdateOverlayGraphic()
        {
            if (_overlay == null) return;
            _overlay.ClearDynamic();
            // character body arrow + eye/gaze source
            var character = FindCharacter();
            if (character != null)
            {
                _hasCharacter = true;
                _characterLocal = ToLocal(character.position);
                _characterYaw = character.transform.eulerAngles.y;
                _overlay.AddArrow(_characterLocal, _characterYaw, 13f,
                                  new Color(0.30f, 0.95f, 1f, 1f), false);
                string gazeSource;
                Transform gaze = ResolveGazeTransform(out gazeSource);
                _gazeSource = gazeSource;
                if (gaze != null)
                {
                    Vector3 forward = gaze.forward;
                    forward.y = 0f;
                    if (forward.sqrMagnitude > 0.01f)
                    {
                        float gazeYaw = Mathf.Atan2(forward.x, forward.z) * Mathf.Rad2Deg;
                        _overlay.AddFan(_characterLocal, gazeYaw, 24f, 110f,
                                        new Color(1f, 0.75f, 0.20f, 0.16f));
                    }
                }
            }
            // anchors
            foreach (var anchor in _anchorMarkers) _overlay.AddDiamond(anchor.Local, anchor.Size, anchor.Color);
            // preview route: world waypoints projected through the live map transform,
            // with the current actor position prepended so single-waypoint routes
            // still draw a line.
            _lastPreviewLocal = new List<Vector2>();
            if (_previewRoute != null && _previewRoute.Count > 0)
            {
                if (character != null) _lastPreviewLocal.Add(ToLocal(character.position));
                for (int i = 0; i < _previewRoute.Count; i++)
                    _lastPreviewLocal.Add(ToLocal(_previewRoute[i]));
                _overlay.AddPolyline(_lastPreviewLocal, Color.green, 3f);
                Vector3 last = _previewRoute[_previewRoute.Count - 1];
                _overlay.AddRing(ToLocal(last), 10f, new Color(0.4f, 1f, 0.5f, 1f), 3f);
            }
            // real navigation route from the shared map navigation system
            _lastNavigationLocal = new List<Vector2>();
            if (WorldMapPanel.HasNavigation && WorldMapPanel.CopyRemainingRoute(_navRouteBuffer))
            {
                if (character != null) _lastNavigationLocal.Add(ToLocal(character.position));
                for (int i = 0; i < _navRouteBuffer.Count; i++)
                    _lastNavigationLocal.Add(ToLocal(_navRouteBuffer[i]));
                _overlay.AddPolyline(_lastNavigationLocal, new Color(0.25f, 0.85f, 1f, 0.95f), 3f);
                Vector3 target = WorldMapPanel.CurrentTarget;
                _overlay.AddRing(ToLocal(target), 9f, new Color(1f, 0.55f, 0.25f, 1f), 3f);
            }
            // ground selection
            if (_hasGround)
            {
                Vector2 local = ToLocalXZ(_groundPoint);
                _overlay.AddRing(local, 12f, new Color(1f, 0.95f, 0.55f, 1f), 2f);
                _overlay.AddCross(local, 8f, new Color(1f, 0.95f, 0.55f, 1f));
            }
            // selected entity ring
            var entity = WorldEntityCatalog.Get(_selectedId);
            if (entity != null)
            {
                Vector2 local = ToLocal(WorldEntityCatalog.WorldPosition(entity));
                _overlay.AddRing(local, 14f, Color.white, 3f);
                if (entity.seat != null && entity.seat.usable)
                {
                    var approach = WorldEntityCatalog.ApproachPoint(entity);
                    if (approach != null)
                    {
                        Vector2 approachLocal = ToLocal(approach.Value);
                        _overlay.AddRing(approachLocal, 6f, new Color(0.4f, 1f, 0.8f, 0.95f), 2f);
                        _overlay.AddDashedLine(local, approachLocal, 1.5f,
                                               new Color(0.4f, 1f, 0.8f, 0.75f), 6f, 4f);
                    }
                }
            }
            if (_debugLayer && WorldMapData.Data != null)
            {
                foreach (var debugEntity in WorldMapData.Data.entities)
                {
                    if (debugEntity == null || debugEntity.bounds == null ||
                        debugEntity.bounds.Length < 4) continue;
                    Vector2 a = ToLocalXZ(new Vector2(debugEntity.bounds[0], debugEntity.bounds[1]));
                    Vector2 b = ToLocalXZ(new Vector2(debugEntity.bounds[2], debugEntity.bounds[3]));
                    _overlay.AddRect(a, b, new Color(1f, 0.35f, 0.35f, 0.35f), 1f);
                }
            }
            _overlay.RebuildMesh();
            _lastGazeSource = _gazeSource;
        }

        /// <summary>
        /// Gaze direction source: the character's real EyeCamera by default (body
        /// arrow stays independent); a vehicle-view source can be registered by the
        /// future vehicle controller.  Falls back to the character transform only
        /// when no eye source exists, and says so in the layout snapshot.
        /// </summary>
        static Transform _vehicleGazeSource;
        static string _lastGazeSource = "none";
        public static void SetVehicleGazeSource(Transform source) { _vehicleGazeSource = source; }

        Transform ResolveGazeTransform(out string source)
        {
            source = "none";
            if (_vehicleGazeSource != null)
            {
                source = "vehicle_view";
                return _vehicleGazeSource;
            }
            var director = UnityEngine.Object.FindObjectOfType<Yixuan.Presentation.CameraDirector>();
            if (director != null && director.EyeSource != null)
            {
                source = "character_eye";
                return director.EyeSource.transform;
            }
            var eyeGo = GameObject.Find("EyeCamera");
            if (eyeGo != null)
            {
                var eyeCamera = eyeGo.GetComponent<Camera>();
                if (eyeCamera != null)
                {
                    source = "character_eye";
                    return eyeCamera.transform;
                }
            }
            var character = FindCharacter();
            if (character != null)
            {
                source = "character_forward_fallback";
                return character;
            }
            return null;
        }

        void BuildLabels()
        {
            if (_labelsHost == null) return;
            _labelsDirty = false;
            // show labels for selected entity, search matches and anchors, capped
            var wanted = new List<KeyValuePair<Vector2, string>>();
            var selected = WorldEntityCatalog.Get(_selectedId);
            if (selected != null)
            {
                Vector2 local = ToLocal(WorldEntityCatalog.WorldPosition(selected));
                wanted.Add(new KeyValuePair<Vector2, string>(local + new Vector2(10f, 6f),
                                                             WorldEntityCatalog.DisplayName(selected)));
            }
            int budget = 28;
            if (_searchIds != null && WorldEntityCatalog.Loaded)
            {
                foreach (var entity in WorldMapCatalogSearch())
                {
                    if (budget-- <= 0) break;
                    Vector2 local = ToLocal(WorldEntityCatalog.WorldPosition(entity));
                    wanted.Add(new KeyValuePair<Vector2, string>(local + new Vector2(8f, 4f),
                                                                 WorldEntityCatalog.DisplayName(entity)));
                }
            }
            else if (_zoom >= 2.2f && WorldMapData.Loaded)
            {
                foreach (var marker in _entityMarkers)
                {
                    if (budget-- <= 0) break;
                    wanted.Add(new KeyValuePair<Vector2, string>(marker.Local + new Vector2(8f, 4f), ""));
                }
            }
            foreach (var anchor in _anchorMarkers)
            {
                if (budget-- <= 0) break;
                // at overview zoom the anchor labels would cover the whole plan;
                // show them at closer zoom, in the 地点 filter or while searching.
                bool showAnchorLabel = _zoom >= 1.6f || _filterCls == "anchor" || _searchIds != null;
                if (!showAnchorLabel) continue;
                string label = AnchorLabelAt(anchor.Local);
                if (!string.IsNullOrEmpty(label))
                    wanted.Add(new KeyValuePair<Vector2, string>(anchor.Local + new Vector2(9f, 2f), label));
            }
            while (_labels.Count < wanted.Count)
            {
                var text = NewText("Lbl" + _labels.Count, _labelsHost, Vector2.zero, Vector2.zero,
                                   Vector2.zero, Vector2.zero, "", 13, TextAnchor.MiddleLeft,
                                   new Color(0.92f, 0.96f, 1f, 0.95f));
                text.raycastTarget = false;
                _labels.Add(text);
            }
            for (int i = 0; i < _labels.Count; i++)
            {
                if (i >= wanted.Count)
                {
                    _labels[i].gameObject.SetActive(false);
                    continue;
                }
                _labels[i].gameObject.SetActive(true);
                _labels[i].text = wanted[i].Value;
                var rt = _labels[i].rectTransform;
                rt.anchorMin = Vector2.zero;
                rt.anchorMax = Vector2.zero;
                rt.pivot = new Vector2(0f, 0f);
                rt.anchoredPosition = wanted[i].Key;
                rt.sizeDelta = new Vector2(220f, 18f);
            }
        }

        IEnumerable<WorldEntityCatalog.Entity> WorldMapCatalogSearch()
        {
            var list = new List<WorldEntityCatalog.Entity>();
            if (_searchIds == null) return list;
            foreach (var entity in WorldEntityCatalog.All)
            {
                if (entity != null && _searchIds.Contains(entity.id)) list.Add(entity);
            }
            return list;
        }

        string AnchorLabelAt(Vector2 local)
        {
            var anchors = WorldEntityCatalog.Anchors;
            if (anchors == null) return null;
            foreach (var anchor in anchors)
            {
                if (anchor == null || anchor.pos == null || anchor.pos.Length < 3) continue;
                Vector2 p = ToLocal(new Vector3(anchor.pos[0], anchor.pos[1], anchor.pos[2]));
                if ((p - local).sqrMagnitude < 1f) return anchor.label;
            }
            return null;
        }

        void UpdateStatusLabel()
        {
            if (_statusText != null) _statusText.text = _status;
        }

        void ResetView()
        {
            _center = new Vector2((WorldMapData.MinX + WorldMapData.MaxX) * 0.5f,
                                  (WorldMapData.MinZ + WorldMapData.MaxZ) * 0.5f);
            _zoom = 1f;
            _previewRoute = null;
            _dirty = true;
            _status = "视图已复位";
            _statusKind = "info";
            UpdateStatusLabel();
        }

        // ---------------- map input (called by BigMapInputHandler) ----------------
        public void PanBy(Vector2 localDelta)
        {
            if (_viewport == null || !WorldMapData.Loaded) return;
            Vector2 size = _viewport.rect.size;
            var span = new Vector2(WorldMapData.MaxX - WorldMapData.MinX, WorldMapData.MaxZ - WorldMapData.MinZ);
            float scale = WorldMapProjection.Scale(size, span, _zoom);
            if (scale < 1e-4f) return;
            _center.x -= localDelta.x / scale;
            _center.y -= localDelta.y / scale;
            ClampView();
            _dirty = true;
        }

        public void ZoomAt(Vector2 local, float scrollDelta)
        {
            if (_viewport == null || !WorldMapData.Loaded) return;
            Vector2 worldBefore = LocalToWorld(local);
            float factor = 1f + 0.18f * scrollDelta;
            _zoom = Mathf.Clamp(_zoom * factor, 0.85f, 16f);
            Vector2 size = _viewport.rect.size;
            var span = new Vector2(WorldMapData.MaxX - WorldMapData.MinX, WorldMapData.MaxZ - WorldMapData.MinZ);
            float scale = WorldMapProjection.Scale(size, span, _zoom);
            if (scale > 1e-4f)
            {
                _center.x = worldBefore.x - (local.x - size.x * 0.5f) / scale;
                _center.y = worldBefore.y - (local.y - size.y * 0.5f) / scale;
            }
            ClampView();
            _dirty = true;
            _status = "缩放 ×" + _zoom.ToString("0.0");
            _statusKind = "info";
        }

        public void HandleMapClick(Vector2 local)
        {
            if (!WorldMapData.Loaded || _viewport == null) return;
            Vector2 viewportSize = _viewport.rect.size;
            if (viewportSize.x < 4f || viewportSize.y < 4f)
            {
                _status = "地图尚未完成布局，忽略本次点击";
                _statusKind = "info";
                return;
            }
            if (local.x < 0f || local.y < 0f || local.x > viewportSize.x || local.y > viewportSize.y)
            {
                _status = "点击位于地图可视区外，忽略";
                _statusKind = "info";
                return;
            }
            // entity marker hit test in viewport-local pixels
            float best = 16f * 16f;
            string bestId = null;
            foreach (var marker in _entityMarkers)
            {
                float distance = (marker.Local - local).sqrMagnitude;
                if (distance < best)
                {
                    best = distance;
                    bestId = MarkerIdAt(marker.Local);
                }
            }
            if (bestId == null)
            {
                foreach (var anchor in _anchorMarkers)
                {
                    float distance = (anchor.Local - local).sqrMagnitude;
                    if (distance < best) { best = distance; bestId = "anchor:" + AnchorLabelAt(anchor.Local); }
                }
            }
            if (bestId != null)
            {
                if (bestId.StartsWith("anchor:", StringComparison.Ordinal))
                {
                    string label = bestId.Substring(7);
                    var anchor = FindAnchorByLabel(label);
                    if (anchor != null)
                    {
                        _hasGround = true;
                        _groundPoint = new Vector2(anchor.pos[0], anchor.pos[2]);
                        _selectedId = null;
                        _previewRoute = null;
                        RefreshInfo();
                        _status = "地点：" + label + " · 点击确认前往";
                        _statusKind = "selected";
                        PreviewRoute();
                        _dirty = true;
                        return;
                    }
                }
                else if (SelectEntity(bestId))
                {
                    PreviewRoute();
                    return;
                }
            }
            Vector2 world = LocalToWorld(local);
            if (world.x < WorldMapData.MinX || world.x > WorldMapData.MaxX ||
                world.y < WorldMapData.MinZ || world.y > WorldMapData.MaxZ)
            {
                _status = "点击超出地图范围，未选择";
                _statusKind = "fail";
                UpdateStatusLabel();
                return;
            }
            _selectedId = null;
            _hasGround = true;
            _groundPoint = world;
            _previewRoute = null;
            RefreshInfo();
            _status = "地面点 (" + world.x.ToString("F1") + ", " + world.y.ToString("F1") + ") · 点击确认前往";
            _statusKind = "selected";
            PreviewRoute();
            _dirty = true;
        }

        string MarkerIdAt(Vector2 local)
        {
            float best = 16f * 16f;
            string bestId = null;
            if (!WorldMapData.Loaded) return null;
            foreach (var entity in WorldMapData.Data.entities)
            {
                if (entity == null || entity.pos == null || entity.pos.Length < 3) continue;
                Vector2 p = ToLocal(new Vector3(entity.pos[0], entity.pos[1], entity.pos[2]));
                float distance = (p - local).sqrMagnitude;
                if (distance < best)
                {
                    best = distance;
                    bestId = entity.id;
                }
            }
            return bestId;
        }

        WorldEntityCatalog.Anchor FindAnchorByLabel(string label)
        {
            var anchors = WorldEntityCatalog.Anchors;
            if (anchors == null) return null;
            foreach (var anchor in anchors)
            {
                if (anchor != null && anchor.label == label) return anchor;
            }
            return null;
        }

        // ---------------- labels ----------------
        void UpdateLabels()
        {
            if (_labelsDirty) BuildLabels();
        }

        // ---------------- character helpers ----------------
        Transform FindCharacter()
        {
            var character = UnityEngine.Object.FindObjectOfType<Yixuan.Core.YixuanCharacter>();
            if (character != null) return character.transform;
            var loco = UnityEngine.Object.FindObjectOfType<CharacterLocomotion>();
            return loco != null ? loco.transform : null;
        }

        Camera FindGameCamera()
        {
            var cameras = Camera.allCameras;
            foreach (var camera in cameras)
            {
                if (camera == null || camera == _uiCamera) continue;
                if (camera.cullingMask == (1 << 5)) continue;
                return camera;
            }
            return Camera.main;
        }

        // ---------------- capture ----------------
        bool CaptureToPng(string path, out int width, out int height, out string error)
        {
            width = 0;
            height = 0;
            error = "";
            if (!_built || _canvas == null || _uiCamera == null || _viewport == null)
            {
                error = "big map not built";
                return false;
            }
            bool wasOpen = _open;
            if (!wasOpen) SetOpen(true);
            _canvas.gameObject.SetActive(true);
            int captureWidth = 1920;
            int captureHeight = 1080;
            var rt = new RenderTexture(captureWidth, captureHeight, 24, RenderTextureFormat.ARGB32,
                                       RenderTextureReadWrite.sRGB);
            rt.name = "BigMapCaptureRT";
            rt.Create();
            var previousTarget = _uiCamera.targetTexture;
            var previousActive = RenderTexture.active;
            bool previousScaler = _scaler != null ? _scaler.enabled : true;
            RenderMode previousMode = _canvas.renderMode;
            Vector3 previousCameraPosition = _uiCamera.transform.position;
            Quaternion previousCameraRotation = _uiCamera.transform.rotation;
            float previousOrthoSize = _uiCamera.orthographicSize;
            var canvasRect = (RectTransform)_canvas.transform;
            Vector2 previousCanvasSize = canvasRect.sizeDelta;
            Vector3 previousCanvasScale = canvasRect.localScale;
            Vector3 previousCanvasPosition = canvasRect.position;
            try
            {
                // In batchmode the editor Game View is a small portrait size, so a
                // Screen Space canvas would be captured with a portrait layout.
                // Switch to a deterministic world-space 1920x1080 canvas for the
                // capture; the rendering path then maps 1 canvas unit to 1 pixel.
                if (_scaler != null) _scaler.enabled = false;
                _canvas.renderMode = RenderMode.WorldSpace;
                _canvas.worldCamera = _uiCamera;
                canvasRect.pivot = new Vector2(0.5f, 0.5f);
                canvasRect.sizeDelta = new Vector2(captureWidth, captureHeight);
                canvasRect.localScale = Vector3.one;
                canvasRect.position = Vector3.zero;
                _uiCamera.orthographic = true;
                _uiCamera.orthographicSize = captureHeight * 0.5f;
                _uiCamera.nearClipPlane = 0.1f;
                _uiCamera.farClipPlane = 100f;
                _uiCamera.transform.position = new Vector3(0f, 0f, -10f);
                _uiCamera.transform.rotation = Quaternion.identity;
                _uiCamera.targetTexture = rt;
                _uiCamera.enabled = true;
                Canvas.ForceUpdateCanvases();
                LayoutRebuilder.ForceRebuildLayoutImmediate(_root);
                Canvas.ForceUpdateCanvases();
                _lastViewportSize = Vector2.zero;
                _dirty = true;
                RebuildMarkersNow();
                DrawBasemap();
                UpdateOverlayGraphic();
                _labelsDirty = true;
                BuildLabels();
                Canvas.ForceUpdateCanvases();
                try
                {
                    string diagPath = Path.Combine(Path.GetDirectoryName(path) ?? ".",
                        "bigmap_capture_diag.txt");
                    File.WriteAllText(diagPath,
                        "screen=" + Screen.width + "x" + Screen.height + "\n" +
                        "rt=" + captureWidth + "x" + captureHeight + "\n" +
                        "canvas=" + ((RectTransform)_canvas.transform).rect.size + "\n" +
                        "scaleFactor=" + _canvas.scaleFactor + "\n" +
                        "viewport=" + _viewport.rect.size + "\n" +
                        "root=" + _root.rect.size + "\n" +
                        "mode=" + _canvas.renderMode + "\n");
                }
                catch { }
                _uiCamera.Render();
                RenderTexture.active = rt;
                var texture = new Texture2D(captureWidth, captureHeight, TextureFormat.RGB24, false);
                texture.ReadPixels(new Rect(0, 0, captureWidth, captureHeight), 0, 0);
                texture.Apply(false, false);
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.WriteAllBytes(path, texture.EncodeToPNG());
                width = captureWidth;
                height = captureHeight;
                Destroy(texture);
                _status = "截图完成 " + captureWidth + "x" + captureHeight + " → " + path;
                _statusKind = "info";
                return true;
            }
            catch (Exception e)
            {
                error = e.Message;
                return false;
            }
            finally
            {
                RenderTexture.active = previousActive;
                if (_uiCamera != null)
                {
                    _uiCamera.targetTexture = previousTarget;
                    _uiCamera.transform.position = previousCameraPosition;
                    _uiCamera.transform.rotation = previousCameraRotation;
                    _uiCamera.orthographicSize = previousOrthoSize;
                    _uiCamera.enabled = _open;
                }
                _canvas.renderMode = previousMode;
                _canvas.worldCamera = _uiCamera;
                canvasRect.sizeDelta = previousCanvasSize;
                canvasRect.localScale = previousCanvasScale;
                canvasRect.position = previousCanvasPosition;
                if (_scaler != null) _scaler.enabled = previousScaler;
                rt.Release();
                Destroy(rt);
                if (!wasOpen) SetOpen(false);
                Canvas.ForceUpdateCanvases();
                _lastViewportSize = Vector2.zero;
                _dirty = true;
            }
        }

        // ---------------- uGUI construction helpers ----------------
        static RectTransform NewRect(string name, Transform parent, Vector2 anchorMin, Vector2 anchorMax,
                                     Vector2 offsetMin, Vector2 offsetMax)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.layer = 5;
            go.transform.SetParent(parent, false);
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = anchorMin;
            rt.anchorMax = anchorMax;
            rt.offsetMin = offsetMin;
            rt.offsetMax = offsetMax;
            rt.localScale = Vector3.one;
            return rt;
        }

        static Image NewImage(string name, Transform parent, Vector2 anchorMin, Vector2 anchorMax,
                              Vector2 offsetMin, Vector2 offsetMax, Color color)
        {
            var rt = NewRect(name, parent, anchorMin, anchorMax, offsetMin, offsetMax);
            var image = rt.gameObject.AddComponent<Image>();
            image.color = color;
            image.raycastTarget = false;
            return image;
        }

        static Text NewText(string name, Transform parent, Vector2 anchorMin, Vector2 anchorMax,
                            Vector2 offsetMin, Vector2 offsetMax, string content, int size,
                            TextAnchor anchor, Color color)
        {
            var rt = NewRect(name, parent, anchorMin, anchorMax, offsetMin, offsetMax);
            var text = rt.gameObject.AddComponent<Text>();
            EnsureFont();
            text.font = _font;
            text.fontSize = size;
            text.text = content;
            text.alignment = anchor;
            text.color = color;
            text.raycastTarget = false;
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.verticalOverflow = VerticalWrapMode.Overflow;
            text.supportRichText = false;
            return text;
        }

        static Button NewButton(string name, Transform parent, Vector2 anchorMin, Vector2 anchorMax,
                                Vector2 offsetMin, Vector2 offsetMax, string label, Color color, Action onClick)
        {
            var image = NewImage(name, parent, anchorMin, anchorMax, offsetMin, offsetMax, color);
            image.raycastTarget = true;
            var button = image.gameObject.AddComponent<Button>();
            var colors = button.colors;
            colors.normalColor = Color.white;
            colors.highlightedColor = new Color(0.85f, 0.92f, 1f, 1f);
            colors.pressedColor = new Color(0.7f, 0.8f, 1f, 1f);
            button.colors = colors;
            button.targetGraphic = image;
            var text = NewText("Label", button.transform, Vector2.zero, Vector2.one,
                               new Vector2(4f, 2f), new Vector2(-4f, -2f), label, 15,
                               TextAnchor.MiddleCenter, new Color(0.93f, 0.96f, 1f, 1f));
            if (onClick != null) button.onClick.AddListener(() => onClick());
            return button;
        }

        static InputField NewInput(string name, Transform parent, Vector2 anchorMin, Vector2 anchorMax,
                                    Vector2 offsetMin, Vector2 offsetMax, string initial)
        {
            var image = NewImage(name, parent, anchorMin, anchorMax, offsetMin, offsetMax,
                                 new Color(0.10f, 0.14f, 0.20f, 1f));
            image.raycastTarget = true;
            var field = image.gameObject.AddComponent<InputField>();
            var text = NewText("Text", image.transform, Vector2.zero, Vector2.one,
                               new Vector2(8f, 2f), new Vector2(-8f, -2f), "", 16,
                               TextAnchor.MiddleLeft, new Color(0.92f, 0.96f, 1f, 1f));
            text.raycastTarget = false;
            text.supportRichText = false;
            var placeholder = NewText("Placeholder", image.transform, Vector2.zero, Vector2.one,
                                      new Vector2(8f, 2f), new Vector2(-8f, -2f), "输入中文或 ID…", 16,
                                      TextAnchor.MiddleLeft, new Color(0.5f, 0.58f, 0.70f, 1f));
            placeholder.raycastTarget = false;
            field.textComponent = text;
            field.placeholder = placeholder;
            field.targetGraphic = image;
            field.lineType = InputField.LineType.SingleLine;
            field.text = initial;
            return field;
        }

        static BigMapMarkerGraphic CreateMarkers(string name, RectTransform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.layer = 5;
            go.transform.SetParent(parent, false);
            var rt = go.GetComponent<RectTransform>();
            rt.pivot = new Vector2(0f, 0f);
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
            // Graphic subclasses need a CanvasRenderer; add it explicitly because
            // RequireComponent is not picked up from the abstract base here.
            go.AddComponent<CanvasRenderer>();
            var graphic = go.AddComponent<BigMapMarkerGraphic>();
            graphic.raycastTarget = false;
            return graphic;
        }
    }

    /// <summary>
    /// Pointer input for the big-map viewport: drag pan (left button only), wheel
    /// zoom, click select.  Every left pointer-down starts a fresh gesture so a
    /// previous drag distance can never block later clicks.
    /// </summary>
    public sealed class BigMapInputHandler : MonoBehaviour, IPointerDownHandler, IPointerUpHandler,
        IPointerClickHandler, IBeginDragHandler, IDragHandler, IEndDragHandler, IScrollHandler,
        IPointerExitHandler
    {
        public BigMapPanel Panel;
        Vector2 _last;
        float _dragDistance;
        bool _leftPressed;
        bool _dragged;

        public float DragDistanceForTest { get { return _dragDistance; } }
        public bool LeftPressedForTest { get { return _leftPressed; } }

        Vector2 ToLocal(PointerEventData eventData)
        {
            var rect = (RectTransform)transform;
            Vector2 local;
            Camera camera = eventData.pressEventCamera;
            if (RectTransformUtility.ScreenPointToLocalPointInRectangle(rect, eventData.position, camera, out local))
                return local;
            return Vector2.zero;
        }

        // ---- shared gesture state machine (used by real events and by tests) ----
        public void BeginGestureForTest(Vector2 local, bool leftButton)
        {
            _leftPressed = leftButton;
            _dragDistance = 0f;
            _dragged = false;
            _last = local;
        }

        public void DragGestureForTest(Vector2 local)
        {
            if (!_leftPressed || Panel == null) return;
            Vector2 delta = local - _last;
            _last = local;
            _dragDistance += delta.magnitude;
            if (_dragDistance > 2f) _dragged = true;
            Panel.PanBy(delta);
        }

        public void EndGestureForTest() { }

        public bool TryClickForTest(Vector2 local)
        {
            if (!_leftPressed) return false;
            _leftPressed = false;
            if (_dragged && _dragDistance > 6f) return false;
            Panel.HandleMapClick(local);
            return true;
        }

        // ---- UnityEngine.EventSystems entry points ----
        public void OnPointerDown(PointerEventData eventData)
        {
            if (eventData.button != PointerEventData.InputButton.Left) return;
            BeginGestureForTest(ToLocal(eventData), true);
        }

        public void OnPointerUp(PointerEventData eventData)
        {
            if (eventData.button != PointerEventData.InputButton.Left) return;
            _leftPressed = false;
        }

        public void OnPointerExit(PointerEventData eventData)
        {
            _leftPressed = false;
        }

        public void OnBeginDrag(PointerEventData eventData)
        {
            if (eventData.button != PointerEventData.InputButton.Left)
            {
                _leftPressed = false;
                return;
            }
            // A new drag may start without a preceding click on this object; make
            // sure the gesture baseline is this pointer, not a stale one.
            BeginGestureForTest(ToLocal(eventData), true);
        }

        public void OnDrag(PointerEventData eventData)
        {
            if (eventData.button != PointerEventData.InputButton.Left)
            {
                _leftPressed = false;
                return;
            }
            if (!_leftPressed) BeginGestureForTest(ToLocal(eventData), true);
            DragGestureForTest(ToLocal(eventData));
        }

        public void OnEndDrag(PointerEventData eventData) { }

        public void OnPointerClick(PointerEventData eventData)
        {
            if (eventData.button != PointerEventData.InputButton.Left) return;
            Vector2 local = ToLocal(eventData);
            // click is allowed only for the gesture that just ended and only when
            // that gesture did not move the map
            if (_dragged && _dragDistance > 6f) { _leftPressed = false; return; }
            _leftPressed = false;
            if (Panel != null) Panel.HandleMapClick(local);
        }

        public void OnScroll(PointerEventData eventData)
        {
            if (Panel == null) return;
            Panel.ZoomAt(ToLocal(eventData), eventData.scrollDelta.y);
        }
    }
}
