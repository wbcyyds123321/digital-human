using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Yixuan.World;
namespace Yixuan.AI.Tasks {
 public partial class TaskMissionRunner {
  sealed class ScriptRoute {
   public string id,target;public Vector3 destination,face,lastPosition; public float travelled,total;public bool done;
   public TaskDestination objectTarget;public readonly List<Vector3> points=new List<Vector3>();
  }
  readonly Dictionary<string,ScriptRoute> _scriptRoutes=new Dictionary<string,ScriptRoute>();
  void AddReferenceTargets(){
   Catalog.Targets.RemoveAll(x=>x.Id=="origin"||x.Id=="mission_origin");
   Catalog.Targets.Add(new TaskDestination{Id="origin",Name="场景原点",Path="reference/scene_origin",Description="位置参照；look_at只注视，不移动；返回请用return_home",Aliases=new[]{"原点","起点","起始位置"},IsAnchor=true,AnchorPosition=Evidence.origin});
   Catalog.Targets.Add(new TaskDestination{Id="mission_origin",Name="本次任务起点",Path="reference/mission_origin",Description="本次任务开始时的位置参照",Aliases=new[]{"本次任务起点"},IsAnchor=true,AnchorPosition=Evidence.task_origin});
  }
  ScriptRoute _lastScriptRoute;
  void StampRouteContext(MissionStepResult r){
   ScriptRoute latest=_lastScriptRoute;
   if(latest!=null&&TaskTargetCatalog.FlatDistance(Motor.transform.position,latest.lastPosition)<=0.04f){r.route_id=latest.id;r.route_total=latest.total;r.route_travelled=latest.travelled;r.route_fraction=latest.total>0?latest.travelled/latest.total:1;r.route_done=latest.done;}
  }
  MissionStep DecodeRoute(string argument,MoonSharp.Interpreter.Table options){
   ScriptRoute route;string target=argument,id=null;
   if(_scriptRoutes.TryGetValue(argument,out route)){target=route.target;id=route.id;if(route.done)throw new Exception("route_already_completed");}
   else {if(argument.StartsWith("route_",StringComparison.Ordinal))throw new Exception("unknown_or_stale_route_handle");target=ResolveCodeTarget(argument);}
   var s=new MissionStep{kind="route_progress",target_id=target,route_id=id,route_fraction=1,timeout=120};
   if(options!=null){
    var f=options.Get("to_fraction");var d=options.Get("distance");bool hf=f.Type!=MoonSharp.Interpreter.DataType.Nil&&f.Type!=MoonSharp.Interpreter.DataType.Void,hd=d.Type!=MoonSharp.Interpreter.DataType.Nil&&d.Type!=MoonSharp.Interpreter.DataType.Void;
    if(hf&&hd)throw new Exception("route_choose_fraction_or_distance");
    if(hf){if(f.Type!=MoonSharp.Interpreter.DataType.Number||double.IsNaN(f.Number)||double.IsInfinity(f.Number)||f.Number<=0||f.Number>1)throw new Exception("route_fraction_range");s.route_fraction=(float)f.Number;}
    if(hd){if(d.Type!=MoonSharp.Interpreter.DataType.Number||!TaskContractRules.ValidMoveDistance((float)d.Number))throw new Exception("route_distance_range");s.distance=(float)d.Number;s.route_fraction=0;}
   }
   if(string.IsNullOrEmpty(id))foreach(var x in _scriptRoutes.Values)if(x.target==target)foreach(var o in Contract.obligations)if(o.during_target==target)throw new Exception("scheduled_route_must_reuse_existing_handle");
   return s;
  }
  static Vector3 PointOnRoute(ScriptRoute route,float distance){
   float walked=0;for(int i=1;i<route.points.Count;i++){float leg=TaskTargetCatalog.FlatDistance(route.points[i-1],route.points[i]);if(walked+leg>=distance)return Vector3.Lerp(route.points[i-1],route.points[i],leg>0?(distance-walked)/leg:0);walked+=leg;}return route.destination;
  }
  IEnumerator FollowRoute(MissionStep s,MissionStepResult r){
   ScriptRoute route=null;
   if(!string.IsNullOrEmpty(s.route_id))_scriptRoutes.TryGetValue(s.route_id,out route);
   if(route==null){
    route=new ScriptRoute{id="route_"+Guid.NewGuid().ToString("N"),target=s.target_id,lastPosition=Motor.transform.position};
    List<Vector3> planned;string error=null;
    if(s.target_id=="origin"||s.target_id=="mission_origin"){
     route.destination=s.target_id=="origin"?Evidence.origin:Evidence.task_origin;planned=WorldData.FindPath(Motor.transform.position,route.destination);
    }else{
     route.objectTarget=Catalog.Get(s.target_id);Vector3 dest;
     if(!Catalog.FindApproach(route.objectTarget,Motor.transform.position,out dest,out planned,out error)){Fail(r,error);yield break;}route.destination=dest;route.face=route.objectTarget.Position;
    }
    if(planned==null||planned.Count==0){Fail(r,"route_unreachable");yield break;}
    route.points.Add(Motor.transform.position);foreach(var p in planned)if(TaskTargetCatalog.FlatDistance(route.points[route.points.Count-1],p)>0.001f)route.points.Add(p);
    if(TaskTargetCatalog.FlatDistance(route.points[route.points.Count-1],route.destination)>0.001f)route.points.Add(route.destination);
    for(int i=1;i<route.points.Count;i++)route.total+=TaskTargetCatalog.FlatDistance(route.points[i-1],route.points[i]);
    _scriptRoutes.Add(route.id,route);EmitRuntime("RoutePlanned",r.id,"",route.id+"|"+route.target+"|"+route.total);
   }
   _lastScriptRoute=route;
   if(route.done){Fail(r,"route_already_completed");yield break;}
   if(TaskTargetCatalog.FlatDistance(Motor.transform.position,route.lastPosition)>0.04f){Fail(r,"route_resume_position_changed");yield break;}
   if(route.objectTarget!=null&&(route.objectTarget.Object==null&&!route.objectTarget.IsAnchor||TaskTargetCatalog.FlatDistance(route.objectTarget.Position,route.face)>0.25f)){Fail(r,"route_target_moved_or_destroyed");yield break;}
   float end=s.distance>0?Mathf.Min(route.total,route.travelled+s.distance):route.total*s.route_fraction;
   if(end<route.travelled-0.005f||end<=route.travelled+0.001f&&route.total>0.025f){Fail(r,"route_progress_must_advance");yield break;}
   r.route_id=route.id;r.route_total=route.total;r.target_position=route.objectTarget!=null?route.face:route.destination;r.approach_position=route.destination;
   var segment=new List<Vector3>{Motor.transform.position};float offset=0;
   for(int i=1;i<route.points.Count;i++){offset+=TaskTargetCatalog.FlatDistance(route.points[i-1],route.points[i]);if(offset>route.travelled+0.002f&&offset<end-0.002f)segment.Add(route.points[i]);}
   Vector3 endpoint=PointOnRoute(route,end);segment.Add(endpoint);r.route.AddRange(segment);
   ClearAttention();if(Gimbal!=null)Gimbal.Center();float radius=Motor.ArriveRadius;Motor.ArriveRadius=0.008f;
   try{
    if(TaskTargetCatalog.FlatDistance(Motor.transform.position,endpoint)>0.008f){
     if(!Motor.MoveAlongPath(segment)){Fail(r,"route_start_failed: "+Motor.LastStatus+" "+Motor.LastError);yield break;}
     var previous=Motor.transform.position;r.attempts=1;
     while(Motor.HasTarget){
      if(route.objectTarget!=null&&(route.objectTarget.Object==null&&!route.objectTarget.IsAnchor||TaskTargetCatalog.FlatDistance(route.objectTarget.Position,route.face)>0.25f)){Motor.Stop("路径目标变化","cancelled");Fail(r,"route_target_moved_or_destroyed");yield break;}
      r.path_length+=TaskTargetCatalog.FlatDistance(previous,Motor.transform.position);previous=Motor.transform.position;yield return null;
     }
     if(Motor.LastStatus!="arrived"){Fail(r,"route_motion_failed: "+Motor.LastStatus+" "+Motor.LastError);yield break;}
    }
    r.distance_error=TaskTargetCatalog.FlatDistance(Motor.transform.position,endpoint);if(r.distance_error>0.025f){Fail(r,"route_endpoint_not_reached");yield break;}
    route.travelled=end;route.lastPosition=Motor.transform.position;route.done=end>=route.total-0.001f;
    r.route_travelled=end;r.route_fraction=route.total>0?end/route.total:1;r.route_done=route.done;
    if(route.done){
     float yaw=s.target_id=="origin"?Evidence.origin_yaw:s.target_id=="mission_origin"?Evidence.task_origin_yaw:route.objectTarget.IsAnchor?Motor.transform.eulerAngles.y:Quaternion.LookRotation(route.face-Motor.transform.position).eulerAngles.y;
     yield return TurnTo(yaw);r.facing_error=Mathf.Abs(Mathf.DeltaAngle(Motor.transform.eulerAngles.y,yaw));if(r.facing_error>FacingTolerance){Fail(r,"route_facing_not_reached");yield break;}
     var arrival=new MissionStepResult{id=r.id+"_arrival",kind=s.target_id=="origin"||s.target_id=="mission_origin"?"return":"navigate",target_id=s.target_id,generation=_generation,status="succeeded",reason="完整可续行路径到达；部分路径不计到达",started=TaskNow,ended=TaskNow,start_position=Motor.transform.position,end_position=Motor.transform.position,target_position=r.target_position,approach_position=route.destination,distance_error=r.distance_error,facing_error=r.facing_error,route_id=route.id,route_total=route.total,route_travelled=end,route_fraction=1,route_done=true};
     Evidence.results.Add(arrival);EmitRuntime("RouteArrived",arrival.id,"succeeded",JsonUtility.ToJson(arrival));
    }
    r.reason=route.done?"route_completed":"route_paused_at_progress";
    EmitRuntime("RouteProgress",r.id,"succeeded",route.id+"|"+r.route_fraction);
   }finally{Motor.ArriveRadius=radius;}
  }
 }
}
