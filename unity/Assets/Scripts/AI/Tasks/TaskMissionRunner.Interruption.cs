using System;
using System.Collections;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEngine;
using Yixuan.Motion;
namespace Yixuan.AI.Tasks {
 public partial class TaskMissionRunner {
  public bool IsSuspended {get;private set;}
  public TaskMissionRunner ActiveInsertion {get;private set;}
  public string LastInsertionReport {get;private set;}="";
  public int PendingInsertions {get{return _insertions.Count;}}
  public bool IsInsertion {get{return _parentMission!=null;}}
  TaskMissionRunner _parentMission; Coroutine _interruption;
  float _pausedAt,_pausedSeconds; string _resumePhase;
  CharacterLocomotion.SuspendedMotion _suspendedMotion;
  sealed class InsertionRequest {public string text,program;public TaskContract contract;}
  readonly Queue<InsertionRequest> _insertions=new Queue<InsertionRequest>();
  float TaskNow {get{return (IsSuspended?_pausedAt:Time.realtimeSinceStartup)-_pausedSeconds;}}
  public static bool LooksLikeInsertion(string text) {
   if(string.IsNullOrWhiteSpace(text))return false;
   return Regex.IsMatch(text,@"继续|接着|恢复") && Regex.IsMatch(text,@"没.{0,4}(完|走)|未完成|剩余|原来|原先|之前|刚才|往返|原任务|原来的任务|走完|目的地") && Regex.IsMatch(text,@"先|暂停|停下|插入|中途|临时");
  }
  public bool InsertTask(string request){return EnqueueInsertion(new InsertionRequest{text=request});}
  public bool InsertContractForTest(string request,string program,TaskContract contract){return EnqueueInsertion(new InsertionRequest{text=request,program=program,contract=contract});}
  bool EnqueueInsertion(InsertionRequest request){
   if(!IsRunning||IsInsertion||!UseCodeExecution||string.IsNullOrWhiteSpace(request.text))return false;
   if(_insertions.Count>=3){RuntimeNotice("InsertionRejected","insertion_queue_full");LastInsertionReport="插入队列已满，原任务继续保留。";return false;}
   // Long-running external effects cannot be rewound. Decline without replacing the parent.
   foreach(var r in Evidence.results) if(r.status=="running" && r.kind!="route_progress"&&r.kind!="navigate"&&r.kind!="return"&&r.kind!="move_relative"&&r.kind!="turn_body"&&r.kind!="wait") {
    LastInsertionReport="当前正在"+r.kind+"，暂不支持在这个步骤中插入；原任务继续。";RuntimeNotice("InsertionRejected",LastInsertionReport);if(Session!=null)Session.MissionFinished(LastInsertionReport);return false;
   }
   _insertions.Enqueue(request);RuntimeNotice("InsertionQueued",request.text);
   if(_interruption==null){_interruption=StartCoroutine(ExecuteInsertions(_generation));if(!IsSuspended)_interruption=null;}return true;
  }
  IEnumerator ExecuteInsertions(int generation){
   _resumePhase=Phase;_pausedAt=Time.realtimeSinceStartup;IsSuspended=true;
   float yaw=Gimbal!=null?Mathf.DeltaAngle(Gimbal.HomeYaw,Gimbal.TargetYaw):0,pitch=Gimbal!=null?Gimbal.TargetPitch:0;
   using(TaskAuthority.Enter(Context.MissionId))_suspendedMotion=Motor.SuspendMotion();
   EmitRuntime("MissionSuspended","","suspended","Lua VM, locals, receipt keys and active motion retained");
   TaskAuthority.Release(Context.MissionId);Phase="suspended_for_insertion";Save();Raise();
   try {
    while(_insertions.Count>0 && generation==_generation && IsRunning){
     var next=_insertions.Dequeue();var go=new GameObject("TaskInsertionRuntime");
     var child=go.AddComponent<TaskMissionRunner>();ActiveInsertion=child;child._parentMission=this;
     child.Session=Session;child.Motor=Motor;child.Gimbal=Gimbal;child.EyeCamera=EyeCamera;
     child.HomeCaptured=HomeCaptured;child.HomePosition=HomePosition;child.HomeYaw=HomeYaw;
     child.VoiceEnabled=VoiceEnabled;child.EvidenceRoot=EvidenceRoot;child.PlannerOverride=PlannerOverride;
     child.PlannerTimeout=PlannerTimeout;child.CodeTimeout=120;child.UseCodeExecution=true;
     if(next.program!=null)child.SubmitContractTest(next.text,next.program,next.contract);else child.Submit(next.text);
     child.Evidence.parent_mission_id=Context.MissionId;child.Save();
     EmitRuntime("InsertionStarted","","running",child.Context.MissionId+"|"+child.RunDirectory);
     while(child.IsRunning&&generation==_generation){Phase="暂停原任务 · 插入："+child.Phase;yield return null;}
     if(generation!=_generation)yield break;
     LastInsertionReport=child.Evidence.status=="succeeded"?"插入动作已完成，继续原任务。":"插入任务未完成（"+child.Evidence.reason+"），继续原任务。";
     Evidence.insertions.Add(new TaskInsertionEvidence{mission_id=child.Context.MissionId,directory=child.RunDirectory,status=child.Evidence.status,reason=child.Evidence.reason,request=next.text});
     EmitRuntime("InsertionFinished","",child.Evidence.status,child.Evidence.reason);
     ActiveInsertion=null;Destroy(go);Save();
    }
   } finally {
    if(ActiveInsertion!=null){if(ActiveInsertion.IsRunning)ActiveInsertion.Cancel("原任务结束，取消插入");Destroy(ActiveInsertion.gameObject);ActiveInsertion=null;}
    _insertions.Clear();_interruption=null;
    if(IsSuspended){_pausedSeconds+=Time.realtimeSinceStartup-_pausedAt;IsSuspended=false;Evidence.paused_seconds=_pausedSeconds;}
    if(generation==_generation && IsRunning){
     bool resumed=TaskAuthority.Acquire(Context.MissionId,1|2|8);
     if(resumed)using(TaskAuthority.Enter(Context.MissionId)){
      resumed=Motor.ResumeMotion(_suspendedMotion);
      if(Gimbal!=null)Gimbal.TurnRelative(yaw,pitch,-1,false);
     }
     _lastPosition=Motor.transform.position;
     if(!resumed){Cancel("resume_state_invalid：插入后身体状态不一致，无法安全继续");}
     else {Phase=_resumePhase;EmitRuntime("MissionResumed","","running","continuing retained VM and current leg");Save();Raise();if(Session!=null)Session.MissionProgress("插入动作结束，继续剩余任务");}
    }
   }
  }
  void CancelInsertions(){
   // Invalidate resume BEFORE disposing the controller. Replacing/cancelling a parent stops both lanes.
   if(_interruption!=null){var c=_interruption;_interruption=null;StopCoroutine(c);}
   if(ActiveInsertion!=null){ActiveInsertion.Cancel("原任务被取消");Destroy(ActiveInsertion.gameObject);ActiveInsertion=null;}
   _insertions.Clear();
   if(IsSuspended){_pausedSeconds+=Time.realtimeSinceStartup-_pausedAt;IsSuspended=false;}
   if(Context!=null)TaskAuthority.Acquire(Context.MissionId,1|2|8);
  }
  string ValidateInsertionContract(){
   if(!IsInsertion||Contract==null)return null;
   foreach(var o in Contract.obligations??new TaskObligation[0])
    if(o.kind!="gesture"&&o.kind!="express"&&o.kind!="speak"&&o.kind!="wait"&&o.kind!="look_at"&&o.kind!="look_direction")return "insertion_requires_stationary_action: "+o.kind;
   // Continuation belongs to the suspended VM; no child may move, turn, or rerun the parent's loop.
   Contract.forbid_move=true;Contract.forbid_body_turn=true;
   if(_parentMission.Contract!=null&&_parentMission.Contract.forbid_speech){Contract.forbid_speech=true;foreach(var o in Contract.obligations)if(o.kind=="speak")return "insertion_parent_silence_forbids_speech";}
   return null;
  }
 }
 [Serializable] public sealed class TaskInsertionEvidence {public string mission_id,directory,request,status,reason;}
}
