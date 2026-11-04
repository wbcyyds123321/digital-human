using System;
using System.Collections;
using System.IO;
using System.Reflection;
using UnityEngine;
namespace Yixuan.AI.Tasks {
 public sealed class TaskRuntimeLifecycleAcceptance:MonoBehaviour {
  public string Output="Captures/Evidence/task_runtime_v2_2026-10-25",DelayedEndpoint;
  public RuntimeTestReport Report;public bool Running;
  ChatSession session;TaskSpeechQueue speech;string oldEndpoint;bool oldVoice,oldInput,oldTts;
  public void Begin(){if(Running)return;session=FindObjectOfType<ChatSession>();speech=session.GetComponent<TaskSpeechQueue>();if(speech==null)speech=session.gameObject.AddComponent<TaskSpeechQueue>();speech.Session=session;Running=true;Report=new RuntimeTestReport{suite="explicit lifecycle disposal, delayed local HTTP synthesis + actual TTS playback; not microphone ASR"};StartCoroutine(Run());}
  void Check(string name,bool ok,string reason=""){Report.cases.Add(new RuntimeTestResult{name=name,pass=ok,expected="true",actual=ok.ToString(),reason=reason});Save();}
  void Save(){Directory.CreateDirectory(Output);File.WriteAllText(Path.Combine(Output,"lifecycle_report.json"),JsonUtility.ToJson(Report,true));}
  IEnumerator Delay(float seconds){float end=Time.realtimeSinceStartup+seconds;while(Time.realtimeSinceStartup<end)yield return null;}
  object Field(object obj,string name){return obj.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic).GetValue(obj);}
  IEnumerator Run(){
   oldEndpoint=session.Tts.Endpoint;oldVoice=session.Config.VoiceEnabled;oldTts=session.Tts.Enabled;oldInput=session.VoiceInput.Enabled;
   try{
    session.Config.VoiceEnabled=true;session.Tts.Enabled=true;var input=session.VoiceInput;input.SetEnabled(true);yield return Delay(0.15f);
    input.SetEnabled(false);yield return null;Check("poll_stop_disposes_request",Field(input,"_pollRequest")==null&&Field(input,"_poll")==null);
    input.SetEnabled(true);yield return Delay(0.15f);input.enabled=false;yield return null;Check("poll_component_disable_disposes_request",Field(input,"_pollRequest")==null&&Field(input,"_poll")==null);
    input.enabled=true;yield return Delay(0.2f);Check("poll_component_reenable_restarts",Field(input,"_poll")!=null);input.SetEnabled(false);
    session.Tts.Endpoint=DelayedEndpoint;var pending=session.QueueTaskSpeech("lifecycle-delay","延迟请求用于验证销毁时主动释放。",()=>true);yield return Delay(0.5f);
    Check("delayed_synthesis_in_flight",pending.Status=="synthesizing",pending.Status);speech.enabled=false;yield return Delay(0.2f);
    Check("disable_during_synthesis_releases",pending.Status=="cancelled"&&speech.ActiveTicket==null&&speech.PendingCount==0&&TaskAuthority.Allows(4)&&!session.Voice.IsSpeaking);
    var disabled=session.QueueTaskSpeech("lifecycle-disabled","禁用时不能发声。",()=>true);Check("disabled_queue_rejects_enqueue",disabled.Status=="cancelled"&&disabled.Error=="speech_queue_disabled");
    speech.enabled=true;session.Tts.Endpoint=oldEndpoint;var playing=session.QueueTaskSpeech("lifecycle-real-audio","你好，我会一边行动，一边和你聊天。",()=>true);float deadline=Time.realtimeSinceStartup+30;
    while((playing.Status=="queued"||playing.Status=="synthesizing")&&Time.realtimeSinceStartup<deadline)yield return null;
    Check("reenable_starts_real_audio",playing.Status=="playing"&&session.Voice.IsSpeaking,playing.Status+"|"+playing.Error);speech.enabled=false;yield return Delay(0.2f);
    Check("disable_during_playback_cleans_audio",playing.Status=="cancelled"&&!session.Voice.IsSpeaking&&session.Voice.Source.clip==null&&TaskAuthority.Allows(4));
    yield return Delay(1);Check("cancelled_synthesis_has_no_late_audio",!session.Voice.IsSpeaking&&speech.ActiveTicket==null);
    speech.enabled=true;var last=session.QueueTaskSpeech("lifecycle-restart","测试完成，这是一条真实的任务语音。",()=>true);deadline=Time.realtimeSinceStartup+35;
    while(last.Status!="succeeded"&&last.Status!="failed"&&last.Status!="cancelled"&&Time.realtimeSinceStartup<deadline)yield return null;
    Check("second_reenable_finishes_real_speech",last.Status=="succeeded"&&last.Started>0&&!session.Voice.IsSpeaking,last.Status+"|"+last.Error);
    yield return null;Check("queue_has_no_remaining_pump_or_iterators",speech.ActiveTicket==null&&Field(speech,"pump")==null&&((System.Collections.ICollection)Field(speech,"routines")).Count==0&&TaskAuthority.Allows(4));
   }finally{session.Tts.Endpoint=oldEndpoint;session.Tts.Enabled=oldTts;session.Config.VoiceEnabled=oldVoice;session.VoiceInput.SetEnabled(oldInput);speech.enabled=true;Report.done=true;Running=false;Save();}
  }
 }
}
