"""Kuzbass miners monument v10 — MPFB human dressed with real MakeHuman clothes assets.

Run: blender.exe -b --python tools/miner_monument_v10.py -- --version v10 [--quick]
"""
import glob
import math
import os
import sys

import bpy

TOOLS = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, TOOLS)
ROOT = os.path.dirname(TOOLS)
sys.path.insert(0, ROOT)

import miner_monument_v7 as B

VERSION = "v10"
QUICK = "--quick" in sys.argv
for i, a in enumerate(sys.argv):
    if a == "--version" and i + 1 < len(sys.argv):
        VERSION = sys.argv[i + 1]
OUT_DIR = os.path.join(ROOT, "assets", "miner_monument")
RENDER_DIR = os.path.join(ROOT, "renders", "lm_miner", VERSION)
os.makedirs(OUT_DIR, exist_ok=True)
os.makedirs(RENDER_DIR, exist_ok=True)

BASE_Z = 3.30
FIG_H = 7.5
MPFB_USER = os.path.join(os.environ["APPDATA"], "Blender Foundation", "Blender", "5.2",
                         "extensions", ".user", "blender_org", "mpfb")
ASSET_DATA = os.path.join(MPFB_USER, "data")


def find_assets():
    clothes = sorted(glob.glob(os.path.join(ASSET_DATA, "clothes", "**", "*.mhclo"), recursive=True))
    hats = [p for p in clothes if os.path.basename(p).lower().startswith(("hats", "helmet", "cap"))] or []
    print("CLOTHES", len(clothes))
    for p in clothes:
        print("  ", os.path.relpath(p, ASSET_DATA))
    return clothes


def pick(paths, keywords, exclude=()):
    out = []
    for p in paths:
        name = os.path.basename(p).lower()
        if any(k in name for k in keywords) and not any(x in name for x in exclude):
            out.append(p)
    return out


def load_clothes(h, path):
    bpy.context.view_layer.objects.active = h
    bpy.ops.object.select_all(action="DESELECT")
    h.select_set(True)
    before = set(o.name for o in bpy.data.objects)
    try:
        res = bpy.ops.mpfb.load_clothes(filepath=path)
        new = [o for o in bpy.data.objects if o.name not in before and o.type == "MESH"]
        print("LOADED", os.path.basename(path), res, [o.name for o in new])
        return new
    except Exception as exc:
        print("LOAD FAIL", os.path.basename(path), exc)
        return []


def main():
    scn = B.reset()
    try:
        bpy.ops.preferences.addon_enable(module="bl_ext.blender_org.mpfb")
    except Exception as exc:
        print("MPFB FAIL", exc)
    bronze = B.mat_bronze()
    brass = B.mat_brass()
    granite = B.mat_granite()
    cloth_mat = B.mat_bronze("MINER_CLOTH_BRONZE", (0.55, 0.80))

    h = B.make_human()
    H = h.dimensions.z
    print("H", round(H, 3), "dims", tuple(round(v, 3) for v in h.dimensions))
    h.data.materials.append(bronze)

    clothes = find_assets()
    # pick clothes: one torso garment, one trousers, one boots
    torso = pick(clothes, ["shirt", "sweater", "jacket", "coat", "cardigan", "polo"])
    trousers = pick(clothes, ["trous", "pants", "jeans", "trouser"])
    boots = pick(clothes, ["boot", "shoe"], exclude=["bootcut"])
    print("PICK torso", [os.path.basename(p) for p in torso][:8])
    print("PICK trousers", [os.path.basename(p) for p in trousers][:8])
    print("PICK boots", [os.path.basename(p) for p in boots][:8])

    loaded = []
    # prefer simple items: one of each, then decide visually
    if torso:
        loaded += load_clothes(h, torso[0])
    if trousers:
        loaded += load_clothes(h, trousers[0])
    if boots:
        loaded += load_clothes(h, boots[0])

    for ob in loaded:
        ob.data.materials.clear()
        ob.data.materials.append(cloth_mat if ob.name.lower().startswith(("shirt", "sweater")) else bronze)
        for p in ob.data.polygons:
            p.use_smooth = True

    if not loaded:
        print("NO CLOTHES LOADED")

    # chest niche: cut into the torso garment if loaded, else the body
    target = None
    for ob in loaded:
        if "shirt" in ob.name.lower() or "sweater" in ob.name.lower() or "jacket" in ob.name.lower():
            target = ob
            break
    if target is None:
        target = h
    niche = B.build_niche(h, target, bronze, brass, H)

    pedestal = B.build_pedestal(granite, brass)
    fig_root = bpy.data.objects.new("MINER_FIGURE_ROOT", None)
    B.link(fig_root)
    fig_objs = [h] + loaded + [o for o in niche if o.type == "MESH"]
    for ob in fig_objs:
        ob.parent = fig_root
    for o in niche:
        if o.type == "LIGHT":
            o.parent = fig_root
    s = FIG_H / H
    fig_root.scale = (s, s, s)
    bpy.context.view_layer.update()
    bpy.ops.object.select_all(action="DESELECT")
    for ob in fig_objs:
        ob.select_set(True)
    bpy.context.view_layer.objects.active = fig_objs[0]
    bpy.ops.object.transform_apply(location=False, rotation=False, scale=True)
    bpy.context.view_layer.update()
    minz = min((ob.matrix_world @ B.Vector(c)).z for ob in fig_objs for c in ob.bound_box)
    fig_root.location.z = BASE_Z - minz
    bpy.context.view_layer.update()
    print("FIG MINZ", round(minz, 3), "root z", round(fig_root.location.z, 3))

    B.setup_studio()
    tgt = (0, 0, 5.3)
    B.add_camera("CAM_ASSET_FRONT", (0, -30, 5.3), tgt, ortho=11.6)
    B.add_camera("CAM_ASSET_SIDE", (30, 0, 5.3), tgt, ortho=11.6)
    B.add_camera("CAM_ASSET_BACK", (0, 30, 5.3), tgt, ortho=11.6)
    B.add_camera("CAM_ASSET_34", (11.5, -14.5, 8.4), (0, 0, 6.0), lens=62)
    B.add_camera("CAM_ASSET_HEAD", (2.0, -4.4, 10.3), (0, -0.02, 10.0), lens=110)
    B.add_camera("CAM_ASSET_CHEST", (1.5, -3.8, 8.55), (0, -0.5, 8.42), lens=110)

    views = [("CAM_ASSET_FRONT", "front.png", (900, 1300)),
             ("CAM_ASSET_SIDE", "side.png", (900, 1300)),
             ("CAM_ASSET_BACK", "back.png", (900, 1300)),
             ("CAM_ASSET_34", "three_quarter.png", (1200, 1000)),
             ("CAM_ASSET_HEAD", "head_detail.png", (1100, 1100)),
             ("CAM_ASSET_CHEST", "chest_detail.png", (1100, 1100))]
    if QUICK:
        views = [v for v in views if v[0] in ("CAM_ASSET_FRONT", "CAM_ASSET_34",
                                             "CAM_ASSET_HEAD", "CAM_ASSET_CHEST")]
    for cam, fn, res in views:
        B.render_view(bpy.data.objects[cam], os.path.join(RENDER_DIR, fn), res)

    blend = os.path.join(OUT_DIR, "miner_monument_%s.blend" % VERSION)
    bpy.ops.wm.save_as_mainfile(filepath=blend)
    tris = sum(len(o.data.polygons) for o in bpy.data.objects if o.type == "MESH")
    print("SAVED", blend)
    print("STATS objects=%d tris=%d" % (len(bpy.data.objects), tris))


if __name__ == "__main__":
    main()
