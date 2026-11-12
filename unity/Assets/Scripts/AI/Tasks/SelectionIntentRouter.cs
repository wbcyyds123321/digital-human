using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Yixuan.AI.Tasks
{
    /// <summary>
    /// One deterministic serializable visual-target command produced by the
    /// selection route guard. Field names mirror SemanticInteractionCommand so
    /// the semantic layer can map it without guessing.
    /// </summary>
    public sealed class SelectionGuardCommand
    {
        public string operation = "visual_target";
        public string direction = "";
        public string anchor_id = "";
        public string source = "";
        public string target = "";
        public string actions = "";
        public string constraints = "";
    }

    public sealed class SelectionIntentResult
    {
        /// <summary>An explicit selection/choose language was detected at all.</summary>
        public bool IsSelection;
        /// <summary>Explicit selection language that a single visual target cannot represent.</summary>
        public bool MultiStep;
        /// <summary>B rev1: the selection words occur only in evaluation/discussion/history
        /// or metalinguistic commentary, so this must never become an execution request.</summary>
        public bool NonAction;
        /// <summary>Machine reason for logs and deterministic tests.</summary>
        public string Reason = "not_selection";
        /// <summary>Non-null only for one executable selection of one described target.</summary>
        public SelectionGuardCommand Command;
    }

    /// <summary>
    /// Deterministic guard for explicit selection synonyms inside the semantic
    /// router. The LLM classifier remains the primary router; this guard only
    /// fires for an unequivocal instruction to select/choose a target, so an
    /// occasional model route=action cannot bypass the real visual selection
    /// chain. It deliberately:
    ///  - never triggers on colour/shape words alone, only on an explicit
    ///    selection verb (选中/选择/选一下/选出来/选上/挑中 ...);
    ///  - keeps negatives, hypotheses, capability questions, quotes, already
    ///    selected-target queries and ordinary navigation out of the override;
    ///  - reports a multi-step selection (先选A再选B / 选中A和B / 依次选...) as
    ///    pending instead of letting it fall through to the action/Lua lane;
    ///  - preserves no_move/no_board/no_sit/leftmost/any_match constraints.
    /// Pure C# (no UnityEngine dependency) so it can be replayed offline.
    /// </summary>
    public static class SelectionIntentRouter
    {
        // Verbs that explicitly ask to select/choose an object.
        static readonly Regex SelectionVerb = new Regex(
            @"(?:选中|选择|选一下|选出来|选上|挑中|选个|选那个|选这个|挑一个|挑一下)",
            RegexOptions.Compiled);

        // A looser mention used only to count clauses; never enough by itself.
        static readonly Regex SelectionMention = new Regex(
            @"(?:选中|选择|选一下|选出来|选上|挑中|选个|选那个|选这个|挑一个|挑一下)",
            RegexOptions.Compiled);

        // Negation directly attached before a selection verb (prefix is checked
        // after removing politeness fillers such as 帮我/给我/你/去).
        static readonly Regex NegatedVerbPrefix = new Regex(
            @"(?:别|不要|不用|不必|无需|不|先别|别再|莫|勿|禁止|不想|不愿)\s*(?:帮我|帮忙|给我|你|您|去|要|再)?\s*$",
            RegexOptions.Compiled);

        static readonly Regex Quoted = new Regex(@"[“”「」『』""]|(?:说|讲|念|引用)[:：]?\s*$",
            RegexOptions.Compiled);

        static readonly Regex Hypothetical = new Regex(
            @"(?:如果|假设|假如|要是|若是|万一|的话|比如说|比方说|比如|测试一下|举例)",
            RegexOptions.Compiled);

        // B rev1: selection language inside an evaluation, opinion, retold/history or
        // metalinguistic comment is NOT an instruction to execute. The patterns are
        // generic Chinese discourse forms, not case-specific sentences.
        static readonly Regex EvaluativeStance = new Regex(
            @"(?:我|我们|自己)?(?:不|不太|不很|有点不|挺|很|比较|特别|真的)?(?:喜欢|讨厌|反感|偏爱|偏好|欣赏|赞成|反对|接受|习惯)[^，。；;]{0,30}(?:选中|选择|挑中|选上|选出来)",
            RegexOptions.Compiled);
        static readonly Regex MetaSelectionReference = new Regex(
            @"(?:这样的|这种|这个|那个|这些|那种)(?:操作|说法|行为|动作|做法|话|事)",
            RegexOptions.Compiled);
        static readonly Regex OpinionJudgement = new Regex(
            @"(?:我觉得|我认为|我感觉|在我看来|看起来|听起来)[^，。；;]{0,40}(?:不合理|不合适|不好|不对|没必要|太麻烦|很麻烦|很奇怪|不该|不应该|不赞成|反对)",
            RegexOptions.Compiled);
        static readonly Regex PastSelectionStatement = new Regex(
            @"(?:^|[，。；,;])\s*我(?:刚才|刚刚|之前|上次|曾经|以前|当时|上回)[^，。；;]{0,20}(?:选中|选择|挑中)(?:了|过)",
            RegexOptions.Compiled);
        static readonly Regex DiscussionVerb = new Regex(
            @"(?:评价|讨论|描述|解释|分析|说说|谈谈|聊聊|回忆|复述|转述|举例说明)",
            RegexOptions.Compiled);
        // B rev2: an explicit imperative clause AFTER an evaluation/discussion span wins.
        // Strong markers are unambiguous commands; weak sequencing words only count when
        // the clause is not itself a metalinguistic reference ("再选中...这样的操作" is
        // still part of the evaluated description, not a new command).
        static readonly Regex StrongCommandMarker = new Regex(
            @"^(?:请|帮我|帮忙|给我|麻烦你?|重复|停止|取消|开始|继续|执行|马上|立刻|现在|试|做)",
            RegexOptions.Compiled);
        static readonly Regex WeakCommandMarker = new Regex(
            @"^(?:你|您|先|再|然后|接着|把|将|去|要|来)",
            RegexOptions.Compiled);
        static readonly Regex DirectiveVerb = new Regex(
            @"(?:选中|选择|挑中|选上|选出来|选一下|停止|取消|开始|继续|执行|重复|做|走|去|来|移动|转身|挥手|等待)",
            RegexOptions.Compiled);

        static readonly Regex CapabilityQuestion = new Regex(
            @"(?:能不能|可不可以|会不会|能否|是否|可以|能|会|有).{0,8}(?:选中|选择|选出来)" +
            @"|(?:选中|选择).{0,6}(?:吗|呢|能力|功能|支持|实现)",
            RegexOptions.Compiled);

        static readonly Regex SelectionQuery = new Regex(
            @"(?:当前|现在|目前|已经|刚才|刚刚|上次|之前|上一次).{0,6}(?:选中|选择|选中的|选的)" +
            @"|(?:选中|选择).{0,4}(?:了|过).{0,4}(?:什么|哪个|哪一个|哪些|哪几个|吗|呢|对象|东西)" +
            @"|(?:选中|选择).{0,4}(?:的|了)?\s*(?:是|为)\s*(?:什么|哪个|哪一个|谁)" +
            @"|(?:什么|哪个|哪一个|哪些|哪几个).{0,8}(?:被|已|当前|现在)?\s*(?:选中|选择)" +
            @"|(?:选中|选择).{0,6}(?:在哪|多远|位置|坐标|介绍|能力|功能)" +
            @"|(?:查|看|问).{0,4}(?:选中|选择)",
            RegexOptions.Compiled);

        // Driving cannot be represented by one visual target handoff.
        static readonly Regex UnsupportedVehicleAction = new Regex(
            @"(?:开车|驾驶|行驶|导航|停车|启动车辆|骑车|开过去|开到)",
            RegexOptions.Compiled);

        static readonly Regex PronounTarget = new Regex(
            @"^(?:它|那个|这个|那个东西|那个玩意儿|刚才那个|刚刚那个|刚才选中的那个|刚选中的那个|刚才说的那个|上面那个|之前那个)$",
            RegexOptions.Compiled);

        static readonly Regex PronounSelectionSentence = new Regex(
            @"^(?:请|帮我|麻烦|你|先|再)?\s*(?:把|将)?\s*(?:它|那个|这个|刚才那个|刚刚那个|刚才选中的那个|刚选中的那个)\s*(?:选中|选上|选出来|选一下|选择)\s*(?:吧|啊|呀)?$",
            RegexOptions.Compiled);

        static readonly string[] LeadingFillers =
        {
            "请", "麻烦你", "麻烦", "劳驾", "帮我", "帮忙", "给我", "你", "您", "先", "现在", "目前",
            "马上", "立刻", "去", "把", "将", "重新", "再次", "再", "试着", "继续", "然后",
            "随便", "任意", "随机", "顺便"
        };

        // Negative constraints about movement/boarding/sitting.
        static readonly Regex NoMove = new Regex(
            @"(?:别|不要|不用|不必|先别|先不要|无需)\s*(?:移动|动|走|走过去|过去|挪)",
            RegexOptions.Compiled);

        static readonly Regex NoBoard = new Regex(
            @"(?:别|不要|不用|先别|先不要)\s*(?:上车|坐车)|不上车",
            RegexOptions.Compiled);

        static readonly Regex NoSit = new Regex(
            @"(?:别|不要|不用|先别|先不要)\s*(?:坐下|坐)|不坐下|不坐",
            RegexOptions.Compiled);

        static readonly Regex AnyMatch = new Regex(
            @"(?:任意|随便|哪个都行|随便一个|任意一个|随便哪个|哪一个都)", RegexOptions.Compiled);

        static readonly Regex Leftmost = new Regex(@"(?:最左|最左边)", RegexOptions.Compiled);
        static readonly Regex LeftmostNegated = new Regex(
            @"(?:别|不要|不用|先别)\s*(?:选|要)?\s*(?:最左|最左边)", RegexOptions.Compiled);

        // near_crosshair is an explicit spatial condition only: a plain "眼前这个红方块"
        // must stay current_view, otherwise the guard would add an unnecessary target
        // restriction the user never asked for.
        static readonly Regex NearCrosshair = new Regex(
            @"(?:准星旁|准星附近|准星旁边|准星那|准星旁边那个)", RegexOptions.Compiled);

        static readonly Regex ApproachAction = new Regex(
            @"(?:走近|走过去|到它旁边|到那(?:个|辆|张|栋)旁边|到旁边|靠近|过去拿|过去看看)",
            RegexOptions.Compiled);

        static readonly Regex SitAction = new Regex(@"(?:坐下|坐到|坐上去|坐一下)", RegexOptions.Compiled);
        static readonly Regex BoardAction = new Regex(@"(?:上车|坐车|进车)", RegexOptions.Compiled);
        static readonly Regex StandAction = new Regex(@"(?:站起来|起身|起立)", RegexOptions.Compiled);
        static readonly Regex QueryAction = new Regex(
            @"(?:在哪|多远|位置|方位|介绍|能力|功能|什么样|离我)", RegexOptions.Compiled);

        static readonly Regex UnclearTarget = new Regex(
            @"^(?:它旁边|那个旁边|旁边|一下|东西|物体|目标)$", RegexOptions.Compiled);

        // Text-level list of several targets ("红色、绿色、蓝色", "红色和绿色方块").
        static readonly Regex MultiTargetList = new Regex(
            @"、|(?:和|与|及|还有).{0,16}(?:物体|方块|立方体|球体|圆柱|胶囊|目标|建筑|车|椅子|长椅)",
            RegexOptions.Compiled);

        // Tail after the selection clause that one confirmed target can still serve.
        static readonly Regex SupportedTail = new Regex(
            @"选中|选择|选上|选出来|选一下|然后|接着|之后|以后|最后|再|先|走过去|走近|靠近|过去|到它旁边|到旁边|旁边|坐下|坐到|坐一下|上车|坐车|站起来|起身|在哪|多远|位置|能力|介绍|告诉我|说一下|汇报|看看|查询|它|吧|啊|呀|哦|嘛|了|的|，|,|。|！|!|？|\?|\s",
            RegexOptions.Compiled);

        // Cut the target phrase at a trailing constraint or follow-up connector.
        // Sequencing words ALWAYS end the target phrase here: leaving "再挥手" in the
        // target would otherwise execute select and silently drop the second step.
        static readonly Regex TargetTailCut = new Regex(
            @"(?:先)?(?:别|不要|不用|不必)\s*(?:移动|动|走|走过去|过去|上车|下车|坐下|坐|挪)" +
            @"|然后|接着|之后|以后|最后|再" +
            @"|先(?=别|不要)",
            RegexOptions.Compiled);

        /// <summary>Classify one latest user input. Never throws.</summary>
        public static SelectionIntentResult Classify(string input)
        {
            var result = new SelectionIntentResult();
            if (string.IsNullOrWhiteSpace(input)) return result;
            string text = input.Trim();
            if (!SelectionMention.IsMatch(text) && !PronounSelectionSentence.IsMatch(text))
                return result;

            result.IsSelection = true;

            // 1. Quotes / retold speech are not live instructions.
            if (Quoted.IsMatch(text))
            {
                result.IsSelection = false;
                result.Reason = "quoted_selection";
                return result;
            }

            // 2. Hypothetical / hypothetical examples.
            if (Hypothetical.IsMatch(text))
            {
                result.IsSelection = false;
                result.Reason = "hypothetical_selection";
                return result;
            }

            // 3. Queries about an already confirmed selection (must stay status/query).
            // Checked before the capability heuristic: "当前选中了什么" is a status
            // query, not an ability question, even though 什么 contains 么.
            if (SelectionQuery.IsMatch(text))
            {
                result.IsSelection = false;
                result.Reason = "selection_query_or_question";
                return result;
            }

            // 4. Capability / abstract ability questions.
            if (CapabilityQuestion.IsMatch(text))
            {
                result.IsSelection = false;
                result.Reason = "capability_question";
                return result;
            }

            // 5. Any other question form about selection is not a live instruction.
            if (ContainsQuestionMarker(text))
            {
                result.IsSelection = false;
                result.Reason = "selection_query_or_question";
                return result;
            }

            // 5b. B rev1: evaluation / opinion / discussion / retold history is not an
            // execution request even when it contains multiple selection verbs and
            // sequencing words. Keep it on the normal chat/status route.
            if (IsNonActionSelectionDiscussion(text))
            {
                result.IsSelection = false;
                result.NonAction = true;
                result.Reason = "selection_evaluation_or_discussion";
                return result;
            }

            var clauses = SplitClauses(text);
            int positiveSelectionClauses = 0;
            int negatedSelectionClauses = 0;
            foreach (string clause in clauses)
            {
                bool mention = SelectionVerb.IsMatch(clause) || PronounSelectionSentence.IsMatch(clause.Trim());
                if (!mention) continue;
                if (ClauseIsNegated(clause)) negatedSelectionClauses++;
                else positiveSelectionClauses++;
            }

            // 5. All selection language negated -> the user asked NOT to select.
            if (positiveSelectionClauses == 0 && negatedSelectionClauses > 0)
            {
                result.IsSelection = false;
                result.Reason = "negated_selection";
                return result;
            }

            // Mixed positive/negative selection is not a single executable command.
            if (positiveSelectionClauses > 0 && negatedSelectionClauses > 0)
            {
                result.MultiStep = true;
                result.Reason = "mixed_select_and_negation_pending";
                return result;
            }

            int mentions = SelectionMention.Matches(text).Count;
            // Multiple selection clauses / an explicit list of several targets:
            // keep it pending instead of dropping the later clauses into Lua.
            if (positiveSelectionClauses > 1 || mentions > 1 || MultiTargetList.IsMatch(text))
            {
                result.MultiStep = true;
                result.Reason = "multi_step_selection_pending";
                return result;
            }

            string core = FirstSelectionClause(text);
            string target = StripSelectionScaffolding(core);
            // "它" / "刚才那个" is a legitimate reused last-confirmed reference and
            // must survive as pronoun instead of being rejected as an unclear target.
            bool pronoun = PronounTarget.IsMatch(target) || PronounSelectionSentence.IsMatch(text.Trim());
            if (pronoun) target = "它";
            if (!pronoun && (string.IsNullOrEmpty(target) || target.Length > 200 || UnclearTarget.IsMatch(target)))
            {
                result.Reason = "selection_target_unclear";
                return result;
            }
            if (SelectionMention.IsMatch(target))
            {
                result.MultiStep = true;
                result.Reason = "multi_step_selection_pending";
                return result;
            }
            // Selection plus driving is not representable by one visual target handoff.
            if (UnsupportedVehicleAction.IsMatch(text) &&
                !Regex.IsMatch(text, @"(?:别|不要|不用|先别)\s*(?:开车|驾驶|上车|下车)"))
            {
                result.MultiStep = true;
                result.Reason = "selection_plus_unsupported_vehicle_action_pending";
                return result;
            }
            // A follow-up clause that the confirmed-target handoff cannot represent
            // must stay pending, never be silently swallowed. The remainder of the
            // selection clause AFTER the extracted target is audited too, so a
            // punctuation-free "选中红色立方体再挥手" cannot drop "再挥手".
            // Negative constraints (先别走过去/不上车/不坐下) are representable and
            // are removed first so they do not look like unsupported follow-ups.
            string clauseTail = "";
            int targetAt = core.IndexOf(target, StringComparison.Ordinal);
            if (targetAt >= 0) clauseTail = core.Substring(targetAt + target.Length);
            string tail = (clauseTail + " " + TextAfterSelection(text, core)).Trim();
            string tailWithoutNegatives = NoMove.Replace(tail, "");
            tailWithoutNegatives = NoBoard.Replace(tailWithoutNegatives, "");
            tailWithoutNegatives = NoSit.Replace(tailWithoutNegatives, "");
            string residual = SupportedTail.Replace(tailWithoutNegatives, "");
            if (residual.Trim().Length > 0)
            {
                result.MultiStep = true;
                result.Reason = "selection_plus_unsupported_followup_pending";
                return result;
            }

            var actions = new List<string> { "select" };
            if (ApproachAction.IsMatch(text))
            {
                if (SitAction.IsMatch(text)) actions.Add("sit");
                else actions.Add("approach");
            }
            else if (SitAction.IsMatch(text)) actions.Add("sit");
            if (BoardAction.IsMatch(text)) actions.Add("board");
            if (StandAction.IsMatch(text)) actions.Add("stand");
            if (QueryAction.IsMatch(text)) actions.Add("query");

            var constraints = new List<string>();
            constraints.Add(pronoun ? "pronoun" : "current_view");
            if (AnyMatch.IsMatch(text)) constraints.Add("any_match");
            if (NoMove.IsMatch(text))
            {
                constraints.Add("no_move");
                actions.Remove("approach");
                actions.Remove("sit");
                actions.Remove("stand");
                actions.Remove("board");
            }
            if (NoBoard.IsMatch(text)) { constraints.Add("no_board"); actions.Remove("board"); }
            if (NoSit.IsMatch(text)) { constraints.Add("no_sit"); actions.Remove("sit"); }
            if (Leftmost.IsMatch(text) && !LeftmostNegated.IsMatch(text)) constraints.Add("leftmost");
            if (NearCrosshair.IsMatch(text)) constraints.Add("near_crosshair");

            result.Command = new SelectionGuardCommand
            {
                operation = "visual_target",
                direction = "",
                anchor_id = "",
                source = text,
                target = target,
                actions = string.Join(",", actions.ToArray()),
                constraints = string.Join(",", constraints.ToArray()),
            };
            result.Reason = "explicit_single_selection";
            return result;
        }

        static readonly Regex DiscussionPivot = new Regex(
            @"^(?:不过|但是|但|可是|然而|而且|另外|所以|那|还有|并且|同时)\s*",
            RegexOptions.Compiled);

        static bool IsEvaluationCarrier(string clause)
        {
            string c = (clause ?? "").Trim();
            if (c.Length == 0) return false;
            // Stance/opinion/past statements are evaluation even if their object is a
            // selection sequence. A discussion verb only evaluates selection when its
            // grammatical object is that selection act/operation, not when it is a
            // report about an already-performed action ("然后解释它的位置").
            if (EvaluativeStance.IsMatch(c) || OpinionJudgement.IsMatch(c) || PastSelectionStatement.IsMatch(c))
                return true;
            if (DiscussionVerb.IsMatch(c) &&
                (SelectionVerb.IsMatch(c) || PronounSelectionSentence.IsMatch(c) || MetaSelectionReference.IsMatch(c)))
                return true;
            return false;
        }

        static bool IsExplicitExecutionCommand(string clause)
        {
            string c = (clause ?? "").Trim();
            if (c.Length == 0) return false;
            if (IsEvaluationCarrier(c)) return false;
            bool pivot = DiscussionPivot.IsMatch(c);
            string stripped = DiscussionPivot.Replace(c, "");
            if (!DirectiveVerb.IsMatch(stripped)) return false;
            bool meta = MetaSelectionReference.IsMatch(stripped);
            // Strong imperative markers are unambiguous new commands even after an
            // evaluation. A pivot word + weak sequencer ("不过/但是 + 先/再") also
            // explicitly turns the sentence toward execution. A lone weak sequencer
            // ("再选中…") is evaluated-content continuation, not a command.
            if (StrongCommandMarker.IsMatch(stripped)) return true;
            if (pivot && WeakCommandMarker.IsMatch(stripped)) return true;
            if (meta && StrongCommandMarker.IsMatch(stripped)) return true;
            return false;
        }

        static bool IsNonActionSelectionDiscussion(string text)
        {
            // B rev3: classify by clause pragmatic role. Evaluation/discussion is a
            // carrier only when it evaluates the selection act itself; later report
            // clauses ("解释它的位置", "描述它") do not turn a preceding imperative into
            // chat. Within an evaluation span, a lone weak "再选中…" is content
            // continuation; only a strong/new command or an explicit pivot command wins.
            var clauses = SplitClauses(text);
            if (clauses.Count == 0) return false;
            int evaluationAt = -1;
            for (int i = 0; i < clauses.Count; i++)
            {
                if (IsEvaluationCarrier(clauses[i])) { evaluationAt = i; break; }
            }
            if (evaluationAt < 0) return false;
            for (int i = evaluationAt + 1; i < clauses.Count; i++)
            {
                if (IsExplicitExecutionCommand(clauses[i])) return false;
            }
            return true;
        }

        /// <summary>True only for one unambiguous selection instruction.</summary>
        public static bool IsSingleSelectionCommand(string input)
        {
            var result = Classify(input);
            return result.IsSelection && !result.MultiStep && result.Command != null;
        }

        static bool ContainsQuestionMarker(string text)
        {
            return text.IndexOf('?') >= 0 || text.IndexOf('？') >= 0 ||
                   text.Contains("吗") || text.Contains("呢") ||
                   text.Contains("什么") || text.Contains("哪个") || text.Contains("哪一个") ||
                   text.Contains("哪些") || text.Contains("哪几个") || text.Contains("怎么") ||
                   text.Contains("为什么") || text.Contains("在哪") || text.Contains("多少") ||
                   text.Contains("多远");
        }

        static bool ClauseIsNegated(string clause)
        {
            foreach (Match match in SelectionVerb.Matches(clause))
            {
                string prefix = clause.Substring(0, match.Index);
                string compact = Regex.Replace(prefix, @"\s+", "");
                if (NegatedVerbPrefix.IsMatch(compact)) return true;
            }
            return false;
        }

        static string FirstSelectionClause(string text)
        {
            foreach (string clause in SplitClauses(text))
            {
                string trimmed = clause.Trim();
                if (string.IsNullOrEmpty(trimmed)) continue;
                if (SelectionVerb.IsMatch(trimmed) || PronounSelectionSentence.IsMatch(trimmed)) return trimmed;
            }
            return text.Trim();
        }

        static string TextAfterSelection(string text, string core)
        {
            int at = text.IndexOf(core, StringComparison.Ordinal);
            if (at < 0) return "";
            int start = at + core.Length;
            if (start >= text.Length) return "";
            return text.Substring(start).Trim();
        }

        static string StripSelectionScaffolding(string clause)
        {
            string core = clause.Trim();
            core = core.Trim('“', '”', '「', '」', '『', '』', '"', '\'', ' ', '\t', '\n', '\r');
            // Leading politeness / verb-preposition scaffolding, repeated.
            bool changed = true;
            int guard = 0;
            while (changed && guard++ < 8)
            {
                changed = false;
                foreach (string filler in LeadingFillers)
                {
                    if (string.IsNullOrEmpty(filler)) continue;
                    if (core.StartsWith(filler, StringComparison.Ordinal))
                    {
                        core = core.Substring(filler.Length).TrimStart();
                        changed = true;
                        break;
                    }
                }
            }
            // Leading selection verb.
            core = Regex.Replace(core, @"^(?:选中|选择|选一下|选出来|选上|挑中|选个|选那个|选这个|挑一个|挑一下)\s*", "");
            // Cut trailing constraint / follow-up connectors from the target phrase.
            string[] pieces = TargetTailCut.Split(core);
            if (pieces.Length > 0) core = pieces[0];
            // Postposed verb ("把红色立方体选中").
            core = Regex.Replace(core, @"\s*(?:选中|选上|选出来|选一下|选择)\s*$", "");
            // Trailing particles.
            core = Regex.Replace(core, @"\s*(?:吧|啊|呀|哦|嘛|呢|哈|啦|的)\s*$", "");
            core = Regex.Replace(core, @"\s*(?:来|去|一下)\s*$", "");
            // Any remaining leading conjunction/preposition.
            core = Regex.Replace(core, @"^(?:把|将|再|然后|接着)\s*", "");
            return core.Trim();
        }

        static List<string> SplitClauses(string text)
        {
            var parts = new List<string>();
            var current = new System.Text.StringBuilder();
            foreach (char c in text)
            {
                if (c == '，' || c == ',' || c == '。' || c == '.' || c == '！' || c == '!' ||
                    c == '？' || c == '?' || c == '；' || c == ';' || c == '：' || c == ':' ||
                    c == '\n' || c == '\r' || c == '、')
                {
                    if (current.Length > 0) { parts.Add(current.ToString()); current.Length = 0; }
                }
                else current.Append(c);
            }
            if (current.Length > 0) parts.Add(current.ToString());
            return parts;
        }
    }
}
