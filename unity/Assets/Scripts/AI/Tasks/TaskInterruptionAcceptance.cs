using System;
using System.Collections;
using System.IO;
using System.Collections.Generic;
using UnityEngine;
namespace Yixuan.AI.Tasks {
 // Explicit harness: deterministic runtime tests and real user/LLM tests are reported separately.
 public sealed class TaskInterruptionAcceptance:MonoBehaviour {
  public string Output="D:/AI/GameScaling/docs/evidence/interrupt_pose_fix_2026-10-25";
  public bool FollowupOnly;
  public bool Running; public string Current="";public RuntimeTestReport Report;
  ChatSession s;TaskMissionRunner r;Vector3 initial;float initialYaw;bool mic,voice,taskVoice;string evidenceRoot;TaskInputMode mode;float codeTimeout;
  string wave,clip;
  public void Begin(){if(Running)return;s=FindObjectOfType<ChatSession>();r=s.Missions;r.ResolveRefs();initial=r.HomePosition;initialYaw=r.HomeYaw;mic=s.VoiceInput.Enabled;voice=s.Config.VoiceEnabled;taskVoice=r.VoiceEnabled;evidenceRoot=r.EvidenceRoot;mode=s.InputMode;codeTimeout=r.CodeTimeout;
   foreach(var g in TaskGestureSkills.Available())if((g.Label??"").Contains("双手")){wave=g.Id;clip=g.ClipName;break;}
   if(wave==null)foreach(var g in TaskGestureSkills.Available())if((g.Label??"").Contains("挥手")){wave=g.Id;clip=g.ClipName;break;}
   Report=new RuntimeTestReport{suite="task interruption, animation authority, body turn: fixtures + real LLM"};Running=true;StartCoroutine(Run());
  }
  void Record(string n,bool ok,string expected,string actual,string reason="",string dir=""){Report.cases.Add(new RuntimeTestResult{name=n,pass=ok,expected=expected,actual=actual,reason=reason,directory=dir});Save();}
  void Save(){Directory.CreateDirectory(Output);File.WriteAllText(Path.Combine(Output,"acceptance_report.json"),JsonUtility.ToJson(Report,true));}
  void ResetPose(){if(r.IsRunning)r.Cancel("acceptance_reset");r.StopAttention();using(TaskAuthority.Enter(null))r.Motor.Stop();r.Motor.transform.position=initial;r.Motor.transform.rotation=Quaternion.Euler(0,initialYaw,0);}
  void Capture(string name){Directory.CreateDirectory(Path.Combine(Output,"frames"));foreach(var c in new[]{Camera.main,r.EyeCamera}){if(c==null)continue;var old=c.targetTexture;var active=RenderTexture.active;var rt=RenderTexture.GetTemporary(960,640,24);Texture2D tex=null;try{c.targetTexture=rt;c.Render();RenderTexture.active=rt;tex=new Texture2D(960,640,TextureFormat.RGB24,false);tex.ReadPixels(new Rect(0,0,960,640),0,0);tex.Apply();File.WriteAllBytes(Path.Combine(Output,"frames",name+(c==r.EyeCamera?"_eye":"_main")+".png"),tex.EncodeToPNG());}finally{c.targetTexture=old;RenderTexture.active=active;RenderTexture.ReleaseTemporary(rt);if(tex!=null)Destroy(tex);}}}
  TaskObligation O(string id,string kind){return new TaskObligation{id=id,source="验收",kind=kind};}
  TaskContract C(params TaskObligation[] obligations){return new TaskContract{forbid_speech=true,obligations=obligations};}
  TaskContract WaveContract(){var o=O("g","gesture");o.action_id=wave;return C(o);}
  TaskContract WaitContract(float duration){var o=O("w","wait");o.min_duration=duration;return C(o);}
  TaskContract LoopContract(int count=3){var a=O("r","move_relative");a.direction="right";a.distance=0.4f;a.count=count;var b=O("l","move_relative");b.direction="left";b.distance=0.4f;b.count=count;return C(a,b);}
  string Loop(int n=3){return "for i=1,"+n+" do local a=game.move_relative('right',{distance=0.4,timeout=2});if not a.ok then return {ok=false,reason=a.reason} end;local b=game.move_relative('left',{distance=0.4,timeout=2});if not b.ok then return {ok=false,reason=b.reason} end end;return {ok=true}";}
  IEnumerator WaitDone(float seconds=60){float end=Time.realtimeSinceStartup+seconds;while(r.IsRunning&&Time.realtimeSinceStartup<end)yield return null;if(r.IsRunning)r.Cancel("acceptance_timeout");}
  IEnumerator WaitMoving(float seconds=65){float end=Time.realtimeSinceStartup+seconds;while((!r.IsRunning||!r.Motor.HasTarget||TaskTargetCatalog.FlatDistance(r.Motor.transform.position,initial)<0.06f)&&Time.realtimeSinceStartup<end)yield return null;}
  IEnumerator Fixture(string name,string source,TaskContract contract,bool expected){Current=name;ResetPose();r.SubmitContractTest("验收："+name,source,contract);yield return WaitDone();Record("fixture/"+name,(r.Evidence.status=="succeeded")==expected,expected?"succeeded":"not succeeded",r.Evidence.status,r.Evidence.reason,r.RunDirectory);}
  IEnumerator ContractLoops(){
   var a=O("a","navigate");a.target_id="test_target";a.count=3;a.repeat_group="trip";
   var b=O("b","return");b.target_id="origin";b.count=3;b.repeat_group="trip";b.after=new[]{"a"};
   var c=C(a,b);var receipts=new List<MissionStepResult>();
   for(int i=0;i<3;i++){receipts.Add(new MissionStepResult{id="a"+i,kind="navigate",target_id="test_target",status="succeeded",started=i*4,ended=i*4+1});receipts.Add(new MissionStepResult{id="b"+i,kind="return",target_id="origin",status="succeeded",started=i*4+2,ended=i*4+3});}
   Record("contract/alternating_loop_succeeds",TaskContractRules.Evaluate(c,receipts,true,20).task_succeeded,"three interleaved pairs satisfy grouped contract",TaskContractRules.Evaluate(c,receipts,true,20).reason);
   var invalid=new List<MissionStepResult>();for(int i=0;i<3;i++)invalid.Add(new MissionStepResult{id="a"+i,kind="navigate",target_id="test_target",status="succeeded",started=i*2,ended=i*2+1});for(int i=0;i<3;i++)invalid.Add(new MissionStepResult{id="b"+i,kind="return",target_id="origin",status="succeeded",started=10+i*2,ended=11+i*2});
   Record("contract/group_rejects_all_out_then_all_back",!TaskContractRules.Evaluate(c,invalid,true,30).task_succeeded,"wrong occurrence order rejected",TaskContractRules.Evaluate(c,invalid,true,30).reason);
   receipts.RemoveAt(receipts.Count-1);Record("contract/missing_last_return_fails",!TaskContractRules.Evaluate(c,receipts,true,20).task_succeeded,"2 of 3 returns is incomplete",TaskContractRules.Evaluate(c,receipts,true,20).reason);
   a.repeat_group="";b.repeat_group="";Record("contract/ambiguous_repeated_dependency_rejected",TaskContractRules.Validate(c,"验收",x=>true,x=>true)=="repeated_sequence_requires_repeat_group","ambiguous repeated after requires explicit group",TaskContractRules.Validate(c,"验收",x=>true,x=>true));
   b.source="最后回程";Record("contract/explicit_all_occurrences_barrier_preserved",TaskContractRules.Evaluate(c,invalid,true,30).task_succeeded,"ungrouped after still means all previous occurrences",TaskContractRules.Evaluate(c,invalid,true,30).reason);
   yield return null;
  }
  IEnumerator PoseTest(){
   Current="gesture_end_while_body_leased";ResetPose();var g=WaveContract().obligations[0];var w=WaitContract(1.5f).obligations[0];w.after=new[]{"g"};
   r.SubmitContractTest("验收：挥手后保持待机","game.gesture('"+wave+"');game.wait(1.5);return {ok=true}",C(g,w));
   float end=Time.realtimeSinceStartup+12;while(r.IsRunning&&!r.Evidence.results.Exists(x=>x.kind=="wait")&&Time.realtimeSinceStartup<end)yield return null;
   bool realIdle=r.IsRunning&&s.Character.AnimPlayer.IsPlaying&&s.Character.AnimPlayer.CurrentName!=clip&&s.Character.Idle!=null&&!s.Character.Idle.Enabled;
   Record("pose/real_idle_while_body_leased",realIdle,"manifest idle playing, procedural idle off, mission still owns body",s.Character.AnimPlayer.CurrentName+"; procedural="+s.Character.Idle.Enabled+"; running="+r.IsRunning);
   Capture("idle_with_body_lease");yield return WaitDone();yield return new WaitForSecondsRealtime(0.3f);Capture("idle_after_gesture");
   Record("pose/idle_persists_after_finish",s.Character.AnimPlayer.IsPlaying&&!s.Character.Idle.Enabled,"external idle persists",s.Character.AnimPlayer.CurrentName+"; procedural="+s.Character.Idle.Enabled);
  }
  IEnumerator InsertionFixture(){
   Current="insert_mid_loop";ResetPose();r.CodeTimeout=4;r.SubmitContractTest("验收：往返三次",Loop(),LoopContract());yield return WaitMoving(8);
   string id=r.Context.MissionId,program=r.LastProgram;Vector3 before=r.Motor.transform.position;int done=r.Evidence.results.FindAll(x=>x.status=="succeeded"&&x.kind=="move_relative").Count;
   bool accepted=r.InsertContractForTest("验收：暂停挥手再继续剩余任务","game.gesture('"+wave+"');return {ok=true}",WaveContract());
   Record("insertion/suspended_mid_leg",accepted&&r.IsSuspended&&r.IsRunning&&done<6,"parent retained and paused during unfinished leg",r.Phase+"; completed="+done);
   float until=Time.realtimeSinceStartup+0.35f;float drift=0;while(Time.realtimeSinceStartup<until){drift=Mathf.Max(drift,Vector3.Distance(before,r.Motor.transform.position));yield return null;}
   Record("insertion/stops_immediately",drift<0.02f,"no travel while insertion runs",drift.ToString());Capture("during_insertion");
   yield return WaitDone();int count=r.Evidence.results.FindAll(x=>x.kind=="move_relative"&&x.status=="succeeded").Count;
   Record("insertion/retains_vm_and_receipts",r.Context.MissionId==id&&r.LastProgram==program&&count==6&&r.Evidence.status=="succeeded","same VM program; exactly 6 movement receipts; succeeded",r.Evidence.status+"; moves="+count,r.Evidence.reason,r.RunDirectory);
   Record("insertion/time_budget_excludes_pause",r.Evidence.paused_seconds>2&&r.Evidence.status=="succeeded","gesture pause >2s does not exhaust 2s movement timeout / 4s VM budget",r.Evidence.paused_seconds+"; active elapsed="+r.Evidence.elapsed);
   Record("insertion/child_receipt",r.Evidence.insertions.Count==1&&r.Evidence.insertions[0].status=="succeeded","one successful child receipt",JsonUtility.ToJson(r.Evidence));r.CodeTimeout=codeTimeout;
  }
  IEnumerator QueueAndFailure(){
   Current="queued_insertion";ResetPose();r.SubmitContractTest("验收：往返",Loop(2),LoopContract(2));yield return WaitMoving(8);
   string id=r.Context.MissionId;bool a=r.InsertContractForTest("验收：临时等待","game.wait(0.5);return {ok=true}",WaitContract(0.5f));
   bool b=r.InsertContractForTest("验收：第二次临时等待","game.wait(0.5);return {ok=true}",WaitContract(0.5f));yield return WaitDone();
   Record("insertion/queued_requests_preserve_parent",a&&b&&r.Context.MissionId==id&&r.Evidence.insertions.Count==2&&r.Evidence.status=="succeeded","two sequential children, same successful parent",r.Evidence.status+"; children="+r.Evidence.insertions.Count,r.Evidence.reason,r.RunDirectory);
   ResetPose();r.SubmitContractTest("验收：往返",Loop(2),LoopContract(2));yield return WaitMoving(8);
   id=r.Context.MissionId;r.InsertContractForTest("验收：不支持的临时移动","game.move_relative('right',{distance=0.4});return {ok=true}",LoopContract(1));yield return WaitDone();
   Record("insertion/rejected_child_resumes_parent",r.Context.MissionId==id&&r.Evidence.status=="succeeded"&&r.Evidence.insertions.Count==1&&r.Evidence.insertions[0].status=="rejected","child cannot move; parent resumes",r.Evidence.status+"; children="+r.Evidence.insertions.Count,r.LastReport,r.RunDirectory);
   ResetPose();r.SubmitContractTest("验收：往返",Loop(),LoopContract());yield return WaitMoving(8);r.InsertContractForTest("验收：临时等待","game.wait(5);return {ok=true}",WaitContract(5));yield return null;r.Cancel("用户停止任务");Vector3 stopped=r.Motor.transform.position;yield return new WaitForSecondsRealtime(0.5f);
   Record("insertion/cancel_stops_parent_and_child",!r.IsRunning&&!r.IsSuspended&&r.ActiveInsertion==null&&!r.Motor.HasTarget&&Vector3.Distance(stopped,r.Motor.transform.position)<0.01f,"both cancelled; no late resume",r.Phase);
   ResetPose();r.SubmitContractTest("验收：往返",Loop(),LoopContract());yield return WaitMoving(8);r.InsertContractForTest("验收：临时挥手","game.gesture('"+wave+"');return {ok=true}",WaveContract());yield return new WaitForSecondsRealtime(0.3f);string old=r.Context.MissionId;
   var o=O("t","turn_body");o.yaw=90;o.reference="body";r.SubmitContractTest("验收：替换为右转","game.turn_body(90);return {ok=true}",C(o));yield return WaitDone();yield return new WaitForSecondsRealtime(4);
   Record("insertion/replacement_no_stale_callback",r.Context.MissionId!=old&&r.Evidence.status=="succeeded"&&r.ActiveInsertion==null&&!s.Character.Idle.Enabled&&s.Character.AnimPlayer.IsPlaying,"new mission succeeds, old animation cannot restore procedural pose",r.Evidence.status+"; clip="+s.Character.AnimPlayer.CurrentName,r.Evidence.reason,r.RunDirectory);
  }
  IEnumerator BodyTurns(){
   foreach(float angle in new[]{90f,-90f,180f,-180f}){var o=O("t","turn_body");o.yaw=angle;o.reference="body";yield return Fixture("turn_"+angle,"local r=game.turn_body("+angle+");return {ok=r.ok,reason=r.reason}",C(o),true);var x=r.Evidence.results.Find(v=>v.kind=="turn_body");Record("geometry/turn_"+angle,x!=null&&x.facing_error<=0.5f&&x.position_drift<=0.02f,"correct signed turn; no translation",x==null?"no receipt":JsonUtility.ToJson(x));}
   var goal=O("t","turn_body");goal.yaw=90;var c=C(goal);c.forbid_body_turn=true;Record("policy/no_body_turn",TaskContractRules.Policy(c,new MissionStep{kind="turn_body",yaw=90})=="policy_body_turn_forbidden","denied",TaskContractRules.Policy(c,new MissionStep{kind="turn_body",yaw=90}));
   c=C(goal);c.forbid_move=true;yield return Fixture("turn_without_translation","game.turn_body(90);return {ok=true}",c,true);
   foreach(string invalid in new[]{"0","181","math.huge","0/0"})yield return Fixture("invalid_turn_"+invalid.Replace('/','_'),"game.turn_body("+invalid+");return {ok=true}",C(goal),false);
   yield return Fixture("excess_turn_count","game.turn_body(90);game.turn_body(90);return {ok=true}",C(goal),false);
   yield return Fixture("turn_wrong_angle","game.turn_body(-90);return {ok=true}",C(goal),false);
   yield return Fixture("parallel_body_conflict","game.parallel({{kind='turn_body',yaw=90},{kind='gesture',action_id='"+wave+"'}});return {ok=true}",C(goal,WaveContract().obligations[0]),false);
  }
  IEnumerator Input(string text,bool expected=true){Current=text;string before=r.RunDirectory;s.Submit(text);yield return null;float end=Time.realtimeSinceStartup+100;while((s.DialogueBusy||r.RunDirectory!=before&&r.IsRunning)&&Time.realtimeSinceStartup<end)yield return null;if(r.IsRunning)r.Cancel("real_input_timeout");Record("real/"+text,r.RunDirectory!=before&&(r.Evidence.status=="succeeded")==expected,expected?"succeeded":"not succeeded",r.Evidence.status,r.Evidence.reason,r.RunDirectory);}
  IEnumerator Followup(){
   Current="insert_after_completed_roundtrip";ResetPose();r.SubmitContractTest("验收：往返四次",Loop(4),LoopContract(4));
   float end=Time.realtimeSinceStartup+8;while((r.Evidence.results.FindAll(x=>x.kind=="move_relative"&&x.status=="succeeded").Count<2||!r.Motor.HasTarget)&&r.IsRunning&&Time.realtimeSinceStartup<end)yield return null;
   int before=r.Evidence.results.FindAll(x=>x.kind=="move_relative"&&x.status=="succeeded").Count;var first=r.Evidence.results[0];string id=r.Context.MissionId;
   r.InsertContractForTest("验收：先挥手再继续剩余任务","game.gesture('"+wave+"');return {ok=true}",WaveContract());yield return WaitDone();
   int total=r.Evidence.results.FindAll(x=>x.kind=="move_relative"&&x.status=="succeeded").Count;
   Record("followup/completed_receipts_not_replayed",before>=2&&total==8&&object.ReferenceEquals(first,r.Evidence.results[0])&&r.Context.MissionId==id&&r.Evidence.status=="succeeded","completed first roundtrip retained; exactly 8 legs total",r.Evidence.status+"; before="+before+"; after="+total,r.Evidence.reason,r.RunDirectory);
   Record("followup/wall_clock_includes_pause",r.Evidence.wall_elapsed>=r.Evidence.elapsed+r.Evidence.paused_seconds-0.05f,"wall=active+paused",r.Evidence.wall_elapsed+" / "+r.Evidence.elapsed+" + "+r.Evidence.paused_seconds);
   ResetPose();r.SubmitContractTest("验收：往返",Loop(2),LoopContract(2));yield return WaitMoving(8);
   var speak=O("s","speak");speak.text="临时发声";r.InsertContractForTest("验收：临时说话","game.speak('临时发声');return {ok=true}",new TaskContract{obligations=new[]{speak}});yield return WaitDone();
   Record("followup/parent_silence_applies_to_insertion",r.Evidence.status=="succeeded"&&r.Evidence.insertions.Count==1&&r.Evidence.insertions[0].reason.Contains("silence"),"child speech rejected; parent resumes",r.Evidence.status,r.Evidence.insertions.Count>0?r.Evidence.insertions[0].reason:"no child",r.RunDirectory);
   ResetPose();r.SubmitContractTest("验收：挥手独占身体","game.gesture('"+wave+"');return {ok=true}",WaveContract());yield return null;id=r.Context.MissionId;
   bool accepted=r.InsertContractForTest("验收：中途等待","game.wait(0.1);return {ok=true}",WaitContract(0.1f));
   Record("followup/unsafe_active_effect_declined",!accepted&&r.IsRunning&&!r.IsSuspended&&r.Context.MissionId==id,"do not interrupt an in-flight animation; preserve task",r.Phase+"; accepted="+accepted);yield return WaitDone();
   ResetPose();r.Submit("先停下挥手，然后继续没走完的任务。");Record("followup/no_parent_no_false_success",!r.IsRunning&&r.Evidence.status=="rejected"&&r.Evidence.reason.Contains("no_active_mission"),"no saved mission means explicit rejection",r.Evidence.status,r.Evidence.reason,r.RunDirectory);
   ResetPose();float oldSpeed=r.Motor.TurnDegreesPerSecond;r.Motor.TurnDegreesPerSecond=45;var turn=O("t","turn_body");turn.yaw=90;
   r.SubmitContractTest("验收：右转中插入","game.turn_body(90);return {ok=true}",C(turn));yield return new WaitForSecondsRealtime(0.3f);r.InsertContractForTest("验收：暂时等待","game.wait(0.4);return {ok=true}",WaitContract(0.4f));yield return WaitDone();r.Motor.TurnDegreesPerSecond=oldSpeed;
   Record("followup/resume_partial_turn",r.Evidence.status=="succeeded"&&r.Evidence.insertions.Count==1&&Mathf.Abs(Mathf.DeltaAngle(initialYaw+90,r.Motor.transform.eulerAngles.y))<0.5f,"resume original turn target",r.Evidence.status,r.Evidence.reason,r.RunDirectory);
   ResetPose();r.SubmitContractTest("验收：往返",Loop(3),LoopContract(3));yield return WaitMoving(8);r.InsertContractForTest("验收：临时挥手","game.gesture('"+wave+"');return {ok=true}",WaveContract());yield return new WaitForSecondsRealtime(0.2f);
   r.Catalog.Refresh(r.Motor.transform);string traffic=null;foreach(var t in r.Catalog.Targets)if(t.Object!=null&&t.Object.name=="TrafficLight"){traffic=t.Id;break;}
   var nav=O("n","navigate");nav.target_id=traffic;r.SubmitContractTest("验收：改去红绿灯","local a=game.navigate('"+traffic+"');return {ok=a.ok,reason=a.reason}",C(nav));yield return new WaitForSecondsRealtime(3.7f);
   Record("followup/cancelled_gesture_never_overwrites_new_walk",r.IsRunning&&r.Motor.HasTarget&&s.Character.AnimPlayer.CurrentName=="Walking"&&!s.Character.Idle.Enabled,"new walk still owns animation after old clip end time",r.Phase+"; clip="+s.Character.AnimPlayer.CurrentName,r.Evidence.reason,r.RunDirectory);yield return WaitDone();
   Record("followup/replacement_navigation_succeeds",r.Evidence.status=="succeeded","new target reached",r.Evidence.status,r.Evidence.reason,r.RunDirectory);
   // Real natural-language case with completed legs, using the user's original wording.
   ResetPose();s.Submit("请走到红绿灯，然后走回原点，往返三次。");end=Time.realtimeSinceStartup+100;
   while((s.DialogueBusy||!r.IsRunning||r.Evidence.results.FindAll(x=>(x.kind=="navigate"||x.kind=="return")&&x.status=="succeeded").Count<2||!r.Motor.HasTarget)&&Time.realtimeSinceStartup<end)yield return null;
   before=r.Evidence.results.FindAll(x=>(x.kind=="navigate"||x.kind=="return")&&x.status=="succeeded").Count;id=r.Context.MissionId;first=r.Evidence.results.Count>0?r.Evidence.results[0]:null;
   s.Submit("你现在先停下做一个挥舞双手的动作然后继续把没走完的地方走完");yield return null;bool paused=r.IsSuspended;
   yield return WaitDone(150);total=r.Evidence.results.FindAll(x=>(x.kind=="navigate"||x.kind=="return")&&x.status=="succeeded").Count;
   Record("real/completed_roundtrip_then_user_exact_insertion",before>=2&&paused&&r.Context.MissionId==id&&r.Evidence.status=="succeeded"&&total==6&&object.ReferenceEquals(first,r.Evidence.results[0]),"same VM and earlier receipts; 3 roundtrips total",r.Evidence.status+"; before="+before+"; after="+total,r.Evidence.reason,r.RunDirectory);
   Record("real/exact_insertion_child_succeeded",r.Evidence.insertions.Count==1&&r.Evidence.insertions[0].status=="succeeded","LLM handles exact original insertion sentence",r.Evidence.insertions.Count>0?r.Evidence.insertions[0].status:"no child",r.LastInsertionReport,r.RunDirectory);Capture("final_idle_after_real_insertion");
   yield return BodyTurns();yield return ContractLoops();yield return PoseTest();
  }
  IEnumerator Real(){
   ResetPose();yield return Input("请现在将身体向右旋转九十度，不要移动。");Capture("real_right90");yield return Input("请身体向左转九十度，不要移动。");
   yield return Input("请把脸朝向我，不要移动。");Record("real/facing_adds_no_move",!r.Evidence.results.Exists(x=>x.kind=="navigate"||x.kind=="move_relative"||x.kind=="return"),"look only; no invented move",r.LastProgram,r.Evidence.reason,r.RunDirectory);
   ResetPose();Current="real_roundtrip_with_injection";s.Submit("请走到红绿灯再走回原点，往返五次。");yield return WaitMoving();string id=r.Context.MissionId,program=r.LastProgram;bool moving=r.IsRunning&&r.Motor.HasTarget;int completed=r.Evidence.results.FindAll(x=>x.status=="succeeded"&&x.kind!="report").Count;
   s.Submit("你现在先停下，做一个挥舞双手的动作，然后继续把没走完的往返跑走完。");yield return null;
   Record("real/injection_pauses_existing_mission",moving&&r.IsSuspended&&r.Context.MissionId==id,"same parent paused before replacing it","moving="+moving+"; suspended="+r.IsSuspended+"; doneBefore="+completed,r.Phase,r.RunDirectory);Capture("real_insertion");
   yield return WaitDone(180);int nav=r.Evidence.results.FindAll(x=>x.kind=="navigate"&&x.status=="succeeded").Count,home=r.Evidence.results.FindAll(x=>x.kind=="return"&&x.status=="succeeded").Count;
   Record("real/five_roundtrips_resume_exactly",r.Context.MissionId==id&&r.LastProgram==program&&r.Evidence.status=="succeeded"&&nav==5&&home==5,"same parent; 5 outward + 5 home legs",r.Evidence.status+"; navigate="+nav+"; home="+home,r.Evidence.reason,r.RunDirectory);
   Record("real/llm_generates_actual_insertion",r.Evidence.insertions.Count==1&&r.Evidence.insertions[0].status=="succeeded","real contract+Lua child succeeds",r.Evidence.insertions.Count==0?"no child":r.Evidence.insertions[0].status,r.LastInsertionReport,r.RunDirectory);Capture("real_roundtrip_done");
  }
  IEnumerator Run(){try{s.VoiceInput.SetEnabled(false);s.SetVoiceOutputEnabled(false);r.VoiceEnabled=false;s.InputMode=TaskInputMode.Auto;r.EvidenceRoot=Path.Combine(Output,"missions");
    if(FollowupOnly){yield return Followup();yield break;}
    Record("gesture/catalog_has_wave",wave!=null,"real manifest wave available",wave+"|"+clip);
    yield return ContractLoops();yield return PoseTest();yield return InsertionFixture();yield return QueueAndFailure();yield return BodyTurns();
    var unit=TaskRuntimeUnitAcceptance.Run(Path.Combine(Output,"unit"));Record("regression/existing_contract_tests",unit.cases.TrueForAll(x=>x.pass),"all previous unit cases pass",unit.cases.Count.ToString());yield return Real();
   }finally{if(r.IsRunning)r.Cancel("acceptance_finished");s.CancelDialogueTurn();s.VoiceInput.SetEnabled(mic);s.SetVoiceOutputEnabled(voice);r.VoiceEnabled=taskVoice;s.InputMode=mode;r.EvidenceRoot=evidenceRoot;r.CodeTimeout=codeTimeout;Report.done=true;Running=false;Current="done";Save();}
  }
 }
}
