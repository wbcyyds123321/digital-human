using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Yixuan.AI.Tasks
{
    [Serializable] public class MissionRequiredAction { public string kind, target_id, action_id, source; public bool camera_only; }
    [Serializable] public class MissionParallelRequirement { public string kind; public int with_action; }
    [Serializable] public class MissionIntent
    {
        public string original_text, error, interpretation;
        public string[] destination_ids = new string[0];
        public MissionRequiredAction[] required_actions = new MissionRequiredAction[0];
        public bool observe, interact, speak, express, return_to_origin;
        public MissionParallelRequirement[] parallel_requirements = new MissionParallelRequirement[0];
        static readonly Regex Split = new Regex(@"[，,。;；!！?？\r\n→]|然后|接着|随后|最后|之后|再(?=去|到|回|走|前往)", RegexOptions.IgnoreCase);
        static readonly Regex Attention = new Regex(@"(?:把|将)?(?:视角|视线|镜头|目光)(?:转向|对准|朝向|移向)|看向|看一下|看下|望向|注视|凝视|面向|朝向|\blook\s+at\b|\bface\s+(?:toward\s+)?", RegexOptions.IgnoreCase);
        static readonly Regex Movement = new Regex(@"站到|站在|停在|前往|走到|走向|去往|走近|靠近|移动到|到达|返回|回到|走回|去|回|到|\bgo\s+to\b|\bwalk\s+to\b|\bhead\s+to\b|\bnavigate\s+to\b|\breturn(?:\s+to)?\b", RegexOptions.IgnoreCase);
        static readonly Regex Observe = new Regex(@"观察|环视|环顾|四周|周围|看看|扫视|\bscan\b|\bobserve\b|\blook\s+around\b", RegexOptions.IgnoreCase);
        static readonly Regex Interact = new Regex(@"交互|触发|解锁|按一下|按\s*[QE]|按下|\binteract\b|\btrigger\b|\bpress\s+[qe]\b", RegexOptions.IgnoreCase);
        static readonly string[] Origins = { "任务起点", "起始位置", "起始点", "出发位置", "出发点", "原来的位置", "起点", "原点", "origin", "starting position", "starting point", "the start", "start" };
        public static bool LooksLikeMission(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            if (LooksLikeTargetLook(text)) return true;
            if (Regex.IsMatch(text, @"^(任务|执行任务|task)\s*[:：]", RegexOptions.IgnoreCase)) return true;
            if (Regex.IsMatch(text, @"哪些物体|哪些地方|哪些目标|你觉得|是否.*到|到.*了吗|走到.*了[吗么]|你现在(?:在|到)哪|为什么|怎么才能", RegexOptions.IgnoreCase)) return false;
            if (Regex.IsMatch(text, @"(?:如果|若|当|循环|重复|每隔|直到|否则).*(?:前往|走到|看向|看下|观察|挥手|挥舞|返回|回到|交互)", RegexOptions.IgnoreCase)) return true;
            return Regex.IsMatch(text, @"前往|走到|走向|去往|导航到|移动到|(?:返回|回到|走回|回)(?:去)?(?:(?:本次|这次)?任务|场景|本次|这次)?(?:原点|起点|起始位置|起始点|出发点|出发位置)|(?:^|让你|请你|先|请|你|帮我|麻烦你)(?:去|到|走近|靠近)\s*[^\s，。]+|做.*(?:动作|手势)|挥舞双手|双手挥舞|挥手|点头|摇头|^(?:请|现在)?(?:观察|环视|环顾)|\b(?:go|walk|head|navigate)\s+to\b|\breturn(?:\s+to)?\s+(?:origin|start|the start|starting position)\b", RegexOptions.IgnoreCase);
        }
        public static bool LooksLikeTargetLook(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            string s = Regex.Replace(text, "[“\"][^”\"]*[”\"]", "");
            if (Regex.IsMatch(s, @"为什么|是不是|是否|了吗|了么|刚才|昨天|假如|如果|没有办法|没法|不能|不可以")) return false;
            s = Regex.Replace(s, @"(?:但是|但|只)(?=看向|望向|注视|面向|把视角|将视角)", "，");
            foreach (string c in Split.Split(s))
                {
                if (Regex.IsMatch(c.Trim(), @"^(?:请|你|现在|先|暂时)*(?:不要|别|不用|禁止)")) continue;
                var m=Attention.Match(c); if(!m.Success) continue;
                string tail=c.Substring(m.Index+m.Length).Trim();
                // Existing direction/user gaze commands keep their own established route.
                if(Regex.IsMatch(tail,@"^(?:左边|右边|左侧|右侧|前方|前面|后面|上面|下面|我|用户|自己)(?:[，。！？!?,\s]|$)")) continue;
                return true;
                }
            return false;
        }
        public static MissionIntent Bind(string request, TaskTargetCatalog catalog)
        {
            var intent = new MissionIntent { original_text = request ?? "" };
            var ids = new List<string>(); var actions = new List<MissionRequiredAction>(); string currentTarget = "";
            string text = Regex.Replace(request ?? "", @"^(任务|执行任务|task)\s*[:：]\s*", "", RegexOptions.IgnoreCase);
            foreach (string raw in Split.Split(Regex.Replace(text, @"(?:但是|但|只)(?=看向|望向|注视|面向|把视角|将视角)", "，")))
            {
                string clause = raw.Trim();
                if (clause.Length == 0 || Regex.IsMatch(clause, @"^(?:(?:请你|请|你|先|现在|暂时)\s*)*(不要|别|不必|无需|不用|不许|禁止|do not\b|don't\b)", RegexOptions.IgnoreCase)) continue;
                // Speech content is data: "我正在去红绿灯" must not create another navigation.
                var speech = Regex.Match(clause, @"说一句|说一声|边走边说|一边说|\bsay\b|\bspeak\b", RegexOptions.IgnoreCase);
                if (speech.Success)
                {
                    intent.speak = true;
                    clause = Regex.Replace(clause.Substring(0, speech.Index), @"(?:同时|一边|边走边|并且|并)\s*$", "").Trim();
                    if (clause.Length == 0) continue;
                }
                string lookTarget = null, lookSource = null;
                var attention = Attention.Match(clause);
                if (attention.Success)
                {
                    lookSource = clause.Substring(attention.Index);
                    string lookTail = Regex.Replace(clause.Substring(attention.Index + attention.Length).Trim(), @"^(?:一下|那个|这个|场景中的|the\s+)", "", RegexOptions.IgnoreCase);
                    string error; int consumed;
                    if (!catalog.MatchStart(lookTail, out lookTarget, out consumed, out error)) { intent.error = error; return intent; }
                    clause = clause.Substring(0, attention.Index);
                }
                string gesture = TaskGestureSkills.Match(clause);
                bool clauseObserve = Observe.IsMatch(clause), clauseInteract = Interact.IsMatch(clause);
                intent.observe |= clauseObserve; intent.interact |= clauseInteract;
                intent.speak |= Regex.IsMatch(clause, @"一边.*说|边走边说|说一句|介绍|\bsay\b|\bspeak\b", RegexOptions.IgnoreCase);
                intent.express |= Regex.IsMatch(clause, @"微笑|表情|开心|\bsmile\b", RegexOptions.IgnoreCase);
                var matches = Movement.Matches(clause); bool bound = false;
                for (int i = 0; i < matches.Count; i++)
                {
                    var m = matches[i];
                    if ((m.Value == "站在" || m.Value == "停在") && Regex.IsMatch(clause.Substring(m.Index + m.Length), @"^(?:当前|这个|现在|这里|原地|此处|这儿)")) continue;
                    if (m.Value == "回" && m.Index + 1 < clause.Length && clause[m.Index + 1] == '报') continue;
                    if (m.Value == "到" && m.Index > 0 && "看得收听知".IndexOf(clause[m.Index - 1]) >= 0) continue;
                    string tail = clause.Substring(m.Index + m.Length).TrimStart(' ', '“', '"', '「', '《');
                    tail = Regex.Replace(tail, @"^(?:那个|这个|场景中的|场景里的|the\s+|a\s+)", "", RegexOptions.IgnoreCase);
                    string id = null, error; int consumed = 0;
                    foreach (string prefix in new[] { "本次任务起点", "这次任务起点", "本次出发位置", "本次任务出发点" }) if (tail.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) { id = "mission_origin"; consumed = prefix.Length; break; }
                    if(id==null) foreach (string alias in Origins) if (tail.StartsWith(alias, StringComparison.OrdinalIgnoreCase)) { id = "origin"; consumed = alias.Length; break; }
                    if (id == null && Regex.IsMatch(m.Value, @"^(返回|回|return)$", RegexOptions.IgnoreCase) && (tail.Length == 0 || tail == "来")) id = "origin";
                    if (id == null && !catalog.MatchStart(tail, out id, out consumed, out error))
                    { intent.error = error; intent.destination_ids = ids.ToArray(); return intent; }
                    string suffix = tail.Substring(MathfMin(consumed, tail.Length)).TrimStart(' ', '”', '"', '」', '》');
                    if (suffix.Length > 0 && !Regex.IsMatch(suffix, @"^(?:之下|下面|下|那[里儿边]|这[里儿边]|旁边|旁|附近|面前|前面|跟前|的位置|那里|处|并|并且|且|看|观察|环顾|环视|扫视|触发|交互|按|停|站|完成|做|再|去|到|回|走|前往|后|一下|告诉|汇报|一边|边|with\b|and\b|then\b|to\b|near\b|beside\b|look\b|observe\b|interact\b|press\b|stop\b|return\b|go\b)", RegexOptions.IgnoreCase))
                    { intent.error = "目标后缀无法确认：" + clause; intent.destination_ids = ids.ToArray(); return intent; }
                    ids.Add(id); currentTarget = id;
                    actions.Add(new MissionRequiredAction { kind = id == "origin" || id == "mission_origin" ? "return" : "navigate", target_id = id, source = clause });
                    intent.return_to_origin |= id == "origin" || id == "mission_origin"; bound = true;
                    // Do not parse movement characters embedded in a bound object's name.
                    int end = m.Index + m.Length + consumed;
                    while (i + 1 < matches.Count && matches[i + 1].Index < end) i++;
                }
                if (lookTarget != null) actions.Add(new MissionRequiredAction { kind="look_at", target_id=lookTarget, source=lookSource, camera_only=!Yixuan.Vision.GazeVoiceIntent.AllowsBodyAssist(request) });
                if (gesture != null) actions.Add(new MissionRequiredAction {kind="gesture", target_id="", action_id=gesture, source=clause});
                if (clauseObserve) actions.Add(new MissionRequiredAction { kind = "observe", target_id = currentTarget, source = clause });
                if (clauseInteract) actions.Add(new MissionRequiredAction { kind = "interact", target_id = currentTarget, source = clause });
                if (!bound && matches.Count == 0 && ids.Count == 0 && !intent.observe && !intent.interact)
                {
                    string id, error; int consumed;
                    if (catalog.MatchStart(clause, out id, out consumed, out error) && consumed == clause.Length)
                    { ids.Add(id); actions.Add(new MissionRequiredAction { kind = "navigate", target_id = id }); }
                }
            }
            intent.destination_ids = ids.ToArray(); intent.required_actions = actions.ToArray();
            intent.parallel_requirements=FindParallel(request,intent.required_actions,intent.speak,intent.express);
            if (ids.Count == 0 && actions.Count==0 && !intent.observe && !intent.speak && !intent.express) intent.error = "没有找到明确任务或目标，请使用场景物体名或完整路径";
            return intent;
        }
        public static MissionParallelRequirement[] FindParallel(string request, MissionRequiredAction[] actions, bool speak, bool express)
        {
            var requirements=new List<MissionParallelRequirement>();
            foreach(Match cue in Regex.Matches(request??"",@"同时|一边|边走边"))
            {
                string tail=request.Substring(cue.Index);
                var boundary=Regex.Match(tail,@"然后|最后|接着|[。；;]");
                if(boundary.Success) tail=tail.Substring(0,boundary.Index);
                int previous=-1, position=-1;
                for(int i=0;i<actions.Length;i++)
                {
                    int p=string.IsNullOrEmpty(actions[i].source)?-1:request.IndexOf(actions[i].source,StringComparison.Ordinal);
                    if(p>=0&&p<=cue.Index&&p>=position) {position=p;previous=i;}
                }
                if(previous<0) continue;
                if(speak&&Regex.IsMatch(tail,@"说|介绍|say|speak",RegexOptions.IgnoreCase)) requirements.Add(new MissionParallelRequirement{kind="speak",with_action=previous});
                if(express&&Regex.IsMatch(tail,@"表情|微笑|开心|smile",RegexOptions.IgnoreCase)) requirements.Add(new MissionParallelRequirement{kind="express",with_action=previous});
            }
            return requirements.ToArray();
        }
        static int MathfMin(int a, int b) { return a < b ? a : b; }
    }
}
