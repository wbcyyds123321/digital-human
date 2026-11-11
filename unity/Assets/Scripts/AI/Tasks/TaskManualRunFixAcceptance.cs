using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MoonSharp.Interpreter;
using UnityEngine;
using Yixuan.AI;
using Yixuan.Vision;
using Yixuan.World;
namespace Yixuan.AI.Tasks
{
    public sealed class TaskManualRunFixAcceptance : MonoBehaviour
    {
        public string Output = "Captures/Acceptance/manual_run_fixes_2026-11-04";
        public bool Running;
        public bool TargetedOnly;
        public string Current;
        public RuntimeTestReport Report;
        TaskMissionRunner r; ChatSession s; Vector3 home; float yaw, oldScale; string oldRoot;
        Fixture planner = new Fixture();
        string Dir { get { return Path.GetFullPath(Path.Combine(Application.dataPath,"..",Output)); } }
        static float Now { get { return Time.realtimeSinceStartup; } }
        public void Begin()
        {
            if(Running)return;
            r=FindObjectOfType<TaskMissionRunner>();r.ResolveRefs();s=r.Session;
            home=r.Motor.transform.position;yaw=r.Motor.transform.eulerAngles.y;oldScale=Time.timeScale;oldRoot=r.EvidenceRoot;
            s.Config=Instantiate(s.Config);s.Config.VoiceEnabled=false;s.Config.VoiceInputEnabled=false;s.Config.VoiceInputMode="off";s.Config.StreamMode="off";
            s.TaskAcknowledgmentEnabled=false;
            if(s.VoiceInput!=null){s.VoiceInput.SetEnabled(false);DestroyImmediate(s.VoiceInput);s.VoiceInput=null;}
            if(s.Tts!=null){DestroyImmediate(s.Tts);s.Tts=null;}
            if(s.Brain!=null)s.Brain.EnableProactive=false;
            if(s.Vision!=null){s.Vision.SetGameView(false);s.Vision.SetWebcam(false);s.Vision.enabled=false;}
            r.VoiceEnabled=false;r.PlannerOverride=planner;r.EvidenceRoot=Path.Combine(Output,"missions");
            Report=new RuntimeTestReport{suite="Captured real Lua and contract replay; actual locomotion; no network LLM/ASR"};Running=true;
            Directory.CreateDirectory(Dir);StartCoroutine(Run());
        }
        void Save(){File.WriteAllText(Path.Combine(Dir,"acceptance_report.json"),JsonUtility.ToJson(Report,true));File.WriteAllText(Path.Combine(Dir,"checkpoint.txt"),Current+" passed="+Report.cases.FindAll(x=>x.pass).Count+"/"+Report.cases.Count);}
        void Check(string name,bool pass,string detail)
        {Report.cases.Add(new RuntimeTestResult{name=name,pass=pass,expected="true",actual=detail,directory=r.RunDirectory});Save();Debug.Log("[ManualRunFixAcceptance] "+(pass?"PASS ":"FAIL ")+name+" "+detail);}
        void Reset()
        {
            if(r.IsRunning)r.Cancel("acceptance_reset");s.CancelDialogueTurn();r.StopAttention();
            using(TaskAuthority.Enter(null)){r.Motor.Stop();r.Gimbal.Center(true);}
            r.Motor.enabled=true;r.Motor.SetWalkingEnabled(true);r.Motor.transform.position=home;r.Motor.transform.rotation=Quaternion.Euler(0,yaw,0);r.CodeTimeout=600;
        }
        IEnumerator Wait(float limit)
        {float end=Now+limit;while(r.IsRunning&&Now<end)yield return null;}
        string State {get{return r.Evidence.status+"|"+r.Evidence.reason+" moving="+r.Motor.IsMoving+" target="+r.Motor.HasTarget;}}
        bool Success {get{return !r.IsRunning&&r.Evidence.status=="succeeded"&&r.Evidence.completion.task_succeeded;}}
        IEnumerator Captured(string directory,float scale,float limit)
        {
            Reset();Time.timeScale=scale;Current="captured/"+directory;
            string from=Path.Combine(Application.dataPath,"..","Captures/TaskMissions",directory);
            var original=JsonUtility.FromJson<MissionEvidence>(File.ReadAllText(Path.Combine(from,"evidence.json")));
            string program=File.ReadAllText(Path.Combine(from,"mission.lua"));
            r.SubmitContractTest(original.request,program,original.contract);yield return Wait(limit);
            bool ordered=true;float end=0;int effects=0;
            foreach(var receipt in r.Evidence.results)if(receipt.kind!="report"){ordered&=receipt.status=="succeeded"&&receipt.started>=end;end=receipt.ended;effects++;}
            Check(Current,Success&&ordered&&effects==original.contract.obligations.Length&&!r.Motor.IsMoving&&!r.Motor.HasTarget,State+" effects="+effects+" ordered="+ordered+" scale="+scale);
        }
        void Unit()
        {
            var vm=new TaskLuaRuntime("local a,b=pcall(function() error('caught') end);local c,d=xpcall(function() error('handled') end,function(e)return 'handled' end);return {ok=not a and not c and d=='handled'}",v=>new Table(v),q=>q);
            var done=vm.Resume();Check("lua/pcall_xpcall",vm.Done&&done.Table.Get("ok").Boolean,"protected calls handle Lua errors");
            vm=new TaskLuaRuntime("return {ok=io==nil and os==nil and require==nil and load==nil and debug==nil and coroutine==nil and print==nil}",v=>new Table(v),q=>q);
            done=vm.Resume();Check("lua/sandbox_remains_restricted",done.Table.Get("ok").Boolean,"no file/network/CLR/dynamic-loader access");
            var route=new List<Vector3>{Vector3.zero,new Vector3(300,0,0)};
            float fast=TaskMissionRunner.NavigationTimeBudget(route,Vector3.zero,1.05f,300),slow=TaskMissionRunner.NavigationTimeBudget(route,Vector3.zero,0.525f,300);
            Check("budget/length_speed",fast>400&&slow>fast,"normal="+fast+" slow="+slow);
            Check("budget/bounded",TaskMissionRunner.NavigationTimeBudget(route,Vector3.zero,0,300)<0&&TaskMissionRunner.NavigationTimeBudget(new List<Vector3>{new Vector3(5000,0,0)},Vector3.zero,1,300)<0,"invalid/over-limit routes rejected");
            foreach(var name in new[]{"navigate","return_home","follow_route"})Check("budget/automatic_"+name,TaskToolRegistry.Find(name).Timeout==0,"no 120-second navigation default");
        }
        IEnumerator Deadline()
        {
            Reset();Time.timeScale=1;Current="timeout/explicit";
            var c=new TaskContract{obligations=new[]{new TaskObligation{id="n",source="国立大学",kind="navigate",target_id="anchor/msu_main"}}};
            r.SubmitContractTest("去国立大学","return {ok=game.navigate('anchor/msu_main',{timeout=0.5}).ok}",c);yield return Wait(5);
            Check(Current,!r.IsRunning&&r.Evidence.status=="failed"&&r.Evidence.reason.Contains("step_timeout")&&!r.Motor.IsMoving&&!r.Motor.HasTarget,State);
            Reset();r.CodeTimeout=1;Current="timeout/total_guard";r.SubmitContractTest("等待十秒","game.wait(10);return {ok=true}",new TaskContract{obligations=new[]{new TaskObligation{id="w",source="等待十秒",kind="wait",min_duration=10}}});yield return Wait(5);
            Check(Current,!r.IsRunning&&r.Evidence.reason.Contains("code_wall_time_exceeded"),State);r.CodeTimeout=600;
        }
        IEnumerator LongRoute()
        {
            Reset();Time.timeScale=1;home=WorldData.GetAnchor("central_plaza").Position;r.Motor.transform.position=home;Current="long_route/wave_university_wave";
            string from=Path.Combine(Application.dataPath,"..","Captures/TaskMissions/mission_20261104-220829-535");
            var original=JsonUtility.FromJson<MissionEvidence>(File.ReadAllText(Path.Combine(from,"evidence.json")));
            r.SubmitContractTest(original.request,File.ReadAllText(Path.Combine(from,"mission.lua")),original.contract);
            float end=Now+520;bool crossed=false;
            while(r.IsRunning&&Now<end)
            {
                var nav=r.Evidence.results.Find(x=>x.kind=="navigate");
                if(!crossed&&nav!=null&&nav.status=="running"&&Now-nav.started>122){crossed=true;Check("long_route/still_moving_after_120s",r.Motor.IsMoving&&nav.timeout_seconds>120,"elapsed="+(Now-nav.started)+" timeout="+nav.timeout_seconds+" path="+nav.path_length);}
                yield return null;
            }
            int waves=r.Evidence.results.FindAll(x=>x.kind=="gesture"&&x.status=="succeeded").Count;
            var arrival=r.Evidence.results.Find(x=>x.kind=="navigate");
            Check(Current,Success&&crossed&&waves==2&&arrival!=null&&arrival.status=="succeeded"&&arrival.ended-arrival.started>120&&!r.Motor.IsMoving&&!r.Motor.HasTarget,State+" waves="+waves+" navSeconds="+(arrival==null?0:arrival.ended-arrival.started));
        }
        IEnumerator Run()
        {
            try
            {
                yield return null;Unit();
                if(!TargetedOnly){yield return Captured("mission_20261104-220301-298",8,85);
                yield return Captured("mission_20261104-221140-103",8,190); }
                yield return Deadline();
                if(!TargetedOnly)yield return LongRoute();
                var unit=TaskRuntimeUnitAcceptance.Run(Path.Combine(Dir,"unit"));foreach(var item in unit.cases)Report.cases.Add(item);
            }
            finally
            {
                if(r.IsRunning)r.Cancel("acceptance_end");Time.timeScale=oldScale;r.EvidenceRoot=oldRoot;r.PlannerOverride=null;r.CodeTimeout=600;
                Report.done=true;Running=false;Current="done";Save();
            }
        }
        sealed class Fixture : ILLMClient
        {
            public bool IsBusy{get{return false;}}public string LastError{get{return null;}}
            public Task<string> ChatAsync(List<ChatMessage> messages,CancellationToken token){return Task.FromResult("执行结果已记录。");}
        }
    }
}
