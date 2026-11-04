using System;
using System.Collections;
using System.IO;
using UnityEngine;
namespace Yixuan.AI.Tasks {
 public sealed class TaskRuntimeRouteAcceptance:MonoBehaviour {
  public bool Running;public RuntimeTestReport Report;public string Output="Captures/Evidence/task_runtime_v2_2026-10-25";TaskMissionRunner runner;
  public void Begin(){if(Running)return;runner=FindObjectOfType<TaskMissionRunner>();Running=true;Report=new RuntimeTestReport{suite="real configured core LLM semantic auto route; hypothetical/status vs imperative, no planner/code injection for tested user input"};StartCoroutine(Run());}
  IEnumerator Case(string name,string text,bool action){var old=runner.Context;int seq=old.Events.Count;var startPosition=runner.Motor.transform.position;float startYaw=runner.Motor.transform.eulerAngles.y,startCameraYaw=runner.Gimbal.TargetYaw,startCameraPitch=runner.Gimbal.TargetPitch;float start=Time.realtimeSinceStartup;runner.Session.Submit(text);float limit=start+75;
   while(Time.realtimeSinceStartup<limit){if(action){if(runner.Context!=old&&!runner.IsRunning)break;}else if(old.Events.Find(e=>e.type=="NarratorClassified"&&e.sequence>seq)!=null||runner.Context!=old)break;yield return null;}
   var classified=old.Events.FindLast(e=>e.type=="NarratorClassified"&&e.sequence>seq);
   bool ok=classified!=null&&(action?runner.Context!=old&&runner.Evidence.status=="succeeded"&&classified.detail.StartsWith("action|"):runner.Context==old&&!runner.Motor.HasTarget&&TaskTargetCatalog.FlatDistance(startPosition,runner.Motor.transform.position)<0.05f&&Mathf.Abs(Mathf.DeltaAngle(startYaw,runner.Motor.transform.eulerAngles.y))<1&&Mathf.Abs(startCameraYaw-runner.Gimbal.TargetYaw)<0.1f&&Mathf.Abs(startCameraPitch-runner.Gimbal.TargetPitch)<0.1f&&(classified.detail.StartsWith("chat|")||classified.detail.StartsWith("status|")));Report.cases.Add(new RuntimeTestResult{name=name,expected=action?"action succeeded":"no new action",actual=runner.Context==old?"no new action":runner.Evidence.status,pass=ok,reason=classified!=null?classified.detail:"classification missing",directory=runner.RunDirectory,elapsed=Time.realtimeSinceStartup-start});File.WriteAllText(Path.Combine(Output,"route_report.json"),JsonUtility.ToJson(Report,true));runner.Session.CancelDialogueTurn();yield return null;
  }
  IEnumerator Run(){var s=runner.Session;var mode=s.InputMode;bool enabled=s.TaskPlanningEnabled,voice=s.Config.VoiceEnabled,ack=s.TaskAcknowledgmentEnabled;s.TaskPlanningEnabled=true;s.InputMode=TaskInputMode.Auto;s.Config.VoiceEnabled=false;s.TaskAcknowledgmentEnabled=false;runner.EvidenceRoot=Path.Combine(Output,"route");
   runner.SubmitContractTest("验收：路由准备","game.look_direction('forward');return {ok=true}",new TaskContract{obligations=new[]{new TaskObligation{id="g",source="验收",kind="look_direction",direction="forward",count=1}}});while(runner.IsRunning)yield return null;
   yield return Case("hypothetical_is_not_action","假如我让你走到红绿灯，你会怎么规划？现在先别执行。",false);
   yield return Case("previous_failure_question_is_not_action","你为什么刚才没走到便利店门口？",false);
   yield return Case("abstract_gaze_without_old_regex","把视线朝上挪一点，身体保持原地，不要说话。",true);
   yield return Case("abstract_gaze_then_recenter","不要说话，抬头仰望天空，之后把视线放回正前方。",true);
   s.InputMode=mode;s.TaskPlanningEnabled=enabled;s.Config.VoiceEnabled=voice;s.TaskAcknowledgmentEnabled=ack;Report.done=true;Running=false;File.WriteAllText(Path.Combine(Output,"route_report.json"),JsonUtility.ToJson(Report,true));
  }
 }
}
