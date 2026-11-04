using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
namespace Yixuan.AI.Tasks {
 [Serializable] public sealed class TaskLaneMetric{public int pair;public bool parallel,task_succeeded,audio_started;public float input_at,first_reply_seconds,first_audio_seconds,task_seconds;public string directory;}
 [Serializable] public sealed class TaskLaneMetrics{public string scope="3 matched pairs: text input during fixed 6-second real Unity wait+gaze task; real DeepSeek dialogue and TTS; no microphone/ASR latency; fixed order, small sample, cache/network confounded";public bool done;public List<TaskLaneMetric> trials=new List<TaskLaneMetric>();}
 public sealed class TaskRuntimePairedAcceptance:MonoBehaviour {
  public bool Running;public TaskLaneMetrics Report;public string Output="Captures/Evidence/task_runtime_v2_2026-10-25";
  public void Begin(){if(Running)return;Running=true;Report=new TaskLaneMetrics();StartCoroutine(Run());}
  IEnumerator Run(){var r=FindObjectOfType<TaskMissionRunner>();var s=r.Session;bool oldP=s.ParallelTaskDialogue,oldTts=s.Tts.Enabled,oldV=s.Config.VoiceEnabled,oldAck=s.TaskAcknowledgmentEnabled;var oldMode=s.InputMode;
   s.Config.VoiceEnabled=true;s.Tts.Enabled=true;s.TaskAcknowledgmentEnabled=false;s.InputMode=TaskInputMode.Dialogue;s.Submit("可以说话了");r.EvidenceRoot=Path.Combine(Output,"paired");
   for(int pair=0;pair<3;pair++)for(int mode=0;mode<2;mode++){
    s.ParallelTaskDialogue=mode==1;var c=new TaskContract{obligations=new[]{new TaskObligation{id="w",kind="wait",source="验收",count=1,min_duration=6},new TaskObligation{id="g",kind="look_direction",direction="forward",source="验收",count=1,after=new[]{"w"}}}};
    r.SubmitContractTest("验收：固定等待后回正","game.wait(6);game.look_direction('forward');return {ok=true}",c);var ctx=r.Context;
    yield return null;var m=new TaskLaneMetric{pair=pair+1,parallel=mode==1,input_at=Time.realtimeSinceStartup,first_reply_seconds=-1,first_audio_seconds=-1,directory=r.RunDirectory};s.Submit("请用一句话聊聊你喜欢的天气。");
    float limit=Time.realtimeSinceStartup+35;while(Time.realtimeSinceStartup<limit){foreach(var e in ctx.Events){if(e.type=="NarratorReply"&&m.first_reply_seconds<0)m.first_reply_seconds=e.at-m.input_at;if(e.type=="SpeechStarted"&&e.detail.Contains("dialogue-")&&m.first_audio_seconds<0){m.first_audio_seconds=e.at-m.input_at;m.audio_started=true;}}if(m.audio_started&&!r.IsRunning)break;yield return null;}
    m.task_succeeded=r.Evidence.status=="succeeded";m.task_seconds=r.Evidence.elapsed;Report.trials.Add(m);File.WriteAllText(Path.Combine(Output,"paired_metrics.json"),JsonUtility.ToJson(Report,true));s.CancelDialogueTurn();if(r.IsRunning)r.Cancel("paired_timeout");yield return null;
   }
   s.ParallelTaskDialogue=oldP;s.Config.VoiceEnabled=oldV;s.Tts.Enabled=oldTts;s.TaskAcknowledgmentEnabled=oldAck;s.InputMode=oldMode;Report.done=true;Running=false;File.WriteAllText(Path.Combine(Output,"paired_metrics.json"),JsonUtility.ToJson(Report,true));
  }
 }
}
