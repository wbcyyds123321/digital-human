"""mod_industrial.py — industrial / infrastructure modules for the Soviet blue-hour
microdistrict.

TYPES = {boiler, garages, stadium_stand, gym, frame, kiosk}

Contract (see docs/MODULE_CONTRACT.md):
  * Blender Z-up, metres, world coords, +Y = north.
  * ``parent`` is the BLD_<id> empty; every object is parented to it and named with
    the block-id prefix.
  * Facades are built with REAL recessed openings: the wall is extruded INWARD from the
    rect edge, so the finished outer face is exactly the rect boundary and every
    opening is a genuine hole (no window decals on flat boxes).
  * No cameras, lights, sky, fog or ground here — the Lead owns the look and the site.
  * All random decisions go through ``rng`` = kit.RNG(block["seed"]).
"""
from __future__ import annotations

import math

import bmesh
import bpy
from mathutils import Euler, Vector

import kit

_BOX_FACES = [(0, 1, 2, 3), (7, 6, 5, 4), (0, 4, 5, 1), (1, 5, 6, 2),
              (2, 6, 7, 3), (3, 7, 4, 0)]

# unit u vector that gives an OUTWARD facing quad for each facade (v is always +Z)
_FACE_U = {"PY": (1, 0, 0), "NY": (-1, 0, 0), "PX": (0, -1, 0), "NX": (0, 1, 0)}
# text3d euler for a readable outward-facing inscription on each facade
_TEXT_ROT = {"PY": (math.radians(90), 0, math.pi), "NY": (math.radians(90), 0, 0),
             "PX": (math.radians(90), 0, math.radians(90)),
             "NX": (math.radians(90), 0, math.radians(-90))}


# ---------------------------------------------------------------------------
# materials
# ---------------------------------------------------------------------------
_M = None


def _mat(name, color, rough=0.9, metal=0.0, emission=None, strength=0.0):
    return kit.mat_solid("IND_" + name, color, rough=rough, metallic=metal,
                         emission=emission, emission_strength=strength)


def _mats():
    """Lazy shared material dict (cached in kit.MATS by unique name)."""
    global _M
    if _M is not None:
        return _M
    _M = {
        # faded red-grey industrial brick
        "brick": _mat("brick", "#7A6459", 0.95),
        "brick2": _mat("brick2", "#6E5B52", 0.95),
        "brick_dark": _mat("brick_dark", "#4F433D", 0.96),
        "brick_soot": _mat("brick_soot", "#3D3531", 0.97),
        "brick_light": _mat("brick_light", "#8A7468", 0.94),
        # concrete
        "conc": _mat("conc", "#9AA0A0", 0.92),
        "conc2": _mat("conc2", "#848B8C", 0.93),
        "conc_dark": _mat("conc_dark", "#5F6666", 0.94),
        "conc_light": _mat("conc_light", "#AEB3B0", 0.9),
        "conc_stain": _mat("conc_stain", "#555B58", 0.96),
        # paint / plaster / metal
        "paint_blue": _mat("paint_blue", "#5D7380", 0.82),
        "plaster": _mat("plaster", "#A7ABA8", 0.9),
        "metal": _mat("metal", "#4B555B", 0.62, 0.22),
        "metal_light": _mat("metal_light", "#93A0A2", 0.66, 0.1),
        "metal_pale": _mat("metal_pale", "#9DA8AA", 0.7, 0.1),
        "metal_green": _mat("metal_green", "#84958E", 0.7, 0.08),
        "metal_dark": _mat("metal_dark", "#2A3034", 0.72, 0.25),
        "door_blue": _mat("door_blue", "#7E93A4", 0.72, 0.08),
        "door_green": _mat("door_green", "#84968B", 0.74, 0.06),
        "door_grey": _mat("door_grey", "#69767C", 0.74, 0.1),
        "rust": _mat("rust", "#6B4A38", 0.88, 0.15),
        "rust_dark": _mat("rust_dark", "#3F2E27", 0.92),
        "pipe": _mat("pipe", "#8E928A", 0.62, 0.35),
        "pipe_rust": _mat("pipe_rust", "#6F5B4C", 0.8, 0.2),
        "wood": _mat("wood", "#574F45", 0.92),
        "ply": _mat("ply", "#6B5F4E", 0.94),
        "roof_metal": _mat("roof_metal", "#55606A", 0.74, 0.22),
        "coal": _mat("coal", "#131518", 0.92, 0.1),
        "void": _mat("void", "#0C1015", 1.0),
        "glass": _mat("glass", "#0A0F16", 0.14, 0.3),
        "glass_cool": _mat("glass_cool", "#16202A", 0.12, 0.35),
        "dirt": _mat("dirt", "#3A342C", 0.98),
        "weeds": _mat("weeds", "#3E4534", 0.97),
        # emissive bits
        "lamp_cool": _mat("lamp_cool", "#DCE8FF", 0.3, emission="#CFE2FF", strength=6.0),
        "lamp_counter": _mat("lamp_counter", "#E8F2FF", 0.3, emission="#D6E6FF", strength=12.0),
        "lamp_warm": _mat("lamp_warm", "#FFC98A", 0.3, emission="#FFB469", strength=16.0),
        "lamp_red": _mat("lamp_red", "#FF3A24", 0.3, emission="#FF2A18", strength=40.0),
        "lamp_white": _mat("lamp_white", "#F2F6FF", 0.3, emission="#E6EEFF", strength=10.0),
    }
    # generated textures (fall back to solid colour if the file is missing)
    _M["rust_tex"] = kit.mat_texture("IND_RUST_TEX", "tex_rust_metal.png",
                                     rough=0.8, metallic=0.35,
                                     fallback=kit.PAL["rust"])
    _M["panel_tex"] = kit.mat_texture("IND_PANEL_TEX", "tex_panel_painted.png",
                                      rough=0.78, metallic=0.2,
                                      fallback=kit.PAL["conc_blue"])
    _M["brick_tex"] = kit.mat_texture("IND_BRICK_TEX", "tex_brick_plaster.png",
                                      rough=0.92, fallback="#7A6459")
    return _M


# ---------------------------------------------------------------------------
# low level mesh helpers
# ---------------------------------------------------------------------------
def _mesh(parent, name, verts, faces, mat, uv=None):
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
    if parent is not None and parent.users_collection:
        parent.users_collection[0].objects.link(obj)
        obj.parent = parent
    else:
        bpy.context.scene.collection.objects.link(obj)
    return obj


def _box_verts(cx, cy, cz, sx, sy, sz, rot=(0.0, 0.0, 0.0)):
    hx, hy, hz = sx / 2.0, sy / 2.0, sz / 2.0
    pts = [(-hx, -hy, -hz), (hx, -hy, -hz), (hx, hy, -hz), (-hx, hy, -hz),
           (-hx, -hy, hz), (hx, -hy, hz), (hx, hy, hz), (-hx, hy, hz)]
    m = Euler(rot, "XYZ").to_matrix()
    out = []
    for p in pts:
        v = m @ Vector(p)
        out.append((v.x + cx, v.y + cy, v.z + cz))
    return out


class Batch:
    """Accumulate many boxes that share one material into a single mesh object."""

    def __init__(self, parent, name, mat):
        self.parent = parent
        self.name = name
        self.mat = mat
        self.verts = []
        self.faces = []

    def box(self, center, size, rot=(0.0, 0.0, 0.0)):
        base = len(self.verts)
        self.verts.extend(_box_verts(center[0], center[1], center[2],
                                     size[0], size[1], size[2], rot))
        for f in _BOX_FACES:
            self.faces.append(tuple(base + i for i in f))

    def seg(self, p0, p1, w, h, extra=0.0):
        """A box strut from p0 to p1 (w across, h thick)."""
        p0, p1 = Vector(p0), Vector(p1)
        d = p1 - p0
        length = d.length
        if length < 1e-4:
            return
        q = Vector((1.0, 0.0, 0.0)).rotation_difference(d.normalized())
        self.box(tuple((p0 + p1) / 2.0), (length + extra, w, h), q.to_euler())

    def ub(self, P0, u, n, s, zc, out, lu, ln, lv):
        """Axis-aligned box positioned in a facade frame (u along wall, n outward)."""
        c = Vector(P0) + Vector(u) * s + Vector(n) * out + Vector((0.0, 0.0, zc))
        sx = abs(u[0]) * lu + abs(n[0]) * ln
        sy = abs(u[1]) * lu + abs(n[1]) * ln
        self.box((c.x, c.y, c.z), (sx, sy, lv))

    def build(self):
        if not self.verts:
            return None
        return _mesh(self.parent, self.name, self.verts, self.faces, self.mat)


def _quad(parent, name, center, u, v, su, sv, mat, uv=None):
    c = Vector(center)
    suv = Vector(u).normalized() * (su / 2.0)
    svv = Vector(v).normalized() * (sv / 2.0)
    verts = [tuple(c - suv - svv), tuple(c - suv + svv),
             tuple(c + suv + svv), tuple(c + suv - svv)]
    return _mesh(parent, name, verts, [(0, 1, 2, 3)], mat, uv=uv)


def _face_quad(parent, name, center, face, w, h, mat, tiles=(1.0, 1.0)):
    """Vertical textured quad on a facade, facing outward."""
    u = _FACE_U[face]
    tw, th = tiles
    uv = [(0.0, 0.0), (0.0, th), (tw, th), (tw, 0.0)]
    return _quad(parent, name, center, u, (0, 0, 1), w, h, mat, uv=uv)


def _tapered_cyl(parent, name, cx, cy, z0, z1, r0, r1, mat, verts=28):
    bm = bmesh.new()
    bmesh.ops.create_cone(bm, cap_ends=True, cap_tris=False, segments=verts,
                          radius1=r0, radius2=r1, depth=(z1 - z0))
    me = bpy.data.meshes.new(name)
    bm.to_mesh(me)
    bm.free()
    if mat:
        me.materials.append(mat)
    obj = bpy.data.objects.new(name, me)
    if parent is not None and parent.users_collection:
        parent.users_collection[0].objects.link(obj)
        obj.parent = parent
    else:
        bpy.context.scene.collection.objects.link(obj)
    obj.location = (cx, cy, (z0 + z1) / 2.0)
    return obj


def _blob(parent, name, center, scale, mat, phase=0.0, subdiv=2):
    """Low-poly irregular mound (coal pile, rubble)."""
    bm = bmesh.new()
    try:
        bmesh.ops.create_icosphere(bm, subdivisions=subdiv, radius=1.0)
    except TypeError:  # older API name
        bmesh.ops.create_icosphere(bm, subdivisions=subdiv, diameter=1.0)
    for v in bm.verts:
        p = v.co.copy()
        n = 1.0 + 0.16 * math.sin(p.x * 3.1 + phase) * math.cos(p.y * 2.7 - phase) \
            + 0.09 * math.sin((p.x + p.y) * 5.3 + phase * 2.0)
        v.co = Vector((p.x * scale[0] * n, p.y * scale[1] * n, p.z * scale[2] * n))
    me = bpy.data.meshes.new(name)
    bm.to_mesh(me)
    bm.free()
    if mat:
        me.materials.append(mat)
    obj = bpy.data.objects.new(name, me)
    if parent is not None and parent.users_collection:
        parent.users_collection[0].objects.link(obj)
        obj.parent = parent
    else:
        bpy.context.scene.collection.objects.link(obj)
    obj.location = center
    return obj


def _ring(parent, name, center, radius, depth, mat, verts=16):
    """thin annulus approximation via 2 nested cylinders is overkill; use a flat disc"""
    return kit.cyl(parent, name, center, radius, depth, mat, verts=verts)


# ---------------------------------------------------------------------------
# facade construction
# ---------------------------------------------------------------------------
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


def _wbox(parent, name, P0, u, n, s0, s1, za, zb, thick, mat, extra=0.0, batch=None):
    """Wall slab from the facade plane extruded INWARD (inside the rect)."""
    if s1 - s0 < 0.02 or zb - za < 0.02:
        return
    length = s1 - s0
    mid = Vector(P0) + Vector(u) * ((s0 + s1) / 2.0) - Vector(n) * ((thick + extra) / 2.0)
    zc = (za + zb) / 2.0
    if abs(u[0]) > 0.5:
        size = (length, thick + extra, zb - za)
    else:
        size = (thick + extra, length, zb - za)
    if batch is not None:
        batch.box((mid.x, mid.y, zc), size)
    else:
        kit.box(parent, name, (mid.x, mid.y, zc), size, mat)


def wall_openings(parent, name, rect, face, z0, z1, thickness, mat, holes,
                  mat_bands=None):
    """Build one facade wall with real rectangular openings.

    holes: [(s_center, width, zb, zt), ...] in absolute z.
    mat_bands: [(z_top, material, extra), ...] ascending -> material for the band
    whose z_top is the first one >= the band top.
    """
    P0, u, L, n = kit.wall_frame(rect, face)
    zcuts = {z0, z1}
    for _s, _w, zb, zt in holes:
        zcuts.add(min(max(zb, z0), z1))
        zcuts.add(min(max(zt, z0), z1))
    if mat_bands:
        for zt, _m, _e in mat_bands:
            if z0 - 1e-6 <= zt <= z1 + 1e-6:
                zcuts.add(zt)
    zcuts = sorted(zcuts)
    batches = {}

    def _batch(m, extra):
        key = (m.name, round(extra, 3))
        b = batches.get(key)
        if b is None:
            b = Batch(parent, f"{name}_{len(batches):02d}", m)
            batches[key] = b
        return b

    for i in range(len(zcuts) - 1):
        za, zb = zcuts[i], zcuts[i + 1]
        if zb - za < 0.02:
            continue
        spans = [(0.0, L)]
        for s, w, hz0, hz1 in holes:
            if hz0 - 1e-6 <= za and zb <= hz1 + 1e-6:
                spans = _sub_spans(spans, s - w / 2.0, s + w / 2.0)
        m, extra = mat, 0.0
        if mat_bands:
            for zt, bm, be in mat_bands:
                if zt >= zb - 1e-6:
                    m, extra = bm, be
                    break
        b = _batch(m, extra)
        for k, (s0, s1) in enumerate(spans):
            _wbox(parent, f"{name}_w{i}_{k}", P0, u, n, s0, s1, za, zb,
                  thickness, m, extra=extra, batch=b)
    for b in batches.values():
        b.build()
    return len(batches)


def even_cols(L, count, margin):
    """Evenly spaced window columns + the resulting window width."""
    span = L - 2.0 * margin
    step = span / count
    cols = [margin + step * (i + 0.5) for i in range(count)]
    return cols, step


def windows(parent, name, rect, face, cols, rows, win_w, win_h, wmats, rng,
            lit_ratio=0.05, grille=False, overrides=None, recess=0.22,
            frame_mat=None):
    """Place kit window units into the openings made by wall_openings."""
    P0, u, _L, n = kit.wall_frame(rect, face)
    stats = {"windows": 0, "lit": 0}
    overrides = overrides or {}
    for fi, (zb, zt) in enumerate(rows):
        zc = (zb + zt) / 2.0
        for ci, s in enumerate(cols):
            lit_override = overrides.get((fi, ci))
            info = kit.window_unit(parent, f"{name}_win{fi}_{ci}", P0, u, n, s, zc,
                                   win_w, win_h, rng, lit_ratio=lit_ratio,
                                   frame_mat=frame_mat, recess=recess,
                                   grille=grille, lit_override=lit_override)
            stats["windows"] += 1
            stats["lit"] += 1 if info["lit"] else 0
    return stats


def mullions(batch, P0, u, n, s, zb, zt, w, n_vert=3, n_horz=1,
             out=-0.06, thick=0.05):
    """Factory-window grid bars in front of the glass (inside the recess)."""
    zc = (zb + zt) / 2.0
    hgt = zt - zb - 0.05
    for i in range(1, n_vert + 1):
        so = -w / 2.0 + w * i / (n_vert + 1)
        batch.ub(P0, u, n, s + so, zc, out, thick, 0.05, hgt)
    for j in range(1, n_horz + 1):
        zo = zb + (zt - zb) * j / (n_horz + 1)
        batch.ub(P0, u, n, s, zo, out, w - 0.06, 0.05, thick)


def stain(batch, P0, u, n, s, zb, zt, width, out=0.03):
    """A vertical weathering / rust streak on a facade."""
    batch.ub(P0, u, n, s, (zb + zt) / 2.0, out, width, 0.045, zt - zb)


def sign_text(parent, name, text, center, face, size, mat, depth=0.02):
    """Cyrillic sign lettering that reads correctly on any facade."""
    return kit.text3d(parent, name, text, center, size, mat,
                      rot=_TEXT_ROT[face], extrude=depth)


def band(parent, rect, face, z, height, depth, mat, out=0.0, batch=None):
    """A proud horizontal band (cornice / plinth / sign band) across one facade."""
    P0, u, L, n = kit.wall_frame(rect, face)
    mid = Vector(P0) + Vector(u) * (L / 2.0) + Vector(n) * (out + depth / 2.0)
    if abs(u[0]) > 0.5:
        size = (L, depth, height)
    else:
        size = (depth, L, height)
    if batch is not None:
        batch.box((mid.x, mid.y, z), size)
    else:
        kit.box(parent, f"{face}_band", (mid.x, mid.y, z), size, mat)


def void_box(parent, name, rect, z0, z1, inset=0.55, mat=None):
    """Dark interior volume so windows/doors read as depth, not hollow sky."""
    m = mat or _mats()["void"]
    x0, y0, x1, y1 = rect
    cx, cy = (x0 + x1) / 2.0, (y0 + y1) / 2.0
    kit.box(parent, name, (cx, cy, (z0 + z1) / 2.0),
            (max(0.5, x1 - x0 - 2 * inset), max(0.5, y1 - y0 - 2 * inset),
             max(0.4, z1 - z0 - 0.25)), m)


# ---------------------------------------------------------------------------
# shared detail helpers
# ---------------------------------------------------------------------------
def pipe_run(parent, name, pts, radius, mat, flanges=True, joint_balls=True):
    """Polyline of pipes with optional flange collars and elbow balls."""
    objs = 0
    for i in range(len(pts) - 1):
        kit.cyl_between(parent, f"{name}_{i}", pts[i], pts[i + 1], radius, mat, verts=10)
        objs += 1
        if joint_balls and 0 < i < len(pts) - 1:
            kit.sphere(parent, f"{name}_el{i}", pts[i], radius * 1.12, mat,
                       segments=10, rings=6)
            objs += 1
    if flanges:
        for i in range(len(pts) - 1):
            p0, p1 = Vector(pts[i]), Vector(pts[i + 1])
            d = (p1 - p0)
            seg = d.length
            if seg < 1.2:
                continue
            d.normalize()
            for f in range(1, max(2, int(seg / 2.4))):
                c = p0 + d * (seg * f / max(2, int(seg / 2.4)))
                kit.cyl(parent, f"{name}_fl{i}_{f}", tuple(c), radius * 1.45, 0.09,
                        mat, verts=10)
                objs += 1
    return objs


def ladder(parent, name, base, height, facing="PY", mat=None, width=0.42, rungs=0.34):
    """Steel wall ladder (vertical rails + rungs)."""
    m = mat or _mats()["metal"]
    bx, by = base
    dz = Vector((0, 0, 1))
    off = {"PY": Vector((0, 0.16, 0)), "NY": Vector((0, -0.16, 0)),
           "PX": Vector((0.16, 0, 0)), "NX": Vector((-0.16, 0, 0))}[facing]
    tang = {"PY": Vector((1, 0, 0)), "NY": Vector((-1, 0, 0)),
            "PX": Vector((0, 1, 0)), "NX": Vector((0, -1, 0))}[facing]
    p = Vector((bx, by, 0)) + off
    for side in (-1, 1):
        a = p + tang * (side * width / 2.0)
        kit.cyl_between(parent, f"{name}_rail{side}", a + dz * 0.1,
                        a + dz * height, 0.028, m, verts=6)
    n = int(height / rungs)
    for i in range(n + 1):
        z = 0.25 + i * height / max(1, n)
        kit.cyl_between(parent, f"{name}_r{i}", p + tang * (-width / 2.0) + dz * z,
                        p + tang * (width / 2.0) + dz * z, 0.016, m, verts=5)
    # safety hoop at the top
    kit.cyl_between(parent, f"{name}_hoop", p + tang * (-width / 2.0) + dz * (height + 0.35),
                    p + tang * (width / 2.0) + dz * (height + 0.35), 0.02, m, verts=5)


def weeds(parent, name, points, rng, mat=None, hmin=0.18, hmax=0.55, n=5):
    """Clumps of dry weeds (cheap leaning blades bundled into one batch)."""
    m = mat or _mats()["weeds"]
    b = Batch(parent, name, m)
    for (x, y) in points:
        for _i in range(n):
            h = rng.uniform(hmin, hmax)
            ang = rng.uniform(0, math.tau)
            lean = rng.uniform(0.08, 0.3)
            base = Vector((x + rng.jitter(0.12), y + rng.jitter(0.12), 0.02))
            tip = base + Vector((math.cos(ang) * lean, math.sin(ang) * lean, h))
            b.seg(base, tip, 0.018, 0.018)
    b.build()
    return len(points)


def rubble(parent, name, center, spread, rng, mat=None, count=14):
    """Pile of broken concrete chunks."""
    m = mat or _mats()["conc2"]
    b = Batch(parent, name, m)
    cx, cy = center
    for _i in range(count):
        x = cx + rng.jitter(spread)
        y = cy + rng.jitter(spread)
        sz = (rng.uniform(0.18, 0.62), rng.uniform(0.18, 0.55), rng.uniform(0.1, 0.32))
        b.box((x, y, sz[2] / 2.0 + 0.01), sz, rot=(rng.jitter(0.4), rng.jitter(0.4),
                                                   rng.uniform(0, math.tau)))
    b.build()


def leaning_boards(parent, name, center, rng, n=6, mat=None, length=2.6):
    m = mat or _mats()["ply"]
    b = Batch(parent, name, m)
    cx, cy = center
    for i in range(n):
        a = rng.uniform(0.35, 1.15) * (1 if i % 2 else -1)
        d = Vector((math.cos(a), math.sin(a), math.sin(abs(a)) * 0.9)).normalized()
        base = Vector((cx + rng.jitter(0.5), cy + rng.jitter(0.5), 0.02))
        b.seg(base, base + d * length, 0.28, 0.035)
    b.build()


# ---------------------------------------------------------------------------
# 1) BOILER HOUSE + CHIMNEY
# ---------------------------------------------------------------------------
def build_boiler(parent, block, rng, ctx):
    m = _mats()
    wm = ctx.get("wmats") or kit.win_materials()
    bid = block["id"]
    x0, y0, x1, y1 = block["rect"]
    stats = {"objects": 0, "windows": 0}

    # ---- massing: boiler hall occupies the east part, coal yard strip on the west
    bx0, by0, bx1, by1 = x0 + 9.0, y0 + 1.4, x1 - 1.6, y1 - 0.8   # 15.4 x 19.8
    z0, z1 = 0.0, 8.8
    rect = (bx0, by0, bx1, by1)
    brick = m["brick"]
    dark_bands = [(1.15, m["brick_dark"], 0.04)]
    thick = 0.42

    # facade layouts ---------------------------------------------------------
    Lpy, Lny = bx1 - bx0, bx1 - bx0
    Lpx = Lny
    # north facade (yard side, faces PY): 4 upper windows, 2 big ground windows,
    # one huge double metal door in the centre with a brick arch.
    cols_py, step_py = even_cols(Lpy, 4, 1.5)
    win_w_py = 2.35
    rows_up = [(5.5, 8.15)]
    rows_gr = [(1.55, 4.25)]
    door_c = Lpy / 2.0
    door_w = 5.1
    holes_py = [(c, win_w_py, 5.5, 8.15) for c in cols_py]
    holes_py += [(cols_py[0], 2.35, 1.55, 4.25), (cols_py[3], 2.35, 1.55, 4.25)]
    holes_py += [(door_c, door_w, 0.0, 4.05)]
    wall_openings(parent, f"{bid}_wallPY", rect, "PY", z0, z1, thick, brick,
                  holes_py, mat_bands=dark_bands)
    stats["windows"] += windows(parent, f"{bid}_PY", rect, "PY",
                                [cols_py[0], cols_py[3]], rows_gr, 2.35, 2.7, wm, rng,
                                lit_ratio=0.16, overrides={(0, 1): True})["windows"]
    stats["windows"] += windows(parent, f"{bid}_PYu", rect, "PY", cols_py, rows_up,
                                win_w_py, 2.65, wm, rng, lit_ratio=0.1)["windows"]

    # east facade (faces the NW drive, PX): 4 upper, 3 ground + steel door
    cols_px, _ = even_cols(Lpx, 4, 1.6)
    holes_px = [(c, 3.2, 5.5, 8.15) for c in cols_px]
    holes_px += [(cols_px[0], 2.1, 1.55, 4.25), (cols_px[1], 2.1, 1.55, 4.25),
                 (cols_px[3], 2.1, 1.55, 4.25)]
    door_px_c = cols_px[2]
    holes_px += [(door_px_c, 1.45, 0.0, 2.45)]
    wall_openings(parent, f"{bid}_wallPX", rect, "PX", z0, z1, thick, brick,
                  holes_px, mat_bands=dark_bands)
    stats["windows"] += windows(parent, f"{bid}_PXu", rect, "PX", cols_px, rows_up,
                                3.2, 2.65, wm, rng, lit_ratio=0.08)["windows"]
    stats["windows"] += windows(parent, f"{bid}_PXg", rect, "PX",
                                [cols_px[0], cols_px[1], cols_px[3]], rows_gr,
                                2.1, 2.7, wm, rng, lit_ratio=0.12)["windows"]

    # south facade (NY): 3 upper, 2 ground, service door in the middle
    cols_ny, _ = even_cols(Lny, 3, 2.0)
    holes_ny = [(c, 3.0, 5.5, 8.15) for c in cols_ny]
    holes_ny += [(cols_ny[0], 2.0, 1.55, 4.25), (cols_ny[2], 2.0, 1.55, 4.25)]
    holes_ny += [(cols_ny[1], 1.3, 0.0, 2.3)]
    wall_openings(parent, f"{bid}_wallNY", rect, "NY", z0, z1, thick, brick,
                  holes_ny, mat_bands=dark_bands)
    stats["windows"] += windows(parent, f"{bid}_NYu", rect, "NY", cols_ny, rows_up,
                                3.0, 2.65, wm, rng, lit_ratio=0.1)["windows"]
    stats["windows"] += windows(parent, f"{bid}_NYg", rect, "NY",
                                [cols_ny[0], cols_ny[2]], rows_gr, 2.0, 2.7, wm, rng,
                                lit_ratio=0.0)["windows"]

    # west facade (NX, coal yard): 3 small high windows + a hatch door
    cols_nx, _ = even_cols(Lpx, 3, 2.4)
    holes_nx = [(c, 1.15, 6.0, 7.85) for c in cols_nx]
    holes_nx += [(cols_nx[2], 1.2, 0.0, 2.2)]
    wall_openings(parent, f"{bid}_wallNX", rect, "NX", z0, z1, thick, brick,
                  holes_nx, mat_bands=dark_bands)
    stats["windows"] += windows(parent, f"{bid}_NX", rect, "NX", cols_nx,
                                [(6.0, 7.85)], 1.15, 1.85, wm, rng,
                                lit_ratio=0.0)["windows"]

    void_box(parent, f"{bid}_void", rect, 0.05, z1 - 0.3, inset=0.7)

    # ---- big double metal doors (north yard side) --------------------------
    P0, u, Lpy2, n = kit.wall_frame(rect, "PY")
    door_y = 0.0  # wall inner face is at y=by1; door sits at the face
    dy = by1 - 0.16
    # recessed dark reveal
    kit.box(parent, f"{bid}_dk", (bx0 + door_c, dy + 0.05, 1.98),
            (door_w - 0.5, 0.12, 3.9), m["void"])
    for side in (-1, 1):
        cx = bx0 + door_c + side * (door_w - 0.55) / 4.0
        kit.box(parent, f"{bid}_door{side}", (cx, dy, 1.95),
                (door_w / 2.0 - 0.28, 0.09, 3.8), m["metal"])
        # riveted ribs + rust
        for i in range(3):
            kit.box(parent, f"{bid}_dorib{side}{i}",
                    (cx - 1.05 + i * 1.05, dy - 0.06, 1.95),
                    (0.09, 0.04, 3.7), m["metal_dark"])
        kit.box(parent, f"{bid}_dorust{side}", (cx, dy - 0.07, 0.55),
                (door_w / 2.0 - 0.4, 0.03, 0.85), m["rust"])
        # strap hinges
        for h in (0.9, 2.9):
            kit.box(parent, f"{bid}_doh{side}{h}", (cx, dy - 0.09, h),
                    (1.0, 0.04, 0.12), m["rust_dark"])
    # brick arch over the doorway
    arch_r = door_w / 2.0
    arch_z = 4.05
    for i in range(11):
        a = math.pi * i / 10.0
        cxp = bx0 + door_c + math.cos(a) * (arch_r + 0.16)
        czp = arch_z + math.sin(a) * (arch_r + 0.16) * 0.55
        b = Batch(parent, f"{bid}_vous{i}", m["brick_light"] if i % 2 else m["brick"])
        b.box((cxp, by1 - 0.18, czp), (0.4, 0.36, 0.62), rot=(0, 0, a))
        b.build()
    kit.box(parent, f"{bid}_kslab", (bx0 + door_c, by1 - 0.18, 2.25),
            (door_w - 0.55, 0.3, 0.5), m["dirt"])

    # steel personnel door on the east facade
    P0x, ux, Lx, nx = kit.wall_frame(rect, "PX")
    kit.box(parent, f"{bid}_pdoor", (bx1 - 0.11, by0 + door_px_c, 1.2),
            (0.09, 1.32, 2.4), m["metal_dark"])
    kit.box(parent, f"{bid}_pdoor_r", (bx1 - 0.12, by0 + door_px_c, 0.3),
            (0.04, 1.2, 0.5), m["rust"])
    kit.box(parent, f"{bid}_pcan", (bx1 + 0.2, by0 + door_px_c, 2.62),
            (0.5, 1.65, 0.16), m["metal"])
    # service door south
    kit.box(parent, f"{bid}_sdoor", (bx0 + cols_ny[1], by0 + 0.11, 1.15),
            (1.2, 0.09, 2.28), m["metal_pale"])

    # ---- brick pilasters + cornice + plinth around the hall -----------------
    pil = Batch(parent, f"{bid}_pilasters", m["brick2"])
    for face, Ll in (("PY", Lpy), ("NY", Lny), ("PX", Lpx), ("NX", Lpx)):
        P0f, uf, Lf, nf = kit.wall_frame(rect, face)
        n_p = max(2, int(Lf / 3.4))
        for i in range(n_p + 1):
            s = 0.4 + (Lf - 0.8) * i / n_p
            pil.ub((0, 0, 0), uf, nf, s, (z1 + 1.15) / 2.0, 0.16, 0.5, 0.3, z1 - 1.15)
    pil.build()
    corn = Batch(parent, f"{bid}_cornice", m["brick_light"])
    for face in ("PY", "NY", "PX", "NX"):
        band(parent, rect, face, z1 - 0.34, 0.42, 0.28, m["brick_light"], batch=corn)
        band(parent, rect, face, z1 - 0.02, 0.22, 0.42, m["brick_dark"], batch=corn)
    corn.build()

    # ---- roof: slab, parapet, vents ----------------------------------------
    kit.roof_flat(parent, f"{bid}_roof", rect, z1, m["conc_dark"])
    kit.parapet(parent, f"{bid}_parapet", rect, z1, 0.55, 0.25, m["brick_dark"],
                coping_mat=m["conc_light"])
    roofd = Batch(parent, f"{bid}_roofbits", m["metal"])
    for i in range(4):
        vx = bx0 + 2.4 + i * 3.6
        vy = by0 + 3.0 + (i % 2) * 9.0
        kit.cyl(parent, f"{bid}_vent{i}", (vx, vy, z1 + 0.85), 0.34, 1.7,
                m["metal_dark"], verts=10)
        kit.cyl(parent, f"{bid}_ventc{i}", (vx, vy, z1 + 1.75), 0.46, 0.1,
                m["rust"], verts=10)
    kit.box(parent, f"{bid}_econ", (bx0 + 11.4, by0 + 14.0, z1 + 0.8),
            (3.2, 2.0, 1.5), m["conc2"])
    kit.box(parent, f"{bid}_econ_top", (bx0 + 11.4, by0 + 14.0, z1 + 1.6),
            (3.4, 2.2, 0.12), m["metal_dark"])
    kit.box(parent, f"{bid}_hatch", (bx0 + 2.2, by0 + 17.0, z1 + 0.35),
            (1.1, 1.1, 0.55), m["metal_dark"])
    roofd.build()
    for (x, y) in ((bx1 - 0.7, by1 - 0.7), (bx1 - 0.7, by0 + 0.7)):
        kit.drainpipe(parent, f"{bid}_drain_{x:.0f}_{y:.0f}", x, y, 0.2, z1 - 0.1)

    # ---- pipe runs along the north facade -----------------------------------
    pyf = by1  # facade plane
    py_off = 0.42
    main_pipes = []
    for k, (pz, pr) in enumerate(((5.05, 0.16), (6.05, 0.11), (6.85, 0.13))):
        pts = [(bx0 + 0.9, pyf + py_off, pz), (bx1 - 0.7, pyf + py_off, pz)]
        # elbows down into the wall at both ends
        pts = [(bx0 + 0.9, pyf + 0.02, pz - 0.9), (bx0 + 0.9, pyf + py_off, pz - 0.55),
               (bx0 + 0.9, pyf + py_off, pz)] + \
              [(bx1 - 0.7, pyf + py_off, pz)] + \
              [(bx1 - 0.7, pyf + py_off, pz - 0.55),
               (bx1 - 0.7, pyf + 0.02, pz - 0.9)]
        pipe_run(parent, f"{bid}_pipe{k}", pts, pr, m["pipe"] if k == 0 else m["pipe_rust"],
                 flanges=True)
    # vertical riser going up to the roof + feed to the chimney
    pipe_run(parent, f"{bid}_riser", [(bx0 + 2.0, pyf + py_off, 6.05),
                                      (bx0 + 2.0, pyf + py_off, z1 + 0.6),
                                      (bx0 + 2.0, by1 - 1.0, z1 + 0.6)],
             0.13, m["pipe_rust"], flanges=False)
    # pipe bridge from the hall roof to the chimney
    bridge = [(bx0 + 0.35, by0 + 9.0, z1 - 0.4), (x0 + 4.2, by0 + 9.0, z1 - 0.4)]
    pipe_run(parent, f"{bid}_bridge", bridge, 0.22, m["pipe"], flanges=True)
    pipe_run(parent, f"{bid}_bridge2", [(bx0 + 0.35, by0 + 10.6, z1 - 1.4),
                                        (x0 + 4.2, by0 + 10.6, z1 - 1.4)],
             0.16, m["pipe_rust"], flanges=False)
    # pipe supports
    sup = Batch(parent, f"{bid}_pipesup", m["metal_dark"])
    for i in range(6):
        sx = bx0 + 1.4 + i * 2.5
        for pz in (5.05, 6.05, 6.85):
            sup.box((sx, pyf + 0.18, pz - 0.02), (0.1, 0.34, 0.08))
    sup.build()

    # ---- weathering streaks + soot -----------------------------------------
    P0, u, _, n = kit.wall_frame(rect, "PY")
    st = Batch(parent, f"{bid}_streaks", m["conc_stain"])
    for i in range(16):
        sx = rng.uniform(1.2, Lpy - 1.2)
        zb = rng.uniform(2.6, 7.6)
        stain(st, P0, u, n, sx, zb - rng.uniform(0.8, 2.4), zb,
              rng.uniform(0.12, 0.38))
    st.build()
    st2 = Batch(parent, f"{bid}_soot", m["brick_soot"])
    Pb, ub2, _, nb = kit.wall_frame(rect, "PX")
    for i in range(8):
        sx = rng.uniform(1.0, Lpx - 1.0)
        st2.ub(Pb, ub2, nb, sx, rng.uniform(7.6, 8.6), 0.03,
               rng.uniform(0.4, 1.2), 0.05, rng.uniform(0.5, 1.4))
    st2.build()

    # ---- ladders, wall lamp, sign -----------------------------------------
    ladder(parent, f"{bid}_ladder", (bx0 + 0.6, by0 + 0.3), z1 + 0.1, facing="NY")
    ladder(parent, f"{bid}_ladderE", (bx1 - 0.3, by1 - 1.4), z1 + 0.1, facing="PX")
    kit.box(parent, f"{bid}_wlamp", (bx0 + door_c, by1 + 0.32, 4.55),
            (0.34, 0.5, 0.16), m["metal_dark"])
    kit.box(parent, f"{bid}_wlampg", (bx0 + door_c, by1 + 0.34, 4.44),
            (0.26, 0.36, 0.05), m["lamp_warm"])
    # sign over the yard doorway (north face -> rotated lettering helper)
    kit.box(parent, f"{bid}_signbg", (bx0 + door_c, by1 + 0.06, 5.0),
            (3.6, 0.12, 0.52), m["metal_dark"])
    sign_text(parent, f"{bid}_sign", "КОТЕЛЬНАЯ", (bx0 + door_c, by1 + 0.13, 5.0),
              "PY", 0.34, m["lamp_white"])
    # small painted unit number plate
    kit.box(parent, f"{bid}_plate", (bx1 - 0.03, by0 + 3.2, 3.4),
            (0.03, 0.6, 0.36), m["conc_light"])

    # =======================================================================
    # CHIMNEY
    # =======================================================================
    ch = block.get("chimney") or [x0 + 4.0, (y0 + y1) / 2, 3.2, 38.0]
    ccx, ccy, cr, chh = ch[0], ch[1], ch[2], ch[3]
    # plan gives the maximum footprint radius (3.2 m) — that is the flared brick base;
    # the stack itself tapers from 0.80x to 0.46x of it so the proportion reads like a
    # real industrial chimney against the 8.8 m hall.
    r_base = cr * 0.80
    r_top = cr * 0.46
    # brick base plinth
    kit.box(parent, f"{bid}_chbase", (ccx, ccy, 0.85),
            (cr * 2.02, cr * 2.02, 1.7), m["brick_dark"])
    kit.box(parent, f"{bid}_chbase2", (ccx, ccy, 1.82),
            (cr * 1.85, cr * 1.85, 0.26), m["brick_light"])
    segs = [(1.95, 12.0, m["brick"]), (12.0, 24.0, m["brick2"]),
            (24.0, 33.0, m["brick_dark"]), (33.0, chh - 0.8, m["brick_soot"])]

    def _r_at(z):
        t = (z - 1.95) / max(0.1, (chh - 0.8) - 1.95)
        return r_base + (r_top - r_base) * t

    for i, (za, zb, mm) in enumerate(segs):
        _tapered_cyl(parent, f"{bid}_chseg{i}", ccx, ccy, za, zb,
                     _r_at(za), _r_at(zb), mm, verts=32)
    # protruding brick band rings every ~6 m
    for i, z in enumerate((7.0, 13.0, 19.0, 25.0, 31.0, 36.2)):
        rr = _r_at(z) + 0.14
        kit.cyl(parent, f"{bid}_chband{i}", (ccx, ccy, z), rr, 0.52,
                m["brick_soot"] if i % 2 else m["brick_dark"], verts=32)
    # metal hoops
    for i, z in enumerate((10.0, 22.0, 34.0)):
        kit.cyl(parent, f"{bid}_chhoop{i}", (ccx, ccy, z),
                _r_at(z) + 0.2, 0.12, m["metal_dark"], verts=32)
    # crown + parapet lip + red aviation light
    kit.cyl(parent, f"{bid}_chcrown", (ccx, ccy, chh - 0.62), r_top + 0.22, 1.24,
            m["brick_soot"], verts=32)
    kit.cyl(parent, f"{bid}_chcap", (ccx, ccy, chh - 0.06), r_top + 0.3, 0.12,
            m["metal_dark"], verts=32)
    kit.cyl(parent, f"{bid}_chmaw", (ccx, ccy, chh + 0.02), r_top - 0.05, 0.06,
            m["void"], verts=24)
    kit.sphere(parent, f"{bid}_chlight", (ccx, ccy, chh + 0.28), 0.24,
               m["lamp_red"], segments=12, rings=8)
    kit.cyl(parent, f"{bid}_chmast", (ccx, ccy, chh + 0.09), 0.05, 0.3,
            m["metal_dark"], verts=6)
    # soot / rust streaks (dark strips on the brick)
    stb = Batch(parent, f"{bid}_chstreaks", m["brick_soot"])
    for i in range(10):
        a = i * math.tau / 10.0 + 0.2
        zz = rng.uniform(4.0, 30.0)
        hh = rng.uniform(2.0, 8.0)
        rr = _r_at(zz + hh / 2.0) + 0.03
        stb.box((ccx + math.cos(a) * rr, ccy + math.sin(a) * rr, zz + hh / 2.0),
                (0.06, rng.uniform(0.3, 0.8), hh), rot=(0, 0, a))
    stb.build()
    # climbing ladder up the stack
    a_l = math.radians(-38.0)
    ldir = Vector((math.cos(a_l), math.sin(a_l), 0))
    ltan = Vector((-math.sin(a_l), math.cos(a_l), 0))
    lad = Batch(parent, f"{bid}_chladder", m["metal_dark"])
    for side in (-1, 1):
        base = Vector((ccx, ccy, 0)) + ldir * (cr + 0.22) + ltan * (side * 0.26)
        top = Vector((ccx, ccy, 0)) + ldir * (_r_at(chh - 1.4) + 0.22) + \
            ltan * (side * 0.26) + Vector((0, 0, chh - 1.2))
        lad.seg(base + Vector((0, 0, 0.4)), top, 0.05, 0.05)
    for i in range(int((chh - 1.6) / 0.4)):
        z = 0.8 + i * 0.4
        rr = _r_at(z) + 0.22
        c = Vector((ccx, ccy, z)) + ldir * rr
        lad.seg(c - ltan * 0.26, c + ltan * 0.26, 0.022, 0.022)
    lad.build()

    # =======================================================================
    # COAL YARD (west strip): retaining wall, coal piles, conveyor, fence
    # =======================================================================
    yx0, yy0, yx1, yy1 = x0 + 0.5, y0 + 0.6, bx0 - 0.6, y1 - 0.5
    # concrete retaining wall along the north edge of the strip
    kit.box(parent, f"{bid}_retwall", ((yx0 + yx1) / 2, yy1 - 0.18, 0.65),
            (yx1 - yx0, 0.36, 1.3), m["conc_dark"])
    for i in range(4):
        kit.box(parent, f"{bid}_retbutt{i}",
                (yx0 + 0.6 + i * (yx1 - yx0 - 1.2) / 3.0, yy1 - 0.34, 0.55),
                (0.42, 0.3, 1.1), m["conc"])
    # south boundary low wall + gate posts
    kit.box(parent, f"{bid}_swall", ((yx0 + yx1) / 2, yy0 + 0.15, 0.35),
            (yx1 - yx0, 0.3, 0.7), m["conc2"])
    # concrete panel fence between yard and the boiler / along the west edge
    kit.fence(parent, f"{bid}_fence_w", (yx0 + 0.1, yy0 + 0.3), (yx0 + 0.1, yy1 - 0.4),
              height=1.9, kind="concrete")
    kit.fence(parent, f"{bid}_fence_s", (yx0 + 0.2, yy0 + 0.3), (yx1 - 1.2, yy0 + 0.3),
              height=1.9, kind="metal")
    # coal piles
    _blob(parent, f"{bid}_coal1", (ccx + 0.4, yy0 + 3.4, 0.85),
          (3.4, 2.1, 0.95), m["coal"], phase=0.7)
    _blob(parent, f"{bid}_coal2", (ccx - 0.9, yy0 + 5.6, 0.5),
          (2.0, 1.4, 0.6), m["coal"], phase=2.1)
    _blob(parent, f"{bid}_coal3", (yx0 + 1.4, yy0 + 2.2, 0.35),
          (1.3, 1.0, 0.45), m["coal"], phase=4.0)
    # coal dust on the ground
    kit.ground_quad(parent, f"{bid}_coaldust",
                    (yx0 + 0.3, yy0 + 0.8, yx1 - 0.5, yy1 - 0.9), 0.02, m["coal"],
                    tile=3.0)
    # conveyor / coal chute climbing to the hall wall
    conv = Batch(parent, f"{bid}_conveyor", m["metal_dark"])
    p_lo = Vector((yx1 - 2.6, yy0 + 7.9, 1.1))
    p_hi = Vector((bx0 + 0.15, yy0 + 7.9, 5.4))
    for side in (-0.55, 0.55):
        conv.seg(p_lo + Vector((0, side, 0)), p_hi + Vector((0, side, 0)), 0.14, 0.3)
    n_seg = 14
    for i in range(n_seg):
        t = (i + 0.5) / n_seg
        p = p_lo.lerp(p_hi, t)
        conv.seg(p + Vector((0, -0.5, 0)), p + Vector((0, 0.5, 0)), 0.07, 0.07)
    conv.box((p_hi.x + 0.6, p_hi.y, p_hi.z + 0.5), (1.4, 1.3, 1.1))
    conv.build()
    # trestle supports
    for i, t in enumerate((0.25, 0.6)):
        p = p_lo.lerp(p_hi, t)
        kit.cyl(parent, f"{bid}_ctr{i}", (p.x, p.y - 0.55, p.z / 2),
                0.09, p.z, m["metal_dark"], verts=6)
    kit.cyl(parent, f"{bid}_ctl", (p_lo.x + 1.0, p_lo.y + 0.55, p_lo.z / 2),
            0.09, p_lo.z, m["metal_dark"], verts=6)
    # yard clutter: pallets + barrels
    for i in range(3):
        kit.box(parent, f"{bid}_pallet{i}", (yx0 + 1.0 + i * 0.9, yy1 - 1.9, 0.09),
                (1.0, 1.1, 0.16), m["wood"], rot=(0, 0, rng.jitter(0.3)))
    kit.cyl(parent, f"{bid}_barrel", (yx1 - 1.0, yy1 - 2.6, 0.44), 0.34, 0.88,
            m["rust"], verts=10)
    weeds(parent, f"{bid}_weeds",
          [(yx0 + 0.7, yy0 + 1.0), (yx1 - 0.8, yy0 + 1.2), (yx1 - 1.6, yy1 - 1.2),
           (yx0 + 0.9, yy1 - 2.0), (bx0 - 0.4, yy1 - 0.9), (bx0 - 0.5, yy0 + 1.6)],
          rng)
    # low fuel oil tank + pump skid against the hall's west wall
    kit.cyl(parent, f"{bid}_tank", (bx0 - 1.9, yy0 + 11.4, 0.85), 1.0, 2.9,
            m["metal_dark"], rot=(0, math.radians(90), 0), verts=14)
    kit.box(parent, f"{bid}_pump", (bx0 - 1.4, yy0 + 13.4, 0.55),
            (1.3, 1.1, 0.9), m["conc2"])
    kit.box(parent, f"{bid}_pumpp", (bx0 - 1.4, yy0 + 13.4, 1.15),
            (0.9, 0.7, 0.3), m["rust"])

    stats["objects"] = sum(1 for o in bpy.data.objects if o.name.startswith(bid))
    stats["chimney_h"] = chh
    return stats


# ---------------------------------------------------------------------------
# 2) COOPERATIVE GARAGES
# ---------------------------------------------------------------------------
def build_garages(parent, block, rng, ctx):
    m = _mats()
    bid = block["id"]
    x0, y0, x1, y1 = block["rect"]
    units = int(block.get("units", 8))
    stats = {"objects": 0, "units": units}
    z_front, z_back = 2.95, 2.52
    w = x1 - x0
    step = w / units
    door_h = 2.42
    door_top = z_front - 0.5
    lintel = 0.5

    # ---- side + back walls -------------------------------------------------
    for face, rectf in (("NX", (x0, (y0 + y1) / 2, x0, y1)), ("PX", (x1, (y0 + y1) / 2, x1, y1))):
        pass
    kit.box_mm(parent, f"{bid}_sideW", x0, y0, 0.0, x0 + 0.32, y1, z_front, m["conc2"])
    kit.box_mm(parent, f"{bid}_sideE", x1 - 0.32, y0, 0.0, x1, y1, z_front, m["conc2"])
    kit.box_mm(parent, f"{bid}_back", x0, y1 - 0.3, 0.0, x1, y1, z_back, m["conc2"])
    # back weathering: rust streaks + a couple of high vents
    rb = Batch(parent, f"{bid}_backstreaks", m["rust_dark"])
    for i in range(10):
        sx = rng.uniform(x0 + 0.6, x1 - 0.6)
        rb.box((sx, y1 + 0.02, rng.uniform(1.2, 2.3)),
               (rng.uniform(0.1, 0.3), 0.04, rng.uniform(0.4, 1.2)))
    rb.build()
    for i in range(3):
        vx = x0 + 4.0 + i * 9.0
        kit.box(parent, f"{bid}_bvent{i}", (vx, y1 - 0.16, 2.15),
                (0.7, 0.1, 0.5), m["metal_dark"])

    # ---- front wall with door openings ------------------------------------
    fr = (x0, y0, x1, y0)
    holes = []
    for i in range(units):
        cx = x0 + step * (i + 0.5)
        holes.append((cx, step - 0.34, 0.0, door_top))
    wall_openings(parent, f"{bid}_wallF", fr, "NY", 0.0, z_front, 0.34, m["conc2"],
                  holes)
    # party piers (proud concrete piers between doors)
    piers = Batch(parent, f"{bid}_piers", m["conc2"])
    for i in range(units + 1):
        px = x0 + step * i
        piers.box((px, y0 - 0.03, z_front / 2.0), (0.34, 0.16, z_front))
    piers.build()
    # concrete lintel band across the front (already part of the wall) + a
    # slightly proud band to catch light
    band(parent, fr, "NY", door_top + 0.22, 0.3, 0.1, m["conc_light"], out=0.02)

    # ---- doors --------------------------------------------------------------
    # faded pale green / blue / grey cooperative doors, a couple with rust panels
    door_mats = [m["metal_pale"], m["door_blue"], m["metal_light"], m["door_green"],
                 m["door_grey"], m["door_blue"], m["metal_light"], m["door_green"]]
    open_unit = 5
    rib_b = Batch(parent, f"{bid}_ribs", m["metal_dark"])
    rust_b = Batch(parent, f"{bid}_doorrust", m["rust"])
    frame_b = Batch(parent, f"{bid}_doorframes", m["metal_dark"])
    yface = y0 + 0.15          # door leaves sit recessed inside the opening
    for i in range(units):
        cx = x0 + step * (i + 0.5)
        dm = door_mats[i % len(door_mats)]
        dw = step - 0.34
        if i == open_unit:
            # open unit: dark interior, one door leaf hanging open
            kit.box(parent, f"{bid}_open_dark", (cx, y0 + 0.32, 1.25),
                    (dw - 0.08, 0.07, 2.42), m["void"])
            kit.box(parent, f"{bid}_open_deep", (cx, y0 + 1.9, 1.4),
                    (dw - 0.2, 0.06, 2.2), m["void"])
            hinge_x = cx - dw / 2.0 + 0.05
            ang = math.radians(-68)
            lx = hinge_x + math.cos(ang) * dw / 2.0
            ly = y0 + 0.16 + math.sin(ang) * dw / 2.0
            kit.box(parent, f"{bid}_open_leaf", (lx, ly, 1.22),
                    (dw, 0.06, 2.4), dm, rot=(0, 0, ang))
            # clutter inside
            kit.box(parent, f"{bid}_oc1", (cx + 0.4, y0 + 0.9, 0.35),
                    (1.1, 0.9, 0.7), m["wood"], rot=(0, 0, 0.3))
            kit.box(parent, f"{bid}_oc2", (cx - 0.7, y0 + 1.4, 0.55),
                    (0.8, 1.1, 1.1), m["metal_dark"])
            kit.cyl(parent, f"{bid}_otyre", (cx + 0.9, y0 + 1.9, 0.34), 0.34, 0.22,
                    m["metal_dark"], rot=(math.radians(90), 0, 0), verts=12)
            continue
        # two corrugated leaves
        for side in (-1, 1):
            lx = cx + side * (dw / 4.0 + 0.01)
            kit.box(parent, f"{bid}_leaf{i}{side}", (lx, yface - 0.055, door_h / 2.0 + 0.08),
                    (dw / 2.0 - 0.03, 0.07, door_h), dm)
            # corrugation ribs
            nrib = 5
            for k in range(nrib):
                rx = lx - dw / 4.0 + 0.12 + (dw / 2.0 - 0.24) * k / (nrib - 1)
                rib_b.box((rx, yface - 0.11, door_h / 2.0 + 0.08),
                          (0.05, 0.035, door_h - 0.16))
            # bottom rust band
            rust_b.box((lx, yface - 0.1, 0.32), (dw / 2.0 - 0.08, 0.03, 0.52))
        if i in (0, 3, 6):
            # heavily rusted corrugated patch on a few doors (generated texture)
            _face_quad(parent, f"{bid}_rustp{i}", (cx, y0 + 0.028, 0.45), "NY",
                       dw - 0.16, 0.95, m["rust_tex"], tiles=(2.0, 0.55))
        # centre post + handles
        frame_b.box((cx, yface - 0.12, door_h / 2.0 + 0.08), (0.09, 0.09, door_h))
        frame_b.box((cx - 0.18, yface - 0.17, 1.05), (0.16, 0.06, 0.05))
        frame_b.box((cx + 0.18, yface - 0.17, 1.05), (0.16, 0.06, 0.05))
        # unit number stencil
        kit.text3d(parent, f"{bid}_num{i}", f"{rng.randint(10, 48)}",
                   (cx, yface - 0.16, door_h - 0.28), 0.18,
                   m["conc_light"], rot=(math.radians(90), 0, 0))
        # a couple of doors get a small high window
        if i in (1, 6):
            kit.box(parent, f"{bid}_dwin{i}", (cx, yface - 0.14, 2.05),
                    (0.5, 0.04, 0.3), m["glass"])
    for b in (rib_b, rust_b, frame_b):
        b.build()

    # ---- roof (single slope, corrugated) -----------------------------------
    slope = math.atan2(z_front - z_back, y1 - y0)
    zc = (z_front + z_back) / 2.0
    kit.box(parent, f"{bid}_roof", ((x0 + x1) / 2, (y0 + y1) / 2, zc + 0.06),
            (w + 0.5, (y1 - y0) + 0.7, 0.1), m["roof_metal"], rot=(-slope, 0, 0))
    ribs = Batch(parent, f"{bid}_roofribs", m["metal_dark"])
    n_rib = 26
    for i in range(n_rib):
        rx = x0 + 0.2 + (w - 0.4) * i / (n_rib - 1)
        ribs.box((rx, (y0 + y1) / 2, zc + 0.15), (0.07, (y1 - y0) + 0.55, 0.07),
                 rot=(-slope, 0, 0))
    ribs.build()
    # fascia + eave + flashings
    kit.box(parent, f"{bid}_fascia", ((x0 + x1) / 2, y0 - 0.33, z_front - 0.12),
            (w + 0.5, 0.08, 0.26), m["metal_dark"])
    kit.box(parent, f"{bid}_eave", ((x0 + x1) / 2, y0 - 0.28, z_front - 0.02),
            (w + 0.55, 0.36, 0.06), m["metal_dark"], rot=(-slope, 0, 0))
    for xx in (x0 + 0.02, x1 - 0.02):
        kit.box(parent, f"{bid}_barge{xx:.0f}", (xx, (y0 + y1) / 2, zc + 0.1),
                (0.08, (y1 - y0) + 0.6, 0.3), m["metal_dark"], rot=(-slope, 0, 0))

    # ---- apron, weeds, cracks ---------------------------------------------
    kit.ground_quad(parent, f"{bid}_apron", (x0, y0 - 1.0, x1, y0), 0.03, m["conc_dark"],
                    tile=2.0)
    crack = Batch(parent, f"{bid}_cracks", m["conc_stain"])
    for i in range(14):
        sx = rng.uniform(x0 + 0.4, x1 - 0.4)
        crack.box((sx, y0 - rng.uniform(0.1, 0.9), 0.045),
                  (rng.uniform(0.2, 1.3), 0.03, 0.012), rot=(0, 0, rng.jitter(0.6)))
    for i in range(10):
        crack.box((rng.uniform(x0 + 0.3, x1 - 0.3), y0 - 0.02, rng.uniform(0.1, 0.5)),
                  (0.03, 0.03, rng.uniform(0.15, 0.5)))
    crack.build()
    pts = [(x0 + rng.uniform(0.6, w - 0.6), y0 - rng.uniform(0.02, 0.5))
           for _ in range(12)]
    weeds(parent, f"{bid}_weeds", pts, rng, hmin=0.22, hmax=0.7, n=6)
    # a few weeds growing in the door gaps
    pts2 = [(x0 + step * i + rng.jitter(0.2), y0 + rng.uniform(0.05, 0.25))
            for i in (0, 2, 3, 7)]
    weeds(parent, f"{bid}_weeds2", pts2, rng, hmin=0.15, hmax=0.4, n=4)
    # side rust streaks
    sb = Batch(parent, f"{bid}_siderust", m["rust_dark"])
    for i in range(8):
        sb.box((x1 - 0.02, rng.uniform(y0 + 0.5, y1 - 0.5), rng.uniform(1.4, 2.4)),
               (0.04, rng.uniform(0.08, 0.3), rng.uniform(0.3, 0.9)))
        sb.box((x0 + 0.02, rng.uniform(y0 + 0.5, y1 - 0.5), rng.uniform(1.2, 2.4)),
               (0.04, rng.uniform(0.08, 0.3), rng.uniform(0.3, 0.9)))
    sb.build()
    stats["objects"] = sum(1 for o in bpy.data.objects if o.name.startswith(bid))
    return stats


# ---------------------------------------------------------------------------
# 3) STADIUM GRANDSTAND + FLOODLIGHTS
# ---------------------------------------------------------------------------
def build_stadium_stand(parent, block, rng, ctx):
    m = _mats()
    bid = block["id"]
    x0, y0, x1, y1 = block["rect"]
    stats = {"objects": 0, "rows": 5, "floodlights": 2}
    X0, X1 = x0 + 0.2, x1 - 0.2            # 27.6 m long
    Yf, Yb = y1 - 0.0, y0 + 0.2            # front (north/field) / back (south)
    base_h = 1.05
    rows = 5
    rise, tread = 0.46, 0.82
    depth = rows * tread                    # 4.1
    back_top = base_h + rows * rise         # 3.35

    # ---- solid stepped stand ------------------------------------------------
    step_b = Batch(parent, f"{bid}_steps", m["conc2"])
    nose_b = Batch(parent, f"{bid}_noses", m["conc_light"])
    for i in range(rows):
        y_front_i = Yf - i * tread
        y_back_i = y_front_i - tread
        top = base_h + (i + 1) * rise
        step_b.box(((X0 + X1) / 2, (y_front_i + y_back_i) / 2, top / 2.0),
                   (X1 - X0, tread + 0.01, top))
        nose_b.box(((X0 + X1) / 2, y_front_i + 0.02, top - 0.035),
                   (X1 - X0, 0.1, 0.07))
    step_b.build()
    nose_b.build()
    # base wall under the first row (front face at y=Yf, faces the field PY)
    fwall_holes = []
    for i, (dx, dw) in enumerate(((4.0, 1.5), (10.0, 1.5), (17.0, 1.5), (23.5, 1.5))):
        fwall_holes.append((dx, dw, 0.0, 1.0))
    fr = (X0, Yf, X1, Yf)
    wall_openings(parent, f"{bid}_frontwall", fr, "PY", 0.0, base_h, 0.34,
                  m["conc2"], fwall_holes, mat_bands=[(0.25, m["conc_dark"], 0.03)])
    for i, (dx, dw) in enumerate(((4.0, 1.5), (10.0, 1.5), (17.0, 1.5), (23.5, 1.5))):
        kit.box(parent, f"{bid}_fdark{i}", (X0 + dx, Yf - 0.3, 0.52),
                (dw - 0.06, 0.05, 1.0), m["void"])
        kit.box(parent, f"{bid}_fdoor{i}", (X0 + dx, Yf - 0.22, 0.5),
                (dw - 0.18, 0.05, 0.95), m["metal_pale"] if i % 2 else m["metal"])
        for k in range(3):
            kit.box(parent, f"{bid}_fgrille{i}_{k}", (X0 + dx, Yf - 0.24, 0.25 + k * 0.3),
                    (dw - 0.25, 0.03, 0.045), m["metal_dark"])
    # graffiti-ish patches on the base wall (outer/north face)
    graf = Batch(parent, f"{bid}_graff", m["conc_stain"])
    for i in range(6):
        graf.box((X0 + rng.uniform(1.0, X1 - X0 - 1.0), Yf + 0.02,
                  rng.uniform(0.3, 0.9)), (rng.uniform(0.4, 1.4), 0.03,
                                          rng.uniform(0.15, 0.5)),
                 rot=(0, 0, rng.jitter(0.8)))
    graf.build()

    # ---- side cheek walls + side stairs ------------------------------------
    side_b = Batch(parent, f"{bid}_sides", m["conc2"])
    for sx in (X0, X1 - 0.32):
        for i in range(rows):
            y_front_i = Yf - i * tread
            top = base_h + (i + 1) * rise
            side_b.box((sx + 0.16, (y_front_i + y_front_i - tread) / 2, top / 2.0),
                       (0.32, tread + 0.01, top))
    side_b.build()
    # side staircases (steep, at both ends, going up along the slope)
    for si, sx in enumerate((X0 + 0.1, X1 - 1.4)):
        n = 14
        stair = Batch(parent, f"{bid}_sstair{si}", m["conc_light"])
        for k in range(n):
            yy = Yf - 0.5 - k * 0.31
            zz = base_h + (k + 1) * (back_top - base_h) / n
            stair.box((sx + 0.65, yy, zz / 2.0), (1.25, 0.33, zz))
        stair.build()
        # diagonal steel handrail following the stair slope
        hr = Batch(parent, f"{bid}_srail{si}", m["metal_dark"])
        hr.seg((sx + 0.06, Yf - 0.35, base_h + 1.0),
               (sx + 0.06, Yf - 4.35, back_top + 1.0), 0.05, 0.05)
        hr.seg((sx + 1.3, Yf - 0.35, base_h + 1.0),
               (sx + 1.3, Yf - 4.35, back_top + 1.0), 0.05, 0.05)
        for k in range(5):
            yy = Yf - 0.5 - k * 0.9
            zz = base_h + (k + 1.0) * (back_top - base_h) / 5.0
            hr.seg((sx + 0.06, yy, zz), (sx + 0.06, yy, zz + 0.95), 0.04, 0.04)
        hr.build()
    # ---- back wall + railing ----------------------------------------------
    kit.box(parent, f"{bid}_backwall", ((X0 + X1) / 2, Yb - 0.11, back_top / 2.0),
            (X1 - X0, 0.24, back_top), m["conc2"])
    kit.box(parent, f"{bid}_backcop", ((X0 + X1) / 2, Yb - 0.11, back_top + 0.06),
            (X1 - X0 + 0.1, 0.34, 0.12), m["conc_light"])
    kit.railing_line(parent, f"{bid}_backrail", (X0 + 0.1, Yb - 0.2, back_top + 0.1),
                     (X1 - 0.1, Yb - 0.2, back_top + 0.1), back_top + 0.12, 0.8,
                     m["metal_dark"])

    # ---- canopy on thin steel columns -------------------------------------
    col_b = Batch(parent, f"{bid}_canopycols", m["metal_dark"])
    z_col_top = 5.15
    cols_x = [X0 + 1.3 + i * (X1 - X0 - 2.6) / 5.0 for i in range(6)]
    for cxp in cols_x:
        kit.cyl(parent, f"{bid}_col{cxp:.1f}", (cxp, Yb + 0.35, (back_top + z_col_top) / 2),
                0.1, z_col_top - back_top, m["metal_dark"], verts=8)
        col_b.box((cxp, Yb + 0.35, back_top + 0.06), (0.4, 0.4, 0.12))
        col_b.box((cxp, Yb + 0.35, z_col_top - 0.05), (0.3, 0.3, 0.1))
    # diagonal braces
    for i, cxp in enumerate(cols_x):
        if i % 2 == 0:
            col_b.seg((cxp, Yb + 0.35, z_col_top - 0.15),
                      (cxp + 1.2, Yb + 1.5, z_col_top - 0.75), 0.06, 0.06)
    col_b.build()
    c_slope = math.atan2(0.34, (Yf + 0.45) - (Yb - 0.3))
    c_y0, c_y1 = Yb - 0.35, Yf + 0.45
    c_zc = z_col_top + 0.12
    kit.box(parent, f"{bid}_canopy", ((X0 + X1) / 2, (c_y0 + c_y1) / 2, c_zc),
            (X1 - X0 + 0.6, c_y1 - c_y0, 0.12), m["roof_metal"],
            rot=(-c_slope, 0, 0))
    kit.box(parent, f"{bid}_canopyfascia", ((X0 + X1) / 2, c_y1 - 0.05, c_zc - 0.14),
            (X1 - X0 + 0.62, 0.06, 0.3), m["metal_dark"])
    joist = Batch(parent, f"{bid}_joists", m["metal_dark"])
    n_j = 10
    for i in range(n_j):
        jxx = X0 + 0.5 + (X1 - X0 - 1.0) * i / (n_j - 1)
        joist.box((jxx, (c_y0 + c_y1) / 2, c_zc - 0.12),
                  (0.09, c_y1 - c_y0, 0.12), rot=(-c_slope, 0, 0))
    joist.build()
    # back beam under the canopy
    kit.box(parent, f"{bid}_cbeam", ((X0 + X1) / 2, Yb + 0.2, z_col_top - 0.18),
            (X1 - X0, 0.18, 0.22), m["metal_dark"])

    # ---- a few benches on the rows -----------------------------------------
    for bi, (bx, row) in enumerate(((6.0, 2), (14.0, 3), (21.5, 2))):
        yy = Yf - (row + 0.5) * tread
        zz = base_h + (row + 1) * rise
        wood = m["wood"]
        kit.box(parent, f"{bid}_bench{bi}_seat", (X0 + bx, yy, zz + 0.44),
                (2.9, 0.34, 0.06), wood)
        kit.box(parent, f"{bid}_bench{bi}_back", (X0 + bx, yy + 0.28, zz + 0.82),
                (2.9, 0.05, 0.3), wood, rot=(math.radians(-10), 0, 0))
        for side in (-1, 1):
            kit.box(parent, f"{bid}_bench{bi}_leg{side}",
                    (X0 + bx + side * 1.1, yy, zz + 0.22),
                    (0.07, 0.36, 0.44), m["metal_dark"])
    # weeds between the rows
    pts = [(X0 + rng.uniform(1.0, X1 - X0 - 1.0), Yf - rng.uniform(0.1, depth)) for _ in range(10)]
    weeds(parent, f"{bid}_weeds", pts, rng, hmin=0.1, hmax=0.35, n=4)

    # =======================================================================
    # FLOODLIGHT MASTS at the field corners
    # =======================================================================
    for mi, (mx, my) in enumerate(((66.0, 70.0), (94.0, 70.0))):
        H = 18.0
        base_half = 0.62
        top_half = 0.26
        # concrete footing
        kit.box(parent, f"{bid}_fl_base{mi}", (mx, my, 0.25),
                (1.7, 1.7, 0.5), m["conc_dark"])
        steel = Batch(parent, f"{bid}_fl_lattice{mi}", m["metal_light"])
        corners = [(-1, -1), (1, -1), (1, 1), (-1, 1)]
        zs = [0.5 + i * (H - 0.5) / 9.0 for i in range(10)]

        def _half(z):
            t = (z - 0.5) / (H - 0.5)
            return base_half + (top_half - base_half) * t

        # 4 legs
        for cx_, cy_ in corners:
            for i in range(len(zs) - 1):
                z_a, z_b = zs[i], zs[i + 1]
                ha, hb = _half(z_a), _half(z_b)
                steel.seg((mx + cx_ * ha, my + cy_ * ha, z_a),
                          (mx + cx_ * hb, my + cy_ * hb, z_b), 0.09, 0.09)
        # horizontal belts + diagonals
        for i in range(1, len(zs)):
            z = zs[i]
            h = _half(z)
            for k in range(4):
                a0 = corners[k]
                a1 = corners[(k + 1) % 4]
                steel.seg((mx + a0[0] * h, my + a0[1] * h, z),
                          (mx + a1[0] * h, my + a1[1] * h, z), 0.05, 0.05)
        for i in range(len(zs) - 1):
            z_a, z_b = zs[i], zs[i + 1]
            ha, hb = _half(z_a), _half(z_b)
            for k in range(4):
                a0 = corners[k]
                a1 = corners[(k + 1) % 4]
                if (i + k) % 2 == 0:
                    steel.seg((mx + a0[0] * ha, my + a0[1] * ha, z_a),
                              (mx + a1[0] * hb, my + a1[1] * hb, z_b), 0.035, 0.035)
                else:
                    steel.seg((mx + a1[0] * ha, my + a1[1] * ha, z_a),
                              (mx + a0[0] * hb, my + a0[1] * hb, z_b), 0.035, 0.035)
        steel.build()
        # top platform + crossarm
        kit.box(parent, f"{bid}_fl_plat{mi}", (mx, my, H + 0.1), (1.1, 1.1, 0.12),
                m["metal_dark"])
        kit.box(parent, f"{bid}_fl_arm{mi}", (mx, my, H + 0.45), (3.4, 0.24, 0.24),
                m["metal_dark"])
        lamp_b = Batch(parent, f"{bid}_fl_lamps{mi}", m["metal_dark"])
        glow_b = Batch(parent, f"{bid}_fl_glow{mi}", m["lamp_cool"])
        for r in range(2):
            for c in range(3):
                lx = mx - 1.25 + c * 1.25
                lz = H + 0.75 + r * 0.62
                lamp_b.box((lx, my - 0.1, lz), (0.52, 0.34, 0.42))
                glow_b.box((lx, my - 0.31, lz - 0.02), (0.42, 0.05, 0.3),
                           rot=(math.radians(-14), 0, 0))
                lamp_b.box((lx, my + 0.12, lz), (0.1, 0.24, 0.1))
        lamp_b.build()
        glow_b.build()
        # feed cable down the mast + warning light
        kit.cyl(parent, f"{bid}_fl_cable{mi}", (mx + 0.3, my + 0.3, H / 2 + 0.3),
                0.03, H, m["metal_dark"], verts=5)
        kit.sphere(parent, f"{bid}_fl_red{mi}", (mx, my, H + 1.55), 0.12,
                   m["lamp_red"], segments=8, rings=6)
        # ladder rungs on the north face
        lad = Batch(parent, f"{bid}_fl_rungs{mi}", m["metal_dark"])
        for i in range(int((H - 2.0) / 0.4)):
            z = 1.2 + i * 0.4
            h = _half(z)
            lad.seg((mx - 0.22, my + h + 0.02, z), (mx + 0.22, my + h + 0.02, z),
                    0.03, 0.03)
        lad.build()
    stats["objects"] = sum(1 for o in bpy.data.objects if o.name.startswith(bid))
    return stats


# ---------------------------------------------------------------------------
# 4) GYM / SPORTS HALL
# ---------------------------------------------------------------------------
def build_gym(parent, block, rng, ctx):
    m = _mats()
    wm = ctx.get("wmats") or kit.win_materials()
    bid = block["id"]
    x0, y0, x1, y1 = block["rect"]
    stats = {"objects": 0, "windows": 0}

    hx0, hy0, hx1, hy1 = x0 + 0.35, y0 + 4.6, x1 - 0.35, y1 - 0.55   # hall mass
    Z1 = 10.7
    ann_x0, ann_x1 = x0 + 3.2, x0 + 13.6
    ann_y0, ann_y1 = y0 + 1.0, hy0 + 0.15
    ann_z = 3.75
    rect = (hx0, hy0, hx1, hy1)
    plaster = m["plaster"]
    blue = m["paint_blue"]
    bands = [(2.45, blue, 0.05), (Z1, plaster, 0.0)]
    thick = 0.42

    # ---- long facades with tall window bands -------------------------------
    for face in ("NY", "PY"):
        Lf = hx1 - hx0
        cols, step = even_cols(Lf, 5, 2.2)
        win_w = step - 0.62
        zb, zt = 4.25, 9.25
        holes = [(c, win_w, zb, zt) for c in cols]
        wall_openings(parent, f"{bid}_wall{face}", rect, face, 0.0, Z1, thick,
                      plaster, holes, mat_bands=bands)
        st = windows(parent, f"{bid}_{face}", rect, face, cols, [(zb, zt)],
                     win_w, zt - zb, wm, rng, lit_ratio=0.08,
                     overrides={(0, 0): False, (0, 2): True, (0, 3): False},
                     recess=0.24)
        stats["windows"] += st["windows"]
        # extra mullions -> reads as a continuous glazed sports-hall band
        mb = Batch(parent, f"{bid}_mull{face}", wm["frame_grey"])
        P0, u, _L, n = kit.wall_frame(rect, face)
        for c in cols:
            mullions(mb, P0, u, n, c, zb, zt, win_w, n_vert=4, n_horz=2,
                     out=-0.07, thick=0.05)
            # horizontal transom bands proud of the wall
            mb.ub(P0, u, n, c, zb - 0.1, 0.02, win_w + 0.3, 0.12, 0.14)
        mb.build()
        # painted plinth weathering + drainpipes
        stb = Batch(parent, f"{bid}_wstain{face}", m["conc_stain"])
        P0, u, _L, n = kit.wall_frame(rect, face)
        for i in range(10):
            stb.ub(P0, u, n, rng.uniform(1.2, Lf - 1.2), rng.uniform(3.0, 6.6),
                   0.035, rng.uniform(0.12, 0.4), 0.045, rng.uniform(0.8, 2.6))
        stb.build()

    # ---- gable ends (PX / NX): high slit windows + doors + ladders ----------
    for face, is_east in (("PX", True), ("NX", False)):
        Lf = hy1 - hy0
        cols = [Lf * 0.22, Lf * 0.5, Lf * 0.78]
        holes = [(c, 1.0, 5.4, 8.9) for c in cols]
        # ground: door + a pair of small windows
        door_c = Lf * 0.32
        holes += [(door_c, 1.35, 0.0, 2.35)]
        holes += [(Lf * 0.62, 1.1, 2.3, 3.6), (Lf * 0.82, 1.1, 2.3, 3.6)]
        wall_openings(parent, f"{bid}_wall{face}", rect, face, 0.0, Z1, thick,
                      plaster, holes, mat_bands=bands)
        st = windows(parent, f"{bid}_{face}s", rect, face, cols, [(5.4, 8.9)],
                     0.95, 3.5, wm, rng, lit_ratio=0.12, recess=0.22)
        stats["windows"] += st["windows"]
        st = windows(parent, f"{bid}_{face}g", rect, face,
                     [Lf * 0.62, Lf * 0.82], [(2.3, 3.6)], 1.05, 1.3, wm, rng,
                     lit_ratio=0.2, grille=True)
        stats["windows"] += st["windows"]
        P0, u, _L, n = kit.wall_frame(rect, face)
        kit.box(parent, f"{bid}_gdoor{face}",
                (P0.x + u.x * door_c + n.x * (-0.14), P0.y + u.y * door_c + n.y * (-0.14),
                 1.18), (1.4 if abs(u.x) < 0.5 else 0.09,
                         0.09 if abs(u.x) < 0.5 else 1.4, 2.3), m["metal_dark"])
        # roof-access ladder
        ladder(parent, f"{bid}_dladder{face}",
               (P0.x + u.x * door_c * 0.2 + n.x * 0.0,
                P0.y + u.y * door_c * 0.2), Z1 + 0.05, facing=face)
        # wall lamp over the side door
        kit.box(parent, f"{bid}_slamp{face}",
                (P0.x + u.x * door_c + n.x * 0.3, P0.y + u.y * door_c + n.y * 0.3, 2.75),
                (0.3, 0.3, 0.12) if abs(u.x) < 0.5 else (0.3, 0.3, 0.12), m["metal_dark"])
        # vent grilles near the roof
        vg = Batch(parent, f"{bid}_vents{face}", m["metal_dark"])
        for i in range(4):
            ss = 0.12 * Lf + i * 0.22 * (Lf - 0.1)
            vg.ub(P0, u, n, min(ss, Lf - 0.4), 9.9, 0.04, 0.8, 0.06, 0.35)
        vg.build()

    # ---- roof parapet + details -------------------------------------------
    kit.roof_flat(parent, f"{bid}_roof", rect, Z1, m["conc_dark"])
    kit.parapet(parent, f"{bid}_parapet", rect, Z1, 0.5, 0.24, m["conc2"],
                coping_mat=m["conc_light"])
    kit.roof_details(parent, f"{bid}_roofdet", rect, Z1 + 0.05, rng, m["conc2"])
    # machine boxes + long skylight strip (typical sports hall roof)
    for i in range(3):
        ux = hx0 + 3.2 + i * 4.6
        kit.box(parent, f"{bid}_roofbox{i}", (ux, hy0 + (hy1 - hy0) * (0.3 + 0.25 * i),
                  Z1 + 0.62), (1.7, 1.25, 1.0), m["metal_light"], bevel=0.03)
        kit.box(parent, f"{bid}_roofboxl{i}", (ux, hy0 + (hy1 - hy0) * (0.3 + 0.25 * i),
                  Z1 + 1.15), (1.8, 1.35, 0.1), m["metal_dark"])
    kit.box(parent, f"{bid}_skylight", ((hx0 + hx1) / 2, hy1 - 3.2, Z1 + 0.3),
            (12.0, 1.5, 0.45), m["glass_cool"])
    kit.box(parent, f"{bid}_skylightf", ((hx0 + hx1) / 2, hy1 - 3.2, Z1 + 0.55),
            (12.2, 1.7, 0.1), m["metal_dark"])
    for ri, xx in enumerate((hx0 + 1.0, hx1 - 1.0)):
        kit.cyl(parent, f"{bid}_rvent{ri}", (xx, hy0 + 2.0, Z1 + 0.5),
                0.22, 1.0, m["metal_dark"], verts=10)

    # ---- painted plinth + belt course (distinct gym look) -----------------
    for face in ("NY", "PY", "PX", "NX"):
        band(parent, rect, face, 2.42, 0.16, 0.16, m["conc_light"], out=0.05)
        band(parent, rect, face, 10.35, 0.2, 0.2, m["conc_light"], out=0.05)

    # ---- entrance annex on the south (NY) facade ---------------------------
    ar = (ann_x0, ann_y0, ann_x1, ann_y1)
    kit.box(parent, f"{bid}_annex", ((ann_x0 + ann_x1) / 2, (ann_y0 + ann_y1) / 2,
                                    ann_z / 2.0),
            (ann_x1 - ann_x0, ann_y1 - ann_y0, ann_z), m["conc2"], bevel=0.04)
    # annex painted band
    kit.box(parent, f"{bid}_annexband", ((ann_x0 + ann_x1) / 2, (ann_y0 + ann_y1) / 2, 0.9),
            (ann_x1 - ann_x0 + 0.1, ann_y1 - ann_y0 + 0.1, 1.8), m["paint_blue"])
    kit.box(parent, f"{bid}_annexroof", ((ann_x0 + ann_x1) / 2, (ann_y0 + ann_y1) / 2,
                                        ann_z + 0.1),
            (ann_x1 - ann_x0 + 0.3, ann_y1 - ann_y0 + 0.3, 0.2), m["conc_dark"])
    # recessed entrance: door + flanking windows
    adoor_c = (ann_x0 + ann_x1) / 2
    annot_face_y = ann_y0 - 0.02
    kit.box(parent, f"{bid}_adark", (adoor_c, ann_y0 + 0.06, 1.35),
            (2.3, 0.12, 2.7), m["void"])
    kit.box(parent, f"{bid}_adoor", (adoor_c, ann_y0 - 0.02, 1.25),
            (2.0, 0.06, 2.5), m["metal_dark"])
    kit.box(parent, f"{bid}_adoorg", (adoor_c, ann_y0 - 0.06, 1.25),
            (0.05, 0.03, 2.45), m["metal_light"])
    kit.box(parent, f"{bid}_adoorgl", (adoor_c, ann_y0 - 0.07, 1.55),
            (1.2, 0.03, 0.55), m["glass_cool"])
    for side in (-1, 1):
        kit.box(parent, f"{bid}_awin{side}", (adoor_c + side * 2.6, ann_y0 - 0.02, 1.75),
                (1.3, 0.06, 1.1), m["glass_cool"])
        kit.box(parent, f"{bid}_awinf{side}", (adoor_c + side * 2.6, ann_y0 - 0.05, 1.75),
                (1.45, 0.04, 1.25), m["frame_grey"] if False else m["metal_pale"])
    # canopy over the entrance (protrudes 1.0 m to the rect edge)
    kit.box(parent, f"{bid}_acann", (adoor_c, ann_y0 - 0.55, 3.05),
            (6.2, 1.3, 0.14), m["conc_light"])
    for side in (-1, 1):
        kit.box(parent, f"{bid}_acannb{side}", (adoor_c + side * 2.9, ann_y0 - 1.0, 2.6),
                (0.12, 0.12, 0.9), m["metal_dark"])
    # steps in front of the annex (stay inside the rect)
    for i in range(3):
        kit.box(parent, f"{bid}_astep{i}", (adoor_c, ann_y0 - 0.35 - i * 0.32, 0.06 + i * 0.12),
                (3.6, 0.34, 0.13 + i * 0.12), m["conc_light"])
    # entrance lamps (cool, one warm)
    for side, mat in ((-1, m["lamp_cool"]), (1, m["lamp_warm"])):
        kit.box(parent, f"{bid}_alamp{side}", (adoor_c + side * 1.6, ann_y0 - 0.18, 2.9),
                (0.22, 0.34, 0.1), mat)
    kit.signboard(parent, f"{bid}_sign", (adoor_c, ann_y0 - 0.03, 3.28),
                  (1, 0, 0), (0, -1, 0), 3.8, 0.46, "СПОРТЗАЛ", rng,
                  bg="#2E3A42", fg="#DCD8C8", glow=1.2)
    # basketball backboards on the east wall, facing the court
    for bi, by in enumerate((hy0 + 3.0, hy0 + 11.0)):
        kit.box(parent, f"{bid}_bb{bi}", (hx1 + 0.06, by, 3.25),
                (0.08, 1.8, 1.05), m["conc_light"])
        kit.box(parent, f"{bid}_bbr{bi}", (hx1 + 0.11, by, 3.2),
                (0.03, 1.2, 0.7), m["metal_dark"])
        # rim + net
        ring = Batch(parent, f"{bid}_rim{bi}", m["rust"])
        for k in range(12):
            a = k * math.tau / 12.0
            ring.box((hx1 + 0.42 + math.cos(a) * 0.24, by + math.sin(a) * 0.24, 3.05),
                     (0.09, 0.09, 0.035), rot=(0, 0, a))
        ring.build()
        net = Batch(parent, f"{bid}_net{bi}", m["conc_light"])
        for k in range(6):
            a = k * math.tau / 6.0
            p0 = Vector((hx1 + 0.42 + math.cos(a) * 0.22, by + math.sin(a) * 0.22, 3.03))
            p1 = Vector((hx1 + 0.42 + math.cos(a) * 0.13, by + math.sin(a) * 0.13, 2.68))
            net.seg(p0, p1, 0.012, 0.012)
        net.build()
        kit.box(parent, f"{bid}_bbarm{bi}", (hx1 + 0.02, by, 3.25),
                (0.16, 0.2, 1.2), m["metal_dark"])

    # ---- interior void volume + floor slab hint ---------------------------
    void_box(parent, f"{bid}_void", rect, 0.02, Z1 - 0.25, inset=0.6, mat=m["void"])
    kit.drainpipe(parent, f"{bid}_drain1", hx0 + 0.4, hy0 + 0.4, 0.2, Z1 - 0.2)
    kit.drainpipe(parent, f"{bid}_drain2", hx1 - 1.6, hy0 + 0.4, 0.2, Z1 - 0.2)

    stats["objects"] = sum(1 for o in bpy.data.objects if o.name.startswith(bid))
    return stats


# ---------------------------------------------------------------------------
# 5) UNFINISHED CONCRETE FRAME
# ---------------------------------------------------------------------------
def build_frame(parent, block, rng, ctx):
    m = _mats()
    bid = block["id"]
    x0, y0, x1, y1 = block["rect"]
    stats = {"objects": 0, "levels": 5}
    # plan grid
    gx = [x0 + 0.5 + i * (x1 - x0 - 1.0) / 3.0 for i in range(4)]
    gy = [y0 + 0.5 + j * (y1 - y0 - 1.0) / 2.0 for j in range(3)]
    levels = [2.8 * (i + 1) for i in range(5)]        # 2.8 .. 14.0
    col = 0.38
    colmat = m["conc2"]
    colb = Batch(parent, f"{bid}_cols", colmat)
    # column heights: top level partial -> staggered silhouette
    heights = {}
    for i in range(4):
        for j in range(3):
            base = levels[3] + 0.22 if (i + j) % 3 != 0 else levels[4] + 0.22
            if (i, j) in ((0, 0), (3, 2)):
                base = levels[4] + 1.5
            if (i, j) == (1, 2):
                base = levels[2] + 0.22
            heights[(i, j)] = base + rng.uniform(0.0, 0.35)
            colb.box((gx[i], gy[j], heights[(i, j)] / 2.0), (col, col, heights[(i, j)]))
    colb.build()
    # exposed column starters at ground (slightly wider footings)
    for i in range(4):
        for j in range(3):
            kit.box(parent, f"{bid}_foot{i}{j}", (gx[i], gy[j], 0.18),
                    (0.62, 0.62, 0.36), m["conc_dark"])

    # ---- floor slabs (full at 2.8/5.6/8.4, partial higher) -----------------
    hole = (gx[1] + 0.3, gy[1] + 0.4, gx[1] + 2.9, gy[1] + 4.4)  # stair void
    slab_b = Batch(parent, f"{bid}_slabs", m["conc"])
    slab_edges = [(x0 + 0.2, y0 + 0.2, x1 - 0.2, y1 - 0.2)]

    def slab_rect(z, rect_, thickness=0.24):
        rx0, ry0, rx1, ry1 = rect_
        # split around the stair hole
        hx0, hy0, hx1, hy1 = hole
        if not (hx1 < rx0 or hx0 > rx1 or hy1 < ry0 or hy0 > ry1):
            slab_b.box(((rx0 + hx0) / 2, (ry0 + ry1) / 2, z - thickness / 2),
                       (hx0 - rx0, ry1 - ry0, thickness))
            slab_b.box(((hx1 + rx1) / 2, (ry0 + ry1) / 2, z - thickness / 2),
                       (rx1 - hx1, ry1 - ry0, thickness))
            slab_b.box(((hx0 + hx1) / 2, (ry0 + hy0) / 2, z - thickness / 2),
                       (hx1 - hx0, hy0 - ry0, thickness))
            slab_b.box(((hx0 + hx1) / 2, (hy1 + ry1) / 2, z - thickness / 2),
                       (hx1 - hx0, ry1 - hy1, thickness))
        else:
            slab_b.box(((rx0 + rx1) / 2, (ry0 + ry1) / 2, z - thickness / 2),
                       (rx1 - rx0, ry1 - ry0, thickness))

    slab_rect(levels[0], slab_edges[0])
    slab_rect(levels[1], slab_edges[0])
    slab_rect(levels[2], slab_edges[0])
    slab_rect(levels[3], (x0 + 0.3, y0 + 0.3, x1 - 0.3, y0 + 8.2))     # partial
    slab_rect(levels[4], (x0 + 0.3, y1 - 5.4, x1 - 5.0, y1 - 0.3), 0.22)  # fragment
    slab_b.build()
    # slab edge beams on the visible (NY) face
    beam = Batch(parent, f"{bid}_beams", m["conc2"])
    for z in levels[:4]:
        for j in range(2):
            beam.box(((gx[j] + gx[j + 1]) / 2, y0 + 0.2, z - 0.4),
                     (gx[j + 1] - gx[j] - col, 0.3, 0.5))
        beam.box((x0 + 0.2, (gy[0] + gy[1]) / 2, z - 0.4),
                 (0.3, gy[1] - gy[0] - col, 0.5))
    beam.build()

    # ---- stair flights in the stair bay -----------------------------------
    stair_b = Batch(parent, f"{bid}_stairs", m["conc2"])
    sx_a, sx_b = gx[1] + 0.35, gx[1] + 1.75
    sy_a = gy[1] + 4.2
    n_st = 9
    for lvl in range(4):
        z_base = levels[lvl - 1] if lvl > 0 else 0.0
        z_top = levels[lvl]
        rise = (z_top - z_base) / n_st
        for k in range(n_st):
            zz = z_base + (k + 1) * rise
            yy = sy_a - (k + 0.5) * 0.33
            stair_b.box(((sx_a + sx_b) / 2, yy, zz / 2.0),
                        (sx_b - sx_a, 0.33, zz))
        # landing
        stair_b.box(((sx_a + sx_b) / 2, sy_a - 1.9, z_top - 0.12),
                    (sx_b - sx_a, 1.4, 0.24))
    stair_b.build()
    rail_b = Batch(parent, f"{bid}_srail", m["metal_dark"])
    for lvl in range(4):
        z_base = levels[lvl - 1] if lvl > 0 else 0.0
        z_top = levels[lvl]
        for k in range(0, n_st, 2):
            zz = z_base + (k + 1) * (z_top - z_base) / n_st
            yy = sy_a - (k + 0.5) * 0.33
            rail_b.box((sx_b + 0.05, yy, zz + 0.45), (0.05, 0.05, 0.9))
    rail_b.build()

    # ---- dark interior void planes so the skeleton reads deep --------------
    # (kept deep inside the plan and interrupted per bay: they must read as shadow
    # in the rooms, never as walls closing the frame)
    void_b = Batch(parent, f"{bid}_void", m["void"])
    mid_y = gy[1]
    for z in levels[:4]:
        for j in range(3):
            if (j + int(z)) % 3 == 0:
                continue
            void_b.box((gx[1] + 0.5, mid_y - 1.1, z - 1.45),
                       (gx[2] - gx[1] - 1.0, 0.08, 2.35))
    for z in levels[1:4]:
        for i in range(2):
            void_b.box((gx[0] + 3.2 + i * 6.0, gy[0] + 2.6, z - 1.45),
                       (2.2, 0.08, 2.4))
    void_b.build()

    # ---- boarded openings on the south face -------------------------------
    ply_b = Batch(parent, f"{bid}_boards", m["ply"])
    boards = ((1.6, levels[1]), (8.2, levels[2]), (13.6, levels[1]),
              (5.0, levels[3]))
    for bi, (bx, bz) in enumerate(boards):
        ply_b.box((bx, y0 + 0.16, bz - 1.25), (2.3, 0.08, 1.7),
                  rot=(0, 0, rng.jitter(0.05)))
        ply_b.box((bx - 0.4, y0 + 0.1, bz - 1.3), (1.2, 0.05, 0.22),
                  rot=(0, 0, 0.25))
    ply_b.build()

    # ---- ground floor partial masonry infill ------------------------------
    block_b = Batch(parent, f"{bid}_blockwork", m["brick_light"])
    for i in range(4):
        h = rng.uniform(1.4, 2.7)
        if i == 1:
            continue
        x_a = gx[i] + 0.2
        x_b = gx[min(i + 1, 3)] - 0.2
        block_b.box(((x_a + x_b) / 2, y0 + 0.2, h / 2.0), (x_b - x_a, 0.24, h))
        # jagged unfinished top
        for k in range(4):
            hh = rng.uniform(0.15, 0.45)
            block_b.box((x_a + (x_b - x_a) * (k + 0.5) / 4.0, y0 + 0.2, h + hh / 2.0),
                        ((x_b - x_a) / 4.0 - 0.05, 0.26, hh))
    block_b.build()

    # ---- rebar stubs on column tops + bent ends ---------------------------
    reb = Batch(parent, f"{bid}_rebar", m["rust_dark"])
    for i in range(4):
        for j in range(3):
            top = heights[(i, j)]
            for (dx, dy) in ((-0.1, -0.1), (0.1, -0.1), (0.1, 0.1), (-0.1, 0.1)):
                p0 = (gx[i] + dx, gy[j] + dy, top)
                h = rng.uniform(0.5, 1.15)
                p1 = (p0[0] + rng.jitter(0.07), p0[1] + rng.jitter(0.07), top + h)
                reb.seg(p0, p1, 0.035, 0.035)
                ang = rng.uniform(0, math.tau)
                reb.seg(p1, (p1[0] + math.cos(ang) * 0.22, p1[1] + math.sin(ang) * 0.22,
                             p1[2] + 0.12), 0.03, 0.03)
    # rebar tails sticking out of slab edges
    for z in levels[:3]:
        for i in range(5):
            rx = rng.uniform(x0 + 1.5, x1 - 1.5)
            reb.seg((rx, y0 + 0.2, z - 0.2), (rx + rng.jitter(0.3), y0 - 0.35, z + 0.1),
                    0.03, 0.03)
    reb.build()

    # ---- construction debris at the base ----------------------------------
    rubble(parent, f"{bid}_rubble1", (x0 + 4.0, y0 + 2.4), 2.2, rng,
           m["conc2"], count=16)
    rubble(parent, f"{bid}_rubble2", (x1 - 3.6, y0 + 4.2), 1.6, rng,
           m["conc_dark"], count=10)
    # block stacks
    st_b = Batch(parent, f"{bid}_blocks", m["brick_light"])
    for s_i, (sx, sy) in enumerate(((x1 - 2.0, y1 - 2.2), (x0 + 2.4, y1 - 3.0))):
        for k in range(4):
            for l in range(3):
                st_b.box((sx + l * 0.42, sy + k * 0.26, 0.2 + l * 0.0),
                         (0.38, 0.22, 0.35))
    st_b.build()
    leaning_boards(parent, f"{bid}_boards2", (x0 + 8.0, y0 + 2.0), rng, n=5)
    # pallets, buckets, barrel, cable reel
    for i in range(4):
        kit.box(parent, f"{bid}_pallet{i}", (x0 + 2.0 + i * 1.4, y0 + 3.6, 0.1),
                (1.1, 1.0, 0.18), m["wood"], rot=(0, 0, rng.jitter(0.4)))
    kit.cyl(parent, f"{bid}_cable_reel", (x1 - 4.0, y1 - 2.0, 0.75), 0.95, 0.9,
            m["wood"], rot=(0, math.radians(90), 0), verts=14)
    kit.cyl(parent, f"{bid}_barrel", (x0 + 12.0, y0 + 1.6, 0.45), 0.34, 0.9,
            m["rust"], verts=10)
    kit.box(parent, f"{bid}_bucket", (x1 - 6.0, y0 + 2.2, 0.2), (0.34, 0.34, 0.4),
            m["metal_dark"], rot=(0, 0, 0.3))
    weeds(parent, f"{bid}_weeds",
          [(x0 + rng.uniform(1.0, x1 - x0 - 1.0),
            y0 + rng.uniform(0.6, 3.0)) for _ in range(14)], rng, hmin=0.2, hmax=0.7)

    # ---- hanging crane cable + hook ---------------------------------------
    hook_x, hook_y = gx[2], gy[2]
    z_top = levels[3] + 0.2
    kit.box(parent, f"{bid}_jib", (hook_x - 1.4, hook_y, z_top + 0.55),
            (2.6, 0.22, 0.22), m["metal_dark"])
    kit.cyl(parent, f"{bid}_jibpost", (hook_x - 0.1, hook_y, z_top + 0.55),
            0.06, 1.2, m["metal_dark"], verts=6)
    kit.cyl_between(parent, f"{bid}_cable", (hook_x - 2.5, hook_y, z_top + 0.4),
                    (hook_x - 2.5, hook_y, 6.2), 0.025, m["metal_dark"])
    kit.cyl(parent, f"{bid}_cable_block", (hook_x - 2.5, hook_y, 6.35), 0.16, 0.24,
            m["rust"], verts=10)
    for i, (a0, a1) in enumerate((((0, 0, -0.15), (0.12, 0, -0.4)),
                                  ((0.12, 0, -0.4), (0.02, 0, -0.62)))):
        p = Vector((hook_x - 2.5, hook_y, 6.2))
        kit.cyl_between(parent, f"{bid}_hook{i}", p + Vector(a0), p + Vector(a1),
                        0.045, m["rust"], verts=6)
    kit.sphere(parent, f"{bid}_hook_tip", (hook_x - 2.48, hook_y, 5.55), 0.09,
               m["rust"], segments=8, rings=6)

    stats["objects"] = sum(1 for o in bpy.data.objects if o.name.startswith(bid))
    stats["top_height"] = max(heights.values())
    return stats


# ---------------------------------------------------------------------------
# 6) SALES KIOSK
# ---------------------------------------------------------------------------
def build_kiosk(parent, block, rng, ctx):
    m = _mats()
    bid = block["id"]
    x0, y0, x1, y1 = block["rect"]
    stats = {"objects": 0}
    KX0, KX1 = x0 + 0.4, x1 - 0.4
    KY0, KY1 = y0 + 0.35, y1 - 0.4
    H = 2.9
    kx_c = (KX0 + KX1) / 2.0
    ky_c = (KY0 + KY1) / 2.0
    front = KY0  # NY face (south, toward the avenue)

    # ---- shell: steel frame + panel walls ---------------------------------
    kit.box(parent, f"{bid}_shell", (kx_c, ky_c, H / 2.0),
            (KX1 - KX0, KY1 - KY0, H), m["metal_light"], bevel=0.02)
    # kick plate / plinth
    kit.box(parent, f"{bid}_kick", (kx_c, ky_c, 0.2),
            (KX1 - KX0 + 0.06, KY1 - KY0 + 0.06, 0.4), m["conc_dark"])
    # textured panel overlay on the front and sides (weathered paint)
    kit.box(parent, f"{bid}_panel_f", (kx_c, front - 0.02, 1.55),
            (KX1 - KX0 - 0.2, 0.04, 2.0), m["paint_blue"])
    # vertical panel ribs
    rib = Batch(parent, f"{bid}_ribs", m["metal_dark"])
    n_rib = 14
    for i in range(n_rib):
        rx = KX0 + 0.1 + (KX1 - KX0 - 0.2) * i / (n_rib - 1)
        rib.box((rx, front - 0.05, 1.6), (0.045, 0.04, 2.4))
    rib.build()
    # corner posts + roof band
    for sx in (KX0, KX1):
        for sy in (front, KY1):
            kit.box(parent, f"{bid}_post{sx:.1f}{sy:.1f}", (sx, sy, H / 2.0),
                    (0.1, 0.1, H), m["metal_dark"])
    kit.box(parent, f"{bid}_roofband", (kx_c, ky_c, H - 0.18),
            (KX1 - KX0 + 0.12, KY1 - KY0 + 0.12, 0.36), m["metal_dark"])
    # roof with a slight overhang + fascia
    kit.box(parent, f"{bid}_roof", (kx_c, ky_c, H + 0.06),
            (KX1 - KX0 + 0.7, KY1 - KY0 + 0.7, 0.12), m["conc_dark"])
    kit.box(parent, f"{bid}_roofedge", (kx_c, ky_c, H + 0.15),
            (KX1 - KX0 + 0.76, KY1 - KY0 + 0.76, 0.06), m["metal_light"])

    # ---- counter window (lit cold) ----------------------------------------
    win_w, win_h = 1.95, 0.95
    wz0 = 1.3
    wz1 = wz0 + win_h
    # recessed dark reveal, then the lit interior right behind the glass line so it
    # actually reads from outside (the kiosk shell is a solid box, no wall opening)
    kit.box(parent, f"{bid}_counterdark", (kx_c, front + 0.05, (wz0 + wz1) / 2.0),
            (win_w + 0.24, 0.1, win_h + 0.16), m["void"])
    kit.box(parent, f"{bid}_counterglow", (kx_c, front - 0.055, (wz0 + wz1) / 2.0),
            (win_w - 0.1, 0.03, win_h - 0.12), m["lamp_counter"])
    # interior shelf silhouettes in front of the glow
    for i, sx in enumerate((-0.62, -0.2, 0.22, 0.66)):
        kit.box(parent, f"{bid}_shelf{i}", (kx_c + sx, front - 0.08, wz0 + 0.3),
                (0.15, 0.03, 0.46), m["metal_dark"])
    kit.box(parent, f"{bid}_seller", (kx_c + 0.05, front - 0.085, wz0 + 0.32),
            (0.5, 0.03, 0.62), m["metal_dark"])
    # frame + sill + propped-open flap
    kit.box(parent, f"{bid}_wf_top", (kx_c, front - 0.04, wz1 + 0.06),
            (win_w + 0.36, 0.1, 0.12), m["metal_dark"])
    kit.box(parent, f"{bid}_wf_l", (kx_c - win_w / 2.0 - 0.12, front - 0.04,
                                   (wz0 + wz1) / 2.0), (0.12, 0.1, win_h + 0.2),
            m["metal_dark"])
    kit.box(parent, f"{bid}_wf_r", (kx_c + win_w / 2.0 + 0.12, front - 0.04,
                                   (wz0 + wz1) / 2.0), (0.12, 0.1, win_h + 0.2),
            m["metal_dark"])
    kit.box(parent, f"{bid}_sill", (kx_c, front - 0.18, wz0 - 0.03),
            (win_w + 0.5, 0.4, 0.08), m["metal_pale"])
    kit.box(parent, f"{bid}_flap", (kx_c, front - 0.5, wz1 + 0.02),
            (win_w + 0.25, 0.9, 0.06), m["metal_light"],
            rot=(math.radians(-62), 0, 0))
    # price list board beside the counter
    kit.box(parent, f"{bid}_price", (KX1 + 0.0, front - 0.06, 1.72),
            (0.04, 0.55, 0.42), m["conc_light"])
    lines = Batch(parent, f"{bid}_pricelines", m["metal_dark"])
    for i in range(4):
        lines.box((KX1 + 0.03, front - 0.06, 1.55 + i * 0.1), (0.02, 0.42, 0.02))
    lines.build()

    # ---- sign band (on the roof fascia, clear of the canopy) ---------------
    kit.signboard(parent, f"{bid}_sign", (kx_c, front - 0.13, H - 0.19),
                  (1, 0, 0), (0, -1, 0), 3.4, 0.34, "ПРОДУКТЫ", rng,
                  bg="#28323A", fg="#E4DEC8", glow=2.4)
    kit.signboard(parent, f"{bid}_sign2", (KX1 - 0.75, front - 0.14, 2.05),
                  (1, 0, 0), (0, -1, 0), 0.9, 0.26, "КИОСК", rng,
                  bg="#3A3A34", fg="#CFC9B4", glow=0.6)

    # ---- canopy over the counter ------------------------------------------
    kit.box(parent, f"{bid}_canopy", (kx_c, front - 0.42, 2.62),
            (KX1 - KX0 + 0.2, 0.9, 0.08), m["metal_light"])
    for sx in (-1.0, 1.0):
        kit.box(parent, f"{bid}_canstr{sx}", (kx_c + sx, front - 0.72, 2.38),
                (0.06, 0.06, 0.5), m["metal_dark"], rot=(math.radians(28), 0, 0))
    kit.box(parent, f"{bid}_canlamp", (kx_c, front - 0.3, 2.56),
            (1.0, 0.16, 0.05), m["lamp_warm"])

    # ---- service hatch on the back + side vent ----------------------------
    kit.box(parent, f"{bid}_backdoor", (kx_c - 0.4, KY1 + 0.0, 1.1),
            (1.0, 0.07, 2.1), m["metal"])
    for i in range(6):
        kit.box(parent, f"{bid}_bdrib{i}", (kx_c - 0.9 + i * 0.2, KY1 + 0.04, 1.1),
                (0.05, 0.03, 2.05), m["metal_dark"])
    kit.box(parent, f"{bid}_hatch", (KX1 + 0.02, ky_c, 1.65),
            (0.05, 0.8, 0.6), m["metal_pale"])
    # AC box + vent pipe on the roof
    kit.box(parent, f"{bid}_ac", (KX0 + 0.9, KY1 - 0.9, H + 0.35),
            (0.9, 0.7, 0.5), m["metal_light"])
    kit.cyl(parent, f"{bid}_vent", (KX1 - 1.0, ky_c, H + 0.35), 0.1, 0.6,
            m["metal_dark"], verts=8)

    # ---- litter + weeds + apron ------------------------------------------
    kit.ground_quad(parent, f"{bid}_apron", (KX0 - 0.3, front - 0.9, KX1 + 0.3, front),
                    0.03, m["conc_dark"], tile=1.5)
    litter = Batch(parent, f"{bid}_litter", m["conc_light"])
    for i in range(9):
        litter.box((kx_c + rng.uniform(-2.2, 2.2), front - rng.uniform(0.1, 0.8), 0.05),
                   (rng.uniform(0.08, 0.25), rng.uniform(0.08, 0.2), 0.04),
                   rot=(0, 0, rng.uniform(0, math.tau)))
    litter.build()
    for i in range(3):
        kit.cyl(parent, f"{bid}_bottle{i}",
                (kx_c + rng.uniform(-2.0, 2.0), front - rng.uniform(0.1, 0.7), 0.12),
                0.045, 0.24, m["glass_cool"], rot=(math.radians(90), 0, 0), verts=8)
    weeds(parent, f"{bid}_weeds",
          [(KX0 + 0.2, front - 0.15), (KX1 - 0.2, front - 0.1),
           (KX1 - 0.1, KY1 - 0.2), (KX0 + 0.15, KY1 - 0.3)], rng, hmin=0.2, hmax=0.5)
    kit.trash_bin(parent, f"{bid}_bin", (KX1 + 0.5, front - 0.4, 0.0))
    # small step in front of the counter
    kit.box(parent, f"{bid}_step", (kx_c, front - 0.42, 0.06),
            (2.6, 0.6, 0.12), m["conc_dark"])
    # poster on the west side wall so the kiosk has no blank flank
    kit.poster(parent, f"{bid}_poster", (KX0 - 0.02, ky_c + 0.2, 1.55),
               (0, 1, 0), (-1, 0, 0), 0.7, 1.0, None, rng)
    kit.box(parent, f"{bid}_ventpipe", (KX0 + 0.25, KY1 - 1.2, H / 2.0),
            (0.09, 0.09, H), m["metal_dark"])

    stats["objects"] = sum(1 for o in bpy.data.objects if o.name.startswith(bid))
    return stats


# ---------------------------------------------------------------------------
TYPES = {
    "boiler": build_boiler,
    "garages": build_garages,
    "stadium_stand": build_stadium_stand,
    "gym": build_gym,
    "frame": build_frame,
    "kiosk": build_kiosk,
}
