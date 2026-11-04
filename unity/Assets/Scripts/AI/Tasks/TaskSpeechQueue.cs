using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
namespace Yixuan.AI.Tasks {
 public sealed class TaskSpeechTicket {
  public string Id=Guid.NewGuid().ToString("N"),Owner,Text,Status="queued",Error;public TaskRuntimeContext Context;public int Generation;public float Queued,Started,Ended;public Func<bool> Valid;
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
  public TaskSpeechTicket Enqueue(string owner,string text,Func<bool> valid){
   var t=new TaskSpeechTicket{Owner=owner,Text=text,Context=Session!=null&&Session.Missions!=null?Session.Missions.Context:null,Generation=generation,Valid=valid,Queued=Time.realtimeSinceStartup};
   if(!isActiveAndEnabled){t.Status="cancelled";t.Error="speech_queue_disabled";t.Ended=Time.realtimeSinceStartup;return t;}
   queue.Enqueue(t);if(Session!=null)Session.SpeechNotice(t,"Queued");if(pump==null)pump=StartCoroutine(Pump());return t;
  }
  bool Valid(TaskSpeechTicket t){return t.Generation==generation&&(t.Valid==null||t.Valid());}
  public void CancelDialogue(){foreach(var t in queue)if(t.Owner!=null&&t.Owner.StartsWith("dialogue-"))t.Status="cancelled";if(active!=null){active.Status="cancelled";StopVoice();}}
  public void Cancel(string owner=null){
   if(owner==null)generation++;
   foreach(var t in queue)if(owner==null||t.Owner==owner)t.Status="cancelled";
   if(active!=null&&(owner==null||active.Owner==owner)){active.Status="cancelled";StopVoice();}
  }
  void StopVoice(){if(active!=null&&Session!=null&&Session.Voice!=null)using(TaskAuthority.Enter(active.Id))Session.Voice.Stop();}
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
    while(queue.Count>0){var t=queue.Dequeue();if(t.Status=="cancelled"||!Valid(t)){t.Status="cancelled";t.Ended=Time.realtimeSinceStartup;if(Session!=null)Session.SpeechNotice(t,"Finished");continue;}active=t;
     if(Session==null||Session.Tts==null||Session.Voice==null){t.Status="failed";t.Error="speech_components_missing";FinishActive();continue;}
     while(!TaskAuthority.Acquire(t.Id,4)){if(!Valid(t)||t.Status=="cancelled")break;yield return null;}
     if(!Valid(t)||t.Status=="cancelled"){t.Status="cancelled";FinishActive();continue;}
     t.Status="synthesizing";Session.SpeechNotice(t,"Synthesizing");clip=null;string error=null;
     yield return Drive(t,Session.Tts.Synthesize(t.Text,"neutral","",0.4f,c=>{if(Valid(t)&&t.Status!="cancelled")clip=c;else if(c!=null)Destroy(c);},e=>error=e));
     if(clip!=null&&Valid(t)&&t.Status!="cancelled"){
      using(TaskAuthority.Enter(t.Id))Session.Voice.Play(clip,Session.Config.VoiceVolume);
      if(!Session.Voice.IsSpeaking){t.Status="failed";t.Error="audio_playback_not_started";FinishActive();continue;}
      t.Status="playing";t.Started=Time.realtimeSinceStartup;Session.SpeechNotice(t,"Started");
      float deadline=Time.realtimeSinceStartup+clip.length+2;
      while(Session.Voice!=null&&Session.Voice.IsSpeaking&&Valid(t)&&t.Status!="cancelled"&&Time.realtimeSinceStartup<deadline)yield return null;
      if(t.Status!="cancelled")t.Status=Valid(t)?"succeeded":"cancelled";
     }else if(t.Status!="cancelled"){t.Status=Valid(t)?"failed":"cancelled";t.Error=error??"tts_failed";}
     FinishActive();
    }
   }finally{DisposeRoutines();FinishActive();pump=null;}
  }
  IEnumerator Drive(TaskSpeechTicket t,IEnumerator routine){
   routines.Push(routine);
   try{while(routines.Count>0&&Valid(t)&&t.Status!="cancelled"){
    var top=routines.Peek();if(!top.MoveNext()){var d=routines.Pop() as IDisposable;if(d!=null)d.Dispose();continue;}
    var nested=top.Current as IEnumerator;if(nested!=null){routines.Push(nested);continue;}
    var async=top.Current as AsyncOperation;if(async!=null){while(!async.isDone&&Valid(t)&&t.Status!="cancelled")yield return null;}else yield return top.Current;
   }}finally{DisposeRoutines();}
  }
  void OnDisable(){
   Cancel();if(pump!=null){StopCoroutine(pump);pump=null;}
   DisposeRoutines();FinishActive();queue.Clear();
  }
 }
}
