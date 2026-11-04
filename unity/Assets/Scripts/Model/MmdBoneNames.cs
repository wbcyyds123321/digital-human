// ============================================================================
//  MmdBoneNames —— 骨骼名的「唯一真相来源」
//
//  【为什么需要这个】
//  项目原来是写死仪玄骨名的（足.L / 腕.R / つま先.L ...）。
//  换成庄方宜之后骨名变成 左足D / 左腕 / 左つま先，
//  所有硬编码的地方会**静默失效**（找不到骨 → 不报错 → 功能悄悄没了）。
//
//  PROJECT_NOTES 里的铁律说得很清楚：
//      「不要把动作 id 写死在 UI 或 AI 里，一律从 MixamoActionManifest 取」
//  骨名同理 —— 集中到一张表，以后换模型只改一处。
//
//  【用法】
//      var t = MmdBoneNames.Resolve("Head", transform);     // 优先表，兜底名字扫描
//      Transform Find(string semantic)                      // 在某个根下找
// ============================================================================

using System.Collections.Generic;
using UnityEngine;

namespace Yixuan.Model
{
    public static class MmdBoneNames
    {
        /// <summary>
        /// 语义名 → 各模型下的实际骨名候选（按优先级）。
        /// 加新模型时在这里追加一列即可，其它代码不用动。
        /// </summary>
        static readonly Dictionary<string, string[]> Map = new Dictionary<string, string[]>
        {
            // ---- 躯干 ----
            { "Center",      new[]{ "センター" } },
            { "Groove",      new[]{ "グルーブ" } },
            { "Waist",       new[]{ "腰" } },
            { "LowerBody",   new[]{ "下半身" } },
            { "UpperBody",   new[]{ "上半身" } },
            { "UpperBody1",  new[]{ "上半身1" } },
            { "UpperBody2",  new[]{ "上半身2" } },
            { "Neck",        new[]{ "首" } },
            { "Head",        new[]{ "頭" } },

            // ---- 肩 / 臂（注意：仪玄是 腕.R，庄方宜是 右腕）----
            { "ShoulderL",   new[]{ "肩.L", "左肩" } },
            { "ShoulderR",   new[]{ "肩.R", "右肩" } },
            { "ArmL",        new[]{ "腕.L", "左腕" } },
            { "ArmR",        new[]{ "腕.R", "右腕" } },
            { "ElbowL",      new[]{ "ひじ.L", "左ひじ" } },
            { "ElbowR",      new[]{ "ひじ.R", "右ひじ" } },
            { "WristL",      new[]{ "手首.L", "左手首" } },
            { "WristR",      new[]{ "手首.R", "右手首" } },

            // ---- 腿：★ 一律指向「变形骨（D骨）」，因为控制骨 0 顶点权重 ----
            { "UpperLegL",   new[]{ "足D.L", "左足D" } },
            { "UpperLegR",   new[]{ "足D.R", "右足D" } },
            { "LowerLegL",   new[]{ "ひざD.L", "左ひざD" } },
            { "LowerLegR",   new[]{ "ひざD.R", "右ひざD" } },
            { "FootL",       new[]{ "足首D.L", "左足首D" } },
            { "FootR",       new[]{ "足首D.R", "右足首D" } },
            // 接地点：优先用「控制骨」的脚趾（它贴着地面），没有就用变形骨
            { "ToeL",        new[]{ "つま先.L", "左つま先", "左足先EX" } },
            { "ToeR",        new[]{ "つま先.R", "右つま先", "右足先EX" } },

            // ---- 眼 ----
            { "EyeL",        new[]{ "目.L", "左目" } },
            { "EyeR",        new[]{ "目.R", "右目" } },
        };

        /// <summary>取某语义名的候选骨名列表（给"只有骨名、还没有 Transform"的场景用）</summary>
        public static string[] Candidates(string semantic)
        {
            string[] c;
            return Map.TryGetValue(semantic, out c) ? c : new[] { semantic };
        }

        /// <summary>在 root 下按语义名找骨骼（表优先，兜底全树扫描）</summary>
        public static Transform Resolve(string semantic, Transform root)
        {
            if (root == null) return null;
            string[] cands;
            if (!Map.TryGetValue(semantic, out cands)) cands = new[] { semantic };

            // 先建一次名字索引（避免多次 GetComponentsInChildren）
            var index = BuildIndex(root);

            // ① 表里的候选名，按顺序
            for (int i = 0; i < cands.Length; i++)
            {
                Transform t;
                if (index.TryGetValue(cands[i], out t)) return t;
            }
            // ② 兜底：把语义名本身当骨名
            Transform d;
            return index.TryGetValue(semantic, out d) ? d : null;
        }

        /// <summary>批量解析，返回语义名→Transform</summary>
        public static Dictionary<string, Transform> ResolveAll(IEnumerable<string> semantics, Transform root)
        {
            var res = new Dictionary<string, Transform>();
            var index = BuildIndex(root);
            foreach (var s in semantics)
            {
                string[] cands;
                if (!Map.TryGetValue(s, out cands)) cands = new[] { s };
                Transform found = null;
                for (int i = 0; i < cands.Length && found == null; i++)
                    index.TryGetValue(cands[i], out found);
                res[s] = found;
            }
            return res;
        }

        /// <summary>名字 → Transform（重名取第一个；PMX 有重名骨，先到先得）</summary>
        public static Dictionary<string, Transform> BuildIndex(Transform root)
        {
            var index = new Dictionary<string, Transform>();
            if (root == null) return index;
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
                if (!index.ContainsKey(t.name)) index[t.name] = t;
            return index;
        }

        /// <summary>诊断：把语义名解析结果打成一张表</summary>
        public static string Report(Transform root)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("[MmdBoneNames] 语义名解析结果（root=" + (root != null ? root.name : "null") + "）");
            foreach (var kv in Map)
            {
                var t = Resolve(kv.Key, root);
                var idx = BuildIndex(root);
                string src = "-";
                if (t != null)
                {
                    for (int i = 0; i < kv.Value.Length; i++)
                        if (kv.Value[i] == t.name) { src = i == 0 ? "首选" : ("候选#" + i); break; }
                }
                sb.AppendLine(string.Format("  {0,-12} → {1,-12} {2}",
                    kv.Key, t != null ? t.name : "(未找到)", t != null ? "✔ " + src : "✘"));
            }
            return sb.ToString();
        }
    }
}
