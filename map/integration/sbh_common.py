# -*- coding: utf-8 -*-
"""sbh_common.py — SovietBlueHour 地图数据 → Unity 集成分析的公共库。

只读 ``Maps/SovietBlueHour/map/`` 下的数据文件，不修改任何地图/Unity 资产。

坐标契约（本文件里唯一的权威定义）
----------------------------------
Blender（源数据）      : Z-up，米，+X 东，+Y 北，地面 z=0
glTF（GLB 导出，yup）  : (gx, gy, gz) = ( bx,  bz, -by )
Unity（UniGLTF 默认 ReverseZ，ScriptedImporter 会再翻转 z 轴）
                       : (ux, uy, uz) = ( gx,  gy, -gz ) = ( bx,  bz,  by )

=> **Unity 实际坐标 = (bx, bz, by)**
   高度(bz) 进 y；北向(by) 进 z，且**符号不变**。

注意 ``nav_graph.json`` / ``collision.json`` / ``scene_manifest.json`` 里的
"unity" 字段用的是另一套约定 ``(x, z, -y)``（z 轴取反），**不能**直接喂给 Unity。
本库一律从 blender 字段换算，并提供 ``nav_unity_*`` 函数把旧字段翻译成实际坐标。

Unity 左手系朝向约定（本库 yaw_deg 的定义）
------------------------------------------
    forward = (sin(yaw), 0, cos(yaw))      yaw=0 → +Z（北）；yaw=90 → +X（东）
    yaw = atan2(forward.x, forward.z) = atan2(dx_blender, dy_blender)   [度]
    pitch = asin(forward.y)               正 = 抬头；Unity 里 camera.eulerAngles.x = -pitch
"""
import json
import math
import os

HERE = os.path.dirname(os.path.abspath(__file__))
MAP_DIR = os.path.abspath(os.path.join(HERE, "..", "map"))

# --------------------------------------------------------------------------
# 坐标换算
# --------------------------------------------------------------------------


def b2u(x, y, z=0.0):
    """Blender 点 (x, y, z) → Unity 实际坐标 (ux, uy, uz) = (x, z, y)。"""
    return [round(float(x), 2), round(float(z), 2), round(float(y), 2)]


def u2b(ux, uy, uz):
    """Unity 实际坐标 → Blender 点（自逆变换）。"""
    return [round(float(ux), 2), round(float(uz), 2), round(float(uy), 2)]


def nav_unity_to_actual(p):
    """把 nav/collision 里的旧 'unity' 字段 (x, z, -y) 翻成 Unity 实际坐标。"""
    return [round(float(p[0]), 2), round(float(p[1]), 2), round(-float(p[2]), 2)]


def yaw_from_blender_dir(dx, dy):
    """Blender 平面方向 (dx 东, dy 北) → Unity yaw（度，0=+Z 北，顺时针向 +X 东为正）。"""
    return round(math.degrees(math.atan2(dx, dy)), 2)


def yaw_to_blender_dir(yaw_deg):
    a = math.radians(yaw_deg)
    return math.sin(a), math.cos(a)


def forward_from_yaw(yaw_deg, pitch_deg=0.0):
    """Unity 朝向 → 单位 forward 向量（Unity 左手系）。"""
    y = math.radians(yaw_deg)
    p = math.radians(pitch_deg)
    return [math.sin(y) * math.cos(p), math.sin(p), math.cos(y) * math.cos(p)]


def yaw_pitch_between(a, b):
    """两点 (unity 坐标) 的 yaw / pitch（度）。"""
    dx, dy, dz = b[0] - a[0], b[1] - a[1], b[2] - a[2]
    horiz = math.hypot(dx, dz)
    yaw = math.degrees(math.atan2(dx, dz))
    pitch = math.degrees(math.atan2(dy, horiz))
    return round(yaw, 2), round(pitch, 2)


# --------------------------------------------------------------------------
# 数据装载
# --------------------------------------------------------------------------


class MapData(object):
    """nav_graph / collision / scene_manifest / plan 的统一只读视图。"""

    def __init__(self, map_dir=MAP_DIR):
        self.map_dir = map_dir
        self.nav = _load_json(os.path.join(map_dir, "nav_graph.json"))
        self.coll = _load_json(os.path.join(map_dir, "collision.json"))
        self.manifest = _load_json(os.path.join(map_dir, "scene_manifest.json"))
        plan_path = os.path.abspath(os.path.join(map_dir, "..", "tools", "plan.json"))
        self.plan = _load_json(plan_path) if os.path.exists(plan_path) else {}

        self.nodes = self.nav["nodes"]
        self.edges = self.nav["edges"]
        self.pois = {p["id"]: p for p in self.nav["pois"]}
        self.route_anchors = {a["name"]: a for a in self.nav["route_anchors"]}
        self.boxes = self.coll["boxes"]
        self.player = self.coll["player"]  # radius / eye / step / walk-under
        self._node_xy = [(n["x"], n["y"], n["z"]) for n in self.nodes]

        # plan.json 里的中文名索引
        self.plan_blocks = {b["id"]: b for b in self.plan.get("blocks", [])}
        self.plan_landmarks = {l["id"]: l for l in self.plan.get("landmarks", [])}
        self.plan_zones = {z["id"]: z for z in self.plan.get("zones", [])}
        self.plan_paths = {p["id"]: p for p in self.plan.get("paths", [])}
        self.plan_roads = {r["id"]: r for r in self.plan.get("roads", [])}

    # ---------------- 基本几何 ----------------

    def node(self, node_id):
        for n in self.nodes:
            if n["id"] == node_id:
                return n
        raise KeyError(node_id)

    def nearest_node(self, x, y, max_dist=None, exclude_ids=()):
        """返回 (node, 平面距离)。"""
        best = None
        best_d = 1e30
        for n in self.nodes:
            if n["id"] in exclude_ids:
                continue
            d = math.hypot(n["x"] - x, n["y"] - y)
            if d < best_d:
                best, best_d = n, d
        if max_dist is not None and best_d > max_dist:
            return None, best_d
        return best, best_d

    def nav_density(self, x, y, r):
        c = 0
        for n in self.nodes:
            if abs(n["x"] - x) <= r and abs(n["y"] - y) <= r:
                if math.hypot(n["x"] - x, n["y"] - y) <= r:
                    c += 1
        return c

    # ---------------- 碰撞 / 地形 ----------------

    def ground_height(self, x, y):
        """该点（Blender x/y）站立面高度 z：台地平台 / 台阶 / 可踏上的矮台 / 默认 0。"""
        h = 0.0
        step = self.player["step_height_m"]
        for b in self.boxes:
            if b["kind"] in ("terrace_step", "terrace_platform"):
                if _in_rect(b["min"][0], b["min"][1], b["max"][0], b["max"][1], x, y, 0.0):
                    h = max(h, b["max"][2])
            elif b["kind"] == "low" and b["max"][2] <= step + 1e-6:
                # 可跨上的矮台（例如 0.25m 高的电车站台）：站上去 y 就等于台面高度
                if _in_rect(b["min"][0], b["min"][1], b["max"][0], b["max"][1], x, y, 0.0):
                    h = max(h, b["max"][2])
        return round(h, 2)

    def blockers_at(self, x, y, base_z=None):
        """站在 (x,y)、脚底 base_z 时真正挡人的盒子（含 radius 外扩）。

        判定：盒子在 XZ 上盖住点（外扩 player.radius），且垂直方向与人形胶囊相交
        [base_z + step_height, base_z + walk_under]。
        台阶(terrace_step)/平台(terrace_platform)/地面不算阻挡；
        拱门过梁(arch_lintel, 底 2.95m) 高于 walk_under → 自然不挡。
        """
        if base_z is None:
            base_z = self.ground_height(x, y)
        r = self.player["radius_m"]
        lo = base_z + self.player["step_height_m"]
        hi = base_z + self.player["walk_under_clearance_m"]
        hits = []
        for b in self.boxes:
            if b["kind"] in ("ground", "terrace_step", "terrace_platform"):
                continue
            if not _in_rect(b["min"][0], b["min"][1], b["max"][0], b["max"][1], x, y, r):
                continue
            if b["max"][2] <= lo + 1e-6:      # 可跨越的低矮物（台阶/矮沿）
                continue
            if b["min"][2] >= hi - 1e-6:      # 头顶以上的过梁/雨棚
                continue
            hits.append(b)
        return hits

    def obstruction_score(self, x, y, base_z=None):
        hits = self.blockers_at(x, y, base_z)
        if not hits:
            return 0.0
        return max(_rect_clearance_far(b, x, y) for b in hits)

    def raycast_xz(self, x, y, dx, dy, max_dist=120.0, min_h=0.6, skip_kinds=("ground",)):
        """已知朝向的水平射线，返回 [(距离, box_id, kind)]，按距离排序。

        min_h: 只统计高度 > min_h 的盒子（矮喷泉沿不算遮挡视线）。
        """
        n = math.hypot(dx, dy)
        if n < 1e-9:
            return []
        dx, dy = dx / n, dy / n
        hits = []
        for b in self.boxes:
            if b["kind"] in skip_kinds or b["kind"] in ("terrace_step", "terrace_platform"):
                continue
            if b["max"][2] <= min_h:
                continue
            t = _ray_rect(x, y, dx, dy, b["min"][0], b["min"][1], b["max"][0], b["max"][1])
            if t is not None and 0.0 <= t <= max_dist:
                hits.append((round(t, 2), b["id"], b["kind"]))
        hits.sort()
        return hits

    def first_hit(self, x, y, yaw_deg, max_dist=120.0, min_h=0.6):
        dx, dy = yaw_to_blender_dir(yaw_deg)
        h = self.raycast_xz(x, y, dx, dy, max_dist, min_h)
        return h[0] if h else None

    # ---------------- 可行走性 ----------------

    def walkable_report(self, x, y, base_z=None, nav_radius=2.5):
        """一个点的完整可行走性判定（position / approach 都用它）。"""
        if base_z is None:
            base_z = self.ground_height(x, y)
        node, dist = self.nearest_node(x, y)
        blockers = self.blockers_at(x, y, base_z)
        water = [b for b in self.boxes if b["kind"] == "water"
                 and _in_rect(b["min"][0], b["min"][1], b["max"][0], b["max"][1], x, y, 0.0)]
        report = {
            "nearest_nav_node": node["id"] if node else None,
            "nearest_nav_dist_m": round(dist, 2),
            "nav_node_kind": node["kind"] if node else None,
            "nav_density_r4": self.nav_density(x, y, 4.0),
            "ground_z": round(base_z, 2),
            "inside_collision": sorted({b["id"] for b in blockers}),
            "inside_water": sorted({b["id"] for b in water}),
            "walkable": bool(blockers == [] and water == [] and dist <= nav_radius),
            "warnings": [],
        }
        if dist > nav_radius:
            report["warnings"].append("距离最近 nav 节点 %.2fm > %.2fm" % (dist, nav_radius))
        if blockers:
            report["warnings"].append("位于碰撞盒内: " + ", ".join(sorted({b["id"] for b in blockers})))
        if water:
            report["warnings"].append("位于水面碰撞盒内: " + ", ".join(sorted({b["id"] for b in water})))
        return report

    def snap(self, x, y, max_dist=6.0):
        """把任意种子点吸附到最近 nav 节点（保证落在导航图上）。"""
        node, d = self.nearest_node(x, y)
        if node is None or d > max_dist:
            return None, d
        return node, d

    # ---------------- 距离 / 命名 ----------------

    def landmark_distances(self, x, y, top=6, max_dist=180.0):
        """附近地标（nav POI）距离表：[(label_zh, id, dist)]。"""
        out = []
        for pid, p in self.pois.items():
            d = math.hypot(p["x"] - x, p["y"] - y)
            if d <= max_dist:
                out.append((pid, d))
        out.sort(key=lambda t: t[1])
        return out[:top]

    def poi(self, pid):
        return self.pois[pid]

    def route_node(self, name):
        return self.node(self.route_anchors[name]["node"])


# --------------------------------------------------------------------------
# 几何小工具
# --------------------------------------------------------------------------


def _in_rect(x0, y0, x1, y1, px, py, pad=0.0):
    return (x0 - pad) <= px <= (x1 + pad) and (y0 - pad) <= py <= (y1 + pad)


def _rect_clearance_far(b, x, y):
    """点到矩形边界外的最小距离（正=在外面）。"""
    dx = max(b["min"][0] - x, 0.0, x - b["max"][0])
    dy = max(b["min"][1] - y, 0.0, y - b["max"][1])
    return math.hypot(dx, dy)


def _ray_rect(px, py, dx, dy, x0, y0, x1, y1):
    """2D 射线 vs 轴对齐矩形（slab 法），返回进入距离 t 或 None。"""
    tmin, tmax = 0.0, 1e30
    for p, d, lo, hi in ((px, dx, x0, x1), (py, dy, y0, y1)):
        if abs(d) < 1e-12:
            if p < lo or p > hi:
                return None
            continue
        t1 = (lo - p) / d
        t2 = (hi - p) / d
        if t1 > t2:
            t1, t2 = t2, t1
        tmin = max(tmin, t1)
        tmax = min(tmax, t2)
        if tmin > tmax:
            return None
    return tmin if tmax >= 0 else None


def _load_json(path):
    with open(path, "r", encoding="utf-8") as f:
        return json.load(f)


def fmt(v, n=2):
    return round(float(v), n)


def dump_json(obj, path):
    with open(path, "w", encoding="utf-8") as f:
        json.dump(obj, f, ensure_ascii=False, indent=2)
    return path
