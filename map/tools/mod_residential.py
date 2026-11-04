"""mod_residential.py — Soviet residential buildings for the Blue-Hour Microdistrict.

Khrushchyovka variants (panel / brick / through-arch / modified) and 9-storey
brezhnevka towers.  Interface per docs/MODULE_CONTRACT.md:

    TYPES = {"khrush_panel": build_khrush_panel, ...}
    build_xxx(parent, block, rng, ctx) -> stats dict

Conventions
-----------
* Z up, metres, ground z = 0.  +Y = north, +X = east.
* block["rect"] = (x0, y0, x1, y1) — the building OUTER footprint.
* `kit.facade_wall` builds walls OUTSIDE the rect it is given, so every builder
  passes a rect shrunk by the wall thickness ("rs").  The four 0.35 x 0.35 corner
  gaps are filled by corner piers; the result is a shell whose outer faces sit
  exactly on the plan rect (balconies / canopies protrude <= 1.2 m).
* Every object is named "<block id>_..." and parented to `parent`.
"""
from __future__ import annotations

import math

import bpy
from mathutils import Vector

import kit

THICK = 0.35          # facade wall thickness (kit convention)
Z0 = 0.0              # buildings stand on z = 0
EMPTY = Vector((0.0, 0.0, 0.0))

# ----------------------------------------------------------------------------
# materials
# ----------------------------------------------------------------------------
_M = {}


def _tex_mat(name, filename, scale=2.8, tint=None, tint_factor=0.5, rough=0.92,
             metallic=0.0):
    """Box-projected weathered material — works on kit boxes that have no UVs."""
    if name in _M:
        return _M[name]
    path = kit.find_image(filename)
    if not path:
        m = kit.mat_solid(name + "_FB", tint or "#A0A8A8", rough)
        _M[name] = m
        return m
    img = bpy.data.images.load(path, check_existing=True)
    img.colorspace_settings.name = "sRGB"
    mat = bpy.data.materials.new(name)
    mat.use_nodes = True
    nt = mat.node_tree
    b = nt.nodes.get("Principled BSDF")
    tc = nt.nodes.new("ShaderNodeTexCoord")
    mp = nt.nodes.new("ShaderNodeMapping")
    mp.inputs["Scale"].default_value = (1.0 / scale, 1.0 / scale, 1.0 / scale)
    tex = nt.nodes.new("ShaderNodeTexImage")
    tex.image = img
    tex.projection = "BOX"
    tex.projection_blend = 0.22
    nt.links.new(tc.outputs["Object"], mp.inputs["Vector"])
    nt.links.new(mp.outputs["Vector"], tex.inputs["Vector"])
    src = tex.outputs["Color"]
    if tint is not None:
        mix = nt.nodes.new("ShaderNodeMix")
        mix.data_type = "RGBA"
        mix.blend_type = "MULTIPLY"
        mix.inputs["Factor"].default_value = tint_factor
        mix.inputs[6].default_value = kit.srgb(tint)
        nt.links.new(tex.outputs["Color"], mix.inputs[7])
        src = mix.outputs[2]
    nt.links.new(src, b.inputs["Base Color"])
    b.inputs["Roughness"].default_value = rough
    b.inputs["Metallic"].default_value = metallic
    _M[name] = mat
    return mat


def mats():
    if _M.get("_ready"):
        return _M
    M = _M
    # --- weathered facade materials (GPT texture, box projected) ---------------
    M["wall_mint"] = _tex_mat("RES_WALL_MINT", "tex_panel_painted.png", 2.75,
                              tint="#A9C4B6", tint_factor=0.30)
    M["wall_blue"] = _tex_mat("RES_WALL_BLUE", "tex_concrete_panel.png", 2.9,
                              tint="#93A6BA", tint_factor=0.55)
    M["wall_offwhite"] = _tex_mat("RES_WALL_OFF", "tex_panel_painted.png", 2.7,
                                  tint="#CFCEC2", tint_factor=0.4)
    M["wall_grey"] = _tex_mat("RES_WALL_GREY", "tex_concrete_panel.png", 3.0,
                              tint="#A6ADB2", tint_factor=0.45)
    M["brick_pale"] = _tex_mat("RES_BRICK_PALE", "tex_brick_plaster.png", 2.5,
                               tint="#C3C6C2", tint_factor=0.25)
    M["brick_warm"] = _tex_mat("RES_BRICK_WARM", "tex_brick_plaster.png", 2.5)
    M["brick_cool"] = _tex_mat("RES_BRICK_COOL", "tex_brick_plaster.png", 2.5,
                               tint="#B9BEC4", tint_factor=0.3)
    # --- solids ---------------------------------------------------------------
    M["plinth"] = kit.mat_solid("RES_PLINTH", kit.PAL["plinth"], rough=0.95)
    M["plinth_dark"] = kit.mat_solid("RES_PLINTH_D", kit.PAL["plinth_dark"], rough=0.96)
    M["conc"] = kit.mat_solid("RES_CONC", "#A9ACA4", rough=0.9)
    M["conc_grey"] = kit.mat_solid("RES_CONC_G", "#8D949A", rough=0.92)
    M["conc_dark"] = kit.mat_solid("RES_CONC_D", "#6A6E6C", rough=0.94)
    M["roof"] = kit.mat_solid("RES_ROOF", "#3D4348", rough=0.9)
    M["roof_dark"] = kit.mat_solid("RES_ROOF_D", "#2B3034", rough=0.85)
    M["metal"] = kit.mat_solid("RES_METAL", "#3C4246", rough=0.55, metallic=0.55)
    M["metal_grey"] = kit.mat_solid("RES_METAL_G", "#565B5C", rough=0.6, metallic=0.4)
    M["rust"] = kit.mat_solid("RES_RUST", kit.PAL["rust"], rough=0.82, metallic=0.2)
    M["rust_dark"] = kit.mat_solid("RES_RUST_D", "#4A342A", rough=0.88)
    M["door_metal"] = kit.mat_solid("RES_DOOR", "#383E40", rough=0.5, metallic=0.55)
    M["door_red"] = kit.mat_solid("RES_DOOR_RED", "#4B3730", rough=0.62, metallic=0.25)
    M["wood_frame"] = kit.mat_solid("RES_WOOD_FRAME", "#5C4A39", rough=0.8)
    M["white_frame"] = kit.mat_solid("RES_WHITE_FRAME", kit.PAL["frame_white"], rough=0.7)
    M["band_blue"] = kit.mat_solid("RES_BAND_BLUE", "#8396A8", rough=0.8)
    M["band_white"] = kit.mat_solid("RES_BAND_WHITE", "#B4B6AE", rough=0.82)
    M["tile_blue"] = kit.mat_solid("RES_TILE_BLUE", "#7D93A4", rough=0.45)
    M["brick_annex"] = _tex_mat("RES_BRICK_ANNEX", "tex_brick_plaster.png", 2.2,
                                tint="#A8836C", tint_factor=0.55)
    M["brick_chimney"] = kit.mat_solid("RES_CHIMNEY", "#6D5F55", rough=0.9)
    M["brick_chimney2"] = kit.mat_solid("RES_CHIMNEY2", "#7C6B5F", rough=0.9)
    M["glass"] = kit.mat_solid("RES_GLASS", "#121A22", rough=0.12, metallic=0.35)
    M["glass_dim"] = kit.mat_solid("RES_GLASS_DIM", "#1B2730", rough=0.2, metallic=0.2)
    M["lamp_warm"] = kit.mat_solid("RES_LAMP", "#FFD8A0", rough=0.25,
                                   emission="#FFC074", emission_strength=16.0)
    M["lamp_porch"] = kit.mat_solid("RES_LAMP_PORCH", "#FFE2B0", rough=0.25,
                                    emission="#FFC57A", emission_strength=10.0)
    M["lamp_tunnel"] = kit.mat_solid("RES_LAMP_TUNNEL", "#FFE7C0", rough=0.25,
                                     emission="#FFD08A", emission_strength=22.0)
    M["lamp_stair"] = kit.mat_solid("RES_LAMP_STAIR", "#E8F0E6", rough=0.25,
                                    emission="#CFE2D8", emission_strength=7.0)
    M["sign_bg"] = kit.mat_solid("RES_SIGN_BG", "#2B353B", rough=0.65)
    M["sign_fg"] = kit.mat_solid("RES_SIGN_FG", "#DCD9C8", rough=0.4,
                                 emission="#E9E4D0", emission_strength=1.6)
    M["plate"] = kit.mat_solid("RES_PLATE", "#D3D1C7", rough=0.6)
    M["num"] = kit.mat_solid("RES_NUM", "#20242A", rough=0.8)
    M["shop_glow"] = kit.mat_solid("RES_SHOP_GLOW", "#FFD9A2", rough=0.4,
                                   emission="#FFC078", emission_strength=2.6)
    M["dirt"] = kit.mat_solid("RES_DIRT", "#2C2A25", rough=1.0)
    M["sill"] = kit.mat_solid("RES_SILL", "#B6B8B0", rough=0.88)
    M["_ready"] = True
    return M


# ----------------------------------------------------------------------------
# geometry helpers (orientation independent)
# ----------------------------------------------------------------------------
def _rect(block):
    x0, y0, x1, y1 = block["rect"]
    return (float(x0), float(y0), float(x1), float(y1))


def _shrunk(rect):
    x0, y0, x1, y1 = rect
    return (x0 + THICK, y0 + THICK, x1 - THICK, y1 - THICK)


def _dims(rect):
    x0, y0, x1, y1 = rect
    return x1 - x0, y1 - y0


def _long_axis(rect):
    dx, dy = _dims(rect)
    return "Y" if dy > dx else "X"


def _long_faces(rect):
    """The two long facades of a rectangular block."""
    return ("PX", "NX") if _long_axis(rect) == "Y" else ("PY", "NY")


def _end_faces(rect):
    return ("NY", "PY") if _long_axis(rect) == "Y" else ("NX", "PX")


def _face_len(rect, face):
    dx, dy = _dims(rect)
    return dy if face in ("PX", "NX") else dx


def _cols(length, spacing, margin):
    out = []
    s = margin
    while s < length - margin + 1e-6:
        out.append(s)
        s += spacing
    return out


def _cols_sym(length, spacing, min_margin=1.35):
    """Evenly distributed, symmetric window columns (arch / tower rhythm)."""
    if length < 2 * min_margin + 0.2:
        return [length / 2.0]
    n = int((length - 2 * min_margin) / spacing + 1e-6) + 1
    margin = (length - (n - 1) * spacing) / 2.0
    return [margin + i * spacing for i in range(n)]


def _fbox(parent, name, P0, u, n, s0, s1, o0, o1, za, zb, mat, bevel=0.0):
    """Axis-aligned box given facade frame (P0,u,n) and s / out / z extents."""
    su, sn, sz = abs(s1 - s0), abs(o1 - o0), abs(zb - za)
    if su < 1e-4 or sn < 1e-4 or sz < 1e-4:
        return None
    c = (P0 + u * ((s0 + s1) / 2.0) + n * ((o0 + o1) / 2.0)
         + Vector((0.0, 0.0, (za + zb) / 2.0)))
    if abs(u.x) > 0.5:
        size = (su, sn, sz)
    else:
        size = (sn, su, sz)
    return kit.box(parent, name, tuple(c), size, mat, bevel=bevel)


def _fquad(parent, name, base, u, v, du, dv, mat, uv=None):
    """Quad centred at `base`, spanned by u*du and v*dv."""
    c = base + u * (du / 2.0) + v * (dv / 2.0)
    return kit.quad(parent, name, tuple(c), u, v, du, dv, mat, uv=uv)


def _sbox(parent, name, center, u, n, su, sn, sz, mat, bevel=0.0):
    """Box centred at `center` with sizes along u / n / z."""
    if abs(u.x) > 0.5:
        size = (su, sn, sz)
    else:
        size = (sn, su, sz)
    return kit.box(parent, name, tuple(center), size, mat, bevel=bevel)


def _disc(parent, name, center, radius, depth, mat, n, verts=14):
    if abs(n.y) > 0.5:
        rot = (math.radians(-90 if n.y > 0 else 90), 0.0, 0.0)
    else:
        rot = (0.0, math.radians(90 if n.x > 0 else -90), 0.0)
    return kit.cyl(parent, name, tuple(center), radius, depth, mat, rot=rot,
                   verts=verts)


def _dish_rot(n, elev_deg=26.0):
    """Cylinder euler so its +Z axis points along n lifted up by elev."""
    e = math.radians(elev_deg)
    if abs(n.y) > 0.5:
        return (math.radians(-(90 - elev_deg)) if n.y > 0
                else math.radians(90 - elev_deg), 0.0, 0.0)
    return (0.0, math.radians(90 - elev_deg) if n.x > 0
            else math.radians(-(90 - elev_deg)), 0.0)


def _text_rot(n):
    if n.y > 0.5:
        zrot = math.pi
    elif n.y < -0.5:
        zrot = 0.0
    elif n.x > 0.5:
        zrot = math.pi / 2
    else:
        zrot = -math.pi / 2
    return (math.radians(90), 0.0, zrot)


def _text(parent, name, body, center, size, mat, n):
    return kit.text3d(parent, name, body, tuple(center), size, mat,
                      rot=_text_rot(n))


# ----------------------------------------------------------------------------
# windows / facade decoration
# ----------------------------------------------------------------------------
def _pick_glass(rng, lit_ratio, avoid_boarded=True):
    wm = kit.win_materials()
    if rng.random() < lit_ratio:
        return wm[rng.wpick(["warm_a", "warm_b", "dim", "stair"],
                            [0.4, 0.28, 0.16, 0.16])]
    keys = ["dark_a", "dark_b"] if avoid_boarded else ["dark_a", "dark_b", "boarded"]
    return wm[rng.pick(keys)]


def _flat_window(parent, name, P0, u, n, s, zc, w, h, out, rng,
                 lit_ratio=0.0, frame_mat=None):
    """Window drawn onto an existing wall plane (stair bays / annexes)."""
    base = P0 + u * s + n * out + Vector((0.0, 0.0, zc))
    gmat = _pick_glass(rng, lit_ratio)
    uv = [(0.14, 0.12), (0.14, 0.85), (0.86, 0.85), (0.86, 0.12)]
    kit.quad(parent, name + "_g", tuple(base + n * 0.004), u, (0, 0, 1),
             w - 0.05, h - 0.05, gmat, uv=uv)
    f = frame_mat or kit.win_materials()["frame"]
    fo = out + 0.02
    _fbox(parent, name + "_fv0", P0, u, n, s - w / 2, s - w / 2 + 0.055, fo, fo + 0.06,
          zc - h / 2, zc + h / 2, f)
    _fbox(parent, name + "_fv1", P0, u, n, s + w / 2 - 0.055, s + w / 2, fo, fo + 0.06,
          zc - h / 2, zc + h / 2, f)
    _fbox(parent, name + "_fh0", P0, u, n, s - w / 2, s + w / 2, fo, fo + 0.06,
          zc - h / 2, zc - h / 2 + 0.05, f)
    _fbox(parent, name + "_fh1", P0, u, n, s - w / 2, s + w / 2, fo, fo + 0.06,
          zc + h / 2 - 0.05, zc + h / 2, f)
    return gmat


def _ext_sills(parent, name, P0, u, n, cols, rows, w, out, win_h=1.5):
    """Small projecting concrete sills under every window — breaks up the wall."""
    mat = mats()["sill"]
    for fi, zc in enumerate(rows):
        for ci, s in enumerate(cols):
            _fbox(parent, f"{name}_sill{fi}_{ci}", P0, u, n,
                  s - w / 2 - 0.1, s + w / 2 + 0.1, out, out + 0.16,
                  zc - win_h / 2 - 0.055, zc - win_h / 2 + 0.015, mat)


def _sat_dish(parent, name, P0, u, n, s, z, rng, scale=1.0):
    base = P0 + u * s + n * (THICK + 0.02) + Vector((0.0, 0.0, z))
    metal = mats()["metal_grey"]
    # bracket
    _fbox(parent, name + "_arm", P0, u, n, s - 0.03, s + 0.03,
          THICK + 0.02, THICK + 0.30, z - 0.03, z + 0.03, metal)
    c = base + n * 0.34
    d = 0.40 * scale
    kit.cyl(parent, name + "_dish", tuple(c), d, 0.05, mats()["conc_grey"],
            rot=_dish_rot(n), verts=14)
    axis = (n * math.cos(math.radians(26)) + Vector((0, 0, 1)) * math.sin(math.radians(26))).normalized()
    kit.cyl_between(parent, name + "_feed", tuple(c + axis * 0.05),
                    tuple(c + axis * 0.42), 0.018, metal, verts=5)
    kit.cyl(parent, name + "_feedc", tuple(c + axis * 0.44), 0.045, 0.05,
            mats()["metal"], rot=_dish_rot(n), verts=8)


def _ac_unit(parent, name, P0, u, n, s, z):
    metal = mats()["metal_grey"]
    dark = mats()["metal"]
    _fbox(parent, name + "_b", P0, u, n, s - 0.40, s + 0.40, THICK, THICK + 0.42,
          z, z + 0.52, metal)
    _fbox(parent, name + "_br", P0, u, n, s - 0.42, s + 0.42, THICK + 0.36,
          THICK + 0.44, z - 0.05, z + 0.02, dark)
    _disc(parent, name + "_fan", P0 + u * s + n * (THICK + 0.44)
          + Vector((0, 0, z + 0.26)), 0.19, 0.03, dark, n, verts=10)
    _fbox(parent, name + "_leg0", P0, u, n, s - 0.3, s - 0.24, THICK + 0.1,
          THICK + 0.4, z - 0.16, z, dark)
    _fbox(parent, name + "_leg1", P0, u, n, s + 0.24, s + 0.3, THICK + 0.1,
          THICK + 0.4, z - 0.16, z, dark)


def _balcony(parent, name, P0, u, n, s, floor_z, width, depth, kind, rng,
             wall_mat=None):
    """Balcony / glazed loggia, orientation-safe (kit.balcony_unit is PY-only)."""
    M = mats()
    slab = M["conc_grey"] if rng.random() < 0.6 else M["plinth"]
    z = floor_z
    _fbox(parent, name + "_slab", P0, u, n, s - width / 2, s + width / 2,
          -0.06, depth, z, z + 0.17, slab)
    zr = z + 0.17
    if kind == "glazed":
        frame = M["white_frame"]
        # bottom opaque panel (metal sheet / painted slab)
        _fbox(parent, name + "_panel", P0, u, n, s - width / 2 + 0.03,
              s + width / 2 - 0.03, depth - 0.10, depth - 0.04, zr, zr + 0.85,
              M["band_white"] if rng.random() < 0.5 else M["metal_grey"])
        # front glazing
        gbase = P0 + u * s + n * (depth - 0.07) + Vector((0, 0, zr + 1.35))
        kit.quad(parent, name + "_glass", tuple(gbase), u, (0, 0, 1),
                 width - 0.18, 1.05, M["glass"])
        # side glazing
        for side in (-1, 1):
            sc = P0 + u * (s + side * (width / 2 - 0.02)) + n * (depth / 2) \
                + Vector((0, 0, zr + 1.35))
            kit.quad(parent, name + f"_sg{side}", tuple(sc), n, (0, 0, 1),
                     depth - 0.28, 1.05, M["glass"])
        # white PVC frame: verticals + rails + corner posts
        for i in range(4):
            ss = s - width / 2 + 0.05 + i * (width - 0.10) / 3.0
            _fbox(parent, name + f"_fv{i}", P0, u, n, ss - 0.03, ss + 0.03,
                  depth - 0.12, depth - 0.05, zr, zr + 2.25, frame)
        _fbox(parent, name + "_ftop", P0, u, n, s - width / 2, s + width / 2,
              depth - 0.13, depth - 0.04, zr + 2.20, zr + 2.28, frame)
        _fbox(parent, name + "_fbot", P0, u, n, s - width / 2, s + width / 2,
              depth - 0.13, depth - 0.04, zr + 0.80, zr + 0.88, frame)
        for side in (-1, 1):
            ss = s + side * (width / 2 - 0.05)
            _fbox(parent, name + f"_sp{side}", P0, u, n, ss - 0.04, ss + 0.04,
                  depth - 0.16, depth + 0.02, zr, zr + 2.26, frame)
        # roof slab of the loggia
        _fbox(parent, name + "_lid", P0, u, n, s - width / 2, s + width / 2,
              -0.02, depth, zr + 2.26, zr + 2.36, M["conc_grey"])
    else:
        m = M["rust"] if rng.random() < 0.5 else M["metal_grey"]
        for side in (-1, 1):
            ss = s + side * (width / 2 - 0.04)
            _fbox(parent, name + f"_cheek{side}", P0, u, n, ss - 0.03, ss + 0.03,
                  0.02, depth, zr, zr + 1.02, m)
        # front railing: rails + posts
        for k, hf in enumerate((0.28, 0.62, 1.0)):
            _fbox(parent, name + f"_rail{k}", P0, u, n, s - width / 2,
                  s + width / 2, depth - 0.06, depth - 0.01,
                  zr + 1.02 * hf - 0.02, zr + 1.02 * hf + 0.02, m)
        for i in range(5):
            ss = s - width / 2 + 0.05 + i * (width - 0.10) / 4.0
            _fbox(parent, name + f"_post{i}", P0, u, n, ss - 0.018, ss + 0.018,
                  depth - 0.07, depth - 0.02, zr, zr + 1.02, m)
    # laundry line on some balconies
    if rng.chance(0.5):
        cloths = ["#B9C3CC", "#C9C4BA", "#7E8F84", "#C0B4A6", "#D5D9DC", "#8E9BB0"]
        line = P0 + u * (s - width / 2 + 0.15) + n * (depth - 0.22) \
            + Vector((0, 0, zr + 1.55))
        for li in range(rng.randint(2, 4)):
            t = 0.18 + 0.75 * (li / 3.0)
            wc = line + u * (t * (width - 0.4))
            m = kit.mat_solid("RES_CLOTH_" + cloths[li % len(cloths)][1:].upper(),
                              cloths[li % len(cloths)], rough=0.92)
            kit.quad(parent, name + f"_cloth{li}", tuple(wc), u, (0, 0, 1),
                     rng.uniform(0.32, 0.52), rng.uniform(0.45, 0.8), m)


def _balcony_cols_for_sections(cols, length, sections):
    """Return (balcony_cols, porch_cols) — one porch + 2 stacks per section."""
    sec_w = length / max(1, sections)
    bal, por = [], []
    for k in range(sections):
        a, b = k * sec_w, (k + 1) * sec_w
        inds = [i for i, c in enumerate(cols) if a + 1e-6 <= c < b - 1e-6]
        if not inds:
            continue
        if len(inds) >= 2:
            bal.append(cols[inds[0]])
            bal.append(cols[inds[-1]])
        else:
            bal.append(cols[inds[0]])
        if len(inds) >= 3:
            por.append(cols[inds[1]])
        else:
            por.append(cols[inds[0]])
    return bal, por


# ----------------------------------------------------------------------------
# shells / roofs / entrances
# ----------------------------------------------------------------------------
def _dark_joints(parent, bid, face):
    """kit draws panel seams as slightly proud strips that catch light — make them
    read as shadow grooves by giving them a dark material."""
    ob = bpy.data.objects.get(f"{bid}_{face}_joints")
    if ob is None or ob.type != "MESH":
        return
    m = mats()["plinth_dark"]
    ob.data.materials.clear()
    ob.data.materials.append(m)


def _corner_piers(parent, bid, rect, z1, mat):
    x0, y0, x1, y1 = rect
    for i, (x, y) in enumerate(((x0, y0), (x1, y0), (x1, y1), (x0, y1))):
        bx0 = x if x == x0 else x - THICK
        bx1 = x + THICK if x == x0 else x
        by0 = y if y == y0 else y - THICK
        by1 = y + THICK if y == y0 else y
        kit.box_mm(parent, f"{bid}_pier{i}", bx0, by0, Z0, bx1, by1, z1, mat)


def _base_skirt(parent, bid, rect):
    x0, y0, x1, y1 = rect
    m = mats()["conc_dark"]
    h, o = 0.22, 0.14
    kit.box_mm(parent, f"{bid}_skirt_s", x0 - o, y0 - o, 0.0, x1 + o, y0 + 0.10, h, m)
    kit.box_mm(parent, f"{bid}_skirt_n", x0 - o, y1 - 0.10, 0.0, x1 + o, y1 + o, h, m)
    kit.box_mm(parent, f"{bid}_skirt_w", x0 - o, y0 + 0.10, 0.0, x0 + 0.10, y1 - 0.10, h, m)
    kit.box_mm(parent, f"{bid}_skirt_e", x1 - 0.10, y0 + 0.10, 0.0, x1 + o, y1 - 0.10, h, m)


def _cornice(parent, bid, rect, z1, face, mat, h=0.22, out=0.12):
    rs = _shrunk(rect)
    P0, u, L, n = kit.wall_frame(rs, face)
    _fbox(parent, f"{bid}_{face}_cornice", P0, u, n, 0.0, L, THICK, THICK + out,
          z1 - h, z1, mat)


def _roof(parent, bid, rect, z1, rng, wall_mat, tower=False, fence=False,
          chimneys=2, tank=False, tower_extra=True):
    M = mats()
    kit.roof_flat(parent, f"{bid}_roof", rect, z1, M["roof"])
    kit.parapet(parent, f"{bid}_parapet", rect, z1, 0.85 if tower else 0.72,
                0.22, wall_mat)
    kit.roof_details(parent, f"{bid}_roofdet", rect, z1 + 0.05, rng, wall_mat,
                     tower=tower)
    x0, y0, x1, y1 = rect
    # brick chimney stacks along the roof edge (classic khrushchyovka silhouette)
    for i in range(chimneys):
        cx = x0 + 2.0 + (i + 0.5) * (x1 - x0 - 4.0) / max(1, chimneys)
        cy = y0 + 1.1 if i % 2 == 0 else y1 - 1.1
        m = M["brick_chimney"] if i % 2 == 0 else M["brick_chimney2"]
        kit.box(parent, f"{bid}_chim{i}", (cx, cy, z1 + 0.75), (1.5, 0.95, 1.5), m,
                bevel=0.03)
        kit.box(parent, f"{bid}_chimcap{i}", (cx, cy, z1 + 1.53), (1.68, 1.12, 0.12),
                M["conc_dark"])
        kit.box(parent, f"{bid}_chimsoot{i}", (cx, cy, z1 + 1.62), (0.55, 0.42, 0.08),
                M["roof_dark"])
    if fence:
        _roof_fence(parent, f"{bid}_rfence", rect, z1 + 0.78, mat=M["metal"])
    if tank or (tower and tower_extra):
        tx = x0 + (x1 - x0) * (0.72 if tower else 0.24)
        ty = y0 + (y1 - y0) * 0.30
        _water_tank(parent, f"{bid}_wtank", tx, ty, z1 + 0.05, M)
    if tower and tower_extra:
        # extra antenna masts on the parapet
        for i in range(3):
            ax = x0 + 1.2 + i * (x1 - x0 - 2.4) / 2.0
            ay = y0 + 0.6
            kit.cyl(parent, f"{bid}_mast{i}", (ax, ay, z1 + 1.6), 0.035, 3.2,
                    M["metal"], verts=5)
            kit.box(parent, f"{bid}_mastb{i}", (ax, ay, z1 + 2.9), (1.0, 0.02, 0.02),
                    M["metal"])


def _roof_fence(parent, name, rect, z, mat=None, h=0.9, step=2.6):
    M = mats()
    mat = mat or M["metal"]
    x0, y0, x1, y1 = rect
    edges = [((x0, y0), (x1, y0)), ((x0, y1), (x1, y1)),
             ((x0, y0), (x0, y1)), ((x1, y0), (x1, y1))]
    for ei, (p0, p1) in enumerate(edges):
        p0, p1 = Vector(p0), Vector(p1)
        d = p1 - p0
        ln = d.length
        if ln < 0.5:
            continue
        d.normalize()
        nposts = max(2, int(ln / step))
        for k in range(nposts + 1):
            p = p0 + d * (ln * k / nposts)
            kit.box(parent, f"{name}_{ei}_p{k}", (p.x, p.y, z + h / 2),
                    (0.05, 0.05, h), mat)
        for j, hf in enumerate((0.45, 0.95)):
            mid = (p0 + p1) / 2
            kit.box(parent, f"{name}_{ei}_r{j}", (mid.x, mid.y, z + h * hf),
                    (abs(d.x) + 0.05, abs(d.y) + 0.05, 0.035), mat)


def _water_tank(parent, name, x, y, z, M):
    kit.cyl(parent, name + "_body", (x, y, z + 1.15), 0.85, 1.5, M["metal_grey"],
            verts=14)
    kit.cyl(parent, name + "_top", (x, y, z + 1.95), 0.88, 0.12, M["rust"], verts=14)
    for i in range(4):
        a = i * math.pi / 2 + math.pi / 4
        lx, ly = x + math.cos(a) * 0.62, y + math.sin(a) * 0.62
        kit.box(parent, f"{name}_leg{i}", (lx, ly, z + 0.21),
                (0.11, 0.11, 0.42), M["metal"])
    kit.box(parent, name + "_pipe", (x + 0.95, y, z + 0.85), (0.12, 0.12, 1.7),
            M["rust"])


def _entrance(parent, bid, P0, u, n, s, rng, wall_mat, num=None, tower=False,
              canopy="flat", tile=True, mailbox=False):
    """Protruding entrance: canopy + posts, double metal door, steps, number plate."""
    M = mats()
    base = P0 + u * s
    std_w = 3.25 if tower else 2.70
    half = std_w / 2.0
    door_w = 0.98 if tower else 0.94
    sill = 0.42
    # tile patch behind the entrance
    if tile:
        _fbox(parent, f"{bid}_ent{s:.0f}_tile", P0, u, n, s - 1.5, s + 1.5,
              THICK - 0.02, THICK + 0.012, 0.10, 2.05, M["tile_blue"])
    # dark doorway recess
    _fbox(parent, f"{bid}_ent{s:.0f}_rec", P0, u, n, s - 1.12, s + 1.12,
          THICK, THICK + 0.06, sill, sill + 2.10, M["plinth_dark"])
    # light concrete surround so the doorway reads at a distance
    for side in (-1, 1):
        js = s + side * 1.20
        _fbox(parent, f"{bid}_ent{s:.0f}_surr{side}", P0, u, n,
              js - 0.09, js + 0.09, THICK, THICK + 0.13, sill, sill + 2.22,
              M["conc"])
    _fbox(parent, f"{bid}_ent{s:.0f}_surrtop", P0, u, n, s - 1.29, s + 1.29,
          THICK, THICK + 0.13, sill + 2.10, sill + 2.24, M["conc"])
    # transom strip over the doors
    _fbox(parent, f"{bid}_ent{s:.0f}_transom", P0, u, n, s - 1.0, s + 1.0,
          THICK + 0.05, THICK + 0.08, sill + 1.96, sill + 2.09, M["glass"])
    # double metal door
    dm = M["door_red"] if rng.random() < 0.55 else M["door_metal"]
    for side in (-1, 1):
        dc = s + side * (door_w / 2 + 0.02)
        _fbox(parent, f"{bid}_ent{s:.0f}_door{side}", P0, u, n,
              dc - door_w / 2, dc + door_w / 2, THICK + 0.08, THICK + 0.13,
              sill, sill + 1.94, dm)
        # light frame line between / around the leaves
        _fbox(parent, f"{bid}_ent{s:.0f}_leaf{side}", P0, u, n,
              dc - side * 0.07 - 0.028, dc - side * 0.07 + 0.028,
              THICK + 0.13, THICK + 0.145, sill + 0.10, sill + 1.86,
              M["metal_grey"])
        # handle
        _fbox(parent, f"{bid}_ent{s:.0f}_h{side}", P0, u, n,
              dc - side * 0.16 - 0.035, dc - side * 0.16 + 0.035,
              THICK + 0.13, THICK + 0.18, sill + 0.95, sill + 1.13, M["metal"])
        # kick plate
        _fbox(parent, f"{bid}_ent{s:.0f}_kick{side}", P0, u, n,
              dc - door_w / 2, dc + door_w / 2, THICK + 0.13, THICK + 0.14,
              sill + 0.02, sill + 0.18, M["metal_grey"])
    _fbox(parent, f"{bid}_ent{s:.0f}_thr", P0, u, n, s - 1.15, s + 1.15,
          THICK, THICK + 0.20, sill - 0.05, sill + 0.015, M["conc_grey"])
    # landing + 2 steps (total protrusion <= 0.9 m beyond the wall face)
    lw = std_w - 0.25
    _fbox(parent, f"{bid}_ent{s:.0f}_land", P0, u, n, s - lw / 2, s + lw / 2,
          THICK, THICK + 0.44, 0.0, sill, M["conc_grey"])
    _fbox(parent, f"{bid}_ent{s:.0f}_step0", P0, u, n, s - lw / 2, s + lw / 2,
          THICK + 0.44, THICK + 0.66, 0.0, 0.28, M["conc_grey"])
    _fbox(parent, f"{bid}_ent{s:.0f}_step1", P0, u, n, s - lw / 2, s + lw / 2,
          THICK + 0.66, THICK + 0.88, 0.0, 0.14, M["conc_grey"])
    # canopy
    cw = std_w + 0.35
    co1 = THICK + 0.92
    if canopy == "shed":
        slope = math.radians(11)
        c = base + n * ((THICK + co1) / 2) + Vector((0, 0, 2.90))
        _sbox(parent, f"{bid}_ent{s:.0f}_canopy", c, u, n, cw, co1 - THICK - 0.06,
              0.20, M["conc_grey"], bevel=0.02)
        # brackets
        for side in (-1, 1):
            _fbox(parent, f"{bid}_ent{s:.0f}_brk{side}", P0, u, n,
                  s + side * (half - 0.35) - 0.04, s + side * (half - 0.35) + 0.04,
                  THICK, THICK + 0.70, 2.45, 2.80, M["metal"])
    else:
        _fbox(parent, f"{bid}_ent{s:.0f}_canopy", P0, u, n, s - cw / 2, s + cw / 2,
              THICK - 0.05, co1, 2.68, 2.94, M["conc_grey"], bevel=0.02)
        _fbox(parent, f"{bid}_ent{s:.0f}_fascia", P0, u, n, s - cw / 2 - 0.04,
              s + cw / 2 + 0.04, co1 - 0.10, co1 + 0.02, 2.62, 3.00, M["conc"])
    # posts
    for side in (-1, 1):
        ps = s + side * (half - 0.22)
        if tower:
            kit.cyl(parent, f"{bid}_ent{s:.0f}_post{side}",
                    tuple(base + u * (side * (half - 0.22)) + n * (co1 - 0.16)
                          + Vector((0, 0, 1.30))), 0.075, 2.60, M["metal_grey"],
                    verts=10)
        else:
            _fbox(parent, f"{bid}_ent{s:.0f}_post{side}", P0, u, n,
                  ps - 0.045, ps + 0.045, co1 - 0.22, co1 - 0.13, 0.0, 2.68,
                  M["metal_grey"])
    # porch lamp under the canopy
    lc = base + n * 0.55 + Vector((0, 0, 2.52))
    kit.cyl(parent, f"{bid}_ent{s:.0f}_lamp", tuple(lc), 0.10, 0.12,
            M["lamp_porch"], rot=_dish_rot(n, 0.0), verts=10)
    _fbox(parent, f"{bid}_ent{s:.0f}_lampbase", P0, u, n, s - 0.14, s + 0.14,
          THICK + 0.02, THICK + 0.18, 2.56, 2.62, M["metal"])
    # house number plate
    if num is None:
        num = str(rng.randint(1, 48))
    plate_c = base + u * (half + 0.16) + n * (THICK + 0.03) + Vector((0, 0, 1.95))
    _fbox(parent, f"{bid}_ent{s:.0f}_plate", P0, u, n, s + half + 0.02,
          s + half + 0.34, THICK, THICK + 0.045, 1.82, 2.08, M["plate"])
    _text(parent, f"{bid}_ent{s:.0f}_num", num,
          plate_c + n * 0.045, 0.18, M["num"], n)
    # mailboxes
    if mailbox:
        _fbox(parent, f"{bid}_ent{s:.0f}_mbox", P0, u, n, s - half - 0.05,
              s - half + 0.55, THICK, THICK + 0.16, 1.25, 1.78, M["metal_grey"])
        for mi in range(2):
            for mj in range(3):
                _fbox(parent, f"{bid}_ent{s:.0f}_m{mi}{mj}", P0, u, n,
                      s - half + 0.02 + mj * 0.17, s - half + 0.16 + mj * 0.17,
                      THICK + 0.16, THICK + 0.175, 1.30 + mi * 0.26,
                      1.42 + mi * 0.26, M["metal"])
    return tuple(base + n * 0.8 + Vector((0, 0, 2.4)))


def _arch_portal(parent, bid, rs, s_arch, z0=Z0, height=2.95, width=4.0):
    M = mats()
    for face in ("PY", "NY"):
        P0, u, L, n = kit.wall_frame(rs, face)
        # vertical concrete jambs
        for side in (-1, 1):
            _fbox(parent, f"{bid}_{face}_jamb{side}", P0, u, n,
                  s_arch + side * width / 2 - 0.16, s_arch + side * width / 2 + 0.16,
                  THICK - 0.02, THICK + 0.12, z0, height + 0.34, M["conc"])
        # lintel band (single heavy architrave, no floating crown)
        _fbox(parent, f"{bid}_{face}_lintel", P0, u, n, s_arch - width / 2 - 0.34,
              s_arch + width / 2 + 0.34, THICK - 0.02, THICK + 0.15,
              height, height + 0.40, M["conc"])
        # thin shadow line under the lintel
        _fbox(parent, f"{bid}_{face}_shadow", P0, u, n, s_arch - width / 2,
              s_arch + width / 2, THICK + 0.15, THICK + 0.17,
              height - 0.10, height, M["plinth_dark"])


def _arch_tunnel(parent, bid, rs, s_arch, height=2.95, width=4.0):
    """Through-passage interior: side walls, ceiling slab, floor, lit lamps, doors."""
    M = mats()
    P0, u, L, n = kit.wall_frame(rs, "PY")
    x0, y0, x1, y1 = rs
    ax = P0.x + s_arch
    hw = width / 2.0
    # side walls
    kit.box_mm(parent, f"{bid}_tun_w0", ax - hw - 0.30, y0, Z0, ax - hw, y1,
               height, M["conc_dark"])
    kit.box_mm(parent, f"{bid}_tun_w1", ax + hw, y0, Z0, ax + hw + 0.30, y1,
               height, M["conc_dark"])
    # ceiling + floor
    kit.box_mm(parent, f"{bid}_tun_ceil", ax - hw, y0, height, ax + hw, y1,
               height + 0.26, M["conc_dark"])
    kit.box_mm(parent, f"{bid}_tun_floor", ax - hw, y0, Z0, ax + hw, y1, 0.07,
               M["conc_grey"])
    # sloped haunch at the entrance mouths (chamfered corners read better)
    for face in ("PY", "NY"):
        PP, uu, LL, nn = kit.wall_frame(rs, face)
        _fbox(parent, f"{bid}_{face}_tunsill", PP, uu, nn, s_arch - hw, s_arch + hw,
              -0.05, 0.10, Z0, 0.05, M["conc_grey"])
    # tunnel lamps (emissive only — the Lead adds real lights)
    for i, yy in enumerate((y0 + 2.6, (y0 + y1) / 2.0, y1 - 2.6)):
        kit.box(parent, f"{bid}_tun_lamp{i}", (ax - hw + 0.16, yy, height - 0.10),
                (0.12, 0.34, 0.10), M["lamp_tunnel"])
        kit.box(parent, f"{bid}_tun_lampc{i}", (ax + hw - 0.16, yy, height - 0.10),
                (0.12, 0.34, 0.10), M["lamp_tunnel"])
    # service doors on both tunnel walls
    for side in (-1, 1):
        xx = ax + side * hw
        kit.box(parent, f"{bid}_tun_door{side}", (xx - side * 0.05, (y0 + y1) / 2.0,
                                                  Z0 + 1.05), (0.10, 0.95, 2.0),
                M["door_metal"])
        kit.box(parent, f"{bid}_tun_dframe{side}", (xx - side * 0.02, (y0 + y1) / 2.0,
                                                    Z0 + 1.08), (0.04, 1.10, 2.14),
                M["conc_grey"])
    # painted block number on the tunnel side wall
    yc = (y0 + y1) / 2.0 + 1.6
    kit.box_mm(parent, f"{bid}_tunsign", ax - hw, yc - 0.8, 1.55,
               ax - hw + 0.035, yc + 0.8, 2.15, M["sign_bg"])
    _text(parent, f"{bid}_tunnum", "12", (ax - hw + 0.06, yc, 1.85), 0.30,
          M["sign_fg"], Vector((1, 0, 0)))


def _annex(parent, bid, rect, rng):
    """1-storey brick shop/utility annex at the east end of the modified block."""
    M = mats()
    x0, y0, x1, y1 = rect
    ax0, ax1 = x1 - 8.0, x1 - 0.6
    ay0, ay1 = y1 - 0.05, y1 + 0.85          # protrudes 0.85 m past the rect
    h = 3.05
    brick = M["brick_annex"]
    kit.box_mm(parent, f"{bid}_annex_w", ax0, ay0, Z0, ax0 + 0.32, ay1, h, brick)
    kit.box_mm(parent, f"{bid}_annex_e", ax1 - 0.32, ay0, Z0, ax1, ay1, h, brick)
    kit.box_mm(parent, f"{bid}_annex_f", ax0, ay1 - 0.32, Z0, ax1, ay1, h, brick)
    kit.box_mm(parent, f"{bid}_annex_b", ax0, ay0, Z0, ax1, ay0 + 0.2, h, brick)
    kit.box_mm(parent, f"{bid}_annex_roof", ax0 - 0.12, ay0, h, ax1 + 0.12,
               ay1 + 0.12, h + 0.18, M["roof"])
    kit.box_mm(parent, f"{bid}_annex_par", ax0 - 0.12, ay1 - 0.10, h + 0.18,
               ax1 + 0.12, ay1 + 0.12, h + 0.34, M["conc_dark"])
    # door + frame
    dx = ax1 - 2.2
    kit.box_mm(parent, f"{bid}_annex_drecess", dx - 0.72, ay1 - 0.10, Z0 + 0.30,
               dx + 0.72, ay1 + 0.02, 2.55, M["plinth_dark"])
    kit.box_mm(parent, f"{bid}_annex_door", dx - 0.62, ay1 - 0.05, Z0 + 0.32,
               dx + 0.62, ay1 + 0.06, 2.28, M["door_metal"])
    kit.box_mm(parent, f"{bid}_annex_step", dx - 0.85, ay1, Z0, dx + 0.85, ay1 + 0.35,
               0.16, M["conc_grey"])
    # shop window, dim warm interior
    wx = ax0 + 2.6
    kit.box_mm(parent, f"{bid}_annex_winrec", wx - 1.0, ay1 - 0.10, Z0 + 0.95,
               wx + 1.0, ay1 + 0.02, 2.20, M["plinth_dark"])
    kit.box_mm(parent, f"{bid}_annex_glass", wx - 0.93, ay1 + 0.02, Z0 + 1.02,
               wx + 0.93, ay1 + 0.05, 2.13, M["shop_glow"])
    for i in range(2):
        kit.box_mm(parent, f"{bid}_annex_mull{i}", wx - 0.06 + i * 0.12, ay1 + 0.01,
                   Z0 + 1.02, wx + 0.06 + i * 0.12, ay1 + 0.06, 2.13, M["metal_grey"])
    # sign above the door
    kit.box_mm(parent, f"{bid}_annex_signbg", dx - 1.35, ay1 + 0.02, 2.52,
               dx + 1.35, ay1 + 0.16, 2.94, M["sign_bg"])
    _text(parent, f"{bid}_annex_signtxt", "МАСТЕРСКАЯ",
          Vector((dx, ay1 + 0.175, 2.73)), 0.24, M["sign_fg"],
          Vector((0, 1, 0)))
    # wall lamp + meter box
    kit.box(parent, f"{bid}_annex_lamp", (dx, ay1 + 0.16, 2.34), (0.16, 0.20, 0.12),
            M["lamp_porch"])
    kit.box(parent, f"{bid}_annex_meter", (ax0 + 4.8, ay1 + 0.08, 1.55),
            (0.42, 0.16, 0.55), M["metal_grey"])
    kit.box(parent, f"{bid}_annex_pipe", (ax0 + 5.4, ay1 + 0.10, 1.5),
            (0.08, 0.08, 3.0), M["rust"])


def _stair_bay(parent, bid, P0, u, n, s, floors, floor_h, z1, rng, wall_mat):
    """Protruding stair/lift bay with small stair windows (brezhnevka rhythm)."""
    M = mats()
    bay_w = 3.0
    _fbox(parent, f"{bid}_stair_bay", P0, u, n, s - bay_w / 2, s + bay_w / 2,
          THICK, THICK + 0.36, Z0, z1, M["band_white"])
    # vertical shadow joints
    for i in range(2):
        ss = s - bay_w / 2 + 0.06 + i * (bay_w - 0.12)
        _fbox(parent, f"{bid}_stair_j{i}", P0, u, n, ss - 0.02, ss + 0.02,
              THICK + 0.36, THICK + 0.375, Z0 + 1.0, z1 - 0.6, M["conc_dark"])
    for f in range(floors):
        zc = Z0 + f * floor_h + floor_h * 0.62
        _flat_window(parent, f"{bid}_stair{f}", P0, u, n, s, zc, 1.0, 1.15,
                     THICK + 0.36, rng, lit_ratio=0.14)
        # landing light strip beside the window
        _fbox(parent, f"{bid}_stair_lip{f}", P0, u, n, s - 0.62, s + 0.62,
              THICK + 0.36, THICK + 0.42, zc - 0.66, zc - 0.58, M["conc_grey"])
    return bay_w


def _lift_head(parent, bid, P0, u, n, s, z1, wall_mat):
    """Elevator machine room above the stair bay + louvres + antenna."""
    M = mats()
    w = 4.0
    _fbox(parent, f"{bid}_lift", P0, u, n, s - w / 2, s + w / 2, -2.05, 0.34,
          z1, z1 + 2.35, wall_mat)
    _fbox(parent, f"{bid}_liftroof", P0, u, n, s - w / 2 - 0.16, s + w / 2 + 0.16,
          -2.20, 0.42, z1 + 2.35, z1 + 2.50, M["roof_dark"])
    for i in range(3):
        ss = s - 1.15 + i * 1.15
        _fbox(parent, f"{bid}_lift_lv{i}", P0, u, n, ss - 0.36, ss + 0.36,
              0.34, 0.40, z1 + 1.55, z1 + 2.05, M["metal"])
    _fbox(parent, f"{bid}_lift_vent", P0, u, n, s - 0.35, s + 0.35, -0.10, 0.06,
          z1 + 2.45, z1 + 2.70, M["metal_grey"])
    _fbox(parent, f"{bid}_lift_mast", P0, u, n, s + 1.2, s + 1.28, -0.9, -0.82,
          z1 + 2.5, z1 + 4.4, M["metal"])
    return


# ----------------------------------------------------------------------------
# generic khrushchyovka shell
# ----------------------------------------------------------------------------
def _khrush_core(parent, block, rng, wall_mat, plinth_mat, spacing=2.7,
                 sections=4, arch=None, balcony_mode="sections",
                 porch_override=None, lit_main=0.14, lit_back=0.10,
                 lit_end=0.05, porch_faces=None, canopy="flat",
                 mailbox=False, glazed_ratio=0.45, balcony_width=2.3,
                 balcony_depth=1.0, sills=True, extra_skip=None,
                 joints=True, grille=False):
    """Build the shell (4 facades + balconies + porches).  Returns info dict."""
    bid = block["id"]
    rect = _rect(block)
    rs = _shrunk(rect)
    floors = int(block.get("floors", 5))
    fh = 2.72
    z1 = Z0 + floors * fh
    wm = kit.win_materials()
    long_faces = _long_faces(rect)
    end_faces = _end_faces(rect)
    faces = list(block.get("faces", []))
    main = faces[0] if faces else long_faces[0]
    if porch_faces is None:
        porch_faces = [f for f in faces if f in long_faces] or [main]
    info = {"rect": rect, "rs": rs, "z1": z1, "floors": floors, "fh": fh,
            "long": long_faces, "ends": end_faces, "main": main,
            "windows": 0, "lit": 0, "porches": []}
    # ---- per-face window layouts (evenly spaced, symmetric) ----
    layout = {}
    for face in long_faces + end_faces:
        is_end = face in end_faces
        L = _face_len(rs, face)
        sp = 3.2 if is_end else spacing
        cols = _cols_sym(L, sp, 1.35)
        layout[face] = {"L": L, "spacing": sp, "cols": cols,
                        "offsets": [c - 1.4 for c in cols],
                        "skip": [], "arches": (), "porches": [], "bal": []}
    if extra_skip:
        for face, extra in extra_skip.items():
            if face in layout:
                layout[face]["skip"].extend(extra)
    if arch:
        for face in long_faces:
            s_arch = layout[face]["L"] / 2.0
            layout[face]["arches"] = ((s_arch, 4.0),)
            info["arch_s"] = s_arch
    for face in long_faces:
        L = layout[face]["L"]
        cols = layout[face]["cols"]
        if face in porch_faces:
            if porch_override and face in porch_override:
                por = list(porch_override[face])
            else:
                _, por = _balcony_cols_for_sections(cols, L, sections)
            layout[face]["porches"] = por
            layout[face]["skip"] = list(por) + list(layout[face]["skip"])
        if balcony_mode == "sections":
            bal, _ = _balcony_cols_for_sections(cols, L, sections)
        elif balcony_mode == "ends":
            bal = [cols[0], cols[-1]] if len(cols) >= 2 else list(cols)
        elif balcony_mode == "rich":
            bal = ([cols[0], cols[3], cols[-4], cols[-1]]
                   if len(cols) >= 8 else list(cols))
        else:
            bal = []
        layout[face]["bal"] = bal
    # ---- facades ----
    for face in long_faces + end_faces:
        cf = layout[face]
        is_end = face in end_faces
        is_main = face in porch_faces
        P0, u, Lf, n = kit.wall_frame(rs, face)
        st = kit.facade_wall(
            parent, f"{bid}_{face}", rs, face, Z0, z1, wall_mat, rng,
            floors=floors, floor_h=fh, win_w=1.35, win_h=1.5,
            spacing=cf["spacing"], wmats=wm, margin=1.4,
            lit_ratio=(lit_main if is_main else (lit_end if is_end else lit_back)),
            thickness=THICK, plinth_h=1.6, plinth_mat=plinth_mat,
            arches=cf["arches"], ground_skip=cf["skip"],
            balcony_cols=(), joints=(joints and not is_end),
            ground_win_bottom=1.45, grille_ground=(grille and not is_end),
            win_offsets=cf["offsets"])
        info["windows"] += st["windows"]
        info["lit"] += st["lit"]
        _dark_joints(parent, bid, face)
        if sills and is_main:
            rows = [Z0 + (2.20 if f == 0
                          else f * fh + (fh - 1.5) * 0.55 + 0.75)
                    for f in range(floors)]
            skip = {round(sc, 2) for sc in cf["skip"]}
            cols2 = [c for c in cf["cols"] if round(c, 2) not in skip]
            _ext_sills(parent, f"{bid}_{face}", P0, u, n, cols2, rows, 1.35,
                       THICK, win_h=1.5)
        # balconies (entrance facades only)
        if cf["bal"] and is_main:
            for fi in range(1, floors):
                for bi, sc in enumerate(cf["bal"]):
                    if arch and abs(sc - info.get("arch_s", -99)) < 2.6:
                        continue
                    kind = "glazed" if rng.random() < glazed_ratio else "open"
                    _balcony(parent, f"{bid}_{face}_bal{fi}_{bi}", P0, u, n, sc,
                             Z0 + fi * fh, balcony_width, balcony_depth, kind,
                             rng, wall_mat)
        # entrances
        for pi, sc in enumerate(cf["porches"]):
            if arch and abs(sc - info.get("arch_s", -99)) < 2.8:
                continue
            pos = _entrance(parent, bid, P0, u, n, sc, rng, wall_mat,
                            tower=False, canopy=canopy, mailbox=mailbox,
                            tile=rng.random() < 0.75)
            info["porches"].append(pos)
    # ---- corner piers + skirt + cornice ----
    _corner_piers(parent, bid, rect, z1, wall_mat)
    _base_skirt(parent, bid, rect)
    for face in long_faces + end_faces:
        _cornice(parent, bid, rect, z1, face, mats()["conc_grey"])
    # ---- through-passage ----
    if arch:
        _arch_portal(parent, bid, rs, info["arch_s"])
        _arch_tunnel(parent, bid, rs, info["arch_s"])
        info["arch"] = True
    return info


def _drainpipes(parent, bid, rect, rs, z1, rng, long_faces, extra_joints=True):
    M = mats()
    x0, y0, x1, y1 = rect
    corners = [(x0 + 0.16, y0 + 0.16), (x1 - 0.16, y0 + 0.16),
               (x0 + 0.16, y1 - 0.16), (x1 - 0.16, y1 - 0.16)]
    for i, (x, y) in enumerate(corners):
        kit.drainpipe(parent, f"{bid}_drain{i}", x, y, 0.25, z1 - 0.05, mat=M["metal"])
    if extra_joints:
        for face in long_faces:
            P0, u, L, n = kit.wall_frame(rs, face)
            cnt = max(2, int(L / 11.0))
            for k in range(1, cnt):
                sc = L * k / cnt
                p = P0 + u * sc + n * (THICK + 0.06)
                kit.drainpipe(parent, f"{bid}_{face}_drain{k}", p.x, p.y, 0.25,
                              z1 - 0.05, mat=M["metal"])


def _weather_extras(parent, bid, rect, rs, rng, long_faces, main, z1, floors,
                    fh, dishes=3, acs=3, contacts=0):
    """Wall-mounted clutter: satellite dishes, AC units, laundry lines."""
    M = mats()
    P0, u, L, n = kit.wall_frame(rs, main)
    for i in range(dishes):
        s = L * rng.uniform(0.08, 0.92)
        z = rng.uniform(0.4, 1.15) * floors * fh
        _sat_dish(parent, f"{bid}_dish{i}", P0, u, n, s, z, rng,
                  scale=rng.uniform(0.8, 1.15))
    for i in range(acs):
        s = L * rng.uniform(0.08, 0.92)
        z = rng.choice([1, 2, 3, 4]) * fh + rng.uniform(1.7, 2.0)
        _ac_unit(parent, f"{bid}_ac{i}", P0, u, n, s, z)
    for face in long_faces:
        if face == main:
            continue
        P0b, ub, Lb, nb = kit.wall_frame(rs, face)
        for i in range(max(1, dishes // 2)):
            s = Lb * rng.uniform(0.08, 0.92)
            z = rng.uniform(0.4, 1.1) * floors * fh
            _sat_dish(parent, f"{bid}_{face}_dish{i}", P0b, ub, nb, s, z, rng,
                      scale=rng.uniform(0.8, 1.1))


def _tame_boarded(parent, bid, rng, keep=0.22):
    """kit's boarded window texture is picked with 1/3 probability — thin it out."""
    wm = kit.win_materials()
    dark = wm["dark_b"]
    for ob in parent.children:
        if ob.type != "MESH" or "_glass" not in ob.name:
            continue
        if not ob.name.startswith(bid):
            continue
        if not ob.data.materials:
            continue
        mat = ob.data.materials[0]
        if mat and mat.name.startswith("WIND_boarded") and not rng.chance(keep):
            ob.data.materials.clear()
            ob.data.materials.append(dark)


def _mix_wood_frames(parent, bid, rng, prob=0.42):
    wood = mats()["wood_frame"]
    for ob in parent.children:
        if ob.type != "MESH" or not ob.name.startswith(bid):
            continue
        if "_frame" not in ob.name and "_fv" not in ob.name and "_fh" not in ob.name:
            continue
        if not ob.data.materials:
            continue
        if rng.chance(prob):
            ob.data.materials.clear()
            ob.data.materials.append(wood)


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


# ============================================================================
# 1. khrushchyovka — prefab panel
# ============================================================================
def build_khrush_panel(parent, block, rng, ctx):
    bid = block["id"]
    M = mats()
    variant = block.get("variant", "pale_mint")
    wall_mat = M["wall_blue"] if variant == "pale_blue" else M["wall_mint"]
    rect = _rect(block)
    sections = int(block.get("sections", 4))
    info = _khrush_core(parent, block, rng, wall_mat, M["plinth"], spacing=2.7,
                        sections=sections, balcony_mode="sections",
                        lit_main=0.16, lit_back=0.12, lit_end=0.07,
                        canopy="flat", mailbox=False, glazed_ratio=0.42,
                        balcony_width=2.3)
    z1 = info["z1"]
    rs = info["rs"]
    _roof(parent, bid, rect, z1, rng, wall_mat, tower=False, fence=False,
          chimneys=3, tank=False)
    _drainpipes(parent, bid, rect, rs, z1, rng, info["long"])
    _weather_extras(parent, bid, rect, rs, rng, info["long"], info["main"],
                    z1, info["floors"], info["fh"], dishes=4, acs=3)
    _tame_boarded(parent, bid, rng, 0.18)
    n, t = _count(parent)
    info.update({"objects": n, "tris": t})
    return info


# ============================================================================
# 2. khrushchyovka — brick (silicate)
# ============================================================================
def build_khrush_brick(parent, block, rng, ctx):
    bid = block["id"]
    M = mats()
    variant = block.get("variant", "silicate_brick")
    wall_mat = M["brick_warm"] if variant == "silicate_brick" else M["brick_pale"]
    rect = _rect(block)
    sections = int(block.get("sections", 3))
    info = _khrush_core(parent, block, rng, wall_mat, M["plinth"], spacing=2.75,
                        sections=sections, balcony_mode="ends",
                        lit_main=0.15, lit_back=0.11, lit_end=0.06,
                        canopy="shed", mailbox=True, glazed_ratio=0.30,
                        balcony_width=2.5, grille=True)
    rect = info["rect"]
    rs = info["rs"]
    z1 = info["z1"]
    _roof(parent, bid, rect, z1, rng, wall_mat, tower=False, fence=True,
          chimneys=3, tank=rng.chance(0.5))
    _drainpipes(parent, bid, rect, rs, z1, rng, info["long"])
    _weather_extras(parent, bid, rect, rs, rng, info["long"], info["main"],
                    z1, info["floors"], info["fh"], dishes=2, acs=1)
    # basement vents along the plinth
    P0, u, L, n = kit.wall_frame(rs, info["main"])
    for i in range(int(L / 6.0)):
        s = 3.0 + i * 6.0
        if s > L - 1.5:
            break
        _fbox(parent, f"{bid}_bvent{i}", P0, u, n, s - 0.25, s + 0.25,
              THICK, THICK + 0.03, 0.45, 0.72, M["plinth_dark"])
    _tame_boarded(parent, bid, rng, 0.20)
    n, t = _count(parent)
    info.update({"objects": n, "tris": t})
    return info


# ============================================================================
# 3. khrushchyovka — brick with through-passage arch
# ============================================================================
def build_khrush_arch(parent, block, rng, ctx):
    bid = block["id"]
    M = mats()
    variant = block.get("variant", "pale_blue")
    wall_mat = M["brick_cool"] if variant == "pale_blue" else M["brick_pale"]
    rect = _rect(block)
    rs = _shrunk(rect)
    long_faces = _long_faces(rect)
    L = _face_len(rs, long_faces[0])
    s_arch = L / 2.0
    cols = _cols_sym(L, 2.7, 1.35)
    # symmetric pair of entrances flanking the arch (outside arch + canopy)
    por = []
    for i in range(len(cols)):
        j = len(cols) - 1 - i
        if j <= i:
            break
        if 3.6 <= abs(cols[i] - s_arch) <= 5.6:
            por = [cols[i], cols[j]]
            break
    if not por:
        por = [cols[2], cols[-3]] if len(cols) > 4 else []
    info = _khrush_core(parent, block, rng, wall_mat, M["plinth"], spacing=2.7,
                        sections=3, balcony_mode="ends", arch=True,
                        lit_main=0.16, lit_back=0.12, lit_end=0.06,
                        canopy="shed", mailbox=True, glazed_ratio=0.42,
                        balcony_width=2.5, grille=True,
                        porch_override={f: por for f in long_faces})
    z1 = info["z1"]
    rs = info["rs"]
    _roof(parent, bid, rect, z1, rng, wall_mat, tower=False, fence=True,
          chimneys=4, tank=False)
    _drainpipes(parent, bid, rect, rs, z1, rng, info["long"])
    _weather_extras(parent, bid, rect, rs, rng, info["long"], info["main"],
                    z1, info["floors"], info["fh"], dishes=3, acs=2)
    _tame_boarded(parent, bid, rng, 0.26)
    n, t = _count(parent)
    info.update({"objects": n, "tris": t})
    return info


# ============================================================================
# 4. khrushchyovka — modified (glazed loggias + brick annex)
# ============================================================================
def build_khrush_mod(parent, block, rng, ctx):
    bid = block["id"]
    M = mats()
    variant = block.get("variant")
    wall_mat = M["wall_mint"] if variant == "glazed_balcony" else M["wall_grey"]
    rect = _rect(block)
    rs = _shrunk(rect)
    x0, y0, x1, y1 = rect
    long_faces = _long_faces(rect)
    main = list(block.get("faces", []))[0]
    # annex occupies the ground floor at the east end of the main facade
    L = _face_len(rs, main)
    cols = _cols_sym(L, 2.7, 1.35)
    annex_s0 = x1 - 8.0 - rs[0]
    skip = [c for c in cols if c > annex_s0 - 0.3]
    # two entrances away from the annex
    base_por, _ = _balcony_cols_for_sections(cols, L, int(block.get("sections", 3)))
    por = [p for p in base_por if p < annex_s0 - 3.0][:2]
    info = _khrush_core(parent, block, rng, wall_mat, M["plinth"], spacing=2.7,
                        sections=int(block.get("sections", 3)),
                        balcony_mode="rich", porch_override={main: por},
                        lit_main=0.17, lit_back=0.12, lit_end=0.08,
                        canopy="shed", mailbox=True, glazed_ratio=0.72,
                        balcony_width=2.4, extra_skip={main: skip})
    z1 = info["z1"]
    rs = info["rs"]
    _roof(parent, bid, rect, z1, rng, wall_mat, tower=False, fence=False,
          chimneys=2, tank=True)
    _drainpipes(parent, bid, rect, rs, z1, rng, info["long"])
    _weather_extras(parent, bid, rect, rs, rng, info["long"], info["main"],
                    z1, info["floors"], info["fh"], dishes=8, acs=6)
    _annex(parent, bid, rect, rng)
    # rooftop clutter: antenna masts + brick parapet patch
    for i in range(4):
        ax = rect[0] + 2.0 + i * (rect[2] - rect[0] - 4.0) / 3.0
        ay = rect[3] - 0.7
        h = rng.uniform(2.0, 3.6)
        kit.cyl(parent, f"{bid}_rant{i}", (ax, ay, z1 + h / 2), 0.028, h,
                M["metal"], verts=5)
        kit.box(parent, f"{bid}_rantb{i}", (ax, ay, z1 + h * 0.72),
                (rng.uniform(0.7, 1.2), 0.02, 0.02), M["metal"])
    kit.box_mm(parent, f"{bid}_roofbrick", rect[0] + 5.0, rect[1] + 1.4, z1,
               rect[0] + 8.4, rect[1] + 2.4, z1 + 0.95, M["brick_chimney"])
    kit.box_mm(parent, f"{bid}_roofbrickcap", rect[0] + 4.85, rect[1] + 1.25,
               z1 + 0.95, rect[0] + 8.55, rect[1] + 2.55, z1 + 1.08, M["conc_dark"])
    _tame_boarded(parent, bid, rng, 0.16)
    _mix_wood_frames(parent, bid, rng, 0.42)
    n, t = _count(parent)
    info.update({"objects": n, "tris": t})
    return info


# ============================================================================
# 5. brezhnevka — 9-storey tower
# ============================================================================
def build_brezhnevka_9(parent, block, rng, ctx):
    bid = block["id"]
    M = mats()
    variant = block.get("variant", "tiled_band")
    if variant == "grey_panel":
        wall_mat, band_mat = M["wall_grey"], M["band_white"]
    else:
        wall_mat, band_mat = M["wall_blue"], M["band_white"]
    rect = _rect(block)
    rs = _shrunk(rect)
    floors = int(block.get("floors", 9))
    fh = 2.8
    z1 = Z0 + floors * fh
    wm = kit.win_materials()
    long_faces = _long_faces(rect)
    end_faces = _end_faces(rect)
    faces = list(block.get("faces", []))
    main = faces[0] if faces else long_faces[0]
    info = {"rect": rect, "rs": rs, "z1": z1, "floors": floors, "fh": fh,
            "long": long_faces, "ends": end_faces, "main": main,
            "windows": 0, "lit": 0, "porches": []}
    # ---- long facade window rhythm: symmetric grid with a stair bay ----
    L_long = _face_len(rs, long_faces[0])
    s_bay = L_long / 2.0
    bay_w = 3.0
    cols_long = [c for c in _cols_sym(L_long, 2.4, 1.3)
                 if abs(c - s_bay) > bay_w / 2 + 0.75]
    win_offs = [c - 1.4 for c in cols_long]
    por_candidates = [c for c in cols_long
                      if abs(c - s_bay) > bay_w / 2 + 1.6]
    porch_cols = ([por_candidates[len(por_candidates) // 2 - 1],
                   por_candidates[len(por_candidates) // 2]]
                  if len(por_candidates) >= 2 else por_candidates[:1])
    row_lo = [Z0 + f * fh + (fh - 1.55) * 0.55 for f in range(floors)]
    for face in long_faces:
        skip = list(porch_cols) if face == main else []
        st = kit.facade_wall(
            parent, f"{bid}_{face}", rs, face, Z0, z1, wall_mat, rng,
            floors=floors, floor_h=fh, win_w=1.5, win_h=1.55, spacing=2.4,
            wmats=wm, margin=1.4, lit_ratio=(0.18 if face == main else 0.13),
            thickness=THICK, plinth_h=1.6, plinth_mat=M["plinth"],
            ground_skip=skip, balcony_cols=(), joints=True,
            ground_win_bottom=1.55, win_offsets=win_offs)
        info["windows"] += st["windows"]
        info["lit"] += st["lit"]
        _dark_joints(parent, bid, face)
        P0, u, LL, n = kit.wall_frame(rs, face)
        # tiled band strips in alternate floor gaps (brezhnevka banding)
        for f in range(1, floors - 1):
            za = row_lo[f] + 1.55 + 0.06
            zb = row_lo[f + 1] - 0.06
            if f % 2 != 0 or zb - za < 0.12:
                continue
            _fbox(parent, f"{bid}_{face}_band{f}", P0, u, n, 0.0, LL,
                  THICK, THICK + 0.035, za, zb, band_mat)
        _fbox(parent, f"{bid}_{face}_bandtop", P0, u, n, 0.0, LL, THICK,
              THICK + 0.035, row_lo[-1] + 1.55 + 0.06, z1 - 0.42, band_mat)
        # exterior sills on the main facade
        if face == main:
            rows = [row_lo[f] + 0.75 for f in range(floors)]
            skip_set = {round(c, 2) for c in skip}
            cols2 = [c for c in cols_long if round(c, 2) not in skip_set]
            _ext_sills(parent, f"{bid}_{face}_s", P0, u, n, cols2, rows, 1.5,
                       THICK, win_h=1.55)
    # ---- short ends ----
    for face in end_faces:
        L = _face_len(rs, face)
        cols = _cols_sym(L, 3.0, 1.45)
        st = kit.facade_wall(
            parent, f"{bid}_{face}", rs, face, Z0, z1, wall_mat, rng,
            floors=floors, floor_h=fh, win_w=1.5, win_h=1.55, spacing=3.0,
            wmats=wm, margin=1.45, lit_ratio=0.10, thickness=THICK,
            plinth_h=1.6, plinth_mat=M["plinth"], balcony_cols=(),
            joints=True, ground_win_bottom=1.55,
            win_offsets=[c - 1.45 for c in cols])
        info["windows"] += st["windows"]
        info["lit"] += st["lit"]
        _dark_joints(parent, bid, face)
        P0, u, LL, n = kit.wall_frame(rs, face)
        for f in range(1, floors - 1):
            za, zb = row_lo[f] + 1.55 + 0.06, row_lo[f + 1] - 0.06
            if f % 2 != 0 or zb - za < 0.12:
                continue
            _fbox(parent, f"{bid}_{face}_band{f}", P0, u, n, 0.0, LL, THICK,
                  THICK + 0.035, za, zb, band_mat)
    # ---- stair bays + lift head ----
    for face in long_faces:
        P0, u, LL, n = kit.wall_frame(rs, face)
        _stair_bay(parent, bid, P0, u, n, s_bay, floors, fh, z1, rng, wall_mat)
        if face == main:
            _lift_head(parent, bid, P0, u, n, s_bay, z1, wall_mat)
    # ---- balconies: glazed loggias at both ends of both long facades ----
    for face in long_faces:
        P0, u, LL, n = kit.wall_frame(rs, face)
        stacks = [cols_long[0], cols_long[-1]]
        for fi in range(2, floors):
            for bi, sc in enumerate(stacks):
                kind = "glazed" if rng.random() < 0.78 else "open"
                _balcony(parent, f"{bid}_{face}_bal{fi}_{bi}", P0, u, n, sc,
                         Z0 + fi * fh, 2.6, 1.05, kind, rng, wall_mat)
    # ---- entrances on the main facade ----
    P0, u, LL, n = kit.wall_frame(rs, main)
    for i, sc in enumerate(porch_cols):
        pos = _entrance(parent, bid, P0, u, n, sc, rng, wall_mat, tower=True,
                        canopy="flat", mailbox=(i == 0), tile=True)
        info["porches"].append(pos)
    # ---- corner piers, skirt, roof ----
    _corner_piers(parent, bid, rect, z1, wall_mat)
    _base_skirt(parent, bid, rect)
    for face in long_faces + end_faces:
        _cornice(parent, bid, rect, z1, face, M["conc_grey"], h=0.26, out=0.14)
    _roof(parent, bid, rect, z1, rng, wall_mat, tower=True, fence=False,
          chimneys=0, tank=False, tower_extra=False)
    _water_tank(parent, f"{bid}_wtank2", rect[0] + 3.4, rect[1] + 3.2, z1 + 0.05, M)
    _drainpipes(parent, bid, rect, rs, z1, rng, long_faces)
    _weather_extras(parent, bid, rect, rs, rng, long_faces, main, z1, floors, fh,
                    dishes=5, acs=2)
    _tame_boarded(parent, bid, rng, 0.25)
    n, t = _count(parent)
    info.update({"objects": n, "tris": t})
    return info


# ----------------------------------------------------------------------------
TYPES = {
    "khrush_panel": build_khrush_panel,
    "khrush_brick": build_khrush_brick,
    "khrush_arch": build_khrush_arch,
    "khrush_mod": build_khrush_mod,
    "brezhnevka_9": build_brezhnevka_9,
}
