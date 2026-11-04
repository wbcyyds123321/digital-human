using UnityEngine;

namespace Yixuan.Motion
{
    /// <summary>
    /// 15 个 Mixamo 动作的人工标注元数据（T2 动作库清单数据化）。
    ///
    /// 【为什么单独一张表，而不是写进场景里的 manifest】
    /// 1. manifest 是场景序列化数据，改起来要开 Unity；这张表是代码，可 diff、可 review。
    /// 2. AI 的 ActionCatalog 需要“中文名 + 时长 + 所需姿态 + 部位 + 情绪标签 + 能否循环/打断”
    ///    才能在 prompt 里给 LLM 说清楚“这个动作是什么、什么时候能用”。
    /// 3. 换动作素材时只改这张表（或只改 manifest 里的 clip 名），两边解耦。
    ///
    /// 【时长不在这张表里】
    /// 时长由菜单 ⑧ 从 clip.length 实测后写进 manifest（换素材自动更新，不会写错）。
    ///
    /// 【标注依据】
    /// PROJECT_NOTES 4.4 的动作清单 + 各动作实际内容；posture/bodyPart 是 T3 分层与
    /// T4 姿态图的地基，所以宁可保守（upper 就是真的只动上半身）。
    /// </summary>
    public static class ActionMetadata
    {
        public class Meta
        {
            public string Id;          // 语义 id（必须与 MixamoActionWiring.Map 的 key 一致）
            public string Label;       // 中文名
            public string Posture;     // stand / sit / kneel
            public string BodyPart;    // full / upper
            public string EmotionTags; // 逗号分隔
            public bool Loop;          // 适合循环（持续状态）
            public bool Interruptible; // 播放中可被别的动作打断
            public string Note;        // 备注（可为空）
        }

        public static readonly Meta[] All = new Meta[]
        {
            new Meta { Id = "idle",        Label = "站立待机",   Posture = "stand", BodyPart = "full",  EmotionTags = "calm,neutral",                 Loop = true,  Interruptible = true,  Note = "" },
            new Meta { Id = "idle_breath", Label = "呼吸待机",   Posture = "stand", BodyPart = "full",  EmotionTags = "calm,relaxed",                 Loop = true,  Interruptible = true,  Note = "" },
            new Meta { Id = "idle_shift",  Label = "换重心待机", Posture = "stand", BodyPart = "full",  EmotionTags = "calm,neutral",                 Loop = true,  Interruptible = true,  Note = "" },
            new Meta { Id = "wave",        Label = "挥手",       Posture = "stand", BodyPart = "upper", EmotionTags = "happy,friendly,greeting",      Loop = false, Interruptible = true,  Note = "" },
            new Meta { Id = "wave_both",   Label = "双手挥手",   Posture = "stand", BodyPart = "upper", EmotionTags = "happy,excited,greeting",       Loop = false, Interruptible = true,  Note = "" },
            new Meta { Id = "clap",        Label = "鼓掌",       Posture = "stand", BodyPart = "full",  EmotionTags = "happy,celebrate",              Loop = false, Interruptible = false, Note = "实际素材是「从坐姿起身并拍手」，站立起播时腿部有下沉，慎用" },
            new Meta { Id = "nod",         Label = "点头",       Posture = "stand", BodyPart = "upper", EmotionTags = "agree,acknowledge,friendly",   Loop = false, Interruptible = true,  Note = "" },
            new Meta { Id = "shake_head",  Label = "摇头",       Posture = "stand", BodyPart = "upper", EmotionTags = "disagree,deny,worried",        Loop = false, Interruptible = true,  Note = "" },
            new Meta { Id = "think",       Label = "思考",       Posture = "stand", BodyPart = "upper", EmotionTags = "thinking,curious,serious",     Loop = true,  Interruptible = true,  Note = "" },
            new Meta { Id = "look_around", Label = "环顾",       Posture = "stand", BodyPart = "upper", EmotionTags = "curious,nervous,searching",    Loop = true,  Interruptible = true,  Note = "" },
            new Meta { Id = "walk",        Label = "走路",       Posture = "stand", BodyPart = "full",  EmotionTags = "neutral,determined",           Loop = true,  Interruptible = true,  Note = "原地版，无根位移" },
            new Meta { Id = "jog",         Label = "慢跑",       Posture = "stand", BodyPart = "full",  EmotionTags = "energetic,urgent",             Loop = true,  Interruptible = true,  Note = "原地版，无根位移" },
            new Meta { Id = "sit",         Label = "坐下待机",   Posture = "sit",   BodyPart = "full",  EmotionTags = "calm,relaxed",                 Loop = true,  Interruptible = true,  Note = "" },
            new Meta { Id = "sit_point",   Label = "坐着指",     Posture = "sit",   BodyPart = "upper", EmotionTags = "explaining,confident",         Loop = true,  Interruptible = true,  Note = "" },
            new Meta { Id = "kneel",       Label = "单膝跪",     Posture = "kneel", BodyPart = "full",  EmotionTags = "serious,respectful",           Loop = true,  Interruptible = true,  Note = "" },
        };

        public static Meta Get(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            for (int i = 0; i < All.Length; i++)
                if (All[i] != null && All[i].Id == id) return All[i];
            return null;
        }

        /// <summary>按 id 把元数据填进 manifest 条目（不动 Duration / ClipName）。找不到返回 false</summary>
        public static bool TryFill(MixamoActionManifest.Entry entry)
        {
            if (entry == null) return false;
            var meta = Get(entry.Id);
            if (meta == null) return false;

            entry.Label = meta.Label;
            entry.Posture = meta.Posture;
            entry.BodyPart = meta.BodyPart;
            entry.EmotionTags = meta.EmotionTags;
            entry.Loop = meta.Loop;
            entry.Interruptible = meta.Interruptible;
            entry.Note = meta.Note;
            return true;
        }

        /// <summary>取中文名；没有标注时退回 id</summary>
        public static string LabelOf(string id)
        {
            var meta = Get(id);
            return (meta != null && !string.IsNullOrEmpty(meta.Label)) ? meta.Label : (id ?? "");
        }

        /// <summary>姿态 id → 中文（站/坐/跪）</summary>
        public static string PostureCn(string posture)
        {
            switch (posture)
            {
                case "stand": return "站";
                case "sit":   return "坐";
                case "kneel": return "跪";
                default:      return string.IsNullOrEmpty(posture) ? "?" : posture;
            }
        }

        /// <summary>部位 id → 中文（全身/上半身）</summary>
        public static string BodyPartCn(string bodyPart)
        {
            switch (bodyPart)
            {
                case "full":  return "全身";
                case "upper": return "上半身";
                default:      return string.IsNullOrEmpty(bodyPart) ? "?" : bodyPart;
            }
        }

        public static string[] Ids()
        {
            var arr = new string[All.Length];
            for (int i = 0; i < All.Length; i++) arr[i] = All[i] != null ? All[i].Id : null;
            return arr;
        }
    }
}
