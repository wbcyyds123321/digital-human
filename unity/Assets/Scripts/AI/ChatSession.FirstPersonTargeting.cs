using UnityEngine;
using Yixuan.Pick;

namespace Yixuan.AI
{
    /// <summary>
    /// B production entry: first-person visual target commands are handled by the
    /// generic targeting service before the normal chat/task routes. The service
    /// captures the CURRENT real EyeCamera frame, locates generically, aims the real
    /// gimbal, and requires a new-frame centre confirmation of the same entity before
    /// publishing the shared selection record. No sentence/entity test shortcuts.
    /// </summary>
    public partial class ChatSession
    {
        /// <summary>Present a locally handled first-person targeting reply through the same chat surface.</summary>
        internal void PublishFirstPersonReply(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            LastReply = text;
            AddHistory("assistant", text);
            RecordAssistantHistory(text, false);
            if (UI != null) { UI.SetReply(text); UI.SetStatus(text.Length > 60 ? text.Substring(0, 60) + "…" : text); }
            Debug.Log("[FirstPersonTargeting] " + text);
            // Confirmed visual results use the same turn-owned speech path as
            // semantic dialogue. A new input invalidates the queued/playing reply.
            int generation = _narratorGeneration;
            int selectionGeneration = SelectionBroker.Generation;
            if (!_speechMuted && Config != null && Config.VoiceEnabled)
                QueueTaskSpeech("dialogue-" + generation, text,
                    () => generation == _narratorGeneration && selectionGeneration == SelectionBroker.Generation && !_speechMuted, 10f);
        }

        /// <summary>Hand the confirmed visual target action to the existing contract/Lua/runner chain.</summary>
        internal bool HandoffVisualActionToRunner(string request, string confirmedEntityId, int sharedToken, string action)
        {
            if (string.IsNullOrEmpty(request) || string.IsNullOrEmpty(confirmedEntityId)) return false;
            EnsureTaskRunner();
            return Missions != null && Missions.SubmitSelectionAction(request, confirmedEntityId, sharedToken, action);
        }

        internal void CancelFirstPersonTargeting()
        {
            FirstPersonTargetingService.CancelRun("chat_cancel_or_destroy");
        }

        /// <summary>True when the text was consumed by the B visual targeting lane.</summary>
        public bool TryHandleFirstPersonTargetInput(string text)
        {
            return FirstPersonTargetingService.TryHandleChat(text, this);
        }
    }
}
