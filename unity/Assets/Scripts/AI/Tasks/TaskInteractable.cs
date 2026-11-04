using System;
using System.Collections;
using UnityEngine;
using UnityEngine.Events;

namespace Yixuan.AI.Tasks
{
    /// <summary>Registered interaction, with a real completion receipt. Replace/derive for asynchronous game scripts.</summary>
    public class TaskInteractable : MonoBehaviour
    {
        public string InteractionName = "触发机关";
        public float MaxDistance = 2f;
        public float FacingTolerance = 20f;
        public UnityEvent OnTriggered = new UnityEvent();
        public int CompletionCount { get; private set; }
        public string LastReceipt { get; private set; }
        public virtual IEnumerator Execute(Transform actor, Action<bool, string> done)
        {
            if (actor == null) { done(false, "actor_missing"); yield break; }
            Vector3 d = transform.position - actor.position; d.y = 0;
            if (d.magnitude > MaxDistance) { done(false, "interaction_out_of_range"); yield break; }
            float yaw = d.sqrMagnitude > 0.0001f ? Quaternion.LookRotation(d).eulerAngles.y : actor.eulerAngles.y;
            if (Mathf.Abs(Mathf.DeltaAngle(actor.eulerAngles.y, yaw)) > FacingTolerance)
            { done(false, "interaction_not_facing_target"); yield break; }
            string error = null;
            try { OnTriggered.Invoke(); } catch (Exception e) { error = e.Message; }
            if (error != null) { done(false, "interaction_script_error: " + error); yield break; }
            CompletionCount++;
            LastReceipt = gameObject.name + ":completed:" + CompletionCount;
            done(true, LastReceipt);
        }
    }
}
