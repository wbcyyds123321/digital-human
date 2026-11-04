using UnityEngine;
using Yixuan.AI.Tasks;

namespace Yixuan.AI
{
    public partial class ChatSession
    {
        [Header("自然语言任务编排")]
        public bool TaskPlanningEnabled = true;
        public TaskMissionRunner Missions;
        void EnsureTaskRunner()
        {
            if (Missions == null) Missions = GetComponent<TaskMissionRunner>();
            if (Missions == null) Missions = gameObject.AddComponent<TaskMissionRunner>();
            Missions.Session = this;
            var panel = GetComponent<TaskMissionPanel>();
            if (panel == null) panel = gameObject.AddComponent<TaskMissionPanel>();
            panel.Runner = Missions;
        }
        bool TryRouteMission(string text)
        {
            if (_nextTurnIsProactive) return false;
            if(System.Text.RegularExpressions.Regex.IsMatch(text,@"^(别说了|不要说话|停止说话|安静|闭嘴)[。！!\s]*$")){SilenceSpeech();if(UI!=null)UI.SetReply("已停止发声，行动继续。");return true;}
            if(System.Text.RegularExpressions.Regex.IsMatch(text,@"^(可以说话了|恢复说话)[。！!\s]*$")){_speechMuted=false;if(Missions!=null&&Missions.Context!=null)Missions.Context.Muted=false;return true;}
            if (Missions != null && !Missions.IsRunning && !string.IsNullOrEmpty(Missions.AttentionTargetId) &&
                System.Text.RegularExpressions.Regex.IsMatch(text, @"^(停止注视|取消注视|不看了)[。！!\s]*$"))
            { Missions.StopAttention(); MissionFinished("已停止注视，视角回正。"); return true; }
            // 非任务行走（聊天里让她走过去）时，显式说“停下/站住”也要真的停下。
            if (Locomotion != null && Locomotion.IsMoving &&
                System.Text.RegularExpressions.Regex.IsMatch(text, @"^(停止|停下|站住|停|别走|别走了|不要走|不要走了)[。！!\s]*$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            { Locomotion.Stop("用户停止", "cancelled"); if(UI!=null)UI.SetReply("好，我停下了。"); return true; }
            if (Missions != null && Missions.IsRunning &&
                System.Text.RegularExpressions.Regex.IsMatch(text, @"^(停止|停下|先停下|停止任务|取消任务|取消|中止|别走了|stop|cancel)[。！!\s]*$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            { CancelDialogueTurn();Missions.Cancel("用户停止任务"); return true; }
            if(Missions!=null&&Missions.IsRunning&&System.Text.RegularExpressions.Regex.IsMatch(text,@"继续走.{0,8}(聊|说)|边走边聊|一边走一边聊")){StartNarrator(text,false);return true;}
            if(InputMode!=TaskInputMode.Dialogue&&Missions!=null&&Missions.IsRunning&&TaskMissionRunner.LooksLikeInsertion(text)){CancelDialogueTurn();Missions.InsertTask(text);return true;}
            if(InputMode!=TaskInputMode.Action)return false;
            if (!TaskPlanningEnabled || !MissionIntent.LooksLikeMission(text)) return false;
            EnsureTaskRunner(); _nextTurnIsProactive = false;
            Missions.Submit(text); return true;
        }
        public void MissionStarted(string text)
        {
            AddHistory("user", text);
            if (UI != null) { UI.SetStatus("任务规划中…"); UI.SetReply("正在根据场景目标规划任务。"); UI.SetInputEnabled(true); }
        }
        public void MissionProgress(string phase)
        { if (UI != null && Missions != null && Missions.IsRunning) UI.SetStatus("任务：" + phase); }
        public void MissionFinished(string report, bool announce = false)
        {
            LastReply = report ?? ""; AddHistory("assistant", LastReply);
            if (announce) AnnounceMissionCompletion();
            if (UI != null) { UI.SetReply(LastReply); UI.SetStatus("在线"); UI.SetInputEnabled(true); }
        }
    }
}
