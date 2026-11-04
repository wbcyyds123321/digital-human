"""mod_civic.py — civic buildings for the Soviet blue-hour microdistrict.

Types (block["type"]):
  dk          Дом культуры / House of Culture   faces NY (south, plaza)
  school      Школа №3 / school + gym wing      faces NY
  kindergarten Детский сад №9                   faces PY (north, play yard)
  clinic      Поликлиника                       faces PY
  shop        Гастроном                         faces NY (avenue)

Everything is built in world coordinates from block["rect"] and parented to the
block empty.  Deterministic via kit.RNG(block["seed"]).  No cameras / lights /
ground are created here — the site + look passes own those.

Local helpers only (kit.py is owned by the Lead and must not be edited).
"""
from __future__ import annotations

import math

import bpy
from mathutils import Vector

import kit

# ============================================================================
# materials — wall sets use a world-space triplanar projection of the GPT
# concrete / paint textures, so they work on plain kit.box meshes (no UVs) and
# stay continuous across all wall pieces of a building.
# ============================================================================
_MCACHE: dict[str, object] = {}


def _solid(name, color, rough=0.88, metallic=0.0, emission=None, es=0.0, alpha=1.0):
    return kit.mat_solid(name, color, rough=rough, metallic=metallic,
                         emission=emission, emission_strength=es, alpha=alpha)


def _rgba_sockets(mix):
    fac = next(s for s in mix.inputs if s.name == "Factor" and s.type == "VALUE")
    a = next(s for s in mix.inputs if s.name == "A" and s.type == "RGBA")
    b = next(s for s in mix.inputs if s.name == "B" and s.type == "RGBA")
    out = next(s for s in mix.outputs if s.name == "Result" and s.type == "RGBA")
    return fac, a, b, out


def _tri_wall(name, filename, tint=None, tile=4.6, rough=0.9, tint_fac=0.72):
    """World-space triplanar-ish wall material from one of the GPT textures."""
    if name in _MCACHE:
        return _MCACHE[name]
    path = kit.find_image(filename)
    if not path:
        mat = kit.mat_solid(name + "_FB", tint or "#8D949A", rough=rough)
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

    tex_xz = proj(("X", "Z"))   # walls facing ±Y
    tex_yz = proj(("Y", "Z"))   # walls facing ±X
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


def _wall_materials():
    """Shared wall / trim palette for the civic buildings (cold desaturated)."""
    if "walls" in _MCACHE:
        return _MCACHE["walls"]
    pal = kit.PAL
    mats = {
        # DK — pale weathered cream plaster over concrete
        "dk": _tri_wall("CIV_DK_WALL", "tex_concrete_panel.png", tint="#A9A79C", tile=4.2),
        "dk_trim": _solid("CIV_DK_TRIM", "#9C9A90", rough=0.9),
        # school — pale grey-beige plaster with a faded blue stripe
        "school": _tri_wall("CIV_SCH_WALL", "tex_concrete_panel.png", tint="#A6A8A2", tile=5.0),
        "school_stripe": _solid("CIV_SCH_STRIPE", "#6E8698", rough=0.85),
        # kindergarten — faded pale blue-green paint
        "kg": _tri_wall("CIV_KG_WALL", "tex_panel_painted.png", tint="#9FB2AC", tile=3.6),
        "kg_trim": _solid("CIV_KG_TRIM", "#8FA6A0", rough=0.86),
        # clinic — pale blue-grey
        "clinic": _tri_wall("CIV_CLI_WALL", "tex_concrete_panel.png", tint="#9DA8B0", tile=4.4),
        "clinic_trim": _solid("CIV_CLI_TRIM", "#A9B0B2", rough=0.85),
        # shop — pale blue-grey panels
        "shop": _tri_wall("CIV_SHOP_WALL", "tex_concrete_panel.png", tint="#9AA4AC", tile=4.8),
        "shop_band": _solid("CIV_SHOP_BAND", "#6E5C55", rough=0.88),
        # shared
        "plinth": _solid("CIV_PLINTH", pal["plinth"], rough=0.95),
        "plinth_dark": _solid("CIV_PLINTH_DARK", pal["plinth_dark"], rough=0.95),
        "roof": _solid("CIV_ROOF", "#383E43", rough=0.94),
        "roof_dark": _solid("CIV_ROOF_DARK", "#2A2F33", rough=0.9),
        "metal": _solid("CIV_METAL", pal["metal_paint"], rough=0.6, metallic=0.45),
        "dark_metal": _solid("CIV_DARK_METAL", pal["metal_dark"], rough=0.55, metallic=0.5),
        "rust": _solid("CIV_RUST", pal["rust"], rough=0.8, metallic=0.2),
        "step": _solid("CIV_STEP", "#5A5E60", rough=0.9),
        "walk": _solid("CIV_WALK", pal["walk"], rough=0.9),
        "asphalt": _solid("CIV_ASPHALT", pal["asphalt"], rough=0.55),
        "glass_dark": _solid("CIV_GLASS_DARK", "#10161E", rough=0.16, metallic=0.25),
        "frame": _solid("CIV_FRAME", pal["frame_grey"], rough=0.7),
        "frame_white": _solid("CIV_FRAME_WHITE", pal["frame_white"], rough=0.72),
        "wood_door": _solid("CIV_WOOD_DOOR", "#4A4238", rough=0.82),
        "door_metal": _solid("CIV_DOOR_METAL", "#34393C", rough=0.55, metallic=0.5),
        "paper": _solid("CIV_PAPER", "#C8C4B4", rough=0.9),
        "sign_bg": _solid("CIV_SIGN_BG", "#2A3740", rough=0.65),
        "sign_fg": _solid("CIV_SIGN_FG", "#E8E2D2", rough=0.5,
                          emission="#E8E2D2", es=2.4),
        "sign_red": _solid("CIV_SIGN_RED", "#7E2A24", rough=0.7,
                           emission="#7E2A24", es=0.8),
        "teal": _solid("CIV_TEAL_RAIL", "#4C6360", rough=0.7, metallic=0.25),
        "flag": _solid("CIV_FLAG_RED", "#7C2A26", rough=0.85),
    }
    _MCACHE["walls"] = mats
    return mats


# ============================================================================
# geometry helpers
# ============================================================================

def _rot_for(n):
    """Euler for text / flat decals so they face along the outward normal n."""
    n = Vector(n)
    if n.y < -0.5:
        return (math.radians(90), 0, 0)
    if n.y > 0.5:
        return (math.radians(90), 0, math.pi)
    if n.x > 0.5:
        return (math.radians(90), 0, math.pi / 2)
    return (math.radians(90), 0, -math.pi / 2)


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


def _wall_box_at(parent, name, P0, u, n, s0, s1, za, zb, thick, mat):
    if s1 - s0 < 0.02 or zb - za < 0.02:
        return None
    mid = Vector(P0) + u * ((s0 + s1) / 2) + n * (thick / 2) + Vector((0, 0, (za + zb) / 2))
    size = (s1 - s0, thick, zb - za) if abs(u.x) > 0.5 else (thick, s1 - s0, zb - za)
    return kit.box(parent, name, (mid.x, mid.y, (za + zb) / 2), size, mat)


def _fix_xsills(parent, prefix, wm):
    """kit.window_unit sizes its sill box along world X, which is wrong on
    X-facing (PX/NX) facades: the sill ends up perpendicular to the wall as a
    long fin. Rebuild those sills with their long axis along the facade."""
    todo = []
    for o in list(parent.children):
        if o.type != "MESH" or not o.name.startswith(prefix):
            continue
        if not o.name.endswith("_sill"):
            continue
        pts = [o.matrix_world @ Vector(c) for c in o.bound_box]
        mnx = min(p.x for p in pts)
        mxx = max(p.x for p in pts)
        mny = min(p.y for p in pts)
        mxy = max(p.y for p in pts)
        if mxx - mnx <= 0.35:      # already thin along X -> Y-facing sill, fine
            continue
        cz = (min(p.z for p in pts) + max(p.z for p in pts)) / 2
        mat = o.data.materials[0] if o.data.materials else wm["sill"]
        todo.append(((mnx + mxx) / 2, (mny + mxy) / 2, cz, mxx - mnx, mat))
        bpy.data.objects.remove(o, do_unlink=True)
    for i, (cx, cy, cz, length, mat) in enumerate(todo):
        kit.box(parent, f"{prefix}_sillfix{i}", (cx, cy, cz), (0.15, length, 0.07), mat)


def _panel_wall(parent, name, rect, face, z0, z1, mat, openings=(), thick=0.35,
                plinth_z=0.0, plinth_mat=None):
    """One facade plane with rectangular openings (real holes) — the custom
    equivalent of kit.facade_wall for entrance / display / veranda zones.
    openings = [(s0, s1, za, zb), ...] in face-local coordinates."""
    P0, u, L, n = kit.wall_frame(rect, face)
    zs = {z0, z1}
    for (_s0, _s1, za, zb) in openings:
        zs.add(max(z0, min(z1, za)))
        zs.add(max(z0, min(z1, zb)))
    zs = sorted(zs)
    count = 0
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
            _wall_box_at(parent, f"{name}_{zi}_{k}", P0, u, n, a, b, za, zb, thick, m)
            count += 1
    return P0, u, L, n


def _glaze(parent, name, P0, u, n, s0, s1, z0, z1, rng, wm, bay=1.9,
           transoms=(), lit_ratio=0.14, frame=None, lit_boost=0.0, plane=0.0,
           dark_keys=("dark_a",), lit_keys=("warm_a", "warm_b", "dim")):
    """Glazed screen: mullion grid + textured glass panes (some warm lit)."""
    P0 = Vector(P0)
    u = Vector(u).normalized()
    n = Vector(n).normalized()
    v = Vector((0, 0, 1))
    frame = frame or wm["frame_grey"]
    nb = max(1, int(round((s1 - s0) / bay)))
    bw = (s1 - s0) / nb
    zs = [z0] + [z for z in transoms if z0 + 0.2 < z < z1 - 0.2] + [z1]
    base = P0 + n * plane
    # mullions
    for i in range(nb + 1):
        s = s0 + i * bw
        c = base + u * s + n * 0.05 + v * ((z0 + z1) / 2)
        size = (0.1, 0.16, z1 - z0 - 0.06) if abs(u.x) > 0.5 else (0.16, 0.1, z1 - z0 - 0.06)
        kit.box(parent, f"{name}_vm{i}", (c.x, c.y, c.z), size, frame)
    # transoms + rails
    for j, z in enumerate(zs[1:]):
        c = base + u * ((s0 + s1) / 2) + n * 0.05 + v * (z - 0.05)
        size = (s1 - s0, 0.16, 0.1) if abs(u.x) > 0.5 else (0.16, s1 - s0, 0.1)
        kit.box(parent, f"{name}_ht{j}", (c.x, c.y, c.z), size, frame)
    # panes
    lit_keys = [k for k in lit_keys if k in wm]
    lit_w = [dict((k, wv) for k, _f, _s, wv in kit.WIN_LIT)[k] for k in lit_keys]
    stats = {"panes": 0, "lit": 0}
    for i in range(nb):
        for j in range(len(zs) - 1):
            za, zb = zs[j], zs[j + 1]
            s = s0 + (i + 0.5) * bw
            zc = (za + zb) / 2
            pw = bw - 0.17
            ph = (zb - za) - 0.16
            if pw < 0.12 or ph < 0.12:
                continue
            lit = rng.random() < (lit_ratio + lit_boost)
            if lit:
                key = rng.wpick(lit_keys, lit_w)
                gmat = wm[key]
            else:
                gmat = wm[rng.pick(list(dark_keys))]
            c = base + u * s + n * 0.02 + v * zc
            uv = [(0.14, 0.12), (0.14, 0.85), (0.86, 0.85), (0.86, 0.12)]
            kit.quad(parent, f"{name}_g{i}_{j}", tuple(c), u, v, pw, ph, gmat, uv=uv)
            stats["panes"] += 1
            stats["lit"] += 1 if lit else 0
    return stats


def _custom_window(parent, name, P0, u, n, s, zc, w, h, rng, wmats,
                   lit_ratio=0.14, grille=False):
    return kit.window_unit(parent, name, P0, u, n, s, zc, w, h, rng,
                           lit_ratio=lit_ratio, grille=grille)


def _steps(parent, name, P0, u, n, s0, s1, depth, z_top, count, mat):
    """Flight of steps climbing from the ground toward the facade.
    P0 is the top-of-flight edge on the facade; steps project along +n."""
    P0 = Vector(P0)
    u = Vector(u).normalized()
    n = Vector(n).normalized()
    tread = depth / count
    for i in range(count):
        h = z_top * (count - i) / count
        c = P0 + u * ((s0 + s1) / 2) + n * (tread * (i + 0.5))
        size = (s1 - s0, tread + 0.02, h)
        if abs(u.x) < 0.5:
            size = (tread + 0.02, s1 - s0, h)
        kit.box(parent, f"{name}_st{i}", (c.x, c.y, h / 2), size, mat)


def _canopy(parent, name, rect_c, z, thick, mat, fascia=True):
    x0, y0, x1, y1 = rect_c
    kit.box_mm(parent, name, x0, y0, z, x1, y1, z + thick, mat, bevel=0.02)
    if fascia:
        kit.box_mm(parent, name + "_f", x0 - 0.06, y0 - 0.06, z - 0.06,
                   x1 + 0.06, y1 + 0.06, z + thick + 0.06,
                   _solid("CIV_CANOPY_FASCIA", "#7C8084", rough=0.85))


def _columns(parent, name, xs, y, z0, z1, sec, mat, cap_mat=None, cap=True):
    cap_mat = cap_mat or _solid("CIV_COL_CAP", "#8E8C84", rough=0.9)
    for i, x in enumerate(xs):
        h = z1 - z0
        kit.box(parent, f"{name}_c{i}", (x, y, z0 + h / 2), (sec, sec, h), mat, bevel=0.015)
        if cap:
            kit.box(parent, f"{name}_cap{i}", (x, y, z1 - 0.11), (sec + 0.22, sec + 0.22, 0.24),
                    cap_mat, bevel=0.01)
            kit.box(parent, f"{name}_base{i}", (x, y, z0 + 0.13), (sec + 0.18, sec + 0.18, 0.26),
                    cap_mat, bevel=0.01)


def _recessed_door(parent, name, P0, u, n, s, z0, w, h, rng, wm,
                   mat=None, glass_frac=0.0, lit=True, double=True):
    """Doorway on one facade, expressed on the EXTERIOR wall plane.
    P0 must already be the outer wall surface (use wall_frame(...) + n*thickness
    when the facade was built by kit.facade_wall)."""
    P0 = Vector(P0)
    u = Vector(u).normalized()
    n = Vector(n).normalized()
    v = Vector((0, 0, 1))
    mat = mat or _solid("CIV_DOOR_METAL2", "#3A3F42", rough=0.55, metallic=0.45)
    dark = _solid("CIV_DOOR_DARK", "#0A0D10", rough=1.0)
    trim = _solid("CIV_DOOR_TRIM", "#7A7C76", rough=0.85)
    base = P0 + u * s
    # door frame is mounted proud of the wall so it also reads on solid walls
    c = base + n * 0.01 + v * (z0 + h / 2)
    size = (w, 0.06, h) if abs(u.x) > 0.5 else (0.06, w, h)
    kit.box(parent, name + "_dark", (c.x, c.y, c.z), size, dark)
    # jambs / lintel slightly proud of the wall
    for side in (-1, 1):
        c = base + u * (side * (w / 2 + 0.07)) + n * 0.05 + v * (z0 + h / 2)
        size = (0.16, 0.1, h + 0.12) if abs(u.x) > 0.5 else (0.1, 0.16, h + 0.12)
        kit.box(parent, f"{name}_jamb{side}", (c.x, c.y, c.z), size, trim)
    c = base + n * 0.05 + v * (z0 + h + 0.08)
    size = (w + 0.3, 0.1, 0.16) if abs(u.x) > 0.5 else (0.1, w + 0.3, 0.16)
    kit.box(parent, name + "_lintel", (c.x, c.y, c.z), size, trim)
    # threshold
    c = base + n * 0.1 + v * (z0 + 0.02)
    size = (w + 0.1, 0.3, 0.06) if abs(u.x) > 0.5 else (0.3, w + 0.1, 0.06)
    kit.box(parent, name + "_thr", (c.x, c.y, c.z), size, trim)
    # leaves (mounted just proud of the wall plane)
    leaves = 2 if double else 1
    lw = w / leaves
    for li in range(leaves):
        lc = base + u * (-w / 2 + lw * (li + 0.5)) + n * 0.05 + v * (z0 + 1.02)
        size = (lw - 0.05, 0.06, 2.0) if abs(u.x) > 0.5 else (0.06, lw - 0.05, 2.0)
        kit.box(parent, f"{name}_leaf{li}", (lc.x, lc.y, lc.z), size, mat)
        if glass_frac > 0:
            gc = base + u * (-w / 2 + lw * (li + 0.5)) + n * 0.085 + v * (z0 + 1.55)
            gm = wm["warm_a"] if (lit and rng.chance(glass_frac)) else wm["dark_a"]
            kit.quad(parent, f"{name}_gl{li}", tuple(gc), u, v, lw * 0.6, 1.0, gm,
                     uv=[(0.2, 0.2), (0.2, 0.8), (0.8, 0.8), (0.8, 0.2)])
    # handle bar
    hc = base + u * (-0.1) + n * 0.1 + v * (z0 + 1.05)
    kit.box(parent, name + "_handle", (hc.x, hc.y, hc.z),
            (0.05, 0.05, 0.7) if abs(u.x) > 0.5 else (0.05, 0.05, 0.7), mat)
    if lit:
        lamp = _solid("CIV_DOOR_LAMP", "#FFD9A0", rough=0.3,
                      emission="#FFC57A", es=16.0)
        lc = base + n * 0.08 + v * (z0 + h + 0.42)
        kit.sphere(parent, name + "_lamp", (lc.x, lc.y, lc.z), 0.12, lamp)
        shade = _solid("CIV_LAMP_SHADE2", "#2E3336", rough=0.6)
        kit.cyl(parent, name + "_shade", (lc.x, lc.y, lc.z + 0.16), 0.22, 0.1, shade,
                verts=10)
    return base


def _poster_case(parent, name, center, u, n, w, h, rng, poster_key=None):
    c = Vector(center)
    u = Vector(u).normalized()
    n = Vector(n).normalized()
    v = Vector((0, 0, 1))
    frame = _solid("CIV_CASE_FRAME", "#565B5E", rough=0.65, metallic=0.3)
    size = (w + 0.14, 0.1, h + 0.14) if abs(u.x) > 0.5 else (0.1, w + 0.14, h + 0.14)
    kit.box(parent, name + "_frame", tuple(c), size, frame)
    back = _solid("CIV_CASE_BACK", "#2E3336", rough=0.9)
    size2 = (w, 0.06, h) if abs(u.x) > 0.5 else (0.06, w, h)
    kit.box(parent, name + "_back", tuple(c + n * 0.05), size2, back)
    pm = kit.poster_materials()
    key = poster_key or rng.pick(list(pm.keys()))
    kit.quad(parent, name + "_poster", tuple(c + n * 0.1), u, v, w - 0.12, h - 0.12,
             pm[key], uv=[(0, 0), (0, 1), (1, 1), (1, 0)])
    # weather strip of grime on the glass
    kit.quad(parent, name + "_dust", tuple(c + n * 0.105), u, v, w - 0.12, h * 0.14,
             _solid("CIV_CASE_DUST", "#3E4348", rough=0.9))
    return c


def _wall_lamp(parent, name, center, u, n):
    c = Vector(center)
    u = Vector(u).normalized()
    n = Vector(n).normalized()
    arm = _solid("CIV_LAMP_ARM", "#3A3E40", rough=0.6, metallic=0.5)
    kit.box(parent, name + "_arm", tuple(c + n * 0.18), (0.06, 0.36, 0.06), arm)
    glow = _solid("CIV_WALL_LAMP", "#FFD8A2", rough=0.3,
                  emission="#FFC57A", es=20.0)
    kit.sphere(parent, name + "_bulb", tuple(c + n * 0.42), 0.16, glow)
    shade = _solid("CIV_LAMP_SHADE", "#2E3336", rough=0.6)
    kit.cyl(parent, name + "_shade", tuple(c + n * 0.42 + Vector((0, 0, 0.17))),
            0.26, 0.12, shade, rot=(math.radians(0), 0, 0), verts=12)


def _mosaic(parent, name, center, u, n, w, h, rng, matset):
    """Abstract Soviet mosaic: one mesh, coloured tesserae with slight relief."""
    c = Vector(center)
    u = Vector(u).normalized()
    n = Vector(n).normalized()
    v = Vector((0, 0, 1))
    col_w, row_h = 0.28, 0.19
    cols = max(4, int(w / col_w))
    rows = max(3, int(h / row_h))
    tw = w / cols
    th = h / rows
    verts, faces, midx = [], [], []
    # palette indices: 0 grout, 1 deep blue, 2 pale blue, 3 red, 4 dark red,
    #                  5 gold, 6 ochre, 7 cream, 8 teal-green
    nmat = len(matset)

    def tile(i, j):
        x = (i + 0.5) / cols
        y = (j + 0.5) / rows
        # ---- sun with rays (centre right) ----
        sx, sy = 0.60, 0.55
        dx, dy = (x - sx) * 1.0, (y - sy) * 1.45
        rr = math.hypot(dx, dy)
        ang = math.atan2(dy, dx)
        if rr < 0.155:
            return 5 if rng.random() < 0.82 else 6
        if rr < 0.44:
            wedge = (ang + math.pi) / (2 * math.pi) * 12.0
            if (wedge % 1.0) < 0.45:
                return 6 if rng.random() < 0.72 else 5
        # ---- red banner: diagonal from upper left to the sun ----
        x0, y0, x1, y1 = 0.045, 0.97, 0.44, 0.40
        vx, vy = x1 - x0, y1 - y0
        ln2 = vx * vx + vy * vy
        t = ((x - x0) * vx + (y - y0) * vy) / ln2
        if 0.0 <= t <= 1.0:
            px, py = x0 + vx * t, y0 + vy * t
            if math.hypot(x - px, y - py) < 0.085:
                return 3 if rng.random() < 0.85 else 4
        # ---- second banner on the right, lower ----
        x0, y0, x1, y1 = 0.96, 0.90, 0.66, 0.30
        vx, vy = x1 - x0, y1 - y0
        t = ((x - x0) * vx + (y - y0) * vy) / (vx * vx + vy * vy)
        if 0.0 <= t <= 1.0:
            px, py = x0 + vx * t, y0 + vy * t
            if math.hypot(x - px, y - py) < 0.07:
                return 3 if rng.random() < 0.8 else 4
        # ---- doves ----
        for (px, py, rx, ry) in ((0.31, 0.83, 0.070, 0.075),
                                 (0.40, 0.70, 0.055, 0.060),
                                 (0.78, 0.78, 0.060, 0.065)):
            if ((x - px) / rx) ** 2 + ((y - py) / ry) ** 2 < 1.0:
                return 7
        # ---- worker / figure silhouettes ----
        for (px, py) in ((0.20, 0.36), (0.83, 0.36)):
            if abs(x - px) < 0.075 and abs(y - py) < 0.20:
                return 4 if rng.random() < 0.75 else 3
            if abs(x - px) < 0.045 and 0.18 < y < 0.26:
                return 4
        # ---- wheat ears in the lower corners ----
        for (px, flip) in ((0.07, 1), (0.93, -1)):
            if 0.04 < abs(x - px) < 0.13 and y < 0.42:
                if (int(y * 14) + int(abs(x - px) * 18)) % 3 == 0:
                    return 6
        # ---- sky / field background ----
        if y > 0.80:
            return 1 if rng.random() < 0.72 else 2
        if y < 0.14:
            return 8 if rng.random() < 0.35 else 1
        return 2 if rng.random() < 0.62 else 1

    for j in range(rows):
        for i in range(cols):
            x0 = (i / cols - 0.5) * w
            x1 = ((i + 1) / cols - 0.5) * w
            z0 = (j / rows - 0.5) * h
            z1 = ((j + 1) / rows - 0.5) * h
            d = 0.015 + rng.random() * 0.022
            base = c + n * d
            p00 = base + u * x0 + v * z0
            p10 = base + u * x1 + v * z0
            p11 = base + u * x1 + v * z1
            p01 = base + u * x0 + v * z1
            k = len(verts)
            verts.extend([tuple(p00), tuple(p10), tuple(p11), tuple(p01)])
            faces.append((k, k + 1, k + 2, k + 3))
            midx.append(tile(i, j) % nmat)
    me = bpy.data.meshes.new(name)
    me.from_pydata(verts, [], faces)
    me.update()
    for m in matset:
        me.materials.append(m)
    for poly, mi in zip(me.polygons, midx):
        poly.material_index = mi
    obj = bpy.data.objects.new(name, me)
    coll = parent.users_collection[0] if parent is not None and parent.users_collection else bpy.context.scene.collection
    coll.objects.link(obj)
    if parent is not None:
        obj.parent = parent
    return obj


def _mosaic_mats():
    if "mosaic" in _MCACHE:
        return _MCACHE["mosaic"]
    cols = ["#221D19", "#2C3A4C", "#4A5C70", "#6E2C24", "#4E1F1A",
            "#8A6A2E", "#9C7C3A", "#A89C82", "#3E544C"]
    mats = [_solid("CIV_MOS_" + str(i), c, rough=0.7) for i, c in enumerate(cols)]
    # subtle self-glow so the panel reads at night without looking neon
    for i, m in enumerate(mats):
        b = m.node_tree.nodes.get("Principled BSDF")
        try:
            b.inputs["Emission Color"].default_value = kit.srgb(cols[i])
            b.inputs["Emission Strength"].default_value = 0.16
        except Exception:  # noqa: BLE001
            pass
    _MCACHE["mosaic"] = mats
    return mats


def _sign(parent, name, center, u, n, w, h, text, bg=None, fg=None, glow=2.2,
          depth=0.14, size_frac=0.5):
    c = Vector(center)
    u = Vector(u).normalized()
    n = Vector(n).normalized()
    bgm = _solid("CIV_SIGNBG_" + str(bg or "#2A3740"), bg or "#2A3740", rough=0.65)
    fgm = _solid("CIV_SIGNFG_" + str(fg or "#E8E2D2"), fg or "#E8E2D2", rough=0.5,
                 emission=fg or "#E8E2D2", es=glow)
    size = (w, depth, h) if abs(u.x) > 0.5 else (depth, w, h)
    kit.box(parent, name + "_bg", (c.x, c.y, c.z), size, bgm)
    kit.text3d(parent, name + "_txt", text, tuple(c + n * (depth / 2 + 0.01)),
               h * size_frac, fgm, rot=_rot_for(n), extrude=0.02)
    return c


def _roof_sign(parent, name, text, center, size, rot_n=(0, -1, 0), mat=None):
    """Rooftop letter sign standing on a light steel frame."""
    c = Vector(center)
    n = Vector(rot_n).normalized()
    mat = mat or _solid("CIV_ROOFSIGN", "#D8D2C2", rough=0.55,
                        emission="#D8D2C2", es=0.9)
    kit.text3d(parent, name + "_txt", text, tuple(c), size, mat,
               rot=_rot_for(n), extrude=0.16)
    steel = _solid("CIV_ROOFSIGN_STEEL", "#3E4448", rough=0.6, metallic=0.5)
    # two horizontal rails behind the letters
    for i, dz in enumerate((-size * 0.35, size * 0.45)):
        kit.box(parent, f"{name}_rail{i}", tuple(c + Vector((0, 0.25, dz))),
                (size * 2.0, 0.1, 0.1), steel)
    for sx in (-size * 0.55, size * 0.55):
        kit.box(parent, f"{name}_post{sx:.0f}", tuple(c + Vector((sx, 0.25, -size * 0.6))),
                (0.12, 0.12, size * 1.4), steel)


def _chimney(parent, name, center, w, d, h, mat=None, cap=True):
    mat = mat or _solid("CIV_BRICK_CHIMNEY", "#6E5750", rough=0.92)
    c = Vector(center)
    kit.box(parent, name + "_shaft", (c.x, c.y, c.z + h / 2), (w, d, h), mat, bevel=0.02)
    if cap:
        dark = _solid("CIV_CHIMNEY_CAP", "#33383A", rough=0.85)
        kit.box(parent, name + "_cap", (c.x, c.y, c.z + h + 0.12),
                (w + 0.22, d + 0.22, 0.24), dark)
        kit.box(parent, name + "_band", (c.x, c.y, c.z + h * 0.72),
                (w + 0.12, d + 0.12, 0.12), _solid("CIV_CHIMNEY_BAND", "#54453F", rough=0.9))


def _vent_box(parent, name, center, w, d, h, rng=None, louvre=True):
    metal = _solid("CIV_VENT_METAL", "#66695F", rough=0.7, metallic=0.2)
    dark = _solid("CIV_VENT_DARK", "#2A2F31", rough=0.9)
    c = Vector(center)
    kit.box(parent, name + "_b", (c.x, c.y, c.z + h / 2), (w, d, h), metal, bevel=0.02)
    kit.box(parent, name + "_cap", (c.x, c.y, c.z + h + 0.06), (w + 0.14, d + 0.14, 0.12), dark)
    if louvre:
        for i in range(3):
            kit.box(parent, f"{name}_lv{i}", (c.x, c.y - d / 2 - 0.01,
                                             c.z + h * (0.3 + i * 0.2)),
                    (w * 0.72, 0.04, h * 0.1), dark)


def _drainpipes(parent, prefix, inner, z1, n_list=()):
    """Drainpipes on the facade corners (+ extra explicit positions)."""
    x0, y0, x1, y1 = inner
    pts = [(x0 - 0.12, y0 - 0.12), (x1 + 0.12, y0 - 0.12),
           (x0 - 0.12, y1 + 0.12), (x1 + 0.12, y1 + 0.12)]
    pts.extend(n_list)
    for i, (x, y) in enumerate(pts):
        kit.drainpipe(parent, f"{prefix}_drain{i}", x, y, 0.15, z1 - 0.15)


def _apron(parent, prefix, rect, mat, z=0.05):
    x0, y0, x1, y1 = rect
    kit.ground_quad(parent, f"{prefix}_apron", (x0, y0, x1, y1), z, mat, tile=3.0)


def _basement_grilles(parent, prefix, P0, u, n, s_list, z=0.55):
    grille = _solid("CIV_BASE_GRILLE", "#22272A", rough=0.7, metallic=0.3)
    P0 = Vector(P0)
    u = Vector(u).normalized()
    n = Vector(n).normalized()
    for i, s in enumerate(s_list):
        c = P0 + u * s + n * 0.36 + Vector((0, 0, z))
        size = (0.55, 0.06, 0.35) if abs(u.x) > 0.5 else (0.06, 0.55, 0.35)
        kit.box(parent, f"{prefix}_bg{i}", (c.x, c.y, c.z), size, grille)


def _interior_glow(parent, prefix, P0, u, n, s0, s1, z0, z1, rng,
                   warm="#E6A468", strength=1.1):
    """Lit shop / lobby interior: glow plane + shelf silhouettes (built on the
    interior side of the wall, i.e. along -n from the facade plane)."""
    P0 = Vector(P0)
    u = Vector(u).normalized()
    n = Vector(n).normalized()
    v = Vector((0, 0, 1))
    glow = _solid("CIV_INTERIOR_GLOW", warm, rough=0.9,
                  emission=warm, es=strength)
    back = P0 - n * 0.5
    kit.quad(parent, prefix + "_glow", tuple(back + u * ((s0 + s1) / 2) + v * ((z0 + z1) / 2)),
             u, v, s1 - s0, z1 - z0, glow)
    dark = _solid("CIV_SHELF", "#26241F", rough=0.9)
    goods = _solid("CIV_GOODS", "#4A4238", rough=0.9)
    for i, z in enumerate((z0 + (z1 - z0) * 0.30, z0 + (z1 - z0) * 0.66)):
        c = P0 - n * 0.3 + u * ((s0 + s1) / 2) + v * z
        size = (s1 - s0 - 0.4, 0.2, 0.09) if abs(u.x) > 0.5 else (0.2, s1 - s0 - 0.4, 0.09)
        kit.box(parent, f"{prefix}_shelf{i}", (c.x, c.y, c.z), size, dark)
        # goods silhouettes standing on the shelf
        nb = max(2, int((s1 - s0) / 0.9))
        for k in range(nb):
            t = (k + 0.5) / nb
            sc = P0 - n * 0.34 + u * (s0 + (s1 - s0) * t) + v * (z + 0.16)
            hh = 0.18 + 0.1 * ((k * 7 + i * 3) % 3)
            size2 = (0.34, 0.16, hh) if abs(u.x) > 0.5 else (0.16, 0.34, hh)
            kit.box(parent, f"{prefix}_good{i}_{k}", (sc.x, sc.y, sc.z), size2, goods)
    # floor + ceiling of the shallow shop box
    for k, z in enumerate((z0 + 0.02, z1 - 0.02)):
        c = P0 - n * 0.26 + u * ((s0 + s1) / 2) + v * z
        size = (s1 - s0, 0.5, 0.04) if abs(u.x) > 0.5 else (0.5, s1 - s0, 0.04)
        kit.box(parent, f"{prefix}_slab{k}", (c.x, c.y, c.z), size, dark)


def _low_fence(parent, prefix, p0, p1, height=1.1, gap=None):
    """Low kindergarten fence: concrete posts + 2 thin rails + sparse bars."""
    p0 = Vector(p0)
    p1 = Vector(p1)
    d = p1 - p0
    L = d.length
    if L < 0.4:
        return
    d.normalize()
    conc = _solid("CIV_FENCE_POST", "#6B6D66", rough=0.92)
    bar = _solid("CIV_FENCE_BAR", "#4A5A54", rough=0.7, metallic=0.3)
    n_post = max(1, int(L / 2.4))
    for i in range(n_post + 1):
        p = p0 + d * (L * i / n_post)
        if gap and gap[0] - 0.9 < i * L / n_post < gap[1] + 0.9:
            continue
        kit.box(parent, f"{prefix}_fp{i}", (p.x, p.y, height * 0.5 + 0.1),
                (0.22, 0.22, height + 0.2), conc, bevel=0.02)
    for k, hf in enumerate((0.42, 0.92)):
        if gap:
            # two runs, leaving the gate open
            for (a, b) in ((0.0, gap[0]), (gap[1], L)):
                if b - a < 0.4:
                    continue
                m0 = p0 + d * a
                m1 = p0 + d * b
                mid = (m0 + m1) / 2
                kit.box(parent, f"{prefix}_fr{k}{int(a)}", (mid.x, mid.y, height * hf),
                        (abs(m1.x - m0.x) + 0.04, abs(m1.y - m0.y) + 0.04, 0.05), bar)
        else:
            mid = (p0 + p1) / 2
            kit.box(parent, f"{prefix}_fr{k}", (mid.x, mid.y, height * hf),
                    (abs(d.x) * L + 0.04, abs(d.y) * L + 0.04, 0.05), bar)
    nbar = int(L / 0.55)
    for i in range(nbar):
        t = (i + 0.5) / nbar
        if gap and gap[0] < t * L < gap[1]:
            continue
        p = p0 + d * (t * L)
        kit.box(parent, f"{prefix}_fb{i}", (p.x, p.y, height * 0.62),
                (0.04, 0.04, height * 0.62), bar)


def _ext_stair(parent, prefix, x_face, y0, y1, z_top, mat):
    """External steel staircase against an east/west wall."""
    rail = _solid("CIV_STAIR_RAIL", "#4A5054", rough=0.6, metallic=0.4)
    step = _solid("CIV_STAIR_STEP", "#5E6164", rough=0.85, metallic=0.2)
    n = 9
    for i in range(n):
        z = z_top * (i + 1) / n
        y = y0 + (y1 - y0) * (i + 0.5) / n
        kit.box(parent, f"{prefix}_st{i}", (x_face + 0.62, y, z),
                (1.1, (y1 - y0) / n + 0.02, 0.06), step)
    # stringers
    kit.cyl_between(parent, prefix + "_str0", (x_face + 0.1, y0, 0.05),
                    (x_face + 0.1, y1, z_top + 0.05), 0.06, rail, verts=6)
    kit.cyl_between(parent, prefix + "_str1", (x_face + 1.14, y0, 0.05),
                    (x_face + 1.14, y1, z_top + 0.05), 0.06, rail, verts=6)
    for side in (0.1, 1.14):
        kit.cyl_between(parent, f"{prefix}_r{int(side * 10)}", (x_face + side, y0, 1.0),
                        (x_face + side, y1, z_top + 1.0), 0.035, rail, verts=6)
    for i in range(4):
        y = y0 + (y1 - y0) * (i + 0.5) / 4
        kit.box(parent, f"{prefix}_rp{i}", (x_face + 0.1, y, 0.55 + (z_top * (i + 0.5) / 4)),
                (0.05, 0.05, 1.0), rail)


def _flagpole(parent, prefix, pos, height=9.0):
    pole = _solid("CIV_FLAGPOLE", "#8A8E8C", rough=0.5, metallic=0.6)
    x, y, z = pos
    kit.cyl(parent, prefix + "_pole", (x, y, z + height / 2), 0.07, height, pole, verts=10)
    kit.sphere(parent, prefix + "_ball", (x, y, z + height + 0.08), 0.1, pole, segments=10, rings=5)
    flagm = _solid("CIV_FLAG", "#7C2A26", rough=0.88)
    # slightly waving flag made of 3 quads
    w, h = 1.5, 1.0
    for i in range(3):
        x0 = x + 0.08 + i * (w / 3)
        dx = 0.08 if i == 1 else 0.0
        kit.quad(parent, f"{prefix}_flag{i}",
                 (x0 + w / 6, y + dx, z + height - 0.55 - h / 2),
                 (1, 0, 0), (0, 0, 1), w / 3, h, flagm)


# ---- roof helpers ---------------------------------------------------------

def _roof_and_parapet(parent, prefix, inner, z, mat, parapet_h=0.72,
                      parapet_mat=None, coping=None):
    ex = 0.4
    rect = (inner[0] - ex, inner[1] - ex, inner[2] + ex, inner[3] + ex)
    kit.roof_flat(parent, prefix + "_roof", rect, z, _wall_materials()["roof"], thickness=0.24)
    kit.parapet(parent, prefix + "_parapet", rect, z, parapet_h, 0.22,
                parapet_mat or mat, coping or _wall_materials()["step"])


def _roof_clutter(parent, prefix, inner, z, rng, wall_mat, vents=((0.0, 0.0),),
                  antennas=2):
    for i, (fx, fy) in enumerate(vents):
        x = inner[0] + (inner[2] - inner[0]) * (0.2 + 0.6 * ((i * 0.37) % 1.0))
        y = inner[1] + (inner[3] - inner[1]) * (0.25 + 0.5 * ((i * 0.61) % 1.0))
        _vent_box(parent, f"{prefix}_rv{i}", (x, y, z + 0.12),
                  1.2, 0.9, 0.85 + 0.2 * (i % 2), rng)
    dark = _solid("CIV_ROOF_ANT", "#2A2E30", rough=0.8)
    for i in range(antennas):
        ax = inner[0] + (inner[2] - inner[0]) * (0.15 + 0.7 * ((i * 0.43 + 0.2) % 1.0))
        ay = inner[1] + (inner[3] - inner[1]) * (0.2 + 0.6 * ((i * 0.29 + 0.35) % 1.0))
        h = 1.8 + 1.6 * ((i * 0.7) % 1.0)
        kit.cyl(parent, f"{prefix}_ra{i}", (ax, ay, z + h / 2), 0.03, h, dark, verts=5)
        for k in range(3):
            kit.box(parent, f"{prefix}_rab{i}_{k}", (ax, ay, z + h * (0.55 + 0.13 * k)),
                    (0.7 + 0.2 * k, 0.02, 0.02), dark)


# ============================================================================
# HOUSE OF CULTURE  (Дом культуры)
# block DK: rect [-26, -34, 26, -12], faces NY
# ============================================================================

def build_dk(parent, block, rng, ctx):
    b = _wall_materials()
    wm = ctx.get("wmats") or kit.win_materials()
    x0, y0, x1, y1 = block["rect"]
    ins_front, ins_side = 1.6, 0.6
    inner = (x0 + ins_side, y0 + ins_front, x1 - ins_side, y1 - ins_side)
    ix0, iy0, ix1, iy1 = inner
    z_wing = 10.4
    z_mid = 11.8
    stats = {"windows": 0, "lit": 0}

    # ---- ground apron ------------------------------------------------------
    _apron(parent, block["id"], (x0 + 0.2, y0 + 0.2, x1 - 0.2, y1 - 0.2), b["walk"])

    # ---- facade walls ------------------------------------------------------
    P0, u, L, n = kit.wall_frame(inner, "NY")
    # front side wings (three tall window rows each)
    for side, (wx0, wx1) in (("W", (ix0, ix0 + 10.2)), ("E", (ix1 - 10.2, ix1))):
        rect_w = (wx0, iy0, wx1, iy1)
        st = kit.facade_wall(parent, f"{block['id']}_front_{side}", rect_w, "NY",
                            0.0, z_wing, b["dk"], rng, wmats=wm,
                            floors=3, floor_h=3.35, win_w=1.6, win_h=2.05,
                            spacing=3.1, margin=1.35, lit_ratio=0.17, thickness=0.36,
                            plinth_h=1.45, plinth_mat=b["plinth"], joints=True,
                            ground_win_bottom=1.7)
        stats["windows"] += st["windows"]
        stats["lit"] += st["lit"]
    # west / east / north facades
    for face, kw in (
        ("NX", dict(floors=3, floor_h=3.35, win_w=1.5, win_h=2.0, spacing=3.6,
                    margin=1.6, ground_skip=[10.6], lit_ratio=0.15)),
        ("PX", dict(floors=3, floor_h=3.35, win_w=1.5, win_h=2.0, spacing=3.6,
                    margin=1.6, ground_skip=[10.6], lit_ratio=0.15)),
        ("PY", dict(floors=3, floor_h=3.35, win_w=1.5, win_h=2.0, spacing=3.5,
                    margin=1.9, lit_ratio=0.14)),
    ):
        st = kit.facade_wall(parent, f"{block['id']}_{face}", inner, face,
                             0.0, z_wing, b["dk"], rng, wmats=wm,
                             thickness=0.36, plinth_h=1.45,
                             plinth_mat=b["plinth"], joints=True,
                             ground_win_bottom=1.7, **kw)
        stats["windows"] += st["windows"]
        stats["lit"] += st["lit"]

    # ---- central portico ---------------------------------------------------
    cx = (ix0 + ix1) / 2
    front_skin = iy0 - 0.36           # outer face of the front wings
    terr_y0 = front_skin - 1.05       # terrace front edge
    z_terr = 0.78
    # terrace slab + steps
    terr = (cx - 16.0, terr_y0, cx + 16.0, iy0 - 0.1)
    kit.box_mm(parent, block["id"] + "_terrace", terr[0], terr[1], 0.0,
               terr[2], terr[3], z_terr, b["plinth"], bevel=0.02)
    _steps(parent, block["id"] + "_dksteps", (0, terr[1], 0), (1, 0, 0), (0, -1, 0),
           terr[0], terr[2], 1.3, z_terr, 3, b["step"])
    # glazed lobby wall (recessed behind the colonnade)
    glass_y = front_skin - 0.18
    for s0, s1 in ((cx - 15.3, cx - 3.3), (cx + 3.3, cx + 15.3)):
        st = _glaze(parent, f"{block['id']}_lobby_{int(s0)}", (cx, glass_y, 0),
                    (1, 0, 0), (0, -1, 0), s0 - cx, s1 - cx, z_terr, 8.35, rng, wm,
                    bay=2.5, transoms=(3.95, 6.1), lit_ratio=0.2,
                    frame=wm["frame_grey"], plane=0.0)
        stats["windows"] += st["panes"]
        stats["lit"] += st["lit"]
    # dark lobby backdrop
    kit.box_mm(parent, block["id"] + "_lobby_back", cx - 15.4, iy0 + 1.0, 0.2,
               cx + 15.4, iy0 + 1.6, 8.4,
               _solid("CIV_LOBBY_DARK", "#15181B", rough=1.0))
    # lobby doors
    _recessed_door(parent, block["id"] + "_dkdoor", (cx, glass_y - 0.02, 0),
                   (1, 0, 0), (0, -1, 0), 0.0, z_terr, 3.6, 3.4, rng, wm,
                   mat=_solid("CIV_DK_DOOR", "#4A3A2C", rough=0.7),
                   glass_frac=0.9, lit=True, double=True)
    # six columns
    xs = [cx + off for off in (-13.6, -8.15, -2.7, 2.7, 8.15, 13.6)]
    _columns(parent, block["id"] + "_dkcol", xs, terr_y0 + 0.45, z_terr, 8.35,
             0.92, b["dk_trim"], cap_mat=_solid("CIV_DK_CAP", "#B0AFA4", rough=0.9))
    # entablature band above the columns
    kit.box_mm(parent, block["id"] + "_dkbeam", cx - 15.6, front_skin - 1.25, 8.35,
               cx + 15.6, front_skin + 0.4, 9.0, b["dk_trim"], bevel=0.02)
    # central raised block with the mosaic
    mid = (cx - 16.0, front_skin - 1.3, cx + 16.0, iy1 - 4.0)
    kit.box_mm(parent, block["id"] + "_dkmid", mid[0], mid[1], 9.0,
               mid[2], mid[3], z_mid, b["dk"], bevel=0.02)
    front_face = mid[1] - 0.02
    # mosaic concrete frame
    frm = b["dk_trim"]
    kit.box_mm(parent, block["id"] + "_mos_t", cx - 12.4, mid[1] - 0.14, 11.15,
               cx + 12.4, front_face, 11.52, frm)
    kit.box_mm(parent, block["id"] + "_mos_b", cx - 12.4, mid[1] - 0.14, 9.1,
               cx + 12.4, front_face, 9.47, frm)
    for side in (-1, 1):
        xx = cx + side * 12.4
        kit.box_mm(parent, block["id"] + f"_mos_s{side}", xx - 0.3 * (side > 0),
                   mid[1] - 0.14, 9.1, xx + 0.3 * (side < 0), front_face, 11.52, frm)
    _mosaic(parent, block["id"] + "_mosaic", (cx, mid[1] - 0.04, 10.3),
            (1, 0, 0), (0, -1, 0), 23.6, 1.68, rng, _mosaic_mats())
    # rooftop sign
    _roof_sign(parent, block["id"] + "_roofsign", "ДОМ КУЛЬТУРЫ",
               (cx, front_skin - 0.9, z_mid + 1.05), 1.35)
    # big vertical poster boards on the glazed lobby
    for side, xx in (("L", cx - 10.3), ("R", cx + 10.3)):
        _poster_case(parent, f"{block['id']}_poster{side}",
                     (xx, glass_y - 0.12, 4.2), (1, 0, 0), (0, -1, 0),
                     1.7, 3.5, rng)
    # paired wall lamps on the wing facades
    for xx in (ix0 + 2.4, ix1 - 2.4):
        _wall_lamp(parent, f"{block['id']}_wl{int(xx)}", (xx, front_skin, 6.4),
                   (1, 0, 0), (0, -1, 0))

    # ---- side entrances ----------------------------------------------------
    for face, side_sign in (("NX", -1), ("PX", 1)):
        Pf, uf, Lf, nf = kit.wall_frame(inner, face)
        face_pt = Pf + nf * 0.36
        _recessed_door(parent, f"{block['id']}_sdoor{face}", face_pt, uf, nf, 10.6,
                       0.15, 1.9, 2.7, rng, wm, glass_frac=0.6, double=False)
        # side canopy
        cc = face_pt + uf * 10.6 + nf * 0.62
        size = (2.6, 1.4, 0.18) if abs(uf.x) > 0.5 else (1.4, 2.6, 0.18)
        kit.box(parent, f"{block['id']}_scape{face}", (cc.x, cc.y, 3.05), size,
                b["dk_trim"], bevel=0.02)
        for sxx in (-1.0, 1.0):
            pc = face_pt + uf * (10.6 + sxx * 0.85) + nf * 1.18 + Vector((0, 0, 1.5))
            kit.box(parent, f"{block['id']}_ssup{face}{int(sxx)}", (pc.x, pc.y, pc.z),
                    (0.1, 0.1, 3.0), b["dark_metal"])
        # 2 small steps
        for i in range(2):
            sc = face_pt + uf * 10.6 + nf * (0.42 + i * 0.28) + Vector((0, 0, 0.06 + i * 0.09))
            size = (1.8, 0.34, 0.12 + i * 0.18) if abs(uf.x) > 0.5 else (0.34, 1.8, 0.12 + i * 0.18)
            kit.box(parent, f"{block['id']}_sstep{face}{i}", (sc.x, sc.y, sc.z), size, b["step"])
    # fire ladder on the east wall
    lad = b["dark_metal"]
    lx = ix1 + 0.4
    for yy in (iy1 - 1.6, iy1 - 2.0):
        kit.box(parent, f"{block['id']}_lad{int(yy * 10)}", (lx, yy, z_wing / 2),
                (0.06, 0.06, z_wing), lad)
    for i in range(9):
        kit.box(parent, f"{block['id']}_ladr{i}", (lx, iy1 - 1.8, 0.6 + i * 1.1),
                (0.05, 0.5, 0.05), lad)

    # ---- roof --------------------------------------------------------------
    _roof_and_parapet(parent, block["id"] + "_w", inner, z_wing, b["dk"])
    _roof_and_parapet(parent, block["id"] + "_m",
                      (mid[0], mid[1], mid[2], mid[3]), z_mid, b["dk"])
    _roof_clutter(parent, block["id"], inner, z_wing + 0.1, rng, b["dk"],
                  vents=((0, 0), (1, 1)), antennas=3)
    _chimney(parent, block["id"] + "_chim", (cx + 9.5, iy0 + 4.0, z_wing + 0.12),
             1.5, 1.2, 2.0)
    _vent_box(parent, block["id"] + "_mvent", (cx - 2.0, mid[1] + 2.2, z_mid + 0.12),
              2.6, 1.7, 1.1, rng)
    # basement grilles + drainpipes
    _basement_grilles(parent, block["id"], (ix0, iy0, 0), (1, 0, 0), (0, -1, 0),
                      [4.0, 8.0, 44.0, 48.0])
    _drainpipes(parent, block["id"], inner, z_wing,
                n_list=[(cx - 16.5, front_skin - 0.1), (cx + 16.5, front_skin - 0.1)])
    for face in ("NX", "PX"):
        _fix_xsills(parent, f"{block['id']}_{face}", wm)

    stats["objects"] = len(parent.children)
    stats["id"] = block["id"]
    return stats


# ============================================================================
# SCHOOL  (Школа №3) + gym wing
# block SCHOOL: rect [2, 8, 50, 32], faces NY
# ============================================================================

def build_school(parent, block, rng, ctx):
    b = _wall_materials()
    wm = ctx.get("wmats") or kit.win_materials()
    x0, y0, x1, y1 = block["rect"]
    inner = (x0 + 0.5, y0 + 1.5, x1 - 0.5, y1 - 0.5)
    ix0, iy0, ix1, iy1 = inner
    z1 = 10.5
    cx = (ix0 + ix1) / 2
    stats = {"windows": 0, "lit": 0}
    pid = block["id"]

    _apron(parent, pid, (x0 + 0.2, y0 + 0.2, x1 - 0.2, y1 - 0.2), b["walk"])

    # ---- main facade -------------------------------------------------------
    doors_s = [26.0 - ix0, 12.5 - ix0, 38.5 - ix0]
    st = kit.facade_wall(parent, pid + "_NY", inner, "NY", 0.0, z1, b["school"], rng,
                         wmats=wm, floors=3, floor_h=3.5, win_w=2.45, win_h=1.9,
                         spacing=3.5, margin=2.1, lit_ratio=0.15, thickness=0.36,
                         plinth_h=1.45, plinth_mat=b["plinth"], joints=True,
                         ground_win_bottom=1.75, ground_skip=doors_s)
    stats["windows"] += st["windows"]
    stats["lit"] += st["lit"]
    # other facades
    for face in ("NX", "PY", "PX"):
        st = kit.facade_wall(parent, pid + "_" + face, inner, face, 0.0, z1,
                             b["school"], rng, wmats=wm, floors=3, floor_h=3.5,
                             win_w=2.4, win_h=1.9, spacing=3.9, margin=2.2,
                             lit_ratio=0.13, thickness=0.36, plinth_h=1.45,
                             plinth_mat=b["plinth"], joints=True, ground_win_bottom=1.75)
        stats["windows"] += st["windows"]
        stats["lit"] += st["lit"]

    # faded colour stripe between ground and first floor (all facades)
    def _stripe(face, za, zb, mat):
        Pf, uf, Lf, nf = kit.wall_frame(inner, face)
        c = Pf + nf * 0.4 + uf * (Lf / 2) + Vector((0, 0, (za + zb) / 2))
        size = (Lf - 0.12, 0.08, zb - za) if abs(uf.x) > 0.5 else (0.08, Lf - 0.12, zb - za)
        kit.box(parent, f"{pid}_stripe_{face}", (c.x, c.y, c.z), size, mat)

    for face in ("NY", "PY", "NX", "PX"):
        _stripe(face, 3.95, 4.4, b["school_stripe"])

    # ---- entrance portico --------------------------------------------------
    port_cx = cx
    terr_top = 0.45
    skin = iy0 - 0.36                 # outer face of the front wall
    terr_y0 = skin - 1.5
    terr_rect = (port_cx - 5.5, terr_y0, port_cx + 5.5, iy0 - 0.1)
    kit.box_mm(parent, pid + "_terr", terr_rect[0], terr_rect[1], 0.0,
               terr_rect[2], terr_rect[3], terr_top, b["plinth"], bevel=0.02)
    _steps(parent, pid + "_steps", (0, terr_y0, 0), (1, 0, 0), (0, -1, 0),
           terr_rect[0], terr_rect[2], 0.75, terr_top, 3, b["step"])
    xs = [port_cx - 4.3, port_cx - 1.45, port_cx + 1.45, port_cx + 4.3]
    _columns(parent, pid + "_pcol", xs, terr_y0 + 0.62, terr_top, 4.55,
             0.55, b["school"], cap_mat=b["clinic_trim"])
    # canopy slab + fascia
    kit.box_mm(parent, pid + "_canopy", port_cx - 5.2, iy0 - 2.2, 4.55,
               port_cx + 5.2, iy0 + 0.6, 4.95, b["school"], bevel=0.03)
    kit.box_mm(parent, pid + "_canopy_f", port_cx - 5.3, iy0 - 2.3, 4.5,
               port_cx + 5.3, iy0 + 0.7, 5.02, b["clinic_trim"])
    _recessed_door(parent, pid + "_maindoor", (port_cx, iy0 - 0.36, 0),
                   (1, 0, 0), (0, -1, 0), 0.0, terr_top, 3.0, 2.9, rng, wm,
                   mat=_solid("CIV_SCH_DOOR", "#3E3A32", rough=0.6),
                   glass_frac=0.9, lit=True, double=True)
    # sign
    _sign(parent, pid + "_sign", (port_cx, skin - 0.02, 6.1), (1, 0, 0), (0, -1, 0),
          7.0, 1.05, "ШКОЛА  №3", bg="#2A3740", fg="#E8E2D2", glow=2.6, size_frac=0.62)
    # second door + steps (west end)
    _recessed_door(parent, pid + "_sdoor", (12.5, iy0 - 0.36, 0), (1, 0, 0),
                   (0, -1, 0), 0.0, 0.0, 1.9, 2.7, rng, wm, glass_frac=0.4, double=False)
    kit.box_mm(parent, pid + "_sdoorcan", 11.0, iy0 - 1.35, 2.95, 14.0, iy0 - 0.4, 3.12,
               b["clinic_trim"])
    _steps(parent, pid + "_ssteps", (0, iy0 - 1.3, 0), (1, 0, 0), (0, -1, 0),
           11.2, 13.8, 0.6, 0.3, 2, b["step"])
    # east service door
    _recessed_door(parent, pid + "_edoor", (38.5, iy0 - 0.36, 0), (1, 0, 0),
                   (0, -1, 0), 0.0, 0.0, 1.9, 2.7, rng, wm, glass_frac=0.3, double=False)
    kit.box_mm(parent, pid + "_edoorcan", 37.0, iy0 - 1.3, 2.95, 40.0, iy0 - 0.4, 3.12,
               b["clinic_trim"])

    # ---- flagpole ----------------------------------------------------------
    _flagpole(parent, pid + "_flag", (ix0 + 4.0, iy0 - 2.6, 0.0), 9.6)

    # ---- gym wing (east end; kept clear of the NE_DRIVE sidewalk at x>=57) --
    gym = (49.2, 16.2, 56.3, 31.6)
    z_gym = 8.4
    gx0, gy0, gx1, gy1 = gym
    st = kit.facade_wall(parent, pid + "_gym_NY", gym, "NY", 0.0, z_gym, b["school"],
                         rng, wmats=wm, floors=2, floor_h=4.05, win_w=1.55,
                         win_h=3.0, spacing=2.7, margin=1.5, lit_ratio=0.16,
                         thickness=0.36, plinth_h=1.45, plinth_mat=b["plinth"],
                         joints=True, ground_win_bottom=1.55)
    stats["windows"] += st["windows"]
    stats["lit"] += st["lit"]
    for face in ("PX", "PY"):
        st = kit.facade_wall(parent, pid + "_gym_" + face, gym, face, 0.0, z_gym,
                             b["school"], rng, wmats=wm, floors=2, floor_h=4.05,
                             win_w=1.95, win_h=1.6, spacing=3.1, margin=1.6,
                             lit_ratio=0.12, thickness=0.36, plinth_h=1.45,
                             plinth_mat=b["plinth"], joints=True, ground_win_bottom=1.75)
        stats["windows"] += st["windows"]
        stats["lit"] += st["lit"]
    # gym roof + parapet + vents + door
    _roof_and_parapet(parent, pid + "_gym", gym, z_gym, b["school"])
    _vent_box(parent, pid + "_gymvent", (gx0 + 2.4, gy0 + 3.0, z_gym + 0.12),
              1.1, 1.1, 1.5, rng)
    _recessed_door(parent, pid + "_gymdoor", (gx1 - 2.2, gy0 - 0.36, 0),
                   (1, 0, 0), (0, -1, 0), 0.0, 0.0, 1.8, 2.6, rng, wm,
                   glass_frac=0.5, double=False)
    kit.box_mm(parent, pid + "_gymcan", gx1 - 3.9, gy0 - 1.4, 2.9, gx1 - 0.5, gy0 + 0.2, 3.05,
               b["clinic_trim"])

    # ---- roof --------------------------------------------------------------
    _roof_and_parapet(parent, pid + "_r", inner, z1, b["school"])
    _roof_clutter(parent, pid, inner, z1 + 0.1, rng, b["school"],
                  vents=((0, 0), (1, 1), (2, 2)), antennas=4)
    _chimney(parent, pid + "_chim", (ix1 - 4.0, iy1 - 3.0, z1 + 0.12), 1.2, 1.1, 2.4)
    # parapet-top snow/grime strip on the front
    kit.box_mm(parent, pid + "_parapet_st", ix0, iy0 - 0.5, z1 + 0.78,
               ix1, iy0 - 0.28, z1 + 0.85, _solid("CIV_GRIME", "#5E625C", rough=0.95))

    # ---- details -----------------------------------------------------------
    _basement_grilles(parent, pid, (ix0, iy0, 0), (1, 0, 0), (0, -1, 0),
                      [5.0, 10.0, 31.0, 36.0, 41.0])
    _drainpipes(parent, pid, inner, z1,
                n_list=[(cx - 3.0, iy0 - 0.12), (cx + 3.0, iy0 - 0.12)])
    _drainpipes(parent, pid + "_g", gym, z_gym)
    for pre in (pid + "_NX", pid + "_PX", pid + "_gym_PX"):
        _fix_xsills(parent, pre, wm)

    stats["objects"] = len(parent.children)
    stats["id"] = pid
    return stats


# ============================================================================
# KINDERGARTEN  (Детский сад №9)
# block KG: rect [-36, 8, -6, 24], faces PY (north -> play yard)
# ============================================================================

def build_kg(parent, block, rng, ctx):
    b = _wall_materials()
    wm = ctx.get("wmats") or kit.win_materials()
    x0, y0, x1, y1 = block["rect"]
    inner = (x0 + 0.5, y0 + 0.5, x1 - 0.5, y1 - 1.6)
    ix0, iy0, ix1, iy1 = inner
    z1 = 6.9
    stats = {"windows": 0, "lit": 0}
    pid = block["id"]

    # L-shaped plan: west wing (full depth) + south-east block, veranda notch
    # in the north-east corner (x -18..-6.6, y 15..22.4).
    vx0, vx1 = -18.2, ix1
    vy0 = 15.0
    west = (ix0, iy0, vx0, iy1)
    south = (vx0, iy0, ix1, vy0)

    _apron(parent, pid, (x0 + 0.2, y0 + 0.2, x1 - 0.2, y1 - 0.2), b["walk"])

    # ---- facades -----------------------------------------------------------
    # west wing north facade (main entrance) + south facade
    entr_x = ix0 + 12.2
    st = kit.facade_wall(parent, pid + "_w_PY", west, "PY", 0.0, z1, b["kg"], rng,
                         wmats=wm, floors=2, floor_h=3.3, win_w=2.15, win_h=1.75,
                         spacing=3.1, margin=1.5, lit_ratio=0.17, thickness=0.36,
                         plinth_h=1.05, plinth_mat=b["plinth"], joints=True,
                         ground_win_bottom=1.15, ground_skip=[entr_x - ix0])
    stats["windows"] += st["windows"]
    stats["lit"] += st["lit"]
    st = kit.facade_wall(parent, pid + "_s_NY", south, "NY", 0.0, z1, b["kg"], rng,
                         wmats=wm, floors=2, floor_h=3.3, win_w=2.15, win_h=1.75,
                         spacing=3.2, margin=1.5, lit_ratio=0.16, thickness=0.36,
                         plinth_h=1.05, plinth_mat=b["plinth"], joints=True,
                         ground_win_bottom=1.15)
    stats["windows"] += st["windows"]
    stats["lit"] += st["lit"]
    # west end wall
    st = kit.facade_wall(parent, pid + "_w_NX", west, "NX", 0.0, z1, b["kg"], rng,
                         wmats=wm, floors=2, floor_h=3.3, win_w=1.7, win_h=1.65,
                         spacing=3.0, margin=1.6, lit_ratio=0.14, thickness=0.36,
                         plinth_h=1.05, plinth_mat=b["plinth"], joints=True,
                         ground_win_bottom=1.15)
    stats["windows"] += st["windows"]
    stats["lit"] += st["lit"]
    # rear (south of west wing)
    st = kit.facade_wall(parent, pid + "_w_S", west, "NY", 0.0, z1, b["kg"], rng,
                         wmats=wm, floors=2, floor_h=3.3, win_w=1.6, win_h=1.6,
                         spacing=3.3, margin=1.6, lit_ratio=0.1, thickness=0.36,
                         plinth_h=1.05, plinth_mat=b["plinth"], joints=True,
                         ground_win_bottom=1.5)
    stats["windows"] += st["windows"]
    stats["lit"] += st["lit"]
    st = kit.facade_wall(parent, pid + "_se_PX", south, "PX", 0.0, z1, b["kg"], rng,
                         wmats=wm, floors=2, floor_h=3.3, win_w=1.7, win_h=1.65,
                         spacing=3.0, margin=1.5, lit_ratio=0.12, thickness=0.36,
                         plinth_h=1.05, plinth_mat=b["plinth"], joints=True,
                         ground_win_bottom=1.15)
    stats["windows"] += st["windows"]
    stats["lit"] += st["lit"]

    # ---- veranda (2-storey covered terrace, open on the north) -------------
    # rear wall of the veranda (building wall facing north)
    v_rect_back = (vx0, vy0 - 0.9, ix1, vy0)
    st = kit.facade_wall(parent, pid + "_v_back", v_rect_back, "PY", 0.0, z1,
                         b["kg"], rng, wmats=wm, floors=2, floor_h=3.3,
                         win_w=2.0, win_h=1.85, spacing=2.9, margin=1.3,
                         lit_ratio=0.28, thickness=0.36, plinth_h=0.0,
                         joints=False, ground_win_bottom=0.85)
    stats["windows"] += st["windows"]
    stats["lit"] += st["lit"]
    # west wall of the veranda (east face of the west wing)
    v_rect_w = (vx0 - 0.9, vy0, vx0, iy1)
    st = kit.facade_wall(parent, pid + "_v_west", v_rect_w, "PX", 0.0, z1,
                         b["kg"], rng, wmats=wm, floors=2, floor_h=3.3,
                         win_w=1.5, win_h=1.8, spacing=2.7, margin=1.5,
                         lit_ratio=0.2, thickness=0.36, plinth_h=0.0,
                         joints=False, ground_win_bottom=0.85)
    stats["windows"] += st["windows"]
    stats["lit"] += st["lit"]
    # veranda east end wall (with the external stair landing)
    v_east = (ix1 - 0.4, vy0, ix1, iy1)
    st = kit.facade_wall(parent, pid + "_v_east", v_east, "PX", 0.0, z1,
                         b["kg"], rng, wmats=wm, floors=2, floor_h=3.3,
                         win_w=1.3, win_h=1.7, spacing=2.6, margin=1.2,
                         lit_ratio=0.22, thickness=0.36, plinth_h=0.0,
                         joints=False, ground_win_bottom=0.85,
                         ground_skip=[0.7])
    stats["windows"] += st["windows"]
    stats["lit"] += st["lit"]
    # veranda slabs + roof
    vx_a, vx_b = vx0, ix1 + 0.15
    vy_a, vy_b = vy0 - 0.3, iy1 + 0.55
    kit.box_mm(parent, pid + "_v_floor", vx_a, vy_a, 0.0, vx_b, vy_b, 0.14, b["walk"])
    kit.box_mm(parent, pid + "_v_f1", vx_a - 0.12, vy_a, 3.25, vx_b + 0.12, vy_b, 3.45,
               b["kg_trim"])
    kit.box_mm(parent, pid + "_v_roof", vx_a - 0.2, vy_a, z1 - 0.28, vx_b + 0.2, vy_b,
               z1, b["kg_trim"])
    # veranda columns (thin, 2 storeys)
    xs = [vx0 + 0.6 + i * 2.75 for i in range(4)]
    for i, xx in enumerate(xs):
        kit.box(parent, f"{pid}_vc{i}a", (xx, iy1 + 0.15, 1.65), (0.17, 0.17, 3.0),
                b["kg_trim"])
        kit.box(parent, f"{pid}_vc{i}b", (xx, iy1 + 0.15, z1 - 1.7), (0.15, 0.15, 3.0),
                b["kg_trim"])
    # blue-grey railings on the upper veranda edge
    rail_mat = b["teal"]
    zz = 3.45
    kit.box_mm(parent, pid + "_v_r0", vx0 + 0.2, iy1 + 0.05, zz + 0.92,
               ix1 - 0.1, iy1 + 0.25, zz + 1.0, rail_mat)
    kit.box_mm(parent, pid + "_v_r1", vx0 + 0.2, iy1 + 0.05, zz + 0.5,
               ix1 - 0.1, iy1 + 0.25, zz + 0.56, rail_mat)
    for i in range(8):
        xx = vx0 + 0.6 + i * ((ix1 - vx0 - 1.2) / 7)
        kit.box(parent, f"{pid}_v_rp{i}", (xx, iy1 + 0.15, zz + 0.52),
                (0.05, 0.05, 1.0), rail_mat)
    # ground-level veranda benches / planters / low table
    kit.bench(parent, pid + "_vbench", (vx0 + 5.2, iy1 - 0.4, 0.14), math.radians(0))
    kit.bench(parent, pid + "_vbench2", (vx0 + 8.8, iy1 - 0.4, 0.14), math.radians(0))
    kit.box_mm(parent, pid + "_vtable", vx0 + 6.6, iy1 - 0.5, 0.14,
               vx0 + 7.5, iy1 + 0.3, 0.72, b["wood_door"])
    planter = _solid("CIV_PLANTER", "#6E6A60", rough=0.9)
    kit.box_mm(parent, pid + "_vpl", vx0 + 2.2, iy1 - 0.5, 0.14,
               vx0 + 3.6, iy1 + 0.3, 0.55, planter)

    # ---- main entrance on the north facade ---------------------------------
    Pn, un, Ln, nn = kit.wall_frame(west, "PY")
    skin_n = iy1 + 0.36
    _recessed_door(parent, pid + "_edoor", Pn + nn * 0.36, un, nn, entr_x - ix0,
                   0.05, 2.0, 2.7, rng, wm,
                   mat=_solid("CIV_KG_DOOR", "#3E4A46", rough=0.7),
                   glass_frac=0.8, lit=True, double=False)
    # entrance terrace + steps
    kit.box_mm(parent, pid + "_eterr", entr_x - 2.6, iy1 + 0.1, 0.0,
               entr_x + 2.6, iy1 + 1.65, 0.3, b["plinth"], bevel=0.02)
    _steps(parent, pid + "_esteps", (0, iy1 + 1.6, 0), (1, 0, 0), (0, 1, 0),
           entr_x - 2.4, entr_x + 2.4, 0.6, 0.3, 2, b["step"])
    # canopy on two slim posts
    kit.box_mm(parent, pid + "_ecan", entr_x - 2.9, iy1 + 0.2, 3.05,
               entr_x + 2.9, iy1 + 2.1, 3.22, b["kg_trim"], bevel=0.02)
    for sx in (-2.5, 2.5):
        kit.box(parent, f"{pid}_ecp{int(sx)}", (entr_x + sx, iy1 + 1.85, 1.6),
                (0.12, 0.12, 3.1), b["kg_trim"])
    _sign(parent, pid + "_sign", (entr_x, skin_n - 0.02, 3.85), (1, 0, 0), (0, 1, 0),
          6.4, 0.9, "ДЕТСКИЙ САД №9", bg="#3A4A45", fg="#E4E6DC",
          glow=2.6, size_frac=0.62)
    _wall_lamp(parent, pid + "_elamp", (entr_x, skin_n, 2.75), (1, 0, 0), (0, 1, 0))

    # ---- low fence along the yard edge -------------------------------------
    gate_a = entr_x - ix0 - 1.2 - 2.0
    _low_fence(parent, pid + "_fence", (ix0 + 1.2, iy1 + 1.5), (ix1 - 1.0, iy1 + 1.5),
               height=1.05, gap=(gate_a, gate_a + 4.2))

    # ---- external steel stair on the east end ------------------------------
    _ext_stair(parent, pid, ix1 + 0.36, vy0 + 0.8, iy1 - 1.2, 3.45, b["dark_metal"])

    # ---- roofs -------------------------------------------------------------
    _roof_and_parapet(parent, pid + "_rw", west, z1, b["kg"], parapet_h=0.5)
    _roof_and_parapet(parent, pid + "_rs", south, z1, b["kg"], parapet_h=0.5)
    _roof_clutter(parent, pid, west, z1 + 0.1, rng, b["kg"],
                  vents=((0, 0), (1, 1), (2, 2)), antennas=3)
    _chimney(parent, pid + "_vent", (ix0 + 3.4, iy0 + 3.0, z1 + 0.1), 0.8, 0.8, 1.15)
    _vent_box(parent, pid + "_vent2", (vx0 - 3.0, iy0 + 2.5, z1 + 0.1), 1.0, 0.8, 0.8)
    _vent_box(parent, pid + "_vent3", (ix1 - 2.6, iy0 + 2.2, z1 + 0.1), 0.9, 0.7, 0.7)
    _basement_grilles(parent, pid, Pn, un, nn, [3.0, 17.5, 22.0])
    _drainpipes(parent, pid, inner, z1)
    for pre in (pid + "_w_NX", pid + "_se_PX", pid + "_v_east", pid + "_v_west"):
        _fix_xsills(parent, pre, wm)

    stats["objects"] = len(parent.children)
    stats["id"] = pid
    return stats


# ============================================================================
# CLINIC  (Поликлиника)
# block CLINIC: rect [-46, -16, -28, -4], faces PY
# ============================================================================

def build_clinic(parent, block, rng, ctx):
    b = _wall_materials()
    wm = ctx.get("wmats") or kit.win_materials()
    x0, y0, x1, y1 = block["rect"]
    inner = (x0 + 0.5, y0 + 0.5, x1 - 0.5, y1 - 1.6)
    ix0, iy0, ix1, iy1 = inner
    z1 = 7.4
    cx = (ix0 + ix1) / 2
    stats = {"windows": 0, "lit": 0}
    pid = block["id"]

    _apron(parent, pid, (x0 + 0.2, y0 + 0.2, x1 - 0.2, y1 - 0.2), b["walk"])

    entr_x = cx
    # north (front) facade
    st = kit.facade_wall(parent, pid + "_PY", inner, "PY", 0.0, z1, b["clinic"], rng,
                         wmats=wm, floors=2, floor_h=3.6, win_w=1.6, win_h=2.1,
                         spacing=2.55, margin=1.6, lit_ratio=0.15, thickness=0.36,
                         plinth_h=1.25, plinth_mat=b["plinth"], joints=True,
                         ground_win_bottom=1.3, ground_skip=[entr_x - ix0])
    stats["windows"] += st["windows"]
    stats["lit"] += st["lit"]
    for face, kw in (
        ("PX", dict(floors=2, floor_h=3.6, win_w=1.6, win_h=2.0, spacing=3.0,
                    margin=1.6)),
        ("NX", dict(floors=2, floor_h=3.6, win_w=1.6, win_h=2.0, spacing=3.0,
                    margin=1.6)),
        ("NY", dict(floors=2, floor_h=3.6, win_w=1.5, win_h=1.9, spacing=3.0,
                    margin=1.7)),
    ):
        st = kit.facade_wall(parent, pid + "_" + face, inner, face, 0.0, z1,
                             b["clinic"], rng, wmats=wm, lit_ratio=0.12,
                             thickness=0.36, plinth_h=1.25, plinth_mat=b["plinth"],
                             joints=True, ground_win_bottom=1.3, **kw)
        stats["windows"] += st["windows"]
        stats["lit"] += st["lit"]

    # ---- projecting stair tower (distinct silhouette, off-centre) ----------
    Pn, un, Ln, nn = kit.wall_frame(inner, "PY")
    skin = iy1 + 0.36
    tower = (ix0 + 0.8, iy1 - 1.2, ix0 + 4.8, iy1 + 0.55)
    kit.box_mm(parent, pid + "_tower", tower[0], tower[1], 0.0,
               tower[2], tower[3], 8.6, b["clinic"], bevel=0.02)
    tcx = (tower[0] + tower[2]) / 2
    # tall narrow stair window + concrete mullions
    dim_stair = _solid("CIV_STAIR_GLASS", "#22302C", rough=0.35, metallic=0.2,
                       emission="#8FA99A", es=0.5)
    kit.quad(parent, pid + "_tower_win", (tcx, tower[3] + 0.02, 5.0), (1, 0, 0),
             (0, 0, 1), 1.5, 4.1, dim_stair,
             uv=[(0.12, 0.08), (0.12, 0.9), (0.88, 0.9), (0.88, 0.08)])
    for i in range(4):
        kit.box(parent, f"{pid}_tbar{i}", (tcx, tower[3] + 0.05, 3.2 + i * 1.2),
                (1.6, 0.06, 0.08), wm["frame_grey"])
    kit.box(parent, f"{pid}_tbar_v", (tcx, tower[3] + 0.05, 5.0),
            (0.1, 0.06, 4.1), wm["frame_grey"])
    # vertical tile band flanking the window
    kit.box(parent, f"{pid}_tband_l", (tower[0] + 0.55, tower[3] + 0.03, 5.0),
            (0.7, 0.06, 8.2), b["clinic_trim"])
    kit.box(parent, f"{pid}_tband_r", (tower[2] - 0.55, tower[3] + 0.03, 5.0),
            (0.7, 0.06, 8.2), b["clinic_trim"])
    kit.box_mm(parent, pid + "_tower_par", tower[0] - 0.08, tower[1], 8.6,
               tower[2] + 0.08, tower[3] + 0.1, 9.02, b["clinic_trim"])
    # rooftop equipment box on the tower
    kit.box_mm(parent, pid + "_tower_box", tcx - 0.9, iy1 - 1.0, 9.02,
               tcx + 0.9, iy1 + 0.3, 9.5, b["roof_dark"])

    # ---- entrance canopy on slim columns + steps ---------------------------
    entr_x = cx
    _recessed_door(parent, pid + "_door", Pn + nn * 0.36, un, nn, entr_x - ix0,
                   0.36, 2.0, 2.8, rng, wm,
                   mat=_solid("CIV_CLI_DOOR", "#3C4448", rough=0.6),
                   glass_frac=0.9, lit=True, double=False)
    kit.box_mm(parent, pid + "_ecan", cx - 3.2, iy1 + 0.1, 3.25,
               cx + 3.2, iy1 + 1.3, 3.55, b["clinic"], bevel=0.02)
    kit.box_mm(parent, pid + "_ecan_f", cx - 3.3, iy1 + 0.05, 3.18,
               cx + 3.3, iy1 + 1.38, 3.62, b["clinic_trim"])
    for sx in (-2.7, 2.7):
        kit.box(parent, f"{pid}_ecol{int(sx)}", (cx + sx, iy1 + 1.12, 1.65),
                (0.26, 0.26, 3.3), b["clinic_trim"])
    kit.box_mm(parent, pid + "_eterr", cx - 3.0, iy1 + 0.05, 0.0,
               cx + 3.0, iy1 + 1.0, 0.36, b["plinth"], bevel=0.02)
    _steps(parent, pid + "_esteps", (0, iy1 + 1.0, 0), (1, 0, 0), (0, 1, 0),
           cx - 2.9, cx + 2.9, 0.6, 0.36, 3, b["step"])
    _sign(parent, pid + "_sign", (cx, skin - 0.02, 4.2), (1, 0, 0), (0, 1, 0),
          6.6, 0.95, "ПОЛИКЛИНИКА", bg="#2C3A44", fg="#E6E8E0", glow=2.6,
          size_frac=0.6)
    # poster case beside the door (clear of the tower)
    _poster_case(parent, pid + "_pcase", (ix0 + 6.6, skin + 0.02, 2.1),
                 (1, 0, 0), (0, 1, 0), 1.1, 1.7, rng)
    _wall_lamp(parent, pid + "_wl", (cx + 2.2, skin, 3.0), (1, 0, 0), (0, 1, 0))

    # ---- ambulance bay at the back -----------------------------------------
    Ps, us, Ls, ns = kit.wall_frame(inner, "NY")
    amb_x = ix1 - 3.2
    skin_s = iy0 - 0.36
    # wide opening: roller door + red cross plate
    _recessed_door(parent, pid + "_amb", (amb_x, skin_s, 0), (1, 0, 0), (0, -1, 0),
                   0.0, 0.0, 3.4, 2.75, rng, wm, mat=b["dark_metal"],
                   glass_frac=0.0, double=True)
    kit.box_mm(parent, pid + "_ambcan", amb_x - 2.2, skin_s - 1.0, 2.95,
               amb_x + 2.2, iy0 - 0.1, 3.12, b["clinic_trim"])
    # concrete ramp / approach
    kit.box_mm(parent, pid + "_ramp", amb_x - 1.9, iy0 - 1.45, 0.0,
               amb_x + 1.9, skin_s, 0.32, b["step"])
    kit.box_mm(parent, pid + "_ramp2", amb_x - 1.9, iy0 - 1.7, 0.0,
               amb_x + 1.9, iy0 - 1.45, 0.15, b["step"])
    cross = _solid("CIV_RED_CROSS", "#7E2E28", rough=0.7, emission="#7E2E28", es=0.6)
    kit.box(parent, pid + "_cross_v", (amb_x, skin_s - 0.04, 4.1), (0.34, 0.06, 1.0), cross)
    kit.box(parent, pid + "_cross_h", (amb_x, skin_s - 0.04, 4.1), (1.0, 0.06, 0.34), cross)

    # ---- roof --------------------------------------------------------------
    _roof_and_parapet(parent, pid, inner, z1, b["clinic"], parapet_h=0.65)
    _roof_clutter(parent, pid, inner, z1 + 0.1, rng, b["clinic"],
                  vents=((0, 0), (1, 1)), antennas=2)
    _chimney(parent, pid + "_chim", (ix0 + 2.6, iy0 + 2.2, z1 + 0.12), 0.9, 0.8, 1.9)
    _basement_grilles(parent, pid, Pn, un, nn, [2.6, 14.2])
    _drainpipes(parent, pid, inner, z1)
    for pre in (pid + "_PX", pid + "_NX"):
        _fix_xsills(parent, pre, wm)

    stats["objects"] = len(parent.children)
    stats["id"] = pid
    return stats


# ============================================================================
# SHOP  (Гастроном)
# block SHOP: rect [92, -68, 112, -56], faces NY (avenue)
# ============================================================================

def build_shop(parent, block, rng, ctx):
    b = _wall_materials()
    wm = ctx.get("wmats") or kit.win_materials()
    x0, y0, x1, y1 = block["rect"]
    inner = (x0 + 0.5, y0 + 1.2, x1 - 0.5, y1 - 0.5)
    ix0, iy0, ix1, iy1 = inner
    z_band = 4.2       # top of the shop-front storey
    z1 = 7.2
    cx = (ix0 + ix1) / 2
    stats = {"windows": 0, "lit": 0}
    pid = block["id"]

    _apron(parent, pid, (x0 + 0.2, y0 + 0.2, x1 - 0.2, y1 - 0.2), b["walk"])

    P0, u, L, n = kit.wall_frame(inner, "NY")
    skin = iy0 - 0.36
    # ---- ground floor: piers + big display windows + central door ----------
    door_w = 3.6
    wins = []
    bay_w = 2.7
    left_start = 1.6
    for i in range(2):
        wins.append((left_start + i * bay_w + 0.06, left_start + (i + 1) * bay_w - 0.06))
    right_mirror = [(L - b1, L - b0) for (b0, b1) in wins]
    wins.extend(reversed(right_mirror))
    door_s = (L - door_w) / 2
    openings = [(s0, s1, 0.95, 3.75) for (s0, s1) in wins]
    openings.append((door_s, door_s + door_w, 0.0, 3.02))
    Pn, un, Ln, nn = _panel_wall(parent, pid + "_front_base", inner, "NY", 0.0,
                                 z_band, b["shop"], openings=openings, thick=0.36,
                                 plinth_z=0.9, plinth_mat=b["plinth_dark"])
    # display window interiors (dim warm glow + shelves) and glass
    for wi, (s0, s1) in enumerate(wins):
        _interior_glow(parent, f"{pid}_shint{wi}", Pn, un, nn, s0 + 0.12, s1 - 0.12,
                       1.05, 3.6, rng)
        mid = (s0 + s1) / 2
        w = s1 - s0 - 0.2
        glass = _solid("CIV_SHOP_GLASS", "#1A222C", rough=0.1, metallic=0.3, alpha=0.35)
        gc = Pn + un * mid + nn * 0.2 + Vector((0, 0, 2.3))
        kit.quad(parent, f"{pid}_shg{wi}", tuple(gc), un, Vector((0, 0, 1)), w, 2.6, glass)
        # frame
        for side in (-1, 1):
            kit.box(parent, f"{pid}_shf{wi}{side}",
                    tuple(Pn + un * (mid + side * w / 2) + nn * 0.1 + Vector((0, 0, 2.3))),
                    (0.1, 0.2, 2.75), b["frame"])
        for zz in (1.0, 3.7):
            kit.box(parent, f"{pid}_shft{wi}{int(zz)}",
                    tuple(Pn + un * mid + nn * 0.1 + Vector((0, 0, zz))),
                    (w + 0.1, 0.2, 0.12), b["frame"])
        # vertical shop-window muntins
        for k in (1, 2):
            kit.box(parent, f"{pid}_shm{wi}{k}",
                    tuple(Pn + un * (s0 + (s1 - s0) * k / 2.4) + nn * 0.12
                          + Vector((0, 0, 2.3))),
                    (0.07, 0.14, 2.6), b["frame"])
        # stall riser under the display window
        kit.box(parent, f"{pid}_shs{wi}",
                tuple(Pn + un * mid + nn * 0.18 + Vector((0, 0, 0.78))),
                (w + 0.2, 0.5, 0.5), b["plinth_dark"])
    # central door (threshold above the steps)
    _recessed_door(parent, pid + "_door", (cx, skin, 0), (1, 0, 0), (0, -1, 0),
                   0.0, 0.3, door_w - 0.3, 2.9, rng, wm,
                   mat=_solid("CIV_SHOP_DOOR", "#4A4E48", rough=0.6, metallic=0.3),
                   glass_frac=0.85, lit=True, double=True)
    # steps + canopy over the door
    _steps(parent, pid + "_dsteps", (0, iy0 - 0.2, 0), (1, 0, 0), (0, -1, 0),
           cx - 2.5, cx + 2.5, 0.9, 0.3, 3, b["step"])
    kit.box_mm(parent, pid + "_dcan", cx - 2.9, iy0 - 1.55, 3.25,
               cx + 2.9, iy0 - 0.1, 3.5, b["shop"], bevel=0.02)
    # sign band above the shop front
    _sign(parent, pid + "_sign", (cx, skin - 0.03, z_band + 0.45), (1, 0, 0), (0, -1, 0),
          11.6, 0.8, "ГАСТРОНОМ", bg="#2E3B42", fg="#E9E3D2", glow=2.6,
          size_frac=0.6)
    # faded red-brown band between the storeys
    kit.box_mm(parent, pid + "_band_f", ix0 - 0.02, iy0 - 0.42, z_band + 0.9,
               ix1 + 0.02, skin, z_band + 1.28, b["shop_band"])
    for face in ("PX", "NX", "PY"):
        Pf, uf, Lf, nf = kit.wall_frame(inner, face)
        c = Pf + nf * 0.4 + uf * (Lf / 2) + Vector((0, 0, z_band + 1.09))
        size = (Lf - 0.1, 0.08, 0.38) if abs(uf.x) > 0.5 else (0.08, Lf - 0.1, 0.38)
        kit.box(parent, f"{pid}_band_{face}", (c.x, c.y, c.z), size, b["shop_band"])
    # poster case on a pier
    _poster_case(parent, pid + "_pcase", (ix0 + 1.6 + 5.4 + 0.6, skin + 0.02, 2.3),
                 (1, 0, 0), (0, -1, 0), 1.0, 1.6, rng)

    # ---- upper floor -------------------------------------------------------
    st = kit.facade_wall(parent, pid + "_up_NY", inner, "NY", z_band, z1, b["shop"], rng,
                         wmats=wm, floors=1, floor_h=3.0, win_w=1.35, win_h=1.2,
                         spacing=2.45, margin=1.9, lit_ratio=0.16, thickness=0.36,
                         plinth_h=0.0, joints=False, ground_win_bottom=1.65)
    stats["windows"] += st["windows"]
    stats["lit"] += st["lit"]
    for face in ("PX", "NX", "PY"):
        # upper band
        st = kit.facade_wall(parent, pid + "_up_" + face, inner, face, z_band, z1,
                             b["shop"], rng, wmats=wm, floors=1, floor_h=3.0,
                             win_w=1.3, win_h=1.2, spacing=2.9, margin=1.8,
                             lit_ratio=0.12, thickness=0.36, plinth_h=0.0,
                             joints=False, ground_win_bottom=1.65)
        stats["windows"] += st["windows"]
        stats["lit"] += st["lit"]
    # ground band: simple walls with small windows on the sides + delivery back
    for face, wins_s in (("PX", (1.4, 4.2)), ("NX", (1.4, 4.2)), ("PY", (1.5, 4.6))):
        Pf, uf, Lf, nf = kit.wall_frame(inner, face)
        ops = [(s - 0.65, s + 0.65, 1.55, 2.85) for s in wins_s]
        _panel_wall(parent, f"{pid}_g_{face}", inner, face, 0.0, z_band, b["shop"],
                    openings=ops, thick=0.36, plinth_z=0.9,
                    plinth_mat=b["plinth_dark"])
        for k, s in enumerate(wins_s):
            _custom_window(parent, f"{pid}_{face}_w{k}", Pf, uf, nf, s, 2.2,
                           1.3, 1.3, rng, wm, lit_ratio=0.2, grille=True)
            stats["windows"] += 1
    # delivery entrance at the back (north) — loading dock + ramp
    Pb, ub, Lb, nb = kit.wall_frame(inner, "PY")
    dyn = iy1 + 0.36
    dlx = ix1 - 4.6
    _recessed_door(parent, pid + "_ddoor", (dlx, dyn, 0), (1, 0, 0), (0, 1, 0),
                   0.0, 0.42, 3.0, 2.7, rng, wm, mat=b["dark_metal"],
                   glass_frac=0.0, double=True)
    kit.box_mm(parent, pid + "_dock", dlx - 2.6, iy1 + 0.1, 0.0,
               dlx + 2.6, iy1 + 1.1, 0.45, b["plinth"], bevel=0.02)
    kit.box_mm(parent, pid + "_dramp", dlx - 2.6, iy1 + 1.1, 0.0,
               dlx + 2.6, iy1 + 1.7, 0.22, b["step"])
    kit.box_mm(parent, pid + "_dcan", dlx - 2.2, dyn - 0.1, 2.95,
               dlx + 2.2, dyn + 1.2, 3.12, b["shop_band"])
    # a couple of crates + a rubbish bin at the back
    kit.box_mm(parent, pid + "_crate0", dlx - 4.4, iy1 + 0.3, 0.0,
               dlx - 3.4, iy1 + 1.1, 0.7, b["wood_door"])
    kit.box_mm(parent, pid + "_crate1", dlx - 4.2, iy1 + 0.45, 0.7,
               dlx - 3.6, iy1 + 0.95, 1.2, b["rust"])
    kit.trash_bin(parent, pid + "_bin", (dlx + 3.6, iy1 + 0.9, 0.0))

    # ---- roof --------------------------------------------------------------
    _roof_and_parapet(parent, pid, inner, z1, b["shop"], parapet_h=0.6)
    _roof_clutter(parent, pid, inner, z1 + 0.1, rng, b["shop"],
                  vents=((0, 0), (1, 1), (2, 2)), antennas=3)
    _vent_box(parent, pid + "_bigvent", (cx + 1.0, iy0 + 2.0, z1 + 0.12),
              3.0, 1.8, 1.25, rng)
    _chimney(parent, pid + "_pipe", (ix0 + 2.2, iy1 - 2.5, z1 + 0.12), 0.55, 0.55, 2.3)
    _drainpipes(parent, pid, inner, z1,
                n_list=[(cx - 3.2, iy0 - 0.12), (cx + 3.2, iy0 - 0.12)])
    for face in ("PX", "NX", "PY"):
        Pf, uf, Lf, nf = kit.wall_frame(inner, face)
        _basement_grilles(parent, pid + "_" + face, Pf, uf, nf, [2.0, Lf - 2.0])
    for pre in (pid + "_up_PX", pid + "_up_NX", pid + "_PX_w", pid + "_NX_w"):
        _fix_xsills(parent, pre, wm)

    stats["objects"] = len(parent.children)
    stats["id"] = pid
    return stats


# ============================================================================
# ROUND 2 — expanded-map community buildings
# ============================================================================

def _steam_stack(parent, name, pos, height, rng=None):
    """Rooftop exhaust / steam vent stack with a rain cap and a bend."""
    metal = _solid("CIV_STEAM_METAL", "#5A5D58", rough=0.72, metallic=0.35)
    dark = _solid("CIV_STEAM_DARK", "#2E3234", rough=0.85)
    x, y, z = pos
    kit.cyl(parent, name + "_p", (x, y, z + height / 2), 0.22, height, metal, verts=10)
    kit.cyl(parent, name + "_cap", (x, y, z + height + 0.05), 0.36, 0.12, dark, verts=10)
    kit.cyl(parent, name + "_base", (x, y, z + 0.18), 0.3, 0.36, dark, verts=10)
    kit.cyl_between(parent, name + "_elbow", (x, y, z + height * 0.55),
                    (x + 0.45, y, z + height * 0.55 + 0.35), 0.1, metal, verts=6)


def _stripe_awning(parent, name, P0, u, n, s0, s1, z_top, depth, drop,
                   col_a, col_b, mat_a, mat_b):
    """Sloped striped awning / market canopy projecting along +n."""
    P0 = Vector(P0)
    u = Vector(u).normalized()
    n = Vector(n).normalized()
    v = Vector((0, 0, 1))
    length = s1 - s0
    n_str = max(2, int(round(length / 0.95)))
    sw = length / n_str
    ang = math.atan2(drop, depth)
    ln = math.hypot(depth, drop)
    for i in range(n_str):
        s = s0 + sw * (i + 0.5)
        c = P0 + u * s + n * (depth / 2) + v * (z_top - drop / 2)
        if abs(u.x) > 0.5:
            size = (sw - 0.015, ln, 0.05)
            rot = (-ang if n.y > 0 else ang, 0, 0)
        else:
            size = (ln, sw - 0.015, 0.05)
            rot = (0, ang if n.x > 0 else -ang, 0)
        kit.box(parent, f"{name}_s{i}", (c.x, c.y, c.z), size,
                mat_a if i % 2 == 0 else mat_b, rot=rot)
    # front valance
    c = P0 + u * ((s0 + s1) / 2) + n * depth + v * (z_top - drop - 0.1)
    size = (length, 0.05, 0.24) if abs(u.x) > 0.5 else (0.05, length, 0.24)
    kit.box(parent, name + "_val", (c.x, c.y, c.z), size, mat_a)
    # support struts
    for frac in (0.1, 0.9):
        s = s0 + length * frac
        p0 = P0 + u * s + v * (z_top + 0.05)
        p1 = P0 + u * s + n * depth + v * (z_top - drop)
        kit.cyl_between(parent, f"{name}_strut{int(frac * 10)}",
                        tuple(p0), tuple(p1), 0.035, _solid("CIV_AWN_STRUT", "#4A4E4C",
                                                            rough=0.6, metallic=0.4), verts=5)


def _crate(parent, name, center, size, produce=None, rng=None):
    """Produce crate with a coloured goods top (market / shop props)."""
    wood = _solid("CIV_CRATE_WOOD", "#6E6455", rough=0.9)
    x, y, z = center
    w, d, h = size
    kit.box(parent, name + "_b", (x, y, z + h / 2), (w, d, h), wood)
    gmat = _solid("CIV_GOODS_" + str(produce or "#7A6A4A"), produce or "#7A6A4A",
                  rough=0.92)
    kit.box(parent, name + "_g", (x, y, z + h + 0.03), (w * 0.86, d * 0.82, 0.1), gmat)


def _barrel(parent, name, center, rng=None):
    x, y, z = center
    rust = _solid("CIV_BARREL", "#5A5248", rough=0.85, metallic=0.2)
    kit.cyl(parent, name + "_b", (x, y, z + 0.44), 0.3, 0.88, rust, verts=10)
    for zz in (0.24, 0.64):
        kit.cyl(parent, name + f"_r{int(zz * 100)}", (x, y, z + zz), 0.315, 0.06,
                _solid("CIV_BARREL_HOOP", "#3A3E3C", rough=0.7, metallic=0.4), verts=10)


# ---- round-2b: cheap secondary-facade detail helpers -----------------------

def _band_strip(parent, pid, inner, faces, za, zb, mat, tag="band2", out=0.40):
    """Thin painted band running along secondary facades (plinth / frieze)."""
    for face in faces:
        Pf, uf, Lf, nf = kit.wall_frame(inner, face)
        c = Pf + nf * out + uf * (Lf / 2) + Vector((0, 0, (za + zb) / 2))
        size = (Lf - 0.1, 0.08, zb - za) if abs(uf.x) > 0.5 else (0.08, Lf - 0.1, zb - za)
        kit.box(parent, f"{pid}_{tag}_{face}{int(za * 10)}", (c.x, c.y, c.z),
                size, mat)


def _panel_seams(parent, pid, inner, faces, z0, z1, step, mat, out=0.38):
    """Vertical panel seams on big secondary walls (silhouette/texture break)."""
    for face in faces:
        Pf, uf, Lf, nf = kit.wall_frame(inner, face)
        n = max(1, int(Lf / step))
        for i in range(1, n):
            s = Lf * i / n
            c = Pf + nf * out + uf * s + Vector((0, 0, (z0 + z1) / 2))
            size = (0.07, 0.06, z1 - z0) if abs(uf.x) > 0.5 else (0.06, 0.07, z1 - z0)
            kit.box(parent, f"{pid}_seam_{face}{i}", (c.x, c.y, c.z), size, mat)


def _blind_window(parent, name, P0, u, n, s, zc, w, h, frame, glass, sill=True):
    """Small service window mounted on a solid wall (3 objects, cheap)."""
    P0 = Vector(P0)
    u = Vector(u).normalized()
    n = Vector(n).normalized()
    c = P0 + u * s + n * 0.03 + Vector((0, 0, zc))
    size = (w, 0.06, h) if abs(u.x) > 0.5 else (0.06, w, h)
    kit.box(parent, name + "_d", (c.x, c.y, c.z), size, glass)
    fc = P0 + u * s + n * 0.02 + Vector((0, 0, zc))
    size = (w + 0.14, 0.06, h + 0.14) if abs(u.x) > 0.5 else (0.06, w + 0.14, h + 0.14)
    kit.box(parent, name + "_f", (fc.x, fc.y, fc.z), size, frame)
    if sill:
        sc = P0 + u * s + n * 0.1 + Vector((0, 0, zc - h / 2 - 0.05))
        size = (w + 0.2, 0.2, 0.07) if abs(u.x) > 0.5 else (0.2, w + 0.2, 0.07)
        kit.box(parent, name + "_s", (sc.x, sc.y, sc.z), size, frame)


def _louvre(parent, name, P0, u, n, s, zc, w, h):
    """Wall louvre / vent grille (3 objects)."""
    P0 = Vector(P0)
    u = Vector(u).normalized()
    n = Vector(n).normalized()
    dark = _solid("CIV_LOUVRE", "#2A2F31", rough=0.85)
    c = P0 + u * s + n * 0.04 + Vector((0, 0, zc))
    size = (w, 0.1, h) if abs(u.x) > 0.5 else (0.1, w, h)
    kit.box(parent, name + "_b", (c.x, c.y, c.z), size, dark)
    for i in range(2):
        lc = P0 + u * s + n * 0.055 + Vector((0, 0, zc - h * 0.22 + i * h * 0.34))
        size = (w * 0.82, 0.06, h * 0.16) if abs(u.x) > 0.5 else (0.06, w * 0.82, h * 0.16)
        kit.box(parent, f"{name}_l{i}", (lc.x, lc.y, lc.z), size,
                _solid("CIV_LOUVRE_SLAT", "#4A4F50", rough=0.7))


def _pallet(parent, name, center, rng=None):
    """Pallet + a couple of stacked crates (service yard clutter)."""
    wood = _solid("CIV_PALLET", "#6A6052", rough=0.92)
    x, y, z = center
    kit.box(parent, name + "_p", (x, y, z + 0.06), (1.1, 0.9, 0.12), wood)
    kit.box(parent, name + "_c0", (x - 0.12, y + 0.08, z + 0.5), (0.62, 0.5, 0.72),
            _solid("CIV_PALLET_CRATE", "#71675A", rough=0.9))
    kit.box(parent, name + "_c1", (x + 0.42, y - 0.14, z + 0.36), (0.5, 0.42, 0.5),
            _solid("CIV_PALLET_CRATE2", "#5E564A", rough=0.9))


def _service_door(parent, pid, P0, u, n, s, w=2.4, h=2.6, z0=0.0):
    """Rear service door + small canopy + lamp (cheap)."""
    _recessed_door(parent, f"{pid}_sdoor", P0, u, n, s, z0, w, h, kit.RNG(3),
                   kit.win_materials(), mat=_solid("CIV_SVC_DOOR", "#3E4448",
                                                  rough=0.6, metallic=0.4),
                   glass_frac=0.0, lit=False, double=True)
    P0 = Vector(P0)
    u = Vector(u).normalized()
    n = Vector(n).normalized()
    c = P0 + u * s + n * 0.5 + Vector((0, 0, z0 + h + 0.22))
    size = (w + 1.3, 1.0, 0.16) if abs(u.x) > 0.5 else (1.0, w + 1.3, 0.16)
    kit.box(parent, f"{pid}_sdcan", (c.x, c.y, c.z), size,
            _solid("CIV_SVC_CANOPY", "#4A4E50", rough=0.7, metallic=0.3))
    lamp = _solid("CIV_SVC_LAMP", "#FFD9A0", rough=0.3,
                  emission="#FFC57A", es=14.0)
    lc = P0 + u * s + n * 0.12 + Vector((0, 0, z0 + h + 0.12))
    kit.sphere(parent, f"{pid}_lamp2{int(s)}", (lc.x, lc.y, lc.z), 0.12, lamp,
               segments=8, rings=4)


# ----------------------------------------------------------------------------
# BANYA — public bathhouse  (rect [-158,22,-138,38], faces PX)
# ----------------------------------------------------------------------------
def build_banya(parent, block, rng, ctx):
    b = _wall_materials()
    wm = ctx.get("wmats") or kit.win_materials()
    pid = block["id"]
    x0, y0, x1, y1 = block["rect"]
    inner = (x0 + 0.5, y0 + 0.5, x1 - 1.6, y1 - 0.5)
    ix0, iy0, ix1, iy1 = inner
    z1 = 10.5
    stats = {"windows": 0, "lit": 0}

    brick = _tri_wall("CIV_BANYA_BRICK", "tex_brick_plaster.png", tint="#B4B1A6",
                      tile=3.0)
    white = _tri_wall("CIV_BANYA_WHITE", "tex_panel_painted.png", tint="#C0BFB4",
                      tile=3.4)
    plinth = b["plinth"]
    walk = b["walk"]
    _apron(parent, pid, (x0 + 0.2, y0 + 0.2, x1 - 0.2, y1 - 0.2), walk)

    # ---- facades (small high windows) -------------------------------------
    entr_s = 30.0 - iy0
    st = kit.facade_wall(parent, pid + "_PX", inner, "PX", 0.0, z1, brick, rng,
                         wmats=wm, floors=3, floor_h=3.3, win_w=1.05, win_h=1.35,
                         spacing=2.8, margin=1.6, lit_ratio=0.09, thickness=0.36,
                         plinth_h=1.3, plinth_mat=plinth, joints=True,
                         ground_win_bottom=2.15, ground_skip=[entr_s])
    stats["windows"] += st["windows"]
    stats["lit"] += st["lit"]
    for face, kw in (
        ("NX", dict(floors=3, floor_h=3.3, win_w=1.0, win_h=1.3, spacing=2.7,
                    margin=1.5, ground_skip=[8.0])),
        ("PY", dict(floors=3, floor_h=3.3, win_w=1.1, win_h=1.35, spacing=2.6,
                    margin=1.6)),
        ("NY", dict(floors=3, floor_h=3.3, win_w=1.1, win_h=1.35, spacing=2.7,
                    margin=1.6)),
    ):
        st = kit.facade_wall(parent, pid + "_" + face, inner, face, 0.0, z1, white if face == "NX" else brick,
                             rng, wmats=wm, lit_ratio=0.08, thickness=0.36,
                             plinth_h=1.3, plinth_mat=plinth, joints=True,
                             ground_win_bottom=2.15, **kw)
        stats["windows"] += st["windows"]
        stats["lit"] += st["lit"]

    # ---- warm entrance porch on the east facade ---------------------------
    P0, u, L, n = kit.wall_frame(inner, "PX")
    skin = ix1 + 0.36
    _recessed_door(parent, pid + "_door", (skin, 0, 0), (0, 1, 0), (1, 0, 0),
                   30.0, 0.36, 2.2, 2.85, rng, wm,
                   mat=_solid("CIV_BANYA_DOOR", "#4A3E30", rough=0.65),
                   glass_frac=0.5, lit=True, double=True)
    kit.box_mm(parent, pid + "_terr", skin - 0.02, 27.6, 0.0,
               skin + 1.05, 32.4, 0.36, plinth, bevel=0.02)
    _steps(parent, pid + "_steps", (skin + 1.05, 0, 0), (0, 1, 0), (1, 0, 0),
           27.7, 32.3, 0.78, 0.36, 3, b["step"])
    # slim canopy on two brick piers
    kit.box_mm(parent, pid + "_can", skin - 0.05, 27.1, 3.3,
               skin + 1.35, 32.9, 3.52, brick, bevel=0.02)
    kit.box_mm(parent, pid + "_can_f", skin - 0.1, 27.0, 3.24,
               skin + 1.42, 33.0, 3.6, white)
    for s in (27.9, 32.1):
        kit.box(parent, f"{pid}_pier{int(s)}", (skin + 1.15, s, 1.83),
                (0.42, 0.42, 3.3), brick)
    _sign(parent, pid + "_sign", (skin + 0.03, 30.0, 4.15), (0, 1, 0), (1, 0, 0),
          5.8, 1.15, "БАНЯ", bg="#2C3A42", fg="#EDE6D4", glow=2.8, size_frac=0.72)
    _wall_lamp(parent, pid + "_wlA", (skin, 26.6, 2.9), (0, 1, 0), (1, 0, 0))
    _wall_lamp(parent, pid + "_wlB", (skin, 33.4, 2.9), (0, 1, 0), (1, 0, 0))

    # ---- wet-side service door at the back --------------------------------
    Pb, ub, Lb, nb = kit.wall_frame(inner, "NX")
    skin_w = ix0 - 0.36
    _recessed_door(parent, pid + "_wet", (skin_w, 0, 0), (0, 1, 0), (-1, 0, 0),
                   27.0, 0.0, 1.7, 2.5, rng, wm, mat=b["dark_metal"],
                   glass_frac=0.0, lit=False, double=False)
    kit.box_mm(parent, pid + "_wetcan", skin_w - 0.95, 26.0, 2.75,
               skin_w + 0.05, 28.0, 2.9, b["roof_dark"])
    for i in range(2):
        kit.box_mm(parent, pid + f"_wetst{i}", skin_w - (0.65 - i * 0.28), 26.2,
                   0.0, skin_w - (0.2 - i * 0.28), 27.8, 0.14 + i * 0.14, b["step"])
    # drain grate + wet patch by the service door
    kit.box_mm(parent, pid + "_grate", skin_w - 1.3, 28.3, 0.0,
               skin_w - 0.5, 29.1, 0.05, _solid("CIV_GRATE", "#2E3234", rough=0.9))

    # ---- brick chimney (rises through the rear roof) ----------------------
    _chimney(parent, pid + "_chim", (-156.3, 36.0, 0.0), 1.9, 1.9, 15.2,
             mat=_solid("CIV_CHIMNEY_BRICK", "#7A6258", rough=0.95))
    # soot cap band + ladder rungs
    for i in range(3):
        kit.box(parent, f"{pid}_chim_band{i}", (-156.3, 36.0, 11.4 + i * 1.25),
                (2.06, 2.06, 0.14), _solid("CIV_CHIMNEY_RING", "#4A403C", rough=0.9))
    for i in range(6):
        kit.box(parent, f"{pid}_chim_rung{i}", (-155.25, 35.2, 1.2 + i * 1.0),
                (0.06, 0.8, 0.06), b["dark_metal"])
    # whitewashed band + cornice break up the plain facades
    for face in ("PX", "NX", "PY", "NY"):
        Pf, uf, Lf, nf = kit.wall_frame(inner, face)
        for (za, zb, mt) in ((3.72, 4.08, white), (z1 - 0.58, z1 - 0.24, white)):
            c = Pf + nf * 0.4 + uf * (Lf / 2) + Vector((0, 0, (za + zb) / 2))
            size = (Lf - 0.1, 0.08, zb - za) if abs(uf.x) > 0.5 else (0.08, Lf - 0.1, zb - za)
            kit.box(parent, f"{pid}_band_{face}{int(za * 10)}", (c.x, c.y, c.z),
                    size, mt)

    # ---- roof --------------------------------------------------------------
    _roof_and_parapet(parent, pid, inner, z1, brick, parapet_h=0.7)
    _roof_clutter(parent, pid, inner, z1 + 0.1, rng, brick,
                  vents=((0, 0), (1, 1)), antennas=2)
    # steam vent stacks
    for i, (sx, sy) in enumerate(((-148.5, 26.0), (-152.0, 30.5), (-146.5, 36.0),
                                  (-144.0, 24.6))):
        _steam_stack(parent, f"{pid}_steam{i}", (sx, sy, z1 + 0.1),
                     1.5 + 0.5 * (i % 3), rng)
    # roof water tank
    kit.cyl(parent, pid + "_tank", (-150.5, 24.2, z1 + 1.0), 0.85, 1.7,
            _solid("CIV_TANK", "#5E625C", rough=0.7, metallic=0.3), verts=12)
    _vent_box(parent, pid + "_vent", (-142.5, 33.0, z1 + 0.12), 1.3, 1.0, 0.9, rng)

    # ---- details -----------------------------------------------------------
    _basement_grilles(parent, pid, P0, u, n, [3.0, 12.5])
    _drainpipes(parent, pid, inner, z1)
    for pre in (pid + "_NX", pid + "_PY", pid + "_NY"):
        _fix_xsills(parent, pre, wm)

    # ---- round-2b: firewood stack + steam pipes by the wet-side door ------
    Pw, uw, Lw, nw = kit.wall_frame(inner, "NX")
    wet_face = Pw + nw * 0.36
    # firewood stack (3 layers of logs) under a small lean-to
    wood = _solid("CIV_FIREWOOD", "#6E5F4A", rough=0.95)
    end_mat = _solid("CIV_FIREWOOD_END", "#8C7A5E", rough=0.95)
    for row in range(3):
        for k in range(4):
            yy = 25.0 + k * 0.34
            zz = 0.2 + row * 0.3
            kit.cyl(parent, f"{pid}_wood{row}{k}", (wet_face.x - 0.55, yy, zz),
                    0.16, 1.5, wood, rot=(0, math.radians(90), 0), verts=7)
            kit.cyl(parent, f"{pid}_woodE{row}{k}", (wet_face.x - 1.31, yy, zz),
                    0.11, 0.04, end_mat, rot=(0, math.radians(90), 0), verts=7)
    kit.box(parent, f"{pid}_leanto", (wet_face.x - 0.5, 25.55, 1.5),
            (1.5, 2.1, 0.1), _solid("CIV_LEANTO", "#4E5250", rough=0.8, metallic=0.3),
            rot=(0, math.radians(-10), 0))
    # steam / hot-water pipes running up the wall beside the door
    pipe = _solid("CIV_STEAM_PIPE", "#5E6260", rough=0.6, metallic=0.4)
    for px, yy in ((wet_face.x - 0.12, 27.9), (wet_face.x - 0.12, 28.35)):
        kit.cyl(parent, f"{pid}_steamvp{int(yy * 10)}", (px, yy, 4.0), 0.075, 7.6,
                pipe, verts=8)
        for zz in (1.0, 3.2, 5.4):
            kit.cyl(parent, f"{pid}_steamfl{int(yy * 10)}{int(zz)}", (px, yy, zz),
                    0.11, 0.1, pipe, verts=8)
    kit.cyl_between(parent, pid + "_steamrun", (wet_face.x - 0.12, 27.9, 7.8),
                    (wet_face.x - 0.12, 31.5, 7.8), 0.075, pipe, verts=8)
    # two small high windows on the rear + one on the side
    Pr, ur, Lr, nr = kit.wall_frame(inner, "NY")
    rear_face = Pr + nr * 0.36
    for k, (s, zc) in enumerate(((5.0, 8.6), (10.5, 8.9))):
        _blind_window(parent, f"{pid}_svcr{k}", rear_face, ur, nr, s, zc, 0.8, 0.7,
                      _solid("CIV_SVC_FRAME", "#8E9088", rough=0.85),
                      _solid("CIV_SVC_GLASS", "#151C22", rough=0.55, metallic=0.0))
    _blind_window(parent, pid + "_svcw", wet_face, uw, nw, 11.5, 8.7, 0.8, 0.7,
                  _solid("CIV_SVC_FRAME", "#8E9088", rough=0.85),
                  _solid("CIV_SVC_GLASS", "#151C22", rough=0.55, metallic=0.0))
    _louvre(parent, pid + "_lvt0", wet_face, uw, nw, 6.0, 3.4, 0.7, 0.55)
    _louvre(parent, pid + "_lvt1", rear_face, ur, nr, 16.0, 2.4, 0.8, 0.6)

    stats["objects"] = len(parent.children)
    stats["id"] = pid
    return stats


# ----------------------------------------------------------------------------
# MARKET — covered market hall  (rect [128,-64,158,-42], faces NX)
# ----------------------------------------------------------------------------
def build_market(parent, block, rng, ctx):
    b = _wall_materials()
    wm = ctx.get("wmats") or kit.win_materials()
    pid = block["id"]
    x0, y0, x1, y1 = block["rect"]
    inner = (x0 + 1.6, y0 + 0.5, x1 - 0.5, y1 - 0.5)
    ix0, iy0, ix1, iy1 = inner
    z_wall = 6.4
    stats = {"windows": 0, "lit": 0}

    wall = _tri_wall("CIV_MKT_WALL", "tex_concrete_panel.png", tint="#9AA0A0", tile=5.2)
    metal = _tri_wall("CIV_MKT_METAL", "tex_rust_metal.png", tint="#8E908C", tile=2.6)
    plinth = b["plinth"]
    _apron(parent, pid, (x0 + 0.2, y0 + 0.2, x1 - 0.2, y1 - 0.2), b["walk"])

    # ---- open front colonnade (NX) ---------------------------------------
    P0, u, L, n = kit.wall_frame(inner, "NX")
    skin = ix0 - 0.36
    piers = [0.4, 5.5, 10.5, 15.5, 20.6]
    openings = [(piers[i] + 0.4, piers[i + 1] - 0.4, 0.95, 5.9)
                for i in range(len(piers) - 1)]
    _panel_wall(parent, pid + "_front", inner, "NX", 0.0, z_wall, wall,
                openings=openings, thick=0.4, plinth_z=0.9,
                plinth_mat=b["plinth_dark"])
    # interior floor + dark ceiling + back/side walls
    kit.box_mm(parent, pid + "_floor", ix0, iy0, 0.0, ix1, iy1, 0.14,
               _solid("CIV_MKT_FLOOR", "#4A4A44", rough=0.95))
    kit.box_mm(parent, pid + "_ceil", ix0, iy0, 6.2, ix1, iy1, 6.3,
               _solid("CIV_MKT_CEIL", "#26282A", rough=0.95))
    # side walls (NY / PY) with high windows
    for face in ("NY", "PY"):
        st = kit.facade_wall(parent, pid + "_" + face, inner, face, 0.0, z_wall,
                             wall, rng, wmats=wm, floors=1, floor_h=6.4,
                             win_w=1.7, win_h=1.15, spacing=3.0, margin=1.5,
                             lit_ratio=0.24, thickness=0.36, plinth_h=0.9,
                             plinth_mat=b["plinth_dark"], joints=False,
                             ground_win_bottom=3.3)
        stats["windows"] += st["windows"]
        stats["lit"] += st["lit"]
    # back wall (PX): delivery doors + small high windows
    Pb, ub, Lb, nb = kit.wall_frame(inner, "PX")
    skin_b = ix1 + 0.36
    d_ops = []
    for ds in (8.0, 13.0):
        d_ops.append((ds - 1.6, ds + 1.6, 0.0, 3.05))
    _panel_wall(parent, pid + "_back", inner, "PX", 0.0, z_wall, wall,
                openings=d_ops, thick=0.36, plinth_z=0.9,
                plinth_mat=b["plinth_dark"])
    for i, ds in enumerate((8.0, 13.0)):
        _recessed_door(parent, f"{pid}_dd{i}", (skin_b, 0, 0), (0, 1, 0),
                       (1, 0, 0), iy0 + ds, 0.0, 2.9, 2.95, rng, wm,
                       mat=b["dark_metal"], glass_frac=0.0, lit=False, double=True)
        kit.box_mm(parent, f"{pid}_dcan{i}", skin_b + 0.05, iy0 + ds - 2.0, 3.15,
                   skin_b + 1.25, iy0 + ds + 2.0, 3.32, b["roof_dark"])
    # clerestory window high on the back wall
    Pf, uf, Lf, nf = kit.wall_frame(inner, "PX")
    _custom_window(parent, f"{pid}_bw0", Pf, uf, nf, 11.5, 5.15, 1.4, 1.0,
                   rng, wm, lit_ratio=0.25)
    stats["windows"] += 1

    # ---- sawtooth steel roof ----------------------------------------------
    ry0, ry1 = iy0 - 0.45, iy1 + 0.45
    ry_c = (ry0 + ry1) / 2
    rise = 1.05
    teeth = 3
    bay = (ix1 - ix0) / teeth
    for i in range(teeth):
        xa = ix0 + i * bay
        xb = xa + bay
        ang = math.atan2(rise, bay)
        ln = math.hypot(bay, rise)
        cx = (xa + xb) / 2
        cz = z_wall + 0.16 + rise / 2
        kit.box(parent, f"{pid}_tooth{i}", (cx, ry_c, cz),
                (ln, ry1 - ry0, 0.14), metal, rot=(0, -ang, 0))
        # vertical glazed clerestory at the high edge, facing the front (-X)
        n_panes = 7
        pw = (ry1 - ry0) / n_panes
        for k in range(n_panes):
            yy = ry0 + pw * (k + 0.5)
            lit = rng.random() < 0.3
            gm = wm["warm_b"] if lit else wm["dark_a"]
            kit.quad(parent, f"{pid}_tg{i}_{k}",
                     (xb - 0.03, yy, z_wall + 0.16 + rise / 2), (0, 1, 0), (0, 0, 1),
                     pw - 0.1, rise - 0.12, gm,
                     uv=[(0.15, 0.15), (0.15, 0.85), (0.85, 0.85), (0.85, 0.15)])
        # eave fascia at the tooth's low edge
        kit.box(parent, f"{pid}_tooth_f{i}", (xa + 0.06, ry_c, z_wall + 0.1),
                (0.14, ry1 - ry0, 0.5), _solid("CIV_MKT_FASCIA", "#5C605E",
                                               rough=0.7, metallic=0.3))
        # metal backing behind the clerestory glazing (no see-through from rear)
        kit.box(parent, f"{pid}_tback{i}", (xb + 0.07, ry_c,
                z_wall + 0.16 + rise / 2), (0.1, ry1 - ry0, rise + 0.08), metal)
    # side closure / stepped fascia at both gable ends
    for side, yy in (("S", ry0 + 0.07), ("N", ry1 - 0.07)):
        for i in range(teeth):
            xa = ix0 + i * bay
            xb = xa + bay
            kit.box(parent, f"{pid}_gable_{side}{i}",
                    ((xa + xb) / 2, yy, z_wall + rise / 2 + 0.12),
                    (bay + 0.02, 0.12, rise + 0.24), metal)
    # front eave gutter
    kit.box_mm(parent, pid + "_gutter", skin - 0.05, ry0, z_wall + 0.02,
               skin + 0.15, ry1, z_wall + 0.18, _solid("CIV_GUTTER", "#4E5250",
                                                       rough=0.7, metallic=0.4))
    # roof vents / hatch / flue on the big metal roof
    _vent_box(parent, pid + "_rv0", (ix0 + 1.2, iy0 + 4.5, z_wall + 0.22),
              1.2, 1.0, 0.8, rng)
    _vent_box(parent, pid + "_rv1", (ix0 + bay + 1.2, iy0 + 16.0, z_wall + 0.22),
              1.0, 0.9, 0.75, rng)
    _chimney(parent, pid + "_flue", (ix0 + 2 * bay + 1.6, iy0 + 17.5, z_wall + 0.22),
             0.5, 0.5, 2.0)

    # ---- striped awning over the open front -------------------------------
    red = _solid("CIV_AWN_RED", "#7E3A2E", rough=0.85)
    cream = _solid("CIV_AWN_CREAM", "#C4B9A2", rough=0.85)
    _stripe_awning(parent, pid + "_awn", (skin, 0, 0), (0, 1, 0), (-1, 0, 0),
                   iy0 + 0.25, iy0 + 20.75, 4.35, 2.1, 0.55, red, cream, red, cream)

    # ---- sign -------------------------------------------------------------
    _sign(parent, pid + "_sign", (skin - 0.03, (iy0 + iy1) / 2, 6.15), (0, 1, 0),
          (-1, 0, 0), 6.2, 0.8, "РЫНОК", bg="#2E3B41", fg="#EAE3D0", glow=2.8,
          size_frac=0.6)

    # ---- market interior: stalls, counters, produce, hanging lamps --------
    wood = _solid("CIV_MKT_WOOD", "#6A5C4A", rough=0.9)
    frame_m = _solid("CIV_MKT_FRAME", "#4A4E4C", rough=0.65, metallic=0.35)
    produce = ["#8A5A34", "#7E4636", "#5E6A42", "#8A7A4A", "#5A4A3A"]
    for row, xx in enumerate((ix0 + 5.0, ix0 + 10.6, ix0 + 16.2, ix0 + 21.8)):
        # long counter parallel to the front
        kit.box(parent, f"{pid}_counter{row}", (xx, (iy0 + iy1) / 2, 0.55),
                (0.75, 12.0, 1.1), wood)
        kit.box(parent, f"{pid}_ctop{row}", (xx, (iy0 + iy1) / 2, 1.14),
                (0.95, 12.2, 0.08), _solid("CIV_MKT_CTOP", "#565A56", rough=0.8))
        for k in range(6):
            yy = iy0 + 3.0 + k * 3.1
            pr = produce[(row + k) % len(produce)]
            _crate(parent, f"{pid}_cr{row}{k}", (xx - 0.1, yy, 1.18),
                   (0.6, 0.82, 0.26), pr)
            if k % 2 == 0:
                # small brown-paper bag / box stacked above
                kit.box(parent, f"{pid}_bag{row}{k}", (xx - 0.1, yy, 1.55),
                        (0.42, 0.55, 0.2), _solid("CIV_PAPER_BAG", "#9A8C70",
                                                  rough=0.95))
    # stalls along the back wall with striped canopies
    for k, yy in enumerate((iy0 + 4.5, iy0 + 10.5, iy0 + 16.5)):
        kit.box(parent, f"{pid}_stall{k}", (ix1 - 1.4, yy, 0.6),
                (1.9, 3.4, 1.2), wood)
        kit.box(parent, f"{pid}_stalltop{k}", (ix1 - 1.4, yy, 1.25),
                (2.1, 3.6, 0.1), frame_m)
        for pi in range(3):
            _crate(parent, f"{pid}_sc{k}{pi}", (ix1 - 1.4, yy - 1.1 + pi * 1.1, 1.32),
                   (1.2, 0.8, 0.28), produce[(k + pi) % len(produce)])
        kit.box(parent, f"{pid}_stcan{k}", (ix1 - 1.5, yy, 2.75),
                (1.6, 3.8, 0.08), red if k % 2 == 0 else cream, rot=(0, math.radians(-8), 0))
    # hanging lamps (warm dim)
    shade = _solid("CIV_MKT_SHADE", "#3A3E3C", rough=0.6, metallic=0.4)
    bulb = _solid("CIV_MKT_BULB", "#FFD9A0", rough=0.3, emission="#FFC070", es=9.0)
    for i, xx in enumerate((ix0 + 4.5, ix0 + 11.0, ix0 + 17.5, ix0 + 23.0)):
        for k, yy in enumerate((iy0 + 4.0, iy0 + 11.0, iy0 + 17.5)):
            hz = 4.6 + 0.15 * ((i + k) % 2)
            kit.cyl_between(parent, f"{pid}_lw{i}{k}", (xx, yy, 6.2), (xx, yy, hz + 0.2),
                            0.012, b["dark_metal"], verts=4)
            kit.cyl(parent, f"{pid}_lsh{i}{k}", (xx, yy, hz + 0.14), 0.3, 0.2, shade,
                    verts=10)
            kit.sphere(parent, f"{pid}_lb{i}{k}", (xx, yy, hz), 0.13, bulb,
                       segments=10, rings=5)

    # ---- produce crates / barrels outside the front -----------------------
    for i in range(5):
        yy = iy0 + 3.0 + i * 3.6
        _crate(parent, f"{pid}_ocr{i}", (skin - 1.5, yy, 0.0), (0.8, 1.0, 0.42),
               produce[i % len(produce)])
        if i % 2 == 0:
            _crate(parent, f"{pid}_ocrb{i}", (skin - 1.5, yy + 0.1, 0.44),
                   (0.7, 0.85, 0.38), produce[(i + 2) % len(produce)])
    _barrel(parent, pid + "_barrelA", (skin - 1.85, iy0 + 2.0, 0.0))
    _barrel(parent, pid + "_barrelB", (skin - 2.05, iy0 + 5.6, 0.0))

    # ---- details -----------------------------------------------------------
    _drainpipes(parent, pid, inner, z_wall)
    _basement_grilles(parent, pid, Pb, ub, nb, [3.0, Lb - 3.0])
    for pre in (pid + "_NY", pid + "_PY"):
        _fix_xsills(parent, pre, wm)

    # ---- round-2b: delivery yard on the back (PX) + end walls -------------
    Pk, uk, Lk, nk = kit.wall_frame(inner, "PX")
    back_face = Pk + nk * 0.36
    # pallets / crates by the two roll-up doors
    for i, ds in enumerate((5.2, 10.2, 15.8)):
        _pallet(parent, f"{pid}_pal{i}", (back_face.x + 0.55, iy0 + ds, 0.0), rng)
    _barrel(parent, pid + "_palB", (back_face.x + 0.7, iy0 + 18.4, 0.0))
    # wall vents + service lamp over the delivery doors
    for i, ds in enumerate((8.0, 13.0)):
        _louvre(parent, f"{pid}_lvtB{i}", back_face, uk, nk, ds, 4.3, 1.0, 0.8)
    _wall_lamp(parent, pid + "_lamp2B", (back_face.x + 0.04, iy0 + 10.5, 3.6),
               uk, nk)
    # plinth band + panel seams on the end walls
    _band_strip(parent, pid, inner, ("PY", "NY"), 0.9, 1.35,
                _solid("CIV_MKT_BAND", "#5E6A66", rough=0.85))
    _panel_seams(parent, pid, inner, ("PY", "NY"), 1.35, z_wall - 0.2, 4.5,
                 _solid("CIV_MKT_SEAM", "#7A807C", rough=0.9))
    # roof ridge vents on both gable ends
    for side, yy in (("S", iy0 - 0.5), ("N", iy1 + 0.5)):
        for k, xo in enumerate((6.0, 16.0)):
            _vent_box(parent, f"{pid}_ridge_{side}{k}",
                      (ix0 + xo, yy, z_wall + 0.55), 1.0, 0.7, 0.7, rng)
    # crates + pallet at the north end of the yard
    _pallet(parent, pid + "_palN", (ix1 - 2.4, iy1 + 1.0, 0.0), rng)

    stats["objects"] = len(parent.children)
    stats["id"] = pid
    return stats


# ----------------------------------------------------------------------------
# CINEMA — Кинотеатр «ЗАРЯ»  (rect [20,-108,60,-92], faces PY)
# ----------------------------------------------------------------------------
def build_cinema(parent, block, rng, ctx):
    b = _wall_materials()
    wm = ctx.get("wmats") or kit.win_materials()
    pid = block["id"]
    x0, y0, x1, y1 = block["rect"]
    inner = (x0 + 0.5, y0 + 0.5, x1 - 0.5, y1 - 1.4)
    ix0, iy0, ix1, iy1 = inner
    z1 = 11.0
    cx = (ix0 + ix1) / 2
    stats = {"windows": 0, "lit": 0}

    cream = _tri_wall("CIV_CINE_WALL", "tex_panel_painted.png", tint="#ADA79B", tile=3.0)
    tile_band = _solid("CIV_CINE_BAND", "#7E8A8C", rough=0.8)
    plinth = b["plinth"]
    _apron(parent, pid, (x0 + 0.2, y0 + 0.2, x1 - 0.2, y1 - 0.2), b["walk"])
    P0, u, L, n = kit.wall_frame(inner, "PY")
    skin = iy1 + 0.36

    # ---- front facade with a recessed entrance portal ---------------------
    portal_s0, portal_s1 = 13.0, 27.0       # local s along the 39 m facade
    up_s = (3.0, 8.0, 31.0, 36.0)
    front_ops = [(portal_s0, portal_s1, 0.0, 6.0)]
    for sx in up_s:
        front_ops.append((sx - 0.62, sx + 0.62, 7.55, 8.85))
    _panel_wall(parent, pid + "_front", inner, "PY", 0.0, z1, cream,
                openings=front_ops, thick=0.36, plinth_z=1.2, plinth_mat=plinth)
    # cornice bands on the front
    for zz, hh in ((6.2, 0.35), (10.55, 0.4)):
        c = Vector((0, skin + 0.1, zz))
        kit.box(parent, f"{pid}_corn{int(zz * 10)}", ((ix0 + ix1) / 2, skin + 0.08, zz),
                (L - 0.1, 0.2, hh), tile_band)
    # vertical pilaster strips on the front
    for k in range(6):
        px = ix0 + 2.2 + k * 6.4
        if portal_s0 - 0.4 < px - ix0 < portal_s1 + 0.4:
            continue
        kit.box(parent, f"{pid}_pil{k}", (px, skin + 0.09, (4.6 + 10.4) / 2),
                (0.62, 0.22, 5.6), tile_band)
    # small upper windows in the blank bays
    for k, sx in enumerate(up_s):
        _custom_window(parent, f"{pid}_uw{k}", P0, u, n, sx, 8.2, 1.15, 1.25, rng, wm,
                       lit_ratio=0.3)
        stats["windows"] += 1

    # ---- recessed foyer + doors ------------------------------------------
    back_y = skin - 3.1
    # side walls of the portal
    for side_s in (portal_s0, portal_s1):
        sx = x0 + 0.5 + side_s
        kit.box_mm(parent, f"{pid}_pwall{int(side_s)}", sx - 0.15, back_y, 0.0,
                   sx + 0.15, skin + 0.06, 6.0, cream)
    kit.box_mm(parent, pid + "_pback", ix0 + portal_s0, back_y - 0.3, 0.0,
               ix0 + portal_s1, back_y, 6.0, cream)
    kit.box_mm(parent, pid + "_pceil", ix0 + portal_s0, back_y, 6.0,
               ix0 + portal_s1, skin + 0.06, 6.2, b["roof_dark"])
    kit.box_mm(parent, pid + "_pfloor", ix0 + portal_s0, back_y, 0.0,
               ix0 + portal_s1, skin + 0.9, 0.12, b["walk"])
    # warm glowing foyer behind the glass doors
    glow = _solid("CIV_CINE_GLOW", "#E8B078", rough=0.9,
                  emission="#E8B078", es=1.15)
    kit.quad(parent, pid + "_fglow", ((ix0 + portal_s0 + ix0 + portal_s1) / 2,
                                      back_y + 0.03, 4.55), (1, 0, 0), (0, 0, 1),
             6.4, 2.2, glow)
    # glass doors in a dark frame
    doorw = 3.4
    for side in (-1, 1):
        dcx = cx + side * (doorw / 2)
        kit.box(parent, f"{pid}_dframe{side}", (dcx, back_y + 0.12, 1.55),
                (doorw, 0.1, 3.1), b["dark_metal"])
        kit.quad(parent, f"{pid}_dglass{side}", (dcx, back_y + 0.18, 1.6),
                 (1, 0, 0), (0, 0, 1), doorw - 0.4, 2.6,
                 wm["warm_a" if side < 0 else "dim"],
                 uv=[(0.15, 0.15), (0.15, 0.85), (0.85, 0.85), (0.85, 0.15)])
    # ticket booth in the foyer
    tbx = ix0 + portal_s0 + 2.0
    kit.box_mm(parent, pid + "_tb", tbx - 0.9, back_y + 1.2, 0.0,
               tbx + 0.9, back_y + 2.8, 2.5, cream)
    kit.box_mm(parent, pid + "_tbwin", tbx - 0.6, back_y + 1.9, 1.15,
               tbx + 0.6, back_y + 2.0, 1.75, _solid("CIV_KASS_GLASS", "#2A2E2C",
                                                    rough=0.3, emission="#C8A878",
                                                    es=1.2))
    kit.box_mm(parent, pid + "_tbcount", tbx - 0.75, back_y + 1.65, 1.02,
               tbx + 0.75, back_y + 2.05, 1.1, b["wood_door"])

    # ---- marquee with bulb strip -----------------------------------------
    my0, my1 = skin + 0.02, skin + 1.75       # projects north
    kit.box_mm(parent, pid + "_marq", cx - 10.5, my0, 4.55, cx + 10.5, my1, 4.95,
               cream, bevel=0.02)
    kit.box_mm(parent, pid + "_marqf", cx - 10.6, my0 - 0.05, 4.48,
               cx + 10.6, my1 + 0.08, 5.02, tile_band)
    kit.box_mm(parent, pid + "_marqs", cx - 10.0, my0 + 0.1, 4.95,
               cx + 10.0, my1 - 0.1, 5.02, _solid("CIV_MARQ_TOP", "#5A5E5C", rough=0.85))
    bulb = _solid("CIV_MARQ_BULB", "#FFDB9C", rough=0.3,
                  emission="#FFC87A", es=26.0)
    for k in range(26):
        bx = cx - 10.2 + k * 0.82
        kit.sphere(parent, f"{pid}_bulb{k}", (bx, my1 + 0.06, 4.72), 0.075, bulb,
                   segments=8, rings=4)
    # pendant lamps under the marquee
    for k in (-1, 0, 1):
        px = cx + k * 4.4
        kit.cyl(parent, f"{pid}_mlamp{k + 1}", (px, my1 - 0.7, 4.36), 0.16, 0.16,
                _solid("CIV_MARQ_LAMP", "#E8D8B0", rough=0.4,
                       emission="#FFD9A0", es=10.0), verts=10)
    # dim warm underglow strip beneath the marquee slab
    kit.box_mm(parent, pid + "_marqund", cx - 9.6, my0 + 0.15, 4.47,
               cx + 9.6, my1 - 0.2, 4.55, _solid("CIV_MARQ_UNDER", "#F0D8A8",
                                               rough=0.5, emission="#FFD9A0", es=2.2))

    # ---- poster cases flanking the entrance ------------------------------
    for side in (-1, 1):
        px = cx + side * 8.6
        _poster_case(parent, f"{pid}_poster{side}", (px, skin + 0.1, 2.9),
                     (1, 0, 0), (0, 1, 0), 2.0, 3.2, rng)
        # lamp bar above each case
        kit.box(parent, f"{pid}_plamp{side}", (px, skin + 0.3, 4.62),
                (1.7, 0.16, 0.12), b["dark_metal"])
        kit.box(parent, f"{pid}_plbulb{side}", (px, skin + 0.3, 4.53),
                (1.6, 0.1, 0.05), _solid("CIV_POSTER_BULB", "#FFE0B0", rough=0.3,
                                         emission="#FFD9A0", es=14.0))

    # ---- facade sign ЗАРЯ + vertical КИНО blade sign ---------------------
    sign_mat = _solid("CIV_CINE_LETTERS", "#DAD4C2", rough=0.5,
                      emission="#D8D2C2", es=1.4)
    steel = _solid("CIV_CINE_STEEL", "#3C4244", rough=0.6, metallic=0.5)
    kit.text3d(parent, pid + "_zarya", "ЗАРЯ", (cx, skin + 0.14, 7.55), 1.85,
               sign_mat, rot=_rot_for((0, 1, 0)), extrude=0.16)
    for i, zz in enumerate((6.85, 8.2)):
        kit.box_mm(parent, pid + f"_zrail{i}", cx - 4.4, skin + 0.06, zz - 0.06,
                   cx + 4.4, skin + 0.2, zz + 0.06, steel)
    # vertical blade sign on the west end of the facade
    bx = ix0 + 2.2
    kit.box_mm(parent, pid + "_blade", bx - 0.13, skin + 0.2, 5.6,
               bx + 0.13, skin + 1.5, 10.4, _solid("CIV_BLADE_BG", "#2A3238",
                                                  rough=0.6))
    for i, ch in enumerate("КИНО"):
        zc = 10.0 - i * 1.12
        for side, sgn in ((-1, -1), (1, 1)):
            kit.text3d(parent, f"{pid}_blade_{i}_{side}", ch,
                       (bx + sgn * 0.17, skin + 0.85, zc), 0.82, sign_mat,
                       rot=_rot_for((sgn, 0, 0)), extrude=0.02)
    kit.box_mm(parent, pid + "_blade_arm", bx - 0.06, skin + 0.06, 9.9,
               bx + 0.06, skin + 0.24, 10.0, steel)

    # ---- steps up to the portal + side walls ------------------------------
    _steps(parent, pid + "_steps", (ix0 + portal_s0 - 1.2, skin + 0.86, 0),
           (1, 0, 0), (0, 1, 0), 0.0, portal_s1 - portal_s0 + 2.4, 0.86, 0.34, 3,
           b["step"])
    for k in range(3):
        kit.box(parent, f"{pid}_stestr{k}", (ix0 + portal_s0 - 1.3 + k * 0.0,
                                             skin + 0.4 - k * 0.28, 0.08 + k * 0.1),
                (0.3, 0.55, 0.2), b["step"])

    # ---- side / rear walls ------------------------------------------------
    for face in ("NX", "PX"):
        st = kit.facade_wall(parent, pid + "_" + face, inner, face, 0.0, z1,
                             cream, rng, wmats=wm, floors=1, floor_h=11.0,
                             win_w=1.2, win_h=1.2, spacing=4.2, margin=2.2,
                             lit_ratio=0.25, thickness=0.36, plinth_h=1.2,
                             plinth_mat=plinth, joints=True, ground_win_bottom=7.6)
        stats["windows"] += st["windows"]
        stats["lit"] += st["lit"]
    st = kit.facade_wall(parent, pid + "_NY", inner, "NY", 0.0, z1, cream, rng,
                         wmats=wm, floors=1, floor_h=11.0, win_w=1.4, win_h=1.3,
                         spacing=3.4, margin=2.0, lit_ratio=0.2, thickness=0.36,
                         plinth_h=1.2, plinth_mat=plinth, joints=True,
                         ground_win_bottom=2.2, ground_skip=[6.0, 26.0])
    stats["windows"] += st["windows"]
    stats["lit"] += st["lit"]
    # rear service door
    _recessed_door(parent, pid + "_rear", (ix0 + 6.0, iy0 - 0.36, 0), (1, 0, 0),
                   (0, -1, 0), 0.0, 0.0, 1.7, 2.5, rng, wm, mat=b["dark_metal"],
                   glass_frac=0.0, lit=False, double=False)
    kit.box_mm(parent, pid + "_rearcan", ix0 + 4.6, iy0 - 1.2, 2.75,
               ix0 + 7.4, iy0 - 0.1, 2.9, b["roof_dark"])

    # ---- round-2b: fly tower / backstage volume above the rear roof ------
    fly = (ix0 + 8.0, iy0 + 0.2, ix0 + 28.0, iy0 + 7.8)
    z_fly = 14.2
    kit.box_mm(parent, pid + "_fly", fly[0], fly[1], 0.0,
               fly[2], fly[3], z_fly, cream, bevel=0.02)
    kit.box_mm(parent, pid + "_fly_par", fly[0] - 0.08, fly[1] - 0.08, z_fly,
               fly[2] + 0.08, fly[3] + 0.08, z_fly + 0.5,
               _solid("CIV_KINO_FLY_CAP", "#7E8888", rough=0.85))
    # vertical panel seams on the visible top band of the fly tower
    for k in range(6):
        fx = fly[0] + 2.0 + k * 3.6
        kit.box(parent, f"{pid}_flyseam{k}", (fx, fly[1] - 0.03, (z1 + z_fly) / 2 + 0.6),
                (0.14, 0.08, z_fly - z1 - 0.4),
                _solid("CIV_KINO_SEAM", "#8A9290", rough=0.9))
    # narrow high windows on the fly-tower rear face
    for k, fx in enumerate((fly[0] + 3.2, fly[0] + 8.4, fly[0] + 13.6, fly[0] + 18.8)):
        _blind_window(parent, f"{pid}_flywin{k}", (0, fly[1], 0),
                      (1, 0, 0), (0, -1, 0), fx, 12.7, 0.75, 1.7,
                      _solid("CIV_KINO_FRAME", "#6E7474", rough=0.85),
                      _solid("CIV_KINO_GLASS", "#141A20", rough=0.55, metallic=0.0),
                      sill=False)
    # fly-tower roof vents
    for k, fx in enumerate((fly[0] + 4.0, fly[0] + 15.0)):
        _vent_box(parent, f"{pid}_flyvent{k}", (fx, (fly[1] + fly[3]) / 2, z_fly + 0.55),
                  1.3, 1.0, 0.8, rng)

    # ---- round-2b: rear (south) wall detail ------------------------------
    Pr, ur, Lr, nr = kit.wall_frame(inner, "NY")
    rear_face = Pr + nr * 0.36
    # big stage / prop door with canopy
    _service_door(parent, pid, rear_face, ur, nr, 24.0, w=2.8, h=3.1)
    # high narrow windows above the rear wall
    for k, (s, zc) in enumerate(((5.0, 8.4), (10.0, 8.7), (30.0, 8.6), (35.0, 8.4))):
        _blind_window(parent, f"{pid}_svc{k}", rear_face, ur, nr, s, zc, 0.9, 1.5,
                      _solid("CIV_KINO_FRAME", "#6E7474", rough=0.85),
                      _solid("CIV_KINO_GLASS", "#141A20", rough=0.55, metallic=0.0))
    # vents + plinth / frieze bands around the secondary walls
    _louvre(parent, pid + "_lvt0", rear_face, ur, nr, 16.0, 6.2, 1.1, 0.8)
    _louvre(parent, pid + "_lvt1", rear_face, ur, nr, 40.0 - 4.0, 3.6, 0.9, 0.7)
    _band_strip(parent, pid, inner, ("NY",), 3.85, 4.25,
                _solid("CIV_KINO_BAND", "#5E6E72", rough=0.85))
    _band_strip(parent, pid, inner, ("NX", "PX"), 1.25, 1.72,
                _solid("CIV_KINO_BAND", "#5E6E72", rough=0.85))
    _band_strip(parent, pid, inner, ("NY", "NX", "PX"), z1 - 0.55, z1 - 0.18,
                _solid("CIV_KINO_FRIEZE", "#B4B2A6", rough=0.9), tag="frieze")
    _panel_seams(parent, pid, inner, ("NX", "PX"), 1.72, z1 - 0.55, 5.2,
                 _solid("CIV_KINO_SEAM2", "#8A9290", rough=0.9))
    for i, sx in enumerate((5.5, 12.5)):
        _pallet(parent, f"{pid}_pal{i}", (ix0 + sx, iy0 - 1.1, 0.0), rng)
    kit.trash_bin(parent, pid + "_bin", (ix1 - 3.0, iy0 - 1.4, 0.0))

    # ---- roof --------------------------------------------------------------
    _roof_and_parapet(parent, pid, inner, z1, cream, parapet_h=0.75)
    _roof_clutter(parent, pid, inner, z1 + 0.1, rng, cream,
                  vents=((0, 0), (1, 1), (2, 2)), antennas=3)
    _vent_box(parent, pid + "_proj", (cx + 6.0, iy1 - 3.0, z1 + 0.12), 2.2, 1.8, 1.5, rng)
    _drainpipes(parent, pid, inner, z1)
    for face in ("NX", "PX"):
        Pf, uf, Lf, nf = kit.wall_frame(inner, face)
        _basement_grilles(parent, pid + "_" + face, Pf, uf, nf, [2.5, Lf - 2.5])
    for pre in (pid + "_NX", pid + "_PX"):
        _fix_xsills(parent, pre, wm)

    stats["objects"] = len(parent.children)
    stats["id"] = pid
    return stats


# ----------------------------------------------------------------------------
# DOM BYTA — household services house  (rect [-44,-104,-20,-92], faces PY)
# ----------------------------------------------------------------------------
def build_dom_byta(parent, block, rng, ctx):
    b = _wall_materials()
    wm = ctx.get("wmats") or kit.win_materials()
    pid = block["id"]
    x0, y0, x1, y1 = block["rect"]
    inner = (x0 + 0.5, y0 + 0.5, x1 - 0.5, y1 - 1.4)
    ix0, iy0, ix1, iy1 = inner
    z_band = 4.2
    z1 = 7.6
    cx = (ix0 + ix1) / 2
    stats = {"windows": 0, "lit": 0}
    wall = _tri_wall("CIV_DBT_WALL", "tex_concrete_panel.png", tint="#A3A79F", tile=4.2)
    plinth = b["plinth"]
    _apron(parent, pid, (x0 + 0.2, y0 + 0.2, x1 - 0.2, y1 - 0.2), b["walk"])
    P0, u, L, n = kit.wall_frame(inner, "PY")
    skin = iy1 + 0.36

    # ---- ground floor: big display windows + central door -----------------
    wins = [(1.8, 5.4), (5.8, 9.4), (13.6, 17.2), (17.6, 21.2)]
    door_s = ((ix1 - ix0) - 3.2) / 2
    opens = [(a, b, 1.05, 3.55) for (a, b) in wins]
    opens.append((door_s, door_s + 3.2, 0.0, 3.0))
    _panel_wall(parent, pid + "_front", inner, "PY", 0.0, z_band, wall,
                openings=opens, thick=0.36, plinth_z=0.55, plinth_mat=b["plinth_dark"])
    for wi, (s0, s1) in enumerate(wins):
        mid = (s0 + s1) / 2
        w = s1 - s0 - 0.16
        _interior_glow(parent, f"{pid}_dint{wi}", P0, u, n, s0 + 0.1, s1 - 0.1,
                       1.1, 3.5, rng, warm="#E0A876", strength=0.6)
        glass = _solid("CIV_DBT_GLASS", "#1A222C", rough=0.1, metallic=0.3, alpha=0.4)
        gc = P0 + u * mid + n * 0.18 + Vector((0, 0, 2.3))
        kit.quad(parent, f"{pid}_dg{wi}", tuple(gc), u, Vector((0, 0, 1)), w, 2.3, glass)
        for side in (-1, 1):
            kit.box(parent, f"{pid}_df{wi}{side}",
                    tuple(P0 + u * (mid + side * w / 2) + n * 0.1
                          + Vector((0, 0, 2.3))), (0.09, 0.2, 2.5), b["frame"])
        for zz in (1.05, 3.55):
            kit.box(parent, f"{pid}_dft{wi}{int(zz * 10)}",
                    tuple(P0 + u * mid + n * 0.1 + Vector((0, 0, zz))),
                    (w + 0.1, 0.2, 0.1), b["frame"])
        kit.box(parent, f"{pid}_dsh{wi}",
                tuple(P0 + u * mid + n * 0.16 + Vector((0, 0, 0.78))),
                (w + 0.2, 0.44, 0.46), b["plinth_dark"])
    # door + steps + canopy
    _recessed_door(parent, pid + "_door", (cx, skin, 0), (1, 0, 0), (0, 1, 0),
                   0.0, 0.3, 2.4, 2.9, rng, wm,
                   mat=_solid("CIV_DBT_DOOR", "#3E4A4C", rough=0.6),
                   glass_frac=0.8, lit=True, double=True)
    _steps(parent, pid + "_steps", (0, iy1 + 1.3, 0), (1, 0, 0), (0, 1, 0),
           cx - 2.2, cx + 2.2, 1.15, 0.3, 3, b["step"])
    kit.box_mm(parent, pid + "_can", cx - 2.8, iy1 + 0.08, 3.35,
               cx + 2.8, iy1 + 1.45, 3.55, wall, bevel=0.02)
    kit.box_mm(parent, pid + "_canf", cx - 2.86, iy1 + 0.04, 3.3,
               cx + 2.86, iy1 + 1.5, 3.62, b["clinic_trim"])
    _sign(parent, pid + "_sign", (cx, skin + 0.03, 4.15), (1, 0, 0), (0, 1, 0),
          8.0, 0.9, "ДОМ БЫТА", bg="#2E3B41", fg="#E7E2D2", glow=2.6, size_frac=0.6)
    _wall_lamp(parent, pid + "_wl0", (ix0 + 2.0, skin, 3.0), (1, 0, 0), (0, 1, 0))

    # ---- upper floor: small windows ---------------------------------------
    st = kit.facade_wall(parent, pid + "_up", inner, "PY", z_band, z1, wall, rng,
                         wmats=wm, floors=1, floor_h=3.4, win_w=1.25, win_h=1.15,
                         spacing=2.3, margin=1.5, lit_ratio=0.22, thickness=0.36,
                         plinth_h=0.0, joints=False, ground_win_bottom=0.85)
    stats["windows"] += st["windows"]
    stats["lit"] += st["lit"]
    # side + rear facades
    for face, kw in (
        ("PX", dict(floors=2, floor_h=3.5, win_w=1.3, win_h=1.2, spacing=3.0,
                    margin=1.6)),
        ("NX", dict(floors=2, floor_h=3.5, win_w=1.3, win_h=1.2, spacing=3.0,
                    margin=1.6)),
        ("NY", dict(floors=2, floor_h=3.5, win_w=1.4, win_h=1.3, spacing=3.2,
                    margin=1.8)),
    ):
        st = kit.facade_wall(parent, pid + "_" + face, inner, face, 0.0, z1,
                             wall, rng, wmats=wm, lit_ratio=0.18, thickness=0.36,
                             plinth_h=1.1, plinth_mat=plinth, joints=True,
                             ground_win_bottom=1.5, **kw)
        stats["windows"] += st["windows"]
        stats["lit"] += st["lit"]
    # rear service door
    Pd, ud, Ld, nd = kit.wall_frame(inner, "NY")
    _recessed_door(parent, pid + "_rear", (ix0 + 4.0, iy0 - 0.36, 0), (1, 0, 0),
                   (0, -1, 0), 0.0, 0.0, 1.7, 2.5, rng, wm, mat=b["dark_metal"],
                   glass_frac=0.0, lit=False, double=False)
    kit.box_mm(parent, pid + "_rearcan", ix0 + 2.6, iy0 - 1.1, 2.75,
               ix0 + 5.4, iy0 - 0.1, 2.9, b["roof_dark"])
    kit.trash_bin(parent, pid + "_bin", (ix1 - 2.0, iy0 - 1.4, 0.0))

    # ---- roof + details ----------------------------------------------------
    _roof_and_parapet(parent, pid, inner, z1, wall, parapet_h=0.6)
    _roof_clutter(parent, pid, inner, z1 + 0.1, rng, wall,
                  vents=((0, 0), (1, 1)), antennas=2)
    _chimney(parent, pid + "_pipe", (ix0 + 2.0, iy1 - 2.6, z1 + 0.12), 0.6, 0.6, 2.1)
    _wall_lamp(parent, pid + "_wl", (cx + 3.4, skin, 3.0), (1, 0, 0), (0, 1, 0))
    _drainpipes(parent, pid, inner, z1)
    for pre in (pid + "_PX", pid + "_NX"):
        _fix_xsills(parent, pre, wm)

    # ---- round-2b: secondary facades (rear + sides) -----------------------
    Pdr, udr, Ldr, ndr = kit.wall_frame(inner, "NY")
    dbt_rear = Pdr + ndr * 0.36
    # painted plinth band + frieze band on the non-main walls
    _band_strip(parent, pid, inner, ("NX", "PX"), 0.72, 1.05,
                _solid("CIV_DBT_BAND", "#5E6E72", rough=0.85))
    _band_strip(parent, pid, inner, ("NY",), 2.85, 3.2,
                _solid("CIV_DBT_BAND", "#5E6E72", rough=0.85))
    _band_strip(parent, pid, inner, ("NY", "NX", "PX"), z1 - 0.5, z1 - 0.16,
                _solid("CIV_DBT_FRIEZE", "#B6B4A8", rough=0.9), tag="frieze")
    _panel_seams(parent, pid, inner, ("NX", "PX"), 1.64, z1 - 0.5, 3.6,
                 _solid("CIV_DBT_SEAM", "#6E7470", rough=0.9))
    # small high service windows on the rear wall (above the window rows)
    frame = _solid("CIV_SVC_FRAME", "#8E9088", rough=0.85)
    glass = _solid("CIV_SVC_GLASS", "#151C22", rough=0.55, metallic=0.0)
    for k, (s, zc) in enumerate(((4.2, 6.5), (9.0, 6.7), (15.0, 6.5), (20.0, 6.7))):
        _blind_window(parent, f"{pid}_svc{k}", dbt_rear, udr, ndr, s, zc, 0.85, 0.7,
                      frame, glass)
    _louvre(parent, pid + "_lvt0", dbt_rear, udr, ndr, 3.0, 4.6, 0.8, 0.6)
    _louvre(parent, pid + "_lvt1", dbt_rear, udr, ndr, 16.5, 3.6, 0.7, 0.55)
    # service lamp over the existing rear door + pallets
    _wall_lamp(parent, pid + "_lamp2R", (dbt_rear.x + 4.0, dbt_rear.y, 2.9),
               udr, ndr)
    _pallet(parent, pid + "_pal0", (ix0 + 8.2, iy0 - 1.0, 0.0), rng)
    _pallet(parent, pid + "_pal1", (ix1 - 3.2, iy0 - 1.1, 0.0), rng)
    # side service door + canopy on the west flank
    Pds, uds, Lds, nds = kit.wall_frame(inner, "NX")
    _service_door(parent, pid, Pds + nds * 0.36, uds, nds, 6.5, w=2.2, h=2.5)

    stats["objects"] = len(parent.children)
    stats["id"] = pid
    return stats


# ----------------------------------------------------------------------------
# BAKERY — Булочная  (rect [-8,-102,6,-92], faces PY)
# ----------------------------------------------------------------------------
def build_bakery(parent, block, rng, ctx):
    b = _wall_materials()
    wm = ctx.get("wmats") or kit.win_materials()
    pid = block["id"]
    x0, y0, x1, y1 = block["rect"]
    inner = (x0 + 0.5, y0 + 0.5, x1 - 0.5, y1 - 1.3)
    ix0, iy0, ix1, iy1 = inner
    z1 = 5.5
    cx = (ix0 + ix1) / 2
    stats = {"windows": 0, "lit": 0}
    wall = _tri_wall("CIV_BAK_WALL", "tex_panel_painted.png", tint="#A7ADA2", tile=3.4)
    plinth = b["plinth"]
    _apron(parent, pid, (x0 + 0.2, y0 + 0.2, x1 - 0.2, y1 - 0.2), b["walk"])
    P0, u, L, n = kit.wall_frame(inner, "PY")
    skin = iy1 + 0.36

    # ---- storefront: big warm window + service door -----------------------
    win_s0, win_s1 = 1.1, 7.6
    door_s0, door_s1 = 8.6, 10.9
    opens = [(win_s0, win_s1, 0.95, 3.4), (door_s0, door_s1, 0.0, 2.8)]
    _panel_wall(parent, pid + "_front", inner, "PY", 0.0, 3.9, wall,
                openings=opens, thick=0.36, plinth_z=0.5,
                plinth_mat=b["plinth_dark"])
    # warm glowing shop window with bread shelves
    glow = _solid("CIV_BAK_GLOW", "#F2A868", rough=0.9,
                  emission="#F2A868", es=1.35)
    wmid = (win_s0 + win_s1) / 2
    ww = win_s1 - win_s0 - 0.2
    kit.quad(parent, pid + "_glow", tuple(P0 + u * wmid + n * -0.5
                                          + Vector((0, 0, 2.15))),
             u, Vector((0, 0, 1)), ww, 2.35, glow)
    # shelves + bread silhouettes
    wood = _solid("CIV_BAK_WOOD", "#6A5A46", rough=0.9)
    bread = _solid("CIV_BAK_BREAD", "#5E4632", rough=0.95)
    for si, zz in enumerate((1.35, 2.0, 2.65)):
        kit.box(parent, f"{pid}_shelf{si}",
                tuple(P0 + u * wmid - n * 0.18 + Vector((0, 0, zz))),
                (ww - 0.3, 0.2, 0.1), wood)
        nb = 6
        for k in range(nb):
            t = (k + 0.5) / nb
            bx = win_s0 + 0.25 + (ww - 0.5) * t
            for j in range(2):
                bc = P0 + u * bx - n * (0.16 + j * 0.1) + Vector((0, 0, zz + 0.16))
                kit.box(parent, f"{pid}_bread{si}{k}{j}", (bc.x, bc.y, bc.z),
                        (0.4, 0.24, 0.2), bread)
    # glass + frame
    glass = _solid("CIV_BAK_GLASS", "#241E1A", rough=0.12, metallic=0.25, alpha=0.28)
    gc = P0 + u * wmid + n * 0.16 + Vector((0, 0, 2.18))
    kit.quad(parent, pid + "_gwin", tuple(gc), u, Vector((0, 0, 1)), ww, 2.35, glass)
    for side in (-1, 1):
        kit.box(parent, f"{pid}_wf{side}",
                tuple(P0 + u * (wmid + side * ww / 2) + n * 0.1
                      + Vector((0, 0, 2.18))), (0.1, 0.2, 2.4), b["frame"])
    for zz in (0.95, 3.4):
        kit.box(parent, f"{pid}_wft{int(zz * 10)}",
                tuple(P0 + u * wmid + n * 0.1 + Vector((0, 0, zz))),
                (ww + 0.1, 0.2, 0.11), b["frame"])
    kit.box(parent, f"{pid}_stall",
            tuple(P0 + u * wmid + n * 0.17 + Vector((0, 0, 0.7))),
            (ww + 0.2, 0.5, 0.4), b["plinth_dark"])
    # striped awning over the window
    red = _solid("CIV_BAK_AWN_A", "#7E362C", rough=0.85)
    cream = _solid("CIV_BAK_AWN_B", "#C6BBA2", rough=0.85)
    _stripe_awning(parent, pid + "_awn", (0, skin, 0), (1, 0, 0), (0, 1, 0),
                   ix0 + win_s0 - 0.2, ix0 + win_s1 + 0.2, 3.55, 1.25, 0.4,
                   red, cream, red, cream)
    # service door + step
    _recessed_door(parent, pid + "_door", (0, skin, 0), (1, 0, 0), (0, 1, 0),
                   ix0 + (door_s0 + door_s1) / 2, 0.0, 2.2, 2.6, rng, wm,
                   mat=_solid("CIV_BAK_DOOR", "#4A4636", rough=0.7),
                   glass_frac=0.6, lit=True, double=False)
    _steps(parent, pid + "_steps", (0, iy1 + 0.85, 0), (1, 0, 0), (0, 1, 0),
           ix0 + door_s0 - 0.4, ix0 + door_s1 + 0.4, 0.8, 0.2, 2, b["step"])

    # ---- sign + upper band -------------------------------------------------
    _sign(parent, pid + "_sign", (cx, skin + 0.03, 4.4), (1, 0, 0), (0, 1, 0),
          8.4, 0.95, "БУЛОЧНАЯ", bg="#37302A", fg="#EFE7D2", glow=2.8,
          size_frac=0.64)
    _wall_lamp(parent, pid + "_wl", (cx, skin, 3.5), (1, 0, 0), (0, 1, 0))

    # ---- upper band: plain frieze with two small end windows --------------
    _panel_wall(parent, pid + "_up", inner, "PY", 3.9, z1, wall,
                openings=[(1.1, 2.2, 4.15, 5.05), (10.8, 11.9, 4.15, 5.05)],
                thick=0.36)
    for k, ss in enumerate((1.65, 11.35)):
        _custom_window(parent, f"{pid}_upw{k}", P0, u, n, ss, 4.6, 1.0, 0.85,
                       rng, wm, lit_ratio=0.3)
        stats["windows"] += 1
    for face in ("PX", "NX", "NY"):
        st = kit.facade_wall(parent, pid + "_" + face, inner, face, 0.0, z1,
                             wall, rng, wmats=wm, floors=1, floor_h=3.9,
                             win_w=1.3, win_h=1.3, spacing=2.9, margin=1.5,
                             lit_ratio=0.22, thickness=0.36, plinth_h=1.0,
                             plinth_mat=plinth, joints=True, ground_win_bottom=1.6)
        stats["windows"] += st["windows"]
        stats["lit"] += st["lit"]
    # rear delivery door
    _recessed_door(parent, pid + "_rear", (ix0 + 2.4, iy0 - 0.36, 0), (1, 0, 0),
                   (0, -1, 0), 0.0, 0.0, 1.6, 2.4, rng, wm, mat=b["dark_metal"],
                   glass_frac=0.0, lit=False, double=False)
    kit.box_mm(parent, pid + "_rearcan", ix0 + 1.2, iy0 - 1.0, 2.65,
               ix0 + 3.6, iy0 - 0.1, 2.8, b["roof_dark"])

    # ---- roof + details ----------------------------------------------------
    _roof_and_parapet(parent, pid, inner, z1, wall, parapet_h=0.55)
    _roof_clutter(parent, pid, inner, z1 + 0.1, rng, wall,
                  vents=((0, 0),), antennas=2)
    _chimney(parent, pid + "_pipe", (ix1 - 1.6, iy1 - 2.2, z1 + 0.12), 0.5, 0.5, 1.8)
    _drainpipes(parent, pid, inner, z1)
    for pre in (pid + "_PX", pid + "_NX"):
        _fix_xsills(parent, pre, wm)

    # ---- round-2b: secondary facades (rear + sides) -----------------------
    Pbr, ubr, Lbr, nbr = kit.wall_frame(inner, "NY")
    bak_rear = Pbr + nbr * 0.36
    _band_strip(parent, pid, inner, ("NX", "PX"), 0.6, 0.95,
                _solid("CIV_BAK_BAND", "#6E5A50", rough=0.88))
    _band_strip(parent, pid, inner, ("NY",), 3.05, 3.4,
                _solid("CIV_BAK_BAND", "#6E5A50", rough=0.88))
    _band_strip(parent, pid, inner, ("NY", "NX", "PX"), z1 - 0.42, z1 - 0.14,
                _solid("CIV_BAK_FRIEZE", "#B8B4A6", rough=0.9), tag="frieze")
    _panel_seams(parent, pid, inner, ("NX", "PX"), 1.4, z1 - 0.42, 2.9,
                 _solid("CIV_BAK_SEAM", "#767A74", rough=0.9))
    frame = _solid("CIV_SVC_FRAME", "#8E9088", rough=0.85)
    glass = _solid("CIV_SVC_GLASS", "#151C22", rough=0.55, metallic=0.0)
    for k, (s, zc) in enumerate(((3.6, 4.55), (8.2, 4.75))):
        _blind_window(parent, f"{pid}_svc{k}", bak_rear, ubr, nbr, s, zc, 0.7, 0.6,
                      frame, glass)
    _louvre(parent, pid + "_lvt0", bak_rear, ubr, nbr, 6.0, 2.6, 0.7, 0.5)
    _wall_lamp(parent, pid + "_lamp2R", (bak_rear.x + 2.4, bak_rear.y, 2.75),
               ubr, nbr)
    _pallet(parent, pid + "_pal0", (ix0 + 6.0, iy0 - 1.0, 0.0), rng)
    kit.trash_bin(parent, pid + "_binR", (ix1 - 1.3, iy0 - 1.2, 0.0))
    # side service door + canopy on the east flank
    Pbs, ubs, Lbs, nbs = kit.wall_frame(inner, "PX")
    _service_door(parent, pid, Pbs + nbs * 0.36, ubs, nbs, 3.0, w=2.0, h=2.4)

    stats["objects"] = len(parent.children)
    stats["id"] = pid
    return stats


TYPES = {
    "dk": build_dk,
    "school": build_school,
    "kindergarten": build_kg,
    "clinic": build_clinic,
    "shop": build_shop,
    # round 2
    "banya": build_banya,
    "bathhouse": build_banya,
    "market": build_market,
    "cinema": build_cinema,
    "dom_byta": build_dom_byta,
    "dombyta": build_dom_byta,
    "bakery": build_bakery,
}
