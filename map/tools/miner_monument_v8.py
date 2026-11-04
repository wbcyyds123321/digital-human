"""Kuzbass miners monument v8 — built on an MPFB human base.

Pipeline: MPFB human (male, muscular) -> pose -> garment shells extracted from
the body -> helmet / chest flame niche / granite pedestal -> bronze materials ->
studio review renders -> asset .blend.

Run: blender.exe -b --python tools/miner_monument_v6.py -- --version v6 [--quick]
"""
import math
import os
import sys

import bpy
import bmesh
import gzip
from mathutils import Vector, Matrix

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
VERSION = "v8"
QUICK = "--quick" in sys.argv
for i, a in enumerate(sys.argv):
    if a == "--version" and i + 1 < len(sys.argv):
        VERSION = sys.argv[i + 1]
OUT_DIR = os.path.join(ROOT, "assets", "miner_monument")
RENDER_DIR = os.path.join(ROOT, "renders", "lm_miner", VERSION)
os.makedirs(OUT_DIR, exist_ok=True)
os.makedirs(RENDER_DIR, exist_ok=True)

MPFB_DATA = os.path.join(os.environ["APPDATA"], "Blender Foundation", "Blender", "5.2",
                         "extensions", "blender_org", "mpfb", "data")
TARGETS = os.path.join(MPFB_DATA, "targets", "macrodetails")

BASE_Z = 3.30
FIG_H = 7.5


# --------------------------------------------------------------------------- basic
def reset():
    bpy.ops.wm.read_factory_settings(use_empty=True)
    scn = bpy.context.scene
    scn.unit_settings.system = "METRIC"
    scn.render.engine = "BLENDER_EEVEE"
    scn.eevee.taa_render_samples = 48
    scn.view_settings.view_transform = "AgX"
    scn.view_settings.look = "AgX - Medium High Contrast"
    scn.view_settings.exposure = 0.35
    return scn


def link(ob):
    bpy.context.scene.collection.objects.link(ob)
    return ob


def rounded_box(name, size, loc, mat=None, bevel=0.01, seg=3, rot=(0, 0, 0)):
    bpy.ops.mesh.primitive_cube_add(size=1)
    ob = bpy.context.object
    ob.name = name
    ob.scale = Vector(size) / 2.0
    bpy.ops.object.transform_apply(location=False, rotation=False, scale=True)
    ob.location = loc
    ob.rotation_euler = rot
    if bevel > 0:
        m = ob.modifiers.new("bev", "BEVEL")
        m.width = bevel
        m.segments = seg
        m.limit_method = "ANGLE"
    if mat:
        ob.data.materials.append(mat)
    return ob


def sphere(name, loc, size, mat, seg=24, rings=16):
    bpy.ops.mesh.primitive_uv_sphere_add(radius=1.0, segments=seg, ring_count=rings,
                                         location=loc)
    o = bpy.context.object
    o.name = name
    o.scale = size
    o.data.materials.append(mat)
    return o


def superellipse(rx, ry, n=40, p=2.1, cx=0.0, cy=0.0, z=0.0):
    pts = []
    for i in range(n):
        a = math.tau * i / n
        c, s = math.cos(a), math.sin(a)
        pts.append(Vector((cx + rx * math.copysign(abs(c) ** (2.0 / p), c),
                           cy + ry * math.copysign(abs(s) ** (2.0 / p), s), z)))
    return pts


def loft(name, rings, mat=None, caps=True, smooth=True):
    n = len(rings[0])
    verts = [v for ring in rings for v in ring]
    faces = []
    for i in range(len(rings) - 1):
        a, b = i * n, (i + 1) * n
        faces += [(a + j, a + (j + 1) % n, b + (j + 1) % n, b + j) for j in range(n)]
    if caps:
        faces.append(tuple(range(n - 1, -1, -1)))
        last = (len(rings) - 1) * n
        faces.append(tuple(last + j for j in range(n)))
    me = bpy.data.meshes.new(name)
    me.from_pydata([tuple(v) for v in verts], [], faces)
    me.validate()
    me.update()
    if smooth:
        for p in me.polygons:
            p.use_smooth = True
    ob = bpy.data.objects.new(name, me)
    bpy.context.scene.collection.objects.link(ob)
    if mat:
        me.materials.append(mat)
    return ob


def join(objs, name):
    objs = [o for o in objs if o is not None]
    bpy.ops.object.select_all(action="DESELECT")
    for o in objs:
        o.select_set(True)
    bpy.context.view_layer.objects.active = objs[0]
    bpy.ops.object.join()
    ob = bpy.context.object
    ob.name = name
    return ob


def apply_mods(ob):
    bpy.context.view_layer.objects.active = ob
    bpy.ops.object.select_all(action="DESELECT")
    ob.select_set(True)
    for m in list(ob.modifiers):
        try:
            bpy.ops.object.modifier_apply(modifier=m.name)
        except Exception as exc:
            print("MODFAIL", ob.name, m.name, exc)
            ob.modifiers.remove(m)


def decimate(ob, ratio):
    m = ob.modifiers.new("dec", "DECIMATE")
    m.ratio = ratio
    apply_mods(ob)


def smooth_shade(ob):
    for p in ob.data.polygons:
        p.use_smooth = True


def bake_shape_keys(ob):
    keys = list(ob.data.shape_keys.key_blocks)
    basis = keys[0]
    active = [k for k in keys[1:] if k.value > 0.0001]
    if not active:
        ob.shape_key_clear()
        return
    for v in ob.data.vertices:
        bco = basis.data[v.index].co
        co = bco.copy()
        for k in active:
            co += (k.data[v.index].co - bco) * k.value
        v.co = co
    ob.shape_key_clear()


# --------------------------------------------------------------------------- materials

def apply_target(h, rel, weight=1.0):
    """Apply a MakeHuman .target.gz (index dx dy dz per line) to the base mesh."""
    p = os.path.join(TARGETS, rel)
    if not os.path.exists(p):
        print("TARGET MISSING", rel)
        return
    count = 0
    total = None
    with gzip.open(p, "rt", encoding="utf-8") as f:
        first = f.readline().strip()
        try:
            total = int(first)
        except ValueError:
            total = None
        for line in f:
            parts = line.split()
            if len(parts) < 4:
                continue
            try:
                idx = int(parts[0])
                dx, dy, dz = float(parts[1]), float(parts[2]), float(parts[3])
            except ValueError:
                continue
            if 0 <= idx < len(h.data.vertices):
                v = h.data.vertices[idx]
                v.co.x += dx * weight
                v.co.y += dy * weight
                v.co.z += dz * weight
                count += 1
    print("TARGET", rel, "applied", count, "of", total)


def front_y_obj(ob, x, z, start=-2.0):
    mw = ob.matrix_world
    origin = mw.inverted() @ Vector((x, start, z))
    d = (mw.inverted().to_3x3() @ Vector((0, 1, 0))).normalized()
    hit, loc, n, idx = ob.ray_cast(origin, d)
    return (mw @ loc).y if hit else None


def cut_covered(h, cover_fn):
    me = h.data
    bm = bmesh.new()
    bm.from_mesh(me)
    bad = [v for v in bm.verts if cover_fn(v.co)]
    bmesh.ops.delete(bm, geom=bad, context="VERTS")
    bm.to_mesh(me)
    bm.free()


def mat_bronze(name="MINER_BRONZE", rough=(0.44, 0.66)):
    m = bpy.data.materials.new(name)
    m.use_nodes = True
    nt = m.node_tree
    bsdf = next(n for n in nt.nodes if n.type == "BSDF_PRINCIPLED")
    bsdf.inputs["Metallic"].default_value = 0.62
    bsdf.inputs["Base Color"].default_value = (0.055, 0.042, 0.030, 1)
    patina = nt.nodes.new("ShaderNodeMixRGB")
    patina.blend_type = "MIX"
    patina.inputs["Color1"].default_value = (0.055, 0.042, 0.030, 1)
    patina.inputs["Color2"].default_value = (0.085, 0.135, 0.105, 1)
    noise = nt.nodes.new("ShaderNodeTexNoise")
    noise.inputs["Scale"].default_value = 3.5
    noise.inputs["Detail"].default_value = 8.0
    noise.inputs["Roughness"].default_value = 0.6
    ramp = nt.nodes.new("ShaderNodeValToRGB")
    ramp.color_ramp.elements[0].position = 0.45
    ramp.color_ramp.elements[0].color = (0, 0, 0, 1)
    ramp.color_ramp.elements[1].position = 0.80
    ramp.color_ramp.elements[1].color = (0.34, 0.34, 0.34, 1)
    nt.links.new(noise.outputs["Fac"], ramp.inputs["Fac"])
    nt.links.new(ramp.outputs["Color"], patina.inputs["Fac"])
    nt.links.new(patina.outputs["Color"], bsdf.inputs["Base Color"])
    ao = nt.nodes.new("ShaderNodeAmbientOcclusion")
    ao.inputs["Distance"].default_value = 0.25
    aom = nt.nodes.new("ShaderNodeMixRGB")
    aom.blend_type = "MULTIPLY"
    aom.inputs["Fac"].default_value = 0.45
    nt.links.new(patina.outputs["Color"], aom.inputs["Color1"])
    nt.links.new(ao.outputs["Color"], aom.inputs["Color2"])
    nt.links.new(aom.outputs["Color"], bsdf.inputs["Base Color"])
    n2 = nt.nodes.new("ShaderNodeTexNoise")
    n2.inputs["Scale"].default_value = 24.0
    n2.inputs["Detail"].default_value = 6.0
    rmap = nt.nodes.new("ShaderNodeMapRange")
    rmap.inputs["To Min"].default_value = rough[0]
    rmap.inputs["To Max"].default_value = rough[1]
    nt.links.new(n2.outputs["Fac"], rmap.inputs["Value"])
    nt.links.new(rmap.outputs["Result"], bsdf.inputs["Roughness"])
    bump = nt.nodes.new("ShaderNodeBump")
    bump.inputs["Strength"].default_value = 0.24
    bump.inputs["Distance"].default_value = 0.010
    nt.links.new(n2.outputs["Fac"], bump.inputs["Height"])
    nt.links.new(bump.outputs["Normal"], bsdf.inputs["Normal"])
    return m


def mat_granite():
    m = bpy.data.materials.new("MINER_GRANITE")
    m.use_nodes = True
    nt = m.node_tree
    bsdf = next(n for n in nt.nodes if n.type == "BSDF_PRINCIPLED")
    bsdf.inputs["Roughness"].default_value = 0.42
    n = nt.nodes.new("ShaderNodeTexNoise")
    n.inputs["Scale"].default_value = 30.0
    n.inputs["Detail"].default_value = 8.0
    ramp = nt.nodes.new("ShaderNodeValToRGB")
    ramp.color_ramp.elements[0].color = (0.020, 0.020, 0.024, 1)
    ramp.color_ramp.elements[1].color = (0.058, 0.058, 0.064, 1)
    nt.links.new(n.outputs["Fac"], ramp.inputs["Fac"])
    nt.links.new(ramp.outputs["Color"], bsdf.inputs["Base Color"])
    bump = nt.nodes.new("ShaderNodeBump")
    bump.inputs["Strength"].default_value = 0.15
    bump.inputs["Distance"].default_value = 0.004
    nt.links.new(n.outputs["Fac"], bump.inputs["Height"])
    nt.links.new(bump.outputs["Normal"], bsdf.inputs["Normal"])
    return m


def mat_brass():
    m = bpy.data.materials.new("MINER_BRASS")
    m.use_nodes = True
    bsdf = next(n for n in m.node_tree.nodes if n.type == "BSDF_PRINCIPLED")
    bsdf.inputs["Base Color"].default_value = (0.28, 0.19, 0.065, 1)
    bsdf.inputs["Metallic"].default_value = 1.0
    bsdf.inputs["Roughness"].default_value = 0.38
    return m


def mat_flame():
    m = bpy.data.materials.new("MINER_FLAME")
    m.use_nodes = True
    nt = m.node_tree
    out = next(n for n in nt.nodes if n.type == "OUTPUT_MATERIAL")
    em = nt.nodes.new("ShaderNodeEmission")
    em.inputs["Color"].default_value = (1.0, 0.36, 0.05, 1)
    em.inputs["Strength"].default_value = 30.0
    nt.links.new(em.outputs["Emission"], out.inputs["Surface"])
    return m


# --------------------------------------------------------------------------- human
def make_human():
    """MPFB base human -> male macrodetail shape key -> baked into the mesh."""
    bpy.ops.mpfb.create_human()
    h = bpy.data.objects["Human"]
    keys = list(h.data.shape_keys.key_blocks)
    target = next((k for k in keys if "$ma" in k.name and "universal" in k.name), None)
    for k in keys[1:]:
        k.value = 0.0
    if target is not None:
        target.value = 1.0
        print("MALE KEY", target.name)
    basis = keys[0]
    active = [k for k in keys[1:] if k.value > 1e-4]
    for v in h.data.vertices:
        bco = basis.data[v.index].co
        co = bco.copy()
        for k in active:
            co += (k.data[v.index].co - bco) * k.value
        v.co = co
    h.shape_key_clear()
    h.data.update()
    bpy.context.view_layer.update()
    print("HUMAN DIMS", tuple(round(v, 3) for v in h.dimensions))
    return h


def pose_human(h):
    bpy.context.view_layer.objects.active = h
    bpy.ops.object.select_all(action="DESELECT")
    h.select_set(True)
    bpy.ops.mpfb.add_standard_rig()
    arm = next(o for o in bpy.data.objects if o.type == "ARMATURE")
    bpy.context.view_layer.objects.active = arm
    bpy.ops.object.mode_set(mode="POSE")

    def point(name, d):
        pb = arm.pose.bones.get(name)
        if not pb:
            return
        m = pb.matrix.copy()
        head = m.translation.copy()
        cur = (m.to_3x3() @ Vector((0, 1, 0))).normalized()
        q = cur.rotation_difference(Vector(d).normalized())
        pb.matrix = Matrix.Translation(head) @ q.to_matrix().to_4x4() @ \
            Matrix.Translation(-head) @ m
        bpy.context.view_layer.update()

    for side, sx in (("L", 1), ("R", -1)):
        point("upperarm01." + side, (sx * 0.14, 0.02, -1.0))
        point("lowerarm01." + side, (sx * 0.05, -0.01, -1.0))
        for nm in ("hand01." + side, "wrist." + side):
            if arm.pose.bones.get(nm):
                point(nm, (sx * 0.02, -0.05, -1.0))
                break
    bpy.ops.object.mode_set(mode="OBJECT")
    bpy.context.view_layer.objects.active = h
    apply_mods(h)
    bpy.data.objects.remove(arm, do_unlink=True)
    bpy.context.view_layer.update()
    print("POSED DIMS", tuple(round(v, 3) for v in h.dimensions))
    return h


def body_front_y(h, x, z, start=-2.0):
    mw = h.matrix_world
    origin = mw.inverted() @ Vector((x, start, z))
    d = (mw.inverted().to_3x3() @ Vector((0, 1, 0))).normalized()
    hit, loc, n, idx = h.ray_cast(origin, d)
    return (mw @ loc).y if hit else None


def shell(name, h, keep, thickness, mat, folds=0.0025):
    """Duplicate the human, keep only the region selected by keep(co), solidify."""
    dup = h.copy()
    dup.data = h.data.copy()
    link(dup)
    dup.name = name
    me = dup.data
    bm = bmesh.new()
    bm.from_mesh(me)
    bad = [v for v in bm.verts if not keep(v.co)]
    bmesh.ops.delete(bm, geom=bad, context="VERTS")
    bm.to_mesh(me)
    bm.free()
    solid = dup.modifiers.new("solid", "SOLIDIFY")
    solid.thickness = thickness
    solid.offset = 1.0
    solid.use_rim = True
    solid.use_rim_only = False
    if folds:
        tex = bpy.data.textures.new(name + "_fold", "CLOUDS")
        tex.noise_scale = 0.06
        dm = dup.modifiers.new("fold", "DISPLACE")
        dm.texture = tex
        dm.strength = folds
        dm.mid_level = 0.5
    apply_mods(dup)
    dup.data.materials.append(mat)
    smooth_shade(dup)
    return dup


def build_figure():
    bronze = mat_bronze()
    bronze_cloth = mat_bronze("MINER_BRONZE_CLOTH", (0.55, 0.80))
    brass = mat_brass()

    h = make_human()
    h = pose_human(h)
    H = h.dimensions.z
    print("H", round(H, 3))
    h.data.materials.append(bronze)

    def keep_jacket(co):
        z = co.z
        if 0.545 * H <= z <= 0.875 * H:
            return True
        if abs(co.x) > 0.12 * H and 0.44 * H <= z <= 0.87 * H:
            return True
        return False

    def keep_trousers(co):
        return 0.035 * H <= co.z <= 0.60 * H

    def keep_boots(co):
        return co.z <= 0.085 * H

    # garment shells are copied from the full body, then the covered body is removed
    jacket = shell("MINER_JACKET", h, keep_jacket, 0.048, bronze_cloth, folds=0.004)
    trousers = shell("MINER_TROUSERS", h, keep_trousers, 0.038, bronze_cloth, folds=0.004)
    boots = shell("MINER_BOOTS", h, keep_boots, 0.040, bronze, folds=0.002)
    cut_covered(h, lambda co: keep_jacket(co) or keep_trousers(co) or keep_boots(co))
    print("BODY AFTER CUT", len(h.data.vertices))

    # masculine silhouette: broaden shoulders, flatten chest, straighten jacket
    for ob in (jacket,):
        for v in ob.data.vertices:
            zf = v.co.z / H
            if 0.63 < zf < 0.78 and v.co.y < 0:
                v.co.y *= 0.86
            if 0.74 < zf < 0.83:
                v.co.x *= 1.07
            if 0.54 < zf < 0.60:
                v.co.x *= 0.96
    for ob in (trousers, boots):
        for v in ob.data.vertices:
            v.co.y *= 0.98

    soles = []
    for s in (-1, 1):
        fp = foot_center(h, s, H)
        soles.append(rounded_box("sole", (0.125, 0.28, 0.024),
                                 (fp.x, fp.y + 0.02, 0.014), bronze, bevel=0.006))
    helmet = build_helmet(h, bronze, brass, H)
    details = build_garment_details(h, jacket, bronze, brass, H)
    niche = build_niche(h, jacket, bronze, brass, H)

    fig_root = bpy.data.objects.new("MINER_FIGURE_ROOT", None)
    link(fig_root)
    fig_objs = [h, jacket, trousers, boots, helmet, details] + soles + \
               [o for o in niche if o.type == "MESH"]
    for ob in fig_objs:
        ob.parent = fig_root
    for o in niche:
        if o.type == "LIGHT":
            o.parent = fig_root
    s = FIG_H / H
    fig_root.scale = (s, s, s)
    bpy.context.view_layer.update()
    bpy.ops.object.select_all(action="DESELECT")
    fig_root.select_set(True)
    bpy.context.view_layer.objects.active = fig_root
    bpy.ops.object.transform_apply(location=False, rotation=False, scale=True)
    for ob in fig_objs:
        ob.select_set(True)
    bpy.ops.object.transform_apply(location=False, rotation=False, scale=True)
    bpy.context.view_layer.update()

    # sit the figure exactly on the pedestal top
    minz = min((ob.matrix_world @ Vector(corner)).z
               for ob in fig_objs for corner in ob.bound_box)
    fig_root.location.z = BASE_Z - minz
    bpy.context.view_layer.update()
    print("FIG MINZ", round(minz, 3), "-> shifted", round(fig_root.location.z, 3))
    return fig_root, h


def foot_center(h, side, H):
    xs = []
    for v in h.data.vertices:
        if v.co.z < 0.05 * H and (v.co.x * side) > 0:
            xs.append(v.co)
    if not xs:
        return Vector((side * 0.1 * H, 0, 0))
    c = sum(xs, Vector()) / len(xs)
    return c


def build_helmet(h, bronze, brass, H):
    # head landmarks
    zs = [v.co.z for v in h.data.vertices]
    top = max(zs)
    head_r = 0.061 * H
    cz = top - 0.05 * H
    cy = 0.0
    n = 44
    rings = [superellipse(head_r * rx, head_r * ry, n=n, p=2.0, cy=cy, z=cz + dz)
             for dz, rx, ry in [(-0.030 * H, 1.01, 1.01), (0.006 * H, 1.08, 1.08),
                                (0.020 * H, 1.03, 1.03), (0.038 * H, 0.93, 0.93),
                                (0.062 * H, 0.80, 0.80), (0.080 * H, 0.56, 0.56),
                                (0.094 * H, 0.26, 0.26)]]
    dome = loft("MINER_HELMET", rings, bronze)
    brim_rings = [superellipse(head_r * rx, head_r * ry, n=n, p=2.0, cy=cy - 0.004 * H,
                              z=cz + dz)
                  for dz, rx, ry in [(-0.026 * H, 1.02, 1.02), (-0.028 * H, 1.22, 1.28),
                                     (-0.020 * H, 1.20, 1.24), (-0.010 * H, 1.10, 1.14)]]
    brim = loft("brim", brim_rings, bronze, caps=False)
    lamp = rounded_box("lamp_body", (0.030 * H, 0.024 * H, 0.026 * H),
                       (0, cy - 0.048 * H, cz + 0.018 * H), brass, bevel=0.004)
    cyl = bpy.ops.mesh.primitive_cylinder_add(radius=0.012 * H, depth=0.006 * H, vertices=20,
                                              location=(0, cy - 0.062 * H, cz + 0.018 * H),
                                              rotation=(math.radians(90), 0, 0))
    lens = bpy.context.object
    lens.data.materials.append(brass)
    return join([dome, brim, lamp, lens], "MINER_HELMET")


def build_niche(h, jacket, bronze, brass, H):
    zc = 0.735 * H
    yf = front_y_obj(jacket, 0.0, zc) or -0.115 * H
    fw, fh = 0.095 * H, 0.125 * H
    cutter = rounded_box("cutter", (fw, 0.11 * H, fh),
                         (0, yf + 0.045 * H, zc), None, bevel=0.0)
    boo = jacket.modifiers.new("niche", "BOOLEAN")
    boo.operation = "DIFFERENCE"
    boo.object = cutter
    boo.solver = "EXACT"
    apply_mods(jacket)
    bpy.data.objects.remove(cutter, do_unlink=True)
    yf2 = front_y_obj(jacket, 0.0, zc) or yf

    obs = []
    for sx, sy, sz, loc in [
        (fw + 0.026 * H, 0.016 * H, 0.016 * H, (0, yf2 + 0.006 * H, zc + fh / 2)),
        (fw + 0.026 * H, 0.016 * H, 0.016 * H, (0, yf2 + 0.006 * H, zc - fh / 2)),
        (0.016 * H, 0.016 * H, fh, (-fw / 2, yf2 + 0.006 * H, zc)),
        (0.016 * H, 0.016 * H, fh, (fw / 2, yf2 + 0.006 * H, zc)),
    ]:
        obs.append(rounded_box("niche_frame", (sx, sy, sz), loc, brass, bevel=0.004))
    obs.append(rounded_box("niche_back", (fw, 0.008 * H, fh), (0, yf2 + 0.035 * H, zc),
                           bronze, bevel=0.004))
    obs.append(rounded_box("lamp_house", (0.05 * H, 0.03 * H, 0.06 * H),
                           (0, yf2 + 0.02 * H, zc), brass, bevel=0.003))
    rings = []
    n = 18
    for dz, r in [(0.0, 0.013 * H), (0.02 * H, 0.011 * H), (0.045 * H, 0.0078 * H),
                  (0.065 * H, 0.0048 * H), (0.085 * H, 0.0026 * H), (0.10 * H, 0.0007 * H)]:
        ring = [Vector((r * math.cos(math.tau * j / n),
                        yf2 + 0.014 * H + r * math.sin(math.tau * j / n) * 0.5,
                        zc - 0.045 * H + dz)) for j in range(n)]
        rings.append(ring)
    obs.append(loft("FLAME", rings, mat_flame()))
    bpy.ops.object.light_add(type="POINT", location=(0, yf2 + 0.025 * H, zc))
    lt = bpy.context.object
    lt.name = "flame_light"
    lt.data.energy = 14.0
    lt.data.color = (1.0, 0.42, 0.1)
    lt.data.shadow_soft_size = 0.03 * H
    return obs


def build_garment_details(h, jacket, bronze, brass, H):
    parts = []

    def put(name, size, x, zf, mat, y_extra=0.0, rot=(0, 0, 0), bevel=0.004):
        z = zf * H
        y = front_y_obj(jacket, x, z)
        if y is None:
            y = -0.115 * H
        loc = (x, y - size[1] * 0.30 + y_extra, z)
        return rounded_box(name, size, loc, mat, bevel=bevel, rot=rot)

    # jacket collar: sits on the shoulder ring, tight around the neck
    zc = 0.842 * H
    n = 48
    rings = [superellipse(r, r * 0.90, n=n, p=2.0, z=zc + dz) for dz, r in [
        (-0.026 * H, 0.050 * H), (-0.012 * H, 0.056 * H), (0.0, 0.054 * H)]]
    parts.append(loft("collar", rings, bronze, caps=False))
    # lapels
    for s in (-1, 1):
        parts.append(put("lapel", (0.040 * H, 0.020 * H, 0.135 * H), s * 0.033 * H, 0.775,
                         bronze, rot=(math.radians(6), 0, math.radians(s * 10))))
    # placket
    parts.append(put("placket", (0.020 * H, 0.014 * H, 0.235 * H), 0, 0.685, bronze))
    # buttons
    for zf in (0.615, 0.655, 0.695, 0.735, 0.775):
        z = zf * H
        y = front_y_obj(jacket, 0.0, z)
        if y is None:
            y = -0.115 * H
        bpy.ops.mesh.primitive_cylinder_add(radius=0.010 * H, depth=0.007 * H, vertices=16,
                                            location=(0.004 * H, y - 0.004 * H, z),
                                            rotation=(math.radians(90), 0, 0))
        b = bpy.context.object
        b.data.materials.append(brass)
        parts.append(b)
    # chest patch pockets with flaps
    for s in (-1, 1):
        parts.append(put("pocket", (0.058 * H, 0.016 * H, 0.050 * H), s * 0.062 * H, 0.655,
                         bronze, rot=(0, 0, math.radians(s * 4))))
        parts.append(put("pocket_flap", (0.062 * H, 0.018 * H, 0.016 * H), s * 0.062 * H, 0.685,
                         bronze, rot=(math.radians(-12), 0, 0)))
    # belt: a band around the jacket
    zb = 0.585 * H
    band = [superellipse(rx, ry, n=48, p=2.2, z=zb + dz) for dz, rx, ry in [
        (-0.018 * H, 0.118 * H, 0.095 * H), (0.006 * H, 0.122 * H, 0.098 * H)]]
    parts.append(loft("belt", band, bronze, caps=False))
    ye = front_y_obj(jacket, 0, zb) or -0.11 * H
    parts.append(rounded_box("buckle", (0.030 * H, 0.012 * H, 0.026 * H),
                             (0, ye - 0.006 * H, zb), brass, bevel=0.003))
    # jacket hem band
    hem = [superellipse(rx, ry, n=48, p=2.2, z=0.545 * H + dz) for dz, rx, ry in [
        (-0.014 * H, 0.132 * H, 0.104 * H), (0.0, 0.140 * H, 0.110 * H)]]
    parts.append(loft("hem", hem, bronze, caps=False))
    return join(parts, "MINER_DETAILS")


def make_text(name, body, loc, size, mat):
    curve = bpy.data.curves.new(name, "FONT")
    curve.body = body
    curve.align_x = "CENTER"
    curve.align_y = "CENTER"
    curve.size = size
    curve.extrude = 0.006
    try:
        curve.font = bpy.data.fonts.load(r"C:\Windows\Fonts\arialbd.ttf", check_existing=True)
    except Exception:
        pass
    ob = bpy.data.objects.new(name, curve)
    link(ob)
    ob.location = loc
    ob.rotation_euler = (math.radians(90), 0, 0)
    ob.data.materials.append(mat)
    bpy.context.view_layer.objects.active = ob
    bpy.ops.object.select_all(action="DESELECT")
    ob.select_set(True)
    bpy.ops.object.convert(target="MESH")
    return bpy.context.object


def build_pedestal(granite, brass):
    obs = []
    obs.append(rounded_box("PED_STEP1", (4.9, 4.9, 0.26), (0, 0, 0.13), granite, bevel=0.03))
    obs.append(rounded_box("PED_STEP2", (4.3, 4.3, 0.26), (0, 0, 0.39), granite, bevel=0.03))
    obs.append(rounded_box("PED_BLOCK", (3.15, 3.15, 2.48), (0, 0, 1.76), granite, bevel=0.02))
    obs.append(rounded_box("PED_CAP", (3.46, 3.46, 0.30), (0, 0, 3.15), granite, bevel=0.02))
    obs.append(rounded_box("PED_PLAQUE", (1.95, 0.06, 1.34), (0, -1.60, 1.95), brass, bevel=0.015))
    for k, (line, z, size) in enumerate([
            ("ПАМЯТЬ", 2.30, 0.175), ("ШАХТЁРАМ", 2.05, 0.175), ("КУЗБАССА", 1.80, 0.175)]):
        obs.append(make_text("plaque_text_%d" % k, line, (0, -1.648, z), size, brass))
    obs.append(make_text("plaque_sub", "1941 — 2003", (0, -1.648, 1.56), 0.10, brass))
    return join(obs, "MINER_PEDESTAL")


# --------------------------------------------------------------------------- studio
def setup_studio():
    scn = bpy.context.scene
    bpy.ops.mesh.primitive_plane_add(size=100, location=(0, 0, 0))
    ground = bpy.context.object
    ground.name = "STUDIO_GROUND"
    gm = bpy.data.materials.new("STUDIO_GROUND")
    gm.use_nodes = True
    bsdf = next(n for n in gm.node_tree.nodes if n.type == "BSDF_PRINCIPLED")
    bsdf.inputs["Base Color"].default_value = (0.03, 0.031, 0.035, 1)
    bsdf.inputs["Roughness"].default_value = 0.5
    ground.data.materials.append(gm)
    world = bpy.data.worlds.new("STUDIO_WORLD")
    scn.world = world
    world.use_nodes = True
    bg = next(n for n in world.node_tree.nodes if n.type == "BACKGROUND")
    bg.inputs["Color"].default_value = (0.014, 0.017, 0.022, 1)

    def area(name, loc, energy, color, size, rot):
        bpy.ops.object.light_add(type="AREA", location=loc)
        o = bpy.context.object
        o.name = name
        o.data.energy = energy
        o.data.color = color
        o.data.size = size
        o.rotation_euler = rot

    area("KEY", (-7.0, -9.0, 11.0), 5200, (1.0, 0.94, 0.86), 5.0,
         (math.radians(50), 0, math.radians(-36)))
    area("FILL", (8.0, -6.5, 5.5), 1500, (0.62, 0.74, 1.0), 8.0,
         (math.radians(78), 0, math.radians(52)))
    area("RIM", (2.0, 9.5, 10.0), 3600, (1.0, 0.78, 0.55), 4.0,
         (math.radians(118), 0, math.radians(176)))
    area("TOP", (0, -0.5, 16.0), 900, (0.82, 0.88, 1.0), 6.0, (0, 0, 0))


def add_camera(name, loc, target, lens=55, ortho=None):
    cd = bpy.data.cameras.new(name)
    cam = bpy.data.objects.new(name, cd)
    link(cam)
    cam.matrix_world = Matrix.Translation(Vector(loc)) @ \
        (Vector(target) - Vector(loc)).to_track_quat("-Z", "Y").to_matrix().to_4x4()
    if ortho:
        cd.type = "ORTHO"
        cd.ortho_scale = ortho
    else:
        cd.lens = lens
    cd.clip_start = 0.1
    cd.clip_end = 400
    return cam


def render_view(cam, out_path, res, samples=40):
    scn = bpy.context.scene
    scn.camera = cam
    scn.render.resolution_x, scn.render.resolution_y = res
    scn.render.resolution_percentage = 100
    scn.eevee.taa_render_samples = samples
    scn.render.image_settings.file_format = "PNG"
    scn.render.filepath = out_path
    bpy.ops.render.render(write_still=True)
    print("RENDERED", out_path)


def main():
    scn = reset()
    try:
        bpy.ops.preferences.addon_enable(module="bl_ext.blender_org.mpfb")
        print("MPFB ENABLED")
    except Exception as exc:
        print("MPFB ENABLE FAIL", exc)
    print("MPFB DATA", os.path.isdir(MPFB_DATA))
    granite = mat_granite()
    brass = mat_brass()

    fig_root, human = build_figure()
    pedestal = build_pedestal(granite, brass)

    setup_studio()
    tgt = (0, 0, 5.3)
    add_camera("CAM_ASSET_FRONT", (0, -30, 5.3), tgt, ortho=11.6)
    add_camera("CAM_ASSET_SIDE", (30, 0, 5.3), tgt, ortho=11.6)
    add_camera("CAM_ASSET_BACK", (0, 30, 5.3), tgt, ortho=11.6)
    add_camera("CAM_ASSET_34", (11.5, -14.5, 8.4), (0, 0, 6.0), lens=62)
    add_camera("CAM_ASSET_HEAD", (2.0, -4.2, 10.3), (0, -0.02, 10.05), lens=110)
    add_camera("CAM_ASSET_CHEST", (1.4, -3.7, 8.55), (0, -0.5, 8.42), lens=110)

    views = [
        ("CAM_ASSET_FRONT", "front.png", (900, 1300)),
        ("CAM_ASSET_SIDE", "side.png", (900, 1300)),
        ("CAM_ASSET_BACK", "back.png", (900, 1300)),
        ("CAM_ASSET_34", "three_quarter.png", (1200, 1000)),
        ("CAM_ASSET_HEAD", "head_detail.png", (1100, 1100)),
        ("CAM_ASSET_CHEST", "chest_detail.png", (1100, 1100)),
    ]
    if QUICK:
        views = [v for v in views if v[0] in ("CAM_ASSET_FRONT", "CAM_ASSET_34",
                                             "CAM_ASSET_HEAD", "CAM_ASSET_CHEST")]
    for cam, fn, res in views:
        render_view(bpy.data.objects[cam], os.path.join(RENDER_DIR, fn), res)

    blend = os.path.join(OUT_DIR, "miner_monument_%s.blend" % VERSION)
    bpy.ops.wm.save_as_mainfile(filepath=blend)
    tris = sum(len(o.data.polygons) for o in bpy.data.objects if o.type == "MESH")
    print("SAVED", blend)
    print("STATS objects=%d tris=%d" % (len(bpy.data.objects), tris))


if __name__ == "__main__":
    main()
