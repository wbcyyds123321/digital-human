"""bench_geometry_gate.py -- detect the bench_variant "rotated parts" defect.

This is the missing validation gate: the old pipeline only validated the bench
*placement* yaw against the generator table, never whether each part's
orientation followed that yaw.  The defect: props.bench_variant() places part
centres through XF(x,y,facing) but PB.box() applies `rot` as a WORLD-axis Euler,
so for benches with facing != 0 the backrest slats keep a world-X lean while the
bench turns.

Gate logic (per bench, in bench-local frame):
  1. backrest slats (large faces whose local centre z is in [0.55, 1.05] m)
     must have normals with |n_local.x| < 0.02  -> catches the world-axis lean
  2. seat slat cluster (horizontal up faces, 0.25..0.95 m above ground, total
     area >= 0.15 m2) must have its long axis within 5 deg of the bench local X
     axis -> catches any yawed-seat defect
  3. backrest must sit on the +local-Y (rear) half, i.e. mean local y of
     backrest faces > mean local y of seat vertices - 0.02
  4. local footprint: every leg/seat vertex inside |x| <= 1.15, |y| <= 0.65
     (generous bound; catches planks sticking crosswise)

Usage (Blender background):
  blender -b <blend> --python bench_geometry_gate.py -- \
      --out <report.json> [--entities <world_entities.v0.json>] \
      [--yaw-source entities|custom] [--render-dir <dir>] [--tag old|new]

--yaw-source entities: yaw/centre come from world_entities.v0.json matched by
                       source_group / source_objects (real map blend).
--yaw-source custom:   yaw/centre come from object custom props
                       dsh_bench_yaw / dsh_bench_variant (prototype blend).

Exit code: 0 = all gates pass, 3 = gate failures detected, 1 = input error.
Nothing is written back to the .blend.
"""
import argparse
import json
import math
import sys
from pathlib import Path

import bpy
from mathutils import Vector

BENCH_PREFIXES = (
    "PROP_BENCH_", "PROP_PAVILION_BENCH_", "PROP_PARK_BENCH_",
    "PROP_TERMINAL_BENCH_", "PROP_FOREST_BENCH_",
)
SEAT_MIN_H = 0.25
SEAT_MAX_H = 0.95
BACK_Z_MIN = 0.55
BACK_Z_MAX = 1.05
BACK_NORMAL_X_MAX = 0.02
SEAT_LONG_AXIS_MAX_DEG = 5.0
FOOTPRINT_HALF_X = 1.15
FOOTPRINT_HALF_Y = 0.65


def args():
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    p = argparse.ArgumentParser()
    p.add_argument("--out", required=True)
    p.add_argument("--blend", default=None, help="open this .blend (factory startup)")
    p.add_argument("--entities", default=None)
    p.add_argument("--yaw-source", choices=["entities", "custom"], default="custom")
    p.add_argument("--render-dir", default=None)
    p.add_argument("--tag", default="")
    return p.parse_args(argv)


def mesh_objects_for(entity):
    """All MESH objects belonging to one bench entity."""
    names = list(entity.get("source_objects") or [])
    group = entity.get("source_group") or ""
    out = []
    for name in names:
        obj = bpy.data.objects.get(name)
        if obj is not None and obj.type == "MESH":
            out.append(obj)
        if obj is not None:
            out.extend(c for c in obj.children_recursive if c.type == "MESH")
    if not out and group:
        obj = bpy.data.objects.get(group)
        if obj is not None:
            out.append(obj) if obj.type == "MESH" else out.extend(
                c for c in obj.children_recursive if c.type == "MESH")
    if not out:
        # fallback: prefix match (merge children built by kit.bench)
        for obj in bpy.data.objects:
            if obj.type == "MESH" and group and obj.name.startswith(group):
                out.append(obj)
    # de-duplicate while keeping order
    seen, uniq = set(), []
    for o in out:
        if o.name not in seen:
            seen.add(o.name)
            uniq.append(o)
    return uniq


def world_triangles(objs):
    """Yield (Matrix, polygon, evaluated mesh) in world space."""
    depsgraph = bpy.context.evaluated_depsgraph_get()
    for obj in objs:
        evaluated = obj.evaluated_get(depsgraph)
        mesh = evaluated.to_mesh()
        m = evaluated.matrix_world
        yield evaluated, mesh, m


def bench_local_frame(objs, yaw_deg, center):
    """World->generator-local frame in Blender axes.

    Generator convention (props.bench_variant): local (lx, ly) is placed through
    XF with facing f, i.e. world = R(f) @ local, facing f = (180 - unity_yaw).
    So world->local = R(-(180 - yaw)) = R(yaw - 180).
    In this frame +local X = bench width (seat long axis), +local Y = BEHIND the
    bench (backrest side), -local Y = the direction the bench faces.
    """
    rad = math.radians(yaw_deg - 180.0)
    c, s = math.cos(rad), math.sin(rad)
    cx, cy = float(center[0]), float(center[1])

    def to_local(v):
        dx, dy = v.x - cx, v.y - cy
        return Vector((dx * c - dy * s, dx * s + dy * c, v.z))

    return to_local


def analyse_bench(entity, yaw_deg, center, to_local):
    """Return gate result dict for one bench."""
    objs = mesh_objects_for(entity)
    if not objs:
        return {"entity_id": entity.get("id"), "status": "INPUT_ERROR",
                "reason": "no MESH objects found", "checks": {}}
    ground_y = float((entity.get("ground") or {}).get("blender_z", 0.0) or 0.0)
    faces = []  # local-space face records
    all_local = []
    for evaluated, mesh, m in world_triangles(objs):
        for poly in mesh.polygons:
            verts = [to_local(m @ mesh.vertices[vi].co) for vi in poly.vertices]
            all_local.extend(verts)
            if len(verts) < 3:
                continue
            n = (m.to_3x3() @ poly.normal)
            n.normalize()
            # rotate normal into local frame (no translation), same R(yaw-180)
            rad_l = math.radians(yaw_deg - 180.0)
            cos_a, sin_a = math.cos(rad_l), math.sin(rad_l)
            n_local = Vector((n.x * cos_a - n.y * sin_a,
                              n.x * sin_a + n.y * cos_a, n.z))
            centre_local = sum(verts, Vector((0, 0, 0))) / len(verts)
            area = float(poly.area)
            faces.append({"n": n_local, "c": centre_local, "area": area})
    if not faces:
        return {"entity_id": entity.get("id"), "status": "INPUT_ERROR",
                "reason": "no faces", "checks": {}}

    checks = {}
    # --- 1. backrest slat orientation -------------------------------------
    back_faces = [f for f in faces
                  if BACK_Z_MIN <= f["c"].z - ground_y <= BACK_Z_MAX
                  and abs(f["n"].z) < 0.6 and f["area"] > 0.05]
    if back_faces:
        max_nx = max(abs(f["n"].x) for f in back_faces)
        checks["backrest_normal_local_x_max"] = round(max_nx, 6)
        checks["backrest_slats"] = len(back_faces)
        checks["backrest_orientation_pass"] = max_nx < BACK_NORMAL_X_MAX
        # lean direction: the slats lean backwards, so for the two big faces of
        # a backrest slat the local normal must have n.z*n.y < 0 (front face
        # n.y<0 -> n.z>0; rear face n.y>0 -> n.z<0).  The old world-X lean is
        # sign-flipped for benches whose yaw is 0/180, which this catches.
        lean_faces = [f for f in back_faces if abs(f["n"].y) > 0.9]
        if lean_faces:
            bad_lean = [f for f in lean_faces if f["n"].z * f["n"].y >= 0.0]
            checks["backrest_lean_bad_faces"] = len(bad_lean)
            checks["backrest_lean_pass"] = len(bad_lean) == 0
            checks["backrest_lean_expected_nz"] = round(
                sum(f["n"].z for f in lean_faces) / len(lean_faces), 6)
        else:
            checks["backrest_lean_bad_faces"] = 0
            checks["backrest_lean_pass"] = True
            checks["backrest_lean_expected_nz"] = None
    else:
        checks["backrest_orientation_pass"] = True  # backless variant
        checks["backrest_slats"] = 0
        checks["backrest_normal_local_x_max"] = 0.0
        checks["backrest_lean_bad_faces"] = 0
        checks["backrest_lean_pass"] = True
        checks["backrest_lean_expected_nz"] = None

    # --- 2. seat cluster + long axis --------------------------------------
    seat_faces = [f for f in faces
                  if f["n"].z > 0.99
                  and SEAT_MIN_H <= f["c"].z - ground_y <= SEAT_MAX_H]
    seat_area = sum(f["area"] for f in seat_faces)
    checks["seat_area_m2"] = round(seat_area, 6)
    checks["seat_faces"] = len(seat_faces)
    if seat_faces and seat_area >= 0.15:
        pts = []
        for f in seat_faces:
            # corners approximated from face centre + extents: use all_local
            pts.append(f["c"])
        # PCA in local XY using seat face centres + their vertex spread
        # (use all seat-cluster vertices by re-filtering z band + |y| small)
        verts = [v for v in all_local
                 if SEAT_MIN_H <= v.z - ground_y <= SEAT_MAX_H + 0.02]
        # restrict to horizontal slab band around the dominant z
        zs = sorted(v.z for v in verts)
        if not zs:
            checks["seat_long_axis_dev_deg"] = None
            checks["seat_orientation_pass"] = False
        else:
            med = zs[len(zs) // 2]
            slab = [v for v in verts if abs(v.z - med) < 0.06]
            mx = sum(v.x for v in slab) / len(slab)
            my = sum(v.y for v in slab) / len(slab)
            sxx = sum((v.x - mx) ** 2 for v in slab) / len(slab)
            syy = sum((v.y - my) ** 2 for v in slab) / len(slab)
            sxy = sum((v.x - mx) * (v.y - my) for v in slab) / len(slab)
            theta = 0.5 * math.atan2(2 * sxy, sxx - syy)
            dev = abs(math.degrees(theta))
            dev = min(dev, 180.0 - dev)
            checks["seat_long_axis_dev_deg"] = round(dev, 6)
            checks["seat_orientation_pass"] = dev < SEAT_LONG_AXIS_MAX_DEG
    else:
        checks["seat_long_axis_dev_deg"] = None
        checks["seat_orientation_pass"] = False

    # --- 3. backrest behind seat (+local Y = behind the bench) -------------
    if back_faces and seat_faces:
        back_y = sum(f["c"].y for f in back_faces) / len(back_faces)
        seat_y = sum(f["c"].y for f in seat_faces) / len(seat_faces)
        checks["backrest_minus_seat_local_y"] = round(back_y - seat_y, 6)
        checks["backrest_behind_pass"] = (back_y - seat_y) > 0.02
    else:
        checks["backrest_minus_seat_local_y"] = None
        checks["backrest_behind_pass"] = True

    # --- 4. footprint bounding --------------------------------------------
    max_x = max(abs(v.x) for v in all_local)
    max_y = max(abs(v.y) for v in all_local)
    checks["footprint_half_x_m"] = round(max_x, 6)
    checks["footprint_half_y_m"] = round(max_y, 6)
    checks["footprint_pass"] = (max_x <= FOOTPRINT_HALF_X and
                                max_y <= FOOTPRINT_HALF_Y)

    passed = all(checks[k] for k in (
        "backrest_orientation_pass", "backrest_lean_pass",
        "seat_orientation_pass", "backrest_behind_pass", "footprint_pass"))
    return {"entity_id": entity.get("id"), "status": "PASS" if passed else "FAIL",
            "checks": checks}


def load_entities_with_yaw(data):
    """entity list with (yaw, center) for every bench."""
    out = []
    for e in data["entities"]:
        if e.get("class") != "bench":
            continue
        yaw = (e.get("orientation") or {}).get("yaw_unity_deg")
        center = (e.get("center") or {}).get("blender") or [0, 0, 0]
        out.append((e, yaw, center))
    return out


def custom_entries():
    """Prototype mode: benches carry their own yaw/variant custom props."""
    out = []
    for obj in bpy.data.objects:
        if obj.type != "MESH":
            continue
        yaw = obj.get("dsh_bench_yaw")
        if yaw is None:
            continue
        center = [float(obj.get("dsh_bench_x", 0.0)),
                  float(obj.get("dsh_bench_y", 0.0)), 0.0]
        entity = {"id": "prototype/" + obj.name, "source_group": obj.name,
                  "source_objects": [obj.name],
                  "ground": {"blender_z": 0.0}}
        out.append((entity, float(yaw), center))
    return out


def main():
    a = args()
    if a.blend:
        bpy.ops.wm.open_mainfile(filepath=a.blend)
    if a.yaw_source == "entities":
        if not a.entities:
            print("GATE INPUT ERROR: --entities required for entities mode")
            sys.exit(1)
        data = json.loads(Path(a.entities).read_text(encoding="utf-8"))
        entries = load_entities_with_yaw(data)
    else:
        entries = custom_entries()
    results = []
    for entity, yaw, center in entries:
        if yaw is None:
            results.append({"entity_id": entity.get("id"),
                            "status": "FAIL",
                            "reason": "yaw_unity_deg unknown; cannot validate",
                            "checks": {}})
            continue
        to_local = bench_local_frame([], float(yaw), center)
        results.append(analyse_bench(entity, float(yaw), center, to_local))
    n_pass = sum(1 for r in results if r["status"] == "PASS")
    n_fail = sum(1 for r in results if r["status"] == "FAIL")
    n_err = sum(1 for r in results if r["status"] == "INPUT_ERROR")
    report = {
        "schema": "sbh.bench-geometry-gate.v1",
        "tag": a.tag,
        "blend": bpy.data.filepath,
        "blend_sha256": None,
        "object_count": len(entries),
        "pass": n_pass,
        "fail": n_fail,
        "input_error": n_err,
        "thresholds": {
            "back_normal_local_x_max": BACK_NORMAL_X_MAX,
            "seat_long_axis_max_deg": SEAT_LONG_AXIS_MAX_DEG,
            "footprint_half_x": FOOTPRINT_HALF_X,
            "footprint_half_y": FOOTPRINT_HALF_Y,
        },
        "results": results,
    }
    out = Path(a.out)
    out.parent.mkdir(parents=True, exist_ok=True)
    out.write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n",
                   encoding="utf-8")
    print("BENCH_GATE", json.dumps({"tag": a.tag, "objects": len(entries),
                                    "pass": n_pass, "fail": n_fail,
                                    "input_error": n_err,
                                    "blend": bpy.data.filepath}, ensure_ascii=False))
    if n_err:
        sys.exit(1)
    sys.exit(0 if n_fail == 0 else 3)


if __name__ == "__main__":
    main()
