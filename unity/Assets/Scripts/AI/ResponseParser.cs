using System;
using System.Collections.Generic;
using UnityEngine;
using Yixuan.Face;
using Yixuan.World;

namespace Yixuan.AI
{
    /// <summary>
    /// LLM 原始文本 → BehaviorPlan 的解析器 + 逐字段校验（design.md 2.1 契约）。
    ///
    /// 【三条原则】
    /// 1. **永远不抛异常**（B2 验收要求）：空输入 / 无 JSON / JSON 非法 / 字段非法，
    ///    都只返回 ok=false 或"降级后的 plan + 警告"，绝不把异常抛给 ChatSession。
    /// 2. 先截取"第一个配对完整的 {...}"：LLM 经常在 JSON 前后加一句废话或 markdown 代码块，
    ///    而花括号计数要跳过字符串里的括号（回复里可能真的有 "{" 字符）。
    /// 3. 非法字段统统"降级 + 记警告"，让 Director 永远拿到一个能安全执行的 plan：
    ///    emotion → neutral / intensity → 0.6 / gesture → null / posture → 保持 / gaze → user。
    ///
    /// 【不写死 id】
    /// 动作/表情 id 一律交给 ActionCatalog 校验（它从 MixamoActionManifest / ExpressionLibrary 现读）。
    /// 只有契约里明确规定的兜底值（neutral、stand/sit/kneel、user/away/down/up）才写死在下面。
    /// </summary>
    public static class ResponseParser
    {
        // ---- 契约里规定的兜底值（不是动作/表情 id，是字段的合法取值）----
        const float DefaultIntensity = 0.6f;
        const string DefaultEmotion = "neutral";
        const string DefaultGaze = "user";
        const int MaxFeelingChars = 30;    // 契约要求 ≤20 字，留一点余量再截
        const int MaxWishChars = 40;

        static readonly string[] PostureIds = new string[] { "stand", "sit", "kneel" };
        static readonly string[] GazeIds = new string[] { "user", "away", "down", "up" };

        /// <summary>
        /// 解析 LLM 回复。默认 ok 的定义：plan != null && plan.HasReply。
        /// 主动反应轮次可用 allowEmptyReply=true：允许“只做表情/动作、不说话”的合法计划。
        /// 任何情况下都返回一个非 null 的 PlanParseResult（raw 原样带回，便于调试）。
        /// </summary>
        public static PlanParseResult Parse(string raw)
        {
            return Parse(raw, false);
        }

        public static PlanParseResult Parse(string raw, bool allowEmptyReply)
        {
            var result = new PlanParseResult();
            result.raw = raw;
            result.plan = null;
            result.ok = false;

            try
            {
                if (string.IsNullOrEmpty(raw))
                {
                    result.warnings.Add("输入为空");
                    return result;
                }

                RefreshCatalog();

                string json = ExtractFirstJsonObject(raw);
                if (string.IsNullOrEmpty(json))
                {
                    result.warnings.Add("没找到配对完整的 JSON 对象 {...}（回复不是 JSON，或花括号不配对/被截断）");
                    return result;
                }

                BehaviorPlan plan = null;
                try
                {
                    plan = JsonUtility.FromJson<BehaviorPlan>(json);
                }
                catch (Exception e)
                {
                    result.warnings.Add("JSON 解析失败：" + e.Message);
                    return result;
                }

                if (plan == null)
                {
                    result.warnings.Add("JSON 解析结果为空");
                    return result;
                }

                NormalizeAndValidate(plan, json, result.warnings);

                result.plan = plan;
                result.ok = allowEmptyReply ? plan != null : plan.HasReply;
                return result;
            }
            catch (Exception e)
            {
                // 兜底：解析器永不把异常抛给调用方（B2）
                result.plan = null;
                result.ok = false;
                result.warnings.Add("解析器内部异常：" + e.Message);
                return result;
            }
        }

        // ---------- 校验 / 归一化 ----------

        static void NormalizeAndValidate(BehaviorPlan plan, string json, List<string> warnings)
        {
            // ---- reply：必填 ----
            plan.reply = TrimOrNull(plan.reply);
            if (string.IsNullOrEmpty(plan.reply))
                warnings.Add("reply 缺失或为空（这句话不会说出口）");

            // ---- 先记下 LLM 原本要的值（行为面板显示"想要 → 实际"，并据此记录缺失的素材）----
            plan.askedEmotion = NormalizeId(plan.emotion);
            plan.askedGesture = NormalizeId(plan.gesture);
            plan.askedDemeanor = NormalizeId(plan.demeanor);
            if (IsNullSynonym(plan.askedGesture)) plan.askedGesture = null;

            // ---- feeling / wish：自由文本，只做截断 ----
            plan.feeling = Truncate(TrimOrNull(plan.feeling), MaxFeelingChars);
            plan.wish = TrimOrNull(plan.wish);
            if (plan.wish != null && IsNullSynonym(plan.wish.ToLowerInvariant())) plan.wish = null;
            plan.wish = Truncate(plan.wish, MaxWishChars);

            // ---- emotion：空 → neutral（契约缺省）；非法 → neutral + 警告 ----
            plan.emotion = NormalizeId(plan.emotion);
            if (string.IsNullOrEmpty(plan.emotion))
            {
                plan.emotion = DefaultEmotion;
            }
            else if (!IsValidEmotion(plan.emotion))
            {
                warnings.Add("emotion「" + plan.emotion + "」不在表情库里，已改为 " + DefaultEmotion);
                plan.emotion = DefaultEmotion;
            }

            // ---- intensity：缺省 0.6，越界 clamp 到 0~1 ----
            // JsonUtility 分不清"字段缺失"和"值是 0"，所以按 JSON 原文判断字段是否存在
            if (!HasJsonField(json, "intensity") || float.IsNaN(plan.intensity) || float.IsInfinity(plan.intensity))
            {
                plan.intensity = DefaultIntensity;
            }
            else if (plan.intensity < 0f)
            {
                warnings.Add("intensity=" + plan.intensity.ToString("F2") + " 小于 0，已 clamp 到 0");
                plan.intensity = 0f;
            }
            else if (plan.intensity > 1f)
            {
                warnings.Add("intensity=" + plan.intensity.ToString("F2") + " 大于 1，已 clamp 到 1");
                plan.intensity = 1f;
            }

            // ---- gesture：空 → null（Director 按情绪补）；非法 → null + 警告 ----
            plan.gesture = NormalizeId(plan.gesture);
            if (string.IsNullOrEmpty(plan.gesture) || IsNullSynonym(plan.gesture))
            {
                plan.gesture = null;
            }
            else if (!IsValidGesture(plan.gesture))
            {
                warnings.Add("gesture「" + plan.gesture + "」不在动作清单里，已忽略（null，由 Director 按情绪补）");
                plan.gesture = null;
            }

            // ---- posture：空 → ""（保持当前）；非法 → "" + 警告 ----
            plan.posture = NormalizeId(plan.posture);
            if (string.IsNullOrEmpty(plan.posture) || IsNullSynonym(plan.posture))
            {
                plan.posture = "";
            }
            else if (!ContainsId(PostureIds, plan.posture))
            {
                warnings.Add("posture「" + plan.posture + "」非法（只支持 stand/sit/kneel），已改为保持当前姿态");
                plan.posture = "";
            }

            // ---- demeanor：空 → 按表情反推；非法 → 按表情反推 + 警告 ----
            plan.demeanor = NormalizeId(plan.demeanor);
            if (string.IsNullOrEmpty(plan.demeanor) || IsNullSynonym(plan.demeanor))
            {
                plan.demeanor = DemeanorCatalog.ForEmotion(plan.emotion);
            }
            else if (!DemeanorCatalog.Has(plan.demeanor))
            {
                string fixedId = DemeanorCatalog.ForEmotion(plan.emotion);
                warnings.Add("demeanor「" + plan.demeanor + "」不在神态库里，已按表情改为 " + fixedId);
                plan.demeanor = fixedId;
            }

            // ---- gaze：空 → 该神态的默认视线；非法 → 同上 + 警告 ----
            string gazeDefault = DemeanorCatalog.Get(plan.demeanor).DefaultGaze ?? DefaultGaze;
            plan.gaze = NormalizeId(plan.gaze);
            if (string.IsNullOrEmpty(plan.gaze) || IsNullSynonym(plan.gaze))
            {
                plan.gaze = gazeDefault;
            }
            else if (!ContainsId(GazeIds, plan.gaze))
            {
                warnings.Add("gaze「" + plan.gaze + "」非法（只支持 user/away/down/up），已改为 " + gazeDefault);
                plan.gaze = gazeDefault;
            }

            // ---- camera / camera_angle：空 → null（不动云台）；非法 → null + 警告 ----
            // 云台动作及默认角度统一由 CameraMoveCatalog 提供（和动作/表情同一种"目录 + 校验"做法）。
            plan.camera = NormalizeId(plan.camera);
            if (string.IsNullOrEmpty(plan.camera) || IsNullSynonym(plan.camera))
            {
                plan.camera = null;
            }
            else if (!CameraMoveCatalog.Has(plan.camera))
            {
                warnings.Add("camera「" + plan.camera + "」不在云台动作表里，已忽略");
                plan.camera = null;
            }

            if (float.IsNaN(plan.camera_angle) || float.IsInfinity(plan.camera_angle) || plan.camera_angle < 0f)
                plan.camera_angle = 0f;
            if (string.IsNullOrEmpty(plan.camera)) plan.camera_angle = 0f;

            if (float.IsNaN(plan.camera_hold) || float.IsInfinity(plan.camera_hold))
                plan.camera_hold = 0f;
            else
                plan.camera_hold = Mathf.Clamp(plan.camera_hold, 0f, 15f);

            // ---- move：走路请求（walk_to / stop / null）；坐标只做基本合法性检查，执行前再查障碍 ----
            plan.move = NormalizeId(plan.move);
            plan.move_target = NormalizeId(plan.move_target);
            if (string.IsNullOrEmpty(plan.move) || IsNullSynonym(plan.move))
            {
                plan.move = null;
                plan.move_target = null;
                plan.move_x = plan.move_z = 0f;
            }
            else if (plan.move != "walk_to" && plan.move != "stop")
            {
                warnings.Add("move「" + plan.move + "」非法（只支持 walk_to/stop/null），已忽略");
                plan.move = null;
                plan.move_target = null;
                plan.move_x = plan.move_z = 0f;
            }
            else if (plan.move == "stop")
            {
                plan.move_target = null;
                plan.move_x = plan.move_z = 0f;
            }
            else // walk_to
            {
                bool hasTarget = !string.IsNullOrEmpty(plan.move_target) && WorldData.GetAnchor(plan.move_target) != null;
                bool hasCoords = HasJsonField(json, "move_x") && HasJsonField(json, "move_z") &&
                                 !float.IsNaN(plan.move_x) && !float.IsInfinity(plan.move_x) &&
                                 !float.IsNaN(plan.move_z) && !float.IsInfinity(plan.move_z) &&
                                 Mathf.Abs(plan.move_x) < 1000f && Mathf.Abs(plan.move_z) < 1000f;
                if (hasTarget)
                {
                    plan.move_x = plan.move_z = 0f;
                }
                else if (hasCoords)
                {
                    plan.move_target = null;
                }
                else
                {
                    warnings.Add("move=walk_to 但既没有有效 move_target 也没有有效 move_x/move_z，已忽略");
                    plan.move = null;
                    plan.move_target = null;
                    plan.move_x = plan.move_z = 0f;
                }
            }

            // ---- body_turn / 相对移动：身体控制和云台分开；坏的数值不执行，冲突的运动字段整体拒绝 ----
            if (HasJsonField(json, "body_turn"))
            {
                if (float.IsNaN(plan.body_turn) || float.IsInfinity(plan.body_turn) || Mathf.Abs(plan.body_turn) > 180f)
                {
                    warnings.Add("body_turn 非法（只能是 -180~180），已忽略");
                    plan.body_turn = 0f;
                }
            }
            if (HasJsonField(json, "move_forward_m") &&
                (float.IsNaN(plan.move_forward_m) || float.IsInfinity(plan.move_forward_m) || Mathf.Abs(plan.move_forward_m) > 3f))
            {
                warnings.Add("move_forward_m 非法（绝对值最大 3m），已忽略");
                plan.move_forward_m = 0f;
            }
            if (HasJsonField(json, "move_right_m") &&
                (float.IsNaN(plan.move_right_m) || float.IsInfinity(plan.move_right_m) || Mathf.Abs(plan.move_right_m) > 3f))
            {
                warnings.Add("move_right_m 非法（绝对值最大 3m），已忽略");
                plan.move_right_m = 0f;
            }

            bool hasRelativeMotion = Mathf.Abs(plan.move_forward_m) > 0.0001f || Mathf.Abs(plan.move_right_m) > 0.0001f;
            bool hasBodyMotion = Mathf.Abs(plan.body_turn) > 0.0001f;
            if (!string.IsNullOrEmpty(plan.move) && (hasBodyMotion || hasRelativeMotion))
            {
                warnings.Add("同一条计划同时包含绝对移动和身体转身/相对移动，运动部分冲突，已全部忽略");
                plan.move = null; plan.move_target = null; plan.move_x = plan.move_z = 0f;
                plan.body_turn = 0f; plan.move_forward_m = plan.move_right_m = 0f;
                hasRelativeMotion = false;
            }
            if (string.IsNullOrEmpty(plan.move))
            {
                plan.move_target = null;
                plan.move_x = plan.move_z = 0f;
            }

            // ---- beats：逐条校验；非法手势的条目丢弃 ----
            if (plan.beats == null)
            {
                plan.beats = new Beat[0];   // 统一成空数组，调用方不用判 null
            }
            else if (plan.beats.Length > 0)
            {
                var kept = new List<Beat>(plan.beats.Length);
                for (int i = 0; i < plan.beats.Length; i++)
                {
                    Beat b = plan.beats[i];
                    if (b == null) continue;

                    string g = NormalizeId(b.gesture);
                    if (string.IsNullOrEmpty(g) || IsNullSynonym(g)) continue;   // 没写动作的节拍直接丢

                    if (!IsValidGesture(g))
                    {
                        warnings.Add("beats[" + i + "].gesture「" + g + "」不在动作清单里，已丢弃该节拍");
                        continue;
                    }

                    if (b.at < 0)
                    {
                        warnings.Add("beats[" + i + "].at=" + b.at + " 为负，已按 0 处理");
                        b.at = 0;
                    }

                    b.gesture = g;
                    kept.Add(b);
                }
                plan.beats = kept.ToArray();
            }
        }

        // ---------- 工具 ----------

        /// <summary>
        /// 截取第一个"配对完整"的 {...}：
        /// 从第一个 '{' 开始数花括号，跳过字符串内部的括号/转义字符；
        /// 数到 depth 归零就返回这一段。找不到（或被截断）返回 null。
        /// </summary>
        static string ExtractFirstJsonObject(string s)
        {
            if (string.IsNullOrEmpty(s)) return null;

            int start = s.IndexOf('{');
            if (start < 0) return null;

            bool inString = false;
            bool escaped = false;
            int depth = 0;

            for (int i = start; i < s.Length; i++)
            {
                char c = s[i];

                if (inString)
                {
                    if (escaped) escaped = false;
                    else if (c == '\\') escaped = true;
                    else if (c == '"') inString = false;
                    continue;
                }

                if (c == '"') { inString = true; continue; }
                if (c == '{') { depth++; continue; }
                if (c == '}')
                {
                    depth--;
                    if (depth == 0) return s.Substring(start, i - start + 1);
                    if (depth < 0) return null;
                }
            }
            return null;   // 花括号不配对
        }

        /// <summary>刷新动作/表情清单（失败也不影响解析，校验只是"降级 + 记警告"）</summary>
        static void RefreshCatalog()
        {
            try { ActionCatalog.Refresh(); }
            catch (Exception e) { Debug.LogWarning("[Parser] ActionCatalog.Refresh 失败（继续用当前清单校验）：" + e.Message); }
        }

        static bool IsValidGesture(string id)
        {
            if (string.IsNullOrEmpty(id)) return false;
            try { return ActionCatalog.HasGesture(id); }
            catch (Exception) { return false; }
        }

        static bool IsValidEmotion(string id)
        {
            if (string.IsNullOrEmpty(id)) return false;
            try
            {
                if (ActionCatalog.HasEmotion(id)) return true;
            }
            catch (Exception)
            {
                // Catalog 未初始化/异常时退回下面的表情库校验
            }
            // 兜底：表情库是静态表，永远可用；Catalog 没准备好也不会误杀合法表情
            return ExpressionLibrary.Get(id) != null;
        }

        /// <summary>JSON 原文里是否出现过某个键（区分"字段缺失"和"值恰好是 0"）</summary>
        static bool HasJsonField(string json, string key)
        {
            if (string.IsNullOrEmpty(json) || string.IsNullOrEmpty(key)) return false;

            string needle = "\"" + key + "\"";
            int idx = 0;
            while ((idx = json.IndexOf(needle, idx, StringComparison.Ordinal)) >= 0)
            {
                int after = idx + needle.Length;
                while (after < json.Length && char.IsWhiteSpace(json[after])) after++;
                if (after < json.Length && json[after] == ':') return true;   // 后面跟冒号才算键
                idx = after;
            }
            return false;
        }

        static string NormalizeId(string s)
        {
            if (string.IsNullOrEmpty(s)) return null;
            return s.Trim().ToLowerInvariant();
        }

        static string Truncate(string s, int max)
        {
            if (string.IsNullOrEmpty(s) || s.Length <= max) return s;
            return s.Substring(0, max) + "…";
        }

        static string TrimOrNull(string s)
        {
            return string.IsNullOrEmpty(s) ? null : s.Trim();
        }

        /// <summary>LLM 有时把"不指定"写成字符串而不是 JSON null</summary>
        static bool IsNullSynonym(string id)
        {
            return id == "null" || id == "none" || id == "nil" || id == "-" || id == "无";
        }

        static bool ContainsId(string[] ids, string id)
        {
            if (ids == null || string.IsNullOrEmpty(id)) return false;
            for (int i = 0; i < ids.Length; i++)
            {
                if (ids[i] == id) return true;
            }
            return false;
        }
    }
}
