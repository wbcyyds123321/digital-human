using System;
using System.Collections;
using System.IO;
using System.Security.Cryptography;
using UnityEngine;
using Yixuan.Vision;

namespace Yixuan.AI.Tasks
{
    public static class TaskAttentionFocus
    {
        // Semantic parts are resolved from live geometry, never from route coordinates.
        public static bool Resolve(TaskDestination t, out Vector3 point, out string source)
        {
            point = Vector3.zero; source = "";
            if (t == null || !t.IsAnchor && t.Object == null) return false;
            if (t.LookAt != null) { point=t.LookAt.position; source="explicit_look_at_pose"; return true; }
            if (t.IsAnchor) { point=t.AnchorPosition + Vector3.up*1.5f; source="anchor_eye_level"; return true; }
            var renderers=t.Object.GetComponentsInChildren<Renderer>();
            Bounds bounds=new Bounds(); bool found=false;
            bool lamp=t.Object.name.StartsWith("TrafficLight", StringComparison.OrdinalIgnoreCase);
            if(lamp)
                foreach(var r in renderers)
                    if(r.enabled && r is MeshRenderer && (r.name=="Red" || r.name=="Yellow" || r.name=="Green"))
                    { if(!found) {bounds=r.bounds;found=true;} else bounds.Encapsulate(r.bounds); }
            if(found) { point=bounds.center;source="signal_lenses_bounds";return true; }
            foreach(var r in renderers)
                if(r.enabled && r is MeshRenderer)
                {if(!found) {bounds=r.bounds;found=true;} else bounds.Encapsulate(r.bounds);}
            point=found ? bounds.center : t.Position;
            source=found ? "renderer_bounds_center" : "object_pivot";
            return true;
        }
    }
    public partial class TaskMissionRunner
    {
        public float GazeTolerance=3f;
        GameObject _attentionPoint;
        TaskDestination _attentionTarget;
        bool _holdAttention;
        public string AttentionTargetId { get { return _holdDirection?"direction:"+_directionName:_holdAttention && _attentionTarget!=null ? _attentionTarget.Id : ""; } }
        public void StopAttention()
        {
            bool owned=_holdDirection||Gimbal!=null && _attentionPoint!=null && Gimbal.TrackingTarget==_attentionPoint.transform;
            ClearAttention();
            if(owned) Gimbal.Center();
        }
        void ClearAttention()
        {
            _holdDirection=false;_directionName=null;
            if(_attentionPoint!=null) Destroy(_attentionPoint);
            _attentionPoint=null; _attentionTarget=null;
            if(_holdAttention && Gimbal!=null) Gimbal.AutoRecenter=_oldAutoRecenter;
            _holdAttention=false;
        }
        void UpdateAttention()
        {
            if(!_holdAttention) return;
            if(Gimbal==null || Gimbal.State!=GazeState.Tracking || _attentionPoint==null || Gimbal.TrackingTarget!=_attentionPoint.transform || Motor!=null && (Motor.IsMoving || !IsRunning && Motor.IsTurning))
            { ClearAttention(); return; }
            Vector3 point; string source;
            if(!TaskAttentionFocus.Resolve(_attentionTarget,out point,out source))
            { ClearAttention(); Gimbal.Center(); return; }
            if(_attentionPoint!=null) _attentionPoint.transform.position=point;
        }
        IEnumerator LookAt(MissionStep s, MissionStepResult r)
        {
            if(Gimbal==null || EyeCamera==null) {Fail(r,"eye_camera_or_gimbal_missing");yield break;}
            var target=Catalog.Get(s.target_id); Vector3 point; string source;
            if(!TaskAttentionFocus.Resolve(target,out point,out source)) {Fail(r,"attention_target_missing");yield break;}
            ClearAttention();
            r.target_path=target.Path;r.target_position=target.Position;r.focus_position=point;r.focus_source=source;r.camera_only=s.camera_only;
            Vector3 start=Motor.transform.position;float startYaw=Motor.transform.eulerAngles.y;
            Vector3 delta=point-(Gimbal.YawPivot!=null?Gimbal.YawPivot.position:EyeCamera.transform.position);
            float worldYaw=Mathf.Atan2(delta.x,delta.z)*Mathf.Rad2Deg;
            float relative=Mathf.DeltaAngle(startYaw+Gimbal.BaseYaw+Gimbal.HomeYaw,worldYaw);
            if(!s.camera_only && Gimbal.BodyAssist && Gimbal.LimitRelativeRange && Mathf.Abs(relative)>Mathf.Abs(Gimbal.MaxRelativeYaw))
            {
                yield return TurnTo(worldYaw-Gimbal.BaseYaw-Gimbal.HomeYaw);
                if(Motor.IsTurning) {Fail(r,"attention_body_turn_timeout");yield break;}
            }
            _attentionPoint=new GameObject("TaskAttentionFocusPoint");_attentionPoint.hideFlags=HideFlags.DontSave;
            _attentionPoint.transform.position=point;_attentionTarget=target;
            Gimbal.Track(_attentionPoint.transform, -1f, false);
            float end=TaskNow+6f,stable=0;
            while(TaskNow<end)
            {
                if(!TaskAttentionFocus.Resolve(target,out point,out source)) {Fail(r,"attention_target_disappeared");yield break;}
                _attentionPoint.transform.position=point;
                r.focus_position=point;
                r.gaze_error=Vector3.Angle(EyeCamera.transform.forward,point-EyeCamera.transform.position);
                r.viewport_position=EyeCamera.WorldToViewportPoint(point);
                r.target_in_frame=r.viewport_position.z>0 && r.viewport_position.x>0 && r.viewport_position.x<1 && r.viewport_position.y>0 && r.viewport_position.y<1;
                r.position_drift=Vector3.Distance(start,Motor.transform.position);
                r.body_rotation=Mathf.Abs(Mathf.DeltaAngle(startYaw,Motor.transform.eulerAngles.y));
                if(r.position_drift>0.03f) {Fail(r,"attention_unexpected_translation");yield break;}
                if(s.camera_only && r.body_rotation>0.5f) {Fail(r,"attention_body_moved_without_permission");yield break;}
                if(!Gimbal.IsObservationSettling)
                {
                    if(r.gaze_error<=GazeTolerance && r.target_in_frame)
                    { stable+=Time.unscaledDeltaTime; if(stable>=0.25f) break; }
                    else {Fail(r,"attention_outside_gimbal_range: 目标超出云台范围，请允许身体转向或调整站立位置");yield break;}
                }
                else stable=0;
                yield return null;
            }
            if(stable<0.25f) {Fail(r,"attention_not_stable");yield break;}
            _holdAttention=true;
            yield return new WaitForEndOfFrame();
            byte[] jpg=CaptureFrame();r.frame_path=Path.Combine(RunDirectory,s.id+"_look_at.jpg");File.WriteAllBytes(r.frame_path,jpg);
            using(var sha=SHA256.Create()) r.frame_sha256=BitConverter.ToString(sha.ComputeHash(jpg)).Replace("-","").ToLowerInvariant();
            r.reason="视角已稳定指向"+target.Name+"，保持注视；这是几何对准，未进行视觉语义识别";
        }
    }
}
