#!/usr/bin/env python3
"""nav.py -- pedestrian navigation graph generator for the Soviet Blue-Hour microdistrict.

Pure Python (no Blender needed). Reads ``tools/plan.json`` and writes:

    map/nav_graph.json          nodes + edges + POIs + route anchors (Blender & Unity coords)
    map/nav_graph.csv           node table (id, blender xyz, unity xyz, kind, name)
    map/nav_graph_edges.csv     edge table (a, b, distance_m)

Graph sources
-------------
* ``paths[]``      -- walkable polylines with width, resampled every ~3.5 m
* ``sidewalks[]``  -- 240 m / strip walks, resampled along the long axis
* ``zones[]``      -- plaza / courtyards / terrace, 4 m grid (building footprints carved out)
* ``blocks[].faces`` (via kit.wall_frame convention) -- building entrance POIs
* W_K3 through-arch passage at x = -81, y in [-34, -22] (tunnel is 4 m wide)
* ``landmarks[]``  -- obelisk / fountain / flags / bus stop / tram stop + building landmarks
* ``nav.anchors`` + ``nav.edges`` -- curated *route anchors* (A_*) used by gameplay scripts

Coordinate contract
-------------------
Blender: metres, Z-up, +Y north, +X east, ground z = 0.
Unity:   metres, Y-up, Z-forward-north -> ``(x, y, z)_unity = (x_bl, z_bl, -y_bl)``.

Usage
-----
    python tools/nav.py
    python tools/nav.py --plan tools/plan.json --out map/
"""
from __future__ import annotations

import argparse
import json
import math
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)

PATH_STEP = 3.5      # resample step along path polylines (m)
SIDEWALK_STEP = 4.0  # resample step along sidewalk center lines (m)
ZONE_STEP = 4.0      # grid step inside walkable zones (m)
LINK_RADIUS = 4.6    # proximity radius that fuses neighbouring walkable surfaces (m)
NODE_MARGIN = 0.65   # building clearance for node placement (m)
EDGE_CLEAR = 0.20    # segment / building clearance (m)

# zones whose surface is walkable at pedestrian level
WALKABLE_ZONES = {"plaza", "court", "terrace", "playground", "steps"}
# Round 2/3: park lawns are crossed on grass, so selected lawn zones are walkable too.
WALKABLE_LAWN_ZONES = {"PARK_LAWN"}
# stepped zones (PLAZA_STEPS) / terraces get a real z from plan terraces + zone heights
STEPS_Z = {"PLAZA_STEPS": 1.2}


# ----------------------------------------------------------------------------
# geometry helpers
# ----------------------------------------------------------------------------
def wall_frame(rect, face):
    """Mirror of kit.wall_frame: returns (P0, (ux, uy), length, (nx, ny))."""
    x0, y0, x1, y1 = rect
    if face == "PY":
        return (x0, y1), (1.0, 0.0), x1 - x0, (0.0, 1.0)
    if face == "NY":
        return (x0, y0), (1.0, 0.0), x1 - x0, (0.0, -1.0)
    if face == "PX":
        return (x1, y0), (0.0, 1.0), y1 - y0, (1.0, 0.0)
    if face == "NX":
        return (x0, y0), (0.0, 1.0), y1 - y0, (-1.0, 0.0)
    raise ValueError("bad face %r" % face)


def rect_expand(rect, m):
    x0, y0, x1, y1 = rect
    return (x0 - m, y0 - m, x1 + m, y1 + m)


def point_in_rect(x, y, rect, m=0.0):
    x0, y0, x1, y1 = rect
    return x0 - m <= x <= x1 + m and y0 - m <= y <= y1 + m


def seg_rect_hit(p, q, rect):
    """Liang-Barsky style segment/AABB overlap (2D)."""
    x0, y0, x1, y1 = rect
    (ax, ay), (bx, by) = p, q
    dx, dy = bx - ax, by - ay
    t0, t1 = 0.0, 1.0
    for edge, d, lo, hi in ((ax, dx, x0, x1), (ay, dy, y0, y1)):
        if abs(d) < 1e-12:
            if edge < lo or edge > hi:
                return False
            continue
        ta = (lo - edge) / d
        tb = (hi - edge) / d
        if ta > tb:
            ta, tb = tb, ta
        t0 = max(t0, ta)
        t1 = min(t1, tb)
        if t0 > t1:
            return False
    return True


def dist(a, b):
    return math.hypot(a[0] - b[0], a[1] - b[1])


def lerp(a, b, t):
    return (a[0] + (b[0] - a[0]) * t, a[1] + (b[1] - a[1]) * t)


def sample_polyline(pts, step):
    """Resample a polyline: keep vertices, insert points every <= step metres."""
    out = [tuple(pts[0])]
    for p, q in zip(pts, pts[1:]):
        seg = dist(p, q)
        n = max(1, int(math.ceil(seg / step)))
        for i in range(1, n + 1):
            out.append(lerp(p, q, i / n))
    # snap vertices that were re-inserted by the previous segment
    return out


# ----------------------------------------------------------------------------
# obstacle model (buildings; khrush_arch blocks have a through-arch tunnel)
# ----------------------------------------------------------------------------
class Obstacles:
    """Building footprints, with through-arch tunnels carved out of khrush_arch blocks.

    Round 2 has two arches: W_K3 (x=-81) and W2_K3 (x=-132).  The arch axis is the
    centre of the block's long (PY/NY) face, exactly as mod_residential builds it.
    """

    ARCH_HALF = 2.0   # tunnel half width (4 m passage between 0.3 m side walls)

    def __init__(self, plan):
        self.rects = []      # (block_id, rect) -- solid volumes at walking height
        self.solids = []     # (block_id, rect) -- boxes used for segment tests
        self.arches = {}     # block_id -> {"ax": arch_x, "y0": .., "y1": ..}
        self.blocks = {b["id"]: b for b in plan["blocks"]}
        self.water = [tuple(float(v) for v in z["rect"]) for z in plan.get("zones", [])
                      if z.get("kind") == "water"]
        base_x, base_y = plan.get("base", [240, 180])
        self.bounds = (-base_x / 2.0, -base_y / 2.0, base_x / 2.0, base_y / 2.0)
        for b in plan["blocks"]:
            rect = tuple(float(v) for v in b["rect"])
            bid = b["id"]
            if b.get("type") == "khrush_arch":
                x0, y0, x1, y1 = rect
                ax = (x0 + x1) / 2.0      # arch sits at the centre of the long face
                ax0, ax1 = ax - self.ARCH_HALF, ax + self.ARCH_HALF
                self.arches[bid] = {"ax": ax, "y0": y0, "y1": y1}
                self.rects += [(bid + "_W", (x0, y0, ax0, y1)),
                               (bid + "_E", (ax1, y0, x1, y1))]
                self.solids += [(bid + "_W", (x0, y0, ax0, y1)),
                                (bid + "_E", (ax1, y0, x1, y1))]
            else:
                self.rects.append((bid, rect))
                self.solids.append((bid, rect))

    def point_blocked(self, x, y, margin=NODE_MARGIN, water=True):
        for _bid, r in self.rects:
            if point_in_rect(x, y, r, margin):
                return True
        if water:
            for r in self.water:
                if point_in_rect(x, y, r, 1.5):
                    return True
        return False

    def in_bounds(self, x, y, margin=1.0):
        x0, y0, x1, y1 = self.bounds
        return x0 + margin <= x <= x1 - margin and y0 + margin <= y <= y1 - margin

    def segment_blocked(self, p, q, clear=EDGE_CLEAR):
        for _bid, r in self.solids:
            if seg_rect_hit(p, q, rect_expand(r, -clear)):
                return True
        for r in self.water:
            if seg_rect_hit(p, q, r):
                return True
        return False

    def push_free(self, x, y, margin=NODE_MARGIN, step=1.2):
        """Move a point out of a building / pond to the nearest free side."""
        if not self.point_blocked(x, y, margin):
            return x, y
        best = None
        for r in [r for _bid, r in self.solids] + list(self.water):
            if not point_in_rect(x, y, r, margin):
                continue
            x0, y0, x1, y1 = r
            cand = [(x0 - margin - step, y), (x1 + margin + step, y),
                    (x, y0 - margin - step), (x, y1 + margin + step)]
            for c in cand:
                d = dist((x, y), c)
                if best is None or d < best[0]:
                    best = (d, c)
        return best[1] if best else (x, y)


# ----------------------------------------------------------------------------
# node store with spatial dedupe
# ----------------------------------------------------------------------------
class NodeStore:
    def __init__(self, cell=1.5):
        self.cell = cell
        self.nodes = []          # dicts: id, x, y, z, kind, name
        self.buckets = {}

    def _key(self, x, y):
        return (int(math.floor(x / self.cell)), int(math.floor(y / self.cell)))

    def find_near(self, x, y, radius):
        kx, ky = self._key(x, y)
        best = None
        for dx in (-1, 0, 1):
            for dy in (-1, 0, 1):
                for nid in self.buckets.get((kx + dx, ky + dy), ()):
                    n = self.nodes[nid]
                    d = math.hypot(n["x"] - x, n["y"] - y)
                    if d <= radius and (best is None or d < best[0]):
                        best = (d, nid)
        return best

    def add(self, x, y, z=0.0, kind="walk", name=None, dedupe=1.35):
        """Add a node, unless an equivalent node already exists within dedupe metres."""
        near = self.find_near(x, y, dedupe)
        if near is not None:
            nid = near[1]
            n = self.nodes[nid]
            # keep the most specific kind label
            if kind != "walk" and n["kind"] == "walk":
                n["kind"] = kind
                n["name"] = name
            return nid, False
        nid = len(self.nodes)
        self.nodes.append({"id": nid, "x": round(x, 3), "y": round(y, 3), "z": round(z, 3),
                           "kind": kind, "name": name})
        self.buckets.setdefault(self._key(x, y), []).append(nid)
        return nid, True


# ----------------------------------------------------------------------------
# graph builder
# ----------------------------------------------------------------------------
class NavGraph:
    def __init__(self, obs):
        self.obs = obs
        self.store = NodeStore()
        self.edges = {}          # (a, b) sorted -> dist

    # -- nodes ---------------------------------------------------------------
    def add_walk(self, x, y, kind="walk", name=None, z=0.0):
        x, y = self.obs.push_free(x, y) if self.obs.point_blocked(x, y) else (x, y)
        return self.store.add(x, y, z, kind, name)[0]

    def link(self, a, b):
        if a == b:
            return
        p, q = self.store.nodes[a], self.store.nodes[b]
        d = dist((p["x"], p["y"]), (q["x"], q["y"]))
        if d < 1e-6 or self.obs.segment_blocked((p["x"], p["y"]), (q["x"], q["y"])):
            return
        key = (a, b) if a < b else (b, a)
        if key not in self.edges or d < self.edges[key]:
            self.edges[key] = d

    def link_chain(self, ids):
        for a, b in zip(ids, ids[1:]):
            self.link(a, b)

    # -- sources -------------------------------------------------------------
    def build_paths(self, plan, stats):
        for p in plan["paths"]:
            pts = sample_polyline([tuple(map(float, q)) for q in p["pts"]], PATH_STEP)
            ids = []
            for x, y in pts:
                if self.obs.point_blocked(x, y) or not self.obs.in_bounds(x, y):
                    continue          # path crosses a building/pond -> break the chain
                ids.append(self.add_walk(x, y, kind="path", name=p["id"]))
            self.link_chain(ids)
            stats["path_nodes"] += len(ids)
        return

    def build_sidewalks(self, plan, stats):
        for s in plan["sidewalks"]:
            x0, y0, x1, y1 = s["rect"]
            if (x1 - x0) >= (y1 - y0):
                # horizontal strip -> walk along the long axis through the middle
                cy = (y0 + y1) / 2.0
                n = max(1, int(math.ceil((x1 - x0) / SIDEWALK_STEP)))
                pts = [(x0 + (x1 - x0) * i / n, cy) for i in range(n + 1)]
            else:
                cx = (x0 + x1) / 2.0
                n = max(1, int(math.ceil((y1 - y0) / SIDEWALK_STEP)))
                pts = [(cx, y0 + (y1 - y0) * i / n) for i in range(n + 1)]
            ids = []
            for x, y in pts:
                if self.obs.point_blocked(x, y) or not self.obs.in_bounds(x, y):
                    continue
                ids.append(self.add_walk(x, y, kind="sidewalk", name=s["id"]))
            self.link_chain(ids)
            stats["sidewalk_nodes"] += len(ids)

    def build_zones(self, plan, stats):
        skip = [z["rect"] for z in plan["zones"] if z["kind"] in ("lawn",)
                and z["id"] not in WALKABLE_LAWN_ZONES]
        for z in plan["zones"]:
            if z["kind"] not in WALKABLE_ZONES and z["id"] not in WALKABLE_LAWN_ZONES:
                continue
            x0, y0, x1, y1 = z["rect"]
            nx = max(2, int(math.ceil((x1 - x0) / ZONE_STEP)))
            ny = max(2, int(math.ceil((y1 - y0) / ZONE_STEP)))
            grid = {}
            for j in range(ny + 1):
                for i in range(nx + 1):
                    x = x0 + (x1 - x0) * i / nx
                    y = y0 + (y1 - y0) * j / ny
                    if self.obs.point_blocked(x, y):
                        continue
                    if any(point_in_rect(x, y, r) for r in skip if r is not z):
                        continue
                    grid[(i, j)] = self.add_walk(x, y, kind="zone", name=z["id"])
                    stats["zone_nodes"] += 1
            for (i, j), nid in grid.items():
                for di, dj in ((1, 0), (0, 1)):
                    other = grid.get((i + di, j + dj))
                    if other is not None:
                        self.link(nid, other)

    def build_arches(self, plan, stats):
        """Carve walk nodes through every khrush_arch tunnel and bridge both ends."""
        x0b, y0b, x1b, y1b = self.obs.bounds
        for bid, arch in self.obs.arches.items():
            ax, ay0, ay1 = arch["ax"], arch["y0"], arch["y1"]
            ys = [ay0 + 0.8 + (ay1 - ay0 - 1.6) * i / 3.0 for i in range(4)]
            ids = [self.add_walk(ax, y, kind="arch", name=bid + "_ARCH") for y in ys]
            self.link_chain(ids)
            for end, stepy in ((ids[0], -1.0), (ids[-1], 1.0)):
                n = self.store.nodes[end]
                px, py = n["x"], n["y"]
                best = None
                for k in range(1, 120):
                    qy = py + stepy * k * 1.0
                    if qy < y0b or qy > y1b:
                        break
                    near = self.store.find_near(px, qy, 5.0)
                    if near is not None and near[1] != end:
                        best = near[1]
                        break
                if best is not None:
                    self.link(end, best)
            stats["arch_nodes"] += len(ids)
            stats["arches"] = stats.get("arches", []) + [bid]

    def build_entrances(self, plan, stats):
        entrances = []
        for b in plan["blocks"]:
            rect = b["rect"]
            bid = b["id"]
            for face in b.get("faces", []):
                P0, u, L, n = wall_frame(rect, face)
                if L < 6:
                    continue
                if bid in self.obs.arches:
                    fracs = (0.22, 0.78)          # entrances flank the tunnel
                elif L >= 26:
                    fracs = (0.25, 0.5, 0.75)
                elif L >= 14:
                    fracs = (0.3, 0.7)
                else:
                    fracs = (0.5,)
                for f in fracs:
                    s = L * f
                    # skip the arch mouth itself
                    if bid in self.obs.arches and abs((P0[0] + u[0] * s) - self.obs.arches[bid]["ax"]) < 5.0:
                        continue
                    x = P0[0] + u[0] * s + n[0] * 2.1
                    y = P0[1] + u[1] * s + n[1] * 2.1
                    if self.obs.point_blocked(x, y):
                        continue
                    nid, _new = self.store.add(x, y, 0.0, "entrance", bid, dedupe=1.2)
                    entrances.append(nid)
                    stats["entrance_nodes"] += 1
        # attach entrances to the walkable network
        for nid in entrances:
            n = self.store.nodes[nid]
            target = self.store.find_near(n["x"], n["y"], 9.0)
            if target is not None and target[1] != nid:
                self.link(nid, target[1])
        return entrances

    def build_pois(self, plan, stats):
        """POI nodes for landmarks + the landmark buildings."""
        pois = []

        def poi(label, label_ru, x, y, kind="landmark", node_kind="poi", connect=14.0):
            nid, _ = self.store.add(x, y, 0.0, node_kind, label, dedupe=1.0)
            n = self.store.nodes[nid]
            near = self.store.find_near(x, y, connect)
            if near is not None and near[1] != nid:
                self.link(nid, near[1])
            pois.append({"id": label, "label": label, "label_ru": label_ru, "kind": kind,
                         "x": round(n["x"], 3), "y": round(n["y"], 3), "z": 0.0,
                         "node": nid})
            stats["poi_nodes"] += 1
            return nid

        # landmark POIs ------------------------------------------------------
        poi("OBELISK", "Обелиск", 0.0, -56.5)
        poi("FOUNTAIN", "Сухой фонтан", 7.6, -43.0)
        poi("FLAGS", "Флаги", 4.8, -67.0)
        poi("BUS_STOP", "Автобус", -40.0, -72.0)
        poi("TRAM_STOP", "Трамвай", 18.0, -81.4)
        poi("BUS_STOP_2", "Автобус (юг)", 40.0, -89.5)
        poi("PAVILION", "Павильон", 54.0, 96.0)       # south shore of the pond
        poi("PARK_SIGN", "Вход в парк", -122.0, 92.5)
        poi("POND", "Пруд", 5.0, 91.5)                # south bank (water itself is blocked)

        # landmark buildings: POI just outside the primary facade ---------------
        building_pois = {
            "DK": ("DK", "Дом культуры"),
            "SCHOOL": ("SCHOOL", "Школа №3"),
            "KG": ("KG", "Детский сад №9"),
            "SHOP": ("SHOP", "Гастроном"),
            "BOILER": ("BOILER", "Котельная"),
            "KIOSK": ("KIOSK", "Киоск"),
            "GYM": ("GYM", "Спортзал"),
            "CLINIC": ("CLINIC", "Поликлиника"),
            "MARKET": ("MARKET", "Рынок"),
            "KINO": ("KINO", "Кинотеатр"),
            "DOM_BYTA": ("DOM_BYTA", "Дом быта"),
            "BAKERY": ("BAKERY", "Булочная"),
            "BANYA": ("BANYA", "Баня"),
            # ---- Round-3 landmark blocks -------------------------------------
            "MOTHERLAND": ("MOTHERLAND", "Родина-мать"),
            "BREST_RELIEF": ("BREST_RELIEF", "Мемориальная стена"),
            "METRO": ("METRO", "Метро"),
            "MSU_MAIN": ("MSU_MAIN", "МГУ"),
            "UKRAINA": ("UKRAINA", "Гостиница Украина"),
            "MFA": ("MFA", "МИД"),
            "HIT": ("HIT", "Институт"),
            "MUSEUM": ("MUSEUM", "Музей"),
            "ROBOTICS": ("ROBOTICS", "Институт робототехники"),
            "MOT": ("MOT", "Министерство транспорта"),
            "DRUZHBA": ("DRUZHBA", "Санаторий Дружба"),
            "UFO_STATION": ("UFO_STATION", "Автостанция"),
            "HEX_STATION": ("HEX_STATION", "Автостанция (шестигранная)"),
        }

        def facade_poi_pos(bid, off=2.4):
            b = self.obs.blocks.get(bid)
            if b is None:
                return None
            face = (b.get("faces") or ["PY"])[0]
            P0, u, L, n = wall_frame(b["rect"], face)
            s = L * 0.22 if bid in self.obs.arches else L / 2.0
            return (P0[0] + u[0] * s + n[0] * off, P0[1] + u[1] * s + n[1] * off)

        # a couple of POIs sit right next to a landmark node -> nudge them apart
        poi_overrides = {"KINO": (32.0, -89.6)}
        for bid, (pid, ru) in building_pois.items():
            pos = poi_overrides.get(pid) or facade_poi_pos(bid)
            if pos is None:
                continue
            nid = poi(pid, ru, pos[0], pos[1], kind="building", node_kind="poi")
            # tie the POI to the nearest entrance of that block when possible
            best = None
            for e in self.store.nodes:
                if e["kind"] == "entrance" and e["name"] == bid:
                    d = math.hypot(e["x"] - pos[0], e["y"] - pos[1])
                    if best is None or d < best[0]:
                        best = (d, e["id"])
            if best is not None:
                self.link(nid, best[1])
        # stadium has no block: fixed POI inside the stand area
        poi("STADIUM", "Стадион", 80.0, 40.0, kind="building", node_kind="poi")

        # ---- Round-3 landmark props (eternal flame, guard box, flag row, sign, plaque) ----
        LANDMARK_POI = {
            "eternal_flame": (2.6, "landmark", "Вечный огонь"),
            "guard_box": (2.2, "landmark", "Караульная будка"),
            "flag_row": (3.2, "landmark", "Ряд флагов"),
            "metro_sign": (2.0, "landmark", "Знак метро"),
            "plaque": (1.8, "landmark", "Памятная доска"),
        }
        for lm in plan.get("landmarks", []):
            typ = lm.get("type", "")
            if typ not in LANDMARK_POI:
                continue
            off, kind, ru = LANDMARK_POI[typ]
            px, py = lm.get("pos", [0.0, 0.0])
            poi(lm["id"], ru, float(px), float(py) - off, kind=kind, node_kind="poi", connect=18.0)

        # ---- monument viewpoints: esplanade + platform above the stairs ----------------
        poi("ESPLANADE", "Эспланада", 0.0, 120.0, kind="route", node_kind="poi", connect=20.0)
        poi("ESPLANADE_TOP", "Площадка монумента", -34.0, 146.0, kind="route",
            node_kind="poi", connect=16.0)
        return pois

    def build_anchors(self, plan, stats):
        nav = plan.get("nav", {})
        anchors = {}
        for name, x, y in nav.get("anchors", []):
            x, y = self.obs.push_free(float(x), float(y))
            nid = self.add_walk(x, y, kind="anchor", name=name)
            anchors[name] = nid
            stats["anchor_nodes"] += 1
        # attach every anchor/route node to the nearest walkable node (short, legal link)
        for name, nid in anchors.items():
            n = self.store.nodes[nid]
            near = self.store.find_near(n["x"], n["y"], 12.0)
            if near is not None and near[1] != nid:
                self.link(nid, near[1])
        skipped = []
        for a, b in nav.get("edges", []):
            if a in anchors and b in anchors:
                na, nb = self.store.nodes[anchors[a]], self.store.nodes[anchors[b]]
                blocked = self.obs.segment_blocked((na["x"], na["y"]), (nb["x"], nb["y"]))
                before = len(self.edges)
                self.link(anchors[a], anchors[b])
                if len(self.edges) == before and blocked:
                    skipped.append((a, b))
        stats["anchor_edges_blocked"] = len(skipped)
        stats["anchor_edges_blocked_list"] = skipped
        return anchors

    def build_platform(self, plan, stats):
        """Tram island platform (walkable in the middle of the avenue)."""
        plat = plan.get("tram", {}).get("stop_platform")
        if not plat:
            return
        x0, y0, x1, y1 = [float(v) for v in plat]
        cy = (y0 + y1) / 2.0
        n = max(1, int(math.ceil((x1 - x0) / 4.0)))
        ids = []
        for i in range(n + 1):
            x = x0 + (x1 - x0) * i / n
            if self.obs.point_blocked(x, cy):
                continue
            ids.append(self.add_walk(x, cy, kind="platform", name="TRAM_PLATFORM"))
        self.link_chain(ids)
        stats["platform_nodes"] += len(ids)

    def build_terraces(self, plan, stats):
        """Monument esplanade stairs (Round 3): walkable steps + platform ring.

        plan["terraces"] = [{id, rect, steps, rise, step_from, platform_from, axis}]
        The steps part (y step_from..platform_from) is one node row per step, and the
        platform above is a grid ring (the statue block itself is excluded by point_blocked).
        """
        for t in plan.get("terraces", []):
            x0, y0, x1, y1 = [float(v) for v in t["rect"]]
            steps = int(t.get("steps", 6))
            rise = float(t.get("rise", 0.45))
            step_from = float(t.get("step_from", y0))
            platform_from = float(t.get("platform_from", (step_from + y1) / 2.0))
            axis = t.get("axis", "PY")
            if axis in ("PY", "NY"):
                xs, ys = x0, y0
                # --- stair rows -------------------------------------------------
                step_h = (platform_from - step_from) / steps if steps else 1.0
                ncols = max(1, int(math.ceil((x1 - x0) / 4.0)))
                rows = []
                for i in range(steps):
                    cy = step_from + step_h * (i + 0.5)
                    row = []
                    for j in range(ncols + 1):
                        x = x0 + (x1 - x0) * j / ncols
                        if self.obs.point_blocked(x, cy):
                            continue
                        row.append(self.store.add(x, cy, (i + 1) * rise, "terrace",
                                                  t["id"] + "_STEPS", dedupe=0.8)[0])
                    for a, b in zip(row, row[1:]):
                        self.link(a, b)
                    rows.append(row)
                for ra, rb in zip(rows, rows[1:]):
                    for a in ra:
                        for b in rb:
                            if abs(self.store.nodes[a]["x"] - self.store.nodes[b]["x"]) < 2.5:
                                self.link(a, b)
                stats["terrace_nodes"] += sum(len(r) for r in rows)
                # --- platform ring ---------------------------------------------
                top_z = steps * rise
                ny = max(1, int(math.ceil((y1 - platform_from) / 3.0)))
                grid = {}
                for j in range(ny + 1):
                    y = platform_from + (y1 - platform_from) * j / ny
                    for i in range(ncols + 1):
                        x = x0 + (x1 - x0) * i / ncols
                        if self.obs.point_blocked(x, y):
                            continue
                        grid[(i, j)] = self.store.add(x, y, top_z, "terrace_platform",
                                                      t["id"] + "_PLATFORM", dedupe=1.6)[0]
                for (i, j), nid in grid.items():
                    for di, dj in ((1, 0), (0, 1)):
                        o = grid.get((i + di, j + dj))
                        if o is not None:
                            self.link(nid, o)
                stats["terrace_nodes"] += len(grid)
                # --- stitch stairs to the esplanade below and the platform above --
                if rows:
                    bottom = rows[0]
                    for nid in bottom:
                        n = self.store.nodes[nid]
                        near = self.store.find_near(n["x"], n["y"] - 2.0, 4.0)
                        if near is not None and near[1] != nid:
                            self.link(nid, near[1])
                for nid in list(grid.values()):
                    n = self.store.nodes[nid]
                    near = self.store.find_near(n["x"], n["y"], 4.0)
                    if near is not None and near[1] != nid:
                        self.link(nid, near[1])

    def apply_terrain_heights(self, plan):
        """Set node z for walkable terraces / steps (ground is otherwise z=0)."""
        zones_steps = []
        for z in plan.get("zones", []):
            if z["id"] in STEPS_Z:
                zones_steps.append((tuple(float(v) for v in z["rect"]), STEPS_Z[z["id"]]))
        for t in plan.get("terraces", []):
            x0, y0, x1, y1 = [float(v) for v in t["rect"]]
            steps = int(t.get("steps", 6))
            rise = float(t.get("rise", 0.45))
            step_from = float(t.get("step_from", y0))
            platform_from = float(t.get("platform_from", (step_from + y1) / 2.0))
            step_h = (platform_from - step_from) / steps if steps else 1.0
            for n in self.store.nodes:
                x, y = n["x"], n["y"]
                if not (x0 - 0.5 <= x <= x1 + 0.5):
                    continue
                if y > platform_from - 0.01:
                    n["z"] = steps * rise
                elif y >= step_from:
                    idx = min(steps, max(1, int((y - step_from) / step_h) + 1))
                    n["z"] = idx * rise
        for (rect, h) in zones_steps:
            x0, y0, x1, y1 = rect
            for n in self.store.nodes:
                if x0 - 0.3 <= n["x"] <= x1 + 0.3 and y0 - 0.3 <= n["y"] <= y1 + 0.3:
                    n["z"] = max(n["z"], h)

    def build_road_crossings(self, plan, stats):
        """Pedestrian crossings: curb-to-curb links on opposite sides of a painted road.

        Roads themselves are not sampled as walkable surfaces, but the map has zebra
        crossings.  Only sidewalk/platform (curb) nodes are paired and one crossing is
        kept per 6 m cell of the road, otherwise two dense sidewalk chains would generate
        thousands of redundant edges.
        """
        roads = [tuple(float(v) for v in r["rect"]) for r in plan.get("roads", [])]
        if not roads:
            return
        cand = [n["id"] for n in self.store.nodes
                if n["kind"] in ("sidewalk", "platform")]
        cell = 22.0
        buckets = {}
        for nid in cand:
            n = self.store.nodes[nid]
            buckets.setdefault((int(math.floor(n["x"] / cell)),
                                int(math.floor(n["y"] / cell))), []).append(nid)
        best = {}      # (road_idx, cell_x, cell_y) -> (dist, a, b)
        for nid in cand:
            n = self.store.nodes[nid]
            kx, ky = int(math.floor(n["x"] / cell)), int(math.floor(n["y"] / cell))
            for dx in (-1, 0, 1):
                for dy in (-1, 0, 1):
                    for oid in buckets.get((kx + dx, ky + dy), ()):
                        if oid <= nid:
                            continue
                        o = self.store.nodes[oid]
                        d = math.hypot(o["x"] - n["x"], o["y"] - n["y"])
                        if d < 6.0 or d > 22.0:
                            continue
                        mx, my = (n["x"] + o["x"]) / 2.0, (n["y"] + o["y"]) / 2.0
                        ridx = next((i for i, r in enumerate(roads)
                                     if point_in_rect(mx, my, r)), None)
                        if ridx is None:
                            continue
                        if self.obs.segment_blocked((n["x"], n["y"]), (o["x"], o["y"])):
                            continue
                        key = (ridx, int(math.floor(mx / 6.0)), int(math.floor(my / 6.0)))
                        if key not in best or d < best[key][0]:
                            best[key] = (d, nid, oid)
        added = 0
        for (d, a, b) in best.values():
            key = (a, b) if a < b else (b, a)
            if key not in self.edges:
                self.edges[key] = d
                added += 1
        stats["road_crossing_edges"] = added

    # -- fusion --------------------------------------------------------------
    def fuse_proximity(self, stats):
        """Connect nearby walkable nodes (<= LINK_RADIUS) that are not wall-separated."""
        nodes = self.store.nodes
        cell = LINK_RADIUS
        buckets = {}
        for n in nodes:
            buckets.setdefault((int(math.floor(n["x"] / cell)), int(math.floor(n["y"] / cell))), []).append(n["id"])
        added = 0
        for n in nodes:
            kx, ky = int(math.floor(n["x"] / cell)), int(math.floor(n["y"] / cell))
            for dx in (-1, 0, 1):
                for dy in (-1, 0, 1):
                    for oid in buckets.get((kx + dx, ky + dy), ()):
                        if oid <= n["id"]:
                            continue
                        o = nodes[oid]
                        d = math.hypot(o["x"] - n["x"], o["y"] - n["y"])
                        if d > LINK_RADIUS or d < 0.9:
                            continue
                        key = (n["id"], oid)
                        if key in self.edges:
                            continue
                        if self.obs.segment_blocked((n["x"], n["y"]), (o["x"], o["y"])):
                            continue
                        self.edges[key] = d
                        added += 1
        stats["fused_edges"] = added

    # -- connectivity --------------------------------------------------------
    def components(self):
        parent = list(range(len(self.store.nodes)))

        def find(i):
            while parent[i] != i:
                parent[i] = parent[parent[i]]
                i = parent[i]
            return i

        for a, b in self.edges:
            ra, rb = find(a), find(b)
            if ra != rb:
                parent[ra] = rb
        groups = {}
        for i in range(len(self.store.nodes)):
            groups.setdefault(find(i), []).append(i)
        return list(groups.values())

    def connect_components(self, stats):
        joined = 0
        while True:
            comps = self.components()
            if len(comps) <= 1:
                break
            comps.sort(key=len, reverse=True)
            main = comps[0]
            merged = False
            for other in comps[1:]:
                best = None
                for aid in other:
                    a = self.store.nodes[aid]
                    for bid in main:
                        b = self.store.nodes[bid]
                        d = math.hypot(a["x"] - b["x"], a["y"] - b["y"])
                        if best is not None and d >= best[0]:
                            continue
                        if self.obs.segment_blocked((a["x"], a["y"]), (b["x"], b["y"])):
                            continue
                        best = (d, aid, bid)
                if best is not None:
                    self.edges[(min(best[1], best[2]), max(best[1], best[2]))] = best[0]
                    joined += 1
                    merged = True
                    break
            if not merged:
                # fall back: bridge the two nearest nodes ignoring obstacles
                other = comps[1]
                best = None
                for aid in other:
                    a = self.store.nodes[aid]
                    for bid in main:
                        b = self.store.nodes[bid]
                        d = math.hypot(a["x"] - b["x"], a["y"] - b["y"])
                        if best is None or d < best[0]:
                            best = (d, aid, bid)
                if best is None:
                    break
                self.edges[(min(best[1], best[2]), max(best[1], best[2]))] = best[0]
                joined += 1
        stats["component_join_edges"] = joined

    # -- grid detours --------------------------------------------------------
    def _astar(self, start, goal, cell=1.0, max_expand=400000):
        """4-neighbour A* on a 1 m grid that respects buildings, water and map bounds."""
        import heapq
        x0b, y0b, x1b, y1b = self.obs.bounds

        def to_cell(p):
            return (int(round(p[0] / cell)), int(round(p[1] / cell)))

        def to_world(c):
            return (c[0] * cell, c[1] * cell)

        def free(c):
            x, y = to_world(c)
            if not self.obs.in_bounds(x, y, 0.6):
                return False
            return not self.obs.point_blocked(x, y)

        def snap(p):
            c = to_cell(p)
            if free(c):
                return c
            fx, fy = self.obs.push_free(p[0], p[1])
            c = to_cell((fx, fy))
            if free(c):
                return c
            for r in range(1, 25):
                for dx in range(-r, r + 1):
                    for dy in (-r, r) if abs(dx) != r else range(-r, r + 1):
                        cand = (c[0] + dx, c[1] + dy)
                        if free(cand):
                            return cand
            return None

        s, g = snap(start), snap(goal)
        if s is None or g is None:
            return None
        openq = [(0, s)]
        came = {s: None}
        cost = {s: 0.0}
        expanded = 0
        while openq and expanded < max_expand:
            _f, cur = heapq.heappop(openq)
            if cur == g:
                break
            expanded += 1
            cx, cy = cur
            for dx, dy in ((1, 0), (-1, 0), (0, 1), (0, -1)):
                nxt = (cx + dx, cy + dy)
                if nxt in came or not free(nxt):
                    continue
                nc = cost[cur] + 1.0
                came[nxt] = cur
                cost[nxt] = nc
                h = abs(nxt[0] - g[0]) + abs(nxt[1] - g[1])
                heapq.heappush(openq, (nc + h, nxt))
        if g not in came:
            return None
        path = []
        cur = g
        while cur is not None and cur != s:
            path.append(to_world(cur))
            cur = came[cur]
        path.reverse()
        # thin the path: keep a point every ~2 cells
        return path[1::2] if len(path) > 4 else path

    def connect_components_grid(self, stats):
        """Join leftover components with legal grid detours (around walls / through arches)."""
        joined = 0
        for _attempt in range(40):
            comps = self.components()
            if len(comps) <= 1:
                break
            comps.sort(key=len, reverse=True)
            main = comps[0]
            best = None
            for other in comps[1:]:
                for aid in other:
                    a = self.store.nodes[aid]
                    for bid in main:
                        b = self.store.nodes[bid]
                        d = math.hypot(a["x"] - b["x"], a["y"] - b["y"])
                        if best is None or d < best[0]:
                            best = (d, aid, bid)
            if best is None:
                break
            _d, aid, bid = best
            a, b = self.store.nodes[aid], self.store.nodes[bid]
            cells = self._astar((a["x"], a["y"]), (b["x"], b["y"]))
            if cells is None:
                stats["grid_detour_failed"] = stats.get("grid_detour_failed", 0) + 1
                break
            prev = aid
            for (cx, cy) in cells:
                nid, _new = self.store.add(cx, cy, 0.0, "detour", None, dedupe=1.6)
                if nid != prev:
                    self.link(prev, nid)
                    prev = nid
            self.link(prev, bid)
            joined += 1
        stats["grid_detour_edges"] = joined

    # -- output --------------------------------------------------------------
    def to_json(self, plan, stats, pois, anchors):
        nodes = []
        for n in self.store.nodes:
            nodes.append({
                "id": n["id"],
                "x": n["x"], "y": n["y"], "z": n["z"],
                "unity": {"x": n["x"], "y": n["z"], "z": -n["y"]},
                "kind": n["kind"],
                "name": n["name"],
            })
        edges = [{"a": a, "b": b, "dist": round(d, 3)} for (a, b), d in sorted(self.edges.items())]
        total = sum(e["dist"] for e in edges)
        doc = {
            "schema": "sbh.nav.v2",
            "generator": "tools/nav.py",
            "map": {"size_m": plan.get("base", [320, 240]),
                    "x": [-plan.get("base", [320, 240])[0] / 2, plan.get("base", [320, 240])[0] / 2],
                    "y": [-plan.get("base", [320, 240])[1] / 2, plan.get("base", [320, 240])[1] / 2]},
            "coordinate_contract": {
                "blender": "metres, Z-up, +Y north, +X east, ground z=0",
                "unity": "(x, y, z)unity = (x, z, -y)blender",
                "walkable_ground_z": 0.0,
            },
            "walkable_sources": {
                "paths": [p["id"] for p in plan["paths"]],
                "sidewalks": [s["id"] for s in plan["sidewalks"]],
                "zones": [z["id"] for z in plan["zones"] if z["kind"] in WALKABLE_ZONES],
                "arch_passages": [
                    {"block": bid, "x": a["ax"], "y": [a["y0"], a["y1"]],
                     "width_m": Obstacles.ARCH_HALF * 2}
                    for bid, a in self.obs.arches.items()],
                "excluded_water": [list(r) for r in self.obs.water],
                "terraces": [
                    {"id": t["id"], "rect": t["rect"], "steps": t.get("steps"),
                     "rise": t.get("rise"), "platform_from": t.get("platform_from"),
                     "top_z": float(t.get("steps", 0)) * float(t.get("rise", 0.0))}
                    for t in plan.get("terraces", [])],
                "walkable_z_m": 0.0 if not plan.get("terraces") else
                    float(plan["terraces"][0].get("steps", 0)) * float(plan["terraces"][0].get("rise", 0.0)),
                "resample": {"path_step_m": PATH_STEP, "sidewalk_step_m": SIDEWALK_STEP,
                             "zone_grid_m": ZONE_STEP, "link_radius_m": LINK_RADIUS},
            },
            "stats": {
                "nodes": len(nodes),
                "edges": len(edges),
                "total_edge_length_m": round(total, 1),
                "components": len(self.components()),
                "pois": len(pois),
                "route_anchors": len(anchors),
                **stats,
            },
            "bounds": {
                "x": [min(n["x"] for n in nodes), max(n["x"] for n in nodes)],
                "y": [min(n["y"] for n in nodes), max(n["y"] for n in nodes)],
            },
            "nodes": nodes,
            "edges": edges,
            "pois": pois,
            "route_anchors": [{"name": k, "node": v} for k, v in anchors.items()],
        }
        return doc


def check_routes(doc, verbose=False):
    """Dijkstra sanity checks over the serialised graph (used by --check / CI)."""
    import heapq
    adj = {}
    for e in doc["edges"]:
        adj.setdefault(e["a"], []).append((e["b"], e["dist"]))
        adj.setdefault(e["b"], []).append((e["a"], e["dist"]))
    nodes = {n["id"]: n for n in doc["nodes"]}
    poi = {p["id"]: p["node"] for p in doc["pois"]}
    anchor = {a["name"]: a["node"] for a in doc["route_anchors"]}

    def resolve(label):
        if label in poi:
            return poi[label]
        if label in anchor:
            return anchor[label]
        raise KeyError("unknown label %r" % label)

    def path(src, dst):
        prev = {src: None}
        best = {src: 0.0}
        pq = [(0.0, src)]
        seen = set()
        while pq:
            d, u = heapq.heappop(pq)
            if u in seen:
                continue
            seen.add(u)
            if u == dst:
                break
            for v, w in adj.get(u, ()):
                nd = d + w
                if nd < best.get(v, 1e18):
                    best[v] = nd
                    prev[v] = u
                    heapq.heappush(pq, (nd, v))
        if dst not in prev:
            return None
        route, cur = [], dst
        while cur is not None:
            route.append(cur)
            cur = prev[cur]
        route.reverse()
        return route, best[dst]

    checks = [
        ("A_AVE_C", "OBELISK"),
        ("BUS_STOP", "DK"),
        ("A_AVE_W", "A_WCOURT"),
        ("A_WCOURT", "A_MID_W"),          # must tunnel through W_K3 arch (x=-81)
        ("A_W2_COURT", "A_MID_W"),        # must tunnel through W2_K3 arch (x=-132)
        ("A_MID_C", "SCHOOL"),
        ("A_MID_W", "A_KG"),
        ("A_MID_E", "STADIUM"),
        ("A_AVE_E", "A_EDRIVE"),
        ("TRAM_STOP", "FLAGS"),
        ("A_MID_W", "A_NORTH"),
        ("A_AVE_C", "SHOP"),
        ("A_AVE_C", "KINO"),              # south civic strip
        ("A_AVE_C", "BUS_STOP_2"),
        ("A_PARK_W", "PAVILION"),         # north park / pond
        ("A_PARK_W", "A_PARK_E"),
        ("A_MARKET", "A_MID_E"),          # east market square
        ("A_BANYA", "A_MID_W"),           # west-2 banya
        # ---- Round-3 landmark districts -----------------------------------
        ("ESPLANADE", "ESPLANADE_TOP"),   # up the monument terrace steps
        ("A_AVE_C", "A_MOTHERLAND"),      # avenue -> monument top
        ("A_HWY", "A_UFO"),               # highway -> UFO bus station
        ("A_HWY", "A_HEX"),               # highway -> hexagonal bus station
        ("A_TERMINAL_W", "A_HEX"),        # terminal apron crossing
        ("A_MID_E", "A_VYSOTKA"),         # vysotka plaza
        ("A_VYSOTKA", "A_EAST_Q_MID"),    # vysotka -> east institute row
        ("A_EAST_Q_N", "A_EAST_Q_S"),     # east institute row N-S
        ("A_PLAZA_N", "A_METRO"),         # metro pavilion
        ("A_WEST_PARK", "A_MOTHERLAND"),  # west forest -> monument
        ("A_BREST", "A_MOTHERLAND"),      # relief wall -> monument top
    ]
    ok = 0
    for a, b in checks:
        src, dst = resolve(a), resolve(b)
        res = path(src, dst)
        if res is None:
            print("ROUTE_FAIL %-12s -> %-12s unreachable" % (a, b))
            continue
        route, length = res
        arch_nodes = [nodes[n]["name"] for n in route if nodes[n]["kind"] == "arch"]
        hilly = [nodes[n]["kind"] for n in route
                 if nodes[n]["kind"] in ("terrace", "terrace_platform")]
        tag = ""
        if arch_nodes:
            tag = "  [via %s]" % (arch_nodes[0] or "arch")
        if hilly:
            tag += "  [via %s]" % hilly[0]
        print("ROUTE %-12s -> %-12s nodes=%3d len=%6.1fm%s" % (a, b, len(route), length, tag))
        if verbose:
            print("      " + " > ".join("%s(%d)" % (nodes[n]["kind"], n) for n in route))
        ok += 1
    # every entrance / poi node must be attached
    attached = set()
    for e in doc["edges"]:
        attached.add(e["a"])
        attached.add(e["b"])
    dangling = [n for n in doc["nodes"] if n["kind"] in ("entrance", "poi", "anchor")
                and n["id"] not in attached]
    # long edges are a smell: report them
    long_edges = sorted(doc["edges"], key=lambda e: -e["dist"])[:5]
    print("CHECK routes_ok=%d/%d dangling_pois=%d components=%d" % (
        ok, len(checks), len(dangling), doc["stats"]["components"]))
    print("  longest edges:", ", ".join("%.1fm %s-%s" % (
        e["dist"], nodes[e["a"]]["kind"], nodes[e["b"]]["kind"]) for e in long_edges))
    if dangling:
        print("  DANGLING", [(n["id"], n["kind"], n["name"]) for n in dangling][:10])
    return ok == len(checks) and not dangling and doc["stats"]["components"] == 1


def write_csv(doc, node_path, edge_path):
    with open(node_path, "w", encoding="utf-8", newline="") as fh:
        fh.write("id,x_bl,y_bl,z_bl,x_unity,y_unity,z_unity,kind,name\n")
        for n in doc["nodes"]:
            fh.write("%d,%.3f,%.3f,%.3f,%.3f,%.3f,%.3f,%s,%s\n" % (
                n["id"], n["x"], n["y"], n["z"],
                n["unity"]["x"], n["unity"]["y"], n["unity"]["z"],
                n["kind"], (n["name"] or "")))
    with open(edge_path, "w", encoding="utf-8", newline="") as fh:
        fh.write("a,b,dist_m\n")
        for e in doc["edges"]:
            fh.write("%d,%d,%.3f\n" % (e["a"], e["b"], e["dist"]))


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--plan", default=os.path.join(ROOT, "tools", "plan.json"))
    ap.add_argument("--out", default=os.path.join(ROOT, "map"))
    ap.add_argument("--check", action="store_true", help="run BFS route checks after writing")
    args = ap.parse_args(argv)

    with open(args.plan, "r", encoding="utf-8") as fh:
        plan = json.load(fh)

    obs = Obstacles(plan)
    g = NavGraph(obs)
    stats = {k: 0 for k in ("path_nodes", "sidewalk_nodes", "zone_nodes", "arch_nodes",
                            "entrance_nodes", "poi_nodes", "anchor_nodes", "platform_nodes",
                            "terrace_nodes",
                            "fused_edges", "component_join_edges", "grid_detour_edges",
                            "road_crossing_edges", "anchor_edges_blocked")}

    g.build_paths(plan, stats)
    g.build_sidewalks(plan, stats)
    g.build_platform(plan, stats)
    g.build_zones(plan, stats)
    g.build_arches(plan, stats)
    g.build_terraces(plan, stats)
    g.build_entrances(plan, stats)
    pois = g.build_pois(plan, stats)
    anchors = g.build_anchors(plan, stats)
    g.fuse_proximity(stats)
    g.build_road_crossings(plan, stats)
    g.connect_components(stats)
    g.connect_components_grid(stats)
    # Round-3: monument stairs / plaza steps get real heights, then POIs mirror their node z
    g.apply_terrain_heights(plan)
    for p in pois:
        p["z"] = g.store.nodes[p["node"]]["z"]

    doc = g.to_json(plan, stats, pois, anchors)
    os.makedirs(args.out, exist_ok=True)
    json_path = os.path.join(args.out, "nav_graph.json")
    csv_path = os.path.join(args.out, "nav_graph.csv")
    edge_csv = os.path.join(args.out, "nav_graph_edges.csv")
    with open(json_path, "w", encoding="utf-8") as fh:
        json.dump(doc, fh, indent=1, ensure_ascii=False)
    write_csv(doc, csv_path, edge_csv)

    st = doc["stats"]
    print("NAV_GRAPH nodes=%d edges=%d total_len=%.1fm components=%d" % (
        st["nodes"], st["edges"], st["total_edge_length_m"], st["components"]))
    print("  sources: paths=%d sidewalks=%d platform=%d zones=%d arch=%d terraces=%d entrances=%d pois=%d anchors=%d" % (
        stats["path_nodes"], stats["sidewalk_nodes"], stats["platform_nodes"], stats["zone_nodes"],
        stats["arch_nodes"], stats["terrace_nodes"], stats["entrance_nodes"],
        stats["poi_nodes"], stats["anchor_nodes"]))
    print("  fusion edges=%d road-crossings=%d component-joins=%d grid-detours=%d anchor-edges-blocked=%d" % (
        stats["fused_edges"], stats["road_crossing_edges"], stats["component_join_edges"],
        stats["grid_detour_edges"], stats["anchor_edges_blocked"]))
    print("  POIs:", ", ".join(p["id"] for p in pois))
    print("  blocked anchor edges (wall-separated, routed around):",
          stats["anchor_edges_blocked_list"])
    print("WROTE", json_path)
    print("WROTE", csv_path)
    print("WROTE", edge_csv)
    if args.check:
        ok = check_routes(doc, verbose=True)
        print("CHECK", "PASS" if ok else "FAIL")
        return 0 if ok else 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
