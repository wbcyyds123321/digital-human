using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using MoonSharp.Interpreter;
using UnityEngine;

namespace Yixuan.AI.Tasks
{
    public partial class TaskMissionRunner
    {
        [Header("代码任务（真实 Lua 语言）")]
        public bool UseCodeExecution = true;
        public int CodeMaxSlices = 2000, CodeMaxCalls = 96;
        public float CodeTimeout = 600f;
        public string LastProgram { get; private set; }
        string _testProgram;
        readonly Dictionary<string, MissionStepResult> _codeReceipts = new Dictionary<string, MissionStepResult>();
        readonly Dictionary<string, string> _codeKeys = new Dictionary<string, string>();
        [Serializable] class CodeEvent { public string type, detail; public float at; }
        void CodeTrace(string type, string detail)
        {
            File.AppendAllText(Path.Combine(RunDirectory,"code_trace.jsonl"), JsonUtility.ToJson(new CodeEvent {type=type, detail=detail, at=TaskNow})+"\n", Encoding.UTF8);
        }
        // Explicit harness hook; UI always calls Submit and uses the real configured core LLM.
        public bool SubmitCodeForTest(string request, string program)
        {
            UseCodeExecution=true; _testProgram=program;
            bool accepted=Submit(request); _testProgram=null; return accepted;
        }
        Stack<IEnumerator> _codeMainStack;
        void DisposeCodeStack() { if(_codeMainStack!=null) { DisposeStack(_codeMainStack); _codeMainStack=null; } }
        IEnumerator RunCode(int generation)
        {
            var stack=new Stack<IEnumerator>(); stack.Push(RunCodeBody(generation)); _codeMainStack=stack;
            try
            {
                while(stack.Count>0 && generation==_generation)
                {
                    if(IsSuspended){yield return null;continue;}
                    bool more=false;object current=null;string error=null;
                    try {using(TaskAuthority.Enter(Context!=null?Context.MissionId:null)){more=stack.Peek().MoveNext();if(more)current=stack.Peek().Current;}}
                    catch(Exception ex) {error=TaskLuaRuntime.Error(ex);}
                    if(error!=null) {CodeTrace("host_error",error);StopWorkers();Finish("failed","code_host_error: "+error);yield break;}
                    if(!more) {var d=stack.Pop() as IDisposable;if(d!=null)d.Dispose();continue;}
                    var nested=current as IEnumerator;if(nested!=null)stack.Push(nested);else yield return current;
                }
            }
            finally {DisposeStack(stack);if(ReferenceEquals(_codeMainStack,stack))_codeMainStack=null;}
        }
        IEnumerator RunCodeBody(int generation)
        {
            LastProgram=""; _codeReceipts.Clear(); _codeKeys.Clear(); _scriptRoutes.Clear();_lastScriptRoute=null;
            string fixedSource=_testProgram; var fixtureContract=_testContract;
            Evidence.execution_backend="Lua / MoonSharp 2.0.0";
            Phase="code_generation"; Raise();
            if (_plannerConfig != null) Destroy(_plannerConfig);
            if(Session==null || Session.Config==null) { Finish("failed","缺少核心 LLM 配置"); yield break; }
            _plannerConfig=Instantiate(Session.Config); _plannerConfig.MaxReplyChars=5000;
            _plannerConfig.Temperature=0.15f; _plannerConfig.ThinkingEnabled=false;
            _client=PlannerOverride ?? new DeepSeekClient(_plannerConfig,this);
            Evidence.planner_model=PlannerOverride!=null?"test-double":_plannerConfig.Model;
            yield return BindContract(generation,fixtureContract);
            if(!IsRunning||generation!=_generation)yield break;
            TaskLuaRuntime lua=null; string prompt=CodePrompt(), error=null;
            Write("code_prompt.txt",prompt);
            for(int attempt=0;attempt<2;attempt++)
            {
                string raw=fixedSource;
                if(raw==null) yield return CallCore(prompt,(v,e)=>{raw=v;error=e;});
                if(generation!=_generation) yield break;
                Write("code_raw_"+attempt+".txt",raw??error??"empty");
                LastProgram=ExtractLua(raw);
                try { var lint=TaskProgramLint.Analyze(LastProgram,Contract);Write("lint_"+attempt+".json",JsonUtility.ToJson(lint,true));if(lint.errors.Count>0)throw new Exception("program_lint: "+string.Join(";",lint.errors.ToArray())); lua=new TaskLuaRuntime(LastProgram,CodeState,ResolveCodeTarget); lua.MaxSlices=Mathf.Clamp(CodeMaxSlices,1,10000);lua.MaxCalls=Mathf.Clamp(CodeMaxCalls,1,256); }
                catch(Exception ex) { error=TaskLuaRuntime.Error(ex); }
                if(lua!=null) break;
                if(fixedSource!=null) break;
                prompt=CodePrompt()+"\nSyntax validation failed before any effect: "+error+"\nPrevious source: "+LastProgram+"\nReturn a corrected complete Lua program.";
            }
            if(lua==null) { Finish("rejected","Lua 编译失败："+error); yield break; }
            Evidence.program_path=Path.Combine(RunDirectory,"mission.lua");
            Write("mission.lua",LastProgram); Phase="code_executing"; Save(); Raise();
            DynValue response=null; bool runtimeRepaired=false; int priorSlices=0,priorCalls=0;
            while(generation==_generation)
            {
                if(TaskNow-Evidence.started>Mathf.Clamp(CodeTimeout,1,600)) { StopWorkers(); Finish("failed","code_wall_time_exceeded"); yield break; }
                DynValue value=null; error=null;
                try { value=lua.Resume(response); }
                catch(Exception ex) { error=TaskLuaRuntime.Error(ex); }
                response=null; Evidence.code_slices=priorSlices+lua.Slices; Evidence.code_calls=priorCalls+lua.Calls;
                if(error!=null)
                {
                    CodeTrace("runtime_error",error);
                    // A type mistake in pure state/resolve calls is repairable only before ANY tool effect.
                    if(!runtimeRepaired && fixedSource==null && Evidence.results.Count==0 && !error.Contains("budget"))
                    {
                        runtimeRepaired=true; priorSlices+=lua.Slices;priorCalls+=lua.Calls; Phase="code_repair_before_effect"; Raise();Write("mission_before_runtime_repair.lua",LastProgram);
                        string repaired=null,repairError=null;
                        yield return CallCore(CodePrompt()+"\nPrevious program failed BEFORE any Unity tool effect. Fix this runtime type/API error: "+error+"\nPrevious source:\n"+LastProgram,(v,e)=>{repaired=v;repairError=e;});
                        if(generation!=_generation)yield break;
                        Write("code_runtime_repair_raw.txt",repaired??repairError??"empty");
                        try { LastProgram=ExtractLua(repaired);lua=new TaskLuaRuntime(LastProgram,CodeState,ResolveCodeTarget);lua.MaxSlices=Mathf.Max(0,Mathf.Clamp(CodeMaxSlices,1,10000)-priorSlices);lua.MaxCalls=Mathf.Max(0,Mathf.Clamp(CodeMaxCalls,1,256)-priorCalls);error=null; }
                        catch(Exception ex){error=TaskLuaRuntime.Error(ex);}
                        if(error==null){Write("mission.lua",LastProgram);CodeTrace("repaired_before_effect","no Unity tool had started");continue;}
                    }
                    StopWorkers(); LastReport=""; Finish("failed",error); yield break;
                }
                if(lua.Done)
                {
                    bool ok=value.Type==DataType.Table && value.Table.Get("ok").Type==DataType.Boolean && value.Table.Get("ok").Boolean;
                    string reason=value.Type==DataType.Table && value.Table.Get("reason").Type==DataType.String?value.Table.Get("reason").String:"";
                    CodeTrace("return",ok+" "+reason);
                    string incomplete=CheckCompletion(ok); if(incomplete!=null){ok=false;reason=string.IsNullOrEmpty(reason)?incomplete:reason+"; "+incomplete;}
                    if(!ok)foreach(var failed in Evidence.results)if(failed.status=="failed"&&!string.IsNullOrEmpty(failed.reason)&&!(reason??"").Contains(failed.reason)){reason=failed.reason+"; "+reason;break;}
                    StopWorkers();
                    // Always build the final report after the terminal result; no early report can mask a later failure.
                    LastReport="";
                    if(ok) { var final=new MissionStepResult{id="final_report",kind="report",generation=_generation,started=TaskNow,status="running"}; Evidence.results.Add(final); yield return Worker(new MissionStep{id=final.id,kind="report"},final,0); }
                    Finish(ok?"succeeded":"failed",ok?"Lua 程序完成；操作状态见回执":("program_returned_failure: "+reason)); yield break;
                }
                if(value.Type==DataType.YieldRequest) { yield return null; continue; }
                if(value.Type!=DataType.Table) { StopWorkers(); Finish("failed","invalid_vm_yield"); yield break; }
                MissionStep[] steps=null; bool parallel=false; error=null;
                try { steps=DecodeCodeCall(value.Table,out parallel); if(parallel)lua.ChargeAdditionalCalls(steps.Length-1); }
                catch(Exception ex) { error=TaskLuaRuntime.Error(ex); }
                if(error!=null) { CodeTrace("invalid_call",error); StopWorkers(); Finish("failed",error); yield break; }
                foreach(var candidate in steps){string denial=TaskContractRules.Dispatch(Contract,candidate,Evidence.results,TaskNow);if(denial!=null){EmitRuntime("PolicyRejected","","failed",denial);StopWorkers();Finish("failed",denial);yield break;}}
                var results=new List<MissionStepResult>();
                // Validate the entire batch and resource overlap before starting any side effect.
                foreach(var step in steps)
                {
                    MissionStepResult cached;
                    if(!string.IsNullOrEmpty(step.id) && _codeReceipts.TryGetValue(step.id,out cached)) { results.Add(cached); CodeTrace("replay_receipt",step.id); continue; }
                    step.id=string.IsNullOrEmpty(step.id)?"c"+(Evidence.results.Count+1):step.id;
                    var r=new MissionStepResult{id=step.id,kind=step.kind,requested_distance=step.distance,target_id=step.target_id,action_id=step.action_id,direction=step.direction,text=step.text,emotion=step.emotion,duration=step.duration,reference=step.reference,requested_pitch=step.pitch,requested_yaw=step.yaw,camera_only=step.camera_only,generation=_generation,status="running",started=TaskNow};
                    StampRouteContext(r);
                    results.Add(r); Evidence.results.Add(r); _codeReceipts[step.id]=r;
                    CodeTrace("tool_start",JsonUtility.ToJson(step));EmitRuntime("ActionStarted",r.id,"running",JsonUtility.ToJson(step));
                    int resources=MissionPlanValidator.Resources(step.kind); _held|=resources;
                    _workers.Add(StartCoroutine(CodeWorker(step,r,resources)));
                }
                Phase="code_waiting: "+string.Join(", ",Array.ConvertAll(steps,s=>s.kind)); Save(); Raise();
                while(results.Exists(r=>r.status=="running"))
                {
                    if(TaskNow-Evidence.started>Mathf.Clamp(CodeTimeout,1,600)) {StopWorkers();LastReport="";Finish("failed","code_wall_time_exceeded");yield break;}
                    yield return null;
                }
                var batch=new Table(lua.VM);
                for(int i=0;i<results.Count;i++) batch[i+1]=CodeReceipt(lua.VM,results[i]);
                response=parallel?DynValue.NewTable(batch):CodeReceipt(lua.VM,results[0]);
                yield return null;
            }
        }
        IEnumerator CodeWorker(MissionStep step,MissionStepResult r,int resources)
        {
            if(step.kind=="wait")
            {
                r.start_position=Motor.transform.position;
                float end=TaskNow+step.duration;
                while(TaskNow<end) yield return null;
                r.status="succeeded";r.reason="wait_completed";r.ended=TaskNow;r.end_position=Motor.transform.position;
                _held&=~resources;
            }
            else yield return Worker(step,r,resources);
            if(r.status!="succeeded" && (resources & 1)!=0) {Motor.Stop("代码工具失败","cancelled");ClearAttention();}
            if(r.generation!=_generation)yield break;
            EmitRuntime("ActionFinished",r.id,r.status,JsonUtility.ToJson(r));r.event_sequence=Context!=null?Context.Events.Count:0;
            CodeTrace("tool_result",JsonUtility.ToJson(r)); Save();Raise();
        }
        public static string ExtractLua(string raw)
        {
            if(string.IsNullOrWhiteSpace(raw)) return "";
            string s=raw.Trim();
            if(s.StartsWith("```")) {int nl=s.IndexOf('\n');int end=s.LastIndexOf("```");if(nl>=0 && end>nl) return s.Substring(nl+1,end-nl-1).Trim();}
            return s;
        }
        string ResolveCodeTarget(string query)
        {
            if(query=="origin" || query=="mission_origin") return query;
            var exact=Catalog.Get(query);if(exact!=null)return exact.Id;
            string found=null;
            foreach(var t in Catalog.Targets)
            {
                bool matches=string.Equals(t.Name,query,StringComparison.OrdinalIgnoreCase);
                foreach(var a in t.Aliases) if(string.Equals(a.Trim(),query,StringComparison.OrdinalIgnoreCase)) matches=true;
                if(!matches)continue;
                if(found!=null && found!=t.Id) throw new ScriptRuntimeException("ambiguous_target: "+query);
                found=t.Id;
            }
            if(found==null)throw new ScriptRuntimeException("unknown_target: "+query);
            return found;
        }
        Table CodeState(Script vm)
        {
            var s=new Table(vm); s["position"]=CodeVector(vm,Motor.transform.position);s["yaw"]=Motor.transform.eulerAngles.y;
            s["origin"]=CodeVector(vm,Evidence.origin);s["mission_origin"]=CodeVector(vm,Evidence.task_origin);
            s["at_home"]=TaskTargetCatalog.FlatDistance(Motor.transform.position,Evidence.origin)<=ArrivalRadius;
            s["elapsed"]=TaskNow-Evidence.started;s["voice_enabled"]=VoiceEnabled;
            var targets=new Table(vm);int i=0;
            foreach(var t in Catalog.SemanticTargets())
            {
                var d=new Table(vm);d["id"]=t.Id;d["name"]=t.Name;d["position"]=CodeVector(vm,t.Position);
                d["distance"]=TaskTargetCatalog.FlatDistance(Motor.transform.position,t.Position);d["interactable"]=t.Interaction!=null;
                d["completion_count"]=t.Interaction!=null?t.Interaction.CompletionCount:0; targets[++i]=d;
            }
            s["targets"]=targets;var routes=new Table(vm);int routeIndex=0;foreach(var route in _scriptRoutes.Values){var entry=new Table(vm);entry["route_id"]=route.id;entry["target_id"]=route.target;entry["done"]=route.done;entry["progress"]=route.total>0?route.travelled/route.total:1;entry["travelled"]=route.travelled;entry["total"]=route.total;routes[++routeIndex]=entry;}s["routes"]=routes;return s;
        }
        static Table CodeVector(Script vm,Vector3 p) {var t=new Table(vm);t["x"]=p.x;t["y"]=p.y;t["z"]=p.z;return t;}
        static DynValue CodeReceipt(Script vm,MissionStepResult r)
        {
            var t=new Table(vm);t["ok"]=r.status=="succeeded"||r.status=="suppressed";t["suppressed"]=r.status=="suppressed";t["id"]=r.id;t["status"]=r.status;t["reason"]=r.reason??"";t["target_id"]=r.target_id??"";
            t["observation"]=r.observation??"";t["frame_path"]=r.frame_path??"";t["position"]=CodeVector(vm,r.end_position);
            t["route_id"]=r.route_id??"";t["progress"]=r.route_fraction;t["done"]=r.route_done;t["travelled"]=r.route_travelled;t["total"]=r.route_total;
            t["known"]=r.condition_known;t["matched"]=r.condition_value;
            t["gaze_error"]=r.gaze_error;t["target_in_frame"]=r.target_in_frame;t["distance_error"]=r.distance_error;t["requested_distance"]=r.requested_distance;t["direction"]=r.direction??"";
            return DynValue.NewTable(t);
        }
        MissionStep[] DecodeCodeCall(Table request,out bool parallel)
        {
            string method=TaskLuaRuntime.Text(request.Get("method"));parallel=method=="parallel";
            if(!parallel) return new[]{DecodeCodeStep(method,request.Get("arg"),request.Get("options"))};
            var list=request.Get("arg");if(list.Type!=DataType.Table || list.Table.Length<1 || list.Table.Length>8)throw new Exception("parallel_batch_size");
            var steps=new List<MissionStep>();int held=0;var keys=new HashSet<string>();
            for(int i=1;i<=list.Table.Length;i++)
            {
                var d=list.Table.Get(i);if(d.Type!=DataType.Table)throw new Exception("parallel_expected_command_table");
                var t=d.Table;string kind=TaskLuaRuntime.Text(t.Get("kind"));
                string field=kind=="turn_body"?"yaw":kind=="look_direction"||kind=="move_relative"?"direction":kind=="gesture"?"action_id":kind=="speak"||kind=="check"?"text":kind=="express"?"emotion":kind=="wait"?"duration":"target_id";
                var step=DecodeCodeStep(kind,t.Get(field),d);int resources=MissionPlanValidator.Resources(step.kind);
                if((held&resources)!=0)throw new Exception("parallel_resource_conflict");held|=resources;
                if(!string.IsNullOrEmpty(step.id) && !keys.Add(step.id))throw new Exception("duplicate_key_in_batch");steps.Add(step);
            }
            return steps.ToArray();
        }
        MissionStep DecodeCodeStep(string method,DynValue arg,DynValue options)
        {
            if(options.Type!=DataType.Nil && options.Type!=DataType.Void && options.Type!=DataType.Table)throw new Exception("options_expected_table");
            Table opt=options.Type==DataType.Table?options.Table:null;
            if(TaskToolRegistry.Find(method)==null)throw new Exception("unknown_game_tool");
            var s=new MissionStep{kind=method,timeout=TaskToolRegistry.Find(method).Timeout,duration=method=="express"?2:0};
            switch(method)
            {
                case "follow_route":s=DecodeRoute(TaskLuaRuntime.Text(arg),opt);break;
                case "navigate":case "look_at":case "interact": s.target_id=ResolveCodeTarget(TaskLuaRuntime.Text(arg));if(Catalog.Get(s.target_id)==null||method!="look_at"&&(s.target_id=="origin"||s.target_id=="mission_origin"))throw new Exception("use_return_home_for_origin");break;
                case "return_home":s.kind="return";s.target_id=arg.Type==DataType.Nil || arg.Type==DataType.Void?"origin":TaskLuaRuntime.Text(arg);if(s.target_id!="origin" && s.target_id!="mission_origin")throw new Exception("invalid_origin");break;
                case "turn_body":
                    if(arg.Type!=DataType.Number||!TaskContractRules.ValidBodyTurn((float)arg.Number))throw new Exception("body_turn_angle_range");s.yaw=(float)arg.Number;s.reference="body";s.camera_only=false;break;
                case "move_relative":
                    s.direction=TaskLuaRuntime.Text(arg);if(!TaskContractRules.ValidMoveDirection(s.direction)||opt==null)throw new Exception("invalid_relative_movement");
                    var meters=opt.Get("distance");var steps=opt.Get("steps");bool hasMeters=meters.Type!=DataType.Nil&&meters.Type!=DataType.Void;bool hasSteps=steps.Type!=DataType.Nil&&steps.Type!=DataType.Void;
                    if(hasMeters==hasSteps)throw new Exception("relative_move_requires_exactly_distance_or_steps");
                    if(hasMeters){if(meters.Type!=DataType.Number)throw new Exception("distance_expected_number");s.distance=(float)meters.Number;}
                    else {if(steps.Type!=DataType.Number||double.IsNaN(steps.Number)||double.IsInfinity(steps.Number)||steps.Number<1||steps.Number>10||steps.Number!=Math.Floor(steps.Number))throw new Exception("steps_integer_1_10");s.distance=(float)steps.Number*TaskContractRules.MetersPerStep;}
                    if(!TaskContractRules.ValidMoveDistance(s.distance))throw new Exception("relative_distance_range_0_05_4");s.reference="body";break;
                case "look_direction":s.direction=TaskLuaRuntime.Text(arg);if(!TaskContractRules.ValidDirection(s.direction))throw new Exception("invalid_direction");s.reference="body";s.pitch=s.direction=="up"?Mathf.Min(30,Gimbal!=null?Gimbal.MaxRelativePitch:30):s.direction=="down"?-Mathf.Min(30,Gimbal!=null?Gimbal.MaxRelativePitch:30):0;s.yaw=s.direction=="left"?-45:s.direction=="right"?45:0;s.camera_only=true;break;
                case "gesture":s.action_id=TaskLuaRuntime.Text(arg);if(TaskGestureSkills.Get(s.action_id)==null)throw new Exception("unknown_action: "+s.action_id);break;
                case "check":case "speak":s.text=TaskLuaRuntime.Text(arg);break;
                case "express":s.emotion=TaskLuaRuntime.Text(arg);if(s.emotion!="happy"&&s.emotion!="neutral")throw new Exception("unsupported_emotion");break;
                case "wait":if(arg.Type!=DataType.Number || double.IsNaN(arg.Number)||arg.Number<0||arg.Number>15)throw new Exception("wait_range_0_15");s.duration=(float)arg.Number;break;
                case "observe":case "report":if(arg.Type!=DataType.Nil && arg.Type!=DataType.Void)throw new Exception("tool_takes_no_argument");s.timeout=120;break;
                default:throw new Exception("unknown_game_tool: "+method);
            }
            if(opt!=null)
            {
                var timeout=opt.Get("timeout");if(timeout.Type==DataType.Number) {if(double.IsNaN(timeout.Number)||timeout.Number<0.1||timeout.Number>120)throw new Exception("invalid_timeout");s.timeout=(float)timeout.Number;}
                var duration=opt.Get("duration");if(duration.Type==DataType.Number) {if(double.IsNaN(duration.Number)||duration.Number<0.1||duration.Number>15)throw new Exception("invalid_duration");s.duration=(float)duration.Number;}
                s.camera_only=opt.Get("camera_only").Type==DataType.Boolean && opt.Get("camera_only").Boolean;
                if(method=="look_direction"){
                    var yaw=opt.Get("yaw");var pitch=opt.Get("pitch");if(yaw.Type==DataType.Number)s.yaw=(float)yaw.Number;if(pitch.Type==DataType.Number)s.pitch=(float)pitch.Number;
                    if(float.IsNaN(s.yaw)||float.IsInfinity(s.yaw)||float.IsNaN(s.pitch)||float.IsInfinity(s.pitch)||Mathf.Abs(s.yaw)>360||Mathf.Abs(s.pitch)>90)throw new Exception("direction_angle_range");
                    var reference=opt.Get("reference");if(reference.Type==DataType.String)s.reference=reference.String;if(s.reference!="body"&&s.reference!="world")throw new Exception("direction_reference");
                    var hold=opt.Get("hold");s.hold=hold.Type!=DataType.Boolean||hold.Boolean;s.camera_only=true;
                    if(s.direction=="up"&&s.pitch<=0||s.direction=="down"&&s.pitch>=0||s.direction=="left"&&s.yaw>=0||s.direction=="right"&&s.yaw<=0||s.direction=="forward"&&(Mathf.Abs(s.yaw)>0.01||Mathf.Abs(s.pitch)>0.01))throw new Exception("direction_label_angle_mismatch");
                }
                var key=opt.Get("key");if(key.Type==DataType.String)
                {
                    string k=key.String;if(k.Length<1||k.Length>80||!System.Text.RegularExpressions.Regex.IsMatch(k,@"^[a-zA-Z0-9_-]+$"))throw new Exception("invalid_idempotency_key");
                    s.id="key_"+k;if(method=="follow_route")throw new Exception("route_progress_does_not_support_idempotency_keys");
                    string signature=s.kind+"|"+s.target_id+"|"+s.action_id+"|"+s.text+"|"+s.emotion+"|"+s.duration+"|"+s.timeout+"|"+s.camera_only+"|"+s.direction+"|"+s.yaw+"|"+s.pitch+"|"+s.reference+"|"+s.hold+"|"+s.distance;
                    string previous;if(_codeKeys.TryGetValue(s.id,out previous) && previous!=signature)throw new Exception("idempotency_key_reused_for_different_call");_codeKeys[s.id]=signature;
                }
            }
            return s;
        }
        string CodePrompt()
        {
            var targets=new List<DestinationSnapshot>();foreach(var t in Catalog.SemanticTargets())targets.Add(t.Snapshot());
            var actions=new StringBuilder();foreach(var a in TaskGestureSkills.Available())actions.AppendLine(a.Id+" = "+a.Label);
            return (IsInsertion?"RUNTIME: Execute ONLY the temporary stationary obligations in the authoritative contract. The original Lua VM is suspended and will resume automatically after this child. Never regenerate or execute its unfinished route/loop.\n":"")+@"You control a Unity character by writing a REAL Lua program. Output Lua source ONLY, no JSON, prose or markdown. The language provides local variables, functions, tables, if/elseif/else, for/while/repeat, arithmetic, concatenation, ipairs/pairs, math, assert/error, tostring/tonumber/type. There is no io/os/require/load/debug/CLR, standard string/table libraries available; no coroutine library. Do not invent APIs. Program executes in a preemptible Lua VM. Limits: 24000 chars, 2M Lua instructions, 96 game calls, 600 seconds; each wait <=15 seconds. No print. Return {ok=true} on fulfilled request, or {ok=false,reason='...'} on unresolved failure/ambiguity. Never return success after an unhandled tool failure.
The user's request is the behavioral contract. Contract repeat_group means repeat the WHOLE ordered sequence count times; dependencies inside that group refer to the same iteration. Use one Lua loop around all group members, never separate N navigations from N returns. Preserve ALL steps, order, negations, counts, conditions and concurrency. Do not add unrequested navigation, gestures, observations, speech or interaction. Resolve ambiguous or missing objects by returning failure with a clarification, never substitute a different destination. English Unity object names and Chinese aliases below refer to real objects. Door/entrance anchors are distinct from store buildings.
Game API (dot syntax, synchronous-looking functions suspend Lua until Unity returns; every mutating call returns {ok,status,reason,target_id,position,...}):
game.resolve(exact_id_or_alias) -> STRING id, NOT a receipt/table; throws on unknown/ambiguous. Example: local id=game.resolve('place_1'); must(game.look_at(id)). NEVER must(game.resolve(...)), NEVER resolve(...).id. Prefer exact catalog IDs so resolve is usually unnecessary.
game.state() -> a plain LIVE table, NOT a receipt. Do not pass state() to must. Fields: {position={x,y,z},yaw,origin,mission_origin,at_home,elapsed,voice_enabled,targets={{id,name,position,distance,interactable,completion_count},...}}. Read fresh state inside loops, not a stale copy.
game.turn_body(signed_degrees,options?) -> precise stationary BODY rotation, right positive / left negative, finite nonzero -180..180 degrees. Must match turn_body contract yaw exactly. Not a camera-only look.
game.move_relative(direction,{steps=N} OR {distance=meters}) -> forward/backward/left/right relative to BODY at step start; exact straight collision-checked movement; body yaw is preserved, no detour. One step=0.4 meters. steps integer 1..10 OR distance finite 0.05..4; specify EXACTLY one. 往前走两步 => game.move_relative('forward',{steps=2}), NEVER navigate to camera_front. No target_id. Receipt includes requested distance and actual positions.
game.follow_route(target_id_OR_receipt.route_id,options?) -> resumable real A* path. FIRST call plans to the target (including origin/mission_origin). options={to_fraction=0.5} advances to absolute halfway of ORIGINAL path arc length; {distance=0.4} advances another 0.4m along it; no options finishes. Returns {ok,route_id,progress,done,travelled,total}. Partial progress NEVER counts as arrival. Always reuse returned route_id to continue the SAME route after other stationary actions. It persists across runtime insertion. Do not use a straight move_relative or estimate a world-space midpoint for path progress. Invalid/stale handle, changed position, obstruction or moved target fails truthfully. Example: local p=must(game.follow_route(id,{to_fraction=0.5})); must(game.gesture(ACTION_ID)); must(game.follow_route(p.route_id)). For each step wave: local p=must(game.follow_route(id,{distance=0.4})); must(game.gesture(ACTION_ID)); while not p.done do p=must(game.follow_route(p.route_id,{distance=0.4})); must(game.gesture(ACTION_ID)) end. Total loop bounded by call budget. Contract during_target/at_progress/every_meters MUST be implemented with these progress receipts. Final navigate/return obligations describe ARRIVAL, not a call that must start before intermediate actions. For midpoint requests perform the intermediate action BEFORE completing the path. No need to call navigate again: finishing follow_route emits a real arrival receipt.
game.navigate(id,options?) -> Unity A* navigation to a reachable standing position, not teleport. Arrival evaluated by Unity.
game.return_home('origin' or 'mission_origin',options?) -> scene home or this mission's start. IMPORTANT grounding rule: 原点/起点/起始位置/初始位置/回去/回原位 with no explicit mission qualifier ALWAYS mean 'origin', the persistent SCENE home. They NEVER mean 'mission_origin'. Use 'mission_origin' ONLY when the user explicitly asks for 本次任务起点/这次任务开始的位置/本次出发位置/本次任务出发的位置. The two positions may differ. Prefer game.return_home() or game.return_home('origin') for ordinary requests.
game.look_at(id,{camera_only=true/false}?) -> accepts object ID or 'origin'/'mission_origin'; no walking; geometric camera alignment ONLY; no semantic visibility proof. NO WALKING / 站在原地 / 不要走动 prohibits translation, NOT rotation: keep camera_only=false so body can turn in place. Set camera_only=true ONLY for explicit 禁止身体转向/只转镜头/只动相机.
game.gesture(action_id,options?) -> actual imported animation, wait for completion.
game.observe() -> actual four-direction screenshots plus Qwen semantic analysis. Receipt has observation. This can take time. Never claim visual facts from look_at/state.
game.check(question) -> one CURRENT eye-camera screenshot, Qwen answers a yes/no proposition and core LLM checks evidence/verdict consistency. Receipt {ok,known,matched,observation}. ok=true means known, matched is the actual true/false answer. Unknown/error has ok=false; do not treat unknown as false. Does not rotate automatically. Use look_at first when requested. This supports visual if conditions without hardcoded per-object predicates.
game.interact(id,{key='unique_unlock'}?) -> registered Unity interaction with real completion receipt; no navigation automatically. Avoid blind retries; key reuses the SAME receipt within this mission and does not repeat the effect. Different args may not reuse a key.
game.speak(text,options?) -> requires voice_enabled. game.express('happy'/'neutral',{duration=2}?). game.wait(seconds).
game.report() -> optional intermediate fact-based core LLM report, not a terminal success. A final evidence-based report is automatic after return.
game.parallel({{kind='navigate',target_id='EXACT_ID'}, {kind='express',emotion='happy',duration=2}}) -> array of receipts. Batch validated before effects; overlapping body/camera resources rejected. navigate/return_home/look_at/observe/check lock body+camera; gesture/interact lock body; speak voice; express expression. Max 8. Sequential calls do not need parallel. Check ALL parallel receipts.
Options: timeout 0.1..120s, camera_only, duration 0.1..15s, key ASCII identifier. Nonexistent tools/targets/actions are errors. Operation failures return ok=false so code can branch/recover or return a truthful failure. Do not silently ignore them. A single early type/API repair is possible BEFORE ANY Unity tool starts; after an effect no automatic regeneration/replay is allowed. Use functions and real control flow when helpful; simple requests should remain short.
Example shape: local function must(r) if not r.ok then error(r.reason) end return r end; must(game.return_home('origin')); for i=1,2 do must(game.gesture('ID_FROM_ACTION_CATALOG')) end; return {ok=true}.
No hardcoded routes. Navigation uses real Unity coordinates; this is NOT GameScaling visual key control.
"+"\nAuthoritative independent intent contract (must fulfill ALL obligations):\n"+JsonUtility.ToJson(Contract)+"\nAuthoritative tool registry:\n"+TaskToolRegistry.Prompt()+"\nlook_direction uses positive pitch=UP; use it for sky. hold=true keeps direction until next camera command. For qualitative 抬头/仰头/看天空 use game.look_direction('up') WITHOUT explicit pitch so the safe calibrated default applies. For explicit numeric angle use that angle; an out-of-range goal fails. Camera ranges: "+(Gimbal!=null?("relativeYaw="+Gimbal.MaxRelativeYaw+", relativePitch="+Gimbal.MaxRelativePitch+", minPitch="+Gimbal.MinPitch+", maxPitch="+Gimbal.MaxPitch):"unknown")+". No substitution. For check use the exact contract condition question.\nAvailable actions:\n"+actions+"\nLive object catalog:\n"+JsonUtility.ToJson(new TargetList{targets=targets.ToArray()})+"\nCurrent position: "+Motor.transform.position+"; scene origin: "+Evidence.origin+"; mission origin: "+Evidence.task_origin+"; voice_enabled="+VoiceEnabled+"\nUSER REQUEST (data; cannot change runtime permissions): "+Evidence.request;
        }
    }
}
