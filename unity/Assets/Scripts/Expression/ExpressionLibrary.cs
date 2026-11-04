using UnityEngine;
using System.Collections.Generic;

namespace Yixuan.Face
{
    /// <summary>
    /// 表情库（数据驱动，15 种表情）。
    ///
    /// 【为什么用形态键名字而不是索引】
    /// 名字可读、可维护；换模型时只要名字对得上就自动适配。
    /// 索引在运行时通过 YixuanBlendShapes 解析一次并缓存。
    ///
    /// 【配方来自 Blender 侧实测 + Unity 侧验证】
    /// 关键经验：笑い 和 にこり 都含"眯眼"成分，叠加过多会把眼睛压死。
    /// 所以每个笑容配方都控制眯眼类权重，靠 口角上げ 挑大梁。
    /// </summary>
    public static class ExpressionLibrary
    {
        public class MorphKey
        {
            public string Morph;
            public float Weight;
            public MorphKey(string m, float w) { Morph = m; Weight = w; }
        }

        public class ExpressionDef
        {
            public string Id;
            public string Label;
            public MorphKey[] Morphs;
        }

        static Dictionary<string, ExpressionDef> _map;
        public static List<ExpressionDef> All { get; private set; }

        public static ExpressionDef Get(string id)
        {
            EnsureInit();
            ExpressionDef d;
            return _map.TryGetValue(id ?? "", out d) ? d : null;
        }

        public static string[] AllIds()
        {
            EnsureInit();
            var a = new string[All.Count];
            for (int i = 0; i < All.Count; i++) a[i] = All[i].Id;
            return a;
        }

        static void EnsureInit()
        {
            if (_map != null) return;
            _map = new Dictionary<string, ExpressionDef>();
            All = new List<ExpressionDef>();

            // ---- 基础 ----
            Add("neutral", "中性", new MorphKey[0]);

            // ---- 笑容系（眯眼类权重受控）----
            Add("smile", "微笑", new MorphKey[] {
                new MorphKey("口角上げ", 0.80f),
                new MorphKey("にこり",   0.35f),
                new MorphKey("にやり２", 0.30f),
            });
            Add("laugh", "大笑", new MorphKey[] {
                new MorphKey("笑い",     0.85f),
                new MorphKey("口横広げ", 0.50f),
                new MorphKey("眼角下",   0.30f),
            });
            Add("grin", "坏笑", new MorphKey[] {
                new MorphKey("にやり",   0.90f),
                new MorphKey("じと目",   0.25f),
                new MorphKey("口角上げ", 0.40f),
            });
            Add("smug", "得意", new MorphKey[] {
                new MorphKey("にやり２", 0.80f),
                new MorphKey("口角上げ", 0.55f),
                new MorphKey("上",       0.30f),
            });

            // ---- 愤怒系 ----
            Add("angry", "生气", new MorphKey[] {
                new MorphKey("怒り目",   0.85f),
                new MorphKey("口角下げ", 0.65f),
                new MorphKey("じと目",   0.45f),
                new MorphKey("真面目",   0.40f),
            });
            Add("rage", "大怒", new MorphKey[] {
                new MorphKey("怒り",     0.90f),
                new MorphKey("怒り目２", 0.85f),
                new MorphKey("口横狭め", 0.50f),
                new MorphKey("下眼上",   0.40f),
            });

            // ---- 悲伤系 ----
            Add("sad", "悲伤", new MorphKey[] {
                new MorphKey("悲しむ",   0.85f),
                new MorphKey("口角下げ", 0.45f),
                new MorphKey("眼角下",   0.40f),
                new MorphKey("下",       0.30f),
            });
            Add("troubled", "困扰", new MorphKey[] {
                new MorphKey("困る",     0.85f),
                new MorphKey("口角下げ", 0.30f),
                new MorphKey("眼角下",   0.35f),
            });

            // ---- 惊讶系 ----
            Add("surprised", "惊讶", new MorphKey[] {
                new MorphKey("びっくり", 1.00f),
                new MorphKey("お",       0.85f),
                new MorphKey("口横広げ", 0.60f),
                new MorphKey("上",       0.50f),
            });
            Add("shock", "震惊", new MorphKey[] {
                new MorphKey("びっくり左", 0.60f),
                new MorphKey("びっくり右", 0.60f),
                new MorphKey("お２",       0.90f),
                new MorphKey("上",         0.60f),
            });

            // ---- 其他 ----
            Add("serious", "认真", new MorphKey[] {
                new MorphKey("真面目",   0.90f),
                new MorphKey("口横狭め", 0.30f),
            });
            Add("shy", "害羞", new MorphKey[] {
                new MorphKey("にこり",   0.60f),
                new MorphKey("眼角下",   0.50f),
                new MorphKey("下",       0.40f),
                new MorphKey("口角上げ", 0.30f),
            });
            Add("wink", "眨眼", new MorphKey[] {
                new MorphKey("ウィンク２", 1.00f),
                new MorphKey("口角上げ",   0.50f),
            });
            Add("winkR", "右眨眼", new MorphKey[] {
                new MorphKey("ウィンク２右", 1.00f),
                new MorphKey("口角上げ",     0.50f),
            });
        }

        static void Add(string id, string label, MorphKey[] morphs)
        {
            var d = new ExpressionDef { Id = id, Label = label, Morphs = morphs };
            _map[id] = d;
            All.Add(d);
        }

        /// <summary>
        /// 注册（或覆盖）一个表情定义。
        /// PresetStore 加载磁盘预设时调用，让 UI 能立刻用到自定义表情。
        /// </summary>
        public static void Register(ExpressionDef def)
        {
            EnsureInit();
            if (def == null || string.IsNullOrEmpty(def.Id)) return;

            ExpressionDef exist;
            if (_map.TryGetValue(def.Id, out exist))
            {
                // 覆盖：保持原位置，更新内容
                for (int i = 0; i < All.Count; i++)
                {
                    if (All[i].Id == def.Id) { All[i] = def; break; }
                }
            }
            else
            {
                All.Add(def);
            }
            _map[def.Id] = def;
        }

        /// <summary>移除一个自定义表情（内置的不会被移除）</summary>
        public static bool Unregister(string id)
        {
            EnsureInit();
            if (string.IsNullOrEmpty(id)) return false;
            if (!_map.ContainsKey(id)) return false;
            _map.Remove(id);
            All.RemoveAll(delegate(ExpressionDef d) { return d.Id == id; });
            return true;
        }
    }
}
