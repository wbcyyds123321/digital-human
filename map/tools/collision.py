#!/usr/bin/env python3
"""collision.py -- collision proxies for the Soviet Blue-Hour microdistrict.

Pure Python (no Blender needed). Reads ``tools/plan.json`` (+ ``map/scene_manifest.json``
for building heights) and writes:

    web/collision.json    list of AABBs for the browser viewer (three.js)
    map/collision.json    same list, engine-neutral (Unity / Unreal)
    map/collision.glb     tiny glTF-binary debug mesh of the same AABBs

Box conventions
---------------
* ``min`` / ``max`` are Blender world coords ``[x, y, z]`` in metres (Z-up, +Y north).
* Unity coords are ``(x, z, -y)`` -- a helper writes ``min_unity`` / ``max_unity``
  only in the console summary; consumers convert with ::
      ux = x;  uy = z;  uz = -y
* Building boxes use the exact footprint rect from ``plan.json``; the facades are
  built inside that rect, so a +/-1.2 m canopies/balconies allowance is documented
  but not added (balconies are above walking height anyway). Wall thickness grows
  *inward*, so players can hug the wall.
* Through-arch houses (``khrush_arch`` blocks: W_K3 at x=-81, W2_K3 at x=-132 in the
  Round-2 map) are split into west block / east block / walk-under lintel so the 4 m
  passages stay walkable.
* Street furniture boxes (obelisk, fountain rim, flagpoles, bus shelters, billboards,
  poster boards, pavilion, park sign, tram platform) are generated from
  ``plan.json["landmarks"]`` so new Round-2 landmarks are included automatically.

Usage
-----
    python tools/collision.py
    python tools/collision.py --plan tools/plan.json --out map --web-out web
"""
from __future__ import annotations

import argparse
import json
import os
import struct
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)

ARCH_X = -81.0
ARCH_HALF = 2.0          # 4 m through-passage
ARCH_LINTEL_Z = 2.95     # underside of the arch ceiling
WALK_CLEAR = 1.75        # player height for "can I walk under it" queries


def manifest_heights(path):
    heights = {}
    if os.path.exists(path):
        with open(path, "r", encoding="utf-8") as fh:
            for entry in json.load(fh):
                if "id" in entry and "height_m" in entry:
                    heights[entry["id"]] = float(entry["height_m"])
    return heights


def box(bid, x0, y0, z0, x1, y1, z1, kind, source=""):
    return {
        "id": bid,
        "min": [round(min(x0, x1), 3), round(min(y0, y1), 3), round(min(z0, z1), 3)],
        "max": [round(max(x0, x1), 3), round(max(y0, y1), 3), round(max(z0, z1), 3)],
        "kind": kind,
        "source": source,
    }


def est_height(block, heights):
    if block["id"] in heights:
        return heights[block["id"]]
    floors = block.get("floors", 2)
    return round(floors * 2.9 + 0.9, 1)


def glb_block_footprints(path):
    """Per-block world AABB from the exported GLB (glTF is Y-up: x, z, -y blender).

    Only *low* blocks (top <= 4.5 m, e.g. single-storey garages) are returned: for those
    the full bbox is a valid walking-height footprint, so canopies / aprons that stick
    out of the plan rect (E2_GARAGES does) still get collision. Tall buildings keep the
    plan rect (balconies are above walking height and must not block the pavement).
    """
    if not path or not os.path.exists(path):
        return {}
    with open(path, "rb") as fh:
        data = fh.read()
    if data[:4] != b"glTF":
        return {}
    n = struct.unpack("<I", data[12:16])[0]
    js = json.loads(data[20:20 + n].decode("utf-8"))
    out = {}
    for node in js.get("nodes", []):
        nm = node.get("name", "")
        if not nm.startswith("BLD_") or "mesh" not in node:
            continue
        bid = nm[4:].split(".")[0]
        mesh = js["meshes"][node["mesh"]]
        lo = [1e9, 1e9, 1e9]
        hi = [-1e9, -1e9, -1e9]
        for prim in mesh.get("primitives", []):
            acc = js["accessors"][prim["attributes"]["POSITION"]]
            for i in range(3):
                lo[i] = min(lo[i], acc["min"][i])
                hi[i] = max(hi[i], acc["max"][i])
        height = hi[1] - lo[1]
        if height > 4.5:
            continue
        # gltf (x, up, z) -> blender (x, -z, up); note y flips so min/max swap
        out[bid] = (lo[0], -hi[2], lo[1], hi[0], -lo[2], hi[1])
    return out


def build_boxes(plan, heights, actual_footprints=None):
    boxes = []
    actual_footprints = actual_footprints or {}
    expanded = []
    base_x, base_y = plan.get("base", [240, 180])
    # ---- ground kill-plane / walkable ground -------------------------------
    boxes.append(box("GROUND_PLANE", -base_x / 2 - 20, -base_y / 2 - 20, -1.0,
                     base_x / 2 + 20, base_y / 2 + 20, 0.0, "ground",
                     "map extent %dx%d + margin" % (base_x, base_y)))

    # ---- buildings ---------------------------------------------------------
    arch_ids = []
    for b in plan["blocks"]:
        bid = b["id"]
        x0, y0, x1, y1 = [float(v) for v in b["rect"]]
        h = est_height(b, heights)
        if b.get("type") == "khrush_arch":
            # through-arch house: west block / east block / walk-under lintel
            ax = (x0 + x1) / 2.0          # arch sits at the centre of the long face
            ax0, ax1 = ax - ARCH_HALF, ax + ARCH_HALF
            arch_ids.append((bid, ax))
            boxes.append(box(bid + "_W", x0, y0, 0.0, ax0, y1, h, "building", "arch west"))
            boxes.append(box(bid + "_E", ax1, y0, 0.0, x1, y1, h, "building", "arch east"))
            boxes.append(box(bid + "_LINTEL", ax0, y0, ARCH_LINTEL_Z, ax1, y1, h,
                             "arch_lintel", "walk-under arch ceiling x=%.0f" % ax))
        else:
            # collision covers the plan rect, widened to the real low-height footprint when
            # the build has a canopy/apron sticking out (E2_GARAGES) -- keeps proxies honest
            fx0, fy0, fx1, fy1 = x0, y0, x1, y1
            fp = actual_footprints.get(bid)
            if fp is not None:
                ax0, ay0, _az0, ax1, ay1, _az1 = fp
                if max(x0 - ax0, ax1 - x1, y0 - ay0, ay1 - y1) > 1.5:
                    fx0, fy0 = min(x0, ax0), min(y0, ay0)
                    fx1, fy1 = max(x1, ax1), max(y1, ay1)
                    expanded.append({"id": bid, "plan_rect": [x0, y0, x1, y1],
                                     "collision_rect": [round(fx0, 2), round(fy0, 2),
                                                        round(fx1, 2), round(fy1, 2)]})
            boxes.append(box(bid, fx0, fy0, 0.0, fx1, fy1, h, "building",
                             "%s / %s floors" % (b.get("type", ""), b.get("floors", "?"))))

    # ---- landmarks / street furniture (generated from plan landmarks) -----
    for lm in plan.get("landmarks", []):
        lid = "LM_" + lm["id"]
        typ = lm.get("type", "")
        x, y = lm.get("pos", [0.0, 0.0])
        if typ == "obelisk":
            boxes.append(box(lid, x - 1.6, y - 1.6, 0.0, x + 1.6, y + 1.6,
                             float(lm.get("height", 18)), "landmark", typ))
        elif typ == "fountain_dry":
            r = float(lm.get("radius", 5.0))
            boxes.append(box(lid, x - r - 0.2, y - r - 0.2, 0.0, x + r + 0.2, y + r + 0.2,
                             0.5, "low", typ))
        elif typ == "flagpoles":
            n = int(lm.get("count", 3))
            for i in range(n):
                dx = (i - (n - 1) / 2.0) * 1.5
                boxes.append(box("%s_%d" % (lid, i), x + dx - 0.08, y - 0.08, 0.0,
                                 x + dx + 0.08, y + 0.08, 8.0, "pole", typ))
        elif typ in ("bus_stop", "pavilion"):
            w, d, h = ((3.6, 1.2, 2.8) if typ == "bus_stop" else (4.2, 4.2, 3.0))
            boxes.append(box(lid, x - w / 2, y - d / 2, 0.0, x + w / 2, y + d / 2, h, "prop", typ))
        elif typ in ("billboard", "poster_board"):
            w = float((lm.get("size") or [6.0, 3.0])[0])
            hgt = float((lm.get("size") or [6.0, 3.0])[1])
            base = 0.8 if typ == "billboard" else 0.0
            boxes.append(box(lid, x - w / 2, y - 0.25, base, x + w / 2, y + 0.25,
                             base + hgt, "prop", typ))
        elif typ == "park_sign":
            boxes.append(box(lid, x - 1.8, y - 0.2, 0.0, x + 1.8, y + 0.2, 2.6, "prop", typ))
        elif typ == "tram_stop":
            pass      # tram island platform comes from plan["tram"] below (steppable)
        # ---- Round-3 memorial props -------------------------------------
        elif typ == "eternal_flame":
            boxes.append(box(lid, x - 1.8, y - 1.8, 0.0, x + 1.8, y + 1.8, 1.1, "prop", typ))
        elif typ == "guard_box":
            boxes.append(box(lid, x - 1.1, y - 1.1, 0.0, x + 1.1, y + 1.1, 2.7, "prop", typ))
        elif typ == "flag_row":
            for i in range(6):
                dx = (i - 2.5) * 2.4
                boxes.append(box("%s_%d" % (lid, i), x + dx - 0.08, y - 0.08, 0.0,
                                 x + dx + 0.08, y + 0.08, 9.0, "pole", typ))
        elif typ == "metro_sign":
            boxes.append(box(lid, x - 0.9, y - 0.2, 0.0, x + 0.9, y + 0.2, 3.0, "prop", typ))
        elif typ == "plaque":
            boxes.append(box(lid, x - 1.4, y - 0.15, 0.5, x + 1.4, y + 0.15, 1.8, "prop", typ))
    # tram platform (plan tram.stop_platform) - low, steppable
    px0, py0, px1, py1 = plan["tram"]["stop_platform"]
    boxes.append(box("TRAM_PLATFORM", px0, py0, 0.0, px1, py1, 0.25, "low", "tram platform"))

    # ---- Round-3 reflecting pools: water is blocked for the player ----------
    for z in plan.get("zones", []):
        if z.get("kind") != "water":
            continue
        x0, y0, x1, y1 = [float(v) for v in z["rect"]]
        boxes.append(box("WATER_" + z["id"], x0, y0, 0.0, x1, y1, 1.0, "water",
                         "water - blocked"))

    # ---- Round-3 monument terraces: walkable steps + platform (steppable) ---
    for t in plan.get("terraces", []):
        tid = t["id"]
        x0, y0, x1, y1 = [float(v) for v in t["rect"]]
        steps = int(t.get("steps", 6))
        rise = float(t.get("rise", 0.45))
        step_from = float(t.get("step_from", y0))
        platform_from = float(t.get("platform_from", (step_from + y1) / 2.0))
        step_h = (platform_from - step_from) / steps if steps else 1.0
        for i in range(steps):
            sy0 = step_from + i * step_h
            sy1 = sy0 + step_h
            boxes.append(box("%s_STEP%d" % (tid, i), x0, sy0, 0.0, x1, sy1,
                             (i + 1) * rise, "terrace_step", "walkable stair step"))
        boxes.append(box("%s_PLATFORM" % tid, x0, platform_from, 0.0, x1, y1,
                         steps * rise, "terrace_platform", "monument platform"))
    # entrance canopies / porch awnings are >= 2.6 m up: not needed at walking height
    return boxes, arch_ids, expanded


# ----------------------------------------------------------------------------
# minimal glTF-binary writer (boxes, vertex-coloured) -- debug/Unity visualisation
# ----------------------------------------------------------------------------
KIND_RGB = {
    "building": (0.72, 0.76, 0.82),
    "arch_lintel": (0.95, 0.62, 0.28),
    "ground": (0.16, 0.20, 0.26),
    "landmark": (0.90, 0.72, 0.56),
    "prop": (0.62, 0.70, 0.78),
    "pole": (0.80, 0.80, 0.80),
    "low": (0.30, 0.62, 0.48),
    "terrace_step": (0.86, 0.72, 0.42),
    "terrace_platform": (0.94, 0.80, 0.48),
    "water": (0.18, 0.42, 0.66),
}

_CUBE_FACES = (
    (0, 1, 2, 3, (0, 0, 1)), (4, 7, 6, 5, (0, 0, -1)),
    (0, 4, 5, 1, (0, -1, 0)), (1, 5, 6, 2, (1, 0, 0)),
    (2, 6, 7, 3, (0, 1, 0)), (3, 7, 4, 0, (-1, 0, 0)),
)


def write_glb(boxes, path):
    positions, normals, colors, indices = [], [], [], []
    for b in boxes:
        mn, mx = b["min"], b["max"]
        rgb = KIND_RGB.get(b["kind"], (0.6, 0.6, 0.6))
        corners = [(mn[0] if i & 1 else mx[0], mn[1] if i & 2 else mx[1], mn[2] if i & 4 else mx[2])
                   for i in range(8)]
        base = len(positions)
        for a, bb, c, d, nrm in _CUBE_FACES:
            for idx in (a, bb, c, d):
                positions.append(corners[idx])
                normals.append(nrm)
                colors.append(rgb)
            # the 4 vertices just appended are the last four of the position array
            indices += [len(positions) - 4, len(positions) - 3, len(positions) - 2,
                        len(positions) - 4, len(positions) - 2, len(positions) - 1]
        # duplicate vertices without sharing is fine for a debug mesh
    vcount = len(positions)
    pos_bytes = b"".join(struct.pack("<3f", *v) for v in positions)
    nrm_bytes = b"".join(struct.pack("<3f", *v) for v in normals)
    col_bytes = b"".join(struct.pack("<3f", *v) for v in colors)
    idx_bytes = b"".join(struct.pack("<I", i) for i in indices)
    bin_blob = pos_bytes + nrm_bytes + col_bytes + idx_bytes
    while len(bin_blob) % 4:
        bin_blob += b"\x00"

    def acc(offset, count, vtype, atype, minmax=None):
        d = {"bufferView": None, "componentType": vtype, "count": count, "type": atype}
        if minmax:
            d["min"], d["max"] = minmax
        return d

    off_pos, off_nrm, off_col = 0, len(pos_bytes), len(pos_bytes) + len(nrm_bytes)
    off_idx = len(pos_bytes) + len(nrm_bytes) + len(col_bytes)
    mins = [min(p[i] for p in positions) for i in range(3)]
    maxs = [max(p[i] for p in positions) for i in range(3)]
    gltf = {
        "asset": {"version": "2.0", "generator": "tools/collision.py"},
        "scene": 0,
        "scenes": [{"nodes": [0], "name": "COLLISION"}],
        "nodes": [{"mesh": 0, "name": "COLLISION_PROXIES"}],
        "meshes": [{"name": "collision_proxies", "primitives": [{
            "attributes": {"POSITION": 0, "NORMAL": 1, "COLOR_0": 2},
            "indices": 3, "material": 0, "mode": 4}]}],
        "materials": [{
            "name": "collision_debug",
            "pbrMetallicRoughness": {"baseColorFactor": [1, 1, 1, 1],
                                     "metallicFactor": 0.0, "roughnessFactor": 0.9},
            "doubleSided": True,
        }],
        "buffers": [{"byteLength": len(bin_blob)}],
        "bufferViews": [
            {"buffer": 0, "byteOffset": off_pos, "byteLength": len(pos_bytes), "target": 34962},
            {"buffer": 0, "byteOffset": off_nrm, "byteLength": len(nrm_bytes), "target": 34962},
            {"buffer": 0, "byteOffset": off_col, "byteLength": len(col_bytes), "target": 34962},
            {"buffer": 0, "byteOffset": off_idx, "byteLength": len(idx_bytes), "target": 34963},
        ],
        "accessors": [
            {"bufferView": 0, "componentType": 5126, "count": vcount, "type": "VEC3",
             "min": mins, "max": maxs},
            {"bufferView": 1, "componentType": 5126, "count": vcount, "type": "VEC3"},
            {"bufferView": 2, "componentType": 5126, "count": vcount, "type": "VEC3"},
            {"bufferView": 3, "componentType": 5125, "count": len(indices), "type": "SCALAR"},
        ],
    }
    js = json.dumps(gltf, separators=(",", ":")).encode("utf-8")
    while len(js) % 4:
        js += b" "
    out = bytearray()
    out += b"glTF" + struct.pack("<II", 2, 12 + 8 + len(js) + 8 + len(bin_blob))
    out += struct.pack("<I", len(js)) + b"JSON" + js
    out += struct.pack("<I", len(bin_blob)) + b"BIN\x00" + bin_blob
    with open(path, "wb") as fh:
        fh.write(out)
    return {"glb_bytes": len(out), "vertices": vcount, "triangles": len(indices) // 3}


def validate_glb(path):
    """Parse the GLB we just wrote and assert every index is inside the vertex buffer."""
    with open(path, "rb") as fh:
        data = fh.read()
    assert data[:4] == b"glTF", "bad GLB magic"
    version, total = struct.unpack("<II", data[4:12])
    assert version == 2 and total == len(data), (version, total, len(data))
    off = 12
    chunks = []
    while off < len(data):
        clen, ctype = struct.unpack("<I4s", data[off:off + 8])
        chunks.append((ctype, off + 8, clen))
        off += 8 + clen
    js = json.loads(data[chunks[0][1]:chunks[0][1] + chunks[0][2]].decode("utf-8"))
    prim = js["meshes"][0]["primitives"][0]
    vcount = js["accessors"][prim["attributes"]["POSITION"]]["count"]
    icount = js["accessors"][prim["indices"]]["count"]
    bin_off = chunks[1][1]
    bv = js["bufferViews"][js["accessors"][prim["indices"]]["bufferView"]]
    raw = data[bin_off + bv["byteOffset"]: bin_off + bv["byteOffset"] + bv["byteLength"]]
    idx = struct.unpack("<%dI" % icount, raw)
    assert max(idx) < vcount, "index out of range: max %d verts %d" % (max(idx), vcount)
    assert len(js["nodes"]) == 1 and js["scenes"][0]["nodes"] == [0]
    return {"vertices": vcount, "triangles": icount // 3, "indices_ok": True,
            "chunks": [c[0].decode() for c in chunks]}


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--plan", default=os.path.join(ROOT, "tools", "plan.json"))
    ap.add_argument("--manifest", default=os.path.join(ROOT, "map", "scene_manifest.json"))
    ap.add_argument("--out", default=os.path.join(ROOT, "map"))
    ap.add_argument("--web-out", default=os.path.join(ROOT, "web"))
    ap.add_argument("--glb", default=os.path.join(ROOT, "map", "SovietBlueHour_game.glb"),
                    help="optional exported GLB used to widen low-building footprints")
    args = ap.parse_args(argv)

    with open(args.plan, "r", encoding="utf-8") as fh:
        plan = json.load(fh)
    heights = manifest_heights(args.manifest)
    footprints = glb_block_footprints(args.glb)
    boxes, arch_ids, expanded = build_boxes(plan, heights, footprints)

    base_x, base_y = plan.get("base", [240, 180])
    doc = {
        "schema": "sbh.collision.v2",
        "generator": "tools/collision.py",
        "map": {"size_m": plan.get("base", [320, 240])},
        "coordinate_contract": {
            "blender": "metres, Z-up, +Y north, +X east; AABBs are axis aligned",
            "unity": "(x, y, z)unity = (x, z, -y)blender; min/max must be re-ordered per axis",
            "up_axis": "z_blender",
        },
        "player": {
            "radius_m": 0.35,
            "eye_height_m": 1.7,
            "step_height_m": 0.35,
            "walk_under_clearance_m": WALK_CLEAR,
        },
        "bounds": {"min": [-base_x / 2 - 20, -base_y / 2 - 20, -1],
                   "max": [base_x / 2 + 20, base_y / 2 + 20,
                           round(max([b["max"][2] for b in boxes] or [30.0]) + 1.0, 1)]},
        "arch_passages": [{"block": bid, "x": ax, "width_m": ARCH_HALF * 2,
                           "lintel_z": ARCH_LINTEL_Z} for bid, ax in arch_ids],
        "footprint_expansions": expanded,
        "stats": {
            "boxes": len(boxes),
            "buildings": sum(1 for b in boxes if b["kind"] == "building"),
            "arch_parts": sum(1 for b in boxes if b["kind"] == "arch_lintel"),
            "props_landmarks": sum(1 for b in boxes if b["kind"] in ("prop", "landmark", "pole")),
            "low_steppable": sum(1 for b in boxes if b["kind"] == "low"),
            "terrace_steps": sum(1 for b in boxes if b["kind"] == "terrace_step"),
            "terrace_platforms": sum(1 for b in boxes if b["kind"] == "terrace_platform"),
            "water_bodies": sum(1 for b in boxes if b["kind"] == "water"),
            "height_source": "map/scene_manifest.json (fallback floors*2.9+0.9)",
        },
        "boxes": boxes,
    }
    os.makedirs(args.out, exist_ok=True)
    os.makedirs(args.web_out, exist_ok=True)
    json_path = os.path.join(args.out, "collision.json")
    web_path = os.path.join(args.web_out, "collision.json")
    for p in (json_path, web_path):
        with open(p, "w", encoding="utf-8") as fh:
            json.dump(doc, fh, indent=1)
    glb_path = os.path.join(args.out, "collision.glb")
    glb_stats = write_glb(boxes, glb_path)
    glb_check = validate_glb(glb_path)

    heights_used = {b["id"]: [b["min"][2], b["max"][2]] for b in boxes if b["kind"] == "building"}
    print("COLLISION boxes=%d buildings=%d props=%d low=%d arches=%s" % (
        len(boxes), doc["stats"]["buildings"], doc["stats"]["props_landmarks"],
        doc["stats"]["low_steppable"], [(b, round(x, 1)) for b, x in arch_ids]))
    print("  map bounds:", doc["bounds"])
    print("  low-block footprint expansions:", expanded)
    print("  heights:", ", ".join("%s=%.1fm" % (k, v[1]) for k, v in list(heights_used.items())[:6]), "...")
    print("  glb:", glb_stats, "| validated:", glb_check)
    print("WROTE", json_path)
    print("WROTE", web_path)
    print("WROTE", glb_path)
    return 0


if __name__ == "__main__":
    sys.exit(main())
