#if UNITY_EDITOR
// ============================================================================
//  ArmClearanceFix —— 运行时「手臂离体」微调（不碰骨架 / Avatar / clip）
//
//  【为什么需要】
//  实测确认（PROJECT_NOTES 13.11 ③f）：这个模型**绑定姿态下**肘/腕就已在脊柱平面后
//  约 9~10cm，手到脊柱距离 0.13，而躯干半宽 0.10~0.13 —— **刚好贴边**。
//  前臂有小臂甲、身侧有裙摆飘带，所以贴边时几何体真的相交。
//
//  ⇒ 这是**模型自带的姿态特征**，不是重定向错误。
//    骨骼位置本身是合理的，所以**不应该再动骨架**（前两轮动骨架都改坏了）。
//
//  【做法】
//  在 LateUpdate（动画之后）给两根上臂各加一个**很小的**外展旋转，
//  让手臂离开躯干几厘米。参数是单一张角度，可关、可调、可回退。
//
//  【为什么必须从很小的角度开始】
//  前两轮教训：37° → 手穿胸口；20° → 手臂像树枝。
//  这次默认只给 4°，并用 `ArmTwistSweep` 那套相位锁定测量验证。
// ============================================================================

using UnityEngine;

namespace Yixuan.Motion
{
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(9000)]   // 在动画之后、GroundSnap(10000) 之前
    public class ArmClearanceFix : MonoBehaviour
    {
        [Header("开关")]
        public bool Enabled = false;

        [Tooltip("外展角度（度）。★ 2~8 度，别更大（20/37 度都改坏过）")]
        [Range(0f, 16f)] public float AbductDegrees = 8f;

        [Tooltip("同时稍微前旋（度），让手臂离开身后的裙摆")]
        [Range(0f, 16f)] public float ForwardDegrees = 0f;

        [Header("★ 按动作豁免")]
        [Tooltip("这些 clip 本来就要求两手交错/靠拢，加外展反而会破坏它们")]
        public string[] ExemptClips =
        {
            "Standing Clap",        // 拍手：手要合
            "Sitting Idle",         // 坐姿：手放腿上
            "Sitting And Pointing", // 坐姿指点
            "Waving",               // 双手挥：手在身前
            "Jogging",              // 跑步：手臂前后摆、贴身
            "Nervously Look Around" // 手在身前
        };

        [Header("调试")]
        public bool VerboseLog = false;

        Transform _armL, _armR;
        Yixuan.Motion.YixuanAnimationPlayer _player;

        void Awake() { Relink(); }

        public void Relink()
        {
            _armL = Yixuan.Model.MmdBoneNames.Resolve("ArmL", transform);
            _armR = Yixuan.Model.MmdBoneNames.Resolve("ArmR", transform);
            if (_player == null) _player = GetComponent<Yixuan.Motion.YixuanAnimationPlayer>();
            if (VerboseLog)
                Debug.Log("[ArmClearanceFix] 左腕=" + (_armL != null ? _armL.name : "✘")
                    + "  右腕=" + (_armR != null ? _armR.name : "✘"));
        }

        /// <summary>当前动作是否在豁免名单里</summary>
        bool IsExempt()
        {
            if (_player == null) _player = GetComponent<Yixuan.Motion.YixuanAnimationPlayer>();
            if (_player == null) return false;
            string cur = _player.CurrentName;
            if (string.IsNullOrEmpty(cur) || ExemptClips == null) return false;
            for (int i = 0; i < ExemptClips.Length; i++)
                if (!string.IsNullOrEmpty(ExemptClips[i]) && cur == ExemptClips[i]) return true;
            return false;
        }

        void LateUpdate()
        {
            if (!Enabled) return;
            if (_armL == null || _armR == null) Relink();
            if (_armL == null || _armR == null) return;

            // ★ 按动作豁免：拍手/坐姿/双手挥这类**本来就要求两手交叠**的动作，
            //   加外展反而会把它们破坏掉（实测 15 个动作里有 6 个间距变小）。
            if (IsExempt()) return;

            // 绕「角色前后轴」外展：左臂 +，右臂 −（对称张开）
            Apply(_armL, +AbductDegrees);
            Apply(_armR, -AbductDegrees);
        }

        void Apply(Transform arm, float abduct)
        {
            if (arm == null) return;
            // 在**世界空间**做增量旋转，再写回 local ——
            // 这样不受骨骼自身绑定朝向的影响（避免"骨轴约定"那类坑）
            //
            // ★★ 符号踩过坑：绕 `Vector3.forward` 做**正**旋转，
            //    会把 +X 转向 −Y（向下）—— 那是**内收**，不是外展！
            //    实测：外展 8° 让左手 x 从 −0.018 变成 +0.041（跑到身体右侧），
            //    角度越大手越往中线靠、交叉得越厉害。
            //    所以这里取负号。
            Quaternion delta = Quaternion.identity;
            if (Mathf.Abs(abduct) > 0.001f)
                delta = Quaternion.AngleAxis(-abduct, transform.forward) * delta;
            if (Mathf.Abs(ForwardDegrees) > 0.001f)
                delta = Quaternion.AngleAxis(ForwardDegrees, transform.right) * delta;

            Quaternion parentWorld = arm.parent != null ? arm.parent.rotation : Quaternion.identity;
            Quaternion wantWorld = delta * arm.rotation;
            arm.localRotation = Quaternion.Inverse(parentWorld) * wantWorld;
        }

        public string Status()
        {
            return string.Format("[ArmClearanceFix] {0}  外展={1:F1}°  前旋={2:F1}°  左腕={3} 右腕={4}",
                Enabled ? "开" : "关", AbductDegrees, ForwardDegrees,
                _armL != null ? _armL.name : "✘", _armR != null ? _armR.name : "✘");
        }
    }
}
#endif
