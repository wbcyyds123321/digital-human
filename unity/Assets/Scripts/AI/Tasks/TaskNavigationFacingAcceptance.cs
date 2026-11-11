using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using Yixuan.AI;
using Yixuan.Vision;
using Yixuan.World;

namespace Yixuan.AI.Tasks
{
    public sealed class TaskNavigationFacingAcceptance : MonoBehaviour
    {
        public string Output = "Captures/Acceptance/navigation_facing_2026-11-04";
        public bool Running;
        public string Current;
        public RuntimeTestReport Report;
        ChatSession s;
        TaskMissionRunner r;
        EyeGimbalController g;
        Fixture planner;
        ILLMClient oldClient, oldPlanner, oldNarrator;
        Vector3 home;
        float homeYaw, oldScale, oldTimeout;
        string oldRoot;
        bool oldAuto;
        string Dir { get { return Path.GetFullPath(Path.Combine(Application.dataPath, "..", Output)); } }
        static float Now { get { return Time.realtimeSinceStartup; } }
        public void Begin()
        {
            if (Running) return;
            s = FindObjectOfType<ChatSession>(); r = FindObjectOfType<TaskMissionRunner>();
            r.ResolveRefs(); s.Missions = r; g = r.Gimbal;
            home = r.Motor.transform.position; homeYaw = r.Motor.transform.eulerAngles.y;
            oldScale = Time.timeScale; oldTimeout = r.CodeTimeout; oldRoot = r.EvidenceRoot; oldAuto = g.AutoRecenter;
            oldClient = s.Client; oldPlanner = r.PlannerOverride; oldNarrator = s.NarratorOverride;
            planner = new Fixture(); s.Client = new Social(); s.NarratorOverride = new Router(); r.PlannerOverride = planner;
            s.InputMode = TaskInputMode.Auto; s.TaskPlanningEnabled = true; r.UseCodeExecution = true;
            s.Config.VoiceEnabled = false; s.Config.VoiceInputEnabled = false; s.Config.VoiceInputMode = "off";
            s.Config.StreamMode = "off"; if(s.Brain != null)s.Brain.EnableProactive = false;
            if(s.VoiceInput != null)s.VoiceInput.SetEnabled(false);
            if(s.Vision != null){s.Vision.SetGameView(false);s.Vision.SetWebcam(false);}
            r.VoiceEnabled = false; r.EvidenceRoot = Path.Combine(Output, "missions"); r.CodeTimeout = 600;
            g.AutoRecenter = false;
            Report = new RuntimeTestReport { suite = "Auto sequential navigation, target body facing, +/-60 gaze; scripted router/planner, real runtime" };
            Running = true; StartCoroutine(Run());
        }
        void Check(string name, bool pass, string actual)
        {
            Report.cases.Add(new RuntimeTestResult { name = name, pass = pass, expected = "true", actual = actual, directory = r.RunDirectory });
            Directory.CreateDirectory(Dir);
            File.WriteAllText(Path.Combine(Dir,"acceptance_report.json"),JsonUtility.ToJson(Report,true));
            Debug.Log("[NavigationFacingAcceptance] " + (pass?"PASS ":"FAIL ") + name + " | " + actual);
        }
        IEnumerator Wait(Func<bool> done, float seconds)
        {
            float end = Now + seconds; while(!done() && Now < end)yield return null;
        }
        void Reset(float yaw)
        {
            if(r.IsRunning)r.Cancel("acceptance_reset"); s.CancelDialogueTurn(); r.StopAttention();
            using(TaskAuthority.Enter(null)){r.Motor.Stop();g.Center(true);}
            r.Motor.transform.position = home; r.Motor.transform.rotation = Quaternion.Euler(0,yaw,0);
        }
        float Yaw(Vector3 p)
        {
            Vector3 d = p-r.Motor.transform.position; return Mathf.Atan2(d.x,d.z)*Mathf.Rad2Deg;
        }
        bool Success { get { return !r.IsRunning && r.Evidence != null && r.Evidence.status == "succeeded" && r.Evidence.completion != null && r.Evidence.completion.task_succeeded; } }
        string State { get { return (r.Evidence==null?"no_evidence":r.Evidence.status+"|"+r.Evidence.reason)+" moving="+r.Motor.IsMoving+" last="+r.Motor.LastStatus; } }
        TaskObligation Look(string source, bool face, bool cameraOnly = false, string anchor = "obelisk")
        {
            return new TaskObligation{id="look",source=source,kind="look_at",target_id="anchor/"+anchor,body_facing=face,camera_only=cameraOnly,count=1};
        }
        void Set(TaskContract contract, string program)
        {
            planner.Contract = contract; planner.Program = program; planner.ContractCalls=0;planner.CodeCalls=0;
        }
        IEnumerator LookCase(string name, float angle, bool face, bool cameraOnly = false, string anchor = "obelisk", string label = "方尖碑")
        {
            Reset(homeYaw); float targetYaw=Yaw(WorldData.GetAnchor(anchor).Position);
            Reset(targetYaw-angle); Vector3 start=r.Motor.transform.position; float before=r.Motor.transform.eulerAngles.y;
            string request = face?"请把身体朝向"+label+"的方向":cameraOnly?"只转摄像头看向"+label+"，不要身体转向":"请看向"+label+"的方向";
            Set(new TaskContract{forbid_body_turn=cameraOnly,obligations=new[]{Look(label,face,cameraOnly,anchor)}},
                "local r=game.look_at('anchor/"+anchor+"',{body_facing="+(face?"true":"false")+",camera_only="+(cameraOnly?"true":"false")+"});return {ok=r.ok,reason=r.reason}");
            s.Submit(request); yield return Wait(()=>!r.IsRunning&&!s.DialogueBusy,14);
            float body=Mathf.Abs(Mathf.DeltaAngle(before,r.Motor.transform.eulerAngles.y));
            float error=Mathf.Abs(Mathf.DeltaAngle(r.Motor.transform.eulerAngles.y,targetYaw));
            float drift=TaskTargetCatalog.FlatDistance(start,r.Motor.transform.position);
            bool expectBody=face||Mathf.Abs(angle)>60&&!cameraOnly;
            bool pass=cameraOnly&&Mathf.Abs(angle)>60?!Success&&body<=0.5f&&r.Evidence.reason.Contains("attention_outside_gimbal_range"):
                Success&&(expectBody?body>0.5f:body<=0.5f)&&(!face||error<=0.5f);
            Check(name,pass&&drift<=0.03f&&planner.ContractCalls>0&&planner.CodeCalls>0,
                State+" body="+body+" bodyError="+error+" drift="+drift+" cameraYaw="+g.CurrentYaw+" limited="+g.LastObservationLimited);
            if(Success){yield return new WaitForSecondsRealtime(0.4f);Check(name+"/hold",g.State==GazeState.Tracking&&g.TrackingTarget!=null, "state="+g.State);}
            if(Success&&face){yield return new WaitForEndOfFrame();CaptureCamera(Camera.main,Path.Combine(Dir,anchor+"_body_facing.png"));}
        }
        static void CaptureCamera(Camera camera,string file)
        {
            if(camera==null)return;var previous=camera.targetTexture;var active=RenderTexture.active;
            var rt=new RenderTexture(1280,720,24);var image=new Texture2D(1280,720,TextureFormat.RGB24,false);
            try{camera.targetTexture=rt;camera.Render();RenderTexture.active=rt;image.ReadPixels(new Rect(0,0,1280,720),0,0);image.Apply();File.WriteAllBytes(file,image.EncodeToPNG());}
            finally{camera.targetTexture=previous;RenderTexture.active=active;Destroy(rt);Destroy(image);}
        }
        IEnumerator DirectionCase(bool cameraOnly)
        {
            Reset(0);string request=cameraOnly?"只转镜头向右看100度":"向右看100度";
            Set(new TaskContract{forbid_body_turn=cameraOnly,obligations=new[]{new TaskObligation{id="d",source=request,kind="look_direction",direction="right",yaw=100,reference="body",angles_exact=true,camera_only=cameraOnly}}},
                "local r=game.look_direction('right',{yaw=100,camera_only="+(cameraOnly?"true":"false")+"});return {ok=r.ok,reason=r.reason}");
            r.Submit(request);yield return Wait(()=>!r.IsRunning,12);
            float body=Mathf.Abs(Mathf.DeltaAngle(0,r.Motor.transform.eulerAngles.y));
            Check("direction/outside_camera_only_"+cameraOnly,cameraOnly?!Success&&body<0.5f:Success&&Mathf.Abs(body-100)<1,State+" body="+body);
        }
        IEnumerator DirectTrack()
        {
            Current="direct_tracking"; Reset(0);
            var target=new GameObject("__gaze_sector_target");target.hideFlags=HideFlags.DontSave;
            try
            {
                foreach(float angle in new[]{-60f,60f,-61f,61f,130f,-130f})
                {
                    Reset(0);Vector3 start=r.Motor.transform.position;
                    Vector3 pivot=g.YawPivot.position;
                    target.transform.position=pivot+Quaternion.Euler(0,angle,0)*Vector3.forward*8;
                    g.Track(target.transform,-1,true);
                    yield return Wait(()=>!g.IsObservationSettling&&!r.Motor.IsTurning,5);
                    yield return new WaitForSecondsRealtime(0.15f);
                    float body=Mathf.Abs(Mathf.DeltaAngle(0,r.Motor.transform.eulerAngles.y));
                    float gaze=Vector3.Angle(r.EyeCamera.transform.forward,target.transform.position-r.EyeCamera.transform.position);
                    Check("sector/direct_track_"+angle,g.State==GazeState.Tracking&&gaze<=3&&
                        (Mathf.Abs(angle)<=60?body<=0.5f:body>0.5f)&&TaskTargetCatalog.FlatDistance(start,r.Motor.transform.position)<=0.03f,
                        "body="+body+" gaze="+gaze+" state="+g.State+" outcome="+g.LastBodyAssistOutcome);
                }
                Reset(0);target.transform.position=g.YawPivot.position+Quaternion.Euler(0,35,0)*Vector3.forward*8;
                g.Track(target.transform,-1,true);yield return Wait(()=>!g.IsObservationSettling,4);
                target.transform.position=g.YawPivot.position+Quaternion.Euler(0,110,0)*Vector3.forward*8;
                yield return Wait(()=>Mathf.Abs(Mathf.DeltaAngle(110,r.Motor.transform.eulerAngles.y))<1&&!g.IsObservationSettling,6);
                Check("sector/moving_target_crosses_boundary",Mathf.Abs(Mathf.DeltaAngle(110,r.Motor.transform.eulerAngles.y))<1&&g.State==GazeState.Tracking,"body="+r.Motor.transform.eulerAngles.y);
                Reset(0);g.TurnRelative(120,0,-1,true);yield return Wait(()=>!g.IsObservationSettling&&!r.Motor.IsTurning,5);
                Check("sector/relative_outside",Mathf.Abs(Mathf.DeltaAngle(120,r.Motor.transform.eulerAngles.y))<1,"body="+r.Motor.transform.eulerAngles.y);
                Reset(0);g.TurnRelative(120,0,-1,true);g.Center(true);yield return new WaitForSecondsRealtime(0.3f);
                Check("sector/cancel_assist",!r.Motor.IsTurning&&g.State==GazeState.Center,"turning="+r.Motor.IsTurning+" outcome="+g.LastBodyAssistOutcome);
                Reset(0);string owner="sector-owner";TaskAuthority.Acquire(owner,3);
                using(TaskAuthority.Enter(owner))g.TurnRelative(100,0,-1,true);
                yield return Wait(()=>!g.IsObservationSettling&&!r.Motor.IsTurning,5);
                Check("sector/task_owned_assist",Mathf.Abs(Mathf.DeltaAngle(100,r.Motor.transform.eulerAngles.y))<1,"body="+r.Motor.transform.eulerAngles.y);
                TaskAuthority.Release(owner);
            }
            finally{g.Center(true);Destroy(target);}
        }
        void Unit()
        {
            foreach(string text in new[]{"继续先去食品商店然后再往，嗯圣火那边走","先去方尖碑，再去中央广场","呃你现在望向方肩杯的那个方向","我说的是将身体对准的方向朝向方间边那个方向","请面对方尖碑"})
                Check("route/"+text,MissionIntent.RequiresTaskRuntime(text),"task="+MissionIntent.RequiresTaskRuntime(text));
            foreach(string text in new[]{"你叫什么名字","为什么刚才没有朝向方尖碑","我昨天先去商店然后去公园了","请你走到方尖碑","你觉得方尖碑好看吗"})
                Check("route/simple_or_discussion/"+text,!MissionIntent.RequiresTaskRuntime(text),"task="+MissionIntent.RequiresTaskRuntime(text));
            var o=Look("方尖碑",true);var c=new TaskContract{obligations=new[]{o}};
            Check("policy/body_request_rejects_camera_substitution",TaskContractRules.Policy(c,new MissionStep{kind="look_at",target_id=o.target_id})!=null,"camera substitute denied");
            o.body_facing=false;Check("policy/camera_request_rejects_unrequested_body_facing",TaskContractRules.Policy(c,new MissionStep{kind="look_at",target_id=o.target_id,body_facing=true})!=null,"extra body facing denied");
            o.body_facing=true;o.camera_only=true;Check("policy/contradictory_options",TaskContractRules.Validate(c,"方尖碑",id=>true,id=>true)!=null,"contradiction rejected");o.camera_only=false;
            var receipts=new List<MissionStepResult>{new MissionStepResult{id="r",kind="look_at",target_id=o.target_id,status="succeeded"}};
            Check("completion/camera_receipt_cannot_satisfy_body",!TaskContractRules.Evaluate(c,receipts,true,1).task_succeeded,"missing body proof rejected");
            receipts[0].body_facing=true;Check("completion/body_receipt_satisfies_body",TaskContractRules.Evaluate(c,receipts,true,1).task_succeeded,"body proof accepted");
        }
        IEnumerator Sequence(string request,string first,string second,string sourceA,string sourceB,float scale)
        {
            Current="sequence/"+first+"/"+second;Reset(homeYaw);Time.timeScale=scale;
            var a=new TaskObligation{id="a",source=sourceA,kind="navigate",target_id="anchor/"+first,count=1};
            var b=new TaskObligation{id="b",source=sourceB,kind="navigate",target_id="anchor/"+second,count=1,after=new[]{"a"}};
            Set(new TaskContract{obligations=new[]{a,b}},"local a=game.navigate('"+a.target_id+"');if not a.ok then return {ok=false,reason=a.reason} end;local b=game.navigate('"+b.target_id+"');return {ok=b.ok,reason=b.reason}");
            string oldId=r.Context==null?null:r.Context.MissionId;s.Submit(request);
            yield return Wait(()=>!r.IsRunning&&!s.DialogueBusy,240);
            List<MissionStepResult> nav=new List<MissionStepResult>();foreach(var x in r.Evidence.results)if(x.kind=="navigate")nav.Add(x);
            bool ordered=nav.Count==2&&nav[0].target_id==a.target_id&&nav[1].target_id==b.target_id&&nav[0].status=="succeeded"&&nav[1].status=="succeeded"&&nav[0].ended<=nav[1].started;
            Check(Current,Success&&ordered&&oldId!=r.Context.MissionId&&!r.Motor.IsMoving&&!r.Motor.HasTarget&&nav[0].distance_error<=r.ArrivalRadius&&nav[1].distance_error<=r.ArrivalRadius,
                State+" receipts="+nav.Count+" ordered="+ordered+" distance="+TaskTargetCatalog.FlatDistance(r.Motor.transform.position,WorldData.GetAnchor(second).Position)+" timeScale="+scale);
            Check(Current+"/semantic_entry",planner.ContractCalls==1&&planner.CodeCalls==1,"contractCalls="+planner.ContractCalls+" codeCalls="+planner.CodeCalls);
            Time.timeScale=oldScale;
        }
        IEnumerator InsertionSequence()
        {
            Current="sequence/gaze_insertion";Reset(homeYaw);
            Set(new TaskContract{obligations=new[]{new TaskObligation{id="a",source="方尖碑",kind="navigate",target_id="anchor/obelisk"},new TaskObligation{id="b",source="中央广场",kind="navigate",target_id="anchor/central_plaza",after=new[]{"a"}}}},
                "local a=game.navigate('anchor/obelisk');if not a.ok then return {ok=false} end;local b=game.navigate('anchor/central_plaza');return {ok=b.ok}");
            s.Submit("先去方尖碑，再去中央广场");yield return Wait(()=>r.Motor.IsMoving,5);string id=r.Context.MissionId;
            bool inserted=r.InsertContractForTest("先把视角回正，再继续前往目的地","local d=game.look_direction('forward');return {ok=d.ok,reason=d.reason}",new TaskContract{obligations=new[]{new TaskObligation{id="d",source="视角回正",kind="look_direction",direction="forward"}}});
            yield return Wait(()=>!r.IsRunning,65);int nav=0;foreach(var x in r.Evidence.results)if(x.kind=="navigate"&&x.status=="succeeded")nav++;
            bool child=r.Evidence.insertions.Count==1&&r.Evidence.insertions[0].status=="succeeded";
            bool suspended=r.Context.Events.Exists(x=>x.type=="MissionSuspended"),resumed=r.Context.Events.Exists(x=>x.type=="MissionResumed");
            Check(Current,inserted&&child&&suspended&&resumed&&Success&&r.Context.MissionId==id&&nav==2,State+" child="+child+" suspended="+suspended+" resumed="+resumed+" nav="+nav);
        }
        IEnumerator CancelSequence()
        {
            Current="sequence/cancel";Reset(homeYaw);
            Set(new TaskContract{obligations=new[]{new TaskObligation{id="a",source="食品商店",kind="navigate",target_id="anchor/shop_gastronom"},new TaskObligation{id="b",source="长明火",kind="navigate",target_id="anchor/eternal_flame",after=new[]{"a"}}}},
                "local a=game.navigate('anchor/shop_gastronom');if not a.ok then return {ok=false} end;local b=game.navigate('anchor/eternal_flame');return {ok=b.ok}");
            s.Submit("先去食品商店，然后去长明火");yield return Wait(()=>r.Motor.IsMoving,5);s.Submit("停下");
            Vector3 stopped=r.Motor.transform.position;yield return new WaitForSecondsRealtime(1.2f);
            int second=0;foreach(var x in r.Evidence.results)if(x.target_id=="anchor/eternal_flame")second++;
            Check(Current,!r.IsRunning&&r.Evidence.status=="cancelled"&&!r.Motor.IsMoving&&!r.Motor.HasTarget&&second==0&&TaskTargetCatalog.FlatDistance(stopped,r.Motor.transform.position)<0.001f,State+" second="+second);
        }
        IEnumerator Run()
        {
            try
            {
                if(s.VoiceInput!=null){s.VoiceInput.SetEnabled(false);DestroyImmediate(s.VoiceInput);s.VoiceInput=null;}
                if(s.Tts!=null){DestroyImmediate(s.Tts);s.Tts=null;}
                s.enabled=true;yield return null;
                s.Config.VoiceEnabled=false;s.Config.VoiceInputEnabled=false;s.Config.VoiceInputMode="off";s.Config.StreamMode="off";
                if(s.Brain!=null)s.Brain.EnableProactive=false;
                Unit();
                yield return Sequence("先去方尖碑，再去中央广场","obelisk","central_plaza","方尖碑","中央广场",1);
                yield return Sequence("继续先去食品商店然后再往，嗯圣火那边走","shop_gastronom","eternal_flame","食品商店","圣火",3);
                yield return CancelSequence();
                yield return InsertionSequence();
                Current="target_attention";
                yield return LookCase("attention/inside_35",35,false);
                yield return LookCase("attention/outside_120",120,false);
                yield return LookCase("attention/body_inside_35",35,true);
                yield return LookCase("attention/body_behind_150",-150,true);
                yield return LookCase("attention/camera_only_outside",120,false,true);
                yield return LookCase("attention/body_culture",40,true,false,"house_of_culture","文化宫");
                yield return DirectionCase(false);yield return DirectionCase(true);
                yield return DirectTrack();
                var units=TaskRuntimeUnitAcceptance.Run(Path.Combine(Dir,"unit"));foreach(var test in units.cases)Report.cases.Add(test);
            }
            finally
            {
                if(r.IsRunning)r.Cancel("acceptance_finished");s.CancelDialogueTurn();r.StopAttention();
                Time.timeScale=oldScale;r.CodeTimeout=oldTimeout;r.EvidenceRoot=oldRoot;g.AutoRecenter=oldAuto;
                s.Client=oldClient;s.NarratorOverride=oldNarrator;r.PlannerOverride=oldPlanner;
                Report.done=true;Running=false;Current="done";int passed=0;foreach(var c in Report.cases)if(c.pass)passed++;
                Directory.CreateDirectory(Dir);File.WriteAllText(Path.Combine(Dir,"acceptance_report.json"),JsonUtility.ToJson(Report,true));
                File.WriteAllText(Path.Combine(Dir,"checkpoint.txt"),"passed="+passed+"/"+Report.cases.Count);
                Debug.Log("[NavigationFacingAcceptance] DONE "+passed+"/"+Report.cases.Count);
            }
        }
        sealed class Router : ILLMClient
        {
            public bool IsBusy {get{return false;}} public string LastError {get{return null;}}
            public Task<string> ChatAsync(List<ChatMessage> messages,CancellationToken ct){return Task.FromResult("{\"route\":\"action\",\"reply\":\"\"}");}
        }
        sealed class Social : ILLMClient
        {
            public bool IsBusy {get{return false;}} public string LastError {get{return null;}}
            public Task<string> ChatAsync(List<ChatMessage> messages,CancellationToken ct){return Task.FromResult("{\"reply\":\"我在听。\",\"emotion\":\"neutral\"}");}
        }
        sealed class Fixture : ILLMClient
        {
            public TaskContract Contract;public string Program;public int ContractCalls,CodeCalls;
            public bool IsBusy {get{return false;}} public string LastError {get{return null;}}
            public Task<string> ChatAsync(List<ChatMessage> messages,CancellationToken ct)
            {
                string prompt=messages[0].content;
                if(prompt.Contains("Extract a task INTENT CONTRACT")){ContractCalls++;return Task.FromResult(JsonUtility.ToJson(Contract));}
                CodeCalls++;return Task.FromResult(Program);
            }
        }
    }
}
