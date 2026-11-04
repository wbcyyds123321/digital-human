using UnityEngine;

namespace Yixuan.Diorama
{
    /// <summary>招牌/灯箱轻微闪烁：灯光强度 + 材质自发光强度。</summary>
    public class DioramaFlicker : MonoBehaviour
    {
        public Light Lamp;
        public Renderer[] Targets;
        public float BaseIntensity = 1f;
        public float FlickerChance = 0.15f;

        MaterialPropertyBlock _mpb;
        float _next;
        bool _dim;

        void Start()
        {
            _mpb = new MaterialPropertyBlock();
            _next = Time.time + Random.Range(1.5f, 4f);
            Apply(BaseIntensity, 1f);
        }

        void Update()
        {
            if (Time.time >= _next)
            {
                _dim = !_dim;
                _next = Time.time + (_dim ? Random.Range(0.04f, 0.12f) : Random.Range(4f, 9f));
                if (Random.value > FlickerChance) _next = Time.time + Random.Range(3f, 7f);
                Apply(BaseIntensity * (_dim ? 0.4f : 1f), _dim ? 0.25f : 1f);
            }
        }

        void Apply(float lightIntensity, float emission)
        {
            if (Lamp != null) Lamp.intensity = lightIntensity;
            if (Targets == null || _mpb == null) return;
            foreach (var r in Targets)
            {
                if (r == null) continue;
                r.GetPropertyBlock(_mpb);
                _mpb.SetFloat("_EmissionStrength", emission);
                r.SetPropertyBlock(_mpb);
            }
        }
    }
}
