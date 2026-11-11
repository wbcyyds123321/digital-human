using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using Yixuan.World;

namespace Yixuan.AI.Tasks
{
    /// <summary>Explicitly launched runtime acceptance; never starts automatically.</summary>
    public class TaskAcceptanceSuite : MonoBehaviour
    {
        public string Root = "D:/AI/GameScaling/docs/evidence/task_orchestration_2026-10-24";
        public bool Running { get; private set; }
        public string CurrentCase { get; private set; }
        public SuiteReport Summary = new SuiteReport();
        TaskMissionRunner _runner;
        Vector3 _start;
        bool _restoreVoiceInput;
        [Serializable] public class SuiteReport { public string status; public int passed,total; public List<SuiteCase> cases=new List<SuiteCase>(); }
        [Serializable] public class SuiteCase { public string name,request,status,reason,directory; public bool pass; public float elapsed,movement_seconds,path_length; public int llm_calls; }
        public void Begin()
        {
            if(Running)return;Running=true;Summary=new SuiteReport{status="running"};StartCoroutine(Run());
        }
        void Save(){Directory.CreateDirectory(Root);File.WriteAllText(Path.Combine(Root,"runtime_suite.json"),JsonUtility.ToJson(Summary,true));}
        void Add(string name,bool pass,string reason)
        {Summary.total++;if(pass)Summary.passed++;Summary.cases.Add(new SuiteCase{name=name,pass=pass,reason=reason});Save();}
        void ResetActor()
        {
            if(_runner.IsRunning)_runner.Cancel("下一验收用例");
            _runner.Motor.Stop("验收重置");_runner.Motor.SetWalkingEnabled(true);
            _runner.Motor.transform.position=_start;_runner.Motor.transform.rotation=Quaternion.identity;
            if(_runner.Gimbal!=null)_runner.Gimbal.Center(true);
            var device=GameObject.Find("GSTarget_place_2");if(device!=null&&device.GetComponent<TaskTestSwitch>()!=null)device.GetComponent<TaskTestSwitch>().ResetFixture();
            WorldData.Refresh();
        }
        IEnumerator Mission(string name,string request,string expected="succeeded")
        {
            CurrentCase=name;ResetActor();_runner.EvidenceRoot=Root+"/missions";
            _runner.Session.SubmitFromUI(request);
            float until=Time.realtimeSinceStartup+450f;
            while(_runner.IsRunning&&Time.realtimeSinceStartup<until)yield return null;
            if(_runner.IsRunning)_runner.Cancel("测试超时");
            var e=_runner.Evidence;
            bool pass=e!=null&&e.request==request&&e.status==expected;
            if(name=="full_observe_interact_return")
            {
                var sw=GameObject.Find("GSTarget_place_2").GetComponent<TaskTestSwitch>();
                pass&=sw.Activated&&e.results.FindAll(x=>x.kind=="observe"&&x.status=="succeeded"&&!string.IsNullOrEmpty(x.observation)).Count==1;
            }
            if(name=="actual_parallel_speech")
            { var speech=e.results.Find(x=>x.kind=="speak");var nav=e.results.Find(x=>x.kind=="navigate");pass&=speech!=null&&nav!=null&&e.results.FindAll(x=>x.kind=="navigate").Count==1&&speech.status=="succeeded"&&speech.reason=="实际语音播放完成"&&speech.started<nav.ended&&speech.ended>nav.started; }
            if(name=="parallel_expression")
            {
                var nav=e.results.Find(x=>x.kind=="navigate");var expression=e.results.Find(x=>x.kind=="express");
                pass&=nav!=null&&expression!=null&&expression.started<nav.ended&&expression.ended>nav.started;
            }
            Summary.total++;if(pass)Summary.passed++;
            Summary.cases.Add(new SuiteCase{name=name,request=request,status=e!=null?e.status:"no_evidence",pass=pass,reason=e!=null?e.reason:"",directory=_runner.RunDirectory,
                elapsed=e!=null?e.elapsed:0,movement_seconds=e!=null?e.movement_seconds:0,path_length=e!=null?e.actual_path_length:0,llm_calls=e!=null?e.llm_calls:0});Save();yield return null;
        }
        bool _codeModeBefore;
        IEnumerator Run()
        {
            _runner=FindObjectOfType<ChatSession>().Missions;_runner.ResolveRefs();_codeModeBefore=_runner.UseCodeExecution;_runner.UseCodeExecution=false;
            _start=new Vector3(-2.4f,_runner.Motor.transform.position.y,3.8f);_runner.VoiceEnabled=false;
            if (_runner.Session.VoiceInput != null) { _restoreVoiceInput = _runner.Session.VoiceInput.Enabled; _runner.Session.VoiceInput.SetEnabled(false); }
            yield return Mission("traffic_light_english","前往TrafficLight");
            yield return Mission("traffic_light_chinese","前往红绿灯");
            yield return Mission("other_real_object","前往GSTestWall");
            for(int n=1;n<=3;n++)yield return Mission("navigation_comparison_"+n,"前往绿色柱子，然后前往蓝色装置，最后回到起点");
            yield return Mission("single_blue_comparison","前往蓝色装置");
            yield return Mission("full_observe_interact_return","去1号地点观察一下四周，然后去2号地点触发一个游戏脚本交互，最后回到原点");
            yield return Mission("parallel_expression","前往红绿灯，同时保持开心的表情三秒");
            yield return Mission("unknown_target","前往NeverExistsXYZ","rejected");
            ResetActor();_runner.Motor.SetWalkingEnabled(false);
            _runner.Submit("前往TrafficLight");while(_runner.IsRunning)yield return null;
            Add("walking_disabled",_runner.Evidence.status=="failed"&&TaskTargetCatalog.FlatDistance(_runner.Motor.transform.position,_start)<0.01f,_runner.Evidence.reason);_runner.Motor.SetWalkingEnabled(true);
            yield return Mission("unregistered_interaction","前往TrafficLight并触发交互","rejected");
            CurrentCase="cancel_during_navigation";ResetActor();_runner.Submit("前往TrafficLight");
            float deadline=Time.realtimeSinceStartup+50f;
            while(_runner.IsRunning&&!_runner.Motor.IsMoving&&Time.realtimeSinceStartup<deadline)yield return null;
            bool moving=_runner.Motor.IsMoving;_runner.Cancel("验收：执行中取消");Vector3 stopped=_runner.Motor.transform.position;
            float wait=Time.realtimeSinceStartup+0.4f;while(Time.realtimeSinceStartup<wait)yield return null;
            Add("cancel_during_navigation",moving&&_runner.Evidence.status=="cancelled"&&!_runner.Motor.HasTarget&&!_runner.Motor.IsTurning&&TaskTargetCatalog.FlatDistance(stopped,_runner.Motor.transform.position)<0.01f,_runner.Evidence.reason);
            CurrentCase="new_input_replaces_mission";ResetActor();_runner.Submit("前往TrafficLight");
            deadline=Time.realtimeSinceStartup+50f;while(_runner.IsRunning&&!_runner.Motor.IsMoving&&Time.realtimeSinceStartup<deadline)yield return null;
            string oldDir=_runner.RunDirectory;_runner.Session.SubmitFromUI("前往绿色柱子");while(_runner.IsRunning)yield return null;
            var old=JsonUtility.FromJson<MissionEvidence>(File.ReadAllText(Path.Combine(oldDir,"evidence.json")));
            Add("new_input_replaces_mission",old.status=="cancelled"&&_runner.Evidence.status=="succeeded"&&_runner.Evidence.intent.destination_ids[0]=="place_1",old.status+" → "+_runner.Evidence.status);
            CurrentCase="disable_component_cleanup";ResetActor();float radius=_runner.Motor.ArriveRadius;
            _runner.Submit("前往TrafficLight");deadline=Time.realtimeSinceStartup+50f;
            while(_runner.IsRunning&&!_runner.Motor.IsMoving&&Time.realtimeSinceStartup<deadline)yield return null;
            moving=_runner.Motor.IsMoving;_runner.enabled=false;stopped=_runner.Motor.transform.position;
            yield return new WaitForSeconds(0.3f);
            Add("disable_component_cleanup",moving&&_runner.Evidence.status=="cancelled"&&!_runner.Motor.HasTarget&&!_runner.Motor.IsTurning&&Mathf.Abs(_runner.Motor.ArriveRadius-radius)<0.0001f&&TaskTargetCatalog.FlatDistance(stopped,_runner.Motor.transform.position)<0.01f,_runner.Evidence.reason);_runner.enabled=true;
            yield return Mission("speech_disabled_explicit_failure","前往绿色柱子，同时说一句你好","failed");
            _runner.VoiceEnabled=true;
            yield return Mission("actual_parallel_speech","前往红绿灯，同时说一句我正在去红绿灯");
            _runner.VoiceEnabled=false;
            CurrentCase="dynamic_object_coordinates";ResetActor();
            var beacon=GameObject.CreatePrimitive(PrimitiveType.Cube);beacon.name="DynamicAcceptanceBeacon";beacon.transform.position=new Vector3(1,WorldData.GroundY+0.5f,6.5f);beacon.transform.localScale=new Vector3(0.3f,1,0.3f);
            Destroy(beacon.GetComponent<Collider>());yield return null;
            yield return Mission("dynamic_beacon_first","前往DynamicAcceptanceBeacon");
            Vector3 first=_runner.Evidence.results[0].approach_position;
            beacon.transform.position=new Vector3(4,WorldData.GroundY+0.5f,6.5f);
            yield return Mission("dynamic_beacon_moved","前往DynamicAcceptanceBeacon");
            Vector3 second=_runner.Evidence.results[0].approach_position;
            Add("approach_follows_live_coordinates",TaskTargetCatalog.FlatDistance(first,second)>1.5f,first+" → "+second);
            beacon.transform.position=new Vector3(100,1,100);
            yield return Mission("unreachable_target","前往DynamicAcceptanceBeacon","failed");
            Destroy(beacon);yield return null;WorldData.Refresh();
            if (_runner.Session.VoiceInput != null) _runner.Session.VoiceInput.SetEnabled(_restoreVoiceInput);
            _runner.UseCodeExecution=_codeModeBefore;
            Summary.status=Summary.passed==Summary.total?"passed":"failed";Running=false;CurrentCase="done";Save();
        }
    }
}
