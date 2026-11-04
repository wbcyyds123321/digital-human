"""kit.py — Soviet Blue-Hour Microdistrict construction kit.

Shared by all module builders. Conventions:
  * Blender Z-up, meters. World coords everywhere.
  * A building rect is (x0, y0, x1, y1) with +Y = north.
  * FACES keys: PY(+Y), NY(-Y), PX(+X), NX(-X).
  * Facade walls are built as real openings (piers + spandrels) so windows are recessed.
  * Window glass uses GPT-generated textures (lit / dark / stairwell variants).
"""
from __future__ import annotations

import math
import os
import random

import bmesh
import bpy
from mathutils import Euler, Vector

KIT_DIR = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(KIT_DIR)
REF_DIR = os.path.join(ROOT, "ref")
MAP_DIR = os.path.join(ROOT, "map")
OUT_DIR = os.path.join(ROOT, "out")

FACES = {
    "PY": (Vector((1, 0, 0)), Vector((0, 0, 1)), Vector((0, 1, 0))),
    "NY": (Vector((1, 0, 0)), Vector((0, 0, 1)), Vector((0, -1, 0))),
    "PX": (Vector((0, 1, 0)), Vector((0, 0, 1)), Vector((1, 0, 0))),
    "NX": (Vector((0, 1, 0)), Vector((0, 0, 1)), Vector((-1, 0, 0))),
}

_IMAGE_CACHE: dict[str, str] = {}


def find_image(filename: str) -> str | None:
    if filename in _IMAGE_CACHE:
        return _IMAGE_CACHE[filename] or None
    if not os.path.isdir(REF_DIR):
        _IMAGE_CACHE[filename] = ""
        return None
    for root, _dirs, files in os.walk(REF_DIR):
        if filename in files:
            p = os.path.join(root, filename)
            _IMAGE_CACHE[filename] = p
            return p
    _IMAGE_CACHE[filename] = ""
    return None


# ----------------------------------------------------------------------------
# colour
# ----------------------------------------------------------------------------
def srgb(c):
    if isinstance(c, str):
        h = c.lstrip("#")
        c = tuple(int(h[i:i + 2], 16) / 255.0 for i in (0, 2, 4))
    r, g, b = c[:3]
    out = [v / 12.92 if v <= 0.04045 else ((v + 0.055) / 1.055) ** 2.4 for v in (r, g, b)]
    return (out[0], out[1], out[2], 1.0)


PAL = {
    "conc_mint": "#A7B2AC", "conc_blue": "#9EA9B3", "conc_grey": "#8D949A",
    "conc_white": "#B4B6B0", "conc_warmgrey": "#A39E96",
    "brick_silicate": "#AEACA2", "brick_red_faded": "#8A7970",
    "plinth": "#3C4044", "plinth_dark": "#2E3236",
    "frame_white": "#D5D3C8", "frame_grey": "#9B9C97",
    "metal_dark": "#24282C", "metal_paint": "#4C555A", "rust": "#6B4A38",
    "roof": "#43494E", "roof_dark": "#2E3337",
    "asphalt": "#252A30", "walk": "#565C64", "plaza": "#72777D",
    "grass": "#39433A", "dirt": "#4B4438", "snow_patch": "#8A8F8D",
    "lamp_warm": "#FFB067", "lamp_sodium": "#FF9A3C",
    "glass_dark": "#0A0F16", "poster_red": "#8C3229", "poster_blue": "#2E4A72",
    "poster_cream": "#C4BAA4", "mosaic": "#8C8370", "concrete_stain": "#5E5A52",
}

MATS: dict[str, bpy.types.Material] = {}


def _principled(mat):
    return mat.node_tree.nodes.get("Principled BSDF")


def mat_solid(name, color, rough=0.85, metallic=0.0, emission=None,
              emission_strength=0.0, alpha=1.0):
    if name in MATS:
        return MATS[name]
    mat = bpy.data.materials.new(name)
    mat.use_nodes = True
    b = _principled(mat)
    b.inputs["Base Color"].default_value = srgb(color)
    b.inputs["Roughness"].default_value = rough
    b.inputs["Metallic"].default_value = metallic
    if alpha < 1.0:
        b.inputs["Alpha"].default_value = alpha
        mat.blend_method = "BLEND"
    if emission is not None:
        b.inputs["Emission Color"].default_value = srgb(emission)
        b.inputs["Emission Strength"].default_value = emission_strength
    MATS[name] = mat
    return mat


def mat_texture(name, filename, rough=0.85, metallic=0.0, emission_strength=0.0,
                tint=None, fallback="#444444"):
    if name in MATS:
        return MATS[name]
    path = find_image(filename)
    if not path:
        return mat_solid(name + "_FB", fallback, rough, metallic)
    img = bpy.data.images.load(path, check_existing=True)
    img.colorspace_settings.name = "sRGB"
    mat = bpy.data.materials.new(name)
    mat.use_nodes = True
    nt = mat.node_tree
    nodes, links = nt.nodes, nt.links
    b = _principled(mat)
    tex = nodes.new("ShaderNodeTexImage")
    tex.image = img
    tex.location = (-460, 240)
    links.new(tex.outputs["Color"], b.inputs["Base Color"])
    b.inputs["Roughness"].default_value = rough
    b.inputs["Metallic"].default_value = metallic
    if emission_strength > 0:
        links.new(tex.outputs["Color"], b.inputs["Emission Color"])
        b.inputs["Emission Strength"].default_value = emission_strength
    if tint is not None:
        mix = nodes.new("ShaderNodeMix")
        mix.data_type = "RGBA"
        mix.blend_type = "MULTIPLY"
        mix.inputs["Factor"].default_value = 0.5
        mix.inputs[6].default_value = srgb(tint)
        links.new(tex.outputs["Color"], mix.inputs[7])
        links.new(mix.outputs[2], b.inputs["Base Color"])
    MATS[name] = mat
    return mat


# ----------------------------------------------------------------------------
# scene helpers
# ----------------------------------------------------------------------------
class RNG(random.Random):
    def chance(self, p):
        return self.random() < p

    def pick(self, seq):
        return seq[int(self.random() * len(seq))]

    def wpick(self, seq, weights):
        return self.choices(seq, weights=weights, k=1)[0]

    def jitter(self, amount):
        return self.uniform(-amount, amount)


def reset_scene():
    bpy.ops.wm.read_factory_settings(use_empty=True)
    scn = bpy.context.scene
    scn.unit_settings.system = "METRIC"
    scn.unit_settings.scale_length = 1.0


def collection(name, parent=None):
    coll = bpy.data.collections.get(name)
    if coll is None:
        coll = bpy.data.collections.new(name)
        (parent or bpy.context.scene.collection).children.link(coll)
    return coll


def group(name, collection_=None, parent=None, loc=(0, 0, 0)):
    if isinstance(collection_, bpy.types.Object):
        parent, collection_ = collection_, None
    if parent is not None and collection_ is None and parent.users_collection:
        collection_ = parent.users_collection[0]
    obj = bpy.data.objects.new(name, None)
    obj.empty_display_type = "PLAIN_AXES"
    obj.empty_display_size = 0.6
    (collection_ or bpy.context.scene.collection).objects.link(obj)
    obj.location = loc
    if parent is not None:
        obj.parent = parent
    return obj


def _coll_of(parent):
    if parent is not None and parent.users_collection:
        return parent.users_collection[0]
    return bpy.context.scene.collection


def _link(obj, parent):
    _coll_of(parent).objects.link(obj)
    if parent is not None:
        obj.parent = parent
    return obj


# ----------------------------------------------------------------------------
# mesh primitives
# ----------------------------------------------------------------------------
def _mesh_obj(name, verts, faces, mat, parent, uv=None):
    me = bpy.data.meshes.new(name)
    me.from_pydata(verts, [], faces)
    me.update()
    if uv is not None:
        layer = me.uv_layers.new(name="UVMap")
        for poly in me.polygons:
            for li in poly.loop_indices:
                layer.data[li].uv = uv[li]
    if mat is not None:
        me.materials.append(mat)
    obj = bpy.data.objects.new(name, me)
    _link(obj, parent)
    return obj


_BOX_FACES = [(0, 3, 2, 1), (4, 5, 6, 7), (0, 1, 5, 4), (1, 2, 6, 5),
              (2, 3, 7, 6), (3, 0, 4, 7)]


def _box_verts(cx, cy, cz, sx, sy, sz, rot=(0, 0, 0)):
    hx, hy, hz = sx / 2, sy / 2, sz / 2
    pts = [(-hx, -hy, -hz), (hx, -hy, -hz), (hx, hy, -hz), (-hx, hy, -hz),
           (-hx, -hy, hz), (hx, -hy, hz), (hx, hy, hz), (-hx, hy, hz)]
    m = Euler(rot, "XYZ").to_matrix()
    out = []
    for p in pts:
        v = m @ Vector(p)
        out.append((v.x + cx, v.y + cy, v.z + cz))
    return out


def _box_uvs(verts, faces, scale=3.0):
    """Box-projected UVs so textured materials show on plain boxes."""
    uv = []
    for f in faces:
        pts = [Vector(verts[i]) for i in f]
        n = (pts[1] - pts[0]).cross(pts[2] - pts[0])
        ax = max(range(3), key=lambda i: abs(n[i]))
        for p in pts:
            if ax == 0:
                uv.append((p.y / scale, p.z / scale))
            elif ax == 1:
                uv.append((p.x / scale, p.z / scale))
            else:
                uv.append((p.x / scale, p.y / scale))
    return uv


def box(parent, name, center, size, mat, rot=(0, 0, 0), bevel=0.0):
    cx, cy, cz = center
    sx, sy, sz = size
    if bevel and bevel > 0:
        bm = bmesh.new()
        bmesh.ops.create_cube(bm, size=1.0)
        bmesh.ops.scale(bm, vec=Vector((sx, sy, sz)), verts=bm.verts)
        bmesh.ops.bevel(bm, geom=list(bm.edges), offset=bevel, segments=1, affect="EDGES")
        me = bpy.data.meshes.new(name)
        bm.to_mesh(me)
        bm.free()
        if mat:
            me.materials.append(mat)
        obj = bpy.data.objects.new(name, me)
        _link(obj, parent)
        obj.location = (cx, cy, cz)
        obj.rotation_euler = rot
        return obj
    verts = _box_verts(cx, cy, cz, sx, sy, sz, rot)
    return _mesh_obj(name, verts, _BOX_FACES, mat, parent, uv=_box_uvs(verts, _BOX_FACES))


def box_mm(parent, name, x0, y0, z0, x1, y1, z1, mat, bevel=0.0):
    return box(parent, name, ((x0 + x1) / 2, (y0 + y1) / 2, (z0 + z1) / 2),
               (abs(x1 - x0), abs(y1 - y0), abs(z1 - z0)), mat, bevel=bevel)


def cyl(parent, name, center, radius, depth, mat, rot=(0, 0, 0), verts=14):
    bm = bmesh.new()
    bmesh.ops.create_cone(bm, cap_ends=True, cap_tris=False, segments=verts,
                          radius1=radius, radius2=radius, depth=depth)
    me = bpy.data.meshes.new(name)
    bm.to_mesh(me)
    bm.free()
    if mat:
        me.materials.append(mat)
    obj = bpy.data.objects.new(name, me)
    _link(obj, parent)
    obj.location = center
    obj.rotation_euler = rot
    return obj


def cyl_between(parent, name, p0, p1, radius, mat, verts=8):
    p0, p1 = Vector(p0), Vector(p1)
    d = p1 - p0
    if d.length < 1e-5:
        return None
    obj = cyl(parent, name, (p0 + p1) / 2, radius, d.length, mat, verts=verts)
    obj.rotation_mode = "QUATERNION"
    obj.rotation_quaternion = d.to_track_quat("Z", "Y")
    return obj


def sphere(parent, name, center, radius, mat, segments=14, rings=7):
    bm = bmesh.new()
    bmesh.ops.create_uvsphere(bm, u_segments=segments, v_segments=rings, radius=radius)
    me = bpy.data.meshes.new(name)
    bm.to_mesh(me)
    bm.free()
    if mat:
        me.materials.append(mat)
    obj = bpy.data.objects.new(name, me)
    _link(obj, parent)
    obj.location = center
    return obj


def quad(parent, name, center, u_vec, v_vec, size_u, size_v, mat, uv=None):
    c = Vector(center)
    su = Vector(u_vec).normalized() * (size_u / 2)
    sv = Vector(v_vec).normalized() * (size_v / 2)
    verts = [tuple(c - su - sv), tuple(c + su - sv), tuple(c + su + sv), tuple(c - su + sv)]
    return _mesh_obj(name, verts, [(0, 1, 2, 3)], mat, parent, uv=uv)


def plane_y(parent, name, center, sx, sz, mat, facing="PY", uv=None):
    u, v, _n = FACES[facing]
    return quad(parent, name, center, u, v, sx, sz, mat, uv=uv)


def prism(parent, name, pts2d, z0, z1, mat, loc=(0, 0, 0)):
    n = len(pts2d)
    verts = [(x + loc[0], y + loc[1], z0) for x, y in pts2d] + \
            [(x + loc[0], y + loc[1], z1) for x, y in pts2d]
    faces = []
    for i in range(n):
        j = (i + 1) % n
        faces.append((i, j, j + n, i + n))
    faces.append(tuple(range(n - 1, -1, -1)))
    faces.append(tuple(range(n, 2 * n)))
    return _mesh_obj(name, verts, faces, mat, parent)


def gable_roof(parent, name, center, size_x, size_y, height, mat, overhang=0.3):
    cx, cy, cz = center
    sx, sy = size_x / 2 + overhang, size_y / 2 + overhang
    verts = [(cx - sx, cy - sy, cz), (cx + sx, cy - sy, cz),
             (cx + sx, cy + sy, cz), (cx - sx, cy + sy, cz),
             (cx - sx, cy, cz + height), (cx + sx, cy, cz + height)]
    faces = [(0, 1, 5, 4), (2, 3, 4, 5), (0, 4, 3), (1, 2, 5), (0, 3, 2, 1)]
    return _mesh_obj(name, verts, faces, mat, parent)


def wire(parent, name, p0, p1, sag, mat, radius=0.012, segments=6):
    p0, p1 = Vector(p0), Vector(p1)
    pts = []
    for i in range(segments + 1):
        t = i / segments
        p = p0.lerp(p1, t)
        p.z -= sag * math.sin(math.pi * t)
        pts.append(p)
    for i in range(segments):
        cyl_between(parent, f"{name}_{i}", pts[i], pts[i + 1], radius, mat, verts=4)


def text3d(parent, name, body, loc, size, mat, rot=(math.radians(90), 0, 0),
           extrude=0.012):
    font = None
    for fp in (r"C:\Windows\Fonts\arialbd.ttf", r"C:\Windows\Fonts\arial.ttf",
               r"C:\Windows\Fonts\tahomabd.ttf"):
        if os.path.exists(fp):
            try:
                font = bpy.data.fonts.load(fp)
                break
            except Exception:
                pass
    cu = bpy.data.curves.new(name, "FONT")
    cu.body = body
    if font:
        cu.font = font
    cu.size = size
    cu.extrude = extrude
    cu.align_x = "CENTER"
    cu.align_y = "CENTER"
    obj = bpy.data.objects.new(name, cu)
    _link(obj, parent)
    obj.location = loc
    obj.rotation_euler = rot
    if mat:
        cu.materials.append(mat)
    try:
        bpy.context.view_layer.objects.active = obj
        obj.select_set(True)
        bpy.ops.object.convert(target="MESH")
        obj.select_set(False)
    except Exception:
        pass
    return obj


# ----------------------------------------------------------------------------
# window materials & window unit
# ----------------------------------------------------------------------------
WIN_LIT = [
    ("warm_a", "tex_window_warm_curtain.png", 3.4, 0.30),
    ("warm_b", "tex_win_warm_kitchen.png", 3.2, 0.24),
    ("tv", "tex_win_tv_blue.png", 2.4, 0.14),
    ("dim", "tex_win_balcony_door.png", 2.2, 0.16),
    ("stair", "tex_win_stair2.png", 2.6, 0.16),
    ("glow", "tex_win_dim_warm.png", 1.4, 0.14),
]
WIN_DARK = [
    ("dark_a", "tex_win_dark_blue.png"),
    ("dark_b", "tex_window_cool_stair.png"),
    ("boarded", "tex_win_boarded.png"),
    ("dim_a", "tex_win_dim_blue.png"),
    ("dim_b", "tex_win_dim_curtain.png"),
]

_WM = None


def win_materials():
    global _WM
    if _WM is not None:
        return _WM
    mats = {}
    for key, fn, strength, _w in WIN_LIT:
        mats[key] = mat_texture("WIN_" + key, fn, rough=0.22, emission_strength=strength,
                                fallback=PAL["glass_dark"])
    for key, fn in WIN_DARK:
        mats[key] = mat_texture("WIND_" + key, fn, rough=0.18, fallback=PAL["glass_dark"])
    mats["frame"] = mat_solid("WIN_FRAME", PAL["frame_white"], rough=0.72)
    mats["frame_grey"] = mat_solid("WIN_FRAME_GREY", PAL["frame_grey"], rough=0.76)
    mats["sill"] = mat_solid("WIN_SILL", PAL["conc_white"], rough=0.9)
    mats["grille"] = mat_solid("WIN_GRILLE", PAL["metal_dark"], rough=0.55, metallic=0.5)
    _WM = mats
    return mats


def _frame_mesh(name, u, v, n, center, w, h, depth, mat, parent):
    c = Vector(center)
    t = 0.075
    bars = [
        (c - u * (w / 2 - t / 2), (t + 0.03, h + t + 0.02, depth)),
        (c + u * (w / 2 - t / 2), (t + 0.03, h + t + 0.02, depth)),
        (c + v * (h / 2 - t / 2), (w + t + 0.02, t + 0.03, depth)),
        (c - v * (h / 2 - t / 2), (w + t + 0.02, t + 0.03, depth)),
        (c, (t * 0.85, h, depth)),
        (c + v * (h * 0.14), (w, t * 0.7, depth)),
    ]
    verts, faces = [], []
    for bc, (su, sv, sn) in bars:
        hu, hv, hn = su / 2, sv / 2, sn / 2
        base = len(verts)
        for du, dv, dn in ((-1, -1, -1), (1, -1, -1), (1, 1, -1), (-1, 1, -1),
                           (-1, -1, 1), (1, -1, 1), (1, 1, 1), (-1, 1, 1)):
            verts.append(tuple(bc + u * (du * hu) + v * (dv * hv) + n * (dn * hn)))
        for f in _BOX_FACES:
            faces.append(tuple(base + i for i in f))
    return _mesh_obj(name, verts, faces, mat, parent)


def window_unit(parent, name, P0, u, n, s, z_center, w, h, rng, lit_ratio=0.15,
                frame_mat=None, recess=0.2, grille=False, lit_override=None,
                uv_inner=(0.14, 0.12, 0.86, 0.85)):
    u = Vector(u).normalized()
    n = Vector(n).normalized()
    v = Vector((0, 0, 1))
    wm = win_materials()
    outer = Vector(P0) + u * s + n * 0.03 + v * z_center
    glass_c = outer + n * (-recess)
    lit = lit_override if lit_override is not None else (rng.random() < lit_ratio)
    if lit:
        key = rng.wpick([k for k, _f, _s, _w in WIN_LIT],
                        [wv for _k, _f, _s, wv in WIN_LIT])
        gmat = wm[key]
    else:
        key = rng.pick([k for k, _f in WIN_DARK])
        gmat = wm[key]
    if frame_mat is None:
        frame_mat = wm["frame"] if rng.random() < 0.7 else wm["frame_grey"]
    wo, ho = w - 0.06, h - 0.06
    hu, hv = u * (wo / 2), v * (ho / 2)
    verts = [tuple(glass_c - hu - hv), tuple(glass_c - hu + hv),
             tuple(glass_c + hu + hv), tuple(glass_c + hu - hv)]
    uv = [(uv_inner[0], uv_inner[1]), (uv_inner[0], uv_inner[3]),
          (uv_inner[2], uv_inner[3]), (uv_inner[2], uv_inner[1])]
    _mesh_obj(name + "_glass", verts, [(0, 1, 2, 3)], gmat, parent, uv=uv)
    _frame_mesh(name + "_frame", u, v, n, outer, w, h, 0.1, frame_mat, parent)
    sill_c = outer + n * 0.07 - v * (h / 2 + 0.035)
    if abs(u.x) > 0.5:
        sill_size = (w + 0.18, 0.15, 0.07)
    else:
        sill_size = (0.15, w + 0.18, 0.07)
    box(parent, name + "_sill", tuple(sill_c), sill_size, wm["sill"])
    if grille:
        for i in range(3):
            off = (i - 1) * (w / 3.1)
            gc = outer + u * off + n * 0.1
            box(parent, name + f"_bars{i}", tuple(gc), (0.028, 0.028, h * 0.92), wm["grille"])
    return {"lit": lit, "kind": key}


# ----------------------------------------------------------------------------
# ground, roads, misc details
# ----------------------------------------------------------------------------
def ground_quad(parent, name, rect, z, mat, tile=8.0):
    x0, y0, x1, y1 = rect
    cx, cy = (x0 + x1) / 2, (y0 + y1) / 2
    sx, sy = x1 - x0, y1 - y0
    u = (sx / tile, 0)
    v = (0, sy / tile)
    verts = [(x0, y0, z), (x1, y0, z), (x1, y1, z), (x0, y1, z)]
    uv = [(0, 0), (u[0], 0), (u[0], v[1]), (0, v[1])]
    return _mesh_obj(name, verts, [(0, 1, 2, 3)], mat, parent, uv=uv)


def curb(parent, name, p0, p1, mat, height=0.14, width=0.18):
    p0, p1 = Vector(p0), Vector(p1)
    mid = (p0 + p1) / 2
    d = p1 - p0
    if abs(d.x) >= abs(d.y):
        size = (abs(d.x), width, height)
    else:
        size = (width, abs(d.y), height)
    return box(parent, name, (mid.x, mid.y, mid.z + height / 2), size, mat)


def steps_block(parent, name, center, width, depth, height, n, mat):
    cx, cy, cz = center
    for i in range(n):
        h = height * (i + 1) / n
        d = depth * (1 - 0.55 * i / max(1, n - 1))
        box(parent, f"{name}_{i}", (cx, cy + d / 2, cz + h / 2 - height / 2),
            (width, d, h + 0.02), mat)


def pipe_run(parent, name, pts, radius, mat, verts=8):
    for i in range(len(pts) - 1):
        cyl_between(parent, f"{name}_{i}", pts[i], pts[i + 1], radius, mat, verts=verts)


# ----------------------------------------------------------------------------
# facade wall
# ----------------------------------------------------------------------------
def wall_frame(rect, face):
    x0, y0, x1, y1 = rect
    if face == "PY":
        return Vector((x0, y1, 0)), Vector((1, 0, 0)), x1 - x0, Vector((0, 1, 0))
    if face == "NY":
        return Vector((x0, y0, 0)), Vector((1, 0, 0)), x1 - x0, Vector((0, -1, 0))
    if face == "PX":
        return Vector((x1, y0, 0)), Vector((0, 1, 0)), y1 - y0, Vector((1, 0, 0))
    if face == "NX":
        return Vector((x0, y0, 0)), Vector((0, 1, 0)), y1 - y0, Vector((-1, 0, 0))
    raise ValueError(face)


def _wall_box(parent, name, P0, u, n, s0, s1, z0, z1, thick, mat, extra=0.0, out=0.0):
    if s1 - s0 < 0.02 or z1 - z0 < 0.02:
        return None
    length = s1 - s0
    mid = P0 + u * ((s0 + s1) / 2) + n * (out + (thick + extra) / 2)
    sz = (length + 0.002, thick + extra, z1 - z0)
    # orient: length along u (X or Y)
    if abs(u.x) > 0.5:
        size = (length, sz[1], sz[2])
    else:
        size = (sz[1], length, sz[2])
    return box(parent, name, (mid.x, mid.y, (z0 + z1) / 2), size, mat)


def _window_columns(L, spacing, margin, win_w):
    s = margin
    cols = []
    while s < L - margin + 1e-6:
        cols.append(s)
        s += spacing
    return cols


def facade_wall(parent, name, rect, face, z0, z1, mat_wall, rng,
                floors, floor_h, win_w, win_h, spacing, wmats,
                margin=1.3, lit_ratio=0.15, thickness=0.35,
                plinth_h=1.15, plinth_mat=None, arches=(), ground_skip=(),
                balcony_cols=(), balcony_every=2, balcony_kind="open",
                balcony_width=2.9, balcony_depth=1.05, joints=True,
                ground_win_bottom=1.55, roof_line=True, grille_ground=False,
                win_offsets=None, bal_start_floor=1):
    """Build one facade wall with real window openings.

    arches: [(s_center, width), ...] full-height ground passages.
    ground_skip: [s_center, ...] where a porch replaces the ground window.
    balcony_cols: optional explicit s-centres; otherwise every `balcony_every` columns.
    """
    P0, u, L, n = wall_frame(rect, face)
    rows = []
    for f in range(floors):
        if f == 0:
            zb = z0 + ground_win_bottom
        else:
            zb = z0 + f * floor_h + (floor_h - win_h) * 0.55
        rows.append((zb, zb + win_h))
    cols = _window_columns(L, spacing, margin, win_w)
    if win_offsets:
        cols = [margin + o for o in win_offsets]
    # remove windows colliding with arches / skipped ground positions
    arch_spans = [(s - w / 2, s + w / 2) for s, w in arches]
    valid_cols = []
    for s in cols:
        if any(a0 - 0.4 < s < a1 + 0.4 for a0, a1 in arch_spans):
            continue
        valid_cols.append(s)
    skip_ground = list(ground_skip) + [s for s, _w in arches]
    # z slices: wall band boundaries
    z_edges = {z0, z1}
    for zb, zt in rows:
        z_edges.add(zb)
        z_edges.add(zt)
    arch_top = z0 + 2.95
    if arches:
        z_edges.add(arch_top)
    z_edges = sorted(z_edges)
    stats = {"windows": 0, "lit": 0}
    for zi in range(len(z_edges) - 1):
        za, zb = z_edges[zi], z_edges[zi + 1]
        if zb - za < 0.03:
            continue
        solid = [(0.0, L)]
        # cut arches
        for a0, a1 in arch_spans:
            if zb <= arch_top + 1e-6:
                solid = _subtract_span(solid, a0, a1)
        # cut window row openings
        row_here = None
        for ri, (rzb, rzt) in enumerate(rows):
            if za > rzb - 1e-6 and zb < rzt + 1e-6:
                row_here = ri
                break
        if row_here is not None:
            for s in valid_cols:
                if row_here == 0 and any(abs(s - g) < 0.6 for g in skip_ground):
                    continue
                solid = _subtract_span(solid, s - win_w / 2, s + win_w / 2)
        # plinth material for bottom slice
        is_plinth = (za < z0 + plinth_h - 1e-6)
        wall_mat = plinth_mat if (is_plinth and plinth_mat is not None) else mat_wall
        extra = 0.045 if is_plinth else 0.0
        out = 0.0
        for k, (s0, s1) in enumerate(solid):
            _wall_box(parent, f"{name}_w{zi}_{k}", P0, u, n, s0, s1, za, zb,
                      thickness, wall_mat, extra=extra, out=out)
    # windows
    for fi, (rzb, rzt) in enumerate(rows):
        zc = (rzb + rzt) / 2
        for ci, s in enumerate(valid_cols):
            if fi == 0 and any(abs(s - g) < 0.6 for g in skip_ground):
                continue
            info = window_unit(parent, f"{name}_win{fi}_{ci}", P0, u, n, s, zc,
                               win_w, win_h, rng, lit_ratio=lit_ratio,
                               grille=(grille_ground and fi == 0))
            stats["windows"] += 1
            stats["lit"] += 1 if info["lit"] else 0
    # balconies
    if balcony_cols == "auto":
        chosen = [s for i, s in enumerate(valid_cols) if i % max(1, balcony_every) == 0]
    else:
        chosen = list(balcony_cols)
    for fi in range(bal_start_floor, floors):
        rz = rows[fi][0]
        for bi, s in enumerate(chosen):
            if any(abs(s - a) < 1.6 for a in skip_ground) and fi == 0:
                continue
            balcony_unit(parent, f"{name}_bal{fi}_{bi}", P0, u, n, s, rz,
                         balcony_width, balcony_depth, balcony_kind, rng,
                         mat_wall if plinth_mat is None else mat_wall)
    # panel joints (single merged object)
    if joints:
        _panel_joints(parent, f"{name}_joints", P0, u, n, L, z0, z1, rows, cols,
                      thickness)
    return stats


def _subtract_span(spans, a0, a1):
    out = []
    for s0, s1 in spans:
        if a1 <= s0 or a0 >= s1:
            out.append((s0, s1))
            continue
        if a0 > s0 + 1e-6:
            out.append((s0, min(a0, s1)))
        if a1 < s1 - 1e-6:
            out.append((max(a1, s0), s1))
    return out


def _panel_joints(parent, name, P0, u, n, L, z0, z1, rows, cols, thickness):
    verts, faces = [], []
    def add_strip(s0, s1, za, zb, out=0.02, h=0.028):
        mid = P0 + u * ((s0 + s1) / 2) + n * out + Vector((0, 0, (za + zb) / 2))
        if abs(u.x) > 0.5:
            size = (s1 - s0, 0.03, h)
        else:
            size = (0.03, s1 - s0, h)
        base = len(verts)
        for p in _box_verts(mid.x, mid.y, mid.z, *size):
            verts.append(p)
        for f in _BOX_FACES:
            faces.append(tuple(base + i for i in f))
    # horizontal joints at floor lines
    for zb, zt in rows:
        add_strip(0, L, zt + 0.02, zt + 0.05)
    # vertical joints between window columns (pier centres)
    for i in range(len(cols) - 1):
        mid_s = (cols[i] + cols[i + 1]) / 2
        add_strip(mid_s - 0.015, mid_s + 0.015, z0 + 0.4, z1 - 0.4, h=0.03)
    if verts:
        _mesh_obj(name, verts, faces, mat_solid("JOINT_MAT", "#6E6E68", rough=0.95), parent)


def balcony_unit(parent, name, P0, u, n, s, floor_z, width, depth, kind, rng, wall_mat):
    v = Vector((0, 0, 1))
    base = Vector(P0) + u * s
    slab_c = base + n * (depth / 2 - 0.05) + v * (floor_z + 0.08)
    box(parent, name + "_slab", tuple(slab_c), (width, depth, 0.18),
        mat_solid("BAL_SLAB", PAL["conc_grey"], rough=0.92), bevel=0.03)
    rail_mat = mat_solid("BAL_RAIL", "#6E7168", rough=0.55, metallic=0.5)
    rail_mat2 = mat_solid("BAL_RAIL_RUST", PAL["rust"], rough=0.7, metallic=0.35)
    rm = rail_mat if rng.random() < 0.6 else rail_mat2
    z = floor_z + 0.17
    if kind == "glazed":
        glass = mat_solid("BAL_GLASS", "#16202A", rough=0.12, metallic=0.2, alpha=1.0)
        frame = mat_solid("BAL_FRAME", PAL["frame_grey"], rough=0.6)
        fc = base + n * (depth - 0.07) + v * (z + 1.05)
        plane_y(parent, name + "_glass", tuple(fc), width - 0.1, 2.0, glass, "PY")
        for i in range(4):
            fx = base + u * (-width / 2 + 0.06 + i * (width - 0.12) / 3) + n * (depth - 0.05)
            box(parent, name + f"_fp{i}", tuple(fx + v * (z + 1.05)),
                (0.06, 0.06, 2.05), frame)
        box(parent, name + "_ftop", tuple(base + n * (depth - 0.06) + v * (z + 2.08)),
            (width, 0.1, 0.08), frame)
    else:
        # side cheeks + front railing
        for side in (-1, 1):
            sc = base + u * (side * (width / 2 - 0.04)) + n * (depth / 2) + v * (z + 0.5)
            box(parent, name + f"_cheek{side}", tuple(sc), (0.05, depth, 1.0), rm)
        front = base + n * (depth - 0.05)
        railing_line(parent, name + "_rail", front - u * (width / 2), front + u * (width / 2),
                     z, 1.0, rm)
    # laundry on some balconies
    if rng.chance(0.4):
        cloth_mats = [mat_solid("CLOTH_" + c, c, rough=0.9) for c in
                      ("#7E8A96", "#A9A6A0", "#5C6B62", "#8F7F72", "#B9BDC1")]
        line_y = base + n * (depth - 0.25)
        for li in range(rng.randint(2, 4)):
            t = -width / 2 + 0.4 + (width - 0.8) * (li / 4)
            wc = line_y + u * t + v * (z + 0.55 - rng.uniform(0.05, 0.25))
            m = rng.pick(cloth_mats)
            q = quad(parent, name + f"_cloth{li}", tuple(wc), u, v,
                     rng.uniform(0.35, 0.55), rng.uniform(0.5, 0.8), m)
            q.rotation_euler[1] = rng.jitter(0.08)


def railing_line(parent, name, p0, p1, z, height, mat):
    p0, p1 = Vector(p0), Vector(p1)
    d = p1 - p0
    length = d.length
    if length < 0.05:
        return
    n_posts = max(2, int(length / 0.5))
    for i in range(n_posts + 1):
        p = p0 + d * (i / n_posts)
        box(parent, f"{name}_p{i}", (p.x, p.y, z + height / 2), (0.035, 0.035, height), mat)
    for k, hf in enumerate((0.35, 0.72, 1.0)):
        mid = (p0 + p1) / 2
        box(parent, f"{name}_h{k}", (mid.x, mid.y, z + height * hf),
            (abs(d.x) + 0.03, abs(d.y) + 0.03, 0.03), mat)


# ----------------------------------------------------------------------------
# building shell helpers
# ----------------------------------------------------------------------------
def roof_flat(parent, name, rect, z, mat, thickness=0.22):
    x0, y0, x1, y1 = rect
    return box_mm(parent, name, x0 - 0.05, y0 - 0.05, z - thickness, x1 + 0.05, y1 + 0.05,
                  z, mat)


def parapet(parent, name, rect, z, height=0.75, thickness=0.22, mat=None,
            coping_mat=None):
    mat = mat or mat_solid("PARAPET", PAL["conc_grey"], rough=0.9)
    coping_mat = coping_mat or mat_solid("COPING", PAL["conc_white"], rough=0.85)
    x0, y0, x1, y1 = rect
    box_mm(parent, name + "_n", x0 - 0.1, y1 - thickness, z, x1 + 0.1, y1, z + height, mat)
    box_mm(parent, name + "_s", x0 - 0.1, y0, z, x1 + 0.1, y0 + thickness, z + height, mat)
    box_mm(parent, name + "_w", x0, y0, z, x0 + thickness, y1, z + height, mat)
    box_mm(parent, name + "_e", x1 - thickness, y0, z, x1, y1, z + height, mat)
    # coping stones slightly proud
    box_mm(parent, name + "_cn", x0 - 0.14, y1 - thickness - 0.03, z + height,
           x1 + 0.14, y1 + 0.03, z + height + 0.06, coping_mat)
    box_mm(parent, name + "_cs", x0 - 0.14, y0 - 0.03, z + height,
           x1 + 0.14, y0 + thickness + 0.03, z + height + 0.06, coping_mat)


def entrance_porch(parent, name, P0, u, n, s, z0, wall_mat, rng, door_mat=None,
                   width=3.3, depth=1.15, height=2.95, steps=3, lit=True,
                   sign=None):
    """Protruding entrance vestibule with steps, metal door, canopy and lamp."""
    P0 = Vector(P0)
    u = Vector(u).normalized()
    n = Vector(n).normalized()
    v = Vector((0, 0, 1))
    base = P0 + u * s
    door_mat = door_mat or mat_solid("DOOR_METAL", "#353B3E", rough=0.55, metallic=0.55)
    dark = mat_solid("DOOR_RECESS", "#0E1114", rough=1.0)
    # side walls
    for side in (-1, 1):
        c = base + u * (side * (width / 2 - 0.09)) + n * (depth / 2) + v * (z0 + height / 2)
        box(parent, name + f"_pw{side}", tuple(c), (0.18, depth, height), wall_mat)
    # roof slab + fascia
    box(parent, name + "_roof",
        tuple(base + n * (depth / 2 - 0.05) + v * (z0 + height + 0.12)),
        (width + 0.25, depth + 0.35, 0.24), wall_mat, bevel=0.02)
    # interior floor
    box(parent, name + "_floor", tuple(base + n * (depth / 2 - 0.1) + v * (z0 + 0.08)),
        (width - 0.2, depth - 0.1, 0.16), mat_solid("PORCH_FLOOR", "#4E5254", rough=0.85))
    # steps
    for i in range(steps):
        sy = z0 + (steps - i) * 0.001
        off = depth + 0.18 + (steps - 1 - i) * 0.34
        box(parent, name + f"_step{i}",
            tuple(base + n * off + v * (z0 + 0.06 + i * 0.13)),
            (width - 0.35, 0.36, 0.13 + (i * 0.001)), mat_solid("STEP", "#5A5E60", rough=0.9))
    # door recess (dark) + metal double door
    dc = base + n * 0.06 + v * (z0 + 1.05)
    plane_y(parent, name + "_dark", tuple(dc), width - 0.55, 2.1, dark, "PY")
    dd = base + n * 0.12 + v * (z0 + 1.02)
    for side in (-1, 1):
        box(parent, name + f"_door{side}",
            tuple(dd + u * (side * (width - 0.7) / 4)), (width - 0.75, 0.05, 1.98),
            door_mat)
    box(parent, name + "_dirt", tuple(base + n * 0.24 + v * (z0 + 0.02)),
        (width - 0.3, 0.5, 0.025), mat_solid("DIRT_MAT", "#2C2A25", rough=1.0))
    # lamp under canopy
    lamp_c = base + n * (depth * 0.55) + v * (z0 + height - 0.2)
    if lit:
        em = mat_solid("PORCH_LAMP", "#FFD9A0", rough=0.2,
                       emission="#FFC57A", emission_strength=14.0)
        sphere(parent, name + "_lamp", tuple(lamp_c), 0.13, em)
    # house number plate
    num = f"{rng.randint(1, 32)}"
    plate_c = base + u * (width / 2 + 0.02) + n * 0.12 + v * (z0 + 1.9)
    box(parent, name + "_plate", tuple(plate_c), (0.34, 0.04, 0.22),
        mat_solid("PLATE", "#D8D6CC", rough=0.6))
    text3d(parent, name + "_num", num,
           tuple(plate_c + n * 0.03), 0.16, mat_solid("NUM_MAT", "#20242A", rough=0.8),
           rot=(math.radians(90), 0, 0))
    return base


def drainpipe(parent, name, x, y, z0, z1, mat=None, radius=0.06):
    mat = mat or mat_solid("DRAINPIPE", "#55584F", rough=0.75, metallic=0.3)
    cyl(parent, name + "_p", (x, y, (z0 + z1) / 2), radius, z1 - z0, mat, verts=8)
    for i in range(4):
        zz = z0 + (z1 - z0) * (0.15 + i * 0.22)
        box(parent, name + f"_br{i}", (x, y, zz), (0.1, 0.1, 0.06), mat)


def roof_details(parent, name, rect, z, rng, wall_mat, tower=False):
    """Vents, antennas, water tank, satellite dishes, elevator machine room."""
    x0, y0, x1, y1 = rect
    cx, cy = (x0 + x1) / 2, (y0 + y1) / 2
    metal = mat_solid("ROOF_METAL", PAL["metal_paint"], rough=0.6, metallic=0.5)
    dark = mat_solid("ROOF_DARK", "#24282A", rough=0.8)
    for i in range(rng.randint(3, 6)):
        vx = rng.uniform(x0 + 1.2, x1 - 1.2)
        vy = rng.uniform(y0 + 1.2, y1 - 1.2)
        if rng.chance(0.5):
            box(parent, f"{name}_vent{i}", (vx, vy, z + 0.42), (0.7, 0.5, 0.75), metal)
            box(parent, f"{name}_ventc{i}", (vx, vy, z + 0.83), (0.85, 0.62, 0.07), dark)
        else:
            cyl(parent, f"{name}_ventp{i}", (vx, vy, z + 0.45), 0.22, 0.9, dark, verts=10)
    # elevator machine room for towers
    if tower:
        box(parent, f"{name}_lift", (cx, cy, z + 1.25), (4.2, 3.4, 2.5), wall_mat,
            bevel=0.04)
        box(parent, f"{name}_liftroof", (cx, cy, z + 2.55), (4.5, 3.7, 0.16), dark)
    # antennas
    for i in range(rng.randint(2, 5 if not tower else 7)):
        ax = rng.uniform(x0 + 0.8, x1 - 0.8)
        ay = rng.uniform(y0 + 0.8, y1 - 0.8)
        h = rng.uniform(1.6, 4.0)
        cyl(parent, f"{name}_ant{i}", (ax, ay, z + h / 2), 0.025, h, dark, verts=5)
        for k in range(rng.randint(2, 4)):
            zz = z + h * (0.55 + 0.12 * k)
            box(parent, f"{name}_antb{i}_{k}", (ax, ay, zz),
                (rng.uniform(0.6, 1.1), 0.02, 0.02), dark)
    # water tank on some roofs
    if rng.chance(0.35):
        tx = rng.uniform(x0 + 2, x1 - 2)
        ty = rng.uniform(y0 + 2, y1 - 2)
        cyl(parent, f"{name}_tank", (tx, ty, z + 1.1), 0.85, 1.6, metal, verts=12)
    # satellite dishes on roof edge
    for i in range(rng.randint(0, 2)):
        dx = rng.uniform(x0 + 1, x1 - 1)
        dy = y0 + 0.6 if rng.chance(0.5) else y1 - 0.6
        cyl(parent, f"{name}_satpole{i}", (dx, dy, z + 0.6), 0.04, 1.2, dark, verts=5)
        cyl(parent, f"{name}_sat{i}", (dx, dy, z + 1.25), 0.32, 0.1, metal, verts=10,
            rot=(math.radians(70), 0, 0))


def ac_unit(parent, name, P0, u, n, s, z, rng):
    c = Vector(P0) + u * s + n * 0.24 + Vector((0, 0, z))
    m = mat_solid("AC_METAL", "#8E9188", rough=0.7, metallic=0.2)
    box(parent, name + "_ac", tuple(c), (0.78, 0.42, 0.52), m)
    cyl(parent, name + "_fan", tuple(c + n * 0.23), 0.19, 0.03,
        mat_solid("AC_FAN", "#3A3D40", rough=0.8), rot=(math.radians(90), 0, 0), verts=10)


# ----------------------------------------------------------------------------
# signs, posters, props
# ----------------------------------------------------------------------------
POSTER_FILES = ["tex_poster_space.png", "tex_poster_labour.png",
                "tex_poster_peace.png", "tex_poster_sport.png"]
POSTER_FILES_WIDE = ["tex_poster_wide_space.png", "tex_poster_wide_labour.png",
                     "tex_poster_wide_peace.png", "tex_poster_wide_sport.png",
                     "tex_poster_wide_mayday.png", "tex_poster_wide_family.png"]
_PM = {}
_PM_WIDE = {}


def poster_materials():
    """Portrait posters (for tall poster cases)."""
    if _PM:
        return _PM
    for i, fn in enumerate(POSTER_FILES):
        key = fn.split(".")[0]
        _PM[key] = mat_texture("POSTER_" + key, fn, rough=0.85,
                               fallback=["#7A4038", "#3E5470", "#8B8570", "#6E5540"][i])
    return _PM


def poster_materials_wide():
    """Landscape posters (for wide boards / billboards)."""
    if _PM_WIDE:
        return _PM_WIDE
    for i, fn in enumerate(POSTER_FILES_WIDE):
        key = fn.split(".")[0]
        _PM_WIDE[key] = mat_texture("POSTER_" + key, fn, rough=0.85,
                                    fallback=["#7A4038", "#3E5470", "#8B8570", "#6E5540",
                                              "#5E4A3A", "#74564A"][i])
    return _PM_WIDE


def pick_poster(width, height, rng=None):
    """Pick a poster material whose aspect roughly matches the frame."""
    wide = (width / max(height, 0.01)) > 1.12
    pm = poster_materials_wide() if wide else poster_materials()
    return pm[(rng or RNG(1)).pick(list(pm.keys()))]


def poster(parent, name, center, u, n, width, height, key=None, rng=None):
    """Poster quad: aspect-matched material + cover-crop UVs (never stretched)."""
    wide = (width / max(height, 0.01)) > 1.12
    pm = poster_materials_wide() if wide else poster_materials()
    keys = list(pm.keys())
    if key is None:
        key = (rng or RNG(1)).pick(keys)
    mat = pm[key]
    c = Vector(center)
    tex_aspect = 1.5 if wide else (2.0 / 3.0)
    quad_aspect = width / max(height, 0.01)
    if quad_aspect >= tex_aspect:
        hh = tex_aspect / quad_aspect
        v0, v1 = (1.0 - hh) / 2, (1.0 + hh) / 2
        uv = [(0, v0), (0, v1), (1, v1), (1, v0)]
    else:
        ww = quad_aspect / tex_aspect
        u0, u1 = (1.0 - ww) / 2, (1.0 + ww) / 2
        uv = [(u0, 0), (u0, 1), (u1, 1), (u1, 0)]
    return quad(parent, name, tuple(c), Vector(u), Vector((0, 0, 1)), width, height, mat, uv=uv)


def billboard(parent, name, center, u, n, width=8.0, height=4.0, rng=None, frame_mat=None):
    """Billboard on two posts with a poster face."""
    c = Vector(center)
    u = Vector(u).normalized()
    n = Vector(n).normalized()
    v = Vector((0, 0, 1))
    fm = frame_mat or mat_solid("BILL_FRAME", "#4A5054", rough=0.6, metallic=0.5)
    for side in (-1, 1):
        p = c + u * side * (width / 2 - 0.35) - v * (height / 2 + 1.3)
        box(parent, name + f"_post{side}", tuple(p), (0.18, 0.18, 3.2), fm)
    box(parent, name + "_panel", tuple(c), (width + 0.2, 0.14, height + 0.2), fm, bevel=0.02)
    poster(parent, name + "_face", tuple(c + n * 0.09), u, n, width - 0.1, height - 0.1,
           None, rng)
    poster(parent, name + "_faceb", tuple(c - n * 0.09), u, -n, width - 0.1, height - 0.1,
           None, rng)
    # small lamp bar to wash the panel
    box(parent, name + "_lampbar", tuple(c + n * 0.4 + v * (height / 2 + 0.15)),
        (width * 0.8, 0.12, 0.08), fm)


def signboard(parent, name, center, u, n, width, height, text, rng=None,
              bg="#2E3A42", fg="#E8E2D2", glow=2.0, depth=0.12):
    c = Vector(center)
    u = Vector(u).normalized()
    n = Vector(n).normalized()
    bg_mat = mat_solid("SIGN_BG_" + str(abs(hash(bg)) % 10000), bg, rough=0.6)
    box(parent, name + "_bg", tuple(c), (width, depth, height), bg_mat)
    fg_mat = mat_solid("SIGN_FG_" + str(abs(hash(fg)) % 10000), fg, rough=0.5,
                       emission=fg, emission_strength=glow)
    text3d(parent, name + "_txt", text, tuple(c + n * (depth / 2 + 0.004)),
           height * 0.55, fg_mat, rot=(math.radians(90), 0, 0))


def poster_board(parent, name, center, u, n, width=5.0, height=2.2, rng=None):
    """Community notice board with several small posters."""
    c = Vector(center)
    u = Vector(u).normalized()
    n = Vector(n).normalized()
    fm = mat_solid("BOARD_FRAME", "#4E5458", rough=0.7, metallic=0.3)
    box(parent, name + "_back", tuple(c), (width, 0.12, height), fm)
    poster(parent, name + "_p0", tuple(c + n * 0.08), u, n, width - 0.35, height - 0.35,
           None, rng)
    poster(parent, name + "_p1", tuple(c - n * 0.08), u, -n, width - 0.35, height - 0.35,
           None, rng)
    post_m = mat_solid("BOARD_POST", "#3C4144", rough=0.8)
    for side in (-1, 1):
        box(parent, name + f"_post{side}", tuple(c + u * side * (width / 2 - 0.2)
                                                 - Vector((0, 0, height / 2 + 0.45))),
            (0.1, 0.1, 1.3), post_m)


def lamp_post(parent, name, pos, height=8.0, kind="street", arm=2.0, rng=None):
    """Sodium street lamp / plaza globe lamp; returns the head position."""
    x, y, z = pos if len(pos) == 3 else (pos[0], pos[1], 0)
    metal = mat_solid("LAMP_METAL", "#3A4044", rough=0.55, metallic=0.5)
    cyl(parent, name + "_pole", (x, y, z + height / 2), 0.09, height, metal, verts=10)
    base = z + height
    if kind == "street":
        ang = rng.uniform(0, math.pi * 2) if rng else 0
        hx = x + math.cos(ang) * arm
        hy = y + math.sin(ang) * arm
        cyl_between(parent, name + "_arm", (x, y, base - 0.15), (hx, hy, base + 0.05),
                    0.05, metal, verts=6)
        head = mat_solid("LAMP_HEAD", "#2F3438", rough=0.5)
        box(parent, name + "_head", (hx, hy, base + 0.03), (0.55, 0.3, 0.16), head)
        warm = mat_solid("LAMP_GLOW", "#FFC98A", rough=0.3,
                         emission="#FFB469", emission_strength=22.0)
        box(parent, name + "_glow", (hx, hy, base - 0.06), (0.46, 0.24, 0.06), warm)
        return (hx, hy, base - 0.2)
    else:  # plaza globe
        warm = mat_solid("LAMP_GLOBE", "#FFE0B8", rough=0.25,
                         emission="#FFCF9A", emission_strength=16.0)
        sphere(parent, name + "_globe", (x, y, base + 0.3), 0.32, warm)
        return (x, y, base + 0.3)


def fence(parent, name, p0, p1, height=2.0, kind="concrete", rng=None):
    """Soviet concrete panel fence or green metal bar fence."""
    p0, p1 = Vector(p0), Vector(p1)
    d = p1 - p0
    length = d.length
    if length < 0.1:
        return
    d.normalize()
    if kind == "concrete":
        mat = mat_solid("FENCE_CONC", "#6E7068", rough=0.9)
        n = max(1, int(length / 2.6))
        for i in range(n):
            p = p0 + d * (length * (i + 0.5) / n)
            c = (p.x, p.y, height / 2 + 0.1)
            if abs(d.x) > abs(d.y):
                size = (length / n - 0.06, 0.12, height)
            else:
                size = (0.12, length / n - 0.06, height)
            box(parent, f"{name}_{i}", c, size, mat)
    else:
        mat = mat_solid("FENCE_GREEN", "#3C4B44", rough=0.7, metallic=0.3)
        n = max(2, int(length / 2.2))
        for i in range(n + 1):
            p = p0 + d * (length * i / n)
            box(parent, f"{name}_p{i}", (p.x, p.y, height / 2), (0.06, 0.06, height), mat)
        for k, hf in enumerate((0.25, 0.75, 0.98)):
            mid = (p0 + p1) / 2
            box(parent, f"{name}_h{k}", (mid.x, mid.y, height * hf),
                (abs(d.x) + 0.04, abs(d.y) + 0.04, 0.035), mat)
        # verticals
        for i in range(int(length / 0.28)):
            p = p0 + d * (length * (i + 0.5) / int(length / 0.28))
            box(parent, f"{name}_v{i}", (p.x, p.y, height * 0.62),
                (0.02, 0.02, height * 0.7), mat)


def bench(parent, name, center, facing=0.0, mat_wood=None, mat_leg=None):
    """Soviet park bench (wooden slats on concrete/metal legs)."""
    x, y, z = center
    wood = mat_wood or mat_solid("BENCH_WOOD", "#5E5A4E", rough=0.85)
    legs = mat_leg or mat_solid("BENCH_LEG", "#35393C", rough=0.7, metallic=0.3)
    for i in range(3):
        box(parent, f"{name}_seat{i}", (x, y, z + 0.45 + i * 0.045),
            (1.8, 0.12, 0.045), wood, rot=(0, 0, facing))
    for i in range(2):
        box(parent, f"{name}_back{i}", (x - math.sin(facing) * 0.16, y + math.cos(facing) * 0.16,
                                        z + 0.72 + i * 0.12), (1.8, 0.045, 0.09), wood,
            rot=(math.radians(-12), 0, facing))
    for side in (-1, 1):
        box(parent, f"{name}_leg{side}",
            (x + math.cos(facing) * side * 0.7, y + math.sin(facing) * side * 0.7, z + 0.22),
            (0.08, 0.5, 0.44), legs, rot=(0, 0, facing))


def trash_bin(parent, name, center, metal=None):
    x, y, z = center
    metal = metal or mat_solid("BIN_METAL", "#4A5050", rough=0.5, metallic=0.55)
    cyl(parent, name + "_body", (x, y, z + 0.42), 0.24, 0.84, metal, verts=10)
    cyl(parent, name + "_rim", (x, y, z + 0.86), 0.27, 0.06, metal, verts=10)
    cyl(parent, name + "_post", (x, y, z + 0.08), 0.05, 0.16,
        mat_solid("BIN_POST", "#2E3234", rough=0.8), verts=6)


def dumpster(parent, name, center, rot=0.0):
    """Rusty wheeled dumpster."""
    x, y, z = center
    rust = mat_solid("DUMP_RUST", PAL["rust"], rough=0.8, metallic=0.25)
    dark = mat_solid("DUMP_DARK", "#31383A", rough=0.7, metallic=0.4)
    box(parent, name + "_body", (x, y, z + 0.62), (1.6, 1.05, 1.0), rust, rot=(0, 0, rot),
        bevel=0.04)
    box(parent, name + "_lid", (x, y, z + 1.16), (1.62, 1.1, 0.08), dark, rot=(0, 0, rot))
    for side in (-1, 1):
        for f in (-1, 1):
            wx = x + side * 0.55 * math.cos(rot) - f * 0.42 * math.sin(rot)
            wy = y + side * 0.55 * math.sin(rot) + f * 0.42 * math.cos(rot)
            cyl(parent, f"{name}_wheel{side}{f}", (wx, wy, z + 0.12), 0.13, 0.08, dark,
                rot=(0, math.radians(90), rot), verts=8)


def bench_set(parent, name, center, facing=0.0, count=2, rng=None):
    for i in range(count):
        bench(parent, f"{name}_{i}", (center[0] + i * 2.4, center[1], center[2]), facing)


def make_sign_text_material():
    return mat_solid("SIGN_TEXT", "#EDE6D6", rough=0.4,
                     emission="#F0E9D8", emission_strength=2.2)
