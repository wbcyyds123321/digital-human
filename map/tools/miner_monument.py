"""Kuzbass miners monument — standalone hero asset (v4).

Bronze miner memorial: 7.5 m figure on a 3.3 m granite pedestal, chest lamp
niche with a burning flame. Studio review renders + saved asset blend.

Run: blender.exe -b --factory-startup --python tools/miner_monument.py -- --version v4 --quick
"""
import math
import os
import sys

import bpy
from mathutils import Vector, Matrix

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
VERSION = "v4"
for i, a in enumerate(sys.argv):
    if a == "--version" and i + 1 < len(sys.argv):
        VERSION = sys.argv[i + 1]
OUT_DIR = os.path.join(ROOT, "assets", "miner_monument")
RENDER_DIR = os.path.join(ROOT, "renders", "lm_miner", VERSION)
os.makedirs(OUT_DIR, exist_ok=True)
os.makedirs(RENDER_DIR, exist_ok=True)

TAU = math.tau
BASE_Z = 3.30          # pedestal height
QUICK = "--quick" in sys.argv


# --------------------------------------------------------------------------- generic
def reset():
    bpy.ops.wm.read_factory_settings(use_empty=True)
    scn = bpy.context.scene
    scn.unit_settings.system = "METRIC"
    scn.render.engine = "BLENDER_EEVEE"
    scn.eevee.taa_render_samples = 48
    scn.view_settings.view_transform = "AgX"
    scn.view_settings.look = "AgX - Medium High Contrast"
    scn.view_settings.exposure = 0.5
    return scn


def new_mesh(name, verts, faces, mat=None, smooth=True):
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


def superellipse(rx, ry, n=40, p=2.1, cx=0.0, cy=0.0, z=0.0):
    pts = []
    for i in range(n):
        a = TAU * i / n
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
    return new_mesh(name, verts, faces, mat, smooth)


def tube(name, path, radii, mat=None, n=24, smooth=True):
    rings = []
    for i, p in enumerate(path):
        if i == 0:
            d = Vector(path[1]) - Vector(path[0])
        elif i == len(path) - 1:
            d = Vector(path[-1]) - Vector(path[-2])
        else:
            d = Vector(path[i + 1]) - Vector(path[i - 1])
        d.normalize()
        ref = Vector((0, 1, 0)) if abs(d.z) > 0.9 else Vector((0, 0, 1))
        side = d.cross(ref).normalized()
        fwd = side.cross(d).normalized()
        ring = [Vector(p) + side * (math.cos(TAU * j / n) * radii[i])
                + fwd * (math.sin(TAU * j / n) * radii[i]) for j in range(n)]
        rings.append(ring)
    return loft(name, rings, mat, smooth=smooth)


def rounded_box(name, size, loc, mat=None, bevel=0.04, seg=3, rot=(0, 0, 0)):
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


def apply_all(ob):
    bpy.context.view_layer.objects.active = ob
    bpy.ops.object.select_all(action="DESELECT")
    ob.select_set(True)
    for m in list(ob.modifiers):
        try:
            bpy.ops.object.modifier_apply(modifier=m.name)
        except Exception as exc:
            print("MODIFIER_FAIL", ob.name, m.name, exc)
            ob.modifiers.remove(m)


def remesh(ob, voxel):
    m = ob.modifiers.new("remesh", "REMESH")
    m.mode = "VOXEL"
    m.voxel_size = voxel
    apply_all(ob)


def smooth_mesh(ob, iters=4, factor=0.4):
    sm = ob.modifiers.new("sm", "SMOOTH")
    sm.factor = factor
    sm.iterations = iters
    apply_all(ob)


def displace(ob, strength, scale, name):
    tex = bpy.data.textures.get(name)
    if tex is None:
        tex = bpy.data.textures.new(name, "CLOUDS")
        tex.noise_scale = scale
        tex.noise_depth = 3
    dm = ob.modifiers.new("disp", "DISPLACE")
    dm.texture = tex
    dm.strength = strength
    dm.mid_level = 0.5
    apply_all(ob)


def smooth_shade(ob):
    for p in ob.data.polygons:
        p.use_smooth = True


def front_surface_y(body, x, z, start_y=-3.0):
    """Raycast from the front onto the body; returns the front surface y."""
    bpy.context.view_layer.update()
    mw = body.matrix_world
    origin = mw.inverted() @ Vector((x, start_y, z))
    d = (mw.inverted().to_3x3() @ Vector((0, 1, 0))).normalized()
    hit, loc, normal, idx = body.ray_cast(origin, d)
    if hit:
        return (mw @ loc).y
    return None


# --------------------------------------------------------------------------- materials
def mat_bronze():
    m = bpy.data.materials.new("MINER_BRONZE")
    m.use_nodes = True
    nt = m.node_tree
    bsdf = next(n for n in nt.nodes if n.type == "BSDF_PRINCIPLED")
    bsdf.inputs["Metallic"].default_value = 0.42
    noise = nt.nodes.new("ShaderNodeTexNoise")
    noise.inputs["Scale"].default_value = 4.0
    noise.inputs["Detail"].default_value = 9.0
    noise.inputs["Roughness"].default_value = 0.55
    ramp = nt.nodes.new("ShaderNodeValToRGB")
    ramp.color_ramp.elements[0].position = 0.45
    ramp.color_ramp.elements[0].color = (0.055, 0.040, 0.026, 1)
    ramp.color_ramp.elements[1].position = 0.85
    ramp.color_ramp.elements[1].color = (0.105, 0.165, 0.130, 1)
    nt.links.new(noise.outputs["Fac"], ramp.inputs["Fac"])
    nt.links.new(ramp.outputs["Color"], bsdf.inputs["Base Color"])
    n2 = nt.nodes.new("ShaderNodeTexNoise")
    n2.inputs["Scale"].default_value = 24.0
    n2.inputs["Detail"].default_value = 6.0
    rmap = nt.nodes.new("ShaderNodeMapRange")
    rmap.inputs["To Min"].default_value = 0.50
    rmap.inputs["To Max"].default_value = 0.74
    nt.links.new(n2.outputs["Fac"], rmap.inputs["Value"])
    nt.links.new(rmap.outputs["Result"], bsdf.inputs["Roughness"])
    bump = nt.nodes.new("ShaderNodeBump")
    bump.inputs["Strength"].default_value = 0.30
    bump.inputs["Distance"].default_value = 0.02
    nt.links.new(n2.outputs["Fac"], bump.inputs["Height"])
    nt.links.new(bump.outputs["Normal"], bsdf.inputs["Normal"])
    return m


def mat_granite():
    m = bpy.data.materials.new("MINER_GRANITE")
    m.use_nodes = True
    nt = m.node_tree
    bsdf = next(n for n in nt.nodes if n.type == "BSDF_PRINCIPLED")
    bsdf.inputs["Roughness"].default_value = 0.40
    n = nt.nodes.new("ShaderNodeTexNoise")
    n.inputs["Scale"].default_value = 35.0
    n.inputs["Detail"].default_value = 8.0
    ramp = nt.nodes.new("ShaderNodeValToRGB")
    ramp.color_ramp.elements[0].color = (0.020, 0.020, 0.024, 1)
    ramp.color_ramp.elements[1].color = (0.062, 0.062, 0.068, 1)
    nt.links.new(n.outputs["Fac"], ramp.inputs["Fac"])
    nt.links.new(ramp.outputs["Color"], bsdf.inputs["Base Color"])
    bump = nt.nodes.new("ShaderNodeBump")
    bump.inputs["Strength"].default_value = 0.15
    bump.inputs["Distance"].default_value = 0.008
    nt.links.new(n.outputs["Fac"], bump.inputs["Height"])
    nt.links.new(bump.outputs["Normal"], bsdf.inputs["Normal"])
    return m


def mat_brass():
    m = bpy.data.materials.new("MINER_BRASS")
    m.use_nodes = True
    bsdf = next(n for n in m.node_tree.nodes if n.type == "BSDF_PRINCIPLED")
    bsdf.inputs["Base Color"].default_value = (0.30, 0.205, 0.070, 1)
    bsdf.inputs["Metallic"].default_value = 1.0
    bsdf.inputs["Roughness"].default_value = 0.36
    return m


def mat_flame():
    m = bpy.data.materials.new("MINER_FLAME")
    m.use_nodes = True
    nt = m.node_tree
    out = next(n for n in nt.nodes if n.type == "OUTPUT_MATERIAL")
    em = nt.nodes.new("ShaderNodeEmission")
    em.inputs["Color"].default_value = (1.0, 0.36, 0.045, 1)
    em.inputs["Strength"].default_value = 30.0
    nt.links.new(em.outputs["Emission"], out.inputs["Surface"])
    return m


def mat_glass():
    m = bpy.data.materials.new("MINER_LAMP_GLASS")
    m.use_nodes = True
    bsdf = next(n for n in m.node_tree.nodes if n.type == "BSDF_PRINCIPLED")
    bsdf.inputs["Base Color"].default_value = (0.08, 0.12, 0.09, 1)
    bsdf.inputs["Roughness"].default_value = 0.12
    bsdf.inputs["Metallic"].default_value = 0.0
    return m


# --------------------------------------------------------------------------- body
def build_body(mat):
    parts = []
    prof = [
        (3.16, 0.84, 0.50), (3.30, 0.86, 0.52), (3.50, 0.84, 0.52),
        (3.80, 0.79, 0.50), (4.10, 0.80, 0.51), (4.40, 0.85, 0.53),
        (4.70, 0.90, 0.56), (5.00, 0.95, 0.58), (5.30, 0.98, 0.59),
        (5.55, 0.96, 0.58), (5.70, 0.90, 0.55), (5.82, 0.78, 0.48),
        (5.90, 0.52, 0.35),
    ]
    rings = [superellipse(rx, ry, n=48, p=2.15, z=z) for z, rx, ry in prof]
    parts.append(loft("torso", rings, mat))
    parts.append(tube("neck", [(0, 0.02, 5.90), (0, 0.0, 6.15), (0, 0.0, 6.34)],
                      [0.26, 0.235, 0.225], mat, n=24))
    for s in (-1, 1):
        arm_path = [(s * 0.52, -0.02, 5.66), (s * 0.86, 0.0, 5.56), (s * 1.02, 0.0, 5.15),
                    (s * 1.10, 0.0, 4.65), (s * 1.15, -0.01, 4.10), (s * 1.17, -0.02, 3.55),
                    (s * 1.18, -0.02, 3.15)]
        parts.append(tube("arm", arm_path, [0.27, 0.245, 0.225, 0.21, 0.20, 0.19, 0.18], mat, n=24))
        leg_path = [(s * 0.26, 0.0, 3.55), (s * 0.40, 0.0, 3.20), (s * 0.45, 0.0, 2.70),
                    (s * 0.48, 0.0, 2.20), (s * 0.50, 0.0, 1.70), (s * 0.52, 0.0, 1.20),
                    (s * 0.53, 0.0, 0.85)]
        parts.append(tube("leg", leg_path, [0.40, 0.36, 0.32, 0.29, 0.27, 0.255, 0.25], mat, n=24))
        parts.append(sphere("foot", (s * 0.55, -0.16, 0.42), (0.28, 0.50, 0.32), mat))
    body = join(parts, "MINER_BODY")
    remesh(body, 0.026)
    smooth_mesh(body, iters=4, factor=0.45)
    displace(body, 0.012, 1.7, "cloth_folds")
    displace(body, 0.004, 7.0, "cast_grain")
    smooth_shade(body)
    print("BODY", len(body.data.polygons))
    return body


def build_head(mat):
    n = 40
    prof = [
        (6.28, 0.24, 0.235, 0.02, 2.1), (6.38, 0.29, 0.27, 0.0, 2.1),
        (6.48, 0.335, 0.31, -0.02, 2.05), (6.60, 0.36, 0.345, -0.03, 2.0),
        (6.72, 0.375, 0.365, -0.028, 2.0), (6.84, 0.38, 0.38, -0.02, 2.0),
        (6.96, 0.38, 0.385, -0.005, 2.0), (7.08, 0.37, 0.38, 0.01, 2.0),
        (7.20, 0.335, 0.355, 0.02, 2.0), (7.30, 0.25, 0.28, 0.025, 2.0),
        (7.38, 0.11, 0.14, 0.03, 2.0),
    ]
    rings = [superellipse(rx, ry, n=n, p=p, cy=cy, z=z) for z, rx, ry, cy, p in prof]
    skull = loft("MINER_HEAD", rings, mat)
    parts = [skull]
    # nose wedge (crisp, half-sunk)
    nose_rings = []
    for z, w, d, y in [(6.99, 0.055, 0.09, -0.36), (6.92, 0.075, 0.13, -0.40),
                       (6.82, 0.095, 0.17, -0.44), (6.74, 0.105, 0.19, -0.46),
                       (6.69, 0.115, 0.15, -0.43)]:
        ring = [Vector((w * math.cos(TAU * j / 10), y + d * math.sin(TAU * j / 10),
                        z + 0.02 * math.sin(TAU * j / 10))) for j in range(10)]
        nose_rings.append(ring)
    parts.append(loft("nose", nose_rings, mat))
    # brow ridge
    for s in (-1, 1):
        parts.append(rounded_box("brow", (0.22, 0.12, 0.085), (s * 0.15, -0.335, 6.955),
                                 mat, bevel=0.03, rot=(math.radians(-8), math.radians(s * 6), 0)))
    # deep-set eye sockets: recessed dark plates under a heavy brow (no beady eyeballs)
    for s in (-1, 1):
        sock = rounded_box("socket", (0.175, 0.06, 0.10), (s * 0.15, -0.318, 6.865),
                           mat, bevel=0.03, rot=(math.radians(-18), 0, 0))
        parts.append(sock)
        parts.append(rounded_box("lid", (0.19, 0.075, 0.05), (s * 0.15, -0.345, 6.925),
                                 mat, bevel=0.02, rot=(math.radians(-22), 0, 0)))
    # cheekbones
    for s in (-1, 1):
        parts.append(sphere("cheek", (s * 0.225, -0.30, 6.79), (0.10, 0.09, 0.105), mat))
        parts.append(sphere("jaw", (s * 0.27, -0.14, 6.53), (0.10, 0.12, 0.12), mat))
    # lips
    parts.append(rounded_box("lip_up", (0.165, 0.055, 0.03), (0, -0.405, 6.615), mat, bevel=0.012))
    parts.append(rounded_box("lip_low", (0.145, 0.06, 0.035), (0, -0.40, 6.575), mat, bevel=0.012))
    parts.append(sphere("chin", (0, -0.37, 6.50), (0.12, 0.10, 0.10), mat))
    for s in (-1, 1):
        parts.append(sphere("ear", (s * 0.365, 0.02, 6.80), (0.04, 0.085, 0.145), mat))
    ob = join(parts, "MINER_HEAD")
    smooth_shade(ob)
    return ob


def build_helmet(mat, brass, glass):
    n = 44
    rings = [superellipse(r, r, n=n, p=2.0, z=z) for z, r in [
        (6.99, 0.44), (7.05, 0.465), (7.14, 0.455), (7.24, 0.42),
        (7.34, 0.345), (7.43, 0.235), (7.50, 0.10)]]
    dome = loft("MINER_HELMET", rings, mat)
    # small brim all around, slightly larger at the front
    brim_rings = []
    for z, rx, ry, cy in [(7.00, 0.44, 0.46, -0.02), (7.005, 0.52, 0.56, -0.05),
                          (7.04, 0.53, 0.57, -0.05), (7.045, 0.47, 0.50, -0.02)]:
        brim_rings.append(superellipse(rx, ry, n=n, p=2.0, cy=cy, z=z))
    brim = loft("brim", brim_rings, mat, caps=False)
    lamp = rounded_box("lamp_body", (0.26, 0.18, 0.22), (0, -0.46, 7.14), brass, bevel=0.04)
    cyl = bpy.ops.mesh.primitive_cylinder_add(radius=0.085, depth=0.04, vertices=20,
                                              location=(0, -0.555, 7.14),
                                              rotation=(math.radians(90), 0, 0))
    lens = bpy.context.object
    lens.name = "lamp_lens"
    lens.data.materials.append(glass)
    ob = join([dome, brim, lamp, lens], "MINER_HELMET")
    smooth_shade(ob)
    return ob


def build_hands(mat):
    parts = []
    for s in (-1, 1):
        x = s * 1.18
        # mitten hand: palm overlapping the sleeve cuff, fingers as a grooved block
        parts.append(rounded_box("palm", (0.23, 0.30, 0.52), (x, -0.02, 2.98), mat, bevel=0.07))
        parts.append(rounded_box("fingers", (0.21, 0.22, 0.30), (x, -0.05, 2.62), mat, bevel=0.05))
        for k in (-1, 1):
            parts.append(rounded_box("finger_ridge", (0.075, 0.20, 0.26),
                                     (x + k * 0.055, -0.055, 2.62), mat, bevel=0.03))
        parts.append(rounded_box("thumb", (0.09, 0.14, 0.26), (x - s * 0.13, -0.09, 2.88),
                                 mat, bevel=0.035, rot=(math.radians(12), 0, math.radians(s * 18))))
    ob = join(parts, "MINER_HANDS")
    smooth_shade(ob)
    return ob


def build_boots(mat):
    parts = []
    for s in (-1, 1):
        x = s * 0.54
        rings = [superellipse(rx, ry, n=32, p=2.3, cx=x, cy=cy, z=z) for z, rx, ry, cy in [
            (0.12, 0.285, 0.56, -0.18), (0.18, 0.295, 0.58, -0.19),
            (0.34, 0.29, 0.48, -0.13), (0.55, 0.28, 0.40, -0.06),
            (0.78, 0.27, 0.35, -0.02), (0.95, 0.26, 0.32, 0.0)]]
        parts.append(loft("boot", rings, mat))
        parts.append(rounded_box("sole", (0.62, 1.26, 0.11), (x, -0.22, 0.055), mat, bevel=0.04))
        parts.append(rounded_box("heel", (0.54, 0.30, 0.10), (x, 0.27, 0.05), mat, bevel=0.03))
        parts.append(rounded_box("toe", (0.48, 0.30, 0.15), (x, -0.62, 0.20), mat,
                                 bevel=0.055, rot=(math.radians(-10), 0, 0)))
    ob = join(parts, "MINER_BOOTS")
    smooth_shade(ob)
    return ob


def build_jacket(mat, brass, body):
    """Garment details snapped to the fused body surface."""
    parts = []
    # collar around the neck
    n = 36
    rings = [superellipse(r, r * 0.95, n=n, p=2.0, z=z) for z, r in [
        (6.02, 0.315), (6.09, 0.375), (6.12, 0.405)]]
    parts.append(loft("collar", rings, mat, caps=False))
    # lapels: sit on chest, y from surface raycast
    for s in (-1, 1):
        y = front_surface_y(body, s * 0.24, 5.55) or -0.56
        parts.append(rounded_box("lapel", (0.22, 0.10, 0.85),
                                 (s * 0.24, y + 0.045, 5.55), mat, bevel=0.03,
                                 rot=(math.radians(5), 0, math.radians(s * 13))))
    # placket + buttons
    ymid = front_surface_y(body, 0.0, 4.55) or -0.56
    parts.append(rounded_box("placket", (0.10, 0.05, 2.10), (0, ymid + 0.02, 4.55), mat, bevel=0.02))
    for z in (3.98, 4.36, 4.74, 5.12):
        y = front_surface_y(body, 0.02, z) or ymid
        bpy.ops.mesh.primitive_cylinder_add(radius=0.05, depth=0.03, vertices=16,
                                            location=(0.02, y - 0.005, z),
                                            rotation=(math.radians(90), 0, 0))
        b = bpy.context.object
        b.name = "button"
        b.data.materials.append(brass)
        parts.append(b)
    # chest pockets well below the niche
    for s in (-1, 1):
        y = front_surface_y(body, s * 0.52, 4.35) or -0.56
        parts.append(rounded_box("pocket", (0.32, 0.05, 0.30), (s * 0.52, y + 0.02, 4.35),
                                 mat, bevel=0.015))
        parts.append(rounded_box("pocket_flap", (0.34, 0.06, 0.09), (s * 0.52, y - 0.005, 4.52),
                                 mat, bevel=0.015, rot=(math.radians(-12), 0, 0)))
    # hip pockets
    for s in (-1, 1):
        y = front_surface_y(body, s * 0.80, 3.85) or -0.50
        parts.append(rounded_box("hip_pocket", (0.09, 0.30, 0.28), (s * 0.80, y + 0.10, 3.85),
                                 mat, bevel=0.02))
    # belt
    yb = front_surface_y(body, 0.0, 3.62) or -0.52
    parts.append(rounded_box("belt", (1.74, max(0.9, abs(yb) * 2 + 0.1), 0.12),
                             (0, 0, 3.62), mat, bevel=0.04))
    parts.append(rounded_box("buckle", (0.18, 0.05, 0.13), (0, yb - 0.01, 3.62), brass, bevel=0.02))
    # cuffs at the wrists
    for s in (-1, 1):
        rings = [superellipse(0.215, 0.215, n=20, p=2.0, cx=s * 1.20, cy=-0.02, z=z)
                 for z in (3.26, 3.18, 3.12)]
        parts.append(loft("cuff", rings, mat, caps=False))
    # jacket hem edge
    hem = [superellipse(rx, ry, n=44, p=2.15, z=z) for z, rx, ry in [
        (3.12, 0.85, 0.52), (3.17, 0.88, 0.54), (3.22, 0.85, 0.52)]]
    parts.append(loft("hem", hem, mat, caps=False))
    ob = join(parts, "MINER_JACKET")
    smooth_shade(ob)
    return ob


def build_chest_niche(mat, brass, glass, flame_mat, body):
    yfront = front_surface_y(body, 0.0, 5.15) or -0.58
    cx, cz = 0.0, 5.15
    bpy.ops.mesh.primitive_cube_add(size=1, location=(cx, yfront + 0.22, cz))
    cutter = bpy.context.object
    cutter.scale = (0.60, 0.62, 0.92)
    bpy.ops.object.transform_apply(location=False, rotation=False, scale=True)
    boo = body.modifiers.new("niche", "BOOLEAN")
    boo.operation = "DIFFERENCE"
    boo.object = cutter
    boo.solver = "EXACT"
    apply_all(body)
    bpy.data.objects.remove(cutter, do_unlink=True)
    yfront = front_surface_y(body, 0.0, 5.15) or yfront

    obs = []
    fw, fh = 0.64, 0.86
    for sx, sy, sz, loc in [
        (fw + 0.12, 0.10, 0.09, (cx, yfront + 0.03, cz + fh / 2 + 0.04)),
        (fw + 0.12, 0.10, 0.09, (cx, yfront + 0.03, cz - fh / 2 - 0.04)),
        (0.09, 0.10, fh + 0.18, (cx - fw / 2 - 0.045, yfront + 0.03, cz)),
        (0.09, 0.10, fh + 0.18, (cx + fw / 2 + 0.045, yfront + 0.03, cz)),
    ]:
        obs.append(rounded_box("niche_frame", (sx, sy, sz), loc, brass, bevel=0.015))
    obs.append(rounded_box("niche_back", (fw, 0.05, fh), (cx, yfront + 0.30, cz), mat, bevel=0.015))
    obs.append(rounded_box("lamp_house", (0.32, 0.22, 0.44), (cx, yfront + 0.16, cz), brass,
                           bevel=0.015))
    obs.append(rounded_box("lamp_glass", (0.24, 0.04, 0.34), (cx, yfront + 0.035, cz), glass,
                           bevel=0.01))
    rings = []
    n = 18
    for z, r, ox, oy in [(0.00, 0.08, 0.0, 0.0), (0.06, 0.072, 0.010, 0.0),
                         (0.14, 0.054, -0.014, 0.007), (0.22, 0.034, 0.010, -0.005),
                         (0.30, 0.016, -0.005, 0.0), (0.37, 0.004, 0.0, 0.0)]:
        ring = [Vector((cx + ox + r * math.cos(TAU * j / n),
                        yfront + 0.07 + oy + r * math.sin(TAU * j / n) * 0.55,
                        cz - 0.16 + z)) for j in range(n)]
        rings.append(ring)
    obs.append(loft("FLAME", rings, flame_mat))
    bpy.ops.object.light_add(type="POINT", location=(cx, yfront + 0.10, cz - 0.04))
    lt = bpy.context.object
    lt.name = "flame_light"
    lt.data.energy = 90.0
    lt.data.color = (1.0, 0.40, 0.08)
    lt.data.shadow_soft_size = 0.28
    return obs


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
    bpy.context.scene.collection.objects.link(ob)
    ob.location = loc
    ob.rotation_euler = (math.radians(90), 0, 0)
    ob.data.materials.append(mat)
    bpy.context.view_layer.objects.active = ob
    bpy.ops.object.select_all(action="DESELECT")
    ob.select_set(True)
    bpy.ops.object.convert(target="MESH")
    return bpy.context.object


def build_pedestal(mat, brass):
    obs = []
    obs.append(rounded_box("PED_STEP1", (4.9, 4.9, 0.26), (0, 0, 0.13), mat, bevel=0.05))
    obs.append(rounded_box("PED_STEP2", (4.3, 4.3, 0.26), (0, 0, 0.39), mat, bevel=0.05))
    # block from 0.52 to 3.00, cap 3.00..3.30 = figure base
    obs.append(rounded_box("PED_BLOCK", (3.15, 3.15, 2.48), (0, 0, 1.76), mat, bevel=0.03))
    obs.append(rounded_box("PED_CAP", (3.46, 3.46, 0.30), (0, 0, 3.15), mat, bevel=0.03))
    # bronze plaque with raised inscription
    obs.append(rounded_box("PED_PLAQUE", (1.95, 0.06, 1.34), (0, -1.60, 1.95), brass, bevel=0.02))
    for k, (line, z, size) in enumerate([
            ("ПАМЯТЬ", 2.30, 0.175), ("ШАХТЁРАМ", 2.05, 0.175), ("КУЗБАССА", 1.80, 0.175)]):
        obs.append(make_text("plaque_text_%d" % k, line, (0, -1.645, z), size, brass))
    obs.append(make_text("plaque_sub", "1941 — 2003", (0, -1.645, 1.56), 0.10, brass))
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
    bg.inputs["Color"].default_value = (0.016, 0.019, 0.024, 1)

    def area(name, loc, energy, color, size, rot):
        bpy.ops.object.light_add(type="AREA", location=loc)
        o = bpy.context.object
        o.name = name
        o.data.energy = energy
        o.data.color = color
        o.data.shape = "SQUARE"
        o.data.size = size
        o.rotation_euler = rot
        return o

    area("KEY", (-7.0, -9.0, 11.0), 5200, (1.0, 0.94, 0.86), 5.0,
         (math.radians(50), 0, math.radians(-36)))
    area("FILL", (8.0, -6.5, 5.5), 1400, (0.62, 0.74, 1.0), 8.0,
         (math.radians(78), 0, math.radians(52)))
    area("RIM", (2.0, 9.5, 10.0), 3600, (1.0, 0.78, 0.55), 4.0,
         (math.radians(118), 0, math.radians(176)))
    area("TOP", (0, -0.5, 16.0), 800, (0.82, 0.88, 1.0), 6.0, (0, 0, 0))
    return ground


def add_camera(name, loc, target, lens=55, ortho=None):
    cam_data = bpy.data.cameras.new(name)
    cam = bpy.data.objects.new(name, cam_data)
    bpy.context.scene.collection.objects.link(cam)
    cam.matrix_world = Matrix.Translation(Vector(loc)) @ \
        (Vector(target) - Vector(loc)).to_track_quat("-Z", "Y").to_matrix().to_4x4()
    if ortho:
        cam_data.type = "ORTHO"
        cam_data.ortho_scale = ortho
    else:
        cam_data.lens = lens
    cam_data.clip_start = 0.1
    cam_data.clip_end = 400
    return cam


def render_view(cam, out_path, res=(1000, 1300), samples=48):
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
    bronze = mat_bronze()
    granite = mat_granite()
    brass = mat_brass()
    glass = mat_glass()
    flame = mat_flame()

    body = build_body(bronze)
    head = build_head(bronze)
    helmet = build_helmet(bronze, brass, glass)
    hands = build_hands(bronze)
    boots = build_boots(bronze)
    jacket = build_jacket(bronze, brass, body)
    niche = build_chest_niche(bronze, brass, glass, flame, body)
    pedestal = build_pedestal(granite, brass)

    root = bpy.data.objects.new("MINER_MONUMENT_ROOT", None)
    bpy.context.scene.collection.objects.link(root)
    root.empty_display_size = 0.6
    figure_objs = [body, head, helmet, hands, boots, jacket] + \
                  [o for o in niche if o.type == "MESH"]
    for ob in figure_objs:
        ob.parent = root
        ob.location.z += BASE_Z
    for o in niche:
        if o.type == "LIGHT":
            o.parent = root
            o.location.z += BASE_Z
    # NOTE: the pedestal stays on the ground (no offset)

    setup_studio()
    tgt = (0.0, 0.0, 5.30)
    add_camera("CAM_ASSET_FRONT", (0, -30, 5.30), tgt, ortho=11.4)
    add_camera("CAM_ASSET_SIDE", (30, 0, 5.30), tgt, ortho=11.4)
    add_camera("CAM_ASSET_BACK", (0, 30, 5.30), tgt, ortho=11.4)
    add_camera("CAM_ASSET_34", (11.5, -14.5, 8.3), (0, 0, 5.9), lens=62)
    add_camera("CAM_ASSET_HEAD", (2.0, -4.3, 10.35), (0, -0.02, 10.10), lens=110)
    add_camera("CAM_ASSET_CHEST", (1.4, -3.7, 8.55), (0, -0.5, 8.45), lens=110)

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
