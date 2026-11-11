"""props.py — street / courtyard props for the Soviet blue-hour microdistrict.

Public interface
----------------
    build(parent, plan, rng, ctx) -> {"lamps": [{"pos": (x, y, z), "kind": "street"|"plaza",
                                                 "height": h}, ...], "stats": {...}}

The module places every courtyard/street prop from ``plan["prop_zones"]`` plus the
landmarks that belong to props (BUS_STOP, BILLBOARD_*, POSTER_BOARD_*):

  * trees (columnar poplar, linden, birch, bare skeletal winter tree)
  * playground equipment (rocket climbing frame, swings, carousel, sandbox,
    horizontal bars, concrete mushroom, slide, spring rider, basketball hoops, goal)
  * courtyard furniture (benches, dumpsters + pads, drying frames with laundry,
    transformer boxes with warning stripes, payphone booths, mailbox banks,
    planters, hydrant, manholes, utility poles + wires)
  * fences (concrete panel, green metal bar, hedges)
  * notice boards / billboards / soviet road signs / bus stop shelter / banner
  * puddles, wet patches, weeds, litter, dead leaves

Everything is deterministic (``rng = kit.RNG(11)`` expected), in metres, parented
under ``parent`` and named with the ``PROP_`` prefix.  No lights are created here —
street lamp head positions are returned for the Lead to light.
"""
from __future__ import annotations

import math
import os
import sys

import bpy
from mathutils import Euler, Vector

_HERE = os.path.dirname(os.path.abspath(__file__))
if _HERE not in sys.path:
    sys.path.insert(0, _HERE)

import kit

TAU = math.pi * 2.0

# ----------------------------------------------------------------------------
# materials
# ----------------------------------------------------------------------------
# name -> (colour, roughness, metallic, emission colour, emission strength)
MATERIAL_SPECS = {
    # trees / vegetation
    "bark_dark": ("#332F29", 0.96, 0.0, None, 0.0),
    "bark_birch": ("#918F84", 0.82, 0.0, None, 0.0),
    "bark_bare": ("#2D2A24", 0.96, 0.0, None, 0.0),
    "foliage_poplar": ("#242C26", 0.95, 0.0, None, 0.0),
    "foliage_poplar2": ("#2B342B", 0.95, 0.0, None, 0.0),
    "foliage_linden": ("#2A332B", 0.95, 0.0, None, 0.0),
    "foliage_linden2": ("#313A2F", 0.95, 0.0, None, 0.0),
    "foliage_birch": ("#333D30", 0.95, 0.0, None, 0.0),
    "weed": ("#5C5541", 0.98, 0.0, None, 0.0),
    "weed_dark": ("#423C2E", 0.98, 0.0, None, 0.0),
    "leaves": ("#3A3328", 0.98, 0.0, None, 0.0),
    # faded painted metal (Soviet playground palette, desaturated)
    "paint_teal": ("#3D5552", 0.72, 0.30, None, 0.0),
    "paint_red": ("#6C3B33", 0.76, 0.28, None, 0.0),
    "paint_ochre": ("#8A7645", 0.76, 0.28, None, 0.0),
    "paint_blue": ("#3A4A5E", 0.72, 0.32, None, 0.0),
    "paint_green": ("#3D4B42", 0.72, 0.32, None, 0.0),
    "paint_white": ("#A8A79C", 0.75, 0.20, None, 0.0),
    "metal_dark": ("#252A2E", 0.60, 0.55, None, 0.0),
    "metal_mid": ("#4A5054", 0.62, 0.50, None, 0.0),
    "galv": ("#7B8185", 0.55, 0.55, None, 0.0),
    "rust": ("#6B4A38", 0.86, 0.22, None, 0.0),
    "rust_light": ("#7C5C42", 0.88, 0.15, None, 0.0),
    # masonry / ground
    "conc": ("#868B8D", 0.92, 0.0, None, 0.0),
    "conc_light": ("#9BA0A1", 0.90, 0.0, None, 0.0),
    "conc_dark": ("#5E6366", 0.94, 0.0, None, 0.0),
    "conc_pad": ("#6A6E6C", 0.95, 0.0, None, 0.0),
    "sand": ("#655C49", 0.99, 0.0, None, 0.0),
    "sand_play": ("#6E6450", 0.99, 0.0, None, 0.0),
    "dirt": ("#443D32", 0.99, 0.0, None, 0.0),
    "wood": ("#5C564A", 0.90, 0.0, None, 0.0),
    "wood_dark": ("#3F3B33", 0.92, 0.0, None, 0.0),
    "wood_red": ("#6A443C", 0.88, 0.0, None, 0.0),
    # glass / water
    "glass": ("#0E151C", 0.12, 0.35, None, 0.0),
    "glass_dim": ("#1A242C", 0.22, 0.20, None, 0.0),
    "puddle": ("#121A22", 0.08, 0.30, None, 0.0),
    "puddle_warm": ("#10161C", 0.09, 0.28, "#31190A", 0.45),
    "wet": ("#141A20", 0.26, 0.25, None, 0.0),
    "wet_dark": ("#10151A", 0.30, 0.20, None, 0.0),
    # cloth
    "cloth_white": ("#9A9A8E", 0.95, 0.0, None, 0.0),
    "cloth_grey": ("#6E7274", 0.95, 0.0, None, 0.0),
    "cloth_blue": ("#495A70", 0.95, 0.0, None, 0.0),
    "cloth_olive": ("#585C46", 0.95, 0.0, None, 0.0),
    "cloth_red": ("#68403A", 0.95, 0.0, None, 0.0),
    # signs / paint
    "sign_blue": ("#2C4470", 0.55, 0.15, None, 0.0),
    "sign_red": ("#6E2A24", 0.55, 0.15, None, 0.0),
    "sign_white": ("#C6C4B8", 0.55, 0.10, None, 0.0),
    "sign_yellow": ("#A89448", 0.58, 0.10, None, 0.0),
    "sign_dark": ("#1A1E22", 0.60, 0.10, None, 0.0),
    "warning_yellow": ("#A5944C", 0.70, 0.15, None, 0.0),
    "warning_dark": ("#1F231F", 0.72, 0.15, None, 0.0),
    "manhole": ("#282C2E", 0.55, 0.40, None, 0.0),
    "emissive_warm": ("#FFC98A", 0.30, 0.0, "#FFB469", 12.0),
    "emissive_dim": ("#E8D6B4", 0.40, 0.0, "#C7A877", 4.0),
    "mailbox": ("#4E5A60", 0.60, 0.30, None, 0.0),
    "booth": ("#4A555C", 0.58, 0.35, None, 0.0),
    "paper": ("#9A9587", 0.95, 0.0, None, 0.0),
    "cardboard": ("#6E6047", 0.96, 0.0, None, 0.0),
    # round 2: north park
    "reed": ("#6A6249", 0.98, 0.0, None, 0.0),
    "reed_head": ("#7A7057", 0.98, 0.0, None, 0.0),
    "duck_body": ("#5E5A50", 0.90, 0.05, None, 0.0),
    "duck_dark": ("#3A3A36", 0.88, 0.10, None, 0.0),
    "roof_dark": ("#2E3337", 0.85, 0.05, None, 0.0),
    # round 3: memorial / terminal / east quarter
    "granite": ("#6C6D68", 0.72, 0.05, None, 0.0),
    "granite_dark": ("#4B4D4B", 0.78, 0.05, None, 0.0),
    "granite_light": ("#8A8B84", 0.68, 0.05, None, 0.0),
    "bronze": ("#6E5A38", 0.52, 0.70, None, 0.0),
    "bronze_dark": ("#4C4029", 0.58, 0.65, None, 0.0),
    "flag_red": ("#6C2A24", 0.92, 0.0, None, 0.0),
    "flag_cream": ("#B8A98C", 0.92, 0.0, None, 0.0),
    "flame": ("#FFB45E", 0.35, 0.0, "#FF8A2A", 16.0),
    "flame_core": ("#FFE6B4", 0.25, 0.0, "#FFD27A", 26.0),
    "laz_cream": ("#B3A377", 0.68, 0.22, None, 0.0),
    "laz_red": ("#8A3A30", 0.70, 0.25, None, 0.0),
    "bus_glass": ("#0E161E", 0.10, 0.35, None, 0.0),
    "rubber": ("#191B1D", 0.92, 0.0, None, 0.0),
    "moss": ("#46523C", 0.98, 0.0, None, 0.0),
    "taxi_white": ("#C7C5BA", 0.62, 0.10, None, 0.0),
    "taxi_dark": ("#23272B", 0.70, 0.10, None, 0.0),
    "car_body_a": ("#5E6A6E", 0.55, 0.35, None, 0.0),
    "car_body_b": ("#7A6E5C", 0.55, 0.30, None, 0.0),
    "car_body_c": ("#4C5560", 0.55, 0.35, None, 0.0),
}

_MCACHE: dict[str, bpy.types.Material] = {}


def mat(key: str):
    """Cached solid PBR material for a key of MATERIAL_SPECS.

    Materials are created directly (not through kit.MATS) so that a scene reset
    between module builds can never hand back a dead material reference.
    """
    m = _MCACHE.get(key)
    if m is not None:
        try:
            if bpy.data.materials.get(m.name) is m:
                return m
        except (ReferenceError, RuntimeError):
            pass
        _MCACHE.clear()
    color, rough, metallic, emission, strength = MATERIAL_SPECS[key]
    m = bpy.data.materials.new("PROP_" + key)
    m.use_nodes = True
    b = m.node_tree.nodes.get("Principled BSDF")
    b.inputs["Base Color"].default_value = kit.srgb(color)
    b.inputs["Roughness"].default_value = rough
    b.inputs["Metallic"].default_value = metallic
    if emission is not None:
        b.inputs["Emission Color"].default_value = kit.srgb(emission)
        b.inputs["Emission Strength"].default_value = strength
    _MCACHE[key] = m
    return m



# ----------------------------------------------------------------------------
# low level geometry accumulator (one merged, multi-material mesh per prop)
# ----------------------------------------------------------------------------
class PB:
    """Accumulates low-poly geometry, emitted as a single mesh object."""

    __slots__ = ("verts", "faces", "face_mat", "mats")

    def __init__(self):
        self.verts: list[tuple] = []
        self.faces: list[tuple] = []
        self.face_mat: list[int] = []
        self.mats: list = []

    # -- material bookkeeping ------------------------------------------------
    def _mi(self, material) -> int:
        if material is None:
            return 0
        for i, m in enumerate(self.mats):
            if m == material:
                return i
        self.mats.append(material)
        return len(self.mats) - 1

    def add(self, verts, faces, material=None):
        base = len(self.verts)
        mi = self._mi(material)
        self.verts.extend(tuple(v) for v in verts)
        for f in faces:
            self.faces.append(tuple(base + i for i in f))
            self.face_mat.append(mi)

    # -- primitives ----------------------------------------------------------
    def tube(self, p0, p1, r0, r1=None, seg=6, material=None, caps=True):
        p0 = Vector(p0)
        p1 = Vector(p1)
        d = p1 - p0
        length = d.length
        if length < 1e-5:
            return
        r1 = r0 if r1 is None else r1
        zax = d / length
        ref = Vector((1, 0, 0)) if abs(zax.z) > 0.9 else Vector((0, 0, 1))
        a = zax.cross(ref).normalized()
        b = zax.cross(a)
        verts = []
        for i in range(seg):
            t = TAU * i / seg
            u = a * math.cos(t) + b * math.sin(t)
            verts.append(tuple(p0 + u * r0))
        for i in range(seg):
            t = TAU * i / seg
            u = a * math.cos(t) + b * math.sin(t)
            verts.append(tuple(p1 + u * r1))
        faces = []
        for i in range(seg):
            j = (i + 1) % seg
            faces.append((i, j, seg + j, seg + i))
        if caps:
            faces.append(tuple(range(seg - 1, -1, -1)))
            faces.append(tuple(range(seg, 2 * seg)))
        self.add(verts, faces, material)

    def polyline(self, pts, r0, r1=None, seg=5, material=None, closed=False):
        n = len(pts)
        if n < 2:
            return
        r1 = r0 if r1 is None else r1
        last = n if closed else n - 1
        for i in range(last):
            t = i / max(1, last - 1)
            self.tube(pts[i], pts[(i + 1) % n], r0 + (r1 - r0) * t,
                      r0 + (r1 - r0) * (t + 1.0 / max(1, last - 1)), seg=seg,
                      material=material, caps=False)

    def blob(self, center, radii, material=None, seg=10, rings=6, jitter=0.0,
             rng=None):
        """Ellipsoid blob (upright uv-sphere), optionally jittered for organic crowns."""
        cx, cy, cz = center
        rx, ry, rz = radii
        verts = []
        ring_start = []
        for ri in range(1, rings):
            phi = math.pi * ri / rings
            sz = math.cos(phi)
            sr = math.sin(phi)
            ring_start.append(len(verts))
            for si in range(seg):
                th = TAU * si / seg
                j = 1.0 + (rng.uniform(-jitter, jitter) if (rng and jitter) else 0.0)
                verts.append((cx + math.cos(th) * sr * rx * j,
                              cy + math.sin(th) * sr * ry * j,
                              cz + sz * rz * j))
        top = len(verts)
        verts.append((cx, cy, cz + rz))
        bot = len(verts)
        verts.append((cx, cy, cz - rz))
        faces = []
        b0 = ring_start[0]
        for si in range(seg):
            sn = (si + 1) % seg
            faces.append((top, b0 + si, b0 + sn))
        for k in range(len(ring_start) - 1):
            a = ring_start[k]
            b = ring_start[k + 1]
            for si in range(seg):
                sn = (si + 1) % seg
                faces.append((a + si, b + si, b + sn, a + sn))
        bl = ring_start[-1]
        for si in range(seg):
            sn = (si + 1) % seg
            faces.append((bot, bl + sn, bl + si))
        self.add(verts, faces, material)

    def box(self, center, size, material=None, rot=(0.0, 0.0, 0.0)):
        cx, cy, cz = center
        sx, sy, sz = size
        hx, hy, hz = sx / 2.0, sy / 2.0, sz / 2.0
        pts = [(-hx, -hy, -hz), (hx, -hy, -hz), (hx, hy, -hz), (-hx, hy, -hz),
               (-hx, -hy, hz), (hx, -hy, hz), (hx, hy, hz), (-hx, hy, hz)]
        m = Euler(rot, "XYZ").to_matrix()
        verts = []
        for p in pts:
            v = m @ Vector(p)
            verts.append((v.x + cx, v.y + cy, v.z + cz))
        faces = [(0, 1, 2, 3), (7, 6, 5, 4), (0, 4, 5, 1), (1, 5, 6, 2),
                 (2, 6, 7, 3), (3, 7, 4, 0)]
        self.add(verts, faces, material)

    def quad(self, center, u, v, su, sv, material=None):
        c = _v3(center)
        uu = _v3(u).normalized() * (su / 2.0)
        vv = _v3(v).normalized() * (sv / 2.0)
        verts = [tuple(c - uu - vv), tuple(c - uu + vv),
                 tuple(c + uu + vv), tuple(c + uu - vv)]
        self.add(verts, [(0, 1, 2, 3)], material)

    def disc(self, center, normal, radius, material=None, seg=12):
        c = _v3(center)
        n = _v3(normal).normalized()
        ref = Vector((1, 0, 0)) if abs(n.z) > 0.9 else Vector((0, 0, 1))
        a = n.cross(ref).normalized()
        b = n.cross(a)
        verts = []
        for i in range(seg):
            t = TAU * i / seg
            verts.append(tuple(c + (a * math.cos(t) + b * math.sin(t)) * radius))
        self.add(verts, [tuple(range(seg))], material)

    def prism(self, pts, thickness_dir, thickness, material=None):
        n = len(pts)
        t = _v3(thickness_dir).normalized() * (thickness / 2.0)
        verts = [tuple(Vector(p) + t) for p in pts] + \
                [tuple(Vector(p) - t) for p in pts]
        faces = [tuple(range(n - 1, -1, -1)), tuple(range(n, 2 * n))]
        for i in range(n):
            j = (i + 1) % n
            faces.append((i, j, n + j, n + i))
        self.add(verts, faces, material)


    # -- emit ----------------------------------------------------------------
    def build(self, parent, name):
        if not self.faces:
            return None
        me = bpy.data.meshes.new(name)
        me.from_pydata(self.verts, [], self.faces)
        me.update()
        for m in self.mats:
            me.materials.append(m)
        for i, poly in enumerate(me.polygons):
            poly.material_index = self.face_mat[i]
        obj = bpy.data.objects.new(name, me)
        coll = None
        if parent is not None and parent.users_collection:
            coll = parent.users_collection[0]
        (coll or bpy.context.scene.collection).objects.link(obj)
        if parent is not None:
            obj.parent = parent
        return obj


def _sub(parent, name, loc=(0, 0, 0)):
    """Create a child empty under ``parent`` inside the same collection."""
    coll = None
    if parent is not None and parent.users_collection:
        coll = parent.users_collection[0]
    return kit.group(name, coll, parent, loc)


def _aim(angle):
    """Tiny rng shim so kit.lamp_post aims the curved arm in a known direction."""
    class _Aim:
        def uniform(self, a, b):
            return angle
    return _Aim()


class XF:
    """Local (x, y, z) -> world transform with a yaw rotation."""

    __slots__ = ("x", "y", "rot", "c", "s")

    def __init__(self, x, y, rot=0.0):
        self.x, self.y, self.rot = x, y, rot
        self.c = math.cos(rot)
        self.s = math.sin(rot)

    def __call__(self, lx, ly, lz):
        return (self.x + lx * self.c - ly * self.s,
                self.y + lx * self.s + ly * self.c,
                lz)

    def d(self, lx, ly):
        """Direction vector (no translation)."""
        return (lx * self.c - ly * self.s, lx * self.s + ly * self.c)


def _circ(center, radius, z, seg=12, start=0.0):
    cx, cy = center
    return [(cx + radius * math.cos(start + TAU * i / seg),
             cy + radius * math.sin(start + TAU * i / seg), z) for i in range(seg)]


def _v3(vec):
    """Vector from 2D or 3D input (2D gets z = 0)."""
    v = Vector(vec)
    if len(v) == 2:
        v = Vector((v.x, v.y, 0.0))
    return v



# ----------------------------------------------------------------------------
# site oracle: roads / blocks / prop zones
# ----------------------------------------------------------------------------
class Site:
    """Answers "may I put a prop here?" and keeps a soft occupancy map."""

    def __init__(self, plan):
        self.plan = plan
        self.roads = [tuple(r["rect"]) for r in plan.get("roads", [])]
        self.blocks = [tuple(b["rect"]) for b in plan.get("blocks", [])]
        self.sidewalks = [tuple(s["rect"]) for s in plan.get("sidewalks", [])]
        self.zones = {z["id"]: z for z in plan.get("zones", [])}
        self.prop = {p["id"]: p for p in plan.get("prop_zones", [])}
        self.landmarks = {l["id"]: l for l in plan.get("landmarks", [])}
        self.paths = plan.get("paths", [])
        self.claims: list[tuple[float, float, float]] = []
        # water bodies (round 2 has an elliptical pond in the north park)
        self.water = []          # list of ellipse/rect descriptors
        for z in plan.get("zones", []):
            if z.get("kind") != "water":
                continue
            x0, y0, x1, y1 = z["rect"]
            if z.get("ellipse"):
                self.water.append(("ellipse", (x0 + x1) / 2.0, (y0 + y1) / 2.0,
                                   (x1 - x0) / 2.0, (y1 - y0) / 2.0))
            else:
                self.water.append(("rect", x0, y0, x1, y1))
        # rectangles where solid props must not go (car places, monument plaza core)
        self.reserved = []
        for pz in plan.get("prop_zones", []):
            if pz.get("type") == "cars":
                x0, y0, x1, y1 = pz["rect"]
                self.reserved.append((x0 - 1.0, y0 - 1.0, x1 + 1.0, y1 + 1.0))
        for lid in ("OBELISK", "FOUNTAIN"):
            lm = self.landmarks.get(lid)
            if lm:
                r = lm.get("radius", 2.0) + 1.6
                x, y = lm["pos"]
                self.reserved.append((x - r, y - r, x + r, y + r))


    # -- rect helpers --------------------------------------------------------
    @staticmethod
    def in_rects(x, y, rects, margin=0.0):
        for (x0, y0, x1, y1) in rects:
            if x0 - margin <= x <= x1 + margin and y0 - margin <= y <= y1 + margin:
                return True
        return False

    def on_road(self, x, y, margin=0.0):
        return self.in_rects(x, y, self.roads, margin)

    def in_building(self, x, y, margin=1.0):
        return self.in_rects(x, y, self.blocks, margin)

    def reserved_at(self, x, y):
        return self.in_rects(x, y, self.reserved, 0.0)

    def in_water(self, x, y, margin=0.0):
        for w in self.water:
            if w[0] == "ellipse":
                _k, cx, cy, rx, ry = w
                if rx <= 0 or ry <= 0:
                    continue
                if ((x - cx) / (rx + margin)) ** 2 + ((y - cy) / (ry + margin)) ** 2 <= 1.0:
                    return True
            else:
                _k, x0, y0, x1, y1 = w
                if x0 - margin <= x <= x1 + margin and y0 - margin <= y <= y1 + margin:
                    return True
        return False

    def ok(self, x, y, road_margin=0.0, block_margin=1.0, allow_reserved=False):
        if self.on_road(x, y, road_margin):
            return False
        if self.in_building(x, y, block_margin):
            return False
        if self.in_water(x, y, 0.5):
            return False
        if not allow_reserved and self.reserved_at(x, y):
            return False
        return True

    def free_near(self, x, y, max_r=14.0, step=2.2, road_margin=0.3,
                  block_margin=1.0, allow_reserved=True):
        """Nearest free point to (x, y) — used when a planned landmark sits on a wall."""
        if self.ok(x, y, road_margin, block_margin, allow_reserved):
            return (x, y)
        r = step
        while r <= max_r:
            n = max(8, int(TAU * r / 2.4))
            for k in range(n):
                a = TAU * k / n
                px, py = x + math.cos(a) * r, y + math.sin(a) * r
                if self.ok(px, py, road_margin, block_margin, allow_reserved):
                    return (px, py)
            r += step
        return None

    # -- claims --------------------------------------------------------------
    def claim(self, x, y, r):
        self.claims.append((x, y, r))

    def clash(self, x, y, r=0.0):
        for cx, cy, cr in self.claims:
            if (x - cx) ** 2 + (y - cy) ** 2 < (r + cr) ** 2:
                return True
        return False

    # -- placement helpers ---------------------------------------------------
    def place(self, zid, count, pad, rng, radius=0.0, road_margin=0.4,
              block_margin=1.0, tries=90, allow_reserved=False):
        """Deterministically sample ``count`` free points inside a prop-zone rect."""
        pz = self.prop.get(zid)
        if pz is None:
            return []
        x0, y0, x1, y1 = pz["rect"]
        pad = min(pad, max(0.2, (x1 - x0) / 2 - 0.2), max(0.2, (y1 - y0) / 2 - 0.2))
        out = []
        for _ in range(count):
            for _try in range(tries):
                x = rng.uniform(x0 + pad, x1 - pad)
                y = rng.uniform(y0 + pad, y1 - pad)
                if not self.ok(x, y, road_margin, block_margin, allow_reserved):
                    continue
                if self.clash(x, y, radius):
                    continue
                self.claim(x, y, radius)
                out.append((x, y))
                break
        return out

    @staticmethod
    def row_points(rect, step, edge=0.0):
        """Evenly spaced points along the long axis of a (thin) rect."""
        x0, y0, x1, y1 = rect
        horizontal = (x1 - x0) >= (y1 - y0)
        length = (x1 - x0) if horizontal else (y1 - y0)
        if length <= 1e-6:
            return []
        usable = max(0.0, length - 2 * edge)
        n = max(2, int(round(usable / step)) + 1)
        pts = []
        for i in range(n):
            t = edge + usable * (i / (n - 1))
            if horizontal:
                pts.append((x0 + t, (y0 + y1) / 2.0))
            else:
                pts.append(((x0 + x1) / 2.0, y0 + t))
        return pts

    def row(self, zid, step, rng=None, edge=1.2, jitter=0.0, radius=1.0,
            road_margin=0.6):
        pz = self.prop.get(zid)
        if pz is None:
            return []
        out = []
        for (x, y) in self.row_points(pz["rect"], step, edge=edge):
            if rng is not None and jitter:
                x += rng.uniform(-jitter, jitter)
                y += rng.uniform(-jitter, jitter)
            if not self.ok(x, y, road_margin=road_margin, block_margin=1.0):
                continue
            if self.clash(x, y, radius):
                continue
            self.claim(x, y, radius)
            out.append((x, y))
        return out

    def zone_rect(self, zid):
        z = self.zones.get(zid)
        return tuple(z["rect"]) if z else None

    def prop_rect(self, zid):
        pz = self.prop.get(zid)
        return tuple(pz["rect"]) if pz else None


# ----------------------------------------------------------------------------
# street / plaza / courtyard / park lamps
# ----------------------------------------------------------------------------
COURT_LAMP_IDS = ("LAMPS_COURT", "LAMPS_PLAY", "LAMPS_MONUMENT")
PARK_LAMP_IDS = ("LAMPS_PARK", "LAMPS_WEST_PARK")
PARK_LAMP_PATHS = ("P_POND_S", "P_POND_N", "P_PARK_W", "P_PARK_E",
                   "P_PARK_TOWERS", "P_MONUMENT_W", "P_MONUMENT_E",
                   "P_EAST_Q_N", "P_EAST_Q_MID", "P_EAST_Q_S", "P_VYSOTKA",
                   "P_TERMINAL_W", "P_TERMINAL_E", "P_TERMINAL_MID")


def _dist_point_seg(px, py, ax, ay, bx, by):
    dx, dy = bx - ax, by - ay
    l2 = dx * dx + dy * dy
    if l2 < 1e-9:
        return math.hypot(px - ax, py - ay)
    t = ((px - ax) * dx + (py - ay) * dy) / l2
    t = max(0.0, min(1.0, t))
    return math.hypot(px - (ax + dx * t), py - (ay + dy * t))


def _near_path(site, x, y, margin=1.5):
    for p in site.paths:
        pts = p.get("pts") or []
        hw = float(p.get("width", 3.0)) / 2.0
        for i in range(len(pts) - 1):
            if _dist_point_seg(x, y, pts[i][0], pts[i][1],
                               pts[i + 1][0], pts[i + 1][1]) < hw + margin:
                return True
    return False


def _aim_at_road(site, x, y):
    """Azimuth from the lamp post towards the nearest carriageway."""
    best = None
    for (x0, y0, x1, y1) in site.roads:
        cx = min(max(x, x0), x1)
        cy = min(max(y, y0), y1)
        dx, dy = cx - x, cy - y
        d = math.hypot(dx, dy)
        if best is None or d < best[0]:
            best = (d, dx, dy)
    if best is None or best[0] < 1e-6:
        return 0.0
    return math.atan2(best[2], best[1])


def _lamp_kind(zid, pz):
    for key in COURT_LAMP_IDS:
        if key in zid:
            return "court"
    for key in PARK_LAMP_IDS:
        if key in zid:
            return "park"
    if "LAMPS_PLAZA" in zid:
        return "plaza"
    return "street"


def _street_line_points(site, pz, step):
    """Row points for a street lamp zone; shifts sideways if the plan rect is on a road."""
    x0, y0, x1, y1 = pz["rect"]
    cx, cy = (x0 + x1) / 2.0, (y0 + y1) / 2.0
    vertical = (y1 - y0) > (x1 - x0)
    base = site.row_points(pz["rect"], step, edge=1.2)
    good = sum(1 for (x, y) in base if site.ok(x, y, 0.5, 1.0, True))
    if good >= 2:
        return base
    for off in (4.8, -4.8, 5.8, -5.8, 6.8, -6.8, 8.0, -8.0, 10.0, -10.0):
        cand = ([(cx + off, yy) for (_x, yy) in base] if vertical
                else [(xx, cy + off) for (xx, _y) in base])
        if sum(1 for (x, y) in cand if site.ok(x, y, 0.5, 1.0, True)) >= 2:
            return cand
    return base


def _court_park_points(site, pz, kind, rng):
    """Sparse warm lamps: courtyard corners / park path edges (raw candidates)."""
    zid = pz["id"]
    x0, y0, x1, y1 = pz["rect"]
    w, h = x1 - x0, y1 - y0
    cands = []
    if min(w, h) < 5.0:
        # thin strip: monument mound / west forest edge / esplanade (row of globes)
        long_axis = max(w, h)
        n = max(2, min(5, int(long_axis / 26.0) + 1))
        pts = Site.row_points(pz["rect"], max(9.0, long_axis / n), edge=2.0)
        if kind == "park":
            rng.shuffle(pts)
        return pts
    if kind == "park":
        for p in site.paths:
            pts = p.get("pts") or []
            if not any(x0 - 8 <= px <= x1 + 8 and y0 - 8 <= py <= y1 + 8
                       for (px, py) in pts):
                continue
            hw = float(p.get("width", 3.0)) / 2.0
            for i in range(len(pts) - 1):
                ax, ay = pts[i][0], pts[i][1]
                bx, by = pts[i + 1][0], pts[i + 1][1]
                seg = math.hypot(bx - ax, by - ay)
                n = max(1, int(seg / 30.0))
                for k in range(n + 1):
                    t = k / n
                    px = ax + (bx - ax) * t
                    py = ay + (by - ay) * t
                    cands.append((px + 2.0 + hw, py))
                    cands.append((px, py + 2.0 + hw))
                    cands.append((px - 2.0 - hw, py))
        rng.shuffle(cands)
        if not cands:
            cands = [(x0 + 2.0, y0 + 2.0), (x1 - 2.0, y1 - 2.0),
                     ((x0 + x1) / 2.0, y1 - 2.0)]
    else:
        # courtyard: corners + an inset 3x3 grid (the "1-2 sparse globes" rule)
        gx = [x0 + 2.0, (x0 + x1) / 2.0, x1 - 2.0]
        gy = [y0 + 2.0, (y0 + y1) / 2.0, y1 - 2.0]
        cands = [(px, py) for px in gx for py in gy]
    return cands


def build_lamps(parent, plan, rng, site, stats):
    """Sodium street lamps, plaza globes, sparse warm court/park globes.

    Returns the light-head positions: {"pos": (x, y, z), "kind": "street"|
    "plaza"|"court"|"park", "height": h}.
    """
    grp = _sub(parent, "PROP_LAMPS")
    lamps = []
    kinds = {"street": 0, "plaza": 0, "court": 0, "park": 0}
    bus_xy = [tuple(lm["pos"][:2]) for lm in plan.get("landmarks", [])
              if lm.get("type") == "bus_stop"]
    pav = [tuple(lm["pos"][:2]) for lm in plan.get("landmarks", [])
           if lm.get("type") == "pavilion"]
    # round 3: keep the monument group and the metro pylon clear of lamp posts
    monument_xy = [tuple(lm["pos"][:2]) for lm in plan.get("landmarks", [])
                   if lm.get("type") in ("eternal_flame", "guard_box",
                                         "flag_row", "plaque", "metro_sign")]
    for pz in plan.get("prop_zones", []):
        if pz.get("type") != "lamps":
            continue
        zid = pz["id"]
        kind = _lamp_kind(zid, pz)
        height = float(pz.get("height", {"street": 8.0, "plaza": 5.5,
                                         "court": 4.0, "park": 3.2}[kind]))
        step = float(pz.get("step", 24))
        if kind in ("court", "park"):
            pts = _court_park_points(site, pz, kind, rng)
            pts = [p for p in pts
                   if not any(math.dist(p, m) < 7.0 for m in monument_xy)]
        elif kind == "plaza":
            x0, y0, x1, y1 = pz["rect"]
            span = (y1 - y0) - 4.0
            n = max(2, int(round(span / max(1.0, step))) + 1)
            pts = []
            for xx in (x0 + 1.3, x1 - 1.3):
                for i in range(n):
                    pts.append((xx, y0 + 2.0 + span * (i / (n - 1))))
        else:
            if zid in ("LAMPS_W_DRIVE", "LAMPS_E_DRIVE"):
                cx = (pz["rect"][0] + pz["rect"][2]) / 2.0
                pts = [(cx, yy) for yy in (-46.0, -16.0)]
            else:
                pts = _street_line_points(site, pz, step)
        want = (2 if kind == "court" else 5) if kind in ("court", "park") else 999
        placed = 0
        for i, (x, y) in enumerate(pts):
            if placed >= want:
                break
            if kind in ("court", "park"):
                if any(math.dist((x, y), b) < 4.2 for b in bus_xy):
                    continue
                if any(math.dist((x, y), p) < 6.5 for p in pav):
                    continue
                if not site.ok(x, y, 0.4, 1.0, True):
                    continue
                if site.clash(x, y, 0.8):
                    continue
                if _near_path(site, x, y, 1.6):
                    continue
                site.claim(x, y, 1.4)
            else:
                if any(math.dist((x, y), b) < 4.6 for b in bus_xy):
                    continue
                if not site.ok(x, y, 0.5, 1.0, True):
                    continue
                if site.clash(x, y, 1.0):
                    continue
                site.claim(x, y, 1.0)
            nm = f"PROP_LAMP_{kind[:4].upper()}_{zid}_{i:02d}"
            if kind in ("court", "park"):
                head = kit.lamp_post(grp, nm, (x, y, 0.0), height, kind="plaza",
                                     rng=None)
                # small hood over the globe reads as a municipal park/court lantern
                kit.cyl(grp, nm + "_hood", (x, y, height + 0.52), 0.42, 0.06,
                        mat("metal_dark"), verts=10)
                kit.cyl(grp, nm + "_base", (x, y, 0.10), 0.24, 0.20,
                        mat("conc_dark"), verts=10)
            else:
                aim = _aim_at_road(site, x, y)
                head = kit.lamp_post(grp, nm, (x, y, 0.0), height, kind="street",
                                     arm=2.1 if height >= 7.5 else 1.7,
                                     rng=_aim(aim))
                kit.cyl(grp, nm + "_base", (x, y, 0.11), 0.44, 0.22,
                        mat("conc_dark"), verts=10)
                kit.box(grp, nm + "_box", (x, y, 2.1), (0.26, 0.20, 0.44),
                        mat("metal_mid"), rot=(0, 0, aim))
            lamps.append({"pos": head, "kind": kind, "height": height})
            kinds[kind] += 1
            placed += 1
    # --- round 3: cover the avenue stretches beyond the original LAMPS_AVENUE rect
    avenue = None
    for r in plan.get("roads", []):
        if r.get("id") == "AVENUE":
            avenue = r["rect"]
            break
    if avenue is not None:
        ax0, ay0, ax1, ay1 = avenue
        covered = []
        for zid in ("LAMPS_AVENUE", "LAMPS_AVE_S"):
            z = site.prop.get(zid)
            if z:
                covered.append((z["rect"][0], z["rect"][2]))
        cx0 = min(c[0] for c in covered) if covered else 0.0
        cx1 = max(c[1] for c in covered) if covered else 0.0
        ext_rows = []
        if ax0 < cx0:
            ext_rows.append((ax0 + 4.0, cx0 - 6.0))
        if ax1 > cx1:
            ext_rows.append((cx1 + 6.0, ax1 - 4.0))
        for (ex0, ex1) in ext_rows:
            for side, y in (("-N", ay1 + 1.2), ("-S", ay0 - 1.2)):
                n = max(1, int((ex1 - ex0) / 26.0))
                for k in range(n + 1):
                    x = ex0 + (ex1 - ex0) * (k / max(1, n))
                    if not site.ok(x, y, 0.6, 1.0, True):
                        continue
                    if site.clash(x, y, 1.0):
                        continue
                    site.claim(x, y, 1.0)
                    nm = f"PROP_LAMP_STREET_EXT_{side}_{int(x)}"
                    aim = _aim_at_road(site, x, y)
                    head = kit.lamp_post(grp, nm, (x, y, 0.0), 8.0,
                                         kind="street", arm=2.1,
                                         rng=_aim(aim))
                    kit.cyl(grp, nm + "_base", (x, y, 0.11), 0.44, 0.22,
                            mat("conc_dark"), verts=10)
                    lamps.append({"pos": head, "kind": "street", "height": 8.0})
                    kinds["street"] += 1
    stats["lamps"] = len(lamps)
    stats["lamp_kinds"] = kinds
    return lamps



# ----------------------------------------------------------------------------
# trees
# ----------------------------------------------------------------------------
def _spread_dir(direction, rng, spread=0.6, up=0.45):
    v = Vector(direction).normalized()
    ref = Vector((0, 0, 1)) if abs(v.z) < 0.9 else Vector((1, 0, 0))
    a = v.cross(ref).normalized()
    b = v.cross(a)
    t = rng.uniform(0.0, TAU)
    perp = a * math.cos(t) + b * math.sin(t)
    out = v * math.cos(spread) + perp * math.sin(spread)
    out.z += up * rng.uniform(0.4, 1.0)
    out.normalize()
    return out


def _tree_poplar(grp, name, x, y, rng):
    h = rng.uniform(12.0, 18.0)
    P = PB()
    bark = mat("bark_dark")
    lean = (rng.uniform(-0.35, 0.35), rng.uniform(-0.35, 0.35))
    top = (x + lean[0], y + lean[1], h * 0.44)
    P.tube((x, y, -0.25), top, 0.30, 0.15, seg=7, material=bark)
    for _ in range(2):
        a = rng.uniform(0.0, TAU)
        P.tube((x, y, 0.30), (x + math.cos(a) * 0.6, y + math.sin(a) * 0.6, 0.02),
               0.10, 0.04, seg=5, material=bark, caps=False)
    # skeleton branches poking through the crown (post-punk silhouette)
    for _ in range(5):
        a = rng.uniform(0.0, TAU)
        rr = rng.uniform(0.25, 0.9)
        zz = h * rng.uniform(0.60, 0.88)
        P.tube(top, (x + lean[0] + math.cos(a) * rr,
                     y + lean[1] + math.sin(a) * rr, zz),
               0.07, 0.02, seg=4, material=bark, caps=False)
    fol = mat("foliage_poplar") if rng.random() < 0.6 else mat("foliage_poplar2")
    # one ragged columnar crown (stacked blobs would show lit bands between them)
    P.blob((x + lean[0], y + lean[1], h * 0.67),
           (h * 0.088, h * 0.088, h * 0.305), fol, rng=rng, jitter=0.20,
           seg=11, rings=8)
    P.blob((x + lean[0] + rng.uniform(-0.3, 0.3),
            y + lean[1] + rng.uniform(-0.3, 0.3), h * 0.90),
           (h * 0.052, h * 0.052, h * 0.090), fol, rng=rng, jitter=0.24,
           seg=8, rings=5)
    P.build(grp, name)
    return h




def _tree_linden(grp, name, x, y, rng):
    h = rng.uniform(9.0, 13.0)
    P = PB()
    bark = mat("bark_dark")
    top = (x + rng.uniform(-0.2, 0.2), y + rng.uniform(-0.2, 0.2), h * 0.36)
    P.tube((x, y, -0.25), top, 0.30, 0.16, seg=7, material=bark)
    P.tube(top, (top[0], top[1], h * 0.52), 0.14, 0.07, seg=6, material=bark)
    for _ in range(5):
        a = rng.uniform(0.0, TAU)
        P.tube((top[0], top[1], h * 0.46),
               (x + math.cos(a) * rng.uniform(0.8, 1.9),
                y + math.sin(a) * rng.uniform(0.8, 1.9), h * 0.66),
               0.07, 0.03, seg=4, material=bark, caps=False)
    fol = mat("foliage_linden") if rng.random() < 0.6 else mat("foliage_linden2")
    P.blob((x, y, h * 0.62), (h * 0.175, h * 0.175, h * 0.190), fol,
           rng=rng, jitter=0.20, seg=11, rings=7)
    a = rng.uniform(0.0, TAU)
    P.blob((x + math.cos(a) * h * 0.055, y + math.sin(a) * h * 0.055,
            h * rng.uniform(0.66, 0.76)),
           (h * 0.090, h * 0.090, h * 0.105), fol, rng=rng, jitter=0.20,
           seg=8, rings=5)
    P.build(grp, name)
    return h




def _tree_birch(grp, name, x, y, rng):
    """Birch in early spring: white trunk, thin drooping branches, sparse catkin tips."""
    h = rng.uniform(8.0, 12.0)
    P = PB()
    bark = mat("bark_birch")
    bark_d = mat("bark_dark")
    lean = (rng.uniform(-0.3, 0.3), rng.uniform(-0.3, 0.3))
    top = (x + lean[0], y + lean[1], h * 0.50)
    P.tube((x, y, -0.25), top, 0.19, 0.085, seg=7, material=bark)
    # dark birch bark patches
    for i in range(5):
        zz = rng.uniform(0.4, h * 0.44)
        t = zz / (h * 0.50)
        a = rng.uniform(0.0, TAU)
        P.box((x + lean[0] * t + math.cos(a) * 0.15,
               y + lean[1] * t + math.sin(a) * 0.15, zz),
              (0.10, 0.16, 0.05), bark_d, rot=(0, 0, a))
    # thin slightly drooping branches
    for i in range(7):
        az = TAU * i / 7.0 + rng.uniform(-0.4, 0.4)
        t = rng.uniform(0.45, 0.98)
        p0 = (x + lean[0] * t, y + lean[1] * t, max(1.0, h * 0.50 * t))
        d = Vector((math.cos(az) * rng.uniform(0.5, 0.9),
                    math.sin(az) * rng.uniform(0.5, 0.9),
                    rng.uniform(1.1, 1.9)))
        _branch(P, p0, d, h * rng.uniform(0.16, 0.26), 0.045, 0.022, 1, rng,
                bark_d, seg=4)
    # NOTE: early-spring birch keeps a bare crown — no foliage blobs, the thin
    # drooping branch network is what should read in the blue-hour silhouette.
    P.build(grp, name)
    return h



def _branch(P, p0, direction, length, r0, r1, depth, rng, material, seg=4):
    d = Vector(direction).normalized()
    p1 = (p0[0] + d.x * length, p0[1] + d.y * length, p0[2] + d.z * length)
    if p1[2] < 0.25 and depth <= 0:
        p1 = (p1[0], p1[1], 0.25)
    P.tube(p0, p1, r0, r1, seg=seg if depth else 4, material=material, caps=False)
    if depth <= 0:
        return
    n = rng.randint(2, 3) if depth > 1 else rng.randint(2, 3)
    for _ in range(n):
        nd = _spread_dir(d, rng, spread=rng.uniform(0.45, 0.85),
                         up=rng.uniform(0.25, 0.7))
        _branch(P, p1, nd, length * rng.uniform(0.5, 0.72), r1 * 0.75,
                r1 * 0.35, depth - 1, rng, material, seg=seg)


def _tree_bare(grp, name, x, y, rng):
    """Bare skeletal tree — the key post-punk silhouette (trunk + 8-15 branches)."""
    h = rng.uniform(6.0, 10.0)
    P = PB()
    bark = mat("bark_bare")
    lean = Vector((rng.uniform(-0.6, 0.6), rng.uniform(-0.4, 0.4), h * 0.5))
    top = (x + lean.x, y + lean.y, lean.z)
    P.tube((x, y, -0.25), top, 0.24, 0.10, seg=6, material=bark)
    # root flare
    for _ in range(3):
        a = rng.uniform(0.0, TAU)
        P.tube((x, y, 0.40), (x + math.cos(a) * 0.55, y + math.sin(a) * 0.55, 0.02),
               0.09, 0.03, seg=4, material=bark, caps=False)
    n_prim = rng.randint(5, 7)
    for i in range(n_prim):
        az = TAU * (i / n_prim) + rng.uniform(-0.5, 0.5)
        t = rng.uniform(0.42, 0.95)
        p0 = (x + lean.x * t, y + lean.y * t, max(0.6, lean.z * t))
        d = Vector((math.cos(az) * rng.uniform(0.6, 1.1),
                    math.sin(az) * rng.uniform(0.6, 1.1),
                    rng.uniform(1.0, 1.9)))
        depth = 2 if i < 2 else 1
        _branch(P, p0, d, h * rng.uniform(0.20, 0.32), 0.075,
                0.055 if depth > 1 else 0.035, depth, rng, bark)
    # vertical leader
    _branch(P, top, (rng.uniform(-0.2, 0.2), rng.uniform(-0.2, 0.2), 1.6),
            h * 0.22, 0.08, 0.03, 1, rng, bark)
    P.build(grp, name)
    return h


TREE_BUILDERS = {
    "poplar": _tree_poplar,
    "linden": _tree_linden,
    "birch": _tree_birch,
    "bare": _tree_bare,
}

ROW_TREE_KINDS = {
    "poplar_row": (("poplar", 0.52), ("bare", 0.38), ("linden", 0.10)),
    "linden_row": (("linden", 0.58), ("bare", 0.27), ("poplar", 0.15)),
    "mixed_row": (("poplar", 0.32), ("birch", 0.30), ("bare", 0.38)),
}
CLUSTER_TREE_KINDS = (("birch", 0.45), ("bare", 0.35), ("poplar", 0.20))

# rows that run next to lamp posts / fences need a small offset so trunks and
# lamp poles do not intersect.
TREE_ROW_CLAMPS = {
    "TREES_AVENUE_ROW": ("y", -71.2),
    "TREES_W_DRIVE": ("x", -50.7),
    "TREES_E_DRIVE": ("x", 50.7),
    "TREES_SCHOOL": ("y", 55.0),
}


ROUND2_TREE_CLAMPS = {
    "TREES_SOUTH_ROW": ("y", -91.5),   # planned line overlapped the south parking lot
}


def _pick_kind(table, rng):
    return rng.wpick([k for k, _w in table], [w for _k, w in table])


def _bus_stop_xy(plan):
    return [tuple(lm["pos"][:2]) for lm in plan.get("landmarks", [])
            if lm.get("type") == "bus_stop"]


def build_trees(parent, plan, rng, site, stats):
    grp = _sub(parent, "PROP_TREES")
    counts = {"poplar": 0, "linden": 0, "birch": 0, "bare": 0}
    zone_counts = {}
    positions = stats.setdefault("tree_positions", [])
    bus_xy = _bus_stop_xy(plan)
    for pz in plan.get("prop_zones", []):
        ztype = pz.get("type")
        if ztype not in ROW_TREE_KINDS:
            continue
        zid = pz["id"]
        step = float(pz.get("step", 9))
        pts = Site.row_points(pz["rect"], step, edge=1.4)
        clamp = ROUND2_TREE_CLAMPS.get(zid) or TREE_ROW_CLAMPS.get(zid)
        made = 0
        for i, (x, y) in enumerate(pts):
            x += rng.uniform(-0.8, 0.8)
            y += rng.uniform(-0.8, 0.8)
            if clamp:
                axis, val = clamp
                if axis == "x":
                    x = val + rng.uniform(-0.25, 0.25)
                else:
                    y = val + rng.uniform(-0.25, 0.25)
            if any(math.dist((x, y), b) < 5.6 for b in bus_xy):
                continue
            if not site.ok(x, y, road_margin=0.9, block_margin=1.0):
                continue
            if site.clash(x, y, 0.9):
                continue
            site.claim(x, y, 0.9)
            kind = _pick_kind(ROW_TREE_KINDS[ztype], rng)
            TREE_BUILDERS[kind](grp, f"PROP_TREE_{zid}_{i:02d}", x, y, rng)
            counts[kind] += 1
            positions.append((x, y))
            made += 1

        zone_counts[zid] = made
    stats["tree_rows"] = zone_counts
    stats["tree_counts"] = counts
    return counts



def build_tree_clusters(parent, plan, rng, site, stats):
    grp = None
    for ch in parent.children:
        if ch.name == "PROP_TREES":
            grp = ch
            break
    if grp is None:
        grp = _sub(parent, "PROP_TREES")

    counts = stats.setdefault("tree_counts", {"poplar": 0, "linden": 0,
                                              "birch": 0, "bare": 0})
    positions = stats.setdefault("tree_positions", [])
    clusters = []
    for pz in plan.get("prop_zones", []):
        if pz.get("type") != "birch_cluster":
            continue
        n = int(pz.get("count", 5))
        pts = site.place(pz["id"], n, pad=1.8, rng=rng, radius=1.4,
                         road_margin=0.8)
        clusters.append((pz["id"], len(pts)))
        for i, (x, y) in enumerate(pts):
            kind = _pick_kind(CLUSTER_TREE_KINDS, rng)
            TREE_BUILDERS[kind](grp, f"PROP_TREE_{pz['id']}_{i:02d}", x, y, rng)
            counts[kind] += 1
            positions.append((x, y))
    # a few extra bare trees in green strips / courtyard corners for silhouette
    EXTRA = [(-110, -16), (-103, -11), (-90, -18), (-76, -13), (-67, -19),
             (70, -14), (78, -18), (91, -12), (105, -17), (113, -13),
             (-70, 6.2), (-40, 6.2), (-12, 6.4), (18, 6.2), (44, 6.5), (86, 6.2),
             (-113, 34), (-60, 58), (30, 60), (100, 62), (-30, 26), (58, 28),
             # round-2 areas
             (-152, -78), (-146, -20), (-134, -76), (-136, 14), (-152, 44),
             (-120, 86), (-90, 88), (-44, 88), (70, 88), (110, 88), (146, 86),
             (-150, 112), (120, 112), (140, -50), (150, -78), (96, -96),
             (-100, -96), (-30, -94), (28, -96), (-120, 26), (60, 84)]
    made = 0
    bus_xy = _bus_stop_xy(plan)
    pav = [tuple(lm["pos"][:2]) for lm in plan.get("landmarks", [])
           if lm.get("type") == "pavilion"]
    for i, (x, y) in enumerate(EXTRA):
        x += rng.uniform(-1.4, 1.4)
        y += rng.uniform(-1.4, 1.4)
        if any(math.dist((x, y), b) < 5.0 for b in bus_xy):
            continue
        if not site.ok(x, y, road_margin=1.0):
            continue
        if site.clash(x, y, 1.4):
            continue
        site.claim(x, y, 1.4)
        _tree_bare(grp, f"PROP_TREE_EXTRA_{i:02d}", x, y, rng)
        counts["bare"] += 1
        positions.append((x, y))
        made += 1
    # generic scatter over the big lawns of the enlarged map (park, W2, south edge)
    lawn_made = 0
    for z in plan.get("zones", []):
        if z.get("kind") != "lawn":
            continue
        x0, y0, x1, y1 = z["rect"]
        area = (x1 - x0) * (y1 - y0)
        if area < 1200.0:
            continue
        n = min(6, max(1, int(area / 2400.0)))
        for k in range(n):
            for _try in range(40):
                x = rng.uniform(x0 + 2.0, x1 - 2.0)
                y = rng.uniform(y0 + 2.0, y1 - 2.0)
                if not site.ok(x, y, road_margin=1.2):
                    continue
                if site.clash(x, y, 1.6):
                    continue
                if any(math.dist((x, y), p) < 8.0 for p in pav):
                    continue
                site.claim(x, y, 1.6)
                kind = _pick_kind(CLUSTER_TREE_KINDS, rng)
                TREE_BUILDERS[kind](grp, f"PROP_TREE_LAWN_{z['id']}_{k}", x, y, rng)
                counts[kind] += 1
                positions.append((x, y))
                lawn_made += 1
                break
    stats["tree_clusters"] = clusters
    stats["tree_extra_bare"] = made
    stats["tree_lawn_scatter"] = lawn_made
    return counts




# ----------------------------------------------------------------------------
# playground equipment
# ----------------------------------------------------------------------------
def _patch(P, x, y, r, material, rng=None, seg=10, z=0.012):
    pts = []
    for i in range(seg):
        t = TAU * i / seg
        rr = r * (1.0 + (rng.uniform(-0.2, 0.2) if rng else 0.0))
        pts.append((x + rr * math.cos(t), y + rr * math.sin(t), z))
    P.add([tuple(p) for p in pts], [tuple(range(seg))], material)


def play_rocket(grp, name, x, y, rot, rng):
    """Ракета — sheet-metal rocket climbing frame."""
    P = PB()
    T = XF(x, y, rot)
    conc = mat("conc_dark")
    red = mat("paint_red")
    ochre = mat("paint_ochre")
    dark = mat("metal_dark")
    rust = mat("rust")
    P.tube(T(0, 0, 0.0), T(0, 0, 0.14), 1.30, 1.24, seg=16, material=conc)
    P.tube(T(0, 0, 0.14), T(0, 0, 2.55), 0.82, 0.68, seg=12, material=red)
    P.tube(T(0, 0, 2.55), T(0, 0, 4.55), 0.68, 0.09, seg=12, material=red)
    P.tube(T(0, 0, 4.55), T(0, 0, 5.20), 0.09, 0.02, seg=6, material=dark)
    # portholes (ring + dark glass) on the cone
    for (zz, aa) in ((2.05, 0.7), (1.45, 2.6), (1.75, 4.5)):
        r_at = 0.82 + (0.68 - 0.82) * (zz - 0.14) / (2.55 - 0.14)
        ca, sa = math.cos(aa), math.sin(aa)
        tx, ty = -sa, ca
        cx, cy = ca * r_at, sa * r_at
        ring = [T(cx + tx * 0.18 * math.cos(TAU * i / 12),
                  cy + ty * 0.18 * math.cos(TAU * i / 12),
                  zz + 0.18 * math.sin(TAU * i / 12)) for i in range(12)]
        P.polyline(ring, 0.030, seg=4, material=ochre, closed=True)
        nrm = (T.c * ca - T.s * sa, T.s * ca + T.c * sa, 0.0)
        P.disc(T(cx + ca * 0.03, cy + sa * 0.03, zz), nrm, 0.16, dark)
        P.disc(T(cx + ca * 0.035, cy + sa * 0.035, zz), nrm, 0.16, dark)
    # three fins
    for k in range(3):
        a = TAU * k / 3.0 + 0.35
        ca, sa = math.cos(a), math.sin(a)
        P.prism([T(ca * 0.78, sa * 0.78, 0.95), T(ca * 0.84, sa * 0.84, 0.03),
                 T(ca * 1.68, sa * 1.68, 0.03)], T.d(-sa, ca), 0.05, ochre)
    # ladder on the west side
    la = math.pi
    ca, sa = math.cos(la), math.sin(la)
    tx, ty = -sa, ca
    for side in (-1, 1):
        P.tube(T(ca * 0.90 + tx * side * 0.28, sa * 0.90 + ty * side * 0.28, 0.14),
               T(ca * 0.76 + tx * side * 0.28, sa * 0.76 + ty * side * 0.28, 2.25),
               0.038, 0.038, seg=5, material=dark)
        # upper handrails above the hatch
        P.tube(T(ca * 0.76 + tx * side * 0.28, sa * 0.76 + ty * side * 0.28, 2.25),
               T(ca * 0.62 + tx * side * 0.28, sa * 0.62 + ty * side * 0.28, 2.60),
               0.032, 0.028, seg=5, material=dark)
    for k in range(6):
        zz = 0.28 + k * (1.95 / 5.0)
        rr = 0.90 + (0.76 - 0.90) * ((zz - 0.14) / 2.11)
        P.tube(T(ca * rr + tx * -0.28, sa * rr + ty * -0.28, zz),
               T(ca * rr + tx * 0.28, sa * rr + ty * 0.28, zz),
               0.024, 0.024, seg=4, material=rust)
    P.build(grp, name)


def play_swing(grp, name, x, y, rot, rng):
    """Metal A-frame swing with two seats and chains."""
    P = PB()
    T = XF(x, y, rot)
    teal = mat("paint_teal")
    ochre = mat("paint_ochre")
    dark = mat("metal_dark")
    wood = mat("wood_red")
    span, H = 3.1, 2.35
    P.tube(T(0, 0, 0.02), T(0, 0, 0.10), 1.9, 1.7, seg=12, material=mat("conc_pad"))
    for side in (-1, 1):
        for leg in (-1, 1):
            foot = T(side * span / 2 + leg * 0.10, leg * 0.62, 0.0)
            apex = T(side * span / 2, 0.0, H)
            P.tube(foot, apex, 0.058, 0.045, seg=6, material=teal)
        # cross brace between the two legs
        P.tube(T(side * span / 2 + side * 0.16, -0.46, 0.95),
               T(side * span / 2 + side * 0.16, 0.46, 0.95),
               0.03, 0.03, seg=4, material=ochre)
    P.tube(T(-span / 2, 0.0, H), T(span / 2, 0.0, H), 0.06, 0.06, seg=8,
           material=teal)
    for seat_x in (-0.78, 0.78):
        for cx in (-0.21, 0.21):
            P.tube(T(seat_x + cx, 0.0, H - 0.03),
                   T(seat_x + cx, 0.0, 0.60), 0.014, 0.014, seg=4, material=dark)
        P.box(T(seat_x, 0.0, 0.57), (0.52, 0.22, 0.05), wood)
        # seat suspension brackets
        P.box(T(seat_x, 0.0, 0.63), (0.48, 0.05, 0.08), dark)
    P.build(grp, name)


def play_carousel(grp, name, x, y, rot, rng):
    """Round metal carousel with rails."""
    P = PB()
    T = XF(x, y, rot)
    conc = mat("conc_dark")
    blue = mat("paint_blue")
    red = mat("paint_red")
    dark = mat("metal_dark")
    P.tube(T(0, 0, 0.0), T(0, 0, 0.22), 1.55, 1.50, seg=16, material=conc)
    P.tube(T(0, 0, 0.22), T(0, 0, 0.32), 1.44, 1.44, seg=16, material=blue)
    P.tube(T(0, 0, 0.32), T(0, 0, 0.40), 0.20, 0.16, seg=8, material=dark)
    P.tube(T(0, 0, 0.40), T(0, 0, 1.00), 0.10, 0.08, seg=8, material=dark)
    for i in range(4):
        a = TAU * i / 4.0
        P.tube(T(math.cos(a) * 0.16, math.sin(a) * 0.16, 0.42),
               T(math.cos(a) * 1.36, math.sin(a) * 1.36, 0.42), 0.045, 0.035,
               seg=4, material=red)
    P.polyline([T(math.cos(TAU * i / 14.0) * 1.42, math.sin(TAU * i / 14.0) * 1.42,
                  0.98) for i in range(14)], 0.035, seg=4, material=blue,
               closed=True)
    P.polyline([T(math.cos(TAU * i / 14.0) * 1.42, math.sin(TAU * i / 14.0) * 1.42,
                  0.62) for i in range(14)], 0.03, seg=4, material=blue,
               closed=True)
    for i in range(5):
        a = TAU * i / 5.0
        P.tube(T(math.cos(a) * 1.40, math.sin(a) * 1.40, 0.40),
               T(math.cos(a) * 1.42, math.sin(a) * 1.42, 0.98), 0.025, 0.025,
               seg=4, material=dark)
    P.build(grp, name)


def play_sandbox(grp, name, x, y, rot, rng, size=2.9):
    """Concrete curb sandbox with (faded) wooden top plank border."""
    P = PB()
    T = XF(x, y, rot)
    conc = mat("conc")
    sand = mat("sand_play")
    wood = mat("wood_red")
    s = size / 2.0
    P.box(T(0, 0, 0.14), (size + 0.34, size + 0.34, 0.28), mat("conc_dark"))
    for (lx, ly, sx, sy) in ((0, -s, size + 0.30, 0.16), (0, s, size + 0.30, 0.16),
                             (-s, 0, 0.16, size + 0.30), (s, 0, 0.16, size + 0.30)):
        P.box(T(lx, ly, 0.20), (sx, sy, 0.24), conc)
        P.box(T(lx, ly, 0.36), (sx * 1.02, sy * 1.02, 0.07), wood)
    # sand surface
    P.box(T(0, 0, 0.27), (size - 0.2, size - 0.2, 0.06), sand)
    for _ in range(4):
        a = rng.uniform(0, TAU)
        rr = rng.uniform(0.1, s - 0.5)
        P.box(T(math.cos(a) * rr, math.sin(a) * rr, 0.31),
              (rng.uniform(0.16, 0.3), rng.uniform(0.12, 0.22), 0.02),
              mat("dirt"))
    P.build(grp, name)


def play_bars(grp, name, x, y, rot, rng):
    """Турник / parallel bars set."""
    P = PB()
    T = XF(x, y, rot)
    paint = mat("paint_teal")
    galv = mat("galv")
    conc = mat("conc_dark")
    # single horizontal bar
    for side in (-1, 1):
        P.tube(T(side * 0.75, 0, 0.0), T(side * 0.75, 0, 2.25), 0.05, 0.042,
               seg=6, material=paint)
        P.tube(T(side * 0.75, 0, 0.0), T(side * 0.75, 0, 0.16), 0.11, 0.10,
               seg=8, material=conc)
    P.tube(T(-0.75, 0, 2.22), T(0.75, 0, 2.22), 0.032, 0.032, seg=8,
           material=galv)
    # parallel bars
    bx = 2.3
    for side in (-1, 1):
        for sx in (-0.65, 0.65):
            P.tube(T(bx + side * 1.25, sx * 0.55, 0.0),
                   T(bx + side * 1.25, sx * 0.55, 1.45), 0.05, 0.045, seg=6,
                   material=paint)
            P.tube(T(bx + side * 1.25, sx * 0.55, 0.0),
                   T(bx + side * 1.25, sx * 0.55, 0.16), 0.10, 0.09, seg=8,
                   material=conc)
        P.tube(T(bx + side * 1.25, -0.55, 1.42), T(bx + side * 1.25, 0.55, 1.42),
               0.028, 0.028, seg=6, material=galv)
    P.tube(T(bx - 1.25, -0.55, 0.70), T(bx + 1.25, -0.55, 0.70), 0.02, 0.02,
           seg=4, material=galv)
    P.tube(T(bx - 1.25, 0.55, 0.70), T(bx + 1.25, 0.55, 0.70), 0.02, 0.02,
           seg=4, material=galv)
    P.build(grp, name)


def play_mushroom(grp, name, x, y, rot, rng):
    """Concrete mushroom climber with a seat ring."""
    P = PB()
    T = XF(x, y, rot)
    conc = mat("conc")
    red = mat("paint_red")
    wood = mat("wood")
    P.tube(T(0, 0, 0.0), T(0, 0, 0.14), 1.05, 1.00, seg=14, material=mat("conc_dark"))
    P.tube(T(0, 0, 0.14), T(0, 0, 2.30), 0.30, 0.24, seg=10, material=conc)
    P.tube(T(0, 0, 2.30), T(0, 0, 2.85), 1.18, 0.06, seg=16, material=red)
    # seat ring around the column
    P.polyline([T(math.cos(TAU * i / 12.0) * 0.58, math.sin(TAU * i / 12.0) * 0.58,
                  0.56) for i in range(12)], 0.06, seg=4, material=wood,
               closed=True)
    for i in range(4):
        a = TAU * i / 4.0
        P.tube(T(math.cos(a) * 0.26, math.sin(a) * 0.26, 0.56),
               T(math.cos(a) * 0.56, math.sin(a) * 0.56, 0.56), 0.035, 0.035,
               seg=4, material=mat("metal_dark"))
    P.build(grp, name)


def play_slide(grp, name, x, y, rot, rng):
    """Sheet-metal slide with ladder and side rails."""
    P = PB()
    T = XF(x, y, rot)
    galv = mat("galv")
    paint = mat("paint_teal")
    dark = mat("metal_dark")
    rust = mat("rust_light")

    P.prism([T(-0.32, 0.0, 2.06), T(0.32, 0.0, 2.06),
             T(0.32, 3.3, 0.36), T(-0.32, 3.3, 0.36)], (0, 0, 1), 0.05, galv)
    for side in (-1, 1):
        P.tube(T(side * 0.34, 0.02, 2.22), T(side * 0.34, 3.35, 0.48), 0.03,
               0.03, seg=5, material=paint)
    # top platform + ladder
    P.box(T(0, -0.42, 2.03), (0.95, 0.85, 0.07), paint)
    for side in (-1, 1):
        P.tube(T(side * 0.42, -0.82, 0.0), T(side * 0.42, -0.82, 2.06), 0.035,
               0.03, seg=5, material=dark)
        P.tube(T(side * 0.42, -0.30, 2.06), T(side * 0.42, -0.30, 1.35), 0.035,
               0.03, seg=5, material=dark)
    for k in range(6):
        zz = 0.22 + k * 0.34
        P.tube(T(-0.42, -0.82, zz), T(0.42, -0.82, zz), 0.022, 0.022, seg=4,
               material=rust if k % 2 else dark)
    P.build(grp, name)


def play_spring(grp, name, x, y, rot, rng):
    """Spring rider (лошадка)."""
    P = PB()
    T = XF(x, y, rot)
    conc = mat("conc_dark")
    blue = mat("paint_blue")
    red = mat("paint_red")
    dark = mat("metal_dark")
    P.box(T(0, 0, 0.05), (0.95, 0.55, 0.10), conc)
    # coil spring
    for k in range(4):
        z0 = 0.10 + k * 0.085
        P.polyline([T(math.cos(TAU * i / 10.0) * 0.09, math.sin(TAU * i / 10.0) * 0.09,
                      z0) for i in range(10)], 0.022, seg=4, material=dark,
                   closed=True)
    P.tube(T(0, 0, 0.44), T(0, 0, 0.52), 0.09, 0.07, seg=6, material=dark)
    # body
    P.box(T(0, 0, 0.66), (0.72, 0.24, 0.26), blue)
    P.box(T(0.40, 0, 0.80), (0.26, 0.20, 0.36), blue)
    P.box(T(0.50, 0, 0.94), (0.30, 0.16, 0.16), blue)
    for side in (-1, 1):
        for lx in (-0.24, 0.24):
            P.tube(T(lx, side * 0.09, 0.38), T(lx, side * 0.09, 0.56), 0.035,
                   0.03, seg=4, material=red)
    P.build(grp, name)


def play_hoop(grp, name, x, y, rot, rng):
    """Basketball hoop: pole + backboard + ring."""
    P = PB()
    T = XF(x, y, rot)
    paint = mat("paint_white")
    red = mat("paint_red")
    dark = mat("metal_dark")
    conc = mat("conc_dark")
    P.tube(T(0, 0, 0.0), T(0, 0, 0.20), 0.30, 0.26, seg=10, material=conc)
    P.tube(T(0, 0, 0.10), T(0, 0, 3.05), 0.075, 0.06, seg=8, material=dark)
    P.tube(T(0, 0.0, 2.85), T(0, -0.42, 3.10), 0.04, 0.04, seg=5, material=dark)
    P.box(T(0, -0.52, 3.45), (1.75, 0.06, 1.05), paint)
    P.box(T(0, -0.56, 3.12), (0.60, 0.02, 0.44), red)
    ring = [T(math.cos(TAU * i / 12.0) * 0.23, -0.86 + math.sin(TAU * i / 12.0) * 0.23,
              3.02) for i in range(12)]
    P.polyline(ring, 0.022, seg=4, material=red, closed=True)
    P.build(grp, name)


def play_goal(grp, name, x, y, rot, rng):
    """Small football goal (posts + crossbar + back braces)."""
    P = PB()
    T = XF(x, y, rot)
    paint = mat("paint_white")
    galv = mat("galv")
    width, height = 4.6, 2.0
    for side in (-1, 1):
        P.tube(T(side * width / 2, 0, 0.0), T(side * width / 2, 0, height), 0.055,
               0.05, seg=6, material=paint)
        P.tube(T(side * width / 2, 0, height - 0.10),
               T(side * width / 2, 1.35, 0.05), 0.04, 0.035, seg=5, material=galv)
    P.tube(T(-width / 2, 0, height), T(width / 2, 0, height), 0.055, 0.055,
           seg=6, material=paint)
    P.tube(T(-width / 2, 1.35, 0.05), T(width / 2, 1.35, 0.05), 0.03, 0.03,
           seg=4, material=galv)
    # simple net hint: vertical strings
    for i in range(7):
        xx = -width / 2 + 0.35 + i * (width - 0.7) / 6.0
        P.tube(T(xx, 0, height), T(xx, 1.3, 0.08), 0.008, 0.008, seg=3,
               material=mat("sign_white"), caps=False)
    P.build(grp, name)


PLAY_BUILDERS = {
    "rocket": (play_rocket, 2.0),
    "swing": (play_swing, 2.4),
    "carousel": (play_carousel, 2.0),
    "sandbox": (play_sandbox, 1.9),
    "bars": (play_bars, 2.2),
    "mushroom": (play_mushroom, 1.4),
    "slide": (play_slide, 2.0),
    "spring": (play_spring, 1.0),
    "hoop": (play_hoop, 1.4),
    "goal": (play_goal, 2.6),
}

# local offsets (lx, ly, rot) around the playground rect centre, mirror = +-1
PLAY_LAYOUTS = {
    "full": [
        ("rocket", -7.6, -3.2, 0.0),
        ("swing", 0.0, -3.2, 0.0),
        ("carousel", 7.2, -3.0, 0.0),
        ("bars", -7.0, 3.3, 0.0),
        ("mushroom", 0.0, 3.5, 0.0),
        ("sandbox", 7.2, 3.5, 0.0),
    ],
    "kindergarten": [
        ("sandbox", -9.0, -4.2, 0.0),
        ("swing", -3.0, -4.2, 0.0),
        ("carousel", 3.2, -4.4, 0.0),
        ("mushroom", 8.8, -4.4, 0.0),
        ("slide", -8.6, 4.2, math.pi),
        ("spring", -2.4, 4.4, 0.0),
        ("bars", 4.2, 4.2, 0.0),
        ("sandbox", 9.2, 4.2, 0.0),
    ],
    "sport_small": [
        ("hoop", -4.6, 0.0, math.pi / 2),
        ("hoop", 4.6, 0.0, -math.pi / 2),
        ("bars", 0.0, -4.6, 0.0),
        ("goal", 0.0, 5.1, 0.0),
    ],
}


def _usable_play_rect(site, rect):
    """Shrink a playground rect away from any carriageway it clips, and return
    (centre_x, centre_y, scale) so the standard layout still fits."""
    x0, y0, x1, y1 = rect
    ux0, uy0, ux1, uy1 = x0, y0, x1, y1
    for (rx0, ry0, rx1, ry1) in site.roads:
        if rx1 <= x0 or rx0 >= x1 or ry1 <= y0 or ry0 >= y1:
            continue
        ox = min(x1, rx1) - max(x0, rx0)
        oy = min(y1, ry1) - max(y0, ry0)
        if ox <= 0 or oy <= 0:
            continue
        if ox <= oy:
            # road is narrow in x -> pull the playground off it sideways
            if abs(rx0 - x0) < abs(x1 - rx1):
                ux0 = max(ux0, rx1 + 0.8)
            else:
                ux1 = min(ux1, rx0 - 0.8)
        else:
            if abs(ry0 - y0) < abs(y1 - ry1):
                uy0 = max(uy0, ry1 + 0.8)
            else:
                uy1 = min(uy1, ry0 - 0.8)
    cx, cy = (ux0 + ux1) / 2.0, (uy0 + uy1) / 2.0
    scale = min(1.0, (ux1 - ux0) / 20.0, (uy1 - uy0) / 12.0)
    return cx, cy, max(0.45, scale)


def build_playgrounds(parent, plan, rng, site, stats):
    grp = _sub(parent, "PROP_PLAY")
    built = []
    for pz in plan.get("prop_zones", []):
        if pz.get("type") != "playground":
            continue
        zid = pz["id"]
        kind = pz.get("kind", "full")
        layout = PLAY_LAYOUTS.get(kind, PLAY_LAYOUTS["full"])
        x0, y0, x1, y1 = pz["rect"]
        cx, cy, layout_scale = _usable_play_rect(site, pz["rect"])
        # mirror flag keeps courtyards asymmetric between east and west blocks
        last = zid.split("_")[-1]
        mirror = -1 if last in ("E", "E2") else 1
        # round 2: PLAY_W2 keeps the rocket as focal point at the west end
        if "W2" in zid:
            mirror = 1
        n = 0
        # trampled ground under the playground
        P0 = PB()
        _patch(P0, cx, cy, min(x1 - x0, y1 - y0) * 0.46, mat("dirt"), rng, seg=14,
               z=0.010)
        P0.build(grp, f"PROP_PLAY_{zid}_ground")
        for i, (ename, dx, dy, drot) in enumerate(layout):
            if ename not in PLAY_BUILDERS:
                continue
            fn, radius = PLAY_BUILDERS[ename]
            # adaptive: the round-2 playground rects can clip a road edge, so
            # squeeze the layout towards the centre before dropping an element
            chosen = None
            for f in (1.0, 0.85, 0.7):
                sc = layout_scale * f
                x = cx + dx * mirror * sc
                y = cy + dy * sc
                if site.ok(x, y, 0.0, 1.0, allow_reserved=True) and \
                        not site.clash(x, y, radius * 0.55):
                    chosen = (x, y)
                    break
            if chosen is None:
                continue
            x, y = chosen
            rot = drot if mirror > 0 else -drot
            site.claim(x, y, radius)
            fn(grp, f"PROP_PLAY_{zid}_{ename}{i}", x, y, rot, rng)
            n += 1
        built.append((zid, n))
    # basketball court markings for the school play area
    pz = site.prop.get("PLAY_SCHOOL")
    if pz:
        x0, y0, x1, y1 = pz["rect"]
        cx, cy = (x0 + x1) / 2.0, (y0 + y1) / 2.0
        P = PB()
        line = mat("sign_white")
        w, h = 10.0, 8.0
        for (ax, ay, bx, by) in ((cx - w / 2, cy - h / 2, cx + w / 2, cy - h / 2),
                                 (cx - w / 2, cy + h / 2, cx + w / 2, cy + h / 2),
                                 (cx - w / 2, cy - h / 2, cx - w / 2, cy + h / 2),
                                 (cx + w / 2, cy - h / 2, cx + w / 2, cy + h / 2),
                                 (cx - w / 2, cy, cx + w / 2, cy)):
            if abs(ax - bx) < 0.01:
                P.box((ax, (ay + by) / 2, 0.013), (0.08, abs(by - ay), 0.012), line)
            else:
                P.box(((ax + bx) / 2, ay, 0.013), (abs(bx - ax), 0.08, 0.012), line)
        P.box((cx, cy, 0.013), (0.08, h, 0.012), line)
        P.build(grp, "PROP_PLAY_SCHOOL_markings")
    stats["playgrounds"] = built
    return built


# ----------------------------------------------------------------------------
# courtyard furniture
# ----------------------------------------------------------------------------
def bench_variant(grp, name, x, y, facing, rng, variant=0):
    """Soviet bench variants: slat bench, concrete-leg bench, backless, broken."""
    P = PB()
    T = XF(x, y, facing)
    wood = mat("wood") if variant != 3 else mat("wood_dark")
    legs = mat("metal_dark") if variant in (0, 3) else mat("conc")
    # Every part is placed through T (which yaws its CENTER around the bench
    # origin), so each part's own orientation must also carry the bench yaw.
    # PB.box() applies its `rot` Euler in world axes (Euler XYZ = Rz*Ry*Rx), so
    # the local -10 deg backrest lean is composed as (lean, 0, facing) exactly
    # like kit.bench().  Passing only the world-X lean here was the bench_variant
    # defect behind the 75 "rotated part geometry" benches: planks were yawed in
    # position but not in orientation.
    for k, ly in enumerate((-0.16, 0.0, 0.16)):
        if variant == 3 and k == 1:
            continue
        P.box(T(0, ly, 0.45), (1.82, 0.13, 0.045), wood, rot=(0.0, 0.0, facing))
    if variant != 2:
        for k, zz in enumerate((0.66, 0.80, 0.94)):
            if variant == 3 and k == 2:
                continue
            P.box(T(0, 0.25 + k * 0.014, zz), (1.82, 0.045, 0.10), wood,
                  rot=(math.radians(-10), 0.0, facing))
        for side in (-1, 1):
            P.box(T(side * 0.80, 0.28, 0.72), (0.05, 0.08, 0.60), legs,
                  rot=(0.0, 0.0, facing))
    for side in (-1, 1):
        P.box(T(side * 0.74, 0.0, 0.22), (0.09, 0.50, 0.45), legs,
              rot=(0.0, 0.0, facing))
        if variant == 1:
            P.box(T(side * 0.74, 0.0, 0.10), (0.16, 0.62, 0.20), mat("conc_dark"),
                  rot=(0.0, 0.0, facing))
    P.build(grp, name)


def dumpster_area(grp, name, x, y, rot, rng, n=2):
    """Concrete waste-bin enclosure with rusty wheeled dumpsters."""
    P = PB()
    T = XF(x, y, rot)
    conc = mat("conc")
    conc_d = mat("conc_dark")
    pad_w = 3.9 if n >= 2 else 2.8
    P.box(T(0, 0, 0.05), (pad_w + 0.6, 2.4, 0.10), conc_d)
    # three low concrete walls (open on the road side)
    P.box(T(0, 1.10, 0.55), (pad_w + 0.5, 0.20, 1.10), conc)
    P.box(T(-(pad_w + 0.3) / 2, 0.0, 0.55), (0.20, 2.3, 1.10), conc)
    P.box(T((pad_w + 0.3) / 2, 0.0, 0.55), (0.20, 2.3, 1.10), conc)
    for i in range(n):
        kit.dumpster(grp, f"{name}_dump{i}", T(-0.95 + i * 1.9, 0.15, 0.06), rot)
    kit.trash_bin(grp, name + "_bin", T(pad_w / 2 + 0.75, -0.5, 0.0))
    P.build(grp, name + "_enclosure")


def laundry_frame(grp, name, x, y, rot, rng, span=5.0):
    """Concrete posts + wires + hanging laundry (Soviet drying frame)."""
    P = PB()
    T = XF(x, y, rot)
    conc = mat("conc_light")
    wire = mat("metal_dark")
    for side in (-1, 1):
        P.box(T(side * span / 2, 0.0, 1.16), (0.22, 0.22, 2.32), conc)
        P.box(T(side * span / 2, 0.0, 2.36), (0.30, 0.30, 0.08), conc)
        # small braces
        P.tube(T(side * span / 2, 0.0, 2.10), T(side * (span / 2 - 0.28), 0.0, 1.92),
               0.03, 0.02, seg=4, material=conc, caps=False)
    heights = (2.16, 1.98, 1.80)
    for hz in heights:
        pts = [T(-span / 2, 0, hz), T(-span / 6.0, 0, hz - 0.07),
               T(span / 6.0, 0, hz - 0.07), T(span / 2, 0, hz)]
        P.polyline(pts, 0.012, seg=4, material=wire)
    cloths = [mat("cloth_grey"), mat("cloth_white"), mat("cloth_blue"),
              mat("cloth_olive"), mat("cloth_red")]
    for i in range(rng.randint(4, 7)):
        t = rng.uniform(-0.40, 0.40) * span
        hz = rng.pick(heights)
        w = rng.uniform(0.40, 0.72)
        hh = rng.uniform(0.45, 0.95)
        u = (T.c, T.s, 0.0)
        P.quad(T(t, rng.uniform(-0.02, 0.02), hz - hh / 2.0 - 0.02), u,
               (0, 0, 1), w, hh, rng.pick(cloths))
    P.build(grp, name)


def build_laundry(parent, plan, rng, site, stats):
    grp = _sub(parent, "PROP_LAUNDRY")
    out = []
    for pz in plan.get("prop_zones", []):
        if pz.get("type") != "laundry":
            continue
        zid = pz["id"]
        x0, y0, x1, y1 = pz["rect"]
        n = int(pz.get("count", 3))
        # round-2 W2 laundry shares its rect with the new playground: keep to the
        # south edge so the frames sit behind the equipment line
        y = (y0 + 0.5) if "W2" in zid else ((y0 + y1) / 2.0 - 0.2)
        for i in range(n):
            x = x0 + 1.6 + (x1 - x0 - 3.2) * (i / max(1, n - 1))
            if not site.ok(x, y, 0.0, 1.0, allow_reserved=True):
                continue
            if site.clash(x, y, 0.5):
                continue
            site.claim(x, y, 1.2)
            laundry_frame(grp, f"PROP_LAUNDRY_{pz['id']}_{i}", x, y, 0.0, rng)
            out.append((round(x, 1), round(y, 1)))
    stats["laundry"] = out



DUMPS_POS = {
    # zone id -> (x, y, rot) : the planned rects overlap building margins, so the
    # waste areas sit in the nearest real courtyard corner instead.
    "DUMPS_W": [(-96.0, -51.4, math.pi / 2)],
    "DUMPS_E": [(96.0, -51.4, -math.pi / 2)],
    "DUMPS_KG": [(-6.0, 42.0, 0.0)],
}


def build_dumpsters(parent, plan, rng, site, stats):
    grp = _sub(parent, "PROP_DUMPS")
    out = []
    for pz in plan.get("prop_zones", []):
        if pz.get("type") != "dumpster":
            continue
        zid = pz["id"]
        n = int(pz.get("count", 1))
        x0, y0, x1, y1 = pz["rect"]
        cx, cy = (x0 + x1) / 2.0, (y0 + y1) / 2.0
        spots = list(DUMPS_POS.get(zid, []))
        if not spots:
            # grid scan outward from the planned rect (new round-2 zones may sit
            # inside a building margin, e.g. DUMPS_W2)
            for rad in (0.0, 2.0, 4.0, 6.0, 8.0, 10.0, 12.0):
                for k in range(12):
                    a = TAU * k / 12.0
                    spots.append((cx + math.cos(a) * rad,
                                  cy + math.sin(a) * rad, 0.0))
        chosen = None
        for (x, y, rot) in spots:
            if site.ok(x, y, 0.5, 1.0, allow_reserved=True) and \
                    not site.clash(x, y, 1.4):
                chosen = (x, y, rot)
                break
        if chosen is None:
            continue
        site.claim(chosen[0], chosen[1], 2.2)
        dumpster_area(grp, f"PROP_DUMPS_{zid}", chosen[0], chosen[1], chosen[2],
                      rng, n=n)
        out.append((round(chosen[0], 1), round(chosen[1], 1)))
    stats["dumpsters"] = out



def transformer_box(grp, name, x, y, rot, rng):
    """Green metal transformer kiosk with warning stripes + lightning sign."""
    P = PB()
    T = XF(x, y, rot)
    body = mat("paint_green")
    dark = mat("metal_dark")
    P.box(T(0, 0, 0.07), (1.80, 1.10, 0.14), mat("conc_dark"))
    P.box(T(0, 0, 1.15), (1.54, 0.90, 2.02), body)
    P.box(T(0, 0, 2.20), (1.64, 0.98, 0.10), mat("metal_mid"))
    # door + vents on the front (local -y)
    P.box(T(0.0, -0.455, 1.22), (1.18, 0.02, 1.72), body)
    for i in range(3):
        P.box(T(-0.45, -0.47, 1.55 + i * 0.14), (0.34, 0.02, 0.05), dark)
    P.box(T(-0.60, -0.47, 1.25), (0.05, 0.03, 0.30), dark)
    # diagonal warning stripes (front)
    for k in range(6):
        lx = -0.66 + k * 0.26
        m = mat("warning_yellow") if k % 2 == 0 else mat("warning_dark")
        u = (T.c * 0.82, T.s * 0.82, 0.32)
        v = (T.c * -0.32, T.s * -0.32, 0.82)
        P.quad(T(lx, -0.468, 0.45), u, v, 0.22, 0.60, m)

    for side in (-1, 1):
        for k in range(4):
            ly = -0.30 + k * 0.22
            m = mat("warning_yellow") if k % 2 == 0 else mat("warning_dark")
            u = (T.c * -0.30, T.s * -0.30, 0.30)
            v = (0, 0, 0.80)
            P.quad(T(side * 0.772, ly, 0.45), u, v, 0.20, 0.55, m)
    # lightning triangle on the door
    tri = [T(-0.20, -0.475, 1.62), T(0.20, -0.475, 1.62), T(0.0, -0.475, 1.18)]
    P.prism(tri, (T.s * -1.0, T.c, 0.0), 0.02, mat("warning_yellow"))
    zbx = [T(0.03, -0.49, 1.55), T(-0.05, -0.49, 1.41), T(0.05, -0.49, 1.40),
           T(-0.02, -0.49, 1.25)]
    P.polyline(zbx, 0.016, seg=3, material=mat("warning_dark"))
    P.build(grp, name)
    # МОЛНИЯ warning plate beside the door
    try:
        nrm = (T.s, -T.c, 0.0)
        kit.text3d(grp, name + "_sign", "МОЛНИЯ", T(0.0, -0.48, 2.02), 0.10,
                   mat("warning_dark"), rot=(math.radians(90), 0, -rot))
    except Exception:  # noqa: BLE001
        pass


def payphone_booth(grp, name, x, y, rot, rng):
    P = PB()
    T = XF(x, y, rot)
    metal = mat("booth")
    glass = mat("glass_dim")
    dark = mat("metal_dark")
    P.box(T(0, 0, 0.07), (1.18, 1.18, 0.14), mat("conc_dark"))
    for sx in (-1, 1):
        for sy in (-1, 1):
            P.box(T(sx * 0.50, sy * 0.50, 1.24), (0.10, 0.10, 2.30), metal)
    P.box(T(0, 0, 2.46), (1.24, 1.24, 0.12), metal)
    P.box(T(0, 0, 2.56), (1.10, 1.10, 0.06), mat("metal_mid"))
    # glazing: back + both sides (door side open)
    P.quad(T(0, 0.50, 1.55), (1, 0, 0), (0, 0, 1), 0.86, 1.35, glass)
    P.quad(T(-0.50, 0, 1.55), (0, 1, 0), (0, 0, 1), 0.86, 1.35, glass)
    P.quad(T(0.50, 0, 1.55), (0, 1, 0), (0, 0, 1), 0.86, 1.35, glass)
    # lower metal panels
    P.box(T(0, 0.50, 0.62), (0.90, 0.04, 0.72), metal)
    P.box(T(-0.50, 0, 0.62), (0.04, 0.90, 0.72), metal)
    P.box(T(0.50, 0, 0.62), (0.04, 0.90, 0.72), metal)
    # payphone unit
    P.box(T(0, 0.40, 1.35), (0.32, 0.16, 0.52), dark)
    P.box(T(0, 0.32, 1.30), (0.20, 0.06, 0.16), mat("metal_mid"))
    P.tube(T(0.17, 0.40, 1.42), T(0.17, 0.40, 1.18), 0.028, 0.028, seg=5,
           material=mat("warning_dark"))
    # shelf
    P.box(T(0, 0.42, 1.02), (0.44, 0.18, 0.04), mat("metal_mid"))
    # ТЕЛЕФОН sign above the doorway
    try:
        kit.text3d(grp, name + "_sign", "ТЕЛЕФОН", T(0, -0.62, 2.34), 0.11,
                   mat("sign_white"), rot=(math.radians(90), 0, -rot))
    except Exception:  # noqa: BLE001
        pass
    P.build(grp, name)


def mailbox_bank(grp, name, x, y, rot, rng):
    P = PB()
    T = XF(x, y, rot)
    mail = mat("mailbox")
    dark = mat("metal_dark")
    P.box(T(0, 0, 1.34), (1.38, 0.34, 1.18), mail)
    P.box(T(0, 0, 1.98), (1.44, 0.40, 0.07), dark)
    for sx in (-1, 1):
        P.box(T(sx * 0.54, 0, 0.36), (0.08, 0.28, 0.72), dark)
    for row in range(4):
        for col in range(3):
            lx = -0.43 + col * 0.43
            lz = 0.92 + row * 0.27
            P.box(T(lx, -0.18, lz), (0.38, 0.02, 0.22), mat("metal_mid"))
            P.box(T(lx, -0.196, lz - 0.06), (0.12, 0.015, 0.02), dark)
    P.build(grp, name)


def hydrant(grp, name, x, y, rot, rng):
    P = PB()
    T = XF(x, y, rot)
    red = mat("paint_red")
    dark = mat("metal_dark")
    P.tube(T(0, 0, 0.0), T(0, 0, 0.92), 0.15, 0.12, seg=8, material=red)
    P.tube(T(0, 0, 0.92), T(0, 0, 1.08), 0.10, 0.05, seg=6, material=red)
    P.tube(T(0, 0, 0.74), T(0, 0, 0.86), 0.19, 0.19, seg=8, material=dark)
    for side in (-1, 1):
        P.tube(T(side * 0.14, 0, 0.64), T(side * 0.28, 0, 0.64), 0.055, 0.05,
               seg=6, material=dark)
    P.build(grp, name)


def planter(grp, name, x, y, rot, rng, kind=0):
    P = PB()
    T = XF(x, y, rot)
    if kind == 0:  # concrete box
        P.box(T(0, 0, 0.30), (1.30, 1.30, 0.60), mat("conc"))
        P.box(T(0, 0, 0.62), (1.14, 1.14, 0.08), mat("dirt"))
    elif kind == 1:  # painted tyre
        P.tube(T(0, 0, 0.0), T(0, 0, 0.24), 0.55, 0.55, seg=14, material=mat("metal_dark"))
        P.tube(T(0, 0, 0.10), T(0, 0, 0.15), 0.57, 0.57, seg=14, material=mat("paint_white"))
        P.disc(T(0, 0, 0.20), (0, 0, 1), 0.50, mat("dirt"), seg=14)
    else:  # concrete pipe
        P.tube(T(0, 0, 0.0), T(0, 0, 0.62), 0.44, 0.42, seg=12, material=mat("conc"))
        P.disc(T(0, 0, 0.55), (0, 0, 1), 0.40, mat("dirt"), seg=12)
    # dead twigs
    base_z = 0.62 if kind == 0 else 0.55
    for k in range(rng.randint(4, 7)):
        a = rng.uniform(0, TAU)
        rr = rng.uniform(0.05, 0.38)
        h = rng.uniform(0.35, 0.75)
        p0 = T(math.cos(a) * rr, math.sin(a) * rr, base_z)
        p1 = (p0[0] + math.cos(a) * 0.18, p0[1] + math.sin(a) * 0.18, base_z + h)
        P.tube(p0, p1, 0.015, 0.004, seg=3, material=mat("weed_dark"),
               caps=False)
    P.build(grp, name)


MANHOLE_SPOTS = [(-83, -45), (-71, -40), (-90, -38), (83, -45), (71, -40),
                 (90, -38), (-12, -50), (14, -46), (0, -60), (-22, -28),
                 (30, -45), (-48, -30), (66, -30), (40, -30), (12, 30)]

BENCH_SPOTS = [
    # (x, y, facing, variant)  facing 0 -> looks south (-Y)
    (-92.0, -36.2, 0.0, 0), (-79.0, -36.2, 0.0, 1),
    (-92.0, -50.4, math.pi, 2), (-79.0, -50.4, math.pi, 0),
    (-57.0, -41.5, math.pi / 2, 1), (-57.0, -45.0, math.pi / 2, 3),
    (92.0, -36.2, 0.0, 1), (79.0, -36.2, 0.0, 0),
    (92.0, -50.4, math.pi, 0), (79.0, -50.4, math.pi, 2),
    (57.0, -41.5, -math.pi / 2, 1), (57.0, -45.0, -math.pi / 2, 0),
    (-18.0, -8.6, 0.0, 0), (-6.0, -8.6, 0.0, 2), (6.0, -8.6, 0.0, 0),
    (18.0, -8.6, 0.0, 1),
    (-29.0, 41.4, math.pi, 0), (-12.5, 41.4, math.pi, 2),
    (-31.0, 27.4, 0.0, 1), (-10.0, 27.4, 0.0, 0),
    (-46.0, -43.0, math.pi / 2, 2), (46.0, -43.0, -math.pi / 2, 2),
]

TRANSFORMER_SPOTS = [(-99.2, -35.6, 0.0), (99.2, -35.6, 0.0),
                     (-6.8, 27.8, 0.0), (32.4, -39.6, math.pi / 2),
                     (-32.4, -39.6, -math.pi / 2), (-44.6, 15.0, math.pi / 2)]

PAYPHONE_SPOTS = [(-36.8, -71.7, math.pi), (-86.2, -36.2, math.pi),
                  (86.2, -36.2, math.pi)]

MAILBOX_SPOTS = [(-73.5, -36.8, 0.0), (73.5, -36.8, 0.0),
                 (-19.5, 25.2, 0.0)]

HYDRANT_SPOTS = [(-52.6, -46.0, 0.0), (56.4, -46.0, 0.0), (42.0, 3.1, 0.0),
                 (-30.0, -46.2, 0.0)]

BIN_SPOTS = [(-88.0, -35.4), (-74.0, -35.4), (88.0, -35.4), (74.0, -35.4),
             (-30.0, 25.4), (-12.0, 25.4), (14.0, -9.4), (-14.0, -9.4),
             (24.0, 33.4), (40.0, 33.4), (-45.0, -15.6), (2.0, -70.6),
             (-48.0, -70.6), (60.0, -31.4), (-60.0, -31.4)]


def build_furniture(parent, plan, rng, site, stats):
    grp = _sub(parent, "PROP_FURNITURE")
    n = 0
    # plaza bench row from the prop zone (classic kit bench reused)
    pz = site.prop.get("BENCH_PLAZA")
    if pz:
        x0, y0, x1, y1 = pz["rect"]
        count = int(pz.get("count", 8))
        y = (y0 + y1) / 2.0
        for i in range(count):
            x = x0 + 1.6 + (x1 - x0 - 3.2) * (i / max(1, count - 1))
            if not site.ok(x, y, 0.4, 1.0):
                continue
            site.claim(x, y, 1.4)
            kit.bench(grp, f"PROP_BENCH_PLAZA_{i}", (x, y, 0.0), 0.0)
            n += 1
    for i, (x, y, facing, variant) in enumerate(BENCH_SPOTS):
        if not site.ok(x, y, 0.4, 1.0, allow_reserved=True):
            continue
        if site.clash(x, y, 0.6):
            continue
        site.claim(x, y, 1.2)
        bench_variant(grp, f"PROP_BENCH_{i:02d}", x, y, facing, rng, variant)
        n += 1
    stats["benches"] = n

    # planters (plaza) + courtyard planters
    planters = 0
    pts = site.place("PLANTERS", int(site.prop.get("PLANTERS", {}).get("count", 6)),
                     pad=2.0, rng=rng, radius=1.5, road_margin=0.4,
                     allow_reserved=True)
    for i, (x, y) in enumerate(pts):
        planter(grp, f"PROP_PLANTER_{i}", x, y, 0.0, rng, kind=i % 3)
        planters += 1
    for i, (x, y) in enumerate(((-23.0, 26.9), (-34.5, -37.4), (34.5, -37.4))):
        if not site.ok(x, y, 0.4, 1.0, allow_reserved=True) or site.clash(x, y, 1.0):
            continue
        site.claim(x, y, 1.2)
        planter(grp, f"PROP_PLANTER_X{i}", x, y, 0.0, rng, kind=1)
        planters += 1
    stats["planters"] = planters

    # transformer kiosks
    tf = 0
    for i, (x, y, rot) in enumerate(TRANSFORMER_SPOTS):
        if not site.ok(x, y, 0.4, 1.0, allow_reserved=True) or site.clash(x, y, 1.4):
            continue
        site.claim(x, y, 1.6)
        transformer_box(grp, f"PROP_TRANSFORMER_{i}", x, y, rot, rng)
        tf += 1
    stats["transformers"] = tf

    # payphones
    for i, (x, y, rot) in enumerate(PAYPHONE_SPOTS):
        if not site.ok(x, y, 0.2, 1.0, allow_reserved=True) or site.clash(x, y, 0.8):
            continue
        site.claim(x, y, 1.1)
        payphone_booth(grp, f"PROP_PAYPHONE_{i}", x, y, rot, rng)

    # mailbox banks
    for i, (x, y, rot) in enumerate(MAILBOX_SPOTS):
        if not site.ok(x, y, 0.2, 1.0, allow_reserved=True) or site.clash(x, y, 0.6):
            continue
        site.claim(x, y, 0.9)
        mailbox_bank(grp, f"PROP_MAILBOX_{i}", x, y, rot, rng)

    # hydrants
    for i, (x, y, rot) in enumerate(HYDRANT_SPOTS):
        if not site.ok(x, y, 0.2, 1.0, allow_reserved=True) or site.clash(x, y, 0.5):
            continue
        site.claim(x, y, 0.7)
        hydrant(grp, f"PROP_HYDRANT_{i}", x, y, rot, rng)

    # litter bins
    for i, (x, y) in enumerate(BIN_SPOTS):
        if not site.ok(x, y, 0.3, 1.0, allow_reserved=True) or site.clash(x, y, 0.4):
            continue
        site.claim(x, y, 0.55)
        kit.trash_bin(grp, f"PROP_BIN_{i:02d}", (x, y, 0.0))

    # --- round 2: benches / bins / planters in the new courtyards & market square
    NEW_BENCH_ZONES = ("W2_COURT", "SOUTH_YARD_W", "SOUTH_YARD_MID", "E2_COURT",
                       "E_MARKET_SQUARE", "PARK_ENTRY_W", "PARK_ENTRY_E",
                       "PARK_LAWN")
    nb2 = 0
    for zid in NEW_BENCH_ZONES:
        z = site.zones.get(zid)
        if not z:
            continue
        x0, y0, x1, y1 = z["rect"]
        cx, cy = (x0 + x1) / 2.0, (y0 + y1) / 2.0
        w, h = x1 - x0, y1 - y0
        if zid == "PARK_LAWN":
            cands = []          # park benches are handled by build_park (pond facing)
        elif w >= h:
            cands = [(cx - w * 0.24, y0 + 1.5, math.pi),
                     (cx + w * 0.24, y1 - 1.5, 0.0)]
        else:
            cands = [(x0 + 1.5, cy - h * 0.22, math.pi / 2),
                     (x1 - 1.5, cy + h * 0.22, -math.pi / 2)]
        for bi, (bx, by, bf) in enumerate(cands):
            if not site.ok(bx, by, 0.4, 1.0, True):
                continue
            if site.clash(bx, by, 0.8):
                continue
            site.claim(bx, by, 1.2)
            bench_variant(grp, f"PROP_BENCH_{zid}_{bi}", bx, by, bf, rng,
                          nb2 % 4)
            nb2 += 1
            if nb2 % 2 == 0:
                binx = bx + rng.uniform(-1.4, 1.4)
                biny = by + rng.uniform(-1.6, 1.6)
                if site.ok(binx, biny, 0.3, 1.0, True) and \
                        not site.clash(binx, biny, 0.5):
                    site.claim(binx, biny, 0.6)
                    kit.trash_bin(grp, f"PROP_BIN_R2_{nb2}", (binx, biny, 0.0))
    stats["benches_r2"] = nb2
    # prop zones of type "benches" (e.g. BENCH_PARK) — semi-random, facing centre
    bp = 0
    for pz in plan.get("prop_zones", []):
        if pz.get("type") != "benches" or pz["id"] == "BENCH_PLAZA":
            continue
        zid = pz["id"]
        x0, y0, x1, y1 = pz["rect"]
        cx, cy = (x0 + x1) / 2.0, (y0 + y1) / 2.0
        count = int(pz.get("count", 6))
        placed = 0
        for _try in range(count * 10):
            if placed >= count:
                break
            bx = rng.uniform(x0 + 1.5, x1 - 1.5)
            by = rng.uniform(y0 + 1.5, y1 - 1.5)
            if not site.ok(bx, by, 0.4, 1.0, True):
                continue
            if site.clash(bx, by, 1.0):
                continue
            site.claim(bx, by, 1.3)
            facing = math.atan2(cx - bx, -(cy - by))
            bench_variant(grp, f"PROP_BENCH_{zid}_{placed}", bx, by, facing, rng,
                          placed % 4)
            placed += 1
        bp += placed
    stats["benches_propzones"] = bp
    # planters at the market square + park entrances
    rp = 0
    for (px, py) in ((122.0, -44.0), (122.0, -56.0), (126.0, -46.5),
                     (-124.5, 93.5), (114.5, 93.5), (-128.0, 94.5),
                     (118.0, 95.5), (60.0, 92.5), (-58.0, 92.0)):
        if not site.ok(px, py, 0.3, 1.0, True) or site.clash(px, py, 1.0):
            continue
        site.claim(px, py, 1.2)
        planter(grp, f"PROP_PLANTER_R2_{rp}", px, py, 0.0, rng, kind=rp % 3)
        rp += 1
    stats["planters_r2"] = rp


    # manholes (flat discs, no claims needed)
    P = PB()
    for i, (x, y) in enumerate(MANHOLE_SPOTS):
        if not site.ok(x, y, 0.0, 0.0, allow_reserved=True):
            continue
        P.disc((x, y, 0.014), (0, 0, 1), 0.36, mat("manhole"), seg=12)
        P.polyline([(x + 0.40 * math.cos(TAU * k / 10.0),
                     y + 0.40 * math.sin(TAU * k / 10.0), 0.012)
                    for k in range(10)], 0.03, seg=3, material=mat("conc_dark"),
                   closed=True)
    # round 2: manholes across the new courts / parking / market square
    for zid in ("W2_COURT", "SOUTH_YARD_W", "SOUTH_YARD_MID", "E_MARKET_SQUARE",
                "SOUTH_PARKING_W", "SOUTH_PARKING_E", "E_MARKET_PARKING",
                "PARK_PATH_ZONE", "W2_BANYA_YARD", "W2_GARAGE_LANE"):
        z = site.zones.get(zid)
        if not z:
            continue
        zx0, zy0, zx1, zy1 = z["rect"]
        nm = min(3, max(1, int(((zx1 - zx0) * (zy1 - zy0)) / 1000.0)))
        for _k in range(nm):
            mx = rng.uniform(zx0 + 2.0, max(zx0 + 2.1, zx1 - 2.0))
            my = rng.uniform(zy0 + 2.0, max(zy0 + 2.1, zy1 - 2.0))
            if not site.ok(mx, my, 0.1, 1.0, True):
                continue
            P.disc((mx, my, 0.014), (0, 0, 1), 0.36, mat("manhole"), seg=12)
    P.build(grp, "PROP_MANHOLES")



# ----------------------------------------------------------------------------
# fences
# ----------------------------------------------------------------------------
def _split_spans(L, gates):
    spans = [(0.0, L)]
    for (a, b) in gates:
        a = max(0.0, min(L, a))
        b = max(0.0, min(L, b))
        if b <= a:
            continue
        out = []
        for (s, e) in spans:
            if b <= s or a >= e:
                out.append((s, e))
                continue
            if a > s:
                out.append((s, a))
            if b < e:
                out.append((b, e))
        spans = out
    return [(s, e) for (s, e) in spans if e - s > 0.9]


def fence_run(grp, name, p0, p1, kind="concrete", height=2.0, gates=(),
              site=None, claim_radius=1.0):
    p0 = Vector(p0)
    p1 = Vector(p1)
    d = p1 - p0
    L = d.length
    if L < 0.5:
        return
    u = d / L
    spans = _split_spans(L, gates)
    for i, (s, e) in enumerate(spans):
        kit.fence(grp, f"{name}_{i}", tuple(p0 + u * s), tuple(p0 + u * e),
                  height=height, kind=kind, rng=None)
    # gate pillars
    for (a, b) in gates:
        for t in (a, b):
            if t <= 0.05 or t >= L - 0.05:
                continue
            px = p0 + u * t
            kit.box(grp, f"{name}_pillar_{int(t * 10)}", (px.x, px.y, height / 2 + 0.15),
                    (0.46, 0.46, height + 0.3), mat("conc"))
            kit.box(grp, f"{name}_pillarcap_{int(t * 10)}", (px.x, px.y, height + 0.36),
                    (0.56, 0.56, 0.10), mat("conc_light"))
    if site is not None:
        n = max(1, int(L / 2.0))
        for i in range(n + 1):
            p = p0 + u * (L * i / n)
            site.claim(p.x, p.y, claim_radius)


def low_metal_fence(grp, name, p0, p1, height=1.25, gates=(), site=None):
    """Green metal bar fence merged into one mesh (long courtyard runs)."""
    P = PB()
    p0 = Vector(p0)
    d = Vector(p1) - p0
    L = d.length
    if L < 0.5:
        return
    u = d / L
    metal = mat("paint_green")
    spans = _split_spans(L, gates)
    for (s, e) in spans:
        nseg = max(1, int(round((e - s) / 1.6)))
        for i in range(nseg + 1):
            p = p0 + u * (s + (e - s) * i / nseg)
            P.box((p.x, p.y, height / 2), (0.07, 0.07, height), metal)
        for hf in (0.18, 0.96):
            mid = p0 + u * ((s + e) / 2)
            P.box((mid.x, mid.y, height * hf),
                  (abs(d.x) * (e - s) / L + 0.05, abs(d.y) * (e - s) / L + 0.05,
                   0.045), metal)
        nb = max(2, int((e - s) / 0.45))
        for i in range(nb):
            p = p0 + u * (s + (e - s) * (i + 0.5) / nb)
            P.box((p.x, p.y, height * 0.58), (0.022, 0.022, height * 0.72), metal)
    P.build(grp, name)
    if site is not None:
        n = max(1, int(L / 2.0))
        for i in range(n + 1):
            p = p0 + u * (L * i / n)
            site.claim(p.x, p.y, 0.9)


def hedge_run(grp, name, p0, p1, height=0.75, width=1.15, gaps=()):
    P = PB()
    p0 = Vector(p0)
    d = Vector(p1) - p0
    L = d.length
    if L < 0.5:
        return
    u = d / L
    green = mat("paint_green")
    n = max(1, int(L / 1.15))
    for i in range(n):
        t = (i + 0.5) * L / n
        if any(a <= t <= b for (a, b) in gaps):
            continue
        p = p0 + u * t
        h = height * (0.86 + 0.14 * ((i * 37) % 10) / 10.0)
        size = (width, L / n - 0.06) if abs(d.y) > abs(d.x) else (L / n - 0.06, width)
        P.box((p.x, p.y, h / 2 + 0.03), (size[0], size[1], h), green)
    P.build(grp, name)


def build_fences(parent, plan, rng, site, stats):
    grp = _sub(parent, "PROP_FENCES")
    # --- school yard (building itself closes the south side)
    fence_run(grp, "PROP_FENCE_SCHOOL_W", (0.5, 33.6), (0.5, 56.4), "concrete", 2.0,
              site=site)
    fence_run(grp, "PROP_FENCE_SCHOOL_E", (53.5, 33.6), (53.5, 56.4), "concrete", 2.0,
              site=site)
    fence_run(grp, "PROP_FENCE_SCHOOL_N", (0.5, 56.4), (53.5, 56.4), "concrete", 2.0,
              gates=((22.4, 29.4),), site=site)
    # --- kindergarten yard
    fence_run(grp, "PROP_FENCE_KG_S", (-38.0, 26.0), (-2.0, 26.0), "concrete", 1.9,
              gates=((13.6, 18.6),), site=site)
    fence_run(grp, "PROP_FENCE_KG_W", (-38.0, 26.0), (-38.0, 43.8), "concrete", 1.9,
              site=site)
    fence_run(grp, "PROP_FENCE_KG_E", (-2.0, 26.0), (-2.0, 43.8), "concrete", 1.9,
              site=site)
    fence_run(grp, "PROP_FENCE_KG_N", (-38.0, 43.8), (-2.0, 43.8), "concrete", 1.9,
              site=site)
    # --- stadium perimeter (east side open where the tower stands)
    fence_run(grp, "PROP_FENCE_STAD_W", (64.6, 34.0), (64.6, 71.8), "concrete", 2.2,
              gates=((5.0, 9.0),), site=site)
    fence_run(grp, "PROP_FENCE_STAD_N", (64.6, 71.8), (96.0, 71.8), "concrete", 2.2,
              site=site)
    fence_run(grp, "PROP_FENCE_STAD_E1", (96.0, 34.0), (96.0, 44.0), "concrete", 2.2,
              site=site)
    fence_run(grp, "PROP_FENCE_STAD_E2", (96.0, 58.0), (96.0, 71.8), "concrete", 2.2,
              site=site)
    # --- boiler fuel yard
    fence_run(grp, "PROP_FENCE_BOIL_W", (-117.6, 44.0), (-117.6, 60.4), "concrete",
              2.4, site=site)
    fence_run(grp, "PROP_FENCE_BOIL_N", (-117.6, 60.4), (-88.2, 60.4), "concrete",
              2.4, site=site)
    fence_run(grp, "PROP_FENCE_BOIL_E", (-88.2, 44.0), (-88.2, 60.4), "concrete",
              2.4, gates=((0.5, 4.5),), site=site)
    # --- garage lane low fence
    low_metal_fence(grp, "PROP_FENCE_GARAGE", (-80.0, 10.8), (-46.0, 10.8), 1.2,
                    gates=((4.0, 8.0), (18.0, 22.0)), site=site)
    # --- plaza garden islands: low metal fence + hedges (kit metal fence reused)
    # the inner edge keeps a 1.2 m gap to the House of Culture block margin
    for side, x0, x1 in (("W", -34.0, -27.4), ("E", 27.4, 34.0)):
        low_metal_fence(grp, f"PROP_FENCE_GARDEN_{side}_S", (x0, -34.0), (x1, -34.0),
                        0.9, gates=((3.2, 4.8),), site=site)
        low_metal_fence(grp, f"PROP_FENCE_GARDEN_{side}_N", (x0, -24.0), (x1, -24.0),
                        0.9, gates=((3.2, 4.8),), site=site)
        if side == "W":   # reuse kit's green metal bar fence for one short run
            kit.fence(grp, f"PROP_FENCE_GARDEN_{side}_W", (x0, -24.0),
                      (x0, -34.0), height=0.9, kind="metal")
        else:
            low_metal_fence(grp, f"PROP_FENCE_GARDEN_{side}_W", (x0, -24.0),
                            (x0, -34.0), 0.9, site=site)
        low_metal_fence(grp, f"PROP_FENCE_GARDEN_{side}_E", (x1, -24.0),
                        (x1, -34.0), 0.9, site=site)
        hedge_run(grp, f"PROP_HEDGE_GARDEN_{side}", (x0 + 0.9, -25.4),
                  (x1 - 0.9, -25.4), 0.7, 0.9)
    # --- long hedges along the north green strips
    hedge_run(grp, "PROP_HEDGE_W_STRIP", (-118.0, -7.4), (-64.0, -7.4), 0.8, 1.15,
              gaps=((31.0, 41.0),))
    hedge_run(grp, "PROP_HEDGE_E_STRIP", (64.0, -7.4), (118.0, -7.4), 0.8, 1.15)
    hedge_run(grp, "PROP_HEDGE_PLAZA_S", (-24.0, -69.4), (24.0, -69.4), 0.65, 0.9,
              gaps=((19.0, 25.0),))
    stats["fences"] = "concrete + metal + hedges"
    stats["fence_objects"] = len(grp.children_recursive)


# ----------------------------------------------------------------------------
# notice boards / billboards / bus stop / signs / banner
# ----------------------------------------------------------------------------
def _text(parent, name, body, loc, size, material, normal=(0, -1, 0)):
    """3D text lying on a plane whose normal points along ``normal``."""
    q = Vector(normal).normalized().to_track_quat("Z", "Y")
    e = q.to_euler()
    try:
        return kit.text3d(parent, name, body, loc, size, material,
                          rot=(e.x, e.y, e.z))
    except Exception:  # noqa: BLE001
        return None


def _lm_rotation(lm):
    """Landmark rot (deg) -> (facing normal, u axis). 0 = faces south (-Y)."""
    a = math.radians(float(lm.get("rot", 0)))
    normal = (math.sin(a), -math.cos(a), 0.0)
    u = (-normal[1], normal[0], 0.0)
    return normal, u


def build_boards(parent, plan, rng, site, stats):
    """All plan landmarks of type billboard / poster_board + poster wall zones."""
    grp = _sub(parent, "PROP_BOARDS")
    n = 0
    for lm in plan.get("landmarks", []):
        ltype = lm.get("type")
        if ltype not in ("billboard", "poster_board"):
            continue
        lid = lm["id"]
        x, y = lm["pos"][0], lm["pos"][1]
        normal, u = _lm_rotation(lm)
        spot = (x, y)
        need = 1.0 if ltype == "billboard" else 0.8
        if not site.ok(x, y, 0.5, 1.0, True) or site.clash(x, y, need):
            spot = site.free_near(x, y, max_r=18.0)
        if spot is None:
            continue
        if ltype == "billboard":
            w, h = lm.get("size", [8.0, 4.0])
            site.claim(spot[0], spot[1], max(w, h) * 0.5)
            kit.billboard(grp, f"PROP_{lid}", (spot[0], spot[1], h / 2.0 + 2.9),
                          u, normal, w, h, rng)
        else:
            w, h = lm.get("size", [5.0, 2.2])
            site.claim(spot[0], spot[1], 1.5)
            kit.poster_board(grp, f"PROP_{lid}", (spot[0], spot[1], h / 2.0 + 1.1),
                             u, normal, w, h, rng)
        n += 1
    # poster wall zones -> free-standing stands near the courtyards
    for pz in plan.get("prop_zones", []):
        if pz.get("type") != "poster_wall":
            continue
        x0, y0, x1, y1 = pz["rect"]
        count = int(pz.get("count", 2))
        if y0 > -34.0:      # POSTERS_DK, north of the House of Culture
            y = -10.4
            normal = (0, 1, 0)
        else:               # POSTERS_PLAZA, on the plaza edge
            y = -35.8
            normal = (0, -1, 0)
        for i in range(count):
            x = x0 + 1.5 + (x1 - x0 - 3.0) * (i / max(1, count - 1))
            if not site.ok(x, y, 0.2, 1.0, allow_reserved=True):
                continue
            if site.clash(x, y, 1.4):
                continue
            site.claim(x, y, 1.6)
            kit.poster_board(grp, f"PROP_POSTERS_{pz['id']}_{i}",
                             (x, y, 2.2 / 2.0 + 1.1), (1, 0, 0), normal, 5.0, 2.2,
                             rng)
            n += 1
    # round 2: extra stands at the market square and the two park entrances
    EXTRA_STANDS = [
        ("MARKET", 119.6, -50.0, (1, 0, 0)),
        ("PARK_W", -119.0, 95.5, (0, -1, 0)),
        ("PARK_E", 114.0, 95.5, (0, -1, 0)),
    ]
    for (tag, x, y, normal) in EXTRA_STANDS:
        if not site.ok(x, y, 0.3, 1.0, True):
            spot = site.free_near(x, y, max_r=8.0)
            if spot is None:
                continue
            x, y = spot
        if site.clash(x, y, 1.2):
            continue
        site.claim(x, y, 1.5)
        u = (-normal[1], normal[0], 0.0)
        kit.poster_board(grp, f"PROP_STAND_{tag}", (x, y, 2.2 / 2.0 + 1.1),
                         u, normal, 5.0, 2.2, rng)
        n += 1
    stats["boards"] = n


def _bus_shelter(grp, name, x, y, rot, rng):
    """Concrete/steel bus shelter centred on (x, y); local -y is the open side."""
    T = XF(x, y, rot)
    metal = mat("metal_mid")
    dark = mat("metal_dark")
    glass = mat("glass_dim")
    P = PB()

    def L(lx, ly, lz):
        return T(lx, ly, lz)
    P.box(L(0, 0, 0.07), (6.5, 2.55, 0.14), mat("conc_pad"))
    for sx in (-2.9, 0.0, 2.9):
        for sy in (-1.06, 1.06):
            P.box(L(sx, sy, 1.31), (0.12, 0.12, 2.48), metal)
            P.box(L(sx, sy, 0.26), (0.22, 0.22, 0.24), mat("conc_dark"))
    P.box(L(0, 0, 2.60), (6.7, 2.75, 0.16), metal)
    P.box(L(0, -1.42, 2.50), (6.7, 0.12, 0.26), dark)
    P.box(L(0, 1.42, 2.50), (6.7, 0.12, 0.26), dark)
    # rear glass wall (two panels + mullions)
    P.quad(L(-1.48, 1.06, 1.66), (1, 0, 0), (0, 0, 1), 2.70, 1.30, glass)
    P.quad(L(1.48, 1.06, 1.66), (1, 0, 0), (0, 0, 1), 2.70, 1.30, glass)
    for mx in (-2.24, -0.72, 0.72, 2.24):
        P.box(L(mx, 1.06, 1.66), (0.05, 0.06, 1.30), metal)
    P.box(L(-1.48, 1.06, 2.32), (2.78, 0.05, 0.06), metal)
    P.box(L(1.48, 1.06, 2.32), (2.78, 0.05, 0.06), metal)
    P.box(L(-1.48, 1.06, 1.00), (2.78, 0.05, 0.06), metal)
    P.box(L(1.48, 1.06, 1.00), (2.78, 0.05, 0.06), metal)
    # side glass on the west end
    P.quad(L(-2.96, 0.0, 1.66), (0, 1, 0), (0, 0, 1), 1.95, 1.30, glass)
    for my in (-0.66, 0.66):
        P.box(L(-2.96, my, 1.66), (0.06, 0.05, 1.30), metal)
    P.box(L(-2.96, 0.0, 2.32), (0.05, 2.05, 0.06), metal)
    # bench (metal slats)
    for k in range(3):
        P.box(L(-0.25, 0.60 + k * 0.15, 0.52), (2.85, 0.11, 0.04), mat("wood"))
    for sx in (-1.25, 1.25):
        P.box(L(sx, 0.60, 0.27), (0.09, 0.44, 0.50), dark)
    # timetable board
    P.box(L(2.05, 1.01, 1.70), (1.15, 0.05, 0.95), dark)
    P.box(L(2.05, 0.97, 1.70), (1.02, 0.02, 0.82), mat("sign_white"))
    for k in range(4):
        P.box(L(2.05, 0.955, 1.94 - k * 0.16), (0.85, 0.012, 0.03),
              mat("sign_dark"))
    # strip light under the roof
    P.box(L(0, 0, 2.48), (2.6, 0.18, 0.06), mat("emissive_warm"))
    P.build(grp, name + "_SHELTER")
    kit.trash_bin(grp, name + "_BIN", T(-3.3, 1.05, 0.0))
    # bus stop sign on its own pole, beside the shelter (steps clear of the road)
    Ps = PB()
    bx, by, _bz = T(-3.6, 0.7, 0.0)
    Ps.tube((bx, by, 0.0), (bx, by, 2.95), 0.045, 0.04, seg=6,
            material=mat("metal_mid"))
    nx, ny = T.d(0, -1.0)
    Ps.disc((bx, by, 3.05), (nx, ny, 0.0), 0.34, mat("sign_white"), seg=14)
    Ps.disc((bx + nx * 0.014, by + ny * 0.014, 3.05), (nx, ny, 0.0), 0.30,
            mat("sign_yellow"), seg=14)
    Ps.box((bx + nx * 0.03, by + ny * 0.03, 3.05), (0.30, 0.02, 0.17),
           mat("sign_dark"), rot=(0, 0, rot))
    Ps.box((bx + nx * 0.045, by + ny * 0.045, 3.00), (0.24, 0.01, 0.06),
           mat("sign_white"), rot=(0, 0, rot))
    for k in range(2):
        Ps.box((bx + nx * 0.045, by + ny * 0.045, 3.14), (0.10, 0.01, 0.06),
               mat("sign_white"), rot=(0, 0, rot))
    Ps.build(grp, name + "_SIGN")
    return T(0, 0, 0)


def build_bus_stop(parent, plan, rng, site, stats):
    """Every plan landmark of type bus_stop (round 1 + the new south-side stop).

    The shelter is oriented from the landmark position towards the nearest
    carriageway so the same generator works on both sides of the avenue.
    """
    grp = _sub(parent, "PROP_BUSSTOP")
    made = 0
    for lm in plan.get("landmarks", []):
        if lm.get("type") != "bus_stop":
            continue
        x, y = lm["pos"][0], lm["pos"][1]
        # direction + distance from the landmark to the nearest road
        best = None
        for (x0, y0, x1, y1) in site.roads:
            rx = min(max(x, x0), x1)
            ry = min(max(y, y0), y1)
            dx, dy = rx - x, ry - y
            d = math.hypot(dx, dy)
            if best is None or d < best[0]:
                best = (d, dx, dy)
        if best is None or best[0] < 0.3:
            dist, dirx, diry = 2.0, 0.0, -1.0
        else:
            dist = best[0]
            dirx, diry = best[1] / dist, best[2] / dist
        half_depth = 1.45
        offset = max(-1.5, min(1.2, dist - half_depth - 0.45))
        cx, cy = x + dirx * offset, y + diry * offset
        rot = math.atan2(dirx, -diry)
        if not site.ok(cx, cy, 0.2, 1.0, True):
            near = site.free_near(cx, cy, max_r=5.0)
            if near is not None:
                cx, cy = near
        site.claim(cx, cy, 3.6)
        _bus_shelter(grp, f"PROP_BUSSTOP_{lm['id']}", cx, cy, rot, rng)
        made += 1
    stats["bus_stops"] = made



def _ped_sign(grp, name, x, y, rot):
    """Пешеходный переход sign: blue square, white triangle, walking figure."""
    P = PB()
    T = XF(x, y, rot)
    P.tube(T(0, 0, 0.0), T(0, 0, 2.55), 0.05, 0.045, seg=6,
           material=mat("metal_mid"))
    P.box(T(0, 0, 2.80), (0.68, 0.035, 0.68), mat("sign_white"),
          rot=(0, 0, rot))
    P.box(T(0, -0.02, 2.80), (0.62, 0.02, 0.62), mat("sign_blue"),
          rot=(0, 0, rot))
    tri = [T(-0.24, -0.035, 2.55), T(0.24, -0.035, 2.55), T(0.0, -0.035, 2.94)]
    P.prism(tri, (T.s, -T.c, 0.0), 0.012, mat("sign_white"))
    # walking figure (abstract)
    P.box(T(0.0, -0.05, 2.72), (0.05, 0.01, 0.14), mat("sign_dark"), rot=(0, 0, rot))
    P.box(T(0.0, -0.05, 2.61), (0.16, 0.01, 0.04), mat("sign_dark"), rot=(0, 0, rot))
    P.build(grp, name)


def _round_sign(grp, name, x, y, rot, text):
    P = PB()
    T = XF(x, y, rot)
    P.tube(T(0, 0, 0.0), T(0, 0, 2.55), 0.05, 0.045, seg=6,
           material=mat("metal_mid"))
    P.disc(T(0.0, 0.0, 2.80), (-T.c * 0.0 + T.s, -T.c, 0.0), 0.34,
           mat("sign_white"), seg=14)
    P.polyline([T(0.29 * math.cos(TAU * k / 14.0), 0.0,
                  2.80 + 0.29 * math.sin(TAU * k / 14.0)) for k in range(14)],
               0.045, seg=3, material=mat("sign_red"), closed=True)
    P.build(grp, name)
    if text:
        _text(grp, name + "_txt", text, T(0.0, -0.048, 2.80), 0.30,
              mat("sign_dark"), normal=(T.s, -T.c, 0.0))


def build_signs(parent, plan, rng, site, stats):
    grp = _sub(parent, "PROP_SIGNS")
    n = 0
    # pedestrian crossings on the north avenue sidewalk
    for i, (x, y, rot) in enumerate(((-2.8, -73.1, 0.0), (2.8, -73.1, 0.0),
                                     (-46.0, -73.1, 0.0), (18.0, -73.1, 0.0))):
        if not site.ok(x, y, 0.2, 1.0, allow_reserved=True):
            continue
        _ped_sign(grp, f"PROP_SIGN_PED_{i}", x, y, rot)
        n += 1
    if site.ok(26.0, -73.1, 0.2, 1.0, allow_reserved=True):
        _round_sign(grp, "PROP_SIGN_40", 26.0, -73.1, 0.0, "40")
        n += 1
    # direction plate on the avenue
    x, y = -68.0, -73.1
    if site.ok(x, y, 0.2, 1.0, allow_reserved=True):
        P = PB()
        P.tube((x, y, 0.0), (x, y, 2.75), 0.05, 0.045, seg=6,
               material=mat("metal_mid"))
        P.box((x, y, 2.55), (1.5, 0.04, 0.34), mat("sign_blue"))
        P.build(grp, "PROP_SIGN_DIR")
        _text(grp, "PROP_SIGN_DIR_txt", "ЛЕНИНСКИЙ ПР-Т", (x, y - 0.04, 2.55),
              0.15, mat("sign_white"))
        n += 1
    # stadium + school + kindergarten entrance signs
    for (x, y, body, wdt) in ((80.0, 32.4, "СТАДИОН", 1.9),
                              (26.5, 34.6, "ШКОЛА №3", 1.8),
                              (-20.5, 27.2, "ДЕТСКИЙ САД №9", 2.4)):
        if not site.ok(x, y, 0.2, 1.0, allow_reserved=True):
            continue
        P = PB()
        P.tube((x - wdt / 2 + 0.15, y, 0.0), (x - wdt / 2 + 0.15, y, 2.15), 0.05,
               0.045, seg=6, material=mat("metal_mid"))
        P.box((x, y, 2.0), (wdt, 0.05, 0.40), mat("sign_blue"))
        P.build(grp, f"PROP_SIGN_ENT_{body[:3]}")
        _text(grp, f"PROP_SIGN_ENT_txt_{body[:3]}", body, (x, y - 0.05, 2.0), 0.19,
              mat("sign_white"))
        site.claim(x, y, 1.2)
        n += 1
    stats["signs"] = n



def build_banner(parent, plan, rng, site, stats):
    """Red banner across the avenue: «СЛАВА ТРУДУ»."""
    grp = _sub(parent, "PROP_BANNER")
    x = -70.0
    y_n, y_s = -73.0, -88.8
    z_bot, z_top = 6.9, 9.1
    P = PB()
    conc = mat("conc_light")
    for yy in (y_n, y_s):
        P.tube((x, yy, 0.0), (x, yy, 9.6), 0.15, 0.10, seg=8, material=conc)
        P.box((x, yy, 0.22), (0.55, 0.55, 0.44), mat("conc_dark"))
        P.box((x, yy, 9.62), (0.22, 0.22, 0.10), mat("metal_mid"))
    P.box((x, (y_n + y_s) / 2.0, (z_bot + z_top) / 2.0),
          (0.06, abs(y_s - y_n), z_top - z_bot), mat("sign_red"))
    P.box((x, (y_n + y_s) / 2.0, z_top + 0.03), (0.10, abs(y_s - y_n) + 0.2, 0.08),
          mat("metal_mid"))
    P.build(grp, "PROP_BANNER_CLOTH")
    yc = (y_n + y_s) / 2.0
    _text(grp, "PROP_BANNER_TXT_E", "СЛАВА ТРУДУ", (x + 0.05, yc, (z_bot + z_top) / 2.0),
          1.05, mat("sign_white"), normal=(1, 0, 0))
    _text(grp, "PROP_BANNER_TXT_W", "СЛАВА ТРУДУ", (x - 0.05, yc, (z_bot + z_top) / 2.0),
          1.05, mat("sign_white"), normal=(-1, 0, 0))
    site.claim(x, y_n, 1.2)
    site.claim(x, y_s, 1.2)
    stats["banner"] = True


# ----------------------------------------------------------------------------
# round 2 — park pavilion (rotunda) + north park / pond props
# ----------------------------------------------------------------------------
def _pond_geom(site):
    """First elliptical water body as (cx, cy, rx, ry)."""
    for w in site.water:
        if w[0] == "ellipse":
            _k, cx, cy, rx, ry = w
            return (cx, cy, rx, ry)
    return None


def _ellipse_pt(pond, ang, f=1.0):
    cx, cy, rx, ry = pond
    return (cx + math.cos(ang) * rx * f, cy + math.sin(ang) * ry * f)


def build_pavilion(parent, plan, rng, site, stats, lamps):
    """Open park rotunda: octagonal platform, 6 slim columns, conical roof, 4 benches."""
    grp = _sub(parent, "PROP_PAVILION")
    made = 0
    for lm in plan.get("landmarks", []):
        if lm.get("type") != "pavilion":
            continue
        x, y = lm["pos"][0], lm["pos"][1]
        rot = math.radians(float(lm.get("rot", 0)))
        T = XF(x, y, rot)
        P = PB()
        conc = mat("conc")
        conc_l = mat("conc_light")
        wood = mat("wood")
        dark = mat("metal_dark")
        roof = mat("metal_mid")
        roof_d = mat("roof_dark")
        # two octagonal steps
        oct8 = [(math.cos(TAU * k / 8.0), math.sin(TAU * k / 8.0)) for k in range(8)]
        P.prism([T(c * 4.1, s * 4.1, 0.0) for (c, s) in oct8], (0, 0, 1), 0.20, conc_l)
        P.prism([T(c * 3.7, s * 3.7, 0.0) for (c, s) in oct8], (0, 0, 1), 0.18, conc)
        P.prism([T(c * 3.55, s * 3.55, 0.0) for (c, s) in oct8], (0, 0, 1), 0.06,
                mat("conc_dark"))
        z_floor = 0.30
        # six slim columns
        for k in range(6):
            a = TAU * k / 6.0 + math.pi / 6.0
            px, py = math.cos(a) * 2.95, math.sin(a) * 2.95
            P.tube(T(px, py, z_floor), T(px, py, 3.55), 0.115, 0.095, seg=8,
                   material=mat("metal_mid"))
            P.box(T(px, py, z_floor + 0.05), (0.30, 0.30, 0.10), dark)
        # ring beam + conical roof + finial
        ring = [T(math.cos(TAU * k / 18.0) * 3.05,
                  math.sin(TAU * k / 18.0) * 3.05, 3.58) for k in range(18)]
        P.polyline(ring, 0.11, seg=5, material=wood, closed=True)
        P.tube(T(0, 0, 3.60), T(0, 0, 5.05), 3.85, 0.06, seg=18, material=roof)
        P.tube(T(0, 0, 3.56), T(0, 0, 3.60), 3.9, 3.85, seg=18, material=roof_d)
        P.tube(T(0, 0, 5.05), T(0, 0, 5.55), 0.05, 0.02, seg=6, material=dark)
        # small stage in the middle
        P.tube(T(0, 0, z_floor), T(0, 0, z_floor + 0.14), 1.15, 1.10, seg=14,
               material=wood)
        P.tube(T(1.35, -0.4, z_floor), T(1.35, -0.4, z_floor + 0.07), 0.28, 0.28,
               seg=10, material=conc)
        # 4 benches facing the middle
        for k in range(4):
            a = TAU * k / 4.0 + math.pi / 4.0
            bx = x + math.cos(a) * 2.25
            by = y + math.sin(a) * 2.25
            facing = math.atan2(math.cos(a + math.pi), -math.sin(a + math.pi))
            bench_variant(grp, f"PROP_PAVILION_BENCH_{k}", bx, by, facing, rng, 0)
        # lantern under the roof (warm park light, returned for the Lead's lamps)
        kit.sphere(grp, "PROP_PAVILION_LANTERN", (x, y, 4.05), 0.16,
                   mat("emissive_warm"))
        P.build(grp, "PROP_PAVILION")
        site.claim(x, y, 4.6)
        lamps.append({"pos": (x, y, 4.05), "kind": "park", "height": 4.2})
        made += 1
    stats["pavilions"] = made


def _reed(P, x, y, rng):
    m = mat("reed") if rng.random() < 0.7 else mat("weed_dark")
    for _k in range(rng.randint(5, 9)):
        a = rng.uniform(0.0, TAU)
        h = rng.uniform(0.55, 1.45)
        lean = rng.uniform(0.05, 0.30)
        p0 = (x + rng.uniform(-0.12, 0.12), y + rng.uniform(-0.12, 0.12), 0.0)
        p1 = (p0[0] + math.cos(a) * lean * h, p0[1] + math.sin(a) * lean * h, h)
        P.tube(p0, p1, 0.012, 0.004, seg=3, material=m, caps=False)
        if rng.random() < 0.5:      # seed head
            P.tube((p1[0], p1[1], h * 0.92), (p1[0], p1[1], h * 1.03), 0.022,
                   0.012, seg=4, material=mat("reed_head"))


def _bollard(P, x, y, rng):
    dark = mat("metal_dark")
    P.tube((x, y, 0.0), (x, y, 0.86), 0.055, 0.048, seg=7, material=dark)
    P.tube((x, y, 0.80), (x, y, 0.88), 0.075, 0.072, seg=8,
           material=mat("emissive_warm"))
    P.tube((x, y, 0.88), (x, y, 0.94), 0.09, 0.05, seg=8, material=dark)


def _rowboat(P, x, y, rot, rng):
    T = XF(x, y, rot)
    wood = mat("wood")
    wood_d = mat("wood_dark")
    red = mat("paint_red")
    hull_z = 0.10
    P.box(T(0, 0, hull_z), (3.1, 0.90, 0.10), wood_d)
    P.box(T(0, -0.53, hull_z + 0.16), (3.15, 0.10, 0.32), wood)
    P.box(T(0, 0.53, hull_z + 0.16), (3.15, 0.10, 0.32), wood)
    # bow / stern wedges
    P.prism([T(-1.55, -0.45, hull_z), T(-1.55, 0.45, hull_z),
             T(-1.95, 0.0, hull_z + 0.20)], (0, 0, 1), 0.28, wood)
    P.box(T(1.55, 0.0, hull_z + 0.16), (0.14, 1.02, 0.34), wood_d)
    # gunwale trim + interior seats
    for sy in (-0.55, 0.55):
        P.box(T(0, sy, hull_z + 0.32), (3.2, 0.07, 0.06), red)
    for sx in (-0.85, 0.15, 1.05):
        P.box(T(sx, 0.0, hull_z + 0.26), (0.22, 1.02, 0.05), wood)
    # oars
    for sy in (-0.75, 0.75):
        P.tube(T(0.1, sy, hull_z + 0.34), T(1.9, sy * 1.5, hull_z + 0.30), 0.022,
               0.018, seg=5, material=wood)
        P.box(T(2.0, sy * 1.6, hull_z + 0.30), (0.5, 0.14, 0.025), wood_d)


def _duck(P, x, y, rot, rng):
    T = XF(x, y, rot)
    body = mat("duck_body")
    dark = mat("duck_dark")
    P.blob(T(0, 0, 0.115), (0.27, 0.17, 0.14), body, rng=rng, jitter=0.12,
           seg=8, rings=5)
    P.tube(T(0.20, 0, 0.16), T(0.30, 0, 0.34), 0.055, 0.045, seg=6,
           material=body)
    P.blob(T(0.32, 0, 0.38), (0.075, 0.07, 0.08), dark, rng=rng, jitter=0.08,
           seg=7, rings=5)
    P.tube(T(0.38, 0, 0.37), T(0.46, 0, 0.355), 0.030, 0.010, seg=5,
           material=mat("sign_yellow"))
    P.prism([T(-0.22, 0.0, 0.16), T(-0.34, 0.03, 0.28), T(-0.30, -0.02, 0.13)],
            (0, 1, 0), 0.05, dark)


def build_park(parent, plan, rng, site, stats, lamps):
    """North park: pond reeds, water-facing benches, bollard lights, jetty, boat,
    ducks, picnic table and the park entrance sign."""
    grp = _sub(parent, "PROP_PARK")
    pond = _pond_geom(site)
    if pond is None:
        stats["park"] = "no water zone"
        return
    cx, cy, rx, ry = pond
    made = {"reeds": 0, "benches": 0, "bins": 0, "bollards": 0}
    wood = mat("wood")
    wood_d = mat("wood_dark")
    # ---- reeds / dry shoreline grass around the pond
    P = PB()
    n_reeds = 0
    for k in range(96):
        ang = TAU * k / 96.0 + rng.uniform(-0.03, 0.03)
        f = rng.uniform(0.97, 1.10)
        x, y = _ellipse_pt(pond, ang, f)
        if site.in_water(x, y, 0.6):
            continue
        if not site.ok(x, y, 0.2, 1.0, True):
            continue
        _reed(P, x, y, rng)
        n_reeds += 1
        if n_reeds % 24 == 0:
            P.build(grp, f"PROP_REEDS_{n_reeds // 24:02d}")
            P = PB()
    P.build(grp, "PROP_REEDS_END")
    made["reeds"] = n_reeds
    # ---- benches around the pond facing the water (+ litter bins)
    bench_angles = [-1.95, -1.35, -0.55, 0.35, 1.05, 1.75]
    bench_pts = []
    nb = 0
    for a in bench_angles:
        bx, by = _ellipse_pt(pond, a, 1.30)
        if not site.ok(bx, by, 0.3, 1.0, True) or site.clash(bx, by, 1.6):
            continue
        site.claim(bx, by, 1.6)
        facing = math.atan2(cx - bx, -(cy - by))
        bench_variant(grp, f"PROP_PARK_BENCH_{nb}", bx, by, facing, rng, nb % 2)
        bench_pts.append((bx, by))
        nb += 1
    made["benches"] = nb
    for i, (bx, by) in enumerate(bench_pts[:3]):
        ox, oy = bx - cx, by - cy
        on = math.hypot(ox, oy) or 1.0
        ox, oy = ox / on, oy / on
        tx, ty = -oy, ox
        off = rng.uniform(-1.2, 1.2)
        binx = bx + ox * 2.4 + tx * off
        biny = by + oy * 2.4 + ty * off
        if site.ok(binx, biny, 0.3, 1.0, True) and \
                not site.clash(binx, biny, 0.6):
            site.claim(binx, biny, 0.7)
            kit.trash_bin(grp, f"PROP_PARK_BIN_{i}", (binx, biny, 0.0))
            made["bins"] += 1
    # ---- bollard lights along the park paths
    Pb = PB()
    nboll = 0
    for p in plan.get("paths", []):
        if p.get("id") not in ("P_PARK_W", "P_PARK_E", "P_POND_S", "P_POND_N",
                               "P_PARK_TOWERS"):
            continue
        pts = p.get("pts") or []
        for i in range(len(pts) - 1):
            ax, ay = pts[i][0], pts[i][1]
            bx, by = pts[i + 1][0], pts[i + 1][1]
            seg = math.hypot(bx - ax, by - ay)
            n = max(1, int(seg / 17.0))
            nx = -(by - ay) / max(1e-6, seg)
            ny = (bx - ax) / max(1e-6, seg)
            for k in range(n + 1):
                t = k / n
                px = ax + (bx - ax) * t + nx * 1.7
                py = ay + (by - ay) * t + ny * 1.7
                if not site.ok(px, py, 0.2, 1.0, True):
                    continue
                if site.clash(px, py, 0.5):
                    continue
                site.claim(px, py, 0.5)
                _bollard(Pb, px, py, rng)
                nboll += 1
    Pb.build(grp, "PROP_PARK_BOLLARDS")
    made["bollards"] = nboll
    # ---- small wooden jetty + rowing boat on the south shore
    jetty_x = cx - 7.0
    y_shore = cy - ry * math.sqrt(max(0.0, 1.0 - ((jetty_x - cx) / rx) ** 2))
    jp = PB()
    jp.box((jetty_x, y_shore + 2.4, 0.24), (1.7, 6.4, 0.10), wood)
    for k in range(9):
        yy = y_shore - 0.6 + k * 0.72
        jp.box((jetty_x, yy, 0.30), (1.62, 0.60, 0.04), wood_d)
    for sy in (-0.72, 0.72):
        jp.box((jetty_x + sy, y_shore + 2.4, 0.22), (0.12, 6.5, 0.14), wood)
    for k in range(4):
        yy = y_shore + 0.4 + k * 1.7
        for sx in (-0.72, 0.72):
            jp.tube((jetty_x + sx, yy, -0.25), (jetty_x + sx, yy, 0.55), 0.09,
                    0.085, seg=6, material=wood_d)
    jp.build(grp, "PROP_PARK_JETTY")
    boat_x = jetty_x + 2.1
    boat_y = y_shore + 4.6
    Pb2 = PB()
    _rowboat(Pb2, boat_x, boat_y, 0.35, rng)
    Pb2.build(grp, "PROP_PARK_BOAT")
    kit.wire(grp, "PROP_PARK_BOAT_ROPE", (jetty_x + 0.6, y_shore + 3.4, 0.30),
             (boat_x - 0.9, boat_y - 0.3, 0.22), 0.05, wood_d, radius=0.014,
             segments=4)
    made["jetty"] = True
    # ---- ducks
    Pd = PB()
    for _k in range(3):
        ang = rng.uniform(-0.9, 0.4)
        f = rng.uniform(0.55, 0.8)
        dx, dy = _ellipse_pt(pond, ang, f)
        _duck(Pd, dx, dy, rng.uniform(0.0, TAU), rng)
    Pd.build(grp, "PROP_PARK_DUCKS")
    # ---- picnic table on the south lawn
    pt_x, pt_y = _ellipse_pt(pond, -1.62, 1.18)
    if site.ok(pt_x, pt_y, 0.3, 1.0, True):
        P = PB()
        Tt = XF(pt_x, pt_y, 0.35)
        P.box(Tt(0, 0, 0.74), (1.9, 0.78, 0.07), wood)
        for sy in (-0.62, 0.62):
            P.box(Tt(0, sy, 0.44), (1.9, 0.30, 0.06), wood)
        for sx in (-0.72, 0.72):
            P.box(Tt(sx, 0, 0.37), (0.09, 1.65, 0.74), wood_d)
            P.box(Tt(sx, 0.62, 0.20), (0.08, 0.30, 0.42), wood_d)
            P.box(Tt(sx, -0.62, 0.20), (0.08, 0.30, 0.42), wood_d)
        P.build(grp, "PROP_PARK_PICNIC")
        site.claim(pt_x, pt_y, 1.6)
        made["picnic"] = True
    # ---- park entrance sign (wooden board with park name)
    for lm in plan.get("landmarks", []):
        if lm.get("type") != "park_sign":
            continue
        sx, sy = lm["pos"][0], lm["pos"][1]
        spot = (sx, sy)
        if not site.ok(sx, sy, 0.3, 1.0, True):
            near = site.free_near(sx, sy, max_r=8.0)
            if near is None:
                continue
            spot = near
        site.claim(spot[0], spot[1], 1.6)
        P = PB()
        for ox in (-1.1, 1.1):
            P.box((spot[0] + ox, spot[1], 1.25), (0.14, 0.14, 2.5), wood)
        P.box((spot[0], spot[1], 2.30), (2.6, 0.10, 0.72), wood_d)
        P.box((spot[0], spot[1], 1.75), (2.2, 0.08, 0.42), wood)
        P.build(grp, "PROP_PARK_SIGN")
        _text(grp, "PROP_PARK_SIGN_TXT", "ПАРК", (spot[0], spot[1] - 0.07, 2.30),
              0.42, mat("sign_white"))
        _text(grp, "PROP_PARK_SIGN_SUB", "КУЛЬТУРЫ И ОТДЫХА",
              (spot[0], spot[1] - 0.06, 1.75), 0.16, mat("sign_white"))
        lamps.append({"pos": (spot[0], spot[1], 2.6), "kind": "park",
                      "height": 2.6})
        made["sign"] = True
    stats["park"] = made


# ----------------------------------------------------------------------------
# utility poles + overhead wires
# ----------------------------------------------------------------------------
def _util_pole(P, x, y):
    conc = mat("conc_light")
    dark = mat("metal_dark")
    P.tube((x, y, -0.1), (x, y, 4.4), 0.20, 0.14, seg=8, material=conc)
    P.tube((x, y, 4.4), (x, y, 8.7), 0.14, 0.085, seg=8, material=conc)
    P.box((x, y, 0.25), (0.55, 0.55, 0.5), mat("conc_dark"))
    for zz, half in ((7.9, 0.95), (7.3, 0.65)):
        P.box((x, y, zz), (0.11, half * 2.0, 0.10), dark)
        for off in (-half, 0.0, half):
            P.tube((x, y + off, zz + 0.05), (x, y + off, zz + 0.22), 0.045, 0.035,
                   seg=5, material=mat("galv"))
    P.box((x, y, 6.8), (0.25, 0.30, 0.36), dark)   # transformer can


def build_poles(parent, plan, rng, site, stats):
    grp = _sub(parent, "PROP_UTIL")
    n = 0
    for zid, y_over in (("POLES_AVENUE", -73.2), ("POLES_W_COURT", -36.3)):
        pz = site.prop.get(zid)
        if not pz:
            continue
        step = float(pz.get("step", 26))
        x0, y0, x1, y1 = pz["rect"]
        if zid == "POLES_AVENUE":
            # interleave with the lamp posts instead of standing on top of them
            n_p = max(2, int((x1 - x0) / step))
            span = step * (n_p - 1)
            start = (x0 + x1) / 2.0 - span / 2.0
            pts = [(start + step * i, y_over) for i in range(n_p)]
        else:
            pts = [(x, y_over) for (x, _y) in
                   site.row_points(pz["rect"], step, edge=2.0)]
        P = PB()
        kept = []
        for (x, y) in pts:
            if not site.ok(x, y, 0.6, 1.0, allow_reserved=True):
                continue
            if site.clash(x, y, 1.2):
                continue
            site.claim(x, y, 1.1)
            _util_pole(P, x, y)
            kept.append((x, y))
            n += 1
        for (ax, ay), (bx, by) in zip(kept[:-1], kept[1:]):
            for off, hz in ((-0.95, 8.15), (0.0, 8.15), (0.95, 8.15), (0.0, 6.95)):
                seg_pts = []
                for k in range(6):
                    t = k / 5.0
                    seg_pts.append((ax + (bx - ax) * t, ay + off + (by - ay) * t,
                                    hz - 0.55 * math.sin(math.pi * t)))
                P.polyline(seg_pts, 0.015, seg=3, material=mat("metal_dark"))
        P.build(grp, f"PROP_UTIL_{zid}")
        stats.setdefault("utility_pole_xy", []).extend((round(a, 1), round(b, 1))
                                                       for (a, b) in kept)
    stats["utility_poles"] = n



# ----------------------------------------------------------------------------
# ground cover: puddles, wet patches, weeds, litter, leaves
# ----------------------------------------------------------------------------
WET_ZONE_KINDS = {"court", "parking", "plaza", "dirt", "terrace", "sport"}

WEED_LINES = [
    (-118.0, -70.7, 118.0, -70.7, 3.4),
    (-118.0, -88.2, 118.0, -88.2, 4.0),
    (-54.2, -74.0, -54.2, 2.0, 3.6),
    (51.2, -74.0, 51.2, 2.0, 3.6),
    (-87.2, 2.0, -87.2, 58.0, 3.8),
    (66.8, 2.0, 66.8, 76.0, 3.8),
    (-118.0, 74.3, 118.0, 74.3, 4.2),
    (0.5, 33.6, 0.5, 56.4, 2.8),
    (53.5, 33.6, 53.5, 56.4, 2.8),
    (0.5, 56.4, 53.5, 56.4, 2.8),
    (-38.0, 26.0, -2.0, 26.0, 2.8),
    (-38.0, 26.0, -38.0, 43.8, 2.8),
    (64.0, 34.0, 64.0, 71.8, 3.0),
    (96.0, 34.0, 96.0, 71.8, 3.0),
    (-117.6, 44.0, -117.6, 60.4, 3.0),
    (-88.2, 44.0, -88.2, 60.4, 3.0),
    (-80.0, 10.8, -46.0, 10.8, 2.8),
    (-100.4, -66.0, -100.4, -24.0, 3.4),
    (100.4, -66.0, 100.4, -24.0, 3.4),
    (-98.6, -24.4, -64.0, -24.4, 3.4),
    (64.0, -24.4, 92.6, -24.4, 3.4),
    (-100.0, -14.0, -60.0, -14.0, 4.6),
    (66.0, -14.0, 100.0, -14.0, 4.6),
    (-36.0, 24.6, -6.0, 24.6, 2.8),
    (2.0, 32.6, 50.0, 32.6, 3.0),
    (-26.0, -33.4, 26.0, -33.4, 3.2),
    (-30.0, -68.8, 30.0, -68.8, 3.2),
    (-66.0, -54.5, -64.0, -34.0, 3.4),
]


def _weed(P, x, y, rng):
    m = mat("weed") if rng.random() < 0.6 else mat("weed_dark")
    n = rng.randint(5, 9)
    for _ in range(n):
        a = rng.uniform(0.0, TAU)
        lean = rng.uniform(0.12, 0.55)
        h = rng.uniform(0.28, 0.80)
        px = x + rng.uniform(-0.09, 0.09)
        py = y + rng.uniform(-0.09, 0.09)
        p1 = (px + math.cos(a) * lean * h, py + math.sin(a) * lean * h, h)
        P.tube((px, py, 0.02), p1, 0.012, 0.004, seg=3, material=m, caps=False)
    if rng.random() < 0.45:
        P.blob((x, y, rng.uniform(0.3, 0.62)),
               (rng.uniform(0.06, 0.11), rng.uniform(0.06, 0.11),
                rng.uniform(0.08, 0.15)), m, seg=5, rings=4, rng=rng, jitter=0.3)


def _litter(P, x, y, rng):
    k = rng.randint(0, 4)
    if k == 0:
        P.box((x, y, 0.010), (0.20, 0.28, 0.008), mat("sign_white"),
              rot=(0, 0, rng.uniform(0, TAU)))
    elif k == 1:
        P.box((x, y, 0.045), (0.34, 0.24, 0.09), mat("cardboard"),
              rot=(0, 0, rng.uniform(0, TAU)))
    elif k == 2:
        P.tube((x, y, 0.035), (x + rng.uniform(-0.2, 0.2),
                               y + rng.uniform(-0.2, 0.2), 0.035), 0.035, 0.03,
               seg=6, material=mat("glass"), caps=True)
    elif k == 3:
        P.box((x, y, 0.06), (0.12, 0.12, 0.12), mat("paper"),
              rot=(rng.uniform(0, 1), 0, rng.uniform(0, TAU)))
    else:
        P.box((x, y, 0.012), (0.14, 0.10, 0.01), mat("sign_white"),
              rot=(0, 0, rng.uniform(0, TAU)))


def _puddle_poly(P, x, y, r, rng, material, z=0.014, seg=9):
    pts = []
    for i in range(seg):
        t = TAU * i / seg
        rr = r * rng.uniform(0.56, 1.20)
        # elliptical stretch + lobed outline so puddles do not read as polygons
        a = 1.0 + 0.35 * math.sin(3.0 * t + rng.uniform(-0.2, 0.2))
        pts.append((x + rr * a * math.cos(t), y + rr * 0.82 * math.sin(t), z))
    P.add([tuple(p) for p in pts], [tuple(range(seg))], material)


def _cover_targets(plan):
    """Counts for the global ground-cover prop zones (scaled with the map area)."""
    tot = {"PUDDLES": 0, "WEEDS": 0, "TRASH": 0}
    for pz in plan.get("prop_zones", []):
        key = {"puddles": "PUDDLES", "weeds": "WEEDS",
               "litter": "TRASH"}.get(pz.get("type"))
        if key:
            tot[key] += int(pz.get("count", 0))
    base = plan.get("base") or [320, 240]
    area = float(base[0]) * float(base[1])
    factor = max(0.8, min(2.2, area / (320.0 * 240.0)))
    return {k: int(round(v * factor)) for k, v in tot.items()}


def _auto_weed_lines(plan, site):
    """Weed seed lines: every road kerb + building plinth — scales with the map."""
    lines = []
    for (x0, y0, x1, y1) in site.roads:
        if (x1 - x0) >= (y1 - y0):
            lines.append((x0, y0 - 1.6, x1, y0 - 1.6))
            lines.append((x0, y1 + 1.6, x1, y1 + 1.6))
        else:
            lines.append((x0 - 1.6, y0, x0 - 1.6, y1))
            lines.append((x1 + 1.6, y0, x1 + 1.6, y1))
    for (x0, y0, x1, y1) in site.blocks:
        lines.append((x0 - 1.35, y0 - 1.35, x1 + 1.35, y0 - 1.35))
        lines.append((x0 - 1.35, y1 + 1.35, x1 + 1.35, y1 + 1.35))
        lines.append((x0 - 1.35, y0 - 1.35, x0 - 1.35, y1 + 1.35))
        lines.append((x1 + 1.35, y0 - 1.35, x1 + 1.35, y1 + 1.35))
    for z in plan.get("zones", []):
        if z.get("kind") not in ("dirt", "playground"):
            continue
        x0, y0, x1, y1 = z["rect"]
        lines.append((x0 + 0.6, y0 + 0.6, x1 - 0.6, y0 + 0.6))
        lines.append((x0 + 0.6, y1 - 0.6, x1 - 0.6, y1 - 0.6))
    return [ln for ln in lines if math.hypot(ln[2] - ln[0], ln[3] - ln[1]) > 1.5]


def _line_sampler(lines, rng):
    lengths = [math.hypot(x1 - x0, y1 - y0) for (x0, y0, x1, y1) in lines]
    total = sum(lengths) or 1.0

    def sample():
        r = rng.uniform(0.0, total)
        acc = 0.0
        seg = lines[-1]
        for ln, L in zip(lines, lengths):
            acc += L
            if r <= acc:
                seg = ln
                break
        t = rng.random()
        return (seg[0] + (seg[2] - seg[0]) * t + rng.uniform(-0.9, 0.9),
                seg[1] + (seg[3] - seg[1]) * t + rng.uniform(-0.9, 0.9))
    return sample


def _in_any_rect(x, y, rects):
    for (x0, y0, x1, y1) in rects:
        if x0 <= x <= x1 and y0 <= y <= y1:
            return True
    return False


def build_ground_cover(parent, plan, rng, site, stats, lamps):
    grp = _sub(parent, "PROP_GROUND")
    targets = _cover_targets(plan)
    wet_rects = []
    for z in plan.get("zones", []):
        if z.get("kind") in WET_ZONE_KINDS:
            wet_rects.append(tuple(z["rect"]))
    for s in plan.get("sidewalks", []):
        wet_rects.append(tuple(s["rect"]))
    if not wet_rects:
        return
    weights = [max(4.0, (r[2] - r[0]) * (r[3] - r[1])) for r in wet_rects]
    lamp_xy = [(p["pos"][0], p["pos"][1]) for p in lamps]

    def sample():
        for _try in range(60):
            rect = rng.choices(wet_rects, weights=weights, k=1)[0]
            x = rng.uniform(rect[0] + 1.0, rect[2] - 1.0)
            y = rng.uniform(rect[1] + 1.0, rect[3] - 1.0)
            if site.ok(x, y, 0.4, 1.0, allow_reserved=True):
                return (x, y)
        return None

    # -- wet patches (darker, wider, under the puddles)
    Pw = PB()
    n_wet = 0
    n_wet_target = max(20, targets["PUDDLES"] // 3)
    for _ in range(n_wet_target):
        p = sample()
        if not p:
            continue
        _puddle_poly(Pw, p[0], p[1], rng.uniform(1.6, 4.2), rng,
                     mat("wet") if rng.random() < 0.6 else mat("wet_dark"),
                     z=0.008, seg=11)
        n_wet += 1
    Pw.build(grp, "PROP_WET_PATCHES")

    # -- puddles
    P = PB()
    n_pud = 0
    warm = 0
    count = int(targets["PUDDLES"] or 45)
    for _ in range(count):
        p = sample()
        if not p:
            continue
        near_lamp = any(math.dist(p, lp) < 10.0 for lp in lamp_xy)
        if near_lamp and rng.random() < 0.3:
            m = mat("puddle_warm")
            warm += 1
        else:
            m = mat("puddle")
        r_pud = rng.uniform(0.55, 2.3)
        _puddle_poly(P, p[0], p[1], r_pud, rng, m, z=0.014,
                     seg=13 if r_pud > 1.3 else 9)
        n_pud += 1
    # a handful of puddles right under the lamps catch the warm sodium light
    lamp_pick = [lp for lp in lamps if lp["kind"] in ("plaza", "court", "park")]
    lamp_pick += [lp for lp in lamps if lp["kind"] == "street"]
    for lp in lamp_pick[:12]:
        if warm >= 10:
            break
        lx, ly = lp["pos"][0], lp["pos"][1]
        for (dx, dy) in ((2.4, 0.9), (-1.9, 1.7), (0.6, -2.2), (-2.6, -1.1)):
            x, y = lx + dx, ly + dy
            if not _in_any_rect(x, y, wet_rects):
                continue
            if not site.ok(x, y, 0.1, 1.0, True):
                continue
            _puddle_poly(P, x, y, rng.uniform(0.9, 1.7), rng, mat("puddle_warm"),
                         z=0.015, seg=9)
            warm += 1
            n_pud += 1
            break
    P.build(grp, "PROP_PUDDLES")

    stats["puddles"] = n_pud
    stats["puddles_warm"] = warm
    stats["wet_patches"] = n_wet

    # -- weeds along every kerb / building plinth (auto-scales with the map)
    lines = _auto_weed_lines(plan, site)
    sampler = _line_sampler(lines, rng)
    target_weeds = int(targets["WEEDS"] or 60)
    P = PB()
    nw = 0
    group = 0
    for _try in range(target_weeds * 5):
        if nw >= target_weeds:
            break
        x, y = sampler()
        if not site.ok(x, y, 0.12, 0.35, allow_reserved=True):
            continue
        _weed(P, x, y, rng)
        nw += 1
        if nw % 18 == 0:
            P.build(grp, f"PROP_WEEDS_{group:02d}")
            P = PB()
            group += 1
    P.build(grp, f"PROP_WEEDS_{group:02d}")
    stats["weeds"] = nw

    # -- litter near bins / dumpsters / stops / playgrounds / new squares
    litter_spots = [(-88, -35.4), (-74, -35.4), (88, -35.4), (74, -35.4),
                    (-30, 25.4), (-12, 25.4), (14, -9.4), (-14, -9.4),
                    (24, 33.4), (40, 33.4), (-45, -15.6), (2, -70.6),
                    (-48, -70.6), (60, -31.4), (-60, -31.4), (-100, -56),
                    (100, -56), (-6, 42), (-40, -72), (18, -81)]
    for z in plan.get("zones", []):
        if z.get("kind") in ("court", "plaza", "parking", "dirt", "playground",
                             "terrace"):
            x0, y0, x1, y1 = z["rect"]
            litter_spots.append(((x0 + x1) / 2.0, (y0 + y1) / 2.0))
    for lm in plan.get("landmarks", []):
        if lm.get("type") in ("bus_stop", "pavilion", "park_sign"):
            litter_spots.append((lm["pos"][0], lm["pos"][1]))
    for pz in plan.get("prop_zones", []):
        if pz.get("type") == "playground":
            x0, y0, x1, y1 = pz["rect"]
            litter_spots.append(((x0 + x1) / 2.0, (y0 + y1) / 2.0))
    P = PB()
    nl = 0
    ln = int(targets["TRASH"] or 40)
    for _try in range(ln * 4):
        if nl >= ln:
            break
        ax, ay = litter_spots[rng.randrange(len(litter_spots))]
        x = ax + rng.uniform(-5.5, 5.5)
        y = ay + rng.uniform(-4.0, 4.0)
        if not site.ok(x, y, 0.3, 0.6, allow_reserved=True):
            continue
        _litter(P, x, y, rng)
        nl += 1
        if nl % 12 == 0:
            P.build(grp, f"PROP_LITTER_{nl // 12:02d}")
            P = PB()
    P.build(grp, "PROP_LITTER_END")
    stats["litter"] = nl

    # -- dead leaves under the trees + along the kerbs
    tree_pos = stats.get("tree_positions", [])
    Pl = PB()
    nl2 = 0
    for i, (x, y) in enumerate(tree_pos):
        if i % 2 == 0:
            continue
        _puddle_poly(Pl, x + rng.uniform(-0.8, 0.8), y + rng.uniform(-0.8, 0.8),
                     rng.uniform(0.7, 1.7), rng, mat("leaves"), z=0.016, seg=9)
        nl2 += 1
    for _try in range(90):
        x, y = sampler()
        if not site.ok(x, y, 0.2, 0.5, allow_reserved=True):
            continue
        _puddle_poly(Pl, x, y, rng.uniform(0.4, 1.0), rng, mat("leaves"),
                     z=0.016, seg=7)
        nl2 += 1
    Pl.build(grp, "PROP_LEAVES")
    stats["leaves"] = nl2



# ----------------------------------------------------------------------------
# round 3 — memorial esplanade landmarks (eternal flame / guard box / flag row /
#           metro pylon / plaque)
# ----------------------------------------------------------------------------
def _star_pts(cx, cy, r_out, r_in, z, rot=0.0, n=5):
    pts = []
    for k in range(2 * n):
        r = r_out if k % 2 == 0 else r_in
        a = rot + math.pi / 2.0 + math.pi * k / n
        pts.append((cx + r * math.cos(a), cy + r * math.sin(a), z))
    return pts


def _memorial_island(grp, plan):
    """Granite island platform inside a memorial pool.

    Only built when a monument landmark actually stands in the water — the final
    v3 plan keeps two reflecting pools flanking the axis walk, with the eternal
    flame / guard / flags on the esplanade itself, so this normally returns None.
    """
    pools = [z["rect"] for z in plan.get("zones", [])
             if z.get("kind") == "water" and "MONUMENT" in z.get("id", "")]
    if not pools:
        return None
    inside = []
    for lm in plan.get("landmarks", []):
        if lm.get("type") not in ("eternal_flame", "guard_box", "flag_row"):
            continue
        lx, ly = lm["pos"][0], lm["pos"][1]
        for (x0, y0, x1, y1) in pools:
            if x0 <= lx <= x1 and y0 <= ly <= y1:
                inside.append((lx, ly))
                break
    if not inside:
        return None
    P = PB()
    conc = mat("granite")
    conc_l = mat("granite_light")
    for (x0, y0, x1, y1) in pools:
        icx, icy = (x0 + x1) / 2.0, (y0 + y1) / 2.0
        rx, ry = (x1 - x0) / 2.0 - 1.2, (y1 - y0) / 2.0 - 0.6
        for (dx, dy, zz, hh, m) in ((0.0, 0.0, 0.17, 0.34, conc),
                                    (0.45, 0.28, 0.37, 0.06, conc_l)):
            pts = [(icx + (rx - dx) * math.cos(TAU * k / 40),
                    icy + (ry - dy) * math.sin(TAU * k / 40), zz)
                   for k in range(40)]
            P.prism(pts, (0, 0, 1), hh, m)
        P.build(grp, "PROP_MEMORIAL_ISLAND")
        return (icx, icy, 0.40)
    return None


def _eternal_flame(grp, x, y, z0, rng):
    P = PB()
    granite = mat("granite")
    bronze = mat("bronze")
    bronze_d = mat("bronze_dark")
    dark = mat("sign_dark")
    # granite curb ring
    P.polyline([(x + 2.9 * math.cos(TAU * k / 20), y + 2.9 * math.sin(TAU * k / 20),
                 z0 + 0.06) for k in range(20)], 0.16, seg=4, material=granite,
               closed=True)
    # granite star + bronze star + dark recess (cut-out illusion)
    P.prism(_star_pts(x, y, 2.35, 1.05, z0 + 0.09), (0, 0, 1), 0.14, granite)
    P.prism(_star_pts(x, y, 1.85, 0.82, z0 + 0.20), (0, 0, 1), 0.10, bronze)
    P.prism(_star_pts(x, y, 1.05, 0.42, z0 + 0.27), (0, 0, 1), 0.08, dark)
    # bronze wreath ring + leaves
    P.polyline([(x + 1.62 * math.cos(TAU * k / 16), y + 1.62 * math.sin(TAU * k / 16),
                 z0 + 0.16) for k in range(16)], 0.045, seg=4, material=bronze_d,
               closed=True)
    for k in range(10):
        a = TAU * k / 10.0
        P.blob((x + 1.62 * math.cos(a), y + 1.62 * math.sin(a), z0 + 0.18),
               (0.14, 0.10, 0.05), bronze, rng=rng, jitter=0.25, seg=6, rings=4)
    # flame cluster (flicker look = several nested cones)
    for (rr, h, m) in ((0.34, 1.10, mat("flame")), (0.22, 0.80, mat("flame_core")),
                       (0.10, 0.52, mat("flame_core"))):
        P.tube((x, y, z0 + 0.30), (x + rng.uniform(-0.05, 0.05),
                                   y + rng.uniform(-0.05, 0.05), z0 + 0.30 + h),
               rr, 0.01, seg=8, material=m)
    # underlight glow disc (the real point light comes from build.py)
    P.disc((x, y, z0 + 0.315), (0, 0, 1), 1.0, mat("flame"), seg=14)
    P.build(grp, "PROP_ETERNAL_FLAME")


def _guard_box(grp, x, y, z0, rng):
    P = PB()
    T = XF(x, y, 0.0)
    green = mat("paint_green")
    dark = mat("metal_dark")
    glass = mat("glass_dim")
    P.box(T(0, 0, z0 + 0.09), (2.05, 2.05, 0.18), mat("granite"))
    P.box(T(0, 0, z0 + 1.40), (1.72, 1.72, 2.45), green)
    # glass front facing west (towards the flame)
    P.box(T(-0.86, 0, z0 + 1.75), (0.05, 1.15, 1.15), glass)
    for oy in (-0.62, 0.0, 0.62):
        P.box(T(-0.88, oy, z0 + 1.75), (0.06, 0.06, 1.30), dark)
    P.box(T(-0.88, 0, z0 + 2.38), (0.06, 1.26, 0.06), dark)
    # door on the south side + handle
    P.box(T(0.0, 0.87, z0 + 1.20), (0.72, 0.05, 1.85), mat("booth"))
    P.box(T(0.28, 0.91, z0 + 1.12), (0.05, 0.05, 0.28), mat("metal_mid"))
    # roof + small lamp under its edge
    P.box(T(0, 0, z0 + 2.72), (2.15, 2.15, 0.16), dark)
    P.box(T(0, 0, z0 + 2.84), (1.85, 1.85, 0.08), mat("metal_mid"))
    P.box(T(-0.95, 0, z0 + 2.58), (0.22, 0.5, 0.08), mat("emissive_warm"))
    P.build(grp, "PROP_GUARD_BOX")
    # two low granite pedestals flanking the sentry
    for i, (px, py) in enumerate(((x - 3.1, y), (x + 3.1, y))):
        Pp = PB()
        Pp.box((px, py, z0 + 0.28), (0.8, 0.8, 0.56), mat("granite"))
        Pp.box((px, py, z0 + 0.60), (0.92, 0.92, 0.08), mat("granite_light"))
        Pp.build(grp, f"PROP_GUARD_PEDESTAL_{i}")


def _flag_pole(grp, name, x, y, z0, rng):
    P = PB()
    P.box((x, y, z0 + 0.20), (0.95, 0.95, 0.40), mat("granite"))
    P.box((x, y, z0 + 0.43), (0.75, 0.75, 0.06), mat("granite_light"))
    pole_h = 10.0
    P.tube((x, y, z0), (x, y, z0 + pole_h), 0.075, 0.042, seg=8,
           material=mat("galv"))
    P.tube((x, y, z0 + 9.55), (x, y, z0 + 9.72), 0.10, 0.10, seg=8,
           material=mat("bronze"))
    P.tube((x, y, z0 + 2.3), (x, y, z0 + 2.42), 0.10, 0.10, seg=8,
           material=mat("bronze"))
    # hanging faded flag (two slightly bent quads) + cream emblem stripe
    fy0, fy1 = z0 + 9.55, z0 + 7.35
    for k in range(2):
        sx = x + 0.32 + k * 1.28
        P.quad((sx, y + 0.02, (fy0 + fy1) / 2.0), (1.0, 0.0, 0.0), (0, 0, 1),
               1.30, fy0 - fy1, mat("flag_red"))
    P.quad((x + 0.63, y - 0.06, z0 + 9.02), (1.0, 0.0, 0.0), (0, 0, 1),
           1.28, 0.28, mat("flag_cream"))
    P.build(grp, name)


def _metro_pylon(grp, x, y, rng):
    P = PB()
    T = XF(x, y, 0.0)
    dark = mat("metal_dark")
    red = mat("sign_red")
    P.box(T(0, 0, 0.20), (1.5, 1.0, 0.40), mat("granite"))
    P.box(T(0, 0, 0.46), (1.2, 0.8, 0.08), mat("granite_light"))
    P.tube(T(0, 0, 0.4), T(0, 0, 4.1), 0.30, 0.24, seg=8, material=dark)
    # sign box
    P.box(T(0, 0, 5.10), (2.55, 0.34, 2.30), red)
    P.box(T(0, 0, 6.30), (2.75, 0.44, 0.14), dark)
    P.box(T(0, 0, 3.88), (2.4, 0.30, 0.12), dark)
    # warm underlight bar
    P.box(T(0, 0, 3.98), (1.9, 0.18, 0.06), mat("emissive_warm"))
    P.build(grp, "PROP_METRO_PYLON")
    # big М on both faces + small МЕТРО caption
    _text(grp, "PROP_METRO_M_S", "М", (x, y - 0.2, 5.10), 1.85,
          mat("sign_white"), normal=(0, -1, 0))
    _text(grp, "PROP_METRO_M_N", "М", (x, y + 0.2, 5.10), 1.85,
          mat("sign_white"), normal=(0, 1, 0))
    _text(grp, "PROP_METRO_CAP", "МЕТРО", (x, y - 0.2, 3.72), 0.30,
          mat("sign_white"), normal=(0, -1, 0))


def _plaque(grp, x, y, rng):
    P = PB()
    T = XF(x, y, 0.0)
    granite = mat("granite")
    bronze = mat("bronze")
    P.box(T(0, 0, 0.11), (3.4, 1.7, 0.22), mat("granite_dark"))
    P.box(T(0, 0, 0.72), (3.0, 1.25, 1.05), granite)
    P.box(T(0, 0, 1.28), (3.2, 1.4, 0.08), mat("granite_light"))
    # bronze wreath on the front face
    P.polyline([(x + 0.9 * math.cos(TAU * k / 14) * 0.8,
                 y - 0.64, 0.72 + 0.62 * math.sin(TAU * k / 14))
                for k in range(14)], 0.045, seg=4, material=bronze, closed=True)
    P.blob((x, y - 0.64, 0.95), (0.16, 0.05, 0.20), bronze, rng=rng,
           jitter=0.2, seg=6, rings=4)
    # urns flanking the plaque
    for i, ox in enumerate((-1.9, 1.9)):
        P.tube(T(ox, 0, 0.0), T(ox, 0, 0.62), 0.30, 0.24, seg=8,
               material=mat("granite_dark"))
        P.tube(T(ox, 0, 0.62), T(ox, 0, 0.86), 0.26, 0.10, seg=8,
               material=bronze)
    P.build(grp, "PROP_STATUE_PLAQUE")
    _text(grp, "PROP_PLAQUE_TXT", "ВЕЧНАЯ СЛАВА ГЕРОЯМ", (x, y - 0.64, 1.02),
          0.155, mat("sign_white"), normal=(0, -1, 0))


def build_monuments(parent, plan, rng, site, stats, lamps):
    grp = _sub(parent, "PROP_MONUMENTS")
    island = _memorial_island(grp, plan)
    zmon = island[2] if island else 0.02
    n = 0
    for lm in plan.get("landmarks", []):
        ltype = lm.get("type")
        x, y = lm["pos"][0], lm["pos"][1]
        if ltype == "eternal_flame":
            _eternal_flame(grp, x, y, zmon, rng)
            # warm underlight for the flame + its stone rim
            lamps.append({"pos": (x, y, 0.95), "kind": "court", "height": 1.0})
            site.claim(x, y, 3.4)
            n += 1
        elif ltype == "guard_box":
            _guard_box(grp, x, y, zmon, rng)
            site.claim(x, y, 3.2)
            n += 1
        elif ltype == "flag_row":
            count = int(lm.get("count", 4)) or 4
            z0 = zmon
            for k in range(count):
                ox = (k - (count - 1) / 2.0) * 3.1
                _flag_pole(grp, f"PROP_FLAG_POLE_{k}", x + ox, y, z0, rng)
            site.claim(x, y, 8.0)
            n += 1
        elif ltype == "metro_sign":
            px, py = x, y
            if not site.ok(px, py, 0.3, 1.0, True):
                near = site.free_near(px, py, max_r=14.0)
                if near is not None:
                    px, py = near
            _metro_pylon(grp, px, py, rng)
            lamps.append({"pos": (px, py, 4.4), "kind": "court",
                          "height": 4.4})
            site.claim(px, py, 2.2)
            n += 1
        elif ltype == "plaque":
            _plaque(grp, x, y, rng)
            site.claim(x, y, 3.4)
            n += 1
    stats["monuments"] = n


# ----------------------------------------------------------------------------
# round 3 — bus terminals (UFO / hexagonal stations)
# ----------------------------------------------------------------------------
def _arc_strip(P, cx, cy, r_in, r_out, a0, a1, top_z, material, material_top,
               seg=20):
    pts = [(cx + math.cos(a0 + (a1 - a0) * k / seg) * r_out,
            cy + math.sin(a0 + (a1 - a0) * k / seg) * r_out, top_z / 2.0)
           for k in range(seg + 1)]
    pts += [(cx + math.cos(a1 - (a1 - a0) * k / seg) * r_in,
             cy + math.sin(a1 - (a1 - a0) * k / seg) * r_in, top_z / 2.0)
            for k in range(seg + 1)]
    P.prism(pts, (0, 0, 1), top_z, material)
    # lighter top surface
    pts2 = [(cx + math.cos(a0 + (a1 - a0) * k / seg) * (r_out - 0.15),
             cy + math.sin(a0 + (a1 - a0) * k / seg) * (r_out - 0.15),
             top_z + 0.012) for k in range(seg + 1)]
    pts2 += [(cx + math.cos(a1 - (a1 - a0) * k / seg) * (r_in + 0.15),
              cy + math.sin(a1 - (a1 - a0) * k / seg) * (r_in + 0.15),
              top_z + 0.012) for k in range(seg + 1)]
    P.add([tuple(p) for p in pts2], [tuple(range(len(pts2)))], material_top)


def _laz_bus(grp, name, x, y, rot, rng):
    P = PB()
    T = XF(x, y, rot)
    cream = mat("laz_cream")
    red = mat("laz_red")
    glass = mat("bus_glass")
    dark = mat("metal_dark")
    rub = mat("rubber")
    L, W = 10.4, 2.5
    P.box(T(0, 0, 0.60), (L - 0.4, W - 0.16, 0.5), dark)
    P.box(T(0, 0, 1.55), (L, W, 1.55), cream)
    P.box(T(0.15, 0, 2.06), (L - 1.5, W + 0.03, 0.60), glass)
    P.box(T(0, 0, 2.50), (L - 0.2, W - 0.04, 0.34), cream)
    P.box(T(0, 0, 2.70), (L - 0.7, W - 0.55, 0.10), mat("metal_mid"))
    P.box(T(0, 0, 1.06), (L + 0.02, W + 0.02, 0.22), red)
    for sx in (-1, 1):
        P.box(T(sx * (L / 2 - 0.03), 0, 2.02), (0.06, W - 0.40, 0.72), glass)
    for dx in (-2.5, 2.7):
        P.box(T(dx, W / 2 - 0.01, 1.38), (0.92, 0.06, 1.72), dark)
        P.box(T(dx, W / 2 + 0.01, 1.92), (0.80, 0.03, 0.50), glass)
    P.box(T(0, 0, 0.36), (L - 1.2, W - 0.5, 0.20), dark)
    for wx in (-3.4, 3.2):
        for wy in (-W / 2 + 0.05, W / 2 - 0.05):
            P.tube(T(wx, wy - 0.14, 0.50), T(wx, wy + 0.14, 0.50), 0.50, 0.50,
                   seg=12, material=rub)
            P.tube(T(wx, wy - 0.15, 0.50), T(wx, wy + 0.15, 0.50), 0.22, 0.22,
                   seg=8, material=mat("metal_mid"))
    for wy in (-0.7, 0.7):
        P.box(T(-L / 2 - 0.06, wy, 0.90), (0.10, 0.34, 0.20),
              mat("sign_white"))
        P.box(T(L / 2 + 0.06, wy, 0.90), (0.10, 0.30, 0.18), mat("sign_red"))
    # roof route board
    P.box(T(-L / 2 + 0.55, 0, 2.36), (0.9, 0.7, 0.16), mat("sign_white"))
    P.build(grp, name)


def _luggage_cart(grp, name, x, y, rot, rng):
    P = PB()
    T = XF(x, y, rot)
    metal = mat("metal_mid")
    P.box(T(0, 0, 0.30), (1.20, 0.68, 0.06), metal)
    for side in (-1, 1):
        P.tube(T(side * 0.55, -0.30, 0.05), T(side * 0.55, -0.30, 0.95), 0.03,
               0.03, seg=5, material=metal)
        P.tube(T(side * 0.55, 0.30, 0.05), T(side * 0.55, 0.30, 0.95), 0.03,
               0.03, seg=5, material=metal)
    P.tube(T(-0.55, 0.30, 0.95), T(0.55, 0.30, 0.95), 0.03, 0.03, seg=5,
           material=metal)
    for wx in (-0.42, 0.42):
        P.tube(T(wx, -0.30, 0.12), T(wx, -0.18, 0.12), 0.12, 0.12, seg=8,
               material=mat("rubber"))
    P.build(grp, name)


def _taxi_sign(grp, name, x, y, rot, rng):
    P = PB()
    T = XF(x, y, rot)
    P.tube(T(0, 0, 0.0), T(0, 0, 2.55), 0.05, 0.045, seg=6,
           material=mat("metal_mid"))
    P.box(T(0, 0, 2.72), (0.95, 0.10, 0.62), mat("taxi_white"))
    for k in range(6):
        m = mat("taxi_dark") if k % 2 == 0 else mat("taxi_white")
        ox = -0.36 + k * 0.145
        P.box(T(ox, -0.06, 2.45), (0.145, 0.02, 0.10), m)
    P.build(grp, name)
    _text(grp, name + "_txt", "ТАКСИ", T(0, -0.07, 2.75), 0.20,
          mat("taxi_dark"), normal=(math.sin(rot), -math.cos(rot), 0.0))


def _terminal_marking(P, cx, cy, r, a0, a1, seg=9, material=None, z=0.02):
    m = material or mat("sign_white")
    for k in range(seg):
        a = a0 + (a1 - a0) * (k + 0.5) / seg
        P.box((cx + math.cos(a) * r, cy + math.sin(a) * r, z),
              (0.10, 0.85, 0.012), m, rot=(0, 0, a))
    return seg


def _stadium_island(P, cx, cy, length, width, top_z, material, material_top):
    """Rounded-end bus island platform."""
    hl = max(0.1, length / 2.0 - width / 2.0)
    hw = width / 2.0
    seg = 14
    pts = []
    for k in range(seg + 1):
        a = -math.pi / 2 + math.pi * k / seg
        pts.append((cx + hl + hw * math.cos(a), cy + hw * math.sin(a),
                    top_z / 2.0))
    for k in range(seg + 1):
        a = math.pi / 2 + math.pi * k / seg
        pts.append((cx - hl + hw * math.cos(a), cy + hw * math.sin(a),
                    top_z / 2.0))
    P.prism(pts, (0, 0, 1), top_z, material)
    inner = []
    for k in range(seg + 1):
        a = -math.pi / 2 + math.pi * k / seg
        inner.append((cx + hl + (hw - 0.20) * math.cos(a),
                      cy + (hw - 0.20) * math.sin(a), top_z + 0.012))
    for k in range(seg + 1):
        a = math.pi / 2 + math.pi * k / seg
        inner.append((cx - hl + (hw - 0.20) * math.cos(a),
                      cy + (hw - 0.20) * math.sin(a), top_z + 0.012))
    P.add([tuple(q) for q in inner], [tuple(range(len(inner)))], material_top)


def build_terminal(parent, plan, rng, site, stats, lamps):
    grp = _sub(parent, "PROP_TERMINAL")
    stations = [b for b in plan.get("blocks", [])
                if b.get("type") in ("bus_station_ufo", "bus_station_hex")]
    made = {"bays": 0, "buses": 0, "benches": 0, "bins": 0, "timetables": 0,
            "carts": 0, "taxi": 0, "markings": 0}
    marks = PB()
    for st in stations:
        x0, y0, x1, y1 = st["rect"]
        cx, cy = (x0 + x1) / 2.0, (y0 + y1) / 2.0
        west = "ufo" in st.get("type", "")
        if west:
            ix, iy, ilen = -94.0, -119.2, 20.0
            bus_x, bus_y, bus_rot = -94.0, -126.4, 0.0
        else:
            ix, iy, ilen = 88.0, -119.2, 14.0
            bus_x, bus_y, bus_rot = 88.0, -126.4, math.pi
        # rounded island platform between the apron and the bus bays
        P = PB()
        _stadium_island(P, ix, iy, ilen, 3.0, 0.22, mat("conc"),
                        mat("conc_light"))
        P.build(grp, f"PROP_TERMINAL_ISLAND_{st['id']}")
        made["bays"] += 1
        site.claim(ix, iy, ilen / 2.0)
        # benches face the bus bays (south) + a litter bin
        for bi, t in enumerate((-0.30, 0.0, 0.30)):
            bx = ix + t * ilen
            bench_variant(grp, f"PROP_TERMINAL_BENCH_{st['id']}_{bi}", bx,
                          iy + 0.15, 0.0, rng, bi % 3)
            made["benches"] += 1
            if bi == 1:
                kit.trash_bin(grp, f"PROP_TERMINAL_BIN_{st['id']}",
                              (ix + ilen * 0.40, iy + 0.95, 0.0))
                made["bins"] += 1
        # timetable board near the east end
        tb = xf = None
        tx, ty = ix + ilen / 2.0 - 1.4, iy + 0.35
        Pb = PB()
        Pb.box((tx, ty, 1.15), (1.00, 0.09, 1.15), mat("metal_dark"))
        Pb.box((tx, ty - 0.055, 1.15), (0.90, 0.02, 1.00), mat("sign_white"))
        for k in range(4):
            Pb.box((tx, ty - 0.068, 1.45 - k * 0.17), (0.75, 0.01, 0.035),
                   mat("sign_dark"))
        Pb.tube((tx, ty, 0.0), (tx, ty, 0.62), 0.05, 0.05, seg=6,
                material=mat("metal_mid"))
        Pb.build(grp, f"PROP_TERMINAL_TIMETABLE_{st['id']}")
        made["timetables"] += 1
        # steel canopy over the middle of the island
        Pc = PB()
        for (ox, oy) in ((-2.1, -1.05), (2.1, -1.05), (-2.1, 1.05), (2.1, 1.05)):
            Pc.tube((ix + ox, iy + oy, 0.22), (ix + ox, iy + oy, 2.55),
                    0.055, 0.05, seg=6, material=mat("metal_mid"))
        Pc.box((ix, iy, 2.62), (4.9, 2.7, 0.12), mat("metal_mid"))
        Pc.box((ix, iy, 2.50), (4.4, 2.3, 0.05), mat("metal_dark"))
        Pc.build(grp, f"PROP_TERMINAL_CANOPY_{st['id']}")
        # curved bay guide arcs + straight bay separators on the apron
        for ax in (ix - ilen / 2.0 + 2.4, ix + ilen / 2.0 - 2.4):
            made["markings"] += _terminal_marking(marks, ax, iy - 1.4, 6.8,
                                                  math.radians(28),
                                                  math.radians(152), seg=6)
        nsep = max(2, int(ilen / 6.5))
        for k in range(nsep + 1):
            sx = ix - ilen / 2.0 + 1.0 + (ilen - 2.0) * k / nsep
            marks.box((sx, iy - 2.4, 0.022), (0.10, 4.4, 0.012),
                      mat("sign_white"))
            made["markings"] += 1
        # parked LAZ bus in the bay + apron lettering
        _laz_bus(grp, f"PROP_LAZ_BUS_{st['id']}", bus_x, bus_y, bus_rot, rng)
        made["buses"] += 1
        _text(grp, f"PROP_TERMINAL_TXT_{st['id']}", "АВТОБУС",
              (ix, iy - 4.8, 0.03), 1.0, mat("sign_white"), normal=(0, 0, 1))
        # luggage carts near the station entrances
        for ci, ox in enumerate((-4.2, 4.2)):
            px, py = cx + ox, -115.4
            if not site.ok(px, py, 0.3, 1.0, True):
                continue
            _luggage_cart(grp, f"PROP_TERMINAL_CART_{st['id']}_{ci}", px, py,
                          rng.uniform(-0.4, 0.4), rng)
            made["carts"] += 1
        # taxi stand sign beside the plaza walkway
        tx2 = -46.0 if west else 44.0
        if site.ok(tx2, -115.2, 0.2, 1.0, True):
            _taxi_sign(grp, f"PROP_TAXI_{st['id']}", tx2, -115.2, 0.0, rng)
            made["taxi"] += 1
    # zebra crossings across the terminal apron
    for (x, y) in ((0, -116.5), (-62, -115.2), (48, -115.2)):
        for k in range(5):
            marks.box((x - 2.0 + k * 0.9, y, 0.022), (0.45, 2.4, 0.012),
                      mat("sign_white"))
            made["markings"] += 1
    marks.build(grp, "PROP_TERMINAL_MARKINGS")
    stats["terminal"] = made


# ----------------------------------------------------------------------------
# round 3 — east quarter (hedges / bike racks / notice boards / parking rows)
# ----------------------------------------------------------------------------
def _simple_car(grp, name, x, y, rot, rng):
    P = PB()
    T = XF(x, y, rot)
    body = mat(rng.choice(("car_body_a", "car_body_b", "car_body_c")))
    glass = mat("bus_glass")
    dark = mat("metal_dark")
    rub = mat("rubber")
    P.box(T(0, 0, 0.52), (4.05, 1.72, 0.50), body)
    P.box(T(-0.15, 0, 0.98), (2.30, 1.62, 0.44), body)
    P.box(T(-0.15, 0, 1.07), (2.18, 1.66, 0.30), glass)
    P.box(T(0, 0, 0.30), (4.10, 1.76, 0.12), dark)
    for wx in (-1.30, 1.30):
        for wy in (-0.80, 0.80):
            P.tube(T(wx, wy - 0.10, 0.34), T(wx, wy + 0.10, 0.34), 0.32, 0.32,
                   seg=10, material=rub)
    for wy in (-0.56, 0.56):
        P.box(T(2.02, wy, 0.72), (0.06, 0.20, 0.13), mat("sign_white"))
        P.box(T(-2.02, wy, 0.70), (0.06, 0.18, 0.12), mat("sign_red"))
    P.build(grp, name)


def _bike_rack(grp, name, x, y, rot, count=5):
    P = PB()
    T = XF(x, y, rot)
    metal = mat("galv")
    for side in (-0.55, 0.55):
        P.tube(T(0, side, 0.07), T(0, side, 0.42), 0.03, 0.03, seg=5,
               material=metal)
    P.box(T(0, 0, 0.07), (0.10, 1.15, 0.06), metal)
    for i in range(count):
        cx = (i - (count - 1) / 2.0) * 0.55
        P.polyline([T(cx, -0.18, 0.04), T(cx, -0.18, 0.72),
                    T(cx, 0.18, 0.72), T(cx, 0.18, 0.04)], 0.028, seg=4,
                   material=metal)
        P.tube(T(cx, -0.18, 0.42), T(cx, 0.18, 0.42), 0.02, 0.02, seg=4,
               material=metal)
    P.build(grp, name)


def build_east_quarter(parent, plan, rng, site, stats):
    grp = _sub(parent, "PROP_EAST_Q")
    made = {"hedges": 0, "bike_racks": 0, "notice_boards": 0, "cars": 0,
            "markings": 0}
    # hedges along the institute / vysotka fronts (west facades)
    for b in plan.get("blocks", []):
        if b.get("type") not in ("institute_sino", "institute_robotics",
                                 "museum_colonnade", "vysotka_main",
                                 "vysotka_hotel", "vysotka_ministry",
                                 "market", "sanatorium_druzhba",
                                 "ministry_transport"):
            continue
        x0, y0, x1, y1 = b["rect"]
        hx = x0 - 2.0
        length = (y1 - y0) - 4.0
        if length < 4.0:
            continue
        hedge_run(grp, f"PROP_HEDGE_{b['id']}", (hx, y0 + 2.0), (hx, y1 - 2.0),
                  0.85, 1.0, gaps=((length / 2.0 - 1.6, length / 2.0 + 1.6),))
        made["hedges"] += 1
    # bike racks + notice boards along the quarter's pedestrian spine
    for i, (bx, by, rot) in enumerate(((126.2, 30.0, 0.0), (126.2, 72.0, 0.0),
                                       (126.2, 104.0, 0.0), (126.2, -22.0, 0.0))):
        if not site.ok(bx, by, 0.4, 1.0, True) or site.clash(bx, by, 1.0):
            continue
        site.claim(bx, by, 1.4)
        _bike_rack(grp, f"PROP_BIKE_RACK_{i}", bx, by, rot, 5)
        made["bike_racks"] += 1
    for i, (nx, ny) in enumerate(((126.6, 50.0), (126.6, 90.0), (126.6, -40.0))):
        if not site.ok(nx, ny, 0.3, 1.0, True) or site.clash(nx, ny, 1.2):
            continue
        site.claim(nx, ny, 1.6)
        kit.poster_board(grp, f"PROP_NOTICE_{i}", (nx, ny, 2.2 / 2.0 + 1.1),
                         (0, 1, 0), (-1, 0, 0), 4.0, 2.0, rng)
        made["notice_boards"] += 1
    # parked rows in the round-3 car zones (vehicles.py only covers round-1 zones)
    marks = PB()
    for zid in ("CARS_VYSOTKA", "CARS_EAST_Q", "CARS_TERMINAL"):
        pz = site.prop.get(zid)
        if not pz:
            continue
        x0, y0, x1, y1 = pz["rect"]
        count = int(pz.get("count", 4))
        w, h = x1 - x0, y1 - y0
        parallel = min(w, h) < 4.0
        if w >= h:
            step = (w - 1.0) / max(1, count)
            for k in range(count):
                x = x0 + 0.8 + step * (k + 0.5)
                y = (y0 + y1) / 2.0
                if not site.ok(x, y, 0.3, 1.0, True):
                    near = site.free_near(x, y, max_r=15.0)
                    if near is None:
                        continue
                    x, y = near
                _simple_car(grp, f"PROP_CAR_{zid}_{k}", x, y,
                            0.0 if parallel else math.pi / 2.0, rng)
                site.claim(x, y, 1.6)
                made["cars"] += 1
                if parallel:
                    marks.box((x, y, 0.02), (0.10, max(3.0, h - 0.6), 0.012),
                              mat("sign_white"))
                    made["markings"] += 1
        else:
            step = (h - 1.0) / max(1, count)
            for k in range(count):
                x = (x0 + x1) / 2.0
                y = y0 + 0.8 + step * (k + 0.5)
                if not site.ok(x, y, 0.3, 1.0, True):
                    near = site.free_near(x, y, max_r=15.0)
                    if near is None:
                        continue
                    x, y = near
                _simple_car(grp, f"PROP_CAR_{zid}_{k}", x, y,
                            math.pi / 2.0 if parallel else 0.0, rng)
                site.claim(x, y, 1.6)
                made["cars"] += 1
    marks.build(grp, "PROP_EAST_Q_MARKINGS")
    stats["east_quarter"] = made


# ----------------------------------------------------------------------------
# round 3 — west forest (trails / fallen logs / benches / stumps / bollards)
# ----------------------------------------------------------------------------
def _trail(P, pts, width, material, z=0.010):
    hw = width / 2.0
    for i in range(len(pts) - 1):
        (ax, ay), (bx, by) = pts[i], pts[i + 1]
        dx, dy = bx - ax, by - ay
        L = math.hypot(dx, dy)
        if L < 1e-6:
            continue
        nx, ny = -dy / L * hw, dx / L * hw
        P.add([(ax + nx, ay + ny, z), (bx + nx, by + ny, z),
               (bx - nx, by - ny, z), (ax - nx, ay - ny, z)], [(0, 1, 2, 3)],
              material)


def _fallen_log(grp, name, x, y, rot, rng, length=4.6):
    P = PB()
    T = XF(x, y, rot)
    bark = mat("bark_bare")
    moss = mat("moss")
    P.tube(T(-length / 2, 0, 0.32), T(length / 2, 0, 0.32), 0.32, 0.24, seg=8,
           material=bark)
    P.tube(T(-length / 2 - 0.05, 0, 0.32), T(-length / 2 - 0.22, 0, 0.32),
           0.40, 0.32, seg=8, material=bark)
    for _k in range(rng.randint(2, 4)):
        bx = rng.uniform(-length * 0.30, length * 0.30)
        a = rng.uniform(0.0, TAU)
        P.tube(T(bx, 0, 0.45), T(bx + math.cos(a) * 0.75,
                                 math.sin(a) * 0.55, 0.88), 0.07, 0.03, seg=5,
               material=bark, caps=False)
    for _k in range(rng.randint(3, 6)):
        mx = rng.uniform(-length * 0.35, length * 0.35)
        P.blob(T(mx, rng.uniform(-0.16, 0.16), 0.50),
               (rng.uniform(0.16, 0.36), rng.uniform(0.10, 0.20),
                rng.uniform(0.05, 0.12)), moss, rng=rng, jitter=0.3, seg=6,
               rings=4)
    P.build(grp, name)


def build_west_forest(parent, plan, rng, site, stats):
    grp = _sub(parent, "PROP_FOREST")
    made = {"trails": 0, "logs": 0, "benches": 0, "stumps": 0, "bollards": 0}
    trails = [
        [(-190, -104), (-186, -70), (-190, -30), (-185, 10), (-189, 50),
         (-185, 92), (-190, 126)],
        [(-186, -18), (-179, 8), (-173, 34), (-169, 62)],
        [(-192, 40), (-198, 74)],
    ]
    P = PB()
    for tr in trails:
        _trail(P, tr, 2.0, mat("dirt"), z=0.010)
        made["trails"] += 1
    P.build(grp, "PROP_FOREST_TRAILS")
    for i, (x, y, rot, ln) in enumerate(((-193, -86, -0.4, 5.2),
                                         (-184, -52, 0.7, 4.2),
                                         (-190, 14, 0.2, 5.6),
                                         (-186, 62, 1.1, 4.6),
                                         (-180, 96, -0.6, 3.8),
                                         (-192, -8, 0.9, 4.4))):
        if not site.ok(x, y, 1.2, 1.0, True):
            continue
        site.claim(x, y, 2.0)
        _fallen_log(grp, f"PROP_FOREST_LOG_{i}", x, y, rot, rng, length=ln)
        made["logs"] += 1
    for i, (x, y, facing) in enumerate(((-186.6, -58, math.pi / 2),
                                        (-185.2, -6, -math.pi / 2),
                                        (-187.0, 54, math.pi / 2))):
        if not site.ok(x, y, 0.6, 1.0, True):
            continue
        site.claim(x, y, 1.5)
        bench_variant(grp, f"PROP_FOREST_BENCH_{i}", x, y, facing, rng, i % 4)
        made["benches"] += 1
    # stumps + bollards along the trails
    Pb = PB()
    for i in range(6):
        x = rng.uniform(-196, -181)
        y = rng.uniform(-110, 140)
        if not site.ok(x, y, 0.8, 1.0, True) or site.clash(x, y, 1.2):
            continue
        site.claim(x, y, 0.9)
        Pb.tube((x, y, 0.0), (x, y, 0.42), 0.34, 0.30, seg=8,
                material=mat("bark_bare"))
        Pb.tube((x, y, 0.42), (x, y, 0.48), 0.30, 0.26, seg=8,
                material=mat("moss"))
        made["stumps"] += 1
    for tr in trails:
        for i in range(len(tr) - 1):
            (ax, ay), (bx, by) = tr[i], tr[i + 1]
            L = math.hypot(bx - ax, by - ay)
            n = max(1, int(L / 24.0))
            for k in range(1, n + 1):
                t = k / (n + 1.0)
                x = ax + (bx - ax) * t + 1.6
                y = ay + (by - ay) * t
                if not site.ok(x, y, 0.2, 1.0, True) or site.clash(x, y, 0.6):
                    continue
                site.claim(x, y, 0.6)
                _bollard(Pb, x, y, rng)
                made["bollards"] += 1
    Pb.build(grp, "PROP_FOREST_STUMPS_BOLLARDS")
    stats["west_forest"] = made


def build(parent, plan, rng=None, ctx=None):
    """Place every prop of the district under ``parent``.

    Returns {"lamps": [{"pos": (x, y, z), "kind": "street"|"plaza", "height": h}, ...],
             "stats": {...}} — the Lead adds the actual point lights.
    """
    rng = rng or kit.RNG(11)
    ctx = ctx or {}
    if not plan:
        plan = ctx.get("plan") or {}
    site = Site(plan)
    root = _sub(parent, "PROP_ROOT")
    stats = {"tree_positions": []}

    lamps = []
    # playgrounds claim their equipment first so the sparse courtyard globes are
    # placed around the equipment instead of on top of it
    build_playgrounds(root, plan, rng, site, stats)
    # round 3: memorial esplanade / terminals / east quarter / forest claim first
    build_monuments(root, plan, rng, site, stats, lamps)
    build_terminal(root, plan, rng, site, stats, lamps)
    build_east_quarter(root, plan, rng, site, stats)
    build_west_forest(root, plan, rng, site, stats)
    lamps.extend(build_lamps(root, plan, rng, site, stats))
    build_pavilion(root, plan, rng, site, stats, lamps)
    build_park(root, plan, rng, site, stats, lamps)
    build_trees(root, plan, rng, site, stats)
    build_laundry(root, plan, rng, site, stats)
    build_dumpsters(root, plan, rng, site, stats)
    build_furniture(root, plan, rng, site, stats)
    build_fences(root, plan, rng, site, stats)
    build_boards(root, plan, rng, site, stats)
    build_bus_stop(root, plan, rng, site, stats)
    build_signs(root, plan, rng, site, stats)
    build_banner(root, plan, rng, site, stats)
    build_poles(root, plan, rng, site, stats)
    build_tree_clusters(root, plan, rng, site, stats)
    build_ground_cover(root, plan, rng, site, stats, lamps)

    # summary counts
    meshes = [o for o in root.children_recursive if o.type == "MESH"]
    stats["objects"] = len(meshes)
    stats["tris"] = sum(len(o.data.polygons) for o in meshes)
    stats["trees"] = int(sum(stats.get("tree_counts", {}).values()))
    stats["lamps"] = len(lamps)
    return {"lamps": lamps, "stats": stats}





