using UnityEngine;
using System.Collections.Generic;

namespace Yixuan.Brain
{
    // ============================================================
    //  世界状态：角色当前"感受"到的一切
    // ============================================================

    public enum Mood
    {
        Neutral,    // 平静
        Happy,      // 愉快
        Curious,    // 好奇
        Bored,      // 无聊
        Tired,      // 疲惫
    }

    [System.Serializable]
    public class WorldState
    {
        /// <summary>距上次外部刺激（用户说话/点击）过了多久</summary>
        public float TimeSinceStimulus = 0f;

        /// <summary>用户是否正在关注（鼠标在窗口内 / 有输入）</summary>
        public bool UserAttentive = false;

        /// <summary>用户最近有没有互动过（30 秒内）</summary>
        public bool RecentlyInteracted = false;

        /// <summary>本次会话里是否真的发生过互动（用于区分"刚启动"和"互动过"）</summary>
        public bool HasEverInteracted = false;

        /// <summary>疲劳度 0~1，随时间累积，互动时下降</summary>
        [Range(0f, 1f)] public float Fatigue = 0f;

        /// <summary>亲密度/熟悉度 0~1，互动越多越高，影响表情选择</summary>
        [Range(0f, 1f)] public float Familiarity = 0f;

        /// <summary>刺激强度 0~1，外部传入（比如语音音量）</summary>
        [Range(0f, 1f)] public float StimulusIntensity = 0f;

        /// <summary>累计互动次数</summary>
        public int InteractionCount = 0;

        /// <summary>场景运行时长</summary>
        public float SessionTime = 0f;
    }

    // ============================================================
    //  注意力模型：给每个"可做之事"打分
    // ============================================================

    /// <summary>
    /// 注意力算法 —— 主动触发机制的核心。
    ///
    /// 【为什么需要它】
    /// 之前的状态机是"定时切表情 + 随机播手势"，它是**被动的**：
    /// 不管用户在与不在、看没看它，行为节奏都一样。
    ///
    /// 注意力模型改成：每帧给所有候选行为打分，选分最高的执行。
    /// 这样角色就有了"什么时候该主动做点什么"的判断力。
    ///
    /// 【评分维度】
    ///   紧迫性  —— 多久没互动了（越久越想主动搭话）
    ///   相关性  —— 当前状态适不适合做这件事
    ///   新鲜度  —— 最近做过的事降权（避免重复）
    /// </summary>
    public class AttentionModel
    {
        public class Candidate
        {
            public string Id;
            public string Kind;      // "expression" | "gesture"
            public float Score;
            public float Cooldown;   // 剩余冷却时间
            public float LastUsed = -999f;
        }

        readonly List<Candidate> _candidates = new List<Candidate>();
        System.Random _rng;

        /// <summary>每帧输出的评分明细（调试用）</summary>
        public List<Candidate> Candidates { get { return _candidates; } }

        public AttentionModel(int seed)
        {
            _rng = new System.Random(seed);
        }

        public void Add(string id, string kind)
        {
            _candidates.Add(new Candidate { Id = id, Kind = kind });
        }

        public void Tick(float dt)
        {
            for (int i = 0; i < _candidates.Count; i++)
            {
                if (_candidates[i].Cooldown > 0f) _candidates[i].Cooldown -= dt;
            }
        }

        public void MarkUsed(string id, float cooldown, float now)
        {
            for (int i = 0; i < _candidates.Count; i++)
            {
                if (_candidates[i].Id != id) continue;
                _candidates[i].Cooldown = cooldown;
                _candidates[i].LastUsed = now;
                return;
            }
        }

        /// <summary>
        /// 重新计算所有候选行为的注意力分数。
        /// 分数含义：这一帧"想做这件事"的欲望强度。
        /// </summary>
        public void Evaluate(WorldState w, Mood mood, float now)
        {
            for (int i = 0; i < _candidates.Count; i++)
            {
                var c = _candidates[i];
                if (c.Cooldown > 0f) { c.Score = 0f; continue; }

                float s = 0f;

                // ---- 通用：越久没互动，主动行为越紧迫 ----
                // 【设计要点】idleFactor 的上限必须能单独把分数顶过阈值，
                // 否则"主动触发"会被 MinActionScore 卡死，角色永远静默。
                // 实测：idleFactor=1 时手势分可达 1.10 > 默认阈值 0.85 ✅
                float idleFactor = Mathf.Clamp01(w.TimeSinceStimulus / 25f);

                // ---- 通用：疲劳度高时抑制所有动作 ----
                float fatigueDamp = 1f - w.Fatigue * 0.6f;

                if (c.Kind == "gesture")
                {
                    // 手势基础分：静默越久越想主动做点什么
                    s = (0.20f + idleFactor * 0.90f) * fatigueDamp;

                    // 情绪加成
                    if (mood == Mood.Happy) s += 0.22f;
                    if (mood == Mood.Curious) s += 0.12f;
                    if (mood == Mood.Tired) s -= 0.25f;
                    if (mood == Mood.Bored) s += 0.15f;

                    // 用户刚互动过 → 响应性提高（这条让"被搭话时有反应"更明显）
                    if (w.RecentlyInteracted) s += 0.30f;
                }
                else // expression
                {
                    // 表情：幅度小、更频繁，但基础分低于手势
                    s = (0.10f + idleFactor * 0.45f) * fatigueDamp;

                    if (c.Id == "smile" && (mood == Mood.Happy || w.Familiarity > 0.4f)) s += 0.30f;
                    if (c.Id == "neutral") s += 0.20f * (1f - idleFactor);
                    if (c.Id == "surprised" && w.StimulusIntensity > 0.5f) s += 0.55f;
                    if (c.Id == "troubled" && mood == Mood.Bored) s += 0.25f;
                    if (c.Id == "serious" && mood == Mood.Tired) s += 0.25f;
                    if (c.Id == "shy" && w.Familiarity > 0.7f) s += 0.18f;
                }

                // ---- 新鲜度惩罚：刚做过的降权 ----
                float sinceUse = now - c.LastUsed;
                if (sinceUse < 30f) s *= Mathf.Lerp(0.25f, 1f, sinceUse / 30f);

                // ---- 随机扰动：避免完全确定性的行为（看起来像机器）----
                s *= (float)(0.85f + _rng.NextDouble() * 0.30f);

                c.Score = Mathf.Max(0f, s);
            }
        }

        /// <summary>取得分最高的候选；低于阈值时返回 null（表示"什么都不做"）</summary>
        public Candidate PickBest(float minScore)
        {
            Candidate best = null;
            float bestScore = minScore;
            for (int i = 0; i < _candidates.Count; i++)
            {
                if (_candidates[i].Score > bestScore)
                {
                    bestScore = _candidates[i].Score;
                    best = _candidates[i];
                }
            }
            return best;
        }

        /// <summary>调试：输出 Top N 评分</summary>
        public string DumpTop(int n)
        {
            var sorted = new List<Candidate>(_candidates);
            sorted.Sort(delegate(Candidate a, Candidate b) { return b.Score.CompareTo(a.Score); });
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < sorted.Count && i < n; i++)
                sb.AppendLine("      " + sorted[i].Id.PadRight(12) + " " + sorted[i].Kind.PadRight(10) +
                              " score=" + sorted[i].Score.ToString("F3") +
                              (sorted[i].Cooldown > 0f ? "  [CD " + sorted[i].Cooldown.ToString("F1") + "s]" : ""));
            return sb.ToString();
        }
    }
}
