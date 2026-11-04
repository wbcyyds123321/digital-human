"""mod_landmark_monument.py — round-3 landmark typologies.

TYPES:
  statue_motherland  colossal concrete female figure with raised sword on a
                     stepped pedestal/mound (silhouette priority)
  relief_wall        Brest-Fortress-style granite relief wall with recessed
                     panels, abstract figures, bronze wreath + date band
  metro_pavilion     Moscow-metro pylon-hall surface vestibule (marble pylons,
                     coffered ceiling, chandeliers, mosaic, bronze doors, М sign)
  institute_sino     1950s Sino-Soviet institute: U/H massing, central tower with
                     hipped roof + spire/flag, colonnade portico, stone trim
  museum_colonnade   Beijing-military-museum-style hall: giant square-column
                     colonnade, deep entablature, tower with red star finial

All builders follow the shared contract: build(parent, block, rng, ctx) -> stats,
geometry parented to the block empty, object names prefixed with block["id"],
deterministic through the passed kits.RNG.
"""
from __future__ import annotations

import math

import bpy
from mathutils import Vector

import kit

# ============================================================================
# materials
# ============================================================================
_MCACHE: dict[str, object] = {}


def _solid(name, color, rough=0.88, metallic=0.0, emission=None, es=0.0):
    return kit.mat_solid(name, color, rough=rough, metallic=metallic,
                         emission=emission, emission_strength=es)


def _rgba_sockets(mix):
    fac = next(s for s in mix.inputs if s.name == "Factor" and s.type == "VALUE")
    a = next(s for s in mix.inputs if s.name == "A" and s.type == "RGBA")
    b = next(s for s in mix.inputs if s.name == "B" and s.type == "RGBA")
    out = next(s for s in mix.outputs if s.name == "Result" and s.type == "RGBA")
    return fac, a, b, out


def _tri_wall(name, filename, tint=None, tile=4.5, rough=0.9, tint_fac=0.72):
    """World-space triplanar (XZ / YZ) wall material from a GPT texture."""
    if name in _MCACHE:
        return _MCACHE[name]
    path = kit.find_image(filename)
    if not path:
        mat = kit.mat_solid(name + "_FB", tint or "#9C9C96", rough=rough)
        _MCACHE[name] = mat
        return mat
    img = bpy.data.images.load(path, check_existing=True)
    try:
        img.colorspace_settings.name = "sRGB"
    except Exception:  # noqa: BLE001
        pass
    mat = bpy.data.materials.new(name)
    mat.use_nodes = True
    nt = mat.node_tree
    nodes, links = nt.nodes, nt.links
    bsdf = nodes.get("Principled BSDF")
    texco = nodes.new("ShaderNodeTexCoord")
    sep = nodes.new("ShaderNodeSeparateXYZ")
    links.new(texco.outputs["Object"], sep.inputs["Vector"])

    def proj(axis_pair):
        comb = nodes.new("ShaderNodeCombineXYZ")
        links.new(sep.outputs[axis_pair[0]], comb.inputs["X"])
        links.new(sep.outputs[axis_pair[1]], comb.inputs["Y"])
        mp = nodes.new("ShaderNodeMapping")
        mp.inputs["Scale"].default_value = (1.0 / tile, 1.0 / tile, 1.0 / tile)
        links.new(comb.outputs["Vector"], mp.inputs["Vector"])
        tex = nodes.new("ShaderNodeTexImage")
        tex.image = img
        tex.interpolation = "Linear"
        tex.extension = "REPEAT"
        links.new(mp.outputs["Vector"], tex.inputs["Vector"])
        return tex

    tex_xz = proj(("X", "Z"))
    tex_yz = proj(("Y", "Z"))
    geo = nodes.new("ShaderNodeNewGeometry")
    vabs = nodes.new("ShaderNodeVectorMath")
    vabs.operation = "ABSOLUTE"
    links.new(geo.outputs["Normal"], vabs.inputs[0])
    sepn = nodes.new("ShaderNodeSeparateXYZ")
    links.new(vabs.outputs["Vector"], sepn.inputs["Vector"])
    mixp = nodes.new("ShaderNodeMix")
    mixp.data_type = "RGBA"
    mixp.blend_type = "MIX"
    fac, a, b, out = _rgba_sockets(mixp)
    links.new(sepn.outputs["X"], fac)
    links.new(tex_xz.outputs["Color"], a)
    links.new(tex_yz.outputs["Color"], b)
    col_out = out
    if tint is not None:
        mixt = nodes.new("ShaderNodeMix")
        mixt.data_type = "RGBA"
        mixt.blend_type = "MULTIPLY"
        ft, at, bt, ot = _rgba_sockets(mixt)
        ft.default_value = tint_fac
        at.default_value = kit.srgb(tint)
        links.new(col_out, bt)
        col_out = ot
    links.new(col_out, bsdf.inputs["Base Color"])
    bsdf.inputs["Roughness"].default_value = rough
    _MCACHE[name] = mat
    return mat


def _sync_mat_caches():
    """kit.reset_scene() replaces all bpy data, but Python caches (kit.MATS and
    ours) keep dead datablock pointers — drop them when the file is empty."""
    try:
        import bpy as _bpy
    except Exception:  # noqa: BLE001
        return
    if len(_bpy.data.materials) == 0:
        try:
            kit.MATS.clear()
        except Exception:  # noqa: BLE001
            pass
        try:
            kit._WM = None
        except Exception:  # noqa: BLE001
            pass
        _MCACHE.clear()
        _LMM_CACHE.clear()


def _wm(ctx):
    """Window material dict that survives kit.reset_scene() (stale _WM cache)."""
    wm = (ctx or {}).get("wmats")
    try:
        if wm and wm.get("frame") is not None and wm["frame"].name in bpy.data.materials:
            return wm
    except ReferenceError:
        pass
    try:
        kit._WM = None
    except Exception:  # noqa: BLE001
        pass
    return kit.win_materials()


def _mats():
    _sync_mat_caches()
    if "lm" in _MCACHE:
        _c = _MCACHE["lm"]
        try:
            if _c["limestone"].name in bpy.data.materials:
                return _c
        except ReferenceError:
            pass
        _MCACHE.pop("lm", None)
    m = {
        # Round-4 LMM landmark materials (clean, close to reference photos)
        "limestone": _lmm("limestone"),
        "limestone_light": _lmm("limestone_light"),
        "stone_rusticated": _lmm("stone_rusticated"),
        "stucco_ochre": _lmm("stucco_ochre"),
        "stucco_cream": _lmm("stucco_cream"),
        "stucco_white": _lmm("stucco_white"),
        "tile_ceramic": _lmm("tile_ceramic"),
        "marble_white": _lmm("marble_white"),
        "marble_red": _lmm("marble_red"),
        "granite_grey": _lmm("granite_grey"),
        "granite_dark": _lmm("granite_dark"),
        "granite_polished": _lmm("granite_polished"),
        "relief_stone": _lmm("relief_stone"),
        "bas_relief_bronze": _lmm("bas_relief_bronze"),
        "concrete_white": _lmm("concrete_white"),
        "concrete_grey": _lmm("concrete_grey"),
        "concrete_dark": _lmm("concrete_dark"),
        "metal_fin": _lmm("metal_fin"),
        "glass_clean": _lmm("glass_clean"),
        "glass_ribbon": _lmm("glass_ribbon"),
        "bronze": _lmm("bronze"),
        "gold": _lmm("gold"),
        "copper_roof": _lmm("copper_roof"),
        "iron_dark": _lmm("iron_dark"),
        "mosaic_blue": _lmm("mosaic_blue"),
        "mosaic_gold": _lmm("mosaic_gold"),
        "mosaic_red": _lmm("mosaic_red"),
        # custom accents
        "red_star": _solid("LM_RED_STAR", "#8E2A24", rough=0.5, metallic=0.25,
                           emission="#8E2A24", es=0.35),
        "roof": _lmm("copper_roof"),
        "roof_dark": _solid("LM_ROOF_DARK", "#3A3E40", rough=0.85),
        "plinth": _lmm("granite_dark"),
        "plinth_dark": _solid("LM_PLINTH", "#3A3D40", rough=0.9),
        "stone": _lmm("limestone"),
        "stone_dark": _lmm("granite_grey"),
        "marble": _lmm("marble_white"),
        "marble_dark": _lmm("granite_grey"),
        "bronze_dark": _lmm("iron_dark"),
        "bronze_patina": _lmm("bas_relief_bronze"),
        "steel": _lmm("metal_fin"),
        "dark_metal": _lmm("iron_dark"),
        "red_cloth": _solid("LM_RED_CLOTH", "#7C2A26", rough=0.9),
        "glass_dark": _lmm("glass_clean"),
        "warm_panel": _solid("LM_WARM", "#F0C98A", rough=0.6,
                             emission="#FFC87A", es=1.6),
        "ceil_coffer": _solid("LM_COFFER", "#D8C298", rough=0.65,
                              emission="#FFC87A", es=1.1),
        "step": _lmm("granite_grey"),
        "walk": _solid("LM_WALK", "#565C64", rough=0.9),
        "asphalt": _solid("LM_ASPHALT", "#252A30", rough=0.55),
        "flag": _solid("LM_FLAG", "#7C2A26", rough=0.9),
        "grime": _solid("LM_GRIME", "#5E6058", rough=0.95),
        "concrete": _lmm("concrete_grey"),
        "granite": _lmm("granite_grey"),
        "lamp_glow": _solid("LM_LAMP_GLOW", "#FFD9A0", rough=0.3,
                            emission="#FFC87A", es=16.0),
    }
    # mosaic tile palette (blue / gold / red / cream, close to smalti mosaics)
    cols = ["#2A2E33", "#2F4C6B", "#55779A", "#7B2E25", "#8E2A24",
            "#B79249", "#C9A85B", "#D8D2C2", "#3E5A62"]
    m["tiles"] = [_solid("LM_TILE_" + str(i), c, rough=0.55) for i, c in
                  enumerate(cols)]
    _MCACHE["lm"] = m
    return m



# ============================================================================
# geometry helpers
# ============================================================================

def _rot_for(n):
    n = Vector(n)
    if n.y < -0.5:
        return (math.radians(90), 0, 0)
    if n.y > 0.5:
        return (math.radians(90), 0, math.pi)
    if n.x > 0.5:
        return (math.radians(90), 0, math.pi / 2)
    return (math.radians(90), 0, -math.pi / 2)


def _frustum(parent, name, cx, cy, sb, st, z0, z1, mat):
    """Vertical frustum: sb/st = (size_x, size_y) at bottom / top, centred cx,cy."""
    bw, bd = sb
    tw, td = st
    verts = [(cx - bw / 2, cy - bd / 2, z0), (cx + bw / 2, cy - bd / 2, z0),
             (cx + bw / 2, cy + bd / 2, z0), (cx - bw / 2, cy + bd / 2, z0),
             (cx - tw / 2, cy - td / 2, z1), (cx + tw / 2, cy - td / 2, z1),
             (cx + tw / 2, cy + td / 2, z1), (cx - tw / 2, cy + td / 2, z1)]
    faces = [(3, 2, 1, 0), (4, 5, 6, 7), (0, 1, 5, 4), (1, 2, 6, 5),
             (2, 3, 7, 6), (3, 0, 4, 7)]
    return kit._mesh_obj(name, verts, faces, mat, parent)


def _taper_between(parent, name, p0, p1, w0, d0, w1, d1, mat):
    """Tapered beam between two points (cross-section in world X / Y)."""
    p0, p1 = Vector(p0), Vector(p1)
    verts = []
    for p, w, d in ((p0, w0, d0), (p1, w1, d1)):
        verts += [(p.x - w / 2, p.y - d / 2, p.z), (p.x + w / 2, p.y - d / 2, p.z),
                  (p.x + w / 2, p.y + d / 2, p.z), (p.x - w / 2, p.y + d / 2, p.z)]
    faces = [(3, 2, 1, 0), (4, 5, 6, 7), (0, 1, 5, 4), (1, 2, 6, 5),
             (2, 3, 7, 6), (3, 0, 4, 7)]
    return kit._mesh_obj(name, verts, faces, mat, parent)


def _hip_roof(parent, name, cx, cy, sx, sy, z0, h, mat):
    """Hipped roof over an sx*sy rectangle."""
    hx, hy = sx / 2, sy / 2
    if sx >= sy:
        rx = max(0.0, hx - hy)
        verts = [(cx - hx, cy - hy, z0), (cx + hx, cy - hy, z0),
                 (cx + hx, cy + hy, z0), (cx - hx, cy + hy, z0),
                 (cx - rx, cy, z0 + h), (cx + rx, cy, z0 + h)]
        faces = [(0, 1, 5, 4), (2, 3, 4, 5), (1, 2, 5), (3, 0, 4)]
    else:
        ry = max(0.0, hy - hx)
        verts = [(cx - hx, cy - hy, z0), (cx + hx, cy - hy, z0),
                 (cx + hx, cy + hy, z0), (cx - hx, cy + hy, z0),
                 (cx, cy - ry, z0 + h), (cx, cy + ry, z0 + h)]
        faces = [(1, 2, 5, 4), (3, 0, 4, 5), (0, 1, 4), (2, 3, 5)]
    return kit._mesh_obj(name, verts, faces, mat, parent)


def _star(parent, name, center, r, depth, mat, axis="X"):
    """Flat five-pointed star extruded along `axis` (X or Y)."""
    cx, cy, cz = center
    pts = []
    for i in range(10):
        ang = math.pi / 2 + i * math.pi / 5
        rr = r if i % 2 == 0 else r * 0.42
        pts.append((math.cos(ang) * rr, math.sin(ang) * rr))
    verts = []
    if axis == "X":
        for dx in (-depth / 2, depth / 2):
            for (a, b) in pts:
                verts.append((cx + dx, cy + a, cz + b))
    else:
        for dy in (-depth / 2, depth / 2):
            for (a, b) in pts:
                verts.append((cx + a, cy + dy, cz + b))
    n = 10
    faces = [tuple(range(n - 1, -1, -1)), tuple(range(n, 2 * n))]
    for i in range(n):
        j = (i + 1) % n
        faces.append((i, j, j + n, i + n))
    return kit._mesh_obj(name, verts, faces, mat, parent)


def _wall_frame(rect, face):
    return kit.wall_frame(rect, face)


def _panel_wall(parent, name, rect, face, z0, z1, mat, openings=(), thick=0.4,
                plinth_z=0.0, plinth_mat=None):
    """One facade plane with real rectangular openings.
    openings = [(s0, s1, za, zb), ...] in face-local coordinates."""
    P0, u, L, n = kit.wall_frame(rect, face)
    zs = {z0, z1}
    for (_s0, _s1, za, zb) in openings:
        zs.add(max(z0, min(z1, za)))
        zs.add(max(z0, min(z1, zb)))
    zs = sorted(zs)
    for zi in range(len(zs) - 1):
        za, zb = zs[zi], zs[zi + 1]
        if zb - za < 0.02:
            continue
        spans = [(0.0, L)]
        for (s0, s1, oz0, oz1) in openings:
            if oz0 <= za + 1e-6 and oz1 >= zb - 1e-6:
                spans = _subtract(spans, s0, s1)
        m = plinth_mat if (plinth_mat is not None and za < z0 + plinth_z - 1e-6) else mat
        for k, (a, b) in enumerate(spans):
            if b - a < 0.02:
                continue
            mid = P0 + u * ((a + b) / 2) + n * (thick / 2) + Vector((0, 0, (za + zb) / 2))
            size = (b - a, thick, zb - za) if abs(u.x) > 0.5 else (thick, b - a, zb - za)
            kit.box(parent, f"{name}_{zi}_{k}", (mid.x, mid.y, (za + zb) / 2), size, m)
    return P0, u, L, n


def _subtract(spans, a0, a1):
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


def _steps(parent, name, P0, u, n, s0, s1, depth, z_top, count, mat):
    P0 = Vector(P0)
    u = Vector(u).normalized()
    n = Vector(n).normalized()
    tread = depth / count
    for i in range(count):
        h = z_top * (count - i) / count
        c = P0 + u * ((s0 + s1) / 2) + n * (tread * (i + 0.5))
        size = (s1 - s0, tread + 0.02, h) if abs(u.x) > 0.5 else (tread + 0.02, s1 - s0, h)
        kit.box(parent, f"{name}_st{i}", (c.x, c.y, h / 2), size, mat)


def _columns_line(parent, name, p0, u, count, spacing, z0, z1, sec, mat,
                  cap_mat=None, base=True, capital=True):
    cap_mat = cap_mat or mat
    p0 = Vector(p0)
    u = Vector(u).normalized()
    for i in range(count):
        c = p0 + u * (i * spacing)
        h = z1 - z0
        kit.box(parent, f"{name}_c{i}", (c.x, c.y, z0 + h / 2), (sec, sec, h), mat,
                bevel=0.02)
        if base:
            kit.box(parent, f"{name}_b{i}", (c.x, c.y, z0 + 0.18),
                    (sec + 0.28, sec + 0.28, 0.36), cap_mat, bevel=0.02)
        if capital:
            kit.box(parent, f"{name}_k{i}", (c.x, c.y, z1 - 0.22),
                    (sec + 0.34, sec + 0.34, 0.44), cap_mat, bevel=0.02)


def _sign(parent, name, center, u, n, w, h, text, bg="#2A3740", fg="#E8E2D2",
          glow=2.2, depth=0.14, size_frac=0.55):
    c = Vector(center)
    u = Vector(u).normalized()
    n = Vector(n).normalized()
    bgm = _solid("LM_SIGNBG_" + str(bg), bg, rough=0.65)
    fgm = _solid("LM_SIGNFG_" + str(fg), fg, rough=0.5, emission=fg, es=glow)
    size = (w, depth, h) if abs(u.x) > 0.5 else (depth, w, h)
    kit.box(parent, name + "_bg", (c.x, c.y, c.z), size, bgm)
    kit.text3d(parent, name + "_txt", text, tuple(c + n * (depth / 2 + 0.01)),
               h * size_frac, fgm, rot=_rot_for(n), extrude=0.02)


def _text(parent, name, body, center, size, mat, n):
    return kit.text3d(parent, name, body, tuple(Vector(center)), size, mat,
                      rot=_rot_for(n), extrude=0.06)


def _streaks(parent, name, P0, u, n, s_list, z0, z1_list, w=0.18):
    """Thin dark weathering streaks running down a vertical face."""
    mat = _solid("LM_STREAK", "#5A5C56", rough=0.95)
    P0 = Vector(P0)
    u = Vector(u).normalized()
    n = Vector(n).normalized()
    for i, s in enumerate(s_list):
        h = z1_list[i % len(z1_list)]
        c = P0 + u * s + n * 0.06 + Vector((0, 0, h / 2 + z0))
        size = (w, 0.05, h) if abs(u.x) > 0.5 else (0.05, w, h)
        kit.box(parent, f"{name}_s{i}", (c.x, c.y, c.z), size, mat)


def _door_unit(parent, name, P0, u, n, s, z0, w, h, mat, frame=None,
               glass_frac=0.0, wm=None, lit=False):
    """Proud-mounted door: dark opening, jambs, leaves, optional lit glass."""
    P0 = Vector(P0)
    u = Vector(u).normalized()
    n = Vector(n).normalized()
    frame = frame or _solid("LM_DOOR_TRIM", "#8A8478", rough=0.8)
    dark = _solid("LM_DOOR_DARK", "#0C0F11", rough=1.0)
    base = P0 + u * s
    c = base + n * 0.01 + Vector((0, 0, z0 + h / 2))
    size = (w, 0.06, h) if abs(u.x) > 0.5 else (0.06, w, h)
    kit.box(parent, name + "_dark", (c.x, c.y, c.z), size, dark)
    for side in (-1, 1):
        c = base + u * (side * (w / 2 + 0.09)) + n * 0.05 + Vector((0, 0, z0 + h / 2))
        size = (0.18, 0.12, h + 0.16) if abs(u.x) > 0.5 else (0.12, 0.18, h + 0.16)
        kit.box(parent, f"{name}_j{side}", (c.x, c.y, c.z), size, frame)
    c = base + n * 0.05 + Vector((0, 0, z0 + h + 0.1))
    size = (w + 0.4, 0.12, 0.2) if abs(u.x) > 0.5 else (0.12, w + 0.4, 0.2)
    kit.box(parent, name + "_lint", (c.x, c.y, c.z), size, frame)
    leaves = 2
    lw = w / leaves
    for li in range(leaves):
        lc = base + u * (-w / 2 + lw * (li + 0.5)) + n * 0.055 + Vector((0, 0, z0 + h / 2 - 0.06))
        size = (lw - 0.06, 0.06, h - 0.12) if abs(u.x) > 0.5 else (0.06, lw - 0.06, h - 0.12)
        kit.box(parent, f"{name}_l{li}", (lc.x, lc.y, lc.z), size, mat)
        if glass_frac > 0 and wm is not None:
            gm = wm["warm_a"] if (lit and (li == 0)) else wm["dark_a"]
            gc = base + u * (-w / 2 + lw * (li + 0.5)) + n * 0.09 + Vector((0, 0, z0 + h * 0.62))
            kit.quad(parent, f"{name}_g{li}", tuple(gc), u, Vector((0, 0, 1)),
                     lw * 0.62, h * 0.44, gm,
                     uv=[(0.2, 0.2), (0.2, 0.8), (0.8, 0.8), (0.8, 0.2)])


def _coffered_ceiling(parent, name, x0, y0, x1, y1, z, beam, coffer, warm, cols=6,
                      rows=3):
    """Grid of beams with recessed warm panels between them."""
    x0, y0, x1, y1 = float(x0), float(y0), float(x1), float(y1)
    for i in range(cols + 1):
        x = x0 + (x1 - x0) * i / cols
        kit.box_mm(parent, f"{name}_bx{i}", x - 0.16, y0, z, x + 0.16, y1, z + 0.32, beam)
    for j in range(rows + 1):
        y = y0 + (y1 - y0) * j / rows
        kit.box_mm(parent, f"{name}_by{j}", x0, y - 0.14, z, x1, y + 0.14, z + 0.3, beam)
    for i in range(cols):
        for j in range(rows):
            cx = x0 + (x1 - x0) * (i + 0.5) / cols
            cy = y0 + (y1 - y0) * (j + 0.5) / rows
            kit.quad(parent, f"{name}_cf{i}_{j}", (cx, cy, z + 0.02), (1, 0, 0),
                     (0, 1, 0), (x1 - x0) / cols - 0.42, (y1 - y0) / rows - 0.36, warm)


def _chandelier(parent, name, center, bx=0.95):
    m = _mats()
    x, y, z = center
    kit.cyl(parent, name + "_stem", (x, y, z + 1.3), 0.06, 2.6, m["bronze"], verts=8)
    kit.cyl(parent, name + "_ring", (x, y, z), bx, 0.16, m["bronze"], verts=16)
    kit.cyl(parent, name + "_ring2", (x, y, z + 0.62), bx * 0.62, 0.12, m["bronze"],
            verts=14)
    glow = _solid("LM_CHAND_GLOW", "#FFE0B0", rough=0.3,
                  emission="#FFC87A", es=18.0)
    for i in range(10):
        ang = i * 2 * math.pi / 10
        kit.sphere(parent, f"{name}_b{i}",
                   (x + math.cos(ang) * bx * 0.88, y + math.sin(ang) * bx * 0.88,
                    z - 0.06), 0.1, glow, segments=8, rings=4)
    for i in range(4):
        ang = math.pi / 2 + i * math.pi / 2
        kit.cyl_between(parent, f"{name}_ch{i}",
                        (x + math.cos(ang) * 0.3, y + math.sin(ang) * 0.3, z + 0.7),
                        (x + math.cos(ang) * bx * 0.8, y + math.sin(ang) * bx * 0.8,
                         z + 0.1), 0.025, m["bronze"], verts=4)


def _tile_panel(parent, name, center, u, n, w, h, rng, cols=None, rows=None):
    """Mosaic panel: one mesh, coloured tesserae, slight relief."""
    m = _mats()
    tiles = m["tiles"]
    c = Vector(center)
    u = Vector(u).normalized()
    n = Vector(n).normalized()
    cols = cols or max(8, int(w / 0.28))
    rows = rows or max(4, int(h / 0.24))
    tw = w / cols
    th = h / rows
    verts, faces, midx = [], [], []
    cx_n, cy_n = 0.5, 0.52

    def tile(i, j):
        x = (i + 0.5) / cols
        y = (j + 0.5) / rows
        dx, dy = x - cx_n, (y - cy_n) * 1.15
        rr = math.hypot(dx, dy)
        if rr < 0.16:
            return 6 if rng.random() < 0.7 else 5
        if rr < 0.42 and int((math.atan2(dy, dx) + math.pi) / (2 * math.pi) * 14) % 2 == 0:
            return 5 if y < cy_n else 6
        if y > 0.86:
            return 1
        if y < 0.12:
            return 4
        if rng.random() < 0.12:
            return rng.randint(3, 8)
        return 2 if rng.random() < 0.6 else 1

    for j in range(rows):
        for i in range(cols):
            x0 = (i / cols - 0.5) * w
            x1 = ((i + 1) / cols - 0.5) * w
            z0 = (j / rows - 0.5) * h
            z1 = ((j + 1) / rows - 0.5) * h
            d = 0.012 + rng.random() * 0.02
            base_c = c + n * d
            k = len(verts)
            verts.extend([tuple(base_c + u * x0 + Vector((0, 0, z0))),
                          tuple(base_c + u * x1 + Vector((0, 0, z0))),
                          tuple(base_c + u * x1 + Vector((0, 0, z1))),
                          tuple(base_c + u * x0 + Vector((0, 0, z1)))])
            faces.append((k, k + 1, k + 2, k + 3))
            midx.append(tile(i, j) % len(tiles))
    me = bpy.data.meshes.new(name)
    me.from_pydata(verts, [], faces)
    me.update()
    for t in tiles:
        me.materials.append(t)
    for poly, mi in zip(me.polygons, midx):
        poly.material_index = mi
    obj = bpy.data.objects.new(name, me)
    coll = parent.users_collection[0] if parent is not None and parent.users_collection else bpy.context.scene.collection
    coll.objects.link(obj)
    if parent is not None:
        obj.parent = parent
    return obj


# ============================================================================
# Round-4 institutional helpers: LMM materials, lofts, tubes, capes, fluted
# columns, layered cornices (local to this module — kit.py untouched)
# ============================================================================
_LMM_CACHE: dict[str, object] = {}


def _lmm(key):
    """Landmark material from tools/lm_materials.py; tinted solid while the
    ref/H albedo textures are still being generated (kit's texture fallback is
    dark grey, so we build our own fallback from the LMM spec tint)."""
    if key in _LMM_CACHE:
        _cached = _LMM_CACHE[key]
        try:
            if _cached.name in bpy.data.materials:
                return _cached
        except ReferenceError:
            pass
        _LMM_CACHE.pop(key, None)
    mat = None
    try:
        import lm_materials as _lmmmod
    except Exception:  # noqa: BLE001
        _lmmmod = None
    if _lmmmod is not None and key in getattr(_lmmmod, "SPECS", {}):
        fname, tint, rough, metallic = _lmmmod.SPECS[key]
        if _lmmmod._find(fname):
            mat = kit.mat_texture("LMM_" + key, fname, rough=rough,
                                  metallic=metallic, tint=tint)
        else:
            mat = kit.mat_solid("LMM_SOLID_" + key, tint, rough=rough,
                                metallic=metallic)
    if mat is None:
        mat = kit.mat_solid("LMM_SOLID_" + key, "#A9A9A2", rough=0.8)
    _LMM_CACHE[key] = mat
    return mat


def _loft(parent, name, rings, mat, seg=36, caps=True):
    """Vertical organic loft.

    rings: [(z, cx, cy, rx, ry, fold_amp, fold_n, phase), ...] bottom -> top.
    fold_amp/fold_n add vertical drapery ridges (`amp` = fraction of radius);
    phase twists the folds ring by ring for a swirling cloth look.
    """
    verts = []
    faces = []
    for (z, cx, cy, rx, ry, amp, fn, ph) in rings:
        for s in range(seg):
            th = 2 * math.pi * s / seg
            rr = 1.0 + amp * math.cos(fn * th + ph)
            verts.append((cx + math.cos(th) * rx * rr,
                          cy + math.sin(th) * ry * rr, z))
    for i in range(len(rings) - 1):
        for s in range(seg):
            a = i * seg + s
            b = i * seg + (s + 1) % seg
            c = (i + 1) * seg + (s + 1) % seg
            d = (i + 1) * seg + s
            faces.append((a, b, c, d))
    if caps:
        faces.append(tuple(range(seg - 1, -1, -1)))
        base = (len(rings) - 1) * seg
        faces.append(tuple(range(base, base + seg)))
    return kit._mesh_obj(name, verts, faces, mat, parent)


def _tube(parent, name, pts, radii, mat, seg=14, flatten=1.0, caps=True):
    """Smooth tapered tube along a polyline (arms, legs, grips, rods)."""
    pts = [Vector(p) for p in pts]
    n = len(pts)
    verts = []
    faces = []
    for i, p in enumerate(pts):
        if i == 0:
            t = (pts[1] - pts[0]).normalized()
        elif i == n - 1:
            t = (pts[-1] - pts[-2]).normalized()
        else:
            t = (pts[i + 1] - pts[i - 1]).normalized()
        up = Vector((0, 0, 1))
        if abs(t.dot(up)) > 0.94:
            up = Vector((0, 1, 0))
        right = t.cross(up).normalized()
        up2 = right.cross(t).normalized()
        r = radii[i]
        for s in range(seg):
            th = 2 * math.pi * s / seg
            verts.append(tuple(p + right * (math.cos(th) * r)
                               + up2 * (math.sin(th) * r * flatten)))
    for i in range(n - 1):
        for s in range(seg):
            a = i * seg + s
            b = i * seg + (s + 1) % seg
            c = (i + 1) * seg + (s + 1) % seg
            d = (i + 1) * seg + s
            faces.append((a, b, c, d))
    if caps:
        faces.append(tuple(range(seg - 1, -1, -1)))
        base = (n - 1) * seg
        faces.append(tuple(range(base, base + seg)))
    return kit._mesh_obj(name, verts, faces, mat, parent)


def _cape(parent, name, rows, mat, cols=16):
    """Curved hanging cloth sheet.

    rows: [(z, cx, cy, half_w, wave), ...] top -> bottom; wave adds billow.
    """
    verts = []
    faces = []
    for (z, cx, cy, hw, wave) in rows:
        for c in range(cols):
            t = c / (cols - 1)
            x = cx + (t - 0.5) * 2.0 * hw
            y = (cy + wave * 0.35 * math.sin((t - 0.5) * math.pi * 2.0)
                 + 0.18 * math.sin(t * math.pi))
            verts.append((x, y, z))
    for i in range(len(rows) - 1):
        for c in range(cols - 1):
            a = i * cols + c
            b = a + 1
            cc = (i + 1) * cols + c + 1
            d = cc - 1
            faces.append((a, b, cc, d))
    return kit._mesh_obj(name, verts, faces, mat, parent)


def _stairs(parent, name, P0, u, n, s0, s1, depth, z_floor, z_top, count, mat):
    """Stair flight between two elevations (works on top of terraces too)."""
    P0 = Vector(P0)
    u = Vector(u).normalized()
    n = Vector(n).normalized()
    tread = depth / count
    for i in range(count):
        h_top = z_floor + (z_top - z_floor) * (count - i) / count
        c = P0 + u * ((s0 + s1) / 2) + n * (tread * (i + 0.5))
        size = (s1 - s0, tread + 0.02, h_top) if abs(u.x) > 0.5 \
            else (tread + 0.02, s1 - s0, h_top)
        kit.box(parent, f"{name}_{i}", (c.x, c.y, h_top / 2), size, mat)


def _column(parent, name, center, r, z0, z1, mat, flutes=20, depth=0.055,
            cap_mat=None, seg=40):
    """Classical fluted column with base, shaft entasis and capital."""
    cap_mat = cap_mat or mat
    x, y = center
    h = z1 - z0
    rings = [
        (z0, x, y, r * 1.18, r * 1.18, 0.0, 8, 0.0),
        (z0 + 0.10 * h, x, y, r * 1.06, r * 1.06, 0.0, 8, 0.0),
        (z0 + 0.16 * h, x, y, r * 1.02, r * 1.02, depth / r, flutes, 0.0),
        (z0 + 0.45 * h, x, y, r, r, depth / r * 1.05, flutes, 0.0),
        (z0 + 0.82 * h, x, y, r * 0.954, r * 0.954, depth / r * 1.1, flutes, 0.0),
        (z0 + 0.90 * h, x, y, r * 0.98, r * 0.98, depth / r, flutes, 0.0),
        (z1 - 0.06 * h, x, y, r * 1.18, r * 1.18, 0.0, 8, 0.0),
    ]
    _loft(parent, name + "_shaft", rings, mat, seg=seg)
    kit.box(parent, name + "_base", (x, y, z0 + 0.13), (r * 2.7, r * 2.7, 0.26),
            cap_mat, bevel=0.02)
    kit.box(parent, name + "_cap", (x, y, z1 - 0.15), (r * 2.9, r * 2.9, 0.3),
            cap_mat, bevel=0.02)
    kit.box(parent, name + "_abacus", (x, y, z1 - 0.02), (r * 3.1, r * 3.1, 0.16),
            cap_mat, bevel=0.02)


def _cornice(parent, name, cx, cy, sx, sy, z, h, mat, steps=3, grow=0.22):
    """Layered cornice (each course projects further than the one below)."""
    for i in range(steps):
        f = i / max(1, steps - 1)
        ox = grow * (i + 1)
        kit.box_mm(parent, f"{name}_{i}", cx - sx / 2 - ox, cy - sy / 2 - ox,
                   z + h * i / steps, cx + sx / 2 + ox, cy + sy / 2 + ox,
                   z + h * (i + 1) / steps, mat, bevel=0.015)


def _mosaic_band(parent, name, center, u, n, w, h, rng, key="mosaic_blue"):
    """Flat mosaic band using an LMM mosaic material (falls back to smalti
    coloured tesserae while the texture generates)."""
    m = _mats()
    c = Vector(center)
    u = Vector(u).normalized()
    n = Vector(n).normalized()
    mat = m.get(key, m["mosaic_blue"])
    if mat.name.endswith("_FB") or "SOLID" in mat.name:
        # coloured tile mesh so the band reads before the albedo lands
        cols = max(6, int(w / 0.26))
        rows = max(3, int(h / 0.22))
        tw, th = w / cols, h / rows
        verts, faces, midx = [], [], []
        tiles = m["tiles"]
        for j in range(rows):
            for i in range(cols):
                x0 = (i / cols - 0.5) * w
                x1 = ((i + 1) / cols - 0.5) * w
                z0 = (j / rows - 0.5) * h
                z1 = ((j + 1) / rows - 0.5) * h
                base = c + n * (0.012 + rng.random() * 0.018)
                k = len(verts)
                verts.extend([tuple(base + u * x0 + Vector((0, 0, z0))),
                              tuple(base + u * x1 + Vector((0, 0, z0))),
                              tuple(base + u * x1 + Vector((0, 0, z1))),
                              tuple(base + u * x0 + Vector((0, 0, z1)))])
                faces.append((k, k + 1, k + 2, k + 3))
                if key == "mosaic_gold":
                    idx = 5 if rng.random() < 0.6 else 6
                elif key == "mosaic_red":
                    idx = 4 if rng.random() < 0.6 else 3
                else:
                    idx = rng.choice([1, 1, 2, 2, 5, 6, 3])
                midx.append(idx % len(tiles))
        me = bpy.data.meshes.new(name)
        me.from_pydata(verts, [], faces)
        me.update()
        for t in tiles:
            me.materials.append(t)
        for poly, mi in zip(me.polygons, midx):
            poly.material_index = mi
        obj = bpy.data.objects.new(name, me)
        coll = parent.users_collection[0] if parent is not None and parent.users_collection else bpy.context.scene.collection
        coll.objects.link(obj)
        if parent is not None:
            obj.parent = parent
        return obj
    return kit.quad(parent, name, tuple(c), u, Vector((0, 0, 1)), w, h, mat)



# ============================================================================
# 1) MOTHERLAND — colossal concrete figure with raised sword
#    block rect [-12,138,12,150], faces PY, h ~34 m
# ============================================================================
def _uv_project(parent, tile=3.0):
    """Box-project UVs (in metres / tile) onto UV-less meshes so the Round-4
    LMM albedo textures actually show on kit boxes (kit.box has no UVs)."""
    from mathutils import Vector  # noqa: F401
    inv = 1.0 / max(0.5, float(tile))
    for ob in parent.children_recursive:
        if ob.type != "MESH" or ob.data.uv_layers:
            continue
        me = ob.data
        if not me.materials:
            continue
        mw = ob.matrix_world
        nmat = mw.to_3x3().inverted().transposed()
        uv = me.uv_layers.new(name="UVMap")
        for poly in me.polygons:
            n = nmat @ poly.normal
            ax = 0 if abs(n.x) >= abs(n.y) and abs(n.x) >= abs(n.z) else (
                1 if abs(n.y) >= abs(n.z) else 2)
            for li in poly.loop_indices:
                co = mw @ me.vertices[me.loops[li].vertex_index].co
                if ax == 2:
                    u, v = co.x, co.y
                elif ax == 0:
                    u, v = co.y, co.z
                else:
                    u, v = co.x, co.z
                uv.data[li].uv = (u * inv, v * inv)



def build_motherland(parent, block, rng, ctx):
    """The Motherland Calls — colossal concrete figure with raised sword.

    Round-4 rebuild: lofted dress with vertical drapery ridges and a forward
    stride, narrow waist, twisted torso, small turned head with wind-blown
    hair, raised right arm with a broad backward-tilted sword, outstretched
    left arm, cloak sweeping behind.  Builds z=0..height (plan z lift is applied
    by build.py), object prefix = block id.
    """
    m = _mats()
    pid = block["id"]
    x0, y0, x1, y1 = block["rect"]
    H = float(block.get("height", 40.0))
    cx, cy = (x0 + x1) / 2.0, (y0 + y1) / 2.0
    stats = {"windows": 0, "lit": 0}
    stone = m["limestone_light"]
    granite = m["granite_grey"]
    dark = m["granite_dark"]
    bronze = m["bronze"]
    gold = m["gold"]

    # ---- stepped mound + pedestal ---------------------------------------
    kit.box_mm(parent, pid + "_ter1", cx - 8.4, cy - 5.0, 0.0,
               cx + 8.4, cy + 5.0, 1.0, granite, bevel=0.16)
    kit.box_mm(parent, pid + "_ter2", cx - 6.6, cy - 4.2, 1.0,
               cx + 6.6, cy + 4.2, 2.0, granite, bevel=0.16)
    kit.box_mm(parent, pid + "_ter3", cx - 4.9, cy - 3.4, 2.0,
               cx + 4.9, cy + 3.4, 3.0, granite, bevel=0.16)
    kit.box_mm(parent, pid + "_ped_base", cx - 3.9, cy - 2.7, 2.8,
               cx + 3.9, cy + 2.7, 3.9, m["stone_rusticated"], bevel=0.04)
    kit.box_mm(parent, pid + "_ped", cx - 4.3, cy - 3.0, 3.9,
               cx + 4.3, cy + 3.0, 5.5, m["limestone"], bevel=0.03)
    _cornice(parent, pid + "_ped_cornice", cx, cy, 8.7, 6.1, 5.5, 0.45, dark, 2,
             grow=0.16)
    kit.box_mm(parent, pid + "_ped_top", cx - 3.9, cy - 2.7, 5.95,
               cx + 3.9, cy + 2.7, 6.1, stone, bevel=0.02)
    _stairs(parent, pid + "_stS", (0, cy - 5.0, 0), (1, 0, 0), (0, -1, 0),
            cx - 6.6, cx + 6.6, 1.6, 0.0, 1.0, 5, m["step"])
    _stairs(parent, pid + "_stS2", (0, cy - 4.2, 0), (1, 0, 0), (0, -1, 0),
            cx - 5.2, cx + 5.2, 0.9, 1.0, 2.0, 4, m["step"])
    _stairs(parent, pid + "_stN", (0, cy + 5.0, 0), (1, 0, 0), (0, 1, 0),
            cx - 5.8, cx + 5.8, 0.8, 0.0, 1.0, 4, m["step"])
    # bronze plaque + inscription on the pedestal front (+Y)
    kit.box_mm(parent, pid + "_plq", cx - 2.5, cy + 2.42, 4.35,
               cx + 2.5, cy + 2.5, 5.05, bronze)
    _text(parent, pid + "_instr", "РОДИНА-МАТЬ ЗОВЁТ", (cx, cy + 2.56, 4.7),
          0.34, gold, (0, 1, 0))
    for i in range(14):
        ang = i * 2 * math.pi / 14
        kit.box(parent, f"{pid}_wreath{i}",
                (cx + math.cos(ang) * 0.62, cy + 2.47,
                 3.55 + math.sin(ang) * 0.62),
                (0.16, 0.1, 0.16), bronze, rot=(0, 0, ang))
    for side in (-1, 1):
        lx = cx + side * 7.1
        kit.cyl(parent, f"{pid}_torchP{side}", (lx, cy - 4.6, 1.75), 0.1, 3.5,
                bronze, verts=10)
        kit.box(parent, f"{pid}_torchB{side}", (lx, cy - 4.6, 3.5),
                (0.5, 0.5, 0.16), bronze)
        kit.sphere(parent, f"{pid}_torchG{side}", (lx, cy - 4.6, 3.72), 0.2,
                   m["lamp_glow"], segments=10, rings=5)

    # =====================================================================
    # FIGURE — total 25.7 m above the pedestal-top pad (abs. ~31.8 m; sword
    # tip reaches the 40 m height contract).
    # =====================================================================
    z0 = 6.1
    # ---- long dress: forward stride, wind-blown hem, drapery ridges -----
    _loft(parent, pid + "_dress", [
        (z0 + 0.0, cx - 0.12, cy + 0.98, 4.35, 2.45, 0.11, 11, 0.00),
        (z0 + 1.4, cx - 0.16, cy + 1.08, 4.15, 2.34, 0.12, 11, 0.22),
        (z0 + 3.0, cx - 0.18, cy + 0.95, 3.65, 2.18, 0.13, 11, 0.45),
        (z0 + 4.8, cx - 0.20, cy + 0.72, 3.15, 2.00, 0.13, 12, 0.68),
        (z0 + 6.6, cx - 0.18, cy + 0.48, 2.72, 1.82, 0.12, 12, 0.92),
        (z0 + 8.4, cx - 0.12, cy + 0.24, 2.32, 1.66, 0.11, 12, 1.15),
        (z0 + 10.0, cx - 0.05, cy + 0.08, 1.98, 1.50, 0.08, 12, 1.35),
        (z0 + 11.4, cx + 0.00, cy + 0.0, 1.78, 1.38, 0.05, 10, 1.50),
        (z0 + 12.6, cx + 0.02, cy + 0.0, 1.66, 1.30, 0.03, 9, 1.60),
    ], stone, seg=48)
    # forward knee/leg mass under the cloth (stride)
    knee = kit.sphere(parent, pid + "_knee", (cx + 0.55, cy + 1.55, z0 + 4.6),
                      1.0, stone, segments=16, rings=8)
    knee.scale = (1.15, 0.75, 2.6)
    # blown back-hem swirl
    kit.sphere(parent, pid + "_hemswirl", (cx - 1.1, cy - 1.2, z0 + 1.3),
               1.1, stone, segments=14, rings=7).scale = (1.6, 0.8, 0.55)
    # diagonal hip drape (tight to the body)
    kit.box(parent, pid + "_hipdrape", (cx - 0.15, cy + 1.5, z0 + 11.6),
            (2.85, 0.24, 0.72), stone, rot=(0, 0.26, 0))

    # ---- waist / torso / shoulders --------------------------------------
    _loft(parent, pid + "_torso", [
        (z0 + 12.6, cx + 0.02, cy + 0.0, 1.66, 1.30, 0.02, 9, 0.0),
        (z0 + 13.3, cx + 0.04, cy + 0.04, 1.72, 1.34, 0.02, 9, 0.15),
        (z0 + 14.4, cx + 0.06, cy + 0.10, 1.90, 1.42, 0.02, 9, 0.35),
        (z0 + 15.6, cx + 0.08, cy + 0.14, 2.16, 1.52, 0.02, 9, 0.55),
        (z0 + 16.8, cx + 0.07, cy + 0.14, 2.42, 1.62, 0.02, 9, 0.75),
        (z0 + 17.9, cx + 0.05, cy + 0.10, 2.66, 1.72, 0.02, 9, 0.95),
        (z0 + 18.8, cx + 0.02, cy + 0.05, 2.78, 1.78, 0.02, 9, 1.10),
        (z0 + 19.4, cx + 0.00, cy + 0.0, 2.42, 1.62, 0.02, 9, 1.20),
    ], stone, seg=44)
    for side in (-1, 1):
        kit.sphere(parent, f"{pid}_sh{side}",
                   (cx + side * 2.35, cy + 0.10, z0 + 19.6), 0.86, stone,
                   segments=16, rings=8)
    # chest drapery bands (diagonal, tight to the torso)
    kit.box(parent, pid + "_sashA", (cx - 0.15, cy + 1.52, z0 + 17.55),
            (3.5, 0.2, 0.6), stone, rot=(0, 0.62, 0))
    kit.box(parent, pid + "_sashB", (cx + 0.15, cy + 1.46, z0 + 15.95),
            (3.1, 0.18, 0.48), stone, rot=(0, -0.5, 0))
    _star(parent, pid + "_chest_star", (cx - 0.05, cy + 1.96, z0 + 17.2), 0.9,
          0.24, gold, axis="Y")
    # neck (longer, thicker)
    _tube(parent, pid + "_neck", [(cx, cy + 0.22, z0 + 19.45),
                                  (cx - 0.02, cy + 0.32, z0 + 20.15),
                                  (cx - 0.04, cy + 0.38, z0 + 20.95)],
          [0.72, 0.64, 0.6], stone, seg=14)

    # ---- head (smaller, turned to her right, chin up) -------------------
    _loft(parent, pid + "_head", [
        (z0 + 20.9, cx + 0.06, cy + 0.40, 0.52, 0.46, 0.0, 6, 0.0),
        (z0 + 21.35, cx + 0.01, cy + 0.40, 0.76, 0.68, 0.0, 6, 0.0),
        (z0 + 21.9, cx - 0.06, cy + 0.38, 0.88, 0.79, 0.0, 6, 0.0),
        (z0 + 22.5, cx - 0.12, cy + 0.34, 0.9, 0.81, 0.0, 6, 0.0),
        (z0 + 23.1, cx - 0.16, cy + 0.29, 0.77, 0.7, 0.0, 6, 0.0),
        (z0 + 23.6, cx - 0.18, cy + 0.23, 0.56, 0.52, 0.0, 6, 0.0),
        (z0 + 23.95, cx - 0.18, cy + 0.18, 0.3, 0.28, 0.0, 6, 0.0),
    ], stone, seg=32)
    # face hint: brow + nose + chin wedges on the +Y side
    kit.box(parent, pid + "_brow", (cx - 0.10, cy + 1.0, z0 + 22.4),
            (0.92, 0.16, 0.15), stone, rot=(0, 0, 0.04))
    kit.box(parent, pid + "_nose", (cx - 0.10, cy + 1.07, z0 + 22.05),
            (0.22, 0.28, 0.38), stone, rot=(0.12, 0, 0))
    kit.box(parent, pid + "_chin", (cx - 0.06, cy + 0.9, z0 + 21.5),
            (0.5, 0.22, 0.26), stone)
    # hair: skull cap + swept locks
    _loft(parent, pid + "_hair", [
        (z0 + 22.0, cx - 0.20, cy + 0.14, 0.92, 0.81, 0.13, 8, 0.0),
        (z0 + 22.65, cx - 0.26, cy + 0.06, 1.02, 0.88, 0.15, 8, 0.35),
        (z0 + 23.3, cx - 0.32, cy + 0.0, 0.95, 0.81, 0.16, 8, 0.70),
        (z0 + 23.85, cx - 0.36, cy - 0.02, 0.77, 0.65, 0.13, 7, 1.00),
        (z0 + 24.25, cx - 0.38, cy - 0.04, 0.62, 0.54, 0.10, 7, 1.20),
    ], stone, seg=30)
    kit.sphere(parent, pid + "_bun", (cx - 0.95, cy - 0.62, z0 + 23.3), 0.56,
               stone, segments=12, rings=6)
    for i, ((bx, by, bz), rr) in enumerate((
            ((-0.52, -0.92, 21.55), [0.26, 0.18, 0.08]),
            ((-0.78, -0.66, 22.2), [0.3, 0.2, 0.08]),
            ((-0.9, -0.3, 22.85), [0.28, 0.17, 0.07]),
            ((-0.78, 0.08, 23.25), [0.24, 0.14, 0.06]))):
        _tube(parent, f"{pid}_lock{i}",
              [(cx - 0.32, cy + 0.15, z0 + bz + 0.5),
               (cx + bx * 0.55, cy + by * 0.6, z0 + bz + 0.08),
               (cx + bx, cy + by, z0 + bz - 0.05)], rr, stone, seg=10)

    # ---- right arm raised with sword (statue's right = -X) --------------
    sh = Vector((cx - 2.40, cy + 0.10, z0 + 19.55))
    elbow = Vector((cx - 3.30, cy + 0.42, z0 + 22.10))
    wrist = Vector((cx - 3.50, cy + 0.60, z0 + 24.55))
    _tube(parent, pid + "_armR", [tuple(sh), (cx - 2.95, cy + 0.28, z0 + 20.9),
                                  tuple(elbow), (cx - 3.48, cy + 0.55, z0 + 23.5),
                                  tuple(wrist)],
          [1.14, 1.0, 0.88, 0.72, 0.56], stone, seg=18)
    hand = wrist + Vector((-0.04, 0.02, 0.38))
    kit.sphere(parent, pid + "_handR", tuple(hand), 0.52, stone, segments=14,
               rings=7)
    for _fi in range(3):
        kit.box(parent, f"{pid}_fist{_fi}",
                (hand.x - 0.26, hand.y - 0.16 + _fi * 0.17, hand.z + 0.16),
                (0.4, 0.13, 0.13), stone, rot=(0, -0.12, 0))
    kit.box(parent, pid + "_thumbR", (hand.x + 0.2, hand.y + 0.28, hand.z + 0.1),
            (0.16, 0.34, 0.16), stone, rot=(0, 0, 0.4))
    # sword: grip + wide guard + broad tapered blade tilted back ~18 deg
    tilt = math.radians(18.0)
    d = Vector((0.03, -math.sin(tilt), math.cos(tilt))).normalized()
    grip0 = hand + d * 0.18
    guard = hand + d * 1.05
    blade_len = max(7.5, H - (guard.z + 0.4))
    tip = guard + d * blade_len
    _tube(parent, pid + "_grip", [tuple(grip0), tuple(guard)], [0.17, 0.17],
          bronze, seg=10)
    guard_c = guard - d * 0.08
    kit.box(parent, pid + "_guard", (guard_c.x, guard_c.y, guard_c.z),
            (2.3, 0.5, 0.34), m["stone_dark"], rot=(-tilt, 0, 0))
    for side in (-1, 1):
        kit.sphere(parent, f"{pid}_guardT{side}",
                   (guard_c.x + side * 1.15, guard_c.y, guard_c.z), 0.17,
                   m["stone_dark"], segments=8, rings=4)
    _taper_between(parent, pid + "_blade", tuple(guard + d * 0.12), tuple(tip),
                   1.25, 0.3, 0.34, 0.12, stone)
    # blade ridge highlight
    _taper_between(parent, pid + "_bladeridge",
                   tuple(guard + d * 0.16 + Vector((0, 0.12, 0))),
                   tuple(tip + Vector((0, 0.09, 0))), 0.16, 0.08, 0.06, 0.04,
                   m["stone_dark"])
    kit.box(parent, pid + "_pommel", (grip0.x, grip0.y - 0.03, grip0.z - 0.12),
            (0.36, 0.36, 0.2), bronze)

    # ---- left arm outstretched (+X, elbow slightly bent forward) --------
    shL = Vector((cx + 2.55, cy + 0.10, z0 + 19.65))
    elbL = Vector((cx + 4.55, cy + 0.30, z0 + 19.4))
    wristL = Vector((cx + 6.35, cy + 1.05, z0 + 19.6))
    _tube(parent, pid + "_armL", [tuple(shL), (cx + 3.55, cy + 0.18, z0 + 19.55),
                                  tuple(elbL), (cx + 5.4, cy + 0.62, z0 + 19.5),
                                  tuple(wristL)],
          [1.02, 0.86, 0.7, 0.58, 0.46], stone, seg=18)
    hx, hy, hz = wristL.x + 0.46, wristL.y + 0.16, wristL.z + 0.02
    kit.box(parent, pid + "_handL", (hx, hy, hz), (1.05, 0.66, 0.46), stone,
            rot=(0, -0.05, 0.05))
    for i in range(4):
        kit.box(parent, f"{pid}_finger{i}",
                (hx + 0.82, hy + (i - 1.5) * 0.23, hz - 0.07 - abs(i - 1.5) * 0.03),
                (0.72 - abs(i - 1.5) * 0.06, 0.16, 0.17), stone,
                rot=(0, -0.12, (i - 1.5) * 0.13))
    kit.box(parent, pid + "_thumb", (hx + 0.18, hy - 0.42, hz + 0.04),
            (0.5, 0.15, 0.16), stone, rot=(0, 0, -0.85))

    # ---- wind-blown cloak behind / to her right -------------------------
    _cape(parent, pid + "_cloak", [
        (z0 + 19.9, cx - 0.2, cy - 0.9, 2.2, 0.20),
        (z0 + 18.6, cx - 0.45, cy - 1.5, 2.8, 0.35),
        (z0 + 16.8, cx - 0.8, cy - 2.2, 3.4, 0.50),
        (z0 + 14.6, cx - 1.2, cy - 2.9, 3.9, 0.65),
        (z0 + 12.2, cx - 1.6, cy - 3.5, 4.2, 0.80),
        (z0 + 10.0, cx - 1.9, cy - 3.9, 4.4, 0.95),
        (z0 + 8.2, cx - 2.15, cy - 4.1, 4.5, 1.10),
    ], stone, cols=20)
    # second fold layer for cloth thickness
    _cape(parent, pid + "_cloak2", [
        (z0 + 18.8, cx + 0.35, cy - 1.10, 1.5, 0.25),
        (z0 + 16.6, cx + 0.15, cy - 1.75, 1.9, 0.40),
        (z0 + 13.8, cx - 0.15, cy - 2.30, 2.2, 0.55),
        (z0 + 11.0, cx - 0.35, cy - 2.60, 2.3, 0.70),
    ], stone, cols=12)
    # fabric blowing off the outstretched left arm (kept behind the arm)
    _cape(parent, pid + "_capeL", [
        (z0 + 19.6, cx + 1.7, cy - 0.9, 0.9, 0.10),
        (z0 + 18.4, cx + 2.5, cy - 1.3, 1.2, 0.25),
        (z0 + 17.0, cx + 3.2, cy - 1.7, 1.3, 0.35),
        (z0 + 15.6, cx + 3.7, cy - 2.0, 1.3, 0.45),
    ], stone, cols=10)
    # wind-blown hair sweep behind the head
    _cape(parent, pid + "_hairsweep", [
        (z0 + 23.95, cx - 0.25, cy - 0.35, 0.72, 0.10),
        (z0 + 23.35, cx - 0.6, cy - 0.95, 0.98, 0.2),
        (z0 + 22.55, cx - 0.95, cy - 1.5, 1.12, 0.3),
        (z0 + 21.75, cx - 1.2, cy - 1.8, 1.0, 0.35),
    ], stone, cols=10)
    # spine drape
    kit.box(parent, pid + "_spine_drape", (cx - 0.05, cy - 1.30, z0 + 13.4),
            (2.0, 0.45, 9.6), stone, rot=(0.12, 0, 0.05))

    # ---- subtle weathering streaks (landmarks stay cleaner) ------------
    streak_mat = _solid("LM_STREAK_SOFT", "#7C7A72", rough=0.95)
    for i, (sx, hz) in enumerate(((-2.6, 2.1), (-1.2, 1.6), (0.4, 2.4),
                                  (1.8, 1.8), (2.9, 1.4))):
        kit.box(parent, f"{pid}_pstreak{i}",
                (cx + sx, cy + 2.45, 3.9 + hz / 2), (0.16, 0.03, hz), streak_mat)
    for i, (dx, dz, hz) in enumerate(((-2.2, 1.2, 3.2), (-0.6, 0.9, 4.0),
                                      (1.1, 1.4, 3.4), (2.4, 1.1, 2.8))):
        kit.box(parent, f"{pid}_dstreak{i}",
                (cx + dx, cy + 2.28 - abs(dx) * 0.10, z0 + dz + hz / 2),
                (0.14, 0.03, hz), streak_mat)

    _uv_project(parent)
    stats["objects"] = len(parent.children)
    stats["id"] = pid
    return stats



# ============================================================================
# 2) BREST_RELIEF — Courage relief wall
#    block rect [-52,124,-12,128], faces PY, h 6.5 m
# ============================================================================
def build_relief_wall(parent, block, rng, ctx):
    """Brest-Fortress Courage memorial wall (Round-4, carved face SOUTH/-Y).

    The wall stands north of the esplanade: the 6 deep relief panels, bronze
    wreath, date band and «МУЖЕСТВО» face -Y (toward the esplanade/city); the +Y
    side is the plain granite back.  Half-metre relief figures are built as
    layered extrusions.
    """
    m = _mats()
    pid = block["id"]
    x0, y0, x1, y1 = block["rect"]
    H = float(block.get("height", 6.5))
    stats = {"windows": 0, "lit": 0}
    granite = m["granite_grey"]
    dark = m["granite_dark"]
    relief = m["relief_stone"]
    bronze = m["bas_relief_bronze"]
    gold = m["gold"]

    YF = y0 + 0.55            # carved front plane (south, -Y side)
    YB = YF + 0.62            # recessed panel back plane (north of it)
    z_base = 0.9

    # ---- plinth (two stepped granite courses) + bronze band -------------
    kit.box_mm(parent, pid + "_plinth0", x0 + 0.3, y1 - 0.2, 0.0, x1 - 0.3,
               YF - 0.15, 0.45, dark, bevel=0.03)
    kit.box_mm(parent, pid + "_plinth1", x0 + 0.7, YF - 0.05, 0.45, x1 - 0.7,
               y1 - 0.35, 0.9, m["granite_polished"], bevel=0.02)
    kit.box_mm(parent, pid + "_bronzeband", x0 + 0.9, YF - 0.1, 0.62,
               x1 - 0.9, YF - 0.02, 0.78, bronze)

    # ---- wall body + coping (plain granite back on +Y) ------------------
    kit.box_mm(parent, pid + "_body", x0 + 1.5, YB, z_base, x1 - 1.5,
               y1 - 0.55, H - 0.35, granite, bevel=0.02)
    _cornice(parent, pid + "_coping", (x0 + x1) / 2, (y0 + y1) / 2,
             x1 - x0 - 0.9, y1 - y0 - 0.5, H - 0.35, 0.3, m["granite_polished"],
             2, grow=0.14)
    # rusticated course at the wall base (south face)
    for i in range(int((x1 - x0 - 3.0) / 1.7)):
        bx = x0 + 1.8 + i * 1.7
        kit.box(parent, f"{pid}_rust{i}", (bx, YF + 0.03, z_base + 0.28),
                (1.55, 0.14, 0.42), m["stone_rusticated"], bevel=0.02)

    # ---- end pylons -----------------------------------------------------
    for side, px in (("L", x0 + 0.35), ("R", x1 - 2.15)):
        kit.box_mm(parent, f"{pid}_pyl{side}", px, YF - 0.18, 0.0, px + 1.8,
                   y1 - 0.4, 6.5, granite, bevel=0.03)
        kit.box_mm(parent, f"{pid}_pylbase{side}", px - 0.12, YF - 0.3, 0.0,
                   px + 1.92, y1 - 0.28, 0.85, dark, bevel=0.02)
        _cornice(parent, f"{pid}_pylcap{side}", px + 0.9, (YF + y1) / 2, 2.0,
                 2.9, 6.2, 0.3, m["granite_polished"], 2, grow=0.12)
        kit.box(parent, f"{pid}_pylplq{side}", (px + 0.9, YF - 0.24, 3.7),
                (1.3, 0.1, 2.1), bronze)
        for k in range(3):
            kit.box(parent, f"{pid}_pylart{side}{k}",
                    (px + 0.55 + k * 0.35, YF - 0.3, 3.7),
                    (0.18, 0.08, 1.5 - 0.2 * k), gold, rot=(0, 0, 0.1 - 0.1 * k))

    # ---- 6 recessed relief panels (front face -Y) -----------------------
    n_pan = 6
    pan_w, gap = 4.8, 1.0
    start = x0 + 2.6
    for pi in range(n_pan):
        px0 = start + pi * (pan_w + gap)
        px1 = px0 + pan_w
        mid = (px0 + px1) / 2
        z_pan0, z_pan1 = 1.5, 5.35
        # recessed panel back face (north of the front layers)
        kit.box_mm(parent, f"{pid}_pr{pi}", px0 - 0.1, YB, z_base, px1 + 0.1,
                   YB + 0.12, z_pan1 + 0.1, granite, bevel=0.02)
        # rubble mound
        kit.box(parent, f"{pid}_rub{pi}", (mid - 0.9, YB - 0.2, z_pan0 + 0.18),
                (2.1, 0.44, 0.36), m["stone_rusticated"], rot=(0, 0.1, 0.2))
        kit.box(parent, f"{pid}_rub2{pi}", (mid + 1.0, YB - 0.18, z_pan0 + 0.14),
                (1.7, 0.38, 0.28), m["stone_rusticated"], rot=(0, -0.12, -0.3))
        nfig = 2 + (pi % 2)
        for fi in range(nfig):
            fx = mid + (fi - (nfig - 1) / 2.0) * 1.35 + rng.uniform(-0.1, 0.1)
            fh = 2.0 + rng.uniform(0.0, 0.55)
            st_y = YB - 0.14            # project toward the viewer (-Y)
            # torso
            kit.box(parent, f"{pid}_p{pi}f{fi}_b",
                    (fx, st_y - 0.2, z_pan0 + fh * 0.55),
                    (0.66, 0.42, fh), relief, rot=(0, rng.jitter(0.06), 0))
            # shoulders
            kit.box(parent, f"{pid}_p{pi}f{fi}_s",
                    (fx, st_y - 0.24, z_pan0 + fh * 0.86),
                    (1.0, 0.4, 0.34), relief)
            # head + helmet/bun
            kit.box(parent, f"{pid}_p{pi}f{fi}_h",
                    (fx, st_y - 0.26, z_pan0 + fh + 0.24),
                    (0.44, 0.36, 0.46), relief, rot=(0, rng.jitter(0.12), 0))
            kit.box(parent, f"{pid}_p{pi}f{fi}_hh",
                    (fx, st_y - 0.2, z_pan0 + fh + 0.46),
                    (0.5, 0.3, 0.2), relief)
            # arms
            kit.box(parent, f"{pid}_p{pi}f{fi}_a1",
                    (fx + 0.62, st_y - 0.24, z_pan0 + fh * 0.78),
                    (0.28, 0.26, fh * 0.55), relief, rot=(0, -0.55, 0))
            kit.box(parent, f"{pid}_p{pi}f{fi}_a2",
                    (fx - 0.58, st_y - 0.26, z_pan0 + fh * 0.6),
                    (0.26, 0.26, fh * 0.34), relief, rot=(0, 0.3, 0.25))
            # legs
            kit.box(parent, f"{pid}_p{pi}f{fi}_l",
                    (fx, st_y - 0.16, z_pan0 + 0.34),
                    (0.78, 0.34, 0.68), relief)
        # flag / bayonet blades behind the group (angled slabs)
        kit.box(parent, f"{pid}_p{pi}_flag", (px1 - 0.9, YB - 0.18, 3.9),
                (1.7, 0.2, 1.35), m["stone_rusticated"], rot=(0, 0.62, 0.32))
        if pi % 2 == 0:
            kit.box(parent, f"{pid}_p{pi}_rifle", (px0 + 0.8, YB - 0.3, 3.1),
                    (0.1, 0.1, 2.2), dark, rot=(0, 0, 0.45))
        # carved panel frame (projects slightly in front of the wall)
        kit.box_mm(parent, f"{pid}_frL{pi}", px0 - 0.16, YF, z_pan0 - 0.05,
                   px0, YB, z_pan1, relief)
        kit.box_mm(parent, f"{pid}_frR{pi}", px1, YF, z_pan0 - 0.05,
                   px1 + 0.16, YB, z_pan1, relief)
        kit.box_mm(parent, f"{pid}_frT{pi}", px0 - 0.16, YF, z_pan1,
                   px1 + 0.16, YB, z_pan1 + 0.18, relief)
        kit.box_mm(parent, f"{pid}_frB{pi}", px0 - 0.16, YF, z_pan0 - 0.22,
                   px1 + 0.16, YB, z_pan0 - 0.05, relief)

    # ---- bronze wreath + date band on the top band (south face) ---------
    wy = YF - 0.03
    wcx = (x0 + x1) / 2.0
    kit.box_mm(parent, pid + "_dateband", wcx - 4.4, wy - 0.05, 5.5,
               wcx + 4.4, wy + 0.02, 6.15, dark)
    for i in range(18):
        ang = i * 2 * math.pi / 18
        kit.box(parent, f"{pid}_wreath{i}",
                (wcx - 6.6 + math.cos(ang) * 0.62, wy - 0.02,
                 5.8 + math.sin(ang) * 0.62),
                (0.18, 0.1, 0.18), bronze, rot=(0, 0, ang))
    btext = _solid("LM_BRONZE_TEXT", "#D8BC82", rough=0.45, metallic=0.4,
                   emission="#D8BC82", es=0.4)
    _text(parent, pid + "_date", "1941 — 1945", (wcx + 1.0, wy - 0.06, 5.8),
          0.5, btext, (0, -1, 0))
    _text(parent, pid + "_title", "МУЖЕСТВО", (wcx - 3.0, wy - 0.06, 5.8), 0.44,
          btext, (0, -1, 0))

    # ---- steps (south) + subtle weathering ------------------------------
    _steps(parent, pid + "_steps", (0, YF - 0.55, 0), (1, 0, 0), (0, -1, 0),
           x0 + 2.5, x1 - 2.5, 0.55, 0.3, 2, m["granite_polished"])
    Pf, uf, Lf, nf = kit.wall_frame((x0 + 1.8, y0, x1 - 1.8, y1 - 0.6), "NY")
    _streaks(parent, pid, Pf + nf * 0.62, uf, nf,
             [3.0, 8.0, 13.5, 19.0, 24.0, 29.5, 34.0], z_base,
             [1.4, 2.0, 1.1, 1.8])

    stats["objects"] = len(parent.children)
    stats["id"] = pid
    return stats



# ============================================================================
# 3) METRO — Moscow-metro pylon-hall vestibule
#    block rect [24,-70,46,-62], faces PY, h 9 m
# ============================================================================
def build_metro(parent, block, rng, ctx):
    """Moscow-metro pylon-hall surface vestibule (Round-4 rebuild).

    Faces NX.  A processional OPEN FRONT: wide granite steps, four front marble
    pylons carrying the entablature, a bronze double door open in the middle,
    a deep marble pylon hall behind (coffered ceiling, three chandeliers, warm
    glow), mosaic + bronze doors on the far end wall, «МЕТРО» frieze and the big
    «М» integrated in the parapet.
    """
    m = _mats()
    wm = _wm(ctx)
    pid = block["id"]
    x0, y0, x1, y1 = block["rect"]
    H = float(block.get("height", 9.0))
    stats = {"windows": 0, "lit": 0}
    marble = m["marble_white"]
    polish = m["granite_polished"]
    bronze = m["bronze"]
    gold = m["gold"]

    bx0, bx1 = x0 + 1.6, x1 - 0.6           # hall 139.6 .. 157.4
    by0, by1 = y0 + 0.7, y1 - 0.7           # hall 46.7 .. 53.3
    cy = (by0 + by1) / 2.0
    z_floor = 0.55
    z_hall = 7.0
    z_roof = 7.9
    z_par = 8.5
    sign_h = H                              # 9.0

    # ---- granite plinth + wide processional steps -----------------------
    kit.box_mm(parent, pid + "_plinth", bx0 - 0.35, by0 - 0.45, 0.0, bx1 + 0.25,
               by1 + 0.45, z_floor, polish, bevel=0.03)
    _stairs(parent, pid + "_steps", (bx0 - 0.35, 0, 0), (0, 1, 0), (-1, 0, 0),
            by0 - 0.45, by1 + 0.45, 1.35, 0.0, z_floor, 4, m["step"])
    kit.box_mm(parent, pid + "_floor", bx0, by0, z_floor, bx1, by1,
               z_floor + 0.06, m["granite_polished"], bevel=0.0)

    # ---- front colonnade: 4 marble pylons + entablature -----------------
    front_x = bx0 + 0.85
    py_list = [by0 + 0.75, cy - 1.05, cy + 1.05, by1 - 0.75]
    for i, yy in enumerate(py_list):
        kit.box(parent, f"{pid}_fpyl{i}", (front_x, yy, z_floor + 3.2),
                (1.15, 1.15, 6.4), marble, bevel=0.02)
        kit.box(parent, f"{pid}_fpylb{i}", (front_x, yy, z_floor + 0.14),
                (1.4, 1.4, 0.28), m["granite_dark"])
        kit.box(parent, f"{pid}_fpylk{i}", (front_x, yy, z_hall - 0.28),
                (1.5, 1.5, 0.44), m["bronze"], bevel=0.02)
        for side in (-1, 1):
            kit.box(parent, f"{pid}_fst{i}{side}",
                    (front_x - 0.6, yy + side * 0.2, z_floor + 3.3),
                    (0.06, 0.22, 5.4), m["bronze_dark"])
    # architrave + frieze + cornice over the colonnade (full width)
    kit.box_mm(parent, pid + "_arch", bx0 - 0.45, by0 - 0.35, z_hall,
               bx0 + 2.1, by1 + 0.35, z_hall + 0.35, marble, bevel=0.02)
    kit.box_mm(parent, pid + "_frieze", bx0 - 0.42, by0 - 0.32, z_hall + 0.35,
               bx0 + 2.05, by1 + 0.32, z_roof - 0.1, polish, bevel=0.02)
    _text(parent, pid + "_metro", "МЕТРО", (bx0 - 0.47, cy, z_hall + 0.7), 0.5,
          _solid("LM_METRO_SIGN2", "#D9B96A", rough=0.4, metallic=0.5,
                 emission="#D9B96A", es=0.5), (-1, 0, 0))
    # big М integrated in the parapet
    sign_mat = _solid("LM_METRO_SIGN", "#D9B96A", rough=0.35, metallic=0.55,
                      emission="#D9B96A", es=0.55)
    kit.box_mm(parent, pid + "_msignframe", bx0 - 0.55, cy - 2.45, z_par - 0.02,
               bx0 - 0.26, cy + 2.45, sign_h - 0.02, bronze, bevel=0.02)
    kit.box_mm(parent, pid + "_msignplate", bx0 - 0.53, cy - 2.3, z_par + 0.04,
               bx0 - 0.22, cy + 2.3, sign_h - 0.04, m["red_cloth"], bevel=0.02)
    _text(parent, pid + "_Msign", "М", (bx0 - 0.57, cy, z_par + 0.24), 1.02,
          sign_mat, (-1, 0, 0))
    # attic band + cornice + parapet
    _cornice(parent, pid + "_corn", bx0 + 0.8, cy, 2.9, by1 - by0 + 0.75,
             z_roof - 0.1, 0.35, m["stucco_cream"], 2, grow=0.16)
    kit.box_mm(parent, pid + "_attic", bx0 - 0.3, by0 - 0.28, z_roof,
               bx1 + 0.2, by1 + 0.28, z_par, m["stucco_cream"], bevel=0.02)
    kit.box_mm(parent, pid + "_roof", bx0 - 0.25, by0 - 0.25, z_roof - 0.05,
               bx1 + 0.2, by1 + 0.25, z_roof + 0.12, m["roof"])

    # ---- bronze entrance doors, open in the middle ----------------------
    kit.box_mm(parent, pid + "_doorframe", bx0 + 1.75, cy - 1.95, z_floor,
               bx0 + 1.95, cy + 1.95, z_floor + 4.9, bronze, bevel=0.02)
    for side in (-1, 1):
        kit.box(parent, f"{pid}_leaf{side}",
                (bx0 + 1.55, cy + side * 1.55, z_floor + 2.45),
                (0.12, 1.65, 4.7), bronze, rot=(0, 0, side * 0.9))
        kit.box(parent, f"{pid}_leafh{side}",
                (bx0 + 1.99, cy + side * 1.9, z_floor + 2.45),
                (0.08, 0.16, 0.5), gold)

    # ---- side walls with tall windows -----------------------------------
    for side, py in (("S", by0), ("N", by1)):
        face = "NY" if py < cy else "PY"
        if face == "PY":
            wall_rect = (bx0 + 2.0, py, bx1 - 0.2, py + 0.32)
        else:
            wall_rect = (bx0 + 2.0, py - 0.32, bx1 - 0.2, py)
        openings = []
        for k in range(3):
            s = (bx0 + 2.0 - wall_rect[0]) + 1.6 + k * 4.6
            openings.append((s, s + 2.0, 1.7, 5.4))
            openings[-1] = (s, s + 2.0, 1.7, 5.4)
        _panel_wall(parent, f"{pid}_sw{side}", wall_rect, face, z_floor, z_hall,
                    marble, openings=openings, thick=0.32, plinth_z=0.0)
        wf = kit.wall_frame(wall_rect, face)
        ext = wf[0] + wf[3] * 0.32
        for k in range(3):
            s = 1.6 + k * 4.6 + 1.0
            _door_unit(parent, f"{pid}_win{side}{k}", ext, wf[1], wf[3], s,
                       1.7, 2.0, 3.7, m["bronze_dark"], frame=gold,
                       glass_frac=1.0, wm=wm, lit=(k != 1))
            stats["windows"] += 1
        # exterior pilasters + cornice
        for k in range(4):
            c = wf[0] + wf[1] * (1.2 + k * 4.6)
            size = (0.4, 0.16, 5.9) if abs(wf[1].x) > 0.5 else (0.16, 0.4, 5.9)
            kit.box(parent, f"{pid}_pil{side}{k}",
                    (c.x, c.y, z_floor + 3.0), size, m["stucco_cream"])
        _cornice(parent, f"{pid}_sidecorn{side}", (bx0 + bx1) / 2,
                 (by0 + by1) / 2, bx1 - bx0 + 0.5, by1 - by0 + 0.5,
                 z_hall - 0.1, 0.35, m["stucco_cream"], 2, grow=0.2)

    # ---- deep pylon hall ------------------------------------------------
    for row, px in enumerate((bx0 + 6.2, bx0 + 11.0, bx0 + 15.0)):
        for col, py in enumerate((cy - 1.9, cy + 1.9)):
            kit.box(parent, f"{pid}_hpyl{row}{col}", (px, py, z_floor + 3.2),
                    (1.3, 1.3, 6.4), marble, bevel=0.02)
            kit.box(parent, f"{pid}_hpylb{row}{col}", (px, py, z_floor + 0.16),
                    (1.6, 1.6, 0.32), m["granite_dark"])
            kit.box(parent, f"{pid}_hpylk{row}{col}", (px, py, z_hall - 0.3),
                    (1.7, 1.7, 0.46), m["bronze"], bevel=0.02)
            for side in (-1, 1):
                kit.box(parent, f"{pid}_hst{row}{col}{side}",
                        (px, py + side * 0.68, z_floor + 3.4),
                        (0.85, 0.05, 5.2), m["bronze_dark"])
            kit.sphere(parent, f"{pid}_sconce{row}{col}",
                       (px, py + 0.72, z_floor + 4.6), 0.13, m["lamp_glow"],
                       segments=8, rings=4)
    # coffered ceiling with warm panels + chandeliers
    _coffered_ceiling(parent, pid + "_coff", bx0 + 2.1, by0 + 0.4, bx1 - 0.4,
                      by1 - 0.4, z_hall - 0.18, marble, marble,
                      m["warm_panel"], cols=6, rows=2)
    for k, cxc in enumerate((bx0 + 4.0, bx0 + 9.0, bx0 + 14.0)):
        _chandelier(parent, f"{pid}_chand{k}", (cxc, cy, 4.9), bx=1.0)
    # far end wall: mosaic + bronze doors + warm light
    kit.box_mm(parent, pid + "_endwall", bx1 - 0.4, by0, z_floor, bx1 - 0.05,
               by1, z_hall, marble)
    _door_unit(parent, pid + "_enddoor", (bx1 - 0.42, 0, 0), (0, 1, 0),
               (-1, 0, 0), cy, z_floor, 4.0, 4.2, bronze,
               frame=m["bronze_dark"])
    _mosaic_band(parent, pid + "_endmosaic", (bx1 - 0.46, cy, z_floor + 5.2),
                 (0, 1, 0), (-1, 0, 0), 4.4, 1.5, rng, key="mosaic_blue")
    kit.box(parent, pid + "_endmframe", (bx1 - 0.5, cy, z_floor + 5.2),
            (0.08, 4.8, 1.85), m["bronze"], bevel=0.02)
    _mosaic_band(parent, pid + "_endmosaic2", (bx1 - 0.55, cy, z_floor + 5.2),
                 (0, 1, 0), (-1, 0, 0), 4.15, 1.3, rng, key="mosaic_gold")
    warm = _solid("LM_METRO_GLOW", "#F6CF92", rough=0.7,
                  emission="#FFC87A", es=2.2)
    kit.quad(parent, pid + "_endglow", (bx1 - 0.58, cy, z_floor + 2.6),
             (0, 1, 0), (0, 0, 1), 3.4, 2.6, warm)
    # interior floor reflection strip
    kit.quad(parent, pid + "_floorstrip",
             ((bx0 + 2.2 + bx1 - 0.6) / 2, cy, z_floor + 0.07),
             (1, 0, 0), (0, 1, 0), bx1 - bx0 - 2.8, by1 - by0 - 1.0,
             m["granite_polished"])

    # ---- warm facade accent lights --------------------------------------
    for side in (-1, 1):
        kit.sphere(parent, f"{pid}_frontlamp{side}",
                   (front_x - 0.62, cy + side * 2.1, z_hall - 0.6), 0.14,
                   m["lamp_glow"], segments=8, rings=4)

    stats["objects"] = len(parent.children)
    stats["id"] = pid
    return stats



# ============================================================================
# 4) INSTITUTE_SINO — 1950s Sino-Soviet institute (U/H massing, tower, portico)
#    block rect [130,20,156,44], faces NX, h 24 m
# ============================================================================
def build_institute(parent, block, rng, ctx):
    """Sino-Soviet 1950s institute (Harbin HIT style) — Round-4 refinement.

    Symmetric U/H massing: central front block with a two-storey colonnade
    portico, two rear wings around a courtyard, tiered central tower with
    hipped copper roofs, lantern and antenna mast, ceramic tile bands and stone
    cornices, tall windows, weathered ochre stucco.
    """
    m = _mats()
    wm = _wm(ctx)
    pid = block["id"]
    x0, y0, x1, y1 = block["rect"]
    H = float(block.get("height", 24.0))
    stats = {"windows": 0, "lit": 0}
    wall = m["stucco_ochre"]
    trim = m["limestone"]
    band = m["tile_ceramic"]
    copper = m["copper_roof"]

    fx = x0 + 1.5                              # front facade plane (inset)
    ix0, ix1 = fx, x1 - 0.5
    iy0, iy1 = y0 + 0.5, y1 - 0.5
    cy = (iy0 + iy1) / 2.0
    front_h = 17.4
    wing_h = 15.2
    court_y0, court_y1 = iy0 + 7.4, iy1 - 7.4   # courtyard between the wings

    # ---- U massing: front block + two rear wings ------------------------
    fb = (ix0, iy0, ix0 + 10.0, iy1)
    wg_s = (ix0 + 10.0, iy0, ix1, court_y0)
    wg_n = (ix0 + 10.0, court_y1, ix1, iy1)
    kit.box_mm(parent, pid + "_frontblock", *fb[:2], 0.0, *fb[2:], front_h,
               wall, bevel=0.02)
    kit.box_mm(parent, pid + "_wings", *wg_s[:2], 0.0, *wg_s[2:], wing_h,
               wall, bevel=0.02)
    kit.box_mm(parent, pid + "_wingn", *wg_n[:2], 0.0, *wg_n[2:], wing_h,
               wall, bevel=0.02)

    # ---- facades (tall windows, ceramic bands) -------------------------
    st = kit.facade_wall(parent, pid + "_front", fb, "NX", 0.0, front_h,
                         wall, rng, wmats=wm, floors=4, floor_h=4.35,
                         win_w=1.45, win_h=2.55, spacing=3.35, margin=1.6,
                         lit_ratio=0.14, thickness=0.36, plinth_h=1.2,
                         plinth_mat=m["granite_dark"], joints=False,
                         ground_win_bottom=1.9)
    stats["windows"] += st["windows"]
    stats["lit"] += st["lit"]
    for face in ("NY", "PY"):
        st = kit.facade_wall(parent, pid + "_fb_" + face, fb, face, 0.0,
                             front_h, wall, rng, wmats=wm, floors=4,
                             floor_h=4.35, win_w=1.15, win_h=2.35, spacing=3.3,
                             margin=1.7, lit_ratio=0.1, thickness=0.36,
                             plinth_h=1.2, plinth_mat=m["granite_dark"],
                             joints=False, ground_win_bottom=1.9)
        stats["windows"] += st["windows"]
        stats["lit"] += st["lit"]
    for rect_, face, name in ((wg_s, "NY", "wsny"), (wg_s, "PY", "wspy"),
                              (wg_n, "NY", "wnny"), (wg_n, "PY", "wnpy")):
        st = kit.facade_wall(parent, pid + "_" + name, rect_, face, 0.0,
                             wing_h, wall, rng, wmats=wm, floors=3, floor_h=4.35,
                             win_w=1.4, win_h=2.4, spacing=3.25, margin=1.6,
                             lit_ratio=0.11, thickness=0.36, plinth_h=1.2,
                             plinth_mat=m["granite_dark"], joints=False,
                             ground_win_bottom=1.9)
        stats["windows"] += st["windows"]
        stats["lit"] += st["lit"]
    for wy0, wy1 in ((iy0, court_y0), (court_y1, iy1)):
        st = kit.facade_wall(parent, pid + "_we" + str(int(wy0)),
                             (ix0 + 10.0, wy0, ix1, wy1), "PX", 0.0, wing_h,
                             wall, rng, wmats=wm, floors=3, floor_h=4.35,
                             win_w=1.3, win_h=2.2, spacing=3.2, margin=1.6,
                             lit_ratio=0.1, thickness=0.36, plinth_h=1.2,
                             plinth_mat=m["granite_dark"], joints=False,
                             ground_win_bottom=1.9)
        stats["windows"] += st["windows"]
        stats["lit"] += st["lit"]

    # ---- ceramic + stone bands, quoins, cornices -----------------------
    for rect_, faces, h in ((fb, ("NX", "NY", "PY"), front_h),
                            (wg_s, ("NY", "PY"), wing_h),
                            (wg_n, ("NY", "PY"), wing_h)):
        for face in faces:
            Pf, uf, Lf, nf = kit.wall_frame(rect_, face)
            for zb in (1.55, 5.9, 10.25, 14.6):
                if zb > h - 1.0:
                    continue
                c = Pf + nf * 0.42 + uf * (Lf / 2) + Vector((0, 0, zb))
                size = (Lf - 0.1, 0.16, 0.32) if abs(uf.x) > 0.5 \
                    else (0.16, Lf - 0.1, 0.32)
                kit.box(parent, f"{pid}_band{face}{int(zb * 10)}", (c.x, c.y, c.z),
                        size, band)
            # top cornice per facade
            _cornice(parent, f"{pid}_corn{face}", (rect_[0] + rect_[2]) / 2,
                     (rect_[1] + rect_[3]) / 2, rect_[2] - rect_[0],
                     rect_[3] - rect_[1], h - 0.35, 0.55, trim, 3, grow=0.24)
    # corner quoins
    for px, py in ((ix0 - 0.02, iy0 + 0.02), (ix0 - 0.02, iy1 - 0.02),
                   (ix1 - 0.02, iy0 + 0.02), (ix1 - 0.02, iy1 - 0.02)):
        kit.box(parent, f"{pid}_quoin{int(px)}{int(py)}", (px, py, front_h / 2),
                (0.72, 0.72, front_h - 0.6), trim)

    # ---- portico: two-storey colonnade + entablature --------------------
    ncol = 6
    y_list = [cy - 5.9 + i * 2.36 for i in range(ncol)]
    for i, yy in enumerate(y_list):
        _column(parent, f"{pid}_pcol{i}", (x0 + 0.62, yy), 0.62, 1.15, 12.2,
                trim, flutes=24, depth=0.05, cap_mat=m["stone_rusticated"])
    kit.box_mm(parent, pid + "_arch", x0 + 0.02, y_list[0] - 0.9, 12.2,
               ix0 + 0.35, y_list[-1] + 0.9, 13.0, trim, bevel=0.03)
    kit.box_mm(parent, pid + "_entab", x0 - 0.1, y_list[0] - 1.1, 13.0,
               ix0 + 0.55, y_list[-1] + 1.1, 14.1, band, bevel=0.03)
    _cornice(parent, pid + "_pcorn", (x0 + ix0) / 2, cy, ix0 - x0 + 1.4,
             y_list[-1] - y_list[0] + 2.6, 14.1, 0.55, trim, 3, grow=0.16)
    # balustrade above the entablature
    for i in range(13):
        yy = y_list[0] - 0.7 + i * (y_list[-1] - y_list[0] + 1.4) / 12
        kit.box(parent, f"{pid}_bal{i}", (x0 + 0.45, yy, 14.85),
                (0.34, 0.34, 0.62), trim)
    kit.box_mm(parent, pid + "_balrail", x0 + 0.15, y_list[0] - 0.9, 15.1,
               x0 + 0.75, y_list[-1] + 0.9, 15.28, trim)
    # terrace + steps + bronze doors
    kit.box_mm(parent, pid + "_terr", x0 + 0.15, y_list[0] - 1.3, 0.0,
               ix0 + 0.3, y_list[-1] + 1.3, 1.15, m["granite_polished"],
               bevel=0.02)
    _stairs(parent, pid + "_steps", (x0 + 0.15, 0, 0), (0, 1, 0), (-1, 0, 0),
            y_list[0] - 1.2, y_list[-1] + 1.2, 0.95, 0.0, 1.15, 4, m["step"])
    P_door, u_door, L_door, n_door = kit.wall_frame(fb, "NX")
    _door_unit(parent, pid + "_door", (ix0 - 0.36, 0, 0), (0, 1, 0), (-1, 0, 0),
               cy, 1.15, 3.4, 4.0, m["bronze"], frame=m["bronze_dark"])
    for side in (-1, 1):
        kit.sphere(parent, f"{pid}_dlamp{side}", (ix0 - 0.45, cy + side * 2.6, 4.5),
                   0.16, m["lamp_glow"], segments=8, rings=4)
    # windows behind the colonnade (2 floors of tall glazing)
    for k in range(3):
        yy = cy - 4.6 + k * 4.6
        _door_unit(parent, f"{pid}_hallwin{k}", (ix0 - 0.36, 0, 0), (0, 1, 0),
                   (-1, 0, 0), yy, 5.6, 3.0, 3.2, m["bronze_dark"],
                   frame=m["bronze"], glass_frac=1.0, wm=wm, lit=(k == 1))
        stats["windows"] += 1

    # ---- central tower: tiered stages + copper roofs + lantern/mast ----
    tw = (ix0, cy - 4.6, ix0 + 9.0, cy + 4.6)
    tkx, tky = (tw[0] + tw[2]) / 2, (tw[1] + tw[3]) / 2
    st1 = 18.3
    st2 = 20.2
    st3 = 21.6
    kit.box_mm(parent, pid + "_tower1", tw[0], tw[1], 0.0, tw[2], tw[3], st1,
               wall, bevel=0.02)
    for i in range(2):
        yy = tw[1] + 1.4 + i * 3.2
        for k in range(2):
            kit.box(parent, f"{pid}_tw1w{i}{k}", (tw[0] - 0.03, yy + k * 1.5, 16.2),
                    (0.1, 1.0, 2.0), m["bronze_dark"])
    _cornice(parent, pid + "_tcorn1", tkx, tky, tw[2] - tw[0], tw[3] - tw[1],
             st1 - 0.3, 0.5, trim, 2, grow=0.3)
    w2 = 7.4
    kit.box_mm(parent, pid + "_tower2", tkx - w2 / 2, tky - w2 / 2, st1,
               tkx + w2 / 2, tky + w2 / 2, st2, wall, bevel=0.02)
    _cornice(parent, pid + "_tcorn2", tkx, tky, w2, w2, st2 - 0.28, 0.45, trim, 2,
             grow=0.26)
    w3 = 5.2
    kit.box_mm(parent, pid + "_tower3", tkx - w3 / 2, tky - w3 / 2, st2,
               tkx + w3 / 2, tky + w3 / 2, st3, wall, bevel=0.02)
    _cornice(parent, pid + "_tcorn3", tkx, tky, w3, w3, st3 - 0.25, 0.4, trim, 2,
             grow=0.22)
    _hip_roof(parent, pid + "_troof", tkx, tky, w3 + 0.7, w3 + 0.7, st3, 0.85,
              copper)
    # lantern + short antenna mast (total height stays inside the 24 m contract)
    kit.box_mm(parent, pid + "_lantern", tkx - 1.2, tky - 1.2, st3 + 0.85,
               tkx + 1.2, tky + 1.2, st3 + 1.55, wall, bevel=0.02)
    kit.cyl(parent, pid + "_mast", (tkx, tky, st3 + 1.97), 0.07, 0.85,
            m["steel"], verts=6)
    kit.cyl(parent, pid + "_mast2", (tkx, tky, st3 + 2.16), 0.03, 0.32,
            m["steel"], verts=5)
    # copper roofs over the front block and wings
    _hip_roof(parent, pid + "_froof", (fb[0] + fb[2]) / 2, (fb[1] + fb[3]) / 2,
              fb[2] - fb[0] + 0.8, fb[3] - fb[1] + 0.8, front_h, 2.0, copper)
    _hip_roof(parent, pid + "_wroof_s", (wg_s[0] + wg_s[2]) / 2,
              (wg_s[1] + wg_s[3]) / 2, wg_s[2] - wg_s[0] + 0.6,
              wg_s[3] - wg_s[1] + 0.6, wing_h, 1.7, copper)
    _hip_roof(parent, pid + "_wroof_n", (wg_n[0] + wg_n[2]) / 2,
              (wg_n[1] + wg_n[3]) / 2, wg_n[2] - wg_n[0] + 0.6,
              wg_n[3] - wg_n[1] + 0.6, wing_h, 1.7, copper)
    # roof vents above the cornices
    for i, (vx, vy) in enumerate(((ix0 + 3.0, iy0 + 3.0),
                                  (ix0 + 7.0, iy1 - 3.0),
                                  (ix0 + 12.0, court_y0 - 2.0))):
        kit.box(parent, f"{pid}_vent{i}", (vx, vy, front_h + 0.7),
                (0.9, 0.8, 0.9), m["roof_dark"])
    # flag on the tower
    kit.cyl(parent, pid + "_flagpole", (tkx + 2.6, tky - 2.6, st3 + 0.75), 0.05,
            1.9, m["steel"], verts=6)
    for i in range(3):
        kit.quad(parent, f"{pid}_flag{i}",
                 (tkx + 2.6 + 0.2 + i * 0.4, tky - 2.6, st3 + 1.55),
                 (1, 0, 0), (0, 0, 1), 0.4, 0.9, m["flag"])
    # light weathering streaks
    Pf, uf, Lf, nf = kit.wall_frame(fb, "NX")
    _streaks(parent, pid, Pf + nf * 0.36, uf, nf, [1.5, 4.2, 7.0, 9.6],
             1.3, [2.2, 1.6, 3.0, 2.4])

    _uv_project(parent)
    stats["objects"] = len(parent.children)
    stats["id"] = pid
    return stats



# ============================================================================
# 5) MUSEUM — Beijing-military-museum-style colonnade hall
#    block rect [130,62,158,86], faces NX, h 22 m + tower
# ============================================================================
def build_museum(parent, block, rng, ctx):
    """Socialist-realist military museum hall (Round-4 refinement).

    Deep central colonnade of square columns over a granite plinth, heavy
    entablature/dentils, bronze doors and relief panels, statue niches, central
    tiered tower with a gold star finial, long window-strip wings.
    """
    m = _mats()
    wm = _wm(ctx)
    pid = block["id"]
    x0, y0, x1, y1 = block["rect"]
    H = float(block.get("height", 22.0))
    stats = {"windows": 0, "lit": 0}
    stone = m["limestone"]
    stone_l = m["limestone_light"]
    granite = m["granite_dark"]
    bronze = m["bronze"]
    gold = m["gold"]

    fx = x0 + 3.3                              # recessed facade plane
    ix1 = x1 - 0.5
    iy0, iy1 = y0 + 0.5, y1 - 0.5
    cy = (iy0 + iy1) / 2.0
    hall_h = 14.0

    # ---- granite plinth + steps -----------------------------------------
    kit.box_mm(parent, pid + "_plinth", x0 + 0.2, iy0 - 0.15, 0.0, ix1 + 0.15,
               iy1 + 0.15, 0.65, granite, bevel=0.03)
    kit.box_mm(parent, pid + "_plinth2", fx - 0.35, iy0 + 0.35, 0.65, ix1 + 0.1,
               iy1 - 0.35, 1.15, m["granite_polished"], bevel=0.02)
    _stairs(parent, pid + "_steps", (fx - 0.35, 0, 0), (0, 1, 0), (-1, 0, 0),
            cy - 6.4, cy + 6.4, 2.0, 0.0, 1.15, 5, m["step"])
    for side in (-1, 1):
        kit.cyl(parent, f"{pid}_railP{side}", (x0 + 0.5, cy + side * 6.5, 1.25),
                0.06, 1.1, bronze, verts=8)
        kit.cyl_between(parent, f"{pid}_rail{side}",
                        (x0 + 0.5, cy + side * 6.5, 1.8),
                        (x0 + 1.9, cy + side * 6.5, 1.3), 0.06, bronze, verts=8)

    # ---- main hall + wings ----------------------------------------------
    kit.box_mm(parent, pid + "_hall", fx, iy0, 0.0, ix1, iy1, hall_h, stone,
               bevel=0.02)
    # window strip facades on the wings (front plane)
    for rect_, face, name, floors in (
            ((fx, iy0, ix1, cy - 6.6), "NX", "wingS", 3),
            ((fx, cy + 6.6, ix1, iy1), "NX", "wingN", 3)):
        st = kit.facade_wall(parent, pid + "_" + name, rect_, face, 0.0, hall_h,
                             stone, rng, wmats=wm, floors=floors,
                             floor_h=4.35, win_w=1.5, win_h=2.5, spacing=2.6,
                             margin=1.2, lit_ratio=0.12, thickness=0.36,
                             plinth_h=0.0, plinth_mat=granite, joints=False,
                             ground_win_bottom=1.35)
        stats["windows"] += st["windows"]
        stats["lit"] += st["lit"]
        # slim stone pilasters at the wing edges (between-bay strips would
        # otherwise hide the facade windows)
        Pf, uf, Lf, nf = kit.wall_frame(rect_, face)
        for k in range(2):
            s = 0.5 + k * (Lf - 1.0)
            c = Pf + uf * s + nf * 0.38
            kit.box(parent, f"{pid}_{name}_strip{k}", (c.x, c.y, hall_h / 2),
                    (0.3, 0.09, hall_h - 1.2), m["granite_grey"])
    # top cornice + parapet for hall & wings
    _cornice(parent, pid + "_cornice", (fx + ix1) / 2, (iy0 + iy1) / 2,
             ix1 - fx, iy1 - iy0, hall_h - 0.45, 0.7, stone_l, 3, grow=0.28)
    kit.box_mm(parent, pid + "_parapet", fx - 0.1, iy0 + 0.1, hall_h + 0.25,
               ix1 + 0.1, iy1 - 0.1, hall_h + 0.95, stone, bevel=0.02)
    # roof
    kit.box_mm(parent, pid + "_roof", fx + 0.1, iy0 + 0.1, hall_h - 0.1,
               ix1 - 0.1, iy1 - 0.1, hall_h + 0.3, m["roof_dark"])

    # ---- deep central colonnade -----------------------------------------
    col_x = x0 + 0.62
    ncol = 6
    y_list = [cy - 6.6 + i * 2.64 for i in range(ncol)]
    for i, yy in enumerate(y_list):
        kit.box(parent, f"{pid}_col{i}", (col_x, yy, 1.15 + 5.6),
                (1.24, 1.24, 11.2), stone_l, bevel=0.02)
        kit.box(parent, f"{pid}_colbase{i}", (col_x, yy, 1.3),
                (1.6, 1.6, 0.32), stone, bevel=0.02)
        kit.box(parent, f"{pid}_colcap{i}", (col_x, yy, 12.35),
                (1.66, 1.66, 0.4), stone, bevel=0.02)
        # fluted face strips on the front of each column
        for k in range(3):
            kit.box(parent, f"{pid}_colf{i}{k}", (col_x - 0.63, yy - 0.3 + k * 0.3, 6.9),
                    (0.06, 0.14, 9.6), stone)
    # architrave + entablature + cornice over the colonnade
    kit.box_mm(parent, pid + "_arch", x0 + 0.06, y_list[0] - 1.0, 12.75,
               fx + 0.2, y_list[-1] + 1.0, 13.55, stone_l, bevel=0.03)
    kit.box_mm(parent, pid + "_entab", x0 - 0.05, y_list[0] - 1.25, 13.55,
               fx + 0.45, y_list[-1] + 1.25, 14.7, stone, bevel=0.03)
    # dentils under the entablature
    n_d = 25
    for i in range(n_d):
        yy = y_list[0] - 0.8 + i * (y_list[-1] - y_list[0] + 1.6) / (n_d - 1)
        kit.box(parent, f"{pid}_dent{i}", (x0 + 0.35, yy, 13.35),
                (0.5, 0.3, 0.3), stone)
    _cornice(parent, pid + "_entcorn", (x0 + fx) / 2, cy,
             fx - x0 + 0.7, y_list[-1] - y_list[0] + 2.8, 14.7, 0.75, stone_l,
             3, grow=0.14)
    # frieze band (bronze relief strip) above the columns
    kit.box_mm(parent, pid + "_frieze", x0 + 0.02, y_list[0] - 0.8, 13.6,
               x0 + 0.18, y_list[-1] + 0.8, 14.55, bronze)

    # ---- recessed facade behind the colonnade: doors, panels, niches ----
    fw_rect = (fx, cy - 6.6, fx + 0.42, cy + 6.6)
    _panel_wall(parent, pid + "_frontwall", (fx, iy0, ix1, iy1), "NX", 1.15,
                hall_h, stone,
                openings=[(cy - 6.3 - iy0, cy - 1.4 - iy0, 1.15, 5.6),
                          (cy - 0.9 - iy0, cy + 0.9 - iy0, 1.15, 5.0),
                          (cy + 1.4 - iy0, cy + 6.3 - iy0, 1.15, 5.6)],
                thick=0.42, plinth_z=1.15)
    # three pairs of bronze doors
    for k, dy in enumerate((-3.85, 0.0, 3.85)):
        _door_unit(parent, f"{pid}_door{k}", (fx - 0.4, 0, 0), (0, 1, 0),
                   (-1, 0, 0), cy + dy, 1.15, 2.5, 4.1, bronze,
                   frame=m["stone_rusticated"])
        kit.box(parent, f"{pid}_doorsplit{k}", (fx - 0.46, cy + dy, 3.15),
                (0.06, 0.06, 4.0), m["granite_dark"])
        # bronze relief panel above each door
        kit.box(parent, f"{pid}_relief{k}", (fx - 0.44, cy + dy, 6.6),
                (0.1, 2.6, 1.2), m["bas_relief_bronze"], bevel=0.02)
        for r in range(3):
            kit.box(parent, f"{pid}_relief{k}{r}", (fx - 0.5, cy + dy - 0.7 + r * 0.7, 6.6),
                    (0.06, 0.5, 0.9), bronze)
    # statue niches between the doors
    for side in (-1, 1):
        ny = cy + side * 6.9
        kit.box(parent, f"{pid}_niche{side}", (fx - 0.45, ny, 3.3),
                (0.12, 1.8, 4.2), m["granite_grey"])
        kit.box(parent, f"{pid}_nichebase{side}", (fx - 0.5, ny, 1.6),
                (0.3, 2.0, 0.5), stone)
        # abstract bronze figure in the niche
        kit.box(parent, f"{pid}_fig{side}", (fx - 0.55, ny, 3.4),
                (0.28, 0.5, 1.9), m["bas_relief_bronze"], bevel=0.03)
        kit.sphere(parent, f"{pid}_fighead{side}", (fx - 0.55, ny, 4.55), 0.22,
                   m["bas_relief_bronze"], segments=10, rings=5)

    # ---- central block + tiered tower + gold star -----------------------
    tw = (fx, cy - 6.0, fx + 10.0, cy + 6.0)
    kit.box_mm(parent, pid + "_tower1", tw[0], tw[1], 0.0, tw[2], tw[3],
               16.3, stone, bevel=0.02)
    _cornice(parent, pid + "_tcorn1", fx + 5.0, cy, 10.0, 12.0, 15.95, 0.6,
             stone_l, 3, grow=0.3)
    kit.box_mm(parent, pid + "_tower2", fx + 1.4, cy - 3.6, 16.3, fx + 8.6,
               cy + 3.6, 18.4, stone, bevel=0.02)
    _cornice(parent, pid + "_tcorn2", fx + 5.0, cy, 7.2, 7.2, 18.1, 0.5,
             stone_l, 2, grow=0.26)
    kit.box_mm(parent, pid + "_tower3", fx + 2.9, cy - 2.1, 18.4, fx + 7.1,
               cy + 2.1, 20.1, stone, bevel=0.02)
    _cornice(parent, pid + "_tcorn3", fx + 5.0, cy, 4.2, 4.2, 19.8, 0.45,
             stone_l, 2, grow=0.22)
    # tower openings (vertical dark slots)
    for k in range(3):
        kit.box(parent, f"{pid}_tslot{k}", (fx - 0.03, cy - 2.6 + k * 2.6, 17.2),
                (0.08, 0.7, 1.6), m["bronze_dark"])
    # spire + gold star finial
    _loft(parent, pid + "_spire", [
        (20.1, fx + 5.0, cy, 1.5, 1.5, 0.0, 8, 0.0),
        (20.6, fx + 5.0, cy, 1.1, 1.1, 0.0, 8, 0.0),
        (21.1, fx + 5.0, cy, 0.5, 0.5, 0.0, 8, 0.0),
        (21.5, fx + 5.0, cy, 0.22, 0.22, 0.0, 8, 0.0),
    ], stone_l, seg=24)
    _star(parent, pid + "_star_y", (fx + 5.0, cy, 20.98), 0.95, 0.32, gold,
          axis="Y")
    _star(parent, pid + "_star_x", (fx + 5.0, cy, 20.98), 0.95, 0.32, gold,
          axis="X")
    kit.cyl(parent, pid + "_starpole", (fx + 5.0, cy, 21.62), 0.05, 0.4,
            gold, verts=6)

    # ---- flagpole at the front ------------------------------------------
    kit.cyl(parent, pid + "_flagpole", (x0 + 1.2, iy1 - 1.6, 8.6), 0.08, 17.2,
            m["steel"], verts=8)
    for i in range(3):
        kit.quad(parent, f"{pid}_flag{i}",
                 (x0 + 1.2 + 0.2 + i * 0.42, iy1 - 1.6, 16.4),
                 (1, 0, 0), (0, 0, 1), 0.42, 1.0, m["flag"])

    # ---- subtle weathering streaks --------------------------------------
    Pf, uf, Lf, nf = kit.wall_frame((fx, iy0, ix1, iy1), "NX")
    _streaks(parent, pid, Pf + nf * 0.05, uf, nf, [2.5, 7.0, 11.5, 16.5],
             1.3, [1.8, 2.6, 1.4, 2.0])

    _uv_project(parent)
    stats["objects"] = len(parent.children)
    stats["id"] = pid
    return stats



TYPES = {
    "statue_motherland": build_motherland,
    "motherland": build_motherland,
    "relief_wall": build_relief_wall,
    "brest_relief": build_relief_wall,
    "metro_pavilion": build_metro,
    "metro": build_metro,
    "institute_sino": build_institute,
    "hit": build_institute,
    "museum_colonnade": build_museum,
    "museum": build_museum,
}
