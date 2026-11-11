using System;
using System.Collections.Generic;
using UnityEngine;

namespace Yixuan.Stage2
{
    /// <summary>
    /// Pure-math ray picking for the stage-2 prototype. No Physics, no colliders.
    /// Broad phase: sample AABB. Narrow phase: two-sided ray/triangle.
    /// Occlusion: raw unexpanded collision AABBs + curated entity AABBs.
    /// </summary>
    public static class Stage2PickMath
    {
        public const float OcclusionToleranceMeters = 0.001f;
        readonly static List<Stage2SampleMesh> _noSamples = new List<Stage2SampleMesh>();

        public static bool PointInsideAabb(Vector3 p, Vector3 mn, Vector3 mx)
        {
            return p.x >= mn.x && p.x <= mx.x && p.y >= mn.y && p.y <= mx.y && p.z >= mn.z && p.z <= mx.z;
        }

        public static bool RayAabb(Vector3 origin, Vector3 dir, Vector3 mn, Vector3 mx, out float tEnter, out float tExit)
        {
            tEnter = 0f;
            tExit = float.PositiveInfinity;
            for (int i = 0; i < 3; i++)
            {
                float o = origin[i];
                float d = dir[i];
                float a = mn[i];
                float b = mx[i];
                if (Mathf.Abs(d) < 1e-9f)
                {
                    if (o < a || o > b) return false;
                }
                else
                {
                    float t1 = (a - o) / d;
                    float t2 = (b - o) / d;
                    if (t1 > t2) { float tmp = t1; t1 = t2; t2 = tmp; }
                    if (t1 > tEnter) tEnter = t1;
                    if (t2 < tExit) tExit = t2;
                    if (tEnter > tExit) return false;
                }
            }
            return tExit >= 0f;
        }

        /// <summary>Moller-Trumbore, double sided (both windings are accepted).</summary>
        public static bool RayTriangle(Vector3 origin, Vector3 dir, Vector3 a, Vector3 b, Vector3 c, out float t)
        {
            t = 0f;
            Vector3 e1 = b - a;
            Vector3 e2 = c - a;
            Vector3 h = Vector3.Cross(dir, e2);
            float det = Vector3.Dot(e1, h);
            if (Mathf.Abs(det) < 1e-12f) return false;
            float inv = 1f / det;
            Vector3 s = origin - a;
            float u = Vector3.Dot(s, h) * inv;
            if (u < -1e-6f || u > 1f + 1e-6f) return false;
            Vector3 q = Vector3.Cross(s, e1);
            float v = Vector3.Dot(dir, q) * inv;
            if (v < -1e-6f || u + v > 1f + 1e-6f) return false;
            float tt = Vector3.Dot(e2, q) * inv;
            if (tt <= 1e-6f) return false;
            t = tt;
            return true;
        }

        public static Vector3 RayFromSnapshot(Stage2CameraSnapshot snap, float viewportX, float viewportY)
        {
            float halfH = Mathf.Tan(snap.Fov * Mathf.Deg2Rad * 0.5f);
            float nx = viewportX * 2f - 1f;
            float ny = viewportY * 2f - 1f;
            Vector3 local = new Vector3(nx * halfH * snap.Aspect, ny * halfH, 1f);
            return (snap.Rotation * local).normalized;
        }

        public static Stage2PickResult PickCenter(Stage2SceneData data, Stage2CameraSnapshot snap,
            Vector3 characterPosition, float maxDistance = 0f)
        {
            if (snap == null || !snap.Valid)
            {
                return Stage2PickResult.Unavailable("no valid camera snapshot");
            }
            float maxDist = maxDistance > 0f ? maxDistance : snap.Far;
            return Pick(data, snap.Position, RayFromSnapshot(snap, 0.5f, 0.5f), maxDist,
                Mathf.Max(0f, snap.Near), characterPosition, snap, null, null);
        }

        public static Stage2PickResult Pick(Stage2SceneData data, Vector3 origin, Vector3 direction,
            float maxDistance, float minDistance, Vector3 characterPosition,
            Stage2CameraSnapshot snapshot,
            IList<Stage2Occluder> extraOccluders = null,
            IList<Stage2SampleMesh> extraSamples = null)
        {
            var result = new Stage2PickResult
            {
                RayOrigin = origin,
                RayDirection = direction,
                MaxDistance = maxDistance,
                MinDistance = minDistance,
                FrameId = snapshot != null ? snapshot.FrameId : -1,
                CameraSource = snapshot != null ? snapshot.Source : "",
                PoseId = snapshot != null ? snapshot.PoseId : "",
                SnapshotId = snapshot != null ? snapshot.SnapshotId : "",
            };
            if (data == null || !data.Valid)
            {
                result.Status = Stage2PickStatus.DataError;
                result.StatusText = "data_error";
                result.Diagnostic = data == null ? "no data" : data.Error;
                return result;
            }
            if (!IsFinite(origin) || !IsFinite(direction))
            {
                result.Status = Stage2PickStatus.Unavailable;
                result.StatusText = "invalid_ray";
                result.Diagnostic = "ray origin/direction is not finite";
                return result;
            }
            float len = direction.magnitude;
            if (len < 1e-6f)
            {
                result.Status = Stage2PickStatus.Unavailable;
                result.StatusText = "invalid_ray";
                result.Diagnostic = "ray direction is zero";
                return result;
            }
            if (maxDistance <= 0f) maxDistance = float.PositiveInfinity;
            direction /= len;
            result.RayDirection = direction;

            // ---- broad phase + narrow phase (all samples, nearest real triangle) ----
            float bestT = float.PositiveInfinity;
            Stage2SampleMesh bestSample = null;
            int bestTri = -1;
            Stage2SampleMesh[] collected = null;
            List<Stage2SampleMesh> collectedList = null;
            if (extraSamples != null && extraSamples.Count > 0)
            {
                collectedList = new List<Stage2SampleMesh>(extraSamples.Count + data.Meshes.Count);
                collectedList.AddRange(extraSamples);
                foreach (var m in data.Meshes.Values) collectedList.Add(m);
            }
            if (collectedList != null) collected = collectedList.ToArray();

            IEnumerable<Stage2SampleMesh> samples;
            if (collectedList != null) samples = collectedList; else samples = data.Meshes.Values;
            foreach (var sample in samples)
            {
                if (sample == null) continue;
                float te, tx;
                if (!RayAabb(origin, direction, sample.Bounds.min, sample.Bounds.max, out te, out tx)) continue;
                if (tx < 0f) continue;
                if (te > maxDistance) continue;
                result.BroadPhaseSamples.Add(sample.Id);
                int[] tris = sample.Triangles;
                Vector3[] verts = sample.Vertices;
                for (int i = 0; i + 2 < tris.Length; i += 3)
                {
                    float t;
                    if (!RayTriangle(origin, direction, verts[tris[i]], verts[tris[i + 1]], verts[tris[i + 2]], out t)) continue;
                    if (t < minDistance || t > maxDistance) continue;
                    if (t < bestT)
                    {
                        bestT = t;
                        bestSample = sample;
                        bestTri = i / 3;
                    }
                }
            }

            // ---- occlusion (nearest occluder ahead of camera) ----
            float occT = float.PositiveInfinity;
            Stage2Box occBox = null;
            for (int i = 0; i < data.Occluders.Count; i++)
            {
                var box = data.Occluders[i];
                float te, tx;
                if (!RayAabb(origin, direction, box.Min, box.Max, out te, out tx)) continue;
                if (tx < 0f) continue;
                if (PointInsideAabb(origin, box.Min, box.Max))
                {
                    result.Diagnostics.Add("eye_inside_occluder:" + box.Id);
                    continue;
                }
                // The occluder may enter before the near plane but still cover visible
                // space.  Compare the overlap of [tEnter,tExit] with [near,far].
                if (tx < minDistance || te > maxDistance) continue;
                float visibleEntry = Mathf.Max(te, minDistance);
                if (visibleEntry > maxDistance) continue;
                if (visibleEntry < occT)
                {
                    occT = visibleEntry;
                    occBox = box;
                }
            }
            if (extraOccluders != null)
            {
                for (int i = 0; i < extraOccluders.Count; i++)
                {
                    var o = extraOccluders[i];
                    if (o == null || o.min == null || o.max == null || o.min.Length != 3 || o.max.Length != 3) continue;
                    Vector3 mn = new Vector3(o.min[0], o.min[1], o.min[2]);
                    Vector3 mx = new Vector3(o.max[0], o.max[1], o.max[2]);
                    float te, tx;
                    if (!RayAabb(origin, direction, mn, mx, out te, out tx)) continue;
                    if (tx < minDistance || te > maxDistance) continue;
                    if (PointInsideAabb(origin, mn, mx))
                    {
                        result.Diagnostics.Add("eye_inside_occluder:" + o.id);
                        continue;
                    }
                    float visibleEntry = Mathf.Max(te, minDistance);
                    if (visibleEntry > maxDistance) continue;
                    if (visibleEntry < occT)
                    {
                        occT = visibleEntry;
                        occBox = new Stage2Box { Id = o.id, Method = o.method, ClassOrKind = o.class_or_kind, Min = mn, Max = mx };
                    }
                }
            }

            if (bestSample == null)
            {
                result.Status = Stage2PickStatus.NoTarget;
                result.StatusText = "no_target";
                if (result.BroadPhaseSamples.Count > 0)
                {
                    result.Diagnostic = "bbox_hit_no_triangle:" + string.Join(",", result.BroadPhaseSamples.ToArray());
                }
                else
                {
                    result.Diagnostic = "no_broad_phase_hit";
                }
                return result;
            }

            result.SampleId = bestSample.Id;
            result.NameZh = bestSample.NameZh;
            result.TriangleIndex = bestTri;
            result.PartLabel = bestSample.PartOfTriangle(bestTri);
            result.HitPoint = origin + direction * bestT;
            result.DistanceFromCamera = bestT;
            result.DistanceFromCharacter = Vector3.Distance(characterPosition, result.HitPoint);

            if (occBox != null && occT < bestT - OcclusionToleranceMeters)
            {
                result.Status = Stage2PickStatus.Blocked;
                result.StatusText = "blocked";
                // A blocked candidate is diagnostic only: it must never look selectable.
                result.BlockedCandidateId = bestSample.Id;
                result.BlockedCandidatePart = bestSample.PartOfTriangle(bestTri);
                result.SampleId = "";
                result.NameZh = "";
                result.PartLabel = "";
                result.TriangleIndex = -1;
                result.HitPoint = Vector3.zero;
                result.DistanceFromCamera = 0f;
                result.DistanceFromCharacter = 0f;
                result.OccluderId = occBox.Id;
                result.OccluderMethod = occBox.Method;
                result.OccluderClass = occBox.ClassOrKind;
                result.OccluderDistance = occT;
                result.Diagnostic = "foreground_occluder";
                return result;
            }

            result.Status = Stage2PickStatus.Hit;
            result.StatusText = "hit";
            return result;
        }

        public static bool IsFinite(Vector3 v)
        {
            return !float.IsNaN(v.x) && !float.IsInfinity(v.x) &&
                   !float.IsNaN(v.y) && !float.IsInfinity(v.y) &&
                   !float.IsNaN(v.z) && !float.IsInfinity(v.z);
        }

        // ------------------------------------------------------------------
        // Synthetic helpers for math-only unit cases (clearly marked in evidence)
        // ------------------------------------------------------------------
        public static Stage2SampleMesh MakeTriangleSample(string id, Vector3 a, Vector3 b, Vector3 c)
        {
            var verts = new[] { a, b, c };
            var bounds = new Bounds(a, Vector3.zero);
            bounds.Encapsulate(b);
            bounds.Encapsulate(c);
            return new Stage2SampleMesh
            {
                Id = id,
                ClassName = "synthetic",
                NameZh = "synthetic",
                Vertices = verts,
                Triangles = new[] { 0, 1, 2 },
                TriangleParts = new[] { 0 },
                Parts = new[] { "synthetic" },
                Bounds = bounds,
                Synthetic = true,
            };
        }

        public static Stage2Occluder MakeOccluder(string id, Vector3 mn, Vector3 mx, string cls = "synthetic")
        {
            return new Stage2Occluder
            {
                id = id,
                method = "synthetic_aabb",
                class_or_kind = cls,
                min = new[] { mn.x, mn.y, mn.z },
                max = new[] { mx.x, mx.y, mx.z },
            };
        }
    }
}
