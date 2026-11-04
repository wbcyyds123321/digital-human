using System.Collections.Generic;
using UnityEngine;

namespace Yixuan.AI
{
    /// <summary>自定义云台动作：会出现在 prompt 清单里，LLM 可以直接选。</summary>
    [System.Serializable]
    public class CustomCameraMove
    {
        public string Id = "left_45";
        public string Label = "往左看45度";
        public float Yaw = -45f;
        public float Pitch = 0f;
        [Tooltip("留空 = 用固定角度；user = 跟踪用户；store = 跟踪便利店")]
        public string Track = "";
    }

    /// <summary>
    /// 云台角度参数（Inspector 可调）——挂在 VisionSystem 上。
    ///
    /// 之前默认角度（±60 / ±28）写死在代码里；现在集中到这里：
    ///   - 默认角度、角度上限、最小角度、整体增益都在 Inspector 调；
    ///   - 想加"往左看45度/向右90度"这种预设，直接往 CustomMoves 里加一条，
    ///     prompt 清单、校验、执行会自动跟上（不用改代码/prompt 文本）。
    /// </summary>
    [DisallowMultipleComponent]
    public class CameraGimbalSettings : MonoBehaviour
    {
        [Header("默认角度（度）")]
        [Tooltip("“往左看”默认转多少度（绝对值）")]
        public float DefaultLeftYaw = 60f;
        [Tooltip("“往右看”默认转多少度（绝对值）")]
        public float DefaultRightYaw = 60f;
        [Tooltip("“抬头”默认仰多少度（绝对值）")]
        public float DefaultUpPitch = 28f;
        [Tooltip("“低头”默认低多少度（绝对值）")]
        public float DefaultDownPitch = 28f;
        [Tooltip("“回头看”的偏航角度（绝对值）")]
        public float BehindYaw = 180f;

        [Header("角度范围 / 整体增益")]
        public float MaxYaw = 180f;
        public float MaxPitch = 60f;
        [Tooltip("允许的最小角度；小于它按它算")]
        public float MinAngle = 1f;
        [Tooltip("所有角度（默认角度和 camera_angle）统一乘这个系数：1=不变，1.2=都转大 20%")]
        public float AngleScale = 1f;

        [Header("自定义云台动作（会出现在 prompt 清单里）")]
        public List<CustomCameraMove> CustomMoves = new List<CustomCameraMove>();

        void Awake()
        {
            CameraMoveCatalog.Bind(this);
        }

        void OnDestroy()
        {
            CameraMoveCatalog.Unbind(this);
        }

        // 只在编辑器里改数值时，让 prompt/解析立刻用新值（不用重进 Play）
        void OnValidate()
        {
            if (Application.isPlaying && isActiveAndEnabled)
                CameraMoveCatalog.Bind(this);
        }
    }
}
