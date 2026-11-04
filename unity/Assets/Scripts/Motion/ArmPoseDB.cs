using UnityEngine;

namespace Yixuan.Motion
{
    /// <summary>
    /// ★★★ 已弃用 —— 请勿再用来摆手臂 ★★★
    ///
    /// ============================================================
    ///  【为什么弃用】
    /// ============================================================
    /// 本表的全部角度都建立在两个前提上，而这两个前提实测都不成立：
    ///   ① 假设 OrigR/OrigL 就是模型真实 rest 值
    ///      → 实测 Prefab/场景里存的是被污染的值：
    ///         腕.R  真实 271.4,0,268.6   存成 57.9,266.4,357.4
    ///         腕.L  真实  88.6,180,268.6 存成  5.0, 91.5,181.4
    ///         肩.R  真实 284.6,0,178.7   存成  0,0,0（被清零）
    ///   ② 假设"上臂 X = 抬放轴"这类轴语义稳定
    ///      → 基准一脏，轴语义跟着变，左右臂还不对称
    /// 两者叠加 → 手臂折到胸前、穿进身体、"挥手"变成横伸。
    ///
    /// ============================================================
    ///  【现在该用什么】
    /// ============================================================
    ///   Yixuan.Motion.ArmPoseSolver —— 解析解算：
    ///     直接指定"手臂该指向哪"，用 FromToRotation 反解旋转。
    ///     与基准无关，因此不会累积误差。
    ///
    ///   本文件的 RD()/LD()/ED() 还被 GestureLibrary 的旧接口引用，
    ///   但那些返回值最终传入 ApplyBoneDelta，而它已过滤掉手臂骨 ——
    ///   所以这些数值【不再影响任何手臂姿态】。保留仅为兼容，勿新增引用。
    /// ============================================================
    /// </summary>
    public static class ArmPoseDB
    {
        // ------------------------------------------------------------
        //  PMX 原始基准（绝对起点）
        // ------------------------------------------------------------
        public static readonly Quaternion OrigR = Quaternion.Euler(271.4f, 0.8f, 267.8f);
        public static readonly Quaternion OrigL = Quaternion.Euler(88.6f, 181.6f, 270.2f);
        public static readonly Quaternion OrigE = Quaternion.Euler(0f, 180f, 356.5f);

        // ------------------------------------------------------------
        //  右臂标定表
        //  X 角 → 手臂指向 / 手位（相对上半身2）
        // ------------------------------------------------------------
        //   X    指向                    手位 d
        //  155  (-0.16, -0.99, -0.02)   (-0.098, -0.177, +0.060)   垂直下垂
        //  140  (-0.41, -0.91, -0.01)   (-0.211, -0.143, +0.064)   略外展下垂
        //   60  (-0.96, +0.24, -0.17)   (-0.458, +0.380, -0.007)   侧平举 ★挥手
        //   25  (-0.71, +0.68, -0.18)   (-0.346, +0.581, -0.011)   侧上举

        /// <summary>右臂：自然下垂（略外展，最自然）</summary>
        public const float R_Down = 148f;
        /// <summary>右臂：垂直下垂</summary>
        public const float R_DownStraight = 155f;
        /// <summary>右臂：侧平举（挥手姿态）</summary>
        public const float R_SideLevel = 60f;
        /// <summary>右臂：侧上举</summary>
        public const float R_SideUp = 25f;
        /// <summary>右臂：完全垂到身侧（最贴近身体）</summary>
        public const float R_FullDown = 168f;

        // ------------------------------------------------------------
        //  左臂标定表
        // ------------------------------------------------------------
        //   X    指向                    手位 d
        //   85  (+0.11, -0.99, +0.04)   (+0.152, -0.289, +0.085)   垂直下垂 ★
        //  100  (-0.15, -0.99, +0.05)   (+0.033, -0.287, +0.087)   垂直下垂
        //    0  (+1.00, +0.02, +0.00)   (+0.556, +0.171, +0.067)   侧平举 ★
        //  -20  (+0.93, +0.36, +0.00)   (+0.525, +0.326, +0.064)   侧上举

        /// <summary>左臂：自然下垂（最自然）</summary>
        public const float L_Down = 85f;
        /// <summary>左臂：垂直下垂（稍偏内）</summary>
        public const float L_DownStraight = 95f;
        /// <summary>左臂：侧平举</summary>
        public const float L_SideLevel = 0f;
        /// <summary>左臂：侧上举</summary>
        public const float L_SideUp = -20f;

        // ------------------------------------------------------------
        //  肘部
        // ------------------------------------------------------------
        /// <summary>肘伸直</summary>
        public const float ElbowStraight = 0f;
        /// <summary>肘微屈（自然放松）</summary>
        public const float ElbowRelaxed = -15f;
        /// <summary>肘屈 90°（小臂朝身前）</summary>
        public const float ElbowRight = -90f;
        /// <summary>肘最大屈</summary>
        public const float ElbowMax = -140f;

        // ------------------------------------------------------------
        //  便捷构造
        // ------------------------------------------------------------

        /// <summary>右臂：X 抬放，Y 扭转，Z 前后 —— 返回可直接赋给 localRotation 的四元数</summary>
        public static Quaternion RightArm(float x, float y = 0f, float z = 0f)
        {
            return OrigR * Quaternion.Euler(x, y, z);
        }

        /// <summary>左臂：X 抬放，Y 扭转，Z 前后</summary>
        public static Quaternion LeftArm(float x, float y = 0f, float z = 0f)
        {
            return OrigL * Quaternion.Euler(x, y, z);
        }

        /// <summary>肘：Z 弯曲（负 = 向身前折），Y 扭转</summary>
        public static Quaternion Elbow(float z, float y = 0f)
        {
            return OrigE * Quaternion.Euler(0f, y, z);
        }

        // ------------------------------------------------------------
        //  自检
        // ------------------------------------------------------------

        /// <summary>
        /// 校验当前手臂姿态是否"合理"。
        /// 判据：
        ///   ① 骨骼长度比 ≈ 1（没被拉伸）
        ///   ② 手臂指向不是异常方向
        /// </summary>
        public static bool ValidatePose(Transform shoulder, Transform elbow, Transform wrist,
                                        out string report)
        {
            report = "";
            if (shoulder == null || elbow == null || wrist == null)
            {
                report = "引用不完整";
                return false;
            }

            float upperLen = (elbow.position - shoulder.position).magnitude;
            float foreLen = (wrist.position - elbow.position).magnitude;
            float span = (wrist.position - shoulder.position).magnitude;
            float straightLen = upperLen + foreLen;
            float ratio = straightLen > 0.0001f ? span / straightLen : 0f;

            var dir = (wrist.position - shoulder.position).normalized;

            if (ratio > 1.02f)
            {
                report = "骨骼被拉长（比值 " + ratio.ToString("F3") + "）→ 关节异常";
                return false;
            }

            report = "比值=" + ratio.ToString("F3") + " 指向=(" +
                     dir.x.ToString("F2") + "," + dir.y.ToString("F2") + "," + dir.z.ToString("F2") + ")";
            return true;
        }

        /// <summary>输出完整标定表（供调试/文档）</summary>
        public static string DumpTable()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("========== 手臂姿态数据库（实测标定）==========");
            sb.AppendLine();
            sb.AppendLine("【右臂】基准 origR = Euler(271.4, 0.8, 267.8)");
            sb.AppendLine("   X角   手臂指向                  手位(相对上半身2)          姿态");
            sb.AppendLine("   155  (-0.16,-0.99,-0.02)      (-0.098,-0.177,+0.060)    垂直下垂");
            sb.AppendLine("   148  (-0.35,-0.93,-0.02)      约(-0.18,-0.15,+0.06)     自然下垂★");
            sb.AppendLine("    60  (-0.96,+0.24,-0.17)      (-0.458,+0.380,-0.007)    侧平举★挥手");
            sb.AppendLine("    25  (-0.71,+0.68,-0.18)      (-0.346,+0.581,-0.011)    侧上举");
            sb.AppendLine();
            sb.AppendLine("【左臂】基准 origL = Euler(88.6, 181.6, 270.2)");
            sb.AppendLine("   X角   手臂指向                  手位(相对上半身2)          姿态");
            sb.AppendLine("    85  (+0.11,-0.99,+0.04)      (+0.152,-0.289,+0.085)    垂直下垂★");
            sb.AppendLine("    95  (-0.15,-0.99,+0.05)      (+0.033,-0.287,+0.087)    垂直下垂");
            sb.AppendLine("     0  (+1.00,+0.02,+0.00)      (+0.556,+0.171,+0.067)    侧平举★");
            sb.AppendLine("   -20  (+0.93,+0.36,+0.00)      (+0.525,+0.326,+0.064)    侧上举");
            sb.AppendLine();
            sb.AppendLine("【肘】弯曲轴 = Z（负值 = 小臂向身前折）");
            sb.AppendLine("     0 = 伸直   -15 = 放松   -90 = 屈90°   -140 = 最大");
            sb.AppendLine();
            sb.AppendLine("【轴映射】");
            sb.AppendLine("   上臂 X = 抬放（主要）  Y = 纯扭转  Z = 前后摆");
            sb.AppendLine("   肘   Z = 弯曲（负=身前）  Y = 纯扭转（位移0）");
            return sb.ToString();
        }
    }
}
