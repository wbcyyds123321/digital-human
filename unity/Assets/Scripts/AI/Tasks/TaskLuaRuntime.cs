using System;
using System.Collections.Generic;
using MoonSharp.Interpreter;

namespace Yixuan.AI.Tasks
{
    // Real Lua VM. No CLR objects, filesystem, network, dynamic loading or nested coroutines are exposed.
    // Instruction limits bound Lua execution, not native allocations: this is not an OS/memory sandbox.
    public sealed class TaskLuaRuntime
    {
        public readonly Script VM;
        public readonly Coroutine Thread;
        public int Slices { get; private set; }
        public int Calls { get; private set; }
        public int MaxSlices = 2000, MaxCalls = 96;
        public const int InstructionsPerSlice = 1000;
        public TaskLuaRuntime(string source, Func<Script, Table> state, Func<string, string> resolve)
        {
            if (string.IsNullOrWhiteSpace(source) || source.Length > 24000) throw new Exception("source_size_limit");
            VM = new Script(CoreModules.Basic | CoreModules.ErrorHandling | CoreModules.TableIterators | CoreModules.Math | CoreModules.String | CoreModules.Table);
            VM.Globals["print"] = DynValue.Nil;
            VM.Globals["collectgarbage"] = DynValue.Nil;
            var game = new Table(VM);
            VM.Globals["game"] = game;
            game["state"] = DynValue.NewCallback((c,a) => { CountCall(); return DynValue.NewTable(state(VM)); });
            game["resolve"] = DynValue.NewCallback((c,a) => { CountCall(); return DynValue.NewString(resolve(Text(a[0]))); });
            foreach (var spec in TaskToolRegistry.All)
            {
                string name = spec.Name;
                game[name] = DynValue.NewCallback((c,a) =>
                {
                    CountCall();
                    var request = new Table(VM); request["method"] = name;
                    request["arg"] = a.Count > 0 ? a[0] : DynValue.Nil;
                    request["options"] = a.Count > 1 ? a[1] : DynValue.Nil;
                    return DynValue.NewYieldReq(new[] { DynValue.NewTable(request) });
                });
            }
            var fn = VM.LoadString(source, null, "mission.lua");
            Thread = VM.CreateCoroutine(fn).Coroutine;
            Thread.AutoYieldCounter = InstructionsPerSlice;
        }
        public void ChargeAdditionalCalls(int count) { for(int i=0;i<count;i++)CountCall(); }
        void CountCall() { if (++Calls > MaxCalls) throw new ScriptRuntimeException("tool_call_budget_exceeded"); }
        public DynValue Resume(DynValue receipt = null)
        {
            if (++Slices > MaxSlices) throw new Exception("instruction_budget_exceeded");
            return receipt == null ? Thread.Resume() : Thread.Resume(receipt);
        }
        public bool Done { get { return Thread.State == CoroutineState.Dead; } }
        public static string Text(DynValue v)
        { if (v.Type != DataType.String || v.String.Length > 3000) throw new ScriptRuntimeException("expected_bounded_string"); return v.String; }
        public static string Error(Exception ex)
        { var lua = ex as InterpreterException; return lua != null ? (lua.DecoratedMessage ?? lua.Message ?? lua.GetType().Name) : (ex.Message ?? ex.GetType().Name); }
    }
}
