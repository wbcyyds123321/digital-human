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
   ClearAttention();Vector3 start=Motor.transform.position;float bodyYaw=Motor.transform.eulerAngles.y;
   float yaw=s.reference=="world"?Mathf.DeltaAngle(bodyYaw+Gimbal.BaseYaw+Gimbal.HomeYaw,s.yaw):s.yaw;
   r.direction=s.direction;r.camera_only=true;r.requested_yaw=s.yaw;r.requested_pitch=s.pitch;
   if(Gimbal.LimitRelativeRange&&(Mathf.Abs(yaw)>Gimbal.MaxRelativeYaw||Mathf.Abs(s.pitch)>Gimbal.MaxRelativePitch)||!Gimbal.UnlimitedPitch&&(s.pitch<Gimbal.MinPitch||s.pitch>Gimbal.MaxPitch)){
    Fail(r,"direction_out_of_range: requested="+yaw+","+s.pitch+" allowedYaw="+Gimbal.MaxRelativeYaw+" allowedPitch="+Gimbal.MaxRelativePitch);yield break;
   }
   Gimbal.TurnRelative(yaw,s.pitch,-1,false);Gimbal.AutoRecenter=false;
   Vector3 desired=Quaternion.Euler(-s.pitch,bodyYaw+Gimbal.BaseYaw+Gimbal.HomeYaw+yaw,0)*Vector3.forward;
   float end=TaskNow+6,stable=0;
   while(TaskNow<end){
    r.gaze_error=Vector3.Angle(EyeCamera.transform.forward,desired);r.position_drift=Vector3.Distance(start,Motor.transform.position);r.body_rotation=Mathf.Abs(Mathf.DeltaAngle(bodyYaw,Motor.transform.eulerAngles.y));
    if(r.position_drift>0.03f||r.body_rotation>0.5f){Fail(r,"direction_unexpected_body_motion");yield break;}
    if(r.gaze_error<=GazeTolerance&&!Gimbal.IsMoving){stable+=Time.unscaledDeltaTime;if(stable>=0.25f)break;}else stable=0;yield return null;
   }
   if(stable<0.25f){Fail(r,"direction_not_stable");yield break;}
   r.actual_yaw=Gimbal.CurrentYaw;r.actual_pitch=Gimbal.CurrentPitch;
   yield return new WaitForEndOfFrame();byte[] jpg=CaptureFrame();r.frame_path=Path.Combine(RunDirectory,s.id+"_direction.jpg");File.WriteAllBytes(r.frame_path,jpg);
   using(var sha=SHA256.Create())r.frame_sha256=BitConverter.ToString(sha.ComputeHash(jpg)).Replace("-","").ToLowerInvariant();
   _holdDirection=s.hold;_directionName=s.direction;if(!s.hold)Gimbal.Center();r.reason="direction_aligned: "+s.direction+"; geometric alignment only";
  }
 }
}
