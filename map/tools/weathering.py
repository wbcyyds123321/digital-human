"""Weathering-overlay material pass for the Soviet Blue-Hour microdistrict (Round 3).

Runs INSIDE Blender at the end of build.py:
    import weathering; weathering.apply(coll_bld, PLAN)
For every building wall/plinth/roof material that uses an image texture, it composites the
source texture with one of the Round-3 GPT-Image weathering tiles (ref/F/tex_wx_*.png or a
procedural fallback), bakes the material tint into the pixels, creates 2 variant materials and
re-assigns them per building so no two buildings share the exact same weathered wall.

Also usable standalone to pre-generate fallback weathering tiles:
    python tools/weathering.py tiles
"""
import json
import os
import random
import sys
from pathlib import Path

ROOT = Path(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
REF = ROOT / "ref"
OUT = REF / "G"
WX = REF / "F"

WANT_KEYS = ("WALL", "BRICK", "PLINTH", "ROOF", "CONC", "GRANITE", "MONUMENT", "PAINT",
             "FACADE", "STONE", "PANEL", "BAND", "CORNICE", "TRIM_STONE", "PLASTER")
SKIP_KEYS = ("LMM_", "GLASS", "FRAME", "SILL", "WIN", "WIND", "DOOR", "METAL", "RUST", "WOOD",
             "CLOTH", "LAMP", "FENCE", "BARK", "TRIM", "GRILLE", "ANT", "STEP", "KERB",
             "ASPHALT", "GRASS", "DIRT", "SOIL", "FIELD", "SAND", "RAIL", "TRACK",
             "PAINT_DIM", "PUDDLE", "WET", "SKIN", "BILLBOARD", "POSTER", "SIGN")


def _wanted(name):
    up = name.upper()
    if any(k in up for k in SKIP_KEYS):
        return False
    return any(k in up for k in WANT_KEYS)


# ---------------------------------------------------------------- fallback tiles
def make_fallback_tiles():
    """Procedural grime tiles for when the GPT wx set is absent (Pillow, host Python)."""
    from PIL import Image, ImageDraw, ImageFilter
    OUT.mkdir(parents=True, exist_ok=True)
    rng = random.Random(20261101)
    made = []
    for idx, kind in enumerate(("grime", "streak", "moss", "peel")):
        size = 512
        img = Image.new("L", (size, size), 0)
        d = ImageDraw.Draw(img)
        if kind in ("grime", "moss"):
            for _ in range(140):
                x, y = rng.randrange(size), rng.randrange(size)
                r = rng.randint(6, 64)
                d.ellipse([x - r, y - r, x + r, y + r], fill=rng.randint(16, 70))
            img = img.filter(ImageFilter.GaussianBlur(7))
        elif kind == "streak":
            for _ in range(110):
                x = rng.randrange(size)
                w = rng.randint(1, 7)
                y0 = rng.randrange(0, size // 2)
                d.rectangle([x, y0, x + w, min(size, y0 + rng.randint(30, size))],
                            fill=rng.randint(18, 80))
            img = img.filter(ImageFilter.GaussianBlur(2))
        else:
            for _ in range(80):
                x, y = rng.randrange(size), rng.randrange(size)
                d.rectangle([x, y, x + rng.randint(6, 46), y + rng.randint(4, 22)],
                            fill=rng.randint(10, 70))
            img = img.filter(ImageFilter.GaussianBlur(1.5))
        p = OUT / ("tex_wx_fallback_%d.png" % idx)
        img.save(p)
        made.append(p)
    print("weathering: wrote %d fallback tiles" % len(made))
    return made


# ---------------------------------------------------------------- Blender side
def _load_tiles(np):
    """Return list of float32 grayscale arrays in [0,1] for the weathering masks."""
    import bpy
    tiles = []
    files = sorted(WX.glob("tex_wx_*.png")) if WX.exists() else []
    if len(files) < 2:
        files = sorted(OUT.glob("tex_wx_fallback_*.png"))
    for f in files:
        try:
            img = bpy.data.images.load(str(f), check_existing=True)
            w, h = img.size
            px = np.array(img.pixels[:], dtype=np.float32).reshape(h, w, 4)
            tiles.append(px[:, :, :3].mean(axis=2))
        except Exception:
            continue
    if not tiles:
        base = np.zeros((256, 256), dtype=np.float32)
        base[::6] = 0.35
        tiles = [base]
    out = []
    for t in tiles:
        h, w = t.shape
        ys = (np.arange(512) * h // 512).clip(0, h - 1)
        xs = (np.arange(512) * w // 512).clip(0, w - 1)
        out.append(t[ys][:, xs])
    print("weathering: %d wx tiles loaded" % len(out))
    return out


def _material_info(mat):
    """Extract base image + tint color/factor from a Blender material node tree."""
    if not mat.use_nodes:
        return None
    nt = mat.node_tree
    bsdf = None
    for n in nt.nodes:
        if n.type == "BSDF_PRINCIPLED":
            bsdf = n
            break
    if bsdf is None:
        return None
    img = None
    tint = None
    factor = 0.5
    seen = set()

    def walk(sock, depth=0):
        nonlocal img, tint, factor
        if depth > 6:
            return
        for link in sock.links:
            node = link.from_node
            if node.name in seen:
                continue
            seen.add(node.name)
            if node.type == "TEX_IMAGE" and node.image is not None:
                img = node.image
            elif node.type in ("MIX_RGB", "MIX"):
                for inp in node.inputs:
                    if inp.type == "RGBA" and not inp.links:
                        col = tuple(inp.default_value)[:3]
                        if any(abs(c - 1.0) > 0.01 for c in col):
                            tint = col
                for inp in node.inputs:
                    if inp.name.lower() in ("fac", "factor") and not inp.links:
                        fv = inp.default_value
                        try:
                            factor = float(fv)
                        except TypeError:
                            factor = float(fv[0])
                for inp in node.inputs:
                    if inp.links:
                        walk(inp, depth + 1)
            elif node.type == "RGB":
                tint = tuple(node.outputs[0].default_value)[:3]
            else:
                for inp in node.inputs:
                    if inp.links:
                        walk(inp, depth + 1)

    walk(bsdf.inputs.get("Base Color"))
    if img is None:
        try:
            col = tuple(bsdf.inputs["Base Color"].default_value)[:3]
        except Exception:
            col = (0.6, 0.6, 0.6)
        rough = float(bsdf.inputs["Roughness"].default_value) if "Roughness" in bsdf.inputs else 0.9
        return {"image": None, "color": col, "tint": None, "factor": 0.0, "rough": rough}
    rough = float(bsdf.inputs["Roughness"].default_value) if "Roughness" in bsdf.inputs else 0.9
    return {"image": img, "tint": tint, "factor": factor, "rough": rough}


def _make_composite(np, bpy, base_img, tile, tint, factor, variant, out_name, color=None):
    n = 512
    if base_img is None:
        rgb = np.tile(np.array(color or (0.6, 0.6, 0.6), dtype=np.float32), (n, n, 1))
    else:
        w, h = base_img.size
        if w == 0 or h == 0:
            return None
        px = np.array(base_img.pixels[:], dtype=np.float32).reshape(h, w, 4)
        rgb = px[:, :, :3].copy()
        ys = (np.arange(n) * h // n).clip(0, h - 1)
        xs = (np.arange(n) * w // n).clip(0, w - 1)
        rgb = rgb[ys][:, xs]
    # grime mask: GPT tiles are WHITE=clean, DARK=grime -> invert luminance
    ty = (np.arange(n) * tile.shape[0] // n).clip(0, tile.shape[0] - 1)
    tx = (np.arange(n) * tile.shape[1] // n).clip(0, tile.shape[1] - 1)
    lum = tile[ty][:, tx]
    if variant == 1:
        lum = lum[::-1, ::-1]
    mask = np.clip((1.0 - lum - 0.30) / 0.70, 0.0, 1.0)
    # bottom-up dirt gradient (Blender pixel row 0 = bottom)
    grad = np.linspace(1.0, 0.82, n, dtype=np.float32).reshape(n, 1)
    mask = np.clip(mask * grad, 0.0, 1.0)
    dark = np.array([0.16, 0.17, 0.18], dtype=np.float32)
    strength = 0.30 + 0.22 * (abs(hash(out_name)) % 7) / 7.0
    comp = rgb * (1.0 - strength * mask[:, :, None]) + dark * (strength * mask[:, :, None])
    if tint is not None:
        t = np.array(tint, dtype=np.float32)
        f = min(0.55, max(0.0, factor * 0.75))
        comp = comp * (1.0 - f) + (comp * t) * f
    comp = np.clip(comp, 0.0, 1.0)
    out = np.dstack([comp, np.ones((n, n), dtype=np.float32)])
    name = "WX_%s_v%d" % (out_name, variant)
    img = bpy.data.images.new(name, n, n, alpha=False)
    img.pixels[:] = out.ravel().tolist()
    img.filepath_raw = str(OUT / (name + ".png"))
    img.file_format = "PNG"
    try:
        img.save()
    except Exception:
        pass
    try:
        img.pack()
    except Exception:
        pass
    return img


def apply(coll_bld=None, plan=None, variants=2):
    import bpy
    import numpy as np

    OUT.mkdir(parents=True, exist_ok=True)
    if not any(WX.glob("tex_wx_*.png")) and not any(OUT.glob("tex_wx_fallback_*.png")):
        try:
            make_fallback_tiles()
        except Exception as e:  # noqa: BLE001
            print("weathering: cannot make fallback tiles", repr(e)[:120])
    tiles = _load_tiles(np)

    blocks = (plan or {}).get("blocks", [])
    rects = [(b["id"], b["rect"]) for b in blocks]

    def block_id_for(obj):
        c = obj.matrix_world.translation
        best, bd = "SITE", 1e9
        for bid, r in rects:
            cx, cy = (r[0] + r[2]) / 2, (r[1] + r[3]) / 2
            dx = max(abs(c.x - cx) - (r[2] - r[0]) / 2, 0)
            dy = max(abs(c.y - cy) - (r[3] - r[1]) / 2, 0)
            d = dx * dx + dy * dy
            if d < bd:
                bd, best = d, bid
        return best

    root = coll_bld or bpy.data.collections.get("BUILDINGS")
    if root is None:
        print("WEATHERING no BUILDINGS collection")
        return {}
    objs = [o for o in root.all_objects if o.type == "MESH" and o.material_slots]
    mats = {}
    for o in objs:
        for slot in o.material_slots:
            m = slot.material
            if (m and m.name not in mats and _wanted(m.name)
                    and not m.name.startswith("WX_")):
                mats[m.name] = m
    print("weathering: candidate materials", len(mats))

    new_mats = {}
    for name, m in sorted(mats.items()):
        info = _material_info(m)
        if info is None:
            continue
        imgs = []
        for v in range(variants):
            tile = tiles[(abs(hash(name)) + v) % len(tiles)]
            try:
                img = _make_composite(np, bpy, info["image"], tile, info["tint"],
                                      info["factor"], v, name,
                                      color=info.get("color"))
            except Exception as e:  # noqa: BLE001
                print("weathering: composite fail", name, repr(e)[:100])
                img = None
            imgs.append(img)
        for v, img in enumerate(imgs):
            if img is None:
                continue
            mat = bpy.data.materials.new("WX_%s_v%d" % (name, v))
            mat.use_nodes = True
            nt = mat.node_tree
            nt.nodes.clear()
            outn = nt.nodes.new("ShaderNodeOutputMaterial")
            bsdf = nt.nodes.new("ShaderNodeBsdfPrincipled")
            tex = nt.nodes.new("ShaderNodeTexImage")
            tex.image = img
            uv = nt.nodes.new("ShaderNodeUVMap")
            nt.links.new(uv.outputs["UV"], tex.inputs["Vector"])
            nt.links.new(tex.outputs["Color"], bsdf.inputs["Base Color"])
            bsdf.inputs["Roughness"].default_value = min(0.97, max(0.4, info["rough"] + 0.05))
            try:
                bsdf.inputs["Specular IOR Level"].default_value = 0.3
            except Exception:
                pass
            nt.links.new(bsdf.outputs["BSDF"], outn.inputs["Surface"])
            new_mats.setdefault(name, []).append(mat)

    stats = {"materials": len(new_mats), "reassigned": 0, "skipped_no_uv": 0}
    for o in objs:
        if not o.data.uv_layers:
            stats["skipped_no_uv"] += 1
            continue
        bid = block_id_for(o)
        h = abs(hash(bid + "|" + o.name)) % 89
        for slot in o.material_slots:
            m = slot.material
            if m is None or m.name not in new_mats:
                continue
            vs = new_mats[m.name]
            slot.material = vs[h % len(vs)]
            stats["reassigned"] += 1
    print("WEATHERING", stats)
    return stats


if __name__ == "__main__":
    cmd = sys.argv[1] if len(sys.argv) > 1 else "tiles"
    if cmd == "tiles":
        make_fallback_tiles()
    else:
        print("usage: python tools/weathering.py tiles   (Blender pass runs from build.py)")
