using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace Yixuan.UI
{
    /// <summary>One map icon: viewport-local position (pivot bottom-left).</summary>
    public struct MapMarker
    {
        public Vector2 Local;
        public Color Color;
        public float Size;
        public bool Selected;
        public int Kind; // 0 entity, 1 anchor
    }

    /// <summary>
    /// Single-mesh marker/route overlay for the big map. One Graphic draws all
    /// entity icons, routes, the character arrow, the gaze fan and the debug
    /// bounds layer, so 800+ entities never need individual GameObjects.
    /// </summary>
    public sealed class BigMapMarkerGraphic : MaskableGraphic
    {
        public List<MapMarker> Markers = new List<MapMarker>();

        struct Primitive
        {
            public int Type; // 0 line, 1 ring, 2 diamond, 3 arrow, 4 fan, 5 rect, 6 cross, 7 polyline
            public Vector2 A;
            public Vector2 B;
            public Vector2 C;
            public float Radius;
            public float Thickness;
            public Color Color;
            public List<Vector2> Points;
        }

        readonly List<Primitive> _prims = new List<Primitive>();
        readonly List<Vector2> _pointsBuffer = new List<Vector2>(64);

        public void ClearDynamic() { _prims.Clear(); }

        public void RebuildMesh() { SetVerticesDirty(); }

        // ---- primitive builders -------------------------------------------
        public void AddLine(Vector2 a, Vector2 b, float thickness, Color color)
        {
            _prims.Add(new Primitive { Type = 0, A = a, B = b, Thickness = thickness, Color = color });
        }

        public void AddDashedLine(Vector2 a, Vector2 b, float thickness, Color color, float dash, float gap)
        {
            float length = Vector2.Distance(a, b);
            if (length < 0.01f) return;
            Vector2 dir = (b - a) / length;
            float travelled = 0f;
            while (travelled < length)
            {
                float end = Mathf.Min(travelled + dash, length);
                AddLine(a + dir * travelled, a + dir * end, thickness, color);
                travelled = end + gap;
            }
        }

        public void AddRing(Vector2 center, float radius, Color color, float thickness)
        {
            _prims.Add(new Primitive
            {
                Type = 1, A = center, Radius = radius, Thickness = thickness, Color = color,
            });
        }

        public void AddDiamond(Vector2 center, float size, Color color)
        {
            _prims.Add(new Primitive { Type = 2, A = center, Radius = size, Color = color });
        }

        public void AddArrow(Vector2 center, float yawDeg, float size, Color color, bool filled)
        {
            _prims.Add(new Primitive
            {
                Type = 3, A = center, Radius = size, Color = color, Thickness = yawDeg,
            });
        }

        public void AddFan(Vector2 center, float yawDeg, float radius, float spreadDeg, Color color)
        {
            _prims.Add(new Primitive
            {
                Type = 4, A = center, Radius = radius, Thickness = yawDeg,
                B = new Vector2(spreadDeg, 0f), Color = color,
            });
        }

        public void AddRect(Vector2 a, Vector2 b, Color color, float thickness)
        {
            Vector2 min = new Vector2(Mathf.Min(a.x, b.x), Mathf.Min(a.y, b.y));
            Vector2 max = new Vector2(Mathf.Max(a.x, b.x), Mathf.Max(a.y, b.y));
            _prims.Add(new Primitive { Type = 5, A = min, B = max, Thickness = thickness, Color = color });
        }

        public void AddCross(Vector2 center, float size, Color color)
        {
            _prims.Add(new Primitive { Type = 6, A = center, Radius = size, Color = color });
        }

        public void AddPolyline(List<Vector2> localPoints, Color color, float thickness)
        {
            if (localPoints == null || localPoints.Count < 2) return;
            var pts = new List<Vector2>(localPoints);
            _prims.Add(new Primitive
            {
                Type = 7, Points = pts, Color = color, Thickness = thickness,
            });
        }

        // ---- mesh ----------------------------------------------------------
        protected override void OnPopulateMesh(VertexHelper vh)
        {
            vh.Clear();
            if (Markers != null)
            {
                for (int i = 0; i < Markers.Count; i++)
                {
                    var marker = Markers[i];
                    float half = marker.Size * 0.5f;
                    var a = new Vector2(marker.Local.x - half, marker.Local.y - half);
                    var b = new Vector2(marker.Local.x + half, marker.Local.y - half);
                    var c = new Vector2(marker.Local.x + half, marker.Local.y + half);
                    var d = new Vector2(marker.Local.x - half, marker.Local.y + half);
                    AddQuad(vh, a, b, c, d, marker.Color);
                    if (marker.Selected)
                        AddRingInternal(vh, marker.Local, marker.Size + 4f, Color.white, 2f);
                }
            }
            for (int i = 0; i < _prims.Count; i++) DrawPrimitive(vh, _prims[i]);
        }

        void DrawPrimitive(VertexHelper vh, Primitive p)
        {
            switch (p.Type)
            {
                case 0:
                    AddLineInternal(vh, p.A, p.B, p.Thickness, p.Color);
                    break;
                case 1:
                    AddRingInternal(vh, p.A, p.Radius, p.Color, p.Thickness);
                    break;
                case 2:
                {
                    float r = p.Radius;
                    AddQuad(vh, p.A + new Vector2(0f, r), p.A + new Vector2(r, 0f),
                            p.A + new Vector2(0f, -r), p.A + new Vector2(-r, 0f), p.Color);
                    break;
                }
                case 3:
                {
                    float yaw = p.Thickness * Mathf.Deg2Rad;
                    Vector2 dir = new Vector2(Mathf.Sin(yaw), Mathf.Cos(yaw));
                    Vector2 side = new Vector2(dir.y, -dir.x);
                    Vector2 tip = p.A + dir * p.Radius;
                    Vector2 left = p.A - dir * (p.Radius * 0.55f) + side * (p.Radius * 0.5f);
                    Vector2 right = p.A - dir * (p.Radius * 0.55f) - side * (p.Radius * 0.5f);
                    AddTriangle(vh, tip, left, right, p.Color);
                    break;
                }
                case 4:
                {
                    float yaw = p.Thickness * Mathf.Deg2Rad;
                    float half = p.B.x * 0.5f * Mathf.Deg2Rad;
                    int segments = 14;
                    for (int i = 0; i < segments; i++)
                    {
                        float a0 = yaw - half + (2f * half) * i / segments;
                        float a1 = yaw - half + (2f * half) * (i + 1) / segments;
                        var v0 = new Vector3(Mathf.Sin(a0), Mathf.Cos(a0), 0f) * p.Radius;
                        var v1 = new Vector3(Mathf.Sin(a1), Mathf.Cos(a1), 0f) * p.Radius;
                        AddTriangle(vh, p.A, p.A + new Vector2(v0.x, v0.y),
                                    p.A + new Vector2(v1.x, v1.y), p.Color);
                    }
                    break;
                }
                case 5:
                {
                    float t = Mathf.Max(0.6f, p.Thickness);
                    AddQuad(vh, new Vector2(p.A.x, p.A.y), new Vector2(p.B.x, p.A.y),
                            new Vector2(p.B.x, p.A.y + t), new Vector2(p.A.x, p.A.y + t), p.Color);
                    AddQuad(vh, new Vector2(p.A.x, p.B.y - t), new Vector2(p.B.x, p.B.y - t),
                            new Vector2(p.B.x, p.B.y), new Vector2(p.A.x, p.B.y), p.Color);
                    AddQuad(vh, new Vector2(p.A.x, p.A.y), new Vector2(p.A.x + t, p.A.y),
                            new Vector2(p.A.x + t, p.B.y), new Vector2(p.A.x, p.B.y), p.Color);
                    AddQuad(vh, new Vector2(p.B.x - t, p.A.y), new Vector2(p.B.x, p.A.y),
                            new Vector2(p.B.x, p.B.y), new Vector2(p.B.x - t, p.B.y), p.Color);
                    break;
                }
                case 6:
                    AddLineInternal(vh, p.A + new Vector2(-p.Radius, 0f), p.A + new Vector2(p.Radius, 0f),
                                    1.5f, p.Color);
                    AddLineInternal(vh, p.A + new Vector2(0f, -p.Radius), p.A + new Vector2(0f, p.Radius),
                                    1.5f, p.Color);
                    break;
                case 7:
                {
                    if (p.Points == null) break;
                    for (int i = 0; i < p.Points.Count - 1; i++)
                        AddLineInternal(vh, p.Points[i], p.Points[i + 1], p.Thickness, p.Color);
                    break;
                }
            }
        }

        static void AddTriangle(VertexHelper vh, Vector2 a, Vector2 b, Vector2 c, Color color)
        {
            int index = vh.currentVertCount;
            vh.AddVert(new Vector3(a.x, a.y, 0f), color, Vector2.zero);
            vh.AddVert(new Vector3(b.x, b.y, 0f), color, Vector2.zero);
            vh.AddVert(new Vector3(c.x, c.y, 0f), color, Vector2.zero);
            vh.AddTriangle(index, index + 1, index + 2);
        }

        static void AddQuad(VertexHelper vh, Vector2 a, Vector2 b, Vector2 c, Vector2 d, Color color)
        {
            int index = vh.currentVertCount;
            vh.AddVert(new Vector3(a.x, a.y, 0f), color, Vector2.zero);
            vh.AddVert(new Vector3(b.x, b.y, 0f), color, Vector2.zero);
            vh.AddVert(new Vector3(c.x, c.y, 0f), color, Vector2.zero);
            vh.AddVert(new Vector3(d.x, d.y, 0f), color, Vector2.zero);
            vh.AddTriangle(index, index + 1, index + 2);
            vh.AddTriangle(index + 2, index + 3, index);
        }

        static void AddLineInternal(VertexHelper vh, Vector2 a, Vector2 b, float thickness, Color color)
        {
            Vector2 dir = b - a;
            float length = dir.magnitude;
            if (length < 0.001f) return;
            dir /= length;
            Vector2 side = new Vector2(-dir.y, dir.x) * (Mathf.Max(0.5f, thickness) * 0.5f);
            AddQuad(vh, a + side, b + side, b - side, a - side, color);
        }

        static void AddRingInternal(VertexHelper vh, Vector2 center, float radius, Color color,
                                    float thickness)
        {
            int segments = 24;
            float inner = Mathf.Max(0.1f, radius - Mathf.Max(0.8f, thickness));
            for (int i = 0; i < segments; i++)
            {
                float a0 = Mathf.PI * 2f * i / segments;
                float a1 = Mathf.PI * 2f * (i + 1) / segments;
                Vector2 d0 = new Vector2(Mathf.Cos(a0), Mathf.Sin(a0));
                Vector2 d1 = new Vector2(Mathf.Cos(a1), Mathf.Sin(a1));
                AddQuad(vh, center + d0 * radius, center + d1 * radius,
                        center + d1 * inner, center + d0 * inner, color);
            }
        }
    }
}
