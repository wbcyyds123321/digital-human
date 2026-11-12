using System;

namespace Yixuan.Pick
{
    /// <summary>
    /// Pure AABB membership check used when a pixel hit publishes a confirmed
    /// selection. The surface point comes from the real depth buffer of the same
    /// frozen frame, so it must lie inside the entity's LIVE world bounds (with a
    /// small epsilon). This catches an obviously impossible surface point (an
    /// entity id / bounds binding mismatch, a stale catalog position, a wrong
    /// projection) instead of clamping the point, editing the entity pivot or
    /// widening the search to the whole frame.
    ///
    /// No UnityEngine dependency: deterministic offline tests can replay it.
    /// </summary>
    public static class SurfaceBoundsCheck
    {
        /// <summary>Allowed slack around the live bounds (metres).</summary>
        public const float DefaultEpsilonMeters = 0.05f;

        public static bool Contains(float px, float py, float pz,
            float minX, float minY, float minZ, float maxX, float maxY, float maxZ,
            out string note)
        {
            return Contains(px, py, pz, minX, minY, minZ, maxX, maxY, maxZ, DefaultEpsilonMeters, out note);
        }

        public static bool Contains(float px, float py, float pz,
            float minX, float minY, float minZ, float maxX, float maxY, float maxZ,
            float epsilonMeters, out string note)
        {
            note = "";
            if (float.IsNaN(px) || float.IsNaN(py) || float.IsNaN(pz) ||
                float.IsInfinity(px) || float.IsInfinity(py) || float.IsInfinity(pz) ||
                float.IsNaN(minX) || float.IsNaN(minY) || float.IsNaN(minZ) ||
                float.IsNaN(maxX) || float.IsNaN(maxY) || float.IsNaN(maxZ) ||
                float.IsInfinity(minX) || float.IsInfinity(minY) || float.IsInfinity(minZ) ||
                float.IsInfinity(maxX) || float.IsInfinity(maxY) || float.IsInfinity(maxZ))
            {
                note = "surface_or_bounds_not_finite";
                return false;
            }
            if (maxX < minX || maxY < minY || maxZ < minZ)
            {
                note = "entity_bounds_invalid";
                return false;
            }
            float epsilon = epsilonMeters < 0f ? 0f : epsilonMeters;
            bool inside = px >= minX - epsilon && px <= maxX + epsilon &&
                          py >= minY - epsilon && py <= maxY + epsilon &&
                          pz >= minZ - epsilon && pz <= maxZ + epsilon;
            if (!inside)
            {
                note = "surface_point_outside_entity_live_bounds point=(" +
                       px.ToString("F4") + "," + py.ToString("F4") + "," + pz.ToString("F4") +
                       ") bounds_min=(" + minX.ToString("F4") + "," + minY.ToString("F4") + "," + minZ.ToString("F4") +
                       ") bounds_max=(" + maxX.ToString("F4") + "," + maxY.ToString("F4") + "," + maxZ.ToString("F4") +
                       ") epsilon=" + epsilon.ToString("F3");
            }
            return inside;
        }
    }
}
