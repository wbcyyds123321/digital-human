// Node-2 actor lifecycle: walk to a vehicle side, board, drive on the legal vehicle road
// network, stop, exit safely, plus the eternal-flame interaction and the short task-owner
// lease.  Persistent "in vehicle" mode is intentionally separate from the task owner.
using System;
using System.Collections.Generic;
using UnityEngine;
using Yixuan.AI.Tasks;
using Yixuan.Motion;
using Yixuan.Stage4;
using Yixuan.World;
using Yixuan.WorldMap;

namespace Yixuan.Dynamic
{
    /// <summary>
    /// Lane-aware road graph for vehicles. Roads are sampled on one or three lane lines
    /// (wide roads get +/- offsets so a vehicle can route around the tram platform and parked
    /// cars instead of driving on the exact centreline). Edges are only usable when the real
    /// vehicle body sweep fits; the entry search first drives FORWARD out of the parking spot
    /// along the current heading and only then joins a lane (long buses cannot rotate in place).
    /// </summary>
    public sealed class DynamicRoadGraph
    {
        public readonly List<Vector3> Nodes = new List<Vector3>();
        readonly List<List<int>> _edges = new List<List<int>>();
        readonly Dictionary<long, List<int>> _cells = new Dictionary<long, List<int>>();
        readonly List<string> _nodeRoad = new List<string>();
        readonly List<float> _nodeLane = new List<float>();
        const float CellSize = 8f;
        const float Step = 2.5f;

        public int Count { get { return Nodes.Count; } }

        public void Build(DynRoad[] roads)
        {
            Nodes.Clear();
            _edges.Clear();
            _cells.Clear();
            _nodeRoad.Clear();
            _nodeLane.Clear();
            if (roads == null) return;
            foreach (var road in roads)
            {
                if (road == null || road.rect == null || road.rect.Length < 4 || !road.drivable) continue;
                float x0 = Mathf.Min(road.rect[0], road.rect[2]);
                float x1 = Mathf.Max(road.rect[0], road.rect[2]);
                float z0 = Mathf.Min(road.rect[1], road.rect[3]);
                float z1 = Mathf.Max(road.rect[1], road.rect[3]);
                float width = Mathf.Min(x1 - x0, z1 - z0);
                bool axisX = road.axis != "z";
                float[] offsets = width >= 8f
                    ? new[] { -width * 0.28f, 0f, width * 0.28f }
                    : new[] { 0f };
                var lanes = new List<List<int>>();
                foreach (var offset in offsets)
                {
                    var samples = new List<int>();
                    if (axisX)
                    {
                        float z = (z0 + z1) * 0.5f + offset;
                        for (float x = x0 + 0.5f; x <= x1 - 0.5f; x += Step)
                            samples.Add(AddNode(new Vector3(x, 0f, z), road.id, offset));
                    }
                    else
                    {
                        float x = (x0 + x1) * 0.5f + offset;
                        for (float z = z0 + 0.5f; z <= z1 - 0.5f; z += Step)
                            samples.Add(AddNode(new Vector3(x, 0f, z), road.id, offset));
                    }
                    for (int i = 1; i < samples.Count; i++) Connect(samples[i - 1], samples[i]);
                    lanes.Add(samples);
                }
                // lane changes: straight across and diagonal (a bus needs a shallow diagonal)
                for (int lane = 1; lane < lanes.Count; lane++)
                {
                    var a = lanes[lane - 1];
                    var b = lanes[lane];
                    int n = Mathf.Min(a.Count, b.Count);
                    for (int k = 0; k < n; k++)
                    {
                        Connect(a[k], b[k]);
                        if (k + 2 < n) Connect(a[k], b[k + 2]);
                        if (k + 2 < b.Count) Connect(b[k], a[k + 2]);
                    }
                }
            }
            // intersections / road joins
            for (int i = 0; i < Nodes.Count; i++)
            {
                foreach (var j in NearbyNodeIndices(Nodes[i], 6.5f))
                {
                    if (j <= i) continue;
                    float dx = Nodes[i].x - Nodes[j].x;
                    float dz = Nodes[i].z - Nodes[j].z;
                    if (dx * dx + dz * dz <= 6.5f * 6.5f) Connect(i, j);
                }
            }
        }

        int AddNode(Vector3 p, string road, float offset)
        {
            p.y = WorldData.GroundHeightAt(p);
            int index = Nodes.Count;
            Nodes.Add(p);
            _edges.Add(new List<int>());
            _nodeRoad.Add(road ?? "");
            _nodeLane.Add(offset);
            long key = CellKey(p.x, p.z);
            if (!_cells.TryGetValue(key, out var list))
            {
                list = new List<int>();
                _cells[key] = list;
            }
            list.Add(index);
            return index;
        }

        void Connect(int a, int b)
        {
            if (a < 0 || b < 0 || a == b) return;
            if (!_edges[a].Contains(b)) _edges[a].Add(b);
            if (!_edges[b].Contains(a)) _edges[b].Add(a);
        }

        static long CellKey(float x, float z)
        {
            int cx = Mathf.FloorToInt(x / CellSize);
            int cz = Mathf.FloorToInt(z / CellSize);
            return ((long)cx << 32) ^ (uint)cz;
        }

        IEnumerable<int> NearbyNodeIndices(Vector3 p, float radius)
        {
            int minx = Mathf.FloorToInt((p.x - radius) / CellSize);
            int maxx = Mathf.FloorToInt((p.x + radius) / CellSize);
            int minz = Mathf.FloorToInt((p.z - radius) / CellSize);
            int maxz = Mathf.FloorToInt((p.z + radius) / CellSize);
            for (int cx = minx; cx <= maxx; cx++)
            {
                for (int cz = minz; cz <= maxz; cz++)
                {
                    long key = ((long)cx << 32) ^ (uint)cz;
                    if (_cells.TryGetValue(key, out var list))
                        foreach (var i in list) yield return i;
                }
            }
        }

        public int NearestNode(Vector2 p, float maxDistance)
        {
            int best = -1;
            float bestSqr = maxDistance * maxDistance;
            foreach (var i in NearbyNodeIndices(new Vector3(p.x, 0f, p.y), maxDistance))
            {
                float dx = Nodes[i].x - p.x;
                float dz = Nodes[i].z - p.y;
                float sqr = dx * dx + dz * dz;
                if (sqr < bestSqr)
                {
                    bestSqr = sqr;
                    best = i;
                }
            }
            return best;
        }

        bool SweepClear(VehicleRuntime v, Vector3 a, Vector3 b, float yaw, out string reason)
        {
            reason = "";
            var world = DynamicWorld.Instance;
            return world != null && world.IsVehicleSweepClear(v, a, b, yaw, out reason);
        }

        bool TurnClear(VehicleRuntime v, Vector3 pivot, float fromYaw, float toYaw)
        {
            var world = DynamicWorld.Instance;
            return world != null && world.IsVehicleTurnClear(v, pivot, fromYaw, toYaw, out _);
        }

        /// <summary>
        /// Build the drive-out entry: move forward along the current heading (checked with the
        /// real body) then connect to a nearby lane node. This is the general replacement for
        /// "rotate in place towards the nearest node", which fails for long vehicles.
        /// </summary>
        public bool TryBuildEntry(VehicleRuntime v, Vector3 start, out List<Vector3> waypoints,
                                  out int startNode, out string error)
        {
            return TryBuildEntry(v, start, out waypoints, out _, out startNode, out error);
        }

        bool TryBuildEntry(VehicleRuntime v, Vector3 start, out List<Vector3> waypoints,
                           out List<float> headings, out int startNode, out string error)
        {
            waypoints = null;
            headings = null;
            startNode = -1;
            error = "";
            if (v == null) { error = "vehicle_missing"; return false; }
            Vector3 forward = Quaternion.Euler(0f, v.CurrentYaw, 0f) * Vector3.forward;
            float bestCost = float.MaxValue;
            // Tight parking spots can have a bench just beyond the bonnet. A short reverse
            // along the existing heading creates turning space without rotating through it.
            float[] distances = { 0f, -0.75f, -1.5f, -2.5f, -5f, -8f, 2.5f, 5f, 8f, 11f, 14f, 18f };
            foreach (var d in distances)
            {
                Vector3 endpoint = start + forward * d;
                endpoint.y = WorldData.GroundHeightAt(endpoint);
                if (!WorldData.IsInsideBounds(endpoint)) continue;
                if (!SweepClear(v, start, endpoint, v.CurrentYaw, out _)) continue;
                foreach (var n in NearbyNodeIndices(endpoint, 20f))
                {
                    Vector3 nodePos = Nodes[n];
                    Vector3 delta = nodePos - endpoint;
                    float flat = new Vector2(delta.x, delta.z).magnitude;
                    if (flat < 0.2f || flat > 20f) continue;
                    float yawToward = Mathf.Atan2(delta.x, delta.z) * Mathf.Rad2Deg;
                    string reason;
                    if (!TurnClear(v, endpoint, v.CurrentYaw, yawToward)) continue;
                    if (!SweepClear(v, endpoint, nodePos, yawToward, out reason)) continue;
                    float turnPenalty = Mathf.Abs(Mathf.DeltaAngle(v.CurrentYaw, yawToward)) * 0.06f;
                    float cost = Mathf.Abs(d) * (d < 0f ? 1.15f : 1f) + flat + turnPenalty;
                    if (cost < bestCost)
                    {
                        bestCost = cost;
                        startNode = n;
                        waypoints = new List<Vector3> { start };
                        headings = new List<float> { v.CurrentYaw };
                        if (Mathf.Abs(d) > 0.1f)
                        {
                            waypoints.Add(endpoint);
                            headings.Add(v.CurrentYaw);
                        }
                        waypoints.Add(nodePos);
                        headings.Add(yawToward);
                    }
                }
            }
            if (startNode < 0)
            {
                // Fallback for tightly parked vehicles that face a wall: if there is enough
                // clearance, make a slow in-place heading change toward a reachable lane node
                // (still validated with the real body at every intermediate heading).
                foreach (var n in NearbyNodeIndices(start, 25f))
                {
                    Vector3 nodePos = Nodes[n];
                    Vector3 delta = nodePos - start;
                    float flat = new Vector2(delta.x, delta.z).magnitude;
                    if (flat < 1.0f || flat > 25f) continue;
                    float yawToward = Mathf.Atan2(delta.x, delta.z) * Mathf.Rad2Deg;
                    float turn = Mathf.Abs(Mathf.DeltaAngle(v.CurrentYaw, yawToward));
                    if (turn < 12f) continue;
                    if (!TurnClear(v, start, v.CurrentYaw, yawToward)) continue;
                    string reason;
                    if (!SweepClear(v, start, nodePos, yawToward, out reason)) continue;
                    float cost = flat + turn * 0.08f;
                    if (cost < bestCost)
                    {
                        bestCost = cost;
                        startNode = n;
                        waypoints = new List<Vector3> { start, nodePos };
                        headings = new List<float> { v.CurrentYaw, yawToward };
                    }
                }
            }
            if (startNode < 0)
            {
                error = "no_vehicle_entry";
                return false;
            }
            return true;
        }

        sealed class RoadState
        {
            public int Node;
            public int Previous;
            public float Yaw;
            public float Cost;
            public RoadState Parent;
            public bool Closed;
        }

        struct RoadOpenItem
        {
            public RoadState State;
            public float Cost;
            public float Score;
        }

        static void PushOpen(List<RoadOpenItem> heap, RoadState state, float score)
        {
            var item = new RoadOpenItem { State = state, Cost = state.Cost, Score = score };
            int index = heap.Count;
            heap.Add(item);
            while (index > 0)
            {
                int parent = (index - 1) / 2;
                if (heap[parent].Score <= item.Score) break;
                heap[index] = heap[parent];
                index = parent;
            }
            heap[index] = item;
        }

        static RoadOpenItem PopOpen(List<RoadOpenItem> heap)
        {
            RoadOpenItem first = heap[0];
            RoadOpenItem last = heap[heap.Count - 1];
            heap.RemoveAt(heap.Count - 1);
            if (heap.Count == 0) return first;
            int index = 0;
            while (index * 2 + 1 < heap.Count)
            {
                int child = index * 2 + 1;
                if (child + 1 < heap.Count && heap[child + 1].Score < heap[child].Score) child++;
                if (heap[child].Score >= last.Score) break;
                heap[index] = heap[child];
                index = child;
            }
            heap[index] = last;
            return first;
        }

        float GoalHeuristic(int node, List<int> goals)
        {
            float distance = float.MaxValue;
            foreach (var goal in goals) distance = Mathf.Min(distance, Vector3.Distance(Nodes[node], Nodes[goal]));
            return distance;
        }

        List<int> AStar(VehicleRuntime v, int start, List<int> goals, float startYaw)
        {
            if (start < 0 || start >= Nodes.Count || goals.Count == 0) return null;
            // An incoming edge determines the actual body heading. A node alone is not a
            // complete search state: reaching the same corner from another road may be the
            // only way to make its next turn without clipping a nearby object.
            var states = new Dictionary<long, RoadState>();
            var sweepCache = new Dictionary<long, bool>();
            var turnCache = new Dictionary<long, bool>();
            var open = new List<RoadOpenItem>();
            var initial = new RoadState { Node = start, Previous = -1, Yaw = startYaw, Cost = 0f };
            states[start] = initial;
            PushOpen(open, initial, GoalHeuristic(start, goals));
            while (open.Count > 0)
            {
                RoadOpenItem item = PopOpen(open);
                RoadState current = item.State;
                if (current.Closed || item.Cost > current.Cost) continue;
                if (goals.Contains(current.Node))
                {
                    var chain = new List<int>();
                    for (RoadState state = current; state != null; state = state.Parent) chain.Add(state.Node);
                    chain.Reverse();
                    return chain;
                }
                current.Closed = true;
                foreach (var next in _edges[current.Node])
                {
                    long key = ((long)(current.Node + 1) << 32) | (uint)next;
                    if (states.TryGetValue(key, out var arrival) && arrival.Closed) continue;
                    Vector3 a = Nodes[current.Node];
                    Vector3 b = Nodes[next];
                    Vector3 delta = b - a;
                    if (Mathf.Abs(delta.x) + Mathf.Abs(delta.z) < 0.05f) continue;
                    float yaw = Mathf.Atan2(delta.x, delta.z) * Mathf.Rad2Deg;
                    if (!sweepCache.TryGetValue(key, out bool clear))
                    {
                        clear = SweepClear(v, a, b, yaw, out _);
                        sweepCache[key] = clear;
                    }
                    if (!clear) continue;
                    long turnKey = ((long)(current.Previous + 1) << 42) |
                                   ((long)current.Node << 21) | (uint)next;
                    if (!turnCache.TryGetValue(turnKey, out clear))
                    {
                        clear = TurnClear(v, a, current.Yaw, yaw);
                        turnCache[turnKey] = clear;
                    }
                    if (!clear) continue;
                    float tentative = current.Cost + new Vector2(delta.x, delta.z).magnitude +
                                      Mathf.Abs(Mathf.DeltaAngle(current.Yaw, yaw)) * 0.02f;
                    if (arrival != null && tentative >= arrival.Cost) continue;
                    if (arrival == null)
                    {
                        arrival = new RoadState { Node = next, Previous = current.Node, Yaw = yaw };
                        states[key] = arrival;
                    }
                    arrival.Cost = tentative;
                    arrival.Parent = current;
                    PushOpen(open, arrival, tentative + GoalHeuristic(next, goals));
                }
            }
            return null;
        }

        public bool FindPath(VehicleRuntime v, Vector3 start, Vector2 goal, float maxGoalDistance,
                             out List<Vector3> path, out string error)
        {
            return FindPath(v, start, goal, maxGoalDistance, out path, out _, out error);
        }

        public bool FindPath(VehicleRuntime v, Vector3 start, Vector2 goal, float maxGoalDistance,
                             out List<Vector3> path, out List<float> headings, out string error)
        {
            path = new List<Vector3>();
            headings = new List<float>();
            error = "";
            List<Vector3> entry;
            List<float> entryHeadings;
            int startNode;
            if (!TryBuildEntry(v, start, out entry, out entryHeadings, out startNode, out error)) return false;
            var goalNodes = new List<int>();
            foreach (var n in NearbyNodeIndices(new Vector3(goal.x, 0f, goal.y), maxGoalDistance))
            {
                float d = Vector2.Distance(new Vector2(Nodes[n].x, Nodes[n].z), goal);
                if (d <= maxGoalDistance) goalNodes.Add(n);
            }
            goalNodes.Sort((a, b) =>
                Vector2.Distance(new Vector2(Nodes[a].x, Nodes[a].z), goal)
                .CompareTo(Vector2.Distance(new Vector2(Nodes[b].x, Nodes[b].z), goal)));
            if (goalNodes.Count > 8) goalNodes.RemoveRange(8, goalNodes.Count - 8);
            // One multi-goal search shares collision checks and heading states instead of
            // rescanning the whole road graph separately for every nearby parking node.
            var bestChain = AStar(v, startNode, goalNodes, entryHeadings[entryHeadings.Count - 1]);
            if (bestChain == null)
            {
                error = "no_vehicle_route";
                return false;
            }
            path = new List<Vector3>(entry);
            headings = new List<float>(entryHeadings);
            // entry already ends with the start node; add the graph chain after it
            for (int i = 1; i < bestChain.Count; i++)
            {
                Vector3 delta = Nodes[bestChain[i]] - Nodes[bestChain[i - 1]];
                path.Add(Nodes[bestChain[i]]);
                headings.Add(Mathf.Atan2(delta.x, delta.z) * Mathf.Rad2Deg);
            }
            if (path.Count < 2)
            {
                error = "empty_vehicle_path";
                return false;
            }
            return true;
        }
    }

    public sealed class DynamicActorSystem : MonoBehaviour
    {
        public enum Mode { OnFoot, Boarding, InVehicle }

        public static DynamicActorSystem Instance { get; private set; }

        public Mode CurrentMode { get; private set; } = Mode.OnFoot;
        public string ActiveActor { get { return CurrentMode == Mode.InVehicle ? "vehicle" : "character"; } }
        public string CurrentVehicleId { get; private set; }
        public string LastStatus { get; private set; } = "idle";
        public string LastError { get; private set; } = "";
        public int RequestGeneration { get; private set; }
        public string TaskOwner { get; private set; }
        public bool InVehicle { get { return CurrentMode == Mode.InVehicle; } }
        public bool VehicleMoving { get { return _driving; } }

        public string DriveDebug
        {
            get
            {
                return "driving=" + _driving + " index=" + _driveIndex + " count=" +
                       (_drivePath != null ? _drivePath.Count : -1) + " dist=" +
                       LastDriveDistance.ToString("F1") + " status=" + LastStatus;
            }
        }
        public bool VehicleParked { get { return InVehicle && !_driving; } }
        public int CompletedDriveRequests { get; private set; }
        public float LastDriveDistance;
        public float LastDriveSeconds;
        public Vector3 LastParkPoint;
        public Vector3 LastExitPoint;
        public Vector3 LastApproachPoint;
        public string LastExitStatus = "";
        public DynamicWorld World { get; private set; }
        public DynamicRoadGraph RoadGraph { get; private set; }
        public string LastBoardVehicleId { get; private set; }
        public string LastFlameStatus { get; private set; } = "";
        public int FlameChanges { get { return World != null && World.Flame != null ? World.Flame.ChangeCount : 0; } }
        public bool FlameLit { get { return World != null && World.Flame != null && World.Flame.Lit; } }

        Vector3 _walkTarget;
        VehicleRuntime _boardVehicle;
        float _walkBudget;
        float _walkElapsed;
        List<Vector3> _drivePath;
        List<float> _driveHeadings;
        int _driveIndex;
        float _driveSpeed;
        float _driveElapsed;
        bool _driving;
        float _driveStartRealtime;
        public sealed class TaskLease
        {
            public readonly string Owner;
            public readonly int Generation;
            internal TaskLease(string owner, int generation) { Owner = owner; Generation = generation; }
        }
        TaskLease _taskLease;
        int _taskGeneration;
        string _boardOwner;
        bool _ownsBoardAuthority;
        Vector3 _boardPivot;
        float _boardYaw;
        bool _savedWalking;
        bool _walkingCaptured;
        GroundSnap _vehicleSnap;
        bool _savedSnap;
        bool _snapCaptured;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetSession()
        {
            Instance = null;
        }

        public static DynamicActorSystem EnsureInstance()
        {
            if (Instance != null) return Instance;
            var existing = FindObjectOfType<DynamicActorSystem>();
            if (existing != null)
            {
                Instance = existing;
                return existing;
            }
            var go = new GameObject("DynamicActorSystem");
            Instance = go.AddComponent<DynamicActorSystem>();
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
            World = DynamicWorld.EnsureInstance();
            if (World == null || !World.Initialize())
            {
                LastError = "dynamic_world_not_ready:" + (World != null ? World.Error : "null");
                return false;
            }
            RoadGraph = new DynamicRoadGraph();
            RoadGraph.Build(DynamicData.Root.roads);
            LastStatus = "ready";
            return true;
        }

        // ---------------------------------------------------------------- owner
        public bool AcquireTask(string owner)
        {
            return AcquireTaskLease(owner) != null;
        }

        public TaskLease AcquireTaskLease(string owner)
        {
            if (string.IsNullOrEmpty(owner) || !isActiveAndEnabled) return null;
            if (_taskLease != null) return _taskLease.Owner == owner ? _taskLease : null;
            if (CurrentMode == Mode.Boarding || _driving) return null;
            _taskLease = new TaskLease(owner, ++_taskGeneration);
            TaskOwner = owner;
            return _taskLease;
        }

        public bool ReleaseTask(string owner)
        {
            return _taskLease != null && _taskLease.Owner == owner && ReleaseTask(_taskLease);
        }

        public bool OwnsTask(TaskLease lease)
        {
            return lease != null && ReferenceEquals(_taskLease, lease);
        }

        public bool CancelOperation(TaskLease lease, int operationGeneration, out string status)
        {
            status = "stale_operation_ignored";
            if (!OwnsTask(lease) || RequestGeneration != operationGeneration) return false;
            using (TaskAuthority.Enter(lease.Owner)) return StopCurrent(out status);
        }

        public bool ReleaseTask(TaskLease lease)
        {
            if (!OwnsTask(lease)) return false;
            // Stop only work belonging to this lease. Persistent occupancy survives its release.
            if (CurrentMode == Mode.Boarding || _driving)
                using (TaskAuthority.Enter(lease.Owner)) StopCurrent(out _);
            _taskLease = null;
            TaskOwner = null;
            return true;
        }

        bool CanControl(out string status)
        {
            status = "";
            if (!isActiveAndEnabled) { status = "dynamic_actor_disabled"; return false; }
            if (_taskLease != null && TaskAuthority.Caller != _taskLease.Owner)
            { status = "dynamic_owned_by_another_task"; return false; }
            if (_ownsBoardAuthority && TaskAuthority.Caller == null)
            {
                if (TaskAuthority.CanContinue(_boardOwner, 3)) return true;
                status = "authority_busy";
                return false;
            }
            if (!TaskAuthority.Allows(3)) { status = "authority_busy"; return false; }
            return true;
        }

        void ReleaseBoardAuthority()
        {
            if (_ownsBoardAuthority && !string.IsNullOrEmpty(_boardOwner)) TaskAuthority.Release(_boardOwner);
            _ownsBoardAuthority = false;
            _boardOwner = null;
        }

        public bool OwnsVehicle(string entityId)
        {
            return InVehicle && CurrentVehicleId == entityId;
        }

        // --------------------------------------------------------------- query
        public string Describe()
        {
            string vehicle = InVehicle ? CurrentVehicleId : "";
            return "mode=" + CurrentMode + " actor=" + ActiveActor + " vehicle=" + vehicle +
                   " moving=" + _driving + " status=" + LastStatus + " error=" + LastError +
                   " flame=" + (World != null && World.Flame != null ? (World.Flame.Lit ? "lit" : "extinguished") : "missing");
        }

        public string DescribeFlame()
        {
            if (World == null || World.Flame == null) return "flame_not_loaded";
            return (World.Flame.Lit ? "lit" : "extinguished") + ";changes=" + World.Flame.ChangeCount;
        }

        // ------------------------------------------------------------- boarding
        /// <summary>
        /// Resolve a real walkable boarding point for a vehicle: exported side candidates first,
        /// then a walkable ring around the current footprint, then the nearest pedestrian nav node.
        /// The point must be inside the map, walkable and clear of static/dynamic obstacles.
        /// </summary>
        public bool TryResolveBoardingPoint(VehicleRuntime v, Vector3 character,
                                            out Vector3 point, out string detail)
        {
            point = Vector3.zero;
            detail = "";
            int candidatesTried = 0, walkable = 0, clear = 0, pathOk = 0;
            if (v == null || World == null) { detail = "vehicle_missing"; return false; }
            var candidates = new List<DynBoarding>(DynamicData.Boarding(v.Spec));
            candidates.RemoveAll(candidate => candidate == null);
            candidates.Sort((a, b) => a.road_distance_m.CompareTo(b.road_distance_m));
            foreach (var candidate in candidates)
            {
                candidatesTried++;
                if (candidate == null || candidate.point == null || candidate.point.Length < 2) continue;
                // Exported points describe the original pose, not a permanent parking place.
                Vector3 original = new Vector3(candidate.point[0], v.Pivot0.y, candidate.point[1]);
                Vector3 p = v.CurrentPivot + Quaternion.Euler(0f, v.CurrentYaw - v.Yaw0, 0f) * (original - v.Pivot0);
                p.y = WorldData.GroundHeightAt(p);
                if (DistanceFromVehicle(v, p) > 2.5f) continue;
                if (!WorldData.IsInsideBounds(p)) continue;
                if (!WorldData.IsWalkable(p)) continue;
                walkable++;
                if (!World.IsPointClear(p, 0.25f, null)) continue;
                clear++;
                if (!TryBoardingPath(character, p, out var candidatePath)) continue;
                pathOk++;
                point = p;
                detail = "candidate side=" + candidate.side + " path=" + candidatePath.Count;
                return true;
            }
            for (int ring = 0; ring < 3; ring++)
            {
                float padding = 1.0f + ring * 0.5f;
                for (int i = 0; i < 16; i++)
                {
                    float angle = i * Mathf.PI * 2f / 16f;
                    Vector3 p = v.Center + v.Right * (Mathf.Cos(angle) * (v.Spec.width_m * 0.5f + padding)) +
                                v.Forward * (Mathf.Sin(angle) * (v.Spec.length_m * 0.5f + padding));
                    p.y = WorldData.GroundHeightAt(p);
                    if (DistanceFromVehicle(v, p) > 2.5f) continue;
                    if (!WorldData.IsInsideBounds(p)) continue;
                    if (!WorldData.IsWalkable(p)) continue;
                    walkable++;
                    if (!World.IsPointClear(p, 0.25f, null)) continue;
                    clear++;
                    if (!TryBoardingPath(character, p, out var ringPath)) continue;
                    pathOk++;
                    point = p;
                    detail = "ring padding=" + padding.ToString("F2") + " path=" + ringPath.Count;
                    return true;
                }
            }
            // last resort: a legal pedestrian navigation node close to the car side
            if (World.Map != null)
            {
                int node = World.Map.NearestUsableNode(v.Center, 260);
                if (node >= 0)
                {
                    Vector3 nodePos = World.Map.NodePosition(node);
                    nodePos.y = WorldData.GroundHeightAt(nodePos);
                    float distance = Vector2.Distance(new Vector2(nodePos.x, nodePos.z),
                                                      new Vector2(v.Center.x, v.Center.z));
                    if (DistanceFromVehicle(v, nodePos) <= 2.5f && WorldData.IsWalkable(nodePos) &&
                        World.IsPointClear(nodePos, 0.25f, null))
                    {
                        if (TryBoardingPath(character, nodePos, out _))
                        {
                            point = nodePos;
                            detail = "nav_node distance=" + distance.ToString("F2");
                            return true;
                        }
                    }
                    detail = "nav_node distance=" + distance.ToString("F2");
                }
            }
            detail = detail + " candidates=" + candidatesTried + " walkable=" + walkable +
                     " clear=" + clear + " path=" + pathOk +
                     " center=" + v.Center.ToString("F1");
            return false;
        }

        static float DistanceFromVehicle(VehicleRuntime v, Vector3 point)
        {
            Vector3 local = Quaternion.Euler(0f, -v.CurrentYaw, 0f) * (point - v.Center);
            float x = Mathf.Max(0f, Mathf.Abs(local.x) - v.Spec.width_m * 0.5f);
            float z = Mathf.Max(0f, Mathf.Abs(local.z) - v.Spec.length_m * 0.5f);
            return new Vector2(x, z).magnitude;
        }

        static bool TryBoardingPath(Vector3 start, Vector3 destination, out List<Vector3> path)
        {
            var found = WorldData.FindPath(start, destination);
            path = null;
            // A direct reachable path legitimately consists of just its destination.
            if (found == null || found.Count == 0) return false;
            var normalized = new List<Vector3> { start };
            Vector3 previous = start;
            foreach (var waypoint in found)
            {
                if (!WorldData.LineWalkable(previous, waypoint)) return false;
                if ((waypoint - previous).sqrMagnitude > 0.0001f) normalized.Add(waypoint);
                previous = waypoint;
            }
            if (Vector2.Distance(new Vector2(previous.x, previous.z), new Vector2(destination.x, destination.z)) > 0.3f)
                return false;
            if (normalized.Count == 1) normalized.Add(destination);
            path = normalized;
            return true;
        }

        public bool BeginBoard(string vehicleId, out string status)
        {
            status = "";
            if (World == null || !World.Ready)
            {
                status = "dynamic_world_not_ready";
                return false;
            }
            if (CurrentMode == Mode.InVehicle)
            {
                status = "already_in_vehicle:" + CurrentVehicleId;
                return false;
            }
            if (CurrentMode == Mode.Boarding)
            {
                status = "already_boarding:" + LastBoardVehicleId;
                return false;
            }
            if (Stage4SeatController.BlocksNormalWalking)
            {
                status = "seated_must_stand_first";
                return false;
            }
            if (!CanControl(out status)) return false;
            if (World.Loco == null) { status = "loco_missing"; return false; }
            var v = World.GetVehicle(vehicleId);
            if (v == null)
            {
                status = "unknown_vehicle:" + vehicleId;
                return false;
            }
            if (!v.Spec.available)
            {
                status = "vehicle_unavailable:" + v.Spec.unavailable_reason;
                return false;
            }
            _boardOwner = _taskLease != null ? _taskLease.Owner : "dynamic.actor." + Guid.NewGuid().ToString("N");
            _ownsBoardAuthority = _taskLease == null;
            if (!TaskAuthority.Acquire(_boardOwner, 3))
            {
                ReleaseBoardAuthority();
                status = "authority_busy";
                return false;
            }
            Vector3 character = World.CharacterPosition;
            Vector3 chosen;
            string detail;
            if (!TryResolveBoardingPoint(v, character, out chosen, out detail))
            {
                ReleaseBoardAuthority();
                status = "no_approach_path: " + detail;
                return false;
            }
            if (!TryBoardingPath(character, chosen, out var path))
            {
                ReleaseBoardAuthority();
                status = "no_approach_path: path_missing after resolve " + detail;
                return false;
            }
            bool started;
            _savedWalking = World.Loco.WalkingEnabled;
            _walkingCaptured = true;
            using (TaskAuthority.Enter(_boardOwner))
            {
                World.Loco.Stop("dynamic board", "cancelled");
                World.Loco.WalkingEnabled = true;
                started = World.Loco.MoveAlongPath(path);
            }
            if (!started)
            {
                World.Loco.WalkingEnabled = _savedWalking;
                _walkingCaptured = false;
                ReleaseBoardAuthority();
                status = "walk_start_failed:" + World.Loco.LastError;
                return false;
            }
            float length = 0f;
            Vector3 previous = character;
            foreach (var p in path)
            {
                length += Vector3.Distance(previous, p);
                previous = p;
            }
            _boardVehicle = v;
            _boardPivot = v.CurrentPivot;
            _boardYaw = v.CurrentYaw;
            _walkTarget = chosen;
            _walkElapsed = 0f;
            _walkBudget = Mathf.Clamp(length / Mathf.Max(0.3f, World.Loco.WalkSpeed) * 2.2f + 12f, 15f, 400f);
            LastBoardVehicleId = vehicleId;
            LastApproachPoint = chosen;
            CurrentMode = Mode.Boarding;
            RequestGeneration++;
            LastStatus = "boarding";
            LastError = "";
            status = "boarding:" + vehicleId;
            return true;
        }

        void EnterVehicle(VehicleRuntime v)
        {
            if (!_walkingCaptured && World.Loco != null)
            {
                _savedWalking = World.Loco.WalkingEnabled;
                _walkingCaptured = true;
            }
            _vehicleSnap = World.Character != null ? World.Character.GetComponent<GroundSnap>() : null;
            if (_vehicleSnap != null)
            {
                _savedSnap = _vehicleSnap.Enabled;
                _snapCaptured = true;
                _vehicleSnap.Enabled = false;
            }
            using (TaskAuthority.Enter(_boardOwner ?? TaskOwner))
            {
                if (World.Loco != null) { World.Loco.Stop("entered vehicle", "cancelled"); World.Loco.WalkingEnabled = false; }
                if (World.Character != null) World.Character.PlayGesture("idle");
            }
            CurrentMode = Mode.InVehicle;
            CurrentVehicleId = v.Spec.entity_id;
            _driving = false;
            _drivePath = null;
            World.FollowCharacterToVehicle(v);
            World.HideCharacter();
            World.EnterVehicleCamera(v);
            ReleaseBoardAuthority();
            LastStatus = "in_vehicle";
            LastError = "";
        }

        // --------------------------------------------------------------- driving
        /// <summary>Cheap pre-check: can this vehicle physically leave its parking spot to the
        /// nearest legal road node (body sweep clear)?  Used for honest availability reporting and
        /// by tests to pick a vehicle that can actually start.</summary>
        public bool CanStartDriving(VehicleRuntime v, out string reason)
        {
            reason = "";
            if (v == null || v.Spec == null) { reason = "vehicle_missing"; return false; }
            if (!v.Spec.available) { reason = "unavailable:" + v.Spec.unavailable_reason; return false; }
            List<Vector3> entry;
            int node;
            string entryError;
            if (!RoadGraph.TryBuildEntry(v, v.CurrentPivot, out entry, out node, out entryError))
            {
                reason = entryError;
                return false;
            }
            return true;
        }

        public bool DriveTo(Vector2 target, out string status)
        {
            status = "";
            if (!CanControl(out status)) return false;
            if (!InVehicle)
            {
                status = "not_in_vehicle";
                return false;
            }
            if (_driving)
            {
                status = "already_driving";
                return false;
            }
            var v = World.GetVehicle(CurrentVehicleId);
            if (v == null)
            {
                status = "vehicle_missing";
                return false;
            }
            List<Vector3> path;
            List<float> headings;
            string error;
            if (!RoadGraph.FindPath(v, v.CurrentPivot, target, 45f, out path, out headings, out error))
            {
                status = "route_blocked_for_vehicle:" + error;
                LastError = status;
                return false;
            }
            _drivePath = path;
            _driveHeadings = headings;
            _driveIndex = 1;
            _driveSpeed = 0f;
            _driveElapsed = 0f;
            _driveStartRealtime = Time.realtimeSinceStartup;
            LastDriveDistance = 0f;
            _driving = true;
            RequestGeneration++;
            LastStatus = "driving";
            LastError = "";
            status = "driving";
            return true;
        }

        public bool DriveToAnchor(string anchorIdOrLabel, out string status)
        {
            status = "";
            if (WorldEntityCatalog.Anchors == null)
            {
                status = "no_anchors";
                return false;
            }
            string needle = (anchorIdOrLabel ?? "").Trim().ToLowerInvariant();
            foreach (var anchor in WorldEntityCatalog.Anchors)
            {
                if (anchor == null) continue;
                string id = (anchor.id ?? "").ToLowerInvariant();
                string label = (anchor.label ?? "").ToLowerInvariant();
                if (id == needle || label == needle || label.Contains(needle) || needle.Contains(label))
                {
                    var p = new Vector3(anchor.pos[0], anchor.pos[1], anchor.pos[2]);
                    return DriveTo(new Vector2(p.x, p.z), out status);
                }
            }
            status = "unknown_anchor:" + anchorIdOrLabel;
            return false;
        }

        public bool DriveToEntity(string entityId, out string status)
        {
            status = "";
            var entity = WorldEntityCatalog.Get(entityId);
            if (entity == null)
            {
                status = "unknown_entity:" + entityId;
                return false;
            }
            Vector3 position = WorldEntityCatalog.WorldPosition(entity);
            int roadNode = RoadGraph.NearestNode(new Vector2(position.x, position.z), 12f);
            if (roadNode < 0)
            {
                status = "destination_not_reachable_by_vehicle";
                LastError = status;
                return false;
            }
            return DriveTo(new Vector2(position.x, position.z), out status);
        }

        // ---------------------------------------------------------------- stop
        public bool StopCurrent(out string status)
        {
            if (!CanControl(out status)) return false;
            return StopCurrentCore(out status);
        }

        bool StopCurrentCore(out string status)
        {
            status = "";
            if (CurrentMode == Mode.Boarding)
            {
                using (TaskAuthority.Enter(_boardOwner))
                {
                    if (World != null && World.Loco != null) World.Loco.Stop("dynamic board cancel", "cancelled");
                }
                ReleaseBoardAuthority();
                if (_walkingCaptured && World != null && World.Loco != null) World.Loco.WalkingEnabled = _savedWalking;
                _walkingCaptured = false;
                _boardVehicle = null;
                CurrentMode = Mode.OnFoot;
                RequestGeneration++;
                LastStatus = "boarding_cancelled";
                status = "boarding_cancelled";
                return true;
            }
            if (InVehicle)
            {
                if (_driving)
                {
                    _driving = false;
                    _drivePath = null;
                    _driveSpeed = 0f;
                    RequestGeneration++;
                    LastStatus = "stopped_in_vehicle";
                    LastExitStatus = "stopped_in_vehicle";
                    status = "stopped_in_vehicle";
                    if (World != null) World.MarkDynamicMoved();
                    return true;
                }
                status = "already_stopped";
                return true;
            }
            status = "on_foot_not_ours";
            return false;
        }

        // ---------------------------------------------------------------- exit
        public void ForceExitForTest(string reason)
        {
            if (!InVehicle) return;
            var v = World.GetVehicle(CurrentVehicleId);
            if (v != null)
            {
                Vector3 side = v.CurrentPivot + v.Right * (v.Spec.width_m * 0.5f + 2.2f);
                side.y = WorldData.GroundHeightAt(side) + 0.02f;
                World.PlaceCharacterAt(side);
            }
            World.ShowCharacter();
            RestoreCharacterOnFoot();
            World.ExitVehicleCamera();
            CurrentMode = Mode.OnFoot;
            CurrentVehicleId = null;
            _driving = false;
            _drivePath = null;
            RequestGeneration++;
            LastStatus = "test_forced_exit";
            LastExitStatus = "test_forced_exit:" + reason;
            World.MarkDynamicMoved();
        }

        public bool TryExitVehicle(out string status)
        {
            status = "";
            if (!CanControl(out status)) return false;
            if (!InVehicle)
            {
                status = "not_in_vehicle";
                return false;
            }
            if (_driving)
            {
                status = "must_stop_first";
                LastExitStatus = status;
                return false;
            }
            var v = World.GetVehicle(CurrentVehicleId);
            if (v == null)
            {
                status = "vehicle_missing";
                return false;
            }
            Vector3 pivot = v.CurrentPivot;
            Vector3 right = v.Right;
            float half = v.Spec.width_m * 0.5f;
            var candidates = new List<Vector3>
            {
                pivot + right * -(half + 1.25f),
                pivot + right * (half + 1.25f),
            };
            Vector3 chosen = Vector3.zero;
            bool found = false;
            foreach (var candidate in candidates)
            {
                Vector3 point = candidate;
                if (!WorldData.IsInsideBounds(point)) continue;
                float y = WorldData.GroundHeightAt(point);
                point.y = y + 0.02f;
                if (!WorldData.IsWalkable(point)) continue;
                if (!World.IsPointClear(point, 0.4f, v.Spec.entity_id)) continue;
                // safe footing: inside the walkable map and not blocked; a road-shoulder exit
                // next to the parked vehicle is legal. The pedestrian nav graph is only used as
                // a preference when a line-of-sight node exists nearby.
                chosen = point;
                found = true;
                break;
            }
            if (!found && World.Map != null)
            {
                // Parking often happens on the roadway itself. Step to the nearest legal
                // pedestrian node within a short, line-of-sight distance instead of treating
                // a road lane as a sidewalk.
                int node = World.Map.NearestUsableNode(pivot, 10);
                if (node >= 0)
                {
                    Vector3 nodePos = World.Map.NodePosition(node);
                    nodePos.y = WorldData.GroundHeightAt(nodePos) + 0.02f;
                    float distance = Vector2.Distance(
                        new Vector2(nodePos.x, nodePos.z), new Vector2(pivot.x, pivot.z));
                    if (distance <= 6.0f && WorldData.IsInsideBounds(nodePos) &&
                        WorldData.IsWalkable(nodePos) &&
                        World.IsPointClear(nodePos, 0.4f, v.Spec.entity_id))
                    {
                        chosen = nodePos;
                        found = true;
                    }
                }
            }
            if (!found)
            {
                status = "no_safe_exit";
                LastExitStatus = status;
                LastStatus = "in_vehicle_no_exit";
                LastError = "no_safe_exit";
                return false;
            }
            World.PlaceCharacterAt(chosen);
            World.ShowCharacter();
            RestoreCharacterOnFoot();
            World.ExitVehicleCamera();
            CurrentMode = Mode.OnFoot;
            LastExitPoint = chosen;
            CurrentVehicleId = null;
            RequestGeneration++;
            LastStatus = "exited";
            LastExitStatus = "exited";
            LastError = "";
            World.MarkDynamicMoved();
            status = "exited";
            return true;
        }

        void RestoreCharacterOnFoot()
        {
            if (World == null) return;
            if (World.Character != null) World.Character.PlayGesture("idle");
            if (_snapCaptured && _vehicleSnap != null)
            {
                // Reset before enabling LateUpdate; the standing pose evaluates next frame.
                _vehicleSnap.ResetAfterReposition();
                _vehicleSnap.Enabled = _savedSnap;
            }
            _snapCaptured = false;
            _vehicleSnap = null;
            if (World.Loco != null && _walkingCaptured) World.Loco.WalkingEnabled = _savedWalking;
            _walkingCaptured = false;
        }

        // --------------------------------------------------------------- flame
        public bool SetFlame(bool lit, out string status)
        {
            status = "";
            if (!CanControl(out status)) return false;
            if (World == null || World.Flame == null)
            {
                status = "flame_not_loaded";
                return false;
            }
            if (CurrentMode != Mode.OnFoot)
            {
                status = "must_be_on_foot";
                return false;
            }
            Vector3 character = World.CharacterPosition;
            bool near = false;
            foreach (var p in World.Flame.Spec.operation_points)
            {
                if (p == null || p.point == null || p.point.Length < 2) continue;
                Vector3 point = new Vector3(p.point[0], 0f, p.point[1]);
                float distance = Vector2.Distance(new Vector2(character.x, character.z), new Vector2(point.x, point.z));
                if (distance <= World.Flame.Spec.interaction_distance_m + 0.9f) near = true;
            }
            if (!near)
            {
                status = "too_far_from_flame_operation_point";
                LastError = status;
                return false;
            }
            string flameStatus;
            bool ok = World.SetFlameState(lit, out flameStatus);
            status = flameStatus;
            LastFlameStatus = flameStatus;
            if (!ok) LastError = flameStatus;
            return ok;
        }

        // --------------------------------------------------------------- update
        void Update()
        {
            if (World == null || !World.Ready) return;
            if (CurrentMode == Mode.Boarding)
            {
                UpdateBoarding();
                return;
            }
            if (InVehicle) UpdateVehicle();
        }

        void UpdateBoarding()
        {
            _walkElapsed += Time.deltaTime;
            var loco = World.Loco;
            if (loco == null)
            {
                AbortBoarding("loco_missing");
                return;
            }
            if (_boardVehicle == null || !TaskAuthority.CanContinue(_boardOwner, 3))
            { AbortBoarding("boarding_owner_or_vehicle_lost"); return; }
            if (Vector3.Distance(_boardVehicle.CurrentPivot, _boardPivot) > 0.35f ||
                Mathf.Abs(Mathf.DeltaAngle(_boardVehicle.CurrentYaw, _boardYaw)) > 5f)
            { AbortBoarding("vehicle_moved_during_approach"); return; }
            float distance = Vector2.Distance(
                new Vector2(loco.transform.position.x, loco.transform.position.z),
                new Vector2(_walkTarget.x, _walkTarget.z));
            if (!loco.HasTarget && loco.LastStatus == "arrived" && distance <= 0.9f &&
                DistanceFromVehicle(_boardVehicle, loco.transform.position) <= 3.4f)
            {
                EnterVehicle(_boardVehicle);
                return;
            }
            if (!loco.HasTarget)
            {
                AbortBoarding("approach_finished_off_target:" + loco.LastError + " d=" + distance.ToString("F2"));
                return;
            }
            if (_walkElapsed > _walkBudget)
            {
                AbortBoarding("approach_timeout");
            }
        }

        void AbortBoarding(string reason)
        {
            using (TaskAuthority.Enter(_boardOwner))
            {
                if (World.Loco != null) World.Loco.Stop("dynamic board abort", "cancelled");
            }
            ReleaseBoardAuthority();
            if (_walkingCaptured && World.Loco != null) World.Loco.WalkingEnabled = _savedWalking;
            _walkingCaptured = false;
            _boardVehicle = null;
            CurrentMode = Mode.OnFoot;
            LastStatus = "boarding_failed";
            LastError = reason;
            RequestGeneration++;
        }

        void OnDisable()
        {
            // Driving runs in Update, so coroutine shutdown alone cannot stop it.
            using (TaskAuthority.Enter(TaskOwner ?? _boardOwner)) StopCurrentCore(out _);
            ReleaseBoardAuthority();
            _taskLease = null;
            TaskOwner = null;
        }

        void OnDestroy()
        {
            OnDisable();
            if (Instance == this) Instance = null;
        }

        void UpdateVehicle()
        {
            var v = World.GetVehicle(CurrentVehicleId);
            if (v == null) return;
            World.FollowCharacterToVehicle(v);
            if (!_driving || _drivePath == null || _driveIndex >= _drivePath.Count) return;
            float remainingBudget = Mathf.Clamp(LastDriveDistance / Mathf.Max(1f, v.Spec.drive.max_speed_mps) * 3f + 30f, 30f, 900f);
            _driveElapsed += Time.deltaTime;
            if (_driveElapsed > remainingBudget)
            {
                _driving = false;
                LastStatus = "drive_timeout_stopped";
                LastError = "drive_timeout";
                return;
            }
            Vector3 target = _drivePath[_driveIndex];
            Vector3 current = v.CurrentPivot;
            Vector3 delta = target - current;
            delta.y = 0f;
            float distance = delta.magnitude;
            if (distance <= 0.001f)
            {
                _driveSpeed = 0f;
                _driveIndex++;
                if (_driveIndex >= _drivePath.Count)
                {
                    _driving = false;
                    LastStatus = "parked";
                    LastParkPoint = v.CurrentPivot;
                    CompletedDriveRequests++;
                    LastDriveSeconds = Time.realtimeSinceStartup - _driveStartRealtime;
                    World.MarkDynamicMoved();
                    return;
                }
                return;
            }
            // Reproduce the planned manoeuvre exactly: first turn at the waypoint, then
            // translate at that heading. Entry headings can retain the original yaw while
            // reversing out. Sliding and turning simultaneously used a different footprint
            // from the planner and clipped benches beside otherwise valid road entries.
            float desiredYaw = _driveHeadings[_driveIndex];
            float yaw = Mathf.MoveTowardsAngle(v.CurrentYaw, desiredYaw, 120f * Time.deltaTime);
            string blockReason;
            if (Mathf.Abs(Mathf.DeltaAngle(v.CurrentYaw, desiredYaw)) > 0.001f)
            {
                _driveSpeed = 0f;
                if (!World.IsVehicleTurnClear(v, current, v.CurrentYaw, yaw, out blockReason))
                {
                    _driving = false;
                    LastStatus = "blocked_stopped:" + blockReason;
                    LastError = "vehicle_blocked:" + blockReason;
                    World.MarkDynamicMoved();
                    return;
                }
                v.ApplyTransform(current, yaw);
                World.FollowCharacterToVehicle(v);
                World.MarkDynamicMoved();
                return;
            }
            yaw = desiredYaw;
            float maxSpeed = v.Spec.drive.max_speed_mps;
            float targetSpeed = Mathf.Min(maxSpeed, Mathf.Sqrt(Mathf.Max(0f, 2f * 1.4f * distance)));
            _driveSpeed = Mathf.MoveTowards(_driveSpeed, targetSpeed, 2.2f * Time.deltaTime);
            float step = _driveSpeed * Time.deltaTime;
            step = Mathf.Min(step, distance);
            Vector3 newPivot = current + delta.normalized * step;
            // ground offset: keep the original relationship of the baked model to the ground
            newPivot.y = WorldData.GroundHeightAt(new Vector3(newPivot.x, 0f, newPivot.z)) + v.Spec.ground_y;
            if (!World.IsVehicleSweepClear(v, current, newPivot, yaw, out blockReason))
            {
                _driving = false;
                LastStatus = "blocked_stopped:" + blockReason;
                LastError = "vehicle_blocked:" + blockReason;
                World.MarkDynamicMoved();
                return;
            }
            v.ApplyTransform(newPivot, yaw);
            LastDriveDistance += step;
            World.FollowCharacterToVehicle(v);
            World.MarkDynamicMoved();
        }
    }
}
