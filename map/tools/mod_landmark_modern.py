"""mod_landmark_modern.py - Round-3 modernist / transport landmarks.

TYPES:
  institute_robotics   ROBOTICS     sculptural concrete robotics institute
  ministry_transport   MOT          ministry slab with vertical concrete fins
  sanatorium_druzhba   DRUZHBA      curved arcade sanatorium (Yalta Druzhba style)
  bus_station_ufo      UFO_STATION  Armenian flying-saucer bus station
  bus_station_hex      HEX_STATION  Tajik stepped-hexagon mountain bus station

Contract (docs/MODULE_CONTRACT.md): Blender Z-up, metres, world coords, +Y north.
Everything is parented to the BLD_<id> empty and named with the block id prefix;
facades are built with REAL recessed openings extruded inward from the rect edge.
No cameras / lights / sky / ground / fog here - the Lead owns look and site.
"""
from __future__ import annotations

import math

import bmesh
import bpy
from mathutils import Euler, Matrix, Vector

import kit
import lm_materials as lmm

# current kit box winding (normals consistent with kit.box / glTF export)
_BOX_FACES = kit._BOX_FACES
_FACE_U = {"PY": (1, 0, 0), "NY": (-1, 0, 0), "PX": (0, -1, 0), "NX": (0, 1, 0)}
_TEXT_ROT = {"PY": (math.radians(90), 0, math.pi), "NY": (math.radians(90), 0, 0),
             "PX": (math.radians(90), 0, math.radians(90)),
             "NX": (math.radians(90), 0, math.radians(-90))}


# ---------------------------------------------------------------------------
# materials
# ---------------------------------------------------------------------------
_M = None


def _mat(name, color, rough=0.9, metal=0.0, emission=None, strength=0.0):
    return kit.mat_solid("LM_" + name, color, rough=rough, metallic=metal,
                         emission=emission, emission_strength=strength)


def _mats():
    global _M
    if _M is not None:
        return _M
    _M = {
        # cold weathered concrete family
        "conc_a": _mat("conc_a", "#A9AEAA", 0.93),
        "conc_b": _mat("conc_b", "#949B98", 0.94),
        "conc_c": _mat("conc_c", "#B7BAB2", 0.92),
        "conc_dark": _mat("conc_dark", "#5E6668", 0.95),
        "conc_stain": _mat("conc_stain", "#4E5558", 0.97),
        "conc_warm": _mat("conc_warm", "#A8A396", 0.93),
        # stone / granite / bronze
        "stone": _mat("stone", "#B4AE9E", 0.9),
        "stone2": _mat("stone2", "#9C9789", 0.92),
        "granite": _mat("granite", "#494F52", 0.88),
        "bronze": _mat("bronze", "#5A4A38", 0.55, 0.7),
        "metal": _mat("metal", "#3C4549", 0.62, 0.25),
        "metal_dark": _mat("metal_dark", "#252B2E", 0.7, 0.3),
        "rust": _mat("rust", "#6B4A38", 0.88, 0.08),
        "glass": _mat("glass", "#0B1117", 0.12, 0.3),
        "glass_cool": _mat("glass_cool", "#16222C", 0.1, 0.35),
        "void": _mat("void", "#0A0E12", 1.0),
        "soil": _mat("soil", "#2C2A24", 0.98),
        "flag": _mat("flag", "#8C3229", 0.9),
        "mosaic0": _mat("mosaic0", "#8FA0A8", 0.7),
        "mosaic1": _mat("mosaic1", "#A98F72", 0.7),
        "mosaic2": _mat("mosaic2", "#C4BAA4", 0.7),
        "mosaic3": _mat("mosaic3", "#5F7482", 0.7),
        "mosaic4": _mat("mosaic4", "#8C5A48", 0.7),
        # emissive
        "glow_warm": _mat("glow_warm", "#FFD9A8", 0.3, emission="#FFC07A",
                          strength=16.0),
        "glow_warm2": _mat("glow_warm2", "#FFCE96", 0.3, emission="#FFB268",
                           strength=8.0),
        "glow_cool": _mat("glow_cool", "#E2ECF8", 0.3, emission="#D2E2F2",
                          strength=7.0),
        "glow_red": _mat("glow_red", "#FF3A24", 0.3, emission="#FF2A18",
                         strength=30.0),
    }
    _M["stone_tex"] = kit.mat_texture("LM_STONE_TEX", "tex_concrete_panel.png",
                                      rough=0.9, tint="#C0BAA8",
                                      fallback="#B4AE9E")
    return _M


# ---------------------------------------------------------------------------
# mesh helpers
# ---------------------------------------------------------------------------
def _mesh(parent, name, verts, faces, mat, uv=None, smooth=False):
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
    if smooth:
        for p in me.polygons:
            p.use_smooth = True
    obj = bpy.data.objects.new(name, me)
    if parent is not None and parent.users_collection:
        parent.users_collection[0].objects.link(obj)
        obj.parent = parent
    else:
        bpy.context.scene.collection.objects.link(obj)
    return obj


def _auto_uv(verts, faces, scale=3.0):
    """Box-projected UVs (same convention as kit._box_uvs) for custom meshes."""
    uv = []
    for f in faces:
        pts = [Vector(verts[i]) for i in f]
        if len(pts) < 3:
            uv.extend([(0.0, 0.0)] * len(pts))
            continue
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


def _recalc_normals(obj):
    bm = bmesh.new()
    bm.from_mesh(obj.data)
    bmesh.ops.recalc_face_normals(bm, faces=list(bm.faces))
    bm.to_mesh(obj.data)
    bm.free()


def _lathe(parent, name, profile, cx, cy, mat, segs=64, smooth=True, scale_uv=3.0,
           recalc=True):
    """Surface of revolution from an open (radius, z) profile around a vertical axis."""
    verts = []
    for (r, z) in profile:
        for i in range(segs):
            a = 2.0 * math.pi * i / segs
            verts.append((cx + math.cos(a) * r, cy + math.sin(a) * r, z))
    faces = []
    n = len(profile)
    for k in range(n - 1):
        base_a, base_b = k * segs, (k + 1) * segs
        for i in range(segs):
            j = (i + 1) % segs
            faces.append((base_a + i, base_b + i, base_b + j, base_a + j))
    obj = _mesh(parent, name, verts, faces, mat,
                uv=_auto_uv(verts, faces, scale_uv), smooth=smooth)
    if recalc:
        _recalc_normals(obj)
    return obj


def _lathe_solid(parent, name, profile, cx, cy, mat, segs=64, smooth=True,
                 scale_uv=3.0):
    """Closed surface of revolution: profile must start/end on the axis (r>0)."""
    obj = _lathe(parent, name, profile, cx, cy, mat, segs=segs, smooth=smooth,
                 scale_uv=scale_uv, recalc=True)
    return obj


def _acc(key, color, strength=8.0, rough=0.35):
    """Landmark accent / emissive material (module-local, LMA_ prefix)."""
    return kit.mat_solid("LMA_" + key, color, rough=rough, emission=color,
                         emission_strength=strength)


class Batch:
    """Merge many boxes that share one material into one mesh object."""

    def __init__(self, parent, name, mat):
        self.parent, self.name, self.mat = parent, name, mat
        self.verts, self.faces = [], []

    def box(self, center, size, rot=(0, 0, 0)):
        base = len(self.verts)
        hx, hy, hz = size[0] / 2, size[1] / 2, size[2] / 2
        pts = [(-hx, -hy, -hz), (hx, -hy, -hz), (hx, hy, -hz), (-hx, hy, -hz),
               (-hx, -hy, hz), (hx, -hy, hz), (hx, hy, hz), (-hx, hy, hz)]
        m = Euler(rot, "XYZ").to_matrix()
        for p in pts:
            v = m @ Vector(p)
            self.verts.append((v.x + center[0], v.y + center[1], v.z + center[2]))
        for f in _BOX_FACES:
            self.faces.append(tuple(base + i for i in f))

    def obox(self, center, size, ax, ay, az):
        """Box oriented by three orthonormal axes."""
        base = len(self.verts)
        hx, hy, hz = size[0] / 2, size[1] / 2, size[2] / 2
        for du, dv, dw in ((-1, -1, -1), (1, -1, -1), (1, 1, -1), (-1, 1, -1),
                           (-1, -1, 1), (1, -1, 1), (1, 1, 1), (-1, 1, 1)):
            p = Vector(center) + Vector(ax) * (du * hx) + Vector(ay) * (dv * hy) + \
                Vector(az) * (dw * hz)
            self.verts.append(tuple(p))
        for f in _BOX_FACES:
            self.faces.append(tuple(base + i for i in f))

    def seg(self, p0, p1, w, h):
        p0, p1 = Vector(p0), Vector(p1)
        d = p1 - p0
        L = d.length
        if L < 1e-4:
            return
        ax = d.normalized()
        ay = Vector((0, 0, 1)).cross(ax)
        if ay.length < 1e-4:
            ay = Vector((1, 0, 0))
        ay.normalize()
        az = ax.cross(ay).normalized()
        self.obox(tuple((p0 + p1) / 2), (L, w, h), ax, ay, az)

    def ub(self, P0, u, n, s, zc, out, lu, ln, lv):
        c = Vector(P0) + Vector(u) * s + Vector(n) * out + Vector((0, 0, zc))
        sx = abs(u[0]) * lu + abs(n[0]) * ln
        sy = abs(u[1]) * lu + abs(n[1]) * ln
        self.box((c.x, c.y, c.z), (sx, sy, lv))

    def build(self):
        if not self.verts:
            return None
        return _mesh(self.parent, self.name, self.verts, self.faces, self.mat)


def _quad(parent, name, center, u, v, su, sv, mat, uv=None):
    c = Vector(center)
    uu = Vector(u).normalized() * (su / 2)
    vv = Vector(v).normalized() * (sv / 2)
    verts = [tuple(c - uu - vv), tuple(c - uu + vv), tuple(c + uu + vv),
             tuple(c + uu - vv)]
    return _mesh(parent, name, verts, [(0, 1, 2, 3)], mat, uv=uv)


def _face_quad(parent, name, center, face, w, h, mat, tiles=(1.0, 1.0)):
    u = _FACE_U[face]
    tw, th = tiles
    uv = [(0.0, 0.0), (0.0, th), (tw, th), (tw, 0.0)]
    return _quad(parent, name, center, u, (0, 0, 1), w, h, mat, uv=uv)


# ---- prisms / rings / arcs -------------------------------------------------
def _prism(parent, name, pts2d, z0, z1, mat):
    n = len(pts2d)
    verts = [(x, y, z0) for x, y in pts2d] + [(x, y, z1) for x, y in pts2d]
    faces = []
    for i in range(n):
        j = (i + 1) % n
        faces.append((i, j, j + n, i + n))
    faces.append(tuple(range(n - 1, -1, -1)))
    faces.append(tuple(range(n, 2 * n)))
    return _mesh(parent, name, verts, faces, mat, uv=_auto_uv(verts, faces))


def _ring_prism(parent, name, outer, inner, z0, z1, mat):
    """Prism with a polygonal hole (outer and inner CCW point lists)."""
    n = len(outer)
    verts = ([(x, y, z0) for x, y in outer] + [(x, y, z1) for x, y in outer] +
             [(x, y, z0) for x, y in inner] + [(x, y, z1) for x, y in inner])
    faces = []
    for i in range(n):
        j = (i + 1) % n
        faces.append((i, j, j + n, i + n))                       # outer side
        a, b = 2 * n + i, 2 * n + j
        faces.append((a + n, b + n, b, a))                       # inner side
        faces.append((i + n, j + n, 2 * n + j, 2 * n + i))       # top
        faces.append((i, 2 * n + i, 2 * n + j, j))               # bottom
    return _mesh(parent, name, verts, faces, mat, uv=_auto_uv(verts, faces))


def _arc_ring(parent, name, cx, cy, r_in, r_out, a0, a1, z0, z1, mat, segs=24,
              caps=True):
    """Curved wall / deck segment: annulus swept between two angles (radians)."""
    verts, faces = [], []
    for i in range(segs + 1):
        a = a0 + (a1 - a0) * i / segs
        ca, sa = math.cos(a), math.sin(a)
        verts += [(cx + ca * r_in, cy + sa * r_in, z0),
                  (cx + ca * r_in, cy + sa * r_in, z1),
                  (cx + ca * r_out, cy + sa * r_out, z0),
                  (cx + ca * r_out, cy + sa * r_out, z1)]
    for i in range(segs):
        b, c = i * 4, (i + 1) * 4
        faces.append((b + 0, b + 1, c + 1, c + 0))   # inner face
        faces.append((b + 2, c + 2, c + 3, b + 3))   # outer face
        faces.append((b + 1, b + 3, c + 3, c + 1))   # top
        faces.append((b + 0, c + 0, c + 2, b + 2))   # bottom
    if caps:
        e = segs * 4
        faces.append((0, 2, 3, 1))
        faces.append((e + 1, e + 3, e + 2, e + 0))
    return _mesh(parent, name, verts, faces, mat, uv=_auto_uv(verts, faces))


def _arc_pts(cx, cy, r, a0, a1, segs):
    return [(cx + math.cos(a0 + (a1 - a0) * i / segs) * r,
             cy + math.sin(a0 + (a1 - a0) * i / segs) * r) for i in range(segs + 1)]


def _arch_between(parent, name, p0, p1, rise, thick, depth, mat, segs=9):
    """Semicircular voussoir arch spanning p0..p1 (same z) in a vertical plane."""
    p0, p1 = Vector(p0), Vector(p1)
    mid = (p0 + p1) / 2
    axis = (p1 - p0)
    half = axis.length / 2
    if half < 0.05:
        return
    ax = axis.normalized()
    plane_n = ax.cross(Vector((0, 0, 1))).normalized()
    b = Batch(parent, name, mat)
    for i in range(segs):
        t0 = math.pi * i / segs
        t1 = math.pi * (i + 1) / segs
        tm = (t0 + t1) / 2
        c = mid + ax * (math.cos(tm) * half) + Vector((0, 0, math.sin(tm) * rise))
        tang = (-ax * math.sin(tm)) + Vector((0, 0, math.cos(tm) * rise / max(half, 1e-3)))
        tang.normalize()
        rad = ax * math.cos(tm) + Vector((0, 0, math.sin(tm)))
        rad.normalize()
        length = (half * (t1 - t0)) * 1.12
        b.obox(tuple(c), (length, depth, thick), tang, plane_n, rad)
    b.build()


def _hex_pts(cx, cy, rx, ry, rot=0.0):
    return [(cx + math.cos(rot + math.pi / 3 * i) * rx,
             cy + math.sin(rot + math.pi / 3 * i) * ry) for i in range(6)]


def _hex_ring(cx, cy, rx_o, ry_o, rx_i, ry_i, rot=0.0):
    return _hex_pts(cx, cy, rx_o, ry_o, rot), _hex_pts(cx, cy, rx_i, ry_i, rot)


def _arc_band(parent, name, cx, cy, r, a0, a1, z0, z1, mat, segs=64,
              u_scale=2.2):
    """Vertical curved band with arc-length/proper wrap UVs (for mosaic friezes)."""
    verts, uvs, faces = [], [], []
    arc_len = abs(a1 - a0) * r
    for i in range(segs + 1):
        a = a0 + (a1 - a0) * i / segs
        ca, sa = math.cos(a), math.sin(a)
        verts += [(cx + ca * r, cy + sa * r, z0), (cx + ca * r, cy + sa * r, z1)]
    vh = (z1 - z0) / u_scale
    for i in range(segs):
        b = i * 2
        faces.append((b, b + 1, b + 3, b + 2))
        u0 = arc_len * i / segs / u_scale
        u1 = arc_len * (i + 1) / segs / u_scale
        uvs += [(u0, 0.0), (u0, vh), (u1, vh), (u1, 0.0)]
    return _mesh(parent, name, verts, faces, mat, uv=uvs)


def _squash_block(parent, cx, cy, sx, sy):
    """Scale every mesh child of a block horizontally about (cx, cy), world space."""
    bpy.context.view_layer.update()
    S = Matrix.Diagonal((sx, sy, 1.0, 1.0))
    T = Matrix.Translation((cx, cy, 0.0))
    M = T @ S @ T.inverted()
    seen = set()
    for o in list(parent.children):
        if o.type != "MESH":
            continue
        if o.data.name in seen:
            continue
        seen.add(o.data.name)
        o.data.transform(M @ o.matrix_world)
        o.matrix_world = Matrix.Identity(4)


def _hex_band(parent, name, cx, cy, rx_o, ry_o, rx_i, ry_i, z0, z1, mat, rot=0.0):
    """Horizontal band around a stretched-hexagon perimeter (mosaic friezes)."""
    o = _hex_pts(cx, cy, rx_o, ry_o, rot)
    i = _hex_pts(cx, cy, rx_i, ry_i, rot)
    return _ring_prism(parent, name, o, i, z0, z1, mat)


def _profile_prism(parent, name, pts_xz, y0, y1, mat):
    """Solid prism from a closed profile in the XZ plane, extruded along Y."""
    n = len(pts_xz)
    verts = [(x, y0, z) for x, z in pts_xz] + [(x, y1, z) for x, z in pts_xz]
    faces = []
    for i in range(n):
        j = (i + 1) % n
        faces.append((i, j, j + n, i + n))
    faces.append(tuple(range(n - 1, -1, -1)))
    faces.append(tuple(range(n, 2 * n)))
    return _mesh(parent, name, verts, faces, mat, uv=_auto_uv(verts, faces))


# ---- facade openings (same convention as round-1 module) -------------------
def _sub_spans(spans, a0, a1):
    out = []
    for s0, s1 in spans:
        if a1 <= s0 + 1e-6 or a0 >= s1 - 1e-6:
            out.append((s0, s1))
            continue
        if a0 > s0 + 1e-6:
            out.append((s0, min(a0, s1)))
        if a1 < s1 - 1e-6:
            out.append((max(a1, s0), s1))
    return out


def wall_openings(parent, name, rect, face, z0, z1, thickness, mat, holes,
                  mat_bands=None):
    """Facade wall extruded INWARD from the rect edge, with real openings.

    holes: [(s_center, width, zb, zt)] in absolute z.
    """
    P0, u, L, n = kit.wall_frame(rect, face)
    zcuts = {z0, z1}
    for _s, _w, zb, zt in holes:
        zcuts.add(min(max(zb, z0), z1))
        zcuts.add(min(max(zt, z0), z1))
    if mat_bands:
        for zt, _mm, _ee in mat_bands:
            if z0 - 1e-6 <= zt <= z1 + 1e-6:
                zcuts.add(zt)
    zcuts = sorted(zcuts)
    batches = {}

    def _batch(mm, extra):
        key = (mm.name, round(extra, 3))
        b = batches.get(key)
        if b is None:
            b = Batch(parent, f"{name}_{len(batches):02d}", mm)
            batches[key] = b
        return b

    for i in range(len(zcuts) - 1):
        za, zb = zcuts[i], zcuts[i + 1]
        if zb - za < 0.02:
            continue
        spans = [(0.0, L)]
        for s, w, hz0, hz1 in holes:
            if hz0 - 1e-6 <= za and zb <= hz1 + 1e-6:
                spans = _sub_spans(spans, s - w / 2, s + w / 2)
        mm, extra = mat, 0.0
        if mat_bands:
            for zt, bm, be in mat_bands:
                if zt >= zb - 1e-6:
                    mm, extra = bm, be
                    break
        b = _batch(mm, extra)
        for s0, s1 in spans:
            if s1 - s0 < 0.02:
                continue
            mid = Vector(P0) + Vector(u) * ((s0 + s1) / 2) - \
                Vector(n) * ((thickness + extra) / 2)
            if abs(u[0]) > 0.5:
                size = (s1 - s0, thickness + extra, zb - za)
            else:
                size = (thickness + extra, s1 - s0, zb - za)
            b.box((mid.x, mid.y, (za + zb) / 2), size)
    for b in batches.values():
        b.build()


def even_cols(L, count, margin):
    span = L - 2 * margin
    step = span / count
    return [margin + step * (i + 0.5) for i in range(count)], step


def windows(parent, name, rect, face, cols, rows, win_w, win_h, wm, rng,
            lit_ratio=0.06, overrides=None, recess=0.24, grille=False,
            frame_mat=None):
    P0, u, _L, n = kit.wall_frame(rect, face)
    overrides = overrides or {}
    out = {"windows": 0, "lit": 0}
    for fi, (zb, zt) in enumerate(rows):
        zc = (zb + zt) / 2
        for ci, s in enumerate(cols):
            info = kit.window_unit(parent, f"{name}_w{fi}_{ci}", P0, u, n, s, zc,
                                   win_w, win_h, rng, lit_ratio=lit_ratio,
                                   lit_override=overrides.get((fi, ci)),
                                   recess=recess, grille=grille,
                                   frame_mat=frame_mat)
            out["windows"] += 1
            out["lit"] += 1 if info["lit"] else 0
    return out


def sign_text(parent, name, text, center, face, size, mat, depth=0.03):
    return kit.text3d(parent, name, text, center, size, mat,
                      rot=_TEXT_ROT[face], extrude=depth)


def streaks(parent, name, P0, u, n, L, rng, mat, count=14, zmin=1.0, zmax=12.0,
            out=0.03):
    b = Batch(parent, name, mat)
    for _i in range(count):
        s = rng.uniform(0.6, L - 0.6)
        zt = rng.uniform(zmin + 1.0, zmax)
        zb = zt - rng.uniform(0.8, 3.6)
        b.ub(P0, u, n, s, (zb + zt) / 2, out, rng.uniform(0.1, 0.4), 0.05,
             zt - zb)
    b.build()


def glow_plane(parent, name, center, u, w, h, mat, face=None):
    return _quad(parent, name, center, u, (0, 0, 1), w, h, mat)


def railing_arc(parent, name, cx, cy, r, a0, a1, z, height, mat, segs=20,
                post_every=3):
    """Curved terrace railing: two arc rails + posts."""
    b = Batch(parent, name, mat)
    for hz in (height, height * 0.52):
        ring = _arc_ring(parent, f"{name}_rail{hz:.2f}", cx, cy, r - 0.03, r + 0.03,
                         a0, a1, z + hz - 0.025, z + hz + 0.025, mat, segs=segs)
    for i in range(0, segs + 1, post_every):
        a = a0 + (a1 - a0) * i / segs
        b.box((cx + math.cos(a) * r, cy + math.sin(a) * r, z + height / 2),
              (0.05, 0.05, height))
    b.build()


# ===========================================================================
# 1) ROBOTICS INSTITUTE  (rect [130,94,156,112], faces NX, h 16)
# ===========================================================================
def build_institute_robotics(parent, block, rng, ctx):
    """Soviet robotics institute (ЦНИИ РТК character).

    White-concrete sculptural composition: a low folded-plate podium with a
    recessed ribbon-glass lobby, a soaring parabolic concrete shell blade over the
    entrance with a cantilevered glazed control box, a cylindrical tower wrapped in
    horizontal ribbon-glass bands with vertical ribs, roof antenna masts + satellite
    dishes, and a robot-arm sculpture on a granite plinth at the entrance.
    """
    bid = block["id"]
    x0, y0, x1, y1 = block["rect"]
    st = {"type": "institute_robotics", "windows": 0}
    conc = lmm.mat("concrete_white")
    conc_g = lmm.mat("concrete_grey")
    conc_d = lmm.mat("concrete_dark")
    fin = lmm.mat("metal_fin")
    glass = lmm.mat("glass_ribbon")
    glass_c = lmm.mat("glass_clean")
    granite = lmm.mat("granite_grey")
    granite_d = lmm.mat("granite_dark")
    iron = lmm.mat("iron_dark")
    bronze = lmm.mat("bronze")
    gold = lmm.mat("gold")
    lit = _acc("rob_warm", "#FFC98A", 1.4)
    lit_cool = _acc("rob_cool", "#DCE8F4", 1.0)

    bx0, bx1 = x0 + 1.3, x1 - 1.3          # 141.3 .. 158.7
    by0, by1 = y0 + 1.0, y1 - 1.0          # 95.0 .. 111.0
    z_base = 6.40
    z_tower = 14.60
    tcx, tcy = 152.6, 106.2                # tower centre
    r_t = 4.35

    # ================= granite plinth + recessed ground floor =================
    kit.box_mm(parent, f"{bid}_plinth", bx0 - 0.35, by0 - 0.35, 0.0,
               bx1 + 0.35, by1 + 0.35, 0.55, granite_d)
    kit.box(parent, f"{bid}_baseband", ((bx0 + bx1) / 2, (by0 + by1) / 2, 0.95),
            (bx1 - bx0 + 0.3, by1 - by0 + 0.3, 0.8), granite)
    kit.box(parent, f"{bid}_cornice", ((bx0 + bx1) / 2, (by0 + by1) / 2,
                                       z_base + 0.18),
            (bx1 - bx0 + 0.6, by1 - by0 + 0.6, 0.36), conc_g)
    kit.box(parent, f"{bid}_base", ((bx0 + bx1) / 2, (by0 + by1) / 2,
                                    (0.55 + z_base) / 2 + 0.2),
            (bx1 - bx0, by1 - by0, z_base - 0.55), conc)
    # recessed ribbon-glass lobby on the west facade
    kit.box(parent, f"{bid}_lobby_glass", (bx0 + 1.15, (by0 + by1) / 2, 2.55),
            (0.10, by1 - by0 - 3.6, 3.9), glass)
    for i in range(9):
        yy = by0 + 1.9 + i * (by1 - by0 - 3.8) / 8
        kit.box(parent, f"{bid}_lm{i}", (bx0 + 1.10, yy, 2.55), (0.07, 0.07, 3.9),
                iron)
    kit.box(parent, f"{bid}_lobby_glow", (bx0 + 1.22, (by0 + by1) / 2, 1.9),
            (0.04, 5.0, 2.4), lit)
    for i in range(4):
        kit.cyl(parent, f"{bid}_pilot{i}", (bx0 + 0.35, by0 + 3.2 + i * 2.9, 2.6),
                0.20, 4.1, conc, verts=10)

    # ================= entrance portico + steps + plaque =================
    ex = bx0 - 0.25
    ey = (by0 + by1) / 2
    for k in (-1, 1):
        for j in (0, 1):
            kit.box(parent, f"{bid}_pcol{k}{j}", (ex + 0.1 + j * 0.05,
                                                 ey + k * 2.3, 2.55),
                    (0.52, 0.52, 4.0), conc)
    kit.box(parent, f"{bid}_pentab", (ex + 0.15, ey, 4.72), (1.3, 5.6, 0.5), conc)
    kit.box(parent, f"{bid}_plaque_frame", (ex - 0.55, ey, 5.72),
            (0.10, 6.3, 1.10), lmm.mat("granite_dark"))
    kit.box(parent, f"{bid}_plaque", (ex - 0.62, ey, 5.72), (0.10, 6.0, 0.95),
            lmm.mat("marble_white"))
    kit.text3d(parent, f"{bid}_plaque_txt", "ИНСТИТУТ РОБОТОТЕХНИКИ",
               (ex - 0.69, ey, 5.72), 0.40, lmm.mat("iron_dark"),
               rot=(math.radians(90), 0, math.radians(-90)))
    # doors + steps
    kit.box(parent, f"{bid}_door_dark", (bx0 + 0.9, ey, 2.0), (0.35, 4.6, 3.0),
            iron)
    kit.box(parent, f"{bid}_door", (bx0 + 0.72, ey, 1.95), (0.12, 4.0, 2.8),
            bronze)
    kit.box(parent, f"{bid}_door_gl", (bx0 + 0.66, ey, 2.3), (0.04, 3.4, 1.7),
            lit_cool)
    for k in range(3):
        kit.box(parent, f"{bid}_step{k}", (bx0 - 0.55 - k * 0.42, ey,
                                          0.11 + k * 0.18),
                (0.44, 6.6, 0.20 + k * 0.02), granite)

    # ================= parabolic concrete shell blade =================
    ys0, ys1 = by0 + 6.0, by0 + 11.4
    shell_prof = []
    n_p = 14
    for i in range(n_p + 1):
        t = i / n_p
        px = bx0 + 0.55 - 1.55 * math.sin(t * math.pi / 2) ** 1.25
        pz = 0.45 + 10.9 * t
        shell_prof.append((px, pz))
    thick = 0.38
    verts, faces = [], []
    for i, (px, pz) in enumerate(shell_prof):
        if i == 0:
            tx, tz = shell_prof[1][0] - px, shell_prof[1][1] - pz
        elif i == len(shell_prof) - 1:
            tx, tz = px - shell_prof[-2][0], pz - shell_prof[-2][1]
        else:
            tx, tz = (shell_prof[i + 1][0] - shell_prof[i - 1][0],
                      shell_prof[i + 1][1] - shell_prof[i - 1][1])
        ln = math.hypot(tx, tz) or 1.0
        nx, nz = tz / ln, -tx / ln
        verts += [(px, ys0, pz), (px + nx * thick, ys0, pz + nz * thick),
                  (px + nx * thick, ys1, pz + nz * thick), (px, ys1, pz)]
    for i in range(len(shell_prof) - 1):
        b, c = i * 4, (i + 1) * 4
        faces.append((b + 1, b + 2, c + 2, c + 1))
        faces.append((b + 0, c + 0, c + 3, b + 3))
        faces.append((b + 0, b + 1, c + 1, c + 0))
        faces.append((b + 3, c + 3, c + 2, b + 2))
    _mesh(parent, f"{bid}_shell", verts, faces, conc,
          uv=_auto_uv(verts, faces), smooth=False)
    # shell ribs on the outer face
    rib_b = Batch(parent, f"{bid}_shellribs", conc_g)
    for k in range(1, 5):
        yy = ys0 + (ys1 - ys0) * k / 5.0
        for i in range(len(shell_prof) - 1):
            px0, pz0 = shell_prof[i]
            px1, pz1 = shell_prof[i + 1]
            ang = math.atan2(pz1 - pz0, px1 - px0)
            rib_b.box(((px0 + px1) / 2 - 0.12, yy, (pz0 + pz1) / 2),
                      (math.hypot(px1 - px0, pz1 - pz0) * 1.05, 0.14, 0.14),
                      rot=(0, -ang, 0))
    rib_b.build()
    # cantilevered glazed control box hanging beside the shell
    cb_x0 = bx0 - 0.75
    kit.box(parent, f"{bid}_cant", (cb_x0 + 1.5, ys0 - 1.4, 5.65),
            (3.0, 4.2, 2.2), conc)
    kit.box(parent, f"{bid}_cant_gl", (cb_x0 + 0.02, ys0 - 1.4, 5.65),
            (0.08, 3.9, 1.5), glass)
    kit.box(parent, f"{bid}_cant_gl2", (cb_x0 + 1.5, ys0 - 3.48, 5.65),
            (2.9, 0.08, 1.5), glass)
    for i in range(4):
        kit.box(parent, f"{bid}_cm{i}", (cb_x0 - 0.04, ys0 - 2.9 + i * 0.9, 5.65),
                (0.05, 0.06, 1.6), iron)
    kit.box(parent, f"{bid}_cant_brow", (cb_x0 + 1.4, ys0 - 1.4, 6.85),
            (3.2, 4.4, 0.22), granite)

    # ================= folded-plate fins + ribbon windows, other facades =====
    def _folded(face, L, z0, z1, base_x, base_y, u, n, n_fin=8):
        """Continuous folded-plate concrete wall in front of a ribbon window."""
        fb = Batch(parent, f"{bid}_fins_{face}", fin)
        ribbon = Batch(parent, f"{bid}_rib_{face}", glass)
        mull = Batch(parent, f"{bid}_ribm_{face}", granite)
        base_ang = math.atan2(u[1], u[0])
        start = 0.9
        panel_w = (L - 2 * start) / n_fin
        for i in range(n_fin):
            s = start + (i + 0.5) * panel_w
            cx_ = base_x + u[0] * s
            cy_ = base_y + u[1] * s
            side = -1 if i % 2 == 0 else 1
            ang = base_ang + side * math.radians(24)
            fx = cx_ + n[0] * 0.26
            fy = cy_ + n[1] * 0.26
            fb.box((fx, fy, (z0 + z1) / 2),
                   (panel_w / math.cos(math.radians(24)) + 0.03, 0.32,
                    z1 - z0), rot=(0, 0, ang))
        # continuous ribbon window behind the folds
        ribbon.box((base_x + u[0] * (L / 2) + n[0] * 0.05,
                    base_y + u[1] * (L / 2) + n[1] * 0.05, (z0 + z1) / 2),
                   (L - 1.9, 0.08, (z1 - z0) * 0.86), rot=(0, 0, base_ang))
        for zi in ((z0 + z1) / 2 - (z1 - z0) * 0.30,
                   (z0 + z1) / 2 + (z1 - z0) * 0.30):
            mull.box((base_x + u[0] * (L / 2) + n[0] * 0.09,
                      base_y + u[1] * (L / 2) + n[1] * 0.09, zi),
                     (L - 1.9, 0.12, 0.16), rot=(0, 0, base_ang))
        for k in range(6):
            s = start + (L - 2 * start) * (k + 0.5) / 6
            mull.box((base_x + u[0] * s + n[0] * 0.09,
                      base_y + u[1] * s + n[1] * 0.09, (z0 + z1) / 2),
                     (0.12, 0.12, (z1 - z0) * 0.86), rot=(0, 0, base_ang))
        fb.build()
        ribbon.build()
        mull.build()

    Lw = by1 - by0
    _folded("PY", bx1 - bx0, 0.9, 5.6, bx0, by1, (1, 0), (0, 1), 7)
    _folded("NY", bx1 - bx0, 0.9, 5.6, bx0, by0, (1, 0), (0, -1), 7)
    _folded("PX", Lw, 0.9, 5.6, bx1, by0, (0, 1), (1, 0), 7)
    # west facade north bay (south bay has the entrance/shell)
    _folded("NXn", 4.6, 0.9, 5.6, bx0, by0 + 0.0, (0, 1), (-1, 0), 2)

    # ================= cylindrical tower with ribbon-glass bands ==========
    kit.cyl(parent, f"{bid}_tcore", (tcx, tcy, (0.55 + z_tower) / 2), r_t,
            z_tower - 0.55, conc, verts=32)
    for bi, (zb, zt) in enumerate(((7.35, 8.85), (9.55, 11.05), (11.75, 13.25))):
        kit.cyl(parent, f"{bid}_tg{bi}", (tcx, tcy, (zb + zt) / 2), r_t + 0.06,
                zt - zb, glass, verts=32)
        kit.cyl(parent, f"{bid}_tgs{bi}", (tcx, tcy, zb - 0.12), r_t + 0.14, 0.24,
                conc_g, verts=32)
        for i in range(24):
            a = 2 * math.pi * i / 24
            kit.box(parent, f"{bid}_tm{bi}{i}",
                    (tcx + math.cos(a) * (r_t + 0.10),
                     tcy + math.sin(a) * (r_t + 0.10), (zb + zt) / 2),
                    (0.08, 0.10, zt - zb), iron, rot=(0, 0, a))
    # vertical fins around the tower
    for i in range(12):
        a = 2 * math.pi * i / 12 + math.pi / 12
        kit.box(parent, f"{bid}_tfin{i}",
                (tcx + math.cos(a) * (r_t + 0.24),
                 tcy + math.sin(a) * (r_t + 0.24), (6.6 + z_tower) / 2),
                (0.36, 0.30, z_tower - 6.6), fin, rot=(0, 0, a))
    # tower roof + railing + masts + satellite dishes
    kit.cyl(parent, f"{bid}_troof", (tcx, tcy, z_tower + 0.16), r_t + 0.35, 0.32,
            conc, verts=32)
    kit.cyl(parent, f"{bid}_tband", (tcx, tcy, z_tower + 0.36), r_t + 0.42, 0.1,
            granite, verts=32)
    for i in range(16):
        a = 2 * math.pi * i / 16
        kit.box(parent, f"{bid}_tpost{i}",
                (tcx + math.cos(a) * (r_t + 0.2), tcy + math.sin(a) * (r_t + 0.2),
                 z_tower + 0.75), (0.05, 0.05, 0.7), iron)
    for k, hh in enumerate((0.45, 0.75)):
        kit.cyl(parent, f"{bid}_trail{k}", (tcx, tcy, z_tower + hh), r_t + 0.2,
                0.04, iron, verts=32)
    for i, (mx, my, mh) in enumerate(((-1.6, -1.2, 1.5), (1.4, -1.6, 1.2),
                                      (0.3, 1.5, 1.8))):
        kit.cyl(parent, f"{bid}_mast{i}", (tcx + mx, tcy + my, z_tower + 0.3 + mh / 2),
                0.05, mh, iron, verts=6)
        for k in range(3):
            kit.box(parent, f"{bid}_mastb{i}{k}",
                    (tcx + mx, tcy + my, z_tower + 0.8 + k * 0.45),
                    (0.7 - k * 0.16, 0.04, 0.04), iron)
    # satellite dishes
    for i, (dx, dy, dz, rz) in enumerate(((1.9, 1.1, 15.15, -35),
                                          (-1.9, 1.9, 15.05, 25))):
        kit.cyl(parent, f"{bid}_dpost{i}", (tcx + dx, tcy + dy, z_tower + 0.55),
                0.07, 1.1, iron, verts=6)
        kit.cyl(parent, f"{bid}_dish{i}", (tcx + dx, tcy + dy, dz), 0.95, 0.10,
                conc_g, rot=(math.radians(62), 0, math.radians(rz)), verts=20)
        kit.cyl(parent, f"{bid}_dishfeed{i}", (tcx + dx, tcy + dy, dz + 0.5), 0.04,
                0.75, iron, verts=5)
    # sawtooth rooflight strip on the base roof (crisp modernist roofline)
    z_teeth = z_base + 0.15
    for i in range(3):
        x_a = bx0 + 2.4 + i * 3.0
        kit.box(parent, f"{bid}_tooth{i}", (x_a + 1.5, by0 + 5.4, z_teeth + 1.05),
                (3.6, 5.2, 0.22), conc, rot=(0, math.radians(-34), 0))
        kit.box(parent, f"{bid}_toothg{i}", (x_a + 3.05, by0 + 5.4, z_teeth + 1.05),
                (0.10, 5.0, 1.9), glass, rot=(0, math.radians(-4), 0))
        kit.box(parent, f"{bid}_toothf{i}", (x_a + 3.05, by0 + 5.4,
                                             z_teeth + 2.02),
                (0.34, 5.3, 0.16), conc_g)
    # stepped corner mass (echoes the round-3 stepped end tower)
    kit.box(parent, f"{bid}_step1", (bx1 - 2.6, by1 - 2.4, z_base + 0.9),
            (4.6, 4.4, 1.8), conc)
    kit.box(parent, f"{bid}_step2", (bx1 - 3.1, by1 - 2.9, z_base + 2.6),
            (3.6, 3.4, 1.6), conc)
    kit.box(parent, f"{bid}_step3", (bx1 - 3.6, by1 - 3.4, z_base + 4.0),
            (2.6, 2.4, 1.4), conc)
    # roof plant on the base
    kit.box(parent, f"{bid}_plant", (bx0 + 12.4, by0 + 2.2, z_base + 0.75),
            (3.4, 2.6, 1.5), conc_g)
    kit.box(parent, f"{bid}_skyl", (bx0 + 7.0, by0 + 3.4, z_base + 0.35),
            (6.0, 1.4, 0.6), glass_c)
    kit.box(parent, f"{bid}_parapet_e", (bx1 - 0.15, (by0 + by1) / 2, z_base + 0.28),
            (0.3, by1 - by0, 0.56), conc)
    kit.box(parent, f"{bid}_parapet_n", ((bx0 + bx1) / 2, by1 - 0.15, z_base + 0.28),
            (bx1 - bx0, 0.3, 0.56), conc)

    # ================= robot-arm sculpture on a granite plinth ============
    rx_, ry_ = bx0 + 2.4, by0 + 2.7
    kit.box(parent, f"{bid}_ra_base", (rx_, ry_, 1.0), (2.4, 2.4, 1.0), granite)
    kit.cyl(parent, f"{bid}_ra_turn", (rx_, ry_, 1.72), 0.62, 0.6, iron, verts=14)
    kit.box(parent, f"{bid}_ra_s1", (rx_ - 0.75, ry_, 2.85), (1.9, 0.72, 0.78),
            conc, rot=(0, math.radians(-42), 0))
    kit.cyl(parent, f"{bid}_ra_j1", (rx_ - 0.42, ry_, 2.42), 0.28, 0.9, iron,
            rot=(0, math.radians(90), 0), verts=12)
    kit.box(parent, f"{bid}_ra_s2", (rx_ - 2.15, ry_, 3.9), (1.55, 0.60, 0.64),
            fin, rot=(0, math.radians(24), 0))
    kit.cyl(parent, f"{bid}_ra_j2", (rx_ - 2.95, ry_, 4.28), 0.24, 0.8, iron,
            rot=(0, math.radians(90), 0), verts=12)
    kit.box(parent, f"{bid}_ra_s3", (rx_ - 3.55, ry_, 4.95), (1.15, 0.46, 0.50),
            conc, rot=(0, math.radians(-55), 0))
    for side in (-1, 1):
        kit.box(parent, f"{bid}_ra_jaw{side}", (rx_ - 4.15, ry_ + side * 0.24, 5.55),
                (0.5, 0.14, 0.6), bronze, rot=(0, math.radians(-58), 0))
    for k, (px, py) in enumerate(((rx_ + 0.9, ry_ + 0.9), (rx_ - 0.9, ry_ - 0.9))):
        kit.cyl(parent, f"{bid}_ra_bolt{k}", (px, py, 1.55), 0.10, 0.12, iron,
                verts=8)

    # subtle weathering streaks only (clean landmark)
    wb = Batch(parent, f"{bid}_stains", kit.mat_solid("LMA_rob_stain",
                                                      "#8B867C", rough=0.95))
    for i in range(10):
        sx = bx0 + 1.0 + rng.random() * (bx1 - bx0 - 2.0)
        wb.box((sx, by0 - 0.02, rng.uniform(2.0, 5.2)),
               (rng.uniform(0.12, 0.3), 0.04, rng.uniform(0.4, 1.4)))
    wb.build()
    st["objects"] = sum(1 for o in bpy.data.objects if o.name.startswith(bid))
    st["tower_h"] = z_tower + 2.3
    return st

# 2) MINISTRY OF TRANSPORT  (rect [174,-58,198,-40], faces NX, h 28)
# ===========================================================================
def build_ministry_transport(parent, block, rng, ctx):
    """Ministry of Transport — monumental granite-fin slab with a transport relief.

    Eight-storey modernist slab clad in grey granite with deep vertical fins and
    recessed dark ribbon windows; the west facade carries a recessed bronze-and-glass
    entrance portal below a large carved transport relief panel (plane / train / ship),
    gold ministry lettering, angled flagpoles on the roof gallery, a dark louvred
    rooftop plant block and antennae.
    """
    bid = block["id"]
    x0, y0, x1, y1 = block["rect"]
    st = {"type": "ministry_transport", "windows": 0}
    granite = lmm.mat("granite_grey")
    granite_d = lmm.mat("granite_dark")
    marble = lmm.mat("marble_white")
    conc = lmm.mat("concrete_white")
    conc_g = lmm.mat("concrete_grey")
    glass = lmm.mat("glass_ribbon")
    glass_c = lmm.mat("glass_clean")
    bronze = lmm.mat("bronze")
    gold = lmm.mat("gold")
    iron = lmm.mat("iron_dark")
    relief = lmm.mat("relief_stone")
    relief_b = lmm.mat("bas_relief_bronze")
    lit = _acc("mot_warm", "#FFC98A", 1.4)
    lit_cool = _acc("mot_cool", "#DCE8F4", 1.1)
    flagmat = kit.mat_solid("LMA_mot_flag", "#8C3229", rough=0.9)

    bx0, bx1 = x0 + 1.2, x1 - 1.2
    by0, by1 = y0 + 1.5, y1 - 1.5
    z_g, z_top = 5.2, 26.4
    floors = 7
    fh = (z_top - z_g) / floors
    cx = (bx0 + bx1) / 2.0
    cy = (by0 + by1) / 2.0
    Lw = by1 - by0
    Ln = bx1 - bx0

    # ================= granite plinth + steps =================
    kit.box_mm(parent, f"{bid}_plinth", bx0 - 0.45, by0 - 0.45, 0.0,
               bx1 + 0.45, by1 + 0.45, 0.55, granite_d)
    for k in range(3):
        kit.box(parent, f"{bid}_step{k}", (bx0 - 0.55 - k * 0.45, cy,
                                          0.16 + k * 0.20),
                (0.45, 8.4, 0.22 + k * 0.02), granite)

    # ================= recessed glazed ground floor =================
    kit.box(parent, f"{bid}_gg_w", (bx0 + 1.75, cy, 2.75),
            (0.12, Lw - 1.2, 4.4), glass)
    kit.box(parent, f"{bid}_gg_e", (bx1 - 1.75, cy, 2.75),
            (0.12, Lw - 1.2, 4.4), glass)
    kit.box(parent, f"{bid}_gg_n", (cx, by1 - 1.75, 2.75),
            (Ln - 1.2, 0.12, 4.4), glass)
    kit.box(parent, f"{bid}_gg_s", (cx, by0 + 1.75, 2.75),
            (Ln - 1.2, 0.12, 4.4), glass)
    for i in range(8):
        kit.box(parent, f"{bid}_gm{i}",
                (bx0 + 1.68, by0 + 1.2 + i * (Lw - 2.4) / 7.0, 2.75),
                (0.08, 0.08, 4.4), iron)
    kit.box(parent, f"{bid}_gg_glow", (bx0 + 1.82, cy, 1.9),
            (0.04, 5.4, 2.6), lit)
    for k in range(4):
        kit.box(parent, f"{bid}_gg_lit{k}", (bx0 + 1.68, cy - 4.6 + k * 3.1, 2.5),
                (0.04, 1.5, 2.6), lit)
    kit.box(parent, f"{bid}_baseband", (cx, cy, 0.95),
            (Ln + 0.55, Lw + 0.55, 0.9), granite_d)
    for i in range(6):
        kit.cyl(parent, f"{bid}_gcol{i}", (bx0 + 0.55, by0 + 2.2 + i * 2.2, 2.6),
                0.30, 4.2, granite, verts=12)
    kit.box(parent, f"{bid}_ground_slab", (cx, cy, z_g - 0.25),
            (Ln + 0.5, Lw + 0.5, 0.5), conc)

    # ================= upper slab body =================
    kit.box(parent, f"{bid}_slab", (cx, cy, (z_g + z_top) / 2),
            (Ln, Lw, z_top - z_g), granite)

    # ================= fin + ribbon-window facades =================
    def _fin_facade(face, base_pt, u, n, L, n_bay, skip=()):
        base_ang = math.atan2(u[1], u[0])
        step = (L - 1.2) / n_bay
        for i in range(n_bay + 1):
            s = 0.6 + i * step
            if i in skip:
                continue
            px = base_pt[0] + u[0] * s + n[0] * 0.44
            py = base_pt[1] + u[1] * s + n[1] * 0.44
            kit.box(parent, f"{bid}_{face}_fin{i}", (px, py,
                                                    (z_g + z_top) / 2 + 0.1),
                    (0.56, 0.88, z_top - z_g + 0.2), granite,
                    rot=(0, 0, base_ang))
        # ribbon windows + spandrels per bay / floor
        for i in range(n_bay):
            if i in skip:
                continue
            s = 0.6 + (i + 0.5) * step
            px = base_pt[0] + u[0] * s + n[0] * 0.10
            py = base_pt[1] + u[1] * s + n[1] * 0.10
            kit.box(parent, f"{bid}_{face}_rib{i}", (px, py,
                                                     (z_g + z_top) / 2 + 0.1),
                    (step - 0.9, 0.09, z_top - z_g - 0.9), glass,
                    rot=(0, 0, base_ang))
            for f in range(floors + 1):
                zz = z_g + f * fh
                kit.box(parent, f"{bid}_{face}_sp{i}{f}", (px, py, zz),
                        (step - 0.8, 0.20, 0.42), conc,
                        rot=(0, 0, base_ang))
        # cornice
        px = base_pt[0] + u[0] * (L / 2) + n[0] * 0.5
        py = base_pt[1] + u[1] * (L / 2) + n[1] * 0.5
        kit.box(parent, f"{bid}_{face}_corn", (px, py, z_top + 0.15),
                (L, 0.6, 0.5), marble, rot=(0, 0, base_ang))
        for f in range(floors + 1):
            zz = z_g + f * fh
            kit.box(parent, f"{bid}_{face}_band{f}", (px, py, zz),
                    (L, 0.4, 0.3), granite_d, rot=(0, 0, base_ang))

    _fin_facade("W", (bx0, by0), (0, 1), (-1, 0), Lw, 5, skip=(2, 3))
    _fin_facade("E", (bx1, by0), (0, 1), (1, 0), Lw, 5)
    _fin_facade("N", (bx0, by1), (1, 0), (0, 1), Ln, 8)
    _fin_facade("S", (bx0, by0), (1, 0), (0, -1), Ln, 8)

    # ================= entrance portal + relief panel + sign =================
    ez0 = 0.55
    # portal frame (granite surround projecting 1.2 m)
    kit.box(parent, f"{bid}_portal_l", (bx0 - 0.6, cy - 3.8, 4.4),
            (1.4, 0.7, 8.2), granite)
    kit.box(parent, f"{bid}_portal_r", (bx0 - 0.6, cy + 3.8, 4.4),
            (1.4, 0.7, 8.2), granite)
    kit.box(parent, f"{bid}_portal_top", (bx0 - 0.6, cy, 8.3),
            (1.4, 8.3, 0.8), granite)
    kit.box(parent, f"{bid}_portal_back", (bx0 - 0.05, cy, 4.3),
            (0.12, 6.6, 7.4), iron)
    kit.box(parent, f"{bid}_door", (bx0 - 0.12, cy, 3.2), (0.10, 5.4, 5.0), bronze)
    for k in (-1, 0, 1):
        kit.box(parent, f"{bid}_dsplit{k}", (bx0 - 0.18, cy + k * 1.75, 3.2),
                (0.05, 0.09, 5.0), iron)
    kit.box(parent, f"{bid}_dglow", (bx0 - 0.20, cy, 4.9), (0.04, 4.8, 0.5),
            lit_cool)
    kit.box(parent, f"{bid}_dcanopy", (bx0 - 1.15, cy, 8.55), (2.3, 8.6, 0.28),
            granite_d)
    for k in (-1, 1):
        kit.cyl(parent, f"{bid}_dcl{k}", (bx0 - 1.15, cy + k * 3.4,
                                                          8.55), 0.10, 0.5, iron,
                verts=8)
    for k in (-1, 0, 1):
        kit.box(parent, f"{bid}_dlight{k}", (bx0 - 1.2, cy + k * 1.8, 8.36),
                (0.24, 0.5, 0.06), lit)
    # big transport relief panel over the portal
    rel_w, rel_h = 7.2, 5.6
    rel_cx = bx0 - 1.10
    rel_cz = 14.1
    kit.box(parent, f"{bid}_rel_frame", (rel_cx, cy, rel_cz),
            (0.55, rel_w + 0.9, rel_h + 0.9), granite)
    kit.box(parent, f"{bid}_rel_bg", (rel_cx - 0.32, cy, rel_cz),
            (0.16, rel_w, rel_h), relief)
    # abstract relief: plane (upper left), ship (right), train (lower left)
    kit.box(parent, f"{bid}_rel_plane", (rel_cx - 0.44, cy - 1.35, rel_cz + 1.45),
            (0.22, 3.3, 0.48), relief_b, rot=(0, math.radians(-6), 0.22))
    for k in (-1, 1):
        kit.box(parent, f"{bid}_rel_wing{k}",
                (rel_cx - 0.44, cy - 1.35 + k * 0.62, rel_cz + 1.45),
                (0.20, 1.7, 0.28), relief_b,
                rot=(0, math.radians(-6), 0.22 + k * 0.5))
    kit.box(parent, f"{bid}_rel_ship", (rel_cx - 0.44, cy + 1.95, rel_cz + 0.15),
            (0.22, 2.2, 1.15), relief_b, rot=(0, 0, -0.08))
    kit.box(parent, f"{bid}_rel_deck", (rel_cx - 0.44, cy + 1.7, rel_cz + 1.0),
            (0.22, 1.05, 0.65), relief_b)
    kit.box(parent, f"{bid}_rel_train", (rel_cx - 0.44, cy - 1.25, rel_cz - 0.95),
            (0.22, 2.4, 1.0), relief_b)
    kit.box(parent, f"{bid}_rel_rail", (rel_cx - 0.44, cy - 1.25, rel_cz - 1.58),
            (0.18, 3.6, 0.12), relief)
    for k in range(4):
        kit.box(parent, f"{bid}_rel_rl{k}", (rel_cx - 0.42, cy - 2.4 + k * 0.8,
                                             rel_cz - 1.42),
                (0.16, 0.1, 0.22), relief_b)
    kit.box(parent, f"{bid}_rel_wave", (rel_cx - 0.44, cy + 0.6, rel_cz - 0.85),
            (0.18, 2.3, 0.18), relief)
    # ministry lettering above the relief
    kit.box(parent, f"{bid}_signbg", (bx0 - 0.95, cy, 18.75), (0.22, 11.0, 1.10),
            granite_d)
    kit.box(parent, f"{bid}_sign_frame", (bx0 - 1.06, cy, 18.75),
            (0.04, 11.2, 1.20), bronze)
    kit.text3d(parent, f"{bid}_signtxt", "МИНИСТЕРСТВО ТРАНСПОРТА",
               (bx0 - 1.10, cy, 18.75), 0.62, gold,
               rot=(math.radians(90), 0, math.radians(-90)))
    # entrance pylon with the Russian transport ministry crest
    py_ = by1 - 1.6
    kit.box(parent, f"{bid}_pylon", (bx0 - 0.9, py_, 3.6), (1.5, 1.4, 7.2), granite)
    kit.box(parent, f"{bid}_pylon_cap", (bx0 - 1.0, py_, 7.35), (1.8, 1.7, 0.3),
            marble)
    kit.box(parent, f"{bid}_pylon_plaq", (bx0 - 1.68, py_, 5.2), (0.08, 1.0, 1.4),
            bronze)
    kit.text3d(parent, f"{bid}_pylon_txt", "МТ", (bx0 - 1.74, py_, 5.2), 0.5, gold,
               rot=(math.radians(90), 0, math.radians(-90)))
    # flagpoles on the roof gallery (angled out over the facade)
    for k, (fy, fh) in enumerate(((cy - 2.8, 1.2), (cy, 1.5), (cy + 2.8, 1.2))):
        kit.cyl(parent, f"{bid}_flagp{k}", (bx0 + 1.1, fy, z_top + 0.8 + fh / 2),
                0.05, fh, iron, verts=6)
        kit.box(parent, f"{bid}_flag{k}", (bx0 + 0.66, fy + 0.5,
                                           z_top + 0.8 + fh),
                (0.05, 1.05, 0.62), flagmat)
    # ================= roof: parapet, railing, plant, antennae =================
    kit.parapet(parent, f"{bid}_parapet", (bx0, by0, bx1, by1), z_top, 0.9, 0.3,
                granite, coping_mat=marble)
    kit.box(parent, f"{bid}_plant", (bx1 - 4.6, by0 + 4.2, z_top + 1.15),
            (6.4, 4.6, 2.1), conc_g)
    kit.box(parent, f"{bid}_plant_louv", (bx1 - 4.6, by0 + 1.85, z_top + 1.15),
            (6.0, 0.10, 1.7), iron)
    kit.box(parent, f"{bid}_plant_cap", (bx1 - 4.6, by0 + 4.2, z_top + 2.28),
            (6.7, 4.9, 0.16), iron)
    kit.cyl(parent, f"{bid}_ant1", (bx1 - 2.0, by0 + 2.4, z_top + 1.6), 0.05, 1.8,
            iron, verts=6)
    kit.cyl(parent, f"{bid}_ant2", (bx1 - 7.0, by0 + 2.0, z_top + 1.35), 0.04, 1.3,
            iron, verts=6)
    for k in range(3):
        kit.box(parent, f"{bid}_antb{k}", (bx1 - 2.0, by0 + 2.4,
                                           z_top + 0.9 + k * 0.45),
                (0.9 - k * 0.2, 0.03, 0.03), iron)
    # subtle weathering: vertical streaks under the cornice only
    wb = Batch(parent, f"{bid}_stains", kit.mat_solid("LMA_mot_stain", "#7F7C74",
                                                      rough=0.95))
    for i in range(12):
        a = rng.random()
        if a < 0.5:
            sx = bx0 - 0.9
            sy = by0 + 1.5 + rng.random() * (Lw - 3.0)
        else:
            sx = bx0 + 1.5 + rng.random() * (Ln - 3.0)
            sy = by1 + 0.02
        wb.box((sx, sy, z_top - rng.uniform(0.8, 2.6)),
               (0.12, 0.04, rng.uniform(0.4, 1.6)))
    wb.build()
    st["objects"] = sum(1 for o in bpy.data.objects if o.name.startswith(bid))
    st["floors"] = 8
    return st

# 3) SANATORIUM DRUZHBA (rect [130,-104,158,-94], faces NX, h 12)
# ===========================================================================
def build_sanatorium_druzhba(parent, block, rng, ctx):
    """Yalta «Druzhba» style curved sanatorium.

    A genuine 152-degree curved block: two storeys of round-arch arcades on stone
    piers in front of warm glazed rooms, a continuous three-row mosaic frieze band
    over the lower arcade, terrace railing, rounded corner towers with stacked
    balcony rings, roof terrace with curved penthouse, and a garden court with the
    entrance on the inner (north) side.
    """
    bid = block["id"]
    x0, y0, x1, y1 = block["rect"]
    st = {"type": "sanatorium_druzhba"}
    stone_l = lmm.mat("limestone_light")
    stone = lmm.mat("limestone")
    conc = lmm.mat("concrete_white")
    marble = lmm.mat("marble_white")
    granite = lmm.mat("granite_grey")
    granite_d = lmm.mat("granite_dark")
    glass = lmm.mat("glass_ribbon")
    glass_d = lmm.mat("glass_clean")
    bronze = lmm.mat("bronze")
    iron = lmm.mat("iron_dark")
    mos_b = lmm.mat("mosaic_blue")
    mos_g = lmm.mat("mosaic_gold")
    mos_r = lmm.mat("mosaic_red")
    lit = _acc("druzhba_warm", "#FFC07A", 1.6)
    lit_cool = _acc("druzhba_cool", "#DCE8F4", 1.2)
    soil = kit.mat_solid("LMA_druzhba_soil", "#33302A", rough=0.98)
    greenery = kit.mat_solid("LMA_druzhba_green", "#40513E", rough=0.95)

    # arc geometry: centre north of the block, arc sagging south
    C = (144.0, -91.43)
    R_out = 13.6
    R_glass = 11.30
    R_in = 9.40
    phi0, phi1 = math.radians(-76), math.radians(76)
    A0 = phi0 - math.pi / 2
    A1 = phi1 - math.pi / 2
    n_bay = 12
    pier_r = R_out - 0.25

    def P(r, phi):
        return (C[0] + math.sin(phi) * r, C[1] - math.cos(phi) * r)

    z_podium = 0.60
    z_spring1, z_apex1 = 2.80, 3.95
    z_spring2, z_apex2 = 8.30, 9.50
    z_floor2 = 5.60
    z_roof = 9.75
    z_parapet = 10.85

    # ================= podium + garden terrace =================
    _arc_ring(parent, f"{bid}_podium", C[0], C[1], R_in - 0.5, R_out + 1.05, A0,
              A1, 0.0, z_podium, stone, segs=48)
    _arc_ring(parent, f"{bid}_podium_edge", C[0], C[1], R_out + 1.05, R_out + 1.35,
              A0, A1, 0.0, 0.30, granite, segs=48)
    # garden court in the concave pocket: paving, planters, benches
    kit.box(parent, f"{bid}_court", ((x0 + x1) / 2, -97.4, 0.12),
            (24.0, 8.2, 0.24), granite_d)
    jb = Batch(parent, f"{bid}_court_joints", granite)
    for i in range(9):
        jb.box(((x0 + x1) / 2 - 11.0 + i * 2.75, -97.4, 0.245), (0.06, 8.0, 0.012))
    for i in range(3):
        jb.box(((x0 + x1) / 2, -94.0 - i * 2.6, 0.245), (23.0, 0.06, 0.012))
    jb.build()
    for i, px in enumerate((135.0, 153.0)):
        kit.box(parent, f"{bid}_planter{i}", (px, -96.2, 0.55), (2.6, 1.3, 0.62),
                stone)
        kit.box(parent, f"{bid}_soil{i}", (px, -96.2, 0.86), (2.3, 1.0, 0.12), soil)
        kit.cyl(parent, f"{bid}_shrub{i}", (px, -96.2, 1.35), 1.05, 0.9, greenery,
                verts=12)
    for k in range(4):
        kit.bench(parent, f"{bid}_court_bench{k}", (138.5 + 3.4 * k, -94.6, 0.24),
                  math.radians(0))

    # ================= continuous stone piers (both storeys) =================
    pier_b = Batch(parent, f"{bid}_piers", stone_l)
    pier_cap = Batch(parent, f"{bid}_piercaps", marble)
    pier_phi = []
    for i in range(n_bay + 1):
        phi = phi0 + (phi1 - phi0) * i / n_bay
        pier_phi.append(phi)
        px, py = P(pier_r, phi)
        pier_b.box((px, py, (z_podium + z_apex2) / 2 + 0.2),
                   (0.78, 0.72, z_apex2 - z_podium + 0.4), rot=(0, 0, phi))
        pier_cap.box((px, py, z_podium + 0.12), (1.0, 0.95, 0.24), rot=(0, 0, phi))
    pier_b.build()
    pier_cap.build()

    # ================= arcades: round arches both levels =================
    def _arcade_level(tag, zs, rise, band_top):
        for i in range(n_bay):
            pa, pb = pier_phi[i], pier_phi[i + 1]
            xa, ya = P(pier_r, pa)
            xb, yb = P(pier_r, pb)
            _arch_between(parent, f"{bid}_{tag}_arch{i}", (xa, ya, zs),
                          (xb, yb, zs), rise, 0.34, 0.42, stone_l, segs=8)
            # haunch blocks filling the spandrel corners next to the piers
            for side, phi, other in ((0, pa, pb), (1, pb, pa)):
                midphi = phi + (other - phi) * 0.09
                hx, hy = P(pier_r, midphi)
                hb = Batch(parent, f"{bid}_{tag}_h{i}{side}", stone_l)
                hb.box((hx, hy, zs + rise * 0.52), (0.62, 0.34, rise * 0.98),
                       rot=(0, 0, midphi))
                hb.build()
        # spandrel band above the arch apexes
        _arc_ring(parent, f"{bid}_{tag}_spand", C[0], C[1], R_out - 0.55, R_out + 0.12,
                  A0, A1, band_top[0], band_top[1], stone, segs=48)

    _arcade_level("g", z_spring1, 1.15, (z_apex1, 4.50))
    _arcade_level("u", z_spring2, 1.20, (z_apex2, z_roof))
    # ground-floor sill wall under the arches (low stone base between piers)
    _arc_ring(parent, f"{bid}_g_sill", C[0], C[1], R_out - 0.55, R_out + 0.10, A0,
              A1, z_podium, z_podium + 0.55, stone, segs=48)

    # ================= continuous mosaic frieze band =================
    _arc_band(parent, f"{bid}_mosaic_gold1", C[0], C[1], R_out + 0.17, A0, A1,
              4.44, 4.60, mos_g, segs=96, u_scale=1.6)
    _arc_band(parent, f"{bid}_mosaic_main", C[0], C[1], R_out + 0.17, A0, A1,
              4.60, 5.34, mos_b, segs=96, u_scale=3.4)
    _arc_band(parent, f"{bid}_mosaic_gold2", C[0], C[1], R_out + 0.17, A0, A1,
              5.34, 5.50, mos_g, segs=96, u_scale=1.6)
    _arc_ring(parent, f"{bid}_mosaic_lip", C[0], C[1], R_out + 0.10, R_out + 0.22,
              A0, A1, 4.32, 4.44, marble, segs=64, caps=False)
    _arc_ring(parent, f"{bid}_mosaic_lip2", C[0], C[1], R_out + 0.10, R_out + 0.22,
              A0, A1, 5.50, 5.62, marble, segs=64, caps=False)

        # ================= rooms + glazing behind the arcade =================
    _arc_ring(parent, f"{bid}_roomwall", C[0], C[1], R_glass + 0.40,
              R_glass + 0.52, A0, A1, z_podium, z_roof,
              lmm.mat("concrete_grey"), segs=56)
    bay_chord = 2 * R_glass * math.sin((phi1 - phi0) / (2 * n_bay))
    panel_w = bay_chord * 0.90
    for tag, zb0, zb1 in (("g", 0.95, 4.45), ("u", 6.35, 9.40)):
        for i in range(n_bay):
            phi = phi0 + (phi1 - phi0) * (i + 0.5) / n_bay
            px, py = P(R_glass, phi)
            if i % 4 == 3:
                mat = glass
            elif i % 7 == 5:
                mat = lit_cool
            else:
                mat = lit
            kit.box(parent, f"{bid}_pane{tag}{i}", (px, py, (zb0 + zb1) / 2),
                    (panel_w, 0.07, zb1 - zb0), mat, rot=(0, 0, phi))
            for side in (-1, 1):
                mx = px + math.cos(phi) * side * panel_w / 2
                my = py + math.sin(phi) * side * panel_w / 2
                kit.box(parent, f"{bid}_pfr{tag}{i}{side}", (mx, my,
                                                            (zb0 + zb1) / 2),
                        (0.10, 0.10, zb1 - zb0), iron)
        _arc_ring(parent, f"{bid}_sill{tag}", C[0], C[1], R_glass - 0.08,
                  R_glass + 0.12, A0, A1, zb0 - 0.22, zb0, marble, segs=56,
                  caps=False)
        _arc_ring(parent, f"{bid}_head{tag}", C[0], C[1], R_glass - 0.08,
                  R_glass + 0.12, A0, A1, zb1, zb1 + 0.16, marble, segs=56,
                  caps=False)

    # ================= inner (courtyard) facade + garden entrance =======
    r_in_face = R_in - 0.44
    _arc_ring(parent, f"{bid}_inner", C[0], C[1], r_in_face - 0.30, R_in + 0.40,
              A0, A1, z_podium, z_roof, stone, segs=56)
    for zz, hh in ((z_podium - 0.02, 0.30), (4.45, 0.22), (5.55, 0.26),
                   (9.45, 0.24)):
        _arc_ring(parent, f"{bid}_inband{int(zz * 10)}", C[0], C[1],
                  r_in_face - 0.44, r_in_face - 0.26, A0, A1, zz, zz + hh,
                  marble, segs=56, caps=False)
    for i in range(n_bay):
        if i in (5, 6):
            continue
        phi = phi0 + (phi1 - phi0) * (i + 0.5) / n_bay
        px, py = P(r_in_face - 0.33, phi)
        for zc, hh in ((2.55, 1.55), (7.55, 1.35)):
            kit.box(parent, f"{bid}_inw{i}{int(zc)}", (px, py, zc),
                    (1.30, 0.10, hh), glass_d, rot=(0, 0, phi))
            kit.box(parent, f"{bid}_inwf{i}{int(zc)}", (px, py, zc + hh / 2 + 0.10),
                    (1.52, 0.10, 0.10), marble, rot=(0, 0, phi))
            kit.box(parent, f"{bid}_inws{i}{int(zc)}", (px, py, zc - hh / 2 - 0.08),
                    (1.52, 0.12, 0.10), marble, rot=(0, 0, phi))
    # central entrance from the garden court
    ex, ey = P(r_in_face - 0.36, 0.0)
    kit.box(parent, f"{bid}_edoor_dark", (ex, ey, 2.0), (3.9, 0.30, 2.85), iron)
    kit.box(parent, f"{bid}_edoor", (ex, ey + 0.12, 1.90), (3.3, 0.10, 2.55),
            bronze)
    kit.box(parent, f"{bid}_edoor_gl", (ex, ey + 0.18, 2.45), (1.5, 0.03, 0.75),
            _acc("druzhba_cool2", "#CFDCEA", 0.55))
    kit.box(parent, f"{bid}_ecanopy", (ex, ey + 0.90, z_podium + 3.10),
            (4.6, 2.0, 0.22), marble)
    for k in (-1, 1):
        kit.cyl(parent, f"{bid}_ecol{k}", (ex + k * 2.0, ey + 1.65,
                                           z_podium + 1.55), 0.09, 3.0, iron,
                verts=8)
    kit.text3d(parent, f"{bid}_sign", "САНАТОРИЙ ДРУЖБА", (ex, ey + 1.94, 3.30),
               0.30, lmm.mat("gold"), rot=(math.radians(90), 0, math.pi))
    for k in range(3):
        kit.box(parent, f"{bid}_estep{k}", (ex, ey + 1.30 + k * 0.42,
                                            z_podium - 0.18 * k - 0.09),
                (4.4, 0.42, 0.18), granite)
    kit.box(parent, f"{bid}_path", (ex, -95.3, 0.265), (3.4, 8.6, 0.03),
            lmm.mat("concrete_grey"))

# ================= floor slab, upper terrace railing, roof =================
    _arc_ring(parent, f"{bid}_slab", C[0], C[1], R_in - 0.55, R_out + 0.10, A0, A1,
              z_floor2, z_floor2 + 0.30, conc, segs=56)
    # curved railing on the upper terrace
    for i in range(n_bay):
        pa = pier_phi[i] + (pier_phi[i + 1] - pier_phi[i]) * 0.10
        pb = pier_phi[i + 1] - (pier_phi[i + 1] - pier_phi[i]) * 0.10
        ra, rb = pa - math.pi / 2, pb - math.pi / 2
        _arc_ring(parent, f"{bid}_rail{i}", C[0], C[1], pier_r - 0.04,
                  pier_r + 0.06, ra, rb, 6.62, 6.70, bronze, segs=6, caps=False)
        _arc_ring(parent, f"{bid}_railb{i}", C[0], C[1], pier_r - 0.04,
                  pier_r + 0.06, ra, rb, 6.05, 6.11, bronze, segs=6, caps=False)
        for k in range(1, 4):
            phi = pa + (rb - ra) * k / 4
            px, py = P(pier_r, phi)
            kit.box(parent, f"{bid}_rp{i}{k}", (px, py, 6.35), (0.05, 0.05, 0.62),
                    bronze, rot=(0, 0, phi))
    _arc_ring(parent, f"{bid}_roof", C[0], C[1], R_in - 0.55, R_out + 0.22, A0, A1,
              z_roof, z_roof + 0.30, conc, segs=56)
    _arc_ring(parent, f"{bid}_parapet", C[0], C[1], R_out - 0.10, R_out + 0.24, A0,
              A1, z_roof + 0.30, z_parapet, stone_l, segs=56)
    _arc_ring(parent, f"{bid}_coping", C[0], C[1], R_out - 0.16, R_out + 0.30, A0,
              A1, z_parapet, z_parapet + 0.14, marble, segs=56)
    # inner parapet + roof terrace
    _arc_ring(parent, f"{bid}_parapet_in", C[0], C[1], R_in - 0.45, R_in - 0.10,
              A0, A1, z_roof + 0.30, z_roof + 1.0, stone_l, segs=56)
    # curved penthouse on the roof (like the round service drums in the reference)
    pc = (144.0, -99.2)
    kit.cyl(parent, f"{bid}_pent", (pc[0], pc[1], 11.05), 3.1, 2.1, stone_l, verts=24)
    kit.cyl(parent, f"{bid}_pent_glass", (pc[0], pc[1], 11.75), 3.16, 0.9, glass_d,
            verts=24)
    kit.cyl(parent, f"{bid}_pent_band", (pc[0], pc[1], 10.05), 3.35, 0.22, marble,
            verts=24)
    kit.cyl(parent, f"{bid}_pent_roof", (pc[0], pc[1], 12.05), 3.3, 0.18, conc,
            verts=24)
    kit.cyl(parent, f"{bid}_pent_mast", (pc[0], pc[1], 12.30), 0.05, 0.4, iron,
            verts=6)
    # roof terrace benches / planters
    for i in range(3):
        kit.bench(parent, f"{bid}_roofbench{i}", (138.0 + i * 5.0, -102.6, 10.05),
                  math.radians(180))

    # ================= rounded corner towers with balcony rings =================
    for ti, (tx, ty) in enumerate(((131.6, -96.2), (156.4, -96.2))):
        kit.cyl(parent, f"{bid}_tw{ti}_base", (tx, ty, 0.35), 2.25, 0.7, granite,
                verts=20)
        kit.cyl(parent, f"{bid}_tw{ti}_core", (tx, ty, 6.0), 1.95, 10.4, conc,
                verts=20)
        for k in range(5):
            zz = 1.05 + k * 2.0
            kit.cyl(parent, f"{bid}_tw{ti}_bal{k}", (tx, ty, zz), 2.30, 0.16,
                    marble, verts=20)
            kit.cyl(parent, f"{bid}_tw{ti}_bal2{k}", (tx, ty, zz + 0.21), 2.10,
                    0.20, glass_d, verts=20)
        for k in range(10):
            a = 2 * math.pi * k / 10
            kit.cyl(parent, f"{bid}_tw{ti}_col{k}",
                    (tx + math.cos(a) * 2.20, ty + math.sin(a) * 2.20, 5.6), 0.07,
                    10.0, conc, verts=6)
        for k in range(5):
            zz = 1.05 + k * 2.0
            kit.cyl(parent, f"{bid}_tw{ti}_rail{k}", (tx, ty, zz + 0.52), 2.26,
                    0.05, bronze, verts=20)
            kit.cyl(parent, f"{bid}_tw{ti}_rail2{k}", (tx, ty, zz + 0.90), 2.26,
                    0.04, bronze, verts=20)
        kit.cyl(parent, f"{bid}_tw{ti}_cap", (tx, ty, 11.35), 2.35, 0.25, marble,
                verts=20)
        kit.cyl(parent, f"{bid}_tw{ti}_drum", (tx, ty, 11.68), 1.55, 0.66, stone_l,
                verts=16)
        kit.cyl(parent, f"{bid}_tw{ti}_dr", (tx, ty, 12.06), 1.75, 0.14, conc,
                verts=16)

    # ================= weathering accents (subtle) =================
    wb = Batch(parent, f"{bid}_stains", kit.mat_solid("LMA_dzw_stain",
                                                      "#8B867C", rough=0.95))
    for i in range(18):
        phi = phi0 + (phi1 - phi0) * rng.random()
        px, py = P(R_out + 0.16, phi)
        wb.box((px, py, 4.4 - rng.uniform(0.05, 0.25)),
               (rng.uniform(0.1, 0.3), 0.05, rng.uniform(0.2, 0.6)),
               rot=(0, 0, phi))
    wb.build()
    st["objects"] = sum(1 for o in bpy.data.objects if o.name.startswith(bid))
    st["arc_sweep_deg"] = 152
    return st

# ===========================================================================
# 4) ARMENIAN "FLYING SAUCER" BUS STATION  (rect [-72,-128,-52,-118], faces PY)
# ===========================================================================
def build_bus_station_ufo(parent, block, rng, ctx):
    """Armenian «flying saucer» bus station.

    Wide 21 m lens-shaped concrete saucer: flat-ish domed top, pronounced rim that
    overhangs a continuous clerestory glass ribbon, ribbed inverted-cone underside,
    six slender splayed legs, round glazed ticket hall beneath, stone podium with
    curved benches, under-disc light ring, lantern + red aviation light.
    """
    bid = block["id"]
    x0, y0, x1, y1 = block["rect"]
    st = {"type": "bus_station_ufo", "windows": 0}
    cx, cy = (x0 + x1) / 2.0, (y0 + y1) / 2.0
    conc = lmm.mat("concrete_white")
    conc_g = lmm.mat("concrete_grey")
    granite = lmm.mat("granite_grey")
    granite_d = lmm.mat("granite_dark")
    stone = lmm.mat("stone_rusticated")
    glass = lmm.mat("glass_clean")
    iron = lmm.mat("iron_dark")
    bronze = lmm.mat("bronze")
    gold = lmm.mat("gold")
    lit_warm = _acc("lit_warm", "#FFB268", 1.5)
    red = _acc("aviation_red", "#FF3A24", 20.0, rough=0.4)
    rust = kit.mat_solid("LMA_ufo_rust", "#7A5A42", rough=0.9)
    R = 10.6
    segs = 72

    # ================= stone podium =================
    kit.cyl(parent, f"{bid}_step", (cx, cy, 0.13), 11.25, 0.26, granite_d, verts=48)
    kit.cyl(parent, f"{bid}_platform", (cx, cy, 0.44), 10.55, 0.36, granite, verts=48)
    kit.cyl(parent, f"{bid}_platform_top", (cx, cy, 0.635), 10.30, 0.05, conc_g,
            verts=48)
    jb = Batch(parent, f"{bid}_paving", granite_d)
    for i in range(24):
        a = 2 * math.pi * i / 24
        jb.box((cx + math.cos(a) * 6.5, cy + math.sin(a) * 6.5, 0.665),
               (8.0, 0.045, 0.015), rot=(0, 0, a + math.pi / 2))
    jb.build()
    # curved stone benches
    for i, adeg in enumerate((38, 142, 218, 322)):
        a0 = math.radians(adeg - 11)
        a1 = math.radians(adeg + 11)
        _arc_ring(parent, f"{bid}_bench{i}", cx, cy, 7.10, 8.00, a0, a1, 0.66,
                  0.98, stone, segs=10)
        _arc_ring(parent, f"{bid}_bench_top{i}", cx, cy, 7.06, 8.04, a0, a1, 0.98,
                  1.03, lmm.mat("granite_polished"), segs=10)
    for i in range(8):
        a = 2 * math.pi * i / 8 + math.pi / 8
        bx = cx + math.cos(a) * 9.85
        by = cy + math.sin(a) * 9.85
        kit.cyl(parent, f"{bid}_bollard{i}", (bx, by, 0.90), 0.09, 0.50, iron,
                verts=10)
        kit.box(parent, f"{bid}_bollard_g{i}", (bx, by, 1.12), (0.13, 0.13, 0.05),
                lit_warm)

    # ================= six slender splayed legs =================
    steel = lmm.mat("metal_fin")
    leg_b = Batch(parent, f"{bid}_legs", conc)
    cap_b = Batch(parent, f"{bid}_legsteel", steel)
    rust_b = Batch(parent, f"{bid}_legrust", rust)
    for i in range(6):
        a = 2 * math.pi * i / 6 + math.radians(30)
        rt, rb = 8.45, 10.55
        top = (cx + math.cos(a) * rt, cy + math.sin(a) * rt, 5.58)
        bot = (cx + math.cos(a) * rb, cy + math.sin(a) * rb, 0.66)
        mid = (cx + math.cos(a) * (rt + rb) / 2, cy + math.sin(a) * (rt + rb) / 2,
               (5.58 + 0.66) / 2)
        leg_b.seg(bot, mid, 0.36, 0.58)
        leg_b.seg(mid, top, 0.44, 0.70)
        # steel capital: bolted plate assembly under the disc
        cap_b.obox((top[0], top[1], top[2] + 0.02), (0.98, 0.74, 0.60),
                   (math.cos(a), math.sin(a), 0), (-math.sin(a), math.cos(a), 0),
                   (0, 0, 1))
        for k in (-0.28, 0.0, 0.28):
            for j in (-0.20, 0.20):
                bx = top[0] + math.cos(a) * j - math.sin(a) * k
                by = top[1] + math.sin(a) * j + math.cos(a) * k
                kit.cyl(parent, f"{bid}_bolt{i}{k}{j}", (bx, by, top[2] + 0.34),
                        0.045, 0.06, lmm.mat("iron_dark"), verts=8)
        for k in (-1, 1):
            gx = top[0] - math.sin(a) * 0.5 * k
            gy = top[1] + math.cos(a) * 0.5 * k
            kit.box(parent, f"{bid}_gusset{i}{k}", (gx, gy, top[2] - 0.18),
                    (0.10, 0.52, 0.42), steel, rot=(0, 0, a))
        for k in (-0.10, 0.11):
            px = cx + math.cos(a) * 8.62 - math.sin(a) * k
            py = cy + math.sin(a) * 8.62 + math.cos(a) * k
            rust_b.box((px, py, 4.30), (0.09, 0.04, 2.5), rot=(0, 0, a))
    leg_b.build()
    cap_b.build()
    rust_b.build()

    # ================= central glazed ticket hall =================
    kit.cyl(parent, f"{bid}_hall_base", (cx, cy, 1.55), 3.46, 2.16, lmm.mat("granite_polished"), verts=32)
    kit.cyl(parent, f"{bid}_hall_collar", (cx, cy, 2.60), 3.56, 0.16, granite,
            verts=32)
    kit.cyl(parent, f"{bid}_hall_floor", (cx, cy, 0.66), 3.40, 0.04, granite, verts=32)
    jb2 = Batch(parent, f"{bid}_hall_joints", granite_d)
    for i in range(14):
        a = 2 * math.pi * i / 14
        jb2.box((cx + math.cos(a) * 3.47, cy + math.sin(a) * 3.47, 1.58),
                (0.04, 0.05, 2.06), rot=(0, 0, a))
    jb2.build()
    lit_panels = (2, 13)
    pan_b = {}
    mull_b = Batch(parent, f"{bid}_hall_mull", iron)
    n_hall = 16
    for i in range(n_hall):
        a0 = 2 * math.pi * i / n_hall
        a1 = 2 * math.pi * (i + 1) / n_hall
        rr = 3.30
        p0 = (cx + math.cos(a0) * rr, cy + math.sin(a0) * rr)
        p1 = (cx + math.cos(a1) * rr, cy + math.sin(a1) * rr)
        mat = lit_warm if i in lit_panels else glass
        key = mat.name
        if key not in pan_b:
            pan_b[key] = Batch(parent, f"{bid}_hall_glass{len(pan_b)}", mat)
        pan_b[key].box(((p0[0] + p1[0]) / 2, (p0[1] + p1[1]) / 2, 4.28),
                       (math.dist(p0, p1) * 1.02, 0.06, 3.24),
                       rot=(0, 0, math.atan2(p1[1] - p0[1], p1[0] - p0[0])))
        mull_b.box((cx + math.cos(a0) * 3.36, cy + math.sin(a0) * 3.36, 4.28),
                   (0.09, 0.09, 3.30), rot=(0, 0, a0))
    for b in pan_b.values():
        b.build()
    mull_b.build()
    _lathe(parent, f"{bid}_hall_rail1", [(3.34, 4.02), (3.34, 4.12)], cx, cy, iron,
           segs=32, smooth=False)
    _lathe(parent, f"{bid}_hall_rail2", [(3.34, 5.86), (3.34, 5.96)], cx, cy, iron,
           segs=32, smooth=False)
    # dim interior core so the glazed drum reads inhabited, not like a lamp
    kit.cyl(parent, f"{bid}_hall_dark", (cx, cy, 4.1), 2.95, 3.2,
            lmm.mat("concrete_dark"), verts=24)
    kit.cyl(parent, f"{bid}_hall_warm", (cx, cy, 2.1), 2.60, 1.4, lit_warm, verts=24)
    kit.box(parent, f"{bid}_hall_counter", (cx - 1.2, cy + 1.4, 1.05),
            (2.2, 0.7, 1.0), granite)
    dy = cy + 3.35
    kit.box(parent, f"{bid}_door_recess", (cx, dy - 0.12, 1.78), (2.3, 0.3, 2.5),
            lmm.mat("concrete_dark"))
    kit.box(parent, f"{bid}_door", (cx, dy + 0.02, 1.72), (2.0, 0.09, 2.3), bronze)
    kit.box(parent, f"{bid}_door_split", (cx, dy + 0.08, 1.72), (0.05, 0.03, 2.3),
            iron)
    kit.box(parent, f"{bid}_door_glow", (cx, dy + 0.10, 2.60), (1.5, 0.03, 0.30),
            _acc("lit_cool", "#DCE8F4", 3.0))
    kit.box(parent, f"{bid}_hall_step", (cx, cy + 3.75, 0.70), (3.2, 0.7, 0.10),
            granite)
    kit.bench(parent, f"{bid}_hall_bench0", (cx + 1.9, cy + 0.9, 0.66),
              math.radians(180))
    kit.bench(parent, f"{bid}_hall_bench1", (cx - 1.9, cy + 0.9, 0.66),
              math.radians(180))

    # ================= the saucer =================
    top = [(0.55, 8.02), (2.30, 8.00), (4.10, 7.95), (6.00, 7.83),
           (7.80, 7.62), (9.30, 7.36), (10.15, 7.12), (10.62, 6.98)]
    profile = top + [
        (10.62, 6.80), (10.14, 6.76), (10.14, 5.92), (10.62, 5.86),
        (10.62, 5.70), (10.30, 5.66), (8.60, 5.62), (6.90, 5.68),
        (5.20, 5.79), (3.90, 5.90), (3.15, 5.96),
    ]
    _lathe(parent, f"{bid}_saucer", profile, cx, cy, conc, segs=segs, smooth=True)
    # rim shadow line + fascia panel joints
    _lathe(parent, f"{bid}_rim_brow", [(10.635, 6.79), (10.635, 6.725)], cx, cy,
           conc_g, segs=60, smooth=False)
    rim_b = Batch(parent, f"{bid}_rimjoints", conc_g)
    for i in range(40):
        a = 2 * math.pi * i / 40
        rim_b.box((cx + math.cos(a) * 10.635, cy + math.sin(a) * 10.635, 6.90),
                  (0.05, 0.05, 0.17), rot=(0, 0, a))
    rim_b.build()
    # radial ribs on the underside (follow the inverted-cone dish, proud of it)
    under_pts = [(3.55, 5.96), (5.20, 5.78), (6.90, 5.66), (8.60, 5.56),
                 (10.05, 5.52)]
    rib_b = Batch(parent, f"{bid}_under_ribs", conc_g)
    for i in range(16):
        a = 2 * math.pi * i / 16
        for k in range(len(under_pts) - 1):
            r0, z0 = under_pts[k]
            r1, z1 = under_pts[k + 1]
            rib_b.seg((cx + math.cos(a) * r0, cy + math.sin(a) * r0, z0 - 0.07),
                      (cx + math.cos(a) * r1, cy + math.sin(a) * r1, z1 - 0.07),
                      0.15, 0.14)
    rib_b.build()
    _lathe(parent, f"{bid}_under_ring", [(6.05, 5.645), (6.18, 5.64)], cx, cy,
           conc_g, segs=48, smooth=False)

    # ================= clerestory glass ribbon under the rim =================
    n_rib = 36
    gl_b = {}
    mull2 = Batch(parent, f"{bid}_rib_mull", iron)
    lit_ribbon = (4, 17, 26, 33)
    for i in range(n_rib):
        a0 = 2 * math.pi * i / n_rib
        a1 = 2 * math.pi * (i + 1) / n_rib
        rr = 10.37
        p0 = (cx + math.cos(a0) * rr, cy + math.sin(a0) * rr)
        p1 = (cx + math.cos(a1) * rr, cy + math.sin(a1) * rr)
        mat = lit_warm if i in lit_ribbon else glass
        key = mat.name
        if key not in gl_b:
            gl_b[key] = Batch(parent, f"{bid}_ribbon{len(gl_b)}", mat)
        gl_b[key].box(((p0[0] + p1[0]) / 2, (p0[1] + p1[1]) / 2, 6.35),
                      (math.dist(p0, p1) * 1.03, 0.05, 0.80),
                      rot=(0, 0, math.atan2(p1[1] - p0[1], p1[0] - p0[0])))
        mull2.box((cx + math.cos(a0) * 10.34, cy + math.sin(a0) * 10.34, 6.35),
                  (0.08, 0.14, 0.86), rot=(0, 0, a0))
    for b in gl_b.values():
        b.build()
    mull2.build()
    _lathe(parent, f"{bid}_rib_rail_top", [(10.31, 6.755), (10.31, 6.80)], cx, cy,
           iron, segs=48, smooth=False)
    _lathe(parent, f"{bid}_rib_rail_bot", [(10.31, 5.90), (10.31, 5.945)], cx, cy,
           iron, segs=48, smooth=False)
    glow = Batch(parent, f"{bid}_underglow", lit_warm)
    for i in range(12):
        a = 2 * math.pi * i / 12
        glow.box((cx + math.cos(a) * 9.80, cy + math.sin(a) * 9.80, 5.545),
                 (1.05, 0.20, 0.04), rot=(0, 0, a + math.pi / 2))
    glow.build()

    # ================= lantern / oculus + aviation light =================
    n_lan = 16
    lan_b = Batch(parent, f"{bid}_lantern_glass", glass)
    lan_m = Batch(parent, f"{bid}_lantern_mull", iron)
    for i in range(n_lan):
        a0 = 2 * math.pi * i / n_lan
        a1 = 2 * math.pi * (i + 1) / n_lan
        rr = 2.22
        p0 = (cx + math.cos(a0) * rr, cy + math.sin(a0) * rr)
        p1 = (cx + math.cos(a1) * rr, cy + math.sin(a1) * rr)
        lan_b.box(((p0[0] + p1[0]) / 2, (p0[1] + p1[1]) / 2, 8.36),
                  (math.dist(p0, p1) * 1.02, 0.05, 0.60),
                  rot=(0, 0, math.atan2(p1[1] - p0[1], p1[0] - p0[0])))
        lan_m.box((cx + math.cos(a0) * 2.19, cy + math.sin(a0) * 2.19, 8.36),
                  (0.06, 0.10, 0.64), rot=(0, 0, a0))
    lan_b.build()
    lan_m.build()
    kit.cyl(parent, f"{bid}_lantern_ring", (cx, cy, 8.06), 2.40, 0.12, conc_g,
            verts=28)
    kit.cyl(parent, f"{bid}_lantern_cap", (cx, cy, 8.70), 2.46, 0.16, iron, verts=28)
    kit.cyl(parent, f"{bid}_lantern_cap2", (cx, cy, 8.80), 1.45, 0.09, conc_g,
            verts=24)
    kit.cyl(parent, f"{bid}_mast", (cx, cy, 8.90), 0.04, 0.30, iron, verts=8)
    kit.sphere(parent, f"{bid}_aviation", (cx, cy, 9.10), 0.13, red, segments=10,
               rings=8)

    # ================= freestanding station sign pylon =================
    sx = cx - 8.4
    sy = cy + 5.9
    kit.box(parent, f"{bid}_signbase", (sx, sy, 0.78), (3.8, 0.8, 0.20), granite_d)
    kit.box(parent, f"{bid}_signpost0", (sx - 1.55, sy, 1.80), (0.12, 0.12, 2.1),
            iron)
    kit.box(parent, f"{bid}_signpost1", (sx + 1.55, sy, 1.80), (0.12, 0.12, 2.1),
            iron)
    kit.box(parent, f"{bid}_signboard", (sx, sy + 0.02, 2.62), (3.9, 0.09, 0.86),
            granite_d)
    kit.box(parent, f"{bid}_signinlay", (sx, sy + 0.075, 2.62), (3.66, 0.03, 0.68),
            lmm.mat("marble_white"))
    kit.text3d(parent, f"{bid}_signtxt", "АВТОВОКЗАЛ", (sx, sy + 0.10, 2.62),
               0.34, gold, rot=(math.radians(90), 0, math.pi))
    # subtle weathering accents only
    wb = Batch(parent, f"{bid}_streaks", kit.mat_solid("LMA_ufo_stain",
                                                       "#8A857C", rough=0.95))
    for i in range(8):
        a = 2 * math.pi * rng.random()
        wb.box((cx + math.cos(a) * 10.64, cy + math.sin(a) * 10.64,
                6.9 - rng.uniform(0.02, 0.12)), (0.04, rng.uniform(0.10, 0.22),
                                                rng.uniform(0.12, 0.34)),
               rot=(0, 0, a))
    wb.build()
    for k in (-1, 1):
        kit.cyl(parent, f"{bid}_planter{k}", (cx + k * 2.7, cy + 4.95, 0.90), 0.46,
                0.28, granite, verts=12)
        kit.cyl(parent, f"{bid}_plant{k}", (cx + k * 2.7, cy + 4.95, 1.12), 0.36,
                0.22, kit.mat_solid("LMA_ufo_plant", "#3C4A3C", rough=0.95),
                verts=10)
    # keep the block contract: 17 m saucer / podium -> 2.0 m max N/S overhang
    _squash_block(parent, cx, cy, 0.80, 0.80)
    st["objects"] = sum(1 for o in bpy.data.objects if o.name.startswith(bid))
    st["saucer_dia"] = 17.0
    return st


# ===========================================================================
# 5) TAJIK STEPPED-HEXAGON BUS STATION  (rect [36,-130,60,-118], faces PY)
# ===========================================================================
def build_bus_station_hex(parent, block, rng, ctx):
    """Tajik stepped-hexagon mountain bus station.

    Four shrinking stretched-hexagon shells stacked over a rubble-stone pavilion:
    each shell has a thick concrete slab edge with a blue/gold mosaic frieze band,
    recessed clerestory glazing between the shells, and an open column-and-glass
    waiting hall with a mosaic-banded ticket kiosk, benches and hanging lamps.
    """
    bid = block["id"]
    x0, y0, x1, y1 = block["rect"]
    st = {"type": "bus_station_hex", "windows": 0}
    cx, cy = (x0 + x1) / 2.0, (y0 + y1) / 2.0
    stone = lmm.mat("stone_rusticated")
    stone_l = lmm.mat("limestone_light")
    conc = lmm.mat("concrete_white")
    conc_g = lmm.mat("concrete_grey")
    marble = lmm.mat("marble_white")
    granite = lmm.mat("granite_grey")
    granite_d = lmm.mat("granite_dark")
    granite_p = lmm.mat("granite_polished")
    glass = lmm.mat("glass_clean")
    glass_rib = lmm.mat("glass_ribbon")
    mos_b = lmm.mat("mosaic_blue")
    mos_g = lmm.mat("mosaic_gold")
    bronze = lmm.mat("bronze")
    iron = lmm.mat("iron_dark")
    gold = lmm.mat("gold")
    lit = _acc("hex_warm", "#FFC07A", 1.4)
    lit_cool = _acc("hex_cool", "#DCE8F4", 1.0)
    booth_blue = kit.mat_solid("LMA_hex_booth_blue", "#3E6A82", rough=0.6,
                               metallic=0.2)
    RX, RY = 10.6, 5.4
    rot = 0.0

    # ---------- plinth + rubble stone pier pavilion ----------
    _prism(parent, f"{bid}_plinth", _hex_pts(cx, cy, RX + 0.6, RY + 0.6, rot),
           0.0, 0.34, granite_d)
    _prism(parent, f"{bid}_plinth2", _hex_pts(cx, cy, RX + 0.35, RY + 0.35, rot),
           0.34, 0.55, granite)
    hex_o = _hex_pts(cx, cy, RX, RY, rot)
    pier_pts = list(hex_o)
    for k in (1, 2):
        t = k / 3.0
        pier_pts.append((hex_o[4][0] + (hex_o[5][0] - hex_o[4][0]) * t,
                         hex_o[4][1] + (hex_o[5][1] - hex_o[4][1]) * t))
    for i, (px, py) in enumerate(pier_pts):
        kit.box(parent, f"{bid}_pier{i}", (px, py, 2.80), (0.95, 0.95, 4.5),
                stone)
        kit.box(parent, f"{bid}_piercap{i}", (px, py, 5.08), (1.14, 1.14, 0.22),
                stone_l)
        kit.box(parent, f"{bid}_pierfoot{i}", (px, py, 0.45), (1.14, 1.14, 0.24),
                granite_d)

    # ---------- glazed / open walls between piers ----------
    open_sides = (3, 5)          # SW / SE bays left open so the hall is visible
    for i in range(6):
        a = hex_o[i]
        b = hex_o[(i + 1) % 6]
        mid = ((a[0] + b[0]) / 2, (a[1] + b[1]) / 2)
        L = math.dist(a, b)
        ang = math.atan2(b[1] - a[1], b[0] - a[0])
        if i not in open_sides:
            kit.box(parent, f"{bid}_glass{i}", (mid[0], mid[1], 2.60),
                    (L - 1.05, 0.12, 3.5), glass, rot=(0, 0, ang))
            for k in range(1, 5):
                t = k / 5.0
                gx = a[0] + (b[0] - a[0]) * t
                gy = a[1] + (b[1] - a[1]) * t
                kit.box(parent, f"{bid}_gm{i}{k}", (gx, gy, 2.60),
                        (0.12, 0.16, 3.5), granite, rot=(0, 0, ang))
        else:
            # open bay: just a deep soffit beam and a mullion frame
            kit.box(parent, f"{bid}_beam{i}", (mid[0], mid[1], 4.62),
                    (L - 0.95, 0.30, 0.55), conc, rot=(0, 0, ang))
        kit.box(parent, f"{bid}_spand{i}", (mid[0], mid[1], 4.86),
                (L - 0.95, 0.34, 0.55), conc, rot=(0, 0, ang))
        kit.box(parent, f"{bid}_sill{i}", (mid[0], mid[1], 0.82),
                (L - 0.95, 0.30, 0.35), stone_l, rot=(0, 0, ang))
    # warm-lit waiting hall backdrop behind the glazing
    # dark waiting-hall interior with a few warm lit panels + bright kiosk
    _hex_band(parent, f"{bid}_hall_wall", cx, cy, RX - 1.15, RY - 0.62,
              RX - 1.30, RY - 0.78, 0.55, 5.2, lmm.mat("concrete_dark"))
    _hex_band(parent, f"{bid}_hall_wall2", cx, cy, RX - 2.3, RY - 1.2,
              RX - 2.45, RY - 1.3, 0.55, 5.2, conc_g)
    for i in range(6):
        a = hex_o[i]
        b = hex_o[(i + 1) % 6]
        mid = ((a[0] + b[0]) / 2, (a[1] + b[1]) / 2)
        L = math.dist(a, b)
        ang = math.atan2(b[1] - a[1], b[0] - a[0])
        for j in (-1, 1):
            px = mid[0] + math.cos(ang) * j * L * 0.22 + math.sin(ang) * 0.9
            py = mid[1] + math.sin(ang) * j * L * 0.22 - math.cos(ang) * 0.9
            kit.box(parent, f"{bid}_ipanel{i}{j}", (px, py, 2.7),
                    (1.0, 0.05, 1.4), lit, rot=(0, 0, ang))
    # south facade: three openings with bronze doors + lit transoms + sign
    a = hex_o[1]
    b = hex_o[2]
    mid = ((a[0] + b[0]) / 2, (a[1] + b[1]) / 2)
    L = math.dist(a, b)
    ang = math.atan2(b[1] - a[1], b[0] - a[0])
    for j in (-1, 1):
        ox = mid[0] + math.cos(ang) * j * (L / 3.4)
        oy = mid[1] + math.sin(ang) * j * (L / 3.4)
        kit.box(parent, f"{bid}_sdoor{j}", (ox, oy, 2.0), (L / 3.4, 0.10, 2.4),
                bronze, rot=(0, 0, ang))
        kit.box(parent, f"{bid}_sdgl{j}", (ox, oy, 2.3), (L / 4.2, 0.03, 1.1),
                lit_cool, rot=(0, 0, ang))
        kit.box(parent, f"{bid}_sdark{j}", (ox, oy, 0.95), (L / 3.6, 0.06, 0.8),
                iron, rot=(0, 0, ang))
    kit.box(parent, f"{bid}_scenter", (mid[0], mid[1], 1.95),
            (L / 3.1, 0.10, 2.3), bronze, rot=(0, 0, ang))
    kit.box(parent, f"{bid}_scenterg", (mid[0], mid[1], 2.55),
            (L / 3.6, 0.04, 0.85), lit, rot=(0, 0, ang))
    # marble sign band with dark letters over the doors
    _hex_band(parent, f"{bid}_sgnband", cx, cy, RX + 0.66, RY + 0.60,
              RX + 0.24, RY + 0.22, 4.30, 4.92, marble)
    kit.text3d(parent, f"{bid}_signtxt", "АВТОВОКЗАЛ",
               (cx, cy + (RY + 0.60) * math.sqrt(3) / 2, 4.61), 0.50, iron,
               rot=(math.radians(90), 0, math.pi))
    # floor + ceiling
    _prism(parent, f"{bid}_floor", _hex_pts(cx, cy, RX - 0.3, RY - 0.3, rot),
           0.55, 0.68, granite_p)
    _prism(parent, f"{bid}_ceiling", _hex_pts(cx, cy, RX - 0.5, RY - 0.5, rot),
           5.0, 5.2, conc_g)

    # ---------- ticket kiosk, benches, hanging lamps ----------
    bx, by = cx - 3.2, cy + 0.6
    kit.cyl(parent, f"{bid}_booth", (bx, by, 1.95), 1.25, 2.8, booth_blue, verts=6)
    kit.cyl(parent, f"{bid}_booth_band", (bx, by, 2.72), 1.29, 0.46, mos_b, verts=6)
    kit.cyl(parent, f"{bid}_booth_cap", (bx, by, 3.44), 1.46, 0.16, conc, verts=6)
    kit.box(parent, f"{bid}_booth_win", (bx + 1.16, by - 0.25, 2.02),
            (0.10, 1.3, 0.95), glass_rib)
    kit.box(parent, f"{bid}_booth_lit", (bx + 0.92, by - 0.70, 2.02),
            (0.10, 0.95, 0.78), lit)
    kit.box(parent, f"{bid}_booth_sill", (bx + 1.22, by - 0.25, 1.48),
            (0.30, 1.5, 0.08), stone_l)
    for k in range(3):
        kit.bench(parent, f"{bid}_bench{k}", (cx + 3.4, cy - 2.4 + k * 2.0, 0.68),
                  math.radians(-90))
    for i in range(3):
        hx = cx - 3.0 + i * 3.2
        kit.cyl(parent, f"{bid}_hang{i}", (hx, cy - 1.0, 4.55), 0.5, 0.1, lit,
                verts=14)
        kit.cyl(parent, f"{bid}_hangw{i}", (hx, cy - 1.0, 4.85), 0.02, 0.6, iron,
                verts=5)
    face_y = cy + RY * math.sqrt(3) / 2
    for k in range(3):
        kit.box(parent, f"{bid}_step{k}", (cx, face_y + 0.55 + k * 0.42,
                                          0.09 + k * 0.15),
                (9.6, 0.42, 0.18 + k * 0.02), granite)

    # ---------- stepped hexagonal shells ----------
    shells = [
        (RX + 1.25, RY + 0.85, 5.15, 5.95, mos_b, "s1"),
        (10.0, 5.10, 6.85, 7.55, mos_g, "s2"),
        (7.90, 4.10, 8.40, 9.05, mos_b, "s3"),
    ]
    clerestories = [
        (8.70, 4.50, 5.95, 6.85, "c1"),
        (6.70, 3.50, 7.55, 8.40, "c2"),
    ]
    for i, (rx, ry, zb, zt, mm, tag) in enumerate(shells):
        _prism(parent, f"{bid}_{tag}_slab", _hex_pts(cx, cy, rx, ry, rot), zb,
               zt, conc)
        _hex_band(parent, f"{bid}_{tag}_mos", cx, cy, rx + 0.02, ry + 0.02,
                  rx - 0.22, ry - 0.20, zb + 0.05, zt - 0.12, mm)
        _hex_band(parent, f"{bid}_{tag}_corn", cx, cy, rx + 0.10, ry + 0.09,
                  rx - 0.06, ry - 0.05, zt - 0.12, zt + 0.04, conc)
        _hex_band(parent, f"{bid}_{tag}_reveal", cx, cy, rx + 0.02, ry + 0.02,
                  rx - 0.32, ry - 0.30, zb - 0.10, zb + 0.05, conc_g)
    for i, (rx, ry, zb, zt, tag) in enumerate(clerestories):
        o = _hex_pts(cx, cy, rx, ry, rot)
        for k in range(6):
            a = o[k]
            b = o[(k + 1) % 6]
            mid = ((a[0] + b[0]) / 2, (a[1] + b[1]) / 2)
            L = math.dist(a, b)
            ang = math.atan2(b[1] - a[1], b[0] - a[0])
            kit.box(parent, f"{bid}_{tag}_gl{k}", (mid[0], mid[1], (zb + zt) / 2),
                    (L * 0.94, 0.10, zt - zb), glass_rib, rot=(0, 0, ang))
            for j in range(1, 5):
                t = j / 5.0
                gx = a[0] + (b[0] - a[0]) * t
                gy = a[1] + (b[1] - a[1]) * t
                kit.box(parent, f"{bid}_{tag}_gm{k}{j}", (gx, gy, (zb + zt) / 2),
                        (0.08, 0.12, zt - zb), granite, rot=(0, 0, ang))
        _hex_band(parent, f"{bid}_{tag}_core", cx, cy, rx - 0.35, ry - 0.33,
                  rx - 0.55, ry - 0.50, zb, zt, conc_g)
    # small stone drum + cap on top (4th stepped level)
    _prism(parent, f"{bid}_drum", _hex_pts(cx, cy, 5.45, 2.95, rot), 9.05, 9.90,
           stone_l)
    _hex_band(parent, f"{bid}_drummos", cx, cy, 5.50, 2.98, 5.30, 2.82, 9.24,
              9.72, mos_g)
    _prism(parent, f"{bid}_cap", _hex_pts(cx, cy, 5.85, 3.15, rot), 9.90, 10.18,
           conc)
    _hex_band(parent, f"{bid}_capband", cx, cy, 5.90, 3.18, 5.72, 3.06, 10.02,
              10.14, marble)
    kit.cyl(parent, f"{bid}_vent", (cx, cy, 10.42), 0.55, 0.5, iron, verts=10)
    kit.cyl(parent, f"{bid}_mast", (cx, cy, 10.95), 0.05, 1.0, iron, verts=6)
    kit.sphere(parent, f"{bid}_red", (cx, cy, 11.42), 0.11,
               _acc("hex_red", "#FF3A24", 18.0), segments=8, rings=6)

    # ---------- freestanding АВТОВОКЗАЛ pylon ----------
    sx, sy = cx - RX - 0.2, cy - 4.2
    kit.box(parent, f"{bid}_pylbase", (sx, sy, 0.75), (0.9, 3.4, 0.22), granite_d)
    kit.box(parent, f"{bid}_pylpost0", (sx, sy - 1.35, 1.95), (0.14, 0.14, 2.5),
            iron)
    kit.box(parent, f"{bid}_pylpost1", (sx, sy + 1.35, 1.95), (0.14, 0.14, 2.5),
            iron)
    kit.box(parent, f"{bid}_pylboard", (sx, sy, 2.92), (0.14, 4.0, 1.0),
            granite_d)
    kit.box(parent, f"{bid}_pylinlay", (sx - 0.09, sy, 2.92), (0.04, 3.74, 0.78),
            marble)
    kit.text3d(parent, f"{bid}_pyltxt", "АВТОВОКЗАЛ", (sx - 0.13, sy, 2.92),
               0.40, iron, rot=(math.radians(90), 0, math.radians(-90)))
    # subtle weathering streaks on the slab fascias
    wb = Batch(parent, f"{bid}_stains", kit.mat_solid("LMA_hex_stain",
                                                      "#8A857C", rough=0.95))
    for i in range(14):
        a = 2 * math.pi * rng.random()
        wb.box((cx + math.cos(a) * (RX + 1.26), cy + math.sin(a) * (RX + 1.26) * 0.6,
                5.45 - rng.uniform(0.02, 0.25)),
               (0.05, rng.uniform(0.10, 0.26), rng.uniform(0.12, 0.4)),
               rot=(0, 0, a))
    wb.build()
    st["objects"] = sum(1 for o in bpy.data.objects if o.name.startswith(bid))
    st["hex_shells"] = 4
    return st

TYPES = {
    "institute_robotics": build_institute_robotics,
    "ministry_transport": build_ministry_transport,
    "sanatorium_druzhba": build_sanatorium_druzhba,
    "bus_station_ufo": build_bus_station_ufo,
    "bus_station_hex": build_bus_station_hex,
}


