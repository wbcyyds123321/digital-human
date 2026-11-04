using UnityEngine;

namespace Yixuan.AI.Tasks
{
    /// <summary>Optional explicit identity/aliases; unmarked scene objects can also be addressed by name/path.</summary>
    public class TaskDestinationMarker : MonoBehaviour
    {
        public string Id;
        public string DisplayName;
        public string Aliases;
        public Transform ApproachPose;
        [Tooltip("可选的视线落点，例如灯头或按钮；不影响导航站立点")]
        public Transform LookAtPose;
    }
}
