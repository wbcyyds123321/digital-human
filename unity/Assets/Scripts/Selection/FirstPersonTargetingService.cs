using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using Newtonsoft.Json;
using Yixuan.AI;
using Yixuan.AI.Tasks;
using Yixuan.World;
using Yixuan.Stage5;
using Yixuan.Stage4;

namespace Yixuan.Pick
{
    [Serializable]
    public class FpVisualRawReply
    {
        public string status;
        public float[] point_uv;
        public float[] bbox_uv;
        public string description;
        public string error;
        public int candidate_index;
        public int[] matching_indices;
    }

    public sealed class FpVisualLocateResult
    {
        public bool Ok;
        public string Error = "";
        public string Raw = "";
        public FpVisualRawReply Reply;
        public string Model = "";
        public int HttpStatus;
        public float Seconds;
        public bool FromBackoff;
        public Yixuan.Vision.VisionServiceDiagnostic Diagnostic;
    }

    /// <summary>
    /// Generic (non-chair-specific) first-person visual target locator. Returns
    /// located/not_found/ambiguous plus a top-left 0..1 bbox and a visible-surface
    /// point on the NORMAL top-left RGB image. Never receives entity ids or world
    /// coordinates as ground truth.
    /// </summary>
    public static class FpVisualClient
    {
        public const int MaxAttempts = 1;

        public static IEnumerator Locate(Yixuan.Vision.QwenVisionClient vision, string instruction,
            byte[] jpeg, byte[] annotatedJpeg, Func<bool> cancelled, Action<FpVisualLocateResult> done)
        {
            var result = new FpVisualLocateResult();
            if (vision == null) { result.Error = "专用视觉客户端不可用"; done(result); yield break; }
            Yixuan.Vision.VisionGroundingReply response = null;
            yield return vision.AskGrounded(instruction, jpeg, annotatedJpeg, cancelled, reply => response = reply);
            if (response == null) { result.Error = "empty_vision_response"; done(result); yield break; }
            result.Model = response.Model;
            result.HttpStatus = response.HttpStatus;
            result.Seconds = response.Seconds;
            result.FromBackoff = response.FromBackoff;
            result.Diagnostic = response.Diagnostic;
            result.Raw = response.Raw;
            result.Error = response.Error;
            if (string.IsNullOrEmpty(result.Error))
            {
                try
                {
                    int first = response.Content.IndexOf('{'), last = response.Content.LastIndexOf('}');
                    if (first < 0 || last <= first) throw new FormatException("no JSON object");
                    result.Reply = JsonConvert.DeserializeObject<FpVisualRawReply>(response.Content.Substring(first, last - first + 1));
                    result.Ok = result.Reply != null && !string.IsNullOrEmpty(result.Reply.status);
                    if (!result.Ok) result.Error = "invalid_grounding_reply";
                }
                catch (Exception exception) { result.Error = "parse_failed: " + exception.Message; }
            }
            done(result);
        }
    }

    [Serializable]
    public sealed class FpVisualRequest
    {
        public string original_text = "";
        public string target_description = "";
        public bool select, query, approach, sit, board, stand;
        public bool pronoun, current_view, any_match;
        public bool forbid_move, forbid_board, forbid_sit;
        public bool leftmost, near_crosshair;
        public string reference_note = "";
        public string raw_constraints = "";
        public string effective_constraints = "";
        public string constraint_guard_reason = "";
    }

    /// <summary>Generic natural-language parsing of visual target commands.</summary>
    public static class FpVisualLanguage
    {
        public static bool LooksLikeVisualTarget(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            return ContainsAny(text, "眼前", "眼前这", "这张", "那把", "那辆", "那栋", "那个", "准星", "最左", "最右",
                "左边那", "右边那", "红色大巴", "公交", "大巴", "椅子", "长椅", "楼", "树", "海报", "路牌",
                "它", "刚才那", "选中", "选出来", "对准", "指给我", "看看它", "它在哪", "离我多远") &&
                   (ContainsAny(text, "选", "选中", "选出来", "对准", "指给我", "哪", "多远", "在哪", "看看", "椅子上", "坐下", "坐", "旁边", "走近", "别上去") ||
                    ContainsAny(text, "它", "刚才那"));
        }

        public static FpVisualRequest Parse(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            var r = new FpVisualRequest { original_text = text };
            r.pronoun = ContainsAny(text, "它", "刚才那", "那个东西", "刚刚那个");
            r.select = ContainsAny(text, "选中", "选出来", "选一下", "给我看", "指给我", "对准", "点击", "选中它");
            r.query = ContainsAny(text, "在哪", "离我多远", "多远", "什么能力", "能干什么", "能做什么", "介绍一下", "位置", "方位");
            r.approach = ContainsAny(text, "到它旁边", "走到它旁边", "走近", "走过去", "到旁边", "旁边", "靠近", "过去", "到它那儿", "到那辆", "到那张");
            r.sit = ContainsAny(text, "坐下", "坐一会儿", "歇一会儿", "坐着", "坐一坐", "坐到");
            r.stand = ContainsAny(text, "起来吧", "站起来", "起身", "不坐了");
            r.board = ContainsAny(text, "上车", "上车吧", "坐车");
            r.forbid_move = ContainsAny(text, "先别动", "先别走", "别动", "不要动", "停着", "别走过去");
            r.forbid_board = ContainsAny(text, "别上车", "不要上车", "先别上车", "不上车");
            r.forbid_sit = ContainsAny(text, "先别坐", "别坐下", "不要坐下");
            r.leftmost = ContainsAny(text, "最左", "最左边");
            r.near_crosshair = ContainsAny(text, "准星旁", "准星附近", "准星旁边", "正前方", "眼前这张", "眼前这");
            if (r.board && ContainsAny(text, "别上车", "不要上车", "不上车")) r.board = false;
            if (r.sit && r.forbid_sit) r.sit = false;
            r.target_description = StripVerbs(text);
            return r;
        }

        static string StripVerbs(string text)
        {
            string t = text;
            foreach (var token in new[] { "请", "帮我", "麻烦", "把", "将", "这张", "那把", "那辆", "那栋", "眼前这张", "眼前这",
                         "选中", "选出来", "选一下", "给我看看", "给我看", "指给我", "对准", "看看", "看下", "看看它", "它在哪",
                         "离我多远", "多远", "到它旁边", "走到它旁边", "走近", "走过去", "别上车", "先别上车", "不要上车",
                         "先别走", "先别动", "坐下", "坐一会儿", "歇一会儿", "起来吧", "站起来", "能干什么", "能做什么", "介绍一下" })
            {
                t = t.Replace(token, " ");
            }
            t = t.Replace("，", " ").Replace(",", " ").Trim();
            if (t.Length == 0) t = text.Trim();
            return t;
        }

        static bool ContainsAny(string text, params string[] keys)
        {
            foreach (var key in keys) if (text.IndexOf(key, StringComparison.Ordinal) >= 0) return true;
            return false;
        }
    }

    /// <summary>
    /// B production visual targeting service. One generic path for select/query/
    /// approach/sit/stand/board-safety. It captures the CURRENT real EyeCamera view
    /// (no pre-centering, no old-bench fallback), locates with the visual model,
    /// binds only the model bbox/point to the ID image, aims the real gimbal, then
    /// requires the new frame centre to hit the SAME entity before publishing the
    /// shared selection record.
    /// </summary>
    public sealed class FirstPersonTargetingService : MonoBehaviour
    {
        static string TargetClassFor(string description)
        {
            string d = description ?? "";
            string lower = d.ToLowerInvariant();
            var classes = new HashSet<string>(StringComparer.Ordinal);
            // Collect every category mentioned, including a relation's reference
            // object. The model still sees the complete description; a class is
            // attached only when the description names one unambiguous category.
            if (d.IndexOf("圆柱", StringComparison.Ordinal) >= 0 || lower.Contains("cylinder")) classes.Add("cylinder");
            if (d.IndexOf("胶囊", StringComparison.Ordinal) >= 0 || lower.Contains("capsule")) classes.Add("capsule");
            if (d.IndexOf("方块", StringComparison.Ordinal) >= 0 || d.IndexOf("立方体", StringComparison.Ordinal) >= 0 ||
                d.IndexOf("正方体", StringComparison.Ordinal) >= 0 || lower.Contains("cube") || lower.Contains("block"))
                classes.Add("cube");
            if (d.IndexOf("球", StringComparison.Ordinal) >= 0 || lower.Contains("sphere") || lower.Contains("ball"))
                classes.Add("sphere");
            if (d.IndexOf("椅", StringComparison.Ordinal) >= 0 || d.IndexOf("凳", StringComparison.Ordinal) >= 0)
                classes.Add("bench");
            if (d.IndexOf("公交", StringComparison.Ordinal) >= 0 || d.IndexOf("大巴", StringComparison.Ordinal) >= 0 ||
                d.IndexOf("车", StringComparison.Ordinal) >= 0) classes.Add("vehicle");
            if (d.IndexOf("楼", StringComparison.Ordinal) >= 0 || d.IndexOf("建筑", StringComparison.Ordinal) >= 0 ||
                d.IndexOf("房", StringComparison.Ordinal) >= 0) classes.Add("building");
            if (d.IndexOf("树", StringComparison.Ordinal) >= 0) classes.Add("tree");
            if (d.IndexOf("海报", StringComparison.Ordinal) >= 0) classes.Add("poster");
            if (classes.Count != 1) return "";
            foreach (string only in classes) return only;
            return "";
        }


        public static FirstPersonTargetingService Instance { get; private set; }
        public static Func<FpVisualRequest, int, FpVisualLocateResult> TestLocateHook;
        public static float TestLocateDelaySeconds;
        public static bool SaveEvidence = true;
        public static string EvidenceRoot = "";
        public static int LastGeneration { get { return _generation; } }
        public static bool IsRunning { get { return _run != null; } }
        public static string LastStatus { get; private set; } = "idle";
        public static string LastReply { get; private set; } = "";
        public static string LastError { get; private set; } = "";
        public static string LastPublishedEntity { get; private set; } = "";
        public static bool MovementStarted { get; private set; }
        public static int LocateCalls { get; private set; }
        public static Vector3 InitialCharacterPosition { get; private set; }
        public static int AimIterations { get; private set; }

        sealed class RunState
        {
            public int generation;
            public int brokerToken;
            public FpVisualRequest request;
            public ChatSession session;
            public Coroutine coroutine;
        }

        static RunState _run;
        static int _generation;
        static int _attempt;

        static void AutoBootstrap()
        {
            EnsureInstance();
        }

        static void EnsureInstance()
        {
            if (Instance != null) return;
            var go = new GameObject("FirstPersonTargetingService");
            go.hideFlags = HideFlags.HideAndDontSave;
            DontDestroyOnLoad(go);
            Instance = go.AddComponent<FirstPersonTargetingService>();
        }

        /// <summary>Production semantic-route entry: the LLM semantic classifier supplies the target/actions/constraints, not a sentence table.</summary>
        /// <summary>
        /// Pure production conversion boundary for semantic visual requests. It
        /// preserves raw model constraints but removes a model-invented absolute
        /// leftmost when the latest real user text only expressed a reference
        /// relation (X left of Y), not a whole-image most-left request.
        /// </summary>
        public static FpVisualRequest BuildSemanticRequestForReplay(string input, SemanticVisualTarget semantic)
        {
            if (semantic == null) return null;
            var request = new FpVisualRequest
            {
                original_text = input ?? "",
                target_description = semantic.target ?? "",
                raw_constraints = semantic.constraints ?? "",
                effective_constraints = semantic.constraints ?? "",
            };
            if (HasCsvToken(request.raw_constraints, "leftmost") && !HasAffirmativeAbsoluteLeftmost(input))
            {
                request.effective_constraints = RemoveCsvToken(request.raw_constraints, "leftmost");
                request.constraint_guard_reason = "dropped_unsupported_absolute_leftmost";
            }
            AddCsvActions(request, semantic.actions);
            AddCsvConstraints(request, request.effective_constraints);
            if (!request.select && !request.query && !request.approach && !request.sit && !request.stand && !request.board)
                request.select = true;
            return request;
        }

        public static bool BeginFromSemantic(ChatSession session, string input, SemanticVisualTarget semantic, int semanticGeneration)
        {
            if (session == null || semantic == null || string.IsNullOrEmpty(semantic.target)) return false;
            var request = BuildSemanticRequestForReplay(input, semantic);
            if (request == null) return false;
            Debug.Log("[FirstPersonTargeting] semantic constraints raw=" + request.raw_constraints +
                      " effective=" + request.effective_constraints +
                      " guard=" + request.constraint_guard_reason);
            return StartRun(session, request);
        }

        static bool HasCsvToken(string csv, string token)
        {
            if (string.IsNullOrEmpty(csv)) return false;
            foreach (var raw in csv.Split(new[] { ',', '，', ';', '；' }, StringSplitOptions.RemoveEmptyEntries))
                if (string.Equals(raw.Trim(), token, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        static string RemoveCsvToken(string csv, string token)
        {
            if (string.IsNullOrEmpty(csv)) return "";
            var kept = new List<string>();
            foreach (var raw in csv.Split(new[] { ',', '，', ';', '；' }, StringSplitOptions.RemoveEmptyEntries))
                if (!string.Equals(raw.Trim(), token, StringComparison.OrdinalIgnoreCase)) kept.Add(raw.Trim());
            return string.Join(",", kept.ToArray());
        }

        /// <summary>
        /// True only for an affirmative whole-image/extreme left request. Relative
        /// X-left-of-Y phrases and negated "别选最左" mentions are not affirmative.
        /// </summary>
        static bool HasAffirmativeAbsoluteLeftmost(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            int at = 0;
            while ((at = text.IndexOf("最左", at, StringComparison.Ordinal)) >= 0)
            {
                int clauseStart = at - 1;
                while (clauseStart >= 0 && "，,。！？；;：:\n".IndexOf(text[clauseStart]) < 0) clauseStart--;
                clauseStart++;
                int clauseEnd = at + 2;
                while (clauseEnd < text.Length && "，,。！？；;：:\n".IndexOf(text[clauseEnd]) < 0) clauseEnd++;
                string prefix = text.Substring(clauseStart, at - clauseStart).Trim();
                string suffix = text.Substring(at + 2, clauseEnd - (at + 2)).Trim();
                bool negated = PrefixNegatesLeftmost(prefix) || PostposedNegatesLeftmost(suffix);
                if (!negated) return true;
                at = clauseEnd > at ? clauseEnd : at + 2;
            }
            return false;
        }

        static bool PrefixNegatesLeftmost(string prefix)
        {
            if (string.IsNullOrEmpty(prefix)) return false;
            string p = prefix.TrimEnd();
            string[] tokens = { "别选", "不要选", "不要", "不选", "别要", "不挑", "排除", "不是", "而非", "禁止" };
            foreach (string token in tokens)
                if (p.EndsWith(token, StringComparison.Ordinal)) return true;
            return false;
        }

        static bool PostposedNegatesLeftmost(string suffix)
        {
            if (string.IsNullOrEmpty(suffix)) return false;
            string[] tokens = { "不要", "别要", "不选", "别选", "不挑", "别挑", "放弃", "排除", "去掉", "拿掉", "不想要" };
            foreach (string token in tokens)
            {
                int idx = suffix.IndexOf(token, StringComparison.Ordinal);
                if (idx < 0) continue;
                int after = idx + token.Length;
                // A target-negating token must end the target phrase or be directly
                // followed by 了/punctuation. “别移动” / “不要移动” are movement
                // negatives and must not cancel the leftmost selection.
                if (after >= suffix.Length) return true;
                char c = suffix[after];
                if (c == '了' || "，,。！？；;：: \t\n".IndexOf(c) >= 0) return true;
            }
            return false;
        }

        public static void SetRealBlockedDetailForTest(string detail)
        {
            LastStatus = "blocked";
            LastError = detail ?? "";
        }

        static bool Stale(RunState run)
        {
            return run == null || run.session == null || !run.session.isActiveAndEnabled ||
                _generation != run.generation || SelectionBroker.Generation != run.brokerToken;
        }

        static void AbortStale(RunState run)
        {
            if (run == null || run.generation != _generation || _run != run) return;
            if (_run != null && _run.generation == run.generation) _run = null;
            LastStatus = "stale";
            LastError = "shared_generation_changed";
        }

        static void AddCsvActions(FpVisualRequest request, string csv)
        {
            if (string.IsNullOrEmpty(csv)) return;
            foreach (var raw in csv.Split(new[] { ',', '，', ';', '；' }, StringSplitOptions.RemoveEmptyEntries))
            {
                switch (raw.Trim().ToLowerInvariant())
                {
                    case "select": request.select = true; break;
                    case "query": request.query = true; break;
                    case "approach": request.approach = true; break;
                    case "sit": request.sit = true; break;
                    case "stand": request.stand = true; break;
                    case "board": request.board = true; break;
                }
            }
        }

        static void AddCsvConstraints(FpVisualRequest request, string csv)
        {
            if (string.IsNullOrEmpty(csv)) return;
            foreach (var raw in csv.Split(new[] { ',', '，', ';', '；' }, StringSplitOptions.RemoveEmptyEntries))
            {
                switch (raw.Trim().ToLowerInvariant())
                {
                    case "leftmost": request.leftmost = true; break;
                    case "near_crosshair": request.near_crosshair = true; break;
                    case "no_move": request.forbid_move = true; break;
                    case "no_board": request.forbid_board = true; break;
                    case "no_sit": request.forbid_sit = true; break;
                    case "pronoun": request.pronoun = true; break;
                    case "current_view": request.current_view = true; break;
                    case "any_match": request.any_match = true; break;
                }
            }
            if (request.forbid_board) request.board = false;
            if (request.forbid_sit) request.sit = false;
            // A pronoun within a newly described visible target (find a car,
            // select it) refers to this observation, never the previous selection.
            if (request.current_view || request.any_match) request.pronoun = false;
        }

        static bool StartRun(ChatSession session, FpVisualRequest request)
        {
            if (request.pronoun && request.query && !request.select && !request.approach && !request.sit && !request.stand && !request.board)
            {
                EnsureInstance();
                session.PublishFirstPersonReply(DescribeSelection(SelectionBroker.Current));
                return true;
            }
            EnsureInstance();
            CancelRun("replaced_by_new_input");
            _generation++;
            _attempt = 0;
            var state = new RunState
            {
                generation = _generation,
                brokerToken = request.pronoun ? SelectionBroker.Generation : SelectionBroker.BeginIntent(),
                request = request,
                session = session,
            };
            _run = state;
            state.coroutine = Instance.StartCoroutine(Run(state));
            return true;
        }

        /// <summary>Production chat entry hook (offline fallback when semantic routing is disabled).</summary>
        public static bool TryHandleChat(string text, ChatSession session)
        {
            if (session == null || string.IsNullOrEmpty(text)) return false;
            if (!FpVisualLanguage.LooksLikeVisualTarget(text)) return false;
            var request = FpVisualLanguage.Parse(text);
            if (request == null) return false;
            return StartRun(session, request);
        }

        public static void CancelRun(string reason)
        {
            if (_run == null) return;
            bool ownsSharedToken = SelectionBroker.Generation == _run.brokerToken;
            _generation++;
            if (Instance != null && _run.coroutine != null) Instance.StopCoroutine(_run.coroutine);
            LastStatus = "cancelled";
            LastError = reason;
            LastReply = "已取消上一轮视觉目标任务。";
            _run = null;
            // Only clean the shared selection state this run owns; never cancel a
            // newer manual/map/AI selection made while this run was pending.
            if (ownsSharedToken)
            {
                SelectionBroker.CancelIntent();
                SelectionBroker.ClearCandidates();
            }
        }

        public static string DescribeState()
        {
            return "status=" + LastStatus + " gen=" + _generation + " running=" + IsRunning +
                   " published=" + LastPublishedEntity + " locate_calls=" + LocateCalls +
                   " error=" + LastError + " reply=" + LastReply;
        }

        public static string DescribeProgress()
        {
            if (_run == null) return "";
            string target = _run.request.target_description;
            switch (LastStatus)
            {
                case "capturing": case "starting": return "我正在获取当前第一人称画面，准备寻找“" + target + "”。";
                case "locating": return "我正在辨认画面里的“" + target + "”，还没有确认目标。";
                case "binding": return "我正在把看到的目标与场景物体核对，还没有开始移动。";
                case "aiming": return "我正在把十字对准“" + target + "”，然后重新取图确认。";
                case "handoff": return "目标已经确认，我正在检查执行条件。";
                default: return "我正在处理“" + target + "”，还没有确认完成。";
            }
        }

        void OnDisable() { if (Instance == this) CancelRun("targeting_service_disabled"); }
        void OnDestroy() { if (Instance == this) { CancelRun("targeting_service_destroyed"); Instance = null; } }

        static IEnumerator Run(RunState run)
        {
            var session = run.session;
            var req = run.request;
            MovementStarted = false;
            LastStatus = "starting";
            LastError = "";
            LastReply = "";
            LastPublishedEntity = "";
            LocateCalls = 0;
            var character = session.Character != null ? session.Character.transform : PickService.FindCharacterTransform();
            var locomotion = session.Locomotion != null ? session.Locomotion : FindObjectOfType<Yixuan.Motion.CharacterLocomotion>();
            var gaze = session.Gaze != null ? session.Gaze : FindObjectOfType<Yixuan.Vision.EyeGimbalController>();
            InitialCharacterPosition = character != null ? character.position : Vector3.zero;

            if (PickService.EnsureRegistry() == false)
            {
                yield return Finish(run, false, "registry unavailable: " + EntityPickRegistry.Error);
                yield break;
            }

            // Pronoun: use the shared confirmed record, never invent coordinates.
            if (req.pronoun)
            {
                var current = SelectionBroker.Current;
                if (current == null || !current.confirmed)
                {
                    yield return Finish(run, false, "还没有共同确认的对象；请先让我选中一个目标。");
                    yield break;
                }
                if (!req.approach && !req.sit && !req.board && !req.stand && !req.select)
                {
                    yield return ReplyWithFacts(run, current);
                    yield break;
                }
                if (req.forbid_move) { req.approach = false; req.sit = false; req.board = false; req.stand = false; }
                if (req.forbid_sit) req.sit = false;
                if (req.forbid_board) req.board = false;
                if (req.approach || req.sit || req.stand || req.board)
                {
                    yield return HandoffToRunner(run, current);
                    yield break;
                }
                yield return ReplyWithFacts(run, current);
                yield break;
            }

            if (!LooksLikeVisualCommand(req))
            {
                yield return ReplyWithFacts(run, SelectionBroker.Current);
                yield break;
            }

            // 1. Capture the CURRENT view first. No center, no old target.
            LastStatus = "capturing";
            var locateFrame = PickService.CaptureEyeFrame();
            if (locateFrame == null || !locateFrame.Ok)
            {
                yield return Finish(run, false, "第一人称截图失败：" + (locateFrame != null ? locateFrame.error : "null"));
                yield break;
            }
            SaveFrameEvidence(locateFrame, "00_locate_frame");
            string jpegError;
            byte[] jpeg = ToJpeg(locateFrame, out jpegError);
            if (jpeg == null)
            {
                yield return Finish(run, false, "截图转 JPEG 失败：" + jpegError);
                yield break;
            }
            SaveVisionInput(locateFrame, jpeg);

            // 2. Generic visual locate (injected hook in deterministic tests only).
            LastStatus = "locating";
            FpVisualLocateResult locate = null;
            LocateCalls++;
            string instruction = BuildInstruction(req);
            if (TestLocateDelaySeconds > 0f)
            {
                // Deterministic pending-response fixture (injected test mode only):
                // a new input during this wait bumps the generation and cancels the run.
                yield return new WaitForSeconds(TestLocateDelaySeconds);
                if (Stale(run)) { AbortStale(run); yield break; }
            }
            if (TestLocateHook != null)
            {
                locate = TestLocateHook(req, _attempt);
                if (locate == null) locate = new FpVisualLocateResult { Error = "injected hook returned null" };
            }
            else
            {
                var vision = UnityEngine.Object.FindObjectOfType<Yixuan.Vision.QwenVisionClient>();
                if (vision == null)
                {
                    yield return Finish(run, false, "没有可用的视觉服务配置。");
                    yield break;
                }
                // The model localizes on the original image. Entity IDs, engine
                // boxes and coordinates are used only AFTER the visual response.
                yield return FpVisualClient.Locate(vision, instruction, jpeg, null,
                    () => Stale(run), r => locate = r);
            }
            if (Stale(run)) { AbortStale(run); yield break; }
            if (locate == null || !locate.Ok)
            {
                SaveLocateResult(locate, instruction, "locate_failed", req);
                yield return Finish(run, false, "视觉定位请求失败：" + (locate != null ? locate.Error : "null"));
                yield break;
            }
            SaveLocateResult(locate, instruction, "locate", req);
            var reply = locate.Reply;
            if (reply == null || string.IsNullOrEmpty(reply.status))
            {
                yield return Finish(run, false, "视觉返回没有有效状态。");
                yield break;
            }
            if (reply.status == "not_found")
            {
                yield return Finish(run, false, "当前画面没有找到你说的目标，我不会换旧目标或移动。");
                yield break;
            }
            if (reply.status == "ambiguous")
            {
                yield return Finish(run, false, "画面里有多个相近目标，请再说明是哪一个（例如最左/准星旁）。");
                yield break;
            }
            if (reply.status != "located")
            {
                yield return Finish(run, false, "视觉返回了未知状态：" + reply.status);
                yield break;
            }
            if (!ValidateReply(reply))
            {
                yield return Finish(run, false, "视觉坐标结构/边界无效：" + (reply.error ?? ""));
                yield break;
            }

            // 3. Bind model point/bbox to the ID image (top-left coordinates map
            // directly into the shared top-origin frame; no 1-v).
            LastStatus = "binding";
            int code;
            Vector3 surfacePoint;
            string bindNote;
            var hit = frameHit(locateFrame, reply.point_uv, out code, out surfacePoint, out bindNote);
            SaveResolvedHitJson("02_model_point_hit", "model_point_before_recovery", reply, locateFrame,
                hit as PickHit, true);
            if (hit == null)
            {
                // The model point may land in a slat gap/background while the bbox is
                // still correct. Recover only through the bbox: keep the semantic
                // target category from the guarded request, require exactly one
                // candidate of that class with a reliable visible pixel, and never
                // let a background building inside the box become the target.
                if (reply.bbox_uv != null && reply.bbox_uv.Length == 4)
                {
                    var box = new Rect(reply.bbox_uv[0], reply.bbox_uv[1],
                        reply.bbox_uv[2] - reply.bbox_uv[0], reply.bbox_uv[3] - reply.bbox_uv[1]);
                    var boxResult = PickService.ResolveBox(locateFrame, box);
                    string expectedClass = TargetClassFor(req.target_description);
                    if (boxResult != null && !string.IsNullOrEmpty(expectedClass))
                    {
                        PickBoxCandidate match = null;
                        int matches = 0;
                        foreach (var candidate in boxResult.candidates)
                        {
                            var e = EntityPickRegistry.Get(candidate.entity_id);
                            if (e == null || e.Cls != expectedClass) continue;
                            if (!candidate.has_reliable_point) continue;
                            matches++; match = candidate;
                        }
                        if (matches == 1 && match != null)
                        {
                            var expectedEntry = EntityPickRegistry.Get(match.entity_id);
                            if (expectedEntry != null)
                            {
                                code = match.sample_code;
                                surfacePoint = match.surface_point;
                                int sx = Mathf.Clamp(match.sample_pixel_x, 0, locateFrame.width - 1);
                                int sy = Mathf.Clamp(match.sample_pixel_y, 0, locateFrame.height - 1);
                                hit = locateFrame.ResolveUv((sx + 0.5f) / locateFrame.width, (sy + 0.5f) / locateFrame.height);
                                SaveBindJson(reply, "bbox_class_recovery", match.entity_id + ";class=" + expectedClass + ";box_ids=" +
                                    string.Join(",", boxResult.candidates.ConvertAll(c => c.entity_id).ToArray()));
                            }
                        }
                        else if (matches > 1)
                        {
                            SaveBindJson(reply, "bbox_same_class_ambiguous", "class=" + expectedClass + ";matches=" + matches);
                            yield return Finish(run, false, "模型框内有多个同类的可见目标，请说明是哪一个。");
                            yield break;
                        }
                    }
                    if (hit == null)
                    {
                        var ids = new List<string>();
                        if (boxResult != null) foreach (var c in boxResult.candidates) ids.Add(c.entity_id);
                        SaveBindJson(reply, "box_no_class_match", "class=" + (expectedClass ?? "") + ";box_ids=" + string.Join(",", ids.ToArray()));
                        yield return Finish(run, false, "模型点没有落在可确认实体上，框内也没有唯一的同类目标：" + string.Join(",", ids.ToArray()));
                        yield break;
                    }
                }
                else
                {
                    SaveBindJson(reply, "no_single_entity", bindNote);
                    yield return Finish(run, false, "模型点/框没有绑定到单一的已验证实体：" + bindNote);
                    yield break;
                }
            }
            var entry = EntityPickRegistry.GetByCode(code);
            if (entry == null || !entry.Selectable)
            {
                yield return Finish(run, false, "命中的 code 没有可选实体：" + code);
                yield break;
            }
            // A non-zero hit on a wrong class (e.g. a building behind the target)
            // must not be accepted: allow only a bbox recovery of the expected class.
            string guardExpectedClass = TargetClassFor(req.target_description);
            if (!string.IsNullOrEmpty(guardExpectedClass) && entry.Cls != guardExpectedClass)
            {
                bool recovered = false;
                if (reply.bbox_uv != null && reply.bbox_uv.Length == 4)
                {
                    var guardBox = new Rect(reply.bbox_uv[0], reply.bbox_uv[1],
                        reply.bbox_uv[2] - reply.bbox_uv[0], reply.bbox_uv[3] - reply.bbox_uv[1]);
                    var guardResult = PickService.ResolveBox(locateFrame, guardBox);
                    PickBoxCandidate match = null; int matches = 0;
                    if (guardResult != null)
                    {
                        foreach (var candidate in guardResult.candidates)
                        {
                            var e = EntityPickRegistry.Get(candidate.entity_id);
                            if (e == null || e.Cls != guardExpectedClass || !candidate.has_reliable_point) continue;
                            matches++; match = candidate;
                        }
                    }
                    if (matches == 1 && match != null)
                    {
                        code = match.sample_code; surfacePoint = match.surface_point;
                        int sx = Mathf.Clamp(match.sample_pixel_x, 0, locateFrame.width - 1);
                        int sy = Mathf.Clamp(match.sample_pixel_y, 0, locateFrame.height - 1);
                        hit = locateFrame.ResolveUv((sx + 0.5f) / locateFrame.width, (sy + 0.5f) / locateFrame.height);
                        entry = EntityPickRegistry.GetByCode(code);
                        recovered = hit != null && entry != null && entry.Cls == guardExpectedClass;
                    }
                }
                if (!recovered)
                {
                    SaveBindJson(reply, "wrong_class_rejected", "point_id=" + entry.Id + ";expected_class=" + guardExpectedClass);
                    yield return Finish(run, false, "模型点落在与描述不一致的其它物体上（" + entry.Id + "），已拒绝，未把背景当目标。");
                    yield break;
                }
            }
            SaveBindJson(reply, "bound", entry.Id + ";uv=" + reply.point_uv[0].ToString("F4") + "," + reply.point_uv[1].ToString("F4"));

            // Point/bbox contradiction must be rejected: a point on a different
            // entity outside the model bbox is not the user's target.
            Rect modelBox = new Rect(0f, 0f, 1f, 1f);
            if (reply.bbox_uv != null && reply.bbox_uv.Length == 4)
            {
                modelBox = new Rect(reply.bbox_uv[0], reply.bbox_uv[1],
                    reply.bbox_uv[2] - reply.bbox_uv[0], reply.bbox_uv[3] - reply.bbox_uv[1]);
                float pu = reply.point_uv != null && reply.point_uv.Length == 2 ? reply.point_uv[0] : -1f;
                float pv = reply.point_uv != null && reply.point_uv.Length == 2 ? reply.point_uv[1] : -1f;
                bool inside = pu >= modelBox.xMin - 0.002f && pu <= modelBox.xMax + 0.002f &&
                              pv >= modelBox.yMin - 0.002f && pv <= modelBox.yMax + 0.002f;
                var boxResult = PickService.ResolveBox(locateFrame, modelBox);
                var boxIds = new List<string>();
                if (boxResult != null) foreach (var c in boxResult.candidates) if (!boxIds.Contains(c.entity_id)) boxIds.Add(c.entity_id);
                // Background/occluding IDs inside the box are tolerated; only a
                // second SUBSTANTIAL object of the SAME semantic class as the point
                // target makes the selection genuinely ambiguous.
                if (!inside)
                {
                    SaveBindJson(reply, "point_bbox_contradiction", "point_in_bbox=" + inside + ";box_ids=" + string.Join(",", boxIds.ToArray()) + ";point_id=" + entry.Id);
                    yield return Finish(run, false, "模型给出的点不在自己的框内，点和框互相矛盾，已拒绝确认。");
                    yield break;
                }
                bool sameClassConflict = false;
                string conflictId = "";
                if (boxResult != null)
                {
                    foreach (var candidate in boxResult.candidates)
                    {
                        if (candidate == null || candidate.entity_id == entry.Id) continue;
                        var other = EntityPickRegistry.Get(candidate.entity_id);
                        if (other == null || other.Cls != entry.Cls) continue;
                        if (candidate.area_fraction < 0.12f) continue;
                        sameClassConflict = true; conflictId = candidate.entity_id; break;
                    }
                }
                if (sameClassConflict && !req.any_match)
                {
                    SaveBindJson(reply, "same_class_ambiguous", "ids=" + string.Join(",", boxIds.ToArray()) + ";conflict=" + conflictId + ";point_id=" + entry.Id);
                    yield return Finish(run, false, "模型框内有两个同类的可见目标（" + entry.Id + " 与 " + conflictId + "），请说明是哪一个。");
                    yield break;
                }
                SaveBindJson(reply, "bbox_ok_background_tolerated", "box_ids=" + string.Join(",", boxIds.ToArray()) + ";point_id=" + entry.Id);
            }

            // Constraint checks use the same frame, no result-driven substitution.
            string constraintError;
            if (!CheckPositionConstraint(locateFrame, entry, req, modelBox, out constraintError))
            {
                yield return Finish(run, false, constraintError);
                yield break;
            }
            SaveResolvedHitJson("02_bound_hit", "accepted_binding_before_aim", reply, locateFrame,
                hit as PickHit, false, surfacePoint);

            // 4. Real gimbal aim (and necessary body assist), then a NEW frame must
            // centre-confirm the same entity before any explicit selection publish.
            LastStatus = "aiming";
            bool aimOk = false;
            string aimError = "";
            AimIterations = 0;
            for (int attempt = 0; attempt < 3 && !aimOk; attempt++)
            {
                AimIterations++;
                yield return AimAt(run, gaze, character, surfacePoint);
                if (Stale(run)) { AbortStale(run); yield break; }
                var confirmFrame = PickService.CaptureEyeFrame();
                if (confirmFrame == null || !confirmFrame.Ok)
                {
                    aimError = "new frame capture failed";
                    continue;
                }
                SaveFrameEvidence(confirmFrame, "0" + (attempt + 1) + "_confirm_frame");
                int centreCode = confirmFrame.CodeAt(confirmFrame.width / 2, confirmFrame.height / 2);
                var centreEntry = EntityPickRegistry.GetByCode(centreCode);
                string centreId = centreEntry != null ? centreEntry.Id : "";
                SaveConfirmJson(attempt, centreCode, centreId, entry.Id, confirmFrame);
                if (centreId != entry.Id) { aimError = "centre=" + centreId + " expected=" + entry.Id; continue; }
                // production publish path (re-captures and publishes the centre hit under the shared token)
                if (SelectionBroker.Generation != run.brokerToken)
                {
                    LastStatus = "stale";
                    LastError = "shared_generation_changed_before_publish";
                    yield break;
                }
                var publishHit = PickService.ConfirmAtCenter("visual_confirm", run.brokerToken, expectedEntityId: entry.Id);
                var publishedRecord = SelectionBroker.Current;
                // ConfirmAtCenter captured its own frame; never attach the earlier
                // confirmFrame to the hit/record actually returned by that call.
                SavePublishJson(attempt, entry.Id, run.brokerToken, publishHit, publishedRecord, PickService.LastFrame);
                if (publishHit == null || !publishHit.Valid || publishHit.entity == null ||
                    publishHit.entity.Id != entry.Id || publishedRecord == null || publishedRecord.entity_id != entry.Id)
                {
                    aimError = "publish did not match centre entity";
                    continue;
                }
                LastPublishedEntity = publishedRecord.entity_id;
                aimOk = true;
            }
            if (!aimOk)
            {
                yield return Finish(run, false, "新帧中心没有确认为同一实体：" + aimError);
                yield break;
            }

            // 5. Reply / follow-up actions.
            var confirmed = SelectionBroker.Current;
            if (confirmed == null)
            {
                yield return Finish(run, false, "选择发布后未读到共享记录。");
                yield break;
            }
            LastStatus = "selected";
            if (req.forbid_move) { req.approach = false; req.sit = false; req.board = false; req.stand = false; }
            if (req.forbid_sit) req.sit = false;
            if (req.forbid_board) req.board = false;
            if (req.approach || req.sit || req.stand || req.board)
            {
                // No parallel executor: hand the original request (with its target
                // and constraints) to the existing independent contract -> Lua ->
                // runner chain, which now sees the SAME confirmed selection as a
                // live task target.
                yield return HandoffToRunner(run, confirmed);
                yield break;
            }
            yield return ReplyWithFacts(run, confirmed);
        }

        static bool LooksLikeVisualCommand(FpVisualRequest req)
        {
            return req.select || req.query || req.approach || req.sit || req.board || req.stand || !string.IsNullOrEmpty(req.target_description);
        }

        static object frameHit(PickFrame frame, float[] pointUv, out int code, out Vector3 surfacePoint, out string note)
        {
            code = 0; surfacePoint = Vector3.zero; note = "";
            if (pointUv == null || pointUv.Length < 2) { note = "missing point_uv"; return null; }
            float u = pointUv[0], v = pointUv[1];
            if (u < 0f || u > 1f || v < 0f || v > 1f) { note = "point uv out of [0,1]"; return null; }
            var hit = frame.ResolveUv(u, v);
            if (hit == null) { note = "no hit"; return null; }
            if (!hit.Valid || hit.entity == null) { note = "point not on selectable entity (code=" + hit.code + ")"; return null; }
            code = hit.code;
            surfacePoint = hit.surface_point;
            return hit;
        }

        static bool ValidateReply(FpVisualRawReply reply)
        {
            if (reply == null) return false;
            if (reply.point_uv == null || reply.bbox_uv == null)
            { reply.error = "located requires point_uv and bbox_uv"; return false; }
            if (reply.point_uv != null)
            {
                if (reply.point_uv.Length != 2) { reply.error = "point_uv length"; return false; }
                foreach (var c in reply.point_uv) if (float.IsNaN(c) || float.IsInfinity(c) || c < 0f || c > 1f) { reply.error = "point_uv bounds"; return false; }
            }
            if (reply.bbox_uv != null)
            {
                if (reply.bbox_uv.Length != 4) { reply.error = "bbox_uv length"; return false; }
                foreach (var c in reply.bbox_uv) if (float.IsNaN(c) || float.IsInfinity(c) || c < 0f || c > 1f) { reply.error = "bbox_uv bounds"; return false; }
                if (reply.bbox_uv[2] <= reply.bbox_uv[0] || reply.bbox_uv[3] <= reply.bbox_uv[1]) { reply.error = "bbox_uv empty"; return false; }
            }
            return true;
        }

        static bool CheckPositionConstraint(PickFrame frame, EntityPickEntry chosen, FpVisualRequest req, Rect modelBox, out string error)
        {
            error = "";
            if (!req.leftmost && !req.near_crosshair) return true;
            // Scope the comparison to what the MODEL actually localized (its bbox
            // over the ID image), not every same-class object in the whole frame.
            var stats = VisibleClassStats(frame, chosen.Cls, new Rect(0f, 0f, 1f, 1f));
            if (stats.Count < 2) return true;   // single localized target: constraint is not a choice
            if (!stats.ContainsKey(chosen.Id)) return true;
            ClassStat best = null;
            foreach (var s in stats.Values)
            {
                if (best == null) best = s;
                else if (req.leftmost ? s.centroidU < best.centroidU : s.centreDistance2D < best.centreDistance2D) best = s;
            }
            if (best == null || best.id != chosen.Id)
            {
                error = "constraint_mismatch: " + (req.leftmost ? "最左" : "准星附近") +
                        " 的可见候选目标是 " + (best != null ? best.id : "none") + "，不是模型给定的 " + chosen.Id;
                return false;
            }
            return true;
        }

        sealed class ClassStat { public string id; public float centroidU; public float centroidV; public float centreDistance2D; }

        static Dictionary<string, ClassStat> VisibleClassStats(PickFrame frame, string cls, Rect box)
        {
            var result = new Dictionary<string, ClassStat>();
            var sumX = new Dictionary<int, double>();
            var sumY = new Dictionary<int, double>();
            var counts = new Dictionary<int, int>();
            int x0 = Mathf.Clamp(Mathf.FloorToInt(box.xMin * frame.width), 0, frame.width - 1);
            int x1 = Mathf.Clamp(Mathf.CeilToInt(box.xMax * frame.width) - 1, 0, frame.width - 1);
            int y0 = Mathf.Clamp(Mathf.FloorToInt(box.yMin * frame.height), 0, frame.height - 1);
            int y1 = Mathf.Clamp(Mathf.CeilToInt(box.yMax * frame.height) - 1, 0, frame.height - 1);
            for (int y = y0; y <= y1; y += 2)
                for (int x = x0; x <= x1; x += 2)
                {
                    int code = frame.CodeAt(x, y);
                    if (code == 0) continue;
                    var entry = EntityPickRegistry.GetByCode(code);
                    if (entry == null || !entry.Selectable || entry.Cls != cls) continue;
                    double sx, sy; int c;
                    sumX.TryGetValue(code, out sx); sumY.TryGetValue(code, out sy); counts.TryGetValue(code, out c);
                    sumX[code] = sx + x; sumY[code] = sy + y; counts[code] = c + 1;
                }
            foreach (var pair in counts)
            {
                var entry = EntityPickRegistry.GetByCode(pair.Key);
                if (entry == null) continue;
                float u = (float)(sumX[pair.Key] / pair.Value + 0.5f) / frame.width;
                float v = (float)(sumY[pair.Key] / pair.Value + 0.5f) / frame.height;
                float du = u - 0.5f, dv = v - 0.5f;
                result[entry.Id] = new ClassStat { id = entry.Id, centroidU = u, centroidV = v, centreDistance2D = du * du + dv * dv };
            }
            return result;
        }

        static IEnumerator AimAt(RunState run, Yixuan.Vision.EyeGimbalController gaze, Transform character, Vector3 worldTarget)
        {
            if (gaze == null || character == null) yield break;
            var eye = PickService.ResolveEye();
            for (int iteration = 0; iteration < 3; iteration++)
            {
                if (Stale(run)) { AbortStale(run); yield break; }
                Vector3 eyePos = eye != null ? eye.transform.position : gaze.transform.position;
                Vector3 local = character.InverseTransformDirection(worldTarget - eyePos);
                float yaw = Mathf.Atan2(local.x, local.z) * Mathf.Rad2Deg - gaze.BaseYaw - gaze.HomeYaw;
                float horizontal = Mathf.Sqrt(local.x * local.x + local.z * local.z);
                float pitch = Mathf.Atan2(local.y, Mathf.Max(0.0001f, horizontal)) * Mathf.Rad2Deg;
                gaze.TurnRelative(yaw, pitch, 2.5f, true);
                float deadline = Time.realtimeSinceStartup + 8f;
                while (Time.realtimeSinceStartup < deadline && gaze.IsObservationSettling)
                {
                    if (Stale(run)) { AbortStale(run); yield break; }
                    yield return null;
                }
                yield return new WaitForSeconds(0.12f);
                if (Stale(run)) { AbortStale(run); yield break; }
                if (eye != null && Vector3.Angle(eye.transform.forward, (worldTarget - eye.transform.position).normalized) < 3.5f) yield break;
            }
        }

        static IEnumerator HandoffToRunner(RunState run, SelectionRecord record)
        {
            if (record == null || !record.confirmed) { yield return Finish(run, false, "没有已确认实体，不能执行动作。"); yield break; }
            LastStatus = "handoff";
            string action = run.request.sit ? "sit" : run.request.stand ? "stand" :
                            run.request.board ? "board" : run.request.approach ? "approach" : "select";
            bool accepted = run.session != null &&
                run.session.HandoffVisualActionToRunner(run.request.original_text, record.entity_id, run.brokerToken, action);
            if (!accepted)
            {
                yield return Finish(run, false, "已确认实体，但既有任务/契约执行器明确拒绝了该动作请求；没有执行任何移动或坐下。");
                yield break;
            }
            yield return Finish(run, true, "已确认：" + record.display_name + "（" + record.entity_id + "）。已将「" + action +
                "」动作连同固定确认 ID 交给既有任务/契约/Lua 执行器；真实结果以执行器回执为准。");
        }

        static IEnumerator ReplyWithFacts(RunState run, SelectionRecord record)
        {
            yield return Finish(run, record != null && record.confirmed, DescribeSelection(record));
        }

        static string DescribeSelection(SelectionRecord record)
        {
            if (record == null || !record.confirmed)
                return "当前没有已确认的选中对象。";
            var live = PickService.SampleLive(record);
            string ability = string.IsNullOrEmpty(record.capabilities) ? "没有可用的交互能力" : record.capabilities;
            string text = "已选中：" + record.display_name + "（" + record.entity_id + "），" +
                          "当前距离约 " + live.distance_m.ToString("F1") + " 米，" +
                          "方位约 " + live.bearing_deg.ToString("F0") + "°（相对身体朝向），" +
                          "位置约 (" + live.current_position.x.ToString("F1") + ", " + live.current_position.y.ToString("F1") + ", " + live.current_position.z.ToString("F1") + ")。" +
                          "可用能力：" + ability + "。";
            if (record.dynamic && live.observed_to_current_m > 0.05f)
                text += " 它是动态目标，我按当前实时位置更新（观察时与现在相差 " + live.observed_to_current_m.ToString("F2") + " 米）。";
            text += " 我的世界坐标是 " + live.character_position.ToString("F1") + "，身体朝向约 " + live.body_yaw_deg.ToString("F0") + "°。";
            return text;
        }

        static IEnumerator ApproachEntity(RunState run, string entityId)
        {
            if (MovementStarted) yield break;
            var session = run.session;
            var character = session.Character != null ? session.Character.transform : PickService.FindCharacterTransform();
            var locomotion = session.Locomotion != null ? session.Locomotion : FindObjectOfType<Yixuan.Motion.CharacterLocomotion>();
            var entry = EntityPickRegistry.Get(entityId);
            if (entry == null || character == null || locomotion == null)
            {
                yield return Finish(run, false, "走近失败：缺少导航或实体数据。");
                yield break;
            }
            Vector3 approach;
            string approachNote;
            if (!TryComputeApproach(entry, character.position, out approach, out approachNote))
            {
                yield return Finish(run, false, "没有找到安全走近点：" + approachNote);
                yield break;
            }
            LastStatus = "approaching";
            MovementStarted = true;
            if (!locomotion.MoveTo(approach))
            {
                yield return Finish(run, false, "导航拒绝了走近目标：" + approachNote);
                yield break;
            }
            float deadline = Time.realtimeSinceStartup + 45f;
            while (Time.realtimeSinceStartup < deadline)
            {
                if (Stale(run)) { AbortStale(run); yield break; }
                if (!locomotion.IsMoving) break;
                yield return null;
            }
            float distance = Vector3.Distance(character.position, entry.LivePosition());
            bool arrived = Vector3.Distance(character.position, approach) < 1.5f;
            yield return Finish(run, arrived,
                arrived ? "已走到 " + entry.DisplayName + " 旁边（保持约 " + distance.ToString("F1") + " 米），按你的要求没有上车。" :
                          "走近未能完成：导航超时或路径受阻（当前位置距目标约 " + distance.ToString("F1") + " 米）。");
        }

        static bool TryComputeApproach(EntityPickEntry entry, Vector3 from, out Vector3 approach, out string note)
        {
            approach = Vector3.zero; note = "";
            var bounds = entry.WorldBounds();
            Vector3 centre = bounds.center;
            Vector3 dir = from - centre; dir.y = 0f;
            if (dir.sqrMagnitude < 0.01f) dir = Vector3.forward;
            dir.Normalize();
            float radius = Mathf.Max(bounds.extents.x, bounds.extents.z) + 1.6f;
            float[] angles = { 0f, 35f, -35f, 70f, -70f, 110f, -110f, 180f };
            foreach (float angle in angles)
            {
                Vector3 d = Quaternion.Euler(0f, angle, 0f) * dir;
                Vector3 candidate = centre + d * radius;
                candidate.y = WorldData.GroundHeightAt(candidate);
                if (!WorldData.IsWalkable(candidate)) continue;
                var path = WorldData.FindPath(from, candidate);
                if (path == null || path.Count == 0) continue;
                approach = candidate;
                note = "angle=" + angle.ToString("F0") + ";radius=" + radius.ToString("F2");
                return true;
            }
            note = "all approach directions rejected by walkability/path";
            return false;
        }

        static IEnumerator SitEntity(RunState run, string entityId, bool requireApproach)
        {
            var seat = FindSeat(entityId);
            if (seat == null)
            {
                yield return Finish(run, false, "这个目标没有注册坐下能力（" + entityId + "），我不会假装坐下。");
                yield break;
            }
            if (requireApproach)
            {
                yield return ApproachEntity(run, entityId);
                if (!string.Equals(LastStatus, "completed", StringComparison.Ordinal) && LastError != "") yield break;
            }
            var controller = Stage4SeatController.Instance != null ? Stage4SeatController.Instance : FindObjectOfType<Stage4SeatController>();
            if (controller == null)
            {
                yield return Finish(run, false, "坐下控制器不可用。");
                yield break;
            }
            LastStatus = "sitting";
            if (!controller.TrySit(entityId))
            {
                yield return Finish(run, false, "坐下请求被拒绝：" + (controller.LastError ?? ""));
                yield break;
            }
            float deadline = Time.realtimeSinceStartup + 20f;
            while (Time.realtimeSinceStartup < deadline)
            {
                if (Stale(run)) { AbortStale(run); yield break; }
                if (controller.State == Stage4SeatController.SeatState.Seated) break;
                if (controller.State == Stage4SeatController.SeatState.Failed) break;
                yield return null;
            }
            bool seated = controller.State == Stage4SeatController.SeatState.Seated;
            yield return Finish(run, seated,
                seated ? "已经在这张椅子上坐稳了。" : "坐下没有完成：" + (controller.State + " " + (controller.LastError ?? "timed out")));
        }

        static IEnumerator StandUp(RunState run)
        {
            var controller = Stage4SeatController.Instance != null ? Stage4SeatController.Instance : FindObjectOfType<Stage4SeatController>();
            if (controller == null || controller.State != Stage4SeatController.SeatState.Seated)
            {
                yield return Finish(run, false, "现在没有坐着。");
                yield break;
            }
            LastStatus = "standing";
            controller.RequestStand();
            float deadline = Time.realtimeSinceStartup + 20f;
            while (Time.realtimeSinceStartup < deadline)
            {
                if (Stale(run)) { AbortStale(run); yield break; }
                if (controller.State != Stage4SeatController.SeatState.Seated && controller.State != Stage4SeatController.SeatState.StandingUp) break;
                yield return null;
            }
            bool stood = controller.State != Stage4SeatController.SeatState.Seated && controller.State != Stage4SeatController.SeatState.StandingUp;
            yield return Finish(run, stood, stood ? "已经站起来，恢复干净步行。" : "起身没有完成：" + controller.State);
        }

        static Stage5SeatCandidate FindSeat(string entityId)
        {
            var character = PickService.FindCharacterTransform();
            var seats = Stage5SeatRegistry.Snapshot(character);
            foreach (var s in seats) if (s != null && s.EntityId == entityId) return s;
            return null;
        }

        static IEnumerator Finish(RunState run, bool success, string text)
        {
            if (Stale(run)) { AbortStale(run); yield break; }
            LastStatus = success ? "completed" : "failed";
            LastReply = text ?? "";
            LastError = success ? "" : (text ?? "");
            if (_run != null && _run.generation == run.generation) _run = null;
            var session = run.session;
            if (session != null) session.PublishFirstPersonReply(LastReply);
            yield return null;
        }

        static string BuildInstruction(FpVisualRequest req)
        {
            var sb = new StringBuilder();
            sb.Append("你是第一人称图像定位器。只根据随附的这一张当前原始截图定位目标，忽略旧对话或旧场景记忆。用户目标：")
                .Append(string.IsNullOrEmpty(req.target_description) ? req.original_text : req.target_description).Append("。");
            sb.Append("返回图像二维坐标：原点为图片左上角，x向右、y向下，所有坐标除以图像宽高归一化到[0,1]。")
                .Append("point_uv=[x,y]必须落在目标本身清楚可见的实体表面，避开窗户空隙、边缘、遮挡物和背景；")
                .Append("bbox_uv=[left,top,right,bottom]只框所选的单个目标，point_uv必须在此框内。")
                .Append("保留用户要求的类别、颜色、车型和位置条件；不要把电车当道路公交车。看不到匹配目标就返回not_found，不能猜画外物体。");
            if (req.any_match)
                sb.Append(" 用户允许找任意一个符合描述的目标；即使看见多个也请选择其中一件清楚可见、便于对准的目标并只返回它的点框。");
            else
                sb.Append(" 若用户所指目标无法在多个匹配对象之间区分，返回ambiguous，不随意替换目标。");
            if (req.leftmost) sb.Append(" 用户明确要求画面最左边的那一个；请在画面中按水平位置选择最左目标。");
            if (req.near_crosshair) sb.Append(" 用户要求准星附近/正前方的目标；不要在画面其他位置找。");
            sb.Append(" 不返回物体编号、实体ID、世界坐标或动作。严格只输出JSON：")
                .Append("{\"status\":\"located|not_found|ambiguous\",\"point_uv\":[0.5,0.5],\"bbox_uv\":[0.3,0.3,0.7,0.7],\"description\":\"目标可见外观\"}。")
                .Append("示例数字仅说明格式，必须按实际图像定位；not_found/ambiguous时point_uv和bbox_uv返回null。");
            return sb.ToString();
        }

        static byte[] ToJpeg(PickFrame frame, out string error)
        {
            error = "";
            try
            {
                if (frame.rgb_png == null || frame.rgb_png.Length == 0) { error = "no rgb png"; return null; }
                var tex = new Texture2D(2, 2, TextureFormat.RGB24, false);
                if (!tex.LoadImage(frame.rgb_png)) { UnityEngine.Object.DestroyImmediate(tex); error = "png load failed"; return null; }
                byte[] jpeg = tex.EncodeToJPG(85);
                UnityEngine.Object.DestroyImmediate(tex);
                return jpeg;
            }
            catch (Exception e) { error = e.Message; return null; }
        }

        static void SaveFrameEvidence(PickFrame frame, string tag)
        {
            if (!SaveEvidence || frame == null || !frame.Ok) return;
            try
            {
                string dir = EvidenceDir();
                EntityIdCapture.SaveFrame(frame, dir, tag, out _);
            }
            catch (Exception e) { Debug.LogWarning("[FirstPersonTargeting] frame evidence failed: " + e.Message); }
        }

        static void SaveVisionInput(PickFrame frame, byte[] jpeg)
        {
            if (!SaveEvidence || jpeg == null) return;
            try
            {
                string dir = EvidenceDir();
                Directory.CreateDirectory(dir);
                File.WriteAllBytes(Path.Combine(dir, "00_vision_input.jpg"), jpeg);
                File.WriteAllText(Path.Combine(dir, "00_vision_input.json"), JsonConvert.SerializeObject(new
                {
                    width = frame.width, height = frame.height,
                    rgb_png_sha256 = EntityIdCapture.Sha256(frame.rgb_png),
                    jpeg_sha256 = EntityIdCapture.Sha256(jpeg),
                    image_orientation = "normal top-left PNG; model top-left uv maps directly into the shared top-origin ID/depth frame",
                    grounding_mode = "model_point_bbox_from_original_image",
                    image_count = 1,
                    model_receives_candidate_overlay = false,
                    model_receives_entity_ids = false,
                    model_receives_world_coordinates = false,
                }, Formatting.Indented));
            }
            catch (Exception e) { Debug.LogWarning("[FirstPersonTargeting] vision input evidence failed: " + e.Message); }
        }

        static void SaveCandidates(FpVisibleCandidates candidates)
        {
            if (!SaveEvidence || candidates == null) return;
            try
            {
                string directory = EvidenceDir();
                Directory.CreateDirectory(directory);
                File.WriteAllBytes(Path.Combine(directory, "00_candidates.jpg"), candidates.AnnotatedJpeg);
                File.WriteAllText(Path.Combine(directory, "00_candidates.json"), JsonConvert.SerializeObject(new {
                    omitted = candidates.Omitted, candidates = candidates.Items.ConvertAll(item => new {
                        index = item.index, entity_id = item.entity_id, pixels = item.pixels,
                        bbox_uv = new[] { item.box.xMin, item.box.yMin, item.box.xMax, item.box.yMax },
                        point_uv = new[] { item.hit.u, item.hit.v }
                    }), model_receives_entity_ids = false, model_receives_world_coordinates = false
                }, Formatting.Indented));
            }
            catch (Exception exception) { Debug.LogWarning("[FirstPersonTargeting] candidate evidence: " + exception.Message); }
        }

        static void SaveLocateResult(FpVisualLocateResult result, string instruction, string tag, FpVisualRequest request)
        {
            if (!SaveEvidence) return;
            try
            {
                Directory.CreateDirectory(EvidenceDir());
                File.WriteAllText(Path.Combine(EvidenceDir(), tag + ".json"), JsonConvert.SerializeObject(new
                {
                    instruction = instruction,
                    original_text = request != null ? request.original_text : "",
                    target_description = request != null ? request.target_description : "",
                    raw_constraints = request != null ? request.raw_constraints : "",
                    effective_constraints = request != null ? request.effective_constraints : "",
                    constraint_guard_reason = request != null ? request.constraint_guard_reason : "",
                    model = result != null ? result.Model : "",
                    ok = result != null && result.Ok,
                    http_status = result != null ? result.HttpStatus : 0,
                    from_backoff = result != null && result.FromBackoff,
                    service_diagnostic = result != null ? result.Diagnostic : null,
                    seconds = result != null ? result.Seconds : 0f,
                    error = result != null ? result.Error : "null",
                    raw = result != null ? result.Raw : "",
                    reply = result != null ? result.Reply : null,
                }, Formatting.Indented));
            }
            catch (Exception e) { Debug.LogWarning("[FirstPersonTargeting] locate evidence failed: " + e.Message); }
        }

        static void SaveBindJson(FpVisualRawReply reply, string status, string detail)
        {
            if (!SaveEvidence) return;
            try
            {
                Directory.CreateDirectory(EvidenceDir());
                File.WriteAllText(Path.Combine(EvidenceDir(), "02_bind.json"), JsonConvert.SerializeObject(new
                {
                    status = status,
                    detail = detail,
                    point_uv = reply != null ? reply.point_uv : null,
                    bbox_uv = reply != null ? reply.bbox_uv : null,
                    description = reply != null ? reply.description : "",
                    coordinate_policy = "model top-left uv -> shared top-origin frame (no 1-v)",
                }, Formatting.Indented));
            }
            catch (Exception e) { Debug.LogWarning("[FirstPersonTargeting] bind evidence failed: " + e.Message); }
        }

        static void SaveConfirmJson(int attempt, int centreCode, string centreId, string expectedId, PickFrame frame)
        {
            if (!SaveEvidence) return;
            try
            {
                Directory.CreateDirectory(EvidenceDir());
                File.WriteAllText(Path.Combine(EvidenceDir(), "03_confirm_" + attempt + ".json"), SelectionJson.Serialize(new
                {
                    attempt = attempt,
                    frame_id = frame.FrameId,
                    centre_code = centreCode,
                    centre_entity = centreId,
                    expected_entity = expectedId,
                    matched = centreId == expectedId,
                    width = frame.width, height = frame.height,
                    eye_position = frame.snapshot != null ? frame.snapshot.position : Vector3.zero,
                    eye_forward = frame.snapshot != null ? frame.snapshot.Forward : Vector3.forward,
                }));
            }
            catch (Exception e) { Debug.LogWarning("[FirstPersonTargeting] confirm evidence failed: " + e.Message); }
        }

        static object ResolvedHitEvidence(PickFrame frame, PickHit hit)
        {
            if (hit == null) return null;
            bool sameSnapshot = frame != null && frame.snapshot != null &&
                object.ReferenceEquals(frame.snapshot, hit.snapshot);
            bool hasDepth = sameSnapshot && frame.depth01 != null && hit.pixel_x >= 0 && hit.pixel_y >= 0 &&
                hit.pixel_x < frame.width && hit.pixel_y < frame.height &&
                hit.pixel_y * frame.width + hit.pixel_x < frame.depth01.Length;
            float? depth = hasDepth ? (float?)frame.DepthAt(hit.pixel_x, hit.pixel_y) : null;
            return new
            {
                valid = hit.Valid,
                is_sky = hit.is_sky,
                entity_id = hit.EntityId,
                entity_name = hit.EntityName,
                entity_class = hit.entity != null ? hit.entity.Cls : "",
                code = hit.code,
                pixel_x = hit.pixel_x, pixel_y = hit.pixel_y,
                u = hit.u, v = hit.v,
                depth_sample_available = hasDepth,
                depth01 = depth,
                eye_depth_m = depth.HasValue && hit.snapshot != null
                    ? (float?)(hit.snapshot.near + depth.Value * (hit.snapshot.far - hit.snapshot.near)) : null,
                surface_point = hit.surface_point,
                distance_m = hit.distance_m,
                eye_position = hit.eye_position,
                eye_forward = hit.eye_forward,
                frame_id = hit.snapshot != null ? hit.snapshot.frame_id : -1,
                snapshot = hit.snapshot,
                sampled_frame_matches_hit_snapshot = sameSnapshot,
                surface_point_valid = hit.Valid && hasDepth && depth.Value > 0f,
            };
        }

        static void SaveResolvedHitJson(string tag, string stage, FpVisualRawReply reply, PickFrame frame,
            PickHit hit, bool resolveOriginalPointWhenMissing, Vector3? aimPoint = null)
        {
            if (!SaveEvidence) return;
            try
            {
                // Preserve the original invalid/sky sample too, rather than
                // replacing its evidence with a later bbox recovery sample.
                if (hit == null && resolveOriginalPointWhenMissing && frame != null && reply != null &&
                    reply.point_uv != null && reply.point_uv.Length == 2)
                    hit = frame.ResolveUv(reply.point_uv[0], reply.point_uv[1]);
                Directory.CreateDirectory(EvidenceDir());
                File.WriteAllText(Path.Combine(EvidenceDir(), tag + ".json"), SelectionJson.Serialize(new
                {
                    schema = "firstperson-selection.resolved-hit.v1",
                    stage,
                    model_point_uv = reply != null ? reply.point_uv : null,
                    model_bbox_uv = reply != null ? reply.bbox_uv : null,
                    description = reply != null ? reply.description : "",
                    resolved_hit = ResolvedHitEvidence(frame, hit),
                    aim_surface_point = aimPoint,
                    coordinate_policy = "model top-left uv -> shared top-origin frame (no 1-v)",
                }));
            }
            catch (Exception e) { Debug.LogWarning("[FirstPersonTargeting] resolved hit evidence failed: " + e.Message); }
        }

        static void SavePublishJson(int attempt, string expectedId, int expectedGeneration,
            PickHit publishHit, SelectionRecord record, PickFrame publishFrame)
        {
            if (!SaveEvidence) return;
            try
            {
                bool hitMatches = publishHit != null && publishHit.Valid && publishHit.EntityId == expectedId;
                bool recordMatches = hitMatches && record != null && record.confirmed &&
                    record.entity_id == expectedId && record.generation == expectedGeneration &&
                    publishHit.snapshot != null && object.ReferenceEquals(record.compute_snapshot, publishHit.snapshot);
                Directory.CreateDirectory(EvidenceDir());
                File.WriteAllText(Path.Combine(EvidenceDir(), "04_publish_" + attempt + ".json"), SelectionJson.Serialize(new
                {
                    schema = "firstperson-selection.actual-publish.v1",
                    attempt,
                    expected_entity = expectedId,
                    expected_generation = expectedGeneration,
                    publish_returned_valid_hit = hitMatches,
                    selection_matches_returned_hit = recordMatches,
                    publish_error = PickService.LastError,
                    publish_rejected = PickService.LastPublishRejected,
                    publish_hit = ResolvedHitEvidence(publishFrame, publishHit),
                    selection_record = record,
                    snapshot_policy = "publish_hit uses only the actual ConfirmAtCenter returned hit snapshot; selection_record is SelectionBroker.Current at that call",
                }));
            }
            catch (Exception e) { Debug.LogWarning("[FirstPersonTargeting] publish evidence failed: " + e.Message); }
        }

        static string EvidenceDir()
        {
            if (!string.IsNullOrEmpty(EvidenceRoot)) return EvidenceRoot;
            string root = Path.Combine(Application.dataPath, "..", "Captures", "FirstPersonSelection", "run_20261110", "B", "evidence", "runtime");
            return root;
        }
    }
}
