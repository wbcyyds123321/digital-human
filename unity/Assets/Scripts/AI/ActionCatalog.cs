using System.Collections.Generic;
using System.Text;
using UnityEngine;
using Yixuan.Face;
using Yixuan.Motion;

namespace Yixuan.AI
{
    /// <summary>
    /// 一个可用动作的只读快照（数据来自场景里的 MixamoActionManifest.Entry）。
    ///
    /// 注意：这是**快照**而不是引用 —— 清单 Refresh() 之后旧对象不会自动更新，
    /// 需要重新从 ActionCatalog.Gestures 取。
    /// </summary>
    public class GestureInfo
    {
        public string Id;           // 语义 id：idle / wave / nod …
        public string Label;        // 中文名：挥手
        public string ClipName;     // 实际 AnimationClip 名
        public float Duration;      // 动作时长（秒），0 = 未知
        public string Posture;      // 所需姿态：stand / sit / kneel
        public string BodyPart;     // 身体部位：full（全身）/ upper（上半身）
        public string EmotionTags;  // 情绪标签，逗号分隔：happy,friendly
        public bool Loop;           // 是否适合循环（持续状态）
        public bool Interruptible;  // 播放中能否被其他动作打断
        public string Note;         // 备注（可为空）

        public override string ToString()
        {
            return (Id ?? "") + "/" + (Label ?? "");
        }
    }

    /// <summary>一个可用表情的只读快照（数据来自 ExpressionLibrary.All）</summary>
    public class EmotionInfo
    {
        public string Id;     // ExpressionLibrary id：neutral / smile …
        public string Label;  // 中文名：微笑

        public override string ToString()
        {
            return (Id ?? "") + "/" + (Label ?? "");
        }
    }

    /// <summary>
    /// 动作/表情清单 —— prompt 注入、id 校验、离线规则脑共用的一张"运行时汇总表"。
    ///
    /// 【铁律：绝不写死 id】
    ///   - gesture 一律从场景里的 MixamoActionManifest（Object.FindObjectOfType）现读；
    ///   - emotion 一律从 ExpressionLibrary.All 现读。
    /// 加了新动作/新表情，AI 自动就能用，不用改这里，更不用改 prompt。
    ///
    /// 【为什么是静态 + Lazy】
    /// ChatSession / RuleBrain / ResponseParser / Director 都要查清单，
    /// 每帧 new 一遍没必要。第一次访问时自动 Refresh()，之后命中缓存；
    /// 场景换了或编辑模式下重跑菜单后可手动 Refresh() 重建。
    ///
    /// 【编辑模式】
    /// Object.FindObjectOfType 在编辑模式下也能找到场景物体，所以编辑器菜单里也能直接用
    /// （运行时文件，不 using UnityEditor）。
    ///
    /// 【manifest 缺失时】
    /// Gestures / Emotions 为空、Ready = false、所有 id 校验返回 false，
    /// 只 Debug.LogWarning **一次**，绝不抛异常 —— AI 退化成"只会说话不会动"。
    /// </summary>
    public static class ActionCatalog
    {
        static readonly List<GestureInfo> _gestures = new List<GestureInfo>();
        static readonly List<EmotionInfo> _emotions = new List<EmotionInfo>();

        /// <summary>固定姿态枚举（T2 只有站/坐/跪；动作的 Posture 字段必须是其中之一）</summary>
        static readonly string[] _postures = new string[] { "stand", "sit", "kneel" };

        static bool _initialized;
        static bool _warnedNoManifest;
        static bool _warnedEmptyActions;
        static bool _warnedEmptyEmotions;
        static float _lastRefreshTime = -999f;

        // ============================================================
        //  生命周期
        // ============================================================

        /// <summary>
        /// 重新扫描场景里的 MixamoActionManifest + ExpressionLibrary，重建两张清单。
        /// 任何时候都可以调（编辑模式 / Play 模式均可）；不会抛异常。
        /// </summary>
        public static void Refresh()
        {
            _initialized = true;
            _lastRefreshTime = Time.realtimeSinceStartup;
            _gestures.Clear();
            _emotions.Clear();

            MixamoActionManifest manifest = null;
            try
            {
                manifest = Object.FindObjectOfType<MixamoActionManifest>();
            }
            catch (System.Exception e)
            {
                Debug.LogWarning("[ActionCatalog] 查找 MixamoActionManifest 异常：" + e.Message);
            }

            if (manifest == null)
            {
                // 只警告一次，避免每轮对话刷屏
                if (!_warnedNoManifest)
                {
                    _warnedNoManifest = true;
                    Debug.LogWarning("[ActionCatalog] 场景里找不到 MixamoActionManifest —— " +
                                     "动作/表情清单为空（AI 只能说、不能做动作）。" +
                                     "请先跑菜单 UnityLITF/仪玄/② 装配 Mixamo 动作。");
                }
                return;
            }
            _warnedNoManifest = false;

            BuildGestures(manifest);
            BuildEmotions();
        }

        /// <summary>第一次访问时自动 Refresh；清单为空时按 1 秒节流重试（场景可能还没加载完）</summary>
        static void EnsureInit()
        {
            if (!_initialized)
            {
                Refresh();
                return;
            }

            // 已经有完整清单 → 直接命中缓存
            if (_gestures.Count > 0 && _emotions.Count > 0) return;

            // 空清单：可能是访问发生在场景物体就绪之前。
            // 限速重试，避免每次调用都 FindObjectOfType（RuleBrain 一次要查很多次）。
            float now = Time.realtimeSinceStartup;
            if (now - _lastRefreshTime < 1f) return;
            Refresh();
        }

        static void BuildGestures(MixamoActionManifest manifest)
        {
            var actions = manifest.Actions;
            if (actions != null)
            {
                for (int i = 0; i < actions.Length; i++)
                {
                    var e = actions[i];
                    if (e == null || string.IsNullOrEmpty(e.Id)) continue;
                    if (FindGestureInternal(e.Id) != null) continue; // 重复 id 只保留第一个
                    _gestures.Add(ToGestureInfo(e));
                }
            }

            if (_gestures.Count == 0)
            {
                // 只警告一次（EnsureInit 会限速重试，重复警告会刷屏）
                if (!_warnedEmptyActions)
                {
                    _warnedEmptyActions = true;
                    Debug.LogWarning("[ActionCatalog] MixamoActionManifest.Actions 为空 —— " +
                                     "动作清单为空，AI 不会做动作。请跑菜单 UnityLITF/仪玄/⑧ 补齐元数据。");
                }
            }
            else
            {
                _warnedEmptyActions = false;
            }
        }

        /// <summary>
        /// manifest 条目 → 只读快照。
        ///
        /// 【元数据兜底】manifest 里的 Label/Posture/... 是编辑器菜单写进去的序列化数据，
        /// 旧场景可能还没填。这里**只补空字段**（去向是人工标注表 ActionMetadata），
        /// 绝不会用 ActionMetadata 来枚举 id —— id 永远以 manifest 为准。
        /// </summary>
        static GestureInfo ToGestureInfo(MixamoActionManifest.Entry e)
        {
            var meta = ActionMetadata.Get(e.Id);
            bool filled = !string.IsNullOrEmpty(e.Label); // 有中文名 = 元数据已填过

            string label = filled
                ? e.Label
                : (meta != null && !string.IsNullOrEmpty(meta.Label) ? meta.Label : e.Id);
            string posture = filled ? e.Posture : (meta != null ? meta.Posture : null);
            string bodyPart = filled ? e.BodyPart : (meta != null ? meta.BodyPart : null);
            string tags = filled ? e.EmotionTags : (meta != null ? meta.EmotionTags : null);
            bool loop = filled ? e.Loop : (meta != null && meta.Loop);
            bool interruptible = filled ? e.Interruptible : (meta != null && meta.Interruptible);
            string note = filled ? e.Note : (meta != null ? meta.Note : null);

            return new GestureInfo
            {
                Id = e.Id,
                ClipName = e.ClipName != null ? e.ClipName : "",
                Label = label != null ? label : e.Id,
                Duration = e.Duration,
                Posture = posture != null ? posture : "",
                BodyPart = bodyPart != null ? bodyPart : "",
                EmotionTags = tags != null ? tags : "",
                Loop = loop,
                Interruptible = interruptible,
                Note = note != null ? note : "",
            };
        }

        static void BuildEmotions()
        {
            // ExpressionLibrary.All 是懒加载的：没人调用过 Get/AllIds 时它还是 null。
            // 这里借 AllIds() 触发它内部初始化，再读 All —— 不写死任何表情 id。
            var list = ExpressionLibrary.All;
            if (list == null)
            {
                ExpressionLibrary.AllIds();
                list = ExpressionLibrary.All;
            }
            if (list == null)
            {
                Debug.LogWarning("[ActionCatalog] ExpressionLibrary.All 为空 —— 表情清单为空。");
                return;
            }

            for (int i = 0; i < list.Count; i++)
            {
                var d = list[i];
                if (d == null || string.IsNullOrEmpty(d.Id)) continue;
                if (FindEmotionInternal(d.Id) != null) continue; // 重复 id 只保留第一个
                _emotions.Add(new EmotionInfo
                {
                    Id = d.Id,
                    Label = !string.IsNullOrEmpty(d.Label) ? d.Label : d.Id,
                });
            }

            if (_emotions.Count == 0)
            {
                // 只警告一次（EnsureInit 会限速重试，重复警告会刷屏）
                if (!_warnedEmptyEmotions)
                {
                    _warnedEmptyEmotions = true;
                    Debug.LogWarning("[ActionCatalog] ExpressionLibrary 里没有可用表情 —— 表情清单为空。");
                }
            }
            else
            {
                _warnedEmptyEmotions = false;
            }
        }

        // ============================================================
        //  只读访问
        // ============================================================

        /// <summary>清单是否可用（找到 manifest 且 gesture/emotion 都非空）</summary>
        public static bool Ready
        {
            get { EnsureInit(); return _gestures.Count > 0 && _emotions.Count > 0; }
        }

        /// <summary>全部可用动作（顺序 = manifest 里的顺序）</summary>
        public static IReadOnlyList<GestureInfo> Gestures
        {
            get { EnsureInit(); return _gestures; }
        }

        /// <summary>全部可用表情（顺序 = ExpressionLibrary 里的顺序）</summary>
        public static IReadOnlyList<EmotionInfo> Emotions
        {
            get { EnsureInit(); return _emotions; }
        }

        /// <summary>固定姿态枚举：{"stand","sit","kneel"}（只读，别改它）</summary>
        public static string[] Postures
        {
            get { return _postures; }
        }

        /// <summary>姿态 id 是否合法（stand / sit / kneel）—— 供 ResponseParser / Director 校验</summary>
        public static bool HasPosture(string posture)
        {
            if (string.IsNullOrEmpty(posture)) return false;
            for (int i = 0; i < _postures.Length; i++)
                if (string.Equals(_postures[i], posture, System.StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>动作 id 是否存在（gesture 清单里有没有）</summary>
        public static bool HasGesture(string id)
        {
            EnsureInit();
            return FindGestureInternal(id) != null;
        }

        /// <summary>表情 id 是否存在（emotion 清单里有没有）</summary>
        public static bool HasEmotion(string id)
        {
            EnsureInit();
            return FindEmotionInternal(id) != null;
        }

        /// <summary>按语义 id 取动作信息；没有返回 null</summary>
        public static GestureInfo GetGesture(string id)
        {
            EnsureInit();
            return FindGestureInternal(id);
        }

        /// <summary>表情 id → 中文名；没有返回传入的 id</summary>
        public static string EmotionLabel(string id)
        {
            EnsureInit();
            var e = FindEmotionInternal(id);
            if (e != null && !string.IsNullOrEmpty(e.Label)) return e.Label;
            return id ?? "";
        }

        /// <summary>所有动作 id（新数组，可随意改）</summary>
        public static string[] GestureIds()
        {
            EnsureInit();
            var ids = new string[_gestures.Count];
            for (int i = 0; i < _gestures.Count; i++) ids[i] = _gestures[i].Id;
            return ids;
        }

        /// <summary>所有表情 id（新数组，可随意改）</summary>
        public static string[] EmotionIds()
        {
            EnsureInit();
            var ids = new string[_emotions.Count];
            for (int i = 0; i < _emotions.Count; i++) ids[i] = _emotions[i].Id;
            return ids;
        }

        // ============================================================
        //  给 prompt 用的文字清单
        // ============================================================

        /// <summary>
        /// 给 prompt 的多行动作清单，每行：id 中文名(时长s,姿态,部位,情绪标签)
        /// 例：wave 挥手(1.5s,站,上半身,happy,friendly,greeting)
        /// </summary>
        public static string DescribeGestures()
        {
            EnsureInit();
            if (_gestures.Count == 0) return "（暂无可用动作）";

            var sb = new StringBuilder();
            for (int i = 0; i < _gestures.Count; i++)
            {
                var g = _gestures[i];
                if (i > 0) sb.Append('\n');

                sb.Append(g.Id).Append(' ').Append(g.Label).Append('(');
                sb.Append(g.Duration > 0f ? g.Duration.ToString("F1") + "s" : "时长未知");
                sb.Append(',').Append(ActionMetadata.PostureCn(g.Posture));
                sb.Append(',').Append(ActionMetadata.BodyPartCn(g.BodyPart));
                if (!string.IsNullOrEmpty(g.EmotionTags)) sb.Append(',').Append(g.EmotionTags);
                sb.Append(')');
            }
            return sb.ToString();
        }

        /// <summary>给 prompt 的多行表情清单，每行：id 中文名。例：smile 微笑</summary>
        public static string DescribeEmotions()
        {
            EnsureInit();
            if (_emotions.Count == 0) return "（暂无可用表情）";

            var sb = new StringBuilder();
            for (int i = 0; i < _emotions.Count; i++)
            {
                if (i > 0) sb.Append('\n');
                sb.Append(_emotions[i].Id).Append(' ').Append(_emotions[i].Label);
            }
            return sb.ToString();
        }

        // ============================================================
        //  情绪 → 动作（Director 没拿到 gesture 时的默认表用）
        // ============================================================

        /// <summary>
        /// 按情绪标签取候选动作 id（EmotionTags 逗号匹配，忽略大小写）。
        /// 没有匹配返回**空数组**（不是 null）。
        /// </summary>
        public static string[] GesturesForEmotion(string emotion)
        {
            EnsureInit();
            if (string.IsNullOrEmpty(emotion)) return new string[0];

            string want = emotion.Trim();
            if (want.Length == 0) return new string[0];

            var result = new List<string>();
            for (int i = 0; i < _gestures.Count; i++)
            {
                var g = _gestures[i];
                if (TagsContain(g.EmotionTags, want)) result.Add(g.Id);
            }
            return result.ToArray();
        }

        /// <summary>按情绪取第一个候选动作；没有返回 null</summary>
        public static string DefaultGesture(string emotion)
        {
            var arr = GesturesForEmotion(emotion);
            return arr.Length > 0 ? arr[0] : null;
        }

        // ============================================================
        //  内部工具
        // ============================================================

        static GestureInfo FindGestureInternal(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            for (int i = 0; i < _gestures.Count; i++)
                if (_gestures[i].Id == id) return _gestures[i];
            return null;
        }

        static EmotionInfo FindEmotionInternal(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            for (int i = 0; i < _emotions.Count; i++)
                if (_emotions[i].Id == id) return _emotions[i];
            return null;
        }

        /// <summary>"happy,friendly,greeting" 里是否含 emotion 这个标签（逗号分隔，忽略大小写）</summary>
        static bool TagsContain(string tags, string emotion)
        {
            if (string.IsNullOrEmpty(tags) || string.IsNullOrEmpty(emotion)) return false;
            string[] parts = tags.Split(',');
            for (int i = 0; i < parts.Length; i++)
            {
                string p = parts[i].Trim();
                if (p.Length > 0 && string.Equals(p, emotion, System.StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }
    }
}
