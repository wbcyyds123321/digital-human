using UnityEngine;

namespace Yixuan.Face
{
    /// <summary>
    /// 仪玄模型的 BlendShape 名称映射辅助。
    ///
    /// 【重要背景】
    /// MMD 模型的形态键是日文命名，VRM 导出后 UniVRM 会保留原始形态键名。
    /// 这里直接操作 BlendShape（index + weight），不依赖 VRM Expression API，最稳。
    ///
    /// 实测该模型共 41 个 BlendShape。
    /// 所有字段用 internal 而非 readonly —— 需要被 ref 赋值。
    /// </summary>
    public class YixuanBlendShapes
    {
        public SkinnedMeshRenderer Renderer;
        Mesh _mesh;

        // ---- 眨眼 ----
        public int Blink = -1;          // まばたき
        public int WinkL = -1;          // ウィンク
        public int WinkR = -1;          // ウィンク右

        // ---- 情绪 ----
        public int Laugh = -1;          // 笑い
        public int SmileSoft = -1;      // にこり
        public int Grin = -1;           // にやり
        public int Angry = -1;          // 怒り
        public int AngryEyes = -1;      // 怒り目
        public int Sad = -1;            // 悲しむ
        public int Troubled = -1;       // 困る
        public int Serious = -1;        // 真面目
        public int Surprised = -1;      // びっくり
        public int Glare = -1;          // じと目

        // ---- 口型（あいうえお → 口型同步）----
        public int A = -1, I = -1, U = -1, E = -1, O = -1;

        // ---- 嘴部微调 ----
        public int MouthCornerUp = -1;      // 口角上げ
        public int MouthCornerDown = -1;    // 口角下げ
        public int MouthWide = -1;          // 口横広げ
        public int MouthNarrow = -1;        // 口横狭め

        // ---- 视线 ----
        public int LookUp = -1;         // 上
        public int LookDown = -1;       // 下

        /// <summary>成功解析到的形态键数量</summary>
        public int ResolvedCount { get; private set; }

        public YixuanBlendShapes(SkinnedMeshRenderer smr)
        {
            Renderer = smr;
            _mesh = (smr != null) ? smr.sharedMesh : null;
        }

        /// <summary>
        /// 形态键查找。策略：**先精确匹配，再模糊包含**。
        ///
        /// 【为什么必须精确优先 —— 踩过的坑】
        /// 第一版直接用 Contains，结果：
        ///   "い"  模糊匹配到了 "笑い"  → I 指向错误
        ///   "怒り" 模糊匹配到了 "怒り目"（因为遍历顺序）→ Angry 和 AngryEyes 撞车
        /// 改成精确优先后，单字形态键（あ/い/う/え/お）和短词（怒り）都能正确命中。
        /// </summary>
        int Find(params string[] candidates)
        {
            if (_mesh == null) return -1;

            // 第一轮：精确匹配
            for (int c = 0; c < candidates.Length; c++)
            {
                string key = candidates[c];
                if (string.IsNullOrEmpty(key)) continue;
                for (int i = 0; i < _mesh.blendShapeCount; i++)
                {
                    if (_mesh.GetBlendShapeName(i) == key) return i;
                }
            }

            // 第二轮：模糊包含（用于兼容别的模型/别的命名）
            for (int c = 0; c < candidates.Length; c++)
            {
                string key = candidates[c];
                if (string.IsNullOrEmpty(key)) continue;
                for (int i = 0; i < _mesh.blendShapeCount; i++)
                {
                    if (_mesh.GetBlendShapeName(i).Contains(key)) return i;
                }
            }

            return -1;
        }

        void Assign(ref int field, params string[] names)
        {
            int i = Find(names);
            field = i;
            if (i >= 0) ResolvedCount++;
        }

        /// <summary>解析全部形态键。返回 this 便于链式调用。</summary>
        public YixuanBlendShapes ResolveAll()
        {
            ResolvedCount = 0;

            // 眨眼
            Assign(ref Blink,      "まばたき", "Blink");
            Assign(ref WinkL,      "ウィンク２", "ウィンク", "Blink_L");
            Assign(ref WinkR,      "ウィンク２右", "ウィンク右", "Blink_R");

            // 情绪
            Assign(ref Laugh,      "笑い", "Joy");
            Assign(ref SmileSoft,  "にこり");
            Assign(ref Grin,       "にやり２", "にやり");
            Assign(ref Angry,      "怒り", "Angry");
            Assign(ref AngryEyes,  "怒り目２", "怒り目");
            Assign(ref Sad,        "悲しむ", "Sorrow");
            Assign(ref Troubled,   "困る");
            Assign(ref Serious,    "真面目");
            Assign(ref Surprised,  "びっくり", "Surprised");
            Assign(ref Glare,      "じと目");

            // 口型
            Assign(ref A, "あ",  "aa");
            Assign(ref I, "い２", "い", "ih");
            Assign(ref U, "う",  "ou");
            Assign(ref E, "え２", "え", "ee");
            Assign(ref O, "お２", "お", "oh");

            // 嘴部
            Assign(ref MouthCornerUp,   "口角上げ");
            Assign(ref MouthCornerDown, "口角下げ");
            Assign(ref MouthWide,       "口横広げ");
            Assign(ref MouthNarrow,     "口横狭め");

            // 视线
            Assign(ref LookUp,   "上");
            Assign(ref LookDown, "下");

            return this;
        }

        /// <summary>设置权重（0~1）</summary>
        public void Set(int index, float weight01)
        {
            if (index < 0 || Renderer == null) return;
            Renderer.SetBlendShapeWeight(index, Mathf.Clamp01(weight01) * 100f);
        }

        public float Get(int index)
        {
            if (index < 0 || Renderer == null) return 0f;
            return Renderer.GetBlendShapeWeight(index) / 100f;
        }

        public string[] ListAll()
        {
            if (_mesh == null) return new string[0];
            var arr = new string[_mesh.blendShapeCount];
            for (int i = 0; i < _mesh.blendShapeCount; i++) arr[i] = _mesh.GetBlendShapeName(i);
            return arr;
        }

        /// <summary>调试摘要</summary>
        public string Dump()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"[YixuanBlendShapes] 解析成功 {ResolvedCount} 个 / 共 {(_mesh != null ? _mesh.blendShapeCount : 0)} 个");
            sb.AppendLine($"  眨眼 Blink={Blink} WinkL={WinkL} WinkR={WinkR}");
            sb.AppendLine($"  情绪 Laugh={Laugh} SmileSoft={SmileSoft} Angry={Angry} AngryEyes={AngryEyes} Surprised={Surprised} Sad={Sad}");
            sb.AppendLine($"  口型 A={A} I={I} U={U} E={E} O={O}");
            sb.AppendLine($"  嘴部 CornerUp={MouthCornerUp} CornerDown={MouthCornerDown}");
            sb.AppendLine($"  视线 Up={LookUp} Down={LookDown}");
            return sb.ToString();
        }
    }
}
