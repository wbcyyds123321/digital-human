"""site.py — ground, roads, sidewalks, plaza, tram rails, stadium, monument.

Everything here is the Lead-owned district shell. Buildings come from modules.
Layering: soil 0.0 / zone surfaces 0.02 / roads 0.03 / markings 0.045 /
sidewalk+plaza+paths 0.14 (raised with kerbs) / steps etc.
"""
from __future__ import annotations

import math
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import bpy

import kit

# ----------------------------------------------------------------------------
# materials
# ----------------------------------------------------------------------------
_SITE_MATS: dict[str, bpy.types.Material] = {}


def m(name):
    return _SITE_MATS[name]


def init_materials():
    if _SITE_MATS:
        return _SITE_MATS
    _SITE_MATS["soil"] = kit.mat_texture("SURF_SOIL", "tex_dirt_grass.png", rough=0.97)
    _SITE_MATS["asphalt"] = kit.mat_texture("SURF_ASPHALT", "tex_asphalt_wet.png",
                                           rough=0.42, metallic=0.05)
    _SITE_MATS["concrete"] = kit.mat_texture("SURF_CONCRETE", "tex_concrete_panel.png",
                                            rough=0.8)
    _SITE_MATS["grass"] = kit.mat_texture("SURF_GRASS", "tex_dirt_grass.png", rough=0.95,
                                         tint="#59645A")
    _SITE_MATS["dirt"] = kit.mat_texture("SURF_DIRT", "tex_dirt_grass.png", rough=0.95,
                                        tint="#8A7F6A")
    _SITE_MATS["plaza"] = kit.mat_texture("SURF_PLAZA", "tex_concrete_panel.png",
                                         rough=0.7, tint="#9AA0A2")
    _SITE_MATS["court"] = kit.mat_texture("SURF_COURT", "tex_asphalt_wet.png", rough=0.55,
                                         tint="#B0B4B8")
    _SITE_MATS["paint"] = kit.mat_solid("SURF_PAINT", "#A9ADAE", rough=0.75)
    _SITE_MATS["paint_dim"] = kit.mat_solid("SURF_PAINT_DIM", "#7E8384", rough=0.85)
    _SITE_MATS["kerb"] = kit.mat_solid("SURF_KERB", "#8D908C", rough=0.9)
    _SITE_MATS["kerb_dark"] = kit.mat_solid("SURF_KERB_DARK", "#5F6363", rough=0.92)
    _SITE_MATS["rail"] = kit.mat_solid("SURF_RAIL", "#8A8E90", rough=0.35, metallic=0.8)
    _SITE_MATS["track"] = kit.mat_solid("SURF_TRACK", "#4A3F3C", rough=0.95)
    _SITE_MATS["field"] = kit.mat_solid("SURF_FIELD", "#3B4C3A", rough=0.95)
    _SITE_MATS["sand"] = kit.mat_solid("SURF_SAND", "#8E8878", rough=0.98)
    _SITE_MATS["granite"] = kit.mat_solid("SURF_GRANITE", "#6E7376", rough=0.55)
    _SITE_MATS["bronze"] = kit.mat_solid("SURF_BRONZE", "#4A4238", rough=0.55,
                                        metallic=0.5)
    _SITE_MATS["monument"] = kit.mat_texture("SURF_MONUMENT", "tex_concrete_panel.png",
                                            rough=0.6, tint="#9CA2A4")
    _SITE_MATS["water"] = kit.mat_solid("SURF_WATER", "#0A1620", rough=0.05, metallic=0.35,
                                       emission="#10202E", emission_strength=0.10)
    return _SITE_MATS


# ----------------------------------------------------------------------------
# helpers
# ----------------------------------------------------------------------------
def _rect(rect):
    return rect


def flat(parent, name, rect, z, mat, tile=8.0):
    return kit.ground_quad(parent, name, rect, z, mat, tile=tile)


def _road_edges(rect):
    x0, y0, x1, y1 = rect
    w, h = x1 - x0, y1 - y0
    if w >= h:  # horizontal road: edges along y0 and y1
        return [((x0, y0), (x1, y0)), ((x0, y1), (x1, y1))]
    return [((x0, y0), (x0, y1)), ((x1, y0), (x1, y1))]


def kerbs(parent, name, rect, skip=(), height=0.15, width=0.26):
    """Add kerb stones along both long edges of a rect, skipping intersections."""
    x0, y0, x1, y1 = rect
    for i, (p0, p1) in enumerate(_road_edges(rect)):
        axis_x = abs(p1[0] - p0[0]) > abs(p1[1] - p0[1])
        length = (p1[0] - p0[0]) if axis_x else (p1[1] - p0[1])
        # build segments between skip intervals
        segs = [(0.0, 1.0)]
        for (s0, s1) in skip:
            new = []
            for (a, b) in segs:
                if s1 <= a or s0 >= b:
                    new.append((a, b))
                    continue
                if s0 > a:
                    new.append((a, s0))
                if s1 < b:
                    new.append((s1, b))
            segs = new
        for (a, b) in segs:
            if b - a < 0.2:
                continue
            if axis_x:
                ax0 = p0[0] + length * a
                ax1 = p0[0] + length * b
                kit.box_mm(parent, f"{name}_k{i}_{a:.1f}", ax0, p0[1] - width / 2,
                          0.02, ax1, p0[1] + width / 2, height, m("kerb"))
            else:
                ay0 = p0[1] + length * a
                ay1 = p0[1] + length * b
                kit.box_mm(parent, f"{name}_k{i}_{a:.1f}", p0[0] - width / 2, ay0,
                           0.02, p0[0] + width / 2, ay1, height, m("kerb"))


def raised_quad(parent, name, rect, z, mat, tile=4.0, depth=0.14):
    """A raised slab (sidewalk / plaza / path) with visible edge."""
    x0, y0, x1, y1 = rect
    kit.box_mm(parent, name + "_slab", x0, y0, 0.02, x1, y1, z, m("kerb_dark"))
    kit.ground_quad(parent, name, (x0 + 0.05, y0 + 0.05, x1 - 0.05, y1 - 0.05),
                    z + 0.005, mat, tile=tile)


def paint_dash(parent, name, p0, p1, width, mat=None, z=0.045):
    mat = mat or m("paint")
    x0, y0 = p0
    x1, y1 = p1
    if abs(x1 - x0) >= abs(y1 - y0):
        kit.box(parent, name, ((x0 + x1) / 2, (y0 + y1) / 2, z),
                (abs(x1 - x0), width, 0.008), mat)
    else:
        kit.box(parent, name, ((x0 + x1) / 2, (y0 + y1) / 2, z),
                (width, abs(y1 - y0), 0.008), mat)


# ----------------------------------------------------------------------------
# ground, zones
# ----------------------------------------------------------------------------
def build_base(parent, plan):
    b = plan["base"]
    half_x, half_y = b[0] / 2 + 320, b[1] / 2 + 260
    flat(parent, "SITE_soil", (-half_x, -half_y, half_x, half_y), 0.0, m("soil"), tile=10.0)


def build_zones(parent, plan):
    zmap = {
        "plaza": (m("plaza"), 2.4, 0.145), "terrace": (m("plaza"), 2.4, 0.145),
        "steps": (m("granite"), 2.0, 0.15), "court": (m("court"), 12.0, 0.02),
        "parking": (m("court"), 12.0, 0.02), "lawn": (m("grass"), 5.0, 0.02),
        "dirt": (m("dirt"), 5.0, 0.025), "playground": (m("sand"), 6.0, 0.03),
        "sport": (m("court"), 10.0, 0.02), "stadium": (m("track"), 10.0, 0.02),
    }
    for z in plan["zones"]:
        kind = z["kind"]
        mat, tile, zz = zmap.get(kind, (m("grass"), 5.0, 0.02))
        if kind == "water":
            build_pond(parent, z)
        elif kind in ("lawn", "dirt", "playground"):
            flat(parent, f"ZONE_{z['id']}", z["rect"], zz, mat, tile=tile)
        elif kind == "stadium":
            flat(parent, f"ZONE_{z['id']}", z["rect"], zz, m("track"), tile=tile)
        elif kind in ("plaza", "terrace"):
            raised_quad(parent, f"ZONE_{z['id']}", z["rect"], zz, mat, tile=tile)
        else:
            flat(parent, f"ZONE_{z['id']}", z["rect"], zz, mat, tile=tile)


def build_roads(parent, plan):
    for r in plan["roads"]:
        flat(parent, f"ROAD_{r['id']}", r["rect"], 0.03, m("asphalt"), tile=9.0)
    # markings on the avenue
    ave = next(r for r in plan["roads"] if r["id"] == "AVENUE")
    x0, y0, x1, y1 = ave["rect"]
    ymid = (y0 + y1) / 2
    for x in range(int(x0) + 3, int(x1) - 3, 8):
        paint_dash(parent, f"AVE_dash_{x}", (x, ymid), (x + 4, ymid), 0.16)
    # lane divisions
    for yy in (ymid - 3.4, ymid + 3.4):
        if y0 + 1 < yy < y1 - 1:
            pass
    # side street markings
    for rid in ("NORTH_ROAD", "MID_DRIVE", "WEST_DRIVE", "EAST_DRIVE", "SOUTH_HWY",
                "EAST3_DRIVE", "WEST_RING", "WEST2_DRIVE", "EAST2_DRIVE",
                "T_SPUR_W", "T_SPUR_E"):
        r = next((rr for rr in plan["roads"] if rr["id"] == rid), None)
        if not r:
            continue
        x0, y0, x1, y1 = r["rect"]
        if x1 - x0 >= y1 - y0:
            ym = (y0 + y1) / 2
            for x in range(int(x0) + 2, int(x1) - 2, 9):
                paint_dash(parent, f"{rid}_dash_{x}", (x, ym), (x + 3.2, ym), 0.12,
                           m("paint_dim"))
        else:
            xm = (x0 + x1) / 2
            for y in range(int(y0) + 2, int(y1) - 2, 9):
                paint_dash(parent, f"{rid}_dash_{y}", (xm, y), (xm, y + 3.2), 0.12,
                           m("paint_dim"))
    # crosswalks across the avenue (whole length)
    for cx in range(int(x0) + 18, int(x1) - 12, 56):
        for k in range(9):
            yy = -86.6 + k * 1.45
            kit.box(parent, f"XWALK_{cx}_{k}", (cx, yy, 0.05), (3.4, 0.72, 0.01),
                    m("paint_dim"))
    # crosswalks over the south highway (terminal spurs + centre)
    for cx in (-77, 0, 77, -160, 160):
        for k in range(5):
            yy = -141.4 + k * 2.1
            kit.box(parent, f"XWALK_HWY_{cx}_{k}", (cx, yy, 0.05), (3.2, 0.9, 0.01),
                    m("paint_dim"))
    # crosswalks over the east3 / west ring at the north road
    for cx in (167, -173):
        for k in range(5):
            yy = 80.4 + k * 1.5
            kit.box(parent, f"XWALK_RING_{cx}_{k}", (cx, yy, 0.05), (3.2, 0.7, 0.01),
                    m("paint_dim"))
    # crosswalks over the north road (park entrances) + centre
    for cx in (-125, 0, 121):
        for k in range(5):
            yy = 81.4 + k * 1.5
            kit.box(parent, f"XWALK_N_{cx}_{k}", (cx, yy, 0.05), (3.2, 0.7, 0.01),
                    m("paint_dim"))
    # crosswalks over the southern service road
    for cx in (-71, 0, 71):
        for k in range(4):
            yy = -110.8 + k * 1.4
            kit.box(parent, f"XWALK_S_{cx}_{k}", (cx, yy, 0.05), (3.2, 0.7, 0.01),
                    m("paint_dim"))
    # crosswalks over the two drives / mid drive (small)
    for cid, cx in (("WEST_DRIVE", -57), ("EAST_DRIVE", 57)):
        for k in range(4):
            yy = -62 + k * 1.5
            kit.box(parent, f"XWALK_{cid}_{k}", (cx, yy, 0.05), (3.6, 0.7, 0.01),
                    m("paint_dim"))
    for k in range(5):
        xx = -34 + k * 1.5
        kit.box(parent, f"XWALK_MID_{k}", (xx, -1, 0.05), (0.7, 3.2, 0.01),
                m("paint_dim"))
    # parking stall lines
    for pz in plan["zones"]:
        if pz["kind"] != "parking":
            continue
        x0, y0, x1, y1 = pz["rect"]
        n = max(2, int((x1 - x0) / 3.0))
        for i in range(n + 1):
            x = x0 + (x1 - x0) * i / n
            kit.box(parent, f"STALL_{pz['id']}_{i}", (x, (y0 + y1) / 2, 0.045),
                    (0.1, y1 - y0 - 0.2, 0.008), m("paint_dim"))


def build_sidewalks(parent, plan):
    for sw in plan["sidewalks"]:
        raised_quad(parent, f"WALK_{sw['id']}", sw["rect"], 0.145, m("concrete"),
                    tile=6.0)
    # kerbs around roads, skipping driveway mouths
    for r in plan["roads"]:
        rid = r["id"]
        skip = []
        x0, y0, x1, y1 = r["rect"]
        if rid == "AVENUE":
            for cx in range(int(x0) + 18, int(x1) - 12, 56):
                skip.append(((cx - 3.0 - x0) / (x1 - x0), (cx + 3.0 - x0) / (x1 - x0)))
        elif rid in ("WEST_DRIVE", "EAST_DRIVE"):
            for cy in (-62, -40, -18):
                skip.append(((cy - 2.5 - y0) / (y1 - y0), (cy + 2.5 - y0) / (y1 - y0)))
        kerbs(parent, f"KERB_{rid}", r["rect"], skip=skip)


def build_paths(parent, plan):
    for p in plan["paths"]:
        pts = p["pts"]
        w = p["width"]
        for i in range(len(pts) - 1):
            (x0, y0), (x1, y1) = pts[i], pts[i + 1]
            if abs(x1 - x0) >= abs(y1 - y0):
                rect = (min(x0, x1) - w / 2, y0 - w / 2, max(x0, x1) + w / 2, y0 + w / 2)
            else:
                rect = (x0 - w / 2, min(y0, y1) - w / 2, x0 + w / 2, max(y0, y1) + w / 2)
            raised_quad(parent, f"PATH_{p['id']}_{i}", rect, 0.14, m("concrete"),
                        tile=5.0, depth=0.1)


def build_tram(parent, plan):
    tram = plan["tram"]
    rail_len = plan["base"][0] - 4
    for ty in tram["track_y"]:
        for off in (-0.762, 0.762):
            kit.box(parent, f"RAIL_{ty}_{off:.2f}", (0, ty + off, 0.045),
                    (rail_len, 0.085, 0.012), m("rail"))
        # dark groove
        kit.box(parent, f"RAIL_GROOVE_{ty}", (0, ty, 0.038), (rail_len, 1.85, 0.008),
                m("kerb_dark"))
    # island platform
    p = tram["stop_platform"]
    raised_quad(parent, "TRAM_PLATFORM", p, 0.22, m("concrete"), tile=2.0, depth=0.2)
    x0, y0, x1, y1 = p
    # low shelter: posts + roof + bench
    for px in (x0 + 2.0, x1 - 2.0):
        for py in (y0 + 0.4, y1 - 0.4):
            kit.box(parent, f"TRAMSHELTER_post_{px}_{py}", (px, py, 1.2),
                    (0.08, 0.08, 2.4), m("kerb_dark"))
    kit.box(parent, "TRAMSHELTER_roof", ((x0 + x1) / 2, (y0 + y1) / 2, 2.55),
            (x1 - x0 - 2.0, 2.2, 0.12), m("kerb"))
    for i in range(3):
        kit.box(parent, f"TRAMSHELTER_bench{i}", ((x0 + x1) / 2, (y0 + y1) / 2 - 0.45 + i * 0.25, 0.62),
                (4.0, 0.08, 0.05), m("kerb_dark"))
    # timetable board
    kit.box(parent, "TRAM_timetable", (x0 + 1.2, (y0 + y1) / 2, 1.35),
            (0.9, 0.06, 1.1), m("kerb_dark"))


# ----------------------------------------------------------------------------
# plaza + monument + flagpoles + fountain
# ----------------------------------------------------------------------------
def build_plaza(parent, plan):
    plaza = next(z for z in plan["zones"] if z["id"] == "PLAZA")
    x0, y0, x1, y1 = plaza["rect"]
    # paving joints (grid)
    for gx in range(int(x0), int(x1) + 1, 5):
        kit.box(parent, f"PLAZA_jx_{gx}", (gx, (y0 + y1) / 2, 0.152),
                (0.04, y1 - y0, 0.006), m("kerb_dark"))
    for gy in range(int(y0), int(y1) + 1, 5):
        kit.box(parent, f"PLAZA_jy_{gy}", ((x0 + x1) / 2, gy, 0.152),
                (x1 - x0, 0.04, 0.006), m("kerb_dark"))
    # central band toward the monument
    kit.box(parent, "PLAZA_band", (0, (y0 + y1) / 2, 0.153), (7.0, y1 - y0, 0.008),
            m("granite"))
    # steps up to the House of Culture
    steps = next((z for z in plan["zones"] if z["id"] == "PLAZA_STEPS"), None)
    if steps:
        sx0, sy0, sx1, sy1 = steps["rect"]
        kit.box_mm(parent, "PLAZA_steps_top", sx0, sy0, 0.02, sx1, sy1, 0.15,
                   m("granite"))
    # monument
    build_monument(parent, plan)
    # fountain
    build_fountain(parent, plan)
    # flagpoles
    build_flags(parent, plan)


def build_monument(parent, plan):
    lm = next(l for l in plan["landmarks"] if l["type"] == "obelisk")
    cx, cy = lm["pos"]
    mat = m("monument")
    # 3-step base
    for i, (sz, h, z) in enumerate(((9.0, 0.35, 0.15), (7.2, 0.35, 0.5), (5.6, 0.4, 0.85))):
        kit.box(parent, f"MON_base{i}", (cx, cy, z + h / 2), (sz, sz, h), mat)
    kit.box(parent, "MON_plinth", (cx, cy, 1.25), (3.0, 3.0, 0.8), m("granite"))
    # tapered obelisk (4-sided) built from stacked boxes
    h_total = lm.get("height", 18.0)
    z = 1.65
    seg = 14
    for i in range(seg):
        t = i / seg
        w = 1.7 * (1 - 0.55 * t)
        hh = h_total / seg
        kit.box(parent, f"MON_shaft{i}", (cx, cy, z + hh / 2), (w, w, hh + 0.02), mat)
        z += hh
    # red star on top (emissive)
    star_mat = kit.mat_solid("MON_STAR", "#7E2A22", rough=0.45,
                             emission="#D8462E", emission_strength=1.4)
    kit.cyl(parent, "MON_star", (cx, cy, z + 0.45), 0.55, 0.22, star_mat, verts=5,
            rot=(0, 0, math.radians(36)))
    # bronze plaque on plinth front
    plaque = kit.mat_solid("MON_PLAQUE", "#5A4C38", rough=0.5, metallic=0.6)
    kit.box(parent, "MON_plaque", (cx, cy - 1.52, 1.2), (2.4, 0.06, 0.9), plaque)
    kit.text3d(parent, "MON_text", "1941 — 1945",
               (cx, cy - 1.56, 1.2), 0.26,
               kit.mat_solid("MON_TEXT", "#C9B68A", rough=0.5,
                             emission="#C9B68A", emission_strength=0.5),
               rot=(math.radians(90), 0, 0))
    # low granite wall behind + eternal flame bowl
    wall = m("granite")
    kit.box(parent, "MON_wallL", (cx - 4.6, cy + 1.0, 0.6), (3.2, 0.5, 0.9), wall)
    kit.box(parent, "MON_wallR", (cx + 4.6, cy + 1.0, 0.6), (3.2, 0.5, 0.9), wall)
    kit.cyl(parent, "MON_bowl", (cx, cy - 3.6, 0.55), 0.7, 0.3, m("bronze"), verts=12)
    flame = kit.mat_solid("MON_FLAME", "#FFB066", rough=0.3,
                          emission="#FF8A3C", emission_strength=4.5)
    kit.cyl(parent, "MON_flame", (cx, cy - 3.6, 0.78), 0.09, 0.22, flame, verts=8)


def build_fountain(parent, plan):
    lm = next(l for l in plan["landmarks"] if l["type"] == "fountain_dry")
    cx, cy = lm["pos"]
    r = lm.get("radius", 5.0)
    ring = m("granite")
    n = 20
    for i in range(n):
        a0 = math.radians(360 * i / n)
        a1 = math.radians(360 * (i + 1) / n)
        x0, y0 = cx + math.cos(a0) * r, cy + math.sin(a0) * r
        x1, y1 = cx + math.cos(a1) * r, cy + math.sin(a1) * r
        kit.cyl_between(parent, f"FNT_rim{i}", (x0, y0, 0.55), (x1, y1, 0.55), 0.32,
                        ring, verts=6)
    kit.cyl(parent, "FNT_floor", (cx, cy, 0.06), r - 0.2, 0.04,
            kit.mat_solid("FNT_FLOOR", "#2A2E30", rough=0.35, metallic=0.15), verts=28)
    kit.cyl(parent, "FNT_center", (cx, cy, 0.5), 0.7, 0.9, ring, verts=12)


def build_flags(parent, plan):
    lm = next(l for l in plan["landmarks"] if l["type"] == "flagpoles")
    cx, cy = lm["pos"]
    count = lm.get("count", 3)
    metal = kit.mat_solid("FLAG_POLE", "#7E8486", rough=0.4, metallic=0.7)
    flag_mat = kit.mat_solid("FLAG_RED", "#6E2420", rough=0.9)
    for i in range(count):
        x = cx + (i - (count - 1) / 2) * 3.0
        kit.cyl(parent, f"FLAG_pole{i}", (x, cy, 6.5), 0.09, 13.0, metal, verts=8)
        kit.cyl(parent, f"FLAG_base{i}", (x, cy, 0.25), 0.35, 0.5, m("granite"), verts=10)
        q = kit.quad(parent, f"FLAG_cloth{i}", (x + 0.95, cy, 11.4),
                     (1, 0, 0), (0, 0, 1), 1.7, 1.05, flag_mat)


# ----------------------------------------------------------------------------
# stadium + school yard
# ----------------------------------------------------------------------------
def build_stadium(parent, plan):
    st = next(z for z in plan["zones"] if z["id"] == "STADIUM")
    x0, y0, x1, y1 = st["rect"]
    # track ring
    flat(parent, "STAD_track", (x0, y0, x1, y1), 0.028, m("track"), tile=8.0)
    fx0, fy0, fx1, fy1 = x0 + 5.5, y0 + 3.5, x1 - 5.5, y1 - 5.5
    flat(parent, "STAD_field", (fx0, fy0, fx1, fy1), 0.032, m("field"), tile=8.0)
    # track lanes
    for k in range(1, 4):
        inset = k * 1.6
        for r in ((x0 + inset, y0 + 3.0, x1 - inset, y0 + 3.1),
                  (x0 + inset, y1 - 3.1, x1 - inset, y1 - 3.0)):
            kit.box(parent, f"STAD_lane_{k}_{r[1]:.0f}",
                    ((r[0] + r[2]) / 2, (r[1] + r[3]) / 2, 0.036),
                    (r[2] - r[0], 0.1, 0.006), m("paint_dim"))
    # field line markings
    mid_x, mid_y = (fx0 + fx1) / 2, (fy0 + fy1) / 2
    kit.box(parent, "STAD_center", (mid_x, mid_y, 0.04), (fx1 - fx0 - 1, 0.1, 0.006),
            m("paint_dim"))
    kit.box(parent, "STAD_halfline", (mid_x, mid_y, 0.04), (0.1, fy1 - fy0 - 1, 0.006),
            m("paint_dim"))
    # goals
    for gy, sgn in ((fy0 + 0.4, 1), (fy1 - 0.4, -1)):
        white = kit.mat_solid("GOAL_WHITE", "#C8CCCC", rough=0.6)
        for sx in (-2.2, 2.2):
            kit.box(parent, f"GOAL_post{gy}_{sx}", (mid_x + sx, gy, 1.1),
                    (0.1, 0.1, 2.2), white)
        kit.box(parent, f"GOAL_bar{gy}", (mid_x, gy, 2.2), (4.5, 0.1, 0.1), white)


def build_school_yard(parent, plan):
    z = next((zz for zz in plan["zones"] if zz["id"] == "SCHOOL_YARD"), None)
    if not z:
        return
    x0, y0, x1, y1 = z["rect"]
    # basketball / sports court markings
    cx, cy = (x0 + x1) / 2, (y0 + y1) / 2
    kit.box(parent, "SCHOOL_court_mid", (cx, cy, 0.035), (x1 - x0 - 4, 0.1, 0.006),
            m("paint_dim"))
    kit.box(parent, "SCHOOL_court_edgeN", (cx, y1 - 2, 0.035), (x1 - x0 - 4, 0.1, 0.006),
            m("paint_dim"))
    kit.box(parent, "SCHOOL_court_edgeS", (cx, y0 + 2, 0.035), (x1 - x0 - 4, 0.1, 0.006),
            m("paint_dim"))


# ----------------------------------------------------------------------------
# build all
# ----------------------------------------------------------------------------
def build_pond(parent, zone):
    """Park pond; rectangular reflecting pools when zone['ellipse'] is False."""
    import math
    x0, y0, x1, y1 = zone["rect"]
    if zone.get("ellipse") is False:
        w, d = x1 - x0, y1 - y0
        cx, cy = (x0 + x1) / 2, (y0 + y1) / 2
        kit.box(parent, "POOL_BASIN", (cx, cy, 0.02), (w, d, 0.04), m("dirt"))
        kit.box(parent, "POOL_WATER", (cx, cy, 0.055), (w - 0.6, d - 0.6, 0.012), m("water"))
        rim_h = 0.36
        kit.box(parent, "POOL_RIM_N", (cx, y1 - 0.15, rim_h / 2), (w, 0.42, rim_h), m("granite"))
        kit.box(parent, "POOL_RIM_S", (cx, y0 + 0.15, rim_h / 2), (w, 0.42, rim_h), m("granite"))
        kit.box(parent, "POOL_RIM_W", (x0 + 0.15, cy, rim_h / 2), (0.42, d, rim_h), m("granite"))
        kit.box(parent, "POOL_RIM_E", (x1 - 0.15, cy, rim_h / 2), (0.42, d, rim_h), m("granite"))
        return
    cx, cy = (x0 + x1) / 2, (y0 + y1) / 2
    rx, ry = (x1 - x0) / 2, (y1 - y0) / 2
    seg = 40
    shore = [(cx + math.cos(2 * math.pi * i / seg) * (rx + 2.4),
              cy + math.sin(2 * math.pi * i / seg) * (ry + 2.4)) for i in range(seg)]
    edge = [(cx + math.cos(2 * math.pi * i / seg) * (rx + 0.45),
             cy + math.sin(2 * math.pi * i / seg) * (ry + 0.45)) for i in range(seg)]
    verts = [(p[0], p[1], 0.025) for p in shore] + [(p[0], p[1], 0.03) for p in edge]
    faces = []
    for i in range(seg):
        j = (i + 1) % seg
        faces.append((i, j, seg + j, seg + i))
    kit._mesh_obj("POND_SHORE", verts, faces, m("dirt"), parent)
    wverts = [(cx + math.cos(2 * math.pi * i / seg) * rx,
               cy + math.sin(2 * math.pi * i / seg) * ry, 0.06) for i in range(seg)]
    kit._mesh_obj("POND_WATER", wverts, [tuple(range(seg))], m("water"), parent)
    for i in range(0, seg, 2):
        a0 = 2 * math.pi * i / seg
        a1 = 2 * math.pi * (i + 2) / seg
        p0 = (cx + math.cos(a0) * (rx + 0.25), cy + math.sin(a0) * (ry + 0.25), 0.18)
        p1 = (cx + math.cos(a1) * (rx + 0.25), cy + math.sin(a1) * (ry + 0.25), 0.18)
        kit.cyl_between(parent, f"POND_RIM_{i}", p0, p1, 0.22, m("concrete"), verts=6)


def build_terraces(parent, plan):
    """Stepped monumental esplanade: N strips rising along +Y, then a raised platform."""
    import math
    for t in plan.get("terraces", []):
        x0, y0, x1, y1 = t["rect"]
        n = max(1, int(t.get("steps", 6)))
        rise = float(t.get("rise", 0.45))
        y_step = float(t.get("step_from", y0))
        y_plat = float(t.get("platform_from", y1))
        w = x1 - x0
        depth = (y_plat - y_step) / n
        for i in range(n):
            z0 = i * rise
            yy = y_step + depth * (i + 0.5)
            kit.box(parent, f"TERR_{t['id']}_{i}", ((x0 + x1) / 2, yy, z0 + rise / 2),
                    (w, depth + 0.02, rise), m("granite"))
        # platform block
        if y_plat < y1:
            h = n * rise
            kit.box(parent, f"TERR_{t['id']}_PLATFORM", ((x0 + x1) / 2, (y_plat + y1) / 2, h / 2),
                    (w, y1 - y_plat, h), m("granite"))
            # shoulder retaining walls
            for sx in (x0 - 0.6, x1 + 0.6):
                kit.box(parent, f"TERR_{t['id']}_WALL_{sx:+.0f}", (sx, (y_step + y1) / 2, h / 2),
                        (1.2, y1 - y_step, h + 0.3), m("concrete"))
        # flanking ramps at both ends
        for sx in (x0 - 2.2, x1 + 2.2):
            for i in range(n):
                z0 = i * rise
                yy = y_step + depth * (i + 0.5)
                kit.box(parent, f"TERR_{t['id']}_RAMP_{sx:+.0f}_{i}", (sx, yy, z0 + rise / 2),
                        (3.4, depth + 0.02, rise), m("concrete"))


def build(parent, plan, rng):
    init_materials()
    root = kit.group("SITE", parent=parent)
    build_base(root, plan)
    build_zones(root, plan)
    build_roads(root, plan)
    build_sidewalks(root, plan)
    build_paths(root, plan)
    build_tram(root, plan)
    build_plaza(root, plan)
    build_stadium(root, plan)
    build_school_yard(root, plan)
    stats = {
        "roads": len(plan["roads"]),
        "sidewalks": len(plan["sidewalks"]),
        "zones": len(plan["zones"]),
        "paths": len(plan["paths"]),
    }
    print("SITE", stats)
    return stats

