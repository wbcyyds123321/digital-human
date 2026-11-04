using UnityEngine;

namespace Yixuan.AI
{
    /// <summary>
    /// 离线规则脑 —— 没有 API Key / 请求失败时的兜底（B3 / B6）。
    ///
    /// 【定位】
    /// 不是要替代 LLM，只是保证"哪怕网络挂了，角色也能听懂最常见的几句话并做出反应"：
    /// 关键词 → 情绪 + 动作 + 一句庄方宜口吻的中文回复。
    ///
    /// 【铁律：id 必须校验】
    /// 规则里只写"想要哪个 id"，填进 BehaviorPlan 之前一律先问 ActionCatalog：
    ///   - 动作：HasGesture(id) 为 false → 换成 DefaultGesture(emotion)，再不行就 null；
    ///   - 表情：HasEmotion(id) 为 false → 退回 neutral，再不行就 null。
    /// 所以场景里没装配 manifest 时，这里只会说话、不会做动作，也不会抛异常。
    ///
    /// 【规则顺序 = 优先级】
    /// 先匹配到的赢。宽泛关键词（好/行/对/嗯）必须放在最后，
    /// 否则"你好"会被"好"抢走、"对不起"会被"对"抢走。
    /// </summary>
    public static class RuleBrain
    {
        /// <summary>一条规则：命中任一关键词 → 用这套情绪/动作/回复</summary>
        class Rule
        {
            public string[] Keywords;
            public string Emotion;  // 首选表情 id（会校验）
            public string Gesture;  // 首选动作 id（会校验；空 = 这句不做动作）
            public string Gaze;     // user / away / down / up
            public string Reply;    // 庄方宜口吻的简短回复
        }

        const string DefaultReply = "嗯，我在听，你接着说。";

        /// <summary>
        /// 规则表（顺序 = 优先级）。
        /// 关键词是中文口语里最常见的说法；英文只留了 hello/thanks/bye 这类。
        /// </summary>
        static readonly Rule[] Rules = new Rule[]
        {
            // 1) 问候
            new Rule {
                Keywords = new[] { "你好", "您好", "嗨", "哈喽", "hello", "早上好", "晚上好", "在吗", "在不在", "好久不见" },
                Emotion = "smile", Gesture = "wave", Gaze = "user",
                Reply = "你好呀，今天想聊点什么？"
            },
            // 2) 道谢
            new Rule {
                Keywords = new[] { "谢谢", "多谢", "感谢", "谢啦", "thanks", "thank you" },
                Emotion = "smile", Gesture = "nod", Gaze = "user",
                Reply = "不客气，能帮上忙就好。"
            },
            // 3) 道歉（必须排在"否定"前面，"不好意思"里含"不好/不"）
            new Rule {
                Keywords = new[] { "对不起", "抱歉", "不好意思", "我的错", "怪我" },
                Emotion = "troubled", Gesture = "shake_head", Gaze = "down",
                Reply = "没关系的，别放在心上。"
            },
            // 4) 提问 / 求解（排在"否定"前面，"为什么不行"应该走思考而不是摇头）
            new Rule {
                Keywords = new[] { "为什么", "怎么", "如何", "什么原因", "怎么办", "怎么会" },
                Emotion = "serious", Gesture = "think", Gaze = "away",
                Reply = "让我想一想……你想了解哪一部分？"
            },
            // 5) 否定
            // ☆ 不写单独的"不"：会误命中"不错/不好意思/不久"；用双字词更稳。
            new Rule {
                Keywords = new[] { "不是", "不对", "不行", "不要", "不想", "不喜欢", "不好", "没有", "不能", "拒绝" },
                Emotion = "neutral", Gesture = "shake_head", Gaze = "user",
                Reply = "嗯，我明白了。那你怎么想？"
            },
            // 6) 惊讶
            new Rule {
                Keywords = new[] { "真的吗", "真的假的", "哇", "居然", "竟然", "天哪", "不会吧", "太意外", "没想到" },
                Emotion = "surprised", Gesture = null, Gaze = "user",
                Reply = "诶？这倒有点出乎意料，能详细说说吗？"
            },
            // 7) 夸奖（表情害羞；动作不做，避免"谢谢→nod、夸奖→nod"连着重复）
            new Rule {
                Keywords = new[] { "漂亮", "好看", "可爱", "真美", "很美" },
                Emotion = "shy", Gesture = null, Gaze = "away",
                Reply = "……谢谢你这么说，我会不好意思的。"
            },
            // 8) 疲惫 / 辛苦
            new Rule {
                Keywords = new[] { "累", "疲惫", "困", "辛苦", "撑不住", "加班" },
                Emotion = "troubled", Gesture = null, Gaze = "down",
                Reply = "辛苦啦，要不要先歇一会儿？"
            },
            // 9) 开心 / 好消息
            new Rule {
                Keywords = new[] { "开心", "高兴", "太好了", "哈哈", "不错", "厉害", "真棒", "棒" },
                Emotion = "laugh", Gesture = "wave_both", Gaze = "user",
                Reply = "哈哈，听起来不错，我也替你高兴。"
            },
            // 10) 告白 / 想念
            new Rule {
                Keywords = new[] { "喜欢你", "爱你", "想你", "心动" },
                Emotion = "shy", Gesture = null, Gaze = "away",
                Reply = "……突然这么说，我一时不知道该怎么接。"
            },
            // 11) 问身份
            new Rule {
                Keywords = new[] { "你是谁", "你叫什么", "名字", "介绍一下你", "自我介绍", "你是哪位" },
                Emotion = "smile", Gesture = null, Gaze = "user",
                Reply = "我是庄方宜，来自终末地，很高兴见到你。"
            },
            // 12) 告别（排在"肯定"前面，"好，再见"应该挥手）
            new Rule {
                Keywords = new[] { "再见", "拜拜", "bye", "下次聊", "回见", "晚安" },
                Emotion = "smile", Gesture = "wave", Gaze = "user",
                Reply = "好，那下次再聊，路上小心。"
            },
            // 13) 肯定 / 应答（关键词最宽泛，必须放最后）
            new Rule {
                Keywords = new[] { "好的", "好呀", "可以", "没问题", "对吧", "是的", "当然", "行", "嗯", "好", "对" },
                Emotion = "smile", Gesture = "nod", Gaze = "user",
                Reply = "嗯，那就这么定了。"
            },
        };

        /// <summary>输入是否命中某条具体规则（默认兜底回复不算规则）</summary>
        public static bool HasRule(string input)
        {
            return Match(input) != null;
        }

        /// <summary>
        /// 规则回复：关键词 → BehaviorPlan（已做 id 校验）。
        /// 永不返回 null、永不抛异常；config 可以传 null。
        ///
        /// 未命中任何规则时返回 neutral + 一句"我在听"的兜底回复。
        /// </summary>
        public static BehaviorPlan Reply(string input, AIConfig config)
        {
            int maxChars = (config != null && config.MaxReplyChars > 0)
                ? config.MaxReplyChars : 60;
            float intensity = Mathf.Clamp01(config != null ? config.DefaultIntensity : 0.6f);

            try
            {
                Rule rule = Match(input);

                if (rule == null)
                {
                    return new BehaviorPlan
                    {
                        reply = Clamp(DefaultReply, maxChars),
                        emotion = PickEmotion("neutral"),
                        intensity = intensity,
                        gesture = null,
                        posture = null,   // 不改姿态
                        gaze = "user",
                        beats = null,
                    };
                }

                return new BehaviorPlan
                {
                    reply = Clamp(rule.Reply, maxChars),
                    emotion = PickEmotion(rule.Emotion),
                    intensity = intensity,
                    gesture = PickGesture(rule.Gesture, rule.Emotion),
                    posture = null,   // 规则脑不主动改变站/坐/跪，交给 Director / 用户
                    gaze = string.IsNullOrEmpty(rule.Gaze) ? "user" : rule.Gaze,
                    beats = null,
                };
            }
            catch (System.Exception e)
            {
                // 理论上走不到这里；真出问题时也保证"有 plan、有话说"
                Debug.LogWarning("[RuleBrain] 规则回复异常，退回默认回复：" + e.Message);
                return new BehaviorPlan
                {
                    reply = Clamp(DefaultReply, maxChars),
                    emotion = null,
                    intensity = intensity,
                    gesture = null,
                    posture = null,
                    gaze = "user",
                    beats = null,
                };
            }
        }

        // ============================================================
        //  内部工具
        // ============================================================

        /// <summary>按优先级找第一条命中的规则；没命中返回 null</summary>
        static Rule Match(string input)
        {
            if (string.IsNullOrEmpty(input)) return null;
            string text = input.Trim();
            if (text.Length == 0) return null;

            for (int r = 0; r < Rules.Length; r++)
            {
                var rule = Rules[r];
                if (rule.Keywords == null) continue;
                for (int k = 0; k < rule.Keywords.Length; k++)
                {
                    string kw = rule.Keywords[k];
                    if (string.IsNullOrEmpty(kw)) continue;
                    if (text.IndexOf(kw, System.StringComparison.OrdinalIgnoreCase) >= 0)
                        return rule;
                }
            }
            return null;
        }

        /// <summary>首选表情 id 经 Catalog 校验；不合法退回 neutral，再不合法返回 null</summary>
        static string PickEmotion(string preferred)
        {
            if (!string.IsNullOrEmpty(preferred) && ActionCatalog.HasEmotion(preferred))
                return preferred;
            if (ActionCatalog.HasEmotion("neutral"))
                return "neutral";
            return null;
        }

        /// <summary>
        /// 首选动作 id 经 Catalog 校验；不合法就按情绪标签从清单里挑第一个；再没有返回 null。
        /// ActionCatalog 为空时这里就是 null —— 不会编造动作。
        /// </summary>
        static string PickGesture(string preferred, string emotion)
        {
            if (!string.IsNullOrEmpty(preferred) && ActionCatalog.HasGesture(preferred))
                return preferred;

            if (!string.IsNullOrEmpty(emotion))
            {
                string fallback = ActionCatalog.DefaultGesture(emotion);
                if (!string.IsNullOrEmpty(fallback) && ActionCatalog.HasGesture(fallback))
                    return fallback;
            }
            return null;
        }

        /// <summary>按 MaxReplyChars 截断（maxChars <= 0 表示不限制）</summary>
        static string Clamp(string s, int maxChars)
        {
            if (string.IsNullOrEmpty(s)) return s ?? "";
            if (maxChars <= 0 || s.Length <= maxChars) return s;
            return s.Substring(0, maxChars);
        }
    }
}
