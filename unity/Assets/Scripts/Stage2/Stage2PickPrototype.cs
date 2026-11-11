using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;
using UnityEngine.UIElements;
using Yixuan.World;

namespace Yixuan.Stage2
{
    /// <summary>
    /// Stage-2 crosshair pick prototype.
    ///
    /// Isolation rules:
    ///  - no new Physics colliders, no entity GameObjects, no scene edits;
    ///  - does not touch EyeCamera / CameraDirector / existing HUD scripts;
    ///  - creates its own camera + RT + OnGUI overlay at runtime, default OFF (F12).
    /// </summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(-32000)]
    public sealed class Stage2PickPrototype : MonoBehaviour
    {
        public const int DefaultWidth = 960;
        public const int DefaultHeight = 540;
        public const float TestViewFov = 60f;

        public static Stage2PickPrototype Instance { get; private set; }

        [Header("Runtime evidence (set by the acceptance runner)")]
        public string EvidenceDir = "";
        public bool Enabled { get; private set; }
        public string DataError { get; private set; } = "";
        public bool DataReady { get { return _data != null && _data.Valid; } }
        public Stage2PickResult LastPick { get; private set; }
        public Stage2CameraSnapshot LastSnapshot { get; private set; }
        public Stage2ConfirmRecord LastConfirm { get; private set; }
        public bool HasRuntimeResources { get { return _cloneCamera != null || _rt != null; } }
        public bool UiBlockerActive { get { return _uiBlocker != null && _uiBlocker.style.display.value == DisplayStyle.Flex; } }

        VisualElement _uiBlocker;
        VisualElement _uiBlockerRoot;
        bool _pointerInsidePreview;
        int _previewControlId;
        int _capturedPointerId = -1;

        public string CurrentSource { get { return _followEye ? "character_eye" : "test_view"; } }
        public string CurrentPoseId { get { return _followEye ? "" : _poseId; } }

        Stage2SceneData _data;
        Camera _cloneCamera;
        RenderTexture _rt;
        Texture2D _white;
        Font _font;
        GUIStyle _labelStyle;
        GUIStyle _titleStyle;
        GUIStyle _smallStyle;
        Camera _eyeCamera;
        Camera _debugEyeOverride;
        bool _debugEyeOverrideSet;
        Transform _character;

        int _rtWidth = DefaultWidth;
        int _rtHeight = DefaultHeight;

        bool _followEye = true;
        string _poseId = "";
        Vector3 _manualEye;
        Vector3 _manualLook;

        int _nextFrameId;
        long _lastRenderedFrame = -1;
        bool _renderDirty = true;
        float _nextRenderTime;
        Rect _previewRect;
        bool _dragging;
        Vector2 _dragLast;

        string _infoText = "";
        string _statusLine = "";
        Color _crosshairColor = Color.white;
        bool _missingEyeReported;

        // ------------------------------------------------------------------
        // bootstrap (runtime only; scene is untouched)
        // ------------------------------------------------------------------
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void AutoBootstrap()
        {
            try
            {
                var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
                bool mapScene = scene.name != null && scene.name.IndexOf("SovietBlueHour", StringComparison.OrdinalIgnoreCase) >= 0;
                if (!mapScene && UnityEngine.Object.FindObjectOfType<MapWorldProfile>() == null) return;
                if (Instance != null) return;
                var go = new GameObject("Stage2PickPrototype");
                go.AddComponent<Stage2PickPrototype>();
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Stage2Pick] bootstrap failed: " + e.Message);
            }
        }

        void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;
            _white = Texture2D.whiteTexture;
        }

        void OnDestroy()
        {
            Disable();
            if (Instance == this) Instance = null;
        }

        void OnApplicationFocus(bool hasFocus)
        {
            if (hasFocus) return;
            _dragging = false;
            if (_previewControlId != 0 && GUIUtility.hotControl == _previewControlId)
                GUIUtility.hotControl = 0;
            if (_uiBlocker != null && _capturedPointerId >= 0) _uiBlocker.ReleasePointer(_capturedPointerId);
            _capturedPointerId = -1;
        }

        // ------------------------------------------------------------------
        // public API
        // ------------------------------------------------------------------
        public void Toggle()
        {
            if (Enabled) Disable(); else Enable();
        }

        public void Enable()
        {
            if (Enabled) return;
            if (_data == null)
            {
                _data = Stage2PickDataLoader.Load();
                DataError = _data.Valid ? "" : _data.Error;
            }
            EnsureSessionDir();
            EnsureRuntime();
            EnsureUiBlocker();
            Enabled = true;
            _renderDirty = true;
            _nextRenderTime = 0f;
            ClearHover("enabled");
            ResolveEye();
            if (_data == null || !_data.Valid)
                _infoText = "数据加载失败: " + DataError;
        }

        public void Disable()
        {
            Enabled = false;
            _dragging = false;
            if (_previewControlId != 0 && GUIUtility.hotControl == _previewControlId)
                GUIUtility.hotControl = 0;
            ReleaseUiBlocker();
            DestroyRuntime();
            LastPick = null;
            LastSnapshot = null;
            _renderDirty = false;
        }

        public Stage2PickResult RenderNow()
        {
            if (!Enabled) return null;
            if (_data == null) _data = Stage2PickDataLoader.Load();
            if (_data == null || !_data.Valid)
            {
                DataError = _data == null ? "no data" : _data.Error;
                LastSnapshot = null;
                LastPick = new Stage2PickResult
                {
                    Status = Stage2PickStatus.DataError,
                    StatusText = "data_error",
                    Diagnostic = DataError,
                };
                _statusLine = "数据错误";
                _infoText = DataError;
                _crosshairColor = new Color(1f, 0.35f, 0.25f);
                return LastPick;
            }
            ResolveEye();
            if (_followEye && _eyeCamera == null)
            {
                LastSnapshot = null;
                LastPick = Stage2PickResult.Unavailable("missing EyeCamera: cannot render character_eye");
                _statusLine = "不可用：缺少 EyeCamera";
                _infoText = LastPick.Diagnostic;
                _crosshairColor = new Color(1f, 0.5f, 0.25f);
                _renderDirty = false;
                if (!_missingEyeReported)
                {
                    _missingEyeReported = true;
                    Debug.LogWarning("[Stage2Pick] missing EyeCamera; character_eye is unavailable");
                }
                return LastPick;
            }
            _missingEyeReported = false;

            EnsureRuntime();
            if (_uiBlocker == null) EnsureUiBlocker();
            if (_rt == null || _cloneCamera == null)
            {
                LastSnapshot = null;
                LastPick = Stage2PickResult.Unavailable("prototype runtime resources missing");
                return LastPick;
            }

            int frameId = _nextFrameId++;
            var snap = BuildSnapshot(frameId);
            if (snap == null)
            {
                LastSnapshot = null;
                LastPick = Stage2PickResult.Unavailable("cannot build camera snapshot");
                return LastPick;
            }

            ConfigureCloneCamera(snap);
            var prevActive = RenderTexture.active;
            var prevTarget = _cloneCamera.targetTexture;
            _cloneCamera.targetTexture = _rt;
            _cloneCamera.Render();
            _cloneCamera.targetTexture = prevTarget;
            RenderTexture.active = prevActive;

            Vector3 characterPosition = _character != null ? _character.position : snap.Position;
            LastSnapshot = snap;
            LastPick = Stage2PickMath.PickCenter(_data, snap, characterPosition, 0f);
            _lastRenderedFrame = frameId;
            _renderDirty = false;
            _nextRenderTime = Time.unscaledTime + 0.033f;
            UpdateInfo(snap);
            return LastPick;
        }

        public bool TryConfirm()
        {
            if (!Enabled || LastPick == null || LastSnapshot == null) return false;
            if (LastPick.Status != Stage2PickStatus.Hit) return false;
            if (LastSnapshot.FrameId != LastPick.FrameId || _lastRenderedFrame != LastSnapshot.FrameId) return false;
            if (IsConfirmBlocked()) return false;
            EnsureSessionDir();
            string path, sha;
            int w, h;
            if (!CaptureCurrentFrame("confirm_" + Sanitize(LastPick.SampleId), out path, out sha, out w, out h))
                return false;
            var rec = new Stage2ConfirmRecord
            {
                frame_id = LastSnapshot.FrameId,
                snapshot_id = LastSnapshot.SnapshotId,
                camera_source = LastSnapshot.Source,
                pose_id = LastSnapshot.PoseId,
                sample_id = LastPick.SampleId,
                name_zh = LastPick.NameZh,
                distance_from_camera = LastPick.DistanceFromCamera,
                occluder_id = LastPick.OccluderId,
                image = path,
                image_sha256 = sha,
                image_width = w,
                image_height = h,
                snapshot = LastSnapshot.ToRecord(),
                pick = LastPick.ToRecord(),
            };
            if (!AppendConfirmRecord(rec)) return false;
            LastConfirm = rec;
            _statusLine = "已确认: " + LastPick.SampleId + "  frame=" + LastSnapshot.FrameId;
            return true;
        }

        public bool IsConfirmBlocked()
        {
            return TextInputFocused();
        }

        public void SetFollowEye()
        {
            _followEye = true;
            _poseId = "";
            ClearHover("follow_eye");
        }

        public void SetTestPose(string poseId)
        {
            if (_data == null || !_data.Valid || _data.Manifest == null) return;
            Stage2Pose pose = FindPose(poseId);
            if (pose == null || pose.eye_position_unity == null || pose.look_at_unity == null ||
                pose.eye_position_unity.Length != 3 || pose.look_at_unity.Length != 3)
                return;
            _followEye = false;
            _poseId = poseId;
            _manualEye = ToVector(pose.eye_position_unity);
            _manualLook = ToVector(pose.look_at_unity);
            ClearHover("pose:" + poseId);
        }

        public void SetManualTestView(Vector3 eye, Vector3 lookAt, string label)
        {
            if (!Stage2PickMath.IsFinite(eye) || !Stage2PickMath.IsFinite(lookAt)) return;
            if ((lookAt - eye).sqrMagnitude < 1e-6f) return;
            _followEye = false;
            _poseId = string.IsNullOrEmpty(label) ? "manual" : label;
            _manualEye = eye;
            _manualLook = lookAt;
            ClearHover("manual_test_view");
        }

        public void ApplyManualLook(float yawDeltaDeg, float pitchDeltaDeg)
        {
            if (_followEye)
            {
                if (_eyeCamera == null) return;
                _manualEye = _eyeCamera.transform.position;
                _manualLook = _manualEye + _eyeCamera.transform.forward * 10f;
                _followEye = false;
                _poseId = "manual";
            }
            else if (!string.IsNullOrEmpty(_poseId) && _poseId != "manual" && !_poseId.EndsWith("+manual"))
            {
                _poseId = _poseId + "+manual";
            }
            Vector3 dir = (_manualLook - _manualEye).normalized;
            float yaw = Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg;
            float pitch = Mathf.Asin(Mathf.Clamp(dir.y, -0.999f, 0.999f)) * Mathf.Rad2Deg;
            yaw += yawDeltaDeg;
            pitch -= pitchDeltaDeg;      // GUI y grows downward
            pitch = Mathf.Clamp(pitch, -85f, 85f);
            Quaternion q = Quaternion.Euler(-pitch, yaw, 0f);
            Vector3 newDir = q * Vector3.forward;
            _manualLook = _manualEye + newDir * 10f;
            ClearHover("manual_look");
        }

        public void SetPreviewSize(int width, int height)
        {
            width = Mathf.Clamp(width, 320, 1920);
            height = Mathf.Clamp(height, 180, 1080);
            if (width == _rtWidth && height == _rtHeight) return;
            _rtWidth = width;
            _rtHeight = height;
            ReleaseRenderTexture();
            if (Enabled) EnsureRuntime();
            ClearHover("preview_size");
        }

        public void DebugSetEyeCamera(Camera camera)
        {
            _debugEyeOverrideSet = true;
            _debugEyeOverride = camera;
            _eyeCamera = camera;
            ClearHover("debug_eye");
        }

        public void DebugClearEyeCameraOverride()
        {
            _debugEyeOverrideSet = false;
            _debugEyeOverride = null;
            _eyeCamera = null;
            ResolveEye();
            ClearHover("debug_eye_restore");
        }

        public Rect GetPreviewRect()
        {
            float scale = GuiScale;
            return ComputePreviewRect(
                Mathf.RoundToInt(Screen.width / scale),
                Mathf.RoundToInt(Screen.height / scale),
                _rtWidth, _rtHeight);
        }

        static float GuiScale
        {
            get
            {
#if UNITY_EDITOR
                float pp = UnityEditor.EditorGUIUtility.pixelsPerPoint;
                return pp > 0.01f ? pp : 1f;
#else
                return 1f;
#endif
            }
        }

        public Vector2 GetCrosshairGuiPosition()
        {
            Rect r = GetPreviewRect();
            return new Vector2(r.x + r.width * 0.5f, r.y + r.height * 0.5f);
        }

        public bool IsPointerInsidePreview(Vector2 guiPoint)
        {
            return GetPreviewRect().Contains(guiPoint);
        }

        public Stage2PickResult PickRay(Vector3 origin, Vector3 direction, float maxDistance, Stage2CameraSnapshot snapshot = null)
        {
            if (_data == null || !_data.Valid) return Stage2PickResult.Unavailable("data not ready");
            Vector3 characterPosition = _character != null ? _character.position :
                (snapshot != null ? snapshot.Position : origin);
            return Stage2PickMath.Pick(_data, origin, direction, maxDistance, 0f,
                characterPosition, snapshot, null, null);
        }

        public bool CaptureEvidence(string directory, string tag, out string path, out string sha256, out int width, out int height)
        {
            return CaptureCurrentFrame(tag, out path, out sha256, out width, out height, directory);
        }

        // ------------------------------------------------------------------
        // runtime resources
        // ------------------------------------------------------------------
        void EnsureRuntime()
        {
            if (_cloneCamera == null)
            {
                var go = new GameObject("Stage2PickCloneCamera");
                go.transform.SetParent(transform, false);
                _cloneCamera = go.AddComponent<Camera>();
                _cloneCamera.enabled = false;
                _cloneCamera.clearFlags = CameraClearFlags.SolidColor;
                _cloneCamera.backgroundColor = new Color(0.03f, 0.045f, 0.07f);
                _cloneCamera.nearClipPlane = 0.05f;
                _cloneCamera.farClipPlane = 900f;
                _cloneCamera.cullingMask = ~(1 << 8);
                _cloneCamera.allowHDR = false;
                _cloneCamera.allowMSAA = false;
            }
            if (_rt == null)
            {
                _rt = new RenderTexture(_rtWidth, _rtHeight, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
                _rt.name = "Stage2PickRT";
                _rt.Create();
            }
            if (_font == null)
            {
                try
                {
                    _font = Font.CreateDynamicFontFromOSFont(
                        new[] { "Microsoft YaHei", "Microsoft YaHei UI", "SimHei", "Arial" }, 16);
                }
                catch { _font = null; }
                if (_font == null)
                {
                    try { _font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"); } catch { _font = null; }
                }
            }
            if (_white == null) _white = Texture2D.whiteTexture;
            if (_character == null)
            {
                var actor = GameObject.Find("Yixuan_Test");
                if (actor != null) _character = actor.transform;
            }
        }

        void EnsureSessionDir()
        {
            if (string.IsNullOrEmpty(EvidenceDir))
            {
                string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + "_" + Guid.NewGuid().ToString("N").Substring(0, 8);
                string root = Application.isEditor
                    ? Path.Combine(Path.GetDirectoryName(Application.dataPath), "Logs", "Stage2Pick")
                    : Path.Combine(Application.persistentDataPath, "Stage2Pick");
                EvidenceDir = Path.Combine(root, "session_" + stamp);
            }
            if (!Directory.Exists(EvidenceDir)) Directory.CreateDirectory(EvidenceDir);
        }

        void EnsureUiBlocker()
        {
            if (_uiBlocker != null) return;
            var chat = GameObject.Find("UI_Chat");
            if (chat == null) return;
            var doc = chat.GetComponent<UIDocument>();
            if (doc == null) return;
            var root = doc.rootVisualElement;
            if (root == null) return;
            _uiBlockerRoot = root;
            _uiBlocker = new VisualElement { name = "Stage2PreviewPointerBlocker", pickingMode = PickingMode.Position };
            _uiBlocker.style.position = Position.Absolute;
            _uiBlocker.style.backgroundColor = Color.clear;
            _uiBlocker.RegisterCallback<PointerDownEvent>(pointerEvent =>
            {
                if (pointerEvent.button != 0) return;
                HandlePreviewPointer(EventType.MouseDown, 0, GuiMousePosition());
                _capturedPointerId = pointerEvent.pointerId;
                _uiBlocker.CapturePointer(pointerEvent.pointerId);
                pointerEvent.StopImmediatePropagation();
                pointerEvent.PreventDefault();
            });
            _uiBlocker.RegisterCallback<PointerMoveEvent>(pointerEvent =>
            {
                if (!_dragging || !_uiBlocker.HasPointerCapture(pointerEvent.pointerId)) return;
                HandlePreviewPointer(EventType.MouseDrag, 0, GuiMousePosition());
                pointerEvent.StopImmediatePropagation();
            });
            _uiBlocker.RegisterCallback<PointerUpEvent>(pointerEvent =>
            {
                if (pointerEvent.button != 0) return;
                HandlePreviewPointer(EventType.MouseUp, 0, GuiMousePosition());
                _uiBlocker.ReleasePointer(pointerEvent.pointerId);
                _capturedPointerId = -1;
                pointerEvent.StopImmediatePropagation();
                pointerEvent.PreventDefault();
            });
            _uiBlocker.RegisterCallback<ClickEvent>(pointerEvent =>
            {
                if (pointerEvent.button != 0) return;
                pointerEvent.StopImmediatePropagation();
                pointerEvent.PreventDefault();
            });
            _uiBlocker.RegisterCallback<PointerCaptureOutEvent>(pointerEvent => _dragging = false);
            root.Add(_uiBlocker);
            _uiBlocker.BringToFront();
            UpdateUiBlockerBounds();
        }

        void UpdateUiBlockerBounds()
        {
            if (_uiBlocker == null || _uiBlockerRoot == null || _uiBlockerRoot.panel == null) return;
            Rect preview = GetPreviewRect();
            float scale = GuiScale;
            Vector2 topLeft = _uiBlockerRoot.WorldToLocal(RuntimePanelUtils.ScreenToPanel(_uiBlockerRoot.panel, preview.min * scale));
            Vector2 bottomRight = _uiBlockerRoot.WorldToLocal(RuntimePanelUtils.ScreenToPanel(_uiBlockerRoot.panel, preview.max * scale));
            _uiBlocker.style.left = topLeft.x;
            _uiBlocker.style.top = topLeft.y;
            _uiBlocker.style.width = Mathf.Max(0f, bottomRight.x - topLeft.x);
            _uiBlocker.style.height = Mathf.Max(0f, bottomRight.y - topLeft.y);
            _uiBlocker.style.display = Enabled ? DisplayStyle.Flex : DisplayStyle.None;
        }

        void ReleaseUiBlocker()
        {
            if (_uiBlocker != null)
            {
                if (_capturedPointerId >= 0) _uiBlocker.ReleasePointer(_capturedPointerId);
                _uiBlocker.RemoveFromHierarchy();
                _uiBlocker = null;
            }
            _uiBlockerRoot = null;
            _capturedPointerId = -1;
            _pointerInsidePreview = false;
        }

        void SetPreviewPointerState(bool inside)
        {
            _pointerInsidePreview = inside;
            UpdateUiBlockerBounds();
        }

        public bool PointerInsidePreview { get { return _pointerInsidePreview; } }

        public void DebugSetPreviewPointerState(bool inside)
        {
            SetPreviewPointerState(inside);
        }

        void EnsureStyles()
        {
            if (_labelStyle != null) return;
            _labelStyle = new GUIStyle(GUI.skin != null ? GUI.skin.label : new GUIStyle());
            if (_font != null) _labelStyle.font = _font;
            _labelStyle.fontSize = 16;
            _labelStyle.normal.textColor = new Color(0.9f, 0.94f, 1f);
            _labelStyle.wordWrap = true;
            _titleStyle = new GUIStyle(_labelStyle);
            _titleStyle.fontSize = 18;
            _titleStyle.normal.textColor = new Color(1f, 0.85f, 0.35f);
            _smallStyle = new GUIStyle(_labelStyle);
            _smallStyle.fontSize = 13;
            _smallStyle.normal.textColor = new Color(0.7f, 0.78f, 0.9f);
        }

        void DestroyRuntime()
        {
            if (_cloneCamera != null)
            {
                if (_cloneCamera.gameObject != null) Destroy(_cloneCamera.gameObject);
                _cloneCamera = null;
            }
            ReleaseRenderTexture();
        }

        void ReleaseRenderTexture()
        {
            if (_rt != null)
            {
                if (RenderTexture.active == _rt) RenderTexture.active = null;
                _rt.Release();
                Destroy(_rt);
                _rt = null;
            }
        }

        void ResolveEye()
        {
            if (_debugEyeOverrideSet)
            {
                _eyeCamera = _debugEyeOverride;
                return;
            }
            if (_eyeCamera != null) return;
            var go = GameObject.Find("EyeCamera");
            if (go != null) _eyeCamera = go.GetComponent<Camera>();
            if (_eyeCamera == null)
            {
                var observer = UnityEngine.Object.FindObjectOfType<Yixuan.Vision.VisionObserver>();
                if (observer != null) _eyeCamera = observer.EyeCamera;
            }
        }

        Stage2CameraSnapshot BuildSnapshot(int frameId)
        {
            var snap = new Stage2CameraSnapshot
            {
                FrameId = frameId,
                RtWidth = _rtWidth,
                RtHeight = _rtHeight,
                Aspect = (float)_rtWidth / Mathf.Max(1, _rtHeight),
                RealtimeSinceStartup = Time.realtimeSinceStartupAsDouble,
                Valid = true,
            };
            if (_followEye)
            {
                if (_eyeCamera == null) return null;
                snap.Source = "character_eye";
                snap.PoseId = "";
                snap.Position = _eyeCamera.transform.position;
                snap.Rotation = _eyeCamera.transform.rotation;
                snap.Fov = _eyeCamera.fieldOfView;
                snap.Near = _eyeCamera.nearClipPlane;
                snap.Far = _eyeCamera.farClipPlane;
            }
            else
            {
                if ((_manualLook - _manualEye).sqrMagnitude < 1e-6f) return null;
                snap.Source = "test_view";
                snap.PoseId = string.IsNullOrEmpty(_poseId) ? "manual" : _poseId;
                snap.Position = _manualEye;
                snap.Rotation = Quaternion.LookRotation((_manualLook - _manualEye).normalized, Vector3.up);
                snap.Fov = TestViewFov;
                snap.Near = 0.05f;
                snap.Far = 900f;
            }
            return snap;
        }

        void ConfigureCloneCamera(Stage2CameraSnapshot snap)
        {
            _cloneCamera.transform.position = snap.Position;
            _cloneCamera.transform.rotation = snap.Rotation;
            _cloneCamera.fieldOfView = snap.Fov;
            _cloneCamera.nearClipPlane = snap.Near;
            _cloneCamera.farClipPlane = snap.Far;
            _cloneCamera.aspect = snap.Aspect;
            if (_eyeCamera != null && snap.Source == "character_eye")
                _cloneCamera.cullingMask = _eyeCamera.cullingMask;
            else
                _cloneCamera.cullingMask = ~(1 << 8);
            snap.Projection = _cloneCamera.projectionMatrix;
        }

        // ------------------------------------------------------------------
        // update / input
        // ------------------------------------------------------------------
        void Update()
        {
            if (Input.GetKeyDown(KeyCode.F12)) Toggle();
            if (!Enabled) return;
            if (_character == null)
            {
                var actor = GameObject.Find("Yixuan_Test");
                if (actor != null) _character = actor.transform;
            }
            Vector2 guiMouse = GuiMousePosition();
            SetPreviewPointerState(IsPointerInsidePreview(guiMouse));
            HandleKeys();
            if (_renderDirty || Time.unscaledTime >= _nextRenderTime) RenderNow();
        }

        void HandleKeys()
        {
            bool typing = TextInputFocused();
            if (!typing)
            {
                if (Input.GetKeyDown(KeyCode.Alpha1)) SetTestPose("P1_bench_test_view");
                else if (Input.GetKeyDown(KeyCode.Alpha2)) SetTestPose("P2_bus_test_view");
                else if (Input.GetKeyDown(KeyCode.Alpha3)) SetTestPose("P3_arch_test_view");
                else if (Input.GetKeyDown(KeyCode.R)) SetFollowEye();
                else if (Input.GetKeyDown(KeyCode.F)) TryConfirm();
            }
            else if (Input.GetKeyDown(KeyCode.F))
            {
                _statusLine = "输入焦点在文本框：F 不确认";
            }
        }

        static Vector2 GuiMousePosition()
        {
            float s = GuiScale;
            return new Vector2(Input.mousePosition.x / s, (Screen.height - Input.mousePosition.y) / s);
        }

        bool HandlePreviewPointer(EventType eventType, int button, Vector2 guiMouse)
        {
            if (!Enabled || button != 0) return false;
            if (eventType == EventType.MouseDown && IsPointerInsidePreview(guiMouse))
            {
                _dragging = true;
                _dragLast = guiMouse;
                return true;
            }
            if (!_dragging) return false;
            if (eventType == EventType.MouseUp)
            {
                _dragging = false;
                return true;
            }
            if (eventType != EventType.MouseDrag) return false;
            Vector2 delta = guiMouse - _dragLast;
            _dragLast = guiMouse;
            if (delta.sqrMagnitude > 0.0001f)
                ApplyManualLook(delta.x * 0.12f, delta.y * 0.12f);
            return true;
        }

        static bool TextInputFocused()
        {
            if (GUIUtility.keyboardControl != 0) return true;
            var chat = GameObject.Find("UI_Chat");
            if (chat == null) return false;
            var doc = chat.GetComponent<UIDocument>();
            if (doc == null) return false;
            var root = doc.rootVisualElement;
            if (root == null || root.panel == null) return false;
            var controller = root.panel.focusController;
            if (controller == null) return false;
            var focused = controller.focusedElement;
            if (focused == null) return false;
            var visual = focused as VisualElement;
            if (visual == null) return false;
            if (visual is TextField) return true;
            if (visual.GetFirstAncestorOfType<TextField>() != null) return true;
            return false;
        }

        // ------------------------------------------------------------------
        // info / overlay
        // ------------------------------------------------------------------
        void ClearHover(string reason)
        {
            LastPick = null;
            LastSnapshot = null;
            _lastRenderedFrame = -1;
            _renderDirty = true;
            _statusLine = "切换中(" + reason + ")";
            _infoText = "";
        }

        void UpdateInfo(Stage2CameraSnapshot snap)
        {
            if (LastPick == null) return;
            switch (LastPick.Status)
            {
                case Stage2PickStatus.Hit:
                    _crosshairColor = new Color(0.45f, 1f, 0.55f);
                    _statusLine = "可选目标";
                    break;
                case Stage2PickStatus.Blocked:
                    _crosshairColor = new Color(1f, 0.72f, 0.25f);
                    _statusLine = "被遮挡";
                    break;
                case Stage2PickStatus.NoTarget:
                    _crosshairColor = new Color(0.85f, 0.9f, 1f);
                    _statusLine = "无目标";
                    break;
                default:
                    _crosshairColor = new Color(1f, 0.4f, 0.25f);
                    _statusLine = LastPick.StatusText;
                    break;
            }
            var sb = new StringBuilder(192);
            sb.Append("来源: ").Append(snap.Source);
            if (!string.IsNullOrEmpty(snap.PoseId)) sb.Append("  pose=").Append(snap.PoseId);
            sb.Append("  帧: ").Append(snap.FrameId).Append('\n');
            if (LastPick.Status == Stage2PickStatus.Hit)
            {
                sb.Append("物体: ").Append(LastPick.NameZh).Append('\n');
                sb.Append("ID: ").Append(LastPick.SampleId).Append('\n');
                if (!string.IsNullOrEmpty(LastPick.PartLabel))
                    sb.Append("命中部件: ").Append(LastPick.PartLabel).Append('\n');
                sb.Append("相机距离: ").Append(LastPick.DistanceFromCamera.ToString("F2")).Append(" m\n");
                sb.Append("角色距离: ").Append(LastPick.DistanceFromCharacter.ToString("F2")).Append(" m\n");
                sb.Append("命中点: ").Append(LastPick.HitPoint.ToString("F2"));
            }
            else if (LastPick.Status == Stage2PickStatus.Blocked)
            {
                sb.Append("遮挡体: ").Append(LastPick.OccluderId).Append(" (").Append(LastPick.OccluderMethod).Append(")\n");
                sb.Append("遮挡距离: ").Append(LastPick.OccluderDistance.ToString("F2")).Append(" m\n");
                sb.Append("背后候选: ").Append(LastPick.BlockedCandidateId);
            }
            else
            {
                sb.Append(LastPick.ShortLabel());
            }
            sb.Append('\n').Append("F12关闭  1/2/3测试位姿  R回正  左键拖动转测试视角  F确认");
            _infoText = sb.ToString();
        }

        void OnGUI()
        {
            if (!Enabled) return;
            int previousDepth = GUI.depth;
            GUI.depth = -100;
            EnsureStyles();
            Rect r = GetPreviewRect();
            _previewControlId = GUIUtility.GetControlID("Stage2PickPreview".GetHashCode(), FocusType.Passive, r);
            _previewRect = r;
            var guiEvent = Event.current;
            if (guiEvent != null && guiEvent.type != EventType.Repaint)
            {
                SetPreviewPointerState(IsPointerInsidePreview(guiEvent.mousePosition));
                ConsumePreviewPointerEvent(guiEvent, r);
            }
            GUI.color = new Color(0f, 0f, 0f, 0.85f);
            GUI.DrawTexture(new Rect(r.x - 2, r.y - 2, r.width + 4, r.height + 4), _white);
            GUI.color = Color.white;
            if (_rt != null)
                GUI.DrawTexture(r, _rt, ScaleMode.StretchToFill, false);
            else
                GUI.Label(r, "原型未初始化", _labelStyle);
            // crosshair at the exact center of the rendered rect
            float cx = r.x + r.width * 0.5f;
            float cy = r.y + r.height * 0.5f;
            GUI.color = _crosshairColor;
            GUI.DrawTexture(new Rect(cx - 10f, cy - 1f, 20f, 2f), _white);
            GUI.DrawTexture(new Rect(cx - 1f, cy - 10f, 2f, 20f), _white);
            GUI.color = Color.white;
            // Keep the information panel inside the preview, bottom-right, so it is
            // not covered by the existing left map HUD / bottom chat UI.
            float panelW = Mathf.Min(r.width - 16f, 760f);
            float panelH = 160f;
            float panelX = r.xMax - panelW - 8f;
            float panelY = r.yMax - panelH - 8f;
            GUI.color = new Color(0.02f, 0.035f, 0.06f, 0.88f);
            GUI.DrawTexture(new Rect(panelX, panelY, panelW, panelH), _white);
            GUI.color = Color.white;
            GUI.Label(new Rect(panelX + 8f, panelY + 4f, panelW - 16f, panelH - 8f),
                (_statusLine ?? "") + "\n" + (_infoText ?? ""), _smallStyle);
            if (!string.IsNullOrEmpty(DataError))
                GUI.Label(new Rect(r.x + 8f, r.y + 8f, r.width - 16f, 60f), "数据错误: " + DataError, _titleStyle);
            GUI.depth = previousDepth;
        }

        void ConsumePreviewPointerEvent(Event e, Rect preview)
        {
            if (e == null) return;
            switch (e.type)
            {
                case EventType.MouseDown:
                    if (HandlePreviewPointer(e.type, e.button, e.mousePosition))
                    {
                        GUIUtility.hotControl = _previewControlId;
                        e.Use();
                    }
                    break;
                case EventType.MouseDrag:
                    if (HandlePreviewPointer(e.type, e.button, e.mousePosition))
                    {
                        e.Use();
                    }
                    break;
                case EventType.MouseUp:
                    if (e.button == 0 && GUIUtility.hotControl == _previewControlId)
                    {
                        HandlePreviewPointer(e.type, e.button, e.mousePosition);
                        GUIUtility.hotControl = 0;
                        e.Use();
                    }
                    break;
            }
        }

        static Rect ComputePreviewRect(int screenW, int screenH, int rtW, int rtH)
        {
            float margin = 16f;
            float maxW = Mathf.Max(64f, screenW - margin * 2f);
            float maxH = Mathf.Max(64f, screenH - 190f);
            float aspect = rtW / (float)Mathf.Max(1, rtH);
            float w = maxW;
            float h = w / aspect;
            if (h > maxH) { h = maxH; w = h * aspect; }
            float x = (screenW - w) * 0.5f;
            float y = 44f;
            return new Rect(x, y, w, h);
        }

        // ------------------------------------------------------------------
        // capture / evidence
        // ------------------------------------------------------------------
        bool CaptureCurrentFrame(string tag, out string path, out string sha256, out int width, out int height,
            string directory = null)
        {
            path = "";
            sha256 = "";
            width = 0;
            height = 0;
            if (_rt == null) return false;
            var prev = RenderTexture.active;
            Texture2D tex = null;
            try
            {
                RenderTexture.active = _rt;
                tex = new Texture2D(_rtWidth, _rtHeight, TextureFormat.RGB24, false, false);
                tex.ReadPixels(new Rect(0, 0, _rtWidth, _rtHeight), 0, 0, false);
                tex.Apply(false, false);
                byte[] png = tex.EncodeToPNG();
                sha256 = Sha256(png);
                string dir = string.IsNullOrEmpty(directory) ? EvidenceDir : directory;
                if (string.IsNullOrEmpty(dir))
                    dir = Path.Combine(Application.persistentDataPath, "Stage2Pick");
                Directory.CreateDirectory(dir);
                string safe = Sanitize(string.IsNullOrEmpty(tag) ? "capture" : tag);
                path = Path.Combine(dir, safe + "_frame" + (_lastRenderedFrame >= 0 ? _lastRenderedFrame : 0) + ".png");
                File.WriteAllBytes(path, png);
                width = _rtWidth;
                height = _rtHeight;
                return true;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Stage2Pick] capture failed: " + e.Message);
                return false;
            }
            finally
            {
                RenderTexture.active = prev;
                if (tex != null) Destroy(tex);
            }
        }

        bool AppendConfirmRecord(Stage2ConfirmRecord record)
        {
            EnsureSessionDir();
            try
            {
                string json = JsonUtility.ToJson(record);
                File.AppendAllText(Path.Combine(EvidenceDir, "confirm.jsonl"), json + "\n", Encoding.UTF8);
                return true;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Stage2Pick] confirm log failed: " + e.Message);
                return false;
            }
        }

        static string Sanitize(string s)
        {
            if (string.IsNullOrEmpty(s)) return "item";
            var sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                sb.Append(char.IsLetterOrDigit(c) || c == '_' || c == '-' ? c : '_');
            }
            return sb.ToString();
        }

        static string Sha256(byte[] bytes)
        {
            using (var sha = SHA256.Create())
            {
                byte[] h = sha.ComputeHash(bytes);
                var sb = new StringBuilder(h.Length * 2);
                for (int i = 0; i < h.Length; i++) sb.Append(h[i].ToString("x2"));
                return sb.ToString();
            }
        }

        static Vector3 ToVector(float[] v)
        {
            return new Vector3(v[0], v[1], v[2]);
        }

        Stage2Pose FindPose(string id)
        {
            if (_data == null || _data.Manifest == null || _data.Manifest.poses == null) return null;
            for (int i = 0; i < _data.Manifest.poses.Length; i++)
                if (_data.Manifest.poses[i] != null && _data.Manifest.poses[i].id == id)
                    return _data.Manifest.poses[i];
            return null;
        }
    }
}
