using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
namespace Yixuan.AI.Tasks {
 public sealed class TaskScriptSemanticsAcceptance:MonoBehaviour {
  public string Output="D:/AI/GameScaling/docs/evidence/runtime_semantics_2026-10-25";public bool FollowupOnly;public bool Running;public string Current;public RuntimeTestReport Report;
  ChatSession session;TaskMissionRunner r;bool mic,voice,taskVoice;string evidenceRoot,wave,lamp;TaskInputMode mode;
  public void Begin(){if(Running)return;session=FindObjectOfType<ChatSession>();r=session.Missions;r.ResolveRefs();mic=session.VoiceInput.Enabled;voice=session.Config.VoiceEnabled;taskVoice=r.VoiceEnabled;mode=session.InputMode;evidenceRoot=r.EvidenceRoot;
   foreach(var g in TaskGestureSkills.Available())if((g.Label??"").Contains("挥手")){wave=g.Id;break;}
   r.Catalog.Refresh(r.Motor.transform);foreach(var t in r.Catalog.SemanticTargets())if((t.Name??"").Contains("红绿灯")){lamp=t.Id;break;}
   Report=new RuntimeTestReport{suite="resumable path scripts, semantic guards, real LLM"};Running=true;StartCoroutine(Run());
  }
  void Record(string name,bool pass,string actual,string reason=""){Report.cases.Add(new RuntimeTestResult{name=name,pass=pass,expected="true",actual=actual,reason=reason,directory=r.RunDirectory});Save();}
  void Save(){Directory.CreateDirectory(Output);File.WriteAllText(Path.Combine(Output,"acceptance_report.json"),JsonUtility.ToJson(Report,true));}
  void ResetPose(bool atLamp=false){if(r.IsRunning)r.Cancel("acceptance_reset");r.StopAttention();using(TaskAuthority.Enter(null))r.Motor.Stop();r.Motor.transform.position=atLamp?new Vector3(4.30f,r.HomePosition.y,3.90f):r.HomePosition;r.Motor.transform.rotation=Quaternion.Euler(0,r.HomeYaw,0);}
  TaskObligation O(string id,string kind,string target=""){return new TaskObligation{id=id,kind=kind,source="验收",target_id=target};}
  TaskContract Schedule(string target,bool periodic=false){var g=O("g","gesture");g.action_id=wave;g.during_target=target;if(periodic)g.every_meters=0.4f;else g.at_progress=0.5f;var a=O("a",target=="origin"?"return":"navigate",target);if(!periodic)a.after=new[]{"g"};return new TaskContract{forbid_speech=true,obligations=new[]{g,a}};}
  string Must="local function must(r) if not r.ok then error(r.reason) end return r end;";
  IEnumerator Done(float timeout=100){float end=Time.realtimeSinceStartup+timeout;while(r.IsRunning&&Time.realtimeSinceStartup<end)yield return null;if(r.IsRunning)r.Cancel("acceptance_timeout");}
  IEnumerator Fixture(string name,string code,TaskContract contract,bool success=true){Current=name;ResetPose();r.SubmitContractTest("验收",code,contract);yield return Done();Record("fixture/"+name,(r.Evidence.status=="succeeded")==success,r.Evidence.status,r.Evidence.reason);}
  IEnumerator Real(string name,string request,bool atLamp=false){Current=name;ResetPose(atLamp);r.Submit(request);yield return Done(120);Record("real/"+name,r.Evidence.status=="succeeded",r.Evidence.status,r.Evidence.reason);}
  void Capture(string name){var cam=r.EyeCamera;if(cam==null)return;Directory.CreateDirectory(Path.Combine(Output,"frames"));var old=cam.targetTexture;var active=RenderTexture.active;var rt=RenderTexture.GetTemporary(960,640,24);Texture2D tex=null;try{cam.targetTexture=rt;cam.Render();RenderTexture.active=rt;tex=new Texture2D(960,640,TextureFormat.RGB24,false);tex.ReadPixels(new Rect(0,0,960,640),0,0);tex.Apply();File.WriteAllBytes(Path.Combine(Output,"frames",name+".png"),tex.EncodeToPNG());}finally{cam.targetTexture=old;RenderTexture.active=active;RenderTexture.ReleaseTemporary(rt);if(tex!=null)Destroy(tex);}}

  IEnumerator Followup(){
   var c=Schedule(lamp);
   Record("rules/direct_navigation_cannot_bypass_schedule",TaskContractRules.Policy(c,new MissionStep{kind="navigate",target_id=lamp})!=null,"policy rejection");
   var bad=Schedule(lamp);bad.obligations[0].every_meters=-1;Record("rules/negative_schedule_rejected",TaskContractRules.Validate(bad,"验收",x=>true,x=>true)!=null,"validation rejection");
   var gaze=O("g","return","origin");gaze.source="看向原点";Record("rules/gaze_return_misclassification_rejected",TaskContractRules.Validate(new TaskContract{obligations=new[]{gaze}},"看向原点",x=>true,x=>true)!=null,"validation rejection");
   var vm=new TaskLuaRuntime("local t={};table.insert(t,string.upper('lua'));return {ok=t[1]=='LUA'}",x=>new MoonSharp.Interpreter.Table(x),x=>x);var value=vm.Resume();Record("runtime/standard_string_and_table_libraries",value.Type==MoonSharp.Interpreter.DataType.Table&&value.Table.Get("ok").Boolean,"native Lua libraries");
   yield return Fixture("scheduled_path_cannot_restart_to_skip_steps",Must+"local p=must(game.follow_route('"+lamp+"'));must(game.follow_route('"+lamp+"',{distance=0.4}));must(game.gesture('"+wave+"'));return {ok=true}",Schedule(lamp,true),false);
   yield return Fixture("direct_navigate_cannot_fake_midpoint",Must+"must(game.navigate('"+lamp+"'));must(game.gesture('"+wave+"'));return {ok=true}",Schedule(lamp),false);
   var speak=O("s","speak");speak.text="测试";ResetPose();r.SubmitContractTest("验收",Must+"must(game.speak('测试'));return {ok=true}",new TaskContract{obligations=new[]{speak}});yield return Done();Record("runtime/speech_disabled_rejected_before_movement",r.Evidence.status=="rejected"&&r.Evidence.results.Count==0,r.Evidence.reason);
   yield return Real("final_midpoint_regression","请去红绿灯，半路停下挥一次手，再继续到终点。全程不要说话。");
   yield return Real("final_periodic_regression","前往红绿灯，每走一步做一下挥手动作，直到到达。全程不要说话。");
   yield return Real("final_origin_gaze_no_negative_hint","请你看一下原点。",true);Record("real/gaze_does_not_need_explicit_no_move",r.Evidence.results.Exists(x=>x.kind=="look_at"&&x.target_id=="origin"&&x.status=="succeeded")&&!r.Evidence.results.Exists(x=>x.kind=="return"||x.kind=="navigate"),r.LastProgram);
   ResetPose();Current="unreachable_store_original";r.Submit("请走向商店门口，每走一步停下来挥一下手。全程不要说话。");yield return Done();Record("real/store_door_preserves_unreachable",r.Evidence.status=="failed"&&!r.Evidence.results.Exists(x=>x.kind=="navigate"&&x.status=="succeeded")&&(r.Evidence.reason.Contains("不可达")||r.Evidence.reason.Contains("unreachable")),r.Evidence.status,r.Evidence.reason);
   ResetPose();Current="route_target_moves";r.SubmitContractTest("验收",Must+"must(game.follow_route('"+lamp+"',{to_fraction=0.8}));return {ok=true}",new TaskContract{forbid_speech=true,obligations=new[]{O("a","navigate",lamp)}});
   float end=Time.realtimeSinceStartup+10;while((!r.Motor.HasTarget||TaskTargetCatalog.FlatDistance(r.Motor.transform.position,r.HomePosition)<0.2f)&&r.IsRunning&&Time.realtimeSinceStartup<end)yield return null;
   var target=r.Catalog.Get(lamp).Object;var old=target.position;target.position+=Vector3.right;try{yield return Done();Record("runtime/moving_target_cancels_route",r.Evidence.status=="failed"&&r.Evidence.reason.Contains("route_target_moved"),r.Evidence.status,r.Evidence.reason);}finally{target.position=old;}
   var unit=TaskRuntimeUnitAcceptance.Run(Path.Combine(Output,"unit_final"));Record("regression/final_previous_units",unit.cases.TrueForAll(x=>x.pass),unit.cases.Count.ToString());
  }
  IEnumerator Run(){try{
   session.VoiceInput.SetEnabled(false);session.SetVoiceOutputEnabled(false);r.VoiceEnabled=false;session.InputMode=TaskInputMode.Action;r.EvidenceRoot=Path.Combine(Output,"missions");
   if(FollowupOnly){yield return Followup();yield break;}
   var unit=TaskRuntimeUnitAcceptance.Run(Path.Combine(Output,"unit"));Record("regression/previous_unit_suite",unit.cases.TrueForAll(x=>x.pass),unit.cases.Count.ToString());
   var c=Schedule(lamp);
   yield return Fixture("midpoint_wave_resume",Must+"local p=must(game.follow_route('"+lamp+"',{to_fraction=0.5}));must(game.gesture('"+wave+"'));must(game.follow_route(p.route_id));return {ok=true}",c);
   var mid=r.Evidence.results.Find(x=>x.kind=="gesture");Record("fixture/action_has_halfway_evidence",mid!=null&&Mathf.Abs(mid.route_fraction-0.5f)<0.005f,mid==null?"none":mid.route_fraction.ToString());Capture("midpoint_completed");
   yield return Fixture("arrive_then_wave_rejected",Must+"local p=must(game.follow_route('"+lamp+"'));must(game.gesture('"+wave+"'));return {ok=true}",Schedule(lamp),false);
   yield return Fixture("partial_route_is_not_arrival",Must+"must(game.follow_route('"+lamp+"',{to_fraction=0.5}));return {ok=true}",new TaskContract{forbid_speech=true,obligations=new[]{O("a","navigate",lamp)}},false);
   yield return Fixture("each_step_wave",Must+"local p=must(game.follow_route('"+lamp+"',{distance=0.4}));must(game.gesture('"+wave+"'));while not p.done do p=must(game.follow_route(p.route_id,{distance=0.4}));must(game.gesture('"+wave+"')) end;return {ok=true}",Schedule(lamp,true));
   yield return Fixture("periodic_missing_waves_rejected",Must+"local p=must(game.follow_route('"+lamp+"',{distance=0.4}));while not p.done do p=must(game.follow_route(p.route_id,{distance=0.4})) end;must(game.gesture('"+wave+"'));return {ok=true}",Schedule(lamp,true),false);
   yield return Fixture("stale_handle_rejected",Must+"must(game.follow_route('route_missing'));return {ok=true}",new TaskContract{forbid_speech=true,obligations=new[]{O("a","navigate",lamp)}},false);
   // A moved actor may not silently resume a cached geometric route.
   var a=O("a","navigate",lamp);var m=O("m","move_relative");m.direction="right";m.distance=0.1f;
   yield return Fixture("displaced_route_rejected",Must+"local p=must(game.follow_route('"+lamp+"',{to_fraction=0.5}));must(game.move_relative('right',{distance=0.1}));must(game.follow_route(p.route_id));return {ok=true}",new TaskContract{forbid_speech=true,obligations=new[]{a,m}},false);
   // Runtime insertion during a path segment retains the route and the parent VM.
   ResetPose();Current="insertion_during_route";r.SubmitContractTest("验收",Must+"local p=must(game.follow_route('"+lamp+"',{to_fraction=0.5}));must(game.gesture('"+wave+"'));must(game.follow_route(p.route_id));return {ok=true}",Schedule(lamp));
   float end=Time.realtimeSinceStartup+15;while((!r.Motor.HasTarget||TaskTargetCatalog.FlatDistance(r.Motor.transform.position,r.HomePosition)<0.2f)&&r.IsRunning&&Time.realtimeSinceStartup<end)yield return null;
   string parent=r.Context.MissionId;var g=O("g","gesture");g.action_id=wave;bool accepted=r.InsertContractForTest("验收",Must+"must(game.gesture('"+wave+"'));return {ok=true}",new TaskContract{forbid_speech=true,obligations=new[]{g}});yield return Done();Record("fixture/insertion_retains_route_and_VM",accepted&&r.Context.MissionId==parent&&r.Evidence.status=="succeeded"&&r.Evidence.insertions.Count==1,r.Evidence.status,r.Evidence.reason);
   yield return Real("midpoint_original_request","请走到红绿灯底下，在这中间停一次，做一次招手的动作，然后再继续走。全程不要说话。");
   var gs=r.Evidence.results.Find(x=>x.kind=="gesture");Record("real/midpoint_before_arrival",gs!=null&&gs.route_fraction>0.05f&&gs.route_fraction<0.95f,gs==null?"none":gs.route_fraction.ToString());
   yield return Real("each_step","请走到红绿灯，每走一步就停下做一次挥手的动作，然后继续，直到到达。全程不要说话。");
   yield return Real("look_at_origin","请你看向原点，不要移动。",true);Record("real/origin_gaze_never_translates",!r.Evidence.results.Exists(x=>x.kind=="navigate"||x.kind=="return"||x.kind=="move_relative")&&r.Evidence.results.Exists(x=>x.kind=="look_at"&&x.status=="succeeded"),r.Evidence.status,r.LastProgram);Capture("look_at_origin");
   yield return Real("ordinary_gesture_then_walk","请挥一下手，然后走到红绿灯。全程不要说话。");
   // Explicit speech exercises the real synthesis/playback service, not a mocked receipt.
   session.SetVoiceOutputEnabled(true);r.VoiceEnabled=true;
   yield return Real("midpoint_poem_real_TTS","请从这里走回原点，走到一半停下，背一句古诗，再走完剩余的路。",true);
   Record("real/poem_audio_finished_at_midpoint",r.Evidence.results.Exists(x=>x.kind=="speak"&&x.status=="succeeded"&&Mathf.Abs(x.route_fraction-0.5f)<0.005f),r.Evidence.status,r.Evidence.reason);
  }finally{if(r.IsRunning)r.Cancel("acceptance_end");session.CancelDialogueTurn();session.VoiceInput.SetEnabled(mic);session.SetVoiceOutputEnabled(voice);r.VoiceEnabled=taskVoice;session.InputMode=mode;r.EvidenceRoot=evidenceRoot;Running=false;Current="done";Report.done=true;Save();}}
 }
}
