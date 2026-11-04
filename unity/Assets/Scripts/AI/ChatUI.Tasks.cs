using UnityEngine;
using UnityEngine.UIElements;

namespace Yixuan.AI
{
    public partial class ChatUI
    {
        bool _taskPanelLayout;
        // UI Toolkit and IMGUI can have different scaling. Reserve the actual input row in screen pixels.
        public float TaskInputTopScreenY
        {
            get
            {
                if(_input==null||_input.parent==null||_root==null||_root.worldBound.height<=0) return Screen.height-84;
                return (_input.parent.worldBound.yMin-_root.worldBound.yMin)*Screen.height/_root.worldBound.height;
            }
        }
        // The task panel already displays the report. Keep the ordinary chat input usable,
        // and prevent its large subtitle/name overlay from covering task controls on short windows.
        public void SetTaskPanelLayout(bool active)
        {
            if (_taskPanelLayout == active) return;
            _taskPanelLayout = active;
            if (_speech != null) _speech.style.visibility = active ? Visibility.Hidden : Visibility.Visible;
            if (_nameLabel != null) _nameLabel.style.visibility = active ? Visibility.Hidden : Visibility.Visible;
            if (_hint != null) _hint.style.visibility = active ? Visibility.Hidden : Visibility.Visible;
            if (_scrim != null) _scrim.style.display = active ? DisplayStyle.None : DisplayStyle.Flex;
        }
    }
}
