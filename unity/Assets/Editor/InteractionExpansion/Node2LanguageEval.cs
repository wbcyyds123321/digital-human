// Node-2 language evaluation harness.
//
//   Unity.exe -batchmode -projectPath <proj>
//     -executeMethod Yixuan.EditorTools.Node2LanguageEval.Run
//
// Plan-only real-model understanding: for every dev/frozen/unseen utterance the production
// contract -> Lua pipeline runs with the configured real model, saves the raw request/reply,
// contract, Lua, lint and validation result, but executes no Unity tool (PlanOnlyEval).
// End-to-end: 16 utterances go through the real ChatUI entry (ChatSession.SubmitFromUI) and
// are judged by actual actor/flame/vehicle state plus runner receipts.
//
// Armed only by this explicit executeMethod.  It never kills the editor process: it requests
// EditorApplication.Exit(logical code) and the external runner records the real process code.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Yixuan.AI;
using Yixuan.AI.Tasks;
using Yixuan.Dynamic;
using Yixuan.Stage4;
using Yixuan.Stage5;
using Yixuan.World;
using Object = UnityEngine.Object;

namespace Yixuan.EditorTools
{
    [InitializeOnLoad]
    public static class Node2LanguageEval
    {
        const string ScenePath = "Assets/Scenes/SovietBlueHour_TaskPlan.unity";
        const string ArmedKey = "SBH_N2L_ARMED";
        const string StageKey = "SBH_N2L_STAGE";
        const string DirKey = "SBH_N2L_DIR";
        const string ExitKey = "SBH_N2L_EXIT";
        const string T0Key = "SBH_N2L_T0";
        const string RootKey = "SBH_N2L_ROOT";
        const string StartedKey = "SBH_N2L_STARTED";

        public static bool IsArmedForTest { get { return _armed; } }
        public static bool AnyRunStartedInEditorSession { get { return _runStarted; } }
        public static string LastRunDir { get; private set; } = "";

        [Serializable] public class LangCase
        {
            public string id;
            public string dataset_split;
            public string utterance;
            public LangInitialState initial_state;
            public int scene_seed;
            public string[] allowed_target_ids;
            public string[] required_kinds;
            public string[] forbidden_kinds;
            public string[] required_targets;
            public LangTargetGroup[] target_groups;
            public LangPredicates final_predicates;
            public string expect;
            public string reject_contains;
            public float timeout;
            public bool expect_no_action;
            public string hint;
            public string[] forbidden_effects;
        }

        [Serializable] public class LangTargetGroup { public string kind; public string[] ids; }

        [Serializable] public class LangInitialState
        {
            public string actor_mode;
            public float[] position;
            public float yaw;
            public string vehicle_id;
            public bool flame_lit;
            public string bench_id;
        }

        [Serializable] public class LangPredicates
        {
            public string after;
            public bool flame_lit;
            public bool check_flame_lit;
        }

        [Serializable] public class LangSets
        {
            public string schema;
            public string generated_at;
            public LangCounts counts;
            public LangCase[] dev;
            public LangCase[] frozen;
            public LangCase[] unseen;
            public LangCase[] e2e;
        }

        [Serializable] public class LangCounts
        {
            public int dev;
            public int frozen;
            public int unseen;
            public int e2e;
            public int plan_total;
        }

        enum Stage { Idle, WaitRuntime, Plan, E2E, Wrap, Exit }

        static bool _armed;
        static bool _runStarted;
        static Stage _stage = Stage.Idle;
        static float _stageStart;
        static string _evidenceDir = "";
        static string _runDir = "";
        static readonly StringBuilder LogBuilder = new StringBuilder();
        static readonly List<string> CaseResults = new List<string>();
        static readonly List<string> Captures = new List<string>();
        static LangSets _sets;
        static int _planIndex;
        static int _e2eIndex;
        static int _planPass;
        static int _planFail;
        static int _planPending;
        static int _e2ePass;
        static int _e2eFail;
        static int _e2ePending;
        static float _caseStart;
        static string _casePhase = "";
        static bool _waitingRunner;
        static bool _waitingChat;
        static TaskMissionRunner _runner;
        static ChatSession _session;
        static string _caseStatus = "";
        static string _caseReason = "";
        static string _caseRunDirectory = "";
        static bool _modelUnavailable;
        static string _modelError = "";
        static bool _voiceMutedForEval;
        static string _startedAt = "";
        static bool _completedAllStages;
        static string _previousMissionId = "";
        static string _caseMissionId = "";
        static string _baselineReply = "";
        static int _baselineVisionGeneration;
        static Vector3 _baselineActor;
        static string _baselineVehicleId = "";
        static bool _baselineInVehicle;
        static int _baselineFlameChanges;
        static float _actorMaxDisplacement;
        static readonly Dictionary<string, Vector3> VehicleStart = new Dictionary<string, Vector3>();
        static readonly Dictionary<string, float> VehicleMaxDisplacement = new Dictionary<string, float>();

        static Node2LanguageEval()
        {
            EditorApplication.update += Tick;
        }

        public static void Run()
        {
            _armed = true;
            _runStarted = true;
            SessionState.SetString(ArmedKey, "1");
            SessionState.SetString(StageKey, "starting");
            SessionState.SetString(T0Key, "");
            _runDir = ResolveRunDir();
            LastRunDir = _runDir;
            _evidenceDir = Path.Combine(_runDir, "language");
            if (File.Exists(Path.Combine(_evidenceDir, "language_eval_report.json")))
                throw new InvalidOperationException("Refusing to overwrite a completed language invocation: " + _evidenceDir);
            Directory.CreateDirectory(_evidenceDir);
            string configuredRoot = Environment.GetEnvironmentVariable("SBH_NODE2_RUN_DIR");
            if (!File.Exists(Path.Combine(_evidenceDir, "sets.json")) && !string.IsNullOrEmpty(configuredRoot))
            {
                string source = Path.Combine(Path.IsPathRooted(configuredRoot) ? configuredRoot : Path.Combine(Path.GetDirectoryName(Application.dataPath), configuredRoot), "language", "sets.json");
                if (File.Exists(source)) File.Copy(source, Path.Combine(_evidenceDir, "sets.json"));
            }
            SessionState.SetString(DirKey, _evidenceDir);
            SessionState.SetString(RootKey, _runDir);
            _startedAt = DateTime.Now.ToString("o");
            SessionState.SetString(StartedKey, _startedAt);
            _completedAllStages = false;
            _casePhase = "";
            _waitingRunner = false;
            _modelUnavailable = false;
            _modelError = "";
            LogBuilder.Clear();
            CaseResults.Clear();
            Captures.Clear();
            _planIndex = 0;
            _e2eIndex = 0;
            _sets = null;
            _planPass = _planFail = _planPending = 0;
            _e2ePass = _e2eFail = _e2ePending = 0;
            Log("== Node2 language eval (plan-only real model + real ChatUI E2E) ==");
            Log("run_dir=" + _runDir);
            try
            {
                if (File.Exists(ScenePath)) EditorSceneManager.OpenScene(ScenePath);
                else Log("SCENE MISSING " + ScenePath);
            }
            catch (Exception e) { Log("open scene failed: " + e.Message); }
            SessionState.SetString(StageKey, "WaitRuntime");
            _stage = Stage.WaitRuntime;
            _stageStart = (float)EditorApplication.timeSinceStartup;
            EditorApplication.isPlaying = true;
        }

        static string ResolveRunDir()
        {
            string root = Path.GetDirectoryName(Application.dataPath) ?? ".";
            string env = Environment.GetEnvironmentVariable("SBH_NODE2_RUN_DIR");
            string name = string.IsNullOrEmpty(env)
                ? "Captures/InteractionExpansion/run_" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "_node2"
                : env;
            string dir = Path.IsPathRooted(name) ? name : Path.Combine(root, name);
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SBH_IA_INVOCATION_ID")))
                dir = Path.Combine(dir, "invocations", "language_" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + "_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(dir);
            return dir;
        }

        static void EnsureRunState()
        {
            if (string.IsNullOrEmpty(_runDir)) _runDir = SessionState.GetString(RootKey, "");
            if (string.IsNullOrEmpty(_startedAt)) _startedAt = SessionState.GetString(StartedKey, "");
            if (!string.IsNullOrEmpty(_runDir)) LastRunDir = _runDir;
            if (string.IsNullOrEmpty(_evidenceDir))
            {
                _evidenceDir = SessionState.GetString(DirKey, "");
                if (!string.IsNullOrEmpty(_evidenceDir)) Directory.CreateDirectory(_evidenceDir);
            }
        }

        static void Log(string message)
        {
            string line = DateTime.Now.ToString("HH:mm:ss.fff") + " " + message;
            LogBuilder.AppendLine(line);
            Debug.Log("[N2L-EVAL] " + message);
        }

        static float Elapsed() { return (float)EditorApplication.timeSinceStartup - _stageStart; }
        static float CaseElapsed() { return (float)EditorApplication.timeSinceStartup - _caseStart; }

        static void SetStage(Stage stage)
        {
            _stage = stage;
            _stageStart = (float)EditorApplication.timeSinceStartup;
            SessionState.SetString(StageKey, stage.ToString());
            Log("-- stage " + stage + " --");
        }

        static void Tick()
        {
            string exitRequest = SessionState.GetString(ExitKey, "");
            if (!string.IsNullOrEmpty(exitRequest) && !EditorApplication.isPlaying)
            {
                int exitCode = 0;
                int.TryParse(exitRequest, out exitCode);
                SessionState.SetString(ExitKey, "");
                Log("editor exit requested with logical code=" + exitCode);
                EditorApplication.Exit(exitCode);
                return;
            }
            EnsureRunState();
            if (!_armed)
            {
                if (SessionState.GetString(ArmedKey, "") == "1") _armed = true;
                else return;
            }
            if (SessionState.GetString(StageKey, "") == "done") return;
            if (_stage == Stage.Exit) return;
            if (_stage == Stage.Idle)
            {
                if (!EditorApplication.isPlaying) return;
                string savedStage = SessionState.GetString(StageKey, "");
                if (savedStage != "starting" && savedStage != "WaitRuntime")
                {
                    Log("unexpected domain reload interrupted stage=" + savedStage + "; keeping evidence and marking incomplete instead of replaying cases");
                    Finish(false);
                    return;
                }
                SetStage(Stage.WaitRuntime);
            }
            if (!EditorApplication.isPlaying) return;
            try
            {
                switch (_stage)
                {
                    case Stage.WaitRuntime: TickWaitRuntime(); break;
                    case Stage.Plan: TickPlan(); break;
                    case Stage.E2E: TickE2E(); break;
                    case Stage.Wrap: TickWrap(); break;
                }
            }
            catch (Exception e)
            {
                Log("FATAL " + e);
                Finish(false);
            }
        }

        static void TickWaitRuntime()
        {
            if (_runner == null) _runner = Object.FindObjectOfType<TaskMissionRunner>();
            if (_session == null) _session = Object.FindObjectOfType<ChatSession>();
            if (_runner == null || _session == null)
            {
                if (Elapsed() > 90f) { Log("runner/session missing"); Finish(false); }
                return;
            }
            var world = DynamicWorld.Instance;
            if (world == null || !world.Ready)
            {
                if (Elapsed() > 120f) { Log("dynamic world not ready: " + (world != null ? world.Error : "null")); Finish(false); }
                return;
            }
            if (_sets == null)
            {
                string path = Path.Combine(_evidenceDir, "sets.json");
                if (!File.Exists(path))
                {
                    Log("sets.json missing at " + path);
                    Finish(false);
                    return;
                }
                _sets = JsonUtility.FromJson<LangSets>(File.ReadAllText(path));
                if (_sets == null)
                {
                    Log("sets.json parse failed");
                    Finish(false);
                    return;
                }
                Log("sets: dev=" + _sets.dev.Length + " frozen=" + _sets.frozen.Length +
                    " unseen=" + _sets.unseen.Length + " e2e=" + _sets.e2e.Length);
            }
            if (!_voiceMutedForEval)
            {
                _voiceMutedForEval = true;
                if (_session != null)
                {
                    _session.SetVoiceOutputEnabled(false);
                    _session.SilenceSpeech();
                }
                if (_runner != null) _runner.VoiceEnabled = false;
                Log("voice output muted for the evaluation run (batchmode; avoids repeated TTS acknowledgements)");
            }
            SetStage(Stage.Plan);
        }

        // ------------------------------------------------------------ plan-only
        static LangCase[] AllPlanCases()
        {
            var list = new List<LangCase>();
            if (_sets.dev != null) list.AddRange(_sets.dev);
            if (_sets.frozen != null) list.AddRange(_sets.frozen);
            if (_sets.unseen != null) list.AddRange(_sets.unseen);
            return list.ToArray();
        }

        static void TickPlan()
        {
            var cases = AllPlanCases();
            if (_planIndex >= cases.Length)
            {
                _casePhase = "";
                _waitingRunner = false;
                SetStage(Stage.E2E);
                return;
            }
            var test = cases[_planIndex];
            if (!_waitingRunner)
            {
                if (_runner.IsRunning)
                {
                    if (CaseElapsed() > 20f) _runner.Cancel("eval cleanup");
                    return;
                }
                if (_casePhase == "")
                {
                    _caseRunDirectory = "";
                    _caseReason = "";
                    _caseStart = (float)EditorApplication.timeSinceStartup;
                    if (!BeginSetup(test))
                    {
                        RecordPlan(test, "setup_failed", _caseReason, "", new string[0], new string[0], "");
                        _planIndex++;
                        _casePhase = "";
                        return;
                    }
                    _casePhase = "setup_wait";
                    return;
                }
                if (_casePhase == "setup_wait")
                {
                    bool done;
                    if (!PollSetup(test, out done) || CaseElapsed() > 180f)
                    {
                        RecordPlan(test, "setup_failed", _caseReason, "", new string[0], new string[0], "");
                        _planIndex++;
                        _casePhase = "";
                        return;
                    }
                    if (!done) return;
                }
                TaskMissionRunner.PlanOnlyEval = true;
                _caseStart = (float)EditorApplication.timeSinceStartup;
                _casePhase = "submitted";
                _caseStatus = "";
                _caseReason = "";
                _caseRunDirectory = "";
                bool accepted = _runner.Submit(test.utterance);
                if (!accepted)
                {
                    RecordPlan(test, "submit_rejected", "", "", new string[0], new string[0], "");
                    _planIndex++;
                    _casePhase = "";
                    return;
                }
                _waitingRunner = true;
                return;
            }
            if (_runner.IsRunning)
            {
                if (CaseElapsed() > Mathf.Max(60f, test.timeout))
                {
                    _runner.Cancel("eval timeout");
                    RecordPlan(test, "timeout", "runner timeout", "", new string[0], new string[0], "");
                    _waitingRunner = false;
                    _planIndex++;
                    _casePhase = "";
                }
                return;
            }
            _waitingRunner = false;
            _caseStatus = _runner.Evidence != null ? _runner.Evidence.status : "unknown";
            _caseReason = _runner.Evidence != null ? _runner.Evidence.reason : "";
            _caseRunDirectory = _runner.RunDirectory ?? "";
            string contractJson = ReadIfExists(Path.Combine(_caseRunDirectory, "contract.json"));
            string lua = ReadIfExists(Path.Combine(_caseRunDirectory, "mission.lua"));
            var kinds = new List<string>();
            var targets = new List<string>();
            if (!string.IsNullOrEmpty(contractJson))
            {
                try
                {
                    var contract = TaskContractRules.ReadJson(contractJson);
                    if (contract != null && contract.obligations != null)
                    {
                        foreach (var o in contract.obligations)
                        {
                            if (o == null) continue;
                            kinds.Add(o.kind);
                            if (!string.IsNullOrEmpty(o.target_id)) targets.Add(o.target_id);
                        }
                    }
                }
                catch (Exception e)
                {
                    _caseReason += " contract_parse:" + e.Message;
                }
            }
            RecordPlan(test, _caseStatus, _caseReason, lua, kinds.ToArray(), targets.ToArray(), contractJson);
            _planIndex++;
            _casePhase = "";
        }

        static string ReadIfExists(string path)
        {
            try { return File.Exists(path) ? File.ReadAllText(path) : ""; }
            catch { return ""; }
        }

        static void RecordPlan(LangCase test, string status, string reason, string lua,
                               string[] kinds, string[] targets, string contractJson)
        {
            var result = new StringBuilder();
            result.Append("{\n");
            result.Append("  \"id\": \"").Append(Escape(test.id)).Append("\",\n");
            result.Append("  \"split\": \"").Append(Escape(test.dataset_split)).Append("\",\n");
            result.Append("  \"utterance\": \"").Append(Escape(test.utterance)).Append("\",\n");
            result.Append("  \"initial_state\": ").Append(JsonUtility.ToJson(test.initial_state ?? new LangInitialState())).Append(",\n");
            result.Append("  \"status\": \"").Append(Escape(status)).Append("\",\n");
            result.Append("  \"reason\": \"").Append(Escape(reason)).Append("\",\n");
            result.Append("  \"expect\": \"").Append(Escape(test.expect)).Append("\",\n");
            result.Append("  \"contract_kinds\": [\"").Append(string.Join("\",\"", kinds)).Append("\"],\n");
            result.Append("  \"contract_targets\": [\"").Append(string.Join("\",\"", targets)).Append("\"],\n");
            reason = reason ?? "";
            lua = lua ?? "";
            bool outcomeOk;
            if (test.expect == "succeeded")
                outcomeOk = status == "succeeded" && !string.IsNullOrEmpty(lua) && !string.IsNullOrEmpty(contractJson);
            else
                outcomeOk = status == "rejected" && reason.StartsWith("intent_contract_invalid:", StringComparison.Ordinal);
            bool requiredOk = true;
            foreach (var kind in test.required_kinds ?? new string[0])
            {
                bool found = Array.IndexOf(kinds, kind) >= 0;
                if (!found) requiredOk = false;
            }
            bool forbiddenOk = true;
            foreach (var kind in test.forbidden_kinds ?? new string[0])
            {
                bool found = Array.IndexOf(kinds, kind) >= 0 || lua.Contains("game." + kind);
                if (found) forbiddenOk = false;
            }
            bool targetsOk = true;
            foreach (var requiredTarget in test.required_targets ?? new string[0])
            {
                // Empty-target state tools act on the current vehicle, not a catalog target.
                bool stateOnly = test.required_kinds != null && test.required_kinds.Length > 0;
                foreach (var kind in test.required_kinds ?? new string[0])
                    stateOnly &= kind == "stop_vehicle" || kind == "exit_vehicle";
                bool matched = stateOnly || Array.Exists(targets, target => SameTarget(target, requiredTarget));
                if (!matched) { targetsOk = false; break; }
            }
            TaskContract parsedContract = null;
            if (!string.IsNullOrEmpty(contractJson))
                try { parsedContract = TaskContractRules.ReadJson(contractJson); } catch { }
            foreach (var group in test.target_groups ?? new LangTargetGroup[0])
            {
                bool matched = false;
                if (parsedContract != null)
                    foreach (var obligation in parsedContract.obligations ?? new TaskObligation[0])
                        if (obligation != null && (string.IsNullOrEmpty(group.kind) || obligation.kind == group.kind) &&
                            Array.Exists(group.ids ?? new string[0], id => SameTarget(id, obligation.target_id))) matched = true;
                targetsOk &= matched;
            }
            bool parametersOk = true;
            if (test.final_predicates != null && test.final_predicates.check_flame_lit &&
                Array.IndexOf(test.required_kinds ?? new string[0], "set_flame") >= 0)
            {
                parametersOk = parsedContract != null && Array.Exists(parsedContract.obligations ?? new TaskObligation[0],
                    o => o != null && o.kind == "set_flame" && o.lit_specified && o.lit == test.final_predicates.flame_lit);
            }
            bool orderOk = true;
            TaskObligation prior = null;
            foreach (string kind in test.required_kinds ?? new string[0])
            {
                var obligation = parsedContract != null
                    ? Array.Find(parsedContract.obligations ?? new TaskObligation[0], o => o != null && o.kind == kind) : null;
                if (obligation == null || (prior != null && !DependsOn(parsedContract, obligation, prior.id, new HashSet<string>()))) orderOk = false;
                prior = obligation;
            }
            bool modelUnavailable = status == "rejected" &&
                (reason.Contains("intent_contract_unavailable") || reason.Contains("api") ||
                 reason.Contains("network") || reason.Contains("unauthorized") || reason.Contains("key"));
            bool pass = outcomeOk && requiredOk && forbiddenOk && targetsOk && parametersOk && orderOk && !modelUnavailable;
            if (pass) _planPass++;
            else if (modelUnavailable || status == "timeout")
            {
                _planPending++;
                _modelUnavailable = _modelUnavailable || modelUnavailable;
                _modelError = string.IsNullOrEmpty(_modelError) ? reason : _modelError;
            }
            else _planFail++;
            result.Append("  \"checks\": {\"outcome\": ").Append(outcomeOk ? "true" : "false")
                  .Append(", \"required_kinds\": ").Append(requiredOk ? "true" : "false")
                  .Append(", \"forbidden_kinds\": ").Append(forbiddenOk ? "true" : "false")
                  .Append(", \"targets\": ").Append(targetsOk ? "true" : "false")
                  .Append(", \"parameters\": ").Append(parametersOk ? "true" : "false")
                  .Append(", \"ordered_obligations\": ").Append(orderOk ? "true" : "false")
                  .Append(", \"pass\": ").Append(pass ? "true" : "false").Append("},\n");
            result.Append("  \"run_directory\": \"").Append(Escape(_caseRunDirectory)).Append("\",\n");
            result.Append("  \"plan_only\": true,\n");
            result.Append("  \"contract_json_present\": ").Append(!string.IsNullOrEmpty(contractJson) ? "true" : "false").Append(",\n");
            result.Append("  \"lua_present\": ").Append(!string.IsNullOrEmpty(lua) ? "true" : "false").Append(",\n");
            result.Append("  \"lua\": \"").Append(Escape(Truncate(lua, 4000))).Append("\"\n}\n");
            string dir = Path.Combine(_evidenceDir, "results");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, test.id + ".json"), result.ToString());
            if (!string.IsNullOrEmpty(_caseRunDirectory) && Directory.Exists(_caseRunDirectory))
            {
                string copy = Path.Combine(dir, test.id + "_mission");
                try
                {
                    Directory.CreateDirectory(copy);
                    foreach (var name in new[] { "contract.json", "mission.lua", "contract_raw.txt",
                                                 "contract_prompt.txt", "plan_only.json", "events.jsonl" })
                    {
                        string src = Path.Combine(_caseRunDirectory, name);
                        if (File.Exists(src)) File.Copy(src, Path.Combine(copy, name), true);
                    }
                }
                catch { }
            }
            CaseResults.Add(result.ToString());
            Log("PLAN " + test.id + " status=" + status + " pass=" + pass +
                " kinds=" + string.Join("/", kinds) + " reason=" + Truncate(reason, 160));
        }

        static string Truncate(string value, int max)
        {
            if (string.IsNullOrEmpty(value)) return "";
            return value.Length <= max ? value : value.Substring(0, max) + "...";
        }

        // ------------------------------------------------------------------ E2E
        static string _setupKind = "";

        static void TickE2E()
        {
            if (_sets.e2e == null || _e2eIndex >= _sets.e2e.Length)
            {
                SetStage(Stage.Wrap);
                return;
            }
            var test = _sets.e2e[_e2eIndex];
            if (_casePhase == "")
            {
                _caseStart = (float)EditorApplication.timeSinceStartup;
                _casePhase = "setup_start";
                TaskMissionRunner.PlanOnlyEval = false;
                return;
            }
            if (_casePhase == "setup_start")
            {
                _caseReason = "";
                bool ok = BeginSetup(test);
                if (!ok)
                {
                    RecordE2E(test, false, "setup_failed: " + _caseReason, "");
                    AdvanceE2E();
                    return;
                }
                _casePhase = "setup_wait";
                return;
            }
            if (_casePhase == "setup_wait")
            {
                bool done;
                bool ok = PollSetup(test, out done);
                if (!ok)
                {
                    RecordE2E(test, false, "setup_failed: " + _caseReason, "");
                    AdvanceE2E();
                    return;
                }
                if (done)
                {
                    _casePhase = "ready";
                    _caseStart = (float)EditorApplication.timeSinceStartup;
                    return;
                }
                if (CaseElapsed() > 180f)
                {
                    RecordE2E(test, false, "setup_timeout kind=" + _setupKind, "");
                    AdvanceE2E();
                }
                return;
            }
            if (_casePhase == "ready")
            {
                CaptureBaseline();
                _caseStart = (float)EditorApplication.timeSinceStartup;
                _casePhase = "submitted";
                _session.SubmitFromUI(test.utterance);
                return;
            }
            if (_casePhase == "submitted")
            {
                ObserveCaseEffects();
                var visual = Stage5VisualSeatAgent.Instance;
                bool busy = (_runner != null && _runner.IsRunning) || (_session != null && _session.IsBusy) ||
                    (visual != null && visual.IsActive);
                if (CaseElapsed() < 0.75f) return;
                if (busy && CaseElapsed() < Mathf.Max(60f, test.timeout)) return;
                if (busy)
                {
                    if (_runner != null && _runner.IsRunning) _runner.Cancel("eval e2e timeout");
                    if (_session != null) _session.CancelCurrentTurn();
                    RecordE2E(test, false, "e2e_timeout", "");
                    AdvanceE2E();
                    return;
                }
                string detail;
                bool ok = EvaluateE2E(test, out detail);
                Capture("e2e_" + test.id + ".png");
                RecordE2E(test, ok, detail, CurrentCaseEvidence(test) != null ? _runner.RunDirectory : "");
                AdvanceE2E();
            }
        }

        static void AdvanceE2E()
        {
            _e2eIndex++;
            _casePhase = "";
            _setupKind = "";
            _caseMissionId = "";
        }

        static bool BeginSetup(LangCase test)
        {
            if (_runner != null && _runner.IsRunning) _runner.Cancel("language evaluation fixture cleanup");
            if (_session != null && _session.IsBusy) _session.CancelCurrentTurn();
            Stage5VisualSeatAgent.NotifyChatCancelled("language evaluation fixture cleanup");
            var actor = DynamicActorSystem.Instance;
            var seat = Object.FindObjectOfType<Stage4SeatController>();
            if (actor == null) { _caseReason = "actor_missing"; return false; }
            if (actor.InVehicle || actor.CurrentMode == DynamicActorSystem.Mode.Boarding)
            {
                actor.StopCurrent(out _);
                if (actor.InVehicle && !actor.TryExitVehicle(out string exitError))
                { _caseReason = "fixture_exit_failed:" + exitError; return false; }
            }
            // Reset only test fixture transforms after prior actions have stopped.
            // Individual requests cannot inherit another case's parking position.
            var world = DynamicWorld.Instance;
            if (world != null)
            {
                foreach (var vehicle in world.Vehicles) vehicle.ApplyTransform(vehicle.Pivot0, vehicle.Yaw0);
                world.RebuildObstacles();
            }
            if (seat != null && seat.State != Stage4SeatController.SeatState.Idle && seat.State != Stage4SeatController.SeatState.Failed)
            {
                if (seat.State == Stage4SeatController.SeatState.Seated) seat.RequestStand();
                else seat.RequestCancel();
                _setupKind = "stand";
                return true;
            }
            if (seat != null && seat.CanResetFailureState) seat.ResetFailureState();
            return ContinueSetup(test);
        }

        static bool ContinueSetup(LangCase test)
        {
            var world = DynamicWorld.Instance;
            var actor = DynamicActorSystem.Instance;
            var seat = Object.FindObjectOfType<Stage4SeatController>();
            if (world == null || actor == null) { _caseReason = "systems_missing"; return false; }
            var initial = test.initial_state ?? new LangInitialState();
            if (initial.position != null && initial.position.Length >= 2)
                world.PlaceCharacterAt(new Vector3(initial.position[0], 0f, initial.position[initial.position.Length >= 3 ? 2 : 1]));
            if (world.Character != null) world.Character.transform.rotation = Quaternion.Euler(0f, initial.yaw, 0f);
            world.SetFlameState(initial.flame_lit, out _);
            if (initial.actor_mode == "in_vehicle" && !string.IsNullOrEmpty(initial.vehicle_id))
            {
                var v = world.GetVehicle(initial.vehicle_id);
                if (v == null) { _caseReason = "vehicle_missing:" + initial.vehicle_id; return false; }
                world.PlaceCharacterAt(v.CurrentPivot + v.Right * 2.2f);
                string status;
                if (!actor.BeginBoard(initial.vehicle_id, out status))
                {
                    _caseReason = "board_start:" + status;
                    return false;
                }
                _setupKind = "board";
                return true;
            }
            if (initial.actor_mode == "seated" && !string.IsNullOrEmpty(initial.bench_id))
            {
                if (seat == null || !seat.TrySit(initial.bench_id))
                {
                    _caseReason = "sit_start:" + (seat != null ? seat.LastError : "no_controller");
                    return false;
                }
                _setupKind = "sit";
                return true;
            }
            _setupKind = "done";
            return true;
        }

        static bool PollSetup(LangCase test, out bool done)
        {
            done = false;
            var actor = DynamicActorSystem.Instance;
            var seat = Object.FindObjectOfType<Stage4SeatController>();
            if (_setupKind == "stand")
            {
                if (seat != null && seat.State != Stage4SeatController.SeatState.Idle && seat.State != Stage4SeatController.SeatState.Failed) return true;
                if (seat != null && seat.CanResetFailureState) seat.ResetFailureState();
                return ContinueSetup(test);
            }
            if (_setupKind == "board")
            {
                if (actor != null && actor.InVehicle &&
                    (test.initial_state == null || actor.CurrentVehicleId == test.initial_state.vehicle_id))
                {
                    done = true;
                    return true;
                }
                if (actor != null && actor.CurrentMode == DynamicActorSystem.Mode.OnFoot)
                {
                    _caseReason = "board_rejected:" + actor.LastError;
                    return false;
                }
                return true;
            }
            if (_setupKind == "sit")
            {
                if (seat != null && seat.State == Stage4SeatController.SeatState.Seated &&
                    test.initial_state != null && seat.CurrentEntity == test.initial_state.bench_id)
                { done = true; return true; }
                if (seat != null && seat.State == Stage4SeatController.SeatState.Failed)
                {
                    _caseReason = "sit_failed:" + seat.LastError;
                    return false;
                }
                return true;
            }
            done = true;
            return true;
        }

        static bool SameTarget(string a, string b)
        {
            if (a == b) return true;
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
            return a.Replace("anchor/", "") == b.Replace("anchor/", "");
        }

        static bool DependsOn(TaskContract contract, TaskObligation current, string priorId, HashSet<string> visited)
        {
            if (current == null || string.IsNullOrEmpty(current.id) || !visited.Add(current.id)) return false;
            foreach (string dependency in current.after ?? new string[0])
            {
                if (dependency == priorId) return true;
                var ancestor = Array.Find(contract.obligations ?? new TaskObligation[0], o => o != null && o.id == dependency);
                if (DependsOn(contract, ancestor, priorId, visited)) return true;
            }
            return false;
        }

        static void CaptureBaseline()
        {
            var world = DynamicWorld.Instance;
            var actor = DynamicActorSystem.Instance;
            _previousMissionId = _runner.Evidence != null ? _runner.Evidence.mission_id : "";
            _caseMissionId = "";
            _baselineReply = _session != null ? _session.LastReply : "";
            _baselineVisionGeneration = Stage5VisualSeatAgent.Instance != null ? Stage5VisualSeatAgent.Instance.RunGeneration : 0;
            _baselineActor = world.CharacterPosition;
            _baselineVehicleId = actor.CurrentVehicleId;
            _baselineInVehicle = actor.InVehicle;
            _baselineFlameChanges = world.Flame != null ? world.Flame.ChangeCount : 0;
            _actorMaxDisplacement = 0f;
            VehicleStart.Clear();
            VehicleMaxDisplacement.Clear();
            foreach (var vehicle in world.Vehicles)
            {
                VehicleStart[vehicle.Spec.entity_id] = vehicle.CurrentPivot;
                VehicleMaxDisplacement[vehicle.Spec.entity_id] = 0f;
            }
        }

        static void ObserveCaseEffects()
        {
            var world = DynamicWorld.Instance;
            if (world == null) return;
            _actorMaxDisplacement = Mathf.Max(_actorMaxDisplacement, Vector3.Distance(_baselineActor, world.CharacterPosition));
            foreach (var vehicle in world.Vehicles)
                if (VehicleStart.TryGetValue(vehicle.Spec.entity_id, out Vector3 start))
                    VehicleMaxDisplacement[vehicle.Spec.entity_id] = Mathf.Max(VehicleMaxDisplacement[vehicle.Spec.entity_id], Vector3.Distance(start, vehicle.CurrentPivot));
            var evidence = _runner != null ? _runner.Evidence : null;
            if (evidence != null && evidence.mission_id != _previousMissionId && !string.IsNullOrEmpty(evidence.mission_id))
                _caseMissionId = evidence.mission_id;
        }

        static MissionEvidence CurrentCaseEvidence(LangCase test)
        {
            var evidence = _runner != null ? _runner.Evidence : null;
            return evidence != null && !string.IsNullOrEmpty(_caseMissionId) && evidence.mission_id == _caseMissionId &&
                evidence.request == test.utterance ? evidence : null;
        }

        static bool NoForbiddenEffects(LangCase test, out string detail)
        {
            var actor = DynamicActorSystem.Instance;
            var world = DynamicWorld.Instance;
            bool noMotion = test.expect_no_action || Array.IndexOf(test.forbidden_effects ?? new string[0], "movement_or_interaction") >= 0;
            int flameChanges = world.Flame != null ? world.Flame.ChangeCount : -1;
            bool stable = !noMotion || (_actorMaxDisplacement <= 0.05f && actor.InVehicle == _baselineInVehicle &&
                actor.CurrentVehicleId == _baselineVehicleId && flameChanges == _baselineFlameChanges);
            if (noMotion) foreach (var moved in VehicleMaxDisplacement.Values) stable &= moved <= 0.05f;
            var evidence = CurrentCaseEvidence(test);
            foreach (var result in evidence != null ? evidence.results : new List<MissionStepResult>())
            {
                if (Array.IndexOf(test.forbidden_kinds ?? new string[0], result.kind) >= 0) stable = false;
                if (noMotion && (result.kind == "board_vehicle" || result.kind == "drive_vehicle" ||
                    result.kind == "exit_vehicle" || result.kind == "set_flame" || result.kind == "navigate")) stable = false;
            }
            detail = "actor_max_displacement=" + _actorMaxDisplacement.ToString("F3") +
                " flame_delta=" + (flameChanges - _baselineFlameChanges) + " unchanged=" + stable;
            return stable;
        }

        static bool EvaluateE2E(LangCase test, out string detail)
        {
            detail = "";
            var actor = DynamicActorSystem.Instance;
            var world = DynamicWorld.Instance;
            if (actor == null || world == null) { detail = "systems_missing"; return false; }
            string hint = test.hint ?? "";
            if (!NoForbiddenEffects(test, out string effects)) { detail = "forbidden_effect:" + effects; return false; }
            if ((test.expect_no_action && hint != "unsupported") || hint == "no_action" || hint == "cancel")
            {
                detail = effects;
                if (hint == "look_only")
                {
                    var visual = Stage5VisualSeatAgent.Instance;
                    bool selected = visual != null && visual.RunGeneration > _baselineVisionGeneration &&
                        visual.LastStatus == "selected_only" && !string.IsNullOrEmpty(visual.LastSelectedEntity) && !visual.IsActive;
                    detail += " selected_only=" + selected + " entity=" + (visual != null ? visual.LastSelectedEntity : "missing");
                    return selected && !actor.VehicleMoving;
                }
                return !actor.VehicleMoving && !(_runner != null && _runner.IsRunning);
            }
            var evidence = CurrentCaseEvidence(test);
            if (hint == "unsupported")
            {
                detail = "unsupported:" + effects + " reply=" + _session.LastReply;
                bool rejected = evidence != null && evidence.status == "rejected" &&
                    evidence.reason.StartsWith("intent_contract_invalid:", StringComparison.Ordinal) && evidence.results.Count == 0;
                bool semanticRefusal = evidence == null && !string.IsNullOrEmpty(test.reject_contains) &&
                    _session.LastReply != _baselineReply && (_session.LastReply ?? "").Contains(test.reject_contains);
                return (rejected || semanticRefusal) && _actorMaxDisplacement <= 0.05f && !actor.InVehicle;
            }
            if (evidence == null || evidence.status != "succeeded")
            { detail = "missing_or_failed_same_request_evidence:" + (evidence != null ? evidence.status + ":" + evidence.reason : "none"); return false; }
            var ordered = evidence.results.FindAll(r => r != null && r.generation == evidence.generation);
            ordered.Sort((a, b) => a.started.CompareTo(b.started));
            foreach (var result in ordered)
                if (result.status != "succeeded") { detail = "tool_failed:" + result.kind + ":" + result.reason; return false; }
            float previousEnd = -1f;
            foreach (string kind in test.required_kinds ?? new string[0])
            {
                var result = ordered.Find(r => r.kind == kind && r.started >= previousEnd - 0.001f);
                if (result == null) { detail = "missing_ordered_receipt:" + kind; return false; }
                previousEnd = result.ended;
                if ((kind == "query_dynamic" || kind == "observe") && string.IsNullOrEmpty(result.observation))
                { detail = "empty_observation:" + kind; return false; }
                if (kind == "drive_vehicle" && (result.reason != "parked_vehicle" ||
                    Vector3.Distance(result.start_position, result.end_position) < 1f))
                { detail = "drive_did_not_arrive_with_displacement:" + result.reason; return false; }
                if (kind == "navigate" && (result.distance_error > _runner.ArrivalRadius ||
                    Vector3.Distance(result.start_position, result.end_position) < 0.2f))
                { detail = "navigation_did_not_arrive:" + result.reason; return false; }
            }
            if (test.required_kinds == null || test.required_kinds.Length == 0)
            { detail = "missing_evaluation_expectations; regenerate the dataset with the corrected generator"; return false; }
            foreach (var drive in ordered.FindAll(r => r.kind == "drive_vehicle"))
            {
                var board = ordered.FindLast(r => r.kind == "board_vehicle" && r.ended <= drive.started + 0.001f);
                string drivenId = board != null ? board.target_id : _baselineVehicleId;
                if (!VehicleMaxDisplacement.TryGetValue(drivenId ?? "", out float moved) || moved < 1f)
                { detail = "no_actual_displacement_for_driven_vehicle:" + drivenId; return false; }
            }
            foreach (string target in test.required_targets ?? new string[0])
                if (!ordered.Exists(r => SameTarget(r.target_id, target)))
                { detail = "missing_target:" + target; return false; }
            foreach (var group in test.target_groups ?? new LangTargetGroup[0])
                if (!ordered.Exists(r => r.kind == group.kind && Array.Exists(group.ids ?? new string[0], id => SameTarget(id, r.target_id))))
                { detail = "wrong_target_for:" + group.kind; return false; }
            if (hint == "flame" || hint == "flame_off")
            {
                bool want = hint == "flame";
                bool ok = world.Flame != null && world.Flame.Lit == want &&
                    ordered.Exists(r => r.kind == "set_flame" && r.lit_specified && r.lit == want && r.actual_lit_known && r.actual_lit == want);
                detail = "same_request flame lit=" + (world.Flame != null ? world.Flame.Lit : false) + " want=" + want;
                return ok;
            }
            if (hint == "flame_query" || hint == "tram_observe" || hint == "vehicle_vicinity" || hint == "no_action")
            {
                bool ok = !actor.InVehicle && world.Flame.ChangeCount == _baselineFlameChanges;
                detail = "state in_vehicle=" + actor.InVehicle + " mode=" + actor.CurrentMode;
                return ok;
            }
            if (hint == "persist" || hint == "stop" || hint == "stand_board")
            {
                bool ok = actor.InVehicle && !actor.VehicleMoving &&
                    (hint == "stand_board" || actor.CurrentVehicleId == _baselineVehicleId);
                detail = "in_vehicle=" + actor.InVehicle + " status=" + actor.LastStatus;
                return ok;
            }
            if (hint == "vehicle_chain" || hint == "exit_sit")
            {
                bool exited = !actor.InVehicle && actor.CurrentMode == DynamicActorSystem.Mode.OnFoot;
                if (hint == "exit_sit")
                {
                    var seat = Object.FindObjectOfType<Stage4SeatController>();
                    exited &= seat != null && seat.State == Stage4SeatController.SeatState.Seated;
                }
                detail = "exited=" + exited + " mode=" + actor.CurrentMode + " status=" + actor.LastStatus;
                return exited;
            }
            if (hint == "navigate")
            {
                bool ok = !actor.InVehicle && _actorMaxDisplacement >= 0.2f;
                detail = "navigation displacement=" + _actorMaxDisplacement.ToString("F2");
                return ok;
            }
            detail = "unjudged_hint:" + hint;
            return false;
        }

        static void RecordE2E(LangCase test, bool pass, string detail, string runDirectory)
        {
            if (pass) _e2ePass++;
            else _e2eFail++;
            var sb = new StringBuilder();
            sb.Append("{\n  \"id\": \"").Append(Escape(test.id)).Append("\",\n");
            sb.Append("  \"utterance\": \"").Append(Escape(test.utterance)).Append("\",\n");
            sb.Append("  \"hint\": \"").Append(Escape(test.hint)).Append("\",\n");
            sb.Append("  \"pass\": ").Append(pass ? "true" : "false").Append(",\n");
            sb.Append("  \"detail\": \"").Append(Escape(detail)).Append("\",\n");
            sb.Append("  \"run_directory\": \"").Append(Escape(runDirectory ?? "")).Append("\",\n");
            sb.Append("  \"mission_id\": \"").Append(Escape(_caseMissionId)).Append("\",\n");
            sb.Append("  \"actor_max_displacement\": ").Append(_actorMaxDisplacement.ToString("R", CultureInfo.InvariantCulture)).Append(",\n");
            var evidence = CurrentCaseEvidence(test);
            sb.Append("  \"same_request_evidence\": ").Append(evidence != null ? JsonUtility.ToJson(evidence) : "null").Append(",\n");
            sb.Append("  \"actor\": \"").Append(Escape(DynamicActorSystem.Instance != null
                ? DynamicActorSystem.Instance.Describe() : "null")).Append("\",\n");
            sb.Append("  \"flame\": \"").Append(Escape((DynamicWorld.Instance != null &&
                DynamicWorld.Instance.Flame != null)
                ? (DynamicWorld.Instance.Flame.Lit ? "lit" : "extinguished") : "missing")).Append("\",\n");
            sb.Append("  \"chat_reply\": \"").Append(Escape(_session != null ? Truncate(_session.LastReply, 300) : "")).Append("\"\n}\n");
            string dir = Path.Combine(_evidenceDir, "e2e");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, test.id + ".json"), sb.ToString());
            Log("E2E " + test.id + " pass=" + pass + " :: " + detail);
        }

        // ----------------------------------------------------------------- wrap
        static void TickWrap()
        {
            int total = _planPass + _planFail + _planPending;
            if (total == 0) { Log("no plan cases ran"); Finish(false); return; }
            Log("plan results pass=" + _planPass + " fail=" + _planFail + " pending=" + _planPending);
            Log("e2e results pass=" + _e2ePass + " fail=" + _e2eFail + " pending=" + _e2ePending);
            _completedAllStages = true;
            Finish(_planFail == 0 && _e2eFail == 0);
        }

        static void Capture(string name)
        {
            var camera = Camera.main;
            var world = DynamicWorld.Instance;
            if (world != null && world.CameraDirector != null && world.CameraDirector.Camera != null)
                camera = world.CameraDirector.Camera;
            if (camera == null) return;
            int width = 1280, height = 720;
            var rt = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32);
            var previousTarget = camera.targetTexture;
            var previousActive = RenderTexture.active;
            Texture2D texture = null;
            try
            {
                rt.Create();
                camera.targetTexture = rt;
                camera.Render();
                RenderTexture.active = rt;
                texture = new Texture2D(width, height, TextureFormat.RGB24, false);
                texture.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                texture.Apply(false, false);
                string path = Path.Combine(_evidenceDir, "e2e", name);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllBytes(path, texture.EncodeToPNG());
                Captures.Add(name);
            }
            catch { }
            finally
            {
                camera.targetTexture = previousTarget;
                RenderTexture.active = previousActive;
                if (texture != null) Object.Destroy(texture);
                rt.Release();
                Object.Destroy(rt);
            }
        }

        static void Finish(bool logicalPass)
        {
            if (_stage == Stage.Exit) return;
            int code = 0;
            if (!logicalPass) code = 1;
            else if (_planPending > 0 || _e2ePending > 0) code = 2;
            try { WriteReport(code, logicalPass); }
            catch (Exception e) { Debug.LogError("[N2L-EVAL] report write failed: " + e); }
            _armed = false;
            SessionState.SetString(ArmedKey, "");
            SessionState.SetString(StageKey, "done");
            Log("language eval finished pass=" + logicalPass + " plan=" + _planPass + "/" + _planFail +
                "/" + _planPending + " e2e=" + _e2ePass + "/" + _e2eFail + "/" + _e2ePending +
                " logical_code=" + code);
            _stage = Stage.Exit;
            SessionState.SetString(ExitKey, code.ToString());
            if (EditorApplication.isPlaying)
            {
                EditorApplication.isPlaying = false;
                return;
            }
            SessionState.SetString(ExitKey, "");
            EditorApplication.Exit(code);
        }

        static void WriteReport(int code, bool logicalPass)
        {
            Directory.CreateDirectory(_evidenceDir);
            var sb = new StringBuilder();
            sb.Append("{\n");
            sb.Append("  \"schema\": \"sbh.node2-language-eval.v1\",\n");
            sb.Append("  \"milestone\": \"node2_vehicles_tram_flame_language\",\n");
            sb.Append("  \"review_round\": 1,\n");
            sb.Append("  \"started_at\": \"").Append(_startedAt).Append("\",\n");
            sb.Append("  \"finished_at\": \"").Append(DateTime.Now.ToString("o")).Append("\",\n");
            sb.Append("  \"run_dir\": \"").Append(Escape(_runDir)).Append("\",\n");
            sb.Append("  \"completed_all_stages\": ").Append(_completedAllStages ? "true" : "false").Append(",\n");
            sb.Append("  \"plan_only\": true,\n");
            sb.Append("  \"plan_results\": {\"pass\": ").Append(_planPass)
              .Append(", \"fail\": ").Append(_planFail)
              .Append(", \"pending\": ").Append(_planPending).Append("},\n");
            sb.Append("  \"e2e_results\": {\"pass\": ").Append(_e2ePass)
              .Append(", \"fail\": ").Append(_e2eFail)
              .Append(", \"pending\": ").Append(_e2ePending).Append("},\n");
            sb.Append("  \"logical_pass\": ").Append(logicalPass ? "true" : "false").Append(",\n");
            sb.Append("  \"logical_exit_code\": ").Append(code).Append(",\n");
            sb.Append("  \"internal_kill\": false,\n");
            sb.Append("  \"exit_path\": \"logical_result_editor_exit\",\n");
            sb.Append("  \"model\": \"configured real model via ChatSession/TaskMissionRunner planner; usage fields are recorded by the client when available\",\n");
            sb.Append("  \"model_unavailable\": ").Append(_modelUnavailable ? "true" : "false").Append(",\n");
            sb.Append("  \"model_error\": \"").Append(Escape(_modelError)).Append("\",\n");
            sb.Append("  \"note\": \"plan-only cases generated the real contract+Lua but executed no tool; E2E cases used the real ChatUI entry and are judged by actual game state/receipts.\",\n");
            sb.Append("  \"captures\": [\"").Append(string.Join("\",\"", Captures.ToArray())).Append("\"],\n");
            sb.Append("  \"cases\": [\n    ").Append(string.Join(",\n    ", CaseResults.ToArray())).Append("\n  ]\n}\n");
            File.WriteAllText(Path.Combine(_evidenceDir, "language_eval_report.json"), sb.ToString());
            File.WriteAllText(Path.Combine(_evidenceDir, "language_eval.log"), LogBuilder.ToString());
            File.WriteAllText(Path.Combine(_evidenceDir, "EXIT_CODE"), code.ToString());
            File.WriteAllText(Path.Combine(_evidenceDir, "COMPLETED.json"),
                "{\"completed_all_stages\": " + (_completedAllStages ? "true" : "false") + ", \"logical_exit_code\": " + code +
                ", \"internal_kill\": false, \"exit_path\": \"logical_result_editor_exit\"}\n");
        }

        static string Escape(string value)
        {
            if (value == null) return "";
            return value.Replace("\\", "/").Replace("\"", "'").Replace("\n", " ").Replace("\r", " ");
        }
    }
}
