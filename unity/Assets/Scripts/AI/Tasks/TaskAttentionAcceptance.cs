using System;
using System.Collections;
using System.IO;
using UnityEngine;
using Yixuan.Vision;
using Yixuan.World;
namespace Yixuan.AI.Tasks
{
    // Explicitly started by acceptance tooling, never on ordinary scene entry.
    public class TaskAttentionAcceptance : MonoBehaviour
    {
        public string Root="Captures/Evidence/task_attention_2026-10-24";
        public bool Running; public string CurrentCase;
        public TaskAcceptanceSuite.SuiteReport Summary=new TaskAcceptanceSuite.SuiteReport();
        TaskMissionRunner r; bool oldMic,oldProactive;string oldRoot;bool oldVoice;
        public void Begin(){if(Running)return;Running=true;Summary=new TaskAcceptanceSuite.SuiteReport{status="running"};StartCoroutine(Run());}
        void Save(){Directory.CreateDirectory(Root);File.WriteAllText(Path.Combine(Root,"attention_runtime.json"),JsonUtility.ToJson(Summary,true));}
        void Check(string name,bool pass,string detail="")
        {Summary.total++;if(pass)Summary.passed++;Summary.cases.Add(new TaskAcceptanceSuite.SuiteCase{name=name,pass=pass,reason=detail});Save();}
        void Contract()
        {
            var c=r.Catalog;c.Refresh(r.Motor.transform);
            var a=MissionIntent.Bind("站在道路中间看向红绿灯",c);
            Check("bind_independent_location_and_focus",a.error==null&&a.required_actions.Length==2&&a.required_actions[0].target_id=="anchor/street_center"&&a.required_actions[1].kind=="look_at"&&a.required_actions[1].target_id!=a.required_actions[0].target_id,JsonUtility.ToJson(a));
            var b=MissionIntent.Bind("你现在站在这个位置把视角转向红绿灯",c);
            Check("bind_exact_user_utterance_no_navigation",b.error==null&&b.destination_ids.Length==0&&b.required_actions.Length==1&&b.required_actions[0].kind=="look_at",JsonUtility.ToJson(b));
            var only=MissionIntent.Bind("身体不要动，只转镜头看向红绿灯",c);
            Check("bind_camera_only_permission",only.error==null&&only.required_actions.Length==1&&only.required_actions[0].camera_only,JsonUtility.ToJson(only));
            var positive=MissionIntent.Bind("不要走过去，只看向红绿灯",c);
            Check("negated_walk_positive_look",positive.error==null&&positive.destination_ids.Length==0&&positive.required_actions.Length==1,JsonUtility.ToJson(positive));
            foreach(string text in new[]{"你现在站在这个位置把视角转向红绿灯","原地看向蓝色小轿车","站在道路中间看向红绿灯"}) Check("route_"+text,MissionIntent.LooksLikeMission(text));
            foreach(string text in new[]{"不要看向红绿灯","你现在看向红绿灯了吗","为什么不能看向红绿灯","我刚才看向红绿灯","说一句“看向红绿灯”","看向左边","看向我"}) Check("no_action_"+text,!MissionIntent.LooksLikeMission(text));
            if(only.error==null&&only.required_actions.Length==1)
            {
                var x=only.required_actions[0];var p=new MissionPlan{steps=new[]{new MissionStep{id="a",kind="look_at",target_id=x.target_id,camera_only=true},new MissionStep{id="b",kind="report",after=new[]{"a"}}}};
                Check("valid_attention_plan",MissionPlanValidator.Validate(p,only,c)==null);
                p.steps[0].camera_only=false;Check("reject_body_permission_escalation",MissionPlanValidator.Validate(p,only,c)=="attention_body_permission_changed");
                p.steps[0].kind="navigate";Check("reject_look_replaced_by_walk",MissionPlanValidator.Validate(p,only,c)!=null);
                p.steps[0].kind="look_at";p.steps[0].target_id="missing";Check("reject_missing_focus",MissionPlanValidator.Validate(p,only,c)!=null);
            }
            Check("attention_owns_camera_and_body",MissionPlanValidator.Resources("look_at")==3);
        }
        IEnumerator Mission(string name,string request,string expected="succeeded")
        {
            CurrentCase=name;r.Session.SubmitFromUI(request);float end=Time.realtimeSinceStartup+150;
            while(r.IsRunning&&Time.realtimeSinceStartup<end)yield return null;
            if(r.IsRunning)r.Cancel("注意力验收超时");
            var e=r.Evidence;bool ok=e!=null&&e.request==request&&e.status==expected;
            Summary.total++;if(ok)Summary.passed++;
            Summary.cases.Add(new TaskAcceptanceSuite.SuiteCase{name=name,request=request,pass=ok,status=e!=null?e.status:"none",reason=e!=null?e.reason:"none",directory=r.RunDirectory,llm_calls=e!=null?e.llm_calls:0,elapsed=e!=null?e.elapsed:0});Save();yield return null;
        }
        MissionStepResult Focus(){return r.Evidence.results.FindLast(x=>x.kind=="look_at");}
        bool _codeModeBefore;
        IEnumerator Run()
        {
            r=FindObjectOfType<TaskMissionRunner>();r.ResolveRefs();_codeModeBefore=r.UseCodeExecution;r.UseCodeExecution=false;oldRoot=r.EvidenceRoot;oldVoice=r.VoiceEnabled;r.EvidenceRoot=Root+"/missions";r.VoiceEnabled=false;
            if(r.Session.VoiceInput!=null){oldMic=r.Session.VoiceInput.Enabled;r.Session.VoiceInput.SetEnabled(false);}
            if(r.Session.Brain!=null){oldProactive=r.Session.Brain.EnableProactive;r.Session.Brain.EnableProactive=false;}
            Contract();
            yield return Mission("actual_user_look_at","你现在站在这个位置把视角转向红绿灯");
            var f=Focus();Check("stationary_target_and_lamp_head",f!=null&&f.status=="succeeded"&&f.position_drift<0.03f&&f.focus_source=="signal_lenses_bounds"&&f.focus_position.y>2.5f, f!=null?JsonUtility.ToJson(f):"none");
            yield return Mission("road_center_then_look","站在道路中间看向红绿灯");
            f=Focus();Check("road_and_focus_independent",r.Evidence.status=="succeeded"&&TaskTargetCatalog.FlatDistance(r.Motor.transform.position,WorldData.Anchors.Find(x=>x.Id=="street_center").Position)<r.ArrivalRadius&&f!=null&&f.target_in_frame&&f.gaze_error<r.GazeTolerance);
            yield return new WaitForSeconds(1.5f);
            Check("retained_after_task_report",r.Gimbal.State==GazeState.Tracking&&!string.IsNullOrEmpty(r.AttentionTargetId)&&Vector3.Angle(r.EyeCamera.transform.forward,f.focus_position-r.EyeCamera.transform.position)<r.GazeTolerance);
            yield return Mission("look_blue_car_without_walking","原地看向蓝色小轿车");
            f=Focus();Check("blue_car_changes_focus_not_location",r.Evidence.status=="succeeded"&&f!=null&&f.target_path.Contains("ParkedVan")&&f.position_drift<0.03f&&f.target_in_frame,f!=null?JsonUtility.ToJson(f):"none");
            // Turn the starting body away; this is test arrangement, never a production route.
            r.Motor.Stop();r.Motor.transform.rotation=Quaternion.Euler(0,270,0);r.Gimbal.Center(true);yield return null;
            yield return Mission("behind_body_assist","原地看向红绿灯");
            f=Focus();Check("body_assist_without_translation",r.Evidence.status=="succeeded"&&f!=null&&f.body_rotation>60&&f.position_drift<0.03f&&f.gaze_error<r.GazeTolerance);
            var lamp=r.Catalog.Targets.Find(x=>x.Object!=null&&x.Object.name=="TrafficLight");Vector3 point;string src;TaskAttentionFocus.Resolve(lamp,out point,out src);
            Vector3 delta=point-r.EyeCamera.transform.position;float yaw=Mathf.Atan2(delta.x,delta.z)*Mathf.Rad2Deg;
            r.Motor.transform.rotation=Quaternion.Euler(0,yaw-25,0);r.Gimbal.Center(true);yield return null;
            yield return Mission("camera_only_in_range","身体不要动，只转镜头看向红绿灯");
            f=Focus();Check("camera_only_does_not_rotate_body",r.Evidence.status=="succeeded"&&f!=null&&f.camera_only&&f.body_rotation<0.5f&&f.position_drift<0.03f);
            r.Motor.transform.rotation=Quaternion.Euler(0,yaw+180,0);r.Gimbal.Center(true);yield return null;
            yield return Mission("camera_only_out_of_range","身体不要动，只转镜头看向红绿灯","failed");
            f=Focus();Check("limited_camera_honest_failure",f!=null&&f.reason.Contains("outside_gimbal_range")&&f.body_rotation<0.5f&&f.position_drift<0.03f&&!r.Motor.IsTurning&&string.IsNullOrEmpty(r.AttentionTargetId),f!=null?f.reason:"");
            yield return Mission("unknown_target_rejected","原地看向紫色独角兽","rejected");
            r.Submit("原地看向红绿灯");yield return null;r.Cancel("测试用户取消");
            Check("cancel_releases_focus_and_turn",r.Evidence.status=="cancelled"&&!r.Motor.IsTurning&&string.IsNullOrEmpty(r.AttentionTargetId));
            // A new, unregistered-in-code target verifies live geometry and target motion.
            var cube=GameObject.CreatePrimitive(PrimitiveType.Cube);cube.name="AttentionAcceptanceCube";
            cube.transform.localScale=Vector3.one*.3f;cube.transform.position=r.Motor.transform.position+r.Motor.transform.forward*4+Vector3.up*1.5f;
            Destroy(cube.GetComponent<Collider>());var marker=cube.AddComponent<TaskDestinationMarker>();marker.Id="attention_acceptance_cube";marker.DisplayName="注意力验收目标";
            yield return null;
            yield return Mission("arbitrary_scene_target","原地看向AttentionAcceptanceCube");
            Vector3 lockedAt=r.Motor.transform.position;cube.transform.position+=r.Motor.transform.right*1.5f;
            yield return new WaitForSeconds(1.5f);
            Check("live_target_movement_tracked",r.Gimbal.State==GazeState.Tracking&&Vector3.Angle(r.EyeCamera.transform.forward,cube.transform.position-r.EyeCamera.transform.position)<r.GazeTolerance&&Vector3.Distance(lockedAt,r.Motor.transform.position)<.03f);
            Destroy(cube);yield return null;yield return null;
            Check("target_destroyed_releases_attention",string.IsNullOrEmpty(r.AttentionTargetId));WorldData.Refresh();
            yield return Mission("restore_view_demo","站在道路中间看向红绿灯");
            string held=r.AttentionTargetId;Vector3 focus=Focus().focus_position;
            Check("realtime_vision_uses_flash",r.Session.Vision.Client.Model==Yixuan.Vision.QwenVisionClient.DefaultModel,r.Session.Vision.Client.Model);
            bool oldGame=r.Session.Vision.GameViewEnabled; bool oldOutput=r.Session.Config.VoiceEnabled; bool oldInputConfig=r.Session.Config.VoiceInputEnabled;
            r.Session.Vision.SetGameView(true);r.Session.Config.VoiceEnabled=false;r.Session.Config.VoiceInputEnabled=false;
            r.Session.SubmitFromUI("你现在的游戏视角看到了什么");float queryEnd=Time.realtimeSinceStartup+75;
            while(r.Session.IsBusy&&Time.realtimeSinceStartup<queryEnd)yield return null;
            bool replied=!r.Session.IsBusy&&!string.IsNullOrWhiteSpace(r.Session.LastReply)&&!string.IsNullOrWhiteSpace(r.Session.LastVisionAnswer);
            File.WriteAllText(Path.Combine(Root,"followup_vision_answer.txt"),r.Session.LastVisionAnswer??"");
            if(r.Session.IsBusy)r.Session.CancelCurrentTurn();
            File.WriteAllText(Path.Combine(Root,"followup_vision_reply.txt"),r.Session.LastReply??"");
            r.Session.Config.VoiceEnabled=oldOutput;r.Session.Config.VoiceInputEnabled=oldInputConfig;r.Session.Vision.SetGameView(oldGame);
            Check("visual_followup_retains_target",replied&&r.AttentionTargetId==held&&r.Gimbal.State==GazeState.Tracking&&Vector3.Angle(r.EyeCamera.transform.forward,focus-r.EyeCamera.transform.position)<r.GazeTolerance,r.Session.LastReply);
            r.Session.SubmitFromUI("停止注视");yield return null;
            Check("explicit_stop_attention",string.IsNullOrEmpty(r.AttentionTargetId)&&r.Gimbal.State!=GazeState.Tracking);
            yield return Mission("restore_final_view","站在道路中间看向红绿灯");
            r.Gimbal.TurnRelative(0,0,-1,false);yield return null;yield return null;
            Check("external_camera_command_releases_lock",string.IsNullOrEmpty(r.AttentionTargetId)&&r.Gimbal.State!=GazeState.Tracking);
            yield return Mission("final_demo_ready","站在道路中间看向红绿灯");
            r.UseCodeExecution=_codeModeBefore;
            Summary.status=Summary.total==Summary.passed?"passed":"failed";CurrentCase="done";Running=false;Save();
            r.EvidenceRoot=oldRoot;r.VoiceEnabled=oldVoice;
            if(r.Session.Brain!=null)r.Session.Brain.EnableProactive=oldProactive;
            if(r.Session.VoiceInput!=null)r.Session.VoiceInput.SetEnabled(oldMic);
        }
    }
}
