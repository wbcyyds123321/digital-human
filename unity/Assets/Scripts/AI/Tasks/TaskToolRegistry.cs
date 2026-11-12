using System;
using System.Text;
namespace Yixuan.AI.Tasks {
 public sealed class TaskToolSpec {
  public string Name,Kind,Schema,Effects;public int Resources;public bool Idempotent;public float Timeout;
  public TaskToolSpec(string n,string k,string s,string e,int r,bool i=false,float t=120){Name=n;Kind=k;Schema=s;Effects=e;Resources=r;Idempotent=i;Timeout=t;}
 }
 public static class TaskToolRegistry {
  public static readonly TaskToolSpec[] All={
   new TaskToolSpec("turn_body","turn_body","(signed_degrees_-180_to_180,options?)","body_yaw_only; positive=right; negative=left; no_translation",3,false,8),
   new TaskToolSpec("move_relative","move_relative","('forward'|'backward'|'left'|'right',{steps=1..10} OR {distance=0.05..4})","straight_body_relative_position,animation; preserves_body_yaw; step=0.4m",3),
   new TaskToolSpec("follow_route","route_progress","(target_id_or_returned_route_id,{to_fraction=0<f<=1} OR {distance=0.05..4} OR nil)","resumable_Astar_path; receipt.route_id/done/progress; default=finish; partial_not_arrival",3,false,0),
   new TaskToolSpec("navigate","navigate","(target_id,options?)","position,body_yaw,animation,camera",3,false,0),
   new TaskToolSpec("return_home","return","('origin'|'mission_origin',options?)","position,body_yaw,animation,camera",3,false,0),
   new TaskToolSpec("sit","sit","(confirmed_target_id)","seated_in_registered_seat_with_real_receipt; requires current confirmed-selection context",3,false,30),
   new TaskToolSpec("stand","stand","(confirmed_target_id)","stood_from_seat_with_real_receipt; requires current confirmed-selection context",3,false,25),
   new TaskToolSpec("look_at","look_at","(target_id,{camera_only=false,body_facing=false}?)","camera,optional_body_yaw",3),
   new TaskToolSpec("look_direction","look_direction","('up'|'down'|'forward'|'left'|'right',{yaw=0,pitch=30,reference='body'|'world',hold=true}?)","camera,optional_body_yaw",3),
   new TaskToolSpec("gesture","gesture","(action_id,options?)","body_animation",1),
   new TaskToolSpec("interact","interact","(target_id,{key='unique_id'}?)","world_state",1,true),
   new TaskToolSpec("observe","observe","()","camera,body_yaw,vision_request",3),
   new TaskToolSpec("select_visible","select_visible","(description,{slot=declared_target_slot_id})","task_scoped_confirmed_visual_target_receipt;real_locate_aim_new_frame_confirm;returns_target_ref",3,false,90),
   new TaskToolSpec("check","check","(exact_contract_question)","vision_request",3),
   new TaskToolSpec("speak","speak","(text)","voice",4),
   new TaskToolSpec("express","express","('happy'|'neutral',{duration=2}?)","expression",8),
   new TaskToolSpec("wait","wait","(seconds_0_to_15)","none",0,false,15),
   new TaskToolSpec("report","report","()","subtitle",0),
   new TaskToolSpec("board_vehicle","board_vehicle","(target_id)","walk_to_the_vehicle_side_then_board;active_actor=vehicle;persistent_in_vehicle",3,false,420),
   new TaskToolSpec("drive_vehicle","drive_vehicle","(target_id_or_anchor_id)","real_vehicle_route;vehicle_and_character_move;stays_in_vehicle",3,false,0),
   new TaskToolSpec("exit_vehicle","exit_vehicle","()","safe_side_exit;active_actor=character;fails_with_no_safe_exit",3,true,60),
   new TaskToolSpec("stop_vehicle","stop_vehicle","()","stop_vehicle_motion;stays_in_vehicle",3,true,15),
   new TaskToolSpec("set_flame","set_flame","(flame_target_id,{lit=true|false})","world_state;requires_proximity_to_operation_point;idempotent",1,true,60),
   new TaskToolSpec("query_dynamic","query_dynamic","(target_id)","read_only_actor_vehicle_flame_position_state",0,true,15),
   new TaskToolSpec("parallel","parallel","({{kind='...',target_id='...'},...})","union_of_batch",0)
  };
  public static TaskToolSpec Find(string name){foreach(var s in All)if(s.Name==name||s.Kind==name)return s;return null;}
  public static int Resources(string kind){var s=Find(kind);return s==null?0:s.Resources;}
  public static string Prompt(){var b=new StringBuilder();foreach(var s in All)b.AppendLine("game."+s.Name+s.Schema+"; effects="+s.Effects+"; resources="+s.Resources+(s.Timeout>0?"; timeout<="+s.Timeout:"; timeout=automatic_path_length_and_walk_speed")+"; keyed="+s.Idempotent);return b.ToString();}
 }
}
