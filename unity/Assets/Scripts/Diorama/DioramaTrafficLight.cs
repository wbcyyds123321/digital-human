using UnityEngine;

namespace Yixuan.Diorama
{
    /// <summary>远处交通信号灯：红/黄/绿循环 + 轻微灯光变化。</summary>
    public class DioramaTrafficLight : MonoBehaviour
    {
        public Light RedLight;
        public Light YellowLight;
        public Light GreenLight;
        public float Period = 12f;

        void Update()
        {
            float t = Mathf.Repeat(Time.time, Period);
            bool red = t < 5f;
            bool green = t >= 5f && t < 10f;
            bool yellow = t >= 10f;

            if (RedLight != null) RedLight.intensity = red ? 3.2f : 0.15f;
            if (YellowLight != null) YellowLight.intensity = yellow ? 3.0f : 0.1f;
            if (GreenLight != null) GreenLight.intensity = green ? 3.0f : 0.1f;
        }
    }
}
