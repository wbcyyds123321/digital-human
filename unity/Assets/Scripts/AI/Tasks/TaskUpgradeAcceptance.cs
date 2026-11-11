using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using Yixuan.World;
namespace Yixuan.AI.Tasks
{
    // Explicitly launched; no automatic test execution in normal Play.
    public class TaskUpgradeAcceptance : MonoBehaviour
    {
        public string Root="D:/AI/GameScaling/docs/evidence/task_upgrade_2026-10-24";
        public bool Running; public string CurrentCase;
        public TaskAcceptanceSuite.SuiteReport Summary=new TaskAcceptanceSuite.SuiteReport();
        TaskMissionRunner r; bool oldMic;
        public void Begin() {if(Running)return;Running=true;Summary=new TaskAcceptanceSuite.SuiteReport{status="running"};StartCoroutine(Run());}
        void Save(){Directory.CreateDirectory(Root);File.WriteAllText(Path.Combine(Root,"upgrade_runtime.json"),JsonUtility.ToJson(Summary,true));}
        void Add(string name,bool pass,string detail)
        {Summary.total++;if(pass)Summary.passed++;Summary.cases.Add(new TaskAcceptanceSuite.SuiteCase{name=name,pass=pass,reason=detail});Save();}
        IEnumerator Mission(string name,string request,string expected="succeeded")
        {
            CurrentCase=name;r.EvidenceRoot=Root+"/missions";string previous=r.RunDirectory;
            r.Session.SubmitFromUI(request);
            float end=Time.realtimeSinceStartup+180;
            while(r.IsRunning&&Time.realtimeSinceStartup<end)yield return null;
            if(r.IsRunning)r.Cancel("升级测试超时");
            var e=r.Evidence;bool ok=e!=null&&r.RunDirectory!=previous&&e.request==request&&e.status==expected;
            Summary.total++;if(ok)Summary.passed++;
            Summary.cases.Add(new TaskAcceptanceSuite.SuiteCase{name=name,request=request,pass=ok,status=e!=null?e.status:"none",reason=e!=null?e.reason:"no mission",directory=r.RunDirectory,elapsed=e!=null?e.elapsed:0,llm_calls=e!=null?e.llm_calls:0});Save();yield return null;
        }
        bool _codeModeBefore;
        IEnumerator Run()
        {
            r=FindObjectOfType<TaskMissionRunner>();r.ResolveRefs();_codeModeBefore=r.UseCodeExecution;r.UseCodeExecution=false;r.VoiceEnabled=false;
            if(r.Session.VoiceInput!=null){oldMic=r.Session.VoiceInput.Enabled;r.Session.VoiceInput.SetEnabled(false);}
            Vector3 home=r.HomePosition;
            yield return Mission("setup_away_from_origin","前往红绿灯");
            yield return Mission("user_exact_failed_asr","先走回原点然后在原点处挥舞双手然后再走回红绿灯这里");
            var e=r.Evidence;var ret=e.results.Find(x=>x.kind=="return");var gesture=e.results.Find(x=>x.kind=="gesture");var nav=e.results.Find(x=>x.kind=="navigate");
            var both=TaskGestureSkills.Match("挥舞双手");var entry=TaskGestureSkills.Get(both);
            Add("actual_two_hand_animation_and_dependency",e.status=="succeeded"&&ret!=null&&gesture!=null&&nav!=null&&gesture.action_id==both&&entry!=null&&gesture.clip_name==entry.ClipName&&gesture.ended-gesture.started>=entry.Duration*0.9f&&nav.started>=gesture.ended&&gesture.started>=ret.ended&&TaskTargetCatalog.FlatDistance(ret.end_position,home)<=r.ArrivalRadius,JsonUtility.ToJson(e));
            yield return Mission("user_punctuated_paraphrase","让她先走回原点，然后做一个挥舞双手的动作，然后再走回红绿灯之下");
            yield return Mission("return_starting_position","回到起始位置");
            Add("stable_scene_origin_not_current_position",r.Evidence.status=="succeeded"&&TaskTargetCatalog.FlatDistance(r.Motor.transform.position,home)<=r.ArrivalRadius&&TaskTargetCatalog.FlatDistance(r.HomePosition,home)<.001f, r.Motor.transform.position.ToString());
            yield return Mission("blue_car_chinese","去蓝色小轿车那里");
            var van=r.Evidence.results.Find(x=>x.kind=="navigate");
            Add("blue_car_is_real_parked_van_not_blue_fixture",r.Evidence.status=="succeeded"&&van!=null&&van.target_path.Contains("ParkedVan")&&!van.target_id.Contains("place_2"),van!=null?van.target_path:"no nav");
            yield return Mission("bicycle_chinese","请走到自行车旁边");
            yield return Mission("streetlamp_chinese","前往路灯附近");
            Vector3 missionStart=r.Motor.transform.position;
            yield return Mission("explicit_current_mission_origin","先去蓝色汽车那里，然后回到本次任务起点");
            Add("mission_origin_is_separate_from_scene_home",r.Evidence.status=="succeeded"&&TaskTargetCatalog.FlatDistance(missionStart,r.Motor.transform.position)<=r.ArrivalRadius&&TaskTargetCatalog.FlatDistance(r.Motor.transform.position,home)>1,JsonUtility.ToJson(r.Evidence));
            yield return Mission("ambiguous_vending_machine","前往自动售货机","rejected");
            yield return Mission("wrong_color_not_silently_substituted","前往紫色小轿车","rejected");
            yield return Mission("unknown_object_not_silently_substituted","前往NeverExistsXYZ","rejected");
            yield return Mission("uninstalled_action_not_omitted","先回原点，然后做一个后空翻动作，再去红绿灯","rejected");
            yield return Mission("negated_navigation_not_executed","请不要前往红绿灯，然后前往绿色柱子");
            Add("negative_target_omitted",r.Evidence.status=="succeeded"&&r.Evidence.intent.destination_ids.Length==1&&r.Evidence.intent.destination_ids[0]=="place_1",JsonUtility.ToJson(r.Evidence.intent));
            yield return Mission("actual_four_view_vision","前往绿色柱子，观察一下四周，然后回到原点");
            var obs=r.Evidence.results.Find(x=>x.kind=="observe");
            Add("semantic_vision_has_four_real_frames",r.Evidence.status=="succeeded"&&obs!=null&&!string.IsNullOrEmpty(obs.observation)&&Directory.GetFiles(r.RunDirectory,"*.jpg").Length==4,obs!=null?obs.observation:"none");
            yield return Mission("full_old_workflow","去1号地点观察一下四周，然后去2号地点触发一个游戏脚本交互，最后回到原点");
            CurrentCase="cancel_during_gesture";r.Submit("先挥舞双手，然后前往红绿灯");
            float until=Time.realtimeSinceStartup+60;
            while(r.IsRunning&&(r.Evidence.results.Find(x=>x.kind=="gesture"&&x.status=="running")==null)&&Time.realtimeSinceStartup<until)yield return null;
            bool during=r.IsRunning&&r.Phase.Contains("gesture");r.Cancel("升级验收：动作中取消");Vector3 stop=r.Motor.transform.position;yield return new WaitForSeconds(.4f);
            Add("cancel_gesture_no_followup_navigation",during&&r.Evidence.status=="cancelled"&&!r.Motor.HasTarget&&TaskTargetCatalog.FlatDistance(stop,r.Motor.transform.position)<.01f,r.Evidence.reason);
            var panel=FindObjectOfType<TaskMissionPanel>();panel.Visible=true;yield return null;yield return null;
            var ui=FindObjectOfType<Yixuan.Vision.VisionControlUI>();
            Add("no_duplicate_vision_panel_overlap",ui!=null&&!ui.LinkPanelActive, "legacy VisionPanel hidden, webcam preview retained");
            panel.Visible=false;yield return null;yield return null;Add("collapse_restores_legacy_switches",ui!=null&&ui.LinkPanelActive,"restored legacy controls");panel.Visible=true;
            foreach(var query in new[]{"你都可以走向哪些物体呀","你觉得你走到红绿灯那里了吗","为什么回到原点没有执行","你现在在哪"}) Add("query_not_navigation_"+query,!MissionIntent.LooksLikeMission(query),query);
            foreach(var word in new[]{"回原点","走回原点","回到起始位置","返回起始点"}) Add("origin_route_"+word,MissionIntent.LooksLikeMission(word),word);
            if(r.Session.VoiceInput!=null)r.Session.VoiceInput.SetEnabled(oldMic);
            r.UseCodeExecution=_codeModeBefore;
            Summary.status=Summary.passed==Summary.total?"passed":"failed";CurrentCase="done";Running=false;Save();
        }
    }
}
