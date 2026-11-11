using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using MoonSharp.Interpreter;
namespace Yixuan.AI.Tasks {
 [Serializable] public sealed class TaskLintReport {public string engine="MoonSharp 2 AST (reflection adapter)";public bool ast_available;public List<string> errors=new List<string>(),direct_calls=new List<string>(),unknown=new List<string>();}
 public static class TaskProgramLint {
  const BindingFlags F=BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
  static object Get(object o,string name){if(o==null)return null;var f=o.GetType().GetField(name,F);return f==null?null:f.GetValue(o);}
  public static TaskLintReport Analyze(string source,TaskContract contract){
   var report=new TaskLintReport();try{
    var vm=new Script(CoreModules.Basic|CoreModules.TableIterators|CoreModules.Math);var a=typeof(Script).Assembly;
    var code=Activator.CreateInstance(a.GetType("MoonSharp.Interpreter.Debugging.SourceCode"),F,null,new object[]{"lint",source,0,vm},null);
    var create=a.GetType("MoonSharp.Interpreter.Tree.Fast_Interface.Loader_Fast").GetMethod("CreateLoadingContext",BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic);
    var ctx=create.Invoke(null,new object[]{vm,code});var ast=Activator.CreateInstance(a.GetType("MoonSharp.Interpreter.Tree.Statements.ChunkStatement"),F,null,new[]{ctx},null);
    report.ast_available=true;var seen=new HashSet<object>();Walk(ast,report,contract,seen,0);
    report.unknown.Add("Control-flow reachability, aliases, computed arguments, loop counts and semantic coverage require runtime receipts; AST inspection is not a proof of task completion.");
    if(report.direct_calls.Count==0&&report.unknown.Count==1&&contract.obligations.Length>0)report.errors.Add("no_game_effects_for_required_obligations");
   }catch(Exception e){report.unknown.Add("AST adapter unavailable: "+e.GetBaseException().Message+"; runtime policy and completion remain mandatory");}
   return report;
  }
  static void Walk(object node,TaskLintReport r,TaskContract c,HashSet<object> seen,int depth){
   if(node==null||depth>128||!seen.Add(node))return;var type=node.GetType();
   if(type.IsGenericType&&type.GetGenericTypeDefinition()==typeof(KeyValuePair<,>)){Walk(type.GetProperty("Key").GetValue(node,null),r,c,seen,depth+1);Walk(type.GetProperty("Value").GetValue(node,null),r,c,seen,depth+1);return;}
   if(type.Name=="FunctionCallExpression"){
    var fun=Get(node,"m_Function");string method=Get(fun,"m_Name") as string;var bas=Get(fun,"m_BaseExp");string baseName=Get(bas,"m_VarName") as string;
    if(baseName=="game"&&method!=null){
     r.direct_calls.Add(method);if(method!="state"&&method!="resolve"&&TaskToolRegistry.Find(method)==null)r.errors.Add("unknown_game_tool: "+method);
     var args=Get(node,"m_Arguments") as IList;var literal=args!=null&&args.Count>0?Get(args[0],"m_Value") as DynValue:null;
     if(method=="navigate"||method=="return_home"||method=="speak"||method=="interact"||method=="gesture"){
      var s=new MissionStep{kind=method=="return_home"?"return":method};if(literal!=null&&literal.Type==DataType.String){if(method=="gesture")s.action_id=literal.String;else if(method=="speak")s.text=literal.String;else s.target_id=literal.String;string p=TaskContractRules.Policy(c,s);if(p!=null)r.errors.Add(p);}else r.unknown.Add("dynamic argument: game."+method);
     }
    }else r.unknown.Add("indirect or local function call");
   }
   if(type.Namespace!=null&&type.Namespace.StartsWith("MoonSharp.Interpreter.Tree")){
    for(var t=type;t!=null&&t.Namespace!=null&&t.Namespace.StartsWith("MoonSharp.Interpreter.Tree");t=t.BaseType)
     foreach(var f in t.GetFields(F|BindingFlags.DeclaredOnly)){var value=f.GetValue(node);var list=value as IList;if(list!=null){foreach(var v in list)Walk(v,r,c,seen,depth+1);}else if(value!=null&&value.GetType().Namespace!=null&&value.GetType().Namespace.StartsWith("MoonSharp.Interpreter.Tree"))Walk(value,r,c,seen,depth+1);}
   }
  }
 }
}
