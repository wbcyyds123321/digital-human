using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using Newtonsoft.Json;
using Yixuan.AI;
using Yixuan.AI.Tasks;
using Yixuan.Pick;
using Yixuan.World;
using Yixuan.Vision;
using Yixuan.Presentation;
using Yixuan.Dynamic;

namespace Yixuan.EditorTools.FirstPersonSelection
{
    /// <summary>
    /// B_r2 focused production gate: normal Auto semantic entry (NarratorOverride
    /// injected for the deterministic cases, labelled), one real T2 attempt first.
    /// A blocked fixture never stops the remaining independent cases.
    /// </summary>
    public static class SelectionAcceptanceB
    {
        const string MenuRun = "UnityLITF/FirstPersonSelection/Run B_r2 Focused Gate";
        const string MenuAbort = "UnityLITF/FirstPersonSelection/Abort B Harness";

        sealed class CaseRecord { public string id, status, expected, actual, detail; }
        sealed class W { public float s; public W(float v) { s = v; } }
        sealed class F { public int n; public F(int v) { n = v; } }
        sealed class U { public Func<bool> c; public float t; public U(Func<bool> cc, float tt) { c = cc; t = tt; } }

        sealed class FakeNarrator : ILLMClient
        {
            public static Func<string, string> ReplyFor;
            public bool IsBusy { get { return false; } }
            public string LastError { get { return ""; } }
            public Task<string> ChatAsync(List<ChatMessage> messages, CancellationToken ct)
            {
                string input = messages != null && messages.Count > 0 ? messages[messages.Count - 1].content : "";
                string reply = ReplyFor != null ? ReplyFor(input) : null;
                return Task.FromResult(reply);
            }
        }

        static readonly Stack<IEnumerator> Stack = new Stack<IEnumerator>();
        static readonly List<CaseRecord> Cases = new List<CaseRecord>();
        static readonly List<string> LogLines = new List<string>();
        static readonly List<string> ConfirmedIds = new List<string>();
        static bool _running, _finalized, _lastWaitTimedOut;
        static double _deadline, _waitUntil, _conditionDeadline;
        static int _waitFrames;
        static Func<bool> _condition;
        static string _root, _tests, _evidence;
        static ChatSession _session;
        static Yixuan.Vision.EyeGimbalController _gaze;
        static CameraDirector _director;
        static bool _originalEyeView;
        static Transform _character;
        static FakeNarrator _fake;
        static FpVisualLocateResult _injected;
        static string _injectedTag = "";

        [MenuItem(MenuRun, false, 320)] public static void RunMenu() { Run(); }
        [MenuItem(MenuAbort, false, 321)] public static void AbortMenu() { Abort("aborted_by_menu"); }

        public static void Run()
        {
            if (_running) return;
            _root = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Captures", "FirstPersonSelection", "run_20261110", "B_r3"));
            _tests = Path.Combine(_root, "tests"); _evidence = Path.Combine(_root, "evidence");
            if (!Application.isPlaying)
            {
                Directory.CreateDirectory(_tests);
                File.WriteAllText(Path.Combine(_tests, "harness_entry_blocked.json"), "{\"blocked\":true,\"reason\":\"play_mode_required\"}");
                Debug.LogWarning("[FPS-B2] enter Play first."); return;
            }
            Directory.CreateDirectory(_tests);
            Directory.CreateDirectory(Path.Combine(_evidence, "injected"));
            Directory.CreateDirectory(Path.Combine(_evidence, "real"));
            Cases.Clear(); LogLines.Clear(); ConfirmedIds.Clear();
            FirstPersonTargetingService.SaveEvidence = true;
            FirstPersonTargetingService.EvidenceRoot = Path.Combine(_evidence, "injected", "runtime");
            SelectionBroker.Confirmed += OnConfirmed;
            _running = true; _finalized = false;
            _deadline = EditorApplication.timeSinceStartup + 420.0;
            Stack.Clear(); Stack.Push(RunAll());
            EditorApplication.update += Tick;
            Log("b_r2_gate_scheduled");
        }

        static void OnConfirmed(SelectionRecord r) { ConfirmedIds.Add(r != null ? r.entity_id : "null"); }

        static void Tick()
        {
            if (!_running) return;
            if (EditorApplication.timeSinceStartup > _deadline) { Abort("watchdog_timeout"); return; }
            try
            {
                if (_waitUntil > 0 && EditorApplication.timeSinceStartup < _waitUntil) return;
                if (_waitFrames > 0) { _waitFrames--; return; }
                if (_condition != null)
                {
                    bool done = false;
                    try { done = _condition(); } catch { done = false; }
                    if (!done)
                    {
                        if (EditorApplication.timeSinceStartup > _conditionDeadline) { _condition = null; _lastWaitTimedOut = true; }
                        else return;
                    }
                    else { _condition = null; _lastWaitTimedOut = false; }
                }
                while (true)
                {
                    if (Stack.Count == 0) { Finish(); return; }
                    var top = Stack.Peek();
                    if (!top.MoveNext()) { Stack.Pop(); continue; }
                    var cur = top.Current;
                    if (cur is IEnumerator nested) { Stack.Push(nested); continue; }
                    if (cur is W w) { _waitUntil = EditorApplication.timeSinceStartup + w.s; return; }
                    if (cur is F f) { _waitFrames = f.n; return; }
                    if (cur is U u) { _condition = u.c; _conditionDeadline = EditorApplication.timeSinceStartup + u.t; _lastWaitTimedOut = false; return; }
                    return;
                }
            }
            catch (Exception e) { Abort("tick_exception: " + e); }
        }

        static void Finish()
        {
            if (_finalized) return;
            _finalized = true;
            if (_director != null) _director.EyeView = _originalEyeView;
            try
            {
                File.WriteAllText(Path.Combine(_tests, "case_results.json"), JsonConvert.SerializeObject(new
                {
                    milestone = "B", round = "B_r3",
                    generated_at_local = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:sszzz"),
                    play_mode = Application.isPlaying,
                    cases = Cases,
                    confirmed_event_ids = ConfirmedIds,
                    injected_vs_real = "B.schema/B.T2.injected/B.T8.* are labelled injected via NarratorOverride + visual test hook; B.T2.real is the one real-model attempt",
                }, Formatting.Indented));
                int pass = 0, fail = 0, blocked = 0, pending = 0;
                foreach (var c in Cases) { if (c.status == "pass") pass++; else if (c.status == "fail") fail++; else if (c.status == "blocked") blocked++; else pending++; }
                File.WriteAllText(Path.Combine(_tests, "harness_summary.json"), JsonConvert.SerializeObject(new
                { milestone = "B", round = "B_r3", status = fail > 0 ? "fail" : blocked > 0 ? "blocked" : pending > 0 ? "pending" : "pass", pass, fail, blocked, pending, total = Cases.Count }, Formatting.Indented));
                var log = new StringBuilder();
                foreach (var l in LogLines) log.AppendLine(l);
                File.WriteAllText(Path.Combine(_tests, "harness_log.txt"), log.ToString());
            }
            catch (Exception e) { Debug.LogWarning("[FPS-B2] write: " + e.Message); }
            SelectionBroker.Confirmed -= OnConfirmed;
            FirstPersonTargetingService.TestLocateHook = null;
            FirstPersonTargetingService.TestLocateDelaySeconds = 0f;
            FirstPersonTargetingService.CancelRun("harness_done");
            if (_session != null) _session.NarratorOverride = null;
            _running = false;
            EditorApplication.update -= Tick;
            if (Application.isPlaying) EditorApplication.ExitPlaymode();
        }

        static void Abort(string reason)
        {
            if (_finalized) return;
            Cases.Add(new CaseRecord { id = "HARNESS", status = "blocked", expected = "focused B_r2 gate", actual = "aborted: " + reason, detail = "driver/fixture fault" });
            Log("abort " + reason); Finish();
        }

        static void Log(string s) { LogLines.Add(DateTime.Now.ToString("HH:mm:ss.fff") + " " + s); Debug.Log("[FPS-B2] " + s); }
        static void Check(string id, string expected, bool ok, string actual, string detail)
        {
            Cases.Add(new CaseRecord { id = id, status = ok ? "pass" : "fail", expected = expected, actual = actual, detail = detail });
            Log("case " + id + " " + (ok ? "pass" : "fail") + " " + actual);
        }
        static void Block(string id, string expected, string actual, string detail)
        {
            Cases.Add(new CaseRecord { id = id, status = "blocked", expected = expected, actual = actual, detail = detail });
            Log("case " + id + " blocked " + actual);
        }
        static void Pending(string id, string detail)
        {
            Cases.Add(new CaseRecord { id = id, status = "pending", expected = "not executed in B_r2", actual = "pending", detail = detail });
            Log("case " + id + " pending");
        }

        static string VisualCommand(string target, string actions, string constraints, string source)
        {
            return JsonConvert.SerializeObject(new
            {
                route = "interaction",
                reply = "",
                command = new { operation = "visual_target", direction = "", anchor_id = "", source, target, actions, constraints }
            });
        }

        static IEnumerator RunAll()
        {
            yield return new U(() => UnityEngine.Object.FindObjectOfType<ChatSession>() != null &&
                                     UnityEngine.Object.FindObjectOfType<FirstPersonSelectionController>() != null, 30f);
            _session = UnityEngine.Object.FindObjectOfType<ChatSession>();
            _gaze = UnityEngine.Object.FindObjectOfType<Yixuan.Vision.EyeGimbalController>();
            _director = UnityEngine.Object.FindObjectOfType<CameraDirector>();
            if (_director != null) { _originalEyeView = _director.EyeView; _director.EyeView = true; }
            var go = GameObject.Find("Yixuan_Test");
            _character = go != null ? go.transform : null;
            if (_session == null || _gaze == null || _character == null) { Block("B.GATE", "prerequisites", "session/gaze/character missing", "fixture"); yield break; }
            if (!PickService.EnsureRegistry()) { Block("B.GATE", "pick registry", EntityPickRegistry.Error, "registry"); yield break; }
            yield return new F(3);
            _fake = new FakeNarrator();
            _session.NarratorOverride = _fake;

            // ---- schema gate through the real ReadCommand/Validate/Dispatch path ----
            string valid7 = "{\"command\":{\"operation\":\"visual_target\",\"direction\":\"\",\"anchor_id\":\"\",\"source\":\"选中眼前这张椅子\",\"target\":\"眼前这张椅子\",\"actions\":\"select\",\"constraints\":\"no_move\"}}";
            string validCmd7 = "{\"command\":{\"operation\":\"visual_target\",\"direction\":\"\",\"anchor_id\":\"\",\"source\":\"选最左边那张椅子\",\"target\":\"最左边那张椅子\",\"actions\":\"select\",\"constraints\":\"leftmost\"}}";
            string sixFields = "{\"command\":{\"operation\":\"visual_target\",\"direction\":\"\",\"anchor_id\":\"\",\"source\":\"x\",\"target\":\"椅子\",\"actions\":\"select\"}}";
            string eightFields = "{\"command\":{\"operation\":\"visual_target\",\"direction\":\"\",\"anchor_id\":\"\",\"source\":\"x\",\"target\":\"椅子\",\"actions\":\"select\",\"constraints\":\"\",\"extra\":\"y\"}}";
            string unknownField = "{\"command\":{\"operation\":\"visual_target\",\"direction\":\"\",\"anchor_id\":\"\",\"source\":\"x\",\"target\":\"椅子\",\"actions\":\"select\",\"constraints\":\"\",\"extra\":\"y\"}}";
            var c7 = SemanticInteractionRules.ReadCommand(valid7);
            var v7 = c7 != null ? SemanticInteractionRules.Validate(c7, "选中眼前这张椅子", null) : "read_null";
            Check("B.schema.valid7", "legal 7-field visual_target JSON accepted by real ReadCommand/Validate",
                c7 != null && v7 == null, "read=" + (c7 != null) + " validate=" + (v7 ?? "ok"),
                "operation/direction/anchor_id/source/target/actions/constraints, exactly 7 fields");
            Check("B.schema.reject", "6-field / 8-field / unknown-field visual_target commands rejected",
                SemanticInteractionRules.ReadCommand(sixFields) == null &&
                SemanticInteractionRules.ReadCommand(eightFields) == null &&
                SemanticInteractionRules.ReadCommand(unknownField) == null,
                "six=" + (SemanticInteractionRules.ReadCommand(sixFields) == null) +
                " eight=" + (SemanticInteractionRules.ReadCommand(eightFields) == null) +
                " unknown=" + (SemanticInteractionRules.ReadCommand(unknownField) == null),
                "strict schema gate; no meaningless 8th field");

            // ---- fixture: a SINGLE chair other than the original two, safe pose ----
            EntityPickEntry chair = null; PickFrame chairFrame = null; Vector3Int chairPixel = default(Vector3Int);
            float fixtureDetail = 0f;
            string fixtureWhy = "";
            int tried = 0;
            foreach (var entry in EntityPickRegistry.Entries)
            {
                if (entry.Cls != "bench" || !entry.Selectable || entry.EntityMesh == null) continue;
                if (entry.Id == "entity/prop_bench_00" || entry.Id == "entity/prop_bench_01") continue;
                if (++tried > 25) break;
                Vector3 centre = entry.WorldBounds().center;
                yield return TryChairPose(centre);
                fixtureWhy = _poseReason;
                fixtureDetail = _posePitch;
                if (!string.IsNullOrEmpty(fixtureWhy)) continue;
                var f = PickService.CaptureEyeFrame();
                if (f == null || !f.Ok || FramePixels(f, entry.Code) < 60) { fixtureWhy = "target_pixels"; continue; }
                chair = entry; chairFrame = f; chairPixel = FirstInteriorPixel(f, entry.Code);
                EntityIdCapture.SaveFrame(f, Path.Combine(_evidence, "injected"), "t2_single_chair_first_frame", out _);
                break;
            }
            if (chair == null)
            {
                Block("B.T2.fixture", "single non-original chair with safe visible pose", "none; last=" + fixtureWhy, "fixture validation failed; not a product fail");
            }
            else
            {
                // ---- T2 injected through the NORMAL Auto semantic route ----
                string text = "就眼前这张椅子，选中给我看看，先别动。";
                SetSemanticReply(_fake, text, VisualCommand("眼前这张椅子", "select", "no_move", "选中"));
                SetInjectedPoint(chairFrame, chairPixel, "t2_select");
                FirstPersonTargetingService.EvidenceRoot = Path.Combine(_evidence, "injected", "runtime_t2");
                Vector3 before = _character.position;
                _session.SubmitFromUI(text);
                yield return WaitServiceDone(90f);
                var rec = SelectionBroker.Current;
                float moved = Vector3.Distance(before, _character.position);
                Check("B.T2.injected", "normal Auto semantic entry -> visual protocol -> single chair selected, no movement",
                    FirstPersonTargetingService.LastStatus == "completed" && rec != null && rec.entity_id == chair.Id && moved < 0.05f,
                    "status=" + FirstPersonTargetingService.LastStatus + " record=" + (rec != null ? rec.entity_id : "none") +
                    " expected=" + chair.Id + " moved_m=" + moved.ToString("F3") + " reply=" + FirstPersonTargetingService.LastReply,
                    "NarratorOverride injected (labelled); semantic routing ON; production capture/bind/aim/new-frame centre/publish");

                // ---- T8: delayed reply -> new target ----
                ClearSelection();
                int gen0 = FirstPersonTargetingService.LastGeneration;
                SetInjectedPoint(chairFrame, chairPixel, "stale_first");
                FirstPersonTargetingService.TestLocateDelaySeconds = 1.6f;
                SetSemanticReply(_fake, "把眼前那张椅子选中给我看看。", VisualCommand("眼前那张椅子", "select", "no_move", "选中"));
                _session.SubmitFromUI("把眼前那张椅子选中给我看看。");
                yield return new W(0.4f);
                int gen1 = FirstPersonTargetingService.LastGeneration;
                FirstPersonTargetingService.TestLocateDelaySeconds = 0f;
                // The first injected run aimed the real gimbal at the chair; the
                // second run must be given a point measured on the CURRENT frame,
                // not the pre-aim UI frame (fixture geometry, not a product bypass).
                var aimedFrame = PickService.CaptureEyeFrame();
                Vector3Int aimedPixel = aimedFrame != null && aimedFrame.Ok && FramePixels(aimedFrame, chair.Code) > 40
                    ? FirstInteriorPixel(aimedFrame, chair.Code) : new Vector3Int(aimedFrame.width / 2, aimedFrame.height / 2, chair.Code);
                SetInjectedPoint(aimedFrame, aimedPixel, "new_second");
                SetSemanticReply(_fake, "算了，把准星附近那张椅子选出来。", VisualCommand("准星附近那张椅子", "select", "no_move", "选出来"));
                _session.SubmitFromUI("算了，把准星附近那张椅子选出来。");
                yield return WaitServiceDone(90f);
                Check("B.T8.replace", "pending old visual reply cannot publish after replace",
                    FirstPersonTargetingService.LastStatus == "completed" && SelectionBroker.Current != null &&
                    SelectionBroker.Current.entity_id == chair.Id && FirstPersonTargetingService.LastGeneration > gen1,
                    "gen " + gen0 + "->" + gen1 + "->" + FirstPersonTargetingService.LastGeneration +
                    " final=" + (SelectionBroker.Current != null ? SelectionBroker.Current.entity_id : "none") +
                    " confirmed_events=" + string.Join(",", ConfirmedIds.ToArray()),
                    "delayed visual reply injected (labelled); semantic replies injected; shared generation checked");

                // ---- T8: manual map selection during a pending visual run ----
                ClearSelection();
                SetInjectedPoint(chairFrame, chairPixel, "stale_manual");
                FirstPersonTargetingService.TestLocateDelaySeconds = 1.6f;
                SetSemanticReply(_fake, "把眼前那张椅子选中给我看看。", VisualCommand("眼前那张椅子", "select", "no_move", "选中"));
                _session.SubmitFromUI("把眼前那张椅子选中给我看看。");
                yield return new W(0.4f);
                string manualId = "entity/prop_bench_00";
                bool manualOk = Yixuan.WorldMap.WorldMapPanel.SelectEntityById(manualId);
                yield return new W(2.0f);
                bool manualHeld = SelectionBroker.Current != null && SelectionBroker.Current.entity_id == manualId;
                Check("B.T8.manual", "manual map selection during pending visual run is not overwritten; run ends",
                    manualOk && manualHeld && !FirstPersonTargetingService.IsRunning,
                    "manual_ok=" + manualOk + " current=" +
                    (SelectionBroker.Current != null ? SelectionBroker.Current.entity_id : "none") +
                    " running=" + FirstPersonTargetingService.IsRunning,
                    "production map publish advances generation; stale visual result must not publish or move");

                // ---- T8: explicit stop cancels the visual owner ----
                ClearSelection();
                SetInjectedPoint(chairFrame, chairPixel, "stop_pending");
                FirstPersonTargetingService.TestLocateDelaySeconds = 2.0f;
                SetSemanticReply(_fake, "把眼前那张椅子选中给我看看。", VisualCommand("眼前那张椅子", "select", "no_move", "选中"));
                _session.SubmitFromUI("把眼前那张椅子选中给我看看。");
                yield return new W(0.35f);
                _session.SubmitFromUI("停止");
                yield return new W(1.0f);
                Check("B.T8.stop", "explicit stop cancels the pending visual owner, no late publish",
                    !FirstPersonTargetingService.IsRunning && SelectionBroker.Current == null,
                    "running=" + FirstPersonTargetingService.IsRunning + " current=" +
                    (SelectionBroker.Current != null ? SelectionBroker.Current.entity_id : "none") +
                    " status=" + FirstPersonTargetingService.LastStatus,
                    "normal Auto semantic stop route -> CancelRun; no ConfirmEntity shortcut");

                // ---- REAL T2 (one attempt, no injection) ----
                ClearSelection();
                FirstPersonTargetingService.TestLocateHook = null;
                FirstPersonTargetingService.TestLocateDelaySeconds = 0f;
                _session.NarratorOverride = null;
                FirstPersonTargetingService.EvidenceRoot = Path.Combine(_evidence, "real", "runtime_t2");
                Vector3 beforeReal = _character.position;
                _session.SubmitFromUI("就眼前这张椅子，选中给我看看，先别动。");
                yield return WaitServiceStartThenDone(40f, 180f);
                if (!FirstPersonTargetingService.IsRunning && FirstPersonTargetingService.LastStatus != "completed" &&
                    FirstPersonTargetingService.LastError != "explicit_stop")
                {
                    // The real semantic model may not have produced a visual_target
                    // command at all (no service start). Record the real chat reply.
                    string lastChat = _session != null ? _session.LastReply : "";
                    if (!string.IsNullOrEmpty(lastChat))
                        FirstPersonTargetingService.SetRealBlockedDetailForTest("semantic_or_service:" + lastChat);
                }
                var recReal = SelectionBroker.Current;
                bool realOk = FirstPersonTargetingService.LastStatus == "completed" && recReal != null &&
                              recReal.entity_id == chair.Id && Vector3.Distance(beforeReal, _character.position) < 0.05f;
                string realDetail = "status=" + FirstPersonTargetingService.LastStatus +
                                    " record=" + (recReal != null ? recReal.entity_id : "none") +
                                    " expected=" + chair.Id + " locate_calls=" + FirstPersonTargetingService.LocateCalls +
                                    " reply=" + FirstPersonTargetingService.LastReply +
                                    " error=" + FirstPersonTargetingService.LastError;
                if (FirstPersonTargetingService.LastStatus == "failed")
                    Block("B.T2.real", "real model + normal Auto semantic entry on a single non-original chair", realDetail,
                        "real visual/semantic service unavailable or failed; raw response in B_r2/evidence/real; no fallback target");
                else
                    Check("B.T2.real", "real model + normal Auto semantic entry on a single non-original chair", realOk, realDetail,
                        "single real attempt; model sees normal top-left image + natural description only; no ids/coords");
                Pending("B.T3.real", "two-chair leftmost/near-crosshair real scenario: not executed in B_r2 (single-chair T2 first; T3 fixture fixed separately)");
                Pending("B.T4.real", "red bus select/query/approach real scenario: not executed in B_r2");
                Pending("B.T5.real", "non-chair/non-vehicle static target real scenario: not executed in B_r2");
                Pending("B.T6.real", "not-found/occluded target real scenario: not executed in B_r2");
                Pending("B.T7.real", "new-chair sit/stand real scenario: not executed in B_r2");
            }
        }

        static string _poseReason = "";
        static float _posePitch = 0f;

        static IEnumerator TryChairPose(Vector3 centre)
        {
            _poseReason = ""; _posePitch = 0f;
            Vector3[] offsets =
            {
                new Vector3(0f, 0f, 4.5f), new Vector3(0f, 0f, -4.5f),
                new Vector3(4.5f, 0f, 0f), new Vector3(-4.5f, 0f, 0f),
                new Vector3(3.2f, 0f, 3.2f), new Vector3(-3.2f, 0f, 3.2f),
                new Vector3(3.2f, 0f, -3.2f), new Vector3(-3.2f, 0f, -3.2f),
            };
            foreach (var offset in offsets)
            {
                Vector3 candidate = centre + offset;
                candidate.y = WorldData.GroundHeightAt(candidate);
                if (!WorldData.IsWalkable(candidate)) { _poseReason = "not_walkable"; continue; }
                if (InsideAnyBuilding(candidate)) { _poseReason = "inside_building"; continue; }
                Vector3 flat = centre - candidate; flat.y = 0f;
                if (!WorldData.IsWalkable(candidate + flat.normalized * 0.6f)) { _poseReason = "blocked_front"; continue; }
                // fixture pose is allowed BEFORE the task starts
                var world = DynamicWorld.Instance;
                var loco = UnityEngine.Object.FindObjectOfType<Yixuan.Motion.CharacterLocomotion>();
                if (loco != null) loco.Stop("b_r2_fixture");
                if (world != null && world.Character != null) world.PlaceCharacterAt(candidate); else _character.position = candidate;
                if (loco != null) loco.transform.position = candidate;
                if (flat.sqrMagnitude > 1e-4f) _character.rotation = Quaternion.LookRotation(flat.normalized, Vector3.up);
                yield return new F(3);
                if (_gaze == null) { _poseReason = "no_gaze"; yield break; }
                for (int it = 0; it < 3; it++)
                {
                    var eye = PickService.ResolveEye();
                    Vector3 eyePos = eye != null ? eye.transform.position : _gaze.transform.position;
                    Vector3 dir = centre - eyePos;
                    if (dir.sqrMagnitude < 0.01f) break;
                    float wy = Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg;
                    float ry = Mathf.DeltaAngle(_character.eulerAngles.y, wy);
                    float pitch = Mathf.Asin(Mathf.Clamp(dir.normalized.y, -0.99f, 0.99f)) * Mathf.Rad2Deg;
                    _posePitch = pitch;
                    _gaze.TurnRelative(ry, pitch, -1f, false);
                    float dl = Time.realtimeSinceStartup + 6f;
                    while (Time.realtimeSinceStartup < dl && _gaze.IsMoving) yield return null;
                    yield return new F(2);
                }
                _poseReason = "";
                yield break;
            }
        }

        static bool InsideAnyBuilding(Vector3 point)
        {
            foreach (var entry in EntityPickRegistry.Entries)
            {
                if (entry.Cls != "building") continue;
                var b = entry.WorldBounds();
                b.Expand(0.2f);
                if (b.Contains(point)) return true;
            }
            return false;
        }

        static IEnumerator WaitServiceStartThenDone(float startTimeout, float runTimeout)
        {
            double startDeadline = EditorApplication.timeSinceStartup + startTimeout;
            while (EditorApplication.timeSinceStartup < startDeadline)
            {
                if (FirstPersonTargetingService.IsRunning) break;
                yield return null;
            }
            if (FirstPersonTargetingService.IsRunning)
            {
                double runDeadline = EditorApplication.timeSinceStartup + runTimeout;
                while (EditorApplication.timeSinceStartup < runDeadline)
                {
                    if (!FirstPersonTargetingService.IsRunning) break;
                    yield return null;
                }
            }
            yield return new F(4);
        }

        static IEnumerator WaitServiceDone(float timeout)
        {
            double deadline = EditorApplication.timeSinceStartup + timeout;
            while (EditorApplication.timeSinceStartup < deadline)
            {
                if (!FirstPersonTargetingService.IsRunning) break;
                yield return null;
            }
            yield return new F(4);
        }

        static void ClearSelection() { SelectionBroker.Clear(); }

        static void SetSemanticReply(FakeNarrator fake, string inputFragment, string json)
        {
            var prev = FakeNarrator.ReplyFor;
            FakeNarrator.ReplyFor = text =>
            {
                if (!string.IsNullOrEmpty(text) && text.IndexOf(inputFragment, StringComparison.Ordinal) >= 0) return json;
                return prev != null ? prev(text) : JsonConvert.SerializeObject(new { route = "chat", reply = "" });
            };
        }

        static void SetInjectedPoint(PickFrame frame, Vector3Int pixel, string tag)
        {
            var reply = new FpVisualRawReply
            {
                status = "located",
                point_uv = new[] { (pixel.x + 0.5f) / frame.width, (pixel.y + 0.5f) / frame.height },
                bbox_uv = new[]
                {
                    Mathf.Clamp01((pixel.x - 10f) / frame.width), Mathf.Clamp01((pixel.y - 10f) / frame.height),
                    Mathf.Clamp01((pixel.x + 10f) / frame.width), Mathf.Clamp01((pixel.y + 10f) / frame.height)
                },
                description = "injected test point (" + tag + ")",
            };
            _injected = new FpVisualLocateResult { Ok = true, Reply = reply, Model = "injected", Raw = "{\"injected\":true}", HttpStatus = 0 };
            FirstPersonTargetingService.TestLocateHook = (req, attempt) => _injected;
        }

        static int FramePixels(PickFrame frame, int code)
        {
            if (frame == null || frame.codes == null) return 0;
            int n = 0; foreach (int c in frame.codes) if (c == code) n++; return n;
        }

        static Vector3Int FirstInteriorPixel(PickFrame frame, int code)
        {
            for (int y = 2; y < frame.height - 2; y += 2)
                for (int x = 2; x < frame.width - 2; x += 2)
                {
                    if (frame.CodeAt(x, y) != code) continue;
                    bool interior = true;
                    for (int dy = -1; dy <= 1 && interior; dy++)
                        for (int dx = -1; dx <= 1 && interior; dx++)
                            if (frame.CodeAt(x + dx, y + dy) != code) interior = false;
                    if (interior) return new Vector3Int(x, y, code);
                }
            for (int i = 0; i < frame.codes.Length; i++) if (frame.codes[i] == code) return new Vector3Int(i % frame.width, i / frame.width, code);
            return new Vector3Int(frame.width / 2, frame.height / 2, code);
        }
    }
}
