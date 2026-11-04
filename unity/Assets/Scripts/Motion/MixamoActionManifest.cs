using UnityEngine;

namespace Yixuan.Motion
{
    /// <summary>
    /// Mixamo 动作清单 —— "语义 id → 实际 clip 名" 的映射表（运行时用，无编辑器依赖）。
    ///
    /// 由 MixamoActionWiring 在编辑器里生成并写入，运行时供 UI / AI / 状态机查表。
    ///
    /// 【为什么要有这一层】
    /// 直接写死 "Waving Gesture" 这种 Mixamo 原始名会把代码和素材绑死：
    /// 换一个挥手动作就得改代码。有这张表之后，代码只说 "wave"，
    /// 素材换了只改编辑器的映射表。
    ///
    /// 【T2 扩展：动作元数据】
    /// 每个动作除了 id / clip 名，还带中文名、时长、所需姿态、身体部位、情绪标签、
    /// 能否循环、能否打断 —— 供 AI 的 ActionCatalog / PromptBuilder / Director 使用。
    /// 这些字段由菜单 UnityLITF/仪玄/⑧ 补齐（人工标注表在 ActionMetadata.cs）。
    /// 旧场景里的 manifest 没有这些字段时全部取默认值，不会报错。
    /// </summary>
    [DisallowMultipleComponent]
    public class MixamoActionManifest : MonoBehaviour
    {
        [System.Serializable]
        public class Entry
        {
            public string Id;        // 语义 id：idle / wave / nod …
            public string ClipName;  // 实际 AnimationClip 名

            // ---------- T2 动作元数据 ----------
            [Tooltip("中文名，如「挥手」")]
            public string Label;
            [Tooltip("动作时长（秒），由 clip 实测填入")]
            public float Duration;
            [Tooltip("所需姿态：stand / sit / kneel")]
            public string Posture;
            [Tooltip("身体部位：full（全身）/ upper（上半身）")]
            public string BodyPart;
            [Tooltip("情绪标签，逗号分隔，如 happy,friendly")]
            public string EmotionTags;
            [Tooltip("是否适合循环播放（持续状态）")]
            public bool Loop;
            [Tooltip("播放过程中能否被其他动作打断")]
            public bool Interruptible;
            [Tooltip("备注（可为空），如「从坐姿起身」")]
            public string Note;
        }

        [Tooltip("语义 id → clip 名。由菜单「UnityLITF/仪玄/② 装配 Mixamo 动作」生成")]
        public Entry[] Actions;

        /// <summary>按语义 id 取 clip 名；找不到返回 null</summary>
        public string Resolve(string id)
        {
            var e = Get(id);
            return e != null ? e.ClipName : null;
        }

        /// <summary>按语义 id 取完整条目；找不到返回 null</summary>
        public Entry Get(string id)
        {
            if (Actions == null || string.IsNullOrEmpty(id)) return null;
            for (int i = 0; i < Actions.Length; i++)
                if (Actions[i] != null && Actions[i].Id == id) return Actions[i];
            return null;
        }

        /// <summary>列出所有可用语义 id</summary>
        public string[] Ids()
        {
            if (Actions == null) return new string[0];
            var arr = new string[Actions.Length];
            for (int i = 0; i < Actions.Length; i++) arr[i] = Actions[i] != null ? Actions[i].Id : null;
            return arr;
        }

        /// <summary>状态摘要（调试用）</summary>
        public string Status()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("[MixamoActionManifest] 共 " + (Actions == null ? 0 : Actions.Length) + " 个语义动作");
            if (Actions != null)
                foreach (var e in Actions)
                    if (e != null)
                        sb.AppendLine("   " + e.Id.PadRight(14) + " → " + e.ClipName +
                                      "  [" + (e.Label ?? "-") + " " + e.Duration.ToString("F2") + "s " +
                                      (e.Posture ?? "-") + "/" + (e.BodyPart ?? "-") + " loop=" + e.Loop + "]");
            return sb.ToString();
        }
    }
}
