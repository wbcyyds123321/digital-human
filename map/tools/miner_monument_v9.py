"""Kuzbass miners monument v9 — MPFB human + heavy work coat modelled as volumes.

The MPFB human supplies the realistic head, face, neck and hands; the clothing is
modelled as proper volumes (greatcoat, trousers, boots) instead of skin-tight shells.

Run: blender.exe -b --python tools/miner_monument_v9.py -- --version v9 [--quick]
"""
import math
import os
import sys

import bpy

TOOLS = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, TOOLS)
ROOT = os.path.dirname(TOOLS)
sys.path.insert(0, ROOT)

import miner_monument_v7 as B  # helpers: MPFB human, materials, studio, pedestal, cameras

VERSION = "v9"
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


def ring(rx, ry, z, n=48, p=2.2, cy=0.0):
    return B.superellipse(rx, ry, n=n, p=p, cy=cy, z=z)


def tube(name, path, radii, mat, n=28):
    rings = []
    for i, p in enumerate(path):
        if i == 0:
            d = B.Vector(path[1]) - B.Vector(path[0])
        elif i == len(path) - 1:
            d = B.Vector(path[-1]) - B.Vector(path[-2])
        else:
            d = B.Vector(path[i + 1]) - B.Vector(path[i - 1])
        d.normalize()
        ref = B.Vector((0, 1, 0)) if abs(d.z) > 0.9 else B.Vector((0, 0, 1))
        side = d.cross(ref).normalized()
        fwd = side.cross(d).normalized()
        r = radii[i]
        rings.append([B.Vector(p) + side * (math.cos(math.tau * j / n) * r)
                      + fwd * (math.sin(math.tau * j / n) * r) for j in range(n)])
    return B.loft(name, rings, mat)


def build_coat(H, bronze, brass):
    parts = []
    # body of the greatcoat: shoulders -> below the knee, slightly flared
    prof = [
        (0.885, 0.112, 0.082), (0.855, 0.118, 0.086), (0.815, 0.120, 0.090),
        (0.760, 0.118, 0.090), (0.700, 0.115, 0.089), (0.650, 0.112, 0.088),
        (0.600, 0.114, 0.090), (0.545, 0.120, 0.094), (0.490, 0.126, 0.099),
        (0.440, 0.130, 0.103), (0.405, 0.132, 0.105),
    ]
    rings = [ring(rx * H, ry * H, z * H, p=2.35) for z, rx, ry in prof]
    parts.append(B.loft("COAT", rings, bronze))
    # sleeves
    for s in (-1, 1):
        path = [(s * 0.095 * H, -0.005 * H, 0.855 * H),
                (s * 0.108 * H, 0.0, 0.790 * H),
                (s * 0.118 * H, 0.0, 0.700 * H),
                (s * 0.125 * H, 0.0, 0.620 * H),
                (s * 0.128 * H, -0.005 * H, 0.560 * H),
                (s * 0.128 * H, -0.008 * H, 0.520 * H)]
        radii = [0.040 * H, 0.037 * H, 0.034 * H, 0.031 * H, 0.028 * H, 0.026 * H]
        parts.append(tube("sleeve", path, radii, bronze))
        # cuff ring
        cuff = [ring(0.029 * H, 0.029 * H, z, n=24, p=2.0) for z in
                (0.520 * H, 0.512 * H, 0.505 * H)]
        for r in cuff:
            for v in r:
                v.x += s * 0.128 * H
                v.y += -0.008 * H
        parts.append(B.loft("cuff", cuff, bronze, caps=False))
    # collar (raised greatcoat collar) + lapels
    collar = [ring(r * H, r * 0.90 * H, z, p=2.0) for z, r in [
        (0.845, 0.062), (0.862, 0.072), (0.882, 0.076), (0.898, 0.068)]]
    parts.append(B.loft("collar", collar, bronze, caps=False))
    for s in (-1, 1):
        parts.append(B.rounded_box("lapel", (0.045 * H, 0.018 * H, 0.15 * H),
                                   (s * 0.032 * H, -0.088 * H, 0.790 * H), bronze,
                                   bevel=0.006, rot=(math.radians(7), 0, math.radians(s * 9))))
    # placket
    parts.append(B.rounded_box("placket", (0.022 * H, 0.014 * H, 0.36 * H),
                               (0, -0.096 * H, 0.690 * H), bronze, bevel=0.004))
    # buttons
    for zf in (0.610, 0.655, 0.700, 0.745, 0.790):
        bpy.ops.mesh.primitive_cylinder_add(radius=0.011 * H, depth=0.008 * H, vertices=18,
                                            location=(0.006 * H, -0.100 * H, zf * H),
                                            rotation=(math.radians(90), 0, 0))
        b = bpy.context.object
        b.data.materials.append(brass)
        parts.append(b)
    # belt + buckle
    belt = [ring(0.117 * H, 0.093 * H, z, p=2.35) for z in (0.585 * H, 0.610 * H)]
    parts.append(B.loft("belt", belt, bronze, caps=False))
    parts.append(B.rounded_box("buckle", (0.034 * H, 0.014 * H, 0.028 * H),
                               (0, -0.098 * H, 0.598 * H), brass, bevel=0.004))
    # side pockets
    for s in (-1, 1):
        parts.append(B.rounded_box("pocket", (0.012 * H, 0.055 * H, 0.055 * H),
                                   (s * 0.116 * H, -0.030 * H, 0.560 * H), bronze, bevel=0.004))
    return B.join(parts, "MINER_COAT")


def build_trousers(H, bronze):
    parts = []
    for s in (-1, 1):
        path = [(s * 0.045 * H, 0.0, 0.560 * H), (s * 0.050 * H, 0.0, 0.470 * H),
                (s * 0.052 * H, 0.0, 0.360 * H), (s * 0.054 * H, 0.0, 0.250 * H),
                (s * 0.055 * H, 0.0, 0.150 * H), (s * 0.056 * H, 0.0, 0.080 * H)]
        parts.append(tube("leg", path, [0.070 * H, 0.064 * H, 0.058 * H, 0.052 * H,
                                        0.048 * H, 0.047 * H], bronze))
    return B.join(parts, "MINER_TROUSERS")


def build_boots(H, bronze):
    parts = []
    for s in (-1, 1):
        x = s * 0.056 * H
        rings = [ring(rx * H, ry * H, z * H, n=32, p=2.4, cy=cy * H) for z, rx, ry, cy in [
            (0.030, 0.048, 0.120, -0.030), (0.060, 0.050, 0.125, -0.032),
            (0.110, 0.050, 0.108, -0.024), (0.170, 0.048, 0.085, -0.014),
            (0.240, 0.046, 0.066, -0.006), (0.300, 0.045, 0.058, 0.0)]]
        for r in rings:
            for v in r:
                v.x += x
        parts.append(B.loft("boot", rings, bronze))
        parts.append(B.rounded_box("sole", (0.105 * H, 0.250 * H, 0.022 * H),
                                   (x, -0.030 * H, 0.012 * H), bronze, bevel=0.005))
        parts.append(B.rounded_box("heel", (0.090 * H, 0.055 * H, 0.020 * H),
                                   (x, 0.075 * H, 0.010 * H), bronze, bevel=0.004))
    return B.join(parts, "MINER_BOOTS")


def build_helmet(H, bronze, brass, head_top, head_cz):
    n = 44
    r0 = 0.058 * H
    rings = [ring(r0 * rx, r0 * ry, head_cz + dz, n=n, p=2.0) for dz, rx, ry in [
        (-0.028 * H, 1.02, 1.02), (0.004 * H, 1.10, 1.10), (0.020 * H, 1.07, 1.07),
        (0.040 * H, 0.95, 0.95), (0.058 * H, 0.74, 0.74), (0.072 * H, 0.45, 0.45),
        (0.080 * H, 0.16, 0.16)]]
    dome = B.loft("MINER_HELMET", rings, bronze)
    brim = [ring(r0 * rx, r0 * ry, head_cz + dz, n=n, p=2.0, cy=-0.004 * H)
            for dz, rx, ry in [(-0.026 * H, 1.04, 1.04), (-0.030 * H, 1.28, 1.34),
                               (-0.016 * H, 1.30, 1.36), (-0.004 * H, 1.16, 1.20)]]
    brim_ob = B.loft("brim", brim, bronze, caps=False)
    lamp = B.rounded_box("lamp_body", (0.032 * H, 0.024 * H, 0.028 * H),
                         (0, -0.056 * H, head_cz + 0.010 * H), brass, bevel=0.004)
    cyl = bpy.ops.mesh.primitive_cylinder_add(radius=0.013 * H, depth=0.006 * H, vertices=20,
                                              location=(0, -0.070 * H, head_cz + 0.010 * H),
                                              rotation=(math.radians(90), 0, 0))
    lens = bpy.context.object
    lens.data.materials.append(brass)
    return B.join([dome, brim_ob, lamp, lens], "MINER_HELMET")


def main():
    scn = B.reset()
    try:
        bpy.ops.preferences.addon_enable(module="bl_ext.blender_org.mpfb")
    except Exception as exc:
        print("MPFB ENABLE FAIL", exc)
    bronze = B.mat_bronze()
    bronze_cloth = B.mat_bronze("MINER_BRONZE_CLOTH", (0.52, 0.74))
    brass = B.mat_brass()
    granite = B.mat_granite()

    h = B.make_human()
    h = B.pose_human(h)
    H = h.dimensions.z
    print("H", round(H, 3))
    h.data.materials.append(bronze)
    # head landmarks
    zs = [v.co.z for v in h.data.vertices]
    head_top = max(zs)
    head_cz = head_top - 0.052 * H

    coat = build_coat(H, bronze_cloth, brass)
    trousers = build_trousers(H, bronze_cloth)
    boots = build_boots(H, bronze)
    helmet = build_helmet(H, bronze, brass, head_top, head_cz)
    niche = B.build_niche(h, coat, bronze, brass, H)
    # build_niche expects a jacket object with front surface; the coat front is at -0.10H
    pedestal = B.build_pedestal(granite, brass)

    fig_root = bpy.data.objects.new("MINER_FIGURE_ROOT", None)
    B.link(fig_root)
    fig_objs = [h, coat, trousers, boots, helmet] + [o for o in niche if o.type == "MESH"]
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
    # scale the parent too
    B.apply_mods(fig_root) if False else None
    bpy.context.view_layer.update()
    # put the feet on the pedestal
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
