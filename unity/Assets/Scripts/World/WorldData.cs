using System.Collections.Generic;
using UnityEngine;

namespace Yixuan.World
{
    /// <summary>可去地点（名字 → 世界坐标），给 LLM 和移动控制器共用。</summary>
    public sealed class WorldAnchor
    {
        public string Id;
        public string Label;
        public Vector3 Position;
    }

    /// <summary>
    /// 场景世界数据：角色位置、可走范围、障碍物、可去地点。
    ///
    /// 场景是我们自己的，坐标/命名对象/渲染包围盒都可以直接读；
    /// 第一版不做 NavMesh（场景没有 Collider），用 Renderer 包围盒近似障碍：
    ///   - 排除地面/低矮贴花/头顶物体/角色自身；
    ///   - 障碍按“角色半径 + 余量”膨胀；
    ///   - 移动前检查候选点，碰到就停，不穿模。
    /// </summary>
    public static class WorldData
    {
        public static Transform Character;
        public static Bounds WalkableBounds;
        public static float GroundY;
        public static float AgentRadius = 0.35f;
        public static float Margin = 0.15f;
        public static readonly List<Bounds> Obstacles = new List<Bounds>();
        public static readonly List<WorldAnchor> Anchors = new List<WorldAnchor>();

        public static bool Ready { get; private set; }
        public static int ObstacleCount { get { return Obstacles.Count; } }
        public static string LastError { get; private set; }

        /// <summary>
        /// 大地图数据源（SovietBlueHour）。非空时 WorldData 使用导出的 collision/nav 数据，
        /// 而不是旧微缩景观的 Renderer 包围盒 + 网格 A*。
        /// </summary>
        public static MapWorldProfile Map { get; private set; }
        public static bool UsingMapNavigation { get { return Map != null && Map.EnableMapNavigation && Map.DataReady; } }

        /// <summary>
        /// 取某个水平位置的地面高度。大地图有高度图（广场约0.15m / 草地约0m），
        /// 旧微缩景观退回固定 GroundY。GroundSnap 用它做动态贴地。
        /// </summary>
        public static float GroundHeightAt(Vector3 position)
        {
            if (Map != null && Map.HasGround) return Map.SurfaceY(position.x, position.z);
            return GroundY;
        }

        public static void RegisterMap(MapWorldProfile map)
        {
            if (map == null) return;
            Map = map;
            Ready = false;
            _walkableGrid = null;
        }

        public static void ClearMap(MapWorldProfile map)
        {
            if (Map != map) return;
            Map = null;
            Ready = false;
            _walkableGrid = null;
        }

        // ---- 网格 A* ----
        public const float GridCellSize = 0.35f;
        static bool[,] _walkableGrid;
        static int _gridW, _gridH;
        static Vector3 _gridMin;

        static bool _logged;
        static readonly string[] IgnoreNames =
        {
            "ground", "plinth", "sidewalk", "street", "road", "drain", "wet", "reflection",
            "shadow", "line", "plane", "base", "diorama", "floor", "marking", "decal",
            "ripple", "puddle", "water", "drip"
        };

        public static void EnsureInit()
        {
            if (Map == null && !Ready)
            {
                var found = MapWorldProfile.FindActive();
                if (found != null)
                {
                    found.EnsureData();
                    RegisterMap(found);
                }
            }
            if (!Ready || Character == null) Refresh();
        }

        public static void Refresh()
        {
            LastError = null;
            Obstacles.Clear();
            Anchors.Clear();
            Character = GameObject.Find("Yixuan_Test") != null
                ? GameObject.Find("Yixuan_Test").transform
                : null;

            if (Map == null) Map = MapWorldProfile.FindActive();
            if (UsingMapNavigation)
            {
                RefreshFromMap();
                return;
            }
            Map = null;

            var ground = GameObject.Find("Ground");
            if (ground != null)
            {
                var renderer = ground.GetComponent<Renderer>();
                if (renderer != null)
                {
                    WalkableBounds = renderer.bounds;
                    GroundY = WalkableBounds.max.y;
                }
            }
            else
            {
                WalkableBounds = new Bounds(Vector3.zero, new Vector3(14f, 1f, 14f));
                GroundY = 0f;
            }

            // 内缩，不让角色贴边/掉出地面
            float inset = AgentRadius + Margin + 0.3f;
            WalkableBounds = new Bounds(
                new Vector3(WalkableBounds.center.x, GroundY, WalkableBounds.center.z),
                new Vector3(Mathf.Max(1f, WalkableBounds.size.x - inset * 2f), 1f,
                            Mathf.Max(1f, WalkableBounds.size.z - inset * 2f)));

            ScanObstacles();
            BuildDefaultAnchors();
            Ready = true;
            BuildWalkableGrid();
            BuildLandmarkAnchors();
            LastError = null;
            if (!_logged)
            {
                _logged = true;
                Debug.Log("[WorldData] 可走范围=" + WalkableBounds + "，障碍=" + Obstacles.Count +
                          "，可去地点=" + Anchors.Count + "，角色=" + (Character != null ? Character.position.ToString("F2") : "无"));
            }
        }

        // ============================================================
        //  大地图模式（SovietBlueHour）
        // ============================================================

        /// <summary>
        /// 用地图导出的 collision/nav 数据填充可走范围、障碍与锚点。
        /// 网格 A* 不再构建（440×340m 网格太慢），FindPath 改走导航图。
        /// </summary>
        static void RefreshFromMap()
        {
            GroundY = Map.GroundY;
            float minX = Map.WalkableMin.x, maxX = Map.WalkableMax.x;
            float minZ = Map.WalkableMin.y, maxZ = Map.WalkableMax.y;
            WalkableBounds = new Bounds(
                new Vector3((minX + maxX) * 0.5f, GroundY, (minZ + maxZ) * 0.5f),
                new Vector3(Mathf.Max(1f, maxX - minX), 1f, Mathf.Max(1f, maxZ - minZ)));

            for (int i = 0; i < Map.ObstacleBounds.Count; i++) Obstacles.Add(Map.ObstacleBounds[i]);
            for (int i = 0; i < Map.Anchors.Count; i++)
            {
                var a = Map.Anchors[i];
                if (a == null) continue;
                float ay = Map.HasGround ? Map.SurfaceY(a.Position.x, a.Position.z) : GroundY;
                var p = new Vector3(a.Position.x, ay, a.Position.z);
                AddAnchor(a.Id, a.Label, p);
            }

            _walkableGrid = null;
            Ready = true;
            LastError = null;
            if (!_logged)
            {
                _logged = true;
                Debug.Log("[WorldData] 大地图模式：可走范围=" + WalkableBounds + "，障碍=" + Obstacles.Count +
                          "，可去地点=" + Anchors.Count + "，导航图=" + (Map.HasGraph ? "有" : "无") +
                          "，角色=" + (Character != null ? Character.position.ToString("F2") : "无"));
            }
        }

        static void ScanObstacles()
        {
            float inflate = 2f * (AgentRadius + Margin);
            Renderer[] renderers = UnityEngine.Object.FindObjectsOfType<Renderer>();
            for (int i = 0; i < renderers.Length; i++)
            {
                var r = renderers[i];
                if (r == null || !r.enabled || !r.gameObject.activeInHierarchy) continue;
                if (r.gameObject.layer == 8) continue;                    // 角色自身层
                if (Character != null && r.transform.IsChildOf(Character)) continue;
                if (BadName(r.gameObject.name)) continue;

                Bounds b = r.bounds;
                if (b.size.x > 20f && b.size.z > 20f) continue;             // 底板/大地面
                if (b.max.y < GroundY + 0.4f) continue;                     // 地面/低矮贴花/台阶面
                if (b.min.y > GroundY + 1.6f) continue;                     // 头顶：雨棚/电线/屋顶
                if (b.center.magnitude > 40f) continue;
                b.Expand(new Vector3(inflate, 0f, inflate));
                Obstacles.Add(b);
            }
        }

        static bool BadName(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            string lower = name.ToLowerInvariant();
            for (int i = 0; i < IgnoreNames.Length; i++)
                if (lower.IndexOf(IgnoreNames[i], System.StringComparison.Ordinal) >= 0) return true;
            return false;
        }

        static void BuildDefaultAnchors()
        {
            if (Character == null) return;
            Vector3 p = Character.position;
            AddAnchor("start_point", "起始位置", p);
            AddAnchor("sidewalk_center", "人行道中央", new Vector3(0f, GroundY, 2.0f));
            AddAnchor("street_center", "街道中央", new Vector3(0f, GroundY, 5.5f));

            var store = GameObject.Find("Store");
            if (store != null)
            {
                Vector3 door = store.transform.position + new Vector3(0f, 0f, 3.3f);
                door.y = GroundY;
                AddAnchor("store_door", "店门口", door);
            }
            AddAnchor("camera_front", "观看者面前", new Vector3(p.x, GroundY, Mathf.Min(WalkableBounds.max.z - 0.8f, Mathf.Max(p.z + 2.2f, 5.6f))));
        }

        /// <summary>
        /// 语义地标：把场景里有名字/组件的对象转成“可站、可达的靠近点”，
        /// 不用对象中心（灯杆中心通常不可走）。目前先接红绿灯，后续可扩展到货架/柜台等。
        /// </summary>
        static void BuildLandmarkAnchors()
        {
            var trafficLight = GameObject.Find("TrafficLight");
            if (trafficLight != null)
            {
                Vector3 approach;
                if (TryFindApproachPoint(trafficLight.transform.position, out approach))
                    AddAnchor("traffic_light_main", "路口红绿灯（红绿灯/信号灯/交通灯）", approach);
            }
        }

        static bool TryFindApproachPoint(Vector3 objectPosition, out Vector3 approach)
        {
            approach = Vector3.zero;
            float[] radii = { 1.2f, 1.6f, 2.0f, 2.4f };
            for (int r = 0; r < radii.Length; r++)
            {
                for (int angle = 0; angle < 360; angle += 45)
                {
                    Vector3 dir = Quaternion.Euler(0f, angle, 0f) * Vector3.forward;
                    Vector3 candidate = objectPosition + dir * radii[r];
                    candidate.y = GroundY;
                    if (!IsWalkable(candidate)) continue;
                    // 从角色起点真实可达才算数；避免选到“能站但过不去”的点。
                    if (Character != null && FindPath(Character.position, candidate) == null) continue;
                    approach = candidate;
                    return true;
                }
            }
            return false;
        }

        public static void AddAnchor(string id, string label, Vector3 position)
        {
            if (string.IsNullOrEmpty(id)) return;
            for (int i = 0; i < Anchors.Count; i++)
                if (Anchors[i].Id == id) return;
            Anchors.Add(new WorldAnchor { Id = id, Label = label, Position = position });
        }

        public static WorldAnchor GetAnchor(string id)
        {
            for (int i = 0; i < Anchors.Count; i++)
                if (Anchors[i].Id == id) return Anchors[i];
            return null;
        }

        public static Vector3 ClampToBounds(Vector3 position)
        {
            position.x = Mathf.Clamp(position.x, WalkableBounds.min.x, WalkableBounds.max.x);
            position.z = Mathf.Clamp(position.z, WalkableBounds.min.z, WalkableBounds.max.z);
            position.y = GroundY;
            return position;
        }

        /// <summary>只检查可走矩形边界，不检查障碍；用于在执行前拒绝非法目标，而不是悄悄 Clamp。</summary>
        public static bool IsInsideBounds(Vector3 position)
        {
            if (!Ready) Refresh();
            return position.x >= WalkableBounds.min.x && position.x <= WalkableBounds.max.x &&
                   position.z >= WalkableBounds.min.z && position.z <= WalkableBounds.max.z;
        }

        /// <summary>这个位置（XZ）能不能站：在可走边界内，且不在任何障碍膨胀盒里。</summary>
        public static bool IsWalkable(Vector3 position)
        {
            if (!Ready) Refresh();
            if (!IsInsideBounds(position)) return false;
            for (int i = 0; i < Obstacles.Count; i++)
            {
                Bounds b = Obstacles[i];
                if (position.x < b.min.x || position.x > b.max.x) continue;
                if (position.z < b.min.z || position.z > b.max.z) continue;
                return false;
            }
            return true;
        }

        // ============================================================
        //  网格 A*：起点到目标绕开膨胀障碍，返回一串世界坐标路点
        // ============================================================

        static void BuildWalkableGrid()
        {
            _gridMin = WalkableBounds.min;
            _gridW = Mathf.Max(1, Mathf.CeilToInt(WalkableBounds.size.x / GridCellSize));
            _gridH = Mathf.Max(1, Mathf.CeilToInt(WalkableBounds.size.z / GridCellSize));
            _walkableGrid = new bool[_gridW, _gridH];
            for (int z = 0; z < _gridH; z++)
                for (int x = 0; x < _gridW; x++)
                    _walkableGrid[x, z] = IsWalkable(CellCenter(x, z));
        }

        static Vector3 CellCenter(int x, int z)
        {
            return new Vector3(_gridMin.x + (x + 0.5f) * GridCellSize, GroundY,
                               _gridMin.z + (z + 0.5f) * GridCellSize);
        }

        static int CellIndex(int x, int z) { return z * _gridW + x; }
        static bool InGrid(int x, int z) { return x >= 0 && z >= 0 && x < _gridW && z < _gridH; }

        static int WorldToCellX(float x)
        {
            return Mathf.Clamp(Mathf.FloorToInt((x - _gridMin.x) / GridCellSize), 0, _gridW - 1);
        }
        static int WorldToCellZ(float z)
        {
            return Mathf.Clamp(Mathf.FloorToInt((z - _gridMin.z) / GridCellSize), 0, _gridH - 1);
        }

        /// <summary>XZ 线段与膨胀障碍盒精确相交检查；采样会漏掉薄障碍或拐角。</summary>
        public static bool LineWalkable(Vector3 a, Vector3 b)
        {
            EnsureInit();
            if (!IsWalkable(a) || !IsWalkable(b)) return false;
            for (int i = 0; i < Obstacles.Count; i++)
            {
                Bounds box = Obstacles[i]; float enter = 0f, exit = 1f;
                if (ClipAxis(a.x, b.x - a.x, box.min.x, box.max.x, ref enter, ref exit) &&
                    ClipAxis(a.z, b.z - a.z, box.min.z, box.max.z, ref enter, ref exit)) return false;
            }
            return true;
        }
        static bool ClipAxis(float start, float delta, float min, float max, ref float enter, ref float exit)
        {
            if (Mathf.Abs(delta) < 0.000001f) return start >= min && start <= max;
            float near = (min - start) / delta, far = (max - start) / delta;
            if (near > far) { float swap = near; near = far; far = swap; }
            enter = Mathf.Max(enter, near); exit = Mathf.Min(exit, far);
            return enter <= exit;
        }

        // ============================================================
        //  导航图 A*（大地图 SovietBlueHour）
        // ============================================================

        /// <summary>线段是否与任一障碍盒（XZ 投影）相交；不检查端点是否在盒内。</summary>
        static bool SegmentBlocked(Vector3 a, Vector3 b)
        {
            for (int i = 0; i < Obstacles.Count; i++)
            {
                Bounds box = Obstacles[i];
                float enter = 0f, exit = 1f;
                if (ClipAxis(a.x, b.x - a.x, box.min.x, box.max.x, ref enter, ref exit) &&
                    ClipAxis(a.z, b.z - a.z, box.min.z, box.max.z, ref enter, ref exit))
                {
                    if (enter <= exit) return true;
                }
            }
            return false;
        }

        static float FlatDist(Vector3 a, Vector3 b)
        {
            float dx = a.x - b.x, dz = a.z - b.z;
            return Mathf.Sqrt(dx * dx + dz * dz);
        }

        /// <summary>
        /// 大地图寻路：起终点用“导航图最近的若干可用节点”接入，图上跑 A*，
        /// 再逐段做几何校验 + 拉直。任何一个连接段不可走就如实返回 null，不硬接。
        /// </summary>
        static List<Vector3> MapGraphFindPath(Vector3 start, Vector3 goal)
        {
            var map = Map;
            if (map == null || !map.HasGraph) { LastError = "没有导航图数据"; return null; }
            if (!IsInsideBounds(goal)) { LastError = "目标点在可走范围外"; return null; }
            if (!IsWalkable(goal)) { LastError = "目标点不可走（在障碍内）"; return null; }
            if (LineWalkable(start, goal)) return new List<Vector3> { goal };

            var nearStart = new List<int>(12);
            var nearGoal = new List<int>(12);
            map.CollectNearbyNodes(start, 10, nearStart);
            map.CollectNearbyNodes(goal, 10, nearGoal);

            var seeds = new List<int>();
            var seedCost = new List<float>();
            for (int i = 0; i < nearStart.Count; i++)
            {
                int idx = nearStart[i];
                if (!map.IsNodeValid(idx)) continue;
                Vector3 np = map.NodePosition(idx);
                // 允许角色贴着墙站：只检查连接段有没有穿障碍，不要求起点本身在膨胀盒外。
                if (SegmentBlocked(start, np)) continue;
                seeds.Add(idx);
                seedCost.Add(FlatDist(start, np));
            }
            var goalNodes = new List<int>();
            var goalCost = new List<float>();
            for (int i = 0; i < nearGoal.Count; i++)
            {
                int idx = nearGoal[i];
                if (!map.IsNodeValid(idx)) continue;
                Vector3 np = map.NodePosition(idx);
                if (SegmentBlocked(np, goal)) continue;
                goalNodes.Add(idx);
                goalCost.Add(FlatDist(np, goal));
            }
            if (seeds.Count == 0) { LastError = "起点附近没有可接入的导航节点（可能被建筑包住）"; return null; }
            if (goalNodes.Count == 0) { LastError = "目标附近没有可接入的导航节点（可能被建筑包住）"; return null; }

            int n = map.Nodes.Length;
            var g = new float[n];
            var came = new int[n];
            var closed = new bool[n];
            var h = new float[n];
            for (int i = 0; i < n; i++) { g[i] = float.MaxValue; came[i] = -2; }
            for (int i = 0; i < n; i++)
            {
                Vector3 np = map.NodePosition(i);
                h[i] = FlatDist(np, goal);
            }
            for (int i = 0; i < seeds.Count; i++)
            {
                int idx = seeds[i];
                if (seedCost[i] < g[idx]) { g[idx] = seedCost[i]; came[idx] = -1; }
            }

            var heap = new List<int>(512);
            for (int i = 0; i < seeds.Count; i++) HeapPush(heap, g, h, seeds[i]);
            bool[] isGoal = new bool[n];
            float[] goalLink = new float[n];
            for (int i = 0; i < goalNodes.Count; i++) { isGoal[goalNodes[i]] = true; goalLink[goalNodes[i]] = goalCost[i]; }

            int bestGoal = -1;
            float bestTotal = float.MaxValue;
            int guard = 0;
            while (heap.Count > 0 && guard++ < 400000)
            {
                int cur = HeapPop(heap, g, h);
                if (cur < 0) break;
                if (closed[cur]) continue;
                closed[cur] = true;
                if (isGoal[cur])
                {
                    float total = g[cur] + goalLink[cur];
                    if (total < bestTotal) { bestTotal = total; bestGoal = cur; }
                }
                if (bestGoal >= 0 && g[cur] + h[cur] >= bestTotal) break;
                int e0 = map.EdgeStart(cur), e1 = map.EdgeEnd(cur);
                for (int e = e0; e < e1; e++)
                {
                    int nb = map.EdgeTarget(e);
                    if (closed[nb]) continue;
                    if (!map.IsEdgeValid(e) || !map.IsNodeValid(nb)) continue;
                    float tentative = g[cur] + map.EdgeWeight(e);
                    if (tentative >= g[nb]) continue;
                    g[nb] = tentative; came[nb] = cur;
                    HeapPush(heap, g, h, nb);
                }
            }

            if (bestGoal < 0) { LastError = "导航图 A* 没有找到通路"; return null; }

            // 回溯节点链
            var chain = new List<int>();
            int c = bestGoal;
            while (c >= 0 && c != -2) { chain.Add(c); c = came[c]; }
            if (c == -2) { LastError = "导航图路径回溯失败"; return null; }
            chain.Reverse();

            var points = new List<Vector3>(chain.Count + 2) { start };
            for (int i = 0; i < chain.Count; i++)
            {
                Vector3 np = map.NodePosition(chain[i]);
                if (FlatDist(points[points.Count - 1], np) < 0.02f) continue;
                if (SegmentBlocked(points[points.Count - 1], np))
                {
                    LastError = "导航图路径节点连接段不可走（node " + chain[i] + "）";
                    return null;
                }
                points.Add(np);
            }
            if (SegmentBlocked(points[points.Count - 1], goal))
            {
                LastError = "导航图路径末段不可走";
                return null;
            }
            points.Add(goal);

            var smoothed = SmoothPath(points);
            for (int i = 1; i < smoothed.Count; i++)
            {
                if (LineWalkable(smoothed[i - 1], smoothed[i])) continue;
                return points;   // 拉直后不可走则退回未拉直路径（已逐段校验）
            }
            return smoothed;
        }

        // 4 叉小根堆（按 f = g + h）；节点数 ~3500，够用。
        static void HeapPush(List<int> heap, float[] g, float[] h, int node)
        {
            heap.Add(node);
            int i = heap.Count - 1;
            while (i > 0)
            {
                int parent = (i - 1) >> 1;
                if (g[heap[parent]] + h[heap[parent]] <= g[heap[i]] + h[heap[i]]) break;
                int tmp = heap[parent]; heap[parent] = heap[i]; heap[i] = tmp; i = parent;
            }
        }

        static int HeapPop(List<int> heap, float[] g, float[] h)
        {
            if (heap.Count == 0) return -1;
            int top = heap[0];
            int last = heap[heap.Count - 1];
            heap.RemoveAt(heap.Count - 1);
            if (heap.Count == 0) return top;
            heap[0] = last;
            int i = 0;
            while (true)
            {
                int l = i * 2 + 1, r = l + 1, best = i;
                if (l < heap.Count && g[heap[l]] + h[heap[l]] < g[heap[best]] + h[heap[best]]) best = l;
                if (r < heap.Count && g[heap[r]] + h[heap[r]] < g[heap[best]] + h[heap[best]]) best = r;
                if (best == i) break;
                int tmp = heap[best]; heap[best] = heap[i]; heap[i] = tmp; i = best;
            }
            return top;
        }

        /// <summary>找从 start 到 goal 的绕障碍路径；没有路返回 null。</summary>
        public static List<Vector3> FindPath(Vector3 start, Vector3 goal)
        {
            EnsureInit();
            if (UsingMapNavigation && Map.HasGraph) return MapGraphFindPath(start, goal);
            if (!IsWalkable(goal)) return null;
            if (LineWalkable(start, goal)) return new List<Vector3> { goal };
            if (_walkableGrid == null) BuildWalkableGrid();

            int sx = WorldToCellX(start.x), sz = WorldToCellZ(start.z);
            int gx = WorldToCellX(goal.x), gz = WorldToCellZ(goal.z);
            int startIndex = FindNearestWalkable(sx, sz, 8);
            int goalIndex = FindNearestWalkable(gx, gz, 8);
            if (startIndex < 0 || goalIndex < 0)
            {
                LastError = "起点或终点附近没有可走格";
                return null;
            }
            if (startIndex == goalIndex)
            {
                // R1：同一个网格也不能直接返回 goal；直线已经验证不可走时，
                // 只有“起点→格中心→终点”两段都可走才接受，否则明确失败。
                int cx = startIndex % _gridW, cz = startIndex / _gridW;
                Vector3 center = CellCenter(cx, cz);
                if (LineWalkable(start, center) && LineWalkable(center, goal))
                    return new List<Vector3> { start, center, goal };
                LastError = "起终点在同一网格但直线不可走";
                return null;
            }

            int count = _gridW * _gridH;
            float[] g = new float[count];
            int[] came = new int[count];
            bool[] closed = new bool[count];
            bool[] open = new bool[count];
            for (int i = 0; i < count; i++) { g[i] = float.MaxValue; came[i] = -1; }
            var openList = new List<int>(128);
            g[startIndex] = 0f;
            openList.Add(startIndex);
            open[ startIndex] = true;

            int goalX = goalIndex % _gridW;
            int goalZ = goalIndex / _gridW;
            int goalReached = -1;
            int guard = 0;
            while (openList.Count > 0 && guard++ < 30000)
            {
                // 小网格直接线性找最小 f，最坏约 1300 个节点，足够快
                int best = 0;
                float bestF = float.MaxValue;
                for (int i = 0; i < openList.Count; i++)
                {
                    int idx = openList[i];
                    int cx = idx % _gridW, cz = idx / _gridW;
                    float f = g[idx] + Octile(cx, cz, goalX, goalZ);
                    if (f < bestF) { bestF = f; best = i; }
                }
                int current = openList[best];
                openList.RemoveAt(best);
                open[current] = false;
                if (current == goalIndex) { goalReached = current; break; }
                closed[current] = true;

                int cxCurrent = current % _gridW, czCurrent = current / _gridW;
                for (int dz = -1; dz <= 1; dz++)
                {
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        if (dx == 0 && dz == 0) continue;
                        int nx = cxCurrent + dx, nz = czCurrent + dz;
                        if (!InGrid(nx, nz) || !_walkableGrid[nx, nz]) continue;
                        if (!LineWalkable(CellCenter(cxCurrent, czCurrent), CellCenter(nx, nz))) continue;
                        // 斜走不允许切角（两个正交邻居都得可走）
                        if (dx != 0 && dz != 0)
                        {
                            if (!_walkableGrid[cxCurrent + dx, czCurrent] ||
                                !_walkableGrid[cxCurrent, czCurrent + dz]) continue;
                        }
                        int nIndex = CellIndex(nx, nz);
                        if (closed[nIndex]) continue;
                        float step = (dx != 0 && dz != 0) ? 1.41421356f : 1f;
                        float tentative = g[current] + step;
                        if (tentative >= g[nIndex]) continue;
                        g[nIndex] = tentative;
                        came[nIndex] = current;
                        if (!open[nIndex]) { open[nIndex] = true; openList.Add(nIndex); }
                    }
                }
            }

            if (goalReached < 0)
            {
                LastError = "A* 没有找到通路";
                return null;
            }

            var cells = new List<int>();
            int cur = goalReached;
            while (cur >= 0)
            {
                cells.Add(cur);
                if (cur == startIndex) break;
                cur = came[cur];
            }
            cells.Reverse();

            // F2：最近网格点也必须能从真实起点/终点连过去；否则换候选，不能硬接。
            int firstConnectable = -1;
            for (int i = 0; i < cells.Count; i++)
            {
                int cx = cells[i] % _gridW, cz = cells[i] / _gridW;
                if (LineWalkable(start, CellCenter(cx, cz))) { firstConnectable = i; break; }
            }
            if (firstConnectable < 0)
            {
                LastError = "起点无法连接到可走网格";
                return null;
            }

            int lastConnectable = -1;
            for (int i = cells.Count - 1; i >= firstConnectable; i--)
            {
                int cx = cells[i] % _gridW, cz = cells[i] / _gridW;
                if (LineWalkable(CellCenter(cx, cz), goal)) { lastConnectable = i; break; }
            }
            if (lastConnectable < 0)
            {
                LastError = "终点无法从可走网格连接";
                return null;
            }

            var points = new List<Vector3>(lastConnectable - firstConnectable + 3);
            points.Add(start);
            for (int i = firstConnectable; i <= lastConnectable; i++)
            {
                int cx = cells[i] % _gridW, cz = cells[i] / _gridW;
                Vector3 p = CellCenter(cx, cz);
                if (Vector2.Distance(new Vector2(points[points.Count - 1].x, points[points.Count - 1].z),
                                     new Vector2(p.x, p.z)) < 0.01f) continue;
                if (!LineWalkable(points[points.Count - 1], p))
                {
                    LastError = "路径网格点连接段不可走";
                    return null;
                }
                points.Add(p);
            }
            if (Vector2.Distance(new Vector2(points[points.Count - 1].x, points[points.Count - 1].z),
                                 new Vector2(goal.x, goal.z)) >= 0.01f)
            {
                if (!LineWalkable(points[points.Count - 1], goal))
                {
                    LastError = "路径末段无法连接到真实终点";
                    return null;
                }
                points.Add(goal);
            }

            for (int i = 1; i < points.Count; i++)
            {
                if (LineWalkable(points[i - 1], points[i])) continue;
                LastError = "路径包含不可走连接段";
                return null;
            }

            List<Vector3> smoothed = SmoothPath(points);
            for (int i = 1; i < smoothed.Count; i++)
            {
                if (LineWalkable(smoothed[i - 1], smoothed[i])) continue;
                // 平滑反而切进障碍：退回未平滑路径（它已经逐段验证过）
                return points;
            }
            return smoothed;
        }

        static int FindNearestWalkable(int x, int z, int maxRadius)
        {
            if (InGrid(x, z) && _walkableGrid[x, z]) return CellIndex(x, z);
            for (int r = 1; r <= maxRadius; r++)
            {
                for (int dz = -r; dz <= r; dz++)
                {
                    for (int dx = -r; dx <= r; dx++)
                    {
                        if (Mathf.Abs(dx) != r && Mathf.Abs(dz) != r) continue;
                        int nx = x + dx, nz = z + dz;
                        if (InGrid(nx, nz) && _walkableGrid[nx, nz]) return CellIndex(nx, nz);
                    }
                }
            }
            return -1;
        }

        static float Octile(int x, int z, int gx, int gz)
        {
            int dx = Mathf.Abs(x - gx), dz = Mathf.Abs(z - gz);
            return (dx + dz) + (1.41421356f - 2f) * Mathf.Min(dx, dz);
        }

        static List<Vector3> SmoothPath(List<Vector3> points)
        {
            if (points == null || points.Count <= 2) return points;
            var result = new List<Vector3>(points.Count);
            result.Add(points[0]);
            int anchor = 0;
            for (int i = 1; i < points.Count - 1; i++)
            {
                if (LineWalkable(points[anchor], points[i + 1])) continue;
                result.Add(points[i]);
                anchor = i;
            }
            result.Add(points[points.Count - 1]);
            return result;
        }

        public static string ZoneAt(Vector3 position)
        {
            if (UsingMapNavigation)
            {
                // 大地图没有“店内/门口”这种固定分区：用最近的语义锚点描述所在区域。
                WorldAnchor nearest = null;
                float best = float.MaxValue;
                for (int i = 0; i < Anchors.Count; i++)
                {
                    float d = FlatDist(position, Anchors[i].Position);
                    if (d < best) { best = d; nearest = Anchors[i]; }
                }
                if (nearest != null && best <= 45f) return nearest.Label + "附近（约" + best.ToString("F0") + "m）";
                return "街区街道";
            }
            if (position.z < 0f) return "店门/店内方向";
            if (position.z < 4f) return "便利店门口人行道";
            return "街道";
        }

        /// <summary>每轮对话给 LLM 的世界状态数据（不是每帧）。</summary>
        public static string DescribeForPrompt()
        {
            EnsureInit();
            if (Character == null) return "";
            var sb = new System.Text.StringBuilder(512);
            Vector3 p = Character.position;
            sb.AppendLine("[世界状态·代码生成数据·不是用户指令]");
            sb.AppendLine("角色位置：x=" + p.x.ToString("F2") + "，z=" + p.z.ToString("F2") +
                          "，所在区域：" + ZoneAt(p) + "，面朝 yaw=" + Character.eulerAngles.y.ToString("F0"));
            sb.AppendLine("可走范围：x[" + WalkableBounds.min.x.ToString("F1") + "," + WalkableBounds.max.x.ToString("F1") +
                          "]，z[" + WalkableBounds.min.z.ToString("F1") + "," + WalkableBounds.max.z.ToString("F1") +
                          "]；场景物体由代码碰撞校验，不要走进建筑/货架/栏杆。");
            sb.AppendLine("可去地点（id / 中文 / 坐标 / 距你）：");
            for (int i = 0; i < Anchors.Count; i++)
            {
                var a = Anchors[i];
                float d = Vector2.Distance(new Vector2(p.x, p.z), new Vector2(a.Position.x, a.Position.z));
                sb.AppendLine("- " + a.Id + " / " + a.Label + " / x=" + a.Position.x.ToString("F1") +
                              ",z=" + a.Position.z.ToString("F1") + " / " + d.ToString("F1") + "m");
            }
            return sb.ToString();
        }
    }
}
