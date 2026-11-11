using UnityEngine;

namespace Yixuan.Stage2
{
    /// <summary>
    /// Test-only IMGUI focus probe used by the acceptance runner to verify that
    /// Stage2PickPrototype.IsConfirmBlocked() sees a real IMGUI text-field focus.
    /// Not created by the prototype; the acceptance runner adds it only during tests.
    /// </summary>
    public sealed class Stage2FocusProbe : MonoBehaviour
    {
        // Must be outside the Stage-2 preview rectangle, otherwise the prototype's
        // pointer ownership consumes the click before IMGUI can focus this field.
        public Rect FieldRect = new Rect(500f, 620f, 420f, 30f);
        public bool Active = true;
        public bool ShowDebugLabel = true;
        public bool BlurRequested;
        public static int LastKeyboardControl;
        string _text = "";

        void OnGUI()
        {
            if (BlurRequested)
            {
                GUI.FocusControl(null);
                GUIUtility.keyboardControl = 0;
                BlurRequested = false;
            }
            if (!Active) return;
            int prevDepth = GUI.depth;
            GUI.depth = -1000;
            if (ShowDebugLabel)
            {
                GUI.Box(new Rect(500f, 588f, 420f, 26f), "IMGUI probe · GUI mouse " + Event.current.mousePosition +
                    " · Screen " + Screen.width + "×" + Screen.height);
            }
            GUI.SetNextControlName("Stage2FocusProbeField");
            GUI.color = new Color(1f, 0.1f, 0.1f, 0.95f);
            GUI.DrawTexture(new Rect(FieldRect.x - 3f, FieldRect.y - 3f,
                FieldRect.width + 6f, FieldRect.height + 6f), Texture2D.whiteTexture);
            GUI.color = Color.white;
            _text = GUI.TextField(FieldRect, _text);
            LastKeyboardControl = GUIUtility.keyboardControl;
            GUI.depth = prevDepth;
        }

        public bool HasImGuiKeyboardControl()
        {
            return LastKeyboardControl != 0 || GUIUtility.keyboardControl != 0;
        }
    }
}
