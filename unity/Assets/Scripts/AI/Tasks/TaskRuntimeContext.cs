using System;
using System.Collections.Generic;
using UnityEngine;
namespace Yixuan.AI.Tasks {
 [Serializable] public sealed class TaskRuntimeEvent {public long sequence;public int generation;public string request_id,mission_id,scene_version,type,tool_id,status,detail;public float at;}
 public sealed class TaskRuntimeContext {
  public readonly string RequestId=Guid.NewGuid().ToString("N"),MissionId=Guid.NewGuid().ToString("N");
  public int Generation,FactVersion;public string SceneVersion,EvidenceDirectory;public bool Active=true,Muted;long sequence;
  public readonly List<TaskRuntimeEvent> Events=new List<TaskRuntimeEvent>();
  public TaskRuntimeEvent Emit(string type,string tool="",string status="",string detail=""){var e=new TaskRuntimeEvent{sequence=++sequence,generation=Generation,request_id=RequestId,mission_id=MissionId,scene_version=SceneVersion,type=type,tool_id=tool,status=status,detail=detail,at=Time.realtimeSinceStartup};Events.Add(e);if(type.StartsWith("Action")||type.StartsWith("Mission")||type=="CompletionChecked")FactVersion++;return e;}
 }
 // A scope must never remain active across a coroutine yield.
 public static class TaskAuthority {
  static readonly string[] Owners=new string[4];public static string Caller{get;private set;}
  public static bool Allows(int mask){for(int i=0;i<4;i++)if((mask&(1<<i))!=0&&Owners[i]!=null&&Owners[i]!=Caller)return false;return true;}
  public static bool Acquire(string owner,int mask){for(int i=0;i<4;i++)if((mask&(1<<i))!=0&&Owners[i]!=null&&Owners[i]!=owner)return false;for(int i=0;i<4;i++)if((mask&(1<<i))!=0)Owners[i]=owner;return true;}
  public static bool CanContinue(string owner,int mask){for(int i=0;i<4;i++)if((mask&(1<<i))!=0&&Owners[i]!=null&&Owners[i]!=owner)return false;return true;}
  public static void Release(string owner){for(int i=0;i<4;i++)if(Owners[i]==owner)Owners[i]=null;}
  public static IDisposable Enter(string owner){return new Scope(owner);}
  sealed class Scope:IDisposable{string before;public Scope(string owner){before=Caller;Caller=owner;}public void Dispose(){Caller=before;}}
  [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]static void Reset(){Array.Clear(Owners,0,Owners.Length);Caller=null;}
 }
}
