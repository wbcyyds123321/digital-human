using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Yixuan.Pick;

namespace Yixuan.Pick
{
    /// <summary>
    /// Auto-bootstrapped runtime owner of the first-person selection feature.
    /// It does not require scene edits: one hidden GameObject is created after
    /// scene load. It warms up the entity pick registry, opens the manual
    /// crosshair confirm (default T), draws the single-entity highlight and a
    /// small selection label. The scene/map/character are never modified.
    /// </summary>
    [DefaultExecutionOrder(900)]
    public sealed class FirstPersonSelectionController : MonoBehaviour
    {
        public static FirstPersonSelectionController Instance { get; private set; }

        public KeyCode ConfirmKey = KeyCode.T;
        public bool HotkeysEnabled = true;
        public bool HighlightEnabled = true;
        public Color HighlightColor = new Color(0.18f, 0.88f, 1.0f, 1.0f);

        public static string LastAction { get; private set; } = "";

        Material _highlightMaterial;
        Shader _highlightShader;
        Font _font;
        GUIStyle _labelStyle;
        bool _warmedUp;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Bootstrap()
        {
            if (Instance != null) return;
            if (Object.FindObjectOfType<FirstPersonSelectionController>() != null) return;
            var go = new GameObject("FirstPersonSelection");
            go.AddComponent<FirstPersonSelectionController>();
        }

        public static FirstPersonSelectionController EnsureInstance()
        {
            if (Instance != null) return Instance;
            var existing = Object.FindObjectOfType<FirstPersonSelectionController>();
            if (existing != null) return existing;
            var go = new GameObject("FirstPersonSelection");
            return go.AddComponent<FirstPersonSelectionController>();
        }

        void Awake()
        {
            Instance = this;
            HighlightColor.a = 1f;
        }

        void Start()
        {
            StartCoroutine(WarmUp());
        }

        IEnumerator WarmUp()
        {
            // One real frame boundary before building heavy geometry: proves the
            // runtime is not depending on editor-only synchronous setup.
            yield return null;
            yield return null;
            bool ready = EntityPickRegistry.EnsureBuilt();
            _warmedUp = true;
            LastAction = ready ? "registry_ready" : "registry_failed:" + EntityPickRegistry.Error;
            Debug.Log("[FirstPersonSelection] warmup ready=" + ready + " ms=" + EntityPickRegistry.BuildMs +
                      " error=" + EntityPickRegistry.Error);
        }

        void Update()
        {
            if (!HotkeysEnabled) return;
            if (Input.GetKeyDown(ConfirmKey)) TryConfirmFromKeyboard();
        }

        /// <summary>
        /// Keyboard interaction guard (R6): only when the actual first-person
        /// view is displayed and no text input / UI search field owns the
        /// keyboard. Explicit program API (ConfirmAtCenterManual) stays available
        /// for tests/AI and is not gated by this.
        /// </summary>
        public static bool KeyboardConfirmAllowed()
        {
            // Any IMGUI text field (map search, etc.) holds keyboardControl while editing.
            if (GUIUtility.keyboardControl != 0) return false;
            var eventSystem = UnityEngine.EventSystems.EventSystem.current;
            if (eventSystem != null && eventSystem.currentSelectedGameObject != null)
            {
                var field = eventSystem.currentSelectedGameObject.GetComponent<UnityEngine.UI.InputField>();
                if (field != null && field.isFocused) return false;
            }
            var chat = UnityEngine.Object.FindObjectOfType<Yixuan.AI.ChatUI>();
            if (chat != null && chat.InputFocused) return false;
            var bigMap = Yixuan.UI.BigMapPanel.Instance;
            if (bigMap != null && Yixuan.UI.BigMapPanel.SearchFocused) return false;
            var director = UnityEngine.Object.FindObjectOfType<Yixuan.Presentation.CameraDirector>();
            if (director == null || !director.EyeView) return false;   // third person must not select via T
            var eye = PickService.ResolveEye();
            if (eye == null || !eye.enabled) return false;
            return true;
        }

        /// <summary>The exact path the T key uses (guard + confirm).</summary>
        public bool TryConfirmFromKeyboard()
        {
            if (!HotkeysEnabled) return false;
            if (!KeyboardConfirmAllowed()) return false;
            return ConfirmAtCenterNow("manual_click") != null;
        }

        void LateUpdate()
        {
            if (!HighlightEnabled || SelectionBroker.Current == null) return;
            DrawHighlight(SelectionBroker.Current);
        }

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
            if (_highlightMaterial != null) Destroy(_highlightMaterial);
            EntityIdCapture.Dispose();
        }

        // ------------------------------------------------------------ manual
        public static PickHit ConfirmAtCenterManual()
        {
            var controller = EnsureInstance();
            return controller.ConfirmAtCenterNow("manual_click");
        }

        public PickHit ConfirmAtCenterNow(string source)
        {
            if (!_warmedUp && !EntityPickRegistry.Ready) EntityPickRegistry.EnsureBuilt();
            var hit = PickService.ConfirmAtCenter(source);
            if (hit != null && hit.Valid)
                LastAction = "confirmed:" + hit.EntityId + " frame=" + hit.snapshot.frame_id +
                            " dist=" + hit.distance_m.ToString("F2");
            else
                LastAction = "confirm_failed:" + PickService.LastError;
            return hit;
        }

        // --------------------------------------------------------- highlight
        void DrawHighlight(SelectionRecord record)
        {
            if (record == null || !record.confirmed) return;
            var entry = EntityPickRegistry.Get(record.entity_id);
            if (entry == null || !entry.Selectable) return;
            EnsureHighlightMaterial();
            if (_highlightMaterial == null) return;
            if (entry.Renderers.Count > 0)
            {
                foreach (var renderer in entry.Renderers)
                {
                    if (renderer == null || !renderer.enabled || !renderer.gameObject.activeInHierarchy) continue;
                    Mesh mesh; int submeshStart, submeshCount;
                    EntityPickRegistry.GetRendererMeshRange(renderer, out mesh, out submeshStart, out submeshCount);
                    if (mesh == null) continue;
                    var matrix = EntityPickRegistry.GetRendererDrawMatrix(renderer);
                    for (int submesh = submeshStart; submesh < submeshStart + submeshCount && submesh < mesh.subMeshCount; submesh++)
                        Graphics.DrawMesh(mesh, matrix, _highlightMaterial, renderer.gameObject.layer, null, submesh);
                }
                return;
            }
            var entityMesh = EntityPickRegistry.GetEntityMesh(entry);
            if (entityMesh == null) return;
            Graphics.DrawMesh(entityMesh, Matrix4x4.identity, _highlightMaterial, 0, null, 0);
        }

        void EnsureHighlightMaterial()
        {
            if (_highlightMaterial != null) return;
            if (_highlightShader == null)
            {
                _highlightShader = Resources.Load<Shader>("FirstPersonSelection/PickHighlight");
                if (_highlightShader == null) _highlightShader = Shader.Find("Hidden/Yixuan/FirstPersonSelection/PickHighlight");
            }
            if (_highlightShader == null)
            {
                Debug.LogWarning("[FirstPersonSelection] highlight shader missing");
                return;
            }
            _highlightMaterial = new Material(_highlightShader) { name = "FPS_Highlight", hideFlags = HideFlags.HideAndDontSave };
            _highlightMaterial.SetColor("_Color", HighlightColor);
            _highlightMaterial.SetFloat("_OutlineWidth", 0.05f);
            _highlightMaterial.SetFloat("_TintAlpha", 0.22f);
        }

        // --------------------------------------------------------------- GUI
        void OnGUI()
        {
            var record = SelectionBroker.Current;
            if (record == null || !record.confirmed) return;
            if (_font == null)
            {
                try
                {
                    _font = Font.CreateDynamicFontFromOSFont(
                        new[] { "Microsoft YaHei", "Microsoft YaHei UI", "SimHei", "Arial" }, 15);
                }
                catch { _font = null; }
                if (_font == null)
                {
                    try { _font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"); } catch { _font = null; }
                }
            }
            if (_labelStyle == null)
            {
                _labelStyle = new GUIStyle(GUI.skin.label)
                {
                    fontSize = 15,
                    alignment = TextAnchor.MiddleCenter,
                    richText = false,
                };
                if (_font != null) _labelStyle.font = _font;
            }
            string capabilities = string.IsNullOrEmpty(record.capabilities) ? "（无可执行能力）" : record.capabilities;
            // HUD shows current distance: re-sample live facts instead of the
            // observation-time distance; the immutable record itself is untouched.
            var live = PickService.SampleLive();
            float hudDistance = live != null && live.available ? live.distance_m : record.distance_m;
            string text = "已选中：" + record.display_name + "  [" + record.entity_id + "]  " +
                          hudDistance.ToString("F1") + "m  ·  " + record.source +
                          "  ·  " + capabilities;
            float width = Mathf.Min(Screen.width - 40f, 760f);
            var rect = new Rect((Screen.width - width) * 0.5f, 18f, width, 26f);
            var previous = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, 0.62f);
            GUI.DrawTexture(rect, Texture2D.whiteTexture);
            GUI.color = new Color(0.35f, 0.95f, 1f, 1f);
            GUI.Label(rect, text, _labelStyle);
            GUI.color = previous;
        }

        // ----------------------------------------------------------- test API
        public static PickFrame CaptureFrameForTest(int width = 0, int height = 0)
        {
            return PickService.CaptureEyeFrame(null, width, height);
        }

        public static PickBoxResult ResolveBoxForTest(PickFrame frame, Rect uvBoxTopLeft)
        {
            return PickService.ResolveBox(frame, uvBoxTopLeft);
        }

        public static SelectionRecord ConfirmEntityForTest(string entityId, string source = "test_entity_click")
        {
            return PickService.ConfirmEntity(entityId, source);
        }

        public static bool EnsureRegistryForTest()
        {
            return EntityPickRegistry.EnsureBuilt();
        }

        public static string CoverageJsonForTest()
        {
            EntityPickRegistry.EnsureBuilt();
            return EntityPickRegistry.Report != null ? SelectionJson.Serialize(EntityPickRegistry.Report) : "{}";
        }
    }
}
