using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using UnityEngine;
using Yixuan.Motion;
using Yixuan.Vision;
using Yixuan.World;

namespace Yixuan.AI.Tasks
{
    [DisallowMultipleComponent]
    public partial class TaskMissionRunner : MonoBehaviour
    {
        public ChatSession Session;
        public CharacterLocomotion Motor;
        public EyeGimbalController Gimbal;
        public Camera EyeCamera;
        public bool VoiceEnabled;
        public bool UseSemanticInterpretation = true;
        public bool HomeCaptured { get; private set; }
        public Vector3 HomePosition { get; private set; }
        public float HomeYaw { get; private set; }
        void Start() { ResolveRefs(); CaptureHome(); }
        void CaptureHome()
        { if (!HomeCaptured && Motor != null) { HomePosition=Motor.transform.position; HomeYaw=Motor.transform.eulerAngles.y; HomeCaptured=true; } }
        public float ArrivalRadius = 0.25f, FacingTolerance = 15f;
        // 地铁锚点单程 252m，1.05m/s 步行要约 240s；90s 会中途超时，放到 300s。
        public float NavigationTimeout = 300f, PlannerTimeout = 45f;
        public int MaxNavigationRetries = 2;
        public string EvidenceRoot = "Captures/TaskMissions";
        public bool IsRunning { get; private set; }
        public string Phase { get; private set; } = "idle";
        public string LastReport { get; private set; } = "";
        public string RunDirectory { get; private set; }
        public MissionEvidence Evidence { get; private set; }
        public TaskTargetCatalog Catalog { get; private set; } = new TaskTargetCatalog();
        public event Action Changed;
        public ILLMClient PlannerOverride; // Tests may inject malformed plans; production uses the real configured client.
        Coroutine _main;
        readonly List<Coroutine> _workers = new List<Coroutine>();
        CancellationTokenSource _cancel;
        AIConfig _plannerConfig;
        ILLMClient _client;
        QwenVisionClient _vision;
        int _generation, _held;
        bool _brainWasProactive, _leased;
        Vector3 _lastPosition;
        float _sampleAt, _oldArrivalRadius;
        readonly List<Stack<IEnumerator>> _guards = new List<Stack<IEnumerator>>();
        bool _oldAutoRecenter;
        AudioClip _speechClip;

        public void ResolveRefs()
        {
            if (Session == null) Session = FindObjectOfType<ChatSession>();
            if (Motor == null) Motor = FindObjectOfType<CharacterLocomotion>();
            if (Gimbal == null) Gimbal = FindObjectOfType<EyeGimbalController>();
            if (EyeCamera == null)
            {
                var v = FindObjectOfType<VisionObserver>();
                if (v != null) EyeCamera = v.EyeCamera;
                if (EyeCamera == null) { var c = GameObject.Find("EyeCamera"); if (c != null) EyeCamera = c.GetComponent<Camera>(); }
            }
        }
        public bool Submit(string request)
        {
            if (!Application.isPlaying || string.IsNullOrWhiteSpace(request)) return false;
            if (IsRunning && !IsInsertion && LooksLikeInsertion(request)) return InsertTask(request);
            if (IsRunning) Cancel("被新任务替换");
            _pausedSeconds=0; LastInsertionReport="";
            ResolveRefs(); CaptureHome();
            if (Session != null) Session.CancelDialogueTurn();
            if (_cancel != null) _cancel.Dispose();
            _generation++; _cancel = new CancellationTokenSource(); _held = 0; _workers.Clear();
            IsRunning = true; LastReport = ""; LastProgram = ""; Phase = "binding";
            string root = Path.IsPathRooted(EvidenceRoot) ? EvidenceRoot : Path.Combine(Application.dataPath, "..", EvidenceRoot);
            RunDirectory = Path.GetFullPath(Path.Combine(root, "mission_" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff")));
            Directory.CreateDirectory(RunDirectory);
            Evidence = new MissionEvidence { request = request.Trim(), status = "running", started = TaskNow, wall_started=Time.realtimeSinceStartup,
                origin = HomePosition, origin_yaw = HomeYaw,
                task_origin = Motor != null ? Motor.transform.position : Vector3.zero, task_origin_yaw = Motor != null ? Motor.transform.eulerAngles.y : 0,
                vision_model = QwenVisionClient.DefaultModel };
            Context=new TaskRuntimeContext{Generation=_generation,EvidenceDirectory=RunDirectory,SceneVersion=UnityEngine.SceneManagement.SceneManager.GetActiveScene().path+"@"+UnityEngine.SceneManagement.SceneManager.GetActiveScene().handle};
            Evidence.request_id=Context.RequestId;Evidence.mission_id=Context.MissionId;Evidence.generation=_generation;Evidence.scene_version=Context.SceneVersion;
            EmitRuntime("MissionStarted","","running",Evidence.request);
            if (Session != null && !IsInsertion) Session.MissionStarted(Evidence.request);
            if (Motor == null) { Finish("failed", "缺少移动控制器"); return false; }
            if(!IsInsertion&&LooksLikeInsertion(request)){Finish("rejected","no_active_mission_to_resume：没有可恢复的运行任务，请先发起原任务");return false;}
            _lastPosition = Motor.transform.position; _sampleAt = 0;
            Catalog.Refresh(Motor.transform);
            AddReferenceTargets();
            Evidence.intent = UseCodeExecution ? null : MissionIntent.Bind(request, Catalog);
            if (Evidence.intent != null) Write("intent.json", JsonUtility.ToJson(Evidence.intent, true));
            else Write("request.txt", request);
            if (!UseCodeExecution && (!UseSemanticInterpretation || PlannerOverride != null) && !string.IsNullOrEmpty(Evidence.intent.error)) { Finish("rejected", Evidence.intent.error); return false; }
            if(!TaskAuthority.Acquire(Context.MissionId,1|2|8)){Finish("rejected","resources_owned_by_another_run");return false;}
            using(TaskAuthority.Enter(Context.MissionId))Acquire();
            _main = StartCoroutine(UseCodeExecution ? RunCode(_generation) : Run(_generation));
            return true;
        }
        void Acquire()
        {
            ClearAttention();
            _leased = true; _oldArrivalRadius = Motor.ArriveRadius;
            if (Session != null && Session.Director != null)
            {
                _brainWasProactive = Session.Brain != null && Session.Brain.EnableProactive;
                Session.Director.SuspendBrain(true);
            }
            if (Gimbal != null) { _oldAutoRecenter = Gimbal.AutoRecenter; Gimbal.AutoRecenter = false; Gimbal.Center(); }
        }
        void Release()
        {
            if (!_leased) return; _leased = false;
            if (Motor != null) Motor.ArriveRadius = _oldArrivalRadius;
            if (Evidence.status != "succeeded" || !(_holdAttention||_holdDirection)) { ClearAttention(); if (Gimbal != null) Gimbal.Center(); }
            if (Gimbal != null) Gimbal.AutoRecenter = (_holdAttention||_holdDirection) ? false : _oldAutoRecenter;
            if (Session != null && Session.Brain != null) Session.Brain.EnableProactive = _brainWasProactive;
        }
        public void Cancel(string reason = "用户停止任务")
        {
            if (!IsRunning) return;
            _generation++; CancelInsertions(); if (_cancel != null) _cancel.Cancel();
            if (_main != null) StopCoroutine(_main); _main = null;
            DisposeCodeStack();
            StopWorkers();
            Finish("cancelled", reason);
        }
        void StopWorkers()
        {
            foreach (var worker in _workers) if (worker != null) StopCoroutine(worker);
            _workers.Clear(); _held = 0;
            foreach (var stack in _guards.ToArray()) DisposeStack(stack);
            _guards.Clear();
            using(TaskAuthority.Enter(Context!=null?Context.MissionId:null)){if (Motor != null) Motor.Stop("任务结束", "cancelled");}
            if (_vision != null) _vision.AbortAskRequest();
            if(Session!=null)Session.CancelMissionSpeech(Context!=null?Context.MissionId:null);
            if (_speechClip != null) { Destroy(_speechClip); _speechClip = null; }
        }
        void Finish(string status, string reason)
        {
            if (Evidence == null) return;
            if (status != "succeeded") LastReport = "";
            Evidence.status = status; Evidence.reason = reason;
            foreach (var r in Evidence.results) if (r.status == "running" || r.status == "pending")
            { r.status = status == "cancelled" ? "cancelled" : "skipped"; r.reason = reason; r.ended = TaskNow; }
            Evidence.ended = TaskNow; Evidence.elapsed = Evidence.ended - Evidence.started;
            Evidence.wall_ended=Time.realtimeSinceStartup;Evidence.wall_elapsed=Evidence.wall_ended-Evidence.wall_started;
            if (Motor != null) { Evidence.final_position = Motor.transform.position; Evidence.final_yaw = Motor.transform.eulerAngles.y; }
            if (string.IsNullOrEmpty(LastReport)) LastReport = FactualReport();
            foreach(var insertion in Evidence.insertions) if(insertion.status!="succeeded") LastReport+="；插入任务未完成："+insertion.reason;
            Evidence.report = LastReport; Evidence.paused_seconds=_pausedSeconds; IsRunning = false; Phase = status; _main = null;
            EmitRuntime(status=="cancelled"?"MissionCancelled":status=="succeeded"?"MissionSucceeded":"MissionFailed","",status,reason);
            using(TaskAuthority.Enter(Context!=null?Context.MissionId:null))Release();
            if(Context!=null){Context.Active=false;TaskAuthority.Release(Context.MissionId);} Save();
            if (Session != null && !IsInsertion) Session.MissionFinished(LastReport, true);
            Raise();
        }
        void Update()
        {
            if (IsSuspended) return;
            UpdateAttention();
            if (!IsRunning || Motor == null) return;
            Vector3 p = Motor.transform.position;
            float moved = TaskTargetCatalog.FlatDistance(p, _lastPosition);
            Evidence.actual_path_length += moved;
            if (Motor.IsMoving) Evidence.movement_seconds += Time.unscaledDeltaTime;
            _lastPosition = p;
            if (TaskNow >= _sampleAt)
            {
                _sampleAt = TaskNow + 0.15f;
                File.AppendAllText(Path.Combine(RunDirectory, "trajectory.jsonl"), JsonUtility.ToJson(new PoseSample {
                    at = TaskNow, position = p, yaw = Motor.transform.eulerAngles.y, phase = Phase }) + "\n");
            }
        }
        [Serializable] class PoseSample { public float at, yaw; public Vector3 position; public string phase; }
        IEnumerator Run(int generation)
        {
            Phase = "planning"; Raise();
            if (_plannerConfig != null) Destroy(_plannerConfig);
            if (Session == null || Session.Config == null) { Finish("failed", "缺少核心 LLM 配置"); yield break; }
            _plannerConfig = Instantiate(Session.Config); _plannerConfig.MaxReplyChars = 1100;
            _plannerConfig.Temperature = 0.15f; _plannerConfig.ThinkingEnabled = false;
            _client = PlannerOverride ?? new DeepSeekClient(_plannerConfig, this);
            Evidence.planner_model = PlannerOverride != null ? "test-double" : _plannerConfig.Model;
            if (UseSemanticInterpretation && PlannerOverride == null)
            {
                Phase="understanding"; Raise();
                string semanticPrompt=MissionSemanticInterpreter.Prompt(Evidence.request,Catalog);
                Write("semantic_prompt.txt",semanticPrompt);
                MissionIntent interpreted=null;
                for(int attempt=0;attempt<2;attempt++)
                {
                    string reply=null, semanticError=null;
                    yield return CallCore(semanticPrompt,(v,e)=>{reply=v;semanticError=e;});
                    if(generation!=_generation) yield break;
                    Write("semantic_raw_"+attempt+".txt",reply??semanticError??"empty");
                    SemanticMission parsed=null;
                    try {parsed=JsonUtility.FromJson<SemanticMission>(ExtractJson(reply));} catch(Exception e) {semanticError=e.Message;}
                    interpreted=MissionSemanticInterpreter.Validate(Evidence.request,parsed,Catalog);
                    if(string.IsNullOrEmpty(interpreted.error)||parsed!=null&&!string.IsNullOrEmpty(parsed.error)) break;
                    semanticPrompt=MissionSemanticInterpreter.Prompt(Evidence.request,Catalog)+"\n上次解释未通过本地校验："+interpreted.error+"。请保留全部任务并修正JSON。";
                }
                Evidence.intent=interpreted; Write("intent.json",JsonUtility.ToJson(interpreted,true));
                if(interpreted==null||!string.IsNullOrEmpty(interpreted.error)) {Finish("rejected",interpreted!=null?interpreted.error:"语义理解失败");yield break;}
            }
            Phase="planning"; Raise();
            string prompt = PlannerPrompt(); Write("planner_prompt.txt", prompt);
            string raw = null, error = null; MissionPlan plan = null;
            for (int attempt = 0; attempt < 2; attempt++)
            {
                yield return CallCore(prompt, (v, e) => { raw = v; error = e; });
                if (generation != _generation) yield break;
                Write("planner_raw_" + attempt + ".txt", raw ?? error ?? "empty");
                try { plan = JsonUtility.FromJson<MissionPlan>(ExtractJson(raw)); } catch (Exception e) { error = "plan_parse_failed: " + e.Message; }
                string validation = plan == null ? error ?? "empty_plan" : MissionPlanValidator.Validate(plan, Evidence.intent, Catalog);
                if (validation == null) break;
                error = validation; plan = null;
                prompt = PlannerPrompt() + "\nYour previous JSON was rejected: " + validation + "\nPrevious reply: " + raw + "\nRepair it. Preserve the user destination contract exactly.";
            }
            if (plan == null) { Finish("rejected", "任务计划未通过校验：" + error); yield break; }
            Evidence.plan = plan; Write("plan.json", JsonUtility.ToJson(plan, true));
            foreach (var step in plan.steps) Evidence.results.Add(new MissionStepResult { id = step.id, kind = step.kind, target_id = step.target_id });
            Phase = "executing"; Save(); Raise();
            while (generation == _generation)
            {
                bool allDone = true;
                for (int i = 0; i < plan.steps.Length; i++)
                {
                    var step = plan.steps[i]; var r = Evidence.results[i];
                    if (r.status == "failed") { StopWorkers(); Finish("failed", step.id + ": " + r.reason); yield break; }
                    if (r.status == "succeeded") continue;
                    allDone = false;
                    if (r.status != "pending" || !DependenciesDone(step)) continue;
                    int resources = MissionPlanValidator.Resources(step.kind);
                    if ((_held & resources) != 0) continue;
                    _held |= resources; r.status = "running"; r.started = TaskNow;
                    Phase = step.id + " " + step.kind; Save(); Raise();
                    _workers.Add(StartCoroutine(Worker(step, r, resources)));
                }
                if (allDone) break;
                if (TaskNow - Evidence.started > 600f) { StopWorkers(); Finish("failed", "任务总超时"); yield break; }
                yield return null;
            }
            if (generation == _generation) { StopWorkers(); Finish("succeeded", "所有请求步骤均已完成"); }
        }
        bool DependenciesDone(MissionStep step)
        {
            foreach (string dep in step.after ?? new string[0])
            { var r = Evidence.results.Find(x => x.id == dep); if (r == null || r.status != "succeeded") return false; }
            return true;
        }
        IEnumerator Worker(MissionStep s, MissionStepResult r, int resources)
        {
            r.start_position = Motor.transform.position;
            IEnumerator routine;
            switch (s.kind)
            {
                case "navigate": case "return": routine = Navigate(s, r); break;
                case "route_progress": routine = FollowRoute(s,r); break;
                case "move_relative": routine = MoveRelative(s,r); break;
                case "turn_body": routine = TurnBody(s,r); break;
                case "look_at": routine = LookAt(s, r); break;
                case "look_direction": routine = LookDirection(s,r); break;
                case "observe": ClearAttention(); routine = Observe(s, r); break;
                case "interact": routine = Interact(s, r); break;
                case "check": routine = CheckView(s, r); break;
                case "gesture": routine = Gesture(s, r); break;
                case "speak": routine = Speak(s, r); break;
                case "express": routine = Express(s, r); break;
                default: routine = Report(s, r); break;
            }
            yield return Guard(routine, r, s.timeout > 0 ? s.timeout : s.kind == "navigate" || s.kind == "return" ? NavigationTimeout : 120f);
            r.ended = TaskNow; r.end_position = Motor.transform.position;
            if (r.status == "running") r.status = "succeeded";
            _held &= ~resources; Save(); Raise();
        }
        static void DisposeStack(Stack<IEnumerator> stack)
        {
            while (stack.Count > 0)
            {
                var d = stack.Pop() as IDisposable;
                try { if (d != null) d.Dispose(); } catch (Exception e) { Debug.LogWarning("[Task] 清理: " + e.Message); }
            }
        }
        IEnumerator Guard(IEnumerator routine, MissionStepResult r, float seconds)
        {
            var stack = new Stack<IEnumerator>(); stack.Push(routine); _guards.Add(stack);
            float until = TaskNow + seconds;
            try
            {
                while (stack.Count > 0)
                {
                    if(IsSuspended){yield return null;continue;}
                    if (TaskNow > until) { Fail(r, "step_timeout"); break; }
                    object current = null; bool more = false; string error = null;
                    try { using(TaskAuthority.Enter(Context!=null?Context.MissionId:null))more = stack.Peek().MoveNext(); if (more) current = stack.Peek().Current; }
                    catch (Exception e) { error = e.GetType().Name + ": " + e.Message; }
                    if (error != null) { Fail(r, error); break; }
                    if (!more) { var done = stack.Pop() as IDisposable; if (done != null) done.Dispose(); continue; }
                    var nested = current as IEnumerator;
                    if (nested != null) stack.Push(nested); else yield return current;
                }
            }
            finally { DisposeStack(stack); _guards.Remove(stack); }
        }
        IEnumerator Navigate(MissionStep s, MissionStepResult r)
        {
            var target = s.kind == "return" ? null : Catalog.Get(s.target_id);
            Vector3 destination = s.target_id == "mission_origin" ? Evidence.task_origin : Evidence.origin, face = Vector3.zero; List<Vector3> route;
            string error;
            if (s.kind != "return")
            {
                if (!Catalog.FindApproach(target, Motor.transform.position, out destination, out route, out error)) { Fail(r, error); yield break; }
                face = target.Position; r.target_path = target.Path; r.target_position = face;
            }
            else { route = WorldData.FindPath(Motor.transform.position, destination); r.target_path = s.target_id == "mission_origin" ? "mission/task_origin" : "scene/origin"; r.target_position = destination; }
            if (route == null) { Fail(r, "unreachable"); yield break; }
            r.approach_position = destination; r.route.AddRange(route);
            ClearAttention();
            if (Gimbal != null) Gimbal.Center();
            float oldRadius = Motor.ArriveRadius; Motor.ArriveRadius = Mathf.Min(oldRadius, ArrivalRadius * 0.5f);
            Vector3 previous = Motor.transform.position;
            try
            {
                bool arrived = false;
                for (int attempt = 0; attempt <= MaxNavigationRetries; attempt++)
                {
                    r.attempts++;
                    if (!Motor.MoveTo(destination)) { Fail(r, Motor.LastStatus + ": " + Motor.LastError); yield break; }
                    while (Motor.HasTarget)
                    {
                        if (target != null && (target.Object == null && !target.IsAnchor || TaskTargetCatalog.FlatDistance(target.Position, r.target_position) > 0.25f))
                        { Motor.Stop("目标移动或丢失", "cancelled"); Fail(r, "target_moved_or_destroyed"); yield break; }
                        Vector3 p = Motor.transform.position; r.path_length += TaskTargetCatalog.FlatDistance(previous, p); previous = p;
                        yield return null;
                    }
                    r.distance_error = TaskTargetCatalog.FlatDistance(Motor.transform.position, destination);
                    if (Motor.LastStatus == "arrived" && r.distance_error <= ArrivalRadius) { arrived = true; break; }
                    if (Motor.LastStatus == "walking_disabled" || !Motor.WalkingEnabled) break;
                    Evidence.navigation_retries++; WorldData.Refresh(); yield return null;
                }
                if (!arrived) { Fail(r, "navigation_failed: " + Motor.LastStatus + ": " + Motor.LastError); yield break; }
                Vector3 delta = face - Motor.transform.position; delta.y = 0;
                float yaw = s.kind == "return" ? (s.target_id == "mission_origin" ? Evidence.task_origin_yaw : Evidence.origin_yaw) : target.IsAnchor || delta.sqrMagnitude < 0.0001f ? Motor.transform.eulerAngles.y : Quaternion.LookRotation(delta).eulerAngles.y;
                yield return TurnTo(yaw);
                r.facing_error = Mathf.Abs(Mathf.DeltaAngle(Motor.transform.eulerAngles.y, yaw));
                if (r.facing_error > FacingTolerance) { Fail(r, "facing_not_reached"); yield break; }
                float until = TaskNow + 0.5f;
                while (TaskNow < until)
                {
                    if (TaskTargetCatalog.FlatDistance(Motor.transform.position, destination) > ArrivalRadius) { Fail(r, "arrival_not_stable"); yield break; }
                    yield return null;
                }
                r.reason = "真实路径到达并面向指定目标";
            }
            finally { Motor.ArriveRadius = oldRadius; }
        }
        IEnumerator TurnTo(float yaw)
        {
            float angle = Mathf.DeltaAngle(Motor.transform.eulerAngles.y, yaw);
            if (Mathf.Abs(angle) > 0.5f) Motor.TurnRelative(angle, "mission");
            float until = TaskNow + 4f;
            while (Motor.IsTurning && TaskNow < until) yield return null;
        }
        IEnumerator Observe(MissionStep s, MissionStepResult r)
        {
            if (EyeCamera == null || Gimbal == null) { Fail(r, "eye_camera_or_gimbal_missing"); yield break; }
            if (_vision == null) { _vision = gameObject.AddComponent<QwenVisionClient>(); _vision.Model = QwenVisionClient.DefaultModel; _vision.AskMaxTokens = 280; _vision.TimeoutSec = 25f; }
            if (!_vision.HasKey && !_vision.LoadKey()) { Fail(r, "vision_key_missing"); yield break; }
            float yaw = Motor.transform.eulerAngles.y;
            var descriptions = new List<string>();
            for (int i = 0; i < 4; i++)
            {
                yield return TurnTo(yaw + i * 90f);
                if (Motor.IsTurning || Mathf.Abs(Mathf.DeltaAngle(Motor.transform.eulerAngles.y, yaw + i * 90f)) > FacingTolerance)
                { Fail(r, "scan_body_heading_not_reached"); yield break; }
                Gimbal.Center();
                float until = TaskNow + 4f;
                while (Gimbal.IsObservationSettling && TaskNow < until) yield return null;
                if (Gimbal.IsObservationSettling) { Fail(r, "scan_camera_not_settled"); yield break; }
                yield return new WaitForEndOfFrame();
                string frame = Path.Combine(RunDirectory, s.id + "_view_" + i + ".jpg");
                byte[] jpg = CaptureFrame(); File.WriteAllBytes(frame, jpg);
                string hash; using (var sha = SHA256.Create()) hash = BitConverter.ToString(sha.ComputeHash(jpg)).Replace("-", "").ToLowerInvariant();
                r.frame_path = frame; r.frame_sha256 = hash;
                Write(s.id + "_view_" + i + ".json", JsonUtility.ToJson(new ViewRecord { yaw = Motor.transform.eulerAngles.y, position = Motor.transform.position, sha256 = hash, frame = frame }, true));
                string answer = null, error = null;
                yield return _vision.Ask("用中文描述当前第一人称画面中能确认的物体、环境和异常；看不清就说明，不猜测画面外内容。", jpg, null, v => answer = v, e => error = e);
                if (string.IsNullOrWhiteSpace(answer)) { Fail(r, "semantic_observation_failed: " + error); yield break; }
                descriptions.Add("方向" + i + "：" + answer); r.observation = string.Join("\n", descriptions.ToArray());
                Write(s.id + "_view_" + i + "_description.txt", answer); Save();
            }
            yield return TurnTo(yaw); Gimbal.Center(); r.reason = "四个方向真实截图并完成视觉识别";
        }
        [Serializable] class ViewRecord { public float yaw; public Vector3 position; public string sha256, frame; }
        byte[] CaptureFrame()
        {
            RenderTexture rt = RenderTexture.GetTemporary(640, 360, 24, RenderTextureFormat.ARGB32);
            var oldTarget = EyeCamera.targetTexture; var oldActive = RenderTexture.active; Texture2D image = null;
            try
            {
                EyeCamera.targetTexture = rt; EyeCamera.Render(); RenderTexture.active = rt;
                image = new Texture2D(640, 360, TextureFormat.RGB24, false); image.ReadPixels(new Rect(0, 0, 640, 360), 0, 0); image.Apply();
                return image.EncodeToJPG(85);
            }
            finally { EyeCamera.targetTexture = oldTarget; RenderTexture.active = oldActive; RenderTexture.ReleaseTemporary(rt); if (image != null) Destroy(image); }
        }
        IEnumerator Interact(MissionStep s, MissionStepResult r)
        {
            var t = Catalog.Get(s.target_id);
            if (t == null || t.Interaction == null) { Fail(r, "interaction_not_registered"); yield break; }
            bool? done = null; string receipt = null;
            yield return t.Interaction.Execute(Motor.transform, (ok, detail) => { done = ok; receipt = detail; });
            if (done != true) { Fail(r, receipt ?? "interaction_missing_completion_receipt"); yield break; }
            r.target_path = t.Path; r.reason = receipt;
        }
        IEnumerator Gesture(MissionStep s, MissionStepResult r)
        {
            var actor=Session!=null?Session.Character:null;
            var entry=TaskGestureSkills.Get(s.action_id);
            if(actor==null||entry==null||actor.AnimPlayer==null||!actor.AnimPlayer.HasClip(entry.ClipName)) {Fail(r,"gesture_clip_missing");yield break;}
            r.action_id=entry.Id; r.clip_name=entry.ClipName;
            Motor.Stop("手势独占身体", "gesture");
            string gestureOwner=Context!=null?Context.MissionId:TaskAuthority.Caller;
            bool finished=false; Action<string> receipt=name=>{if(name==entry.ClipName)finished=true;};
            actor.AnimPlayer.AnimationFinished+=receipt;
            try
            {
                actor.PlayGesture(entry.Id); yield return null;
                if(actor.AnimPlayer.CurrentName!=entry.ClipName) {Fail(r,"gesture_not_started");yield break;}
                if(entry.Loop)
                {
                    float end=TaskNow+Mathf.Clamp(s.duration>0?s.duration:entry.Duration,0.1f,15f);
                    while(TaskNow<end)
                    {if(actor.AnimPlayer.CurrentName!=entry.ClipName) {Fail(r,"gesture_interrupted");yield break;} yield return null;}
                    finished=true;
                }
                else while(!finished)
                {if(actor.AnimPlayer.CurrentName!=entry.ClipName) {Fail(r,"gesture_interrupted");yield break;} yield return null;}
                r.reason="真实动画播放完成："+entry.Label+" / "+entry.ClipName;
            }
            finally
            {
                actor.AnimPlayer.AnimationFinished-=receipt;
                if(actor.AnimPlayer.CurrentName==entry.ClipName&&TaskAuthority.CanContinue(gestureOwner,1))
                    using(TaskAuthority.Enter(gestureOwner))foreach(var idle in TaskGestureSkills.Available()) if(idle.Loop&&idle.Posture=="stand"&&(idle.Label??"").Contains("待机")) {actor.PlayGesture(idle.Id);break;}
            }
        }
        IEnumerator Speak(MissionStep s, MissionStepResult r)
        {
            if(!VoiceEnabled){Fail(r,"speech_requested_but_voice_disabled");yield break;}
            int gen=_generation;var ctx=Context;
            if(Session==null){Fail(r,"speech_session_missing");yield break;}
            var ticket=Session.QueueTaskSpeech(ctx.MissionId,s.text,()=>gen==_generation&&ctx.Active&&!ctx.Muted);
            while(ticket.Status=="queued"||ticket.Status=="synthesizing"||ticket.Status=="playing")yield return null;
            if(ticket.Status=="cancelled"&&gen==_generation){r.status="suppressed";r.reason="speech_interrupted_by_user; not a completed utterance";}
            else if(ticket.Status!="succeeded")Fail(r,"speech_"+ticket.Status+": "+ticket.Error);else r.reason="实际语音播放完成";
        }
        IEnumerator Express(MissionStep s, MissionStepResult r)
        {
            if (Session == null || Session.Character == null || Session.Character.Expressions == null) { Fail(r, "expression_driver_missing"); yield break; }
            // Tool emotion is semantic; the expression library uses the existing smile id.
            Session.Character.SetExpression(s.emotion == "happy" ? "smile" : s.emotion);
            float end = TaskNow + Mathf.Clamp(s.duration > 0 ? s.duration : 2f, 0.1f, 15f);
            while (TaskNow < end) yield return null;
            r.reason = "表情已设置并保持";
        }
        IEnumerator Report(MissionStep s, MissionStepResult r)
        {
            // State-bearing statements use controlled receipt text; model prose cannot change them.
            LastReport="";r.reason="authoritative_receipt_report";yield return null;
        }
        string FactualReport()
        {
            if (Evidence == null) return "";
            var b = new StringBuilder(); b.Append("任务状态：").Append(Evidence.status).Append("。 ");
            if (!string.IsNullOrEmpty(Evidence.reason)) b.Append(Evidence.reason).Append("。\n");
            foreach (var r in Evidence.results) if (r.kind != "report")
            { b.Append(r.id).Append(' ').Append(r.kind).Append(' ').Append(r.target_id).Append(": ").Append(r.status).Append(' ').Append(r.reason).AppendLine(); if (!string.IsNullOrEmpty(r.observation)) b.AppendLine(r.observation); }
            return b.ToString();
        }
        string PlannerPrompt()
        {
            var targets = new List<DestinationSnapshot>();
            foreach (var action in Evidence.intent.required_actions) { var t = Catalog.Get(action.target_id); if (t != null && !targets.Exists(x=>x.id==t.Id)) targets.Add(t.Snapshot()); }
            return "You plan a Unity character mission. Return one JSON object ONLY. Never write executable code.\n" +
                "Authoritative original request and exact destination order: " + JsonUtility.ToJson(Evidence.intent) + "\n" +
                "Requested real Unity targets: " + JsonUtility.ToJson(new TargetList { targets = targets.ToArray() }) + "\n" +
                "Mission origin pose: " + Evidence.origin + ", yaw=" + Evidence.origin_yaw + "\n" +
                "Schema: {\"summary\":\"...\",\"steps\":[{\"id\":\"s1\",\"kind\":\"navigate\",\"target_id\":\"EXACT_ID\",\"action_id\":\"\",\"after\":[],\"text\":\"\",\"emotion\":\"\",\"camera_only\":false,\"duration\":0,\"timeout\":90},...]}\n" +
                "Kinds: navigate, look_at, observe, interact, return, gesture, speak, express, report. 'after' lists prior step ids that must finish. Steps must be topologically ordered. " +
                "The required_actions array is authoritative: emit each navigate/look_at/observe/interact/return/gesture exactly once, with the exact target_id and order, each depending on the preceding required action. Never move an observation to a different location. Navigate each requested destination in its exact order; each later navigate/return must depend on the previous movement. origin and mission_origin MUST use kind return. For a gesture the JSON MUST include the action_id field, e.g. {id:s2,kind:gesture,target_id:EMPTY_STRING,action_id:THE_REQUIRED_ID,after:[s1]}. Gesture MUST use the exact action_id in required_actions, target_id empty. Finish the animation BEFORE the next body action. " +
                "look_at turns the eye camera toward its target WITHOUT walking there. It needs no navigation dependency unless a location was requested first. Copy camera_only exactly from the corresponding required_action (true forbids body rotation). Do not replace look_at with observe or navigate. Single look_at uses look_at then report. " +
                "Observe only if requested, after navigating to its target; it means a real four-direction camera scan and semantic vision analysis. " +
                "Interact only if requested and target has interactable=true, after navigating there. No invented interactions. " +
                "A full request normally orders navigate→observe→navigate→interact→return→report: each step waits for the preceding required work. " +
                "Speak only if speak=true; express only if express=true (emotion happy or neutral, duration 0.1–15). parallel_requirements is MANDATORY, not optional: with_action is a zero-based required_actions index. For its speak/express step copy EXACTLY the same after list as that required action (e.g. navigate.after=[] and express.after=[]), NEVER depend on that body step. This ensures simultaneous launch. Without an explicit parallel requirement follow the requested sequence. " +
                "No unrequested observations, gesture, speech, interaction or return. Do not omit requested work. Always end in one report depending on all prior work. " +
                "For a single destination use navigate then report. Keep plans short. Unity handles coordinates, A*, arrival and errors.\n" +
                "Original user text: " + Evidence.request;
        }
        [Serializable] class TargetList { public DestinationSnapshot[] targets; }
        IEnumerator CallCore(string prompt, Action<string, string> done)
        {
            Evidence.llm_calls++;
            using (var token = CancellationTokenSource.CreateLinkedTokenSource(_cancel.Token))
            {
                var task = _client.ChatAsync(new List<ChatMessage> { new ChatMessage("system", prompt) }, token.Token);
                float end = TaskNow + PlannerTimeout;
                while (!task.IsCompleted && TaskNow < end) yield return null;
                if (!task.IsCompleted) { token.Cancel(); done(null, "core_llm_timeout"); yield break; }
                if (task.IsFaulted || task.IsCanceled) { done(null, "core_llm_cancelled_or_faulted"); yield break; }
                done(task.Result, _client.LastError);
            }
        }
        public static string ExtractJson(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "";
            int first = raw.IndexOf('{'), last = raw.LastIndexOf('}');
            return first >= 0 && last > first ? raw.Substring(first, last - first + 1) : raw;
        }
        static void Fail(MissionStepResult r, string error) { r.status = "failed"; r.reason = error; }
        void Raise() { if (Session != null) Session.MissionProgress(Phase); if (Changed != null) Changed(); }
        void Write(string name, string text) { File.WriteAllText(Path.Combine(RunDirectory, name), text ?? "", Encoding.UTF8); }
        void Save() { if (Evidence != null && !string.IsNullOrEmpty(RunDirectory)) Write("evidence.json", JsonUtility.ToJson(Evidence, true)); }
        void OnDisable() { Cancel("任务组件停用"); if(!IsInsertion)ClearAttention(); }
        void OnDestroy() { Cancel("任务组件销毁"); if(!IsInsertion)ClearAttention(); if (_plannerConfig != null) Destroy(_plannerConfig); if (_cancel != null) _cancel.Dispose(); }
    }
}
