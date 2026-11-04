using UnityEngine;

namespace Yixuan.Motion
{
    /// <summary>
    /// 关节活动范围（ROM）约束 + 姿态合法性校验。
    ///
    /// ============================================================
    ///  【为什么需要这个】
    /// ============================================================
    /// 之前设计动作是"试参数 → 看数值 → 再试"，没有规则判断什么算扭曲，
    /// 所以反复出现"手翻到背后""手臂穿过胸口"这类离谱错误，
    /// 每次都要等渲出图才发现。
    ///
    /// 本文件把「人体能做到什么」写成硬约束：
    ///   - 动作设计时：Clamp() 自动把超范围的角度拉回来
    ///   - 校验时    ：Validate() 报告哪些动作违规，不用等渲图
    ///
    /// ============================================================
    ///  【标定依据】
    /// ============================================================
    /// 实测（《仪玄骨骼坐标系手册》）：
    ///   肘 ひじ.R/L 绕 Y → 子骨骼偏离 0°（纯扭转轴）
    ///   肘 ひじ.R/L 绕 X/Z → 线性弯曲轴
    ///   上臂 腕.R/L 绕 Y → 偏离仅 1~3°（纯扭转轴）
    ///   上臂 腕.R/L 绕 X/Z → 线性弯曲轴
    ///
    /// 所有旋转的骨骼长度比恒为 1.000 → 骨骼刚性，问题在角度用错轴。
    /// </summary>
    public static class JointLimits
    {
        // ============================================================
        //  肘部（ひじ）—— 单轴铰链
        // ============================================================

        /// <summary>肘弯曲轴 = Z。面向：负值 = 小臂向【身前】折</summary>
        public const float ElbowBendMin = -150f;   // 最大屈肘（手能碰到肩）
        public const float ElbowBendMax = 0f;      // 完全伸直

        /// <summary>肘的"侧摆"容差 —— 人的肘不能侧向弯，只允许极小量</summary>
        public const float ElbowSideMax = 20f;

        /// <summary>肘部扭转容差（人前臂能旋前旋后，但肘本身不该扭）</summary>
        public const float ElbowTwistMax = 15f;

        // ============================================================
        //  上臂（腕）—— 球窝关节
        // ============================================================

        /// <summary>上臂抬放轴 = X。右臂正 = 外侧抬起（左臂相反）</summary>
        public const float ArmLiftMin = -120f;
        public const float ArmLiftMax = 150f;

        /// <summary>上臂前后摆轴 = Z</summary>
        public const float ArmSwingMin = -110f;
        public const float ArmSwingMax = 110f;

        /// <summary>上臂扭转（应该用 腕捩 骨骼，上臂自身别扭太多）</summary>
        public const float ArmTwistMax = 90f;

        // ============================================================
        //  肩部
        // ============================================================
        public const float ShoulderMax = 25f;

        // ============================================================
        //  校验
        // ============================================================

        public enum Verdict { Ok, Clamped, Invalid }

        public class Issue
        {
            public string Gesture;
            public string Bone;
            public string Axis;
            public float Value;
            public float Clamped;
            public Verdict Level;
            public string Reason;

            public override string ToString()
            {
                return "[" + Level + "] " + Gesture + " / " + Bone + " / " + Axis +
                       " = " + Value.ToString("F0") + "° → " + Clamped.ToString("F0") + "°   (" + Reason + ")";
            }
        }

        /// <summary>把单个角度夹到合法区间</summary>
        public static float Clamp(float v, float min, float max)
        {
            return Mathf.Clamp(v, min, max);
        }

        /// <summary>
        /// 校验一个骨骼角度是否合法。
        /// 返回 true = 合法；false = 会被夹紧（越界）
        /// </summary>
        public static bool ValidateBone(string boneName, float x, float y, float z, out string reason)
        {
            reason = "";
            bool isElbow = boneName != null && (boneName.StartsWith("ひじ"));
            bool isUpperArm = boneName != null && (boneName == "腕.R" || boneName == "腕.L");
            bool isShoulder = boneName != null && (boneName.StartsWith("肩"));

            if (isElbow)
            {
                if (z > ElbowBendMax || z < ElbowBendMin)
                {
                    reason = "肘弯曲超范围（合法 " + ElbowBendMin + "~" + ElbowBendMax + "）";
                    return false;
                }
                if (Mathf.Abs(x) > ElbowSideMax)
                {
                    reason = "肘不能侧摆（|X| ≤ " + ElbowSideMax + "）";
                    return false;
                }
                if (Mathf.Abs(y) > ElbowTwistMax)
                {
                    reason = "肘不能自扭（|Y| ≤ " + ElbowTwistMax + "，扭转请用手捩）";
                    return false;
                }
            }
            else if (isUpperArm)
            {
                if (x < ArmLiftMin || x > ArmLiftMax)
                {
                    reason = "上臂抬放超范围（合法 " + ArmLiftMin + "~" + ArmLiftMax + "）";
                    return false;
                }
                if (z < ArmSwingMin || z > ArmSwingMax)
                {
                    reason = "上臂前后摆超范围（合法 " + ArmSwingMin + "~" + ArmSwingMax + "）";
                    return false;
                }
                if (Mathf.Abs(y) > ArmTwistMax)
                {
                    reason = "上臂扭转过多（|Y| ≤ " + ArmTwistMax + "，扭转请用腕捩）";
                    return false;
                }
            }
            else if (isShoulder)
            {
                if (Mathf.Abs(x) > ShoulderMax || Mathf.Abs(z) > ShoulderMax)
                {
                    reason = "肩部幅度过大（各轴 ≤ " + ShoulderMax + "）";
                    return false;
                }
            }
            return true;
        }

        /// <summary>夹紧一个骨骼角度，返回修正后的值</summary>
        public static Vector3 ClampBone(string boneName, float x, float y, float z)
        {
            bool isElbow = boneName != null && boneName.StartsWith("ひじ");
            bool isUpperArm = boneName != null && (boneName == "腕.R" || boneName == "腕.L");
            bool isShoulder = boneName != null && boneName.StartsWith("肩");

            if (isElbow)
            {
                return new Vector3(
                    Mathf.Clamp(x, -ElbowSideMax, ElbowSideMax),
                    Mathf.Clamp(y, -ElbowTwistMax, ElbowTwistMax),
                    Mathf.Clamp(z, ElbowBendMin, ElbowBendMax));
            }
            if (isUpperArm)
            {
                return new Vector3(
                    Mathf.Clamp(x, ArmLiftMin, ArmLiftMax),
                    Mathf.Clamp(y, -ArmTwistMax, ArmTwistMax),
                    Mathf.Clamp(z, ArmSwingMin, ArmSwingMax));
            }
            if (isShoulder)
            {
                return new Vector3(
                    Mathf.Clamp(x, -ShoulderMax, ShoulderMax),
                    y,
                    Mathf.Clamp(z, -ShoulderMax, ShoulderMax));
            }
            return new Vector3(x, y, z);
        }

        /// <summary>校验整个动作库，返回文本报告</summary>
        public static string ValidateLibrary()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("========== 动作库合法性校验 ==========");
            sb.AppendLine("规则来源：实测 ROM（见《仪玄骨骼坐标系手册》）");
            sb.AppendLine();

            // ★ 必须先触发库初始化 —— All 在 EnsureInit 之前是 null
            GestureLibrary.AllIds();

            int totalBones = 0, badBones = 0;
            var all = GestureLibrary.All;
            if (all == null) { sb.AppendLine("❌ 动作库为空"); return sb.ToString(); }

            for (int i = 0; i < all.Count; i++)
            {
                var g = all[i];
                var problems = new System.Collections.Generic.List<string>();

                CheckKeys(g, g.Hold, "Hold", problems, ref totalBones, ref badBones);
                CheckKeys(g, g.Swing, "Swing", problems, ref totalBones, ref badBones);

                if (problems.Count > 0)
                {
                    sb.AppendLine("❌ " + g.Label + "(" + g.Id + ")");
                    for (int k = 0; k < problems.Count; k++) sb.AppendLine("      " + problems[k]);
                }
                else
                {
                    sb.AppendLine("✅ " + g.Label + "(" + g.Id + ")");
                }
            }

            sb.AppendLine();
            sb.AppendLine("骨骼角度总数 = " + totalBones + "，违规 = " + badBones);
            return sb.ToString();
        }

        static void CheckKeys(GestureLibrary.GestureDef g, GestureLibrary.BoneKey[] keys,
                              string phase, System.Collections.Generic.List<string> problems,
                              ref int total, ref int bad)
        {
            if (keys == null) return;
            for (int i = 0; i < keys.Length; i++)
            {
                var k = keys[i];
                if (k == null || string.IsNullOrEmpty(k.Bone)) continue;
                total++;
                string reason;
                if (!ValidateBone(k.Bone, k.X, k.Y, k.Z, out reason))
                {
                    bad++;
                    problems.Add(phase + " " + k.Bone + " (" +
                                 k.X.ToString("F0") + ", " + k.Y.ToString("F0") + ", " + k.Z.ToString("F0") + ")  → " + reason);
                }
            }
        }

        // ============================================================
        //  ★ 位置约束 —— 抓"手横穿身体"这类角度合法但位置错误的姿态
        // ============================================================
        //
        //  【为什么角度约束不够】
        //  挥手曾经出现"右臂横穿胸口"：角度 (-100, 0, -40) 完全在 ROM 内，
        //  但它把手臂往【身体内侧】转了 —— 角度合法，位置错误。
        //  所以必须再检查手的【世界位置】。

        /// <summary>手相对身体中轴的位置区域</summary>
        public enum Side { Any, RightOuter, LeftOuter, Center }

        public class PoseIssue
        {
            public string Gesture;
            public string What;
            public Verdict Level;
            public string Detail;
            public override string ToString()
            {
                return "[" + Level + "] " + Gesture + " / " + What + "   " + Detail;
            }
        }

        /// <summary>
        /// 检查手的位置是否合理。
        /// 依据：角色面朝 -Z，故 身前 = dZ &lt; 0。
        /// </summary>
        /// <param name="side">期望手在哪一侧</param>
        /// <param name="needFront">是否必须手在身前（dZ &lt; -0.05）</param>
        public static bool ValidateHandPosition(string gestureId, string hand,
            Vector3 dLocal, Side side, bool needFront, System.Collections.Generic.List<PoseIssue> issues)
        {
            bool ok = true;

            // ---- ① 不能穿过身体中线 ----
            //  右手应该在 dX <= 0 一侧（角色面朝 -Z，右手在 -X 侧）
            //  左边界给小容差：抱胸时双手在胸前交叉，允许略微过线
            const float CrossTolerance = 0.12f;
            if (side == Side.RightOuter && dLocal.x > CrossTolerance)
            {
                ok = false;
                issues.Add(new PoseIssue {
                    Gesture = gestureId, What = hand + "位置",
                    Level = Verdict.Invalid,
                    Detail = "右手跑到身体左侧 dX=" + dLocal.x.ToString("F3") + "（应 ≤ +0.12）→ 横穿身体"
                });
            }
            if (side == Side.LeftOuter && dLocal.x < -CrossTolerance)
            {
                ok = false;
                issues.Add(new PoseIssue {
                    Gesture = gestureId, What = hand + "位置",
                    Level = Verdict.Invalid,
                    Detail = "左手跑到身体右侧 dX=" + dLocal.x.ToString("F3") + "（应 ≥ -0.12）→ 横穿身体"
                });
            }

            // ---- ② 挥手类必须在身体外侧 ----
            if (side == Side.RightOuter && gestureId != null &&
                (gestureId == "wave" || gestureId == "waveBye"))
            {
                if (dLocal.x > -0.30f)
                {
                    ok = false;
                    issues.Add(new PoseIssue {
                        Gesture = gestureId, What = hand + "外侧性",
                        Level = Verdict.Invalid,
                        Detail = "挥手手应在身体外侧 dX < -0.30，实际 " + dLocal.x.ToString("F3") + " → 会穿过胸口"
                    });
                }
            }
            if (side == Side.LeftOuter && gestureId != null &&
                (gestureId == "wave" || gestureId == "waveBye"))
            {
                if (dLocal.x < 0.30f)
                {
                    ok = false;
                    issues.Add(new PoseIssue {
                        Gesture = gestureId, What = hand + "外侧性",
                        Level = Verdict.Invalid,
                        Detail = "挥手手应在身体外侧 dX > 0.30，实际 " + dLocal.x.ToString("F3")
                    });
                }
            }

            // ---- ③ 不能太靠身后 ----
            if (dLocal.z > 0.28f)
            {
                ok = false;
                issues.Add(new PoseIssue {
                    Gesture = gestureId, What = hand + "前后",
                    Level = Verdict.Invalid,
                    Detail = "手太靠身后 dZ=" + dLocal.z.ToString("F3") + "（应 ≤ +0.28）"
                });
            }

            // ---- ④ 需要身前时，必须在身前 ----
            if (needFront && dLocal.z > -0.05f)
            {
                ok = false;
                issues.Add(new PoseIssue {
                    Gesture = gestureId, What = hand + "身前性",
                    Level = Verdict.Invalid,
                    Detail = "要求手在身前，实际 dZ=" + dLocal.z.ToString("F3") + "（应 < -0.05）"
                });
            }

            return ok;
        }

        /// <summary>动作的手部位置约束表</summary>
        public static void GetHandConstraint(string id, out Side rightSide, out Side leftSide,
                                              out bool rightFront, out bool leftFront)
        {
            // 默认：手在各自外侧，不强制身前
            rightSide = Side.RightOuter;  leftSide = Side.LeftOuter;
            rightFront = false;           leftFront = false;

            switch (id)
            {
                // 单手动作用右手，左手保持自然（不做外侧约束）
                case "wave": case "waveBye":
                    rightSide = Side.RightOuter; rightFront = false;
                    leftSide = Side.Any; break;
                case "greet": case "think":
                    rightSide = Side.RightOuter;
                    leftSide = Side.Any; break;

                // 双手胸前动作：允许过中线（交叉）
                case "clap": case "crossArms":
                    rightSide = Side.Any; leftSide = Side.Any;
                    rightFront = true; leftFront = true; break;

                case "shrug": case "handsOnHips": case "stretch":
                    rightSide = Side.Any; leftSide = Side.Any; break;

                // 身体动作：手不动
                case "nod": case "shakeHead": case "bow": case "turn":
                    rightSide = Side.Any; leftSide = Side.Any; break;
            }
        }
    }
}
