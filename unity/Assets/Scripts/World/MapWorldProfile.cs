using System;
using System.Collections.Generic;
using UnityEngine;
using Yixuan.World;

namespace Yixuan.World
{
    /// <summary>
    /// SovietBlueHour 大地图的导航/碰撞数据源。
    ///
    /// 为什么需要它：
    ///   旧场景是 14×14m 微缩景观，WorldData 用 Renderer 包围盒 + 0.35m 网格 A* 就能工作；
    ///   新地图是 440×340m 的合并网格（51 个 BLD_* 大网格），
    ///   Renderer 包围盒超过 20m 会被旧规则当成“地面/底板”跳过 → 角色会穿墙。
    ///   因此新地图改用导出阶段已经生成好的：
    ///     - collision.json（91 个 AABB 障碍 / 地面 / 水体 / 平台）
    ///     - nav_compact.json（3486 导航节点 + 8551 边，1 个连通分量；坐标已转为 Unity 实际坐标）
    ///     - anchors.json（人工/脚本整理的语义锚点，中文标签给 LLM 用）
    ///
    /// 坐标：地图用 UniGLTF ReverseZ 导入，Unity 实际 = (blender.x, blender.z, blender.y)。
    /// nav 数据在集成时已按这个公式转换好，运行时只需要直接使用。
    ///
    /// 该组件挂在场景里的地图根节点上；WorldData 发现它后自动切到大地图模式。
    /// 不挂它（或 EnableMapNavigation=false）时 WorldData 保持旧的微缩景观行为。
    /// </summary>
    [DisallowMultipleComponent]
    public class MapWorldProfile : MonoBehaviour
    {
        [Header("数据（留空则从 Resources/SovietBlueHour 读取）")]
        public TextAsset CollisionJson;
        public TextAsset NavJson;
        public TextAsset AnchorsJson;
        [Tooltip("SITE 网格烘焙的地面高度图（地形不是平的：广场约0.15m、草地约0m）")]
        public TextAsset GroundJson;

        [Header("开关")]
        [Tooltip("启用后 WorldData 使用本组件提供的大地图导航数据")]
        public bool EnableMapNavigation = true;
        [Tooltip("为碰撞盒生成 BoxCollider（第三人称相机避障用；不影响角色移动，角色走 WorldData 逻辑）")]
        public bool CreateCameraColliders = true;

        [Header("地面 / 可走范围（Unity 实际坐标）")]
        public float GroundY = 0f;
        public Vector2 WalkableMin = new Vector2(-196f, -145.5f);
        public Vector2 WalkableMax = new Vector2(200f, 152.4f);

        [Header("出生点（Unity 实际坐标）")]
        public Vector3 SpawnPoint = new Vector3(0f, 0f, -72f);
        public float SpawnYaw = 0f;

        /// <summary>障碍盒（已按角色半径膨胀；不含地面/可穿过的拱门顶）。</summary>
        public readonly List<Bounds> ObstacleBounds = new List<Bounds>();

        /// <summary>原始障碍盒（未膨胀，给相机碰撞体用）。</summary>
        readonly List<Bounds> _rawBoxes = new List<Bounds>();

        /// <summary>原始障碍 AABB + 类型（小地图绘制用；ground/arch_lintel 不收录）。</summary>
        public readonly List<Bounds> RawObstacleBounds = new List<Bounds>();
        public readonly List<string> RawObstacleKinds = new List<string>();

        /// <summary>锚点定义（id/中文标签/坐标）。</summary>
        public readonly List<WorldAnchor> Anchors = new List<WorldAnchor>();

        [Serializable] public class BoxEntry { public string id; public float[] min; public float[] max; public string kind; }
        [Serializable] public class CollisionDoc { public BoxEntry[] boxes; }
        /// <summary>导航节点：x/z 为 Unity 实际水平坐标，y 为高度（0 = 地面层）。</summary>
        [Serializable] public class NavNodeEntry { public float x; public float y; public float z; public string kind; }
        [Serializable] public class NavEdgeEntry { public int a; public int b; public float d; }
        [Serializable] public class NavDoc { public NavNodeEntry[] nodes; public NavEdgeEntry[] edges; }
        [Serializable] public class AnchorEntry { public string id; public string label; public float[] pos; public string approach; public string source; }
        [Serializable] public class AnchorDoc { public AnchorEntry[] anchors; public float[] spawn; public float spawn_yaw; }
        [Serializable] public class GroundDoc { public float min_x; public float min_z; public float cell; public int w; public int h; public float[] heights; }

        public NavNodeEntry[] Nodes { get; private set; }
        public NavEdgeEntry[] Edges { get; private set; }
        public bool HasGraph { get { return Nodes != null && Nodes.Length > 0 && Edges != null; } }
        public bool DataReady { get; private set; }
        public string LastError { get; private set; }

        // 地面高度图
        float _groundMinX, _groundMinZ, _groundCell;
        int _groundW, _groundH;
        float[] _groundHeights;
        public bool HasGround { get { return _groundHeights != null && _groundW > 0; } }

        // 邻接表（构建一次）
        int[] _edgeStart;
        int[] _edgeTarget;
        float[] _edgeCost;

        // 导航有效性：落在建筑/道具/水体碰撞盒里的节点、穿过碰撞盒的边，加载时禁用。
        bool[] _nodeValid;
        bool[] _edgeValidCsr;
        public int ValidNodeCount { get; private set; }
        public int ValidEdgeCount { get; private set; }
        public bool IsNodeValid(int index) { return _nodeValid == null || (index >= 0 && index < _nodeValid.Length && _nodeValid[index]); }
        public bool IsEdgeValid(int csrIndex) { return _edgeValidCsr == null || (csrIndex >= 0 && csrIndex < _edgeValidCsr.Length && _edgeValidCsr[csrIndex]); }

        void Awake()
        {
            EnsureData();
        }

        void OnEnable()
        {
            if (!DataReady) EnsureData();
            if (EnableMapNavigation) WorldData.RegisterMap(this);
        }

        void OnDisable()
        {
            WorldData.ClearMap(this);
        }

        public static MapWorldProfile FindActive()
        {
            return UnityEngine.Object.FindObjectOfType<MapWorldProfile>();
        }

        /// <summary>解析数据（幂等）。</summary>
        public void EnsureData()
        {
            if (DataReady) return;
            LastError = null;
            try
            {
                if (CollisionJson == null) CollisionJson = Resources.Load<TextAsset>("SovietBlueHour/collision");
                if (NavJson == null) NavJson = Resources.Load<TextAsset>("SovietBlueHour/nav_compact");
                if (AnchorsJson == null) AnchorsJson = Resources.Load<TextAsset>("SovietBlueHour/anchors");
                if (GroundJson == null) GroundJson = Resources.Load<TextAsset>("SovietBlueHour/ground_height");

                ObstacleBounds.Clear();
                _rawBoxes.Clear();
                RawObstacleBounds.Clear();
                RawObstacleKinds.Clear();
                Anchors.Clear();

                if (CollisionJson != null)
                {
                    var doc = JsonUtility.FromJson<CollisionDoc>(CollisionJson.text);
                    if (doc != null && doc.boxes != null)
                    {
                        float inflate = 2f * (WorldData.AgentRadius + WorldData.Margin);
                        for (int i = 0; i < doc.boxes.Length; i++)
                        {
                            var b = doc.boxes[i];
                            if (b == null || b.min == null || b.max == null || b.min.Length < 3 || b.max.Length < 3) continue;
                            string kind = b.kind ?? "";
                            // 地面不挡路；拱门顶在 2.95m 以上，角色 1.7m 可以从下面走过。
                            if (kind == "ground") continue;
                            if (kind == "arch_lintel") continue;
                            // 台阶/平台：角色没有爬升逻辑（GroundSnap 固定 GroundY），改成障碍避免穿模。
                            // collision_unity.json 的 min/max 已转成 Unity 实际坐标 [x, y(高度), z]。
                            var bounds = new Bounds(
                                new Vector3((b.min[0] + b.max[0]) * 0.5f, (b.min[1] + b.max[1]) * 0.5f, (b.min[2] + b.max[2]) * 0.5f),
                                new Vector3(Mathf.Abs(b.max[0] - b.min[0]), Mathf.Abs(b.max[1] - b.min[1]), Mathf.Abs(b.max[2] - b.min[2])));
                            _rawBoxes.Add(bounds);
                            RawObstacleBounds.Add(bounds);
                            RawObstacleKinds.Add(kind);
                            var inflated = bounds;
                            inflated.Expand(new Vector3(inflate, 0f, inflate));
                            ObstacleBounds.Add(inflated);
                        }
                    }
                }

                if (NavJson != null)
                {
                    var nav = JsonUtility.FromJson<NavDoc>(NavJson.text);
                    Nodes = nav != null ? nav.nodes : null;
                    Edges = nav != null ? nav.edges : null;
                    BuildEdges();
                }

                if (AnchorsJson != null)
                {
                    var doc = JsonUtility.FromJson<AnchorDoc>(AnchorsJson.text);
                    if (doc != null)
                    {
                        if (doc.spawn != null && doc.spawn.Length >= 3)
                        {
                            // anchors.json 的 spawn 也是 Unity 实际坐标 [x, y(高度), z]。
                            SpawnPoint = new Vector3(doc.spawn[0], doc.spawn[1], doc.spawn[2]);
                            if (doc.spawn_yaw > -1000f) SpawnYaw = doc.spawn_yaw;
                        }
                        if (doc.anchors != null)
                        {
                            for (int i = 0; i < doc.anchors.Length; i++)
                            {
                                var a = doc.anchors[i];
                                if (a == null || a.pos == null || a.pos.Length < 3) continue;
                                // anchors.json 的 pos 用 [x, y, z] = Unity 实际坐标（y 是高度）。
                                Anchors.Add(new WorldAnchor { Id = a.id, Label = a.label, Position = new Vector3(a.pos[0], a.pos[1], a.pos[2]) });
                            }
                        }
                    }
                }

                if (GroundJson != null)
                {
                    var g = JsonUtility.FromJson<GroundDoc>(GroundJson.text);
                    if (g != null && g.heights != null && g.w > 0 && g.h > 0)
                    {
                        _groundMinX = g.min_x; _groundMinZ = g.min_z; _groundCell = g.cell > 0f ? g.cell : 1f;
                        _groundW = g.w; _groundH = g.h; _groundHeights = g.heights;
                    }
                }

                DataReady = true;
                if (CreateCameraColliders && Application.isPlaying) CreateColliders();
                Debug.Log("[MapWorldProfile] 数据就绪：障碍=" + ObstacleBounds.Count + "，导航节点=" +
                          (Nodes != null ? Nodes.Length : 0) + "，边=" + (Edges != null ? Edges.Length : 0) +
                          "，锚点=" + Anchors.Count + "，出生点=" + SpawnPoint.ToString("F2"));
            }
            catch (Exception e)
            {
                LastError = "地图导航数据解析失败：" + e.Message;
                DataReady = false;
                Debug.LogError("[MapWorldProfile] " + LastError);
            }
        }

        /// <summary>
        /// 取 Unity 水平坐标 (x,z) 处的地面高度（米）。没有高度图时退回 GroundY。
        /// 地图的地面不是平的：广场/人行道约 0.15m，公园草地约 0m。
        /// </summary>
        public float SurfaceY(float x, float z)
        {
            if (!HasGround) return GroundY;
            int ix = Mathf.RoundToInt((x - _groundMinX) / _groundCell);
            int iz = Mathf.RoundToInt((z - _groundMinZ) / _groundCell);
            if (ix < 0 || iz < 0 || ix >= _groundW || iz >= _groundH) return GroundY;
            return _groundHeights[iz * _groundW + ix];
        }

        /// <summary>建立相机避障用的 BoxCollider（运行时，不影响角色移动逻辑）。</summary>
        void CreateColliders()
        {
            if (_rawBoxes.Count == 0) return;
            if (transform.Find("MapCollisionBoxes") != null) return;
            var root = new GameObject("MapCollisionBoxes");
            root.transform.SetParent(transform, false);
            for (int i = 0; i < _rawBoxes.Count; i++)
            {
                Bounds b = _rawBoxes[i];
                if (b.size.y < 0.05f) continue;
                var go = new GameObject("COLL_" + i);
                go.transform.SetParent(root.transform, false);
                go.transform.position = b.center;
                var box = go.AddComponent<BoxCollider>();
                box.center = Vector3.zero;
                box.size = b.size;
                box.isTrigger = false;
            }
            Debug.Log("[MapWorldProfile] 相机避障碰撞盒=" + root.transform.childCount);
        }

        /// <summary>建立 CSR 邻接表。</summary>
        void BuildEdges()
        {
            if (Edges == null || Nodes == null) return;
            int n = Nodes.Length;
            var start = new int[n + 1];
            for (int i = 0; i < Edges.Length; i++)
            {
                int a = Edges[i].a, b = Edges[i].b;
                if (a < 0 || a >= n || b < 0 || b >= n) continue;
                start[a + 1]++;
                start[b + 1]++;
            }
            for (int i = 0; i < n; i++) start[i + 1] += start[i];
            var target = new int[start[n]];
            var cost = new float[start[n]];
            var cursor = new int[n];
            for (int i = 0; i < n; i++) cursor[i] = start[i];
            for (int i = 0; i < Edges.Length; i++)
            {
                int a = Edges[i].a, b = Edges[i].b;
                if (a < 0 || a >= n || b < 0 || b >= n) continue;
                float d = Edges[i].d > 0f ? Edges[i].d : Vector2.Distance(new Vector2(Nodes[a].x, Nodes[a].z), new Vector2(Nodes[b].x, Nodes[b].z));
                target[cursor[a]] = b; cost[cursor[a]] = d; cursor[a]++;
                target[cursor[b]] = a; cost[cursor[b]] = d; cursor[b]++;
            }
            _edgeStart = start; _edgeTarget = target; _edgeCost = cost;
            BuildWalkability();
        }

        /// <summary>2D 线段与障碍盒（XZ 投影）相交；与 WorldData 的判定保持一致。</summary>
        bool SegmentBlocked2D(Vector3 a, Vector3 b)
        {
            for (int i = 0; i < ObstacleBounds.Count; i++)
            {
                Bounds box = ObstacleBounds[i];
                float enter = 0f, exit = 1f;
                if (Clip2D(a.x, b.x - a.x, box.min.x, box.max.x, ref enter, ref exit) &&
                    Clip2D(a.z, b.z - a.z, box.min.z, box.max.z, ref enter, ref exit))
                {
                    if (enter <= exit) return true;
                }
            }
            return false;
        }

        static bool Clip2D(float start, float delta, float min, float max, ref float enter, ref float exit)
        {
            if (Mathf.Abs(delta) < 0.000001f) return start >= min && start <= max;
            float near = (min - start) / delta, far = (max - start) / delta;
            if (near > far) { float t = near; near = far; far = t; }
            enter = Mathf.Max(enter, near);
            exit = Mathf.Min(exit, far);
            return enter <= exit;
        }

        bool InsideAnyObstacle(float x, float z)
        {
            for (int i = 0; i < ObstacleBounds.Count; i++)
            {
                Bounds b = ObstacleBounds[i];
                if (x < b.min.x || x > b.max.x) continue;
                if (z < b.min.z || z > b.max.z) continue;
                return true;
            }
            return false;
        }

        /// <summary>用碰撞盒过滤不可走导航节点/边（A* 只用有效数据）。</summary>
        void BuildWalkability()
        {
            if (Nodes == null || _edgeStart == null) return;
            int n = Nodes.Length;
            _nodeValid = new bool[n];
            for (int i = 0; i < n; i++)
            {
                _nodeValid[i] = !InsideAnyObstacle(Nodes[i].x, Nodes[i].z);
            }
            int total = _edgeTarget.Length;
            _edgeValidCsr = new bool[total];
            for (int i = 0; i < n; i++)
            {
                Vector3 pi = NodePosition(i);
                for (int e = _edgeStart[i]; e < _edgeStart[i + 1]; e++)
                {
                    int j = _edgeTarget[e];
                    if (!_nodeValid[i] || !_nodeValid[j]) { _edgeValidCsr[e] = false; continue; }
                    _edgeValidCsr[e] = !SegmentBlocked2D(pi, NodePosition(j));
                }
            }
            int validNodes = 0, validEdges = 0;
            for (int i = 0; i < n; i++) if (_nodeValid[i]) validNodes++;
            for (int e = 0; e < total; e++) if (_edgeValidCsr[e]) validEdges++;
            ValidNodeCount = validNodes;
            ValidEdgeCount = validEdges / 2;
            Debug.Log("[MapWorldProfile] 导航有效性：节点 " + validNodes + "/" + n + "，边 " + (validEdges / 2) + "/" + Edges.Length);
        }

        /// <summary>节点编号 → Unity 实际坐标（x, GroundY, 原 blender 平面 y）。</summary>
        public Vector3 NodePosition(int index)
        {
            var n = Nodes[index];
            return new Vector3(n.x, GroundY, n.z);
        }

        /// <summary>节点是否在地面层（忽略露台/平台节点，角色没有爬升）。</summary>
        public bool NodeUsable(int index)
        {
            return Mathf.Abs(Nodes[index].y) < 0.4f;
        }

        public int NearestUsableNode(Vector3 p, int maxCheck = 40)
        {
            if (Nodes == null) return -1;
            int best = -1;
            float bestD = float.MaxValue;
            for (int i = 0; i < Nodes.Length; i++)
            {
                if (!NodeUsable(i) || !IsNodeValid(i)) continue;
                float dx = Nodes[i].x - p.x, dz = Nodes[i].z - p.z;
                float d = dx * dx + dz * dz;
                if (d < bestD) { bestD = d; best = i; }
            }
            return best;
        }

        /// <summary>按距离升序取附近可用节点（最多 count 个），供 A* 接入/接出。</summary>
        public void CollectNearbyNodes(Vector3 p, int count, List<int> outIndices)
        {
            outIndices.Clear();
            if (Nodes == null) return;
            // 地图节点 ~3500 个：线性扫描一遍并存下前 count 个最小距离即可（导航是低频操作）。
            float[] bestD = new float[count];
            int[] bestI = new int[count];
            int filled = 0;
            for (int i = 0; i < Nodes.Length; i++)
            {
                if (!NodeUsable(i) || !IsNodeValid(i)) continue;
                float dx = Nodes[i].x - p.x, dz = Nodes[i].z - p.z;
                float d = dx * dx + dz * dz;
                if (filled < count)
                {
                    bestD[filled] = d; bestI[filled] = i; filled++;
                    // 插入排序保持升序
                    for (int k = filled - 1; k > 0 && bestD[k] < bestD[k - 1]; k--)
                    {
                        float td = bestD[k]; bestD[k] = bestD[k - 1]; bestD[k - 1] = td;
                        int ti = bestI[k]; bestI[k] = bestI[k - 1]; bestI[k - 1] = ti;
                    }
                }
                else if (d < bestD[count - 1])
                {
                    bestD[count - 1] = d; bestI[count - 1] = i;
                    for (int k = count - 1; k > 0 && bestD[k] < bestD[k - 1]; k--)
                    {
                        float td = bestD[k]; bestD[k] = bestD[k - 1]; bestD[k - 1] = td;
                        int ti = bestI[k]; bestI[k] = bestI[k - 1]; bestI[k - 1] = ti;
                    }
                }
            }
            for (int i = 0; i < filled; i++) outIndices.Add(bestI[i]);
        }

        /// <summary>节点 a → b 的边代价（若无直接边返回 -1）。仅用于校验/调试。</summary>
        public float EdgeCost(int a, int b)
        {
            if (_edgeStart == null) return -1f;
            for (int e = _edgeStart[a]; e < _edgeStart[a + 1]; e++)
                if (_edgeTarget[e] == b) return _edgeCost[e];
            return -1f;
        }

        public int EdgeStart(int node) { return _edgeStart[node]; }
        public int EdgeEnd(int node) { return _edgeStart[node + 1]; }
        public int EdgeTarget(int e) { return _edgeTarget[e]; }
        public float EdgeWeight(int e) { return _edgeCost[e]; }
    }
}
