using System.Collections;
using UnityEngine;
namespace Yixuan.AI.Tasks {
 public partial class TaskMissionRunner {
  IEnumerator MoveRelative(MissionStep s,MissionStepResult r){
   ClearAttention();if(Gimbal!=null)Gimbal.Center();
   r.direction=s.direction;r.reference="body";r.requested_distance=s.distance;r.start_position=Motor.transform.position;
   float initialYaw=Motor.transform.eulerAngles.y;
   var axis=s.direction=="forward"?Motor.transform.forward:s.direction=="backward"?-Motor.transform.forward:s.direction=="left"?-Motor.transform.right:Motor.transform.right;
   axis.y=0;axis.Normalize();r.target_position=r.start_position+axis*s.distance;r.approach_position=r.target_position;r.route.Add(r.target_position);
   try {
    if(!Motor.MoveRelative(s.direction,s.distance)){Fail(r,Motor.LastStatus+": "+Motor.LastError);yield break;}
    var previous=Motor.transform.position;
    while(Motor.HasTarget){r.path_length+=TaskTargetCatalog.FlatDistance(previous,Motor.transform.position);previous=Motor.transform.position;yield return null;}
    r.path_length+=TaskTargetCatalog.FlatDistance(previous,Motor.transform.position);
    r.distance_error=TaskTargetCatalog.FlatDistance(Motor.transform.position,r.target_position);
    r.body_rotation=Mathf.Abs(Mathf.DeltaAngle(initialYaw,Motor.transform.eulerAngles.y));
    if(Motor.LastStatus!="arrived"||r.distance_error>0.03f||r.body_rotation>0.5f){Fail(r,"relative_move_failed: "+Motor.LastStatus+": "+Motor.LastError);yield break;}
    r.status="succeeded";r.reason="body_relative_straight_move_confirmed";
   } finally {if(Motor.HasTarget)using(TaskAuthority.Enter(Context!=null?Context.MissionId:null))Motor.Stop("相对移动结束", "cancelled");}
  }
 }
}
