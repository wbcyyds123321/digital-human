using System;
using System.Collections;
using System.IO;
using UnityEngine;
namespace Yixuan.AI.Tasks {
 public sealed class TaskRuntimeRealAcceptance:MonoBehaviour {
  public TaskMissionRunner Runner;public RuntimeTestReport Report;public bool Running;public string Output="D:/AI/GameScaling/docs/evidence/task_runtime_v2_2026-10-25";
  public void Begin(){if(Running)return;Runner=FindObjectOfType<TaskMissionRunner>();Running=true;Report=new RuntimeTestReport{suite="real configured core LLM -> independent contract -> real Lua -> real Unity; no source injection"};StartCoroutine(Run());}
  void Save(){File.WriteAllText(Path.Combine(Output,"real_report.json"),JsonUtility.ToJson(Report,true));}
  IEnumerator Case(string name,string request,string expected){
   Runner.Submit(request);float until=Time.realtimeSinceStartup+180;while(Runner.IsRunning&&Time.realtimeSinceStartup<until)yield return null;if(Runner.IsRunning)Runner.Cancel("real_harness_timeout");
   var e=Runner.Evidence;Report.cases.Add(new RuntimeTestResult{name=name,expected=expected,actual=e.status,pass=expected=="not_succeeded"?e.status!="succeeded":e.status==expected,reason=e.reason,directory=Runner.RunDirectory,elapsed=e.elapsed});Save();yield return null;
  }
  IEnumerator Run(){
   Runner.Session.ParallelTaskDialogue=false;Runner.EvidenceRoot=Path.Combine(Output,"real");Runner.VoiceEnabled=false;
   yield return Case("sky_gaze","不要说话，站在原地抬头看一下天空","succeeded");
   yield return Case("sky_then_forward","不要说话，站在原地先抬头看天空，然后视角回正看前方","succeeded");
   yield return Case("home_wave_lamp","不要说话，先走回原点，然后做一个挥舞双手的动作，最后走到红绿灯之下","succeeded");
   yield return Case("stationary_lamp_near_high_target","不要说话，不要移动，看向红绿灯","not_succeeded");
   yield return Case("road_center_lamp_attention","不要说话，先走到街道中央，然后站在原地看向红绿灯","succeeded");
   yield return Case("blue_car_dynamic_target","不要说话，走到蓝色小轿车那里","succeeded");
   yield return Case("door_unreachable","不要说话，走到便利店门口","not_succeeded");
   yield return Case("absent_target_no_substitution","不要说话，走到紫色大象雕像旁边","not_succeeded");
   yield return Case("camera_only_car","不要说话，不要移动，也不要身体转向，只转相机看向蓝色小轿车","succeeded");
   yield return Case("repeat_loop","不要说话，站在原地，重复两次抬头看天空再回正","succeeded");
   yield return Case("visual_condition","不要说话，先抬头看天空并判断天空是不是黑色的，如果是黑色就回到原点，否则视角回正；如果无法判断就保持不动","succeeded");
   yield return Case("parallel_navigation_expression","不要说话，一边走到红绿灯下面，一边保持开心表情两秒","succeeded");
   Runner.Session.ParallelTaskDialogue=true;Report.done=true;Running=false;Save();
  }
 }
}
