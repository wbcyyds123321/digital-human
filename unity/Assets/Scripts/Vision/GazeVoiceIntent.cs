using System;
using System.Text.RegularExpressions;
using UnityEngine;
using Yixuan.AI;

namespace Yixuan.Vision
{
    public enum GazeIntentType
    {
        None,
        Center,
        Relative,
        LookAtStore,
        LookAtUser
    }

    public struct GazeIntent
    {
        public GazeIntentType Type;
        public float Yaw;
        public float Pitch;
        public float HoldSeconds;
        /// <summary>本次观察是否允许身体配合转向（F3：用户说“只转镜头/不要转身”时为 false）。</summary>
        public bool AllowBodyAssist;

        public static GazeIntent None
        {
            get { return new GazeIntent { Type = GazeIntentType.None, HoldSeconds = 6f, AllowBodyAssist = true }; }
        }
    }

    /// <summary>
    /// 云台语音指令的本地确定性解析。
    /// 不依赖 LLM：说“回头看/往左看/抬头/回正”时相机立即动，
    /// 后面 LLM 再慢慢负责语言回复；避免为了转头等 2 秒。
    /// </summary>
    public static class GazeVoiceIntent
    {
        // F3：整句权限约束必须先于从句过滤提取。否则“不要转身，只往后看”里的
        // 否定从句会被 StripInvalidClauses 丢掉，身体配合权限也随之丢失。
        static readonly Regex BodyAssistProhibition = new Regex(
            @"(?:不要|别|不用|不必|无需|禁止|不许|不准|不能|不想|不需要)[^，,。！？!?；;\n]{0,10}(?:转身|转身体|动身体|身体(?:转|动))" +
            @"|身体(?:不要|别|不用|不必|禁止|不许|不准|不能)(?:动|转)" +
            @"|身体(?:保持)?不动" +
            @"|只(?:转|动)(?:镜头|摄像头|相机|视角|云台)" +
            @"|只(?:是)?(?:把)?(?:镜头|摄像头|相机|视角|云台)[^，,。！？!?；;\n]{0,8}(?:转|动|移|偏|看)",
            RegexOptions.Compiled);

        /// <summary>整句是否允许身体配合观察；只转镜头/不要转身时返回 false。</summary>
        public static bool AllowsBodyAssist(string text)
        {
            if (string.IsNullOrEmpty(text)) return true;
            return !BodyAssistProhibition.IsMatch(text);
        }

        public static bool TryParse(string text, out GazeIntent intent)
        {
            intent = GazeIntent.None;
            if (string.IsNullOrEmpty(text)) return false;
            string s = text.Trim();
            if (s.Length == 0) return false;

            bool allowBodyAssist = AllowsBodyAssist(s);

            // P5/R3：先按从句切分，丢掉姿态问题从句、否定/过去从句，
            // 再对剩下的“当前命令”做整句判断。这样“我是不是低头了，往左看”
            // 不会在分句前被“是不是”一票否决。
            s = StripInvalidClauses(s);
            if (string.IsNullOrEmpty(s)) return false;
            intent.AllowBodyAssist = allowBodyAssist;

            // P5：用户问自己的姿态（"我低头了吗"）不转云台；
            // 但如果同一句里还有真正的方向命令（"我低头了吗，往左看"），允许后面的从句继续执行。
            if (VisualQueryIntent.IsUserPostureQuestion(s) && !HasNonPostureDirection(s)) return false;

            // 明确的疑问句不是当前命令（此时 s 已去掉姿态/否定/过去从句）。
            if (s.Contains("要不要") || s.Contains("是不是") || s.Contains("是否"))
                return false;

            // ★ 视觉问答不是云台命令：
            //   "你看看我现在什么表情" 里含 "看我"，会被误判成 LookAtUser，
            //   然后 ChatSession 会白等一轮"云台稳定 + 新观察"（实测多花 5~8 秒）。
            //   但如果句子里有真正的方向词（回头/往左看/抬头…），仍按命令处理。
            if (IsVisionQuestionLike(s) && !HasExplicitDirection(s))
                return false;

            // ★ 身体走路 / 身体转身不是云台命令：
            //   “往左走”不能被宽泛的“往左”截获；“转身/背过去”交给身体控制字段（body_turn）。
            if (IsBodyMoveOnly(s)) return false;
            if (HasAny(s, "回正", "复位", "看前面", "看前方", "正前方", "面朝前", "回到正面",
                          "转回来", "转回正面", "回到默认", "回默认", "回到原位", "归位"))
            {
                intent.Type = GazeIntentType.Center;
                intent.HoldSeconds = 4f;
                return true;
            }

            // 看店/看便利店（同义：柜台/入口/门口/店门方向/面向店…）
            if (HasAny(s, "看店", "看便利店", "看商店", "看收银台", "看货架", "看门口",
                          "看柜台", "面向柜台", "转向柜台", "看入口", "看店门", "店门方向",
                          "面向店", "面向便利店", "朝店", "看向柜台"))
            {
                intent.Type = GazeIntentType.LookAtStore;
                intent.HoldSeconds = 7f;
                return true;
            }

            // 看用户/镜头（同义：面向我/朝向我…）
            if (HasAny(s, "看我", "看镜头", "看摄像头", "看用户", "看我这边",
                          "面向我", "朝向我", "看向我"))
            {
                intent.Type = GazeIntentType.LookAtUser;
                intent.HoldSeconds = 6f;
                return true;
            }

            float yaw = 0f;
            float pitch = 0f;
            bool hit = false;
            string yawId = null, pitchId = null;
            float defaultYawAngle = 0f;

            // 后方 / 左后 / 右后
            if (HasAny(s, "左后方", "左后"))
            {
                yawId = "left"; defaultYawAngle = 135f;
                hit = true;
            }
            else if (HasAny(s, "右后方", "右后"))
            {
                yawId = "right"; defaultYawAngle = 135f;
                hit = true;
            }
            else if (HasAny(s, "回头看", "回头", "往后看", "向后看", "后面", "身后") ||
                     (HasAny(s, "镜头", "摄像头", "相机", "视角", "云台") &&
                      HasAny(s, "往后转", "向后转", "转过去", "转到后面", "转后面")))
            {
                yawId = "behind";
                hit = true;
            }
            else if (HasAny(s, "左边", "向左看", "往左看", "看左", "左侧", "往左", "朝左",
                              "向左边", "往左边", "左手边", "左转", "转左", "望向左边"))
            {
                yawId = "left";
                hit = true;
            }
            else if (HasAny(s, "右边", "向右看", "往右看", "看右", "右侧", "往右", "朝右",
                              "向右边", "往右边", "右手边", "右转", "转右", "望向右边"))
            {
                yawId = "right";
                hit = true;
            }
            // 专业说法：顺时针 = 往右（yaw 正），逆时针 = 往左（yaw 负）
            else if (HasAny(s, "顺时针"))
            {
                yawId = "right";
                hit = true;
            }
            else if (HasAny(s, "逆时针"))
            {
                yawId = "left";
                hit = true;
            }

            if (HasAny(s, "抬头", "看上面", "往上看", "看上方", "仰头",
                          "仰起头", "抬起一下头", "向上看", "望向上面", "看天花板", "看天"))
            {
                pitchId = "up";
                hit = true;
            }
            else if (HasAny(s, "低头", "看下面", "往下看", "看下方",
                              "俯视", "向下看", "看地面", "看地上", "垂下头", "把头低下去"))
            {
                pitchId = "down";
                hit = true;
            }

            if (!hit) return false;

            // 角度（可选）：解析"往左转30度 / 抬头四十五度 / 往右偏二十度"里的度数，
            // 覆盖默认角度。统一走 CameraMoveCatalog：乘整体增益 AngleScale、按 Inspector 上限 clamp。
            float degrees, unused;
            bool explicitAngle = TryParseDegrees(s, out degrees);
            if (yawId != null)
                CameraMoveCatalog.TryResolve(yawId, explicitAngle ? degrees : defaultYawAngle, out yaw, out unused);
            if (pitchId != null)
                CameraMoveCatalog.TryResolve(pitchId, explicitAngle ? degrees : 0f, out unused, out pitch);

            intent.Type = GazeIntentType.Relative;
            intent.Yaw = yaw;
            intent.Pitch = pitch;
            intent.HoldSeconds = 6f;
            return true;
        }

        // ---------------- 角度解析（阿拉伯数字 + 常见中文数字） ----------------

        static readonly Regex ArabicDegrees = new Regex(@"(\d{1,3})\s*(?:°|度)", RegexOptions.Compiled);
        static readonly Regex ChineseDegrees = new Regex(@"([一二三四五六七八九十百零两]+)\s*(?:°|度)", RegexOptions.Compiled);

        static bool TryParseDegrees(string text, out float degrees)
        {
            degrees = 0f;
            if (string.IsNullOrEmpty(text)) return false;

            Match m = ArabicDegrees.Match(text);
            if (m.Success && int.TryParse(m.Groups[1].Value, out int a))
            {
                degrees = a;
                return true;
            }
            m = ChineseDegrees.Match(text);
            if (m.Success && TryParseChineseNumber(m.Groups[1].Value, out int c))
            {
                degrees = c;
                return true;
            }
            return false;
        }

        /// <summary>解析 1~999 的常见中文数字（十五/三十/四十五/一百八十/二十）。</summary>
        static bool TryParseChineseNumber(string s, out int value)
        {
            value = 0;
            if (string.IsNullOrEmpty(s)) return false;
            int result = 0, current = 0;
            foreach (char c in s)
            {
                int digit;
                switch (c)
                {
                    case '一': digit = 1; break;
                    case '二': case '两': digit = 2; break;
                    case '三': digit = 3; break;
                    case '四': digit = 4; break;
                    case '五': digit = 5; break;
                    case '六': digit = 6; break;
                    case '七': digit = 7; break;
                    case '八': digit = 8; break;
                    case '九': digit = 9; break;
                    case '零': digit = 0; break;
                    case '十':
                        if (current == 0) current = 1;
                        result += current * 10;
                        current = 0;
                        digit = -1;
                        break;
                    case '百':
                        if (current == 0) current = 1;
                        result += current * 100;
                        current = 0;
                        digit = -1;
                        break;
                    default: return false;
                }
                if (digit >= 0) current = digit;
            }
            result += current;
            if (result <= 0 || result > 999) return false;
            value = result;
            return true;
        }

        static bool HasAny(string s, params string[] words)
        {
            for (int i = 0; i < words.Length; i++)
                if (s.Contains(words[i])) return true;
            return false;
        }

        /// <summary>句子里是否在问"你看到了什么"（这类句子应交给实时视觉问答，而不是转云台）。</summary>
        static bool IsVisionQuestionLike(string s)
        {
            bool question = s.Contains("?") || s.Contains("？") ||
                            HasAny(s, "什么", "几", "吗", "呢", "是不是", "有没有", "怎么样", "什么样", "多少");
            if (!question) return false;
            return HasAny(s, "看到", "看见", "看得到", "看得见", "看得清", "表情", "手里", "手上",
                              "拿着", "比划", "数字", "手势", "画面", "摄像头", "游戏里", "店里",
                              "后面", "前面", "周围", "穿", "颜色", "什么字", "长相", "东西",
                              // 用户状态类问句也常含"看我"，不应触发"看我"云台命令
                              "姿势", "动作", "样子", "状态", "在干嘛", "在做什么", "换了",
                              "抬手", "举手", "低头", "转头", "穿什么");
        }

        /// <summary>真正的方向命令词（有这些词时，即使句尾是问句也先转云台）。</summary>
        static bool HasExplicitDirection(string s)
        {
            return HasAny(s, "回头", "往后看", "向后看", "往左看", "向左看", "看左边", "往右看",
                              "向右看", "看右边", "抬头", "低头", "回正", "复位",
                              "看店", "看便利店", "看门口",
                              // 同义/专业说法
                              "顺时针", "逆时针", "朝左", "朝右", "左手边", "右手边",
                              "左转", "右转", "看向柜台", "面向柜台", "看柜台");
        }

        /// <summary>
        /// 姿态问题句里是否还有"非姿态"的方向命令（往左看/回头/看店…）。
        /// 用于区分"我现在低头了吗"（纯姿态问题，不转云台）和"我低头了吗，往左看"（含当前命令）。
        /// </summary>
        static bool HasNonPostureDirection(string s)
        {
            return HasAny(s, "回头", "往后看", "向后看", "转身", "转过去",
                              "往左看", "向左看", "看左边", "往左", "朝左", "左手边", "左转", "转左",
                              "往右看", "向右看", "看右边", "往右", "朝右", "右手边", "右转", "转右",
                              "回正", "复位", "看前面", "看前方", "正前方", "转回来", "回默认",
                              "看店", "看便利店", "看门口", "看柜台", "面向柜台", "看向柜台", "看入口", "看店门",
                              "顺时针", "逆时针",
                              "看上面", "看下面", "往上看", "往下看", "看上方", "看下方");
        }

        /// <summary>
        /// 按标点切从句，只丢弃“带方向命令且含否定/过去”的从句。
        /// 这样“刚才没看清，现在往左看”仍保留后半句；
        /// “我昨天回头看了一下”整句没有当前命令，会被清空。
        /// </summary>
        static string StripInvalidClauses(string s)
        {
            char[] separators = { '，', ',', '。', '！', '!', '？', '?', '；', ';', '\n' };
            string[] parts = s.Split(separators, StringSplitOptions.RemoveEmptyEntries);
            if (parts == null || parts.Length == 0) return s;

            var kept = new System.Text.StringBuilder();
            for (int i = 0; i < parts.Length; i++)
            {
                string clause = parts[i].Trim();
                if (clause.Length == 0) continue;
                // P5：姿态问题从句本身不是云台命令（"我低头了吗"），丢掉它，
                // 只保留同一句里的真正指令（"往左看"）。
                if (VisualQueryIntent.IsUserPostureQuestion(clause)) continue;
                // R2：按从句区分身体/走路和观察。身体转身、走路从句不进入云台候选；
                // "往左走一步，然后往右看" 只保留后半句观察意图。
                if (IsBodyMoveOnly(clause)) continue;
                if (HasNegation(clause) || HasPast(clause)) continue;
                if (kept.Length > 0) kept.Append('，');
                kept.Append(clause);
            }
            return kept.ToString();
        }

        static bool HasNegation(string clause)
        {
            // “特别”里的“别”不是否定，先去掉再判断。
            string c = clause.Replace("特别", "");
            return HasAny(c, "不要", "别", "不用", "不必", "禁止", "不许", "不准", "勿", "莫", "不是", "并非", "不往", "不向", "不朝", "不转", "不抬", "不低", "不能", "不想", "不需要");
        }

        static bool HasPast(string clause)
        {
            return HasAny(clause, "昨天", "刚才", "刚刚", "之前", "以前", "曾经",
                                  "上次", "上一次", "那次", "当时", "梦里", "说过", "提到");
        }

        /// <summary>“往左走 / 往前走两步 / 走到门口 / 身体向左转 / 转过身”是身体动作，不是云台命令；带“看/望”的复合句仍交给云台处理。</summary>
        static bool IsBodyMoveOnly(string s)
        {
            bool walk = HasAny(s, "往左走", "往右走", "往前走", "往后走", "向左走", "向右走", "向前走", "向后走",
                                  "走两步", "走一步", "走过去", "走到", "前进", "后退", "退后");
            bool bodyTurn = HasAny(s, "转身", "转过身", "背过去", "背过身") ||
                            (HasAny(s, "身体") && HasAny(s, "转", "面向", "朝向"));
            bool look = HasAny(s, "看", "望", "镜头", "云台", "视线", "转头", "扭头");
            return (walk || bodyTurn) && !look;
        }

    }
}
