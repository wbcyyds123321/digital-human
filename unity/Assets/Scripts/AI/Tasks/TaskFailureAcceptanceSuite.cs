using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using Yixuan.Vision;

namespace Yixuan.AI.Tasks
{
    /// <summary>Explicit fault injection; production never starts these cases.</summary>
    public class TaskFailureAcceptanceSuite : MonoBehaviour
    {
        public string Root = "D:/AI/GameScaling/docs/evidence/task_orchestration_2026-10-24";
        public bool Running { get; private set; }
        public TaskAcceptanceSuite.SuiteReport Summary = new TaskAcceptanceSuite.SuiteReport();
        TaskMissionRunner _runner; bool _voiceInput; Vector3 _start;
        sealed class Replies : ILLMClient
        {
            readonly string[] _values; int _index;
            public Replies(params string[] values) { _values=values; }
            public bool IsBusy {get{return false;}} public string LastError {get{return null;}}
            public Task<string> ChatAsync(List<ChatMessage> messages,CancellationToken ct)
            { return Task.FromResult(_values[Math.Min(_index++,_values.Length-1)]); }
        }
        sealed class NeverReplies : ILLMClient
        {
            public bool IsBusy {get{return true;}} public string LastError {get{return null;}}
            public async Task<string> ChatAsync(List<ChatMessage> messages,CancellationToken ct)
            {await Task.Delay(Timeout.Infinite,ct);return null;}
        }
        void Record(string name,bool ok,string detail)
        {
            Summary.total++;if(ok)Summary.passed++;
            Summary.cases.Add(new TaskAcceptanceSuite.SuiteCase{name=name,pass=ok,reason=detail,directory=_runner.RunDirectory,status=_runner.Evidence.status});
            Save();
        }
        void Save(){Directory.CreateDirectory(Root);File.WriteAllText(Path.Combine(Root,"fault_suite.json"),JsonUtility.ToJson(Summary,true));}
        public void Begin(){if(Running)return;Running=true;StartCoroutine(Run());}
        void ResetActor(){if(_runner.IsRunning)_runner.Cancel("故障验收重置");_runner.Motor.Stop();_runner.Motor.transform.position=_start;_runner.Motor.transform.rotation=Quaternion.identity;}
        IEnumerator Wait(){float end=Time.realtimeSinceStartup+100;while(_runner.IsRunning&&Time.realtimeSinceStartup<end)yield return null;if(_runner.IsRunning)_runner.Cancel("故障验收超时");}
        bool _codeModeBefore;
        IEnumerator Run()
        {
            Summary.status="running";_runner=FindObjectOfType<TaskMissionRunner>();_runner.ResolveRefs();_codeModeBefore=_runner.UseCodeExecution;_runner.UseCodeExecution=false;_runner.EvidenceRoot=Root+"/fault_missions";
            _start=new Vector3(-2.4f,_runner.Motor.transform.position.y,3.8f);
            if(_runner.Session.VoiceInput!=null){_voiceInput=_runner.Session.VoiceInput.Enabled;_runner.Session.VoiceInput.SetEnabled(false);}
            ResetActor();_runner.PlannerOverride=new Replies("not JSON");_runner.Submit("前往绿色柱子");yield return Wait();
            Record("malformed_plan_never_moves",_runner.Evidence.status=="rejected"&&_runner.Evidence.llm_calls==2&&TaskTargetCatalog.FlatDistance(_start,_runner.Motor.transform.position)<0.01f,_runner.Evidence.reason);
            ResetActor();string plan=JsonUtility.ToJson(new MissionPlan{steps=new[]{new MissionStep{id="s1",kind="navigate",target_id="place_1",after=new string[0]},new MissionStep{id="s2",kind="report",after=new[]{"s1"}}}});
            _runner.PlannerOverride=new Replies("not JSON",plan,"已到达绿色柱子。");_runner.Submit("前往绿色柱子");yield return Wait();
            Record("one_repair_then_real_execution",_runner.Evidence.status=="succeeded"&&_runner.Evidence.llm_calls==3&&_runner.Evidence.results[0].distance_error<=0.25f,_runner.Evidence.reason);
            ResetActor();float oldTimeout=_runner.PlannerTimeout;_runner.PlannerTimeout=0.3f;_runner.PlannerOverride=new NeverReplies();_runner.Submit("前往绿色柱子");yield return Wait();
            Record("planner_timeout_never_moves",_runner.Evidence.status=="rejected"&&_runner.Evidence.llm_calls==2&&TaskTargetCatalog.FlatDistance(_start,_runner.Motor.transform.position)<0.01f,_runner.Evidence.reason);_runner.PlannerTimeout=oldTimeout;_runner.PlannerOverride=null;
            ResetActor();var vision=_runner.gameObject.AddComponent<QwenVisionClient>();vision.Model="qwen3.7-flash-2026-07-15";vision.Endpoint="http://127.0.0.1:1/intentional-unavailable";vision.TimeoutSec=5f;
            var field=typeof(TaskMissionRunner).GetField("_vision",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic);field.SetValue(_runner,vision);
            _runner.Submit("去1号地点观察一下四周");yield return Wait();
            var scan=_runner.Evidence.results.Find(x=>x.kind=="observe");
            Record("vision_failure_stops_no_fake_success",_runner.Evidence.status=="failed"&&scan!=null&&scan.status=="failed"&&scan.reason.Contains("semantic_observation_failed")&&!_runner.Motor.HasTarget&&!_runner.Motor.IsTurning,_runner.Evidence.reason);
            field.SetValue(_runner,null);Destroy(vision);
            ResetActor();_runner.VoiceEnabled=true;_runner.Submit("前往红绿灯，同时说一句我正在去红绿灯");yield return Wait();
            var say=_runner.Evidence.results.Find(x=>x.kind=="speak");var nav=_runner.Evidence.results.Find(x=>x.kind=="navigate");
            Record("real_speech_overlaps_one_navigation",_runner.Evidence.status=="succeeded"&&say!=null&&nav!=null&&_runner.Evidence.results.FindAll(x=>x.kind=="navigate").Count==1&&say.started<nav.ended&&say.ended>nav.started&&say.reason=="实际语音播放完成",_runner.Evidence.report);_runner.VoiceEnabled=false;
            ResetActor();_runner.Submit("前往红绿灯");float until=Time.realtimeSinceStartup+50;while(_runner.IsRunning&&!_runner.Motor.IsMoving&&Time.realtimeSinceStartup<until)yield return null;
            var before=_runner.Evidence;var proactive=typeof(ChatSession).GetField("_nextTurnIsProactive",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic);proactive.SetValue(_runner.Session,true);_runner.Session.Submit("系统触发的视觉接话");
            Record("synthetic_comment_does_not_cancel",_runner.IsRunning&&ReferenceEquals(before,_runner.Evidence),_runner.Phase);_runner.Cancel("故障验收结束");
            if(_runner.Session.VoiceInput!=null)_runner.Session.VoiceInput.SetEnabled(_voiceInput);
            _runner.UseCodeExecution=_codeModeBefore;
            Summary.status=Summary.total==Summary.passed?"passed":"failed";Running=false;Save();
        }
    }
}
