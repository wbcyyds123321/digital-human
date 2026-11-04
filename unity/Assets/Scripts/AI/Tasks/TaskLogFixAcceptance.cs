using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
namespace Yixuan.AI.Tasks {
 // Explicitly invoked acceptance harness. Fixture cases are separate from real LLM cases.
 public sealed class TaskLogFixAcceptance:MonoBehaviour {
  public string Output="D:/AI/GameScaling/docs/evidence/runtime_log_fix_2026-10-25";
  public bool FollowupOnly;public RuntimeTestReport Report;public bool Running;public string Current="";public bool AudioObserved;public float MaxRms,MaxMouthWeight;public bool VoiceRestored;
  ChatSession s;TaskMissionRunner r;Vector3 initial;float initialYaw;bool mic,gameView,webcam,voice,taskVoice;string evidenceRoot;float lastSample;
  public void Begin(){if(Running)return;s=FindObjectOfType<ChatSession>();r=s.Missions;initial=r.HomeCaptured?r.HomePosition:r.Motor.transform.position;initialYaw=r.HomeCaptured?r.HomeYaw:r.Motor.transform.eulerAngles.y;mic=s.VoiceInput.Enabled;gameView=s.Vision.GameViewEnabled;webcam=s.Vision.WebcamEnabled;voice=s.Config.VoiceEnabled;taskVoice=r.VoiceEnabled;evidenceRoot=r.EvidenceRoot;Report=new RuntimeTestReport{suite=FollowupOnly?"runtime log fixes followup: movement budget, dialogue references, real PCM mouth":"runtime log fixes: fixtures + real user Submit, Qwen frames and audio"};Running=true;StartCoroutine(Run());}
  void Update(){if(!Running||s==null||s.Voice==null||s.Voice.Source==null||Time.realtimeSinceStartup-lastSample<0.05f)return;lastSample=Time.realtimeSinceStartup;var a=s.Voice.Source;if(a.isPlaying){float[] data=new float[1024];a.GetOutputData(data,0);double sum=0;foreach(var v in data)sum+=v*v;float rms=(float)Math.Sqrt(sum/data.Length);MaxRms=Mathf.Max(MaxRms,rms);if(a.timeSamples>0&&rms>0.00001f){AudioObserved=true;var exp=s.Character.Expressions;if(exp!=null&&exp.Shapes!=null){var shapes=exp.Shapes;MaxMouthWeight=Mathf.Max(MaxMouthWeight,Mathf.Max(shapes.Get(shapes.A),Mathf.Max(shapes.Get(shapes.I),Mathf.Max(shapes.Get(shapes.U),Mathf.Max(shapes.Get(shapes.E),shapes.Get(shapes.O))))));}}}}
  void Record(string name,bool pass,string expected,string actual,string reason="",string directory=""){Report.cases.Add(new RuntimeTestResult{name=name,pass=pass,expected=expected,actual=actual,reason=reason,directory=directory});Save();}
  void Save(){Directory.CreateDirectory(Output);File.WriteAllText(Path.Combine(Output,"acceptance_report.json"),JsonUtility.ToJson(Report,true));}
  void Capture(string name){
   Directory.CreateDirectory(Path.Combine(Output,"frames"));foreach(var c in new[]{Camera.main,s.Vision.EyeCamera}){
    if(c==null)continue;var target=c.targetTexture;var active=RenderTexture.active;var rt=RenderTexture.GetTemporary(960,640,24);Texture2D tex=null;
    try{c.targetTexture=rt;c.Render();RenderTexture.active=rt;tex=new Texture2D(960,640,TextureFormat.RGB24,false);tex.ReadPixels(new Rect(0,0,960,640),0,0);tex.Apply();File.WriteAllBytes(Path.Combine(Output,"frames",name+"_"+(c==s.Vision.EyeCamera?"eye":"main")+".png"),tex.EncodeToPNG());}
    finally{c.targetTexture=target;RenderTexture.active=active;RenderTexture.ReleaseTemporary(rt);if(tex!=null)Destroy(tex);}
   }
  }
  TaskContract C(string direction="forward",float distance=0.8f){return new TaskContract{forbid_speech=true,obligations=new[]{new TaskObligation{id="m",source="验收",kind="move_relative",direction=direction,distance=distance,count=1}}};}
  IEnumerator Fixture(string name,string source,TaskContract contract,bool expectedSuccess,float yaw=0){
   Current=name;r.StopAttention();r.Motor.transform.position=initial;r.Motor.transform.rotation=Quaternion.Euler(0,yaw,0);r.SubmitContractTest("验收："+name,source,contract);float limit=Time.realtimeSinceStartup+45;
   while(r.IsRunning&&Time.realtimeSinceStartup<limit)yield return null;if(r.IsRunning)r.Cancel("fixture_timeout");
   bool ok=r.Evidence.status=="succeeded";Record("fixture/"+name,ok==expectedSuccess,expectedSuccess?"succeeded":"not_succeeded",r.Evidence.status,r.Evidence.reason,r.RunDirectory);
   if(expectedSuccess&&ok){var x=r.Evidence.results.Find(v=>v.kind=="move_relative");Record("geometry/"+name,x!=null&&x.distance_error<=0.03f&&x.body_rotation<=0.5f,"distance_error<=0.03m; yaw drift<=0.5deg",x==null?"no receipt":JsonUtility.ToJson(x));Capture(name);}
   yield return null;
  }
  IEnumerator Input(string text,bool? success=null){
   Current=text;string old=r.RunDirectory;s.Submit(text);float limit=Time.realtimeSinceStartup+100;
   yield return null;while((s.DialogueBusy||r.RunDirectory!=old&&r.IsRunning)&&Time.realtimeSinceStartup<limit)yield return null;
   if(s.DialogueBusy||r.RunDirectory!=old&&r.IsRunning){s.CancelDialogueTurn();if(r.IsRunning)r.Cancel("acceptance_timeout");Record("real/"+text,false,"complete within 100s","timeout");yield break;}
   bool newMission=r.RunDirectory!=old;bool pass=success.HasValue?newMission&&(r.Evidence.status=="succeeded")==success.Value:!newMission&&!string.IsNullOrWhiteSpace(s.LastReply);
   Record("real/"+text,pass,success.HasValue?(success.Value?"succeeded":"not_succeeded"):"dialogue; no new mission",newMission?r.Evidence.status:s.LastReply,newMission?r.Evidence.reason:"",newMission?r.RunDirectory:"");
   yield return new WaitForSecondsRealtime(0.3f);
  }
  IEnumerator ExpressionChecks(){
   Current="facial_drivers_across_frames";var exp=s.Character.Expressions;var blink=s.Character.Blink;exp.EnsureShapes();var shapes=exp.Shapes;
   var c=new TaskContract{forbid_move=true,forbid_speech=true,obligations=new[]{new TaskObligation{id="e",source="验收",kind="express",emotion="happy",min_duration=2}}};
   r.SubmitContractTest("验收：保持微笑","game.express('happy',{duration=2});return {ok=true}",c);
   float timeout=Time.realtimeSinceStartup+6;while(r.IsRunning&&exp.CurrentId!="smile"&&Time.realtimeSinceStartup<timeout)yield return null;
   if(blink!=null)blink.TriggerBlink();float minSmile=1,maxBlink=0;int frames=0,badBlink=0;
   float end=Time.realtimeSinceStartup+0.7f;while(Time.realtimeSinceStartup<end){yield return new WaitForEndOfFrame();frames++;minSmile=Mathf.Min(minSmile,shapes.Get(shapes.MouthCornerUp));if(blink!=null){float actual=shapes.Get(shapes.Blink);maxBlink=Mathf.Max(maxBlink,actual);if(Mathf.Abs(actual-blink.CurrentValue*blink.ClosingStrength)>0.02f)badBlink++;}}
   Record("expression_happy_maps_to_smile",exp.CurrentId=="smile"&&frames>5&&minSmile>=0.7f,"smile; mouth corner >=0.7 across frames",exp.CurrentId+"; frames="+frames+"; min="+minSmile);
   Record("blink_survives_vrm",blink!=null&&maxBlink>0.15f&&badBlink==0,"blink shape follows driver after VRM LateUpdate","max="+maxBlink+"; mismatched frames="+badBlink);Capture("expression_smile");
   while(r.IsRunning&&Time.realtimeSinceStartup<timeout)yield return null;if(r.IsRunning)r.Cancel("expression_timeout");
   c.obligations[0].emotion="neutral";c.obligations[0].min_duration=0.5f;r.SubmitContractTest("验收：恢复中性","game.express('neutral',{duration=0.5});return {ok=true}",c);
   timeout=Time.realtimeSinceStartup+5;while(r.IsRunning&&Time.realtimeSinceStartup<timeout)yield return null;yield return new WaitForEndOfFrame();
   Record("expression_neutral_clears_smile",r.Evidence.status=="succeeded"&&exp.CurrentId=="neutral"&&shapes.Get(shapes.MouthCornerUp)<=0.01f,"neutral with no stale smile",exp.CurrentId+"; corner="+shapes.Get(shapes.MouthCornerUp));Capture("expression_neutral");
  }
  IEnumerator Followup(){
   var c=C("right",0.4f);c.obligations[0].count=2;
   yield return Fixture("authorized_relative_loop","for i=1,2 do local r=game.move_relative('right',{steps=1});if not r.ok then return {ok=false,reason=r.reason} end end;return {ok=true}",c,true);
   yield return Fixture("relative_idempotent_replay","game.move_relative('forward',{steps=2,key='same'});game.move_relative('forward',{steps=2,key='same'});return {ok=true}",C(),true);
   int relativeCalls=0;foreach(var result in r.Evidence.results)if(result.kind=="move_relative")relativeCalls++;Record("idempotent_moves_once",relativeCalls==1,"one physical move for two same-key calls",relativeCalls.ToString());
   yield return Input("我叫小林，请记住我的名字。",null);yield return Input("我刚才告诉你我叫什么？",null);Record("dialogue_reference_memory",s.LastReply.Contains("小林"),"remember latest stated name without route contamination",s.LastReply);
   s.SetVoiceOutputEnabled(true);r.VoiceEnabled=true;Current="real_task_audio_and_mouth";string text="这是一段真实的任务语音。正在检查有声音时嘴部是否张合，停止后是否能正确闭合。";
   r.SubmitContractTest("验收：真实语音口型","local r=game.speak('"+text+"');return {ok=r.ok,reason=r.reason}",new TaskContract{forbid_move=true,obligations=new[]{new TaskObligation{id="s",source="验收",kind="speak",text=text}}});
   bool captured=false;float limit=Time.realtimeSinceStartup+45;while(r.IsRunning&&Time.realtimeSinceStartup<limit){if(!captured&&AudioObserved&&MaxMouthWeight>0.05f){Capture("real_speech_mouth");captured=true;}yield return null;}
   Record("real_task_speech_success",!r.IsRunning&&r.Evidence.status=="succeeded","real TTS action succeeded",r.Evidence.status,r.Evidence.reason,r.RunDirectory);
   Record("audible_output_samples",AudioObserved,"playing actual AudioSource with positive RMS",MaxRms.ToString("F6"));
   Record("mouth_during_real_speech",MaxMouthWeight>0.01f,"positive mouth shape while real audio plays",MaxMouthWeight.ToString("F6"));
   s.SilenceSpeech();yield return new WaitForSecondsRealtime(0.5f);var shapes=s.Character.Expressions.Shapes;float mouth=Mathf.Max(shapes.Get(shapes.A),Mathf.Max(shapes.Get(shapes.I),Mathf.Max(shapes.Get(shapes.U),Mathf.Max(shapes.Get(shapes.E),shapes.Get(shapes.O)))));
   Record("silence_stops_audio_and_mouth",!s.Voice.Source.isPlaying&&mouth<0.01f,"audio stops; mouth <0.01","playing="+s.Voice.Source.isPlaying+"; mouth="+mouth);
   s.Submit("可以说话了");
  }
  IEnumerator Run(){
   try{
    s.VoiceInput.SetEnabled(false);s.Vision.SetGameView(true);s.Vision.SetWebcam(false);s.Vision.SaveAskFramesForDebug=true;r.EvidenceRoot=Path.Combine(Output,"missions");s.SetVoiceOutputEnabled(false);
    if(FollowupOnly){yield return Followup();yield break;}
    var unit=TaskRuntimeUnitAcceptance.Run(Path.Combine(Output,"unit"));Record("existing_contract_suite",unit.cases.TrueForAll(x=>x.pass),"34/34",unit.cases.Count+" cases");
    var c=C();Record("relative_policy_exact",TaskContractRules.Policy(c,new MissionStep{kind="move_relative",direction="forward",distance=0.8f})==null,"allowed","checked");
    Record("relative_policy_wrong_distance",TaskContractRules.Policy(c,new MissionStep{kind="move_relative",direction="forward",distance=1.2f})!=null,"denied","checked");
    c.forbid_move=true;Record("relative_policy_no_move",TaskContractRules.Policy(c,new MissionStep{kind="move_relative",direction="forward",distance=0.8f})!=null,"denied","checked");
    Record("relative_nonfinite",!TaskContractRules.ValidMoveDistance(float.NaN)&&!TaskContractRules.ValidMoveDistance(float.PositiveInfinity),"denied","checked");
    yield return Fixture("forward_two_steps","local r=game.move_relative('forward',{steps=2});return {ok=r.ok,reason=r.reason}",C(),true);
    yield return Fixture("backward_one_step","local r=game.move_relative('backward',{steps=1});return {ok=r.ok,reason=r.reason}",C("backward",0.4f),false);
    yield return Fixture("right_one_step","local r=game.move_relative('right',{steps=1});return {ok=r.ok,reason=r.reason}",C("right",0.4f),true);
    yield return Fixture("left_one_step","local r=game.move_relative('left',{steps=1});return {ok=r.ok,reason=r.reason}",C("left",0.4f),true);
    yield return Fixture("yaw90_forward","local r=game.move_relative('forward',{distance=0.8});return {ok=r.ok,reason=r.reason}",C(),true,90);
    yield return Fixture("blocked_no_detour","game.move_relative('backward',{distance=2});return {ok=true}",C("backward",2),false);
    c=new TaskContract{forbid_move=true,forbid_speech=true,obligations=new[]{new TaskObligation{id="v",source="验收",kind="look_direction",direction="up"}}};yield return Fixture("forbidden_dynamic_call","local n='move_relative';game[n]('forward',{steps=2});return {ok=true}",c,false);
    yield return Fixture("excess_repetition","game.move_relative('forward',{steps=2});game.move_relative('forward',{steps=2});return {ok=true}",C(),false);
    yield return Fixture("negative_distance","game.move_relative('forward',{distance=-1});return {ok=true}",C(),false);
    yield return Fixture("fractional_steps","game.move_relative('forward',{steps=1.5});return {ok=true}",C(),false);
    yield return Fixture("double_units","game.move_relative('forward',{steps=2,distance=0.8});return {ok=true}",C(),false);
    yield return Fixture("nonfinite_distance","game.move_relative('forward',{distance=math.huge});return {ok=true}",C(),false);
    yield return ExpressionChecks();
    r.StopAttention();r.Motor.transform.position=initial;r.Motor.transform.rotation=Quaternion.Euler(0,initialYaw,0);
    yield return Input("请你往前走两步",true);Capture("real_forward");
    yield return Input("请往后退两步",true);
    yield return Input("请先向右移动一步，然后向左移动一步",true);
    s.SetVoiceOutputEnabled(true);
    yield return Input("你叫什么名字？",null);Record("persona_identity",s.LastReply.Contains(s.CurrentCharacterProfile().CharacterName),s.CurrentCharacterProfile().CharacterName,s.LastReply);
    yield return Input("你那边现在天是黑的还是白的？",null);Record("fresh_vision_answer",!string.IsNullOrWhiteSpace(s.Vision.LastRealtimeAnswer)&&!s.LastReply.Contains("navigate")&&!s.LastReply.Contains("任务状态"),"fresh Qwen answer",s.LastReply,s.Vision.Client.ServiceBlocked?"BLOCKED_EXTERNAL: "+s.Vision.Client.ServiceStatus:"");Capture("real_vision");
    yield return Input("描述一下你现在看到了什么",null);
    Record("vision_quota_failure_explained",!s.Vision.Client.ServiceBlocked||s.LastReply.Contains("额度")||s.LastReply.Contains("服务"),"if quota exhausted, say service cause",s.LastReply);
    s.Vision.SetGameView(false);yield return Input("你能看到现在游戏里的画面吗？",null);Record("disabled_vision_truthful",s.LastReply.Contains("未")||s.LastReply.Contains("没有")||s.LastReply.Contains("开启"),"source not enabled",s.LastReply);s.Vision.SetGameView(true);
    Current="parallel_navigation_and_vision";s.Submit("请走到红绿灯，然后回到本次任务起点，往返三次");float limit=Time.realtimeSinceStartup+65;while((!r.IsRunning||!r.Phase.Contains("code_waiting"))&&Time.realtimeSinceStartup<limit)yield return null;
    string mission=r.RunDirectory;bool wasRunning=r.IsRunning;
    yield return Input("你现在眼前能看到什么？",null);Record("parallel_vision_preserves_mission",wasRunning&&r.IsRunning&&r.RunDirectory==mission&&r.Evidence.status!="cancelled","same mission not cancelled",r.Phase,r.Evidence.reason,r.RunDirectory);
    yield return Input("任务现在完成到哪一步了？",null);Record("short_status",s.LastReply.Length<=110&&!s.LastReply.Contains("scene/")&&!s.LastReply.Contains("navigate"),"<=110 chars; no paths",s.LastReply);
    limit=Time.realtimeSinceStartup+100;while(r.IsRunning&&Time.realtimeSinceStartup<limit)yield return null;Record("traffic_roundtrip_regression",!r.IsRunning&&r.Evidence.status=="succeeded","succeeded",r.Evidence.status,r.Evidence.reason,r.RunDirectory);Capture("traffic_done");
    yield return Input("请你走向商店门口",false);Record("door_failure_truthful",r.Evidence.reason.Contains("通路")||r.Evidence.reason.Contains("不可达")||r.Evidence.reason.Contains("unreachable"),"unreachable without substitution",r.Evidence.reason);
    float audioLimit=Time.realtimeSinceStartup+12;while(!AudioObserved&&Time.realtimeSinceStartup<audioLimit)yield return null;Record("audible_output_samples",AudioObserved,"playing audio with positive RMS",MaxRms.ToString("F6"));Record("mouth_during_real_speech",MaxMouthWeight>0.01f,"positive mouth shape from real speech UDP",MaxMouthWeight.ToString("F6"));
    s.SilenceSpeech();yield return new WaitForSecondsRealtime(0.3f);Record("silence_stops_audio",!s.Voice.Source.isPlaying,"stopped",s.Voice.Source.isPlaying.ToString());s.Submit("可以说话了");
   }
   finally {if(r.IsRunning)r.Cancel("acceptance_finished");s.CancelDialogueTurn();s.VoiceInput.SetEnabled(mic);s.SetVoiceOutputEnabled(voice);r.VoiceEnabled=taskVoice;s.Vision.SetGameView(gameView);s.Vision.SetWebcam(webcam);r.EvidenceRoot=evidenceRoot;VoiceRestored=s.VoiceInput.Enabled==mic;Report.done=true;Running=false;Current="done";Save();}
  }
 }
}
