using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
namespace Yixuan.AI.Tasks {
 [Serializable] public sealed class TaskContract {
  public string version="task-contract/1",error;public bool forbid_move,forbid_body_turn,forbid_speech,forbid_interact;
  public TaskObligation[] obligations=new TaskObligation[0];public TaskCondition[] conditions=new TaskCondition[0];
 }
 [Serializable] public sealed class TaskCondition {public string id,source,question;public float max_age=30;}
 [Serializable] public sealed class TaskObligation {
  public string repeat_group;
  public string during_target; public float at_progress,every_meters;
  public string id,source,kind,target_id,action_id,direction,text,emotion,reference,condition_id,branch;public float min_duration,pitch,yaw,distance;public bool angles_exact;public string[] after,overlap;
  public int count=1;public bool final;public float max_age;public bool camera_only;
 }
 [Serializable] public sealed class TaskCompletion {
  public bool program_succeeded,task_succeeded;public string reason;public List<string> satisfied=new List<string>(),missing=new List<string>(),unknown=new List<string>();
 }
 public static class TaskContractRules {
  public static void ApplyExplicitPermissions(TaskContract c,string request){
   if(c==null)return;
   // A later stage's "stand still" must not ban an earlier navigation stage.
   // This lexical defense only adds leading/global prohibitions; the independent
   // extractor represents local gaze restrictions with camera_only.
   string text=request??"";var parts=System.Text.RegularExpressions.Regex.Split(text,@"然后|之后|接着|随后|到达后");
   string scope=parts.Length>0?parts[0]:text;
   var firstMove=System.Text.RegularExpressions.Regex.Match(scope,@"走到|前往|走向|去往|回到|走回|移动到");if(firstMove.Success)scope=scope.Substring(0,firstMove.Index);
   foreach(System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(text,@"(?:全程|始终|整个任务)[^，。；;]{0,28}"))scope+="，"+m.Value;
   c.forbid_move|=System.Text.RegularExpressions.Regex.IsMatch(scope,@"不要(移动|走动|走路)|不许(移动|走动)|站在原地|原地不动|只(转|动)(镜头|相机)");
   c.forbid_body_turn|=System.Text.RegularExpressions.Regex.IsMatch(scope,@"(不要|禁止|不许).{0,3}(身体|身子).{0,3}(转|动)|只(转|动)(镜头|相机)");
   c.forbid_speech|=System.Text.RegularExpressions.Regex.IsMatch(scope,@"不要(说话|出声|讲话)|别(说话|出声)|保持安静|禁止发声");
   c.forbid_interact|=System.Text.RegularExpressions.Regex.IsMatch(scope,@"不要(交互|触发)|禁止(交互|触发)");
  }
  public static string Validate(TaskContract c,string request,Func<string,bool> target,Func<string,bool> action) {
   if(c==null)return "contract_missing";if(!string.IsNullOrEmpty(c.error))return c.error;
   if(c.obligations==null||c.obligations.Length==0||c.obligations.Length>48)return "contract_requires_obligations";
   bool routeLanguage=System.Text.RegularExpressions.Regex.IsMatch(request,@"走到一半|走到.*?一半|半路|中途.{0,10}(停|挥|说|背)|在这中间|每走.{0,8}(挥|招|停|做)");
   bool schedule=false;foreach(var candidate in c.obligations)if(!string.IsNullOrEmpty(candidate.during_target))schedule=true;
   if(routeLanguage&&!schedule)return "route_progress_requirement_missing: preserve intermediate/periodic effect with during_target and at_progress/every_meters";
   var ids=new HashSet<string>();var conditions=new HashSet<string>();
   foreach(var q in c.conditions??new TaskCondition[0]){if(string.IsNullOrEmpty(q.id)||!conditions.Add(q.id)||string.IsNullOrEmpty(q.question)||string.IsNullOrEmpty(q.source)||!request.Contains(q.source)||q.max_age<=0||q.max_age>120)return "invalid_condition";}
   foreach(var o in c.obligations){
    if(string.IsNullOrEmpty(o.id)||!ids.Add(o.id)||string.IsNullOrEmpty(o.source)||!request.Contains(o.source))return "ungrounded_obligation";
    if(o.count<1||o.count>32||TaskToolRegistry.Find(o.kind)==null||o.kind=="parallel"||o.kind=="report")return "invalid_obligation_kind_or_count";
    if(o.kind=="route_progress")return "route_progress_is_runtime_evidence_use_during_target";
    if((o.kind=="navigate"||o.kind=="interact")&&(o.target_id=="origin"||o.target_id=="mission_origin"))return "use_return_for_home_translation";
    if(o.kind=="return"&&System.Text.RegularExpressions.Regex.IsMatch(o.source,@"看向|看一下|看下|注视|望向|面向|面对")&&!System.Text.RegularExpressions.Regex.IsMatch(o.source,@"走|回到|返回|前往"))return "gaze_is_not_return_navigation";
    if(o.kind=="navigate"||o.kind=="look_at"||o.kind=="interact")if(string.IsNullOrEmpty(o.target_id)||!target(o.target_id))return "unknown_contract_target";
    if(o.kind=="return"&&o.target_id!="origin"&&o.target_id!="mission_origin")return "invalid_home";
    if(o.kind!="gesture"&&!string.IsNullOrEmpty(o.action_id))return "action_id_only_for_gesture";
    if(c.forbid_speech&&o.kind=="speak")return "negative_speech_must_not_be_positive_obligation";
    if(c.forbid_move&&(o.kind=="navigate"||o.kind=="return"||o.kind=="move_relative"))return "movement_contradicts_global_constraint";
    if(o.kind=="express"&&o.emotion!="happy"&&o.emotion!="neutral")return "invalid_expression_emotion";
    if(o.min_duration<0||o.min_duration>15||float.IsNaN(o.min_duration))return "invalid_required_duration";
    if(o.kind=="gesture"&&!action(o.action_id))return "unknown_contract_action";
    if(o.kind=="move_relative"&&(!ValidMoveDirection(o.direction)||!ValidMoveDistance(o.distance)))return "invalid_relative_movement";
    if(o.kind=="turn_body"&&(!ValidBodyTurn(o.yaw)||c.forbid_body_turn))return "invalid_or_forbidden_body_turn";
    if(o.kind=="look_direction"&&!ValidDirection(o.direction))return "invalid_direction";
    if(!string.IsNullOrEmpty(o.condition_id)&&(!conditions.Contains(o.condition_id)||(o.branch!="true"&&o.branch!="false"&&o.branch!="unknown")))return "invalid_condition_branch";
   }
   foreach(var o in c.obligations){
    if(float.IsNaN(o.at_progress)||float.IsInfinity(o.at_progress)||float.IsNaN(o.every_meters)||float.IsInfinity(o.every_meters))return "invalid_route_schedule";
    if(!string.IsNullOrEmpty(o.during_target)){
     if(o.kind!="gesture"&&o.kind!="speak"&&o.kind!="express"&&o.kind!="wait"&&o.kind!="look_at"&&o.kind!="check")return "route_schedule_requires_stationary_effect";
     if(o.count!=1||!string.IsNullOrEmpty(o.repeat_group)||!string.IsNullOrEmpty(o.condition_id))return "scheduled_effect_cannot_have_ambiguous_repetition_or_branch";
     if((o.at_progress>0)==(o.every_meters>0)||o.at_progress>=1||o.at_progress<0||o.every_meters<0||o.every_meters>0&&!ValidMoveDistance(o.every_meters))return "route_schedule_requires_fraction_or_distance";
     bool destination=false;foreach(var d in c.obligations)if((d.kind=="navigate"||d.kind=="return")&&d.target_id==o.during_target)destination=true;
     if(!destination)return "route_schedule_requires_arrival_obligation";
    }else if(o.at_progress!=0||o.every_meters!=0)return "route_schedule_target_required";
   }
   var prior=new HashSet<string>();foreach(var o in c.obligations){foreach(var id in o.after??new string[0])if(!prior.Contains(id))return "invalid_order_reference";prior.Add(o.id);foreach(var id in o.overlap??new string[0])if(!ids.Contains(id)||id==o.id)return "invalid_overlap_reference";}
   foreach(var q in c.conditions??new TaskCondition[0]){bool hasCheck=false;foreach(var o in c.obligations)if(o.kind=="check"&&o.text==q.question&&string.IsNullOrEmpty(o.condition_id))hasCheck=true;if(!hasCheck)return "condition_requires_check_obligation";}
   var groups=new Dictionary<string,int>();var closed=new HashSet<string>();string activeGroup=null;
   var byId=new Dictionary<string,TaskObligation>();foreach(var o in c.obligations)byId[o.id]=o;
   int expanded=0;
   foreach(var o in c.obligations){
    string group=o.repeat_group??"";
    if(group!=activeGroup){if(!string.IsNullOrEmpty(activeGroup))closed.Add(activeGroup);if(closed.Contains(group))return "repeat_group_must_be_contiguous";activeGroup=group;}
    if(group.Length>0){if(!System.Text.RegularExpressions.Regex.IsMatch(group,@"^[a-zA-Z0-9_-]{1,40}$"))return "invalid_repeat_group";int n;if(groups.TryGetValue(group,out n)&&n!=o.count)return "repeat_group_count_mismatch";groups[group]=o.count;expanded+=o.count;}
    else {expanded++;foreach(var dep in o.after??new string[0]){var priorObligation=byId[dep];if(o.count>1&&priorObligation.count>1&&o.source==priorObligation.source&&string.IsNullOrEmpty(priorObligation.repeat_group))return "repeated_sequence_requires_repeat_group";}}
   }
   if(expanded>256)return "expanded_obligation_limit";
   return null;
  }
  public const float MetersPerStep=0.4f;
  public static bool ValidMoveDirection(string d){return d=="forward"||d=="backward"||d=="left"||d=="right";}
  public static bool ValidMoveDistance(float d){return !float.IsNaN(d)&&!float.IsInfinity(d)&&d>=0.05f&&d<=4f;}
  public static bool ValidBodyTurn(float d){return !float.IsNaN(d)&&!float.IsInfinity(d)&&Math.Abs(d)>=0.1f&&Math.Abs(d)<=180;}
  public static bool ValidDirection(string d){return d=="up"||d=="down"||d=="forward"||d=="left"||d=="right";}
  public static string Policy(TaskContract c,MissionStep s,bool fromRoute=false) {
   if(c==null)return "contract_missing";
   if(s.kind=="route_progress")return Policy(c,RouteAuthorization(s),true);
   if(!fromRoute&&(s.kind=="navigate"||s.kind=="return"))foreach(var o in c.obligations)if(o.during_target==s.target_id)return "scheduled_route_requires_follow_route";
   if(s.kind=="turn_body"&&!ValidBodyTurn(s.yaw))return "invalid_body_turn";
   if(c.forbid_move&&(s.kind=="navigate"||s.kind=="return"||s.kind=="move_relative"))return "policy_translation_forbidden";
   if(s.kind=="move_relative"&&(!ValidMoveDirection(s.direction)||!ValidMoveDistance(s.distance)))return "policy_invalid_relative_movement";
   if(c.forbid_body_turn&&(s.kind=="turn_body"||s.kind=="navigate"||s.kind=="return"||s.kind=="observe"||s.kind=="look_at"&&!s.camera_only))return "policy_body_turn_forbidden";
   if(c.forbid_speech&&s.kind=="speak")return "policy_speech_forbidden";
   if(c.forbid_interact&&s.kind=="interact")return "policy_interaction_forbidden";
   if(s.kind=="wait"||s.kind=="report")return null;
   if(s.kind=="check"){foreach(var q in c.conditions??new TaskCondition[0])if(q.question==s.text)return null;}
   foreach(var o in c.obligations)if(Matches(o,s))return null;
   return "policy_unrequested_tool_or_target: "+s.kind+" "+s.target_id;
  }
  static MissionStep RouteAuthorization(MissionStep s){return new MissionStep{kind=s.target_id=="origin"||s.target_id=="mission_origin"?"return":"navigate",target_id=s.target_id};}
  public static bool Matches(TaskObligation o,MissionStep s){return o.kind==s.kind&&(o.kind!="turn_body"||Math.Abs(o.yaw-s.yaw)<0.05f)&&(o.kind!="move_relative"||ValidMoveDistance(o.distance)&&ValidMoveDistance(s.distance)&&Math.Abs(o.distance-s.distance)<=0.005f)&&(string.IsNullOrEmpty(o.target_id)||o.target_id==s.target_id)&&(string.IsNullOrEmpty(o.action_id)||o.action_id==s.action_id)&&(string.IsNullOrEmpty(o.direction)||o.direction==s.direction)&&(string.IsNullOrEmpty(o.text)||o.text==s.text)&&(string.IsNullOrEmpty(o.emotion)||o.emotion==s.emotion)&&(o.min_duration<=0||s.duration+0.05f>=o.min_duration)&&(!o.angles_exact||(Math.Abs(o.pitch-s.pitch)<0.05&&Math.Abs(o.yaw-s.yaw)<0.05&&(string.IsNullOrEmpty(o.reference)||o.reference==s.reference)))&&(!o.camera_only||s.camera_only);}
  static MissionStep Step(MissionStepResult r){return new MissionStep{kind=r.kind,distance=r.requested_distance,target_id=r.target_id,action_id=r.action_id,direction=r.direction,text=r.text,emotion=r.emotion,duration=r.duration,pitch=r.requested_pitch,yaw=r.requested_yaw,reference=r.reference,camera_only=r.camera_only};}
  static MissionStepResult ConditionResult(TaskCondition q,List<MissionStepResult> receipts,float at,bool fresh=true){MissionStepResult found=null;foreach(var r in receipts)if(r.kind=="check"&&r.text==q.question&&r.ended<=at&&(!fresh||at-r.ended<=q.max_age))if(found==null||r.ended>found.ended)found=r;return found;}
  public static string Branch(TaskContract c,TaskObligation o,List<MissionStepResult> receipts,float at,bool fresh=true){if(string.IsNullOrEmpty(o.condition_id))return "unconditional";foreach(var q in c.conditions)if(q.id==o.condition_id){var r=ConditionResult(q,receipts,at,fresh);return r==null?"unresolved":!r.condition_known?"unknown":r.condition_value?"true":"false";}return "unresolved";}
  public static string Dispatch(TaskContract c,MissionStep s,List<MissionStepResult> receipts,float at,bool fromRoute=false){if(s.kind=="route_progress")return Dispatch(c,RouteAuthorization(s),receipts,at,true);string p=Policy(c,s,fromRoute);if(p!=null)return p;if(s.kind=="wait"||s.kind=="report"||s.kind=="check")return null;
   if(s.kind=="turn_body"){int budget=0,used=0;foreach(var o in c.obligations)if(Matches(o,s)){string branch=Branch(c,o,receipts,at);if(branch=="unconditional"||branch==o.branch)budget+=o.count;}foreach(var r in receipts)if(r.kind=="turn_body"&&Math.Abs(r.requested_yaw-s.yaw)<0.05f){if(!string.IsNullOrEmpty(s.id)&&s.id==r.id)return null;used++;}if(used>=budget)return "policy_body_turn_count_exceeded";}
   if(s.kind=="move_relative"){
    int budget=0,used=0;foreach(var o in c.obligations)if(Matches(o,s)){string b=Branch(c,o,receipts,at);if(b=="unconditional"||b==o.branch)budget+=o.count;}
    foreach(var r in receipts)if(r.kind=="move_relative"&&r.direction==s.direction&&Math.Abs(r.requested_distance-s.distance)<=0.005f){if(!string.IsNullOrEmpty(s.id)&&r.id==s.id)return null;used++;}
    if(used>=budget)return "policy_relative_move_count_exceeded";
   }
   foreach(var o in c.obligations)if(Matches(o,s)){string b=Branch(c,o,receipts,at);if(b=="unconditional"||b==o.branch)return null;}return "policy_conditional_branch_not_proven";}
  // Expand each repeated sequence into occurrence obligations before matching receipts.
  // A dependency inside a repeat group refers to the SAME iteration; the next
  // iteration starts after the previous group's last member. Outside deps are barriers.

  static bool ScheduleHit(TaskObligation o,MissionStepResult effect,List<MissionStepResult> receipts){
   if(string.IsNullOrEmpty(o.during_target))return true;
   foreach(var p in receipts)if(p.kind=="route_progress"&&p.status=="succeeded"&&p.target_id==o.during_target&&p.route_id==effect.route_id&&p.ended<=effect.started+0.001f&&Math.Abs(p.route_fraction-effect.route_fraction)<0.002f){
    if(o.at_progress>0)return !effect.route_done&&Math.Abs(p.route_fraction-o.at_progress)<0.005f;
    return Math.Abs(p.requested_distance-o.every_meters)<0.005f;
   }
   return false;
  }
  static bool EverySegmentFulfilled(TaskObligation o,List<MissionStepResult> receipts,int generation){
   var routes=new Dictionary<string,List<MissionStepResult>>();
   foreach(var p in receipts)if(p.kind=="route_progress"&&p.target_id==o.during_target&&p.status=="succeeded"&&(generation<0||p.generation==generation)){
    List<MissionStepResult> list;if(!routes.TryGetValue(p.route_id,out list))routes[p.route_id]=list=new List<MissionStepResult>();list.Add(p);
   }
   foreach(var list in routes.Values){
    if(list.Count==0||!list[list.Count-1].route_done)continue;bool all=true;
    for(int i=0;i<list.Count;i++){
     var p=list[i];if(Math.Abs(p.requested_distance-o.every_meters)>0.005f){all=false;break;}
     bool hit=false;foreach(var effect in receipts)if(effect.status=="succeeded"&&effect.generation==p.generation&&Matches(o,Step(effect))&&ScheduleHit(o,effect,receipts)&&effect.route_id==p.route_id&&effect.started>=p.ended-0.001f&&(i==list.Count-1||effect.ended<=list[i+1].started+0.001f)){hit=true;break;}
     if(!hit){all=false;break;}
    }
    if(all)return true;
   }
   return false;
  }
  static TaskContract ExpandRepeatGroups(TaskContract original){
   bool has=false;foreach(var x in original.obligations??new TaskObligation[0])if(!string.IsNullOrEmpty(x.repeat_group))has=true;if(!has)return original;
   var list=new List<TaskObligation>();var lastId=new Dictionary<string,string>();
   for(int index=0;index<original.obligations.Length;){
    var first=original.obligations[index];bool grouped=!string.IsNullOrEmpty(first.repeat_group);int end=index+1;
    if(grouped)while(end<original.obligations.Length&&original.obligations[end].repeat_group==first.repeat_group)end++;
    var memberIds=new HashSet<string>();for(int k=index;k<end;k++)memberIds.Add(original.obligations[k].id);
    int iterations=grouped?first.count:1;
    for(int iteration=0;iteration<iterations;iteration++)for(int k=index;k<end;k++){
     var o=original.obligations[k];var copy=JsonUtility.FromJson<TaskObligation>(JsonUtility.ToJson(o));
     copy.id=grouped?o.id+"__iteration_"+(iteration+1):o.id;copy.count=grouped?1:o.count;copy.repeat_group=null;
     var deps=new List<string>();foreach(string dep in o.after??new string[0])deps.Add(grouped&&memberIds.Contains(dep)?dep+"__iteration_"+(iteration+1):lastId.ContainsKey(dep)?lastId[dep]:dep);
     if(grouped){if(k>index)deps.Add(original.obligations[k-1].id+"__iteration_"+(iteration+1));else if(iteration>0)deps.Add(original.obligations[end-1].id+"__iteration_"+iteration);}
     copy.after=deps.ToArray();var overlaps=new List<string>();foreach(string dep in o.overlap??new string[0])overlaps.Add(grouped&&memberIds.Contains(dep)?dep+"__iteration_"+(iteration+1):lastId.ContainsKey(dep)?lastId[dep]:dep);copy.overlap=overlaps.ToArray();
     // An explicit final pose applies to the last occurrence, not earlier milestones.
     if(grouped&&iteration<iterations-1)copy.final=false;
     list.Add(copy);
    }
    for(int k=index;k<end;k++)lastId[original.obligations[k].id]=grouped?original.obligations[k].id+"__iteration_"+iterations:original.obligations[k].id;
    index=end;
   }
   return new TaskContract{version=original.version,error=original.error,forbid_move=original.forbid_move,forbid_body_turn=original.forbid_body_turn,forbid_speech=original.forbid_speech,forbid_interact=original.forbid_interact,conditions=original.conditions,obligations=list.ToArray()};
  }
  public static TaskCompletion Evaluate(TaskContract c,List<MissionStepResult> receipts,bool program,float now,int generation=-1){
   var v=new TaskCompletion{program_succeeded=program};if(c==null){v.reason="contract_missing";return v;}
   var used=new HashSet<string>();var matches=new Dictionary<string,List<MissionStepResult>>();
   c=ExpandRepeatGroups(c);
   foreach(var o in c.obligations){
    string branch=Branch(c,o,receipts,now,false);
    if(branch=="unknown"){bool handled=false;foreach(var candidate in c.obligations)if(candidate.condition_id==o.condition_id&&candidate.branch=="unknown")handled=true;if(!handled){v.unknown.Add(o.id+":condition_unknown_unhandled");continue;}}if(branch=="unresolved"){v.unknown.Add(o.id+":condition_not_evaluated_or_expired");continue;}
    if(branch!="unconditional"&&branch!=o.branch)continue;
    var hits=new List<MissionStepResult>();
    foreach(var r in receipts){
     if(generation>=0&&r.generation!=generation)continue;
     bool unknownHandled=false;if(o.kind=="check"&&r.condition_evaluated&&!r.condition_known)foreach(var cond in c.conditions??new TaskCondition[0])if(cond.question==r.text)foreach(var branchObligation in c.obligations)if(branchObligation.condition_id==cond.id&&branchObligation.branch=="unknown")unknownHandled=true;
     if(r.status!="succeeded"&&!unknownHandled||used.Contains(r.id)||!Matches(o,Step(r))||!ScheduleHit(o,r,receipts))continue;
     if(!string.IsNullOrEmpty(o.condition_id)&&Branch(c,o,receipts,r.started)!=o.branch)continue;
     bool ordered=true;foreach(var dep in o.after??new string[0]){List<MissionStepResult> previous;if(!matches.TryGetValue(dep,out previous)||previous.Count==0||previous[previous.Count-1].ended>r.started)ordered=false;}
     if(!ordered)continue;
     if(o.max_age>0&&now-r.ended>o.max_age)continue;
     if(o.final){bool overwritten=false;int mask=TaskToolRegistry.Resources(o.kind);foreach(var later in receipts)if(later.started>r.ended&&later.status=="succeeded"&&(TaskToolRegistry.Resources(later.kind)&mask)!=0&&later.kind!="check")overwritten=true;if(overwritten)continue;}
     if(o.kind=="check"&&!r.condition_known&&!unknownHandled)continue;
     hits.Add(r);used.Add(r.id);if(hits.Count==o.count)break;
    }
    matches[o.id]=hits;if(hits.Count<o.count)v.missing.Add(o.id+":"+o.kind+" "+o.target_id+" expected="+o.count+" actual="+hits.Count);else v.satisfied.Add(o.id);
   }
   foreach(var o in c.obligations){List<MissionStepResult> a;if(!matches.TryGetValue(o.id,out a)||a.Count==0)continue;foreach(var dep in o.overlap??new string[0]){List<MissionStepResult>b;bool overlaps=false;if(matches.TryGetValue(dep,out b))foreach(var x in a)foreach(var y in b)if(x.started<y.ended&&y.started<x.ended)overlaps=true;if(!overlaps)v.missing.Add(o.id+":no_required_overlap_with_"+dep);}}
   foreach(var o in c.obligations)if(o.every_meters>0&&!EverySegmentFulfilled(o,receipts,generation))v.missing.Add(o.id+":route_interval_effect_missing_or_wrong_spacing");
   v.task_succeeded=program&&v.missing.Count==0&&v.unknown.Count==0;v.reason=v.task_succeeded?"contract_fulfilled":!program?"program_failed":"contract_incomplete: "+string.Join(";",v.missing.ToArray())+" "+string.Join(";",v.unknown.ToArray());return v;
  }
 }
}
