using System.Text;

namespace Yixuan.AI
{
    /// <summary>
    /// 增量解析 LLM 流式输出的 JSON 契约。
    ///
    /// 契约字段顺序：feeling → demeanor → gaze → camera → emotion → intensity → gesture → posture → wish → reply → beats。
    /// 做法：
    ///   - reply 之前的字段都是短字段：等 `"reply"` 出现时，前面的内容已完整，直接提取填 Plan；
    ///   - reply 是字符串：逐字符解码（含 \" \\ \n \uXXXX），通过 DrainNewReplyChars 增量吐出；
    ///   - 流结束后仍用 ResponseParser 对完整 JSON 做一次权威解析（beats 等）。
    ///
    /// 纯 C#，不依赖 Unity API（方便单测/后台线程）。
    /// </summary>
    public class StreamingPlanParser
    {
        readonly StringBuilder _raw = new StringBuilder();
        readonly StringBuilder _reply = new StringBuilder();

        public readonly BehaviorPlan Plan = new BehaviorPlan();

        int _replyStart = -1;      // reply 值内容在 _raw 中的起始下标
        int _replyPos;             // 已扫描到的 _raw 下标
        int _replyEmitted;         // 已经通过 DrainNewReplyChars 吐出的字符数
        bool _replyDone;

        public bool ReplyStarted { get { return _replyStart >= 0; } }
        public bool ReplyDone { get { return _replyDone; } }
        public string RawText { get { return _raw.ToString(); } }
        public string ReplyText { get { return _reply.ToString(); } }

        public void Feed(string chunk)
        {
            if (string.IsNullOrEmpty(chunk)) return;
            _raw.Append(chunk);
            if (_replyStart < 0)
            {
                TryStartReply();
                if (_replyStart < 0) return;
            }
            if (!_replyDone) ConsumeReply();
        }

        /// <summary>取出自上次调用以来新解出的 reply 字符（供 TTS 分句/字幕增量）。</summary>
        public string DrainNewReplyChars()
        {
            string all = _reply.ToString();
            if (all.Length <= _replyEmitted) return "";
            string delta = all.Substring(_replyEmitted);
            _replyEmitted = all.Length;
            return delta;
        }

        /// <summary>用流结束时拿到的文本补一个 Plan（reply 用流式累计的版本）。</summary>
        public BehaviorPlan BuildPlan()
        {
            BehaviorPlan p = Plan;
            p.reply = _reply.ToString();
            if (string.IsNullOrEmpty(p.emotion)) p.emotion = "neutral";
            if (string.IsNullOrEmpty(p.source)) p.source = "LLM(流式)";
            return p;
        }

        // ------------------------------------------------------------------
        //  reply 起始定位 + 前置字段提取
        // ------------------------------------------------------------------
        void TryStartReply()
        {
            string s = _raw.ToString();
            int key = s.IndexOf("\"reply\"", System.StringComparison.Ordinal);
            if (key < 0) return;
            int colon = s.IndexOf(':', key + 7);
            if (colon < 0) return;
            int q = colon + 1;
            while (q < s.Length && char.IsWhiteSpace(s[q])) q++;
            if (q >= s.Length || s[q] != '"') return;

            ApplyPrefixFields(s.Substring(0, key));

            _replyStart = q + 1;
            _replyPos = _replyStart;
        }

        void ApplyPrefixFields(string prefix)
        {
            Plan.feeling = ExtractString(prefix, "feeling");
            Plan.demeanor = ExtractString(prefix, "demeanor");
            Plan.gaze = ExtractString(prefix, "gaze");
            Plan.camera = ExtractString(prefix, "camera");
            float? angle = ExtractFloat(prefix, "camera_angle");
            Plan.camera_angle = angle.HasValue ? angle.Value : 0f;
            float? hold = ExtractFloat(prefix, "camera_hold");
            Plan.camera_hold = hold.HasValue ? hold.Value : 0f;
            Plan.move = ExtractString(prefix, "move");
            Plan.move_target = ExtractString(prefix, "move_target");
            float? moveX = ExtractFloat(prefix, "move_x");
            float? moveZ = ExtractFloat(prefix, "move_z");
            Plan.move_x = moveX.HasValue ? moveX.Value : 0f;
            Plan.move_z = moveZ.HasValue ? moveZ.Value : 0f;
            float? bodyTurn = ExtractFloat(prefix, "body_turn");
            Plan.body_turn = bodyTurn.HasValue ? bodyTurn.Value : 0f;
            float? forward = ExtractFloat(prefix, "move_forward_m");
            Plan.move_forward_m = forward.HasValue ? forward.Value : 0f;
            float? right = ExtractFloat(prefix, "move_right_m");
            Plan.move_right_m = right.HasValue ? right.Value : 0f;
            Plan.emotion = ExtractString(prefix, "emotion");
            Plan.gesture = ExtractString(prefix, "gesture");
            Plan.posture = ExtractString(prefix, "posture");
            Plan.wish = ExtractString(prefix, "wish");
            Plan.askedEmotion = Plan.emotion;
            Plan.askedDemeanor = Plan.demeanor;
            Plan.askedGesture = Plan.gesture;

            float? intensity = ExtractFloat(prefix, "intensity");
            Plan.intensity = intensity.HasValue ? intensity.Value : 0.6f;
        }

        // ------------------------------------------------------------------
        //  reply 字符串逐字符解码（支持转义/unicode）
        // ------------------------------------------------------------------
        void ConsumeReply()
        {
            string s = _raw.ToString();
            int i = _replyPos;
            while (i < s.Length)
            {
                char c = s[i];
                if (c == '"')
                {
                    _replyDone = true;
                    _replyPos = i + 1;
                    return;
                }

                if (c == '\\')
                {
                    if (i + 1 >= s.Length) break;      // 转义序列还没收完整，等下一块
                    char e = s[i + 1];
                    if (e == 'u')
                    {
                        if (i + 5 >= s.Length) break;   // \uXXXX 未收完
                        int code;
                        if (int.TryParse(s.Substring(i + 2, 4),
                                         System.Globalization.NumberStyles.HexNumber,
                                         System.Globalization.CultureInfo.InvariantCulture, out code))
                        {
                            _reply.Append((char)code);
                            i += 6;
                            continue;
                        }
                        _reply.Append(e); i += 2; continue;   // 非法 \u，保留原字符
                    }

                    switch (e)
                    {
                        case '"': _reply.Append('"'); break;
                        case '\\': _reply.Append('\\'); break;
                        case '/': _reply.Append('/'); break;
                        case 'b': _reply.Append('\b'); break;
                        case 'f': _reply.Append('\f'); break;
                        case 'n': _reply.Append('\n'); break;
                        case 'r': _reply.Append('\r'); break;
                        case 't': _reply.Append('\t'); break;
                        default: _reply.Append(e); break;
                    }
                    i += 2;
                    continue;
                }

                _reply.Append(c);
                i++;
            }
            _replyPos = i;
        }

        // ------------------------------------------------------------------
        //  小工具：从（可能不完整的）JSON 前缀里提取字段
        // ------------------------------------------------------------------
        public static string ExtractString(string json, string key)
        {
            if (string.IsNullOrEmpty(json) || string.IsNullOrEmpty(key)) return null;
            string needle = "\"" + key + "\"";
            int idx = json.IndexOf(needle, System.StringComparison.Ordinal);
            if (idx < 0) return null;
            int colon = json.IndexOf(':', idx + needle.Length);
            if (colon < 0) return null;
            int q = colon + 1;
            while (q < json.Length && char.IsWhiteSpace(json[q])) q++;
            if (q >= json.Length || json[q] != '"') return null;

            StringBuilder sb = new StringBuilder();
            int i = q + 1;
            while (i < json.Length)
            {
                char c = json[i];
                if (c == '"') return sb.ToString();
                if (c == '\\')
                {
                    if (i + 1 >= json.Length) return null;
                    char e = json[i + 1];
                    if (e == 'u')
                    {
                        if (i + 5 >= json.Length) return null;
                        int code;
                        if (int.TryParse(json.Substring(i + 2, 4),
                                         System.Globalization.NumberStyles.HexNumber,
                                         System.Globalization.CultureInfo.InvariantCulture, out code))
                            sb.Append((char)code);
                        i += 6; continue;
                    }
                    switch (e)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        default: sb.Append(e); break;
                    }
                    i += 2; continue;
                }
                sb.Append(c); i++;
            }
            return null;   // 字符串还没结束（等后续数据）
        }

        public static float? ExtractFloat(string json, string key)
        {
            if (string.IsNullOrEmpty(json) || string.IsNullOrEmpty(key)) return null;
            string needle = "\"" + key + "\"";
            int idx = json.IndexOf(needle, System.StringComparison.Ordinal);
            if (idx < 0) return null;
            int colon = json.IndexOf(':', idx + needle.Length);
            if (colon < 0) return null;
            int i = colon + 1;
            while (i < json.Length && char.IsWhiteSpace(json[i])) i++;
            int start = i;
            while (i < json.Length && (char.IsDigit(json[i]) || json[i] == '-' || json[i] == '.' || json[i] == 'e' || json[i] == 'E' || json[i] == '+'))
                i++;
            if (i <= start) return null;
            float v;
            if (float.TryParse(json.Substring(start, i - start), System.Globalization.NumberStyles.Float,
                               System.Globalization.CultureInfo.InvariantCulture, out v))
                return v;
            return null;
        }
    }
}
