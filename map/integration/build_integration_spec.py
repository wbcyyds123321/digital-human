# -*- coding: utf-8 -*-
"""build_integration_spec.py — 生成 Maps/SovietBlueHour/integration/integration_spec.json

用法:
    python build_integration_spec.py

只读 map/ 与 integration/ 下 lead 生成的运行时数据（不覆盖），输出 integration_spec.json。
坐标一律 Unity 实际坐标 = (blender.x, blender.z, blender.y)，见 sbh_common.py 顶部契约。
"""
import json
import math
import os
import struct
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import sbh_common as S  # noqa: E402

HERE = os.path.dirname(os.path.abspath(__file__))
MAP = os.path.join(HERE, "..", "map")
GLB = os.path.join(MAP, "SovietBlueHour_game.glb")
OUT = os.path.join(HERE, "integration_spec.json")


# ==========================================================================
# 0. 坐标换算的实证：直接从 GLB 的 accessor min/max 反推
# ==========================================================================

def glb_mesh_bbox(glb_path, node_name):
    """读取 GLB JSON chunk，返回某 node 的 mesh 在 glTF 世界空间的 bbox。"""
    with open(glb_path, "rb") as f:
        magic, ver, total = struct.unpack("<4sII", f.read(12))
        if magic != b"glTF":
            raise RuntimeError("not a GLB")
        clen, ctype = struct.unpack("<II", f.read(8))
        doc = json.loads(f.read(clen).decode("utf-8"))
    nodes = doc["nodes"]
    for n in nodes:
        if n.get("name", "").split(".")[0] == node_name.split(".")[0]:
            mi = n.get("mesh")
            if mi is None:
                continue
            mn = [1e30] * 3
            mx = [-1e30] * 3
            for prim in doc["meshes"][mi]["primitives"]:
                acc = doc["accessors"][prim["attributes"]["POSITION"]]
                for k in range(3):
                    mn[k] = min(mn[k], acc["min"][k])
                    mx[k] = max(mx[k], acc["max"][k])
            t = n.get("translation", [0.0, 0.0, 0.0])
            mn = [mn[k] + t[k] for k in range(3)]
            mx = [mx[k] + t[k] for k in range(3)]
            return {"gltf_min": [round(v, 2) for v in mn], "gltf_max": [round(v, 2) for v in mx],
                    "node_translation": t}
    raise KeyError(node_name)


def gltf_bbox_to_blender(b):
    """gltf (x, y_up, z) → blender (x, -z, y)"""
    return ([b[0], -b[2], b[1]])


def gltf_bbox_to_unity(b):
    """gltf (x, y_up, z) → Unity ReverseZ 实际 (x, y, -z)"""
    return ([b[0], b[1], -b[2]])


def coord_verification():
    checks = []
    for name, man_id in (("SHOP", "SHOP"), ("METRO", "METRO"), ("MOTHERLAND", "MOTHERLAND")):
        bb = glb_mesh_bbox(GLB, "BLD_" + man_id)
        bl_of_min = gltf_bbox_to_blender(bb["gltf_min"])
        bl_of_max = gltf_bbox_to_blender(bb["gltf_max"])
        un_min = gltf_bbox_to_unity(bb["gltf_min"])
        un_max = gltf_bbox_to_unity(bb["gltf_max"])
        man = next(m for m in MB.manifest if m["id"] == man_id)
        cx, cy = man["center_blender"]
        checks.append({
            "landmark": name,
            "cn": man["cn"],
            "glb_mesh": "BLD_" + man_id,
            "glb_gltf_bbox_min": bb["gltf_min"],
            "glb_gltf_bbox_max": bb["gltf_max"],
            "glb_node_translation": bb["node_translation"],
            "derived_blender_xz_bbox": [min(bl_of_min[0], bl_of_max[0]), min(bl_of_min[1], bl_of_max[1]),
                                        max(bl_of_min[0], bl_of_max[0]), max(bl_of_min[1], bl_of_max[1])],
            "derived_unity_actual_bbox_min": [round(min(un_min[k], un_max[k]), 2) for k in range(3)],
            "derived_unity_actual_bbox_max": [round(max(un_min[k], un_max[k]), 2) for k in range(3)],
            "manifest_center_blender": man["center_blender"],
            "manifest_center_unity_field": man["center_unity"],
            "unity_actual_center_from_blender": S.b2u(cx, cy, 0.0),
            "unity_actual_center_from_manifest_field_flipped": S.nav_unity_to_actual(man["center_unity"]),
            "wrong_if_manifest_field_used_verbatim": [round(v, 2) for v in man["center_unity"]],
            "match": abs(min(un_min[0], un_max[0])) >= 0 and abs((un_min[0] + un_max[0]) / 2 - cx) < 0.6
                     and abs((un_min[2] + un_max[2]) / 2 - cy) < 0.6,
            "manifest_field_flip_needed": abs(man["center_unity"][2] + man["center_blender"][1]) < 0.51,
        })
    return checks


# ==========================================================================
# 1. 出生点 + 第三人称相机（lead 要求：face-to-face / over-shoulder 成对）
# ==========================================================================

def camera_rig(char_unity, char_yaw, ground_y, yaw_offset, dist, pitch, look_h):
    """按 lead 的参数化算相机。

    yawOffset: 0 = 角色正后方；180 = 角色正前方（面对面）；正 = 绕向一侧。
    pitch: 正值 = 相机在上往下拍。
    相机方位 bearing = char_yaw + 180 + yawOffset（Unity yaw 约定：0=+Z，顺时针为正）
    """
    brg = math.radians(char_yaw + 180.0 + yaw_offset)
    cam = [char_unity[0] + dist * math.sin(brg),
           ground_y + look_h + dist * math.tan(math.radians(pitch)),
           char_unity[2] + dist * math.cos(brg)]
    look = [char_unity[0], ground_y + look_h, char_unity[2]]
    cam = [round(v, 2) for v in cam]
    look = [round(v, 2) for v in look]
    yaw, cam_pitch = S.yaw_pitch_between(cam, look)
    return {
        "unity_position": cam,
        "look_at": look,
        "camera_yaw_deg": yaw,          # 相机自身朝向（Unity yaw）
        "pitch_deg": cam_pitch,         # 负 = 俯拍（与 lead 的 pitch 参数相反号）
        "fov_vertical_deg": 60.0,
        "yawOffset": yaw_offset,
        "distance": dist,
        "pitch": pitch,
        "lookHeight": look_h,
        "view_bearing_deg": round((char_yaw + yaw_offset) % 360.0, 2),
    }


def vision_from_unity(cam, look, max_d=140.0):
    """相机视线（Unity）→ 转 blender 做 2D 射线，找画面中轴上的第一个体块。"""
    dx, dz = look[0] - cam[0], look[2] - cam[2]
    hits = MB.raycast_xz(cam[0], cam[2], dx, dz, max_dist=max_d, min_h=0.8)
    return [(hid, hk, round(d, 1)) for d, hid, hk in hits[:4]]


def cam_clearance(cam, look):
    """相机是否插进建筑 / 视线上是否有遮挡。"""
    x, z, y = cam[0], cam[2], cam[1]
    inside = []
    for b in MB.boxes:
        if b["kind"] in ("ground", "terrace_step", "terrace_platform", "low"):
            continue
        if b["min"][0] <= x <= b["max"][0] and b["min"][1] <= z <= b["max"][1]:
            if b["min"][2] < y < b["max"][2]:
                inside.append(b["id"])
    # 相机到角色连线上的遮挡（只看高过相机的体块）
    dx, dz = look[0] - x, look[2] - z
    seg = math.hypot(dx, dz)
    blocked = []
    for d, hid, hk in MB.raycast_xz(x, z, dx, dz, max_dist=seg - 0.25, min_h=0.8):
        blocked.append({"id": hid, "kind": hk, "dist_m": d})
    return {"inside_collision": sorted(set(inside)),
            "line_of_sight_blocked": blocked[:3],
            "ok": not inside}


SPAWNS_CFG = [
    {
        "id": "SP_C1_PLAZA",
        "label_zh": "中央广场·文化宫前",
        "blender_xy": (-14.0, -62.0),
        "yaw_deg": 160.0,
        "face": dict(yaw_offset=225.0, dist=3.8, pitch=6.0, look_h=1.10),
        "ots": dict(yaw_offset=0.0, dist=4.3, pitch=12.0, look_h=1.30),
        "preview": "previews/C1_FACE.png / previews/C1_OTS.png",
        "rationale_zh": ("站在中央广场西南象限的广场砖上，角色面朝东南（yaw=160）；相机放到角色前侧 45°、"
                         "稍高俯拍，画面里角色在中间，身后是文化宫『ДОМ КУЛЬТУРЫ』招牌、马赛克饰带、"
                         "柱廊暖窗和方尖碑，远景还能看到带红星的斯大林式塔楼。地面是广场花岗岩，平整无高差。"),
        "view_face": "文化宫正立面（招牌/彩马赛克饰带/柱廊/暖黄窗）+ 方尖碑（高 18m）+ 广场铺装 + 右侧远景斯大林式塔楼",
        "view_ots": "广场南缘、旗杆、列宁大街与电车站、广告牌，两侧 9 层塔楼 T_W / T_E 夹出的街谷与雾",
    },
    {
        "id": "SP_C2_MONUMENT",
        "label_zh": "纪念轴起点·池塘南岸",
        "blender_xy": (0.0, 90.0),
        "yaw_deg": 180.0,
        "face": dict(yaw_offset=180.0, dist=4.2, pitch=5.0, look_h=1.10),
        "ots": dict(yaw_offset=0.0, dist=4.5, pitch=10.0, look_h=1.30, alt_char_yaw=0.0),
        "preview": "previews/C2_FACE.png",
        "rationale_zh": ("北公园池塘南岸、纪念轴起点，角色面对镜头（yaw=180，即面朝南），身后依次是公园池塘、"
                         "纪念广场、六级台地与祖国母亲雕像基座，左侧是打光的布列斯特勇气浮雕墙、右侧是纪念旗阵，"
                         "构图有纵深（水—广场—台地—雕像四层）。注意：若要用越肩机位，请把角色同时转到 yaw=0"
                         "（面向纪念轴），相机机位 (0, ~85.5) 不变，否则相机会落到池塘水面上。"),
        "view_face": "公园池塘水面倒影 + 纪念广场 + 六级台地 + 祖国母亲雕像基座（雕像本体高 40m，画面顶部被裁切）+ 左侧亮灯的浮雕墙 + 右侧旗阵",
        "view_ots": "南向：北环路、中区的第三中学/幼儿园/锅炉房烟囱一带、广场与街谷（越肩变体见 ots_bg_override）",
        "ots_bg_override": "越肩变体（角色转向 yaw=0）：纪念轴、长明火、六级台地、祖国母亲雕像——与 C2_FACE 同一机位反向看",
    },
    {
        "id": "SP_C3_TRAM",
        "label_zh": "电车站台",
        "blender_xy": (11.33, -81.5),
        "yaw_deg": 90.0,
        "face": dict(yaw_offset=180.0, dist=3.4, pitch=6.0, look_h=1.10),
        "ots": dict(yaw_offset=0.0, dist=4.2, pitch=12.0, look_h=1.25),
        "preview": "previews/C3_FACE.png / previews/C3_OTS.png",
        "rationale_zh": ("站台是 0.25m 高的木铺装候车台（可跨上），角色面朝东（yaw=90）；相机在西侧回拍，"
                         "画面左边是一节亮着窗的红色电车，脚下是木站台与湿反光的轨道，右侧是列宁大街、"
                         "路灯与广告牌，远处是雾里的街区 —— 最有『苏联蓝调』味道的一张。"),
        "view_face": "电车车身与亮窗 + 湿地轨道 + 木站台 + 大街路灯 + 广告牌 + 远处街区（C3_FACE 背景）",
        "view_ots": "站台与轨道向东延伸、东侧街区与路灯、雾中的街道纵深（C3_OTS 背景）",
    },
]


def build_spawns():
    out = []
    for cfg in SPAWNS_CFG:
        bx, by = cfg["blender_xy"]
        node, ndist = MB.nearest_node(bx, by)
        # 吸附到最近 nav 节点（保证 A* 可用）
        px, py, pz = node["x"], node["y"], node["z"]
        ground = MB.ground_height(px, py)
        if node["z"] > ground:
            ground = node["z"]
            # 台地节点直接取节点高度；普通网格节点取地形高度
        if MB.ground_height(px, py) > 0:
            ground = MB.ground_height(px, py)
        uni = S.b2u(px, py, ground)
        char_u = [uni[0], ground, uni[2]]
        yaw = cfg["yaw_deg"]
        rpt = MB.walkable_report(px, py)
        wr = {"nearest_nav_node": node["id"], "snap_dist_m": round(ndist, 2),
              "nav_node_kind": node["kind"], "ground_y": round(ground, 2),
              "walkable": rpt["walkable"], "inside_collision": rpt["inside_collision"],
              "inside_water": rpt["inside_water"], "nav_density_r4": rpt["nav_density_r4"]}
        face = camera_rig(char_u, yaw, ground, **cfg["face"])
        face["clearance"] = cam_clearance(face["unity_position"], face["look_at"])
        face["line_of_sight_hits"] = vision_from_unity(face["unity_position"], face["look_at"])
        ots_cfg = dict(cfg["ots"])
        alt_yaw = ots_cfg.pop("alt_char_yaw", None)
        ots_yaw = yaw if alt_yaw is None else alt_yaw
        ots = camera_rig(char_u, ots_yaw, ground, **ots_cfg)
        ots["clearance"] = cam_clearance(ots["unity_position"], ots["look_at"])
        ots["line_of_sight_hits"] = vision_from_unity(ots["unity_position"], ots["look_at"])
        if alt_yaw is not None:
            ots["note_zh"] = ("越肩机位需要把角色同时转到 yaw=%s（面向纪念轴），相机机位不变；"
                              "否则相机会落在池塘水面上。" % alt_yaw)
        # 附近地标
        near = []
        for pid, d in MB.landmark_distances(px, py, top=6):
            near.append({"id": pid.lower(), "label_zh": POI_CN.get(pid, pid),
                         "distance_m": round(d, 2),
                         "unity_position": S.b2u(MB.poi(pid)["x"], MB.poi(pid)["y"], MB.poi(pid)["z"])})
        fwd = MB.first_hit(px, py, yaw, max_dist=120)
        bwd = MB.first_hit(px, py, yaw + 180, max_dist=120)
        out.append({
            "id": cfg["id"],
            "label_zh": cfg["label_zh"],
            "unity_position": char_u,
            "unity_position_with_ground_y": [char_u[0], round(ground, 2), char_u[2]],
            "yaw_deg": yaw,
            "facing_zh": compass_zh(yaw),
            "forward_unity": [round(v, 3) for v in S.forward_from_yaw(yaw)],
            "snapped_from_blender": [bx, by],
            "rationale_zh": cfg["rationale_zh"],
            # view_forward = 角色面朝的方向看到什么（= 越肩机位的背景）
            "view_forward": cfg["view_ots"],
            # view_behind = 角色身后是什么（= 面对面机位的背景）
            "view_behind": cfg["view_face"],
            "forward_first_hit": ({"id": fwd[1], "kind": fwd[2], "dist_m": fwd[0]} if fwd else None),
            "behind_first_hit": ({"id": bwd[1], "kind": bwd[2], "dist_m": bwd[0]} if bwd else None),
            "nearby_landmarks": near,
            "walkable_check": wr,
            "cameras": {"face_to_face": face, "over_shoulder": ots},
            "ots_bg_override": cfg.get("ots_bg_override"),
            "preview_images": cfg["preview"],
        })
    return out


# ==========================================================================
# 2. 锚点
# ==========================================================================

POI_CN = {
    "OBELISK": "方尖碑", "FOUNTAIN": "干涸喷泉", "FLAGS": "旗杆", "BUS_STOP": "公交站",
    "TRAM_STOP": "电车站", "BUS_STOP_2": "公交站（南）", "PAVILION": "公园凉亭",
    "PARK_SIGN": "公园入口", "POND": "公园池塘", "DK": "文化宫", "SCHOOL": "第三中学",
    "KG": "第九幼儿园", "SHOP": "食品商店", "BOILER": "锅炉房", "KIOSK": "售货亭",
    "GYM": "体育馆", "CLINIC": "门诊部", "MARKET": "市场", "KINO": "电影院",
    "DOM_BYTA": "生活服务楼", "BAKERY": "面包房", "BANYA": "公共浴场",
    "MOTHERLAND": "祖国母亲雕像", "BREST_RELIEF": "勇气浮雕墙", "METRO": "地铁站",
    "MSU_MAIN": "国立大学主楼", "UKRAINA": "乌克兰饭店", "MFA": "外交部大楼",
    "HIT": "中苏学堂", "MUSEUM": "军事博物馆", "ROBOTICS": "机器人研究院",
    "UFO_STATION": "飞碟车站", "HEX_STATION": "六边形车站", "STADIUM": "体育场",
    "ETERNAL_FLAME": "长明火", "GUARD_BOX": "岗亭", "FLAG_ROW_MONUMENT": "纪念旗阵",
    "METRO_SIGN": "地铁标志", "STATUE_PLAQUE": "碑文", "ESPLANADE": "纪念广场",
    "ESPLANADE_TOP": "纪念台地", "POND_S": "公园池塘南岸",
}

# (id, label_zh, 种子 blender x, y, 来源类型, 来源键, approach 偏移 dx,dy)
ANCHORS_CFG = [
    ("central_plaza", "中央广场", -10.0, -46.0, "curated", None, 4.0, -2.0),
    ("obelisk", "方尖碑", None, None, "nav_poi", "OBELISK", -8.0, 2.0),
    ("fountain", "干涸喷泉", None, None, "nav_poi", "FOUNTAIN", 0.0, -4.0),
    ("house_of_culture", "文化宫", None, None, "nav_poi", "DK", -8.0, -6.0),
    ("flags_plaza", "旗杆广场", None, None, "nav_poi", "FLAGS", -8.0, 4.0),
    ("avenue_lenin", "列宁大街", None, None, "route_anchor", "A_AVE_C", 0.0, -5.0),
    ("bus_stop", "公交站", None, None, "nav_poi", "BUS_STOP", -6.0, 0.0),
    ("tram_stop", "电车站", None, None, "nav_poi", "TRAM_STOP", -8.0, 0.0),
    ("shop_gastronom", "食品商店", None, None, "nav_poi", "SHOP", 0.0, -4.0),
    ("kiosk", "售货亭", None, None, "nav_poi", "KIOSK", -4.0, 0.0),
    ("boiler_house", "锅炉房", None, None, "nav_poi", "BOILER", 0.0, -6.0),
    ("banya", "公共浴场", None, None, "nav_poi", "BANYA", -6.0, 0.0),
    ("school_3", "第三中学", None, None, "nav_poi", "SCHOOL", 0.0, -6.0),
    ("kindergarten_9", "第九幼儿园", None, None, "nav_poi", "KG", 0.0, -8.0),
    ("gym", "体育馆", None, None, "nav_poi", "GYM", -6.0, 0.0),
    ("stadium", "小区体育场", None, None, "nav_poi", "STADIUM", 0.0, -8.0),
    ("market", "市场", None, None, "nav_poi", "MARKET", -6.0, 0.0),
    ("cinema", "电影院", 24.0, -90.0, "curated", None, 0.0, -3.0),
    ("bakery", "面包房", None, None, "nav_poi", "BAKERY", 0.0, 4.0),
    ("metro", "地铁站", None, None, "nav_poi", "METRO", -6.0, 0.0),
    ("msu_main", "国立大学主楼", None, None, "nav_poi", "MSU_MAIN", -6.0, 0.0),
    ("vysotka_plaza", "高楼广场", 180.0, -4.0, "curated", None, -6.0, 0.0),
    ("mfa", "外交部大楼", None, None, "nav_poi", "MFA", -6.0, 0.0),
    ("motherland", "祖国母亲雕像", -4.0, 128.5, "curated", None, 0.0, -6.0),
    ("eternal_flame", "长明火", None, None, "nav_poi", "ETERNAL_FLAME", 0.0, -4.0),
    ("monument_esplanade", "纪念广场", -48.0, 120.0, "curated", None, 10.0, 0.0),
    ("brest_relief", "勇气浮雕墙", -32.0, 118.0, "curated", None, 0.0, -4.0),
    ("north_park", "北公园", -70.0, 100.0, "curated", None, 10.0, -4.0),
    ("pond", "公园池塘南岸", None, None, "nav_poi", "POND", 0.0, -4.0),
    ("ufo_station", "飞碟车站", None, None, "nav_poi", "UFO_STATION", 0.0, -6.0),
]


def build_anchors():
    out = []
    warn = []
    for aid, label, sx, sy, src, key, adx, ady in ANCHORS_CFG:
        if src == "nav_poi":
            p = MB.poi(key)
            bx, by, bz = p["x"], p["y"], p["z"]
            raw = {"blender": [round(bx, 2), round(by, 2), round(bz, 2)], "node": p["node"]}
        elif src == "route_anchor":
            n = MB.route_node(key)
            bx, by, bz = n["x"], n["y"], n["z"]
            raw = {"blender": [round(bx, 2), round(by, 2), round(bz, 2)], "node": n["id"]}
        else:
            bx, by, bz = sx, sy, 0.0
            raw = {"blender": [round(bx, 2), round(by, 2), 0.0], "node": None}
        # position: 吸附到最近 nav 节点（但要先保证不在碰撞体里）
        pos_node, pd = MB.snap(bx, by, max_dist=6.0)
        pos_rpt = MB.walkable_report(bx, by)
        if pos_rpt["inside_collision"] or pos_rpt["inside_water"] or pos_node is None:
            # 退回到最近的安全节点
            best = None
            for radius in (6.0, 12.0, 20.0):
                for n in MB.nodes:
                    d = math.hypot(n["x"] - bx, n["y"] - by)
                    if d > radius:
                        continue
                    r = MB.walkable_report(n["x"], n["y"])
                    if r["walkable"] and (best is None or d < best[1]):
                        best = (n, d)
                if best:
                    break
            if best:
                pos_node, pd = best
                pos_rpt = MB.walkable_report(pos_node["x"], pos_node["y"])
                warn.append({"anchor": aid, "issue": "position 种子在障碍/水面内，已回退 %.2fm 到 nav 节点 #%d"
                                                    % (pd, pos_node["id"])})
            else:
                warn.append({"anchor": aid, "issue": "20m 内找不到可行走节点，改用种子点"})
                pos_node = MB.nearest_node(bx, by)[0]
        px, py = pos_node["x"], pos_node["y"]
        pg = MB.ground_height(px, py)
        if pos_node["z"] > pg:
            pg = pos_node["z"]
        # approach: 沿指定方向偏移后吸附
        ax, ay = px + adx, py + ady
        ap_node, apd = MB.snap(ax, ay, max_dist=8.0)
        ap_rpt = MB.walkable_report(ax, ay) if ap_node is None else MB.walkable_report(ap_node["x"], ap_node["y"])
        if ap_node is not None:
            apx, apy = ap_node["x"], ap_node["y"]
        else:
            apx, apy = px, py
            warn.append({"anchor": aid, "issue": "approach 8m 内无 nav 节点，回退为 position"})
        apg = MB.ground_height(apx, apy)
        if ap_node is not None and ap_node["z"] > apg:
            apg = ap_node["z"]
        pos_u = [round(px, 2), round(pg, 2), round(py, 2)]
        ap_u = [round(apx, 2), round(apg, 2), round(apy, 2)]
        rr = MB.walkable_report(px, py)
        ar = MB.walkable_report(apx, apy)
        out.append({
            "id": aid,
            "label_zh": label,
            "position": pos_u,
            "approach_position": ap_u,
            "source": src if src != "route_anchor" else "route_anchor",
            "source_key": key,
            "notes": ("nav node #%s，吸附 %.2fm" % (pos_node["id"], pd) if src != "curated"
                      else "curated，吸附到 nav node #%s（%.2fm）" % (pos_node["id"], pd)),
            "walkable_check": {
                "position": {k: rr[k] for k in ("walkable", "nearest_nav_dist_m", "inside_collision", "inside_water", "ground_z")},
                "approach": {k: ar[k] for k in ("walkable", "nearest_nav_dist_m", "inside_collision", "inside_water", "ground_z")},
            },
        })
    return out, warn


# ==========================================================================
# 3. 碰撞盒 / 可走范围 / 地标名
# ==========================================================================

def build_collision():
    out = []
    for b in MB.boxes:
        mn, mx = b["min"], b["max"]           # blender (x, y, z)  min/max
        umin = [round(mn[0], 2), round(mn[2], 2), round(mn[1], 2)]   # (x, z, y)
        umax = [round(mx[0], 2), round(mx[2], 2), round(mx[1], 2)]
        out.append({"id": b["id"], "kind": b["kind"],
                    "min": umin, "max": umax,
                    "blender_min": mn, "blender_max": mx,
                    "source": b.get("source", "")})
    return out


def bounds_option(x0, z0, x1, z1, name, note):
    nodes_in = [n for n in MB.nodes
                if x0 <= n["x"] <= x1 and z0 <= n["y"] <= z1]
    return {
        "id": name,
        "unity_min": [round(x0, 2), round(z0, 2)],
        "unity_max": [round(x1, 2), round(z1, 2)],
        "blender_rect": [round(x0, 2), round(z0, 2), round(x1, 2), round(z1, 2)],
        "size_m": [round(x1 - x0, 2), round(z1 - z0, 2)],
        "nav_nodes_inside": len(nodes_in),
        "note_zh": note,
    }


def build_bounds():
    main_box = bounds_option(-114.0, -92.0, 90.0, 68.0, "core_demo_200x160",
                             "主城区演示框：中央广场 + 文化宫 + 列宁大街 + 东西院 + 学校/幼儿园/体育馆，"
                             "200×160m；覆盖推荐出生点和 C1 景点，A* 在小图上最快。")
    full = bounds_option(-196.0, -145.5, 200.0, 152.4, "nav_full",
                         "整个导航范围（=nav_graph.bounds），包含东区地铁/高楼群、纪念广场、北公园、"
                         "南侧车站；节点数 ×3，路径明显变长，建议只在需要走远场景时临时扩框。")
    # 推荐：core + 两条通往地标的走廊
    west_east = bounds_option(-160.0, -92.0, 165.0, 100.0, "core_plus_landmarks",
                              "折中方案：在 core 基础上向东扩到地铁/高楼群、向北扩到纪念轴，"
                              "仍然排除南侧长途车站区和西二区，约 325×192m。")
    return {
        "recommended": "core_demo_200x160",
        "options": [main_box, west_east, full],
        "world_bounds_unity": {"min": [-220.0, -170.0], "max": [220.0, 170.0],
                               "note": "真正的地面碰撞盒 GROUND_PLANE 在 Unity 里是 x[-220,220] z[-170,170]（y -1..0）"},
    }


def build_landmark_names():
    out = []
    for m in MB.manifest:
        cx, cy = m["center_blender"]
        out.append({
            "id": m["id"], "label_zh": m["cn"], "unity_center": S.b2u(cx, cy, 0.0),
            "height_m": m["height_m"], "floors": m["floors"], "type": m["type"],
            "manifest_center_unity_raw": m["center_unity"],
            "note": "manifest 的 center_unity 是旧约定 (x,z,-y)，Unity 用 unity_center 即可",
        })
    return out


# ==========================================================================
# 4. lead 的 anchors.json 标签/坐标评审
# ==========================================================================

ANCHOR_REVIEW = [
    {"id": "metro_front", "current": "地铁站前", "current_pos": [32.15, 0.0, -64.0],
     "suggestion_label": "地铁站前（先修坐标）", "suggested_pos": [135.0, 0.0, 48.0],
     "reason_zh": "坐标错位：(32.15,-64) 在中央广场东南，离真正的地铁站亭 (138..158, 46..54) 约 100m。"
                  "A_METRO 的 route 节点在过滤后索引错位，需要按原 nav_graph 节点 id=3482 重新解析。",
     "severity": "high"},
    {"id": "kino", "current": "电影院", "current_pos": [36.0, 0.0, -90.0],
     "suggestion_label": "电影院（КИНО）", "suggested_pos": [30.0, 0.0, -89.9],
     "reason_zh": "与 bus_stop_2 完全同坐标 (36,-90)。电影院盒子在 x20..60, z-108..-92，"
                  "门口入口节点是 (30,-89.9)，把 kino 放到 30 就分开了。",
     "severity": "high"},
    {"id": "pond_north", "current": "池塘北岸", "current_pos": [5.0, 0.0, 91.5],
     "suggestion_label": "池塘南岸", "suggested_pos": [5.0, 0.0, 91.5],
     "reason_zh": "池塘水面在 z 94..116，z=91.5 在南岸；北岸应该是 z≈115。而且与 pond 锚点同一坐标，建议删除其一。",
     "severity": "high"},
    {"id": "pond", "current": "公园池塘", "current_pos": [5.0, 0.0, 91.5],
     "suggestion_label": "公园池塘（南岸观景位）", "suggested_pos": [5.0, 0.0, 91.5],
     "reason_zh": "与 pond_north 重复；这个点其实是池塘南岸的观景点，不是池塘本身。",
     "severity": "medium"},
    {"id": "motherland", "current": "祖国母亲塑像", "current_pos": [-4.0, 0.0, 128.5],
     "suggestion_label": "祖国母亲雕像（纪念广场机位）", "suggested_pos": [-4.0, 0.0, 128.5],
     "reason_zh": "位置在纪念广场、雕像基座南侧 10m，并不在雕像脚下；叫『雕像』会让 LLM 以为能走到雕像。"
                  "雕像本体是不可进入的（底座 x±12, z138..150）。",
     "severity": "medium"},
    {"id": "fountain", "current": "干喷泉广场", "suggestion_label": "干涸喷泉",
     "reason_zh": "『干喷泉广场』像地名；数据里是 fountain_dry / 干涸喷泉，改名后和『中央广场』不会混。",
     "severity": "medium"},
    {"id": "kiosk", "current": "报刊亭", "suggestion_label": "售货亭",
     "reason_zh": "plan.json 里 KIOSK 的 cn 是『售货亭』，不是报刊亭。",
     "severity": "medium"},
    {"id": "plaza_center", "current": "广场中央", "current_pos": [0.0, 0.0, -55.0],
     "suggestion_label": "广场中央（方尖碑旁）", "suggested_pos": [-10.0, 0.0, -55.0],
     "reason_zh": "(0,-55) 距 obelisk (0,-56.5) 只有 1.5m，两个锚点几乎重合；"
                  "方尖碑本身占 (x±1.6, z-53.6..-50.4)，建议把广场中央挪到 (-10,-55) 让语义分开。",
     "severity": "medium"},
    {"id": "boulevard_center", "current": "林荫大道中段", "current_pos": [0.0, 0.0, -72.0],
     "suggestion_label": "广场主道路口 / 列宁大街广场路口",
     "reason_zh": "P_BOULEVARD 是南北向的『广场主道』（z-74..-34），AVENUE 才是东西向的『列宁大街』；"
                  "(0,-72) 是两者交叉口。叫『林荫大道』容易和 plan 里的 AVENUE 混淆。",
     "severity": "medium"},
    {"id": "boulevard_west", "current": "林荫大道西段", "suggestion_label": "列宁大街西段"},
    {"id": "boulevard_east", "current": "林荫大道东段", "suggestion_label": "列宁大街东段"},
    {"id": "start_point", "current": "起始位置", "suggested_pos": None,
     "suggestion_label": "起始位置（保留）", "reason_zh": "和 sidewalk_center、camera_front 都在出生点 3m 内；"
                                                          "建议只保留 start_point，另两个标成 alias 或在 LLM 提示里忽略。"},
    {"id": "sidewalk_center", "current": "人行道中央", "current_pos": [0.0, 0.0, -60.0],
     "suggestion_label": "（建议删除，用 start_point）"},
    {"id": "street_center", "current": "街道中央", "current_pos": [0.0, 0.0, -72.0],
     "suggestion_label": "（与 boulevard_center 同坐标，建议删除）"},
    {"id": "camera_front", "current": "观看者面前", "current_pos": [0.0, 0.0, -57.6],
     "suggestion_label": "（建议删除，这是相机位不是地点）"},
    {"id": "store_door", "current": "面包房门口", "current_pos": [-1.0, 0.0, -89.6],
     "suggestion_label": "面包房门口（Булочная）", "reason_zh": "可以保留，和 bakery 锚点同坐标，"
                                                                 "建议只在 LLM 提示里当 bakery 的别名。"},
    {"id": "pavilion", "current": "公园凉亭", "suggestion_label": "公园凉亭"},
    {"id": "pavilion_front", "current": "凉亭前", "current_pos": [54.0, 0.0, 104.0],
     "suggestion_label": "公园凉亭（北侧）", "reason_zh": "凉亭本体在 (54,100)，104 是它的北侧空地；两个都在，"
                                                            "标签最好能区分。"},
    {"id": "dk_west", "current": "文化宫西侧"}, {"id": "dk_east", "current": "文化宫东侧"},
    {"id": "plaza_south", "current": "广场南端"}, {"id": "plaza_north", "current": "广场北端"},
    {"id": "south_street_center", "current": "南街中段"},
]


def build_anchor_review():
    """对照 lead 的 anchors.json 做差异检查（不修改 lead 文件）。"""
    path = os.path.join(HERE, "anchors.json")
    lead = json.load(open(path, encoding="utf-8")) if os.path.exists(path) else {"anchors": []}
    lead_map = {a["id"]: a for a in lead.get("anchors", [])}
    out = []
    for r in ANCHOR_REVIEW:
        a = lead_map.get(r["id"])
        rec = {
            "id": r["id"],
            "current_label": a["label"] if a else r.get("current"),
            "current_pos": a["pos"] if a else r.get("current_pos"),
            "suggested_label": r.get("suggestion_label", a["label"] if a else None),
            "suggested_pos": r.get("suggested_pos"),
            "reason_zh": r.get("reason_zh", ""),
            "severity": r.get("severity", "low"),
        }
        out.append(rec)
    return out


# ==========================================================================
# 5. 验证 + 输出
# ==========================================================================

def build_validation(spawns, anchors):
    per_spawn = []
    for sp in spawns:
        for kind, cam in sp["cameras"].items():
            per_spawn.append({
                "spawn": sp["id"], "camera": kind,
                "camera_position": cam["unity_position"],
                "camera_inside_collision": cam["clearance"]["inside_collision"],
                "line_of_sight_blocked": cam["clearance"]["line_of_sight_blocked"],
                "view_bearing_deg": cam["view_bearing_deg"],
                "first_hit_along_axis": cam["line_of_sight_hits"],
            })
    ok = sum(1 for a in anchors if a["walkable_check"]["position"]["walkable"]
             and a["walkable_check"]["approach"]["walkable"])
    return {
        "spawn_and_camera_checks": per_spawn,
        "anchors_walkable": {"total": len(anchors), "both_ok": ok},
        "nav_collision_conflicts": [
            {"id": "LM_OBELISK", "issue_zh": "nav 的 P_BOULEVARD 节点 (0,-52) 正好落在方尖碑碰撞盒内；"
                                             "A* 会直线穿过方尖碑。Unity 端要么给这条边加代价，要么在 (0,-52) 附近把路径左右推开 3.5m。"},
            {"id": "P_MONUMENT_AXIS", "issue_zh": "纪念轴 nav 只有 (0,90) 与 (0,118) 两个节点，中间 28m 一条边直接跨过公园池塘；"
                                                  "碰撞盒 WATER_PARK_POND 会挡住角色。需禁用该边或改成绕池塘的路径。"},
            {"id": "BREST_RELIEF/FLAG_ROW_MONUMENT", "issue_zh": "两个 nav POI 节点落在倒影池水面碰撞盒内（(-32,121.6) 与 (26,124.8)），"
                                                               "直接当目的地会走不过去；已在锚点里改用岸上回退点。"},
            {"id": "LM_FOUNTAIN", "issue_zh": "干喷泉碰撞盒高 0.5m（> step 0.35），nav 却有节点穿过喷泉；"
                                              "Unity 里要么允许跳过 0.5m 矮沿，要么绕行。"},
            {"id": "WATER_PARK_POND", "issue_zh": "池塘水面碰撞盒是矩形 AABB，但水面是椭圆；"
                                                  "四个角约 3~5m 的干地被误判为水，绕池塘时贴着 AABB 外侧走即可。"},
            {"id": "TRAM_PLATFORM", "issue_zh": "电车站台顶面 z=0.25（steppable），但 nav 节点 z=0；"
                                                "角色站在站台上时根节点 y 要用 0.25，否则会陷进站台 0.25m。"},
        ],
        "ground_height_notes": [
            "全图地面 y=0（GROUND_PLANE 顶面），角色根节点 y=0 即可。",
            "台地：纪念广场六级台阶 (z 132..138, 每级 0.45m) → 平台 z 138..150 高 2.7m。角色要上台地时根节点 y=2.7。",
            "电车站台：x 4..26, z -83.2..-79.8, 顶面 y=0.25。",
        ],
    }


def main():
    global MB
    MB = S.MapData()
    print("loaded: nodes=%d edges=%d boxes=%d buildings=%d" %
          (len(MB.nodes), len(MB.edges), len(MB.boxes), len(MB.manifest)))

    coords = coord_verification()
    spawns = build_spawns()
    anchors, awarn = build_anchors()
    bounds = build_bounds()
    land = build_landmark_names()
    review = build_anchor_review()
    validation = build_validation(spawns, anchors)

    spec = {
        "schema": "sbh.unity.integration.v1",
        "generated_by": "integration/build_integration_spec.py",
        "source_data": {
            "nav_graph": "map/nav_graph.json (3486 nodes / 8551 edges / 43 poi / 52 route_anchors)",
            "collision": "map/collision.json (91 AABB, player r=0.35 step=0.35 walk-under=1.75)",
            "scene_manifest": "map/scene_manifest.json (48 buildings with cn names)",
            "plan": "tools/plan.json (中文地名)",
            "glb": "map/SovietBlueHour_game.glb (117MB, 51 mesh, 571 materials)",
            "lead_runtime_files": ["integration/collision_unity.json", "integration/nav_compact.json",
                                   "integration/anchors.json", "integration/walkability_report.json"],
        },
        "coordinate_mapping": {
            "formula_zh": "Unity 实际坐标 = (blender.x, blender.z, blender.y)：blender 的高度 z 进 Unity 的 y，"
                          "blender 的北向 y 进 Unity 的 z（符号不变）。",
            "blender_to_gltf": "glTF(yup) = (bx, bz, -by)  —— Blender glTF 导出器固定转换",
            "unigltf_import": "UniGLTF ScriptedImporter 默认 ReverseZ：Unity = (gx, gy, -gz)",
            "blender_to_unity_actual": "Unity = (gx, gy, -gz) = (bx, bz, by)",
            "nav_json_unity_field": "nav_graph/collision/scene_manifest 的 'unity' 字段用的是 (x, z, -y)，"
                                    "z 轴与 Unity 实际相反，绝对不能直接当 Unity 坐标用。",
            "collision_aabb_rule": "AABB 直接换算：unity_min = (bx_min, bz_min, by_min)，"
                                   "unity_max = (bx_max, bz_max, by_max)，各轴 min/max 顺序不需要交换。",
            "unity_yaw_convention": {
                "yaw_deg": "0 = 面朝 +Z（= Blender 北）；90 = 面朝 +X（= Blender 东）；顺时针为正",
                "forward": "forward = (sin(yaw), 0, cos(yaw))",
                "from_blender_dir": "yaw = atan2(dx_blender, dy_blender)",
                "camera_pitch": "正 = 抬头；unity camera.eulerAngles.x = -pitch",
            },
            "verification_3_landmarks": coords,
            "verification_note_zh": "三条独立证据互相印证：①GLB 里 BLD_* 网格的 glTF bbox 反推回 blender 与 "
                                    "scene_manifest.rect/center_blender 吻合；②nav POI 的 blender 坐标；"
                                    "③scene_manifest.center_unity 字段恰好是 (x,0,-y)，与 Unity 实际 (x,0,y) 差一个符号。",
        },
        "spawn_candidates": spawns,
        "recommended_spawn": {
            "id": spawns[0]["id"],
            "label_zh": spawns[0]["label_zh"],
            "unity_position": spawns[0]["unity_position_with_ground_y"],
            "yaw_deg": spawns[0]["yaw_deg"],
            "default_camera": "face_to_face",
            "composition_zh": "面对面 3/4（相机在角色前侧 45°、俯角 6°）",
            "reason_zh": ("① 背景最有信息量：文化宫招牌 + 马赛克饰带 + 柱廊暖窗 + 方尖碑 + 远景红星塔楼，"
                          "四层景深；② 地面是花岗岩广场砖，平整无高差、无遮挡物；③ 相机 3.8m 处四周都是"
                          "开阔广场，右键环绕 360° 都不会插进建筑；④ 离 lead 的默认出生点 (0,0,-60) 只有 14m，"
                          "可以直接替换。"),
            "vs_lead_spawn_zh": ("lead 现有出生点 (0,0,-60) 朝 yaw=0：正前方 8m 就是方尖碑，会把文化宫入口"
                                 "完全挡住（web 端 live_r3_spawn.png 就是这个构图）。本方案把角色往西南挪 14m、"
                                 "转向 160°，让方尖碑落到画面右侧、文化宫当背景。"),
        },
        "camera_candidates": build_camera_candidates(spawns),
        "anchors": anchors,
        "anchor_warnings": awarn,
        "lead_anchors_review": {
            "note_zh": "以下是对 lead 生成的 integration/anchors.json（66 个）的评审建议，只给建议、未修改该文件。",
            "suggestions": review,
        },
        "collision_boxes": build_collision(),
        "walkable_bounds": bounds,
        "landmark_names": land,
        "validation": validation,
        "image_evidence": IMAGE_EVIDENCE,
        "renders_made": [
            {"path": "previews/C1_FACE.png", "desc": "C1 面对面构图（文化宫 + 方尖碑背景）"},
            {"path": "previews/C1_OTS.png", "desc": "C1 越肩构图（列宁大街方向）"},
            {"path": "previews/C2_FACE.png", "desc": "C2 面对面构图（纪念轴 + 雕像基座 + 浮雕墙）"},
            {"path": "previews/C3_FACE.png", "desc": "C3 面对面构图（电车 + 木站台 + 大街）"},
            {"path": "previews/C3_OTS.png", "desc": "C3 越肩构图（轨道向西延伸）"},
            {"path": "previews/EV_A.png", "desc": "C1 眼平背景参考（与 CAM_PLAZA 同机位方向）"},
        ],
    }
    with open(OUT, "w", encoding="utf-8") as f:
        json.dump(spec, f, ensure_ascii=False, indent=1)
    print("wrote", OUT, os.path.getsize(OUT), "bytes")
    print("spawns:", [(s["id"], s["unity_position_with_ground_y"], s["yaw_deg"],
                       s["walkable_check"]["walkable"]) for s in spawns])
    print("anchors ok:", validation["anchors_walkable"])
    for s in spawns:
        print(s["id"], "face cam", s["cameras"]["face_to_face"]["unity_position"],
              "ots cam", s["cameras"]["over_shoulder"]["unity_position"])
        print("   face clearance", s["cameras"]["face_to_face"]["clearance"])
        print("   ots  clearance", s["cameras"]["over_shoulder"]["clearance"])


def compass_zh(yaw_deg):
    """Unity yaw → 中文八方位（0=+Z 北，顺时针）。"""
    names = ["北", "东北", "东", "东南", "南", "西南", "西", "西北"]
    return names[int(((yaw_deg % 360.0) + 22.5) // 45.0) % 8]


def build_camera_candidates(spawns):
    out = []
    n = 1
    for sp in spawns:
        for kind, cam in sp["cameras"].items():
            out.append({
                "id": "CAM_%02d_%s_%s" % (n, sp["id"].replace("SP_", ""), kind.upper()),
                "label_zh": ("面对面 3/4（展示数字人）" if kind == "face_to_face" else "越肩（游戏跟随）"),
                "spawn": sp["id"],
                "unity_position": cam["unity_position"],
                "look_at": cam["look_at"],
                "yaw_deg": cam["camera_yaw_deg"],
                "pitch_deg": cam["pitch_deg"],
                "fov_vertical_deg": cam["fov_vertical_deg"],
                "rig_params": {k: cam[k] for k in ("yawOffset", "distance", "pitch", "lookHeight")},
                # 面对面机位看到的是角色身后；越肩机位看到的是角色面朝方向
                "background_zh": ((sp.get("ots_bg_override") if kind == "over_shoulder" else None)
                                  or (sp["view_behind"] if kind == "face_to_face" else sp["view_forward"])),
                "occlusion_zh": ("相机在开阔区域内，到角色视线无遮挡" if not cam["clearance"]["line_of_sight_blocked"]
                                 else "视线被 %s 遮挡" % cam["clearance"]["line_of_sight_blocked"]),
                "clip_risk": cam["clearance"]["inside_collision"],
                "preview": sp["preview_images"],
            })
            n += 1
    return out


IMAGE_EVIDENCE = [
    {"path": "renders/final/CAM_HERO.png", "seen_zh": "全场 3/4 鸟瞰（相机 205,-195,85 → Unity (205,85,-195)）："
                                                     "蓝调天空 + 雾、成片 5 层赫鲁晓夫楼与 9 层塔楼、广场暖灯、"
                                                     "右侧祖国的白色雕像轮廓。判断：整体夜景观感成立，但地面细节在远处丢失。"},
    {"path": "renders/final/CAM_PLAZA.png", "seen_zh": "中央广场眼平 2.1m：文化宫『ДОМ КУЛЬТУРЫ』招牌、"
                                                       "马赛克饰带、柱廊、海报灯箱、左侧台阶、右侧方尖碑基座。"
                                                       "判断：这是全图最好的『数字人背景』——有招牌文字、有暖光、有地标。"},
    {"path": "renders/final/CAM_DK.png", "seen_zh": "文化宫近景仰拍：马赛克饰带细节 + 柱廊 + 海报 + 门厅暖光。"
                                                   "判断：适合走近的 B 机位，不适合当默认背景（太近）。"},
    {"path": "renders/final/CAM_AVE.png", "seen_zh": "列宁大街公交站眼平 1.75m：候车亭、广告牌、接触网杆、"
                                                     "蓝色巴士；画面被灯杆分割、前景偏满。判断：不做出生点，可做街景。"},
    {"path": "renders/final/CAM_ESPLANADE.png", "seen_zh": "纪念广场眼平 2.8m 仰拍：祖国母亲雕像全高、六面旗、"
                                                          "长明火、左侧倒影池、脚下台阶。判断：史诗感最强，但地面仰角大，"
                                                          "适合镜头运动而不是数字人站桩。"},
    {"path": "renders/final/CAM_LM_MOTHERLAND.png", "seen_zh": "雕像 30mm 近景：裙摆褶皱、举剑手臂、斗篷、花岗岩基座、"
                                                               "右下旗阵。判断：雕像本体做得很足，但基座 3m 高，角色站旁边会被压住。"},
    {"path": "renders/final/CAM_LM_MSU.png", "seen_zh": "东区高楼群：莫大主楼（尖顶红星/钟塔/柱廊）+ 乌克兰饭店赭石立面对比。"
                                                       "判断：东侧是最『斯大林式』的背景，但离出生点 180m。"},
    {"path": "renders/final/CAM_VYSOTKA.png", "seen_zh": "34m 高机位拍东区天际线：五座塔楼层层叠叠 + 屋顶红星 + 窗外暖光。"
                                                        "判断：预告片镜头；地面角色只能在广场里借它当远景。"},
    {"path": "renders/final/CAM_ARCH.png", "seen_zh": "过关楼拱门 1.85m：4m 宽拱洞、洞内三盏暖灯、砖墙面、平房窗。"
                                                     "判断：氛围极好但空间窄，相机 4m 会卡进拱洞，只能当特写机位。"},
    {"path": "renders/final/CAM_SOUTH_ST.png", "seen_zh": "电影院（КИНО/ЗИМА 霓虹）+ 雨湿大道 + 巴士 + 远处电车 + 接触网。"
                                                        "判断：全图最『电影感』的街景，但在大道南侧，离广场 40m。"},
    {"path": "web/_shots/live_r3_spawn.png", "seen_zh": "web 端默认出生点 live 截图：站在 (0,-65.5) 朝北，方尖碑正好在画面正中"
                                                       "把文化宫门厅挡住。判断：这正是不能直接用 (0,-60) yaw=0 当展示机位的原因。"},
    {"path": "web/_shots/plaza_obelisk.png", "seen_zh": "web 端从 (8,-62) 朝 NNE 眼平：文化宫全景 + 右侧树 + 远处塔楼。"
                                                        "判断：确认广场平视视野开阔、没有树/杆挡住文化宫。"},
    {"path": "web/_shots/esplanade_motherland.png", "seen_zh": "web 端纪念广场 (34,112) 朝 WNW：雕像 + 旗阵 + 长明火 + 倒影池 + 树。"
                                                               "判断：纪念区可走，但树列和旗杆会切画面。"},
    {"path": "web/_shots/aerial_overview.png", "seen_zh": "web 端高空总览：整块 400×300 地块、深蓝雾、右侧东区高楼群。"
                                                         "判断：适合放在加载/过场，不适合地面展示。"},
    {"path": "map/district_plan.png", "seen_zh": "2D 总平面 400×300（北朝上）：48 栋建筑、道路/步道/广场/水面/台阶、"
                                                 "图例与比例尺。用来核对所有坐标分区。"},
]


if __name__ == "__main__":
    main()
