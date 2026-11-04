using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MoonSharp.Interpreter;
using UnityEngine;

namespace Yixuan.AI.Tasks
{
    // Opt-in acceptance harness. Test-injected programs are explicitly separate from core-LLM runs.
    public class TaskCodeAcceptance : MonoBehaviour
    {
        public string Root="D:/AI/GameScaling/docs/evidence/task_code_2026-10-24";
        public bool Running {get;private set;}
        public TaskAcceptanceSuite.SuiteReport Summary=new TaskAcceptanceSuite.SuiteReport();
        TaskMissionRunner r; Vector3 start; bool oldVoice; string oldRoot;
        class TestReport : ILLMClient
        {
            public bool IsBusy {get{return false;}} public string LastError {get{return null;}}
            public Task<string> ChatAsync(List<ChatMessage> messages,CancellationToken ct) {return Task.FromResult("验收用替身汇报；实际操作以Unity回执为准。");}
        }
        class Replies : ILLMClient
        {
            readonly Queue<string> values;public Replies(params string[] source){values=new Queue<string>(source);}
            public bool IsBusy {get{return false;}} public string LastError {get{return null;}}
            public Task<string> ChatAsync(List<ChatMessage> messages,CancellationToken ct){return Task.FromResult(values.Count>0?values.Dequeue():"验收汇报");}
        }
        void Add(string name,bool pass,string detail)
        {
            Summary.total++;if(pass)Summary.passed++;Summary.cases.Add(new TaskAcceptanceSuite.SuiteCase{name=name,pass=pass,reason=detail,directory=r!=null?r.RunDirectory:null});Save();
        }
        void Save(){Directory.CreateDirectory(Root);File.WriteAllText(Path.Combine(Root,"code_runtime_suite.json"),JsonUtility.ToJson(Summary,true));}
        TaskLuaRuntime VM(string source){return new TaskLuaRuntime(source,s=>new Table(s),s=>s);}
        void Pure(string name,string source,bool throws=false)
        {
            bool pass=false;string detail="";
            try {var vm=VM(source);vm.MaxSlices=4;var v=vm.Resume();while(!vm.Done && v.Type==DataType.YieldRequest)v=vm.Resume();pass=!throws&&vm.Done&&v.Type==DataType.Boolean&&v.Boolean;detail=vm.Done?"real Lua completed":"suspended";}
            catch(Exception ex){pass=throws;detail=TaskLuaRuntime.Error(ex);}
            Add(name,pass,detail);
        }
        public void Begin(){if(Running)return;Running=true;StartCoroutine(Run());}
        IEnumerator Wait()
        {float end=Time.realtimeSinceStartup+100;while(r.IsRunning&&Time.realtimeSinceStartup<end)yield return null;if(r.IsRunning)r.Cancel("验收超时");}
        IEnumerator Program(string name,string source,string expected="succeeded",string reason=null)
        {
            r.SubmitCodeForTest("验收注入源码："+name,source);yield return Wait();
            Add(name,r.Evidence.status==expected&&(reason==null||r.Evidence.reason.Contains(reason)),r.Evidence.status+": "+r.Evidence.reason);
        }
        IEnumerator Run()
        {
            Summary=new TaskAcceptanceSuite.SuiteReport{status="running"};r=FindObjectOfType<TaskMissionRunner>();r.ResolveRefs();
            if(r.IsRunning)r.Cancel("开始代码验收");start=r.Motor.transform.position;oldRoot=r.EvidenceRoot;r.EvidenceRoot=Root+"/injected_missions";
            if(r.Session.VoiceInput!=null){oldVoice=r.Session.VoiceInput.Enabled;r.Session.VoiceInput.SetEnabled(false);}
            r.PlannerOverride=new TestReport();
            Pure("functions_variables_numeric_for","local function sum(n) local x=0 for i=1,n do x=x+i end return x end return sum(10)==55");
            Pure("closures_and_tables","local function f() local n=0 return function() n=n+1 return n end end local f1=f();local a={f1(),f1()};return a[1]==1 and a[2]==2");
            Pure("while_repeat_break","local n=0;while true do n=n+1;if n==3 then break end end repeat n=n-1 until n==0 return n==0");
            Pure("if_elseif_else","local n=2;local v;if n==1 then v=1 elseif n==2 then v=20 else v=3 end return v==20");
            Pure("generic_for_math","local n=0;for _,v in ipairs({1,2,3}) do n=n+v end return math.abs(n-6)==0");
            Pure("no_filesystem","return io.open('test')",true);
            Pure("no_process","return os.execute('anything')",true);
            Pure("no_load_or_import","return require('anything')",true);
            Pure("no_clr","return luanet.import_type('System.IO.File')",true);
            Pure("syntax_error","local = true",true);
            Pure("infinite_loop_preemption","while true do end",true);
            Pure("recursive_failure_preemption","local function f(n) return f(n+1) end return f(0)",true);
            try
            {
                var vm=VM("local r=game.wait(0);if not r.ok then return r.reason=='fixture_failure' end return false");var first=vm.Resume();var response=new Table(vm.VM);response["ok"]=false;response["reason"]="fixture_failure";var result=vm.Resume(DynValue.NewTable(response));
                Add("yield_receipt_drives_branch",first.Type==DataType.Table&&result.Boolean&&vm.Done,"actual VM continuation, not a JSON interpreter");
            }catch(Exception ex){Add("yield_receipt_drives_branch",false,TaskLuaRuntime.Error(ex));}
            yield return Program("real_animation_loop_and_function","local function must(x) assert(x.ok,x.reason) end for i=1,2 do must(game.gesture('nod')) end return {ok=true}");
            Add("two_real_animation_receipts",r.Evidence.results.FindAll(x=>x.kind=="gesture"&&x.status=="succeeded").Count==2,"two completed imported clips");
            yield return Program("unknown_target_fails_without_substitution","game.navigate('missing_fixture_target');return {ok=true}","failed","unknown_target");
            yield return Program("unknown_action_no_procedural_fallback","game.gesture('missing_animation');return {ok=true}","failed","unknown_action");
            yield return Program("invalid_tool_denied","game.teleport({x=10});return {ok=true}","failed");
            int before=r.Evidence.results.Count;
            yield return Program("parallel_conflict_before_effect","game.parallel({{kind='navigate',target_id='place_1'},{kind='gesture',action_id='nod'}});return {ok=true}","failed","parallel_resource_conflict");
            Add("conflict_zero_effects",r.Evidence.results.Count==0,"batch rejected before tool_start");
            yield return Program("parallel_navigation_expression","local x=game.parallel({{kind='navigate',target_id='place_1'},{kind='express',emotion='happy',duration=1}});for _,r in ipairs(x) do assert(r.ok,r.reason) end return {ok=true}");
            var nav=r.Evidence.results.Find(x=>x.kind=="navigate");var exp=r.Evidence.results.Find(x=>x.kind=="express");
            Add("actual_parallel_overlap",nav!=null&&exp!=null&&nav.started<exp.ended&&exp.started<nav.ended,"body and expression overlap");
            yield return Program("return_home_live_state","assert(game.return_home('origin').ok);local live=game.state();return {ok=live.at_home}");
            // place_2 is far from home, so the first interaction must fail; Lua handles the real receipt.
            yield return Program("tool_failure_else_branch","local a=game.interact('place_2');if a.ok then return {ok=false,reason='fixture unexpectedly near'} else assert(game.state().at_home);return {ok=true,reason='handled real out of range'} end");
            Add("failed_tool_preserved_in_evidence",r.Evidence.results.Exists(x=>x.kind=="interact"&&x.status=="failed"),"failure receipt remains visible after handled branch");
            var device=r.Catalog.Get("place_2");int count=device!=null&&device.Interaction!=null?device.Interaction.CompletionCount:-1;
            yield return Program("interaction_idempotent_replay","assert(game.navigate('place_2').ok);local a=game.interact('place_2',{key='unlock_once'});assert(a.ok,a.reason);local b=game.interact('place_2',{key='unlock_once'});return {ok=b.ok and a.id==b.id}");
            Add("interaction_triggered_exactly_once",device!=null&&device.Interaction!=null&&device.Interaction.CompletionCount==count+1,"registered completion counter delta");
            yield return Program("key_reuse_different_args_rejected","assert(game.wait(0,{key='same'}).ok);game.wait(1,{key='same'});return {ok=true}","failed","idempotency_key_reused");
            int slices=r.CodeMaxSlices;r.CodeMaxSlices=2;
            yield return Program("runtime_loop_budget","while true do end","failed","instruction_budget_exceeded");r.CodeMaxSlices=slices;
            int calls=r.CodeMaxCalls;r.CodeMaxCalls=3;
            yield return Program("runtime_tool_budget","for i=1,8 do game.state() end return {ok=true}","failed","tool_call_budget_exceeded");r.CodeMaxCalls=calls;
            r.CodeMaxCalls=2;yield return Program("parallel_subcalls_share_budget","game.parallel({{kind='wait',duration=0.1},{kind='wait',duration=0.1},{kind='wait',duration=0.1}});return {ok=true}","failed","tool_call_budget_exceeded");
            Add("over_budget_batch_zero_effects",r.Evidence.results.Count==0,"subcalls charged before any start");r.CodeMaxCalls=calls;
            float timeout=r.CodeTimeout;r.CodeTimeout=1;
            yield return Program("wall_timeout_during_tool","game.wait(10);return {ok=true}","failed","code_wall_time_exceeded");r.CodeTimeout=timeout;
            r.SubmitCodeForTest("取消验收","game.wait(10);game.navigate('place_1');return {ok=true}");yield return null;r.Cancel("验收取消");var cancelled=r.Evidence;yield return new WaitForSecondsRealtime(0.2f);
            Add("cancel_no_late_side_effect",cancelled.status=="cancelled"&&cancelled.results.Count==1&&!r.Motor.HasTarget,"cancel disposes worker and program");
            r.SubmitCodeForTest("被替换的旧程序","game.wait(10);game.navigate('place_1');return {ok=true}");var old=r.Evidence;
            yield return Program("replacement_new_program","return {ok=game.state().position.x~=nil}");
            Add("replacement_old_generation_stopped",old.status=="cancelled"&&old.results.Count==1,"old source did not resume");
            yield return Program("program_explicit_failure","return {ok=false,reason='need clarification'}","failed","need clarification");
            yield return Program("missing_return_cannot_succeed","local x=1","failed","program_returned_failure");
            r.PlannerOverride=new Replies("local = true","return {ok=true}","语法修复验收汇报");r.Submit("验收：语法修复前无动作");yield return Wait();
            Add("syntax_repair_before_effect",r.Evidence.status=="succeeded"&&r.Evidence.llm_calls==3&&r.Evidence.results.Count==1,"one repair, final report only");
            r.PlannerOverride=new Replies("local id=game.resolve('place_1');return {ok=id.id}","return {ok=true}","类型修复验收汇报");r.Submit("验收：纯查询类型错误修复");yield return Wait();
            Add("type_repair_before_effect",r.Evidence.status=="succeeded"&&r.Evidence.llm_calls==3&&File.Exists(Path.Combine(r.RunDirectory,"mission_before_runtime_repair.lua")),"initial source and repaired source both retained");
            calls=r.CodeMaxCalls;r.CodeMaxCalls=1;
            r.PlannerOverride=new Replies("local s=game.state();local x=nil;return {ok=x.ok}","game.wait(0);return {ok=true}");r.Submit("验收：早期修复不增加工具预算");yield return Wait();
            Add("repair_cannot_grant_extra_call",r.Evidence.status=="failed"&&r.Evidence.reason.Contains("tool_call_budget_exceeded")&&r.Evidence.results.Count==0,"exhausted query budget remains exhausted after type repair");r.CodeMaxCalls=calls;
            r.PlannerOverride=new Replies("assert(game.wait(0).ok);error('after_effect')","return {ok=true}");r.Submit("验收：动作之后严禁重写重放");yield return Wait();
            Add("never_repair_after_effect",r.Evidence.status=="failed"&&r.Evidence.llm_calls==1&&r.Evidence.results.Count==1,"no second LLM request and no replay");
            r.PlannerOverride=null;r.EvidenceRoot=oldRoot;r.UseCodeExecution=true;
            if(r.Session.VoiceInput!=null)r.Session.VoiceInput.SetEnabled(oldVoice);
            Summary.status=Summary.total==Summary.passed?"passed":"failed";Running=false;Save();
        }
    }
}
