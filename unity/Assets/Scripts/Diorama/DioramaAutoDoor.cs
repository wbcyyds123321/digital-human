using UnityEngine;

namespace Yixuan.Diorama
{
    /// <summary>便利店自动门：周期性左右滑动，带一点等待与缓动。</summary>
    public class DioramaAutoDoor : MonoBehaviour
    {
        public Transform LeftPanel;
        public Transform RightPanel;
        public float OpenDistance = 0.85f;
        public float Period = 10f;
        public float OpenDuration = 1.6f;

        Vector3 _leftClosed, _rightClosed;

        void Start()
        {
            if (LeftPanel != null) _leftClosed = LeftPanel.localPosition;
            if (RightPanel != null) _rightClosed = RightPanel.localPosition;
        }

        void Update()
        {
            float t = Mathf.Repeat(Time.time, Period);
            float k;
            if (t < OpenDuration) k = Mathf.SmoothStep(0f, 1f, t / OpenDuration);
            else if (t < Period - OpenDuration) k = 1f;
            else k = Mathf.SmoothStep(1f, 0f, (t - (Period - OpenDuration)) / OpenDuration);

            if (LeftPanel != null) LeftPanel.localPosition = _leftClosed + Vector3.left * OpenDistance * k;
            if (RightPanel != null) RightPanel.localPosition = _rightClosed + Vector3.right * OpenDistance * k;
        }
    }
}
