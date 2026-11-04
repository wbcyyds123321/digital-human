using UnityEditor;
using UnityEngine;

namespace Yixuan.EditorTools
{
    /// <summary>
    /// 演示/截图时关闭 Game 视图的 Gizmos 叠加层。
    /// Gizmos（灯光的小太阳/灯泡图标等）只在编辑器里由 Game 视图绘制，
    /// 真实 Build 不会出现；但演示时不应该盖在画面上。
    /// </summary>
    [InitializeOnLoad]
    public static class GameViewGizmoHider
    {
        static GameViewGizmoHider()
        {
            EditorApplication.playModeStateChanged += _ => Hide();
            EditorApplication.delayCall += Hide;
        }

        [MenuItem("Tools/Yixuan/Hide GameView Gizmos")]
        static void Hide()
        {
            var type = typeof(Editor).Assembly.GetType("UnityEditor.GameView");
            if (type == null) return;

            var gameView = EditorWindow.GetWindow(type);
            if (gameView == null) return;

            var flags = System.Reflection.BindingFlags.Instance
                        | System.Reflection.BindingFlags.Public
                        | System.Reflection.BindingFlags.NonPublic;

            var show = type.GetProperty("showGizmos", flags);
            if (show != null) show.SetValue(gameView, false, null);

            var draw = type.GetProperty("drawGizmos", flags);
            if (draw != null) draw.SetValue(gameView, false, null);

            gameView.Repaint();
        }
    }
}
