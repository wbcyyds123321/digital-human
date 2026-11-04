using System.Collections.Generic;
using UnityEngine;
using ADBRuntime;
using ADBRuntime.Mono;

namespace Yixuan.Motion
{
    [DisallowMultipleComponent]
    public class ZhuangfyHairDynamics : MonoBehaviour
    {
        public bool Enabled = false;
        public bool AutoBuildOnEnable = true;
        public string ChainKeyword = "hairpilot";
        public string LeftBonePrefix = "hair_base_L_b__jnt";
        public string RightBonePrefix = "hair_base_R_b__jnt";
        public int MaxPointsPerSide = 21;
        public bool CreateHeadCollider = true;
        public bool CreateUpperChestCollider = true;
        public float HeadColliderRadius = 0.1f;
        public float UpperChestColliderRadius = 0.12f;
        public bool VerboseLog = false;
        public bool EnableColliders = false;
        public bool UseLongChainPilot = false;
        public string ShortLeftBonePrefix = "hair_base_L_a_";
        public string ShortRightBonePrefix = "hair_base_R_a_";
        public string ShortBoneSuffix = "_jnt";
        public int ShortBoneIndexDigits = 2;
        public int ShortMaxPointsPerSide = 4;
        public int PhysicsIteration = 2;
        public string BoneNameSuffix = "";
        public int BoneIndexDigits = 0;

        readonly List<Transform> _simulatedBones = new List<Transform>();
        readonly List<Vector3> _restPositions = new List<Vector3>();
        readonly List<Quaternion> _restRotations = new List<Quaternion>();
        readonly List<Vector3> _restScales = new List<Vector3>();
        readonly List<ADBChainProcessor> _chains = new List<ADBChainProcessor>();
        readonly List<ADBRuntimePoint> _points = new List<ADBRuntimePoint>();
        readonly List<GameObject> _colliderObjects = new List<GameObject>();
        ADBRuntimeController _controller;
        ADBPhysicsSetting _setting;
        bool _appliedEnabled = true;
        bool _restoreScheduled;
        bool _rebuildScheduled;
        int _cleanupReadyFrame = -1;
        bool _nanDetected;
        bool _nanReported;

        public bool IsBuilt => _controller != null;
        public int SimulatedBoneCount => _simulatedBones.Count;
        public int WeightedHairBoneCount { get; private set; }
        public int ChainCount => _chains.Count;
        public int PointCount => _controller != null ? _controller.GetPointCount() : 0;
        public Transform TipBone { get; private set; }
        public string BuildSummary { get; private set; } = "";
        public float LastRestorePosError { get; private set; } = -1f;
        public float LastRestoreRotError { get; private set; } = -1f;

        void Awake()
        {
            _appliedEnabled = Enabled;
            if (Enabled && AutoBuildOnEnable) Build();
        }

        void OnEnable()
        {
            _appliedEnabled = Enabled;
            if (Application.isPlaying && Enabled && AutoBuildOnEnable && !IsBuilt) _rebuildScheduled = true;
        }

        void OnDisable()
        {
            _rebuildScheduled = false;
            Teardown(true);
        }

        void OnDestroy()
        {
            _rebuildScheduled = false;
            Teardown(true);
        }

        void OnValidate()
        {
            if (!Application.isPlaying) return;
            if (Enabled == _appliedEnabled) return;
            _appliedEnabled = Enabled;
            if (Enabled) _rebuildScheduled = true;
            else
            {
                _rebuildScheduled = false;
                Teardown(true);
            }
        }

        void Update()
        {
            if (!Application.isPlaying) return;
            if (_cleanupReadyFrame >= 0 && Time.frameCount < _cleanupReadyFrame) return;
            if (IsBuilt && !_nanDetected && HasNonFiniteBones())
            {
                _nanDetected = true;
                if (!_nanReported)
                {
                    _nanReported = true;
                    Debug.LogError("[ZhuangfyHairDynamics] 检测到 NaN/Inf，已停止模拟并恢复 local pose");
                }
                Teardown(true);
                BuildSummary = "non-finite simulation detected; stopped";
                return;
            }
            if (_restoreScheduled && _controller == null)
            {
                RestoreRestPose();
                _restoreScheduled = false;
                ClearSimulationState();
            }
            if (_rebuildScheduled && _controller == null)
            {
                _rebuildScheduled = false;
                if (Enabled && !_nanDetected) Build();
            }
        }

        public void SetEnabled(bool value)
        {
            Enabled = value;
            _appliedEnabled = value;
            if (!Application.isPlaying) return;
            if (value)
            {
                if (!IsBuilt && !_nanDetected) _rebuildScheduled = true;
            }
            else
            {
                _rebuildScheduled = false;
                Teardown(true);
            }
        }

        public void Rebuild()
        {
            if (!Application.isPlaying)
            {
                BuildSummary = "edit-mode build skipped";
                return;
            }
            _nanDetected = false;
            _nanReported = false;
            _rebuildScheduled = false;
            Teardown(true);
            if (Enabled) _rebuildScheduled = true;
        }

        public void ResetPilot()
        {
            if (!Application.isPlaying)
            {
                Debug.LogWarning("[ZhuangfyHairDynamics] 请在 Play 模式下重置");
                return;
            }
            _nanDetected = false;
            _nanReported = false;
            Teardown(true);
            if (Enabled) _rebuildScheduled = true;
        }

        public void Build()
        {
            if (IsBuilt) return;
            if (!Application.isPlaying)
            {
                BuildSummary = "edit-mode build skipped";
                return;
            }
            if (_cleanupReadyFrame >= 0 && Time.frameCount < _cleanupReadyFrame)
            {
                _rebuildScheduled = true;
                return;
            }
            _cleanupReadyFrame = -1;
            _restoreScheduled = false;
            _rebuildScheduled = false;
            RestoreRestPose();
            ClearSimulationState();
            var weightedBones = BuildWeightedBoneSet();
            if (weightedBones == null)
            {
                BuildSummary = "skinned mesh bone weights unavailable";
                if (VerboseLog) Debug.LogWarning("[ZhuangfyHairDynamics] " + BuildSummary);
                return;
            }
            bool useLongChain = UseLongChainPilot;
            string pilotLeftPrefix = useLongChain ? LeftBonePrefix : (string.IsNullOrEmpty(ShortLeftBonePrefix) ? "hair_base_L_a_" : ShortLeftBonePrefix);
            string pilotRightPrefix = useLongChain ? RightBonePrefix : (string.IsNullOrEmpty(ShortRightBonePrefix) ? "hair_base_R_a_" : ShortRightBonePrefix);
            string pilotSuffix = useLongChain ? (BoneNameSuffix ?? "") : (ShortBoneSuffix ?? "_jnt");
            int pilotDigits = useLongChain ? BoneIndexDigits : (ShortBoneIndexDigits > 0 ? ShortBoneIndexDigits : 2);
            int pilotMaxPoints = useLongChain ? MaxPointsPerSide : (ShortMaxPointsPerSide > 1 ? ShortMaxPointsPerSide : 4);
            CollectSide(pilotLeftPrefix, weightedBones, _leftBones, pilotMaxPoints, pilotSuffix, pilotDigits);
            CollectSide(pilotRightPrefix, weightedBones, _rightBones, pilotMaxPoints, pilotSuffix, pilotDigits);
            if (_leftBones.Count < 2 || _rightBones.Count < 2)
            {
                BuildSummary = "insufficient hair bones left=" + _leftBones.Count + " right=" + _rightBones.Count;
                if (VerboseLog) Debug.LogWarning("[ZhuangfyHairDynamics] " + BuildSummary);
                return;
            }
            _simulatedBones.Clear();
            _simulatedBones.AddRange(_leftBones);
            _simulatedBones.AddRange(_rightBones);
            SnapshotRestPose();
            WeightedHairBoneCount = CountWeighted(weightedBones);
            _setting = Resources.Load<ADBPhysicsSetting>("ADBHairPilotSetting");
            if (_setting != null) _setting = Instantiate(_setting);
            if (_setting == null)
            {
                _setting = ScriptableObject.CreateInstance<ADBPhysicsSetting>();
                ApplyFallbackSetting(_setting);
            }
            ApplyConservativeSetting(_setting);
            Transform leftParent = _leftBones[0].parent;
            Transform rightParent = _rightBones[0].parent;
            if (leftParent != null && leftParent == rightParent)
            {
                var sharedChain = CreateChain(leftParent);
                AddSide(sharedChain, _leftBones);
                AddSide(sharedChain, _rightBones);
            }
            else
            {
                AddSide(CreateChain(leftParent), _leftBones);
                AddSide(CreateChain(rightParent), _rightBones);
            }
            _controller = gameObject.AddComponent<ADBRuntimeController>();
            _controller.updateMode = ADBRuntime.Mono.UpdateMode.LateUpdate;
            _controller.colliderCollisionType = ColliderCollisionType.Constraint;
            _controller.iteration = Mathf.Clamp(PhysicsIteration, 1, 16);
            _controller.bufferTime = 0.02f;
            _controller.isDrawGizmo = false;
            if (EnableColliders && CreateHeadCollider) AddBoneCollider("Head", HeadColliderRadius, ColliderChoice.Head, "ADB_HeadCollider");
            if (EnableColliders && CreateUpperChestCollider) AddBoneCollider("UpperBody2", UpperChestColliderRadius, ColliderChoice.UpperBody, "ADB_UpperChestCollider");
            TipBone = _simulatedBones[_simulatedBones.Count - 1];
            BuildSummary = (useLongChain ? "long" : "short") + " chains=" + _chains.Count + " bones=" + _simulatedBones.Count + " weighted=" + WeightedHairBoneCount + " points=" + _points.Count;
            if (VerboseLog) Debug.Log("[ZhuangfyHairDynamics] built " + BuildSummary);
        }

        readonly List<Transform> _leftBones = new List<Transform>();
        readonly List<Transform> _rightBones = new List<Transform>();

        ADBChainProcessor CreateChain(Transform parent)
        {
            var chainParent = parent != null ? parent : transform;
            var chain = ADBChainProcessor.CreateADBChainProcessor(chainParent, ChainKeyword, _setting);
            _chains.Add(chain);
            return chain;
        }

        void AddSide(ADBChainProcessor chain, List<Transform> bones)
        {
            ADBRuntimePoint previousPoint = null;
            for (int pointIndex = 0; pointIndex < bones.Count; pointIndex++)
            {
                var point = ADBRuntimePoint.CreateRuntimePoint(bones[pointIndex], pointIndex, ChainKeyword, _setting.isAllowComputeOtherConstraint);
                _points.Add(point);
                if (previousPoint == null) chain.AddChild(point);
                else previousPoint.AddChild(point);
                previousPoint = point;
            }
        }

        HashSet<string> BuildWeightedBoneSet()
        {
            var renderer = GetComponentInChildren<SkinnedMeshRenderer>(true);
            if (renderer == null || renderer.sharedMesh == null || renderer.bones == null) return null;
            var counts = new Dictionary<string, int>();
            var boneWeights = renderer.sharedMesh.boneWeights;
            for (int weightIndex = 0; weightIndex < boneWeights.Length; weightIndex++)
            {
                CountBoneWeight(renderer, boneWeights[weightIndex].boneIndex0, boneWeights[weightIndex].weight0, counts);
                CountBoneWeight(renderer, boneWeights[weightIndex].boneIndex1, boneWeights[weightIndex].weight1, counts);
                CountBoneWeight(renderer, boneWeights[weightIndex].boneIndex2, boneWeights[weightIndex].weight2, counts);
                CountBoneWeight(renderer, boneWeights[weightIndex].boneIndex3, boneWeights[weightIndex].weight3, counts);
            }
            return new HashSet<string>(counts.Keys);
        }

        void CountBoneWeight(SkinnedMeshRenderer renderer, int boneIndex, float weight, Dictionary<string, int> counts)
        {
            if (weight <= 0.0001f || boneIndex < 0 || boneIndex >= renderer.bones.Length || renderer.bones[boneIndex] == null) return;
            var boneName = renderer.bones[boneIndex].name;
            counts[boneName] = counts.TryGetValue(boneName, out var existingCount) ? existingCount + 1 : 1;
        }

        int CountWeighted(HashSet<string> weightedBones)
        {
            int weightedCount = 0;
            for (int boneIndex = 0; boneIndex < _simulatedBones.Count; boneIndex++)
            {
                if (_simulatedBones[boneIndex] != null && weightedBones.Contains(_simulatedBones[boneIndex].name)) weightedCount++;
            }
            return weightedCount;
        }

        void CollectSide(string prefix, HashSet<string> weightedBones, List<Transform> destination, int maxPoints, string suffix, int indexDigits)
        {
            destination.Clear();
            if (weightedBones == null) return;
            var boneIndexMap = Yixuan.Model.MmdBoneNames.BuildIndex(transform);
            if (!boneIndexMap.TryGetValue(BoneName(prefix, 1, suffix, indexDigits), out var currentBone) || currentBone == null) return;
            var collectedBones = new List<Transform>();
            collectedBones.Add(currentBone);
            for (int pointIndex = 2; pointIndex <= maxPoints; pointIndex++)
            {
                Transform nextBone = null;
                for (int childIndex = 0; childIndex < currentBone.childCount; childIndex++)
                {
                    var child = currentBone.GetChild(childIndex);
                    if (child.name == BoneName(prefix, pointIndex, suffix, indexDigits))
                    {
                        nextBone = child;
                        break;
                    }
                }
                if (nextBone == null) break;
                collectedBones.Add(nextBone);
                currentBone = nextBone;
            }
            int lastWeightedIndex = -1;
            for (int collectedIndex = 0; collectedIndex < collectedBones.Count; collectedIndex++)
                if (weightedBones.Contains(collectedBones[collectedIndex].name)) lastWeightedIndex = collectedIndex;
            if (lastWeightedIndex < 0) return;
            for (int collectedIndex = 0; collectedIndex <= lastWeightedIndex; collectedIndex++)
                destination.Add(collectedBones[collectedIndex]);
        }

        static string BoneName(string prefix, int boneIndex, string suffix, int indexDigits)
        {
            string numberText = indexDigits > 0 ? boneIndex.ToString("D" + indexDigits) : boneIndex.ToString();
            return prefix + numberText + suffix;
        }

        bool HasNonFiniteBones()
        {
            for (int boneIndex = 0; boneIndex < _simulatedBones.Count; boneIndex++)
            {
                var bone = _simulatedBones[boneIndex];
                if (bone == null) continue;
                var position = bone.position;
                if (float.IsNaN(position.x) || float.IsNaN(position.y) || float.IsNaN(position.z)) return true;
                if (float.IsInfinity(position.x) || float.IsInfinity(position.y) || float.IsInfinity(position.z)) return true;
            }
            return false;
        }

        void AddBoneCollider(string semantic, float radius, ColliderChoice choice, string objectName)
        {
            var bone = Yixuan.Model.MmdBoneNames.Resolve(semantic, transform);
            if (bone == null) return;
            var colliderObject = new GameObject(objectName);
            colliderObject.transform.SetParent(bone, false);
            colliderObject.transform.localPosition = Vector3.zero;
            colliderObject.transform.localRotation = Quaternion.identity;
            colliderObject.transform.localScale = Vector3.one;
            var sphere = colliderObject.AddComponent<SphereCollider>();
            sphere.isTrigger = true;
            sphere.center = Vector3.zero;
            sphere.radius = radius;
            var reader = colliderObject.AddComponent<ADBColliderReader>();
            reader.unityCollider = sphere;
            reader.colliderMask = choice;
            reader.collideFunc = CollideFunc.OutsideLimit;
            reader.isReadOnly = false;
            reader.isStatic = false;
            _colliderObjects.Add(colliderObject);
        }

        static void ApplyFallbackSetting(ADBPhysicsSetting setting)
        {
            setting.frictionValue = 0f;
            setting.gravityScaleValue = 0.05f;
            setting.dampingValue = 0.9f;
            setting.stiffnessLocalValue = 0.5f;
            setting.lengthLimitForceScaleValue = 1f;
            setting.elasticityVelocityValue = 0.05f;
            setting.elasticityValue = 0.05f;
            setting.velocityIncreaseValue = 0f;
            setting.moveInertValue = 0f;
            setting.isComputeVirtual = true;
            setting.isAllowComputeOtherConstraint = false;
            setting.isComputeStructuralVertical = true;
            setting.isComputeStructuralHorizontal = false;
            setting.isComputeBendingVertical = true;
            setting.isComputeBendingHorizontal = false;
            setting.isComputeShear = false;
            setting.isComputeCircumference = false;
            setting.isCollideStructuralVertical = true;
            setting.isCollideStructuralHorizontal = true;
            setting.isLoopRootPoints = true;
            setting.isAutoComputeWeight = true;
            setting.gravity = new Vector3(0f, -9.81f, 0f);
            setting.colliderChoice = (ColliderChoice)(-1);
        }

        static void ApplyConservativeSetting(ADBPhysicsSetting setting)
        {
            if (setting == null) return;
            setting.dampingValue = 0.9f;
            setting.elasticityValue = 0.05f;
            setting.elasticityVelocityValue = 0.05f;
            setting.velocityIncreaseValue = 0f;
            setting.moveInertValue = 0f;
        }

        void SnapshotRestPose()
        {
            _restPositions.Clear();
            _restRotations.Clear();
            _restScales.Clear();
            for (int boneIndex = 0; boneIndex < _simulatedBones.Count; boneIndex++)
            {
                _restPositions.Add(_simulatedBones[boneIndex].localPosition);
                _restRotations.Add(_simulatedBones[boneIndex].localRotation);
                _restScales.Add(_simulatedBones[boneIndex].localScale);
            }
        }

        void RestoreRestPose()
        {
            for (int boneIndex = 0; boneIndex < _simulatedBones.Count && boneIndex < _restPositions.Count; boneIndex++)
            {
                if (_simulatedBones[boneIndex] == null) continue;
                _simulatedBones[boneIndex].localPosition = _restPositions[boneIndex];
                _simulatedBones[boneIndex].localRotation = _restRotations[boneIndex];
                _simulatedBones[boneIndex].localScale = _restScales[boneIndex];
            }
        }

        public float RestPoseMaxError(out float maxRotationDegrees)
        {
            maxRotationDegrees = 0f;
            float maxPositionDelta = 0f;
            for (int boneIndex = 0; boneIndex < _simulatedBones.Count && boneIndex < _restPositions.Count; boneIndex++)
            {
                if (_simulatedBones[boneIndex] == null) continue;
                float positionDelta = (_simulatedBones[boneIndex].localPosition - _restPositions[boneIndex]).magnitude;
                if (positionDelta > maxPositionDelta) maxPositionDelta = positionDelta;
                float rotationDelta = Quaternion.Angle(_simulatedBones[boneIndex].localRotation, _restRotations[boneIndex]);
                if (rotationDelta > maxRotationDegrees) maxRotationDegrees = rotationDelta;
            }
            return maxPositionDelta;
        }

        void CleanupVirtualPoints()
        {
            for (int boneIndex = 0; boneIndex < _simulatedBones.Count; boneIndex++)
            {
                var bone = _simulatedBones[boneIndex];
                if (bone == null) continue;
                for (int childIndex = bone.childCount - 1; childIndex >= 0; childIndex--)
                {
                    var child = bone.GetChild(childIndex);
                    if (child.name.EndsWith(" virtual")) DestroyOwnedObject(child.gameObject);
                }
            }
        }

        void Teardown(bool restore)
        {
            _cleanupReadyFrame = Application.isPlaying ? Time.frameCount + 1 : -1;
            if (_controller != null)
            {
                _controller.enabled = false;
                DestroyOwnedObject(_controller);
                _controller = null;
            }
            if (_setting != null)
            {
                DestroyOwnedObject(_setting);
                _setting = null;
            }
            for (int chainIndex = 0; chainIndex < _chains.Count; chainIndex++) DestroyOwnedObject(_chains[chainIndex]);
            for (int pointIndex = 0; pointIndex < _points.Count; pointIndex++) DestroyOwnedObject(_points[pointIndex]);
            for (int colliderIndex = 0; colliderIndex < _colliderObjects.Count; colliderIndex++) DestroyOwnedObject(_colliderObjects[colliderIndex]);
            CleanupVirtualPoints();
            _chains.Clear();
            _points.Clear();
            _colliderObjects.Clear();
            if (restore)
            {
                RestoreRestPose();
                LastRestorePosError = RestPoseMaxError(out var rotationDegrees);
                LastRestoreRotError = rotationDegrees;
                _restoreScheduled = true;
            }
            else
            {
                ClearSimulationState();
            }
        }

        void ClearSimulationState()
        {
            _leftBones.Clear();
            _rightBones.Clear();
            _simulatedBones.Clear();
            _restPositions.Clear();
            _restRotations.Clear();
            _restScales.Clear();
            _chains.Clear();
            _points.Clear();
            _colliderObjects.Clear();
            TipBone = null;
            WeightedHairBoneCount = 0;
            BuildSummary = "";
        }

        static void DestroyOwnedObject(Object target)
        {
            if (target == null) return;
            if (Application.isPlaying) Object.Destroy(target);
            else Object.DestroyImmediate(target);
        }

        [ContextMenu("ADB Hair/Rebuild")]
        void ContextRebuild()
        {
            Rebuild();
        }

        [ContextMenu("ADB Hair/Reset")]
        void ContextReset()
        {
            ResetPilot();
        }
    }
}
