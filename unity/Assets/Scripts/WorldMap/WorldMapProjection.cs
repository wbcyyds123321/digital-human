using UnityEngine;

namespace Yixuan.WorldMap
{
    /// <summary>One metric projection for the basemap, markers and pointer coordinates.</summary>
    public static class WorldMapProjection
    {
        public static float Scale(Vector2 viewportSize, Vector2 worldSpan, float zoom)
        {
            return Mathf.Min(viewportSize.x / Mathf.Max(0.001f, worldSpan.x),
                             viewportSize.y / Mathf.Max(0.001f, worldSpan.y)) * zoom;
        }

        public static Vector2 WorldToMap(Vector2 world, Vector2 viewportSize, Vector2 center,
                                        Vector2 worldSpan, float zoom)
        {
            float scale = Scale(viewportSize, worldSpan, zoom);
            return viewportSize * 0.5f + new Vector2(world.x - center.x, center.y - world.y) * scale;
        }

        public static Vector2 MapToWorld(Vector2 local, Vector2 viewportSize, Vector2 center,
                                        Vector2 worldSpan, float zoom)
        {
            Vector2 offset = (local - viewportSize * 0.5f) / Scale(viewportSize, worldSpan, zoom);
            return center + new Vector2(offset.x, -offset.y);
        }

        public static Rect ImageRect(Vector2 viewportSize, Vector2 center, Vector2 min, Vector2 max, float zoom)
        {
            Vector2 span = max - min;
            Vector2 topLeft = WorldToMap(new Vector2(min.x, max.y), viewportSize, center, span, zoom);
            return new Rect(topLeft, span * Scale(viewportSize, span, zoom));
        }
    }
}
