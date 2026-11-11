// Node-2 dynamic world: vehicle/tram/flame runtimes, current transforms, dynamic obstacles,
// math picking and the vehicle camera. No Collider/Rigidbody/CharacterController are added.
using System.Collections.Generic;
using UnityEngine;
using Yixuan.Motion;
using Yixuan.World;
using Yixuan.WorldMap;
using Yixuan.UI;

namespace Yixuan.Dynamic
{
    [System.Serializable]
    public class VehicleRuntime
    {
        public DynVehicle Spec;
        public Transform Node;
        public Vector3 Pivot0;
        public Vector3 CurrentPivot;
        public float Yaw0;
        public float CurrentYaw;
        public List<PickMesh> PickMeshes = new List<PickMesh>();
        public Bounds LocalPickBounds;
        public bool PickReady;

        public Vector3 Forward
        {
            get { return Quaternion.Euler(0f, CurrentYaw, 0f) * Vector3.forward; }
        }

        public Vector3 Right
        {
            get { return Quaternion.Euler(0f, CurrentYaw, 0f) * Vector3.right; }
        }

        public Vector3 Center
        {
            get { return CenterAt(CurrentPivot, CurrentYaw); }
        }

        public Vector3 CenterAt(Vector3 pivot, float yaw)
        {
            float ox = (Spec.bounds[0] + Spec.bounds[2]) * 0.5f - Pivot0.x;
            float oz = (Spec.bounds[1] + Spec.bounds[3]) * 0.5f - Pivot0.z;
            return pivot + Quaternion.Euler(0f, yaw - Yaw0, 0f) * new Vector3(ox, 0f, oz);
        }

        /// <summary>Axis aligned world bounds of the rotated body (ground footprint).</summary>
        public Bounds Footprint
        {
            get
            {
                float radians = CurrentYaw * Mathf.Deg2Rad;
                float c = Mathf.Abs(Mathf.Cos(radians));
                float s = Mathf.Abs(Mathf.Sin(radians));
                // length runs along forward, width along right: world X extent uses sin/cos of yaw
                float halfX = s * Spec.length_m * 0.5f + c * Spec.width_m * 0.5f;
                float halfZ = c * Spec.length_m * 0.5f + s * Spec.width_m * 0.5f;
                Vector3 center = Center;
                return new Bounds(new Vector3(center.x, CurrentPivot.y + Spec.height_m * 0.5f, center.z),
                                  new Vector3(halfX * 2f, Spec.height_m, halfZ * 2f));
            }
        }

        public void ApplyTransform(Vector3 pivot, float yaw)
        {
            CurrentPivot = pivot;
            CurrentYaw = yaw;
            if (Node == null) return;
            Node.position = pivot;
            Node.rotation = Quaternion.Euler(0f, yaw - Yaw0, 0f);
        }
    }

    public class PickMesh
    {
        public Vector3[] vertices;
        public int[] triangles;
    }

    public class TramRuntime
    {
        public DynTram Spec;
        public Transform Node;
        public Vector3 Pivot0;
        public float Yaw0;
        public float X;
        public float CurrentYaw;
        public float YawTarget;
        public float Speed;
        public float StartDirection;
        public int Direction;
        public float Dwell;
        public int Arrivals;
        public string State = "moving";   // moving | dwelling | blocked
        public string BlockReason = "";
        public List<PickMesh> PickMeshes = new List<PickMesh>();
        public bool PickReady;

        public Vector3 CurrentPivot
        {
            get { return new Vector3(X, Pivot0.y, Spec.track.z); }
        }

        public Vector3 Center
        {
            get { return BoundsAt(X, CurrentYaw).center; }
        }

        public Bounds Footprint
        {
            get { return BoundsAt(X, CurrentYaw); }
        }

        public Bounds BoundsAt(float x, float yaw)
        {
            Vector3 sourceOffset = new Vector3((Spec.bounds[0] + Spec.bounds[2]) * 0.5f - Pivot0.x,
                0f, (Spec.bounds[1] + Spec.bounds[3]) * 0.5f - Pivot0.z);
            Vector3 center = new Vector3(x, Pivot0.y + Spec.height_m * 0.5f, Spec.track.z) +
                             Quaternion.Euler(0f, yaw - Yaw0, 0f) * sourceOffset;
            float radians = yaw * Mathf.Deg2Rad;
            float c = Mathf.Abs(Mathf.Cos(radians));
            float s = Mathf.Abs(Mathf.Sin(radians));
            return new Bounds(center, new Vector3(s * Spec.length_m + c * Spec.width_m,
                Spec.height_m, c * Spec.length_m + s * Spec.width_m));
        }

        public Bounds RotationSweep(float fromYaw, float toYaw)
        {
            Bounds swept = BoundsAt(X, fromYaw);
            swept.Encapsulate(BoundsAt(X, toYaw));
            float start = (fromYaw - Yaw0) * Mathf.Deg2Rad;
            float finish = start + Mathf.DeltaAngle(fromYaw, toYaw) * Mathf.Deg2Rad;
            float minAngle = Mathf.Min(start, finish);
            float maxAngle = Mathf.Max(start, finish);
            float quarterTurn = Mathf.PI * 0.5f;
            Vector3 offset = new Vector3((Spec.bounds[0] + Spec.bounds[2]) * 0.5f - Pivot0.x,
                0f, (Spec.bounds[1] + Spec.bounds[3]) * 0.5f - Pivot0.z);
            for (int side = -1; side <= 1; side += 2)
            for (int end = -1; end <= 1; end += 2)
            {
                Vector3 corner = offset + Quaternion.Euler(0f, Yaw0, 0f) *
                    new Vector3(side * Spec.width_m * 0.5f, 0f, end * Spec.length_m * 0.5f);
                // Each corner describes a circular arc around the source pivot. Include
                // its exact X/Z extrema between the endpoints, not just sampled angles.
                float phase = Mathf.Atan2(corner.z, corner.x);
                int first = Mathf.CeilToInt((minAngle - phase) / quarterTurn);
                int last = Mathf.FloorToInt((maxAngle - phase) / quarterTurn);
                for (int k = first; k <= last; k++)
                {
                    float angle = phase + k * quarterTurn;
                    Vector3 p = CurrentPivot + Quaternion.Euler(0f, angle * Mathf.Rad2Deg, 0f) * corner;
                    swept.Encapsulate(p);
                }
            }
            return swept;
        }

        public int CompletedRoundTrips
        {
            get { return Arrivals / 2; }
        }
    }

    public class FlameRuntime
    {
        public DynFlame Spec;
        public Transform Node;
        public Vector3 Pivot0;
        public Light FireLight;
        public bool Lit = true;
        public int ChangeCount;
        public string LastStatus = "lit";
        public float LastChangeRealtime;

        public void SetLit(bool lit)
        {
            Lit = lit;
            ChangeCount++;
            LastChangeRealtime = Time.realtimeSinceStartup;
            LastStatus = lit ? "lit" : "extinguished";
            if (Node != null) Node.gameObject.SetActive(lit);
            if (FireLight != null) FireLight.intensity = lit ? 2.4f : 0f;
        }
    }

    public sealed class DynamicWorld : MonoBehaviour
    {
        public static DynamicWorld Instance { get; private set; }

        public bool Ready { get; private set; }
        public string Error { get; private set; } = "";
        public MapWorldProfile Map { get; private set; }
        public CharacterLocomotion Loco { get; private set; }
        public Yixuan.Core.YixuanCharacter Character { get; private set; }
        public Presentation.CameraDirector CameraDirector { get; private set; }
        public VehicleCameraRig CameraRig { get; private set; }
        public readonly List<VehicleRuntime> Vehicles = new List<VehicleRuntime>();
        // Roads and the autonomous tram have different controllers, but one identity each.
        public int TotalVehicleCount { get { return Vehicles.Count + (Tram != null ? 1 : 0); } }
        public TramRuntime Tram { get; private set; }
        public FlameRuntime Flame { get; private set; }
        public int StaticVehicleObstaclesRemoved { get; private set; }
        public string LastObstacleError = "";

        float _obstacleTimer;
        bool _obstaclesDirty = true;
        readonly Dictionary<string, Transform> _nodes = new Dictionary<string, Transform>();
        readonly List<Bounds> _staticBase = new List<Bounds>();
        readonly Dictionary<string, List<PickMesh>> _pickByEntity = new Dictionary<string, List<PickMesh>>();
        readonly Dictionary<string, Transform> _pickTransform = new Dictionary<string, Transform>();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetSession()
        {
            Instance = null;
        }

        public static DynamicWorld EnsureInstance()
        {
            if (Instance != null) return Instance;
            var existing = FindObjectOfType<DynamicWorld>();
            if (existing != null)
            {
                Instance = existing;
                return existing;
            }
            var go = new GameObject("DynamicWorld");
            Instance = go.AddComponent<DynamicWorld>();
            return Instance;
        }

        void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;
        }

        public bool Initialize()
        {
            if (Ready) return true;
            if (!DynamicData.EnsureLoaded())
            {
                Error = DynamicData.Error;
                Debug.LogError("[DynamicWorld] " + Error);
                return false;
            }
            Map = MapWorldProfile.FindActive();
            if (Map == null)
            {
                Error = "找不到 MapWorldProfile";
                return false;
            }
            Map.EnsureData();
            if (!Map.DataReady)
            {
                Error = "地图数据未就绪：" + Map.LastError;
                return false;
            }
            Loco = FindObjectOfType<CharacterLocomotion>();
            Character = FindObjectOfType<Yixuan.Core.YixuanCharacter>();
            CameraDirector = FindObjectOfType<Presentation.CameraDirector>();
            // A failed attempt may be retried after a missing map node is restored.
            // Rebuild one runtime per entity rather than accumulating partial results.
            Vehicles.Clear();
            Tram = null;
            Flame = null;
            _nodes.Clear();
            _pickByEntity.Clear();
            _pickTransform.Clear();
            Error = "";
            var mapRoot = FindDeep(Map.transform, "SovietBlueHour_Geometry") ??
                          FindDeep(transform.root, "SovietBlueHour_Geometry");
            if (mapRoot == null) mapRoot = FindAnyRoot();
            CollectNodes(mapRoot != null ? mapRoot : transform.root);

            int missing = 0;
            foreach (var spec in DynamicData.Root.vehicles)
            {
                if (spec == null) continue;
                // The tram's pose, picking and obstacle all belong to TramRuntime below.
                // Creating a road runtime for it leaves a second, stationary copy at spawn.
                if (spec.@class == "tram" || (DynamicData.Root.tram != null &&
                    spec.entity_id == DynamicData.Root.tram.entity_id)) continue;
                Transform node;
                if (!_nodes.TryGetValue(spec.node, out node))
                {
                    missing++;
                    continue;
                }
                var vt = new VehicleRuntime
                {
                    Spec = spec,
                    Node = node,
                    Pivot0 = new Vector3(spec.pivot_unity[0], spec.pivot_unity[1], spec.pivot_unity[2]),
                    Yaw0 = spec.yaw_deg,
                };
                vt.CurrentPivot = vt.Pivot0;
                vt.CurrentYaw = vt.Yaw0;
                vt.ApplyTransform(vt.Pivot0, vt.Yaw0);
                BuildPickMeshes(node, vt.PickMeshes, spec.entity_id);
                vt.PickReady = vt.PickMeshes.Count > 0;
                Vehicles.Add(vt);
            }

            if (DynamicData.Root.tram != null && _nodes.TryGetValue(DynamicData.Root.tram.node, out var tramNode))
            {
                var spec = DynamicData.Root.tram;
                Tram = new TramRuntime
                {
                    Spec = spec,
                    Node = tramNode,
                    Pivot0 = new Vector3(spec.pivot_unity[0], spec.pivot_unity[1], spec.pivot_unity[2]),
                    Yaw0 = spec.yaw_deg,
                    X = spec.pivot_unity[0],
                    StartDirection = spec.track.start_direction,
                };
                Tram.Direction = Tram.StartDirection >= 0f ? 1 : -1;
                Tram.CurrentYaw = spec.yaw_deg;
                Tram.YawTarget = spec.yaw_deg;
                BuildPickMeshes(tramNode, Tram.PickMeshes, spec.entity_id);
                Tram.PickReady = Tram.PickMeshes.Count > 0;
                ApplyTramTransform();
            }
            else
            {
                Error = "缺少有轨电车节点 " + (DynamicData.Root.tram != null ? DynamicData.Root.tram.node : "?");
                missing++;
            }

            if (DynamicData.Root.flame != null)
            {
                var spec = DynamicData.Root.flame;
                Transform flameNode;
                if (_nodes.TryGetValue(spec.node, out flameNode))
                {
                    Flame = new FlameRuntime
                    {
                        Spec = spec,
                        Node = flameNode,
                        Pivot0 = new Vector3(spec.pivot_unity[0], spec.pivot_unity[1], spec.pivot_unity[2]),
                        Lit = spec.default_lit,
                    };
                    var existingLight = flameNode.Find("EternalFlameLight");
                    var lightGo = existingLight != null ? existingLight.gameObject : new GameObject("EternalFlameLight");
                    lightGo.transform.SetParent(flameNode, false);
                    var lightPos = new Vector3(spec.light_position[0], spec.light_position[1], spec.light_position[2]);
                    lightGo.transform.position = lightPos;
                    Flame.FireLight = lightGo.GetComponent<Light>();
                    if (Flame.FireLight == null) Flame.FireLight = lightGo.AddComponent<Light>();
                    Flame.FireLight.type = LightType.Point;
                    Flame.FireLight.color = new Color(1f, 0.58f, 0.22f);
                    Flame.FireLight.range = 9f;
                    Flame.FireLight.intensity = Flame.Lit ? 2.4f : 0f;
                    Flame.FireLight.shadows = LightShadows.None;
                    if (flameNode != null) flameNode.gameObject.SetActive(Flame.Lit);
                }
                else missing++;
            }

            BuildStaticBase();
            if (CameraDirector != null && CameraDirector.Camera != null)
            {
                CameraRig = CameraDirector.Camera.GetComponent<VehicleCameraRig>();
                if (CameraRig == null) CameraRig = CameraDirector.Camera.gameObject.AddComponent<VehicleCameraRig>();
                CameraRig.Bind(this);
            }
            RebuildObstacles();
            Ready = missing == 0;
            if (!Ready && string.IsNullOrEmpty(Error)) Error = "动态实体缺少节点：" + missing;
            Debug.Log("[DynamicWorld] ready=" + Ready + " roadVehicles=" + Vehicles.Count +
                      " totalVehicles=" + TotalVehicleCount +
                      " nodes=" + _nodes.Count + " missing=" + missing +
                      " staticVehicleObstaclesRemoved=" + StaticVehicleObstaclesRemoved +
                      " tram=" + (Tram != null) + " flame=" + (Flame != null) + " error=" + Error);
            return Ready;
        }

        Transform FindAnyRoot()
        {
            var mapWrapper = GameObject.Find("SovietBlueHour_Map");
            if (mapWrapper != null) return mapWrapper.transform;
            var candidates = FindObjectsOfType<Transform>();
            foreach (var t in candidates)
                if (t.name == "SovietBlueHour_Geometry") return t;
            return null;
        }

        void CollectNodes(Transform root)
        {
            if (root == null) return;
            var all = root.GetComponentsInChildren<Transform>(true);
            foreach (var t in all)
            {
                if (t == null) continue;
                if (!_nodes.ContainsKey(t.name)) _nodes[t.name] = t;
            }
        }

        Transform FindDeep(Transform root, string name)
        {
            if (root == null) return null;
            if (root.name == name) return root;
            foreach (Transform child in root)
            {
                var found = FindDeep(child, name);
                if (found != null) return found;
            }
            return null;
        }

        // ------------------------------------------------------------ queries
        public VehicleRuntime GetVehicle(string entityId)
        {
            foreach (var v in Vehicles)
                if (v.Spec.entity_id == entityId) return v;
            return null;
        }

        public bool TryGetNode(string nodeName, out Transform node)
        {
            return _nodes.TryGetValue(nodeName, out node);
        }

        public Vector3? CurrentPositionOf(string entityId)
        {
            if (string.IsNullOrEmpty(entityId)) return null;
            if (Tram != null && Tram.Spec.entity_id == entityId) return Tram.Center;
            var v = GetVehicle(entityId);
            if (v != null) return v.Center;
            return null;
        }

        public Bounds? CurrentBoundsOf(string entityId)
        {
            if (Tram != null && Tram.Spec.entity_id == entityId) return Tram.Footprint;
            var v = GetVehicle(entityId);
            if (v != null) return v.Footprint;
            return null;
        }

        public Vector3 CharacterPosition
        {
            get
            {
                if (Character != null) return Character.transform.position;
                return Loco != null ? Loco.transform.position : Vector3.zero;
            }
        }

        // ------------------------------------------------- dynamic obstacles
        void BuildStaticBase()
        {
            _staticBase.Clear();
            StaticVehicleObstaclesRemoved = 0;
            foreach (var b in Map.ObstacleBounds) _staticBase.Add(b);
            var dynamicStatic = new List<Bounds>();
            foreach (var v in Vehicles) dynamicStatic.Add(InflatedStatic(v.Spec.bounds));
            if (Tram != null) dynamicStatic.Add(InflatedStatic(Tram.Spec.bounds));
            for (int i = _staticBase.Count - 1; i >= 0; i--)
            {
                bool remove = false;
                foreach (var candidate in dynamicStatic)
                {
                    if (Mathf.Abs(candidate.center.x - _staticBase[i].center.x) < 0.3f &&
                        Mathf.Abs(candidate.center.z - _staticBase[i].center.z) < 0.3f &&
                        Mathf.Abs(candidate.size.x - _staticBase[i].size.x) < 0.4f &&
                        Mathf.Abs(candidate.size.z - _staticBase[i].size.z) < 0.4f)
                    {
                        remove = true;
                        break;
                    }
                }
                if (remove)
                {
                    _staticBase.RemoveAt(i);
                    StaticVehicleObstaclesRemoved++;
                }
            }
        }

        static Bounds InflatedStatic(float[] bounds)
        {
            float cx = (bounds[0] + bounds[2]) * 0.5f;
            float cz = (bounds[1] + bounds[3]) * 0.5f;
            float sx = Mathf.Abs(bounds[2] - bounds[0]) + 1.0f;
            float sz = Mathf.Abs(bounds[3] - bounds[1]) + 1.0f;
            return new Bounds(new Vector3(cx, 0.5f, cz), new Vector3(sx, 1.5f, sz));
        }

        public void MarkObstaclesDirty()
        {
            _obstaclesDirty = true;
        }

        public void RebuildObstacles()
        {
            if (Map == null) return;
            Map.ObstacleBounds.Clear();
            foreach (var b in _staticBase) Map.ObstacleBounds.Add(b);
            foreach (var v in Vehicles)
            {
                var f = v.Footprint;
                f.Expand(new Vector3(1.0f, 0f, 1.0f));
                Map.ObstacleBounds.Add(f);
            }
            if (Tram != null)
            {
                var f = Tram.Footprint;
                f.Expand(new Vector3(1f, 0f, 1f));
                Map.ObstacleBounds.Add(f);
            }
            WorldData.Refresh();
            _obstaclesDirty = false;
        }

        public List<Bounds> ObstacleBoundsExcluding(string entityId)
        {
            var list = new List<Bounds>(_staticBase);
            foreach (var v in Vehicles)
            {
                if (v.Spec.entity_id == entityId) continue;
                var f = v.Footprint;
                f.Expand(new Vector3(1.0f, 0f, 1.0f));
                list.Add(f);
            }
            if (Tram != null && (Tram.Spec == null || Tram.Spec.entity_id != entityId))
            {
                var f = Tram.Footprint;
                f.Expand(new Vector3(1f, 0f, 1f));
                list.Add(f);
            }
            return list;
        }

        public bool IsPointClear(Vector3 point, float margin, string excludeEntityId = null)
        {
            var boundsList = ObstacleBoundsExcluding(excludeEntityId);
            foreach (var b in boundsList)
            {
                if (point.x > b.min.x - margin && point.x < b.max.x + margin &&
                    point.z > b.min.z - margin && point.z < b.max.z + margin) return false;
            }
            return true;
        }

        /// <summary>Whole oriented body, using the same source-pivot offset as rendering/picking.</summary>
        public bool IsVehicleBodyClear(VehicleRuntime v, Vector3 pivot, float yaw, out string reason)
        {
            return IsVehicleSweepClear(v, pivot, pivot, yaw, out reason);
        }

        /// <summary>Exact XZ separating-axis test for a fixed-heading translated body.</summary>
        public bool IsVehicleSweepClear(VehicleRuntime v, Vector3 from, Vector3 to, float yaw, out string reason)
        {
            if (v == null || v.Spec == null) { reason = "vehicle_missing"; return false; }
            return IsVehicleSweepClear(v, from, to, yaw, ObstacleBoundsExcluding(v.Spec.entity_id), out reason);
        }

        bool IsVehicleSweepClear(VehicleRuntime v, Vector3 from, Vector3 to, float yaw,
                                 List<Bounds> obstacles, out string reason)
        {
            reason = "";
            float halfW = v.Spec.width_m * 0.5f;
            float halfL = v.Spec.length_m * 0.5f;
            Vector3 forward = Quaternion.Euler(0f, yaw, 0f) * Vector3.forward;
            Vector3 right = Quaternion.Euler(0f, yaw, 0f) * Vector3.right;
            Vector3 centerA = v.CenterAt(from, yaw);
            Vector3 centerB = v.CenterAt(to, yaw);
            // The swept convex hull has the body axes plus the normal of its travel segment.
            Vector3 travel = centerB - centerA;
            travel.y = 0f;
            Vector3 center = (centerA + centerB) * 0.5f;
            Vector3 normal = new Vector3(-travel.z, 0f, travel.x).normalized;
            for (int end = 0; end < 2; end++)
            for (int a = -1; a <= 1; a += 2)
            for (int b = -1; b <= 1; b += 2)
            {
                Vector3 p = (end == 0 ? centerA : centerB) + forward * (a * halfL) + right * (b * halfW);
                if (!WorldData.IsInsideBounds(p)) { reason = "outside_map"; return false; }
            }
            foreach (var box in obstacles)
            {
                if (SeparatedVehicleSweep(box, Vector3.right, center, travel, right, forward, halfW, halfL) ||
                    SeparatedVehicleSweep(box, Vector3.forward, center, travel, right, forward, halfW, halfL) ||
                    SeparatedVehicleSweep(box, right, center, travel, right, forward, halfW, halfL) ||
                    SeparatedVehicleSweep(box, forward, center, travel, right, forward, halfW, halfL) ||
                    (travel.sqrMagnitude > 0.000001f &&
                     SeparatedVehicleSweep(box, normal, center, travel, right, forward, halfW, halfL))) continue;
                reason = "obstacle@" + box.center.x.ToString("F1") + "," + box.center.z.ToString("F1") +
                         " size=" + box.size.x.ToString("F1") + "x" + box.size.z.ToString("F1");
                return false;
            }
            return true;
        }

        static bool SeparatedVehicleSweep(Bounds box, Vector3 axis, Vector3 center, Vector3 travel,
                                          Vector3 right, Vector3 forward, float halfW, float halfL)
        {
            Vector3 difference = box.center - center;
            difference.y = 0f;
            float distance = Mathf.Abs(Vector3.Dot(difference, axis));
            float bodyRadius = halfW * Mathf.Abs(Vector3.Dot(right, axis)) +
                               halfL * Mathf.Abs(Vector3.Dot(forward, axis)) +
                               0.5f * Mathf.Abs(Vector3.Dot(travel, axis));
            float boxRadius = box.extents.x * Mathf.Abs(axis.x) + box.extents.z * Mathf.Abs(axis.z);
            return distance >= bodyRadius + boxRadius;
        }

        /// <summary>Planning and runtime both turn while stationary, then translate.</summary>
        public bool IsVehicleTurnClear(VehicleRuntime v, Vector3 pivot, float fromYaw, float toYaw, out string reason)
        {
            reason = "";
            if (v == null || v.Spec == null) { reason = "vehicle_missing"; return false; }
            float turn = Mathf.DeltaAngle(fromYaw, toYaw);
            if (Mathf.Abs(turn) < 0.001f) return IsVehicleBodyClear(v, pivot, toYaw, out reason);
            // A turn only visits the circle around this pivot. Cull far-away obstacles once
            // and reuse the snapshot instead of rebuilding the world for every sample.
            float radius = new Vector2(v.Spec.width_m, v.Spec.length_m).magnitude * 0.5f +
                           Vector3.Distance(v.CenterAt(pivot, fromYaw), pivot);
            var obstacles = ObstacleBoundsExcluding(v.Spec.entity_id);
            obstacles.RemoveAll(box => pivot.x + radius <= box.min.x || pivot.x - radius >= box.max.x ||
                                       pivot.z + radius <= box.min.z || pivot.z - radius >= box.max.z);
            if (obstacles.Count == 0 &&
                WorldData.IsInsideBounds(pivot + new Vector3(radius, 0f, radius)) &&
                WorldData.IsInsideBounds(pivot + new Vector3(-radius, 0f, radius)) &&
                WorldData.IsInsideBounds(pivot + new Vector3(radius, 0f, -radius)) &&
                WorldData.IsInsideBounds(pivot + new Vector3(-radius, 0f, -radius))) return true;
            int samples = Mathf.Max(1, Mathf.CeilToInt(Mathf.Abs(turn)));
            for (int i = 0; i <= samples; i++)
            {
                float yaw = fromYaw + turn * i / samples;
                if (!IsVehicleSweepClear(v, pivot, pivot, yaw, obstacles, out reason))
                {
                    reason = "turn:" + reason;
                    return false;
                }
            }
            return true;
        }

        // ------------------------------------------------------------ character
        public void HideCharacter()
        {
            if (Character == null) return;
            foreach (var r in Character.GetComponentsInChildren<Renderer>(true))
                if (r != null) r.enabled = false;
        }

        public void ShowCharacter()
        {
            if (Character == null) return;
            foreach (var r in Character.GetComponentsInChildren<Renderer>(true))
                if (r != null) r.enabled = true;
        }

        public void PlaceCharacterAt(Vector3 worldPosition)
        {
            if (Character == null) return;
            float y = WorldData.GroundHeightAt(new Vector3(worldPosition.x, 0f, worldPosition.z)) + 0.02f;
            Character.transform.position = new Vector3(worldPosition.x, y, worldPosition.z);
            if (Loco != null) Loco.transform.position = Character.transform.position;
        }

        public void FollowCharacterToVehicle(VehicleRuntime v)
        {
            if (Character == null || v == null) return;
            Vector3 seat = v.CurrentPivot + Quaternion.Euler(0f, v.CurrentYaw, 0f) * new Vector3(0.18f, 0.62f, 0.15f);
            Character.transform.position = seat;
            if (Loco != null) Loco.transform.position = seat;
        }

        // --------------------------------------------------------------- camera
        public void EnterVehicleCamera(VehicleRuntime v)
        {
            if (CameraRig != null) CameraRig.Enter(v);
            BigMapPanel.SetVehicleGazeSource(v != null ? v.Node : null);
        }

        public void ExitVehicleCamera()
        {
            if (CameraRig != null) CameraRig.Exit();
            BigMapPanel.SetVehicleGazeSource(null);
        }

        // ------------------------------------------------------------- math pick
        void BuildPickMeshes(Transform node, List<PickMesh> target, string entityId)
        {
            if (node == null) return;
            Matrix4x4 toLocal = node.worldToLocalMatrix;
            foreach (var mf in node.GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf == null || mf.sharedMesh == null) continue;
                var mesh = mf.sharedMesh;
                var verts = mesh.vertices;
                var tris = mesh.triangles;
                if (verts.Length == 0 || tris.Length == 0) continue;
                Matrix4x4 m = toLocal * mf.transform.localToWorldMatrix;
                var local = new Vector3[verts.Length];
                for (int i = 0; i < verts.Length; i++) local[i] = m.MultiplyPoint3x4(verts[i]);
                target.Add(new PickMesh { vertices = local, triangles = tris });
            }
            if (target.Count > 0) _pickTransform[entityId] = node;
        }

        public bool TryPick(Ray worldRay, out string entityId, out Vector3 worldPoint,
                            out float distance, float maxDistance = 300f)
        {
            entityId = null;
            worldPoint = Vector3.zero;
            distance = float.MaxValue;
            foreach (var v in Vehicles)
            {
                if (!v.PickReady || v.Node == null) continue;
                if (TryPickNode(v.Node, v.PickMeshes, worldRay, maxDistance, out var p, out var d) && d < distance)
                {
                    distance = d;
                    entityId = v.Spec.entity_id;
                    worldPoint = p;
                }
            }
            if (Tram != null && Tram.PickReady && Tram.Node != null)
            {
                if (TryPickNode(Tram.Node, Tram.PickMeshes, worldRay, maxDistance, out var p, out var d) && d < distance)
                {
                    distance = d;
                    entityId = Tram.Spec.entity_id;
                    worldPoint = p;
                }
            }
            return entityId != null;
        }

        static bool TryPickNode(Transform node, List<PickMesh> meshes, Ray worldRay, float maxDistance,
                                out Vector3 worldPoint, out float distance)
        {
            worldPoint = Vector3.zero;
            distance = float.MaxValue;
            Ray localRay = new Ray(node.InverseTransformPoint(worldRay.origin),
                                   node.InverseTransformDirection(worldRay.direction));
            foreach (var pm in meshes)
            {
                var tris = pm.triangles;
                var verts = pm.vertices;
                for (int i = 0; i < tris.Length; i += 3)
                {
                    Vector3 a = verts[tris[i]];
                    Vector3 b = verts[tris[i + 1]];
                    Vector3 c = verts[tris[i + 2]];
                    if (RayTriangle(localRay, a, b, c, out float t) && t > 0.01f && t < distance &&
                        t <= maxDistance)
                    {
                        distance = t;
                        worldPoint = node.TransformPoint(localRay.origin + localRay.direction * t);
                    }
                }
            }
            return distance < float.MaxValue;
        }

        static bool RayTriangle(Ray ray, Vector3 a, Vector3 b, Vector3 c, out float distance)
        {
            distance = 0f;
            Vector3 ab = b - a;
            Vector3 ac = c - a;
            Vector3 p = Vector3.Cross(ray.direction, ac);
            float det = Vector3.Dot(ab, p);
            if (Mathf.Abs(det) < 1e-7f) return false;
            float invDet = 1f / det;
            Vector3 t = ray.origin - a;
            float u = Vector3.Dot(t, p) * invDet;
            if (u < 0f || u > 1f) return false;
            Vector3 q = Vector3.Cross(t, ab);
            float v = Vector3.Dot(ray.direction, q) * invDet;
            if (v < 0f || u + v > 1f) return false;
            distance = Vector3.Dot(ac, q) * invDet;
            return distance > 0f;
        }

        public bool TryPickAtScreen(Camera camera, Vector2 screenPoint, out string entityId,
                                    out Vector3 worldPoint, out float distance)
        {
            entityId = null;
            worldPoint = Vector3.zero;
            distance = -1f;
            if (camera == null) return false;
            Ray ray = camera.ScreenPointToRay(new Vector3(screenPoint.x, screenPoint.y, 0f));
            return TryPick(ray, out entityId, out worldPoint, out distance);
        }

        public bool TryPickAtCrosshair(Camera camera, out string entityId, out Vector3 worldPoint,
                                       out float distance)
        {
            return TryPickAtScreen(camera, new Vector2(camera.pixelWidth * 0.5f, camera.pixelHeight * 0.5f),
                                   out entityId, out worldPoint, out distance);
        }

        // --------------------------------------------------------------- update
        void Update()
        {
            if (!Ready) return;
            float dt = Time.deltaTime;
            UpdateTram(dt);
            if (Flame != null && Flame.FireLight != null && Flame.Lit)
            {
                Flame.FireLight.intensity = 2.2f + Mathf.Sin(Time.time * 9.7f) * 0.25f;
            }
            _obstacleTimer += dt;
            if (_obstaclesDirty && _obstacleTimer > 0.12f)
            {
                _obstacleTimer = 0f;
                RebuildObstacles();
                if (BigMapPanel.IsOpen) BigMapPanel.MarkDynamicMarkersDirty();
            }
        }

        public void MarkDynamicMoved()
        {
            _obstaclesDirty = true;
        }

        // ---------------------------------------------------------------- tram
        void UpdateTram(float dt)
        {
            if (Tram == null || dt <= 0f) return;
            var track = Tram.Spec.track;
            float oldX = Tram.X;
            float oldYaw = Tram.CurrentYaw;
            float decel = Mathf.Max(0.01f, track.decel_mps2);
            float safetyGap = Mathf.Max(0.05f, WorldData.Margin);
            // Turning changes the physical footprint as well. Do not rotate through a person
            // or parked car while dwelling/reversing at an endpoint.
            float nextYaw = Mathf.MoveTowardsAngle(oldYaw, Tram.YawTarget, 45f * dt);
            Bounds sweep = Tram.RotationSweep(oldYaw, nextYaw);
            string rotationBlock = "";
            bool canTurn = Mathf.Abs(Mathf.DeltaAngle(oldYaw, nextYaw)) < 0.001f ||
                           TramRotationClear(sweep, safetyGap, out rotationBlock);
            if (!canTurn)
            {
                Tram.Speed = 0f;
                Tram.State = "blocked";
                Tram.BlockReason = rotationBlock;
                return;
            }
            Tram.CurrentYaw = nextYaw;
            // Clearance is measured from the front of the whole body, not a point near the
            // pivot. Always retain the actual stop line, even for sudden close obstructions.
            string blockReason;
            float clearance = TramForwardClearance(sweep, safetyGap, out blockReason);
            float brakingDistance = Tram.Speed * Tram.Speed / (2f * decel);
            float lookAhead = Mathf.Max(Mathf.Max(0f, track.block_distance_m),
                brakingDistance + Tram.Speed * dt + safetyGap);
            bool blocked = clearance <= lookAhead;
            if (Tram.Dwell > 0f)
            {
                Tram.Dwell = Mathf.Max(0f, Tram.Dwell - dt);
                Tram.Speed = 0f;
                Tram.State = "dwelling";
                Tram.BlockReason = "";
                if (Tram.Dwell <= 0f)
                    ReverseTramDirection();
            }
            else
            {
                float endDistance = Mathf.Max(0f, Tram.Direction > 0 ?
                    track.travel_max_x - Tram.X : Tram.X - track.travel_min_x);
                float targetSpeed = Mathf.Min(Mathf.Max(0f, track.speed_mps),
                    Mathf.Sqrt(2f * decel * endDistance));
                if (blocked)
                {
                    targetSpeed = 0f;
                    Tram.State = "blocked";
                    Tram.BlockReason = blockReason;
                }
                else
                {
                    Tram.State = "moving";
                    Tram.BlockReason = "";
                }
                float previousSpeed = Mathf.Max(0f, Tram.Speed);
                float accel = targetSpeed > previousSpeed ? Mathf.Max(0.01f, track.accel_mps2) : decel;
                Tram.Speed = Mathf.MoveTowards(previousSpeed, targetSpeed, accel * dt);
                float proposedStep = (previousSpeed + Tram.Speed) * 0.5f * dt;
                float step = Mathf.Min(proposedStep, Mathf.Min(endDistance, clearance));
                Tram.X += Tram.Direction * step;
                // Braking alone cannot protect a pedestrian appearing inside the stopping
                // distance. Clamp this frame at the safe line and stay there until it clears.
                if (clearance <= proposedStep && clearance <= endDistance)
                {
                    Tram.Speed = 0f;
                    Tram.State = "blocked";
                    Tram.BlockReason = blockReason;
                }
                else if (endDistance <= proposedStep)
                {
                    Tram.X = Tram.Direction > 0 ? track.travel_max_x : track.travel_min_x;
                    Tram.Speed = 0f;
                    Tram.Arrivals++;
                    Tram.Dwell = Mathf.Max(0f, track.end_dwell_s);
                    Tram.State = "dwelling";
                    Tram.BlockReason = "";
                    if (Tram.Dwell <= 0f) ReverseTramDirection();
                }
            }
            ApplyTramTransform();
            if (Mathf.Abs(Tram.X - oldX) > 0.0001f ||
                Mathf.Abs(Mathf.DeltaAngle(Tram.CurrentYaw, oldYaw)) > 0.0001f) MarkDynamicMoved();
        }

        void ReverseTramDirection()
        {
            Tram.Direction = -Tram.Direction;
            Tram.YawTarget = Tram.Spec.yaw_deg + (Tram.Direction > 0 ? 180f : 0f);
        }

        Bounds CharacterFootprint()
        {
            float radius = Mathf.Max(0.1f, WorldData.AgentRadius);
            return new Bounds(CharacterPosition, new Vector3(radius * 2f, 1.8f, radius * 2f));
        }

        bool TramRotationClear(Bounds swept, float safetyGap, out string reason)
        {
            reason = "";
            if ((Character != null || Loco != null) && OverlapsXZ(swept, CharacterFootprint(), safetyGap))
            {
                reason = "blocked_by_character";
                return false;
            }
            foreach (var vehicle in Vehicles)
            {
                if (!OverlapsXZ(swept, vehicle.Footprint, safetyGap)) continue;
                reason = "blocked_by_vehicle";
                return false;
            }
            return true;
        }

        static bool OverlapsXZ(Bounds a, Bounds b, float margin)
        {
            return a.max.x + margin > b.min.x && a.min.x - margin < b.max.x &&
                   a.max.z + margin > b.min.z && a.min.z - margin < b.max.z;
        }

        float TramForwardClearance(Bounds body, float safetyGap, out string reason)
        {
            float nearest = float.PositiveInfinity;
            reason = "";
            if (Character != null || Loco != null)
            {
                float gap = ForwardClearance(body, CharacterFootprint(), Tram.Direction, safetyGap);
                if (gap < nearest)
                {
                    nearest = gap;
                    reason = "blocked_by_character";
                }
            }
            foreach (var vehicle in Vehicles)
            {
                float gap = ForwardClearance(body, vehicle.Footprint, Tram.Direction, safetyGap);
                if (gap >= nearest) continue;
                nearest = gap;
                reason = "blocked_by_vehicle";
            }
            return nearest;
        }

        static float ForwardClearance(Bounds body, Bounds obstacle, int direction, float safetyGap)
        {
            if (obstacle.min.z >= body.max.z + safetyGap || obstacle.max.z <= body.min.z - safetyGap)
                return float.PositiveInfinity;
            // An object completely behind the rear must not stop a departing tram.
            if (direction > 0)
            {
                if (obstacle.max.x <= body.min.x - safetyGap) return float.PositiveInfinity;
                return Mathf.Max(0f, obstacle.min.x - body.max.x - safetyGap);
            }
            if (obstacle.min.x >= body.max.x + safetyGap) return float.PositiveInfinity;
            return Mathf.Max(0f, body.min.x - obstacle.max.x - safetyGap);
        }

        void ApplyTramTransform()
        {
            if (Tram == null || Tram.Node == null) return;
            Tram.Node.position = new Vector3(Tram.X, Tram.Pivot0.y, Tram.Spec.track.z);
            Tram.Node.rotation = Quaternion.Euler(0f, Tram.CurrentYaw - Tram.Yaw0, 0f);
        }

        // --------------------------------------------------------------- flame
        public bool SetFlameState(bool lit, out string status)
        {
            status = "";
            if (Flame == null)
            {
                status = "flame_not_loaded";
                return false;
            }
            if (Flame.Lit == lit)
            {
                status = lit ? "already_lit" : "already_extinguished";
                return true;
            }
            Flame.SetLit(lit);
            status = lit ? "lit" : "extinguished";
            return true;
        }
    }

    /// <summary>Chase camera used while active_actor=vehicle; records selection_source=vehicle_view.</summary>
    public sealed class VehicleCameraRig : MonoBehaviour
    {
        public bool Active { get; private set; }
        public string ViewSource { get { return Active ? "vehicle_view" : "character"; } }
        DynamicWorld _world;
        VehicleRuntime _vehicle;
        bool _savedEnabled;
        bool _savedFollow;
        Transform _savedFollowTarget;
        float _savedFov;
        Camera _camera;

        public void Bind(DynamicWorld world)
        {
            _world = world;
            _camera = GetComponent<Camera>();
        }

        public void Enter(VehicleRuntime vehicle)
        {
            if (vehicle == null || _world == null) return;
            _vehicle = vehicle;
            if (!Active)
            {
                _savedEnabled = _world.CameraDirector != null && _world.CameraDirector.enabled;
                _savedFollow = _world.CameraDirector != null && _world.CameraDirector.FollowCharacter;
                _savedFollowTarget = _world.CameraDirector != null ? _world.CameraDirector.FollowTarget : null;
                _savedFov = _camera != null ? _camera.fieldOfView : 60f;
            }
            Active = true;
            if (_world.CameraDirector != null) _world.CameraDirector.enabled = false;
            if (_camera != null) _camera.fieldOfView = 55f;
        }

        public void Exit()
        {
            Active = false;
            _vehicle = null;
            if (_world != null && _world.CameraDirector != null)
            {
                _world.CameraDirector.enabled = _savedEnabled;
                _world.CameraDirector.FollowCharacter = _savedFollow;
                _world.CameraDirector.FollowTarget = _savedFollowTarget;
                _world.CameraDirector.SnapThirdPersonView();
            }
            if (_camera != null) _camera.fieldOfView = _savedFov;
        }

        void LateUpdate()
        {
            if (!Active || _vehicle == null || _camera == null) return;
            Vector3 pivot = _vehicle.CurrentPivot;
            Vector3 forward = _vehicle.Forward;
            Vector3 position = pivot - forward * 7.2f + Vector3.up * 3.0f;
            _camera.transform.position = Vector3.Lerp(_camera.transform.position, position, 1f - Mathf.Exp(-8f * Time.deltaTime));
            Vector3 look = pivot + Vector3.up * 1.15f;
            Vector3 dir = look - _camera.transform.position;
            if (dir.sqrMagnitude > 0.001f)
                _camera.transform.rotation = Quaternion.Slerp(_camera.transform.rotation,
                    Quaternion.LookRotation(dir.normalized, Vector3.up), 1f - Mathf.Exp(-10f * Time.deltaTime));
        }
    }
}
