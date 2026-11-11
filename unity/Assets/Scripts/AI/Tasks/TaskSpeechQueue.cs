using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
namespace Yixuan.AI.Tasks {
 public sealed class TaskSpeechTicket {
  public string Id=Guid.NewGuid().ToString("N"),Owner,Text,Status="queued",Error;public TaskRuntimeContext Context;public int Generation;public long VoiceTurnId;public float Queued,Started,Ended,StartDeadline;public Func<bool> Valid;
 }
 public sealed class TaskSpeechQueue:MonoBehaviour {
  public ChatSession Session;
  public TaskSpeechTicket ActiveTicket{get{return active;}}
  public int PendingCount{get{return queue.Count;}}
  readonly Queue<TaskSpeechTicket> queue=new Queue<TaskSpeechTicket>();
  // Keep the nested iterators explicitly: stopping a Unity coroutine must also
  // dispose the iterator that owns the in-flight UnityWebRequest.
  readonly Stack<IEnumerator> routines=new Stack<IEnumerator>();
  TaskSpeechTicket active;int generation;Coroutine pump;AudioClip clip;
  public TaskSpeechTicket Enqueue(string owner,string text,Func<bool> valid,float startWithinSeconds=0){
   var t=new TaskSpeechTicket{Owner=owner,Text=text,Context=Session!=null&&Session.Missions!=null?Session.Missions.Context:null,Generation=generation,Valid=valid,Queued=Time.realtimeSinceStartup};
   if(!isActiveAndEnabled){t.Status="cancelled";t.Error="speech_queue_disabled";t.Ended=Time.realtimeSinceStartup;return t;}
   if(startWithinSeconds>0)t.StartDeadline=t.Queued+startWithinSeconds;
   queue.Enqueue(t);if(Session!=null)Session.SpeechNotice(t,"Queued");if(pump==null)pump=StartCoroutine(Pump());return t;
  }
  bool Valid(TaskSpeechTicket t){return t.Generation==generation&&Session!=null&&Session.Config!=null&&Session.Config.VoiceEnabled&&(t.Valid==null||t.Valid());}
  bool ReadyToStart(TaskSpeechTicket t){
   if(!Valid(t))return false;
   if(t.StartDeadline>0&&Time.realtimeSinceStartup>=t.StartDeadline){t.Error="speech_start_expired";return false;}
   return true;
  }
  public void CancelDialogue(){foreach(var t in queue)if(t.Owner!=null&&t.Owner.StartsWith("dialogue-"))t.Status="cancelled";if(active!=null&&active.Owner!=null&&active.Owner.StartsWith("dialogue-")){active.Status="cancelled";StopVoice();}}
  public void Cancel(string owner=null){
   if(owner==null)generation++;
   foreach(var t in queue)if(owner==null||t.Owner==owner)t.Status="cancelled";
   if(active!=null&&(owner==null||active.Owner==owner)){active.Status="cancelled";StopVoice();}
  }
  void StopVoice(){
   if(active==null||Session==null)return;
   if(active.VoiceTurnId>0&&Session.VoiceInput!=null&&active.Status!="succeeded"&&Session.isActiveAndEnabled)Session.StartCoroutine(Session.VoiceInput.CancelSay(active.VoiceTurnId));
   if(Session.Voice!=null)using(TaskAuthority.Enter(active.Id))Session.Voice.Stop();
  }
  void FinishActive(){
   if(active==null)return;var t=active;
   StopVoice();if(clip!=null)Destroy(clip);clip=null;
   if(t.Status=="queued"||t.Status=="synthesizing"||t.Status=="playing")t.Status="cancelled";
   t.Ended=Time.realtimeSinceStartup;TaskAuthority.Release(t.Id);active=null;
   if(Session!=null)Session.SpeechNotice(t,"Finished");
  }
  void DisposeRoutines(){while(routines.Count>0){var d=routines.Pop() as IDisposable;if(d!=null)d.Dispose();}}
  IEnumerator Pump(){
   // StartCoroutine may run synchronously until first yield.
   yield return null;
   try{
    while(queue.Count>0){var t=queue.Dequeue();if(t.Status=="cancelled"||!ReadyToStart(t)){t.Status="cancelled";t.Ended=Time.realtimeSinceStartup;if(Session!=null)Session.SpeechNotice(t,"Finished");continue;}active=t;
     bool duplex=Session!=null&&Session.Config!=null&&Session.Config.VoiceInputMode=="duplex"&&Session.VoiceInput!=null;
     if(Session==null||(!duplex&&(Session.Tts==null||Session.Voice==null))){t.Status="failed";t.Error="speech_components_missing";FinishActive();continue;}
     while(!TaskAuthority.Acquire(t.Id,4)){if(!ReadyToStart(t)||t.Status=="cancelled")break;yield return null;}
     if(!ReadyToStart(t)||t.Status=="cancelled"){t.Status="cancelled";FinishActive();continue;}
     if(duplex){yield return Stream(t);FinishActive();continue;}
     t.Status="synthesizing";Session.SpeechNotice(t,"Synthesizing");clip=null;string error=null;
     yield return Drive(t,Session.Tts.Synthesize(t.Text,"neutral","",0.4f,c=>{if(ReadyToStart(t)&&t.Status!="cancelled")clip=c;else if(c!=null)Destroy(c);},e=>error=e));
     if(clip!=null&&ReadyToStart(t)&&t.Status!="cancelled"){
      using(TaskAuthority.Enter(t.Id))Session.Voice.Play(clip,Session.Config.VoiceVolume);
      if(!Session.Voice.IsSpeaking){t.Status="failed";t.Error="audio_playback_not_started";FinishActive();continue;}
      t.Status="playing";t.Started=Time.realtimeSinceStartup;Session.SpeechNotice(t,"Started");
      float deadline=Time.realtimeSinceStartup+clip.length+2;
      while(Session.Voice!=null&&Session.Voice.IsSpeaking&&Valid(t)&&t.Status!="cancelled"&&Time.realtimeSinceStartup<deadline)yield return null;
      if(t.Status!="cancelled")t.Status=Valid(t)?"succeeded":"cancelled";
     }else if(t.Status!="cancelled"){t.Status=ReadyToStart(t)?"failed":"cancelled";t.Error=error??t.Error??"tts_failed";}
     FinishActive();
    }
   }finally{DisposeRoutines();FinishActive();pump=null;}
  }
  void NoticeStreamStart(TaskSpeechTicket t){
   if(t.Status=="cancelled"||t.Started>0||Session==null||Session.VoiceInput==null||!Session.VoiceInput.HasTurnStarted(t.VoiceTurnId))return;
   t.Started=Session.VoiceInput.TurnStartedAt(t.VoiceTurnId);t.Status="playing";Session.SpeechNotice(t,"Started");
  }
  bool CanDrive(TaskSpeechTicket t){
   NoticeStreamStart(t);
   return t.Status!="cancelled"&&(t.Started>0?Valid(t):ReadyToStart(t));
  }
  IEnumerator Stream(TaskSpeechTicket t){
   var output=Session.VoiceInput;t.VoiceTurnId=Yixuan.Voice.VoiceInputController.NextTurnId();
   t.Status="synthesizing";Session.SpeechNotice(t,"Synthesizing");bool accepted=false;string error=null;
   // The semantic reply is already validated. Stream its audio through the same
   // sidecar that supplies playback reference to AEC; never speak raw JSON.
   yield return Drive(t,output.SayQueue(t.Text,"neutral","",0.4f,1,true,t.VoiceTurnId,(ok,err)=>{accepted=ok;error=err;}));
   NoticeStreamStart(t);
   if(!CanDrive(t)){t.Status="cancelled";t.Error=t.Error??"speech_invalidated";yield break;}
   if(!accepted){t.Status="failed";t.Error=error??"tts_queue_rejected";yield break;}
   yield return Drive(t,output.SayFinish(t.VoiceTurnId,1));
   float deadline=Time.realtimeSinceStartup+120;
   while(output.IsTurnActive(t.VoiceTurnId)&&CanDrive(t)&&Time.realtimeSinceStartup<deadline)yield return null;
   NoticeStreamStart(t);
   if(!CanDrive(t)){t.Status="cancelled";t.Error=t.Error??"speech_invalidated";}
   else if(output.IsTurnActive(t.VoiceTurnId)){t.Status="failed";t.Error="tts_completion_timeout";}
   else if(!string.IsNullOrEmpty(output.TurnError(t.VoiceTurnId))){t.Status="failed";t.Error=output.TurnError(t.VoiceTurnId);}
   else if(t.Started<=0){t.Status="failed";t.Error="audio_playback_not_started";}
   else t.Status="succeeded";
  }
  IEnumerator Drive(TaskSpeechTicket t,IEnumerator routine){
   routines.Push(routine);
   try{while(routines.Count>0&&CanDrive(t)){
    var top=routines.Peek();if(!top.MoveNext()){var d=routines.Pop() as IDisposable;if(d!=null)d.Dispose();continue;}
    var nested=top.Current as IEnumerator;if(nested!=null){routines.Push(nested);continue;}
    var async=top.Current as AsyncOperation;if(async!=null){while(!async.isDone&&CanDrive(t))yield return null;}else yield return top.Current;
   }}finally{DisposeRoutines();}
  }
  void OnDisable(){
   Cancel();if(pump!=null){StopCoroutine(pump);pump=null;}
   DisposeRoutines();FinishActive();queue.Clear();
  }
 }
}
