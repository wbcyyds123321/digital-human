using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
namespace Yixuan.AI.Tasks {
 public sealed class TaskDelegatedWalkAcceptance:MonoBehaviour {
  public string Output="Captures/Acceptance/delegated_walk_2026-11-05";public bool Running;public string Current;public RuntimeTestReport Report;
  TaskMissionRunner runner;ChatSession session;Vector3 home;Quaternion rotation;float scale;string oldRoot;bool oldVoice,oldAck,oldTaskVoice,oldBrain,oldInput;TaskInputMode oldMode;
  static float Now {get{return Time.realtimeSinceStartup;}}
  string Dir {get{return Path.GetFullPath(Path.Combine(Application.dataPath,"..",Output));}}
  public void Begin(){
   if(Running)return;runner=FindObjectOfType<TaskMissionRunner>();runner.ResolveRefs();session=runner.Session;
   home=runner.Motor.transform.position;rotation=runner.Motor.transform.rotation;scale=Time.timeScale;oldRoot=runner.EvidenceRoot;oldVoice=session.Config.VoiceEnabled;oldAck=session.TaskAcknowledgmentEnabled;oldTaskVoice=runner.VoiceEnabled;oldMode=session.InputMode;
   oldInput=session.VoiceInput!=null&&session.VoiceInput.Enabled;oldBrain=session.Brain!=null&&session.Brain.EnableProactive;
   if(session.VoiceInput!=null)session.VoiceInput.SetEnabled(false);if(session.Brain!=null)session.Brain.EnableProactive=false;
   if(session.Vision!=null){session.Vision.SetGameView(false);session.Vision.SetWebcam(false);}
   session.Config.VoiceEnabled=false;session.TaskAcknowledgmentEnabled=false;session.InputMode=TaskInputMode.Auto;runner.VoiceEnabled=false;runner.PlannerOverride=null;session.NarratorOverride=null;runner.EvidenceRoot=Path.Combine(Output,"missions");
   Time.timeScale=4;Directory.CreateDirectory(Dir);Report=new RuntimeTestReport{suite="Actual configured LLM narrator + independent choice contract + Lua, real ChatSession.SubmitVoice and locomotion; travel at 4x time; no ASR audio or semantic vision calls"};Running=true;StartCoroutine(Run());
  }
  void Save(){File.WriteAllText(Path.Combine(Dir,"acceptance_report.json"),JsonUtility.ToJson(Report,true));File.WriteAllText(Path.Combine(Dir,"checkpoint.txt"),Current+" passed="+Report.cases.FindAll(x=>x.pass).Count+"/"+Report.cases.Count);}
  void Check(string name,bool pass,string detail){Report.cases.Add(new RuntimeTestResult{name=name,pass=pass,expected="true",actual=detail,directory=runner.RunDirectory});Save();Debug.Log("[DelegatedWalkAcceptance] "+(pass?"PASS ":"FAIL ")+name+" "+detail);}
  IEnumerator Delay(float sec){float end=Now+sec;while(Now<end)yield return null;}
  void ResetActor(){if(runner.IsRunning)runner.Cancel("acceptance_reset");session.CancelDialogueTurn();runner.StopAttention();using(TaskAuthority.Enter(null)){runner.Motor.Stop();runner.Motor.transform.position=home;runner.Motor.transform.rotation=rotation;}session.Brain.EnableProactive=false;}
  IEnumerator BeginRequest(string text){var previous=runner.Context;session.SubmitVoice(text);float end=Now+35;while((runner.Context==previous||runner.Contract==null||runner.Phase=="intent_contract"||runner.Phase=="planning_code")&&Now<end){if(runner.Context!=previous&&!runner.IsRunning)break;yield return null;}}
  bool ChosenVisits(string input){if(runner.Contract==null)return false;var ids=new HashSet<string>();int n=0;foreach(var o in runner.Contract.obligations){if(o.kind!="navigate")continue;n++;if(runner.Catalog.Get(o.target_id)==null||!input.Contains(o.source)||!ids.Add(o.target_id))return false;}return n>=2&&n<=4;}
  string Visits(){var list=new List<string>();foreach(var o in runner.Contract!=null?runner.Contract.obligations:new TaskObligation[0])if(o.kind=="navigate")list.Add(o.target_id);return string.Join(" -> ",list.ToArray());}
  bool Success {get{return !runner.IsRunning&&runner.Evidence!=null&&runner.Evidence.status=="succeeded"&&runner.Evidence.completion!=null&&runner.Evidence.completion.task_succeeded;}}
  IEnumerator Run(){
   try{
    Current="routing";Save();
    string[] positive={"是吗那你在四处走走看看","不要老停在这儿四处走走看看","在四处随便走走","别老往一个地走多走点别的地方附近哪些建筑喜欢的就往那边走走看看","你自己挑两个附近的地方去逛逛","wander around"};
    foreach(var text in positive)Check("routing/"+text,MissionIntent.RequiresTaskRuntime(text),"candidate="+MissionIntent.LooksLikeOpenMovement(text));
    string[] negative={"为什么不能四处随便走走","假如你四处随便走走会怎么样","请解释“随便四处走走”这句话","不要随便走走","请走到方尖碑","站在原地看向方尖碑不要走动"};
    foreach(var text in negative)Check("routing/no_open_choice/"+text,!MissionIntent.LooksLikeOpenMovement(text),"candidate="+MissionIntent.LooksLikeOpenMovement(text));
    Current="actual_original_request";ResetActor();Save();string request=positive[0];yield return BeginRequest(request);
    Check("real_model/original_reaches_task_runtime",runner.Context!=null&&runner.Evidence.request==request,runner.Evidence.status+"|"+runner.Evidence.reason);
    Check("real_model/selected_distinct_catalog_visits",ChosenVisits(request),Visits());
    bool order=true;string prior=null;int chosen=0;foreach(var o in runner.Contract!=null?runner.Contract.obligations:new TaskObligation[0])if(o.kind=="navigate"){if(prior!=null&&Array.IndexOf(o.after??new string[0],prior)<0)order=false;prior=o.id;chosen++;}
    Check("real_model/selection_order_in_contract",order&&chosen>=2,Visits());
    float end=Now+135;while(runner.IsRunning&&Now<end)yield return null;
    int arrivals=runner.Evidence.results.FindAll(x=>x.kind=="navigate"&&x.status=="succeeded").Count;
    Check("real_model/full_itinerary_arrives",Success&&arrivals==chosen&&chosen>=2&&!runner.Motor.IsMoving&&!runner.Motor.HasTarget,runner.Evidence.status+"|"+runner.Evidence.reason+" arrivals="+arrivals+" chosen="+chosen+" "+Visits());
    bool onlyRequested=true;foreach(var x in runner.Evidence.results)if(x.kind=="observe"||x.kind=="speak"||x.kind=="interact")onlyRequested=false;
    Check("real_model/no_unrequested_scan_speech_interaction",onlyRequested,"receipts="+runner.Evidence.results.Count);
    string extra=null;foreach(var target in runner.Catalog.SemanticTargets()){bool selected=false;foreach(var o in runner.Contract.obligations)if(o.target_id==target.Id)selected=true;if(!selected){extra=target.Id;break;}}
    Check("policy/unselected_destination_still_denied",extra!=null&&TaskContractRules.Policy(runner.Contract,new MissionStep{kind="navigate",target_id=extra})!=null,extra);
    Check("policy/unrequested_interaction_still_denied",TaskContractRules.Policy(runner.Contract,new MissionStep{kind="interact",target_id=extra})!=null,extra);
    Check("runtime/completed_visits_available_to_planner",runner.RecentVisitedTargets.Length==chosen,string.Join(",",runner.RecentVisitedTargets));
    var visitedBeforeCorrection=runner.RecentVisitedTargets;
    Current="actual_original_correction_and_stop";ResetActor();Save();request=positive[3];yield return BeginRequest(request);
    Check("real_model/correction_selects_new_itinerary",ChosenVisits(request),Visits());
    bool newPlace=false;foreach(var o in runner.Contract.obligations)if(o.kind=="navigate"&&Array.IndexOf(visitedBeforeCorrection,o.target_id)<0)newPlace=true;
    Check("real_model/variety_uses_other_places",newPlace,Visits());
    end=Now+30;while(runner.IsRunning&&!runner.Motor.IsMoving&&Now<end)yield return null;
    Check("real_model/correction_starts_navigation",runner.IsRunning&&runner.Motor.IsMoving,runner.Evidence.status+"|"+runner.Evidence.reason+" "+Visits());
    session.SubmitVoice("停下");var stopped=runner.Motor.transform.position;yield return Delay(1.2f);
    Check("runtime/explicit_stop_cancels_without_late_resume",!runner.IsRunning&&runner.Evidence.status=="cancelled"&&!runner.Motor.IsMoving&&!runner.Motor.HasTarget&&TaskTargetCatalog.FlatDistance(stopped,runner.Motor.transform.position)<0.01f,runner.Evidence.status+"|"+runner.Evidence.reason);
    Check("runtime/cancelled_target_not_recorded_as_visited",string.Join(",",runner.RecentVisitedTargets)==string.Join(",",visitedBeforeCorrection),string.Join(",",runner.RecentVisitedTargets));
    Current="real_advice_query";ResetActor();Save();var ctx=runner.Context;session.SubmitVoice("你觉得附近哪里适合散步");end=Now+20;while(session.DialogueBusy&&Now<end)yield return null;
    Check("real_model/advice_question_does_not_walk",runner.Context==ctx&&!runner.IsRunning&&!runner.Motor.IsMoving&&!session.DialogueBusy,session.LastReply);
    Current="real_stationary_negative";ResetActor();Save();request="请站在原地看向方尖碑，不要移动，不要说话";var position=runner.Motor.transform.position;yield return BeginRequest(request);end=Now+20;while(runner.IsRunning&&Now<end)yield return null;
    Check("real_model/stationary_instruction_preserves_translation_ban",Success&&runner.Contract!=null&&runner.Contract.forbid_move&&TaskTargetCatalog.FlatDistance(position,runner.Motor.transform.position)<0.02f,runner.Evidence.status+"|"+runner.Evidence.reason+" moved="+TaskTargetCatalog.FlatDistance(position,runner.Motor.transform.position));
    var unit=TaskRuntimeUnitAcceptance.Run(Path.Combine(Dir,"unit"));foreach(var item in unit.cases)Report.cases.Add(item);Current="finished";
   }finally{
    if(runner.IsRunning)runner.Cancel("acceptance_end");session.CancelDialogueTurn();Time.timeScale=scale;runner.EvidenceRoot=oldRoot;runner.VoiceEnabled=oldTaskVoice;session.Config.VoiceEnabled=oldVoice;session.TaskAcknowledgmentEnabled=oldAck;session.InputMode=oldMode;
    if(session.Brain!=null)session.Brain.EnableProactive=oldBrain;if(session.VoiceInput!=null)session.VoiceInput.SetEnabled(oldInput);Report.done=true;Running=false;Save();
   }
  }
 }
}
