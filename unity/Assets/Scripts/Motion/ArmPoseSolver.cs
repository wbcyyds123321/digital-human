using UnityEngine;

namespace Yixuan.Motion
{
    /// <summary>
    /// 手臂姿态【解析解算器】—— 取代"查表猜欧拉角"的做法。
    ///
    /// ============================================================
    ///  【为什么放弃 ArmPoseDB 的查表法】
    /// ============================================================
    /// 查表法的前提是"基准姿态 + 轴语义"都正确。实测发现两个前提都崩了：
    ///   ① 场景/Prefab 里存的骨骼 rest 值本身已被污染
    ///      （腕.R 存成 57.9/266.4/357.4，而 VRM 原始值是 271.4/0/268.6）
    ///   ② ArmPoseDB 又在这个脏基准上再叠一次偏移 → 误差累积
    ///   结果：手臂折到胸前、穿进身体。
    ///
    /// ============================================================
    ///  【本解算器的原理 —— 与基准无关，因此不会累积误差】
    /// ============================================================
    /// 不去问"该转多少度"，而是直接指定【手臂在角色空间里该指向哪】：
    ///     自然下垂 → 上臂指向 ≈ (∓0.15, -0.99, 0)   （∓ = 左+/右-）
    ///
    /// 然后解出唯一需要的旋转：
    ///     Δ = FromToRotation(当前指向, 目标指向)        ← 世界空间
    ///     localRotation = (parentWorld * restLocal)⁻¹ * Δ * (parentWorld * restLocal)
    ///
    /// 因为它读的是【当前实际指向】再算差值，无论基准多脏都能一次到位。
    /// 旋转用 FromToRotation（最小旋转），不引入额外扭转。
    /// ============================================================
    /// </summary>
    public static class ArmPoseSolver
    {
        /// <summary>
        /// 把 t 的局部旋转解成"让 [t → tip] 的指向变成 dirWorld"。
        ///
        /// ★ 必须显式给出 tip（真正的下一个关节，比如肘）。
        ///   不能用"第一个子骨骼" —— MMD 骨骼在肘之前挂了
        ///   腕捩/腕捩1/腕捩2/腕捩3 一串捻转辅助骨，取第一个会量错方向。
        /// </summary>
        public static Quaternion Aim(Transform t, Transform tip, Vector3 dirWorld)
        {
            if (t == null) return Quaternion.identity;

            Vector3 current = (tip != null)
                ? (tip.position - t.position).normalized
                : ChildDirection(t);

            if (current.sqrMagnitude < 1e-8f) return t.localRotation;   // 量不到方向，原样返回

            Vector3 target = dirWorld.normalized;
            Quaternion delta = Quaternion.FromToRotation(current, target);

            Quaternion parentWorld = t.parent != null ? t.parent.rotation : Quaternion.identity;
            return Quaternion.Inverse(parentWorld) * delta * parentWorld * t.localRotation;
        }

        /// <summary>骨骼指向（到第一个有位移的子骨骼的方向，仅作兜底）</summary>
        public static Vector3 ChildDirection(Transform t)
        {
            if (t == null || t.childCount == 0) return Vector3.zero;
            Transform child = FirstUsableChild(t);
            if (child == null) return Vector3.zero;
            return (child.position - t.position).normalized;
        }

        /// <summary>取第一个有实际位移的子骨骼（跳过零长度的辅助骨）</summary>
        static Transform FirstUsableChild(Transform t)
        {
            for (int i = 0; i < t.childCount; i++)
            {
                var c = t.GetChild(i);
                if ((c.position - t.position).sqrMagnitude > 1e-8f) return c;
            }
            return null;
        }

        /// <summary>
        /// 在【上臂父骨坐标系】里指定方向，解出该手臂的局部旋转。
        ///
        /// ⚠️ 注意：本函数会把 dirInParent 转到世界空间再解算。
        ///    如果父骨的局部轴与世界轴不正交（MMD 的肩C/手腕链常见），
        ///    InverseTransformDirection/TransformDirection 会引入剪切误差。
        ///    那种情况下请直接用 SolveArm(世界空间向量)。
        /// </summary>
        public static void AimDir(Transform upperArm, Transform lowerArm, Transform hand,
                                  Vector3 dirInParent, Vector3 bendAxisInParent, float bendDegrees)
        {
            if (upperArm == null) return;
            Transform ns = upperArm.parent;
            if (ns == null) return;

            Vector3 worldDir = ns.TransformDirection(dirInParent.normalized);
            Vector3 worldAxis = ns.TransformDirection(bendAxisInParent.normalized);
            SolveArm(upperArm, lowerArm, hand, worldDir, worldAxis, bendDegrees);
        }

        /// <summary>
        /// 解出整条手臂的姿态。
        /// </summary>
        /// <param name="upperArm">上臂根（腕.R / 腕.L）</param>
        /// <param name="lowerArm">肘（ひじ.R / ひじ.L）</param>
        /// <param name="hand">手首（用于解小臂指向；可为空）</param>
        /// <param name="upperDir">上臂目标指向（世界空间）</param>
        /// <param name="bendAxisWorld">肘弯曲轴（世界空间）—— 小臂绕它旋转</param>
        /// <param name="bendDegrees">弯曲角度（决定小臂相对上臂折多少）</param>
        public static void SolveArm(Transform upperArm, Transform lowerArm, Transform hand,
                                    Vector3 upperDir, Vector3 bendAxisWorld, float bendDegrees)
        {
            if (upperArm == null) return;

            // ① 上臂指向目标（tip = 肘，跳过腕捩辅助骨）
            if (upperDir.sqrMagnitude > 1e-8f)
                upperArm.localRotation = Aim(upperArm, lowerArm, upperDir);

            // ② 肘弯曲：把弯曲旋转施加到【肘的局部空间】
            if (lowerArm != null && bendAxisWorld.sqrMagnitude > 1e-8f)
            {
                Quaternion bend = Quaternion.AngleAxis(bendDegrees, bendAxisWorld.normalized);
                Quaternion parentWorld = lowerArm.parent != null ? lowerArm.parent.rotation : Quaternion.identity;
                lowerArm.localRotation = Quaternion.Inverse(parentWorld) * bend * parentWorld * lowerArm.localRotation;
            }
        }

        /// <summary>
        /// 便捷：按"自然下垂"语义摆手臂。
        /// </summary>
        /// <param name="isRight">true = 右臂</param>
        /// <param name="characterRoot">角色根（用于把"角色空间方向"转到世界空间）</param>
        /// <param name="outward">外展角度（度）0 = 完全垂直，越大手离身体越远</param>
        /// <param name="elbowBend">肘弯曲（度，0 = 完全伸直）</param>
        public static void SolveNaturalDown(Transform upperArm, Transform lowerArm, Transform hand,
                                            Transform characterRoot, bool isRight,
                                            float outward, float elbowBend)
        {
            if (upperArm == null || characterRoot == null) return;

            // 角色空间里：+X = 角色的左手边，-X = 右手边，-Y = 下
            float side = isRight ? -1f : 1f;
            float rad = outward * Mathf.Deg2Rad;
            Vector3 localDir = new Vector3(side * Mathf.Sin(rad), -Mathf.Cos(rad), 0f);
            Vector3 worldDir = characterRoot.TransformDirection(localDir);

            // 肘弯曲轴 = 角色空间的左右轴 → 小臂绕它向后折（自然放松姿态）
            Vector3 bendAxis = characterRoot.TransformDirection(new Vector3(side, 0f, 0f));

            // 符号：右臂取正、左臂取负，使小臂都朝【身后】折（避免穿胸）
            SolveArm(upperArm, lowerArm, hand, worldDir, bendAxis, isRight ? elbowBend : -elbowBend);
        }

        /// <summary>把 rest 姿态存进 localRotation（用于复位被污染的基准）</summary>
        public static void ForceRest(Transform t, Vector3 restEuler)
        {
            if (t != null) t.localRotation = Quaternion.Euler(restEuler);
        }

        // ------------------------------------------------------------
        //  VRM 原始 rest 值（从 Assets/Models/yixuan.vrm 直接读出，权威）
        // ------------------------------------------------------------
        public static readonly Vector3 RestShoulderR = new Vector3(284.58f, 0f, 178.73f);
        public static readonly Vector3 RestShoulderL = new Vector3(75.42f, 0f, 178.73f);
        public static readonly Vector3 RestUpperArmR = new Vector3(271.36f, 0f, 268.62f);
        public static readonly Vector3 RestUpperArmL = new Vector3(88.64f, 180f, 268.62f);
        public static readonly Vector3 RestLowerArmR = new Vector3(0f, 180f, 356.54f);
        public static readonly Vector3 RestLowerArmL = new Vector3(0f, 180f, 356.54f);
        public static readonly Vector3 RestHandR = new Vector3(358.87f, 180.05f, 0.34f);
        public static readonly Vector3 RestHandL = new Vector3(1.13f, 179.95f, 0.34f);

        /// <summary>把整条手臂链复位到 VRM 原始 rest（T-pose），幂等</summary>
        public static void ResetArmToRest(Transform shoulder, Transform upperArm, Transform lowerArm, Transform hand, bool isRight)
        {
            ForceRest(shoulder, isRight ? RestShoulderR : RestShoulderL);
            ForceRest(upperArm, isRight ? RestUpperArmR : RestUpperArmL);
            ForceRest(lowerArm, isRight ? RestLowerArmR : RestLowerArmL);
            ForceRest(hand, isRight ? RestHandR : RestHandL);
        }

        /// <summary>诊断：报告一条手臂链的实际指向与肘角</summary>
        public static string Report(Transform shoulder, Transform upperArm, Transform lowerArm, Transform hand, string tag)
        {
            if (upperArm == null || lowerArm == null) return tag + " 引用不完整";
            Vector3 u = (lowerArm.position - upperArm.position).normalized;
            Vector3 span = hand != null ? (hand.position - lowerArm.position) : Vector3.zero;
            Vector3 f = span.normalized;
            float elbow = hand != null ? Vector3.Angle(upperArm.position - lowerArm.position, hand.position - lowerArm.position) : 0f;
            return tag + "  上臂指向=(" + u.x.ToString("F3") + "," + u.y.ToString("F3") + "," + u.z.ToString("F3") + ")" +
                   "  小臂指向=(" + f.x.ToString("F3") + "," + f.y.ToString("F3") + "," + f.z.ToString("F3") + ")" +
                   "  肘角=" + elbow.ToString("F1") + "°" +
                   (hand != null ? "  手Wpos=" + hand.position.ToString("F4") : "");
        }
    }
}
