using UnityEngine;

namespace Yixuan.Diorama
{
    /// <summary>微缩景观轨道相机：左键拖拽旋转、滚轮缩放，无 UI。</summary>
    public class DioramaOrbitCamera : MonoBehaviour
    {
        public Vector3 Target = new Vector3(0f, 1.2f, 0f);
        public float Distance = 18f;
        public float MinDistance = 7f;
        public float MaxDistance = 30f;
        public float Yaw = 35f;
        public float Pitch = 24f;
        public float RotateSpeed = 3.0f;
        public float ZoomSpeed = 1.2f;
        public float Smooth = 10f;

        float _yaw, _pitch, _distance;

        void Start()
        {
            _yaw = Yaw;
            _pitch = Pitch;
            _distance = Distance;
            Apply(true);
        }

        void LateUpdate()
        {
            if (Input.GetMouseButton(0))
            {
                _yaw += Input.GetAxis("Mouse X") * RotateSpeed;
                _pitch -= Input.GetAxis("Mouse Y") * RotateSpeed;
                _pitch = Mathf.Clamp(_pitch, -12f, 80f);
            }

            float scroll = Input.mouseScrollDelta.y;
            if (Mathf.Abs(scroll) > 0.01f)
                _distance = Mathf.Clamp(_distance - scroll * ZoomSpeed, MinDistance, MaxDistance);

            _yaw = Mathf.Lerp(_yaw, _yaw, 1f);
            Yaw = _yaw;
            Pitch = Mathf.Lerp(Pitch, _pitch, Time.deltaTime * Smooth);
            Distance = Mathf.Lerp(Distance, _distance, Time.deltaTime * Smooth);
            Apply(false);
        }

        void Apply(bool instant)
        {
            Quaternion rot = Quaternion.Euler(Pitch, Yaw, 0f);
            Vector3 pos = Target + rot * new Vector3(0f, 0f, -Distance);
            transform.position = instant ? pos : Vector3.Lerp(transform.position, pos, Time.deltaTime * Smooth);
            transform.LookAt(Target);
        }
    }
}
