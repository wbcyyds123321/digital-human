using UnityEngine;

namespace Yixuan.Diorama
{
    /// <summary>地面积水涟漪：循环缩放/淡出圆环。</summary>
    public class DioramaRipples : MonoBehaviour
    {
        public LineRenderer[] Rings;
        public float Speed = 0.55f;
        public float MinScale = 0.25f;
        public float MaxScale = 1.6f;
        public Color RingColor = new Color(0.7f, 0.9f, 1f, 0.5f);

        float[] _offset;
        MaterialPropertyBlock _mpb;

        void Start()
        {
            _mpb = new MaterialPropertyBlock();
            _offset = new float[Rings != null ? Rings.Length : 0];
            for (int i = 0; i < _offset.Length; i++) _offset[i] = Random.value;
        }

        void Update()
        {
            if (Rings == null) return;
            for (int i = 0; i < Rings.Length; i++)
            {
                var r = Rings[i];
                if (r == null) continue;
                float p = Mathf.Repeat(Time.time * Speed + _offset[i], 1f);
                float s = Mathf.Lerp(MinScale, MaxScale, p);
                r.transform.localScale = new Vector3(s, 1f, s);
                Color c = RingColor;
                c.a *= (1f - p);
                r.GetPropertyBlock(_mpb);
                _mpb.SetColor("_Color", c);
                r.SetPropertyBlock(_mpb);
            }
        }
    }
}
