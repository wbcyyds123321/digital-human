using UnityEngine;
using System.Collections.Generic;

namespace Yixuan.Brain
{
    /// <summary>
    /// 仪玄的"大脑" —— 分层状态机 + 注意力驱动的主动触发。
    ///
    /// 【三层架构】
    ///   L1 待机层  ProceduralIdleMotion + BlinkController
    ///              永远在跑，保证角色不会"死"。本脚本不干预。
    ///   L2 情绪层  本脚本的 _mood，由世界状态驱动转移，决定默认表情
    ///   L3 行为层  GestureLibrary 的手势，由注意力模型选出并播放
    ///
    /// 【和旧 AutoPlay 的区别】
    ///   旧：定时器到了就切下一个表情 / 随机播一个手势（被动、机械）
    ///   新：每 tick 给所有候选行为打注意力分，选分最高的执行；
    ///       用户互动时"响应性"提高，长时间没人时"主动性"提高（主动触发）
    ///
    /// 【给 AI 预留的接口】
    ///   ReceiveStimulus(intensity)  —— 外部刺激（语音/点击/LLM 输出）
    ///   SetMood(Mood)               —— LLM 可以直接指定情绪
    ///   DoGesture(id) / DoExpression(id) —— LLM 直接触发行为
    /// 这样 AI 既能"接管"，也能只在旁边施加影响。
    /// </summary>
    [DisallowMultipleComponent]
    public class YixuanBrain : MonoBehaviour
    {
        [Header("引用（留空自动查找）")]
        public Yixuan.Core.YixuanCharacter Character;

        [Header("开关")]
        [Tooltip("启用注意力驱动的主动触发（关掉就退回旧的定时器模式）")]
        public bool EnableProactive = true;
        [Tooltip("接管 YixuanCharacter 的 AutoPlay（避免两套逻辑打架）")]
        public bool TakeOverAutoPlay = true;

        [Header("节奏")]
        [Tooltip("决策间隔（秒）。太短会显得躁动，太长会显得迟钝")]
        public float DecisionInterval = 1.5f;
        [Tooltip("行为最低分阈值，低于此值什么都不做")]
        public float MinActionScore = 0.85f;
        [Tooltip("两次主动行为之间的最短间隔")]
        public float MinActionGap = 10f;
        [Tooltip("刚启动时的静默期（秒）—— 避免一进游戏就疯狂做动作")]
        public float StartupSilence = 3f;

        [Header("世界状态演化")]
        [Tooltip("疲劳累积速度（每秒）")]
        public float FatigueRate = 0.004f;
        [Tooltip("单次互动降低的疲劳")]
        public float FatigueReliefPerInteraction = 0.12f;
        [Tooltip("每次互动增加的熟悉度")]
        public float FamiliarityPerInteraction = 0.03f;
        [Tooltip("多久算'最近互动过'")]
        public float RecentInteractionWindow = 30f;

        [Header("调试")]
        public bool VerboseLog = true;
        [Tooltip("在 Console 打印每次决策的评分明细")]
        public bool LogScores = false;

        // ---- 状态 ----
        public WorldState World { get; private set; }
        public Mood CurrentMood { get; private set; }
        public string LastDecision { get; private set; }

        AttentionModel _attention;
        float _decideTimer;
        float _lastActionTime = -999f;

        // 情绪停留时间（避免抖动）
        float _moodDwell;
        const float MinMoodDwell = 4f;

        void Awake()
        {
            World = new WorldState();
            // ★ 初始状态修正：还没互动过，不能标记为"最近互动"
            //   否则一进游戏就被当成"用户刚说过话"，行为节奏会失真。
            //   用一个足够大的数（而不是 999）避免日志里出现诡异的天文数字。
            World.TimeSinceStimulus = 120f;
            CurrentMood = Mood.Neutral;
            _attention = new AttentionModel(20260921);
            LastDecision = "(启动)";
        }

        void Start()
        {
            ResolveRefs();
            BuildCandidates();

            if (TakeOverAutoPlay && Character != null)
            {
                Character.SetAutoPlay(false);
                if (VerboseLog) Debug.Log("[Brain] 已接管 AutoPlay，改用注意力驱动");
            }
            ApplyMoodExpression();
            if (VerboseLog) Debug.Log(Status());
        }

        void ResolveRefs()
        {
            if (Character == null) Character = FindObjectOfType<Yixuan.Core.YixuanCharacter>();
        }

        /// <summary>
        /// 从语义表里取"适合 AI 主动触发"的动作 id。
        ///
        /// 【为什么要筛】
        /// 语义表里有 15 个动作，但不是都适合 AI 自己乱放：
        ///   走路/慢跑/坐下/单膝跪 —— 这些是"用户意图"级别的状态切换，
        ///   让 AI 自己突然开始走路会很怪。所以只挑短促的手势/表情类。
        /// 【为什么要有兜底】
        /// 万一场景里没挂 MixamoActionManifest，退回最保守的一组，
        /// 至少不会因为数组为空而让 AI 完全不动作。
        /// </summary>
        static readonly string[] AiGestureWhitelist =
        {
            "wave", "wave_both", "nod", "shake_head", "think", "look_around"
            // ⚠ 不要加 "clap"：素材文件实际是 "Standing Clap From Sitting"（从坐姿站起来拍手），
            //   站立时自动播放会出现腿部穿地 17cm（PROJECT_NOTES 4.4）。手动按钮仍保留该动作。
        };

        string[] GestureIdsFromManifest()
        {
            if (Character == null) Character = GetComponent<Yixuan.Core.YixuanCharacter>();
            if (Character == null) Character = FindObjectOfType<Yixuan.Core.YixuanCharacter>();

            var manifest = (Character != null) ? Character.Manifest : null;
            if (manifest == null) manifest = FindObjectOfType<Yixuan.Motion.MixamoActionManifest>();

            if (manifest == null || manifest.Actions == null || manifest.Actions.Length == 0)
            {
                Debug.LogWarning("[Brain] 找不到 MixamoActionManifest —— " +
                                 "AI 动作候选退回保守列表。请先跑菜单 UnityLITF/仪玄/② 装配");
                return new string[] { "wave", "nod", "think" };
            }

            var list = new System.Collections.Generic.List<string>();
            foreach (var id in AiGestureWhitelist)
            {
                if (!string.IsNullOrEmpty(manifest.Resolve(id))) list.Add(id);
            }
            if (list.Count == 0)
            {
                Debug.LogWarning("[Brain] 语义表里没有可用的 AI 动作 id");
                return new string[] { "wave" };
            }

            if (VerboseLog)
                Debug.Log("[Brain] AI 动作候选（" + list.Count + " 个）: " + string.Join(", ", list.ToArray()));
            return list.ToArray();
        }

        void BuildCandidates()
        {
            // ★ 动作候选从 MixamoActionManifest（语义表）里取，**不再写死**。
            //
            //   踩坑记录：原来这里硬编码了一串 GestureLibrary 的旧 id
            //     { "wave","greet","nod","shakeHead","think",
            //       "handsOnHips","shrug","waveBye","stretch","turn" }
            //   但换成 Mixamo 动作后，语义表里只剩
            //     idle / wave / nod / shake_head / think / walk / sit …
            //   于是 greet / handsOnHips / shrug / waveBye / stretch / turn
            //   全部查不到 → PlayGesture 什么都不做 → 角色卡在半截姿势，
            //   看起来像"被吊在空中"。
            //
            //   从表里取之后，AI 只可能挑到真的存在、真的能播的动作。
            string[] gestures = GestureIdsFromManifest();
            for (int i = 0; i < gestures.Length; i++)
                _attention.Add(gestures[i], "gesture");

            // 表情候选（子集 —— 只让"可主动切换"的参与竞争）
            string[] exprs = { "smile", "neutral", "surprised", "troubled", "serious", "shy", "grin", "smug" };
            for (int i = 0; i < exprs.Length; i++)
                _attention.Add(exprs[i], "expression");
        }

        void Update()
        {
            if (Character == null) { ResolveRefs(); return; }

            // ---- 世界状态演化 ----
            World.SessionTime += Time.deltaTime;
            World.TimeSinceStimulus += Time.deltaTime;
            // ★ 只有真的互动过，才算"最近互动" —— 否则一进游戏就是 true，行为节奏失真
            World.RecentlyInteracted = World.HasEverInteracted &&
                                       World.TimeSinceStimulus < RecentInteractionWindow;
            World.Fatigue = Mathf.Clamp01(World.Fatigue + FatigueRate * Time.deltaTime);

            // 用户注意力：鼠标在窗口内视为"在关注"
            World.UserAttentive = Input.mousePresent;

            // 刺激强度自然衰减
            World.StimulusIntensity = Mathf.MoveTowards(World.StimulusIntensity, 0f, Time.deltaTime * 0.8f);

            _moodDwell += Time.deltaTime;
            _attention.Tick(Time.deltaTime);

            if (!EnableProactive) return;

            // ---- 决策节拍 ----
            _decideTimer += Time.deltaTime;
            if (_decideTimer >= DecisionInterval)
            {
                _decideTimer = 0f;
                Decide();
            }
        }

        // ============================================================
        //  决策
        // ============================================================
        void Decide()
        {
            // 0) 启动静默期：刚进游戏先安静一会儿，别一上来就疯狂做动作
            if (World.SessionTime < StartupSilence)
            {
                LastDecision = "(启动静默)";
                return;
            }

            // 1) 情绪转移（有最短停留，避免抖动）
            UpdateMood();

            // 2) 注意力评分
            _attention.Evaluate(World, CurrentMood, World.SessionTime);

            // 3) 选最高分（受最小间隔限制）
            if (World.SessionTime - _lastActionTime < MinActionGap) return;

            var pick = _attention.PickBest(MinActionScore);
            if (pick == null)
            {
                LastDecision = "(静默)";
                return;
            }

            Execute(pick);
        }

        void UpdateMood()
        {
            if (_moodDwell < MinMoodDwell) return;

            // 各情绪的倾向分
            float tired = Mathf.Clamp01(World.Fatigue * 1.4f);
            float curious = World.StimulusIntensity * (World.UserAttentive ? 1.2f : 0.6f);
            // 熟悉度只在实际互动过之后才影响心情（避免一进游戏就 Happy）
            float happy = World.HasEverInteracted
                        ? (World.Familiarity * 0.8f + (World.RecentlyInteracted ? 0.3f : 0f))
                        : 0f;
            float bored = Mathf.Clamp01(World.TimeSinceStimulus / 45f) * (1f - World.Fatigue);

            Mood best = CurrentMood;
            float bestScore = 0.35f;   // 低于这个保持原情绪

            if (tired > bestScore) { bestScore = tired; best = Mood.Tired; }
            if (curious > bestScore) { bestScore = curious; best = Mood.Curious; }
            if (happy > bestScore) { bestScore = happy; best = Mood.Happy; }
            if (bored > bestScore) { bestScore = bored; best = Mood.Bored; }

            if (best != CurrentMood)
            {
                CurrentMood = best;
                _moodDwell = 0f;
                ApplyMoodExpression();
                if (VerboseLog) Debug.Log("[Brain] 情绪 → " + CurrentMood);
            }
        }

        void ApplyMoodExpression()
        {
            if (Character == null) return;
            switch (CurrentMood)
            {
                case Mood.Happy: Character.SetExpression("smile"); break;
                case Mood.Curious: Character.SetExpression("surprised"); break;
                case Mood.Bored: Character.SetExpression("troubled"); break;
                case Mood.Tired: Character.SetExpression("serious"); break;
                default: Character.SetExpression("neutral"); break;
            }
        }

        void Execute(AttentionModel.Candidate c)
        {
            // 走路时只允许表情，不播手势（没有上下半身分层，避免抢 walk 动画）
            if (c.Kind == "gesture" && Yixuan.Motion.CharacterLocomotion.AnyWalking)
            {
                LastDecision = "walking:skip-gesture:" + c.Id;
                return;
            }
            _lastActionTime = World.SessionTime;
            // 冷却：手势要久一点，表情短一点
            float cd = (c.Kind == "gesture") ? 12f : 8f;
            _attention.MarkUsed(c.Id, cd, World.SessionTime);

            if (c.Kind == "gesture") Character.PlayGesture(c.Id);
            else Character.SetExpression(c.Id);

            LastDecision = c.Kind + ":" + c.Id + " (score=" + c.Score.ToString("F2") + ")";

            if (VerboseLog)
            {
                Debug.Log("[Brain] 主动行为 → " + LastDecision + "  情绪=" + CurrentMood +
                          "  疲劳=" + World.Fatigue.ToString("F2"));
                if (LogScores) Debug.Log("[Brain] 评分明细:\n" + _attention.DumpTop(6));
            }
        }

        // ============================================================
        //  外部接口（给 UI / AI / LLM 用）
        // ============================================================

        /// <summary>
        /// 接收外部刺激。AI 层把 LLM 的输出、语音识别结果等通过这里喂进来。
        /// 会刷新"最近互动"，降低疲劳，提高熟悉度。
        /// </summary>
        public void ReceiveStimulus(float intensity)
        {
            World.StimulusIntensity = Mathf.Clamp01(intensity);
            World.TimeSinceStimulus = 0f;
            World.RecentlyInteracted = true;
            World.HasEverInteracted = true;
            World.InteractionCount++;
            World.Fatigue = Mathf.Clamp01(World.Fatigue - FatigueReliefPerInteraction);
            World.Familiarity = Mathf.Clamp01(World.Familiarity + FamiliarityPerInteraction);

            if (VerboseLog)
                Debug.Log($"[Brain] 收到刺激 intensity={intensity:F2}  第 {World.InteractionCount} 次互动");
        }

        /// <summary>AI 直接指定情绪（越过自动转移）</summary>
        public void SetMood(Mood m)
        {
            CurrentMood = m;
            _moodDwell = 0f;
            ApplyMoodExpression();
            if (VerboseLog) Debug.Log("[Brain] AI 指定情绪 → " + m);
        }

        /// <summary>AI 直接触发一个动作（不计入注意力冷却）</summary>
        public void DoGesture(string id)
        {
            if (Character == null) return;
            if (Yixuan.Motion.CharacterLocomotion.AnyWalking) return;
            Character.PlayGesture(id);
            LastDecision = "AI:gesture:" + id;
        }

        /// <summary>AI 直接设置表情</summary>
        public void DoExpression(string id)
        {
            if (Character == null) return;
            Character.SetExpression(id);
            LastDecision = "AI:expression:" + id;
        }

        /// <summary>立即强制一次决策（不等节拍）</summary>
        public void ForceDecide()
        {
            _decideTimer = DecisionInterval;
            Decide();
        }

        public string Status()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("========== YixuanBrain ==========");
            sb.AppendLine($"  情绪={CurrentMood}  上次决策={LastDecision}");
            sb.AppendLine($"  主动触发={(EnableProactive ? "开" : "关")}  决策间隔={DecisionInterval}s  阈值={MinActionScore}");
            sb.AppendLine($"  世界: 无互动={World.TimeSinceStimulus:F1}s  疲劳={World.Fatigue:F2}  熟悉={World.Familiarity:F2}" +
                          $"  刺激={World.StimulusIntensity:F2}  互动次数={World.InteractionCount}");
            sb.AppendLine("  当前评分 Top6:");
            sb.Append(_attention.DumpTop(6));
            return sb.ToString();
        }
    }
}
