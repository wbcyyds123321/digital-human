using System;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using Yixuan.AI.Tasks;
using Yixuan.Stage2;
using Yixuan.UI;

namespace Yixuan.EditorTools
{
    /// <summary>
    /// Stage-2-only desktop Play recorder. Not used in batchmode. Writes one JSONL
    /// sample per ~0.1s with character/task/HUD/prototype state so real mouse and
    /// keyboard interactions leave an auditable before/after record.
    /// </summary>
    [InitializeOnLoad]
    public static class Stage2DesktopRecorder
    {
        static string _path;
        static double _next;
        static bool _announced;

        static Stage2DesktopRecorder()
        {
            EditorApplication.update += Tick;
        }

        static void Tick()
        {
            if (Application.isBatchMode) return;
            if (!EditorApplication.isPlaying)
            {
                _path = null;
                _announced = false;
                return;
            }
            if (string.IsNullOrEmpty(_path))
            {
                string root = Path.Combine(Path.GetDirectoryName(Application.dataPath), "Logs", "stage2");
                Directory.CreateDirectory(root);
                _path = Path.Combine(root, "desktop_state_" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".jsonl");
                _next = 0;
                File.AppendAllText(_path, Sample("play_start") + "\n", Encoding.UTF8);
                Debug.Log("[S2-DESKTOP] recorder " + _path);
            }
            if (EditorApplication.timeSinceStartup < _next) return;
            _next = EditorApplication.timeSinceStartup + 0.1;
            File.AppendAllText(_path, Sample("periodic") + "\n", Encoding.UTF8);
        }

        static string Sample(string kind)
        {
            var sb = new StringBuilder(512);
            var runner = UnityEngine.Object.FindObjectOfType<TaskMissionRunner>();
            var actor = GameObject.Find("Yixuan_Test");
            var proto = Stage2PickPrototype.Instance;
            var hud = UnityEngine.Object.FindObjectOfType<UnifiedHud>();
            var eye = GameObject.Find("EyeCamera");
            var main = Camera.main;
            Vector3 pos = actor != null ? actor.transform.position : Vector3.zero;
            Quaternion rot = actor != null ? actor.transform.rotation : Quaternion.identity;
            bool taskRunning = runner != null && runner.IsRunning;
            string phase = runner != null ? runner.Phase : "";
            string report = runner != null ? runner.LastReport : "";
            string evidenceStatus = "";
            string evidenceReason = "";
            if (runner != null && runner.Evidence != null)
            {
                evidenceStatus = runner.Evidence.status ?? "";
                evidenceReason = runner.Evidence.reason ?? "";
            }
            bool hudOpen = false;
            string hudTab = "";
            if (hud != null)
            {
                var t = hud.GetType();
                var openField = t.GetField("_open", BindingFlags.Instance | BindingFlags.NonPublic);
                var tabField = t.GetField("_tab", BindingFlags.Instance | BindingFlags.NonPublic);
                if (openField != null) hudOpen = (bool)openField.GetValue(hud);
                if (tabField != null) hudTab = tabField.GetValue(hud).ToString();
            }
            bool uiFocus = UIDocumentFocused();
            int kbControl = GUIUtility.keyboardControl;
            int hotControl = GUIUtility.hotControl;
            bool dragging = false;
            int capturedPointer = -1;
            if (proto != null)
            {
                var pt = proto.GetType();
                var dragField = pt.GetField("_dragging", BindingFlags.Instance | BindingFlags.NonPublic);
                var capField = pt.GetField("_capturedPointerId", BindingFlags.Instance | BindingFlags.NonPublic);
                if (dragField != null) dragging = (bool)dragField.GetValue(proto);
                if (capField != null) capturedPointer = (int)capField.GetValue(proto);
            }
            sb.Append("{\"kind\":\"").Append(kind).Append("\",")
              .Append("\"t\":").Append(Time.realtimeSinceStartup.ToString("F3")).Append(',')
              .Append("\"frame\":").Append(Time.frameCount).Append(',')
              .Append("\"char\":[").Append(pos.x.ToString("F3")).Append(',').Append(pos.y.ToString("F3")).Append(',').Append(pos.z.ToString("F3")).Append("],")
              .Append("\"charYaw\":").Append(rot.eulerAngles.y.ToString("F2")).Append(',')
              .Append("\"taskRunning\":").Append(taskRunning ? "true" : "false").Append(',')
              .Append("\"taskPhase\":\"").Append(Esc(phase)).Append("\",")
              .Append("\"taskReport\":\"").Append(Esc(report)).Append("\",")
              .Append("\"evidenceStatus\":\"").Append(Esc(evidenceStatus)).Append("\",")
              .Append("\"evidenceReason\":\"").Append(Esc(evidenceReason)).Append("\",")
              .Append("\"hudOpen\":").Append(hudOpen ? "true" : "false").Append(',')
              .Append("\"hudTab\":\"").Append(Esc(hudTab)).Append("\",")
              .Append("\"uiTextFocus\":").Append(uiFocus ? "true" : "false").Append(',')
              .Append("\"keyboardControl\":").Append(kbControl).Append(',')
              .Append("\"hotControl\":").Append(hotControl).Append(',')
              .Append("\"dragging\":").Append(dragging ? "true" : "false").Append(',')
              .Append("\"capturedPointer\":").Append(capturedPointer).Append(',')
              .Append("\"protoEnabled\":").Append(proto != null && proto.Enabled ? "true" : "false").Append(',')
              .Append("\"protoSource\":\"").Append(proto != null ? Esc(proto.CurrentSource) : "").Append("\",")
              .Append("\"protoPose\":\"").Append(proto != null ? Esc(proto.CurrentPoseId) : "").Append("\",")
              .Append("\"protoInside\":").Append(proto != null && proto.PointerInsidePreview ? "true" : "false").Append(',')
              .Append("\"protoUiBlocker\":").Append(proto != null && proto.UiBlockerActive ? "true" : "false").Append(',')
              .Append("\"pick\":\"").Append(proto != null && proto.LastPick != null ? Esc(proto.LastPick.Status + ":" + proto.LastPick.SampleId) : "").Append("\",")
              .Append("\"pickDistance\":").Append(proto != null && proto.LastPick != null ? proto.LastPick.DistanceFromCamera.ToString("F3") : "0").Append(',')
              .Append("\"eye\":[").Append(eye != null ? eye.transform.position.x.ToString("F3") + "," + eye.transform.position.y.ToString("F3") + "," + eye.transform.position.z.ToString("F3") : "0,0,0").Append("],")
              .Append("\"main\":[").Append(main != null ? main.transform.position.x.ToString("F3") + "," + main.transform.position.y.ToString("F3") + "," + main.transform.position.z.ToString("F3") : "0,0,0").Append("]}")
              ;
            return sb.ToString();
        }

        static bool UIDocumentFocused()
        {
            var chat = GameObject.Find("UI_Chat");
            if (chat == null) return false;
            var doc = chat.GetComponent<UIDocument>();
            if (doc == null || doc.rootVisualElement == null || doc.rootVisualElement.panel == null) return false;
            var f = doc.rootVisualElement.panel.focusController.focusedElement;
            return f != null;
        }

        static string Esc(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "");
        }
    }
}
