using System.Text.RegularExpressions;
using UnityEngine;

namespace Yixuan.Stage5
{
    public enum Stage5IntentKind { None, SitFront, SitBehind, LookOnly, Stand, Cancel, Unsupported, SitNearby }

    public sealed class Stage5Intent
    {
        public Stage5IntentKind Kind;
        public string Original = "";
        public string Direction = "";
        public string Reason = "";

        public override string ToString() { return Kind + " " + Direction + " " + Reason; }
    }

    /// <summary>
    /// Conservative natural-language gate for the first Stage-5 version.
    /// It only claims commands that explicitly mention a bench/seat and a sit
    /// or look action. Questions, hypotheticals, negatives and other chatter go
    /// back to the normal chat lane instead of triggering movement.
    /// </summary>
    public static class Stage5IntentParser
    {
        static readonly Regex Chair = new Regex("椅子|长椅|座位|凳子|座椅", RegexOptions.IgnoreCase);
        static readonly Regex SitVerb = new Regex("坐下|坐上去|坐到|坐一下|坐一坐|坐坐|去坐|坐下来|坐(?:一|几|两)?(?:会儿?|阵子?|分钟)", RegexOptions.IgnoreCase);
        static readonly Regex LookVerb = new Regex("看看|看一下|看下|看一看|选中|选一下|定位|指一下|指出|找", RegexOptions.IgnoreCase);
        static readonly Regex NegativeMove = new Regex("先别过去|不要过去|别过去|先别走|不要走|别走|只看|只选|不要动|别动|先不要过去|不用过去", RegexOptions.IgnoreCase);
        static readonly Regex NegativeSit = new Regex("不要坐|别坐|不想坐|先不坐|不用坐|禁止坐|别坐下|(?:不要|别|不用|不必|先不)[^，,。；;！？!?]{0,24}坐", RegexOptions.IgnoreCase);
        static readonly Regex Hypothetical = new Regex("如果|假如|假设|要是|会不会|能不能|可不可以|想不想|要不要|是不是|吗[？?]?|[？?]$", RegexOptions.IgnoreCase);
        static readonly Regex QuestionLike = new Regex("谁|什么|哪|怎么|为什么|是否|是不是|坐着的人|的人|吗|呢|[？?]", RegexOptions.IgnoreCase);
        static readonly Regex NarrativeLike = new Regex("我刚|我刚刚|我刚才|我已经|我曾经|我之前|刚刚|刚才|刚才|之前|昨天|上次|当时|曾经", RegexOptions.IgnoreCase);
        static readonly Regex Composite = new Regex(@"(?:[，,；;。]\s*(?:再|然后|接着|随后|之后|并且|同时|顺便))|(?:再|然后|接着|随后|之后)\s*(?:去|走|到|坐下|坐|站起|起身|看|拿|打开|过去)", RegexOptions.IgnoreCase);
        static readonly Regex StandExact = new Regex(@"^(?:(?:请你|麻烦你|你|请|现在|先|快|赶紧|那是|那就|那|别[坐做]了|不[坐做]了|走[啊呀吧]?)\s*[，,。！!]?\s*)*(?:起身|起来|站起来|站起|从椅子上起来|从椅子起来|别坐了|不坐了)(?:一下|吧|啊|呀|啦|[。！!\s])*$", RegexOptions.IgnoreCase);
        static readonly Regex CancelExact = new Regex("^(取消|停止|停下|停下任务|停止任务|中止|算了|先停一下)[。！!\\s]*$", RegexOptions.IgnoreCase);
        static readonly Regex Behind = new Regex("身后|背后|后面|后边|后方|往后看|回头", RegexOptions.IgnoreCase);
        static readonly Regex Front = new Regex("面前|前面|前方|眼前|跟前|正前方|正面", RegexOptions.IgnoreCase);
        static readonly Regex Nearby = new Regex("附近|周围|周边|近处|身边", RegexOptions.IgnoreCase);
        static readonly Regex CommandStart = new Regex("^(?:你(?:先|现在)?\\s*)?(去|到|走到|走向|往|看看|看一下|看下|看一看|选中|选一下|定位|指一下|指出|找|帮我|请|现在|先|麻烦)", RegexOptions.IgnoreCase);
        static readonly Regex AddressedCommand = new Regex("你(?:先|现在)?\\s*(?:去|到|走到|走向|往|看看|看一下|看下|看一看|选中|选一下|定位|指一下|指出|找|帮我|请|麻烦)", RegexOptions.IgnoreCase);
        static readonly Regex ReportedCommand = new Regex("(?:我|你|他|她|有人|别人)(?:说|讲|问|写|读|提到|看到|看见|听到|听见)|听说|据说|比如|例如|举例|引用|这句|这条指令|[“”‘’\"]", RegexOptions.IgnoreCase);

        public static Stage5Intent Parse(string input)
        {
            var intent = new Stage5Intent { Original = input ?? "" };
            string text = (input ?? "").Trim();
            if (text.Length == 0) return intent;

            if (StandExact.IsMatch(text)) { intent.Kind = Stage5IntentKind.Stand; intent.Reason = "explicit_stand"; return intent; }
            if (CancelExact.IsMatch(text)) { intent.Kind = Stage5IntentKind.Cancel; intent.Reason = "explicit_cancel"; return intent; }

            // Questions / descriptions of the user's own past experience are not
            // commands. This is intentionally checked before chair/sit keywords.
            if (QuestionLike.IsMatch(text) || NarrativeLike.IsMatch(text)) return intent;
            if (Hypothetical.IsMatch(text)) return intent;
            if (NegativeSit.IsMatch(text)) return intent;

            bool hasChair = Chair.IsMatch(text);
            bool look = LookVerb.IsMatch(text);
            bool sit = SitVerb.IsMatch(text);
            if (!hasChair && !sit) return intent;

            // Reject unsupported compound requests wholesale instead of executing
            // only the first segment and dropping the rest.
            if (Composite.IsMatch(text) && (look || sit))
            {
                intent.Kind = Stage5IntentKind.Unsupported;
                intent.Reason = "compound_request_not_supported_v1";
                return intent;
            }

            // A visual chair action must be phrased as an imperative command, not
            // as a statement about the user ("我刚..." / "椅子上坐着的人...").
            if ((look || sit) && (ReportedCommand.IsMatch(text) ||
                (!CommandStart.IsMatch(text) && !AddressedCommand.IsMatch(text)))) return intent;

            // "看看/选中...先别过去" is look-only, never walking/sitting.
            if (look && NegativeMove.IsMatch(text))
            {
                intent.Kind = Stage5IntentKind.LookOnly;
                intent.Direction = Behind.IsMatch(text) ? "behind" : Front.IsMatch(text) ? "front" : Nearby.IsMatch(text) ? "nearby" : "unspecified";
                intent.Reason = "look_only_negative_move";
                return intent;
            }
            // A pure look/selection request without a sit verb.
            if (look && !sit && hasChair)
            {
                intent.Kind = Stage5IntentKind.LookOnly;
                intent.Direction = Behind.IsMatch(text) ? "behind" : Front.IsMatch(text) ? "front" : Nearby.IsMatch(text) ? "nearby" : "unspecified";
                intent.Reason = "look_only";
                return intent;
            }
            if (NegativeMove.IsMatch(text) && !look) return intent;
            if (sit && hasChair)
            {
                intent.Kind = Behind.IsMatch(text) ? Stage5IntentKind.SitBehind : Front.IsMatch(text) ? Stage5IntentKind.SitFront : Stage5IntentKind.SitNearby;
                intent.Direction = Behind.IsMatch(text) ? "behind" : Front.IsMatch(text) ? "front" : "nearby";
                intent.Reason = "explicit_sit";
                return intent;
            }
            return intent;
        }

        public static bool IsLikelyStage5Chat(string text)
        {
            return Parse(text).Kind != Stage5IntentKind.None;
        }
    }
}
