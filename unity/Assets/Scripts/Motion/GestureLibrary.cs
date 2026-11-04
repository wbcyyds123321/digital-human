using UnityEngine;
using System.Collections.Generic;

namespace Yixuan.Motion
{
    /// <summary>
    /// 动作库（数据驱动）—— 全部姿态由 ArmPoseDB 的实测数据插值生成。
    ///
    /// ============================================================
    ///  【设计原则 —— 为什么这次不一样】
    /// ============================================================
    /// 之前是"手写角度 → 试 → 看数值 → 再改"，反复出错。
    ///
    /// 现在改成：
    ///   1. ArmPoseDB 提供**实测锚点**（下垂/侧平举/侧上举的确切角度+指向）
    ///   2. 本文件用 Lift01(t) 在锚点之间**插值**，不手写角度
    ///   3. JointLimits 做**角度约束**校验
    ///   4. 每个动作都做**手臂指向验证**（不是只看位置）
    ///
    /// 好处：姿态一定落在"实测过、确实合理"的区间内，不会突然拧成麻花。
    /// ============================================================
    /// </summary>
    public static class GestureLibrary
    {
        public class BoneKey
        {
            public string Bone;
            public float X, Y, Z;
            public BoneKey(string b, float x, float y, float z) { Bone = b; X = x; Y = y; Z = z; }
        }

        /// <summary>
        /// 手臂姿态（解析解算，不走欧拉增量）。
        ///
        /// 【为什么单独做一类】
        /// 旧做法用 "腕.R 的欧拉增量" 表示抬臂，前提是 ArmPoseDB 的基准与轴语义正确。
        /// 实测该前提不成立 —— 于是"挥手"从下垂再转 RD(0.55) 后手臂横伸到体侧、
        /// 袖子穿过肩膀网格。现在改成直接指定语义化角度，由 ArmPoseSolver 反解旋转。
        /// </summary>
        public class ArmPose
        {
            public bool Right;
            /// <summary>抬起度：0 = 自然下垂，90 = 侧平举，180 = 举过头顶</summary>
            public float Lift;
            /// <summary>前摆：正 = 往身前摆</summary>
            public float Forward;
            /// <summary>肘弯曲（0 = 伸直）</summary>
            public float Elbow;
            /// <summary>外展覆盖（&lt;0 = 用角色的 IdleOutward 基准）</summary>
            public float Outward = -1f;
            /// <summary>抬起度的摆动幅度（挥手用）</summary>
            public float LiftSwing;
            /// <summary>前摆的摆动幅度</summary>
            public float ForwardSwing;

            public static ArmPose R(float lift, float elbow, float forward = 0f, float outward = -1f)
            { return new ArmPose { Right = true, Lift = lift, Forward = forward, Elbow = elbow, Outward = outward }; }
            public static ArmPose L(float lift, float elbow, float forward = 0f, float outward = -1f)
            { return new ArmPose { Right = false, Lift = lift, Forward = forward, Elbow = elbow, Outward = outward }; }
        }

        public class GestureDef
        {
            public string Id;
            public string Label;
            public float Duration = 2.5f;
            public float SwingCycles = 2f;
            public BoneKey[] Hold;
            public BoneKey[] Swing;
            /// <summary>手臂姿态（解析解算）</summary>
            public ArmPose[] Arms;
        }

        /// <summary>
        /// 手臂骨骼名 —— 这些骨不走增量路径，改由 ArmPoseSolver 解算。
        ///
        /// ★ 覆盖两个模型的命名：仪玄是 `腕.R`，庄方宜是 `右腕`。
        ///   原来只有仪玄那一套，换模型后 IsArmBone 全返回 false →
        ///   手臂增量与解析解算**两套同时写骨骼**，互相覆盖 → 手臂姿态错乱。
        /// </summary>
        static readonly string[] ArmBones =
        {
            // 仪玄
            "腕.R", "腕.L", "ひじ.R", "ひじ.L",
            // 庄方宜
            "右腕", "左腕", "右ひじ", "左ひじ",
        };

        public static bool IsArmBone(string b)
        {
            if (string.IsNullOrEmpty(b)) return false;
            for (int i = 0; i < ArmBones.Length; i++) if (ArmBones[i] == b) return true;
            return false;
        }

        static Dictionary<string, GestureDef> _map;
        public static List<GestureDef> All { get; private set; }

        public static GestureDef Get(string id)
        {
            EnsureInit();
            GestureDef d;
            return _map.TryGetValue(id ?? "", out d) ? d : null;
        }

        // ============================================================
        //  ★ 姿态插值器 —— 返回【增量】（相对自然站姿）
        // ============================================================
        //
        //  【重要】ApplyBoneDelta 的语义是：
        //      最终 = 基准 × Euler(增量)
        //  所以这里必须返回 (目标绝对角 - 基准角)，不能返回绝对角！
        //
        //  早期版本的坑：直接返回绝对角 → 被当成增量叠加 →
        //               挥手变成"从下垂再转 60°"→ 手臂横穿到身体内侧
        //
        //  右臂锚点: 下垂148 / 侧平举60 / 侧上举25
        //  左臂锚点: 下垂 85 / 侧平举 0 / 侧上举-20
        // ============================================================

        /// <summary>右臂抬起度 t 对应的【增量】（0=下垂, 0.5=侧平举, 1.0=侧上举）</summary>
        public static float RD(float t)
        {
            t = Mathf.Clamp01(t);
            float abs;
            if (t <= 0.5f)
                abs = Mathf.Lerp(ArmPoseDB.R_Down, ArmPoseDB.R_SideLevel, t / 0.5f);
            else
                abs = Mathf.Lerp(ArmPoseDB.R_SideLevel, ArmPoseDB.R_SideUp, (t - 0.5f) / 0.5f);
            return abs - ArmPoseDB.R_Down;   // ★ 返回增量
        }

        /// <summary>左臂抬起度 t 对应的【增量】</summary>
        public static float LD(float t)
        {
            t = Mathf.Clamp01(t);
            float abs;
            if (t <= 0.5f)
                abs = Mathf.Lerp(ArmPoseDB.L_Down, ArmPoseDB.L_SideLevel, t / 0.5f);
            else
                abs = Mathf.Lerp(ArmPoseDB.L_SideLevel, ArmPoseDB.L_SideUp, (t - 0.5f) / 0.5f);
            return abs - ArmPoseDB.L_Down;   // ★ 返回增量
        }

        /// <summary>肘弯曲【增量】（基准已经是 ElbowRelaxed）</summary>
        public static float ED(float targetBend)
        {
            return targetBend - ArmPoseDB.ElbowRelaxed;
        }

        // 抬臂幅度语义
        const float T_DOWN = 0f;      // 自然下垂
        const float T_HALF = 0.25f;   // 半抬（手在腰胯高度）
        const float T_LEVEL = 0.5f;   // 侧平举
        const float T_HIGH = 0.75f;   // 侧上举
        const float T_TOP = 1f;       // 高举过头

        // ============================================================
        //  动作定义
        // ============================================================

        static void EnsureInit()
        {
            if (_map != null) return;
            _map = new Dictionary<string, GestureDef>();
            All = new List<GestureDef>();

            // ---------- 挥手 ----------
            Add(new GestureDef {
                Id = "wave", Label = "挥手", Duration = 3.0f, SwingCycles = 3f,
                Hold = new BoneKey[] {
                    new BoneKey("頭", 0f, 0f, -6f),
                },
                Arms = new ArmPose[] {
                    new ArmPose { Right = true, Lift = 88f, Forward = 14f, Elbow = 22f,
                                  Outward = 25f, LiftSwing = 10f, ForwardSwing = 10f },
                },
            });

            // ---------- 打招呼 ----------
            Add(new GestureDef {
                Id = "greet", Label = "打招呼", Duration = 2.2f, SwingCycles = 1f,
                Hold = new BoneKey[] {
                    new BoneKey("上半身2", 0f, 0f, -4f),
                    new BoneKey("頭", 0f, 0f, -5f),
                },
                Arms = new ArmPose[] {
                    ArmPose.R(62f, 45f, 30f, 18f),
                },
            });

            // ---------- 点头 ----------
            Add(new GestureDef {
                Id = "nod", Label = "点头", Duration = 1.8f, SwingCycles = 2f,
                Hold = new BoneKey[] {
                    new BoneKey("頭", 12f, 0f, 0f),
                    new BoneKey("首", 5f, 0f, 0f),
                },
                Swing = new BoneKey[] { new BoneKey("頭", 11f, 0f, 0f) },
            });

            // ---------- 摇头 ----------
            Add(new GestureDef {
                Id = "shakeHead", Label = "摇头", Duration = 2.0f, SwingCycles = 3f,
                Hold = new BoneKey[] { new BoneKey("首", 0f, 0f, 5f) },
                Swing = new BoneKey[] { new BoneKey("頭", 0f, 0f, 16f) },
            });

            // ---------- 鞠躬 ----------
            Add(new GestureDef {
                Id = "bow", Label = "鞠躬", Duration = 2.6f, SwingCycles = 0.5f,
                Hold = new BoneKey[] {
                    new BoneKey("上半身", 26f, 0f, 0f),
                    new BoneKey("上半身2", 16f, 0f, 0f),
                    new BoneKey("首", 10f, 0f, 0f),
                    new BoneKey("頭", 8f, 0f, 0f),
                },
                Swing = new BoneKey[] { new BoneKey("上半身", 6f, 0f, 0f) },
                // 双臂略前摆（手在身前，像欠身）
                Arms = new ArmPose[] {
                    ArmPose.R(16f, 40f, 22f, 14f),
                    ArmPose.L(16f, 40f, 22f, 14f),
                },
            });

            // ---------- 思考 ----------
            Add(new GestureDef {
                Id = "think", Label = "思考", Duration = 3.0f, SwingCycles = 1f,
                Hold = new BoneKey[] {
                    new BoneKey("頭", 6f, 0f, -10f),
                },
                Swing = new BoneKey[] { new BoneKey("頭", 0f, 0f, 5f) },
                // 右手抬到下巴附近（不是甩到脸前）
                Arms = new ArmPose[] {
                    ArmPose.R(30f, 112f, 20f, 22f),
                },
            });

            // ---------- 叉腰 ----------
            Add(new GestureDef {
                Id = "handsOnHips", Label = "叉腰", Duration = 3.0f, SwingCycles = 0.5f,
                Hold = new BoneKey[] { },
                // 手贴胯：肘向外张、手落在髋侧。
                // 注：本角色上臂+小臂=0.454，而肩→髋≈0.51，严格"叉腰"手够不到髋骨；
                //     这里用"肘外张 + 手垂到髋侧"的自然变体（akimbo），观感等价且不会穿模。
                Arms = new ArmPose[] {
                    ArmPose.R(4f, 18f, -2f, 48f),
                    ArmPose.L(4f, 18f, -2f, 48f),
                },
            });

            // ---------- 鼓掌 ----------
            Add(new GestureDef {
                Id = "clap", Label = "鼓掌", Duration = 3.0f, SwingCycles = 4f,
                Hold = new BoneKey[] { },
                Arms = new ArmPose[] {
                    new ArmPose { Right = true, Lift = 34f, Forward = 40f, Elbow = 88f, Outward = 22f, ForwardSwing = 5f },
                    new ArmPose { Right = false, Lift = 34f, Forward = 40f, Elbow = 88f, Outward = 22f, ForwardSwing = 5f },
                },
            });

            // ---------- 摊手 ----------
            Add(new GestureDef {
                Id = "shrug", Label = "摊手", Duration = 2.4f, SwingCycles = 0.5f,
                Hold = new BoneKey[] {
                    new BoneKey("頭", 4f, 0f, 0f),
                },
                Arms = new ArmPose[] {
                    ArmPose.R(42f, 58f, 26f, 34f),
                    ArmPose.L(42f, 58f, 26f, 34f),
                },
            });

            // ---------- 抱胸 ----------
            Add(new GestureDef {
                Id = "crossArms", Label = "抱胸", Duration = 3.0f, SwingCycles = 0.5f,
                Hold = new BoneKey[] { },
                // 双手在胸前交叉：中抬 + 大屈肘 + 前摆
                Arms = new ArmPose[] {
                    ArmPose.R(30f, 108f, 34f, 26f),
                    ArmPose.L(30f, 108f, 34f, 26f),
                },
            });

            // ---------- 告别 ----------
            Add(new GestureDef {
                Id = "waveBye", Label = "告别", Duration = 2.6f, SwingCycles = 3f,
                Hold = new BoneKey[] {
                    new BoneKey("頭", 0f, 0f, -4f),
                },
                Arms = new ArmPose[] {
                    new ArmPose { Right = true, Lift = 95f, Forward = 10f, Elbow = 26f,
                                  Outward = 26f, LiftSwing = 8f, ForwardSwing = 8f },
                },
            });

            // ---------- 伸懒腰 ----------
            Add(new GestureDef {
                Id = "stretch", Label = "伸懒腰", Duration = 3.2f, SwingCycles = 0.5f,
                Hold = new BoneKey[] {
                    new BoneKey("上半身2", -10f, 0f, 0f),
                    new BoneKey("頭", 14f, 0f, 0f),
                },
                // 双臂高举外张（Y 形）。lift=150 时手过顶且左右精确对称。
                // ★ lift 别超过 150：再高肘会朝内收，又往中线靠（实测 165 已开始不齐）。
                Arms = new ArmPose[] {
                    ArmPose.R(150f, 0f, 8f, 26f),
                    ArmPose.L(150f, 0f, 8f, 26f),
                },
            });

            // ---------- 转身 ----------
            Add(new GestureDef {
                Id = "turn", Label = "转身", Duration = 3.0f, SwingCycles = 0.5f,
                Hold = new BoneKey[] { new BoneKey("頭", 0f, 0f, 20f) },
                Swing = new BoneKey[] {
                    new BoneKey("センター", 0f, 0f, 40f),
                    new BoneKey("頭", 0f, 0f, 15f),
                },
            });

            // ---------- 挥左手 ----------
            Add(new GestureDef {
                Id = "waveL", Label = "挥左手", Duration = 3.0f, SwingCycles = 3f,
                Hold = new BoneKey[] {
                    new BoneKey("頭", 0f, 0f, 6f),
                },
                Arms = new ArmPose[] {
                    new ArmPose { Right = false, Lift = 88f, Forward = 12f, Elbow = 22f,
                                  Outward = 25f, LiftSwing = 10f, ForwardSwing = 10f },
                },
            });
        }

        static void Add(GestureDef d)
        {
            _map[d.Id] = d;
            All.Add(d);
        }

        public static string[] AllIds()
        {
            EnsureInit();
            var arr = new string[All.Count];
            for (int i = 0; i < All.Count; i++) arr[i] = All[i].Id;
            return arr;
        }

        public static string[] AllLabels()
        {
            EnsureInit();
            var arr = new string[All.Count];
            for (int i = 0; i < All.Count; i++) arr[i] = All[i].Label;
            return arr;
        }
    }
}
