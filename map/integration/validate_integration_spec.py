# -*- coding: utf-8 -*-
"""validate_integration_spec.py — 对 integration_spec.json 做独立复算校验。

只读 integration_spec.json + map/ 原始数据，重新算一遍并打印 PASS/FAIL。
用法:
    python validate_integration_spec.py
退出码 0 = 全部通过。
"""
import json
import math
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import sbh_common as S  # noqa: E402

HERE = os.path.dirname(os.path.abspath(__file__))
SPEC = os.path.join(HERE, "integration_spec.json")


def main():
    spec = json.load(open(SPEC, encoding="utf-8"))
    M = S.MapData()
    fails = []

    # ---- 1. 坐标换算自洽：Unity 实际 = (bx, bz, by) ----
    for v in spec["coordinate_mapping"]["verification_3_landmarks"]:
        mn = v["derived_unity_actual_bbox_min"]
        mx = v["derived_unity_actual_bbox_max"]
        bmn = v["derived_blender_xz_bbox"]
        # blender xz bbox → unity bbox: x 不变, z = y
        ok = (abs(mn[0] - bmn[0]) < 0.02 and abs(mx[0] - bmn[2]) < 0.02
              and abs(mn[2] - bmn[1]) < 0.02 and abs(mx[2] - bmn[3]) < 0.02)
        if not ok:
            fails.append("coord " + v["landmark"])
        if not v["match"]:
            fails.append("glb/manifest mismatch " + v["landmark"])
        ctr = v["unity_actual_center_from_blender"]
        ctr2 = M.manifest and v["manifest_center_blender"]
        if abs(ctr[0] - ctr2[0]) > 0.02 or abs(ctr[2] - ctr2[1]) > 0.02:
            fails.append("center map " + v["landmark"])
        print("  coord %-11s blender_xz=%s unity_bbox=%s..%s  %s"
              % (v["landmark"], bmn, mn, mx, "OK" if ok and v["match"] else "FAIL"))

    # ---- 2. 出生点可行走 ----
    for sp in spec["spawn_candidates"]:
        ux, uy, uz = sp["unity_position_with_ground_y"]
        bx, by = ux, uz                       # Unity → blender (x, y) = (ux, uz)
        r = M.walkable_report(bx, by)
        ok = r["walkable"] and abs(r["ground_z"] - uy) < 0.02
        print("  spawn %-16s pos=%s yaw=%6.2f nav=%.2fm ground=%.2f %s"
              % (sp["id"], sp["unity_position_with_ground_y"], sp["yaw_deg"],
                 r["nearest_nav_dist_m"], r["ground_z"], "OK" if ok else "FAIL"))
        if not ok:
            fails.append("spawn " + sp["id"])

    # ---- 3. 相机不插墙 + 视线不挡 + Rig 参数复算 ----
    for sp in spec["spawn_candidates"]:
        ux, uy, uz = sp["unity_position_with_ground_y"]
        for kind, cam in sp["cameras"].items():
            p = cam["unity_position"]
            inside = []
            for b in M.boxes:
                if b["kind"] in ("ground", "terrace_step", "terrace_platform", "low"):
                    continue
                if b["min"][0] <= p[0] <= b["max"][0] and b["min"][1] <= p[2] <= b["max"][1]:
                    if b["min"][2] < p[1] < b["max"][2]:
                        inside.append(b["id"])
            # 复算 rig：cam 水平距离/pitch/lookHeight
            dx, dz = p[0] - cam["look_at"][0], p[2] - cam["look_at"][2]
            h = math.hypot(dx, dz)
            pitch = math.degrees(math.atan2(p[1] - cam["look_at"][1], h))
            ok = (not inside and abs(h - cam["distance"]) < 0.05
                  and abs(pitch - cam["pitch"]) < 0.1)
            print("  cam   %-16s %-12s pos=%s dist=%.2f pitch=%.2f clip=%s %s"
                  % (sp["id"], kind, p, h, pitch, inside or "-", "OK" if ok else "FAIL"))
            if not ok:
                fails.append("cam %s/%s" % (sp["id"], kind))

    # ---- 4. 锚点 position/approach 可行走 ----
    n_ok = 0
    for a in spec["anchors"]:
        ok = True
        for key in ("position", "approach_position"):
            x, y, z = a[key]
            r = M.walkable_report(x, z)
            if not (r["walkable"] and not r["inside_water"]):
                ok = False
                print("  anchor %-20s %-12s %s  FAIL nav=%.2f coll=%s"
                      % (a["id"], key, a[key], r["nearest_nav_dist_m"], r["inside_collision"]))
        n_ok += 1 if ok else 0
        if not ok:
            fails.append("anchor " + a["id"])
    print("  anchors walkable: %d/%d" % (n_ok, len(spec["anchors"])))

    # ---- 5. 碰撞盒：数量 / 地面 / Unity 轴序 ----
    boxes = spec["collision_boxes"]
    ok = len(boxes) == 91 and all(b["min"][k] <= b["max"][k] for b in boxes for k in range(3))
    g = next(b for b in boxes if b["kind"] == "ground")
    ok = ok and g["max"][1] == 0.0 and g["min"][1] == -1.0
    print("  collision boxes: %d  ground=%s  %s" % (len(boxes), g["min"] + g["max"], "OK" if ok else "FAIL"))
    if not ok:
        fails.append("collision")

    # ---- 6. 可走范围 ----
    for o in spec["walkable_bounds"]["options"]:
        x0, z0 = o["unity_min"]
        x1, z1 = o["unity_max"]
        cnt = sum(1 for n in M.nodes if x0 <= n["x"] <= x1 and z0 <= n["y"] <= z1)
        ok = cnt == o["nav_nodes_inside"]
        print("  bounds %-20s %s..%s nodes=%d %s" % (o["id"], o["unity_min"], o["unity_max"], cnt, "OK" if ok else "FAIL"))
        if not ok:
            fails.append("bounds " + o["id"])

    # ---- 7. 地标名表 ----
    ok = len(spec["landmark_names"]) == 48 and all(
        abs(m["unity_center"][2] - m["manifest_center_unity_raw"][2] * -1) < 0.51
        or abs(m["manifest_center_unity_raw"][2]) < 0.01
        for m in spec["landmark_names"])
    print("  landmark_names: %d  %s" % (len(spec["landmark_names"]), "OK" if ok else "FAIL"))
    if not ok:
        fails.append("landmark_names")

    print("\n%s  (%d failures)" % ("ALL PASS" if not fails else "FAILURES: " + ", ".join(fails), len(fails)))
    return 0 if not fails else 1


if __name__ == "__main__":
    sys.exit(main())
