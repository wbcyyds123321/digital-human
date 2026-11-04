"""vehicles.py — Soviet cars, bus, tram car, utility poles, catenary + wires.

Public API (called by build.py):
    build(parent, plan, rng, ctx) -> dict of counts

All geometry is built in WORLD coordinates (metres, Z up, +Y north) and parented to
`parent`. Real-world scale. No lights / cameras / ground / rails are created here —
the Lead owns those. Deterministic for a given kit.RNG().

Vehicle models (local frame: +X = front, +Y = left, z=0 = tyre contact plane):
    VAZ-2101 Zhiguli (sedan), GAZ-24 Volga (sedan), Moskvich-412 (sedan),
    UAZ-469 (boxy jeep w/ spare wheel), ZIL-130 (cab + flatbed truck),
    LAZ-695 bus, Tatra-T3 tram car.
Paint is deliberately desaturated (faded cream / grey-blue / olive / red-brown / black).

Placement follows plan.json prop_zones (CARS_*, POLES_*) plus courtyard / kerb cars,
a bus stopped at BUS_STOP and one far down the avenue, a tram at TRAM stop_platform.

Object name prefixes: VEH_ / POLE_ / TRAM_ / WIRE_.
"""
from __future__ import annotations

import math
import os
import sys

_HERE = os.path.dirname(os.path.abspath(__file__))
if _HERE not in sys.path:
    sys.path.insert(0, _HERE)

import bmesh
import bpy
from mathutils import Euler, Matrix, Vector

import kit

TAU = math.pi * 2.0
RAD = math.radians


# ----------------------------------------------------------------------------
# materials (lazily created, cached inside kit.MATS)
# ----------------------------------------------------------------------------
_M: dict[str, object] = {}


def _mat(key, color, rough=0.7, metallic=0.0, emission=None, strength=0.0):
    full = "VEH_" + key
    if full in _M:
        return _M[full]
    m = kit.mat_solid(full, color, rough=rough, metallic=metallic,
                      emission=emission, emission_strength=strength)
    _M[full] = m
    return m


def _blend_hex(hexc, target="#9A978C", amount=0.34):
    """Fade a paint colour towards a dusty grey (worn / sun-bleached paint)."""
    a = hexc.lstrip("#")
    b = target.lstrip("#")
    out = []
    for i in (0, 2, 4):
        c0 = int(a[i:i + 2], 16) / 255.0
        c1 = int(b[i:i + 2], 16) / 255.0
        out.append(round((c0 * (1 - amount) + c1 * amount) * 5) / 5.0)
    return "#" + "".join(f"{int(max(0, min(1, v)) * 255):02X}" for v in out)


PAINTS = {
    "cream": "#8F8D7D",
    "beige": "#95917F",
    "bluegrey": "#61707A",
    "paleblue": "#74838B",
    "olive": "#5C6353",
    "darkgreen": "#3E4B44",
    "redbrown": "#583D36",
    "charcoal": "#31363A",
    "steel": "#566B78",
    "fadedred": "#6B423A",
    "greygreen": "#6B7264",
}


def paint(key, worn=False):
    key = key if key in PAINTS else "beige"
    if worn:
        return _mat("PAINT_" + key + "_WORN", _blend_hex(PAINTS[key]),
                    rough=0.92, metallic=0.0)
    return _mat("PAINT_" + key, PAINTS[key], rough=0.56, metallic=0.16)


def tire():
    return _mat("TIRE", "#1B1D1F", rough=0.95)


def hub():
    return _mat("HUB", "#83878A", rough=0.42, metallic=0.72)


def chrome():
    return _mat("CHROME", "#A6ADB2", rough=0.24, metallic=1.0)


def glass():
    return _mat("GLASS", "#0A1016", rough=0.08, metallic=0.30)


def glass_lit():
    return _mat("GLASS_LIT", "#A6BCC9", rough=0.3,
                emission="#89A4B8", strength=1.05)


def headlamp():
    return _mat("HEADLAMP", "#C3C7B6", rough=0.16,
                emission="#CBD2C0", strength=0.30)


def taillamp():
    return _mat("TAILLAMP", "#482622", rough=0.32,
                emission="#5E2620", strength=0.10)


def amber():
    return _mat("AMBER", "#8A5A2C", rough=0.35, emission="#9A5F2A", strength=0.5)


def plate():
    return _mat("PLATE", "#C1C1B4", rough=0.65)


def rust():
    return _mat("RUST", "#5B4135", rough=0.96)


def dark():
    return _mat("DARK", "#23272A", rough=0.8)


def trim():
    return _mat("TRIM", "#2D3235", rough=0.45, metallic=0.55)


def canvas():
    return _mat("CANVAS", "#4C4C3E", rough=0.95)


def wood():
    return _mat("WOOD", "#584A39", rough=0.9)


def bedmetal():
    return _mat("BEDMETAL", "#4B584F", rough=0.75, metallic=0.3)


def buscream():
    return _mat("BUS_CREAM", "#93928A", rough=0.7)


def busblue():
    return _mat("BUS_BLUE", "#4A5F6B", rough=0.7)


def tramcream():
    return _mat("TRAM_CREAM", "#A29D90", rough=0.68)


def tramred():
    return _mat("TRAM_RED", "#683B34", rough=0.68)


def poleconc():
    return _mat("POLECONC", "#7C807C", rough=0.92)


def polewood():
    return _mat("POLEWOOD", "#554B3E", rough=0.95)


def polemetal():
    return _mat("POLEMETAL", "#3C4246", rough=0.6, metallic=0.5)


def insulator():
    return _mat("INSULATOR", "#B5B3A2", rough=0.35)


def wiremat():
    return _mat("WIRE", "#1D2125", rough=0.6, metallic=0.4)


def transformer():
    return _mat("TRANSFORMER", "#49514F", rough=0.6, metallic=0.4)


# ----------------------------------------------------------------------------
# mesh accumulator: many primitives -> ONE multi-material mesh object
# ----------------------------------------------------------------------------
_BOX_PTS = [(-1, -1, -1), (1, -1, -1), (1, 1, -1), (-1, 1, -1),
            (-1, -1, 1), (1, -1, 1), (1, 1, 1), (-1, 1, 1)]
_BOX_FACES = [(0, 1, 2, 3), (7, 6, 5, 4), (0, 4, 5, 1), (1, 5, 6, 2),
              (2, 6, 7, 3), (3, 7, 4, 0)]


class MeshAcc:
    """Accumulate primitives (local space), then emit one mesh object."""

    def __init__(self):
        self.verts = []
        self.faces = []          # (indices tuple, material index)
        self.mats = []
        self._t = Matrix.Identity(4)
        self.tris = 0

    # -- transform -----------------------------------------------------------
    def at(self, loc, yaw=0.0):
        self._t = Matrix.Translation(Vector(loc)) @ Matrix.Rotation(yaw, 4, "Z")
        return self

    # -- internals -----------------------------------------------------------
    def _add(self, verts, faces, mat):
        base = len(self.verts)
        t = self._t
        for v in verts:
            w = t @ Vector((v[0], v[1], v[2]))
            self.verts.append((w.x, w.y, w.z))
        if mat not in self.mats:
            self.mats.append(mat)
        mi = self.mats.index(mat)
        for f in faces:
            self.faces.append((tuple(base + i for i in f), mi))
            self.tris += max(0, len(f) - 2)

    # -- primitives ----------------------------------------------------------
    def box(self, center, size, mat, rot=(0.0, 0.0, 0.0)):
        m = Euler(rot, "XYZ").to_matrix()
        c = Vector(center)
        hx, hy, hz = size[0] / 2.0, size[1] / 2.0, size[2] / 2.0
        pts = [c + m @ Vector((x * hx, y * hy, z * hz)) for x, y, z in _BOX_PTS]
        self._add(pts, _BOX_FACES, mat)

    def box_mm(self, x0, y0, z0, x1, y1, z1, mat):
        self.box(((x0 + x1) / 2, (y0 + y1) / 2, (z0 + z1) / 2),
                 (abs(x1 - x0), abs(y1 - y0), abs(z1 - z0)), mat)

    def cyl(self, center, radius, depth, mat, rot=(0.0, 0.0, 0.0), segs=10):
        m = Euler(rot, "XYZ").to_matrix()
        c = Vector(center)
        pts = []
        for sign in (-1, 1):
            for k in range(segs):
                a = TAU * k / segs
                pts.append(c + m @ Vector((math.cos(a) * radius,
                                           math.sin(a) * radius,
                                           sign * depth / 2.0)))
        faces = []
        for k in range(segs):
            j = (k + 1) % segs
            faces.append((k, j, segs + j, segs + k))
        faces.append(tuple(range(segs - 1, -1, -1)))
        faces.append(tuple(range(segs, 2 * segs)))
        self._add(pts, faces, mat)

    def tube(self, p0, p1, radius, mat, segs=5):
        p0, p1 = Vector(p0), Vector(p1)
        d = p1 - p0
        if d.length < 1e-6:
            return
        dn = d.normalized()
        up = Vector((0, 0, 1))
        if abs(dn.dot(up)) > 0.985:
            up = Vector((0, 1, 0))
        u = dn.cross(up).normalized()
        v = dn.cross(u).normalized()
        pts = []
        for p in (p0, p1):
            for k in range(segs):
                a = TAU * k / segs
                pts.append(p + (u * math.cos(a) + v * math.sin(a)) * radius)
        faces = []
        for k in range(segs):
            j = (k + 1) % segs
            faces.append((k, j, segs + j, segs + k))
        faces.append(tuple(range(segs - 1, -1, -1)))
        faces.append(tuple(range(segs, 2 * segs)))
        self._add(pts, faces, mat)

    def prism_xz(self, pts, y0, y1, mat):
        n = len(pts)
        verts = [(x, y0, z) for x, z in pts] + [(x, y1, z) for x, z in pts]
        faces = []
        for i in range(n):
            j = (i + 1) % n
            faces.append((i, j, n + j, n + i))
        faces.append(tuple(range(n)))
        faces.append(tuple(range(n, 2 * n)))
        self._add(verts, faces, mat)

    def prism_xy(self, pts, z0, z1, mat):
        n = len(pts)
        verts = [(x, y, z0) for x, y in pts] + [(x, y, z1) for x, y in pts]
        faces = []
        for i in range(n):
            j = (i + 1) % n
            faces.append((i, j, n + j, n + i))
        faces.append(tuple(range(n)))
        faces.append(tuple(range(n, 2 * n)))
        self._add(verts, faces, mat)

    def wire(self, p0, p1, sag, mat, radius=0.012, segs=5):
        p0, p1 = Vector(p0), Vector(p1)
        prev = p0
        for i in range(1, segs + 1):
            t = i / segs
            p = p0.lerp(p1, t)
            p.z -= sag * math.sin(math.pi * t)
            self.tube(prev, p, radius, mat, segs=4)
            prev = p

    # -- emit ----------------------------------------------------------------
    def finish(self, parent, name, kind=None):
        me = bpy.data.meshes.new(name)
        me.from_pydata(self.verts, [], [f for f, _m in self.faces])
        me.update()
        for m in self.mats:
            me.materials.append(m)
        for poly, (_f, mi) in zip(me.polygons, self.faces):
            poly.material_index = mi
        bm = bmesh.new()
        bm.from_mesh(me)
        bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
        bm.to_mesh(me)
        bm.free()
        me.update()
        obj = bpy.data.objects.new(name, me)
        coll = (parent.users_collection[0]
                if parent is not None and parent.users_collection
                else bpy.context.scene.collection)
        coll.objects.link(obj)
        if parent is not None:
            obj.parent = parent
        if kind:
            obj["sbh_veh"] = kind
        obj["sbh_tris"] = self.tris
        return obj


# ----------------------------------------------------------------------------
# shared wheel / wear helpers
# ----------------------------------------------------------------------------
def _wheel(acc, x, y, r, width, flat=False):
    rr = r * 0.70 if flat else r
    z = rr + (0.0 if not flat else 0.0)
    acc.cyl((x, y, z), rr, width, tire(), rot=(RAD(90), 0, 0), segs=10)
    if not flat:
        acc.cyl((x, y, z), rr * 0.52, width + 0.02, hub(),
                rot=(RAD(90), 0, 0), segs=8)
        acc.cyl((x, y, z), rr * 0.20, width + 0.05, chrome(),
                rot=(RAD(90), 0, 0), segs=8)
    else:
        acc.cyl((x, y, z), rr * 0.50, width + 0.01, hub(),
                rot=(RAD(90), 0, 0), segs=8)


def _rust_patches(acc, rng, L, W, belt, count=5):
    for _i in range(count):
        side = -1 if rng.random() < 0.5 else 1
        px = rng.uniform(-L * 0.38, L * 0.38)
        pz = rng.uniform(0.36, belt - 0.14)
        sw = rng.uniform(0.18, 0.46)
        sh = rng.uniform(0.09, 0.22)
        acc.box((px, side * (W / 2 + 0.004), pz), (sw, 0.008, sh), rust())


def _plate_pair(acc, x_front, x_rear, z=0.46):
    acc.box((x_front + 0.015, 0.0, z), (0.03, 0.30, 0.11), plate())
    acc.box((x_rear - 0.015, 0.0, z), (0.03, 0.30, 0.11), plate())


# ----------------------------------------------------------------------------
# sedans: VAZ-2101 / GAZ-24 / Moskvich-412
# ----------------------------------------------------------------------------
SEDANS = {
    "vaz2101": dict(L=4.10, W=1.60, H=1.46, belt=0.88, wheel_r=0.29, tire_w=0.17,
                    wb=2.42, front_over=1.43, rear_over=1.13,
                    grille_w=0.64, grille_z=0.70, bars=3, label="VAZ-2101"),
    "gaz24": dict(L=4.735, W=1.80, H=1.50, belt=0.92, wheel_r=0.31, tire_w=0.19,
                  wb=2.80, front_over=1.66, rear_over=1.28,
                  grille_w=1.16, grille_z=0.72, bars=5, label="GAZ-24"),
    "moskvich412": dict(L=4.25, W=1.55, H=1.48, belt=0.90, wheel_r=0.29,
                        tire_w=0.16, wb=2.40, front_over=1.48, rear_over=1.16,
                        grille_w=0.70, grille_z=0.71, bars=4, label="Moskvich-412"),
}


def _sedan(acc, rng, spec, body, wreck=None, roof_rack=False):
    L, W, H = spec["L"], spec["W"], spec["H"]
    belt, wr, tw, wb = spec["belt"], spec["wheel_r"], spec["tire_w"], spec["wb"]
    x0, x1 = -L / 2.0, L / 2.0
    ax_f, ax_r = wb / 2.0, -wb / 2.0
    sill = 0.30
    arch = wr + 0.07

    def arch_pts(ax):
        return [(ax - arch - 0.05, sill), (ax - arch, sill + 0.10),
                (ax - arch * 0.60, sill + arch * 0.74),
                (ax, sill + arch * 0.90),
                (ax + arch * 0.60, sill + arch * 0.74),
                (ax + arch, sill + 0.10), (ax + arch + 0.05, sill)]

    prof = [(x0, sill)]
    prof += arch_pts(ax_r)
    prof += arch_pts(ax_f)
    prof += [(x1, sill), (x1, 0.54), (x1 - 0.03, belt - 0.14),
             (x1 - spec["front_over"], belt),
             (x0 + spec["rear_over"], belt),
             (x0 + spec["rear_over"] * 0.52, belt - 0.07),
             (x0 + 0.06, belt - 0.16), (x0, 0.55)]
    acc.prism_xz(prof, -W / 2.0, W / 2.0, body)

    # greenhouse (dark glass trapezoid) + painted roof + pillars
    gw = W - 0.12
    gx_ws = x1 - spec["front_over"]
    gx_rw = x0 + spec["rear_over"]
    gh = H - 0.04
    roof_f = gx_ws - 0.30
    roof_r = gx_rw + 0.30
    acc.prism_xz([(gx_ws, belt - 0.02), (roof_f, gh), (roof_r, gh),
                  (gx_rw, belt - 0.02)], -gw / 2.0, gw / 2.0, glass())
    acc.box(((roof_f + roof_r) / 2.0, 0.0, gh + 0.026),
            (abs(roof_f - roof_r) + 0.06, gw + 0.06, 0.055), body)
    for side in (-1, 1):
        y = side * (gw / 2.0 + 0.005)
        # A pillar
        dx, dz = roof_f - gx_ws, gh - belt
        ln = math.hypot(dx, dz)
        acc.box(((gx_ws + roof_f) / 2.0, y, (belt + gh) / 2.0),
                (0.06, 0.05, ln), body, rot=(0, math.atan2(dx, dz), 0))
        # C pillar
        dx, dz = roof_r - gx_rw, gh - belt
        ln = math.hypot(dx, dz)
        acc.box(((gx_rw + roof_r) / 2.0, y, (belt + gh) / 2.0),
                (0.07, 0.05, ln), body, rot=(0, math.atan2(dx, dz), 0))
        # B pillar
        axb = (gx_ws + gx_rw) / 2.0 + 0.05
        acc.box((axb, y, (belt + gh) / 2.0), (0.055, 0.05, gh - belt + 0.02), body)
        # chrome window surround
        acc.box(((gx_ws + gx_rw) / 2.0, side * (gw / 2.0 + 0.012), belt + 0.035),
                (abs(gx_ws - gx_rw), 0.02, 0.025), chrome())
        # door seams + handles
        for sx in (gx_ws - 0.22, axb - 0.55, gx_rw + 0.30):
            acc.box((sx, side * (W / 2.0 + 0.004), (sill + belt) / 2.0 + 0.02),
                    (0.012, 0.008, belt - sill - 0.10), dark())
        for hx in (gx_ws - 0.42, gx_rw + 0.55):
            acc.box((hx, side * (W / 2.0 + 0.012), belt - 0.11),
                    (0.15, 0.022, 0.032), chrome())

    # bumpers + grille + lamps + plates
    acc.box((x1 - 0.02, 0.0, 0.455), (0.12, W - 0.04, 0.13), chrome())
    acc.box((x0 + 0.02, 0.0, 0.455), (0.12, W - 0.04, 0.13), chrome())
    gy = spec["grille_w"]
    acc.box((x1 - 0.012, 0.0, spec["grille_z"]), (0.035, gy, 0.21), dark())
    for bi in range(spec["bars"]):
        bz = spec["grille_z"] - 0.075 + bi * (0.15 / max(1, spec["bars"] - 1))
        acc.box((x1 - 0.002, 0.0, bz), (0.02, gy - 0.06, 0.018), chrome())
    for side in (-1, 1):
        hy = side * (W / 2.0 - 0.26)
        acc.cyl((x1 - 0.012, hy, spec["grille_z"]), 0.115, 0.035, chrome(),
                rot=(0, RAD(90), 0), segs=10)
        acc.cyl((x1 + 0.004, hy, spec["grille_z"]), 0.098, 0.03, headlamp(),
                rot=(0, RAD(90), 0), segs=10)
        acc.box((x1 + 0.005, side * (W / 2.0 - 0.04), spec["grille_z"] - 0.18),
                (0.03, 0.16, 0.09), amber())
        acc.box((x0 - 0.005, side * (W / 2.0 - 0.20), belt - 0.14),
                (0.03, 0.26, 0.12), taillamp())
    _plate_pair(acc, x1, x0)
    # one driver-side mirror (period correct)
    acc.box((gx_ws - 0.12, gw / 2.0 + 0.06, belt + 0.20), (0.16, 0.02, 0.02), trim())
    acc.box((gx_ws - 0.18, gw / 2.0 + 0.10, belt + 0.22), (0.10, 0.035, 0.10), chrome())
    # underbody + wheel wells
    acc.box((0.0, 0.0, 0.36), (L * 0.86, W * 0.60, 0.16), dark())
    for ax in (ax_f, ax_r):
        acc.box((ax, 0.0, wr + 0.10), (0.92, W * 0.94, 0.34), dark())
    # wheels
    flat_at = wreck.get("flat") if wreck else None
    missing_at = wreck.get("missing") if wreck else None
    for ax, tag in ((ax_f, "f"), (ax_r, "r")):
        for side, s in ((1, "l"), (-1, "r")):
            if missing_at == tag + s:
                acc.tube((ax, side * (W / 2.0 - 0.06), wr),
                         (ax, side * (W / 2.0 + 0.02), wr), 0.055, hub(), segs=6)
                acc.box((ax, side * (W / 2.0 - 0.02), 0.10), (0.42, 0.22, 0.10), dark())
                acc.box((ax - 0.15, side * (W / 2.0 + 0.02), 0.13),
                        (0.24, 0.20, 0.26), poleconc())
                continue
            _wheel(acc, ax, side * (W / 2.0 - 0.09), wr, tw,
                   flat=(flat_at == tag + s))
    if wreck:
        _rust_patches(acc, rng, L, W, belt, count=6)
        acc.box((0.2, 0.0, gh + 0.02), (0.6, 0.5, 0.02), rust())
    if roof_rack:
        rl = abs(roof_f - roof_r) * 0.82
        for side in (-1, 1):
            acc.box(((roof_f + roof_r) / 2.0, side * (gw / 2.0 - 0.13), gh + 0.10),
                    (rl, 0.035, 0.035), trim())
        for i in range(3):
            rx = (roof_f + roof_r) / 2.0 - rl / 2.0 + rl * i / 2.0
            acc.box((rx, 0.0, gh + 0.11), (0.035, gw - 0.20, 0.03), trim())


# ----------------------------------------------------------------------------
# UAZ-469 jeep
# ----------------------------------------------------------------------------
def _uaz(acc, rng, body, wreck=None):
    L, W = 4.20, 1.78
    wr, tw = 0.40, 0.22
    x0, x1 = -L / 2.0, L / 2.0
    ax_f, ax_r = 1.40, -1.30
    # frame / chassis
    acc.box((0.0, 0.0, 0.66), (L - 0.35, 1.15, 0.16), dark())
    # body tub (rear cargo) + cowl + hood
    acc.box((-0.55, 0.0, 1.02), (2.55, 1.76, 0.60), body)
    acc.box((0.95, 0.0, 1.05), (1.05, 1.56, 0.40), body)          # hood
    acc.box((1.55, 0.0, 1.04), (0.42, 1.70, 0.50), body)          # front clip
    acc.box((2.02, 0.0, 1.04), (0.10, 1.74, 0.52), body)          # grille panel
    for side in (-1, 1):
        acc.box((1.45, side * 0.86, 1.02), (1.35, 0.18, 0.34), body)   # front fender
        acc.box((-1.28, side * 0.86, 1.02), (1.15, 0.18, 0.32), body)  # rear fender
        acc.box((0.15, side * 0.91, 0.55), (1.45, 0.14, 0.06), trim())  # running board
    # front grille slats + round headlights
    acc.box((2.075, 0.0, 1.04), (0.03, 1.06, 0.34), dark())
    for i in range(6):
        gy = -0.44 + i * 0.176
        acc.box((2.09, gy, 1.04), (0.02, 0.035, 0.32), chrome())
    for side in (-1, 1):
        hy = side * 0.72
        acc.cyl((2.10, hy, 1.12), 0.105, 0.04, headlamp(), rot=(0, RAD(90), 0), segs=8)
        acc.cyl((2.08, hy, 1.12), 0.125, 0.03, chrome(), rot=(0, RAD(90), 0), segs=8)
        acc.box((2.06, side * 0.55, 0.86), (0.03, 0.16, 0.10), amber())
        acc.box((-2.06, side * 0.70, 0.98), (0.03, 0.14, 0.12), taillamp())
    # bumpers
    acc.box((2.14, 0.0, 0.66), (0.14, 1.94, 0.17), trim())
    acc.box((-2.12, 0.0, 0.66), (0.14, 1.86, 0.17), trim())
    # windshield (leaned back a little) + frame
    acc.box((0.80, 0.0, 1.62), (0.035, 1.52, 0.62), glass(),
            rot=(0, RAD(-10), 0))
    for dy, dz, sy, sz in ((0, 0.34, 1.60, 0.06), (0, -0.34, 1.60, 0.06),
                           (0.79, 0, 0.06, 0.70), (-0.79, 0, 0.06, 0.70)):
        acc.box((0.82, dy, 1.62 + dz * 0.5), (0.06, sy, sz), body, rot=(0, RAD(-10), 0))
    # door windows + pillars (cargo area behind is canvas-sided)
    for side in (-1, 1):
        sy = side * 0.885
        acc.box((0.36, sy, 1.52), (0.62, 0.03, 0.44), glass())
        acc.box((-0.06, sy, 1.52), (0.09, 0.045, 0.50), body)      # B pillar
        acc.box((0.68, sy, 1.52), (0.08, 0.045, 0.50), body)      # A pillar base
        acc.box((0.05, side * (W / 2 + 0.005), 1.10), (0.012, 0.008, 0.55), dark())
        acc.box((0.05, side * (W / 2 + 0.012), 1.20), (0.13, 0.02, 0.03), trim())
    # canvas top over cargo area
    acc.box((-0.92, 0.0, 1.93), (1.95, 1.74, 0.14), canvas())
    for side in (-1, 1):
        acc.box((-0.92, side * 0.875, 1.61), (1.92, 0.035, 0.56), canvas())
        acc.box((-1.28, side * 0.90, 1.60), (0.55, 0.02, 0.24), glass())
        acc.box((-0.52, side * 0.90, 1.60), (0.46, 0.02, 0.22), glass())
    for i in range(3):
        acc.box((-1.72 + i * 0.68, 0.0, 1.99), (0.06, 1.72, 0.03), trim())
    # rear panel with spare wheel on the back
    acc.box((-1.88, 0.0, 1.00), (0.06, 1.70, 0.56), body)
    acc.cyl((-2.02, 0.0, 1.02), 0.38, 0.18, tire(), rot=(RAD(90), 0, 0), segs=10)
    acc.cyl((-2.12, 0.0, 1.02), 0.19, 0.05, hub(), rot=(RAD(90), 0, 0), segs=8)
    acc.box((-1.97, 0.0, 1.02), (0.05, 0.78, 0.10), trim())
    # mirrors, tow hooks, exhaust
    for side in (-1, 1):
        acc.box((0.86, side * 1.00, 1.55), (0.03, 0.30, 0.03), trim())
        acc.box((0.86, side * 1.12, 1.62), (0.05, 0.10, 0.16), chrome())
    acc.cyl((-2.14, -0.55, 0.45), 0.05, 0.30, trim(), rot=(0, RAD(90), 0), segs=6)
    # wheels
    flat_at = wreck.get("flat") if wreck else None
    for ax, tag in ((ax_f, "f"), (ax_r, "r")):
        for side, s in ((1, "l"), (-1, "r")):
            _wheel(acc, ax, side * 0.83, wr, tw, flat=(flat_at == tag + s))
    if wreck:
        _rust_patches(acc, rng, L, W, 1.15, count=6)


# ----------------------------------------------------------------------------
# ZIL-130 truck (cab + flatbed with side rails)
# ----------------------------------------------------------------------------
def _zil(acc, rng, body):
    L = 6.6
    x0, x1 = -L / 2.0, L / 2.0
    wr = 0.52
    # chassis rails + bumper
    for side in (-1, 1):
        acc.box((0.0, side * 0.46, 0.72), (L - 0.30, 0.16, 0.20), dark())
    acc.box((3.26, 0.0, 0.62), (0.16, 2.46, 0.22), trim())
    acc.box((-3.32, 0.0, 0.86), (0.14, 2.40, 0.20), trim())
    # cab
    acc.box((2.40, 0.0, 1.58), (1.32, 2.20, 1.48), body)
    acc.box((3.06, 0.0, 1.46), (0.16, 2.16, 1.30), body)
    acc.box((2.40, 0.0, 2.33), (1.36, 2.22, 0.06), body)
    # grille + bars + headlights
    acc.box((3.15, 0.0, 1.42), (0.05, 1.32, 0.72), dark())
    for i in range(3):
        acc.box((3.17, 0.0, 1.14 + i * 0.26), (0.03, 1.26, 0.05), chrome())
    for side in (-1, 1):
        acc.cyl((3.18, side * 0.86, 1.06), 0.135, 0.05, chrome(),
                rot=(0, RAD(90), 0), segs=10)
        acc.cyl((3.20, side * 0.86, 1.06), 0.115, 0.04, headlamp(),
                rot=(0, RAD(90), 0), segs=10)
        acc.box((3.16, side * 1.06, 0.86), (0.03, 0.16, 0.10), amber())
        # front fender
        acc.box((2.62, side * 1.16, 0.96), (1.20, 0.24, 0.42), body)
        acc.box((2.30, side * 1.22, 0.56), (0.70, 0.20, 0.12), trim())   # step
        acc.box((2.40, side * 1.12, 1.68), (0.06, 0.06, 0.46), trim())  # mirror arm
        acc.box((2.40, side * 1.28, 1.92), (0.05, 0.20, 0.26), chrome())
    # windscreen + side windows
    acc.box((3.02, 0.0, 1.88), (0.04, 1.80, 0.70), glass(), rot=(0, RAD(-6), 0))
    for side in (-1, 1):
        acc.box((2.36, side * 1.105, 1.86), (0.80, 0.03, 0.56), glass())
        acc.box((2.36, side * 1.12, 1.46), (0.72, 0.05, 0.20), body)   # door upper
        acc.box((2.36, side * 1.12, 1.05), (1.26, 0.05, 0.62), body)   # door lower
        acc.box((2.36, side * 1.145, 1.14), (0.14, 0.02, 0.03), chrome())
        acc.box((1.76, side * 1.105, 1.86), (0.06, 0.03, 0.56), body)  # B pillar
    # roof details
    acc.box((2.35, 0.0, 2.38), (0.9, 1.3, 0.08), body)
    # flatbed: floor + side boards + stakes + tailgate + headboard
    acc.box((-0.97, 0.0, 1.02), (4.36, 2.50, 0.14), wood())
    acc.box((1.20, 0.0, 1.38), (0.08, 2.50, 0.86), bedmetal())
    for side in (-1, 1):
        for zz in (1.20, 1.40, 1.60):
            acc.box((-0.97, side * 1.25, zz), (4.30, 0.055, 0.15), bedmetal())
        for sx in (-3.02, -1.95, -0.88, 0.20):
            acc.box((sx, side * 1.25, 1.24), (0.10, 0.10, 0.58), bedmetal())
    for zz in (1.20, 1.40, 1.60):
        acc.box((-3.13, 0.0, zz), (0.055, 2.50, 0.15), bedmetal())
    for sx in (-3.13,):
        acc.box((sx, 0.0, 1.24), (0.10, 2.44, 0.58), bedmetal())
    # cargo (crates)
    acc.box((-2.55, 0.55, 1.32), (0.72, 0.72, 0.72), wood())
    acc.box((-1.72, -0.35, 1.28), (0.66, 0.66, 0.62), wood())
    acc.box((-2.30, -0.62, 1.22), (0.52, 0.52, 0.50), bedmetal())
    # dual rear wheels + front wheels
    for side in (-1, 1):
        for ay in ((0.84, 1.10) if side > 0 else (-1.10, -0.84)):
            _wheel(acc, -1.55, ay, wr, 0.24)
        _wheel(acc, 2.55, side * 1.08, wr, 0.28)
        acc.box((-2.15, side * 1.00, 0.30), (0.05, 0.36, 0.42), dark())  # mudflap
    # fuel tank + exhaust
    acc.cyl((0.20, -1.06, 0.74), 0.25, 1.10, trim(), rot=(0, RAD(90), 0), segs=10)
    acc.cyl((-2.95, 0.60, 0.42), 0.055, 0.8, trim(), rot=(0, RAD(90), 0), segs=6)
    _plate_pair(acc, 3.28, -3.30, z=0.50)


# ----------------------------------------------------------------------------
# LAZ-695 bus
# ----------------------------------------------------------------------------
def _bus(acc, rng, stopped=False):
    L, W = 10.5, 2.5
    x0, x1 = -L / 2.0, L / 2.0
    wr = 0.50
    ax_f, ax_r = 3.20, -3.00
    # lower body: segmented so the wheel arches are real openings (wheels visible)
    lz0, lz1, arch_h, arch_top = 0.50, 1.75, 0.92, 1.08
    spans = [(x0 + 0.06, ax_r - arch_h), (ax_r + arch_h, ax_f - arch_h),
             (ax_f + arch_h, x1 - 0.06)]
    for sx0, sx1 in spans:
        acc.box(((sx0 + sx1) / 2.0, 0.0, (lz0 + lz1) / 2.0),
                (sx1 - sx0, W - 0.04, lz1 - lz0), buscream())
    for ax in (ax_f, ax_r):
        acc.box((ax, 0.0, (arch_top + lz1) / 2.0), (arch_h * 2.0, W - 0.04,
                                                   lz1 - arch_top), buscream())
        for side in (-1, 1):
            acc.box((ax, side * (W / 2.0 - 0.16), 0.70), (arch_h * 2.0 - 0.10,
                                                          0.14, 0.46), dark())
    # blue livery band + cream skirt trim
    acc.box((0.0, 0.0, 1.72), (L - 0.10, W - 0.02, 0.30), busblue())
    acc.box((0.0, 0.0, 0.56), (L - 0.14, W - 0.06, 0.10), trim())
    # continuous dark glass band, then painted pillars
    acc.box((0.0, 0.0, 2.24), (L - 0.22, W - 0.12, 0.90), glass())
    n_win = 12
    ww = ((L - 0.55) - 0.60) / n_win
    # pillars: n_win+1 verticals each side
    for i in range(n_win + 1):
        px = -((L - 0.55) / 2.0) + 0.30 + i * ww
        for side in (-1, 1):
            acc.box((px, side * (W / 2.0 - 0.045), 2.24), (0.10, 0.03, 0.92),
                    buscream())
    # lit / dark window hint: 2 dim lit panes on the right side near doors
    for xl in (3.1, -0.2):
        acc.box((xl, -(W / 2.0 - 0.055), 2.22), (0.78, 0.02, 0.72), glass_lit())
    # roof (slightly domed by chamfer prism)
    acc.box((0.0, 0.0, 2.80), (L - 0.30, W - 0.30, 0.18), buscream())
    acc.prism_xy([(-(L - 0.55) / 2.0, -(W - 0.55) / 2.0),
                  ((L - 0.55) / 2.0, -(W - 0.55) / 2.0),
                  ((L - 0.55) / 2.0, (W - 0.55) / 2.0),
                  (-(L - 0.55) / 2.0, (W - 0.55) / 2.0)], 2.88, 3.00, buscream())
    for i in range(3):
        acc.box((-3.4 + i * 3.2, 0.0, 3.03), (1.1, 0.7, 0.10), trim())
    # windscreen (2 panes) with painted frame + destination board above
    for side in (-1, 1):
        acc.box((x1 - 0.155, side * 0.55, 2.06), (0.04, 1.0, 0.84), glass(),
                rot=(0, RAD(-7), 0))
    acc.box((x1 - 0.15, 0.0, 2.06), (0.07, 0.12, 0.90), buscream())
    for side in (-1, 1):
        acc.box((x1 - 0.14, side * 1.06, 2.06), (0.10, 0.12, 0.92), buscream())
        acc.box((x1 - 0.13, side * 0.55, 2.50), (0.10, 1.06, 0.08), buscream())
        acc.box((x1 - 0.13, side * 0.55, 1.62), (0.10, 1.06, 0.08), buscream())
    acc.box((x1 - 0.02, 0.0, 2.70), (0.06, 2.10, 0.26), dark())
    # front face: bumper, grille, headlights
    acc.box((x1 + 0.02, 0.0, 0.68), (0.16, W - 0.04, 0.24), trim())
    acc.box((x1 - 0.02, 0.0, 1.10), (0.06, 0.92, 0.34), dark())
    for side in (-1, 1):
        acc.cyl((x1 + 0.02, side * 0.82, 1.18), 0.115, 0.05, headlamp(),
                rot=(0, RAD(90), 0), segs=10)
        acc.cyl((x1, side * 0.82, 1.18), 0.14, 0.04, chrome(),
                rot=(0, RAD(90), 0), segs=10)
        acc.box((x1 + 0.03, side * 1.10, 0.92), (0.05, 0.16, 0.12), amber())
    # rear: window + engine vents + bumper + lights
    acc.box((x0 + 0.10, 0.0, 2.28), (0.04, 1.70, 0.82), glass())
    for i in range(4):
        acc.box((x0 + 0.10, 0.0, 1.32 + i * 0.16), (0.05, 1.90, 0.08), trim())
    acc.box((x0 - 0.02, 0.0, 0.68), (0.16, W - 0.10, 0.22), trim())
    for side in (-1, 1):
        acc.box((x0 + 0.05, side * 0.94, 1.02), (0.05, 0.20, 0.16), taillamp())
    # doors on the right side (local -Y): front door ahead of the axle, rear door behind
    for dx in (4.52, -4.42):
        dy = -(W / 2.0 + 0.005)
        acc.box((dx, dy, 1.30), (1.34, 0.05, 0.66), buscream())
        for leaf in (-0.34, 0.34):
            acc.box((dx + leaf, dy - 0.02, 2.10), (0.58, 0.04, 0.90), glass())
            acc.box((dx + leaf, dy - 0.03, 2.10), (0.60, 0.05, 0.94), trim())
            acc.box((dx + leaf, dy - 0.045, 2.10), (0.54, 0.05, 0.84), glass())
        acc.box((dx, dy - 0.05, 2.10), (0.07, 0.06, 0.94), buscream())
        acc.box((dx, dy - 0.045, 1.62), (1.30, 0.06, 0.07), buscream())
        acc.box((dx, dy - 0.045, 2.58), (1.30, 0.06, 0.07), buscream())
        acc.box((dx, dy - 0.32, 0.46), (1.10, 0.62, 0.10), trim())   # folding step
    # wheels standing proud of the body side (arches are real openings)
    for ax in (ax_f, ax_r):
        for side in (-1, 1):
            _wheel(acc, ax, side * 1.12, wr, 0.36)
    for side in (-1, 1):
        acc.box((ax_r - 0.55, side * 1.00, 0.34), (0.05, 0.40, 0.42), dark())  # mudflap
    # interior seat blocks (read through the lit panes)
    for i in range(5):
        for side in (-1, 1):
            acc.box((-4.2 + i * 2.1, side * 0.70, 1.32), (1.5, 0.42, 0.44), dark())
    # mirrors + plates
    for side in (-1, 1):
        acc.box((x1 - 0.35, side * 1.34, 2.05), (0.06, 0.42, 0.05), trim())
        acc.box((x1 - 0.30, side * 1.52, 1.98), (0.05, 0.16, 0.30), dark())
    _plate_pair(acc, x1 + 0.01, x0 - 0.01, z=0.62)


# ----------------------------------------------------------------------------
# Tatra-T3 tram car
# ----------------------------------------------------------------------------
TRAM_FP = [(7.50, -0.95), (7.42, -1.20), (7.20, -1.25), (-7.20, -1.25),
           (-7.42, -1.20), (-7.50, -0.95), (-7.50, 0.95), (-7.42, 1.20),
           (-7.20, 1.25), (7.20, 1.25), (7.42, 1.20), (7.50, 0.95)]


def _tram(acc, rng):
    # body shell (cream) + faded red skirt + domed roof
    acc.prism_xy(TRAM_FP, 0.70, 2.88, tramcream())
    red_fp = [(x * 1.004, y * 1.004) for x, y in TRAM_FP]
    acc.prism_xy(red_fp, 0.62, 1.42, tramred())
    roof_fp = [(x * 0.955, y * 0.93) for x, y in TRAM_FP]
    acc.prism_xy(roof_fp, 2.88, 3.05, tramcream())
    # underframe (kept narrow so the wheels stay visible from the side)
    acc.box((0.0, 0.0, 0.64), (13.6, 1.45, 0.18), dark())
    # window band with pillars (11 windows per side)
    x_start, x_end = -6.85, 6.85
    n = 11
    win_w = (x_end - x_start) / n - 0.15
    lit_x = (2.15, -1.55)
    for i in range(n):
        xc = x_start + (x_end - x_start) * (i + 0.5) / n
        is_lit = any(abs(xc - lx) < 0.62 for lx in lit_x)
        for side in (-1, 1):
            mat = glass_lit() if (is_lit and side == -1) else glass()
            acc.box((xc, side * 1.252, 2.13), (win_w, 0.016, 0.94), mat)
    for i in range(n + 1):
        px = x_start + (x_end - x_start) * i / n - 0.075
        for side in (-1, 1):
            acc.box((px, side * 1.262, 2.13), (0.13, 0.026, 0.98), tramcream())
    # windscreens (2 panes each end) + centre pillar + wipers
    for xf in (7.512, -7.512):
        for side in (-1, 1):
            acc.box((xf, side * 0.47, 2.16), (0.018, 0.86, 0.92), glass())
        acc.box((xf, 0.0, 2.16), (0.022, 0.10, 0.96), tramcream())
        acc.box((xf, 0.0, 1.64), (0.03, 2.10, 0.10), tramcream())
    # destination boards + route box
    acc.box((7.53, 0.0, 2.72), (0.06, 1.55, 0.30), dark())
    acc.box((-7.53, 0.0, 2.72), (0.06, 1.55, 0.30), dark())
    # headlights / tail lights / bumpers / cowcatcher
    for side in (-1, 1):
        acc.cyl((7.55, side * 0.72, 1.28), 0.085, 0.05, headlamp(),
                rot=(0, RAD(90), 0), segs=8)
        acc.box((-7.55, side * 0.80, 1.16), (0.04, 0.14, 0.22), taillamp())
    for xf in (7.56, -7.56):
        acc.box((xf, 0.0, 0.62), (0.16, 2.24, 0.30), dark())
        acc.box((xf * 0.995, 0.0, 0.90), (0.10, 2.30, 0.16), tramred())
    # doors on the platform side (local -Y): 3 double doors with dark frames
    for dx in (4.85, 0.45, -3.85):
        dy = -1.258
        # dark rubber frame / recess
        acc.box((dx, dy, 1.78), (1.40, 0.05, 1.62), trim())
        acc.box((dx, dy - 0.03, 1.25), (1.30, 0.05, 0.56), tramred())
        for leaf in (-0.33, 0.33):
            acc.box((dx + leaf, dy - 0.05, 2.10), (0.58, 0.05, 0.90), glass())
        acc.box((dx, dy - 0.06, 2.10), (0.07, 0.06, 0.94), tramcream())
        acc.box((dx, dy - 0.06, 2.58), (1.30, 0.07, 0.08), tramcream())
        acc.box((dx, dy - 0.06, 1.55), (1.30, 0.07, 0.08), tramcream())
        acc.box((dx, -1.10, 0.40), (1.10, 0.52, 0.09), trim())      # folding step
        for hx in (-0.64, 0.64):
            acc.tube((dx + hx, -1.30, 1.52), (dx + hx, -1.30, 2.56), 0.022,
                     chrome(), segs=4)
    # bogies (2 x two axles) + wheels visible below the body
    for bx in (4.5, -4.5):
        acc.box((bx, 0.0, 0.62), (2.75, 1.30, 0.34), dark())
        acc.box((bx, 0.0, 0.84), (1.9, 1.05, 0.20), trim())
        for ax_off in (-1.0, 1.0):
            ax = bx + ax_off
            for side in (-1, 1):
                _wheel(acc, ax, side * 0.762, 0.35, 0.14)
                acc.box((ax, side * 0.60, 0.60), (0.26, 0.18, 0.26), trim())
        for side in (-1, 1):
            acc.cyl((bx + 0.9, side * 0.55, 0.55), 0.10, 0.26, trim(),
                    rot=(0, RAD(90), 0), segs=8)
            acc.box((bx, side * 0.85, 0.86), (2.5, 0.12, 0.10), trim())
    # roof equipment + pantograph (single-arm, contact bar at z=5.6)
    for i, rx in enumerate((-1.2, 1.6, 4.0)):
        acc.box((rx, 0.0, 3.10), (0.85, 0.62, 0.12), trim())
    px0 = -3.55
    for side in (-1, 1):
        for dy in (-0.42, 0.42):
            acc.cyl((px0, dy * 0.7, 3.14), 0.055, 0.16, insulator(), segs=6)
    acc.box((px0, 0.0, 3.26), (0.95, 1.05, 0.10), trim())
    # lower arm + upper arm (slight bend) reaching z=5.54
    acc.tube((px0 + 0.35, 0.0, 3.32), (px0 - 0.55, 0.0, 4.55), 0.035, trim(), segs=5)
    acc.tube((px0 - 0.55, 0.0, 4.55), (px0 + 0.10, 0.0, 5.54), 0.030, trim(), segs=5)
    acc.tube((px0 - 0.55, 0.0, 4.55), (px0 - 0.90, 0.0, 5.52), 0.020, trim(), segs=4)
    acc.box((px0 + 0.10, 0.0, 5.57), (0.12, 1.25, 0.055), trim())   # contact strip
    for side in (-1, 1):
        acc.box((px0 + 0.10, side * 0.62, 5.60), (0.06, 0.20, 0.035), chrome())
    # interior seat blocks + partition (visible through lit panes)
    for i in range(6):
        for side in (-1, 1):
            acc.box((-5.0 + i * 2.0, side * 0.72, 1.30), (1.5, 0.44, 0.42), dark())
    acc.box((3.2, 0.0, 1.55), (0.08, 2.2, 1.3), trim())


def _tram_text(parent, plan_tram):
    """Destination boards with Cyrillic route sign (separate FONT-mesh objects)."""
    tm = kit.mat_solid("VEH_TRAM_SIGN_FG", "#D8D3C2", rough=0.4,
                       emission="#CFC9B4", emission_strength=1.4)
    loc = plan_tram
    objs = []
    # front (faces -X because the tram faces west): text at world x = 15-7.58
    objs.append(kit.text3d(parent, "TRAM_dest_front", "3 ЦЕНТР",
                           (loc[0] - 7.60, loc[1], 2.72), 0.155, tm,
                           rot=(RAD(90), 0, RAD(-90))))
    objs.append(kit.text3d(parent, "TRAM_dest_rear", "3 ЦЕНТР",
                           (loc[0] + 7.60, loc[1], 2.72), 0.155, tm,
                           rot=(RAD(90), 0, RAD(90))))
    objs.append(kit.text3d(parent, "TRAM_route_side", "3",
                           (loc[0] + 5.2, loc[1] - 1.272, 2.30), 0.30, tm,
                           rot=(RAD(90), 0, RAD(0))))
    return [o for o in objs if o is not None]


def _bus_text(parent, pos, yaw):
    """Destination board above the windscreen of the stopped bus."""
    tm = kit.mat_solid("VEH_BUS_SIGN_FG", "#D9D4C2", rough=0.45,
                       emission="#CFC9B2", emission_strength=1.1)
    # front is at local +X; bus is yaw=pi -> front is -X world side
    fx = pos[0] - 5.28
    if abs(yaw) < 1.0:      # facing +X
        fx = pos[0] + 5.28
        rot = (RAD(90), 0, RAD(90))
    else:                   # facing -X
        rot = (RAD(90), 0, RAD(-90))
    return kit.text3d(parent, "VEH_bus_dest", "5 ГАСТРОНОМ",
                      (fx, pos[1], 2.55), 0.135, tm, rot=rot)


# ----------------------------------------------------------------------------
# spawn helper
# ----------------------------------------------------------------------------
def _spawn(parent, name, kind, x, y, yaw_deg, rng, paint_key="beige",
           z=0.0, wreck=None, rack=False, stopped=False):
    acc = MeshAcc().at((x, y, z), RAD(yaw_deg))
    if kind in SEDANS:
        _sedan(acc, rng, SEDANS[kind], paint(paint_key, worn=wreck is not None),
               wreck=wreck, roof_rack=rack)
    elif kind == "uaz469":
        _uaz(acc, rng, paint(paint_key, worn=wreck is not None), wreck=wreck)
    elif kind == "zil130":
        _zil(acc, rng, paint(paint_key, worn=wreck is not None))
    elif kind == "bus":
        _bus(acc, rng, stopped=stopped)
    elif kind == "tram":
        _tram(acc, rng)
    else:
        raise ValueError("unknown vehicle kind " + str(kind))
    obj = acc.finish(parent, name, kind=kind)
    return obj, acc.tris


# ----------------------------------------------------------------------------
# utility poles + wires
# ----------------------------------------------------------------------------
def _utility_pole(acc, x, y, h, wooden=False, transformer_on=False, ladder=False):
    pole_mat = polewood() if wooden else poleconc()
    if wooden:
        acc.cyl((x, y, h / 2.0), 0.145, h, pole_mat, segs=8)
        acc.box((x, y, h - 0.70), (0.11, 2.0, 0.11), polewood())
    else:
        acc.cyl((x, y, h * 0.28), 0.20, h * 0.56, pole_mat, segs=8)
        acc.cyl((x, y, h * 0.72), 0.145, h * 0.44, pole_mat, segs=8)
        acc.box((x, y, 0.17), (0.52, 0.52, 0.34), poleconc())
        acc.box((x, y, h - 0.90), (0.10, 2.10, 0.10), polemetal())
    arm_z = h - 0.70 if wooden else h - 0.90
    # insulators on the crossarm (3-phase)
    for dy in (-0.95, 0.0, 0.95):
        acc.cyl((x, y + dy, arm_z + 0.10), 0.055, 0.15, insulator(), segs=8)
    # low voltage / comms bracket
    acc.box((x, y, h - 1.85), (0.08, 0.95, 0.07), polemetal())
    for dy in (-0.42, 0.42):
        acc.cyl((x, y + dy, h - 1.80), 0.045, 0.10, insulator(), segs=6)
    if transformer_on:
        ty = y - 0.62
        acc.cyl((x, ty, h - 2.25), 0.30, 0.88, transformer(), segs=10)
        for dz in (-0.28, 0.28):
            acc.box((x, (y + ty) / 2.0, h - 2.25 + dz), (0.10, 0.52, 0.08),
                    polemetal())
        for dy in (-0.14, 0.0, 0.14):
            acc.cyl((x, ty + dy, h - 1.76), 0.045, 0.18, insulator(), segs=6)
    if ladder:
        for i in range(5):
            acc.box((x, y + 0.22, h - 2.6 + i * 0.38), (0.05, 0.24, 0.035), trim())
    return h - 0.70 + 0.16 if wooden else h - 0.90 + 0.16


def _catenary_pole(acc, x, y, track_y, side):
    h = 8.6
    acc.cyl((x, y, h / 2.0), 0.135, h, polemetal(), segs=8)
    acc.box((x, y, 0.22), (0.42, 0.42, 0.44), poleconc())
    arm_z = 7.30
    # bracket arm over the nearest track + diagonal tie
    acc.tube((x, y, arm_z), (x, track_y, arm_z), 0.045, polemetal(), segs=5)
    mid_y = (y + track_y) / 2.0
    acc.tube((x, y, 5.95), (x, mid_y, arm_z - 0.05), 0.030, polemetal(), segs=4)
    acc.tube((x, y, arm_z - 0.55), (x, mid_y, arm_z - 0.04), 0.022, polemetal(), segs=4)
    # dropper + insulator down to the contact wire
    acc.tube((x, track_y, arm_z), (x, track_y, 5.78), 0.024, polemetal(), segs=4)
    acc.cyl((x, track_y, 5.70), 0.055, 0.14, insulator(), segs=6)
    acc.box((x, y, arm_z), (0.10, 0.16, 0.10), trim())
    return h


def _service_drop(acc_w, p0, p1, sag=0.55):
    acc_w.wire(p0, p1, sag, wiremat(), radius=0.010, segs=5)


# ----------------------------------------------------------------------------
# placement
# ----------------------------------------------------------------------------
def _zone(plan, zid):
    for z in plan.get("prop_zones", []):
        if z["id"] == zid:
            return z
    return None


def _place_zone_cars(parent, plan, rng, stats):
    """Parked cars in the CARS_* prop zones (parallel rows, real scale)."""
    n = 0

    def put(kind, x, y, yaw, pk, z=0.0, wreck=None, tag=""):
        nonlocal n
        n += 1
        name = f"VEH_{kind}_{tag or str(n).zfill(2)}"
        _obj, tris = _spawn(parent, name, kind, x, y, yaw, rng, pk,
                            z=z, wreck=wreck)
        stats["cars" if kind != "zil130" else "trucks"] += 1
        stats["tris"] += tris
        stats["vehicles"] += 1

    # CARS_W — parallel row on the west parking strip (y = -67.25)
    zw = _zone(plan, "CARS_W")
    if zw:
        x0, y0, x1, y1 = zw["rect"]
        ym = (y0 + y1) / 2.0 - 0.55
        xs = [-108.0, -100.0, -92.5, -84.0, -76.0]
        kinds = ["vaz2101", "gaz24", "moskvich412", "vaz2101", "uaz469"]
        pks = ["beige", "darkgreen", "paleblue", "bluegrey", "olive"]
        yaws = [180, 0, 180, 180, 0]
        for i, x in enumerate(xs):
            put(kinds[i], x + rng.jitter(0.25), ym + rng.jitter(0.12),
                yaws[i], pks[i], z=0.02, tag=f"W{i}")
    # CARS_E — east parking strip (avoid the shop footprint x>=92)
    ze = _zone(plan, "CARS_E")
    if ze:
        x0, y0, x1, y1 = ze["rect"]
        ym = (y0 + y1) / 2.0 - 0.55
        xs = [67.0, 73.0, 79.0, 85.0, 90.5]
        kinds = ["gaz24", "vaz2101", "moskvich412", "uaz469", "gaz24"]
        pks = ["bluegrey", "cream", "beige", "olive", "darkgreen"]
        yaws = [0, 0, 180, 0, 180]
        for i, x in enumerate(xs):
            put(kinds[i], x + rng.jitter(0.2), ym + rng.jitter(0.12),
                yaws[i], pks[i], z=0.02, tag=f"E{i}")
    # CARS_SHOP — parking bays on the wide pavement in front of the shop
    zs = _zone(plan, "CARS_SHOP")
    if zs:
        x0, y0, x1, y1 = zs["rect"]
        ym = (y0 + y1) / 2.0
        for i, x in enumerate((95.0, 102.0, 109.0)):
            put(["gaz24", "vaz2101", "moskvich412"][i], x + rng.jitter(0.2),
                ym, [180, 0, 180][i], ["beige", "bluegrey", "cream"][i],
                z=0.145, tag=f"S{i}")
    # CARS_DK — row on the House-of-Culture terrace
    zd = _zone(plan, "CARS_DK")
    if zd:
        x0, y0, x1, y1 = zd["rect"]
        ym = (y0 + y1) / 2.0
        for i, x in enumerate((-10.0, -1.5, 7.0)):
            put(["gaz24", "vaz2101", "uaz469"][i], x, ym + rng.jitter(0.1),
                [0, 180, 0][i], ["darkgreen", "beige", "olive"][i],
                z=0.145, tag=f"D{i}")
    # CARS_GARAGE — nose-in parking in the garage lane
    zg = _zone(plan, "CARS_GARAGE")
    if zg:
        x0, y0, x1, y1 = zg["rect"]
        for i, x in enumerate((-74.0, -69.0, -64.0)):
            put(["vaz2101", "moskvich412", "gaz24"][i], x, 15.0,
                90, ["paleblue", "beige", "cream"][i], z=0.025,
                tag=f"G{i}")


def _place_extra_cars(parent, plan, rng, stats):
    """Courtyard / avenue kerb cars + truck + two rusted wrecks."""
    n = 0

    def put(kind, x, y, yaw, pk, z=0.0, wreck=None, rack=False, tag=""):
        nonlocal n
        n += 1
        name = f"VEH_{kind}_{tag or ('X' + str(n))}"
        _obj, tris = _spawn(parent, name, kind, x, y, yaw, rng, pk,
                            z=z, wreck=wreck, rack=rack)
        stats["cars" if kind != "zil130" else "trucks"] += 1
        stats["tris"] += tris
        stats["vehicles"] += 1

    # courtyards
    put("vaz2101", -69.5, -49.0, 90, "fadedred", z=0.02, tag="WCOURT1")
    put("moskvich412", -66.8, -38.6, -90, "beige", z=0.02, tag="WCOURT2")
    put("gaz24", 68.2, -49.2, -90, "bluegrey", z=0.02, tag="ECOURT1")
    put("uaz469", 98.6, -38.4, 90, "olive", z=0.02, tag="ECOURT2")
    # along the avenue kerbs (north lane / south lane)
    put("vaz2101", -101.0, -76.4, 180, "cream", z=0.03, tag="KERB1")
    put("moskvich412", 70.5, -76.4, 180, "paleblue", z=0.03, tag="KERB2")
    put("gaz24", -72.0, -86.4, 0, "darkgreen", z=0.03, tag="KERB3")
    put("vaz2101", 86.0, -86.4, 0, "beige", z=0.03, tag="KERB4")
    # one GAZ-24 with a roof rack (period detail)
    put("gaz24", -92.5, -66.2, 0, "greygreen", z=0.02, rack=True, tag="RACK")
    # ZIL-130 truck parked in the garage lane
    put("zil130", -58.0, 14.5, 180, "steel", z=0.025, tag="ZIL")
    # rusted wrecks
    put("vaz2101", -50.5, 12.2, 0, "fadedred", z=0.025,
        wreck={"missing": "fl"}, tag="WRECK1")
    put("moskvich412", 98.0, -50.2, 180, "beige", z=0.02,
        wreck={"flat": "fl"}, tag="WRECK2")
    stats["wrecks"] += 2


def _place_buses(parent, plan, rng, stats):
    for tag, x, y, yaw, z in (("01", -40.0, -76.55, 180, 0.03),
                              ("02", 52.0, -86.40, 0, 0.03)):
        _obj, tris = _spawn(parent, f"VEH_LAZ695_{tag}", "bus", x, y, yaw, rng,
                            "cream", z=z, stopped=(tag == "01"))
        stats["buses"] += 1
        stats["vehicles"] += 1
        stats["tris"] += tris
        if tag == "01":
            _bus_text(parent, (x, y, z), yaw)


def _place_tram(parent, plan, rng, stats):
    tr = plan.get("tram", {})
    track_y = tr.get("track_y", [-84.2, -78.8])[0]
    x = 15.0
    _obj, tris = _spawn(parent, "TRAM_T3_01", "tram", x, track_y + 0.01, 180,
                        rng, "cream", z=0.051)
    stats["tram"] += 1
    stats["vehicles"] += 1
    stats["tris"] += tris
    _tram_text(parent, (x, track_y + 0.01))
    return tr


def _place_poles(parent, plan, rng, stats):
    acc_p = MeshAcc()
    acc_w = MeshAcc()
    n_wires = 0

    # ---- utility line along the avenue north kerb (y≈-75.0) ----------------
    x_positions = [-110.0, -84.0, -58.0, -32.0, -6.0, 20.0, 46.0, 72.0, 98.0]
    heights = [10.4, 9.6, 10.8, 9.8, 10.2, 9.5, 10.6, 9.7, 10.9]
    wooden_idx = {0, 5}
    y_pole = -74.95
    poles = []
    for i, px in enumerate(x_positions):
        h = heights[i]
        top = _utility_pole(acc_p, px, y_pole, h, wooden=(i in wooden_idx),
                            transformer_on=(i == 4), ladder=(i in (2, 6)))
        poles.append({"x": px, "y": y_pole, "h": h, "wooden": i in wooden_idx,
                      "anchor_z": top})
    for a, b in zip(poles, poles[1:]):
        for dy in (-0.95, 0.0, 0.95):
            p0 = (a["x"], a["y"] + dy, a["anchor_z"])
            p1 = (b["x"], b["y"] + dy, b["anchor_z"])
            acc_w.wire(p0, p1, rng.uniform(0.55, 0.85), wiremat(),
                       radius=0.011, segs=5)
            n_wires += 1
        p0 = (a["x"], a["y"] - 0.42, a["h"] - 1.72)
        p1 = (b["x"], b["y"] - 0.42, b["h"] - 1.72)
        acc_w.wire(p0, p1, rng.uniform(0.8, 1.05), wiremat(),
                   radius=0.014, segs=5)
        n_wires += 1
    stats["utility_poles"] += len(poles)

    # service drops (cross-street wires) from avenue poles to building walls
    drops = [((-84.0, -74.95, 8.9), (-84.0, -66.0, 7.0)),
             ((-32.0, -74.95, 8.9), (-34.0, -70.0, 7.3)),
             ((20.0, -74.95, 8.9), (34.0, -70.0, 7.2)),
             ((72.0, -74.95, 8.9), (72.0, -66.0, 7.1)),
             ((98.0, -74.95, 8.9), (98.0, -68.0, 7.2))]
    for a, b in drops:
        _service_drop(acc_w, a, b)
        n_wires += 1

    # ---- west-courtyard pole line (wooden, POLES_W_COURT) ------------------
    wcourt = [(-100.0, 9.6), (-83.0, 9.0), (-66.0, 9.9)]
    w_poles = []
    for i, (px, h) in enumerate(wcourt):
        y = -34.6
        top = _utility_pole(acc_p, px, y, h, wooden=True)
        w_poles.append({"x": px, "y": y, "h": h, "anchor_z": top})
    for a, b in zip(w_poles, w_poles[1:]):
        for dy in (-0.70, 0.70):
            acc_w.wire((a["x"], a["y"] + dy, a["anchor_z"]),
                       (b["x"], b["y"] + dy, b["anchor_z"]),
                       rng.uniform(0.45, 0.65), wiremat(), radius=0.011, segs=5)
            n_wires += 1
        acc_w.wire((a["x"], a["y"] - 0.30, a["h"] - 1.55),
                   (b["x"], b["y"] - 0.30, b["h"] - 1.55),
                   rng.uniform(0.6, 0.85), wiremat(), radius=0.013, segs=5)
        n_wires += 1
    # one wire crossing the west drive to the W_K3 block wall
    _service_drop(acc_w, (-66.0, -34.6, 8.1), (-63.5, -30.0, 6.8), sag=0.25)
    n_wires += 1
    stats["utility_poles"] += len(w_poles)

    # ---- tram catenary: poles both sides, brackets, span + contact wires ---
    tr = plan.get("tram", {})
    track_ns = tr.get("track_y", [-84.2, -78.8])
    wire_h = tr.get("wire_height", 5.6)
    xs = [-110.0 + 24.0 * i for i in range(10)]
    cat = []
    for px in xs:
        for side, py, ty in ((-1, -73.2, track_ns[1]), (1, -89.2, track_ns[0])):
            _catenary_pole(acc_p, px, py, ty, side)
            cat.append((px, py, ty))
    stats["catenary_poles"] += len(cat)
    # cross-span support wires between the two pole lines
    for px in xs:
        n_y, s_y = -73.2, -89.2
        acc_w.wire((px, n_y, 8.35), (px, s_y, 8.35), 0.42, wiremat(),
                   radius=0.013, segs=6)
        n_wires += 1
        # hangers down to each contact wire
        for ty in track_ns:
            t = (ty - n_y) / (s_y - n_y)
            z = 8.35 - 0.42 * math.sin(math.pi * t)
            acc_w.tube((px, ty, z), (px, ty, wire_h - 0.02), 0.018, wiremat(), segs=4)
            n_wires += 1
    # two longitudinal contact wires with a small zig-zag sag per span
    for ty in track_ns:
        for i in range(len(xs) - 1):
            acc_w.wire((xs[i], ty, wire_h), (xs[i + 1], ty, wire_h), 0.10,
                       wiremat(), radius=0.018, segs=4)
            n_wires += 1

    acc_p.finish(parent, "POLE_UTILITY_AND_CATENARY", kind="poles")
    acc_w.finish(parent, "WIRE_OVERHEAD", kind="wires")
    stats["wires"] = n_wires
    stats["tris"] += acc_p.tris + acc_w.tris
    return stats


# ----------------------------------------------------------------------------
# public build
# ----------------------------------------------------------------------------
def build(parent, plan, rng, ctx=None):
    """Add all vehicles + road infrastructure under `parent`. Returns counts."""
    if rng is None:
        rng = kit.RNG(21)
    stats = {"vehicles": 0, "cars": 0, "buses": 0, "trucks": 0, "tram": 0,
             "wrecks": 0, "utility_poles": 0, "catenary_poles": 0, "wires": 0,
             "tris": 0}
    _place_zone_cars(parent, plan, rng, stats)
    _place_extra_cars(parent, plan, rng, stats)
    _place_buses(parent, plan, rng, stats)
    _place_tram(parent, plan, rng, stats)
    _place_poles(parent, plan, rng, stats)
    print("VEHICLES", stats)
    return stats


# ----------------------------------------------------------------------------
# test-only inspection row (not used by build.py)
# ----------------------------------------------------------------------------
def test_row(parent, rng=None):
    """One of every vehicle type in two rows, plus both wreck variants.

    Test script only — never called from build(). Placed around y≈+18..+27.
    """
    if rng is None:
        rng = kit.RNG(21)
    stats = {"tris": 0, "n": 0}

    def put(kind, x, y, yaw, pk, z=0.0, wreck=None, rack=False, tag=""):
        stats["n"] += 1
        name = f"VEH_ROW_{kind}_{tag}"
        _o, tris = _spawn(parent, name, kind, x, y, yaw, rng, pk,
                          z=z, wreck=wreck, rack=rack)
        stats["tris"] += tris
        return _o

    y1, y2 = 18.0, 27.0
    put("vaz2101", -20.0, y1, 0, "beige", tag="vaz1")
    put("gaz24", -13.0, y1, 0, "darkgreen", tag="gaz")
    put("moskvich412", -6.0, y1, 0, "paleblue", tag="mosk")
    put("uaz469", 1.5, y1, 0, "olive", tag="uaz")
    put("bus", 12.0, y1, 0, "cream", tag="bus")
    put("zil130", -12.0, y2, 0, "steel", tag="zil")
    put("gaz24", -2.0, y2, 0, "greygreen", rack=True, tag="gazrack")
    put("vaz2101", 5.0, y2, 0, "fadedred", wreck={"missing": "fl"}, tag="wreck1")
    put("moskvich412", 11.5, y2, 0, "beige", wreck={"flat": "fl"}, tag="wreck2")
    return stats
