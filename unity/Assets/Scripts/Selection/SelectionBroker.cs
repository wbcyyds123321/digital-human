using System;
using System.Collections.Generic;
using UnityEngine;

namespace Yixuan.Pick
{
    /// <summary>
    /// Single shared confirmed-selection record. Candidates are never stored
    /// here as confirmed selections. Consumers (highlight, big map, minimap,
    /// LLM facts, task facts, later Lua executor) all read Current.
    ///
    /// Generation rule: every new intent (manual confirm, AI localization plan,
    /// replacement target, cancel) moves Generation forward. A result that was
    /// produced for an older generation can never publish into Current.
    /// </summary>
    public static class SelectionBroker
    {
        public static SelectionRecord Current { get; private set; }
        /// <summary>Monotonic version, bumped on every confirmed/cancelled change.</summary>
        public static int Version { get; private set; }
        /// <summary>Current intent generation. Late results from older generations are rejected.</summary>
        public static int Generation { get; private set; }
        public static string LastRejectReason { get; private set; } = "";

        /// <summary>Candidate set from the latest un-confirmed observation (never authoritative).</summary>
        public static PickBoxResult PendingCandidates { get; private set; }
        /// <summary>Generation the pending candidates belong to (stale candidates are dropped).</summary>
        public static int PendingCandidatesGeneration { get; private set; } = -1;

        public static event Action<SelectionRecord> Confirmed;
        public static event Action Cleared;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetSession()
        {
            Current = null;
            Version = 0;
            Generation = 0;
            PendingCandidates = null;
            PendingCandidatesGeneration = -1;
            LastRejectReason = "";
            Confirmed = null;
            Cleared = null;
        }

        /// <summary>Start a new intent (task/select request). Returns its generation.</summary>
        public static int BeginIntent()
        {
            Generation++;
            PendingCandidates = null;
            PendingCandidatesGeneration = -1;
            LastRejectReason = "";
            return Generation;
        }

        /// <summary>
        /// Begin an explicit manual/map selection. A new user selection must
        /// invalidate any older pending AI token so a late AI result cannot
        /// overwrite the user's choice.
        /// </summary>
        public static int BeginManualSelection()
        {
            return BeginIntent();
        }

        /// <summary>Cancel the pending intent; old results become stale immediately.</summary>
        public static void CancelIntent()
        {
            Generation++;
            PendingCandidates = null;
            PendingCandidatesGeneration = -1;
            LastRejectReason = "cancelled";
        }

        /// <summary>Bind a candidate observation to the generation that produced it.</summary>
        public static bool StoreCandidates(int generation, PickBoxResult candidates)
        {
            if (candidates == null || generation != Generation)
            {
                LastRejectReason = "stale_or_empty_candidates";
                return false;
            }
            candidates.generation = generation;
            candidates.dominant_is_confirmed = false;
            PendingCandidates = candidates;
            PendingCandidatesGeneration = generation;
            return true;
        }

        public static void ClearCandidates()
        {
            PendingCandidates = null;
            PendingCandidatesGeneration = -1;
        }

        /// <summary>Clear the confirmed selection, advance the generation, and drop candidates bound to the old generation.</summary>
        public static void Clear()
        {
            Generation++;
            Version++;
            Current = null;
            PendingCandidates = null;
            PendingCandidatesGeneration = -1;
            LastRejectReason = "cleared";
            var handler = Cleared;
            if (handler != null) handler();
        }

        /// <summary>
        /// Publish a confirmed selection only when the caller's generation is
        /// still current. Returns false for stale results; the record is not
        /// installed and no event fires.
        /// </summary>
        public static bool TryPublish(int generation, SelectionRecord record)
        {
            if (record == null || string.IsNullOrEmpty(record.entity_id))
            {
                LastRejectReason = "empty_record";
                return false;
            }
            if (generation >= 0 && generation != Generation)
            {
                LastRejectReason = "stale_generation result=" + generation + " current=" + Generation;
                return false;
            }
            record.generation = generation;
            record.confirmed = true;
            Current = record;
            Version++;
            LastRejectReason = "";
            PendingCandidates = null;
            PendingCandidatesGeneration = -1;
            var handler = Confirmed;
            if (handler != null) handler(record);
            return true;
        }

        /// <summary>Publish against the current generation (manual click / map click).</summary>
        public static bool Publish(SelectionRecord record)
        {
            return TryPublish(Generation, record);
        }

        public static bool IsCurrent(string entityId)
        {
            return Current != null && Current.confirmed && Current.entity_id == entityId;
        }
    }
}
