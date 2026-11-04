using System.Collections;
using UnityEngine;
namespace Yixuan.AI.Tasks {
 public partial class TaskMissionRunner {
  IEnumerator TurnBody(MissionStep s,MissionStepResult r){
   if(!TaskContractRules.ValidBodyTurn(s.yaw)){Fail(r,"invalid_body_turn");yield break;}
   ClearAttention();if(Gimbal!=null)Gimbal.Center();
   Vector3 start=Motor.transform.position;float initial=Motor.transform.eulerAngles.y;
   float target=Mathf.Repeat(initial+s.yaw,360f);
   if(!Motor.TurnRelative(s.yaw,"mission_body_turn")){Fail(r,"body_turn_not_started");yield break;}
   float until=TaskNow+6;while(Motor.IsTurning&&TaskNow<until)yield return null;
   r.actual_yaw=Motor.transform.eulerAngles.y;r.facing_error=Mathf.Abs(Mathf.DeltaAngle(r.actual_yaw,target));
   r.position_drift=Vector3.Distance(start,Motor.transform.position);r.body_rotation=Mathf.Abs(Mathf.DeltaAngle(initial,r.actual_yaw));
   if(Motor.IsTurning||r.facing_error>0.5f||r.position_drift>0.02f){Motor.StopTurning();Fail(r,"body_turn_not_confirmed");yield break;}
   r.reason="stationary_body_turn_confirmed: "+s.yaw+" degrees; error="+r.facing_error;
  }
 }
}
