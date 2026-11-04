using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
namespace Yixuan.AI.Tasks {
 [Serializable] public sealed class RuntimeTestResult {public string name,expected,actual,reason,directory;public bool pass;public float elapsed;}
 [Serializable] public sealed class RuntimeTestReport {public string suite;public List<RuntimeTestResult> cases=new List<RuntimeTestResult>();public bool done;}
 public static class TaskRuntimeUnitAcceptance {
  static TaskObligation O(string id,string kind,string target=""){return new TaskObligation{id=id,source="测试",kind=kind,target_id=target,count=1};}
  static MissionStepResult R(string id,string kind,string target,float start,float end){return new MissionStepResult{id=id,kind=kind,target_id=target,status="succeeded",started=start,ended=end};}
  public static RuntimeTestReport Run(string directory){
   var report=new RuntimeTestReport{suite="pure contract/policy/AST/ownership, no real LLM"};
   Action<string,bool> check=(name,ok)=>report.cases.Add(new RuntimeTestResult{name=name,pass=ok,expected="true",actual=ok.ToString()});
   var c=new TaskContract{obligations=new[]{O("home","return","origin"),O("wave","gesture"),O("lamp","navigate","lamp")}};c.obligations[1].after=new[]{"home"};c.obligations[2].after=new[]{"wave"};
   check("empty program cannot satisfy mission",!TaskContractRules.Evaluate(c,new List<MissionStepResult>(),true,10).task_succeeded);
   var receipts=new List<MissionStepResult>{R("1","return","origin",1,2),R("2","gesture","",3,4),R("3","navigate","lamp",5,6)};
   check("ordered receipts satisfy",TaskContractRules.Evaluate(c,receipts,true,10).task_succeeded);
   receipts[1].started=0;check("wrong order rejected",!TaskContractRules.Evaluate(c,receipts,true,10).task_succeeded);receipts[1].started=3;
   check("stale generation cannot satisfy",!TaskContractRules.Evaluate(c,receipts,true,10,9).task_succeeded);
   check("program failure retained",!TaskContractRules.Evaluate(c,receipts,false,10).task_succeeded);
   c.forbid_move=true;check("translation denied",TaskContractRules.Policy(c,new MissionStep{kind="navigate",target_id="lamp"})!=null);c.forbid_move=false;
   c.forbid_speech=true;check("silence denied",TaskContractRules.Policy(c,new MissionStep{kind="speak",text="hello"})!=null);
   c.forbid_body_turn=true;check("body turn denied",TaskContractRules.Policy(c,new MissionStep{kind="look_at",target_id="lamp"})!=null);c.forbid_body_turn=false;
   check("target substitution denied",TaskContractRules.Policy(c,new MissionStep{kind="navigate",target_id="car"})!=null);
   c.obligations[0].final=true;check("overwritten final state rejected",!TaskContractRules.Evaluate(c,receipts,true,10).task_succeeded);c.obligations[0].final=false;
   c.obligations[1].count=2;check("one receipt not two gestures",!TaskContractRules.Evaluate(c,receipts,true,10).task_succeeded);c.obligations[1].count=1;
   c.obligations[1].overlap=new[]{"lamp"};check("nonoverlap rejected",!TaskContractRules.Evaluate(c,receipts,true,10).task_succeeded);c.obligations[1].overlap=null;
   var q=new TaskCondition{id="q",source="测试",question="天空黑吗",max_age=30};var cc=new TaskContract{conditions=new[]{q},obligations=new[]{O("look","look_direction"),O("branch","return","origin")}};cc.obligations[0].direction="up";cc.obligations[1].condition_id="q";cc.obligations[1].branch="true";
   var cr=new List<MissionStepResult>{new MissionStepResult{id="look",kind="look_direction",direction="up",status="succeeded",started=1,ended=2},new MissionStepResult{id="q",kind="check",text=q.question,status="failed",started=3,ended=4}};
   check("unknown not false",TaskContractRules.Branch(cc,cc.obligations[1],cr,5)=="unknown");
   check("unknown conditional cannot silently succeed",!TaskContractRules.Evaluate(cc,cr,true,5).task_succeeded);
   check("true branch denied when unknown",TaskContractRules.Dispatch(cc,new MissionStep{kind="return",target_id="origin"},cr,5)!=null);
   cr[1].condition_known=true;cr[1].condition_value=true;cr[1].status="succeeded";check("true branch allowed",TaskContractRules.Dispatch(cc,new MissionStep{kind="return",target_id="origin"},cr,5)==null);
   check("expired condition denied",TaskContractRules.Dispatch(cc,new MissionStep{kind="return",target_id="origin"},cr,50)!=null);
   check("ungrounded contract rejected",TaskContractRules.Validate(c,"other",x=>true,x=>true)!=null);
   check("ownership acquired",TaskAuthority.Acquire("unit-test",3));check("unowned external body rejected",!TaskAuthority.Allows(1));using(TaskAuthority.Enter("unit-test"))check("owner allowed",TaskAuthority.Allows(3));TaskAuthority.Release("unit-test");check("owner released",TaskAuthority.Allows(3));
   var lint=TaskProgramLint.Analyze("return {ok=true}",c);check("AST adapter available",lint.ast_available);check("empty AST rejects missing effects",lint.errors.Count>0);
   lint=TaskProgramLint.Analyze("-- game.navigate('lamp')\nlocal x='game.navigate(lamp)'; return {ok=true}",c);check("comments and strings not mistaken for calls",lint.direct_calls.Count==0);
   lint=TaskProgramLint.Analyze("game.navigate('car'); return {ok=true}",c);check("AST constant target violation",lint.errors.Count>0);
   lint=TaskProgramLint.Analyze("local f=game.navigate; f('car'); return {ok=true}",c);check("indirect call marked unknown",lint.unknown.Count>1);
   var timed=new TaskContract{obligations=new[]{new TaskObligation{id="e",kind="express",source="测试",emotion="happy",count=1,min_duration=2}}};
   check("wrong emotion denied",TaskContractRules.Policy(timed,new MissionStep{kind="express",emotion="neutral",duration=2})!=null);
   check("short duration denied",TaskContractRules.Policy(timed,new MissionStep{kind="express",emotion="happy",duration=1})!=null);
   var angled=new TaskContract{obligations=new[]{new TaskObligation{id="g",kind="look_direction",source="测试",direction="up",count=1,angles_exact=true,pitch=20}}};
   check("wrong precise angle denied",TaskContractRules.Policy(angled,new MissionStep{kind="look_direction",direction="up",pitch=10})!=null);
   var scoped=new TaskContract();TaskContractRules.ApplyExplicitPermissions(scoped,"不要说话，先走到街道中央，然后站在原地看向红绿灯");check("local stationary does not forbid earlier navigation",!scoped.forbid_move&&scoped.forbid_speech);
   scoped=new TaskContract();TaskContractRules.ApplyExplicitPermissions(scoped,"站在原地先抬头，然后回正");check("leading stationary remains global",scoped.forbid_move);
   scoped=new TaskContract();TaskContractRules.ApplyExplicitPermissions(scoped,"先抬头，然后回正，整个任务不要移动");check("explicit global prohibition remains global",scoped.forbid_move);
   scoped=new TaskContract();TaskContractRules.ApplyExplicitPermissions(scoped,"先走到街道中央，站在原地看向红绿灯");check("local stationary without connector not global",!scoped.forbid_move);
   report.done=true;Directory.CreateDirectory(directory);File.WriteAllText(Path.Combine(directory,"unit_report.json"),JsonUtility.ToJson(report,true));return report;
  }
 }
 public sealed class TaskRuntimeAcceptance:MonoBehaviour {
  public TaskMissionRunner Runner;public string Output="Captures/Evidence/task_runtime_v2_2026-10-25";public RuntimeTestReport Report;public bool Running;
  public void Begin(){if(Running)return;Runner=FindObjectOfType<TaskMissionRunner>();Running=true;Report=new RuntimeTestReport{suite="injected Lua + independent explicit test contracts; actual Unity tools"};StartCoroutine(Run());}
  TaskContract C(params TaskObligation[] o){return new TaskContract{obligations=o};}
  TaskObligation O(string id,string kind,string target="",string direction=""){return new TaskObligation{id=id,source="验收",kind=kind,target_id=target,direction=direction,count=1};}
  IEnumerator Case(string name,string source,TaskContract contract,string expected){
   float start=Time.realtimeSinceStartup;Runner.SubmitContractTest("验收："+name,source,contract);float limit=start+70;
   while(Runner.IsRunning&&Time.realtimeSinceStartup<limit)yield return null;if(Runner.IsRunning)Runner.Cancel("harness_timeout");
   var e=Runner.Evidence;Report.cases.Add(new RuntimeTestResult{name=name,expected=expected,actual=e.status,reason=e.reason,directory=Runner.RunDirectory,elapsed=e.elapsed,pass=expected=="not_succeeded"?e.status!="succeeded":e.status==expected});Save();yield return null;
  }
  void Save(){Directory.CreateDirectory(Output);File.WriteAllText(Path.Combine(Output,"executor_report.json"),JsonUtility.ToJson(Report,true));}
  IEnumerator Run(){
   Runner.Session.ParallelTaskDialogue=false;Runner.VoiceEnabled=false;Runner.EvidenceRoot=Path.Combine(Output,"injected");Runner.CodeTimeout=60;
   yield return Case("empty_return_not_success","return {ok=true}",C(O("a","return","origin")),"not_succeeded");
   yield return Case("missing_step_not_success","game.look_direction('up');return {ok=true}",C(O("a","look_direction","","up"),O("b","look_direction","","forward")),"failed");
   var prohibited=C(O("a","look_direction","","up"));prohibited.forbid_move=true;
   yield return Case("static_forbid_move","game.navigate('place_1');return {ok=true}",prohibited,"not_succeeded");
   yield return Case("dynamic_forbid_move","local n='navigate';game[n]('place_1');return {ok=true}",prohibited,"failed");
   var silence=C(O("a","look_direction","","up"));silence.forbid_speech=true;
   yield return Case("dynamic_forbid_speech","local n='speak';game[n]('不应播放');return {ok=true}",silence,"failed");
   yield return Case("up_real_frame","local r=game.look_direction('up');return {ok=r.ok}",C(O("a","look_direction","","up")),"succeeded");
   yield return Case("forward_real_frame","local r=game.look_direction('forward');return {ok=r.ok}",C(O("a","look_direction","","forward")),"succeeded");
   yield return Case("angle_out_of_range","local r=game.look_direction('up',{pitch=89});return {ok=r.ok}",C(O("a","look_direction","","up")),"failed");
   yield return Case("parallel_conflict","game.parallel({{kind='look_direction',direction='up'},{kind='look_direction',direction='forward'}});return {ok=true}",C(O("a","look_direction","","up"),O("b","look_direction","","forward")),"failed");
   yield return Case("parallel_atomic_policy","game.parallel({{kind='look_direction',direction='up'},{kind='speak',text='不该播放'}});return {ok=true}",silence,"failed");
   var order=C(O("a","look_direction","","up"),O("b","look_direction","","forward"));order.obligations[1].after=new[]{"a"};
   yield return Case("wrong_order","game.look_direction('forward');game.look_direction('up');return {ok=true}",order,"failed");
   yield return Case("ordered_gaze","game.look_direction('up');game.look_direction('forward');return {ok=true}",order,"succeeded");
   yield return Case("loop_count","for i=1,2 do game.look_direction('up');game.look_direction('forward') end;return {ok=true}",C(new TaskObligation{id="a",source="验收",kind="look_direction",direction="up",count=2},new TaskObligation{id="b",source="验收",kind="look_direction",direction="forward",count=2}),"succeeded");
   yield return Case("instruction_budget","while true do end;game.look_direction('up')",C(O("a","look_direction","","up")),"not_succeeded");
   yield return Case("up_label_with_down_angle","game.look_direction('up',{pitch=-20});return {ok=true}",C(O("a","look_direction","","up")),"failed");
   Runner.CodeTimeout=600;Runner.Session.ParallelTaskDialogue=true;Report.done=true;Running=false;Save();
  }
 }
}
