using System;
using System.Collections;
using System.IO;
using System.Security.Cryptography;
using UnityEngine;
namespace Yixuan.AI.Tasks {
 public partial class TaskMissionRunner {
  bool _holdDirection;string _directionName;
  IEnumerator LookDirection(MissionStep s,MissionStepResult r){
   if(Gimbal==null||EyeCamera==null){Fail(r,"eye_camera_or_gimbal_missing");yield break;}
   ClearAttention();
   Vector3 start=Motor.transform.position;
   float bodyYaw=Motor.transform.eulerAngles.y;
   float eyeBefore=Gimbal.CurrentYaw;
   float worldBefore=Mathf.Repeat(bodyYaw+Gimbal.BaseYaw+eyeBefore,360f);
   r.eye_yaw_before=eyeBefore;r.eye_pitch_before=Gimbal.CurrentPitch;r.body_yaw_before=bodyYaw;
   r.eye_world_yaw_before=worldBefore;
   // Two separate semantics (F5):
   //  - reference='body' (default): "再向右看30度" is a DELTA from the CURRENT real eye
   //    direction. The gimbal API expects a HOME-RELATIVE parameter (TargetYaw =
   //    HomeYaw + Clamp(input), CurrentYaw already contains HomeYaw), so the delta must
   //    be converted to that convention; passing eyeBefore+delta directly added HomeYaw
   //    a second time when HomeYaw != 0.
   //  - reference='world': yaw is an ABSOLUTE world heading. World eye heading is
   //    bodyYaw + BaseYaw + CurrentYaw (CurrentYaw already includes HomeYaw), so the
   //    TurnRelative input is requestedWorld - bodyYaw - BaseYaw - HomeYaw.
   float yaw=s.reference=="world"
     ?Mathf.DeltaAngle(bodyYaw+Gimbal.BaseYaw+Gimbal.HomeYaw,s.yaw)
     :Mathf.DeltaAngle(Gimbal.HomeYaw,eyeBefore+s.yaw);
   r.direction=s.direction;r.camera_only=s.camera_only;r.requested_yaw=s.yaw;r.requested_pitch=s.pitch;
   bool outOfRange=Gimbal.LimitRelativeRange&&Mathf.Abs(yaw)>Gimbal.MaxRelativeYaw+0.01f;
   bool outOfPitch=!Gimbal.UnlimitedPitch&&(s.pitch<Gimbal.MinPitch||s.pitch>Gimbal.MaxPitch);
   // A precise numeric eye angle beyond the gimbal range must fail explicitly.
   // Only a qualitative look may use the existing body-assist permission.
   if(outOfPitch||outOfRange&&(s.precise_angles||s.camera_only||!Gimbal.BodyAssist)){
     float allowed=Gimbal.LimitRelativeRange?Gimbal.MaxRelativeYaw:90f;
     Fail(r,"direction_out_of_range: requested_relative_from_current_eye="+yaw.ToString("F1")+"; current_eye="+eyeBefore.ToString("F1")+"; allowedYaw="+allowed+(s.precise_angles?"; precise_angle_refused_without_body_turn":""));yield break;
   }
   Gimbal.TurnRelative(yaw,s.pitch,-1,!s.camera_only);Gimbal.AutoRecenter=false;
   float end=TaskNow+6,stable=0;
   while(TaskNow<end){
    // Desired world direction follows the actual body (body assist) and the accepted
    // gimbal target; it is never derived from the un-clamped request.
    Vector3 desired=Quaternion.Euler(-Gimbal.TargetPitch,Motor.transform.eulerAngles.y+Gimbal.BaseYaw+Gimbal.TargetYaw,0)*Vector3.forward;
    r.gaze_error=Vector3.Angle(EyeCamera.transform.forward,desired);r.position_drift=Vector3.Distance(start,Motor.transform.position);r.body_rotation=Mathf.Abs(Mathf.DeltaAngle(bodyYaw,Motor.transform.eulerAngles.y));
    if(r.position_drift>0.03f||s.camera_only&&r.body_rotation>0.5f){Fail(r,"direction_unexpected_body_motion");yield break;}
    if(r.gaze_error<=GazeTolerance&&!Gimbal.IsObservationSettling){stable+=Time.unscaledDeltaTime;if(stable>=0.25f)break;}else stable=0;yield return null;
   }
   if(stable<0.25f){Fail(r,"direction_not_stable");yield break;}
   r.actual_yaw=Gimbal.CurrentYaw;r.actual_pitch=Gimbal.CurrentPitch;
   r.eye_yaw_after=Gimbal.CurrentYaw;r.eye_pitch_after=Gimbal.CurrentPitch;
   r.body_yaw_after=Motor.transform.eulerAngles.y;r.body_rotation=Mathf.Abs(Mathf.DeltaAngle(bodyYaw,r.body_yaw_after));
   r.eye_world_yaw_after=Mathf.Repeat(r.body_yaw_after+Gimbal.BaseYaw+r.eye_yaw_after,360f);
   // A world-reference look must actually reach the requested world heading; a body
   // assist is allowed only under the existing qualitative-look rule, and the final
   // world error is checked here instead of trusting the request fields.
   if(s.reference=="world"&&Mathf.Abs(Mathf.DeltaAngle(r.eye_world_yaw_after,s.yaw))>GazeTolerance){Fail(r,"world_direction_not_reached: actual_world="+r.eye_world_yaw_after.ToString("F1")+" requested="+s.yaw.ToString("F1"));yield break;}
   yield return new WaitForEndOfFrame();byte[] jpg=CaptureFrame();r.frame_path=Path.Combine(RunDirectory,s.id+"_direction.jpg");File.WriteAllBytes(r.frame_path,jpg);
   using(var sha=SHA256.Create())r.frame_sha256=BitConverter.ToString(sha.ComputeHash(jpg)).Replace("-","").ToLowerInvariant();
   _holdDirection=s.hold;_directionName=s.direction;if(!s.hold)Gimbal.Center();r.reason="direction_aligned: "+s.direction+"; geometric alignment only";
  }
 }
}
