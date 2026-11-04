using System;
using System.Collections;
using System.IO;
using UnityEngine;
namespace Yixuan.AI.Tasks {
 public sealed class TaskRuntimeConcurrentAcceptance:MonoBehaviour {
  public TaskMissionRunner Runner;public RuntimeTestReport Report;public bool Running;public string Output="Captures/Evidence/task_runtime_v2_2026-10-25";
  bool oldVoice,oldParallel,oldTaskVoice,oldTts;TaskInputMode oldMode;
  public void Begin(){if(Running)return;Runner=FindObjectOfType<TaskMissionRunner>();Running=true;Report=new RuntimeTestReport{suite="injected action contracts + actual movement, real independent narrator, real TTS, barge/cancel API entrypoints; no ASR microphone claim"};StartCoroutine(Run());}
  TaskObligation O(string id,string kind,string target=""){return new TaskObligation{id=id,source="验收",kind=kind,target_id=target,count=1};}
  void Check(string name,bool ok,string detail=""){Report.cases.Add(new RuntimeTestResult{name=name,pass=ok,expected="true",actual=ok.ToString(),reason=detail,directory=Runner.RunDirectory});Save();}
  void Save(){Directory.CreateDirectory(Output);File.WriteAllText(Path.Combine(Output,"concurrent_report.json"),JsonUtility.ToJson(Report,true));}
  IEnumerator Delay(float seconds){float end=Time.realtimeSinceStartup+seconds;while(Time.realtimeSinceStartup<end)yield return null;}
  void LongMove(){var c=new TaskContract{obligations=new[]{O("n","navigate","scene/RainyConvenienceDiorama[0]/Exterior[4]/TrafficLight[40]"),new TaskObligation{id="w",source="验收",kind="wait",count=3}}};Runner.SubmitContractTest("验收：移动中闲聊、抢话及禁声", "game.navigate('scene/RainyConvenienceDiorama[0]/Exterior[4]/TrafficLight[40]');game.wait(15);game.wait(15);game.wait(15);return {ok=true}",c);}
  IEnumerator WaitDone(float seconds){float end=Time.realtimeSinceStartup+seconds;while(Runner.IsRunning&&Time.realtimeSinceStartup<end)yield return null;}
  IEnumerator Run(){
   var session=Runner.Session;oldVoice=session.Config.VoiceEnabled;oldTts=session.Tts.Enabled;oldParallel=session.ParallelTaskDialogue;oldTaskVoice=Runner.VoiceEnabled;oldMode=session.InputMode;
   session.Config.VoiceEnabled=false;session.ParallelTaskDialogue=true;session.InputMode=TaskInputMode.Auto;Runner.EvidenceRoot=Path.Combine(Output,"concurrent");Runner.VoiceEnabled=false;
   LongMove();yield return Delay(0.5f);var ctx=Runner.Context;var start=Runner.Motor.transform.position;
   session.CancelDialogueTurn();yield return Delay(0.7f);Check("barge_does_not_cancel_movement",Runner.IsRunning&&Runner.Context==ctx&&Vector3.Distance(start,Runner.Motor.transform.position)>0.1f,"API barge event; actual meters="+Vector3.Distance(start,Runner.Motor.transform.position));
   var target=Runner.Motor.Target;bool accepted=Runner.Motor.MoveTo(Runner.HomePosition);Runner.Motor.Stop("external interference");Runner.Session.Director.ReturnToIdle();
   var yaw=Runner.Gimbal.TargetYaw;Runner.Gimbal.TurnRelative(50,20);Runner.Gimbal.BeginRecenter();Runner.Gimbal.AbandonObservationForNewTurn();string gesture=Runner.Session.Character.LastAction;Runner.Session.Character.TriggerWave();
   Check("external_motor_camera_character_director_rejected",!accepted&&Runner.Motor.HasTarget&&Runner.Motor.Target==target&&Mathf.Abs(Runner.Gimbal.TargetYaw-yaw)<0.01f&&Runner.Session.Character.LastAction==gesture);
   int replyAfter=ctx.Events.Count;start=Runner.Motor.transform.position;session.Submit("继续走，跟我聊聊你喜欢什么天气");yield return Delay(1f);Check("chat_input_keeps_action_generation",Runner.IsRunning&&Runner.Context==ctx&&Vector3.Distance(start,Runner.Motor.transform.position)>0.1f);
   float deadline=Time.realtimeSinceStartup+35;while(ctx.Events.Find(e=>e.type=="NarratorReply"&&e.sequence>replyAfter)==null&&Time.realtimeSinceStartup<deadline)yield return null;
   Check("real_narrator_reply_while_mission_active",Runner.IsRunning&&ctx.Events.Find(e=>e.type=="NarratorReply")!=null,session.LastReply);
   session.Submit("别说了");yield return Delay(0.2f);Check("silence_only_mutes",Runner.IsRunning&&Runner.Context==ctx&&ctx.Muted);
   session.Submit("停下");var stopped=Runner.Motor.transform.position;yield return Delay(2f);Check("explicit_stop_releases_resources_and_no_restart",!Runner.IsRunning&&!Runner.Motor.HasTarget&&TaskAuthority.Allows(1|2|8)&&Vector3.Distance(stopped,Runner.Motor.transform.position)<0.02f);
   var oldCtx=ctx;var frozen=oldCtx.Events.Count;
   var gaze=new TaskContract{obligations=new[]{new TaskObligation{id="g",source="验收",kind="look_direction",direction="up",count=1}}};
   Runner.SubmitContractTest("验收：新任务抬头","game.look_direction('up');return {ok=true}",gaze);var newCtx=Runner.Context;
   var ticket=new TaskSpeechTicket{Context=oldCtx,Owner="old",Text="old result"};session.SpeechNotice(ticket,"Finished");
   Check("old_speech_event_stays_in_original_ledger",oldCtx.Events.Count==frozen+1&&newCtx.Events.Find(e=>e.type=="SpeechFinished")==null);
   yield return WaitDone(8);Check("replacement_task_completes_without_old_receipts",Runner.Evidence.status=="succeeded"&&Runner.Context==newCtx&&Runner.Evidence.results.TrueForAll(r=>r.generation==newCtx.Generation));
   session.Config.VoiceEnabled=true;session.Tts.Enabled=true;session.Submit("可以说话了");
   var speech=session.QueueTaskSpeech("acceptance-audio","你好，我会一边行动，一边和你聊天。",()=>true);float queued=Time.realtimeSinceStartup;deadline=queued+30;
   while((speech.Status=="queued"||speech.Status=="synthesizing")&&Time.realtimeSinceStartup<deadline)yield return null;
   Check("real_tts_audio_started",speech.Status=="playing"&&session.Voice.IsSpeaking,"first_audio_seconds="+(speech.Started-queued)+"; status="+speech.Status+"; error="+speech.Error);
   yield return Delay(0.3f);session.Submit("别说了");yield return Delay(0.3f);Check("silence_stops_actual_audio_and_queue",!session.Voice.IsSpeaking&&speech.Status=="cancelled");
   bool valid=true;var stale=session.QueueTaskSpeech("acceptance-expired","这条过期语音不应该播放。",()=>valid);valid=false;yield return Delay(0.3f);Check("expired_speech_discarded_before_play",stale.Status=="cancelled"&&stale.Started==0);
   session.Submit("可以说话了");Runner.VoiceEnabled=true;
   var sc=new TaskContract{obligations=new[]{O("s","speak")}};
   Runner.SubmitContractTest("验收：任务实际发声","local r=game.speak('测试完成，这是一条真实的任务语音。');return {ok=r.ok}",sc);yield return WaitDone(35);
   Check("task_speak_has_real_playback_receipt",Runner.Evidence.status=="succeeded"&&Runner.Evidence.results.Find(r=>r.kind=="speak"&&r.status=="succeeded")!=null,Runner.Evidence.reason);
   // Replacing during actual in-flight synthesis must not leak old audio or receipts.
   Runner.SubmitContractTest("验收：旧任务等待语音","game.speak('这是一条旧任务语音，接下来马上会被新任务替换。');game.wait(10);return {ok=true}",new TaskContract{obligations=new[]{O("s","speak"),O("w","wait")}});
   yield return Delay(0.15f);oldCtx=Runner.Context;
   Runner.SubmitContractTest("验收：替换后相机回正","game.look_direction('forward');return {ok=true}",new TaskContract{obligations=new[]{new TaskObligation{id="g",source="验收",kind="look_direction",direction="forward",count=1}}});newCtx=Runner.Context;
   yield return WaitDone(10);yield return Delay(2);Check("replacement_cancels_old_tts_without_leak",Runner.Context==newCtx&&Runner.Evidence.status=="succeeded"&&!session.Voice.IsSpeaking&&Runner.Evidence.results.FindAll(r=>r.kind!="report").Count==1);
   var fixture=new GameObject("RuntimeInteractionFixture");fixture.transform.position=Runner.Motor.transform.position+Runner.Motor.transform.forward;var marker=fixture.AddComponent<TaskDestinationMarker>();marker.Id="acceptance_interaction";marker.DisplayName="验收按钮";var interaction=fixture.AddComponent<TaskInteractable>();
   var ic=new TaskContract{obligations=new[]{O("i","interact","acceptance_interaction")}};
   Runner.SubmitContractTest("验收：交互幂等","game.interact('acceptance_interaction',{key='same-key'});game.interact('acceptance_interaction',{key='same-key'});return {ok=true}",ic);yield return WaitDone(10);
   Check("duplicate_interaction_does_not_repeat_effect",Runner.Evidence.status=="succeeded"&&interaction.CompletionCount==1,"actual_count="+interaction.CompletionCount+"; "+Runner.Evidence.reason);Destroy(fixture);
   if(Runner.IsRunning)Runner.Cancel("acceptance_finished");session.CancelDialogueTurn();session.Config.VoiceEnabled=oldVoice;session.Tts.Enabled=oldTts;session.ParallelTaskDialogue=oldParallel;session.InputMode=oldMode;Runner.VoiceEnabled=oldTaskVoice;Report.done=true;Running=false;Save();
  }
 }
}
