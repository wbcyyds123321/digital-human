using System;
using System.Collections.Generic;
using UnityEngine;

namespace Yixuan.AI.Tasks
{
    /// <summary>
    /// One task-scoped, verified visual target binding. The handle string is only
    /// an index into this mission's ledger; it is never trusted by itself.
    /// </summary>
    [Serializable]
    public sealed class TaskTargetBinding
    {
        public string slot = "";
        public string target_ref = "";
        public string entity_id = "", entity_name = "", cls = "", capabilities = "";
        public string description = "", source = "";
        public string mission_id = "", scene_version = "";
        public int mission_generation = -1;
        /// <summary>SelectionBroker.Generation this binding published under.</summary>
        public int broker_generation = -1;
        public long locate_frame_id = -1, confirm_frame_id = -1;
        public string observation_group_id = "", observation_mode = "";
        public double confirmed_at;
        public Vector3 observed_position, surface_point;
        /// <summary>Seeded from the pre-task confirmed-selection handoff (B lane).</summary>
        public bool forced_baseline;
        public bool valid = true;
        public string invalidation_reason = "";
        public int use_count;
    }

    [Serializable]
    public sealed class TaskSelectionLedgerDto
    {
        public string schema = "task-selection-ledger.v1";
        public string mission_id = "", scene_version = "";
        public int mission_generation = -1;
        public TaskTargetBinding[] bindings = new TaskTargetBinding[0];
    }

    /// <summary>
    /// Task-scoped verified target handles, deliberately separate from the single
    /// global UI SelectionBroker generation:
    ///  - a legal in-task selection of B binds its own slot and KEEPS A;
    ///  - the global UI still shows one (single) selection;
    ///  - an external selection/intent change, scene change, entity destruction or
    ///    task end invalidates these handles;
    ///  - a handle is unforgeable: resolution only looks into this mission's
    ///    in-memory ledger, so a model-written id/handle can never be used as a
    ///    visual binding.
    /// </summary>
    public sealed class TaskSelectionLedger
    {
        public const string HandlePrefix = "tref/";
        readonly Dictionary<string, TaskTargetBinding> _bySlot = new Dictionary<string, TaskTargetBinding>(StringComparer.Ordinal);
        readonly Dictionary<string, TaskTargetBinding> _byRef = new Dictionary<string, TaskTargetBinding>(StringComparer.Ordinal);
        readonly List<TaskTargetBinding> _order = new List<TaskTargetBinding>();
        readonly HashSet<int> _ownedBrokerGenerations = new HashSet<int>();
        readonly HashSet<int> _seenBrokerGenerations = new HashSet<int>();

        public string MissionId = "";
        public string SceneVersion = "";
        public int MissionGeneration = -1;
        public bool Invalidated { get; private set; }
        public string InvalidationReason = "";
        int _sequence;

        public IList<TaskTargetBinding> Bindings { get { return _order; } }
        public int Count { get { return _order.Count; } }

        public TaskSelectionLedger(int missionGeneration, string missionId, string sceneVersion)
        {
            MissionGeneration = missionGeneration;
            MissionId = missionId ?? "";
            SceneVersion = sceneVersion ?? "";
        }

        public void RegisterOwnedBrokerGeneration(int generation)
        {
            if (generation >= 0) _ownedBrokerGenerations.Add(generation);
            _seenBrokerGenerations.Add(generation);
        }

        /// <summary>True only for a broker generation produced by this task itself.</summary>
        public bool IsOwnedBrokerGeneration(int generation)
        {
            return _ownedBrokerGenerations.Contains(generation);
        }

        public bool IsKnownBrokerGeneration(int generation)
        {
            return _seenBrokerGenerations.Contains(generation) || _ownedBrokerGenerations.Contains(generation);
        }

        public static bool LooksLikeHandle(string value)
        {
            return !string.IsNullOrEmpty(value) && value.StartsWith(HandlePrefix, StringComparison.Ordinal);
        }

        /// <summary>
        /// Seed the immutable pre-task visual handoff (B lane: user confirmed this
        /// entity before the task started). It is not re-selected inside the task
        /// and it survives a later in-task selection of another slot.
        /// </summary>
        public bool TrySeedForcedBaseline(string entityId, string action)
        {
            if (string.IsNullOrEmpty(entityId)) return false;
            var current = Yixuan.Pick.SelectionBroker.Current;
            if (current == null || !current.confirmed || current.entity_id != entityId) return false;
            var binding = new TaskTargetBinding
            {
                slot = "",
                target_ref = "",
                entity_id = current.entity_id,
                entity_name = current.display_name,
                cls = current.cls,
                capabilities = current.capabilities,
                description = "confirmed_selection_handoff",
                source = current.source,
                mission_id = MissionId,
                scene_version = SceneVersion,
                mission_generation = MissionGeneration,
                broker_generation = current.generation,
                locate_frame_id = current.frame_id,
                confirm_frame_id = current.frame_id,
                confirmed_at = current.time_since_startup,
                observed_position = current.entity_position,
                surface_point = current.surface_point,
                forced_baseline = true,
                valid = true,
            };
            Add(binding);
            if (current.generation >= 0) RegisterOwnedBrokerGeneration(current.generation);
            return true;
        }

        string BuildHandle(string slot)
        {
            _sequence++;
            string mission = MissionId.Length > 8 ? MissionId.Substring(0, 8) : MissionId;
            return HandlePrefix + mission + "/" + MissionGeneration + "/" + (_sequence.ToString("00")) + "/" + slot;
        }

        /// <summary>
        /// Bind one declared slot to a REAL confirmed selection record. The entity
        /// id comes only from the production visual confirmation, never from the
        /// contract or the model-authored description.
        /// </summary>
        public bool TryBindSlot(string slot, string description, string source, Yixuan.Pick.SelectionRecord record,
            int brokerGeneration, long locateFrameId, out TaskTargetBinding binding, out string error)
        {
            binding = null;
            error = "";
            if (Invalidated) { error = "selection_ledger_invalidated: " + InvalidationReason; return false; }
            if (string.IsNullOrEmpty(slot)) { error = "target_slot_required"; return false; }
            if (_bySlot.ContainsKey(slot)) { error = "target_slot_already_bound:" + slot; return false; }
            if (record == null || !record.confirmed || string.IsNullOrEmpty(record.entity_id))
            { error = "selection_not_confirmed"; return false; }
            var entry = Yixuan.Pick.EntityPickRegistry.Get(record.entity_id);
            if (entry == null || !entry.Selectable)
            { error = "confirmed_entity_not_live_selectable:" + record.entity_id; return false; }
            if (brokerGeneration >= 0)
            {
                if (record.generation != brokerGeneration) { error = "selection_generation_mismatch"; return false; }
                RegisterOwnedBrokerGeneration(brokerGeneration);
            }
            binding = new TaskTargetBinding
            {
                slot = slot,
                target_ref = BuildHandle(slot),
                entity_id = record.entity_id,
                entity_name = record.display_name,
                cls = record.cls,
                capabilities = record.capabilities,
                description = description ?? "",
                source = source ?? "",
                mission_id = MissionId,
                scene_version = SceneVersion,
                mission_generation = MissionGeneration,
                broker_generation = brokerGeneration,
                locate_frame_id = locateFrameId,
                confirm_frame_id = record.frame_id,
                confirmed_at = record.time_since_startup,
                observed_position = record.entity_position,
                surface_point = record.surface_point,
                forced_baseline = false,
                valid = true,
            };
            Add(binding);
            return true;
        }

        void Add(TaskTargetBinding binding)
        {
            _order.Add(binding);
            if (!string.IsNullOrEmpty(binding.slot)) _bySlot[binding.slot] = binding;
            if (!string.IsNullOrEmpty(binding.target_ref)) _byRef[binding.target_ref] = binding;
        }

        public TaskTargetBinding GetBySlot(string slot)
        {
            TaskTargetBinding binding;
            return !string.IsNullOrEmpty(slot) && _bySlot.TryGetValue(slot, out binding) ? binding : null;
        }

        public bool IsSlotBound(string slot) { return GetBySlot(slot) != null; }

        public bool TryGetForcedBaseline(out TaskTargetBinding binding)
        {
            binding = null;
            foreach (var candidate in _order) if (candidate != null && candidate.forced_baseline && candidate.valid) { binding = candidate; return true; }
            return false;
        }

        /// <summary>
        /// Resolve an opaque target handle for use. Fails for unbound, forged,
        /// cross-task, scene-changed, entity-destroyed and task-ended handles.
        /// </summary>
        public bool TryResolveHandle(string reference, out TaskTargetBinding binding, out string error)
        {
            binding = null;
            error = "";
            if (!LooksLikeHandle(reference)) { error = "invalid_target_handle"; return false; }
            if (!_byRef.TryGetValue(reference, out binding))
            { error = "target_handle_not_bound_in_this_task"; return false; }
            if (!binding.valid) { error = "target_handle_expired: " + binding.invalidation_reason; return false; }
            if (Invalidated) { error = "target_handle_expired: " + InvalidationReason; return false; }
            if (binding.mission_generation != MissionGeneration || binding.mission_id != MissionId)
            { error = "target_handle_wrong_task"; return false; }
            string liveScene = CurrentSceneVersion();
            if (binding.scene_version != SceneVersion || liveScene != SceneVersion)
            {
                binding.valid = false; binding.invalidation_reason = "scene_changed";
                error = "target_handle_scene_changed"; return false;
            }
            var entry = Yixuan.Pick.EntityPickRegistry.Get(binding.entity_id);
            if (entry == null || !entry.Selectable)
            {
                binding.valid = false; binding.invalidation_reason = "entity_destroyed";
                error = "target_handle_entity_destroyed:" + binding.entity_id; return false;
            }
            binding.use_count++;
            return true;
        }

        public static string CurrentSceneVersion()
        {
            var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            return scene.path + "@" + scene.handle;
        }

        /// <summary>Cheap periodic liveness: scene identity + every bound entity still selectable.</summary>
        public bool ValidateAllLive(out string reason)
        {
            reason = "";
            if (Invalidated) { reason = InvalidationReason; return false; }
            if (CurrentSceneVersion() != SceneVersion)
            {
                InvalidateAll("scene_changed");
                reason = "scene_changed";
                return false;
            }
            foreach (var binding in _order)
            {
                if (binding == null || !binding.valid) continue;
                var entry = Yixuan.Pick.EntityPickRegistry.Get(binding.entity_id);
                if (entry == null || !entry.Selectable)
                {
                    binding.valid = false;
                    binding.invalidation_reason = "entity_destroyed";
                    reason = "entity_destroyed:" + binding.entity_id;
                    return false;
                }
            }
            return true;
        }

        public void InvalidateAll(string reason)
        {
            if (Invalidated) return;
            Invalidated = true;
            InvalidationReason = string.IsNullOrEmpty(reason) ? "invalidated" : reason;
            foreach (var binding in _order)
            {
                if (binding == null || !binding.valid) continue;
                binding.valid = false;
                binding.invalidation_reason = InvalidationReason;
            }
        }

        public string DescribeJson()
        {
            return JsonUtility.ToJson(new TaskSelectionLedgerDto
            {
                mission_id = MissionId,
                scene_version = SceneVersion,
                mission_generation = MissionGeneration,
                bindings = _order.ToArray(),
            }, true);
        }

        public string Describe()
        {
            if (_order.Count == 0) return "no_target_bindings";
            var parts = new List<string>();
            foreach (var binding in _order)
            {
                parts.Add((string.IsNullOrEmpty(binding.slot) ? "forced" : binding.slot) + "=" + binding.entity_id +
                          "(" + (binding.valid ? "valid" : "invalid:" + binding.invalidation_reason) + ")");
            }
            return string.Join("; ", parts.ToArray());
        }
    }
}
