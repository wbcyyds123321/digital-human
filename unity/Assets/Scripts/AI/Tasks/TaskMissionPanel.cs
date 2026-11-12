using System.Text;
using UnityEngine;
using Yixuan.Vision;
namespace Yixuan.AI.Tasks
{
    public class TaskMissionPanel : MonoBehaviour
    {
        public TaskMissionRunner Runner;
        public bool Visible = true;
        [Tooltip("嵌入统一控制台时为 true：不画打开按钮/收起、不抢 ChatUI 布局")]
        public bool Embedded = false;
        public Rect EmbeddedRect = new Rect(0, 0, 0, 0);
        public string Request = "先走回原点，然后做一个挥舞双手的动作，然后再走到红绿灯之下。";
        Vector2 _scroll;
        bool _showTargets;
        string _targets = "目标按真实物体汇总；中文名来自对象/材质标签，不是固定路线。";
        GUIStyle _wrap;
        VisionControlUI _visionUi;
        VisionObserver _observer;
        float InputTop { get { return Runner!=null&&Runner.Session!=null&&Runner.Session.UI!=null?Runner.Session.UI.TaskInputTopScreenY:Screen.height-84; } }
        public Rect PanelRect { get { if (Embedded && EmbeddedRect.width > 40f) return EmbeddedRect; return new Rect(12,12,Mathf.Min(455,Mathf.Max(250,Screen.width*0.44f)),Mathf.Max(120,Mathf.Min(InputTop-28,680))); } }
        void SyncLayout(bool active)
        {
            if (_visionUi == null) _visionUi=FindObjectOfType<VisionControlUI>();
            if (_observer == null) _observer=FindObjectOfType<VisionObserver>();
            if (_visionUi != null) _visionUi.SetTaskPanelLayout(active);
            if (Runner != null && Runner.Session != null && Runner.Session.UI != null) Runner.Session.UI.SetTaskPanelLayout(active);
        }
        void LateUpdate() { if (!Embedded) SyncLayout(Visible && enabled); }
        void OnDisable() { if (!Embedded) SyncLayout(false); }
        void OnGUI()
        {
            if (Runner == null) return;
            if (!Visible) { if(!Embedded && GUI.Button(new Rect(12,Mathf.Max(12,InputTop-42),112,28),"打开任务面板")) Visible=true; return; }
            if (_wrap == null) _wrap = new GUIStyle(GUI.skin.label) { wordWrap = true };
            GUILayout.BeginArea(PanelRect, GUI.skin.box);
            GUILayout.BeginHorizontal(); GUILayout.Label("LLM 行为程序 · Unity 技能",_wrap);
            if(!Embedded && GUILayout.Button("收起",GUILayout.Width(48),GUILayout.Height(24))) {Visible=false; SyncLayout(false);}
            GUILayout.EndHorizontal();
            // 视觉/语音/第一人称开关已收进统一控制台（本面板只保留任务执行相关内容）。
            _scroll=GUILayout.BeginScrollView(_scroll);
            GUILayout.Label("状态：" + Runner.Phase + "  | 可插入动作后继续原任务", _wrap);
            if(!string.IsNullOrEmpty(Runner.PhaseSummaryText)) GUILayout.Label("阶段事实：" + Runner.PhaseSummaryText, _wrap);
            if(Runner.IsSuspended)GUILayout.Label("原任务进度已保留；插入："+(Runner.ActiveInsertion!=null?Runner.ActiveInsertion.Phase:"准备中")+"；排队 "+Runner.PendingInsertions,_wrap);
            if(!string.IsNullOrEmpty(Runner.LastInsertionReport))GUILayout.Label(Runner.LastInsertionReport,_wrap);
            if(!Runner.IsRunning) Runner.UseCodeExecution=GUILayout.Toggle(Runner.UseCodeExecution,"代码模式：LLM 生成 Lua（关闭则使用旧 JSON 计划）");
            if(Runner.Session!=null){GUILayout.Label("聊天输入路由：自动识别 / 强制行动 / 只对话",_wrap);Runner.Session.InputMode=(Yixuan.AI.TaskInputMode)GUILayout.Toolbar((int)Runner.Session.InputMode,new[]{"自动","行动","对话"});}
            Request=GUILayout.TextArea(Request,GUILayout.Height(58));
            GUILayout.BeginHorizontal();
            if(GUILayout.Button("规划并执行",GUILayout.Height(28))) {_showTargets=false;Runner.Submit(Request);}
            if(GUILayout.Button("停止",GUILayout.Height(28))) Runner.Cancel();
            GUILayout.EndHorizontal();
            if(Runner.Contract!=null){GUILayout.Label("独立契约："+Runner.Contract.obligations.Length+"项义务；禁移动="+Runner.Contract.forbid_move+"；禁语音="+Runner.Contract.forbid_speech,_wrap);}
            if(Runner.Evidence!=null&&Runner.Evidence.completion!=null)GUILayout.Label("程序成功="+Runner.Evidence.completion.program_succeeded+" / 任务成功="+Runner.Evidence.completion.task_succeeded,_wrap);
            Runner.VoiceEnabled=GUILayout.Toggle(Runner.VoiceEnabled,"启用任务语音（要求说话但关闭时会明确报错）");
            GUILayout.BeginHorizontal();
            if(GUILayout.Button("场景物体 / 中文名称"))
            {
                Runner.ResolveRefs();Runner.Catalog.Refresh(Runner.Motor!=null?Runner.Motor.transform:null);
                var b=new StringBuilder();
                foreach(var t in Runner.Catalog.SemanticTargets()) b.AppendLine(t.Name+" | "+(t.Object!=null?t.Object.name:t.Id)+ (t.Interaction!=null?" [可交互]":""));
                _targets=b.ToString();_showTargets=true;
            }
            if(GUILayout.Button("回原点")) Request="回到起始位置";
            GUILayout.EndHorizontal();
            if(Runner.HomeCaptured) GUILayout.Label("场景原点："+Runner.HomePosition.ToString("F2")+"；本次任务起点另行记录。",_wrap);
            if(_showTargets) {GUILayout.Label(_targets,_wrap);if(GUILayout.Button("返回任务结果"))_showTargets=false;}
            else if(Runner.Evidence!=null)
            {
                GUILayout.Label("原始任务："+Runner.Evidence.request,_wrap);
                GUILayout.Label("执行器："+Runner.Evidence.execution_backend+" · 核心 LLM 调用："+Runner.Evidence.llm_calls,_wrap);
                if(Runner.Evidence.intent!=null&&!string.IsNullOrEmpty(Runner.Evidence.intent.interpretation)) GUILayout.Label("理解："+Runner.Evidence.intent.interpretation,_wrap);
                if(!string.IsNullOrEmpty(Runner.LastProgram)) { GUILayout.Label("本轮生成的 Lua 代码（按真实回执执行）",_wrap); GUILayout.TextArea(Runner.LastProgram); }
                foreach(var r in Runner.Evidence.results) GUILayout.Label(r.id+" "+r.kind+" → "+(r.kind=="gesture"?r.action_id:r.target_id)+"\n"+r.status+" "+r.reason,_wrap);
                if(!string.IsNullOrEmpty(Runner.LastReport)) GUILayout.Label(Runner.LastReport,_wrap);
            }
            else GUILayout.Label(_targets,_wrap);
            GUILayout.Label("LLM → Lua 程序 / 旧计划 → Unity 技能 → 真实回执。",_wrap);
            GUILayout.EndScrollView();GUILayout.EndArea();
        }
    }
}
