using System.Collections.Generic;
using UnityEngine;
using Yixuan.Stage4;

namespace Yixuan.Stage5
{
    public sealed class Stage5SeatCandidate
    {
        public string EntityId;
        public string Label;
        public string EntityClass;
        public Vector3 SeatPoint;
        public Vector3 ApproachPoint;
        public float SeatYaw;
        public string[] Abilities = { "sit" };
        public float Distance;
        public float LocalYaw;
        public bool IsBehind;

        public string Describe()
        {
            return EntityId + "（" + Label + "，" + Distance.ToString("0.0") + "m，" +
                   (IsBehind ? "身后" : "前方") + "）";
        }
    }

    /// <summary>
    /// Lightweight logical entities over the universal Stage-4 seat package. No
    /// GameObjects, no renderers and no colliders are created; the Stage-4 v1
    /// sidecar is the source of truth for seat/approach positions and abilities
    /// are only advertised from this executable adapter.
    /// </summary>
    public static class Stage5SeatRegistry
    {
        public static bool Ready { get { return Stage4SeatData.Loaded && Stage4SeatData.Entries != null && Stage4SeatData.Entries.Length > 0; } }
        public static string Error { get { return Stage4SeatData.Loaded ? "" : (Stage4SeatData.Error ?? "stage4 data not loaded"); } }

        public static List<Stage5SeatCandidate> Snapshot(Transform character)
        {
            var list = new List<Stage5SeatCandidate>();
            if (!Ready || character == null) return list;
            Vector3 forward = character.forward;
            forward.y = 0f;
            if (forward.sqrMagnitude < 1e-6f) forward = Vector3.forward;
            forward.Normalize();
            foreach (var entry in Stage4SeatData.Entries)
            {
                if (entry == null || string.IsNullOrEmpty(entry.entity_id)) continue;
                Vector3 seat = entry.SeatPoint;
                Vector3 to = seat - character.position;
                to.y = 0f;
                float distance = to.magnitude;
                float localYaw = to.sqrMagnitude < 1e-6f ? 0f : Vector3.SignedAngle(forward, to.normalized, Vector3.up);
                list.Add(new Stage5SeatCandidate
                {
                    EntityId = entry.entity_id,
                    Label = DisplayName(entry),
                    EntityClass = entry.entity_class,
                    SeatPoint = seat,
                    ApproachPoint = NavigationApproach(entry, character.position),
                    SeatYaw = entry.SeatYaw,
                    Distance = distance,
                    LocalYaw = localYaw,
                    IsBehind = distance > 0.05f && Mathf.Abs(localYaw) > 90f,
                });
            }
            return list;
        }

        public static Stage5SeatCandidate Find(string id)
        {
            if (!Ready || string.IsNullOrEmpty(id)) return null;
            foreach (var entry in Stage4SeatData.Entries)
            {
                if (entry == null || entry.entity_id != id) continue;
                return new Stage5SeatCandidate
                {
                    EntityId = entry.entity_id,
                    Label = DisplayName(entry),
                    EntityClass = entry.entity_class,
                    SeatPoint = entry.SeatPoint,
                    ApproachPoint = NavigationApproach(entry, Vector3.zero),
                    SeatYaw = entry.SeatYaw,
                };
            }
            return null;
        }

        public static bool CanSit(string id)
        {
            var c = Find(id);
            return c != null && System.Array.IndexOf(c.Abilities, "sit") >= 0;
        }

        static string DisplayName(Stage4SeatEntry entry)
        {
            var catalogEntity = Yixuan.World.WorldEntityCatalog.Get(entry.entity_id);
            if (catalogEntity != null && !string.IsNullOrEmpty(catalogEntity.display_name))
                return catalogEntity.display_name;
            return entry.entity_id;
        }

        /// <summary>
        /// Best walkable approach for one bench (front/right/left/back candidates).
        /// Path validation stays with the movement caller; this only guarantees the
        /// point itself is walkable, so no obstacle is shrunk to make it pass.
        /// </summary>
        static Vector3 NavigationApproach(Stage4SeatEntry entry, Vector3 from)
        {
            Vector3 approach;
            string reason;
            if (Stage4SeatData.ResolveApproach(entry, from, false, out approach, out reason))
                return approach;
            return entry.ApproachPoint;
        }
    }
}
