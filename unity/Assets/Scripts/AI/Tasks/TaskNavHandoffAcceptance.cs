using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using Yixuan.World;

namespace Yixuan.AI.Tasks
{
    public sealed class TaskNavHandoffAcceptance : MonoBehaviour
    {
        public string Output = "Captures/Acceptance/nav_handoff_fix_2026-11-04";
        public bool Running;
        public bool BrainOnly;
        public bool ChainOnly;
        public bool BrainTurnsOnly;
        public bool PlainArrivalOnly;
        public string Current = "";
        public RuntimeTestReport Report;

        const string AnchorA = "obelisk";
        const string AnchorB = "central_plaza";
        static readonly string InsertionPhrase =
            "你现在先停一下，做一个挥舞双手的动作，然后继续把没走完的路走完";
        static readonly string InsertionPhrase2 = "先挥舞双手，然后继续前往目的地";
        static readonly string ReplacementPhrase = "别去方尖碑了，改去中央广场";
        static readonly string GazePhrase = "你先把视角回正一下";

        ChatSession s;
        TaskMissionRunner r;
        Vector3 _home;
        float _homeYaw;
        bool _mic, _voice, _taskVoice, _panelVisible;
        TaskInputMode _mode;
        string _evidenceRoot;
        ILLMClient _oldClient, _oldPlanner, _oldNarrator;
        TaskMissionPanel _panel;
        ScriptedChatClient _chat;
        ScriptedNarrator _narrator;
        ScriptedPlanner _planner;
        string _wave, _waveClip;
        bool _oldBrainProactive;
        string _oldStreamMode, _oldVoiceInputMode, _oldVoiceEndpoint;
        bool _oldVoiceInputEnabled, _oldVoiceVerbose;

        public void Begin()
        {
            if (Running) return;
            s = FindObjectOfType<ChatSession>();
            if (s == null) { Debug.LogError("[NavHandoffAcceptance] 找不到 ChatSession，无法验收"); return; }
            s.Missions = s.Missions != null ? s.Missions : FindObjectOfType<TaskMissionRunner>();
            r = s.Missions;
            if (r == null) { Debug.LogError("[NavHandoffAcceptance] 找不到 TaskMissionRunner，无法验收"); return; }
            r.ResolveRefs();
            _home = r.HomePosition; _homeYaw = r.HomeYaw;
            _mic = s.VoiceInput != null && s.VoiceInput.Enabled;
            _voice = s.Config != null && s.Config.VoiceEnabled;
            _taskVoice = r.VoiceEnabled;
            _mode = s.InputMode;
            _evidenceRoot = r.EvidenceRoot;
            _oldClient = s.Client; _oldPlanner = r.PlannerOverride; _oldNarrator = s.NarratorOverride;
            _oldBrainProactive = s.Brain != null && s.Brain.EnableProactive;
            _oldStreamMode = s.Config != null ? s.Config.StreamMode : null;
            _oldVoiceInputEnabled = s.Config != null && s.Config.VoiceInputEnabled;
            _oldVoiceInputMode = s.Config != null ? s.Config.VoiceInputMode : null;
            _oldVoiceEndpoint = s.VoiceInput != null ? s.VoiceInput.Endpoint : null;
            _oldVoiceVerbose = s.VoiceInput != null && s.VoiceInput.VerboseLog;
            _panel = FindObjectOfType<TaskMissionPanel>();
            _panelVisible = _panel != null && _panel.Visible;
            _wave = null; _waveClip = null;
            foreach (var g in TaskGestureSkills.Available())
            {
                if ((g.Label ?? "").Contains("双手")) { _wave = g.Id; _waveClip = g.ClipName; break; }
            }
            if (_wave == null)
                foreach (var g in TaskGestureSkills.Available())
                    if ((g.Label ?? "").Contains("挥手")) { _wave = g.Id; _waveClip = g.ClipName; break; }
            if (_wave == null) { Debug.LogError("[NavHandoffAcceptance] manifest 里没有双手/挥手动作，无法验收"); return; }
            Report = new RuntimeTestReport { suite = "chat navigation adoption + pause/action/resume (test doubles: chat/narrator/planner)", cases = new List<RuntimeTestResult>() };
            Running = true; Current = "setup";
            Debug.Log("[NavHandoffAcceptance] 开始：wave=" + _wave + " clip=" + _waveClip + " anchorA=" + AnchorA + " anchorB=" + AnchorB);
            StartCoroutine(Run());
        }

        string OutDir { get { return Path.GetFullPath(Path.Combine(Application.dataPath, "..", Output)); } }
        static float Now { get { return Time.realtimeSinceStartup; } }
        static float Flat(Vector3 a, Vector3 b) { return TaskTargetCatalog.FlatDistance(a, b); }
        float AnchorDistance(string anchor)
        {
            WorldAnchor a = WorldData.GetAnchor(anchor);
            return a == null ? -1f : Flat(r.Motor.transform.position, a.Position);
        }
        bool ArrivedAt(string anchor, float tolerance)
        {
            if (r.Motor.HasTarget || r.Motor.IsMoving) return false;
            if (r.Motor.LastStatus != "arrived") return false;
            float distance = AnchorDistance(anchor);
            return r.Evidence != null && r.Evidence.status == "succeeded" && distance >= 0f && distance <= tolerance;
        }

        void Record(string name, bool ok, string expected, string actual, string reason = "", string dir = "")
        {
            Report.cases.Add(new RuntimeTestResult { name = name, pass = ok, expected = expected, actual = actual, reason = reason, directory = dir });
            Save();
            Debug.Log("[NavHandoffAcceptance] " + (ok ? "PASS " : "FAIL ") + name + " | expected=" + expected + " | actual=" + actual +
                      (string.IsNullOrEmpty(reason) ? "" : " | " + reason));
        }
        void Save()
        {
            Directory.CreateDirectory(OutDir);
            File.WriteAllText(Path.Combine(OutDir, "acceptance_report.json"), JsonUtility.ToJson(Report, true));
        }
        IEnumerator WaitFor(Func<bool> condition, float seconds)
        {
            float end = Now + seconds;
            while (!condition() && Now < end) yield return null;
        }
        void ResetPose(string queuedReply = null)
        {
            if (r.IsRunning) r.Cancel("acceptance_reset");
            s.CancelDialogueTurn();
            r.StopAttention();
            using (TaskAuthority.Enter(null)) r.Motor.Stop();
            r.Motor.transform.position = _home;
            r.Motor.transform.rotation = Quaternion.Euler(0f, _homeYaw, 0f);
            _chat.Clear(); _chat.Queue(string.IsNullOrEmpty(queuedReply) ? PlanJson(AnchorA) : queuedReply);
            s.Client = _chat;
        }

        IEnumerator Run()
        {
            try
            {
                s.InputMode = TaskInputMode.Auto;
                s.SetVoiceOutputEnabled(false);
                if (s.VoiceInput != null) s.VoiceInput.SetEnabled(false);
                r.VoiceEnabled = false;
                r.EvidenceRoot = Path.Combine(Output, "missions");
                if (s.Config != null) { s.Config.VoiceInputEnabled = false; s.Config.VoiceInputMode = "off"; }
                _chat = new ScriptedChatClient(); _narrator = new ScriptedNarrator();
                _planner = new ScriptedPlanner(_wave);
                s.Client = _chat; s.NarratorOverride = _narrator; r.PlannerOverride = _planner;
                if (_panel != null) _panel.Visible = false;
                if (s.Brain != null) s.Brain.EnableProactive = false;
                if (s.Vision != null) { s.Vision.SetGameView(false); s.Vision.SetWebcam(false); }
                yield return null;
                Record("setup/wave_from_manifest", _wave != null && !string.IsNullOrEmpty(_waveClip),
                    "gesture id and clip resolved from MixamoActionManifest-backed TaskGestureSkills", _wave + "|" + _waveClip);
                if (BrainOnly)
                {
                    yield return CaseBrain();
                    yield break;
                }
                if (ChainOnly)
                {
                    yield return CaseBrainChain();
                    yield break;
                }
                if (BrainTurnsOnly)
                {
                    yield return CaseBrainTurnDelay();
                    yield break;
                }
                if (PlainArrivalOnly)
                {
                    yield return CasePlainArrival();
                    yield break;
                }
                yield return CaseStatic();
                yield return CaseA();
                yield return CaseH();
                yield return CaseB();
                yield return CaseC();
                yield return CaseD();
                yield return CaseF();
                yield return CaseG();
                yield return CaseRegression();
                yield return CaseBrain();
                yield return CaseBrainChain();
                yield return CaseBrainTurnDelay();
                yield return CasePlainArrival();
                yield return CaseContractIsolation();
                yield return CaseSuspendStopSynonyms();
                yield return CaseStreaming();
            }
            finally
            {
                if (r != null && r.IsRunning) r.Cancel("acceptance_finished");
                if (s != null)
                {
                    s.CancelDialogueTurn();
                    s.Client = _oldClient;
                    s.NarratorOverride = _oldNarrator;
                    s.StreamOverride = null;
                    s.InputMode = _mode;
                    if (s.Brain != null) s.Brain.EnableProactive = _oldBrainProactive;
                    if (s.Config != null)
                    {
                        if (_oldStreamMode != null) s.Config.StreamMode = _oldStreamMode;
                        if (_oldVoiceInputMode != null) s.Config.VoiceInputMode = _oldVoiceInputMode;
                        s.Config.VoiceInputEnabled = _oldVoiceInputEnabled;
                    }
                    if (s.VoiceInput != null)
                    {
                        if (_oldVoiceEndpoint != null) s.VoiceInput.Endpoint = _oldVoiceEndpoint;
                        s.VoiceInput.VerboseLog = _oldVoiceVerbose;
                        s.VoiceInput.SetEnabled(_mic);
                    }
                    s.SetVoiceOutputEnabled(_voice);
                }
                if (r != null) { r.PlannerOverride = _oldPlanner; r.EvidenceRoot = _evidenceRoot; r.VoiceEnabled = _taskVoice; }
                if (_panel != null) _panel.Visible = _panelVisible;
                int passed = 0, total = Report.cases.Count;
                foreach (var c in Report.cases) if (c.pass) passed++;
                Report.done = true; Running = false; Current = "done";
                Directory.CreateDirectory(OutDir);
                File.WriteAllText(Path.Combine(OutDir, "checkpoint.txt"),
                    "passed=" + passed + "/" + total + " at " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "\n");
                Save();
                Debug.Log("[NavHandoffAcceptance] 完成：passed=" + passed + "/" + total + "，报告=" + Path.Combine(OutDir, "acceptance_report.json"));
            }
        }

        IEnumerator CaseStatic()
        {
            Current = "static";
            Record("static/user_insertion_sentence_routes_to_insertion", TaskMissionRunner.LooksLikeInsertion(InsertionPhrase),
                "original user phrasing detected as temporary insertion", InsertionPhrase);
            Record("static/continue_to_destination_routes_to_insertion", TaskMissionRunner.LooksLikeInsertion(InsertionPhrase2),
                "先…然后继续前往目的地 phrasing detected as temporary insertion", InsertionPhrase2);
            Record("static/replacement_is_not_insertion",
                !TaskMissionRunner.LooksLikeInsertion(ReplacementPhrase) &&
                !TaskMissionRunner.LooksLikeInsertion("先走到中央广场，然后再走到方尖碑") &&
                !TaskMissionRunner.LooksLikeInsertion(GazePhrase),
                "destination change / gaze correction are not fast-routed as insertion",
                ReplacementPhrase + " | 先走到中央广场，然后再走到方尖碑 | " + GazePhrase);
            ResetPose();
            bool accepted = r.Submit("你先停下挥手，然后继续没走完的任务。");
            bool honest = !accepted && r.Evidence != null && r.Evidence.status == "rejected" &&
                          (r.Evidence.reason ?? "").Contains("no_active_mission_to_resume");
            Record("static/no_recoverable_mission_is_rejected_honestly", honest,
                "no saved navigation/mission -> explicit rejection, no false success",
                "accepted=" + accepted + " status=" + (r.Evidence != null ? r.Evidence.status : "?") + " reason=" + (r.Evidence != null ? r.Evidence.reason : "?"),
                "", r.RunDirectory);
            ResetPose();
            yield return null;
        }

        IEnumerator CaseA()
        {
            Current = "A";
            ResetPose();
            yield return null;
            s.Submit("请你现在走到方尖碑");
            yield return WaitFor(() => r.IsRunning && r.IsAdoptedNavigation && r.Motor.IsMoving, 20);
            bool adopted = r.IsRunning && r.IsAdoptedNavigation && r.Motor.TargetAnchor == AnchorA && r.Context != null;
            string missionId = r.Context != null ? r.Context.MissionId : null;
            Record("A/chat_navigation_adopted_as_pausable_mission", adopted,
                "Auto chat walk (Director->MoveToAnchor) becomes a running mission with evidence",
                "running=" + r.IsRunning + " adopted=" + r.IsAdoptedNavigation + " anchor=" + r.Motor.TargetAnchor + " evidence=" + r.RunDirectory,
                "policy=" + (r.Evidence != null ? r.Evidence.contract_source : "?"), r.RunDirectory);
            Vector3 start = r.Motor.transform.position;
            yield return WaitFor(() => Flat(start, r.Motor.transform.position) >= 1.0f, 25);
            yield return WaitFor(() => s.StateName == "Idle", 10);
            Vector3 beforePause = r.Motor.transform.position;
            s.Submit(InsertionPhrase);
            yield return WaitFor(() => r.IsSuspended, 12);
            bool paused = r.IsSuspended && r.IsRunning && !r.Motor.IsMoving && !r.Motor.HasTarget && string.IsNullOrEmpty(r.Motor.TargetAnchor);
            Record("A/navigation_paused_before_temporary_action", paused,
                "moving halted, route/target retained by the suspended parent",
                "suspended=" + r.IsSuspended + " moving=" + r.Motor.IsMoving + " hasTarget=" + r.Motor.HasTarget + " anchor=" + (r.Motor.TargetAnchor ?? "null"),
                "insertion=" + (r.ActiveInsertion != null ? r.ActiveInsertion.Phase : "pending"), r.RunDirectory);
            float drift = 0f, settle = -1f;
            if (r.IsSuspended)
            {
                yield return null;
                Vector3 pausedAt = r.Motor.transform.position;
                settle = Flat(beforePause, pausedAt);
                float until = Now + 0.35f;
                while (Now < until) { drift = Mathf.Max(drift, Flat(pausedAt, r.Motor.transform.position)); yield return null; }
            }
            Record("A/stop_transition_within_handoff_bound", r.IsSuspended && settle >= 0f && settle < 0.03f,
                "the stopping frame adds no travel beyond the locomotion handoff bound (0.03m)",
                settle.ToString("F4") + "m", "measured from submit to first suspended frame", r.RunDirectory);
            Record("A/no_translation_while_action_plays", r.IsSuspended && drift < 0.02f,
                "body position unchanged while the temporary action plays", drift.ToString("F4") + "m", "", r.RunDirectory);
            bool sawClip = false;
            float gestureEnd = Now + 20f;
            while (Now < gestureEnd && (r.IsSuspended || r.ActiveInsertion != null))
            {
                if (s.Character != null && s.Character.AnimPlayer != null && s.Character.AnimPlayer.CurrentName == _waveClip) sawClip = true;
                yield return null;
            }
            Record("A/real_manifest_animation_played", sawClip,
                "temporary action plays the real manifest clip for the requested gesture", "clip=" + _waveClip);
            yield return WaitFor(() => !r.IsSuspended && r.Motor.HasTarget, 15);
            bool resumed = r.Motor.HasTarget && r.Motor.TargetAnchor == AnchorA && r.IsRunning && r.Context != null && r.Context.MissionId == missionId;
            Record("A/resumed_remaining_route_same_parent", resumed,
                "same adopted parent mission resumes the remaining route to the original target",
                "hasTarget=" + r.Motor.HasTarget + " anchor=" + (r.Motor.TargetAnchor ?? "null") + " sameMission=" + (r.Context != null && r.Context.MissionId == missionId),
                "phase=" + r.Phase, r.RunDirectory);
            yield return WaitFor(() => !r.IsRunning, 75);
            float arrive = WorldData.GetAnchor(AnchorA) != null ? Flat(r.Motor.transform.position, WorldData.GetAnchor(AnchorA).Position) : -1f;
            bool arrived = !r.IsRunning && r.Evidence != null && r.Evidence.status == "succeeded" && arrive >= 0f && arrive <= 0.4f;
            Record("A/arrived_original_target", arrived,
                "chat navigation completes at the original anchor after the insertion",
                "status=" + (r.Evidence != null ? r.Evidence.status : "?") + " distance=" + arrive.ToString("F2") + "m reason=" + (r.Evidence != null ? r.Evidence.reason : "?"),
                "", r.RunDirectory);
            bool completionFilled = r.Evidence != null && r.Evidence.completion != null &&
                                    r.Evidence.completion.program_succeeded && r.Evidence.completion.task_succeeded;
            string actionFacts = s.CurrentActionFacts;
            bool factsSayDone = !string.IsNullOrEmpty(actionFacts) && actionFacts.Contains("任务已完成") && !actionFacts.Contains("没有全部完成");
            Record("A/completion_receipt_filled_on_arrival", completionFilled,
                "adopted navigation arrival writes a real completion receipt (not only status=succeeded)",
                r.Evidence == null || r.Evidence.completion == null ? "no completion" :
                ("program=" + r.Evidence.completion.program_succeeded + " task=" + r.Evidence.completion.task_succeeded + " reason=" + r.Evidence.completion.reason),
                "", r.RunDirectory);
            Record("A/controlled_facts_report_task_completed", factsSayDone,
                "the controlled-fact status layer reports the completed navigation instead of generic failure text",
                actionFacts, "", r.RunDirectory);
            string childDir = r.Evidence != null && r.Evidence.insertions.Count == 1 ? r.Evidence.insertions[0].directory : null;
            string parentEvents = ReadIfExists(r.RunDirectory, "events.jsonl");
            string childEvidenceRaw = childDir != null ? ReadIfExists(childDir, "evidence.json") : "";
            bool childSucceeded = r.Evidence != null && r.Evidence.insertions.Count == 1 && r.Evidence.insertions[0].status == "succeeded";
            bool handoffEvidence = parentEvents.Contains("ChatNavigationAdopted") && parentEvents.Contains("MissionSuspended") &&
                                   parentEvents.Contains("InsertionFinished") && parentEvents.Contains("MissionResumed");
            bool childRealAnimation = childEvidenceRaw.Contains(_waveClip) && childEvidenceRaw.Contains("succeeded");
            bool noReplay = r.Evidence != null && r.Evidence.results.Count == 1;
            Record("A/handoff_evidence_child_receipt_no_replay", childSucceeded && handoffEvidence && childRealAnimation && noReplay,
                "parent evidence records adoption/suspend/resume, child receipt records the manifest clip, route not replayed",
                "child=" + childSucceeded + " handoff=" + handoffEvidence + " clip=" + childRealAnimation + " singleLeg=" + noReplay,
                "parent=" + r.RunDirectory + " child=" + childDir, r.RunDirectory);
            yield return null;
        }

        IEnumerator CaseH()
        {
            Current = "H";
            ResetPose();
            yield return null;
            s.Submit("请你现在走到方尖碑");
            yield return WaitFor(() => r.IsRunning && r.IsAdoptedNavigation && r.Motor.IsMoving, 20);
            string missionId = r.Context != null ? r.Context.MissionId : null;
            Vector3 start = r.Motor.transform.position;
            yield return WaitFor(() => Flat(start, r.Motor.transform.position) >= 0.6f, 20);
            TaskContract fixture = new TaskContract
            {
                forbid_speech = true,
                obligations = new[] { new TaskObligation { id = "g", source = "验收", kind = "gesture", action_id = _wave } }
            };
            bool accepted = r.InsertContractForTest("验收：暂停挥手再继续剩余任务",
                "local r=game.gesture('" + _wave + "');return {ok=r.ok,reason=r.reason}", fixture);
            yield return WaitFor(() => r.IsSuspended, 6);
            bool pausedByFixture = r.IsSuspended && r.IsRunning && r.Context != null && r.Context.MissionId == missionId;
            yield return WaitFor(() => !r.IsSuspended && r.Motor.HasTarget, 20);
            bool resumed = r.Motor.HasTarget && r.Motor.TargetAnchor == AnchorA && r.IsRunning &&
                           r.Evidence.insertions.Count == 1 && r.Evidence.insertions[0].status == "succeeded";
            Record("H/legacy_fixture_lua_insertion_on_adopted_parent", accepted && pausedByFixture && resumed,
                "existing InsertContractForTest Lua insertion still suspends/resumes an adopted chat navigation",
                "accepted=" + accepted + " paused=" + pausedByFixture + " resumed=" + resumed +
                " child=" + (r.Evidence != null && r.Evidence.insertions.Count > 0 ? r.Evidence.insertions[0].status : "none"),
                "same mission + fixture contract(source=验收)", r.RunDirectory);
            r.Cancel("acceptance");
            yield return null;
        }

        IEnumerator CaseB()
        {
            Current = "B";
            ResetPose();
            yield return null;
            s.Submit("请你现在走到方尖碑");
            yield return WaitFor(() => r.IsRunning && r.IsAdoptedNavigation && r.Motor.IsMoving, 20);
            string missionId = r.Context != null ? r.Context.MissionId : null;
            Vector3 start = r.Motor.transform.position;
            yield return WaitFor(() => Flat(start, r.Motor.transform.position) >= 0.8f, 20);
            yield return WaitFor(() => s.StateName == "Idle", 10);
            string replyBefore = s.LastReply;
            s.Submit("你叫什么名字");
            yield return WaitFor(() => !string.IsNullOrEmpty(s.LastReply) && s.LastReply != replyBefore, 8);
            Vector3 p0 = r.Motor.transform.position;
            yield return WaitFor(() => Flat(p0, r.Motor.transform.position) >= 0.15f, 8);
            bool chatKeepsWalking = r.IsRunning && r.Motor.IsMoving && r.Context != null && r.Context.MissionId == missionId &&
                                    Flat(p0, r.Motor.transform.position) >= 0.15f;
            Record("B/chat_keeps_navigation_running", chatKeepsWalking,
                "ordinary social chat is answered in the dialogue lane without stopping/replacing navigation",
                "moving=" + r.Motor.IsMoving + " sameMission=" + (r.Context != null && r.Context.MissionId == missionId) + " reply=" + s.LastReply,
                "policy=test-double narrator chat route", r.RunDirectory);
            Vector3 p1 = r.Motor.transform.position;
            s.Submit("你现在走到哪儿了？");
            yield return WaitFor(() => !string.IsNullOrEmpty(s.LastReply) && s.LastReply.Contains("走向"), 8);
            yield return WaitFor(() => Flat(p1, r.Motor.transform.position) >= 0.15f, 8);
            bool statusKeepsWalking = r.IsRunning && r.Motor.IsMoving && Flat(p1, r.Motor.transform.position) >= 0.15f;
            Record("B/progress_query_keeps_navigation_running", statusKeepsWalking,
                "progress query answers from controlled facts without stopping/replacing navigation",
                "moving=" + r.Motor.IsMoving + " reply=" + s.LastReply, "policy=test-double narrator status route", r.RunDirectory);
            r.Cancel("acceptance");
            yield return null;
        }

        IEnumerator CaseC()
        {
            Current = "C";
            ResetPose();
            yield return null;
            s.Submit("请你现在走到方尖碑");
            yield return WaitFor(() => r.IsRunning && r.IsAdoptedNavigation && r.Motor.IsMoving, 20);
            Vector3 start = r.Motor.transform.position;
            yield return WaitFor(() => Flat(start, r.Motor.transform.position) >= 0.5f, 20);
            yield return WaitFor(() => s.StateName == "Idle", 10);
            s.Submit("停下");
            yield return WaitFor(() => !r.IsRunning && !r.Motor.HasTarget && !r.Motor.IsMoving, 10);
            bool stopped = !r.IsRunning && !r.Motor.HasTarget && !r.Motor.IsMoving && string.IsNullOrEmpty(r.Motor.TargetAnchor) &&
                           r.Evidence != null && r.Evidence.status == "cancelled";
            Record("C/explicit_stop_cancels_adopted_navigation", stopped,
                "用户停止 cancels the adopted mission (not only the motor) and leaves no running body",
                "running=" + r.IsRunning + " hasTarget=" + r.Motor.HasTarget + " status=" + (r.Evidence != null ? r.Evidence.status : "?"),
                "phase=" + r.Phase, r.RunDirectory);
            Vector3 frozen = r.Motor.transform.position;
            yield return new WaitForSecondsRealtime(1.2f);
            bool noResurrect = !r.IsRunning && !r.Motor.HasTarget && !r.Motor.IsMoving && Flat(frozen, r.Motor.transform.position) < 0.01f;
            Record("C/no_late_resume_after_stop", noResurrect,
                "no cancelled insertion/resume can come back after the stop",
                "running=" + r.IsRunning + " drift=" + Flat(frozen, r.Motor.transform.position).ToString("F3") + "m", "", r.RunDirectory);
            yield return null;
        }

        IEnumerator CaseD()
        {
            Current = "D";
            ResetPose();
            yield return null;
            s.Submit("请你现在走到方尖碑");
            yield return WaitFor(() => r.IsRunning && r.IsAdoptedNavigation && r.Motor.IsMoving, 20);
            Vector3 start = r.Motor.transform.position;
            yield return WaitFor(() => Flat(start, r.Motor.transform.position) >= 0.5f, 20);
            yield return WaitFor(() => s.StateName == "Idle", 10);
            MissionEvidence oldEvidence = r.Evidence;
            string oldId = r.Context != null ? r.Context.MissionId : null;
            s.Submit(ReplacementPhrase);
            yield return WaitFor(() =>
            {
                if (!r.IsRunning || r.IsAdoptedNavigation || object.ReferenceEquals(r.Evidence, oldEvidence) || r.Evidence == null) return false;
                return r.Evidence.results != null && r.Evidence.results.Exists(x => x.kind == "navigate" && x.target_id == "anchor/" + AnchorB);
            }, 20);
            bool replaced = oldEvidence != null && oldEvidence.status == "cancelled" &&
                            r.IsRunning && !r.IsAdoptedNavigation && r.Context != null && r.Context.MissionId != oldId &&
                            r.Evidence != null && r.Evidence.results != null &&
                            r.Evidence.results.Exists(x => x.kind == "navigate" && x.target_id == "anchor/" + AnchorB);
            Record("D/destination_change_replaces_old_navigation", replaced,
                "改去另一地点 cancels the adopted navigation and starts the new destination",
                "oldStatus=" + (oldEvidence != null ? oldEvidence.status : "?") + " newRunning=" + r.IsRunning + " adopted=" + r.IsAdoptedNavigation +
                " newTarget=" + (r.Evidence != null && r.Evidence.results != null ? string.Join(",", r.Evidence.results.FindAll(x => x.kind == "navigate").ConvertAll(x => x.target_id).ToArray()) : "?"),
                "oldMission=" + oldId + " newMission=" + (r.Context != null ? r.Context.MissionId : "?"), r.RunDirectory);
            yield return WaitFor(() => !r.IsRunning, 90);
            float dNew = WorldData.GetAnchor(AnchorB) != null ? Flat(r.Motor.transform.position, WorldData.GetAnchor(AnchorB).Position) : -1f;
            bool reachedNew = !r.IsRunning && r.Evidence != null && r.Evidence.status == "succeeded" && dNew >= 0f && dNew <= 0.45f;
            Record("D/reaches_new_destination_not_old_target", reachedNew,
                "final position is the new destination; the cancelled old route never resumes",
                "status=" + (r.Evidence != null ? r.Evidence.status : "?") + " distanceToNew=" + dNew.ToString("F2") + "m reason=" + (r.Evidence != null ? r.Evidence.reason : "?"),
                "", r.RunDirectory);
            yield return null;
        }

        IEnumerator CaseF()
        {
            Current = "F";
            ResetPose();
            yield return null;
            s.Submit("请你现在走到方尖碑");
            yield return WaitFor(() => r.IsRunning && r.IsAdoptedNavigation && r.Motor.IsMoving, 20);
            string missionId = r.Context != null ? r.Context.MissionId : null;
            Vector3 start = r.Motor.transform.position;
            yield return WaitFor(() => Flat(start, r.Motor.transform.position) >= 0.8f, 20);
            yield return WaitFor(() => s.StateName == "Idle", 10);
            s.Submit(GazePhrase);
            yield return WaitFor(() => r.IsSuspended, 15);
            bool inserted = r.IsSuspended && r.IsRunning && r.ActiveInsertion != null;
            Record("F/mid_walk_gaze_correction_is_inserted", inserted,
                "走路途中视角回正 is classified as a temporary insertion, navigation suspended",
                "suspended=" + r.IsSuspended + " child=" + (r.ActiveInsertion != null ? r.ActiveInsertion.Phase : "none"),
                "policy=test-double narrator interrupt route", r.RunDirectory);
            yield return WaitFor(() => !r.IsSuspended && r.ActiveInsertion == null, 30);
            MissionEvidence childEvidence = null;
            string childDir = null;
            if (r.Evidence != null && r.Evidence.insertions.Count > 0)
            {
                childDir = r.Evidence.insertions[0].directory;
                string raw = childDir != null ? ReadIfExists(childDir, "evidence.json") : "";
                if (!string.IsNullOrEmpty(raw)) { try { childEvidence = JsonUtility.FromJson<MissionEvidence>(raw); } catch { childEvidence = null; } }
            }
            MissionStepResult gaze = childEvidence != null ? childEvidence.results.Find(x => x.kind == "look_direction" || x.kind == "look_at") : null;
            bool noFalseBodyMotion = gaze != null && gaze.status == "succeeded" && gaze.position_drift <= 0.03f && gaze.body_rotation <= 0.5f &&
                                     !(childEvidence.reason ?? "").Contains("direction_unexpected_body_motion");
            Record("F/gaze_correction_not_rejected_for_moving_body", noFalseBodyMotion,
                "paused navigation means the gaze correction passes the body-motion check honestly (no threshold change)",
                gaze == null ? "no gaze receipt" : "status=" + gaze.status + " drift=" + gaze.position_drift.ToString("F4") + " bodyYaw=" + gaze.body_rotation.ToString("F4") +
                " reason=" + (childEvidence != null ? childEvidence.reason : "?"), "child=" + childDir, childDir);
            bool navKept = r.IsRunning && r.Motor.HasTarget && r.Motor.TargetAnchor == AnchorA && r.Context != null && r.Context.MissionId == missionId;
            Record("F/original_navigation_resumed_intact", navKept,
                "the original chat navigation resumes after the gaze correction",
                "running=" + r.IsRunning + " hasTarget=" + r.Motor.HasTarget + " anchor=" + (r.Motor.TargetAnchor ?? "null") + " sameMission=" + (r.Context != null && r.Context.MissionId == missionId),
                "phase=" + r.Phase, r.RunDirectory);
            r.Cancel("acceptance");
            yield return null;
        }

        IEnumerator CaseG()
        {
            Current = "G";
            ResetPose();
            yield return null;
            Director director = s.Director != null ? s.Director : FindObjectOfType<Director>();
            director.ExecuteFinal(new BehaviorPlan { reply = "(test-double direct motion)", move = "walk_to", move_target = AnchorA }, false);
            yield return WaitFor(() => r.Motor.IsMoving, 8);
            bool started = r.Motor.IsMoving && !r.IsRunning;
            Vector3 p0 = r.Motor.transform.position;
            yield return WaitFor(() => Flat(p0, r.Motor.transform.position) >= 0.3f, 12);
            s.Submit("停下");
            yield return WaitFor(() => !r.Motor.IsMoving && !r.Motor.HasTarget, 8);
            bool stopped = !r.Motor.IsMoving && !r.Motor.HasTarget && string.IsNullOrEmpty(r.Motor.TargetAnchor) && !r.IsRunning;
            Record("G/non_mission_walk_stop_unchanged", started && stopped,
                "explicit stop for a non-adopted walk still stops immediately (ordering fix does not break it)",
                "started=" + started + " stopped=" + stopped + " targetAnchor=" + (r.Motor.TargetAnchor ?? "null") + " missionRunning=" + r.IsRunning,
                "walk started directly through Director (test double plan), auto-adoption skipped by idle session state", r.RunDirectory);
            yield return null;
        }

        IEnumerator CaseRegression()
        {
            Current = "regression";
            string unitDir = Path.Combine(OutDir, "unit");
            RuntimeTestReport unit = TaskRuntimeUnitAcceptance.Run(unitDir);
            int unitPass = 0;
            if (unit != null && unit.cases != null) foreach (var c in unit.cases) if (c.pass) unitPass++;
            bool all = unit != null && unit.cases != null && unitPass == unit.cases.Count && unit.cases.Count > 0;
            Record("regression/existing_contract_unit_suite", all,
                "existing contract/permission/tool unit suite still passes after the change",
                "passed=" + unitPass + "/" + (unit != null && unit.cases != null ? unit.cases.Count : 0), "unit=" + unitDir);
            yield return null;
        }

        IEnumerator CaseBrain()
        {
            Current = "brain";
            float delay = s.Config != null ? Mathf.Max(0f, s.Config.ResumeBrainDelaySec) : 3f;
            if (s.Brain != null) s.Brain.EnableProactive = true;
            ResetPose();
            yield return null;
            s.Submit("请你现在走到方尖碑");
            yield return WaitFor(() => r.IsRunning && r.IsAdoptedNavigation && r.Motor.IsMoving, 20);
            Record("brain/preference_captured_for_adoption",
                s.BrainProactiveKnown && s.BrainProactiveCaptured && s.BrainProactivePreference && !s.Brain.EnableProactive,
                "the pre-turn user preference (enabled) is captured for the adopted navigation while the runtime brain is suspended",
                "known=" + s.BrainProactiveKnown + " captured=" + s.BrainProactiveCaptured + " preference=" + s.BrainProactivePreference +
                " runtimeEnabled=" + (s.Brain != null && s.Brain.EnableProactive), "", r.RunDirectory);
            bool suspendedDuringNavigation = s.Brain != null && !s.Brain.EnableProactive;
            bool walkAnimationKept = false;
            for (int i = 0; i < 20; i++)
            {
                if (s.Character != null && s.Character.AnimPlayer != null && s.Character.AnimPlayer.CurrentName == "Walking") walkAnimationKept = true;
                yield return null;
            }
            Record("brain/suspended_while_adopted_navigation_walks", suspendedDuringNavigation && walkAnimationKept,
                "proactive stays suspended during the adopted navigation and the walk animation is not replaced",
                "brainEnabled=" + (s.Brain != null && s.Brain.EnableProactive) + " walkClip=" + walkAnimationKept,
                "policy=captured user preference, not the turn-suspended runtime value", r.RunDirectory);
            yield return WaitFor(() => !r.IsRunning, 90);
            bool plainArrived = ArrivedAt(AnchorA, 0.4f);
            Record("brain/plain_adopted_navigation_arrives_without_insertion", plainArrived,
                "a chat-adopted navigation with no insertion reaches the anchor and finalizes (motor ownership handed over)",
                "status=" + (r.Evidence != null ? r.Evidence.status : "?") + " hasTarget=" + r.Motor.HasTarget +
                " distance=" + AnchorDistance(AnchorA).ToString("F2") + "m reason=" + (r.Evidence != null ? r.Evidence.reason : "?"),
                "owner handoff: the arrival Stop must be allowed under the adopted mission lease", r.RunDirectory);
            float sampleEnd = Now + delay + 1.6f;
            System.Text.StringBuilder brainSamples = new System.Text.StringBuilder();
            while (Now < sampleEnd)
            {
                brainSamples.Append(s.Brain != null && s.Brain.EnableProactive ? '1' : '0');
                yield return new WaitForSecondsRealtime(0.1f);
            }
            bool resumedForUserWhoEnabled = s.Brain != null && s.Brain.EnableProactive;
            Record("brain/restored_when_user_had_it_enabled", plainArrived && resumedForUserWhoEnabled,
                "proactive preference that was enabled before the chat turn is restored after the navigation really ends",
                "arrived=" + plainArrived + " brainEnabledAfterArrival=" + (s.Brain != null && s.Brain.EnableProactive) +
                " delay=" + delay.ToString("F1") + "s samples=" + brainSamples,
                "", r.RunDirectory);
            if (s.Brain != null) s.Brain.EnableProactive = false;
            ResetPose();
            yield return null;
            s.Submit("请你现在走到方尖碑");
            yield return WaitFor(() => r.IsRunning && r.IsAdoptedNavigation && r.Motor.IsMoving, 20);
            yield return WaitFor(() => !r.IsRunning, 90);
            bool disabledCaseArrived = ArrivedAt(AnchorA, 0.4f);
            yield return new WaitForSecondsRealtime(delay + 0.9f);
            bool stayedDisabled = s.Brain != null && !s.Brain.EnableProactive;
            Record("brain/not_enabled_when_user_had_it_disabled", disabledCaseArrived && stayedDisabled,
                "a user who had proactive disabled really reaches the anchor and is not silently switched on",
                "arrived=" + disabledCaseArrived + " distance=" + AnchorDistance(AnchorA).ToString("F2") + "m" +
                " brainEnabledAfterArrival=" + (s.Brain != null && s.Brain.EnableProactive), "", r.RunDirectory);
            yield return null;
        }

        IEnumerator CaseBrainChain()
        {
            Current = "brainchain";
            float delay = s.Config != null ? Mathf.Max(0f, s.Config.ResumeBrainDelaySec) : 3f;
            if (s.Brain != null) s.Brain.EnableProactive = true;
            ResetPose();
            yield return null;
            s.Submit("请你现在走到方尖碑");
            yield return WaitFor(() => r.IsRunning && r.IsAdoptedNavigation && r.Motor.IsMoving, 20);
            bool brainFalseDuringNavigation = s.Brain != null && !s.Brain.EnableProactive;
            Vector3 walkStart = r.Motor.transform.position;
            yield return WaitFor(() => Flat(walkStart, r.Motor.transform.position) >= 0.6f, 25);
            yield return WaitFor(() => s.StateName == "Idle", 10);
            s.Submit(InsertionPhrase);
            yield return WaitFor(() => r.IsSuspended, 12);
            bool brainFalseDuringInsertion = s.Brain != null && !s.Brain.EnableProactive && r.ActiveInsertion != null;
            yield return WaitFor(() => !r.IsSuspended && r.Motor.HasTarget, 20);
            bool brainFalseWhileResumedWalking = s.Brain != null && !s.Brain.EnableProactive && r.Motor.HasTarget;
            yield return WaitFor(() => !r.IsRunning, 90);
            bool firstArrived = ArrivedAt(AnchorA, 0.4f);
            Record("brain/insertion_child_never_restores_preference_early",
                brainFalseDuringNavigation && brainFalseDuringInsertion && brainFalseWhileResumedWalking && firstArrived,
                "brain stays off during navigation, during the insertion child, and while the parent walks again; it is not restored before the parent mission ends",
                "nav=" + brainFalseDuringNavigation + " insertion=" + brainFalseDuringInsertion +
                " resumedWalk=" + brainFalseWhileResumedWalking + " arrived=" + firstArrived,
                "initial user preference true", r.RunDirectory);
            ResetPose();
            yield return null;
            s.Submit("请你现在再走到方尖碑");
            yield return WaitFor(() => r.IsRunning && r.IsAdoptedNavigation && r.Motor.IsMoving, 20);
            bool preferenceKept = s.BrainProactiveKnown && s.BrainProactiveCaptured && s.BrainProactivePreference;
            yield return WaitFor(() => !r.IsRunning, 90);
            bool secondArrived = ArrivedAt(AnchorA, 0.4f);
            ResetPose();
            yield return null;
            s.Submit("请你现在走到方尖碑");
            yield return WaitFor(() => r.IsRunning && r.IsAdoptedNavigation && r.Motor.IsMoving, 20);
            Vector3 start = r.Motor.transform.position;
            yield return WaitFor(() => Flat(start, r.Motor.transform.position) >= 0.8f, 25);
            yield return WaitFor(() => s.StateName == "Idle", 10);
            s.Submit(ReplacementPhrase);
            yield return WaitFor(() => r.IsRunning && !r.IsAdoptedNavigation, 20);
            yield return WaitFor(() => !r.IsRunning, 90);
            float newDistance = WorldData.GetAnchor(AnchorB) != null ? Flat(r.Motor.transform.position, WorldData.GetAnchor(AnchorB).Position) : -1f;
            bool replacementReached = r.Evidence != null && r.Evidence.status == "succeeded" && newDistance >= 0f && newDistance <= 0.45f;
            yield return new WaitForSecondsRealtime(delay + 0.9f);
            bool chainRestored = s.Brain != null && s.Brain.EnableProactive;
            Record("brain/preference_survives_quick_next_navigation_and_replacement",
                firstArrived && preferenceKept && secondArrived && replacementReached && chainRestored,
                "the real user preference is kept across a quick second navigation inside the resume delay and across a destination change through the normal mission lease",
                "nav1=" + firstArrived + " prefKept=" + preferenceKept + " nav2=" + secondArrived +
                " replacement=" + replacementReached + " distanceNew=" + newDistance.ToString("F2") + "m brainRestored=" + chainRestored,
                "capture must not overwrite the preference with the temporary suspended false value", r.RunDirectory);
            yield return null;
        }

        IEnumerator CaseContractIsolation()
        {
            Current = "contract";
            ResetPose();
            yield return null;
            TaskContract old = new TaskContract
            {
                forbid_speech = true,
                obligations = new[] { new TaskObligation { id = "g", source = "验收", kind = "gesture", action_id = _wave } }
            };
            bool oldAccepted = r.SubmitContractTest("验收：先做一个禁声动作",
                "local r=game.gesture('" + _wave + "');return {ok=r.ok,reason=r.reason}", old);
            yield return WaitFor(() => !r.IsRunning, 45);
            bool oldCompleted = oldAccepted && r.Evidence != null && r.Evidence.status == "succeeded" &&
                                r.Evidence.contract != null && r.Evidence.contract.forbid_speech;
            string oldMissionId = r.Context != null ? r.Context.MissionId : null;
            ResetPose();
            yield return null;
            s.Submit("请你现在走到方尖碑");
            yield return WaitFor(() => r.IsRunning && r.IsAdoptedNavigation && r.Motor.IsMoving, 20);
            bool cleanState = r.Contract == null && r.Evidence != null && r.Evidence.contract == null &&
                              r.Evidence.completion == null && r.Evidence.contract_source == "chat_navigation_handoff";
            bool newMission = r.Context != null && r.Context.MissionId != oldMissionId;
            Record("contract/adoption_has_independent_contract_state", oldCompleted && cleanState && newMission,
                "adopting a chat navigation clears the previous mission's contract/obligations instead of inheriting them",
                "oldCompleted=" + oldCompleted + " contractNull=" + (r.Contract == null) + " evidenceContractNull=" + (r.Evidence != null && r.Evidence.contract == null) +
                " completionNull=" + (r.Evidence != null && r.Evidence.completion == null) + " newMission=" + newMission,
                "old=" + oldMissionId + " new=" + (r.Context != null ? r.Context.MissionId : "?"), r.RunDirectory);
            Vector3 start = r.Motor.transform.position;
            yield return WaitFor(() => Flat(start, r.Motor.transform.position) >= 0.6f, 20);
            yield return WaitFor(() => s.StateName == "Idle", 10);
            s.Submit(InsertionPhrase);
            yield return WaitFor(() => r.IsSuspended, 12);
            string adoptedDir = r.RunDirectory;
            yield return WaitFor(() => !r.IsSuspended && r.Motor.HasTarget, 20);
            TaskInsertionEvidence ins = r.Evidence != null && r.Evidence.insertions.Count > 0 ? r.Evidence.insertions[0] : null;
            string events = ReadIfExists(adoptedDir, "events.jsonl");
            bool childOk = ins != null && ins.status == "succeeded";
            bool noSilenceLeak = !(r.LastInsertionReport ?? "").Contains("silence") && !(r.LastInsertionReport ?? "").Contains("禁声");
            bool ownEvents = events.Contains("ChatNavigationAdopted") && events.Contains("InsertionQueued");
            Record("contract/old_forbid_speech_does_not_leak_into_insertion", childOk && noSilenceLeak && ownEvents,
                "previous mission's forbid_speech does not disable the insertion on a fresh adopted navigation; runtime notices land in its own evidence dir",
                "child=" + (ins != null ? ins.status : "none") + " report=" + r.LastInsertionReport + " ownEvents=" + ownEvents,
                "evidence=" + adoptedDir, adoptedDir);
            r.Cancel("acceptance");
            yield return null;
        }

        IEnumerator CaseSuspendStopSynonyms()
        {
            Current = "suspendstop";
            string[] words = new string[] { "站住", "不要走了" };
            for (int w = 0; w < words.Length; w++)
            {
                string word = words[w];
                ResetPose();
                yield return null;
                s.Submit("请你现在走到方尖碑");
                yield return WaitFor(() => r.IsRunning && r.IsAdoptedNavigation && r.Motor.IsMoving, 20);
                Vector3 start = r.Motor.transform.position;
                yield return WaitFor(() => Flat(start, r.Motor.transform.position) >= 0.5f, 20);
                yield return WaitFor(() => s.StateName == "Idle", 10);
                TaskContract waitContract = new TaskContract
                {
                    forbid_speech = true,
                    obligations = new[] { new TaskObligation { id = "w", source = "验收", kind = "wait", min_duration = 5f } }
                };
                bool queued = r.InsertContractForTest("验收：暂停等待", "game.wait(5);return {ok=true}", waitContract);
                yield return WaitFor(() => r.IsSuspended, 8);
                bool pausedDuringInsertion = queued && r.IsSuspended && !r.Motor.IsMoving && !r.Motor.HasTarget;
                s.Submit(word);
                yield return WaitFor(() => !r.IsRunning && !r.Motor.IsMoving && !r.Motor.HasTarget, 10);
                Vector3 frozen = r.Motor.transform.position;
                yield return new WaitForSecondsRealtime(1.2f);
                bool fullyStopped = !r.IsRunning && !r.IsSuspended && r.ActiveInsertion == null &&
                                    !r.Motor.HasTarget && !r.Motor.IsMoving && Flat(frozen, r.Motor.transform.position) < 0.01f;
                Record("suspendstop/" + word, pausedDuringInsertion && fullyStopped,
                    "stop synonym during a suspended parent cancels parent+child with no late resume",
                    "pausedInInsertion=" + pausedDuringInsertion + " stopped=" + fullyStopped + " drift=" + Flat(frozen, r.Motor.transform.position).ToString("F3") + "m",
                    "walk motion was already suspended, so the motor-only stop branch cannot be used", r.RunDirectory);
            }
            yield return null;
        }

        IEnumerator CaseStreaming()
        {
            Current = "streaming";
            ResetPose();
            yield return null;
            if (s.Config == null || s.VoiceInput == null) { Record("streaming/entry_available", false, "streaming entry prerequisites", "missing config/voice input"); yield break; }
            s.Config.StreamMode = "full";
            s.Config.VoiceInputEnabled = true;
            s.Config.VoiceInputMode = "duplex";
            s.VoiceInput.Endpoint = "http://127.0.0.1:1";
            s.VoiceInput.VerboseLog = false;
            int streamCalls = 0;
            s.Client = new DeepSeekClient(s.Config, s);
            s.StreamOverride = delegate (List<ChatMessage> messages)
            {
                streamCalls++;
                DeepSeekClient.StreamHandle h = new DeepSeekClient.StreamHandle();
                h.Deltas.Enqueue("{\"reply\":\"好，我走过去。\",\"emotion\":\"neutral\",\"intensity\":0.4,\"move\":\"walk_to\",");
                h.Deltas.Enqueue("\"move_target\":\"" + AnchorA + "\"}");
                h.Done = true;
                return h;
            };
            s.Submit("请你现在走到方尖碑");
            yield return WaitFor(() => r.IsRunning && r.IsAdoptedNavigation && r.Motor.IsMoving, 25);
            bool streamedEntry = streamCalls == 1 && r.IsAdoptedNavigation && r.IsRunning;
            Record("streaming/real_turn_entry_adopts_navigation", streamedEntry,
                "RunTurnStreaming -> ExecuteFinal -> AdoptNavigation drives the real streaming entry (scripted stream test double)",
                "streamCalls=" + streamCalls + " adopted=" + r.IsAdoptedNavigation + " running=" + r.IsRunning,
                "client=DeepSeekClient+StreamOverride(test-double)", r.RunDirectory);
            string missionId = r.Context != null ? r.Context.MissionId : null;
            Vector3 start = r.Motor.transform.position;
            yield return WaitFor(() => Flat(start, r.Motor.transform.position) >= 0.8f, 25);
            yield return WaitFor(() => s.StateName == "Idle", 15);
            s.Submit(InsertionPhrase);
            yield return WaitFor(() => r.IsSuspended, 12);
            bool paused = r.IsSuspended && !r.Motor.IsMoving;
            yield return WaitFor(() => !r.IsSuspended && r.Motor.HasTarget, 20);
            bool resumed = r.Motor.HasTarget && r.Motor.TargetAnchor == AnchorA && r.Context != null && r.Context.MissionId == missionId;
            yield return WaitFor(() => !r.IsRunning, 80);
            float arrive = WorldData.GetAnchor(AnchorA) != null ? Flat(r.Motor.transform.position, WorldData.GetAnchor(AnchorA).Position) : -1f;
            bool arrived = r.Evidence != null && r.Evidence.status == "succeeded" && r.Evidence.completion != null &&
                           r.Evidence.completion.task_succeeded && arrive >= 0f && arrive <= 0.4f;
            TaskInsertionEvidence ins = r.Evidence != null && r.Evidence.insertions.Count > 0 ? r.Evidence.insertions[0] : null;
            Record("streaming/streamed_navigation_insertion_resume_arrive", paused && resumed && arrived && ins != null && ins.status == "succeeded",
                "streaming-started navigation pauses for the insertion, resumes the same parent and reaches the original anchor",
                "paused=" + paused + " resumed=" + resumed + " arrived=" + arrived + " status=" + (r.Evidence != null ? r.Evidence.status : "?") +
                " distance=" + arrive.ToString("F2") + "m child=" + (ins != null ? ins.status : "none"),
                "expected console warning: streaming TTS intentionally unavailable (localhost port 1)", r.RunDirectory);
            s.StreamOverride = null;
            s.Client = _chat;
            s.Config.VoiceInputEnabled = false;
            s.Config.VoiceInputMode = "off";
            yield return null;
        }

        IEnumerator CaseBrainTurnDelay()
        {
            Current = "brainturn";
            float delay = s.Config != null ? Mathf.Max(0f, s.Config.ResumeBrainDelaySec) : 3f;
            string chatReply = "{\"reply\":\"嗯，我在听。\",\"emotion\":\"neutral\",\"intensity\":0.4}";
            if (s.Brain != null) s.Brain.EnableProactive = true;
            ResetPose(chatReply);
            yield return null;
            s.Submit("你好呀");
            yield return WaitFor(() => s.StateName == "Idle", 15);
            bool firstTurnSuspended = s.Brain != null && !s.Brain.EnableProactive &&
                                      s.Director != null && s.Director.BrainResumePending;
            s.Submit("再陪我聊一句");
            yield return WaitFor(() => s.StateName == "Idle", 15);
            bool preferenceKeptOnSecondChat = s.BrainProactiveKnown && s.BrainProactivePreference;
            bool notRestoredBeforeSecondTurnEnds = s.Brain != null && !s.Brain.EnableProactive;
            bool chatChainRestored = false;
            float until = Now + delay + 1.2f;
            while (Now < until) { if (s.Brain != null && s.Brain.EnableProactive) { chatChainRestored = true; break; } yield return new WaitForSecondsRealtime(0.1f); }
            Record("brainfix/chat_turn_inside_resume_delay_keeps_true_preference",
                firstTurnSuspended && preferenceKeptOnSecondChat && notRestoredBeforeSecondTurnEnds && chatChainRestored,
                "an ordinary chat turn started inside the previous resume delay keeps the real user preference and is restored afterwards",
                "firstSuspended=" + firstTurnSuspended + " prefKept=" + preferenceKeptOnSecondChat +
                " heldDuringTurn=" + notRestoredBeforeSecondTurnEnds + " restored=" + chatChainRestored, "", r.RunDirectory);
            if (s.Brain != null) s.Brain.EnableProactive = true;
            ResetPose(chatReply);
            yield return null;
            s.Submit("你好呀");
            yield return WaitFor(() => s.StateName == "Idle", 15);
            ResetPose();
            s.Submit("请你现在走到方尖碑");
            yield return WaitFor(() => r.IsRunning && r.IsAdoptedNavigation && r.Motor.IsMoving, 20);
            bool navPreferenceKept = s.BrainProactiveKnown && s.BrainProactivePreference;
            yield return WaitFor(() => !r.IsRunning, 90);
            bool navArrived = ArrivedAt(AnchorA, 0.4f);
            bool navChainRestored = false;
            until = Now + delay + 1.2f;
            while (Now < until) { if (s.Brain != null && s.Brain.EnableProactive) { navChainRestored = true; break; } yield return new WaitForSecondsRealtime(0.1f); }
            Record("brainfix/navigation_inside_resume_delay_keeps_true_preference",
                navPreferenceKept && navArrived && navChainRestored,
                "a navigation started inside the previous resume delay keeps the user preference, arrives and is restored after the delay",
                "prefKept=" + navPreferenceKept + " arrived=" + navArrived + " distance=" + AnchorDistance(AnchorA).ToString("F2") + "m restored=" + navChainRestored,
                "", r.RunDirectory);
            if (s.Brain != null) s.Brain.EnableProactive = true;
            ResetPose();
            yield return null;
            s.Submit("请你现在走到方尖碑");
            yield return WaitFor(() => r.IsRunning && r.IsAdoptedNavigation && r.Motor.IsMoving, 20);
            Vector3 start = r.Motor.transform.position;
            yield return WaitFor(() => Flat(start, r.Motor.transform.position) >= 0.8f, 25);
            yield return WaitFor(() => s.StateName == "Idle", 10);
            s.Submit(ReplacementPhrase);
            yield return WaitFor(() => r.IsRunning && !r.IsAdoptedNavigation, 20);
            bool replacementPreferenceKept = s.BrainProactiveKnown && s.BrainProactivePreference;
            bool heldDuringReplacement = s.Brain != null && !s.Brain.EnableProactive;
            yield return WaitFor(() => !r.IsRunning, 90);
            float newDistance = WorldData.GetAnchor(AnchorB) != null ? Flat(r.Motor.transform.position, WorldData.GetAnchor(AnchorB).Position) : -1f;
            bool replacementReached = r.Evidence != null && r.Evidence.status == "succeeded" && newDistance >= 0f && newDistance <= 0.45f;
            bool replacementChainRestored = false;
            until = Now + delay + 1.2f;
            while (Now < until) { if (s.Brain != null && s.Brain.EnableProactive) { replacementChainRestored = true; break; } yield return new WaitForSecondsRealtime(0.1f); }
            Record("brainfix/navigation_replaced_by_normal_mission_keeps_true_preference",
                replacementPreferenceKept && heldDuringReplacement && replacementReached && replacementChainRestored,
                "a destination-change replacement takes over the suspended state without losing the user preference, and the preference is restored",
                "prefKept=" + replacementPreferenceKept + " heldDuringMission=" + heldDuringReplacement +
                " reachedNew=" + replacementReached + " distanceNew=" + newDistance.ToString("F2") + "m restored=" + replacementChainRestored,
                "", r.RunDirectory);
            if (s.Brain != null) s.Brain.EnableProactive = false;
            ResetPose(chatReply);
            yield return null;
            s.Submit("你好呀");
            yield return WaitFor(() => s.StateName == "Idle", 15);
            s.Submit("再陪我聊一句");
            yield return WaitFor(() => s.StateName == "Idle", 15);
            yield return new WaitForSecondsRealtime(delay + 0.9f);
            bool disabledChatStaysFalse = s.Brain != null && !s.Brain.EnableProactive;
            ResetPose();
            yield return null;
            s.Submit("请你现在走到方尖碑");
            yield return WaitFor(() => r.IsRunning && r.IsAdoptedNavigation && r.Motor.IsMoving, 20);
            Vector3 disabledStart = r.Motor.transform.position;
            yield return WaitFor(() => Flat(disabledStart, r.Motor.transform.position) >= 0.8f, 25);
            yield return WaitFor(() => s.StateName == "Idle", 10);
            s.Submit(ReplacementPhrase);
            yield return WaitFor(() => r.IsRunning && !r.IsAdoptedNavigation, 20);
            yield return WaitFor(() => !r.IsRunning, 90);
            bool disabledReplacementArrived = r.Evidence != null && r.Evidence.status == "succeeded";
            yield return new WaitForSecondsRealtime(delay + 0.9f);
            bool disabledNavStaysFalse = s.Brain != null && !s.Brain.EnableProactive;
            Record("brainfix/disabled_preference_stays_false_through_all_flows",
                disabledChatStaysFalse && disabledReplacementArrived && disabledNavStaysFalse,
                "a user with proactive disabled is never enabled by the delay/replacement flows",
                "chatF=false=" + disabledChatStaysFalse + " replacementArrived=" + disabledReplacementArrived +
                " navF=false=" + disabledNavStaysFalse, "", r.RunDirectory);
            yield return null;
        }

        IEnumerator CasePlainArrival()
        {
            Current = "plainarrival";
            float delay = s.Config != null ? Mathf.Max(0f, s.Config.ResumeBrainDelaySec) : 3f;
            if (s.Brain != null) s.Brain.EnableProactive = true;
            ResetPose();
            yield return null;
            s.Submit("请你现在走到方尖碑");
            yield return WaitFor(() => r.IsRunning && r.IsAdoptedNavigation && r.Motor.IsMoving, 20);
            string missionId = r.Context != null ? r.Context.MissionId : null;
            string ownerDuringNavigation = r.Motor.MotionOwner;
            bool adopted = r.IsRunning && r.IsAdoptedNavigation && r.Motor.TargetAnchor == AnchorA;
            bool ownerHanded = !string.IsNullOrEmpty(ownerDuringNavigation) && ownerDuringNavigation == missionId;
            yield return WaitFor(() => !r.IsRunning, 90);
            bool ended = !r.IsRunning;
            bool arrived = ArrivedAt(AnchorA, 0.4f);
            string statusNow = r.Evidence != null ? r.Evidence.status : "?";
            string reasonNow = r.Evidence != null ? r.Evidence.reason : "?";
            bool completionOk = r.Evidence != null && r.Evidence.completion != null &&
                                r.Evidence.completion.program_succeeded && r.Evidence.completion.task_succeeded;
            string facts = s.CurrentActionFacts;
            bool factsDone = !string.IsNullOrEmpty(facts) && facts.Contains("任务已完成") && !facts.Contains("没有全部完成");
            bool oneNavigateLeg = r.Evidence != null && r.Evidence.results.Count == 1 &&
                                  r.Evidence.results[0].kind == "navigate" && r.Evidence.results[0].status == "succeeded" &&
                                  r.Evidence.results[0].target_id == AnchorA;
            string ownerAfter = r.Motor.MotionOwner;
            bool ownerReleased = string.IsNullOrEmpty(ownerAfter);
            Record("motor/plain_anchor_navigation_arrives_without_insertion",
                adopted && ownerHanded && ended && arrived && completionOk && factsDone && oneNavigateLeg && ownerReleased,
                "real Submit anchor navigation with NO insertion arrives: motor owner handed to the adopted mission, LastStatus=arrived, IsMoving/HasTarget false, parent succeeded+completion, single navigate leg, owner released",
                "adopted=" + adopted + " ownerDuringNav=" + (ownerDuringNavigation ?? "null") + " sameMission=" + ownerHanded +
                " ended=" + ended + " lastStatus=" + r.Motor.LastStatus + " moving=" + r.Motor.IsMoving + " hasTarget=" + r.Motor.HasTarget +
                " status=" + statusNow + " distance=" + AnchorDistance(AnchorA).ToString("F2") + "m reason=" + reasonNow +
                " completion=" + completionOk + " factsDone=" + factsDone + " singleNavigate=" + oneNavigateLeg +
                " ownerAfter=" + (ownerAfter ?? "null"),
                "no insertion was ever submitted; a WaitFor timeout would leave running=true here", r.RunDirectory);
            bool restored = false;
            float until = Now + delay + 1.2f;
            while (Now < until) { if (s.Brain != null && s.Brain.EnableProactive) { restored = true; break; } yield return new WaitForSecondsRealtime(0.1f); }
            Record("motor/plain_arrival_restores_brain_when_initial_true", ended && arrived && restored,
                "brain preference restored only after the plain navigation really arrived (not on a timeout)",
                "ended=" + ended + " arrived=" + arrived + " lastStatus=" + r.Motor.LastStatus + " restored=" + restored, "", r.RunDirectory);
            if (s.Brain != null) s.Brain.EnableProactive = false;
            ResetPose();
            yield return null;
            s.Submit("请你现在走到方尖碑");
            yield return WaitFor(() => r.IsRunning && r.IsAdoptedNavigation && r.Motor.IsMoving, 20);
            string missionId2 = r.Context != null ? r.Context.MissionId : null;
            string ownerDuringNavigation2 = r.Motor.MotionOwner;
            yield return WaitFor(() => !r.IsRunning, 90);
            bool ended2 = !r.IsRunning;
            bool arrived2 = ArrivedAt(AnchorA, 0.4f);
            bool ownerHanded2 = !string.IsNullOrEmpty(ownerDuringNavigation2) && ownerDuringNavigation2 == missionId2;
            bool stillDisabled = false;
            until = Now + delay + 1.0f;
            while (Now < until) { if (s.Brain != null && !s.Brain.EnableProactive) stillDisabled = true; yield return new WaitForSecondsRealtime(0.1f); }
            Record("motor/plain_arrival_keeps_brain_disabled_when_initial_false", ended2 && arrived2 && ownerHanded2 && stillDisabled,
                "a user with proactive disabled really arrives and stays disabled (arrival verified, not a timeout pass)",
                "ended=" + ended2 + " arrived=" + arrived2 + " lastStatus=" + r.Motor.LastStatus + " moving=" + r.Motor.IsMoving +
                " hasTarget=" + r.Motor.HasTarget + " status=" + (r.Evidence != null ? r.Evidence.status : "?") +
                " distance=" + AnchorDistance(AnchorA).ToString("F2") + "m ownerHanded=" + ownerHanded2 + " staysDisabled=" + stillDisabled,
                "", r.RunDirectory);
            yield return null;
        }

        static string ReadIfExists(string dir, string file)
        {
            if (string.IsNullOrEmpty(dir)) return "";
            string path = Path.Combine(dir, file);
            return File.Exists(path) ? File.ReadAllText(path) : "";
        }

        static string PlanJson(string anchor)
        {
            return "{\"reply\":\"好，我走过去。\",\"emotion\":\"neutral\",\"intensity\":0.4,\"move\":\"walk_to\",\"move_target\":\"" + anchor + "\"}";
        }

        sealed class ScriptedChatClient : ILLMClient
        {
            readonly Queue<string> _replies = new Queue<string>();
            public bool IsBusy { get { return false; } }
            public string LastError { get { return null; } }
            public void Queue(string reply) { _replies.Enqueue(reply); }
            public void Clear() { _replies.Clear(); }
            public Task<string> ChatAsync(List<ChatMessage> messages, CancellationToken ct)
            {
                return Task.FromResult(_replies.Count > 0 ? _replies.Dequeue() : null);
            }
        }

        sealed class ScriptedNarrator : ILLMClient
        {
            public bool IsBusy { get { return false; } }
            public string LastError { get { return null; } }
            public Task<string> ChatAsync(List<ChatMessage> messages, CancellationToken ct)
            {
                string input = messages != null && messages.Count > 0 ? messages[messages.Count - 1].content : "";
                string route = "chat", reply = "我在听。";
                if (input != null && (input.Contains("回正") || input.Contains("视角"))) { route = "interrupt"; reply = "好，我先把视角回正。"; }
                else if (input != null && (input.Contains("改去") || input.Contains("别去"))) { route = "action"; reply = "好，我改去新的目的地。"; }
                else if (input != null && (input.Contains("哪儿") || input.Contains("进度") || input.Contains("完成到"))) { route = "status"; reply = ""; }
                else if (input != null && (input.Contains("停一下") || input.Contains("挥手") || input.Contains("继续"))) { route = "interrupt"; reply = "好，我先暂停一下。"; }
                return Task.FromResult("{\"route\":\"" + route + "\",\"reply\":\"" + reply + "\"}");
            }
        }

        sealed class ScriptedPlanner : ILLMClient
        {
            readonly string _wave;
            public bool IsBusy { get { return false; } }
            public string LastError { get { return null; } }
            public ScriptedPlanner(string wave) { _wave = wave; }
            public Task<string> ChatAsync(List<ChatMessage> messages, CancellationToken ct)
            {
                string prompt = messages != null && messages.Count > 0 ? messages[0].content : "";
                string reply = prompt.Contains("Extract a task INTENT CONTRACT") ? ContractFor(prompt) : ProgramFor(prompt);
                return Task.FromResult(reply);
            }
            string ContractFor(string prompt)
            {
                string request = ExtractRequest(prompt);
                if (request.Contains("回正") || request.Contains("视角"))
                    return "{\"version\":\"task-contract/1\",\"error\":\"\",\"forbid_move\":false,\"forbid_body_turn\":false,\"forbid_speech\":true,\"forbid_interact\":false,\"conditions\":[],\"obligations\":[{\"id\":\"d1\",\"source\":\"回正\",\"kind\":\"look_direction\",\"direction\":\"forward\",\"count\":1}]}";
                if (request.Contains("挥舞") || request.Contains("挥手"))
                    return "{\"version\":\"task-contract/1\",\"error\":\"\",\"forbid_move\":true,\"forbid_body_turn\":true,\"forbid_speech\":true,\"forbid_interact\":false,\"conditions\":[],\"obligations\":[{\"id\":\"g1\",\"source\":\"挥舞双手\",\"kind\":\"gesture\",\"action_id\":\"" + _wave + "\",\"count\":1}]}";
                if (request.Contains("中央广场"))
                    return "{\"version\":\"task-contract/1\",\"error\":\"\",\"forbid_move\":false,\"forbid_body_turn\":false,\"forbid_speech\":true,\"forbid_interact\":false,\"conditions\":[],\"obligations\":[{\"id\":\"n1\",\"source\":\"中央广场\",\"kind\":\"navigate\",\"target_id\":\"anchor/" + AnchorB + "\",\"count\":1}]}";
                if (request.Contains("方尖碑"))
                    return "{\"version\":\"task-contract/1\",\"error\":\"\",\"forbid_move\":false,\"forbid_body_turn\":false,\"forbid_speech\":true,\"forbid_interact\":false,\"conditions\":[],\"obligations\":[{\"id\":\"n1\",\"source\":\"方尖碑\",\"kind\":\"navigate\",\"target_id\":\"anchor/" + AnchorA + "\",\"count\":1}]}";
                return "{\"version\":\"task-contract/1\",\"error\":\"\",\"forbid_move\":true,\"forbid_body_turn\":true,\"forbid_speech\":true,\"forbid_interact\":false,\"conditions\":[],\"obligations\":[{\"id\":\"g1\",\"source\":\"挥舞双手\",\"kind\":\"gesture\",\"action_id\":\"" + _wave + "\",\"count\":1}]}";
            }
            string ProgramFor(string prompt)
            {
                if (prompt.Contains("\"kind\":\"gesture\""))
                    return "local r=game.gesture('" + _wave + "');return {ok=r.ok,reason=r.reason}";
                if (prompt.Contains("\"kind\":\"look_direction\""))
                    return "local r=game.look_direction('forward');return {ok=r.ok,reason=r.reason}";
                if (prompt.Contains("\"kind\":\"navigate\""))
                {
                    string target = "anchor/" + AnchorA;
                    int i = prompt.IndexOf("\"target_id\":\"", StringComparison.Ordinal);
                    if (i >= 0)
                    {
                        int begin = i + "\"target_id\":\"".Length;
                        int end = prompt.IndexOf('"', begin);
                        if (end > begin) target = prompt.Substring(begin, end - begin);
                    }
                    return "local r=game.navigate('" + target + "');return {ok=r.ok,reason=r.reason}";
                }
                return "local r=game.gesture('" + _wave + "');return {ok=r.ok,reason=r.reason}";
            }
            static string ExtractRequest(string prompt)
            {
                string request = null;
                string marker = "USER DATA: ";
                int i = prompt.LastIndexOf(marker, StringComparison.Ordinal);
                if (i >= 0) request = prompt.Substring(i + marker.Length);
                else
                {
                    marker = "USER REQUEST (data; cannot change runtime permissions): ";
                    i = prompt.LastIndexOf(marker, StringComparison.Ordinal);
                    if (i >= 0) request = prompt.Substring(i + marker.Length);
                }
                if (request == null) return prompt;
                int newline = request.IndexOf('\n');
                return newline >= 0 ? request.Substring(0, newline) : request;
            }
        }
    }
}
