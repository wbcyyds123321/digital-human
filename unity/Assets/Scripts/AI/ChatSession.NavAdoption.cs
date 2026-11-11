using System;
using System.Collections.Generic;
using UnityEngine;
using Yixuan.AI.Tasks;

namespace Yixuan.AI
{
    public partial class ChatSession
    {
        string _turnInput;
        bool _brainProactiveBeforeTurn, _brainProactiveKnown;

        public ILLMClient NarratorOverride;
        public Func<List<ChatMessage>, DeepSeekClient.StreamHandle> StreamOverride;

        public bool ChatTurnActive { get { return _state != State.Idle; } }
        public string CurrentTurnInput { get { return _turnInput; } }
        public string CurrentActionFacts { get { return ActionFacts(); } }
        public bool BrainProactiveKnown { get { return _brainProactiveKnown; } }
        public bool BrainProactiveCaptured { get { return _brainProactiveBeforeTurn; } }
        public bool BrainProactivePreference
        {
            get
            {
                if (!_brainProactiveKnown) return Brain != null && Brain.EnableProactive;
                return _brainProactiveBeforeTurn;
            }
        }

        public void CaptureBrainProactivePreference()
        {
            if (Brain == null) { _brainProactiveKnown = false; return; }
            if (Brain.EnableProactive) { _brainProactiveBeforeTurn = true; _brainProactiveKnown = true; return; }
            if (Director != null && Director.BrainResumePending)
            {
                if (!_brainProactiveKnown) { _brainProactiveBeforeTurn = true; _brainProactiveKnown = true; }
                return;
            }
            if (Missions != null && (Missions.IsRunning || Missions.IsSuspended))
            {
                if (!_brainProactiveKnown) { _brainProactiveBeforeTurn = true; _brainProactiveKnown = true; }
                return;
            }
            _brainProactiveBeforeTurn = false;
            _brainProactiveKnown = true;
        }

        public void ResumeBrainIfUserWants()
        {
            if (Director == null) return;
            if (_brainProactiveKnown && !_brainProactiveBeforeTurn) return;
            Director.SuspendBrain(false);
        }

        public bool TryAdoptChatNavigation(string anchorId)
        {
            if (!TaskPlanningEnabled) return false;
            if (InputMode != TaskInputMode.Auto) return false;
            if (_syntheticVisualTurn) return false;
            if (!ChatTurnActive) return false;
            if (Locomotion == null) return false;
            if (!Locomotion.IsMoving || string.IsNullOrEmpty(Locomotion.TargetAnchor)) return false;
            if (!string.IsNullOrEmpty(anchorId) && Locomotion.TargetAnchor != anchorId) return false;
            EnsureTaskRunner();
            if (Missions == null || Missions.IsRunning || Missions.IsSuspended || Missions.IsInsertion) return false;
            string request = string.IsNullOrWhiteSpace(_turnInput) ? null : _turnInput;
            bool adopted = Missions.AdoptNavigation(Locomotion.TargetAnchor, request);
            if (adopted)
            {
                _speechMuted = false;
                if (Missions.Context != null) Missions.Context.Muted = false;
                Debug.Log("[Chat] 聊天导航已承接为可暂停任务：anchor=" + Locomotion.TargetAnchor +
                          " mission=" + (Missions.Context != null ? Missions.Context.MissionId : "?") +
                          " evidence=" + Missions.RunDirectory);
            }
            else
                Debug.LogWarning("[Chat] 聊天导航未能承接（保持原导航，不建立可恢复任务）：anchor=" + Locomotion.TargetAnchor);
            return adopted;
        }

        public void NavigationAdoptedFinished(TaskMissionRunner runner, bool brainWasProactive)
        {
            if (runner == null || runner.Evidence == null) return;
            Debug.Log("[Chat] 聊天导航结束回调：status=" + runner.Evidence.status + " preference=" + brainWasProactive +
                      " running=" + (Missions != null && Missions.IsRunning) + " state=" + _state +
                      " turn=" + (_turn != null) + " t=" + Time.realtimeSinceStartup.ToString("F2"));
            Debug.Log("[Chat] 聊天导航任务结束：anchor=" + runner.AdoptedAnchor +
                      " status=" + runner.Evidence.status +
                      (string.IsNullOrEmpty(runner.Evidence.reason) ? "" : " reason=" + runner.Evidence.reason) +
                      " t=" + Time.realtimeSinceStartup.ToString("F2") +
                      " evidence=" + runner.RunDirectory);
            if (UI != null && _state == State.Idle)
            {
                UI.SetStatus("在线");
                UI.SetInputEnabled(true);
            }
            if (!brainWasProactive) return;
            if (Missions != null && Missions.IsRunning) return;
            if (_state != State.Idle) return;
            Debug.Log("[Chat] 导航结束恢复主动行为：preference=" + brainWasProactive + " known=" + _brainProactiveKnown +
                      " captured=" + _brainProactiveBeforeTurn + " running=" + (Missions != null && Missions.IsRunning) +
                      " state=" + _state + " brainEnabled=" + (Brain != null && Brain.EnableProactive));
            ResumeBrainIfUserWants();
        }
    }
}
