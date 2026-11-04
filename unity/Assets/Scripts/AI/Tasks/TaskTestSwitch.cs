using UnityEngine;

namespace Yixuan.AI.Tasks
{
    /// <summary>Acceptance fixture: the registered script changes the device lamp and records an observable state.</summary>
    public class TaskTestSwitch : TaskInteractable
    {
        public bool Activated { get; private set; }
        void Awake() { OnTriggered.AddListener(Activate); }
        void Activate()
        {
            Activated = true;
            var r = GetComponent<Renderer>();
            if (r != null) r.material.color = Color.yellow;
        }
        public void ResetFixture()
        {
            Activated = false;
            var r = GetComponent<Renderer>();
            if (r != null) r.material.color = new Color(0.08f, 0.35f, 0.95f);
        }
    }
}
