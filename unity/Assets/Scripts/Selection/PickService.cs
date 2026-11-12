using System;
using System.Collections.Generic;
using UnityEngine;

namespace Yixuan.Pick
{
    /// <summary>
    /// One selection entry point shared by a manual crosshair confirm, the AI
    /// visual-confirm path (node B) and map/entity clicks. The only thing that
    /// publishes a ConfirmedSelection is a real pixel hit on the current EyeCamera
    /// frame (or an explicitly-authorized entity click that carries no image hit).
    /// </summary>
    public static class PickService
    {
        public static PickFrame LastFrame { get; private set; }
        public static PickHit LastCenterHit { get; private set; }
        public static string LastError { get; private set; } = "";
        /// <summary>True when the latest confirm built a record but the publish was rejected (stale generation).</summary>
        public static bool LastPublishRejected { get; private set; }
        /// <summary>Live bounds used by the last surface-point validation (diagnostics only).</summary>
        public static Bounds LastLiveBounds { get; private set; }
        /// <summary>"ok" or the explicit reason the last surface point was rejected.</summary>
        public static string LastSurfaceValidationNote { get; private set; } = "";

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetSession()
        {
            LastFrame = null;
            LastCenterHit = null;
            LastError = "";
            LastPublishRejected = false;
            LastLiveBounds = new Bounds();
            LastSurfaceValidationNote = "";
        }

        public static Camera ResolveEye()
        {
            var go = GameObject.Find("EyeCamera");
            if (go != null)
            {
                var camera = go.GetComponent<Camera>();
                if (camera != null) return camera;
            }
            var cameras = UnityEngine.Object.FindObjectsOfType<Camera>();
            foreach (var camera in cameras)
            {
                if (camera != null && camera.name.Contains("EyeCamera")) return camera;
            }
            LastError = "missing EyeCamera";
            return null;
        }

        public static bool EnsureRegistry()
        {
            if (EntityPickRegistry.EnsureBuilt()) return true;
            LastError = "selection registry unavailable: " + EntityPickRegistry.Error;
            return false;
        }

        /// <summary>Capture the current EyeCamera frame without changing the camera.</summary>
        public static PickFrame CaptureEyeFrame(Camera eye = null, int width = 0, int height = 0)
        {
            if (!EnsureRegistry()) return null;
            if (eye == null) eye = ResolveEye();
            if (eye == null) return null;
            var frame = EntityIdCapture.Capture(eye, width, height);
            LastFrame = frame;
            if (!frame.Ok) LastError = frame.error;
            return frame;
        }

        public static PickHit ResolveAtUv(PickFrame frame, float u, float v)
        {
            if (frame == null || !frame.Ok) return null;
            return frame.ResolveUv(u, v);
        }

        /// <summary>
        /// Resolve a UV box (top-left origin, like model output) to candidate
        /// entities. A box that contains several substantial entities is marked
        /// ambiguous; it must never be auto-collapsed to the first candidate.
        /// </summary>
        public static PickBoxResult ResolveBox(PickFrame frame, Rect uvBoxTopLeft)
        {
            var result = new PickBoxResult { ok = false, uv_box = uvBoxTopLeft };
            if (frame == null || !frame.Ok)
            {
                result.note = "no_frame";
                return result;
            }
            float xMin = Mathf.Clamp01(Mathf.Min(uvBoxTopLeft.xMin, uvBoxTopLeft.xMax));
            float xMax = Mathf.Clamp01(Mathf.Max(uvBoxTopLeft.xMin, uvBoxTopLeft.xMax));
            float yTop = Mathf.Clamp01(Mathf.Min(uvBoxTopLeft.yMin, uvBoxTopLeft.yMax));
            float yBottom = Mathf.Clamp01(Mathf.Max(uvBoxTopLeft.yMin, uvBoxTopLeft.yMax));
            // Model/image boxes are top-left origin, and the ID frame rows are also
            // top-origin (readback order), so no vertical flip is needed here.
            var box = Rect.MinMaxRect(xMin, yTop, xMax, yBottom);
            int x0 = Mathf.Clamp(Mathf.FloorToInt(box.xMin * frame.width), 0, frame.width - 1);
            int x1 = Mathf.Clamp(Mathf.CeilToInt(box.xMax * frame.width) - 1, 0, frame.width - 1);
            int y0 = Mathf.Clamp(Mathf.FloorToInt(box.yMin * frame.height), 0, frame.height - 1);
            int y1 = Mathf.Clamp(Mathf.CeilToInt(box.yMax * frame.height) - 1, 0, frame.height - 1);
            int totalPixels = Mathf.Max(1, (x1 - x0 + 1) * (y1 - y0 + 1));
            var pixelsByCode = new Dictionary<int, List<Vector2Int>>();
            for (int y = y0; y <= y1; y++)
            {
                for (int x = x0; x <= x1; x++)
                {
                    int code = frame.CodeAt(x, y);
                    if (code == 0) continue;
                    if (frame.DepthAt(x, y) <= 0f) continue;   // valid depth only
                    List<Vector2Int> pixels;
                    if (!pixelsByCode.TryGetValue(code, out pixels))
                    {
                        pixels = new List<Vector2Int>(64);
                        pixelsByCode[code] = pixels;
                    }
                    pixels.Add(new Vector2Int(x, y));
                }
            }
            var list = new List<PickBoxCandidate>();
            foreach (var pair in pixelsByCode)
            {
                var entry = EntityPickRegistry.GetByCode(pair.Key);
                if (entry == null) continue;
                // correct centroid: sum then divide, no -1 seed bias
                double sumX = 0.0, sumY = 0.0;
                foreach (var pixel in pair.Value) { sumX += pixel.x; sumY += pixel.y; }
                float centerU = (float)(sumX / pair.Value.Count + 0.5) / frame.width;
                float centerV = (float)(sumY / pair.Value.Count + 0.5) / frame.height;
                var candidate = new PickBoxCandidate
                {
                    entity_id = entry.Id,
                    name = entry.DisplayName,
                    cls = entry.Cls,
                    pixel_count = pair.Value.Count,
                    area_fraction = pair.Value.Count / (float)totalPixels,
                    centroid_uv = new Vector2(centerU, centerV),
                    distance_m = Vector3.Distance(frame.snapshot.position, entry.LivePosition()),
                    sample_code = pair.Key,
                };
                list.Add(candidate);
            }
            list.Sort((a, b) => b.pixel_count.CompareTo(a.pixel_count));
            result.candidates.AddRange(list);
            result.ok = frame.Ok;
            if (list.Count == 0)
            {
                result.note = "no_selectable_entity_in_box";
                return result;
            }
            result.ambiguous = list.Count > 1;
            if (list.Count > 1)
            {
                float secondFraction = list[1].pixel_count / (float)list[0].pixel_count;
                result.note = secondFraction >= 0.25f
                    ? "box covers multiple entities; do not auto-select the first"
                    : "box touches more than one entity; multi-entity box is not a confirmed selection";
            }

            // Representative point for EVERY candidate, each from that candidate's
            // OWN ID/depth pixels inside this box. A non-dominant entity (e.g. a chair
            // under a large building box) must get its own traceable sample instead of
            // inheriting the dominant candidate's reliability.
            foreach (var candidate in list)
            {
                List<Vector2Int> ownPixels;
                if (!pixelsByCode.TryGetValue(candidate.sample_code, out ownPixels) || ownPixels == null || ownPixels.Count == 0)
                {
                    candidate.has_reliable_point = false;
                    candidate.point_note = "no own ID pixels in this box";
                    continue;
                }
                var ordered = new List<Vector2Int>(ownPixels);
                Vector2 centerPx = new Vector2(
                    (candidate.centroid_uv.x * frame.width) - 0.5f,
                    (candidate.centroid_uv.y * frame.height) - 0.5f);
                ordered.Sort((a, b) =>
                    ((a.x - centerPx.x) * (a.x - centerPx.x) + (a.y - centerPx.y) * (a.y - centerPx.y))
                    .CompareTo((b.x - centerPx.x) * (b.x - centerPx.x) + (b.y - centerPx.y) * (b.y - centerPx.y)));
                int attempts = Mathf.Min(64, ordered.Count);
                for (int i = 0; i < attempts; i++)
                {
                    var pixel = ordered[i];
                    float u = (pixel.x + 0.5f) / frame.width;
                    float v = (pixel.y + 0.5f) / frame.height;
                    var hit = frame.ResolveUv(u, v);
                    if (hit == null || !hit.Valid || hit.entity == null) continue;
                    if (hit.code != candidate.sample_code || hit.entity.Id != candidate.entity_id) continue;
                    if (hit.distance_m <= 0f) continue;
                    candidate.surface_point = hit.surface_point;
                    candidate.distance_m = hit.distance_m;
                    candidate.sample_pixel_x = pixel.x;
                    candidate.sample_pixel_y = pixel.y;
                    candidate.sample_depth01 = frame.DepthAt(pixel.x, pixel.y);
                    candidate.has_reliable_point = true;
                    candidate.point_note = "pixel belongs to this entity's own ID/depth mask";
                    break;
                }
                if (!candidate.has_reliable_point)
                {
                    candidate.surface_point = Vector3.zero;
                    candidate.point_note = "no reliable own-pixel aiming point in this box";
                }
            }
            return result;
        }

        /// <summary>
        /// Capture the current eye frame and confirm the entity under the fixed
        /// centre crosshair. Shared by manual confirm and (node B) AI confirm.
        /// A negative generation means an explicit manual selection: it advances
        /// the shared generation first, invalidating any older pending AI token.
        /// Returns null when the publish was rejected so callers cannot report a
        /// failed publish as a confirmed hit.
        /// </summary>
        public static PickHit ConfirmAtCenter(string source, int generation = -1, Camera eye = null, string expectedEntityId = null)
        {
            LastError = "";
            LastPublishRejected = false;
            LastLiveBounds = new Bounds();
            LastSurfaceValidationNote = "";
            bool manual = generation < 0;
            if (manual) generation = SelectionBroker.BeginManualSelection();
            var frame = CaptureEyeFrame(eye);
            if (frame == null)
            {
                LastError = string.IsNullOrEmpty(LastError) ? "no_frame" : LastError;
                return null;
            }
            var hit = frame.ResolveUv(0.5f, 0.5f);
            LastCenterHit = hit;
            if (hit == null || !hit.Valid)
            {
                LastError = hit != null && hit.is_sky ? "no_surface_at_center" : "no_selectable_entity_at_center";
                return null;
            }
            if (!string.IsNullOrEmpty(expectedEntityId) && hit.EntityId != expectedEntityId)
            {
                LastError = "center_entity_changed: expected=" + expectedEntityId + " actual=" + hit.EntityId;
                LastPublishRejected = true;
                return null;
            }
            var record = BuildRecord(hit, source, generation);
            // The surface point comes from the same frozen depth buffer that produced
            // the pixel hit, so it must fall inside the entity's CURRENT world bounds
            // with a small epsilon. A stale catalog position, a wrong entity binding or
            // a broken projection fails here instead of publishing an impossible point.
            // This is an error, never a clamp / pivot edit / whole-frame re-search.
            string surfaceError;
            if (!ValidateSurfacePoint(record, hit, out surfaceError))
            {
                record.surface_inside_live_bounds = false;
                record.surface_validation_note = surfaceError;
                LastError = "surface_point_invalid: " + surfaceError;
                LastPublishRejected = true;
                return null;
            }
            record.surface_inside_live_bounds = true;
            record.surface_validation_note = "ok";
            if (!SelectionBroker.TryPublish(generation, record))
            {
                LastError = SelectionBroker.LastRejectReason;
                LastPublishRejected = true;
                return null;
            }
            return hit;
        }

        /// <summary>
        /// Validate the same-frame depth surface point against the entity's live
        /// world bounds (bounds are a necessary condition, not the only proof: the
        /// ID code and depth already bound the pixel to this entity this frame).
        /// </summary>
        public static bool ValidateSurfacePoint(SelectionRecord record, PickHit hit, out string error)
        {
            error = "";
            LastSurfaceValidationNote = "";
            if (record == null || hit == null)
            {
                error = "missing_record_or_hit";
                LastSurfaceValidationNote = error;
                return false;
            }
            // A point must come from a real surface of THIS frame: valid depth
            // (is_sky=false implies depth01>0), positive eye distance, finite
            // coordinates, and a snapshot that can reproject it back to the hit
            // pixel. Bounds membership alone would accept e.g. Vector3.zero for an
            // object whose AABB happens to contain the origin.
            if (hit.is_sky || hit.distance_m <= 0f)
            {
                error = "surface_point_has_no_valid_depth distance=" + hit.distance_m.ToString("F3") +
                        " is_sky=" + hit.is_sky;
                LastSurfaceValidationNote = error;
                return false;
            }
            Vector3 sp = hit.surface_point;
            if (!IsFinite(sp.x) || !IsFinite(sp.y) || !IsFinite(sp.z))
            {
                error = "surface_point_not_finite point=(" + sp.x + "," + sp.y + "," + sp.z + ")";
                LastSurfaceValidationNote = error;
                return false;
            }
            if (hit.snapshot == null)
            {
                error = "surface_point_missing_camera_snapshot";
                LastSurfaceValidationNote = error;
                return false;
            }
            float depth01;
            Vector3 projected = hit.snapshot.ProjectWorldToViewport(sp, out depth01);
            if (projected.x < -0.5f || projected.y < -0.5f ||
                projected.x > 1.5f || projected.y > 1.5f)
            {
                error = "surface_point_reprojection_failed projected=(" +
                        projected.x.ToString("F4") + "," + projected.y.ToString("F4") + ")";
                LastSurfaceValidationNote = error;
                return false;
            }
            float width = Mathf.Max(1, hit.snapshot.width);
            float height = Mathf.Max(1, hit.snapshot.height);
            float reprojectionPx = Mathf.Sqrt(
                Mathf.Pow((projected.x - hit.u) * width, 2f) +
                Mathf.Pow((projected.y - hit.v) * height, 2f));
            if (reprojectionPx > 2f)
            {
                error = "surface_point_reprojection_mismatch reprojection_px=" + reprojectionPx.ToString("F2") +
                        " predicted_uv=(" + projected.x.ToString("F4") + "," + projected.y.ToString("F4") +
                        ") hit_uv=(" + hit.u.ToString("F4") + "," + hit.v.ToString("F4") + ")";
                LastSurfaceValidationNote = error;
                return false;
            }
            Bounds bounds = new Bounds(
                (record.bounds_min + record.bounds_max) * 0.5f,
                record.bounds_max - record.bounds_min);
            LastLiveBounds = bounds;
            if (bounds.size.x <= 0f && bounds.size.y <= 0f && bounds.size.z <= 0f)
            {
                error = "entity_live_bounds_unavailable";
                LastSurfaceValidationNote = error;
                return false;
            }
            string note;
            bool inside = SurfaceBoundsCheck.Contains(
                sp.x, sp.y, sp.z,
                record.bounds_min.x, record.bounds_min.y, record.bounds_min.z,
                record.bounds_max.x, record.bounds_max.y, record.bounds_max.z,
                SurfaceBoundsCheck.DefaultEpsilonMeters, out note);
            LastSurfaceValidationNote = inside
                ? "ok reprojection_px=" + reprojectionPx.ToString("F2")
                : note;
            if (!inside) error = note;
            return inside;
        }

        static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        /// <summary>
        /// Confirm by entity id without an image hit (map click / reuse). A new
        /// map/manual selection advances generation so stale AI results cannot
        /// overwrite it. Returns null when the publish failed.
        /// </summary>
        public static SelectionRecord ConfirmEntity(string entityId, string source, int generation = -1)
        {
            if (!EnsureRegistry())
            {
                LastError = "selection registry unavailable: " + EntityPickRegistry.Error;
                return null;
            }
            var entry = EntityPickRegistry.Get(entityId);
            if (entry == null || !entry.Selectable)
            {
                LastError = "entity not selectable: " + (entityId ?? "null");
                return null;
            }
            if (generation < 0) generation = SelectionBroker.BeginManualSelection();
            var record = BuildEntityRecord(entry, source, generation, Vector3.zero, false);
            if (!SelectionBroker.TryPublish(generation, record))
            {
                LastError = SelectionBroker.LastRejectReason;
                LastPublishRejected = true;
                return null;
            }
            return record;
        }

        public static SelectionRecord BuildRecord(PickHit hit, string source, int generation)
        {
            var record = BuildEntityRecord(hit.entity, source, generation, hit.surface_point, true);
            record.frame_id = hit.snapshot != null ? hit.snapshot.frame_id : -1;
            record.frame_count = hit.snapshot != null ? hit.snapshot.frame_count : -1;
            record.time_since_startup = hit.snapshot != null ? hit.snapshot.time_since_startup : 0d;
            record.camera_source = hit.snapshot != null ? hit.snapshot.source : "";
            record.eye_position = hit.eye_position;
            record.eye_forward = hit.eye_forward;
            record.compute_snapshot = hit.snapshot;
            record.surface_distance_from_eye_m = hit.distance_m;
            return record;
        }

        public static SelectionRecord BuildEntityRecord(EntityPickEntry entry, string source, int generation,
                                                       Vector3 surfacePoint, bool hasSurface)
        {
            var record = new SelectionRecord
            {
                entity_id = entry != null ? entry.Id : "",
                name = entry != null ? entry.Name : "",
                display_name = entry != null ? entry.DisplayName : "",
                cls = entry != null ? entry.Cls : "",
                kind = entry != null ? entry.Kind : "",
                confirmed = true,
                source = source ?? "unknown",
                generation = generation,
                dynamic = entry != null && entry.Dynamic,
                capabilities = entry != null ? entry.Capabilities : "",
                bounds_from_live_renderers = entry != null && entry.UsesLiveRendererBounds,
                live_position_note = entry == null
                    ? "no entity"
                    : entry.UsesLiveRendererBounds
                        ? "live renderer bounds sampled at publish time"
                        : entry.Dynamic
                            ? "live transform sampled at publish time"
                            : "static catalog/mesh position",
            };
            Vector3 entityPosition = entry != null ? entry.LivePosition() : Vector3.zero;
            record.entity_position = entityPosition;
            record.surface_point = hasSurface ? surfacePoint : entityPosition;
            if (entry != null)
            {
                // Same live source the ID pass drew this frame; never a build-time
                // catalog snapshot for explicit lab assets (runtime-moved geometry).
                var bounds = entry.LiveWorldBounds();
                record.bounds_min = bounds.min;
                record.bounds_max = bounds.max;
            }
            var character = FindCharacterTransform();
            if (character != null)
            {
                record.character_position = character.position;
                record.body_yaw_deg = character.eulerAngles.y;
                Vector3 delta = entityPosition - character.position;
                float distance = delta.magnitude;
                record.distance_m = distance;
                if (delta.sqrMagnitude > 1e-4f)
                {
                    float yawTo = Mathf.Atan2(delta.x, delta.z) * Mathf.Rad2Deg;
                    record.bearing_deg = Mathf.DeltaAngle(record.body_yaw_deg, yawTo);
                }
            }
            else
            {
                record.character_position = record.eye_position;
                record.distance_m = Vector3.Distance(record.eye_position, entityPosition);
            }
            return record;
        }

        /// <summary>
        /// Live, read-only re-sampling of the current confirmed selection for
        /// LLM/task/map facts (R5). The immutable observation record is kept
        /// untouched; this returns current entity/character/eye relations with a
        /// sample timestamp and an availability reason for dynamic entities.
        /// </summary>
        public static SelectionLiveFacts SampleLive(SelectionRecord record = null, Transform character = null)
        {
            var selection = record ?? SelectionBroker.Current;
            var facts = new SelectionLiveFacts
            {
                sampled_time = Time.realtimeSinceStartupAsDouble,
                sampled_frame = Time.frameCount,
            };
            if (selection == null || !selection.confirmed)
            {
                facts.available = false;
                facts.reason = "no_confirmed_selection";
                return facts;
            }
            facts.entity_id = selection.entity_id;
            facts.display_name = selection.display_name;
            facts.cls = selection.cls;
            facts.capabilities = selection.capabilities;
            facts.source = selection.source;
            facts.generation = selection.generation;
            facts.observed_frame_id = selection.frame_id;
            facts.observed_time = selection.time_since_startup;
            facts.observed_position = selection.entity_position;
            facts.observed_surface_point = selection.surface_point;
            facts.dynamic = selection.dynamic;
            facts.age_seconds = selection.time_since_startup > 0.0
                ? (float)Math.Max(0.0, facts.sampled_time - selection.time_since_startup)
                : 0f;

            var entity = EntityPickRegistry.Get(selection.entity_id);
            Vector3 currentPosition = selection.entity_position;
            if (entity == null)
            {
                facts.available = false;
                facts.reason = "entity_no_longer_in_registry";
            }
            else if (entity.Selectable || entity.Renderers.Count > 0 || entity.EntityMesh != null)
            {
                currentPosition = entity.LivePosition();
                facts.available = true;
                facts.position_source = entity.Dynamic ? "live_dynamic_transform" : "static_source_geometry";
            }
            else
            {
                facts.available = false;
                facts.reason = "entity_geometry_unavailable";
            }
            facts.current_position = currentPosition;
            facts.observed_to_current_m = Vector3.Distance(selection.entity_position, currentPosition);

            var characterTransform = character != null ? character : FindCharacterTransform();
            if (characterTransform != null)
            {
                facts.character_position = characterTransform.position;
                facts.body_yaw_deg = characterTransform.eulerAngles.y;
            }
            var eye = ResolveEye();
            if (eye != null)
            {
                facts.eye_position = eye.transform.position;
                facts.eye_forward = eye.transform.forward;
                facts.pitch_deg = Mathf.Asin(Mathf.Clamp(eye.transform.forward.y, -0.999f, 0.999f)) * Mathf.Rad2Deg;
            }
            Vector3 anchor = facts.character_position.sqrMagnitude > 1e-6f || characterTransform != null
                ? facts.character_position
                : facts.eye_position;
            if (!facts.available)
            {
                anchor = facts.eye_position;
                currentPosition = facts.eye_position;
            }
            Vector3 delta = currentPosition - anchor;
            facts.distance_m = delta.magnitude;
            if (delta.sqrMagnitude > 1e-6f)
            {
                float yawTo = Mathf.Atan2(delta.x, delta.z) * Mathf.Rad2Deg;
                facts.bearing_deg = characterTransform != null
                    ? Mathf.DeltaAngle(facts.body_yaw_deg, yawTo)
                    : yawTo;
            }
            return facts;
        }

        public static Transform FindCharacterTransform()
        {
            var world = Yixuan.Dynamic.DynamicWorld.Instance;
            if (world != null && world.Character != null) return world.Character.transform;
            var loco = UnityEngine.Object.FindObjectOfType<Yixuan.Motion.CharacterLocomotion>();
            if (loco != null) return loco.transform;
            var go = GameObject.Find("Yixuan_Test");
            return go != null ? go.transform : null;
        }
    }
}
