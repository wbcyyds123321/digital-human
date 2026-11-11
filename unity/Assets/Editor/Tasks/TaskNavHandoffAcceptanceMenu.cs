using UnityEditor;
using UnityEngine;
using Yixuan.AI.Tasks;

namespace Yixuan.AI.EditorTools
{
    public static class TaskNavHandoffAcceptanceMenu
    {
        [MenuItem("UnityLITF/任务编排/运行聊天导航暂停恢复验收（Play）", false, 30)]
        public static void Run()
        {
            if (!EditorApplication.isPlaying)
            {
                Debug.LogError("[NavHandoffAcceptance] 请先进入 Play 模式，再运行本验收（不会自动 Play）。");
                return;
            }
            GameObject go = GameObject.Find("__TaskNavHandoffAcceptance");
            if (go == null) go = new GameObject("__TaskNavHandoffAcceptance");
            TaskNavHandoffAcceptance acceptance = go.GetComponent<TaskNavHandoffAcceptance>();
            if (acceptance == null) acceptance = go.AddComponent<TaskNavHandoffAcceptance>();
            if (!acceptance.Running) acceptance.Begin();
        }
    }
}
