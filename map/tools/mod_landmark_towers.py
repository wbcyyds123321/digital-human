"""mod_landmark_towers.py - Round-4 landmark rebuild (5 Stalinist / civic landmarks).

Standalone studio-modelled landmarks for the Soviet Blue-Hour microdistrict:

    TYPES = {
        "vysotka_main":     build_vysotka_main,       # MSU main building (MSU_MAIN)
        "vysotka_hotel":    build_vysotka_hotel,      # Hotel Ukraina    (UKRAINA)
        "vysotka_ministry": build_vysotka_ministry,   # Foreign Ministry (MFA)
        "institute_sino":   build_institute_sino,     # Sino-Soviet inst.(HIT)
        "museum_colonnade": build_museum_colonnade,   # Military museum  (MUSEUM)
    }

Round-4 rules
-------------
* Landmark materials come from lm_materials (LMM_*) - clean stone / stucco / granite /
  marble / bronze / copper matching the reference photos, exempt from the residential
  weathering pass.
* Geometry is rich: batched stone ornament (window surrounds, pilasters, string
  courses, dentilled cornices, balustrades, quoins, rustication), styled tier facades,
  clock turrets, tiered shafts, octagonal crowns, ribbed spires, statues, flags, crests,
  a giant colonnade entry porch and a Chinese hipped roof with upturned eaves.
* Real recessed windows on every facade (kit.facade_wall openings + stone surrounds).
* rect = OUTER footprint of the base mass; tiers are built on shrunk rects and the
  corner gaps closed with corner piers, so all outer faces land on the plan rect.
  Everything is named "<block id>_..." and parented under the block root.
"""
from __future__ import annotations

import math

import bmesh
import bpy
from mathutils import Vector

import kit
import lm_materials as lmm

THICK = 0.35
Z0 = 0.0
FACES = ("PY", "NY", "PX", "NX")

# ----------------------------------------------------------------------------
# materials
# ----------------------------------------------------------------------------
_LM = {}


def M(key):
    """Landmark material from the Round-4 LMM library (cached)."""
    return lmm.mat(key)


def _solid(name, color, rough=0.7, metallic=0.0, emission=None, strength=0.0):
    if name in _LM:
        return _LM[name]
    m = kit.mat_solid(name, color, rough=rough, metallic=metallic,
                      emission=emission, emission_strength=strength)
    _LM[name] = m
    return m


def mats():
    if _LM.get("_ready"):
        return _LM
    X = _LM
    X["star_red"] = _solid("LM4_STAR_RED", "#7E2118", rough=0.4, metallic=0.35,
                           emission="#9E2C1E", strength=0.22)
    X["star_gold"] = _solid("LM4_STAR_GOLD", "#C6A356", rough=0.3, metallic=0.85)
    X["roof_tile"] = _solid("LM4_ROOF_TILE", "#4C5055", rough=0.72)
    X["roof_tile_dk"] = _solid("LM4_ROOF_TILE_DK", "#33383C", rough=0.78)
    X["red_band"] = _solid("LM4_RED_BAND", "#7A3B33", rough=0.6)
    X["white_paint"] = _solid("LM4_WHITE_PAINT", "#DDD8CB", rough=0.8)
    X["crest"] = _solid("LM4_CREST", "#B79A5E", rough=0.38, metallic=0.7)
    X["dark_frame"] = _solid("LM4_DARK_FRAME", "#3B3A35", rough=0.6, metallic=0.2)
    X["lamp"] = _solid("LM4_LAMP", "#FFE0AE", rough=0.3,
                       emission="#FFC87E", strength=8.0)
    X["flag_red"] = _solid("LM4_FLAG_RED", "#8E2A22", rough=0.85)
    X["_ready"] = True
    return X


# ----------------------------------------------------------------------------
# batched ornament mesh (one object per material per fa莽ade 鈫?cheap + fast)
# ----------------------------------------------------------------------------
class _Batch:
    """Accumulates axis-aligned boxes and flushes them into one mesh object."""

    __slots__ = ("parent", "name", "mat", "verts", "faces")

    def __init__(self, parent, name, mat):
        self.parent = parent
        self.name = name
        self.mat = mat
        self.verts = []
        self.faces = []

    def box(self, x0, y0, z0, x1, y1, z1):
        xa, xb = (x0, x1) if x0 <= x1 else (x1, x0)
        ya, yb = (y0, y1) if y0 <= y1 else (y1, y0)
        za, zb = (z0, z1) if z0 <= z1 else (z1, z0)
        if xb - xa < 1e-5 or yb - ya < 1e-5 or zb - za < 1e-5:
            return self
        b = len(self.verts)
        self.verts += [(xa, ya, za), (xb, ya, za), (xb, yb, za), (xa, yb, za),
                       (xa, ya, zb), (xb, ya, zb), (xb, yb, zb), (xa, yb, zb)]
        for f in ((3, 2, 1, 0), (4, 5, 6, 7), (0, 1, 5, 4),
                  (1, 2, 6, 5), (2, 3, 7, 6), (3, 0, 4, 7)):
            self.faces.append(tuple(b + i for i in f))
        return self

    def ring(self, rect, z0, z1, over=0.0, t=0.25):
        x0, y0, x1, y1 = rect
        self.box(x0 - over, y1 - t, z0, x1 + over, y1 + over, z1)
        self.box(x0 - over, y0 - over, z0, x1 + over, y0 + t, z1)
        self.box(x0 - over, y0 + t, z0, x0 + t, y1 - t, z1)
        self.box(x1 - t, y0 + t, z0, x1 + over, y1 - t, z1)
        return self

    def flush(self, uv_scale=3.0):
        if not self.faces:
            return None
        me = bpy.data.meshes.new(self.name)
        me.from_pydata(self.verts, [], self.faces)
        me.update()
        layer = me.uv_layers.new(name="UVMap")
        for poly in me.polygons:
            n = poly.normal
            ax = max(range(3), key=lambda i: abs(n[i]))
            for li in poly.loop_indices:
                co = me.vertices[me.loops[li].vertex_index].co
                if ax == 0:
                    uv = (co.y / uv_scale, co.z / uv_scale)
                elif ax == 1:
                    uv = (co.x / uv_scale, co.z / uv_scale)
                else:
                    uv = (co.x / uv_scale, co.y / uv_scale)
                layer.data[li].uv = uv
        if self.mat is not None:
            me.materials.append(self.mat)
        obj = bpy.data.objects.new(self.name, me)
        coll = (self.parent.users_collection[0]
                if self.parent is not None and self.parent.users_collection
                else bpy.context.scene.collection)
        coll.objects.link(obj)
        obj.parent = self.parent
        return obj


def _link(obj, parent):
    coll = (parent.users_collection[0]
            if parent is not None and parent.users_collection
            else bpy.context.scene.collection)
    coll.objects.link(obj)
    if parent is not None:
        obj.parent = parent
    return obj


def _mesh(parent, name, verts, faces, mat):
    me = bpy.data.meshes.new(name)
    me.from_pydata(verts, [], faces)
    me.update()
    if mat is not None:
        me.materials.append(mat)
    obj = bpy.data.objects.new(name, me)
    return _link(obj, parent)


# ----------------------------------------------------------------------------
# basic helpers
# ----------------------------------------------------------------------------
def _rect(block):
    x0, y0, x1, y1 = block["rect"]
    return (float(x0), float(y0), float(x1), float(y1))


def _shrunk(rect, t=THICK):
    x0, y0, x1, y1 = rect
    return (x0 + t, y0 + t, x1 - t, y1 - t)


def _center(rect):
    x0, y0, x1, y1 = rect
    return ((x0 + x1) / 2.0, (y0 + y1) / 2.0)


def _cols(length, spacing, margin):
    out = []
    s = margin
    while s < length - margin + 1e-6:
        out.append(s)
        s += spacing
    return out


def _rows(z0, floors, fh, win_h, ground_bottom):
    out = []
    for f in range(floors):
        if f == 0:
            zb = z0 + ground_bottom
        else:
            zb = z0 + f * fh + (fh - win_h) * 0.55
        out.append((zb, zb + win_h))
    return out


def _fbox_b(B, P0, u, n, s0, s1, o0, o1, za, zb):
    """Box in a fa莽ade frame, appended to a batch."""
    c = (P0 + u * ((s0 + s1) / 2.0) + n * ((o0 + o1) / 2.0))
    half_u = abs(s1 - s0) / 2.0
    half_n = abs(o1 - o0) / 2.0
    if abs(u.x) > 0.5:
        B.box(c.x - half_u, c.y - half_n, za, c.x + half_u, c.y + half_n, zb)
    else:
        B.box(c.x - half_n, c.y - half_u, za, c.x + half_n, c.y + half_u, zb)
    return B


def _fbox(parent, name, P0, u, n, s0, s1, o0, o1, za, zb, mat, bevel=0.0):
    if abs(s1 - s0) < 1e-4 or abs(o1 - o0) < 1e-4 or abs(zb - za) < 1e-4:
        return None
    c = P0 + u * ((s0 + s1) / 2.0) + n * ((o0 + o1) / 2.0)
    if abs(u.x) > 0.5:
        size = (abs(s1 - s0), abs(o1 - o0), abs(zb - za))
    else:
        size = (abs(o1 - o0), abs(s1 - s0), abs(zb - za))
    c = Vector((c.x, c.y, (za + zb) / 2.0))
    return kit.box(parent, name, tuple(c), size, mat, bevel=bevel)


def _box_mm(parent, name, x0, y0, z0, x1, y1, z1, mat):
    return kit.box_mm(parent, name, x0, y0, z0, x1, y1, z1, mat)


def _face_parts(rect, face):
    sh = _shrunk(rect)
    P0, u, L, n = kit.wall_frame(sh, face)
    return P0, u, L, n, sh


# ----------------------------------------------------------------------------
# stone ornament 鈥?cornices, balustrades, string courses, quoins, rustication
# ----------------------------------------------------------------------------
def _cornice(parent, name, rect, z, mat_a=None, mat_b=None, over=0.55, h=0.72,
             dentils=True, dentil_mat=None):
    """Multi-layer dentilled cornice ring (batched into 2 objects)."""
    mat_a = mat_a or M("limestone_light")
    mat_b = mat_b or M("limestone")
    A = _Batch(parent, name + "_light", mat_a)
    B = _Batch(parent, name + "_stone", mat_b)
    x0, y0, x1, y1 = rect
    A.ring(rect, z, z + h * 0.30, over=0.10, t=0.16)
    if dentils:
        step = 0.52
        dz0, dz1 = z + h * 0.30, z + h * 0.46
        nx = max(1, int((x1 - x0 + 2 * over) / step))
        for i in range(nx + 1):
            px = x0 - over + (x1 - x0 + 2 * over) * i / nx
            A.box(px - 0.08, y1 - 0.03, dz0, px + 0.08, y1 + over * 0.55, dz1)
            A.box(px - 0.08, y0 - over * 0.55, dz0, px + 0.08, y0 + 0.03, dz1)
        ny = max(1, int((y1 - y0) / step))
        for i in range(ny + 1):
            py = y0 + (y1 - y0) * i / ny
            A.box(x1 - 0.03, py - 0.08, dz0, x1 + over * 0.55, py + 0.08, dz1)
            A.box(x0 - over * 0.55, py - 0.08, dz0, x0 + 0.03, py + 0.08, dz1)
    B.ring(rect, z + h * 0.46, z + h * 0.82, over=over, t=0.30)
    A.ring(rect, z + h * 0.82, z + h, over=over - 0.07, t=0.28)
    A.flush()
    B.flush()


def _balustrade(parent, name, rect, z, h=1.05, mat=None, pier_step=3.0,
                over=0.05):
    """Stone balustrade ring: rails + balusters + piers (batched)."""
    mat = mat or M("limestone_light")
    B = _Batch(parent, name, mat)
    x0, y0, x1, y1 = rect
    B.ring(rect, z, z + 0.16, over=over, t=0.26)
    B.ring(rect, z + h - 0.16, z + h, over=over + 0.04, t=0.26)
    bh = h - 0.32
    step = 0.46
    nx = max(1, int((x1 - x0) / step))
    for i in range(nx + 1):
        px = x0 + (x1 - x0) * i / nx
        B.box(px - 0.055, y1 - 0.11 + over, z + 0.16, px + 0.055,
              y1 - 0.02 + over, z + 0.16 + bh)
        B.box(px - 0.055, y0 + 0.02 - over, z + 0.16, px + 0.055,
              y0 + 0.11 - over, z + 0.16 + bh)
    ny = max(1, int((y1 - y0) / step))
    for i in range(ny + 1):
        py = y0 + (y1 - y0) * i / ny
        B.box(x1 - 0.11 + over, py - 0.055, z + 0.16, x1 - 0.02 + over,
              py + 0.055, z + 0.16 + bh)
        B.box(x0 + 0.02 - over, py - 0.055, z + 0.16, x0 + 0.11 - over,
              py + 0.055, z + 0.16 + bh)
    npx = max(1, int((x1 - x0) / pier_step))
    for i in range(npx + 1):
        px = x0 + (x1 - x0) * i / npx
        for py in (y0, y1):
            B.box(px - 0.18, py - 0.18, z, px + 0.18, py + 0.18, z + h + 0.12)
    npy = max(1, int((y1 - y0) / pier_step))
    for i in range(npy + 1):
        py = y0 + (y1 - y0) * i / npy
        for px in (x0, x1):
            B.box(px - 0.18, py - 0.18, z, px + 0.18, py + 0.18, z + h + 0.12)
    B.flush()


def _string_course(parent, name, rect, z, mat=None, h=0.24, over=0.10):
    B = _Batch(parent, name, mat or M("limestone_light"))
    B.ring(rect, z, z + h, over=over, t=0.14)
    B.flush()


def _rustication(parent, name, rect, z0, z1, mat=None, course=0.62,
                 over=0.07, joint_mat=None):
    """Rusticated stone base courses: alternating proud bands + joint lines."""
    mat = mat or M("stone_rusticated")
    joint_mat = joint_mat or M("granite_dark")
    B = _Batch(parent, name, mat)
    J = _Batch(parent, name + "_joints", joint_mat)
    n = max(1, int((z1 - z0) / course))
    for i in range(n):
        za = z0 + (z1 - z0) * i / n
        zb = za + (z1 - z0) / n - 0.05
        ov = over if i % 2 == 0 else over * 0.55
        B.ring(rect, za, zb, over=ov, t=0.10)
        J.ring(rect, zb, zb + 0.05, over=ov * 0.5, t=0.09)
    B.flush()
    J.flush()


def _quoins(parent, name, rect, z0, z1, mat=None, step=1.7, proud=0.09):
    B = _Batch(parent, name, mat or M("limestone_light"))
    x0, y0, x1, y1 = rect
    corners = ((x0, y0, 1, 1), (x1, y0, -1, 1), (x1, y1, -1, -1),
               (x0, y1, 1, -1))
    n = max(1, int((z1 - z0) / step))
    for ci, (cx, cy, dx, dy) in enumerate(corners):
        for i in range(n):
            a = (1.0 if i % 2 == 0 else 0.68) + proud
            za = z0 + i * (z1 - z0) / n
            zb = za + (z1 - z0) / n - 0.04
            bx0, bx1 = sorted((cx, cx + dx * a))
            by0, by1 = sorted((cy, cy + dy * a))
            B.box(bx0, by0, za, bx1, by1, zb)
    B.flush()


def _window_surrounds(parent, name, P0, u, n, L, cols, rows, win_w, win_h,
                      mat=None, keystone=True, hood=0.0, j=0.17):
    """Stone window surrounds (jambs, lintel, sill, keystone, optional hood)."""
    B = _Batch(parent, name, mat or M("limestone_light"))
    o = THICK + 0.055
    o2 = THICK + 0.14
    for (zb, zt) in rows:
        for s in cols:
            s0, s1 = s - win_w / 2.0, s + win_w / 2.0
            _fbox_b(B, P0, u, n, s0 - j, s0, o - 0.01, o, zb - 0.06, zt + 0.14)
            _fbox_b(B, P0, u, n, s1, s1 + j, o - 0.01, o, zb - 0.06, zt + 0.14)
            _fbox_b(B, P0, u, n, s0 - j - 0.04, s1 + j + 0.04, o - 0.01, o2,
                    zt, zt + 0.24)
            if keystone:
                mid = (s0 + s1) / 2.0
                _fbox_b(B, P0, u, n, mid - 0.13, mid + 0.13, o, o2 + 0.05,
                        zt - 0.02, zt + 0.34)
            _fbox_b(B, P0, u, n, s0 - 0.14, s1 + 0.14, o - 0.01, o2, zb - 0.16,
                    zb)
            _fbox_b(B, P0, u, n, s0 - 0.10, s1 + 0.10, o2, o2 + 0.07,
                    zb - 0.24, zb - 0.14)
            if hood:
                _fbox_b(B, P0, u, n, s0 - 0.10, s1 + 0.10, o, o2 + 0.02,
                        zt + 0.28, zt + 0.28 + hood)
    B.flush()


def _pilasters(parent, name, P0, u, n, L, cols, z0, z1, w=0.5, mat=None,
               cap_mat=None, out=0.16):
    """Giant-order pilaster strips midway between window columns."""
    B = _Batch(parent, name, mat or M("limestone_light"))
    C = _Batch(parent, name + "_caps", cap_mat or M("limestone"))
    mids = [(cols[i] + cols[i + 1]) / 2.0 for i in range(len(cols) - 1)]
    if not mids:
        return
    for i, s in enumerate(mids):
        _fbox_b(B, P0, u, n, s - w / 2.0, s + w / 2.0, THICK - 0.02,
                THICK + out, z0 + 0.15, z1 - 0.55)
        _fbox_b(C, P0, u, n, s - w / 2.0 - 0.07, s + w / 2.0 + 0.07,
                THICK - 0.02, THICK + out + 0.06, z0, z0 + 0.22)
        _fbox_b(C, P0, u, n, s - w / 2.0 - 0.07, s + w / 2.0 + 0.07,
                THICK - 0.02, THICK + out + 0.06, z1 - 0.55, z1 - 0.35)
    B.flush()
    C.flush()


# ----------------------------------------------------------------------------
# window restyle (kit windows 鈫?clean landmark glass / painted frames)
# ----------------------------------------------------------------------------
def _restyle_windows(parent, bid, tag, face, rng, lit=0.05, frame=None,
                     sill=None, dark=None):
    pre = f"{bid}_{tag}_{face}_win"
    wm = kit.win_materials()
    frame = frame or M("stucco_white")
    sill = sill or M("limestone_light")
    dark = dark or M("glass_clean")
    for ob in parent.children:
        if not ob.name.startswith(pre) or ob.type != "MESH":
            continue
        if ob.name.endswith("_glass"):
            m = wm[rng.pick(["warm_a", "warm_b", "dim"])] if rng.random() < lit \
                else dark
            ob.data.materials.clear()
            ob.data.materials.append(m)
        elif ob.name.endswith("_frame"):
            ob.data.materials.clear()
            ob.data.materials.append(frame)
        elif ob.name.endswith("_sill"):
            ob.data.materials.clear()
            ob.data.materials.append(sill)


# ----------------------------------------------------------------------------
# styled fa莽ade: kit openings + stone ornament
# ----------------------------------------------------------------------------
def _facade(parent, bid, tag, rect, face, z0, z1, wall_mat, rng, *,
            floors, fh, win_w, win_h, spacing, margin, lit=0.05,
            plinth_mat=None, plinth_h=0.0, ground_bottom=None,
            surr=True, surr_mat=None, pilaster_w=0.0, pilaster_mat=None,
            strings=0, string_mat=None, keystone=True, hood=0.0,
            frame=None, sill=None, dark=None):
    if ground_bottom is None:
        ground_bottom = max(0.55, (fh - win_h) * 0.55)
    P0, u, L, n, sh = _face_parts(rect, face)
    wm = kit.win_materials()
    st = kit.facade_wall(
        parent, f"{bid}_{tag}_{face}", sh, face, z0, z1, wall_mat, rng,
        floors=floors, floor_h=fh, win_w=win_w, win_h=win_h, spacing=spacing,
        wmats=wm, margin=margin, lit_ratio=lit, thickness=THICK,
        plinth_h=plinth_h, plinth_mat=plinth_mat,
        ground_win_bottom=ground_bottom, joints=False, balcony_cols=())
    _restyle_windows(parent, bid, tag, face, rng, lit=lit, frame=frame,
                     sill=sill, dark=dark)
    cols = _cols(L, spacing, margin)
    rows = _rows(z0, floors, fh, win_h, ground_bottom)
    if surr:
        _window_surrounds(parent, f"{bid}_{tag}_{face}_surr", P0, u, n, L,
                          cols, rows, win_w, win_h, surr_mat,
                          keystone=keystone, hood=hood)
    if pilaster_w:
        _pilasters(parent, f"{bid}_{tag}_{face}_pil", P0, u, n, L, cols,
                   z0, z1, pilaster_w, pilaster_mat)
    for i in range(strings):
        zz = z0 + (z1 - z0) * (i + 1) / (strings + 1)
        _string_course(parent, f"{bid}_{tag}_{face}_str{i}", rect, zz,
                       string_mat)
    return st


# ----------------------------------------------------------------------------
# mass helpers: tier, corner piers, parapets, roof
# ----------------------------------------------------------------------------
def _corner_piers(parent, name, rect, z0, z1, mat, proud=0.08, size=0.7):
    B = _Batch(parent, name, mat)
    x0, y0, x1, y1 = rect
    k = size / 2.0
    for cx, cy in ((x0 + k, y0 + k), (x1 - k, y0 + k), (x1 - k, y1 - k),
                   (x0 + k, y1 - k)):
        B.box(cx - k - proud, cy - k - proud, z0, cx + k + proud,
              cy + k + proud, z1)
    B.flush()


def _tier(parent, bid, tag, rect, z0, floors, fh, wall_mat, rng, *,
          win_w, win_h, spacing, margin, lit=0.05, plinth_mat=None,
          plinth_h=0.0, ground_bottom=None, surr=True, surr_mat=None,
          pilaster_w=0.0, pilaster_mat=None, strings=0, string_mat=None,
          keystone=True, hood=0.0, cornice_h=0.0, cornice_mat=None,
          cornice_mat_b=None, cornice_over=0.55, dentils=True,
          parapet=0.0, parapet_mat=None):
    """One stepped tier: 4 styled fa莽ades + corner piers + cornice + balustrade."""
    stats = {"windows": 0, "lit": 0}
    z1 = z0 + floors * fh
    for face in FACES:
        st = _facade(parent, bid, tag, rect, face, z0, z1, wall_mat, rng,
                     floors=floors, fh=fh, win_w=win_w, win_h=win_h,
                     spacing=spacing, margin=margin, lit=lit,
                     plinth_mat=plinth_mat, plinth_h=plinth_h,
                     ground_bottom=ground_bottom, surr=surr,
                     surr_mat=surr_mat, pilaster_w=pilaster_w,
                     pilaster_mat=pilaster_mat, strings=strings,
                     string_mat=string_mat, keystone=keystone, hood=hood)
        stats["windows"] += st["windows"]
        stats["lit"] += st["lit"]
    _corner_piers(parent, f"{bid}_{tag}_corner", rect, z0, z1,
                  pilaster_mat or wall_mat, 0.07, 0.66)
    top = z1
    if cornice_h > 0:
        _cornice(parent, f"{bid}_{tag}_cornice", rect, z1, cornice_mat,
                 cornice_mat_b, cornice_over, cornice_h, dentils)
        top = z1 + cornice_h
    if parapet > 0:
        _balustrade(parent, f"{bid}_{tag}_bal", rect, top, parapet,
                    mat=parapet_mat)
    return top, stats


def _parapet_ring(parent, name, rect, z, h=0.55, t=0.24, mat=None):
    B = _Batch(parent, name, mat or M("limestone_light"))
    B.ring(rect, z, z + h, over=0.0, t=t)
    B.flush()


def _roof_flat(parent, name, rect, z, mat=None):
    x0, y0, x1, y1 = rect
    return kit.box_mm(parent, name, x0, y0, z - 0.20, x1, y1, z,
                      mat or M("copper_roof"))


# ----------------------------------------------------------------------------
# steeple parts: cones, spires, stars, clocks, pinnacles, statues, flags
# ----------------------------------------------------------------------------
def _cone(parent, name, center, r0, r1, depth, mat, seg=8, rot=(0.0, 0.0, 0.0)):
    bm = bmesh.new()
    bmesh.ops.create_cone(bm, cap_ends=True, cap_tris=False, segments=seg,
                          radius1=r0, radius2=r1, depth=depth)
    me = bpy.data.meshes.new(name)
    bm.to_mesh(me)
    bm.free()
    if mat is not None:
        me.materials.append(mat)
    obj = bpy.data.objects.new(name, me)
    _link(obj, parent)
    obj.location = center
    obj.rotation_euler = rot
    return obj


def _spire(parent, name, cx, cy, z0, z1, r0, r1, mat=None, ring_mat=None,
           seg=8, rings=(0.10, 0.28, 0.50, 0.70, 0.88), ball=True,
           ball_r=None):
    """Tapered ribbed spire with collar rings + gilded ball; returns ball top z."""
    mat = mat or M("iron_dark")
    ring_mat = ring_mat or M("gold")
    h = z1 - z0
    _cone(parent, name + "_shaft", (cx, cy, (z0 + z1) / 2.0), r0, r1, h, mat,
          seg=seg)
    for i, frac in enumerate(rings):
        rr = r0 + (r1 - r0) * frac
        kit.cyl(parent, f"{name}_ring{i}", (cx, cy, z0 + h * frac), rr * 1.06,
                0.14, ring_mat, verts=seg)
    if ball:
        br = ball_r if ball_r else max(0.26, r1 * 1.9)
        kit.sphere(parent, name + "_ball", (cx, cy, z1 + br * 0.75), br,
                   ring_mat, segments=10, rings=6)
        return z1 + br * 1.5
    return z1


def _star(parent, name, cx, cy, cz, size, depth=0.4, mat=None, gold=False):
    """Five-pointed star, crossed double-sided so it reads from every angle."""
    X = mats()
    mat = mat or (X["star_gold"] if gold else X["star_red"])
    r_out = size / 2.0
    r_in = r_out * 0.42
    pts = []
    for k in range(10):
        ang = math.pi / 2.0 + k * math.pi / 5.0
        r = r_out if k % 2 == 0 else r_in
        pts.append((math.cos(ang) * r, math.sin(ang) * r))
    for pi, (ax_a, ax_b, ax_d) in enumerate(((0, 2, 1), (1, 2, 0))):
        verts = []
        for d in (-depth / 2.0, depth / 2.0):
            for (a, b) in pts:
                v = [0.0, 0.0, 0.0]
                v[ax_a] = a
                v[ax_b] = b
                v[ax_d] = d
                verts.append((cx + v[0], cy + v[1], cz + v[2]))
        faces = [tuple(range(9, -1, -1)), tuple(range(10, 20))]
        for i in range(10):
            j = (i + 1) % 10
            faces.append((i, j, 10 + j, 10 + i))
        _mesh(parent, f"{name}_{pi}", verts, faces, mat)


def _clock(parent, name, center, radius, n, rim_mat=None, face_mat=None):
    rim = rim_mat or M("gold")
    face = face_mat or M("granite_dark")
    c = Vector(center)
    up = Vector((0.0, 0.0, 1.0))
    nn = Vector(n).normalized()
    side = up.cross(nn).normalized()
    if abs(nn.x) > 0.5:
        rot = (0.0, math.radians(90 if nn.x > 0 else -90), 0.0)
    else:
        rot = (math.radians(90 if nn.y > 0 else -90), 0.0, 0.0)
    kit.cyl(parent, name + "_rim", tuple(c + nn * 0.05), radius, 0.16, rim,
            rot=rot, verts=18)
    kit.cyl(parent, name + "_face", tuple(c + nn * 0.09), radius * 0.84, 0.16,
            face, rot=rot, verts=18)
    for ln, ang in ((0.58, 1.15), (0.82, -0.45)):
        d = (side * math.sin(ang) + up * math.cos(ang)) * radius * ln
        mid = c + nn * 0.18 + d * 0.5
        kit.box(parent, name + f"_h{ln}", tuple(mid),
                (abs(d.x) + 0.035, abs(d.y) + 0.035, abs(d.z) + 0.035), rim)


def _pinnacle(parent, name, x, y, z, w=1.0, h=2.4, mat=None, tip_mat=None):
    """Gothic obelisk pinnacle: shaft + tapered cap + tip."""
    mat = mat or M("limestone_light")
    tip_mat = tip_mat or M("iron_dark")
    kit.box(parent, name + "_b", (x, y, z + h * 0.28), (w, w, h * 0.56), mat)
    _cone(parent, name + "_c", (x, y, z + h * 0.56 + h * 0.30), w * 0.60, 0.06,
          h * 0.60, mat, seg=4)
    _cone(parent, name + "_tip", (x, y, z + h + 0.28), 0.09, 0.02, 0.55, tip_mat,
          seg=6)


def _statue(parent, name, x, y, z, h=2.6, ped_h=1.1, mat=None, ped_mat=None):
    """Simplified monumental figure: pedestal + robe mass + shoulders + head."""
    mat = mat or M("limestone")
    ped_mat = ped_mat or M("granite_grey")
    kit.box(parent, name + "_ped", (x, y, z + ped_h / 2.0),
            (0.95, 0.95, ped_h), ped_mat)
    kit.box(parent, name + "_pedcap", (x, y, z + ped_h + 0.06),
            (1.08, 1.08, 0.12), ped_mat)
    bz = z + ped_h + 0.12
    _cone(parent, name + "_robe", (x, y, bz + h * 0.32), 0.42, 0.24,
          h * 0.64, mat, seg=8)
    kit.box(parent, name + "_sh", (x, y, bz + h * 0.70), (0.52, 0.42, h * 0.20),
            mat)
    kit.sphere(parent, name + "_head", (x, y, bz + h * 0.88), h * 0.09, mat,
               segments=8, rings=6)


def _flag(parent, name, x, y, z, mast_h=7.0, w=2.4, h=1.4, yaw=0.0,
          cloth_mat=None):
    kit.cyl(parent, name + "_mast", (x, y, z + mast_h / 2.0), 0.045, mast_h,
            M("bronze"), verts=6)
    c = (x + math.cos(yaw) * w / 2.0, y + math.sin(yaw) * w / 2.0,
         z + mast_h - h / 2.0 - 0.08)
    along_x = abs(math.cos(yaw)) >= 0.5
    size = (w, 0.05, h) if along_x else (0.05, w, h)
    kit.box(parent, name + "_cloth", c, size, cloth_mat or mats()["flag_red"])
    kit.box(parent, name + "_fin", (x, y, z + mast_h + 0.06), (0.1, 0.1, 0.16),
            M("gold"))


# ----------------------------------------------------------------------------
# classical parts: columns, entablature, pediment, colonnade, portico
# ----------------------------------------------------------------------------
def _column(parent, name, x, y, z0, h, r=0.42, col_mat=None, cap_mat=None,
            square=False, shaft_seg=12):
    col_mat = col_mat or M("limestone_light")
    cap_mat = cap_mat or M("limestone")
    if square:
        kit.box(parent, name + "_base", (x, y, z0 + 0.18),
                (r * 2.5, r * 2.5, 0.36), cap_mat)
        kit.box(parent, name + "_shaft", (x, y, z0 + h / 2.0),
                (r * 1.7, r * 1.7, h), col_mat)
        kit.box(parent, name + "_cap", (x, y, z0 + h - 0.10),
                (r * 2.3, r * 2.3, 0.24), cap_mat)
    else:
        kit.cyl(parent, name + "_base", (x, y, z0 + 0.16), r * 1.30, 0.32,
                cap_mat, verts=shaft_seg)
        kit.cyl(parent, name + "_shaft", (x, y, z0 + h / 2.0), r, h, col_mat,
                verts=shaft_seg)
        kit.cyl(parent, name + "_cap", (x, y, z0 + h - 0.12), r * 1.34, 0.26,
                cap_mat, verts=shaft_seg)
        kit.box(parent, name + "_abacus", (x, y, z0 + h + 0.02),
                (r * 2.9, r * 2.9, 0.16), cap_mat)


def _entablature(parent, name, P0, u, n, s0, s1, o0, o1, z0, z1, mat=None,
                 trim_mat=None, dentils=True):
    mat = mat or M("limestone")
    trim = trim_mat or M("limestone_light")
    B = _Batch(parent, name, mat)
    T = _Batch(parent, name + "_trim", trim)
    _fbox_b(B, P0, u, n, s0, s1, o0, o1, z0, z0 + (z1 - z0) * 0.30)
    _fbox_b(T, P0, u, n, s0 - 0.05, s1 + 0.05, o0, o1 + 0.05,
            z0 + (z1 - z0) * 0.30, z0 + (z1 - z0) * 0.55)
    if dentils:
        h0 = z0 + (z1 - z0) * 0.55
        cnt = max(2, int((s1 - s0) / 0.55))
        for i in range(cnt + 1):
            ss = s0 + (s1 - s0) * i / cnt
            _fbox_b(T, P0, u, n, ss - 0.08, ss + 0.08, o0, o1 + 0.10, h0,
                    h0 + (z1 - z0) * 0.18)
    _fbox_b(T, P0, u, n, s0 - 0.10, s1 + 0.10, o0, o1 + 0.22,
            z0 + (z1 - z0) * 0.73, z1)
    B.flush()
    T.flush()


def _raking_bar(parent, name, P0, u, n, o0, o1, sa, za, sb, zb, t=0.30,
                mat=None):
    """Sloped cornice bar from (sa, za) to (sb, zb) in a facade frame."""
    A = P0 + u * sa + n * o0
    B = P0 + u * sb + n * o0
    dv = n * (o1 - o0)
    verts = []
    for (P, z) in ((A, za), (B, zb)):
        for dz in (-t, 0.0):
            for dd in (Vector((0, 0, 0)), dv):
                verts.append((P.x + dd.x, P.y + dd.y, z + dz))
    faces = [(0, 1, 3, 2), (4, 6, 7, 5), (0, 4, 5, 1), (2, 3, 7, 6),
             (0, 2, 6, 4), (1, 5, 7, 3)]
    _mesh(parent, name, verts, faces, mat or M("limestone_light"))


def _pediment(parent, name, P0, u, n, s_c, width, z_base, height, depth,
              mat=None, trim_mat=None, relief_mat=None):
    """Triangular pediment (tympanum + raking cornice) in a fa莽ade frame."""
    mat = mat or M("limestone")
    trim = trim_mat or M("limestone_light")
    s0, s1 = s_c - width / 2.0, s_c + width / 2.0
    o0 = THICK - 0.05
    o1 = THICK + depth
    ha = abs(u.x) > 0.5
    p0 = P0 + u * s0 + n * o0
    if ha:
        wv = [(p0.x, p0.y, z_base), (p0.x + (s1 - s0), p0.y, z_base),
              (p0.x + (s1 - s0) / 2, p0.y, z_base + height),
              (p0.x, p0.y + (o1 - o0), z_base),
              (p0.x + (s1 - s0), p0.y + (o1 - o0), z_base),
              (p0.x + (s1 - s0) / 2, p0.y + (o1 - o0), z_base + height)]
        f = [(0, 1, 2), (5, 4, 3), (0, 3, 4, 1), (1, 4, 5, 2), (2, 5, 3, 0)]
    else:
        wv = [(p0.x, p0.y, z_base), (p0.x, p0.y + (s1 - s0), z_base),
              (p0.x, p0.y + (s1 - s0) / 2, z_base + height),
              (p0.x + (o1 - o0), p0.y, z_base),
              (p0.x + (o1 - o0), p0.y + (s1 - s0), z_base),
              (p0.x + (o1 - o0), p0.y + (s1 - s0) / 2, z_base + height)]
        f = [(1, 0, 2), (3, 4, 5), (4, 3, 0, 1), (5, 4, 1, 2), (3, 5, 2, 0)]
    _mesh(parent, name + "_tymp", wv, f, mat)
    # sloped raking cornice: two bars following the roof pitch + horizontal base
    _raking_bar(parent, name + "_rake_l", P0, u, n, o0 - 0.02, o1 + 0.10,
                s0 - 0.10, z_base - 0.06, s_c, z_base + height, 0.34, trim)
    _raking_bar(parent, name + "_rake_r", P0, u, n, o0 - 0.02, o1 + 0.10,
                s_c, z_base + height, s1 + 0.10, z_base - 0.06, 0.34, trim)
    apex = P0 + u * s_c + n * (o0 + (o1 - o0) / 2.0)
    kit.box(parent, name + "_apex", (apex.x, apex.y, z_base + height + 0.10),
            (0.9, 0.9, 0.34), trim)
    BASE = _Batch(parent, name + "_base", trim)
    _fbox_b(BASE, P0, u, n, s0 - 0.22, s1 + 0.22, o0, o1 + 0.16,
            z_base - 0.24, z_base)
    BASE.flush()
    R = _Batch(parent, name + "_rel", relief_mat or M("relief_stone"))
    mid = s_c
    _fbox_b(R, P0, u, n, mid - width * 0.14, mid + width * 0.14, o1 - 0.02,
            o1 + 0.05, z_base + 0.20, z_base + height * 0.55)
    R.flush()


def _colonnade(parent, name, P0, u, n, s0, s1, count, z0, col_h, col_r=0.42,
               col_mat=None, cap_mat=None, square=False, out=0.55):
    col_mat = col_mat or M("limestone_light")
    cap_mat = cap_mat or M("limestone")
    base = P0 + n * (THICK + out)
    for i in range(count):
        ss = ((s0 + s1) / 2.0 if count == 1
              else s0 + (s1 - s0) * i / float(count - 1))
        p = base + u * ss
        _column(parent, f"{name}_col{i}", p.x, p.y, z0, col_h, col_r, col_mat,
                cap_mat, square=square)
    _fbox(parent, name + "_styl", P0, u, n, s0 - 0.5, s1 + 0.5,
          THICK + 0.02, THICK + out + col_r * 1.4, 0.0, z0 + 0.12,
          M("granite_grey"))


def _steps(parent, name, P0, u, n, s0, s1, z_top, n_steps=4, depth=0.38,
           mat=None):
    mat = mat or M("granite_grey")
    depth = min(depth, 0.96 / max(1, n_steps))     # keep protrusion <= ~1.15 m
    for i in range(n_steps):
        # i = 0 is the outermost (lowest) tread, i = n-1 is the top tread
        o0 = THICK + 0.16 + (n_steps - 1 - i) * depth
        o1 = o0 + depth + 0.03
        zt = z_top * float(i + 1) / float(n_steps)
        _fbox(parent, f"{name}_{i}", P0, u, n, s0, s1, o0, o1, 0.0,
              max(0.14, zt), mat)


def _chinese_roof(parent, name, cx, cy, z0, w, d, h, mat=None, trim_mat=None,
                  upturn=0.45):
    """Hipped Chinese roof: upturned eave corners + concave-tiered tile roof."""
    mat = mat or mats()["roof_tile"]
    trim = trim_mat or M("iron_dark")
    ov = 0.85
    # eaves: thin dark fascia ring + upturned corner tips
    kit.box(parent, name + "_eave", (cx, cy, z0 + 0.12),
            (w + 2 * ov, d + 2 * ov, 0.24), trim)
    hw, hd = (w + 2 * ov) / 2.0, (d + 2 * ov) / 2.0
    for sx in (-1, 1):
        for sy in (-1, 1):
            px, py = cx + sx * hw, cy + sy * hd
            kit.box(parent, f"{name}_tip{sx}{sy}",
                    (px + sx * 0.22, py + sy * 0.22, z0 + 0.30),
                    (0.75, 0.75, 0.16), trim,
                    rot=(sy * upturn * 0.9, -sx * upturn * 0.9, 0.0))
    # tile surface: concave stack of quadrilateral rings (steep near the ridge)
    z = z0 + 0.24
    sizes = [w + 2 * ov, d + 2 * ov]
    steps = 4
    for i in range(steps):
        t = (i + 1) / float(steps)
        frac = 1.0 - 0.94 * (t ** 0.72)
        top_w = (w + 2 * ov) * max(0.06, frac)
        top_d = (d + 2 * ov) * max(0.06, frac)
        dh = h * (0.42 - 0.26 * t)
        _frustum(parent, f"{name}_ring{i}", cx, cy, sizes[0], sizes[1], top_w,
                 top_d, z, z + dh, mat)
        sizes = [top_w, top_d]
        z += dh
    # ridge + finial
    kit.box(parent, name + "_ridge", (cx, cy, z + 0.08),
            (max(0.5, sizes[0] * 0.9), max(0.5, sizes[1] * 0.9), 0.16), trim)
    kit.sphere(parent, name + "_ball", (cx, cy, z + 0.36), 0.26, M("gold"),
               segments=10, rings=6)
    kit.cyl(parent, name + "_mast", (cx, cy, z + 1.05), 0.04, 1.3,
            M("iron_dark"), verts=6)
    return z + 1.7


def _frustum(parent, name, cx, cy, sx0, sy0, sx1, sy1, z0, z1, mat):
    a0x, a0y = sx0 / 2.0, sy0 / 2.0
    a1x, a1y = sx1 / 2.0, sy1 / 2.0
    verts = [(cx - a0x, cy - a0y, z0), (cx + a0x, cy - a0y, z0),
             (cx + a0x, cy + a0y, z0), (cx - a0x, cy + a0y, z0),
             (cx - a1x, cy - a1y, z1), (cx + a1x, cy - a1y, z1),
             (cx + a1x, cy + a1y, z1), (cx - a1x, cy + a1y, z1)]
    faces = [(3, 2, 1, 0), (4, 5, 6, 7), (0, 1, 5, 4), (1, 2, 6, 5),
             (2, 3, 7, 6), (3, 0, 4, 7)]
    return _mesh(parent, name, verts, faces, mat)


def _oct_drum(parent, name, cx, cy, z0, z1, radius, mat=None, trim=None,
              pinnacles=8, pin_mat=None):
    """Octagonal drum with collars and a ring of mini pinnacles."""
    mat = mat or M("limestone_light")
    trim = trim or M("limestone")
    pin_mat = pin_mat or M("limestone_light")
    _cone(parent, name + "_drum", (cx, cy, (z0 + z1) / 2.0), radius, radius,
          z1 - z0, mat, seg=8)
    kit.cyl(parent, name + "_top", (cx, cy, z1 - 0.10), radius * 1.10, 0.22,
            trim, verts=8)
    kit.cyl(parent, name + "_base", (cx, cy, z0 + 0.12), radius * 1.12, 0.24,
            trim, verts=8)
    for i in range(pinnacles):
        a = 2.0 * math.pi * i / pinnacles + math.pi / pinnacles
        px = cx + math.cos(a) * radius * 0.92
        py = cy + math.sin(a) * radius * 0.92
        kit.box(parent, f"{name}_pin{i}_b", (px, py, z0 + 0.55),
                (0.36, 0.36, 0.75), pin_mat)
        _cone(parent, f"{name}_pin{i}_c", (px, py, z0 + 1.50), 0.26, 0.05,
              1.1, pin_mat, seg=4)


def _vent(parent, name, x, y, z, mat=None):
    kit.box(parent, name + "_b", (x, y, z + 0.35), (0.95, 0.8, 0.7),
            mat or M("limestone"))
    kit.box(parent, name + "_cap", (x, y, z + 0.74), (1.1, 0.95, 0.1),
            M("copper_roof"))


def _roof_service(parent, bid, tag, rect, z, rng, vents=3, statues=0, masts=0,
                  mat=None):
    x0, y0, x1, y1 = rect
    w, d = x1 - x0, y1 - y0
    for i in range(vents):
        fx = 0.18 + 0.64 * (i + 0.5) / max(1, vents)
        _vent(parent, f"{bid}_{tag}_vent{i}", x0 + w * fx, y0 + d * 0.22, z,
              mat)
    for i in range(statues):
        fx = 0.16 + 0.68 * (i + 0.5) / max(1, statues)
        _statue(parent, f"{bid}_{tag}_stat{i}", x0 + w * fx, y0 + d * 0.16,
                z + 0.1, h=2.2, ped_h=1.3, mat=M("limestone_light"))
    for i in range(masts):
        fx = 0.22 + 0.56 * (i + 0.5) / max(1, masts)
        kit.cyl(parent, f"{bid}_{tag}_mast{i}", (x0 + w * fx, y1 - d * 0.18,
                                                z + 2.1), 0.05, 4.2,
                M("iron_dark"), verts=6)


def _count(parent):
    n = 0
    t = 0
    for c in parent.children:
        n += 1
        if c.type == "MESH":
            t += len(c.data.polygons)
        cn, ct = _count(c)
        n += cn
        t += ct
    return n, t


def _emblem(parent, name, center, radius, n, ring_mat=None, face_mat=None):
    """Round gilt emblem on a facade: bronze ring + dark disc + gold arc bars."""
    c = Vector(center)
    nn = Vector(n).normalized()
    if abs(nn.x) > 0.5:
        rot = (0.0, math.radians(90 if nn.x > 0 else -90), 0.0)
    else:
        rot = (math.radians(90 if nn.y > 0 else -90), 0.0, 0.0)
    kit.cyl(parent, name + "_ring", tuple(c + nn * 0.04), radius, 0.18,
            ring_mat or M("bronze"), rot=rot, verts=20)
    kit.cyl(parent, name + "_disc", tuple(c + nn * 0.10), radius * 0.82, 0.18,
            face_mat or M("granite_dark"), rot=rot, verts=20)
    up = Vector((0, 0, 1))
    side = up.cross(nn).normalized()
    sc = c + nn * 0.22
    for k, ang in enumerate((0.4, 2.1)):
        d = (side * math.sin(ang) + up * math.cos(ang)) * radius * 0.5
        mid = sc + d
        kit.box(parent, name + f"_bar{k}", tuple(mid),
                (abs(d.x) * 1.2 + 0.12, abs(d.y) * 1.2 + 0.12,
                 abs(d.z) * 1.2 + 0.12), M("gold"))


def _crest(parent, name, center, radius, n):
    """Gilt wreath + star crest (clock crown / tower emblem)."""
    X = mats()
    c = Vector(center)
    nn = Vector(n).normalized()
    if abs(nn.x) > 0.5:
        rot = (0.0, math.radians(90 if nn.x > 0 else -90), 0.0)
    else:
        rot = (math.radians(90 if nn.y > 0 else -90), 0.0, 0.0)
    kit.cyl(parent, name + "_ring", tuple(c + nn * 0.03), radius, 0.16,
            M("gold"), rot=rot, verts=16)
    kit.cyl(parent, name + "_disc", tuple(c + nn * 0.08), radius * 0.84, 0.16,
            M("bronze"), rot=rot, verts=16)
    _star(parent, name + "_star", c.x + nn.x * 0.18, c.y + nn.y * 0.18, c.z,
          radius * 1.45, 0.22, X["star_gold"])
    kit.cyl(parent, name + "_ring2", tuple(c + nn * 0.12), radius * 0.55, 0.14,
            M("gold"), rot=rot, verts=14)


# ==== BUILDERS ====
def _accum(stats, st):
    stats["windows"] += st.get("windows", 0)
    stats["lit"] += st.get("lit", 0)


def _flag_pole_top(parent, name, x, y, z, h=6.0, red=True):
    kit.cyl(parent, name, (x, y, z + h / 2.0), 0.035, h, M("bronze"), verts=6)
    m = mats()["flag_red"] if red else M("stucco_cream")
    kit.box(parent, name + "_cloth", (x + 0.9, y, z + h - 0.55),
            (1.8, 0.04, 1.0), m)


def _skirt(parent, bid, rect, z0=0.0, h=0.55, over=0.16, mat=None):
    B = _Batch(parent, bid + "_skirt", mat or M("granite_dark"))
    B.ring(rect, z0, z0 + h, over=over, t=0.5)
    B.flush()


# ============================================================================
# 1. MSU MAIN BUILDING 鈥?vysotka_main
# ============================================================================
def build_vysotka_main(parent, block, rng, ctx):
    bid = block["id"]
    stats = {"windows": 0, "lit": 0}
    rect = _rect(block)
    x0, y0, x1, y1 = rect
    cx, cy = _center(rect)
    wal, pil, trim = M("limestone"), M("limestone_light"), M("limestone_light")
    base_m, plinth_m = M("stone_rusticated"), M("granite_dark")
    X = mats()
    FH, FL, ground = 6.2, 3, 4.0
    # ---------------- base / wing mass ----------------
    z, st = _tier(parent, bid, "base", rect, 0.0, FL, FH, wal, rng,
                  win_w=1.65, win_h=3.40, spacing=2.75, margin=1.35, lit=0.05,
                  plinth_mat=plinth_m, plinth_h=3.3, ground_bottom=ground,
                  surr_mat=pil, strings=3, string_mat=trim, hood=0.45,
                  pilaster_w=0.62, pilaster_mat=pil,
                  cornice_h=1.00, cornice_mat=trim, cornice_mat_b=wal,
                  cornice_over=0.72, parapet=1.05)
    _accum(stats, st)
    base_top = z
    _skirt(parent, bid, rect, 0.0, 1.05, 0.22, plinth_m)
    _rustication(parent, bid + "_rust", rect, 1.05, ground + 0.5, base_m, 0.64,
                 0.15, plinth_m)
    _string_course(parent, bid + "_redband", rect, base_top - 1.55,
                   X["red_band"], 0.30, 0.10)
    _quoins(parent, bid + "_quoins", rect, ground, base_top - 1.4, pil, 1.85)
    # roof-terrace ornament: obelisk finials along the balustrade + corner statues
    for i, (sx, sy) in enumerate(((x0 + 1.2, y0 + 1.2), (x1 - 1.2, y0 + 1.2),
                                   (x1 - 1.2, y1 - 1.2), (x0 + 1.2, y1 - 1.2))):
        _statue(parent, f"{bid}_roofstat{i}", sx, sy, base_top + 0.2, h=3.0,
                ped_h=1.7, mat=pil)
    for i in range(5):
        fy = y0 + (y1 - y0) * (0.14 + 0.18 * i)
        _pinnacle(parent, f"{bid}_balpinW{i}", x0 + 0.35, fy, base_top + 0.2,
                  0.7, 2.2, pil, M("iron_dark"))
        _pinnacle(parent, f"{bid}_balpinE{i}", x1 - 0.35, fy, base_top + 0.2,
                  0.7, 2.2, pil, M("iron_dark"))
    for i in range(3):
        fx = x0 + (x1 - x0) * (0.25 + 0.25 * i)
        _pinnacle(parent, f"{bid}_balpinS{i}", fx, y0 + 0.35, base_top + 0.2,
                  0.7, 2.2, pil, M("iron_dark"))
        _pinnacle(parent, f"{bid}_balpinN{i}", fx, y1 - 0.35, base_top + 0.2,
                  0.7, 2.2, pil, M("iron_dark"))
    _roof_service(parent, bid, "baseroof", rect, base_top + 0.15, rng, vents=4)
    for i, (fx, fy) in enumerate(((x0 + 2.2, y0 + 2.2), (x0 + 2.2, y1 - 2.2))):
        _flag(parent, f"{bid}_wingflag{i}", fx, fy, base_top + 1.1, 8.0, 2.6, 1.5,
              math.radians(90))

    # ---------------- four corner clock turrets ----------------
    tur_xy = ((x0 + 0.30, y0 + 0.30), (x1 - 4.70, y0 + 0.30),
              (x1 - 4.70, y1 - 4.70), (x0 + 0.30, y1 - 4.70))
    for ti, (tx, ty) in enumerate(tur_xy):
        trect = (tx, ty, tx + 4.4, ty + 4.4)
        tshaft, st = _tier(parent, bid, f"tur{ti}", trect, base_top, 2, 4.3, wal,
                           rng, win_w=0.95, win_h=2.1, spacing=1.9, margin=0.95,
                           lit=0.06, ground_bottom=0.8, surr_mat=pil,
                           keystone=False, cornice_h=0.42, cornice_mat=trim,
                           cornice_mat_b=wal, cornice_over=0.30)
        _accum(stats, st)
        tcx, tcy = tx + 2.2, ty + 2.2
        # clock band with corner pilasters
        kit.box(parent, f"{bid}_turclkband{ti}", (tcx, tcy, tshaft + 1.45),
                (4.5, 4.5, 2.9), wal)
        for cx4, cy4 in ((tx + 0.55, ty + 0.55), (tx + 3.85, ty + 0.55),
                         (tx + 3.85, ty + 3.85), (tx + 0.55, ty + 3.85)):
            kit.box(parent, f"{bid}_turpil{ti}_{cx4:.0f}_{cy4:.0f}",
                    (cx4, cy4, tshaft + 1.45), (0.8, 0.8, 2.9), pil)
        _cornice(parent, f"{bid}_turtop{ti}", trect, tshaft + 2.9, trim, wal,
                 0.34, 0.50, dentils=True)
        for _nm, _c, _n in (("w", (tx - 0.03, tcy, tshaft + 1.45), (-1, 0, 0)),
                            ("e", (tx + 4.43, tcy, tshaft + 1.45), (1, 0, 0)),
                            ("s", (tcx, ty - 0.03, tshaft + 1.45), (0, -1, 0)),
                            ("n", (tcx, ty + 4.43, tshaft + 1.45), (0, 1, 0))):
            _clock(parent, f"{bid}_turclk{ti}{_nm}", _c, 1.05, _n)
        z0 = tshaft + 2.9 + 0.50
        _oct_drum(parent, f"{bid}_turdrum{ti}", tcx, tcy, z0, z0 + 1.45, 2.35,
                  pil, trim, pinnacles=8, pin_mat=pil)
        _spire(parent, f"{bid}_tursp{ti}", tcx, tcy, z0 + 1.45, z0 + 4.9, 1.15,
               0.06, M("copper_roof"), M("gold"), seg=8,
               rings=(0.2, 0.45, 0.7))
        _star(parent, f"{bid}_turstar{ti}", tcx, tcy, z0 + 5.35, 1.15, 0.24,
              mats()["star_gold"], gold=True)
        _flag(parent, f"{bid}_turflag{ti}", tcx, tcy, z0 + 5.2, 4.6, 1.6, 1.0,
              math.radians(30 + 80 * ti))

    # ---------------- central tower ----------------
    tw = 17.0
    trect = (cx - tw / 2.0, cy - tw / 2.0, cx + tw / 2.0, cy + tw / 2.0)
    t1top, st = _tier(parent, bid, "t1", trect, base_top, 3, 4.4, wal, rng,
                      win_w=1.50, win_h=2.85, spacing=2.8, margin=1.4, lit=0.06,
                      ground_bottom=0.95, surr_mat=pil, pilaster_w=0.58,
                      pilaster_mat=pil, strings=1, string_mat=trim,
                      cornice_h=0.68, cornice_mat=trim, cornice_mat_b=wal,
                      cornice_over=0.50, parapet=0.95)
    _accum(stats, st)
    _string_course(parent, bid + "_t1red", trect, t1top - 1.35, X["red_band"],
                   0.28, 0.09)
    for i, (sx, sy) in enumerate(((cx - 6.7, cy - 6.7), (cx + 6.7, cy - 6.7),
                                   (cx + 6.7, cy + 6.7), (cx - 6.7, cy + 6.7))):
        _statue(parent, f"{bid}_tstat{i}", sx, sy, t1top + 0.15, h=2.9, ped_h=1.4,
                mat=pil)
    tw = 14.0
    trect = (cx - tw / 2.0, cy - tw / 2.0, cx + tw / 2.0, cy + tw / 2.0)
    t2top, st = _tier(parent, bid, "t2", trect, t1top, 2, 4.3, wal, rng,
                      win_w=1.35, win_h=2.60, spacing=2.7, margin=1.35, lit=0.07,
                      ground_bottom=0.92, surr_mat=pil, pilaster_w=0.52,
                      pilaster_mat=pil, cornice_h=0.62, cornice_mat=trim,
                      cornice_mat_b=wal, cornice_over=0.44, parapet=0.85)
    _accum(stats, st)
    clk_z = t2top - 2.8
    for _nm, _c, _n in (("w", (cx - tw / 2.0 - 0.03, cy, clk_z), (-1, 0, 0)),
                        ("e", (cx + tw / 2.0 + 0.03, cy, clk_z), (1, 0, 0)),
                        ("s", (cx, cy - tw / 2.0 - 0.03, clk_z), (0, -1, 0)),
                        ("n", (cx, cy + tw / 2.0 + 0.03, clk_z), (0, 1, 0))):
        _clock(parent, f"{bid}_clk_{_nm}", _c, 1.30, _n)
    tw = 11.0
    trect = (cx - tw / 2.0, cy - tw / 2.0, cx + tw / 2.0, cy + tw / 2.0)
    t3top, st = _tier(parent, bid, "t3", trect, t2top, 1, 4.3, wal, rng,
                      win_w=1.25, win_h=2.40, spacing=2.5, margin=1.25, lit=0.07,
                      ground_bottom=0.93, surr_mat=pil, pilaster_w=0.48,
                      pilaster_mat=pil, cornice_h=0.58, cornice_mat=trim,
                      cornice_mat_b=wal, cornice_over=0.42, parapet=0.75)
    _accum(stats, st)
    # tiered crown: octagon -> smaller octagon -> tapered bronze spire -> star
    crown0 = t3top
    _oct_drum(parent, bid + "_crownA", cx, cy, crown0, crown0 + 2.45, 4.60, pil,
              trim, pinnacles=8, pin_mat=pil)
    crown1 = crown0 + 2.45
    _oct_drum(parent, bid + "_crownB", cx, cy, crown1, crown1 + 2.05, 3.55, pil,
              trim, pinnacles=8, pin_mat=pil)
    crown2 = crown1 + 2.05
    _spire(parent, bid, cx, cy, crown2, 64.6, 2.45, 0.12,
           M("bronze"), M("gold"), seg=8,
           rings=(0.05, 0.18, 0.32, 0.46, 0.60, 0.74, 0.87, 0.95))
    kit.cyl(parent, bid + "_starcollar", (cx, cy, 65.15), 1.25, 0.35,
            M("gold"), verts=12)
    _star(parent, bid + "_star", cx, cy, 66.9, 3.45, 0.60)
    _crest(parent, bid + "_clockcrest", (cx - 7.03, cy, t2top - 0.55), 1.15,
           (-1, 0, 0))
    for i, (dx, dy) in enumerate(((-4.2, -4.2), (4.2, -4.2), (4.2, 4.2),
                                   (-4.2, 4.2))):
        kit.cyl(parent, f"{bid}_fl{i}", (cx + dx, cy + dy, t3top + 2.6), 0.06,
                4.6, M("iron_dark"), verts=6)
        kit.box(parent, f"{bid}_flh{i}", (cx + dx, cy + dy, t3top + 4.8),
                (0.7, 0.35, 0.22), X["lamp"])
    _roof_service(parent, bid, "t1roof", (cx - 7.4, cy - 7.4, cx + 7.4, cy + 7.4),
                  t1top + 0.2, rng, vents=2)

    # ---------------- west front: giant portico + side colonnades -------------
    P0, u, L, n, _sh = _face_parts(rect, "NX")
    _colonnade(parent, bid + "_port", P0, u, n, L / 2.0 - 7.6, L / 2.0 + 7.6, 8,
               3.3, 11.4, 0.50, trim, pil, out=0.72)
    _entablature(parent, bid + "_portent", P0, u, n, L / 2.0 - 8.4,
                 L / 2.0 + 8.4, THICK + 0.55, THICK + 1.20, 14.7, 17.1, wal,
                 trim)
    _fbox(parent, bid + "_portattic", P0, u, n, L / 2.0 - 8.1, L / 2.0 + 8.1,
          THICK + 0.55, THICK + 1.05, 17.1, 19.0, wal)
    _fbox(parent, bid + "_portrelief", P0, u, n, L / 2.0 - 3.8, L / 2.0 + 3.8,
          THICK + 1.05, THICK + 1.12, 17.4, 18.8, M("relief_stone"))
    _steps(parent, bid + "_portsteps", P0, u, n, L / 2.0 - 4.8, L / 2.0 + 4.8,
           3.5, 4, 0.24, M("limestone_light"))
    _bronze_door_front(parent, bid + "_pdoor", P0, u, n, L / 2.0, 3.5, 3.2, 5.6)
    for side in (-1, 1):
        s_c = L / 2.0 + side * (L / 2.0 - 3.2)
        _colonnade(parent, f"{bid}_sideport{side}", P0, u, n, s_c - 2.8,
                   s_c + 2.8, 4, 3.3, 8.6, 0.42, trim, pil, out=0.60)
        _entablature(parent, f"{bid}_sideent{side}", P0, u, n, s_c - 3.4,
                     s_c + 3.4, THICK + 0.45, THICK + 1.00, 11.9, 13.6, wal,
                     trim)
        _fbox(parent, f"{bid}_sideattic{side}", P0, u, n, s_c - 3.2, s_c + 3.2,
              THICK + 0.45, THICK + 0.90, 13.6, 14.8, wal)
        _steps(parent, f"{bid}_sidesteps{side}", P0, u, n, s_c - 2.9, s_c + 2.9,
               3.5, 4, 0.23, M("limestone_light"))
    # prestige details: gilt crests flanking the portico + tower panels + lanterns
    _crest(parent, bid + "_basecrest", (x0 - 0.04, cy, 18.05), 0.70,
           (-1, 0, 0))
    for sy in (-4.6, 4.6):
        _crest(parent, f"{bid}_sidecrest{int(sy * 10)}",
               (cx + sy, y0 - 0.04, 18.05), 0.62, (0, -1, 0))
    for ly in (-6.2, 6.2):
        kit.box(parent, f"{bid}_lantern{int(ly)}", (x0 - 0.62, cy + ly, 13.9),
                (0.42, 0.42, 0.75), mats()["lamp"])
        kit.box(parent, f"{bid}_lanterncap{int(ly)}", (x0 - 0.62, cy + ly, 14.35),
                (0.52, 0.52, 0.14), M("bronze"))
    n_obj, n_tri = _count(parent)
    stats.update({"objects": n_obj, "tris": n_tri,
                  "height": float(block.get("spire") or 68.0)})
    return stats
def _bronze_door_front(parent, name, P0, u, n, s, z, w=3.0, h=5.4):
    """Recessed entrance: dark reveal, bronze doors, stone frame."""
    _fbox(parent, name + "_rec", P0, u, n, s - w / 2.0 - 0.25, s + w / 2.0 + 0.25,
          THICK - 0.02, THICK + 0.06, z - 0.1, z + h + 0.3, M("granite_dark"))
    _fbox(parent, name + "_door", P0, u, n, s - w / 2.0, s + w / 2.0,
          THICK + 0.06, THICK + 0.10, z, z + h, M("bronze"))
    _fbox(parent, name + "_seam", P0, u, n, s - 0.035, s + 0.035,
          THICK + 0.10, THICK + 0.13, z, z + h, M("gold"))
    for side in (-1, 1):
        _fbox(parent, name + f"_jamb{side}", P0, u, n,
              s + side * (w / 2.0 + 0.15), s + side * (w / 2.0 + 0.42),
              THICK - 0.02, THICK + 0.22, z - 0.2, z + h + 0.35, pil_or(1))
    _fbox(parent, name + "_lintel", P0, u, n, s - w / 2.0 - 0.5, s + w / 2.0 + 0.5,
          THICK - 0.02, THICK + 0.24, z + h + 0.35, z + h + 0.75, pil_or(1))


def pil_or(_x=0):
    return M("limestone_light")


# ============================================================================
# 2. HOTEL UKRAINA 鈥?vysotka_hotel
# ============================================================================
def build_vysotka_hotel(parent, block, rng, ctx):
    bid = block["id"]
    stats = {"windows": 0, "lit": 0}
    rect = _rect(block)
    x0, y0, x1, y1 = rect
    cx, cy = _center(rect)
    wal, trim = M("stucco_ochre"), M("stucco_cream")
    base_m, plinth_m = M("stone_rusticated"), M("granite_dark")
    FH, ground = 4.6, 3.2
    # ---------------- rusticated wing / base mass ----------------
    z, st = _tier(parent, bid, "base", rect, 0.0, 4, FH, wal, rng,
                  win_w=1.25, win_h=2.55, spacing=2.05, margin=1.05, lit=0.07,
                  plinth_mat=plinth_m, plinth_h=2.8, ground_bottom=ground,
                  surr_mat=trim, strings=2, string_mat=trim, hood=0.32,
                  pilaster_w=0.38, pilaster_mat=trim,
                  cornice_h=0.85, cornice_mat=trim, cornice_mat_b=wal,
                  cornice_over=0.55, parapet=0.95)
    _accum(stats, st)
    base_top = z
    _skirt(parent, bid, rect, 0.0, 0.85, 0.18, plinth_m)
    _rustication(parent, bid + "_rust", rect, 0.85, ground + 0.2, base_m, 0.58,
                 0.12, plinth_m)
    _quoins(parent, bid + "_quoin", rect, ground, base_top - 0.9, trim, 1.8)
    for i in range(4):
        fy = y0 + (y1 - y0) * (0.16 + 0.22 * i)
        _pinnacle(parent, f"{bid}_balpin{i}", x0 + 0.30, fy, base_top + 0.15,
                  0.6, 1.9, trim, M("iron_dark"))

    # ---------------- four corner turrets (faceted spires) ----------------
    tur = ((x0 + 0.25, y0 + 0.25), (x1 - 3.75, y0 + 0.25),
           (x1 - 3.75, y1 - 3.75), (x0 + 0.25, y1 - 3.75))
    for ti, (tx, ty) in enumerate(tur):
        trect = (tx, ty, tx + 3.5, ty + 3.5)
        tz, st = _tier(parent, bid, f"tur{ti}", trect, base_top, 2, 4.2, wal,
                       rng, win_w=0.85, win_h=1.95, spacing=1.8, margin=0.9,
                       lit=0.08, ground_bottom=0.8, surr_mat=trim,
                       keystone=False, cornice_h=0.45, cornice_mat=trim,
                       cornice_mat_b=wal, cornice_over=0.28)
        _accum(stats, st)
        tcx, tcy = tx + 1.75, ty + 1.75
        _clock(parent, f"{bid}_turclk{ti}w", (tx - 0.03, tcy, tz - 2.1), 0.78,
               (-1, 0, 0))
        _clock(parent, f"{bid}_turclk{ti}s", (tcx, ty - 0.03, tz - 2.1), 0.78,
               (0, -1, 0))
        # octagonal drum + faceted spire + gold star + flag
        _oct_drum(parent, f"{bid}_turd{ti}", tcx, tcy, tz, tz + 1.35, 1.85,
                  trim, wal, pinnacles=8, pin_mat=trim)
        _spire(parent, f"{bid}_tursp{ti}", tcx, tcy, tz + 1.35, tz + 5.4, 1.30,
               0.06, M("bronze"), M("gold"), seg=8,
               rings=(0.18, 0.40, 0.62, 0.82))
        _star(parent, f"{bid}_turstar{ti}", tcx, tcy, tz + 5.9, 1.05, 0.22,
              mats()["star_gold"], gold=True)
        _flag(parent, f"{bid}_turfl{ti}", tcx, tcy, tz + 5.8, 4.2, 1.5, 0.9,
              math.radians(25 + 80 * ti))

    # ---------------- central tower: 10.0 -> 8.2 -> 6.4 -> crown -> spire ----
    tw = 10.0
    trect = (cx - tw / 2.0, cy - tw / 2.0, cx + tw / 2.0, cy + tw / 2.0)
    t1top, st = _tier(parent, bid, "t1", trect, base_top, 3, 3.9, wal, rng,
                      win_w=1.20, win_h=2.15, spacing=2.15, margin=1.05,
                      lit=0.09, ground_bottom=0.88, surr_mat=trim,
                      pilaster_w=0.40, pilaster_mat=trim, strings=1,
                      string_mat=trim, cornice_h=0.55, cornice_mat=trim,
                      cornice_mat_b=wal, cornice_over=0.38, parapet=0.85)
    _accum(stats, st)
    # corner pinnacles + statues at the shaft shoulders
    for i, (dx, dy) in enumerate(((-1, -1), (1, -1), (1, 1), (-1, 1))):
        px = cx + dx * (tw / 2.0 - 0.35)
        py = cy + dy * (tw / 2.0 - 0.35)
        _pinnacle(parent, f"{bid}_tp{i}", px, py, t1top - 0.05, 0.95, 3.1, trim,
                  M("iron_dark"))
        _statue(parent, f"{bid}_tstat{i}", px, py, t1top + 0.05, h=2.0,
                ped_h=0.9, mat=trim)
    # one compact balcony per face per floor (hotel signature)
    for fi, fz in enumerate((base_top + 4.0, base_top + 7.9)):
        for face in ("NX", "PX", "PY", "NY"):
            P0, u, L, n = kit.wall_frame(_shrunk(trect), face)
            for bi, off in enumerate((L * 0.5,)):
                _fbox(parent, f"{bid}_bal{fi}{face}{bi}_slab", P0, u, n,
                      off - 1.0, off + 1.0, THICK, THICK + 0.75, fz, fz + 0.16,
                      trim)
                p0 = P0 + u * (off - 0.95) + n * (THICK + 0.70)
                p1 = P0 + u * (off + 0.95) + n * (THICK + 0.70)
                kit.railing_line(parent, f"{bid}_bal{fi}{face}{bi}_r", p0, p1,
                                 fz + 0.16, 0.85, M("bronze"))
    tw = 8.2
    trect = (cx - tw / 2.0, cy - tw / 2.0, cx + tw / 2.0, cy + tw / 2.0)
    t2top, st = _tier(parent, bid, "t2", trect, t1top, 2, 3.5, wal, rng,
                      win_w=1.10, win_h=2.00, spacing=2.0, margin=1.0, lit=0.10,
                      ground_bottom=0.85, surr_mat=trim, pilaster_w=0.36,
                      pilaster_mat=trim, cornice_h=0.50, cornice_mat=trim,
                      cornice_mat_b=wal, cornice_over=0.34, parapet=0.75)
    _accum(stats, st)
    _clock(parent, bid + "_t2clk_w", (cx - tw / 2.0 - 0.03, cy, t2top - 2.1),
           1.12, (-1, 0, 0))
    tw = 6.4
    trect = (cx - tw / 2.0, cy - tw / 2.0, cx + tw / 2.0, cy + tw / 2.0)
    t3top, st = _tier(parent, bid, "t3", trect, t2top, 1, 3.5, wal, rng,
                      win_w=1.00, win_h=1.95, spacing=1.85, margin=0.95,
                      lit=0.10, ground_bottom=0.85, surr_mat=trim,
                      cornice_h=0.45, cornice_mat=trim, cornice_mat_b=wal,
                      cornice_over=0.30, parapet=0.70)
    _accum(stats, st)
    # crown drum + very tall thin faceted spire (body 42, spire top ~50)
    _oct_drum(parent, bid + "_crown", cx, cy, t3top, t3top + 1.85, 2.75, trim,
              wal, pinnacles=8, pin_mat=trim)
    crown_top = t3top + 1.85
    _spire(parent, bid, cx, cy, crown_top, 49.0, 1.45, 0.06,
           M("bronze"), M("gold"), seg=8,
           rings=(0.04, 0.16, 0.30, 0.44, 0.58, 0.72, 0.85, 0.94))
    kit.cyl(parent, bid + "_starcollar", (cx, cy, 49.15), 0.9, 0.28, M("gold"),
            verts=12)
    _star(parent, bid + "_star", cx, cy, 50.0, 2.15, 0.45)
    for i, (dx, dy) in enumerate(((-2.2, -2.2), (2.2, -2.2), (2.2, 2.2),
                                   (-2.2, 2.2))):
        kit.cyl(parent, f"{bid}_fl{i}", (cx + dx, cy + dy, t3top + 1.2), 0.05,
                2.8, M("iron_dark"), verts=6)
        kit.box(parent, f"{bid}_flh{i}", (cx + dx, cy + dy, t3top + 2.7),
                (0.5, 0.3, 0.2), mats()["lamp"])

    # ---------------- entrance canopy + bronzed doors ----------------
    P0, u, L, n, _sh = _face_parts(rect, "NX")
    _colonnade(parent, bid + "_can", P0, u, n, L / 2.0 - 2.4, L / 2.0 + 2.4, 4,
               3.2, 5.2, 0.26, trim, trim, out=0.60)
    _entablature(parent, bid + "_canent", P0, u, n, L / 2.0 - 3.0,
                 L / 2.0 + 3.0, THICK + 0.40, THICK + 0.95, 8.4, 9.4, wal, trim)
    _fbox(parent, bid + "_canattic", P0, u, n, L / 2.0 - 2.8, L / 2.0 + 2.8,
          THICK + 0.40, THICK + 0.85, 9.4, 10.2, wal)
    _steps(parent, bid + "_cansteps", P0, u, n, L / 2.0 - 2.6, L / 2.0 + 2.6,
           3.2, 4, 0.26, M("limestone_light"))
    _bronze_door_front(parent, bid + "_cdoor", P0, u, n, L / 2.0, 3.2, 2.6, 4.4)
    _roof_service(parent, bid, "baseroof", rect, base_top + 0.1, rng, vents=3,
                  masts=2)
    # entrance / wall lanterns + bronze plaques
    for ly in (-2.5, 2.5):
        kit.box(parent, f"{bid}_lantern{int(ly * 10)}", (x0 - 0.55, cy + ly, 8.6),
                (0.34, 0.34, 0.62), mats()["lamp"])
        kit.box(parent, f"{bid}_lanterncap{int(ly * 10)}",
                (x0 - 0.55, cy + ly, 8.95), (0.44, 0.44, 0.12), M("bronze"))
    for sy in (8.2, -8.2):
        _fbox(parent, f"{bid}_plaq{int(sy)}", P0, u, n, L / 2.0 + sy - 1.1,
              L / 2.0 + sy + 1.1, THICK - 0.02, THICK + 0.09, 5.6, 6.6,
              M("bronze"))
    n_obj, n_tri = _count(parent)
    stats.update({"objects": n_obj, "tris": n_tri,
                  "height": float(block.get("spire") or 50.0)})
    return stats


def build_vysotka_ministry(parent, block, rng, ctx):
    bid = block["id"]
    stats = {"windows": 0, "lit": 0}
    rect = _rect(block)
    x0, y0, x1, y1 = rect
    cx, cy = _center(rect)
    wal, trim = M("granite_grey"), M("limestone_light")
    base_m, plinth_m = M("stone_rusticated"), M("granite_dark")
    X = mats()
    FH, ground = 4.0, 2.9
    # ---------------- hulking slab with dense vertical window grid ------------
    z, st = _tier(parent, bid, "base", rect, 0.0, 5, FH, wal, rng,
                  win_w=1.22, win_h=1.95, spacing=1.80, margin=0.90, lit=0.08,
                  plinth_mat=plinth_m, plinth_h=2.6, ground_bottom=ground,
                  surr_mat=trim, pilaster_w=0.42, pilaster_mat=trim,
                  strings=1, string_mat=trim, keystone=False,
                  cornice_h=0.95, cornice_mat=trim, cornice_mat_b=wal,
                  cornice_over=0.60, parapet=0.95)
    _accum(stats, st)
    base_top = z
    _skirt(parent, bid, rect, 0.0, 1.0, 0.20, plinth_m)
    _rustication(parent, bid + "_rust", rect, 1.0, ground + 0.2, base_m, 0.62,
                 0.13, plinth_m)
    _quoins(parent, bid + "_quoin", rect, ground, base_top - 1.0, trim, 1.7)
    _roof_service(parent, bid, "baseroof", rect, base_top + 0.1, rng, vents=4,
                  masts=3)
    # wing corner pavilions (small towers with obelisk pinnacles)
    for i, (px, py) in enumerate(((x0 + 1.7, y0 + 1.7), (x1 - 1.7, y0 + 1.7),
                                   (x1 - 1.7, y1 - 1.7), (x0 + 1.7, y1 - 1.7))):
        kit.box(parent, f"{bid}_wpav{i}", (px, py, base_top + 2.6),
                (3.1, 3.1, 5.2), wal)
        for fx, fy in ((-1, -1), (1, -1), (1, 1), (-1, 1)):
            kit.box(parent, f"{bid}_wpav{i}p{fx}{fy}",
                    (px + fx * 1.3, py + fy * 1.3, base_top + 2.7),
                    (0.42, 0.42, 5.0), trim)
        for wi, (ox, oy, sx, sy) in enumerate((
                (0.0, -1.57, 1.05, 0.06), (0.0, 1.57, 1.05, 0.06),
                (-1.57, 0.0, 0.06, 1.05), (1.57, 0.0, 0.06, 1.05))):
            kit.box(parent, f"{bid}_wpav{i}win{wi}",
                    (px + ox, py + oy, base_top + 2.9), (sx, sy, 1.75),
                    M("glass_clean"))
            kit.box(parent, f"{bid}_wpav{i}winf{wi}",
                    (px + ox * 1.01, py + oy * 1.01, base_top + 3.85),
                    (sx + 0.25 if sx > 0.1 else 0.06, sy + 0.25 if sy > 0.1 else 0.06,
                     0.22), trim)
        _cornice(parent, f"{bid}_wpavc{i}",
                 (px - 1.55, py - 1.55, px + 1.55, py + 1.55), base_top + 5.2,
                 trim, wal, 0.30, 0.45, dentils=True)
        _pinnacle(parent, f"{bid}_wpavp{i}", px, py, base_top + 5.65, 1.25, 3.6,
                  trim, M("iron_dark"))
    # ---------------- central tower: dense ribbed tiers + crest + spire ------
    tw, td = 9.6, 8.2
    trect = (cx - tw / 2.0, cy - td / 2.0, cx + tw / 2.0, cy + td / 2.0)
    t1top, st = _tier(parent, bid, "t1", trect, base_top, 3, 3.4, wal, rng,
                      win_w=0.98, win_h=1.90, spacing=1.72, margin=0.86,
                      lit=0.09, ground_bottom=0.85, surr_mat=trim,
                      pilaster_w=0.40, pilaster_mat=trim, strings=1,
                      string_mat=trim, keystone=False,
                      cornice_h=0.58, cornice_mat=trim, cornice_mat_b=wal,
                      cornice_over=0.40, parapet=0.8)
    _accum(stats, st)
    for i, (dx, dy) in enumerate(((-1, -1), (1, -1), (1, 1), (-1, 1))):
        _pinnacle(parent, f"{bid}_tpin{i}", cx + dx * (tw / 2.0 - 0.45),
                  cy + dy * (td / 2.0 - 0.45), t1top - 0.15, 0.95, 3.1, trim,
                  M("iron_dark"))
    # big crest emblem + clock on the tower front
    _crest(parent, bid + "_crest", (cx - 4.84, cy, t1top - 3.6), 1.75,
           (-1, 0, 0))
    _clock(parent, bid + "_tclk_s", (cx, cy - td / 2.0 - 0.04, t1top - 3.6),
           1.35, (0, -1, 0))
    tw, td = 7.8, 6.8
    trect = (cx - tw / 2.0, cy - td / 2.0, cx + tw / 2.0, cy + td / 2.0)
    t2top, st = _tier(parent, bid, "t2", trect, t1top, 2, 3.2, wal, rng,
                      win_w=0.92, win_h=1.80, spacing=1.65, margin=0.82,
                      lit=0.10, ground_bottom=0.82, surr_mat=trim,
                      pilaster_w=0.36, pilaster_mat=trim, keystone=False,
                      cornice_h=0.52, cornice_mat=trim, cornice_mat_b=wal,
                      cornice_over=0.36, parapet=0.75)
    _accum(stats, st)
    for i, (dx, dy) in enumerate(((-1, -1), (1, -1), (1, 1), (-1, 1))):
        _pinnacle(parent, f"{bid}_tpin2{i}", cx + dx * (tw / 2.0 - 0.38),
                  cy + dy * (td / 2.0 - 0.38), t2top - 0.15, 0.8, 2.7, trim,
                  M("iron_dark"))
    # crown + small spire + star
    _oct_drum(parent, bid + "_crown", cx, cy, t2top, t2top + 1.55, 2.35, trim,
              wal, pinnacles=8, pin_mat=trim)
    _spire(parent, bid, cx, cy, t2top + 1.55, t2top + 4.9, 1.55, 0.08,
           M("iron_dark"), M("gold"), seg=8,
           rings=(0.08, 0.24, 0.42, 0.6, 0.78, 0.92))
    _star(parent, bid + "_star", cx, cy, t2top + 5.75, 1.75, 0.4)
    # ---------------- entrances + wing porticoes ----------------
    P0, u, L, n, _sh = _face_parts(rect, "NX")
    _colonnade(parent, bid + "_port", P0, u, n, L / 2.0 - 2.9, L / 2.0 + 2.9, 4,
               3.3, 7.6, 0.38, trim, trim, out=0.62)
    _entablature(parent, bid + "_portent", P0, u, n, L / 2.0 - 3.5,
                 L / 2.0 + 3.5, THICK + 0.48, THICK + 1.05, 10.9, 12.2, wal,
                 trim)
    _fbox(parent, bid + "_portattic", P0, u, n, L / 2.0 - 3.3, L / 2.0 + 3.3,
          THICK + 0.48, THICK + 0.95, 12.2, 13.2, wal)
    _bronze_door_front(parent, bid + "_pdoor", P0, u, n, L / 2.0, 3.4, 2.8, 4.6)
    _steps(parent, bid + "_psteps", P0, u, n, L / 2.0 - 3.0, L / 2.0 + 3.0,
           3.4, 4, 0.24, M("limestone_light"))
    for side in (-1, 1):
        s_c = L / 2.0 + side * (L / 2.0 - 3.0)
        _colonnade(parent, f"{bid}_wp{side}", P0, u, n, s_c - 2.1, s_c + 2.1, 4,
                   3.3, 6.0, 0.30, trim, trim, out=0.50)
        _entablature(parent, f"{bid}_wpe{side}", P0, u, n, s_c - 2.7,
                     s_c + 2.7, THICK + 0.40, THICK + 0.92, 9.3, 10.4, wal,
                     trim)
        _steps(parent, f"{bid}_wps{side}", P0, u, n, s_c - 2.4, s_c + 2.4, 3.3,
               3, 0.26, M("limestone_light"))
    # entrance plaque + lanterns
    for ly in (-2.6, 2.6):
        kit.box(parent, f"{bid}_lantern{int(ly * 10)}", (x0 - 0.5, cy + ly, 8.4),
                (0.32, 0.32, 0.6), mats()["lamp"])
        kit.box(parent, f"{bid}_lanterncap{int(ly * 10)}",
                (x0 - 0.5, cy + ly, 8.72), (0.42, 0.42, 0.12), M("bronze"))
    _fbox(parent, bid + "_plaque", P0, u, n, L / 2.0 - 2.4, L / 2.0 + 2.4,
          THICK - 0.02, THICK + 0.08, 5.2, 6.4, M("bronze"))
    n_obj, n_tri = _count(parent)
    stats.update({"objects": n_obj, "tris": n_tri,
                  "height": float(block.get("height") or 38.0)})
    return stats


def build_institute_sino(parent, block, rng, ctx):
    bid = block["id"]
    stats = {"windows": 0, "lit": 0}
    rect = _rect(block)
    x0, y0, x1, y1 = rect
    cx, cy = _center(rect)
    wal, trim = M("stucco_ochre"), M("stucco_cream")
    plinth_m = M("granite_dark")
    X = mats()
    FH, ground = 3.9, 2.2
    mainr = (x0, y0, x0 + 17.0, y1)
    z, st = _tier(parent, bid, "main", mainr, 0.0, 3, FH, wal, rng,
                  win_w=1.50, win_h=2.30, spacing=2.75, margin=1.4, lit=0.06,
                  plinth_mat=plinth_m, plinth_h=2.0, ground_bottom=ground,
                  surr_mat=trim, strings=2, string_mat=trim, hood=0.45,
                  pilaster_w=0.46, pilaster_mat=trim,
                  cornice_h=0.90, cornice_mat=trim, cornice_mat_b=wal,
                  cornice_over=0.58, parapet=0.55)
    _accum(stats, st)
    main_top = z
    _skirt(parent, bid, mainr, 0.0, 0.9, 0.18, plinth_m)
    _rustication(parent, bid + "_rust", mainr, 0.9, ground, M("stone_rusticated"),
                 0.60, 0.11, plinth_m)
    _quoins(parent, bid + "_quoin", mainr, ground, main_top - 0.7, trim, 1.8)
    # low copper hip roofs over the front block
    _hip_roof(parent, bid + "_mainroof", (mainr[0] + 0.6, mainr[1] + 0.6,
                                        mainr[2] - 0.6, mainr[3] - 0.6),
              main_top, 2.0)
    # rear wings (H massing)
    for side in (-1, 1):
        wy0 = (y0 + 1.5) if side < 0 else (y1 - 11.0)
        wrect = (x0 + 17.0, wy0, x1 - 0.0, wy0 + 9.5)
        wz, st = _tier(parent, bid, f"wing{side}", wrect, 0.0, 3, FH, wal,
                       rng, win_w=1.45, win_h=2.30, spacing=2.8, margin=1.4,
                       lit=0.05, plinth_mat=plinth_m, plinth_h=2.7,
                       ground_bottom=ground, surr_mat=trim, strings=1,
                       string_mat=trim, hood=0.35, pilaster_w=0.42,
                       pilaster_mat=trim, cornice_h=0.8,
                       cornice_mat=trim, cornice_mat_b=wal,
                       cornice_over=0.50, parapet=0.45)
        _accum(stats, st)
        _hip_roof(parent, f"{bid}_wingroof{side}",
                  (wrect[0] + 0.5, wy0 + 0.5, wrect[2] - 0.5, wy0 + 9.0),
                  wz, 1.9)
    # ---------------- central tower with Chinese hipped roof ----------------
    tw = 9.2
    trect = (x0 + 4.6, cy - tw / 2.0, x0 + 4.6 + tw, cy + tw / 2.0)
    tz, st = _tier(parent, bid, "tower", trect, main_top, 3, 2.5, wal, rng,
                   win_w=1.05, win_h=1.90, spacing=1.85, margin=0.92, lit=0.08,
                   ground_bottom=0.80, surr_mat=trim, pilaster_w=0.38,
                   pilaster_mat=trim, cornice_h=0.50, cornice_mat=trim,
                   cornice_mat_b=wal, cornice_over=0.34)
    _accum(stats, st)
    roof_top = _chinese_roof(parent, bid + "_cnroof",
                             (trect[0] + trect[2]) / 2.0,
                             (trect[1] + trect[3]) / 2.0, tz, tw + 0.5,
                             tw + 0.5, 1.8, upturn=0.42)
    suite = _crest(parent, bid + "_tcrest", (trect[0] - 0.04, cy, tz - 1.4),
                   1.0, (-1, 0, 0))
    # ---------------- portico + pediment + grand stairs ----------------
    P0, u, L, n, _sh = _face_parts(mainr, "NX")
    _colonnade(parent, bid + "_port", P0, u, n, L / 2.0 - 3.2, L / 2.0 + 3.2, 6,
               3.2, 8.2, 0.36, trim, trim, out=0.58)
    _entablature(parent, bid + "_portent", P0, u, n, L / 2.0 - 3.8,
                 L / 2.0 + 3.8, THICK + 0.50, THICK + 1.12, 11.4, 13.0, wal,
                 trim)
    _pediment(parent, bid + "_ped", P0, u, n, L / 2.0, 10.4, 13.0, 2.9, 1.0,
              wal, trim, M("relief_stone"))
    _steps(parent, bid + "_steps", P0, u, n, L / 2.0 - 4.0, L / 2.0 + 4.0, 3.2,
           6, 0.24, M("limestone_light"))
    _bronze_door_front(parent, bid + "_door", P0, u, n, L / 2.0, 3.2, 3.0, 4.4)
    _balustrade(parent, bid + "_attic", mainr, main_top, 0.5, trim)
    for i, (fx, fy) in enumerate(((x0 + 3.0, y0 + 3.0), (x0 + 3.0, y1 - 3.0))):
        _flag(parent, f"{bid}_flag{i}", fx, fy, main_top + 0.5, 6.5, 2.4, 1.4,
              math.radians(90))
    _roof_service(parent, bid, "roof", (mainr[0] + 3, mainr[1] + 3,
                                       x0 + 14, mainr[3] - 3), main_top + 0.1,
                  rng, vents=2)
    # tower crest (red star in gilt wreath) + portico lanterns
    _crest(parent, bid + "_pedcrest", (x0 - 1.06, cy, 14.05), 0.92,
           (-1, 0, 0))
    for ly in (-3.4, 3.4):
        kit.box(parent, f"{bid}_lantern{int(ly * 10)}", (x0 - 0.55, cy + ly, 9.6),
                (0.3, 0.3, 0.55), mats()["lamp"])
        kit.box(parent, f"{bid}_lanterncap{int(ly * 10)}",
                (x0 - 0.55, cy + ly, 9.9), (0.4, 0.4, 0.1), M("bronze"))
    n_obj, n_tri = _count(parent)
    stats.update({"objects": n_obj, "tris": n_tri,
                  "height": float(block.get("height") or 24.0)})
    return stats


def _hip_roof(parent, name, rect, z, h, mat=None):
    """Low hipped roof (4 sloped planes) fitted to a rectangle."""
    mat = mat or M("copper_roof")
    x0, y0, x1, y1 = rect
    cx, cy = (x0 + x1) / 2.0, (y0 + y1) / 2.0
    w, d = x1 - x0, y1 - y0
    _frustum(parent, name, cx, cy, w, d, w * 0.30, d * 0.16, z, z + h, mat)
    kit.box(parent, name + "_cap", (cx, cy, z + h + 0.05),
            (w * 0.32, d * 0.18, 0.14), M("iron_dark"))
    return z + h


def build_museum_colonnade(parent, block, rng, ctx):
    bid = block["id"]
    stats = {"windows": 0, "lit": 0}
    rect = _rect(block)
    x0, y0, x1, y1 = rect
    cx, cy = _center(rect)
    wal, trim = M("limestone"), M("limestone_light")
    plinth_m = M("granite_dark")
    X = mats()
    FH = 4.8
    hall_top = 3.0 + 2 * FH                      # 12.6
    ent_top = hall_top + 1.9                     # 14.5
    body = (x0 + 2.8, y0, x1, y1)
    # ---------------- hall shell (front wall recessed behind the colonnade) ---
    P0b, ub, Lb, nb, _shb = _face_parts(body, "NX")
    st = _facade(parent, bid, "hall", body, "NX", 0.0, hall_top, wal, rng,
                 floors=2, fh=FH, win_w=1.5, win_h=2.5, spacing=2.6,
                 margin=1.3, lit=0.05, plinth_mat=plinth_m, plinth_h=2.4,
                 ground_bottom=3.2, surr_mat=trim, hood=0.5)
    _accum(stats, st)
    for face in ("PY", "NY", "PX"):
        st = _facade(parent, bid, "hall", rect, face, 0.0, hall_top, wal, rng,
                     floors=2, fh=FH, win_w=1.5, win_h=2.5, spacing=2.6,
                     margin=1.3, lit=0.05, plinth_mat=plinth_m, plinth_h=3.0,
                     ground_bottom=3.2, surr_mat=trim, hood=0.5)
        _accum(stats, st)
    _corner_piers(parent, bid + "_corn", rect, 0.0, hall_top, wal, 0.08, 0.8)
    _skirt(parent, bid, rect, 0.0, 1.0, 0.22, plinth_m)
    _rustication(parent, bid + "_rust", rect, 1.0, 3.0, M("stone_rusticated"),
                 0.60, 0.13, plinth_m)
    kit.box_mm(parent, bid + "_porchroof", x0, y0, hall_top - 0.6,
               body[0] + 0.5, y1, hall_top + 0.2, wal)
    _roof_flat(parent, bid + "_hallroof", rect, hall_top, wal)
    # rear attic volume + skylights so the big flat roof reads as a museum block
    attic = (x0 + 9.5, y0 + 2.2, x1 - 2.0, y1 - 2.2)
    kit.box_mm(parent, bid + "_attic", attic[0], attic[1], hall_top,
               attic[2], attic[3], hall_top + 2.1, wal)
    _cornice(parent, bid + "_atticc", attic, hall_top + 2.1, trim, wal, 0.45,
             0.75, dentils=True)
    for i in range(4):
        sy = y0 + 3.5 + (y1 - y0 - 7.0) * (i + 0.5) / 4.0
        kit.box(parent, f"{bid}_skylight{i}", (x0 + 5.6, sy, hall_top + 0.55),
                (2.4, 2.0, 1.1), trim)
        kit.box(parent, f"{bid}_skylightg{i}", (x0 + 5.6, sy, hall_top + 1.12),
                (2.0, 1.6, 0.10), M("glass_ribbon"))
    # ---------------- giant square-column colonnade across the front ---------
    P0, u, L, n, _sh = _face_parts(rect, "NX")
    col_s0, col_s1 = 1.4, L - 1.4
    _colonnade(parent, bid + "_col", P0, u, n, col_s0, col_s1, 12, 3.0, 9.0,
               0.44, trim, trim, square=True, out=0.52)
    _entablature(parent, bid + "_coent", P0, u, n, col_s0 - 0.9, col_s1 + 0.9,
                 THICK + 0.42, THICK + 1.08, 12.0, ent_top, wal, trim)
    _fbox(parent, bid + "_cofrieze", P0, u, n, col_s0 - 0.6, col_s1 + 0.6,
          THICK + 1.08, THICK + 1.14, 13.0, 13.7, M("relief_stone"))
    # ---------------- central projecting portico + bronze doors -------------
    _colonnade(parent, bid + "_port", P0, u, n, L / 2.0 - 4.4, L / 2.0 + 4.4, 6,
               3.0, 9.8, 0.48, trim, trim, square=True, out=0.62)
    _entablature(parent, bid + "_portent", P0, u, n, L / 2.0 - 5.1,
                 L / 2.0 + 5.1, THICK + 0.56, THICK + 1.14, 12.8,
                 ent_top + 0.3, wal, trim)
    _bronze_door_front(parent, bid + "_doors", P0, u, n, L / 2.0, 3.0, 3.4, 5.2)
    _steps(parent, bid + "_steps", P0, u, n, L / 2.0 - 6.0, L / 2.0 + 6.0, 3.0,
           5, 0.30, M("limestone_light"))
    # niches + statues between the colonnade bays
    for i, ss in enumerate((L / 2.0 - 7.6, L / 2.0 - 6.2, L / 2.0 + 6.2,
                            L / 2.0 + 7.6)):
        _fbox(parent, f"{bid}_niche{i}", P0, u, n, ss - 0.72, ss + 0.72,
              THICK + 0.05, THICK + 0.35, 3.0, 7.2, M("relief_stone"))
        _statue(parent, f"{bid}_nstat{i}", P0.x + u.x * ss + n.x * 0.95,
                P0.y + u.y * ss + n.y * 0.95, 3.0, h=2.5, ped_h=0.9,
                mat=M("limestone_light"))
    # ---------------- central tower block with red star + spire -------------
    tw, td = 8.8, 7.0
    trect = (x0 + 1.0, cy - tw / 2.0, x0 + 1.0 + td, cy + tw / 2.0)
    tz, st = _tier(parent, bid, "tower", trect, ent_top, 1, 3.0, wal, rng,
                   win_w=1.15, win_h=2.2, spacing=2.2, margin=1.1, lit=0.07,
                   ground_bottom=0.75, surr_mat=trim, pilaster_w=0.46,
                   pilaster_mat=trim, cornice_h=0.45, cornice_mat=trim,
                   cornice_mat_b=wal, cornice_over=0.36)
    _accum(stats, st)
    tower_top = tz
    # crown pavilion with the big red star in a circular bronze frame
    kit.box(parent, bid + "_crown", ((trect[0] + trect[2]) / 2.0,
                                    (trect[1] + trect[3]) / 2.0,
                                    tower_top + 0.7),
            (5.6, 6.2, 1.4), wal)
    _cornice(parent, bid + "_crownc", (trect[0] + 1.2, trect[1] + 1.0,
                                       trect[2] - 1.2, trect[3] - 1.0),
             tower_top + 1.4, trim, wal, 0.30, 0.42, dentils=True)
    _emblem(parent, bid + "_starring", (trect[0] - 0.04, cy, tower_top - 2.0),
            1.30, Vector((-1.0, 0.0, 0.0)))
    _star(parent, bid + "_crownstar", trect[0] - 0.16, cy, tower_top - 2.0, 1.75,
          0.30, X["star_red"])
    kit.cyl(parent, bid + "_spire", ((trect[0] + trect[2]) / 2.0,
                                    (trect[1] + trect[3]) / 2.0,
                                    tower_top + 1.9), 0.13, 1.9,
            M("iron_dark"), verts=8)
    _cone(parent, bid + "_spirec", ((trect[0] + trect[2]) / 2.0,
                                    (trect[1] + trect[3]) / 2.0,
                                    tower_top + 3.2), 0.28, 0.02, 0.9,
          M("gold"), seg=8)
    _star(parent, bid + "_spiretstar", ((trect[0] + trect[2]) / 2.0),
          (trect[1] + trect[3]) / 2.0, tower_top + 3.8, 0.95, 0.22,
          X["star_gold"], gold=True)
    _flag(parent, bid + "_bigflag", x0 + 3.4, cy + 5.4, hall_top + 0.6, 4.6,
          2.2, 1.3, math.radians(90))
    # roof parapets + obelisk finials
    _balustrade(parent, bid + "_roofbal", (body[0] + 0.4, y0 + 0.4, x1 - 0.4,
                                           y1 - 0.4), hall_top + 0.20, 0.85,
                trim, pier_step=3.2)
    for i, (px, py) in enumerate(((x0 + 1.4, y0 + 1.4), (x0 + 1.4, y1 - 1.4),
                                   (x1 - 1.4, y0 + 1.4), (x1 - 1.4, y1 - 1.4))):
        _pinnacle(parent, f"{bid}_roofpin{i}", px, py, hall_top + 0.4, 1.15,
                  3.1, trim, M("iron_dark"))
    _roof_service(parent, bid, "roof", (body[0] + 2, y0 + 3, x1 - 3, y1 - 3),
                  hall_top + 0.2, rng, vents=4, masts=2)
    # fluting on the giant square columns (vertical recessed channels)
    o_face = THICK + 0.52 + 0.374
    for i in range(12):
        ss = col_s0 + (col_s1 - col_s0) * i / 11.0
        for fo in (-0.19, 0.0, 0.19):
            _fbox(parent, f"{bid}_flute{i}_{int(fo * 100)}", P0, u, n,
                  ss + fo - 0.035, ss + fo + 0.035, o_face - 0.055,
                  o_face + 0.005, 3.25, 11.75, M("granite_grey"))
    for i in range(6):
        ss = L / 2.0 - 4.4 + 8.8 * i / 5.0
        for fo in (-0.2, 0.2):
            _fbox(parent, f"{bid}_flutep{i}_{int(fo * 100)}", P0, u, n,
                  ss + fo - 0.035, ss + fo + 0.035,
                  THICK + 0.62 + 0.374 - 0.055, THICK + 0.62 + 0.374 + 0.005,
                  3.25, 12.15, M("granite_grey"))
    # bronze relief panels on the door wall
    for sx in (-3.6, -2.2, 2.2, 3.6):
        _fbox(parent, f"{bid}_doorrel{int(sx * 10)}", P0, u, n,
              L / 2.0 + sx - 0.55, L / 2.0 + sx + 0.55, THICK + 0.02,
              THICK + 0.10, 3.2, 6.6, M("bronze"))
    n_obj, n_tri = _count(parent)
    stats.update({"objects": n_obj, "tris": n_tri,
                  "height": float(block.get("height") or 22.0)})
    return stats


# ----------------------------------------------------------------------------
TYPES = {
    "vysotka_main": build_vysotka_main,
    "vysotka_hotel": build_vysotka_hotel,
    "vysotka_ministry": build_vysotka_ministry,
    "institute_sino": build_institute_sino,
    "museum_colonnade": build_museum_colonnade,
}
