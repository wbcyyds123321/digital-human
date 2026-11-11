"""把 SovietBlueHour 导出数据转成 Unity 运行时数据（Unity 实际坐标）。

坐标契约（UniGLTF ReverseZ 导入，已由 geometry_probe 实测确认）：
    Blender (bx, by, bz)  ->  Unity (bx, bz, by)
其中 Blender z 是高度，Unity y 是高度。

产出（Unity 构建器会复制到 Assets/Resources/SovietBlueHour/）：
    collision_unity.json : 91 个 AABB，min/max = [x, y(高度), z]
    nav_compact.json      : 导航图，node = {x, y(高度), z}，edge = {a, b, d}
    anchors.json          : 语义锚点 + 出生点（已用碰撞盒 + 导航图可达性校验）
    walkability_report.json
"""
import json
import math
import os

ROOT = os.environ.get("SBH_PROJECT_ROOT", ".")
MAP = os.path.join(ROOT, "Maps", "SovietBlueHour", "map")
OUT = os.path.join(ROOT, "Maps", "SovietBlueHour", "integration")
os.makedirs(OUT, exist_ok=True)


def b2u(x, y, z):
    """Blender -> Unity 实际坐标。"""
    return (x, z, y)


# ---------------------------------------------------------------- collision
coll = json.load(open(os.path.join(MAP, "collision.json"), encoding="utf-8"))
boxes_out = []
for b in coll["boxes"]:
    x0, y0, z0 = b["min"]
    x1, y1, z1 = b["max"]
    umin = [min(x0, x1), min(z0, z1), min(y0, y1)]
    umax = [max(x0, x1), max(z0, z1), max(y0, y1)]
    boxes_out.append({
        "id": b["id"],
        "min": [round(v, 3) for v in umin],
        "max": [round(v, 3) for v in umax],
        "kind": b.get("kind", ""),
    })
json.dump({"schema": "sbh.unity.collision.v1", "boxes": boxes_out},
          open(os.path.join(OUT, "collision_unity.json"), "w", encoding="utf-8"),
          ensure_ascii=False, indent=1)

# ---------------------------------------------------------------- nav graph
nav = json.load(open(os.path.join(MAP, "nav_graph.json"), encoding="utf-8"))
nodes_out = []
for n in nav["nodes"]:
    ux, uy, uz = b2u(n["x"], n["y"], n["z"])
    nodes_out.append({"x": round(ux, 3), "y": round(uy, 3), "z": round(uz, 3),
                      "kind": n.get("kind", "")})
edges_out = [{"a": e["a"], "b": e["b"], "d": round(e["dist"], 3)} for e in nav["edges"]]
json.dump({"schema": "sbh.unity.nav.v1", "nodes": nodes_out, "edges": edges_out},
          open(os.path.join(OUT, "nav_compact.json"), "w", encoding="utf-8"),
          ensure_ascii=False)

NODE = {n["id"]: n for n in nav["nodes"]}
POI = {p["id"]: p for p in nav["pois"]}
RA = {a["name"]: a for a in nav["route_anchors"]}
MANIFEST = {m["id"]: m for m in json.load(open(os.path.join(MAP, "scene_manifest.json"), encoding="utf-8"))}

SPAWN = [-14.0, 0.0, -62.0]
SPAWN_YAW = 160.0

# ---------------------------------------------------------------- 导航有效性
R = 0.35
M = 0.15
solid = [b for b in boxes_out if b["kind"] not in ("ground", "arch_lintel")]


def inside_box_2d(px, pz, b):
    return (b["min"][0] - R - M <= px <= b["max"][0] + R + M and
            b["min"][2] - R - M <= pz <= b["max"][2] + R + M)


node_pos = [(n["x"], n["z"]) for n in nodes_out]
node_valid = [not any(inside_box_2d(px, pz, b) for b in solid) for (px, pz) in node_pos]
ground_valid = [node_valid[i] and abs(nodes_out[i]["y"]) < 0.4 for i in range(len(nodes_out))]
print("nav nodes valid:", sum(node_valid), "/", len(node_valid),
      " ground+valid:", sum(ground_valid), "/", len(ground_valid))


def _seg_blocked(ax, az, bx, bz):
    for b in solid:
        e0, e1, ok = 0.0, 1.0, True
        for st, dl, mn, mx in ((ax, bx - ax, b["min"][0], b["max"][0]),
                               (az, bz - az, b["min"][2], b["max"][2])):
            if abs(dl) < 1e-6:
                if not (mn <= st <= mx):
                    ok = False
                    break
            else:
                n1, f1 = (mn - st) / dl, (mx - st) / dl
                if n1 > f1:
                    n1, f1 = f1, n1
                e0, e1 = max(e0, n1), min(e1, f1)
        if ok and e0 <= e1:
            return True
    return False


adj = {}
for e in edges_out:
    a, b = e["a"], e["b"]
    if not (node_valid[a] and node_valid[b]):
        continue
    if _seg_blocked(nodes_out[a]["x"], nodes_out[a]["z"], nodes_out[b]["x"], nodes_out[b]["z"]):
        continue
    adj.setdefault(a, []).append(b)
    adj.setdefault(b, []).append(a)

_seed = min((i for i in range(len(nodes_out)) if ground_valid[i]),
            key=lambda i: (node_pos[i][0] - SPAWN[0]) ** 2 + (node_pos[i][1] - SPAWN[2]) ** 2)
reachable = {_seed}
_stack = [_seed]
while _stack:
    u = _stack.pop()
    for v in adj.get(u, ()):
        if v not in reachable:
            reachable.add(v)
            _stack.append(v)
print("reachable ground nodes from spawn:", len(reachable), "/", sum(ground_valid))

# ---------------------------------------------------------------- 锚点
anchors = []
seen_ids = set()
used_nodes = set()


def walkable(px, pz):
    return not any(inside_box_2d(px, pz, b) for b in solid)


def nearest_reachable(px, pz, max_radius=45.0, avoid_used=True):
    best, best_d = None, max_radius * max_radius
    for i in reachable:
        if not ground_valid[i]:
            continue
        if avoid_used and i in used_nodes:
            continue
        d = (node_pos[i][0] - px) ** 2 + (node_pos[i][1] - pz) ** 2
        if d < best_d:
            best_d, best = d, i
    if best is None:
        return None
    return [round(node_pos[best][0], 2), 0.0, round(node_pos[best][1], 2)], best


def add(aid, label, pos, source, note="", node_id=None):
    if aid in seen_ids:
        return
    if pos is None:
        print("  skip anchor (不可达):", aid, label)
        return
    seen_ids.add(aid)
    anchors.append({"id": aid, "label": label,
                    "pos": [round(float(v), 2) for v in pos],
                    "source": source, "notes": note})
    if node_id is not None:
        used_nodes.add(node_id)


def add_at_node(aid, label, node_id, source, note=""):
    n = NODE[node_id]
    ux, uy, uz = b2u(n["x"], n["y"], n["z"])
    if node_id in reachable and ground_valid[node_id]:
        add(aid, label, [round(ux, 2), 0.0, round(uz, 2)], source, note or ("node=" + str(node_id)), node_id)
        return
    hit = nearest_reachable(ux, uz)
    if hit is None:
        print("  skip anchor (附近无可达节点):", aid, label)
        return
    pos, used = hit
    add(aid, label, pos, source, (note or ("node=" + str(node_id))) + " -> reachable node=" + str(used), used)


# ---- 1) 分析阶段验证过的 30 个锚点（position 不可走时用 approach 站位）
SPEC_PATH = os.path.join(OUT, "integration_spec.json")
spec_anchors = []
if os.path.exists(SPEC_PATH):
    try:
        spec_anchors = json.load(open(SPEC_PATH, encoding="utf-8")).get("anchors") or []
    except Exception as e:
        print("  (integration_spec.json 读取失败，退回 POI 生成):", e)

for a in spec_anchors:
    aid = a.get("id")
    label = a.get("label_zh") or aid
    pos = a.get("position") or []
    approach = a.get("approach_position") or []
    if not aid or len(pos) < 3:
        continue
    if aid == 'tram_stop':   # 站台 0.25m 高、角色 GroundSnap 固定 y=0，站不上站台，避免误导 LLM
        continue
    target = None
    if walkable(pos[0], pos[2]) and any(
            i in reachable for i in range(len(nodes_out))
            if abs(node_pos[i][0] - pos[0]) < 0.05 and abs(node_pos[i][1] - pos[2]) < 0.05):
        target = [pos[0], 0.0, pos[2]]
    elif len(approach) >= 3:
        hit = nearest_reachable(approach[0], approach[2])
        if hit is not None:
            target = hit[0]
            used_nodes.add(hit[1])
    if target is None:
        hit = nearest_reachable(pos[0], pos[2])
        if hit is not None:
            target = hit[0]
            used_nodes.add(hit[1])
    add(aid, label, target, "curated", a.get("notes", "")[:80])

# ---- 2) 其余建筑 POI（中文本地化）
POI_EXTRA = {
    "CLINIC": ("clinic", "门诊部"),
    "DOM_BYTA": ("dom_byta", "生活服务楼"),
    "ROBOTICS": ("robotics", "机器人研究院"),
    "MUSEUM": ("museum", "军事博物馆"),
    "UKRAINA": ("ukraina", "乌克兰饭店"),
    "MOT": ("mot", "交通部大楼"),
    "DRUZHBA": ("druzhba", "友谊疗养院"),
    "HIT": ("hit", "中苏学堂"),
    "HEX_STATION": ("hex_station", "六边形车站"),
    "PAVILION": ("park_pavilion", "公园凉亭"),
    "PARK_SIGN": ("park_entrance", "公园入口"),
}
for poi_id, (aid, label) in POI_EXTRA.items():
    p = POI.get(poi_id)
    if not p:
        continue
    add_at_node(aid, label, p["node"], "nav_poi")

# 公交总站（从 route anchor 取，A_TERMINAL_W 校验过位置合理）
ra = RA.get("A_TERMINAL_W")
if ra:
    add_at_node("bus_terminal", "公交总站", ra["node"], "route_anchor")

# 西公园：路线锚点 A_WEST_PARK 位置在 x=-188，属地图西缘
ra = RA.get("A_WEST_PARK")
if ra:
    add_at_node("west_park", "西公园", ra["node"], "route_anchor")

# ---- 3) 旧场景兼容 id（保留给已有验收脚本/提示词）
add("start_point", "起始位置", SPAWN, "legacy")
add("market_square", "市场广场", [125.5, 0.0, -50.0], "legacy")
add("street_center", "街道中央", [0.0, 0.0, -72.0], "legacy")

# “商店门口”映射到面包房门口（地图里的近处商店）
bakery = next((a for a in anchors if a["id"] == "bakery"), None)
if bakery is not None:
    add("store_door", "面包房门口", bakery["pos"], "legacy", "alias of bakery")

json.dump({
    "schema": "sbh.unity.anchors.v1",
    "spawn": SPAWN,
    "spawn_yaw": SPAWN_YAW,
    "anchors": anchors,
}, open(os.path.join(OUT, "anchors.json"), "w", encoding="utf-8"), ensure_ascii=False, indent=1)

# ---------------------------------------------------------------- 校验
def inside_box(px, pz, b):
    return (b["min"][0] - R - M <= px <= b["max"][0] + R + M and
            b["min"][2] - R - M <= pz <= b["max"][2] + R + M)


report = {"anchors": [], "spawn": {}, "nav": {"nodes_total": len(nodes_out),
          "nodes_valid": sum(node_valid), "nodes_ground_valid": sum(ground_valid),
          "reachable_from_spawn": len(reachable)}}
for a in anchors:
    px, pz = a["pos"][0], a["pos"][2]
    hit = [b["id"] for b in solid if inside_box(px, pz, b)]
    report["anchors"].append({"id": a["id"], "label": a["label"], "pos": a["pos"],
                              "inside_obstacles": hit, "ok": len(hit) == 0})
hit = [b["id"] for b in solid if inside_box(SPAWN[0], SPAWN[2], b)]
report["spawn"] = {"pos": SPAWN, "yaw": SPAWN_YAW, "inside_obstacles": hit, "ok": len(hit) == 0}
bad = [r for r in report["anchors"] if not r["ok"]]
json.dump(report, open(os.path.join(OUT, "walkability_report.json"), "w", encoding="utf-8"),
          ensure_ascii=False, indent=1)

print("collision boxes:", len(boxes_out))
print("nav nodes:", len(nodes_out), "edges:", len(edges_out))
print("anchors:", len(anchors), "walkable:", len(anchors) - len(bad))
print("spawn:", SPAWN, "ok=", report["spawn"]["ok"], report["spawn"]["inside_obstacles"])
if bad:
    print("WARN anchors inside obstacles:")
    for r in bad:
        print("  ", r["id"], r["label"], r["pos"], r["inside_obstacles"])
