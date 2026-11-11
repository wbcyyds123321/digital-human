using System;
using System.Collections;
using System.IO;
using System.Reflection;
using UnityEngine;
namespace Yixuan.AI.Tasks {
 public sealed class TaskLongSpeechAcceptance:MonoBehaviour {
  public string Output="Captures/Acceptance/long_speech_2026-11-05",Text,DelayedEndpoint;
  public bool Running;public string Current;public RuntimeTestReport Report;
  ChatSession session;TaskSpeechQueue speech;string endpoint;bool voiceEnabled,ttsEnabled,inputEnabled,brainEnabled;
  static float Now {get{return Time.realtimeSinceStartup;}}
  string Dir {get{return Path.GetFullPath(Path.Combine(Application.dataPath,"..",Output));}}
  public void Begin(){
   if(Running)return;session=FindObjectOfType<ChatSession>();session.CancelDialogueTurn();
   speech=session.GetComponent<TaskSpeechQueue>();if(speech==null)speech=session.gameObject.AddComponent<TaskSpeechQueue>();speech.Session=session;
   endpoint=session.Tts.Endpoint;voiceEnabled=session.Config.VoiceEnabled;ttsEnabled=session.Tts.Enabled;
   inputEnabled=session.VoiceInput!=null&&session.VoiceInput.Enabled;brainEnabled=session.Brain!=null&&session.Brain.EnableProactive;
   if(session.VoiceInput!=null)session.VoiceInput.SetEnabled(false);if(session.Brain!=null)session.Brain.EnableProactive=false;
   session.SetVoiceOutputEnabled(true);Report=new RuntimeTestReport{suite="Original long narration via PublishNarrator + actual MiniMax audio and Unity playback; local delayed HTTP fixture; no LLM or ASR"};
   Running=true;Directory.CreateDirectory(Dir);StartCoroutine(Run());
  }
  void Save(){File.WriteAllText(Path.Combine(Dir,"acceptance_report.json"),JsonUtility.ToJson(Report,true));File.WriteAllText(Path.Combine(Dir,"checkpoint.txt"),Current+" passed="+Report.cases.FindAll(x=>x.pass).Count+"/"+Report.cases.Count);}
  void Check(string name,bool pass,string detail){Report.cases.Add(new RuntimeTestResult{name=name,pass=pass,expected="true",actual=detail});Save();Debug.Log("[LongSpeechAcceptance] "+(pass?"PASS ":"FAIL ")+name+" "+detail);}
  IEnumerator Delay(float seconds){float until=Now+seconds;while(Now<until)yield return null;}
  IEnumerator WaitStart(TaskSpeechTicket t){float until=Now+22;while((t.Status=="queued"||t.Status=="synthesizing")&&Now<until)yield return null;}
  IEnumerator WaitEnd(TaskSpeechTicket t,float seconds=35){float until=Now+seconds;while((t.Status=="queued"||t.Status=="synthesizing"||t.Status=="playing")&&Now<until)yield return null;}
  string State(TaskSpeechTicket t){return "status="+t.Status+" error="+t.Error+" age="+(Now-t.Queued).ToString("F3")+" played="+(t.Started>0?(t.Ended>0?t.Ended:Now)-t.Started:0).ToString("F3")+" speaking="+session.Voice.IsSpeaking;}
  IEnumerator Run(){
   try{
    Current="original_long_narration";Save();
    int gen=(int)typeof(ChatSession).GetField("_narratorGeneration",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(session);
    typeof(ChatSession).GetMethod("PublishNarrator",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(session,new object[]{Text,null,gen,false,false});
    float until=Now+22;while(speech.ActiveTicket==null&&Now<until)yield return null;
    var t=speech.ActiveTicket;Check("narrator_queues_complete_original_text",t!=null&&t.Text==Text&&t.StartDeadline>t.Queued,session.LastReply);
    if(t==null)yield break;yield return WaitStart(t);
    float length=session.Voice.Source.clip!=null?session.Voice.Source.clip.length:0;
    Check("real_tts_long_clip_starts",t.Status=="playing"&&length>20,State(t)+" clip="+length.ToString("F3"));
    until=t.Queued+10.5f;while(Now<until&&t.Status=="playing")yield return null;
    Check("narrator_continues_beyond_ten_second_age",t.Status=="playing"&&session.Voice.IsSpeaking&&Now>=t.StartDeadline,State(t));
    float peak=0;until=Now+32;
    while(t.Status=="playing"&&Now<until){if(session.Voice.Source.isPlaying)peak=Mathf.Max(peak,session.Voice.Source.time);yield return null;}
    Check("complete_long_audio_reaches_tail",t.Status=="succeeded"&&peak>=length-0.25f&&(t.Ended-t.Started)>=length-0.25f,State(t)+" clip="+length.ToString("F3")+" peakAudioTime="+peak.ToString("F3"));
    Check("completed_queue_releases_audio_and_authority",!session.Voice.IsSpeaking&&session.Voice.Source.clip==null&&speech.ActiveTicket==null&&TaskAuthority.Allows(4)&&session.LastSpeechStatus=="播放完成",session.LastSpeechStatus);
    Current="start_expiry";Save();
    const string blocker="long_speech_acceptance_blocker";bool held=TaskAuthority.Acquire(blocker,4);
    try{t=session.QueueTaskSpeech("dialogue-expired",Text,()=>true,0.25f);yield return WaitEnd(t,2);Check("expired_before_voice_lease_never_starts",held&&t.Status=="cancelled"&&t.Started==0&&t.Error=="speech_start_expired",State(t));}
    finally{TaskAuthority.Release(blocker);}
    Check("expired_waiter_does_not_replay_later",!session.Voice.IsSpeaking&&speech.ActiveTicket==null&&TaskAuthority.Allows(4),State(t));
    if(!string.IsNullOrEmpty(DelayedEndpoint)){
     session.Tts.Endpoint=DelayedEndpoint;t=session.QueueTaskSpeech("dialogue-synthesis-expired",Text,()=>true,0.35f);yield return WaitEnd(t,3);
     Check("expiry_during_synthesis_disposes_without_audio",t.Status=="cancelled"&&t.Started==0&&t.Error=="speech_start_expired"&&speech.ActiveTicket==null&&TaskAuthority.Allows(4),State(t));
     yield return Delay(1.3f);Check("late_synthesis_response_never_starts_audio",!session.Voice.IsSpeaking&&speech.ActiveTicket==null,State(t));session.Tts.Endpoint=endpoint;
    }
    Current="explicit_cancellation";Save();
    t=session.QueueTaskSpeech("dialogue-new-input",Text,()=>true,10f);yield return WaitStart(t);Check("replacement_fixture_starts",t.Status=="playing",State(t));
    session.CancelDialogueTurn();yield return Delay(0.15f);Check("new_input_cancels_active_long_audio",t.Status=="cancelled"&&!session.Voice.IsSpeaking&&speech.ActiveTicket==null&&TaskAuthority.Allows(4),State(t));
    t=session.QueueTaskSpeech("dialogue-mute",Text,()=>true,10f);yield return WaitStart(t);Check("mute_fixture_starts",t.Status=="playing",State(t));
    session.SetVoiceOutputEnabled(false);yield return Delay(0.15f);Check("voice_output_off_cancels_active_long_audio",t.Status=="cancelled"&&!session.Voice.IsSpeaking&&speech.ActiveTicket==null,State(t));session.SetVoiceOutputEnabled(true);
    bool valid=true;t=session.QueueTaskSpeech("mission-validity",Text,()=>valid,10f);yield return WaitStart(t);Check("validity_fixture_starts",t.Status=="playing",State(t));
    valid=false;yield return Delay(0.15f);Check("mission_invalidated_still_stops_playback",t.Status=="cancelled"&&!session.Voice.IsSpeaking&&speech.ActiveTicket==null&&TaskAuthority.Allows(4),State(t));
    t=session.QueueTaskSpeech("dialogue-disabled",Text,()=>true,10f);yield return WaitStart(t);speech.enabled=false;yield return Delay(0.15f);
    Check("component_disable_stops_and_cleans_playback",t.Status=="cancelled"&&!session.Voice.IsSpeaking&&speech.ActiveTicket==null&&speech.PendingCount==0&&TaskAuthority.Allows(4),State(t));speech.enabled=true;
    Check("queue_reenabled_without_late_playback",!session.Voice.IsSpeaking&&speech.ActiveTicket==null,State(t));
    Current="finished";
   }finally{
    session.CancelDialogueTurn();speech.enabled=true;session.Tts.Endpoint=endpoint;session.SetVoiceOutputEnabled(voiceEnabled);session.Tts.Enabled=ttsEnabled;
    if(session.VoiceInput!=null)session.VoiceInput.SetEnabled(inputEnabled);if(session.Brain!=null)session.Brain.EnableProactive=brainEnabled;
    Report.done=true;Running=false;Save();
   }
  }
 }
}
