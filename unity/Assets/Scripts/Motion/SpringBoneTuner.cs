using UnityEngine;

namespace Yixuan.Motion
{
    /// <summary>
    /// SpringBone（VRM 布料/头发物理）参数调整器。
    ///
    /// 【为什么必须调】
    /// 实测该模型所有 SpringBone 关节的参数是：
    ///     m_gravityPower   = 0     ← 重力为 0，布料不落重力！
    ///     m_jointRadius    = 0     ← 无体积
    ///     m_stiffnessForce = 1
    ///     m_dragForce      = 0.5
    ///
    /// gravityPower = 0 意味着布料只靠 stiffness 回弹，完全不响应重力。
    /// 手臂从 T-pose 放下后，袖子会**保持 T-pose 时的形状悬在半空**，
    /// 这就是"布料不自然"的根本原因。
    ///
    /// 【执行时机】
    /// 必须早于 UniVRM 的 Vrm10Instance 处理 SpringBone。
    /// 用 DefaultExecutionOrder(-1000) 保证在它之前跑。
    /// </summary>
    [DefaultExecutionOrder(-1000)]
    [DisallowMultipleComponent]
    public class SpringBoneTuner : MonoBehaviour
    {
        [Header("总开关")]
        public bool Enabled = true;

        [Header("袖子")]
        [Tooltip("重力强度。0 = 不落重力（原值），建议 0.15~0.30")]
        public float SleeveGravityPower = 0.22f;
        [Tooltip("关节半径，给布料一点体积避免穿模")]
        public float SleeveJointRadius = 0.012f;
        [Tooltip("刚度。越小越软")]
        public float SleeveStiffness = 0.7f;

        [Header("头发")]
        public float HairGravityPower = 0.15f;
        public float HairJointRadius = 0.008f;
        public float HairStiffness = 0.9f;

        [Header("裙子")]
        public float SkirtGravityPower = 0.18f;
        public float SkirtJointRadius = 0.010f;
        public float SkirtStiffness = 0.85f;

        [Header("其他（配件等）")]
        public float MiscGravityPower = 0.15f;
        public float MiscJointRadius = 0.008f;
        public float MiscStiffness = 0.9f;

        [Header("通用阻尼（覆盖全部）")]
        public float DragForce = 0.55f;

        [Header("调试")]
        public bool VerboseLog = true;

        public int TunedCount { get; private set; }

        void Awake()
        {
            if (Enabled) Apply();
        }

        /// <summary>按骨骼名分类并应用参数</summary>
        public void Apply()
        {
            TunedCount = 0;
            var all = GetComponentsInChildren<MonoBehaviour>(true);
            int sleeveN = 0, hairN = 0, skirtN = 0, miscN = 0;

            for (int i = 0; i < all.Length; i++)
            {
                var c = all[i];
                if (c == null) continue;
                if (!c.GetType().Name.Contains("SpringBoneJoint")) continue;

                string n = c.gameObject.name;
                float gp, jr, sf;

                if (n.Contains("袖"))
                { gp = SleeveGravityPower; jr = SleeveJointRadius; sf = SleeveStiffness; sleeveN++; }
                else if (n.Contains("髮") || n.Contains("髪") || n.Contains("Hair"))
                { gp = HairGravityPower; jr = HairJointRadius; sf = HairStiffness; hairN++; }
                else if (n.Contains("裙"))
                { gp = SkirtGravityPower; jr = SkirtJointRadius; sf = SkirtStiffness; skirtN++; }
                else
                { gp = MiscGravityPower; jr = MiscJointRadius; sf = MiscStiffness; miscN++; }

                SetField(c, "m_gravityPower", gp);
                SetField(c, "m_jointRadius", jr);
                SetField(c, "m_stiffnessForce", sf);
                SetField(c, "m_dragForce", DragForce);
                TunedCount++;
            }

            if (VerboseLog)
                Debug.Log($"[SpringBoneTuner] 已调参 {TunedCount} 个关节 —— " +
                          $"袖 {sleeveN} / 发 {hairN} / 裙 {skirtN} / 其他 {miscN}");
        }

        static void SetField(object comp, string field, float value)
        {
            var t = comp.GetType();
            var f = t.GetField(field, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            if (f != null) { f.SetValue(comp, value); return; }
            var p = t.GetProperty(field, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            if (p != null) p.SetValue(comp, value, null);
        }

        /// <summary>恢复为 VRM 原始值（gravityPower=0, jointRadius=0, stiffness=1, drag=0.5）</summary>
        public void RevertToOriginal()
        {
            var all = GetComponentsInChildren<MonoBehaviour>(true);
            for (int i = 0; i < all.Length; i++)
            {
                var c = all[i];
                if (c == null || !c.GetType().Name.Contains("SpringBoneJoint")) continue;
                SetField(c, "m_gravityPower", 0f);
                SetField(c, "m_jointRadius", 0f);
                SetField(c, "m_stiffnessForce", 1f);
                SetField(c, "m_dragForce", 0.5f);
            }
            Debug.Log("[SpringBoneTuner] 已恢复原始参数");
        }

        /// <summary>验证用摘要</summary>
        public string Status()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("[SpringBoneTuner] tuned=" + TunedCount);
            var all = GetComponentsInChildren<MonoBehaviour>(true);
            int shown = 0;
            for (int i = 0; i < all.Length; i++) {
                var c = all[i];
                if (c == null || !c.GetType().Name.Contains("SpringBoneJoint")) continue;
                string n = c.gameObject.name;
                if (shown >= 3 && !n.Contains("袖")) continue;
                if (shown >= 6) break;
                var t = c.GetType();
                var fG = t.GetField("m_gravityPower");
                var fR = t.GetField("m_jointRadius");
                var fS = t.GetField("m_stiffnessForce");
                sb.AppendLine($"    {n}: gravity={(fG != null ? fG.GetValue(c).ToString() : "?")}" +
                              $" radius={(fR != null ? fR.GetValue(c).ToString() : "?")}" +
                              $" stiffness={(fS != null ? fS.GetValue(c).ToString() : "?")}");
                shown++;
            }
            return sb.ToString();
        }
    }
}
