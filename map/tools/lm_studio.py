"""lm_studio.py — isolated studio QA harness for landmark modules (Round 4).

Every landmark type is modeled STANDALONE in this studio scene first: neutral ground,
3-point light rig, turntable + close-up cameras, high-res renders, stats printed. Only after
it reads correctly here does build.py place it at its map rect.

CLI (inside Blender):
    blender -b --factory-startup --python tools/lm_studio.py -- \
        --module mod_landmark_monument --type statue_motherland --out out/studio/motherland \
        [--block '{"id":"MOTHERLAND","rect":[-12,138,12,150],"faces":["PY"],"height":40}']

Python API:
    import lm_studio
    lm_studio.render_studio("mod_landmark_towers", "vysotka_main", out_dir, block=dict(...))
"""
import importlib
import json
import math
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))


def _look():
    import look  # noqa: E402
    return look


def setup_studio(scn, kit, root, b):
    """Neutral, PROVEN studio look: preview_util sky/sun + 3 fill points + granite ground."""
    import preview_util

    preview_util.setup(scn, exposure=1.0, fog=False, ground=False)
    x0, y0, x1, y1 = b["rect"]
    cx, cy = (x0 + x1) / 2, (y0 + y1) / 2
    h = float(b.get("height", 20))
    span = max(x1 - x0, y1 - y0, h * 0.8, 12.0)
    ground = kit.mat_texture("STUDIO_GROUND", "tex_concrete_panel.png", rough=0.8, tint="#4A5058")
    kit.box(root, "STUDIO_GROUND", (cx, cy, -0.06), (900, 900, 0.12), ground)
    for name, pos, energy, color in (
        ("STUDIO_FILL_W", (cx - span * 1.6, cy - span * 1.5, h * 0.9), 30000.0, "#D6E2F2"),
        ("STUDIO_FILL_E", (cx + span * 1.6, cy - span * 0.6, h * 0.8), 18000.0, "#FFE2BE"),
        ("STUDIO_FILL_B", (cx, cy + span * 1.8, h * 1.1), 22000.0, "#BFD2EE"),
    ):
        look = __import__("look")
        look.add_point(scn, name, pos, energy=energy, color=color, radius=1.0, shadow=False)


def b_box_h(b):
    return float(b.get("height", 20))


def bbox_all(objs):
    from mathutils import Vector
    lo = Vector((1e9, 1e9, 1e9))
    hi = Vector((-1e9, -1e9, -1e9))
    for o in objs:
        if o.type != "MESH":
            continue
        for c in o.bound_box:
            w = o.matrix_world @ Vector(c)
            lo = Vector((min(lo.x, w.x), min(lo.y, w.y), min(lo.z, w.z)))
            hi = Vector((max(hi.x, w.x), max(hi.y, w.y), max(hi.z, w.z)))
    return lo, hi


def render_studio(module_name, type_name, out_dir, block=None, views=6, res=(1100, 800),
                  samples=24):
    """Build one landmark standalone and render turntable + close-up QA frames."""
    import bpy
    from mathutils import Vector, Matrix

    import kit
    import look

    os.makedirs(out_dir, exist_ok=True)
    kit.reset_scene()
    scn = bpy.context.scene
    coll = kit.collection("STUDIO")
    root = kit.group("BLD_" + (block or {}).get("id", type_name), coll)
    b = dict(block or {})
    b.setdefault("id", type_name)
    b.setdefault("rect", [-20, -20, 20, 20])
    b.setdefault("faces", ["PY"])
    b.setdefault("height", 20)

    mod = importlib.import_module(module_name)
    types = getattr(mod, "TYPES", {})
    fn = types.get(type_name)
    if fn is None:
        raise SystemExit("NO TYPE %s in %s (have %s)" % (type_name, module_name, list(types)))

    ctx = {"plan": {"blocks": [b]}, "wmats": kit.win_materials()}
    stats = fn(root, b, kit.RNG(b.get("seed", 7)), ctx)
    print("STUDIO_BUILT", type_name, stats if isinstance(stats, dict) else "")

    lo, hi = bbox_all([o for o in bpy.data.objects if o.type == "MESH"])
    # frame from the planned block rect + height (modules build at map coordinates)
    x0, y0, x1, y1 = b["rect"]
    cx, cy = (x0 + x1) / 2, (y0 + y1) / 2
    height = float(b.get("height", 20))
    size = max(x1 - x0, y1 - y0, height * 0.85, 10.0)
    cz = height * 0.48
    print("STUDIO_FRAME", round(cx, 1), round(cy, 1), round(size, 1), round(height, 1))

    setup_studio(scn, kit, root, b)

    # render settings
    scn.render.engine = "BLENDER_EEVEE"
    scn.render.resolution_x, scn.render.resolution_y = res
    scn.render.film_transparent = False
    try:
        scn.eevee.taa_render_samples = samples
        scn.eevee.use_raytracing = True
    except Exception:
        pass
    look.setup_color(scn, exposure=1.25, view_transform="Khronos PBR Neutral")

    cam_data = bpy.data.cameras.new("STUDIO_CAM")
    cam = bpy.data.objects.new("STUDIO_CAM", cam_data)
    scn.collection.objects.link(cam)
    scn.camera = cam
    cam_data.clip_end = 5000

    def shot(name, pos, target, lens):
        cam.location = pos
        cam.rotation_euler = (Vector(target) - Vector(pos)).to_track_quat("-Z", "Y").to_euler()
        cam.data.lens = lens
        path = os.path.join(out_dir, name + ".png")
        look.render_still(scn, cam, path, res=res, samples=samples)
        print("STUDIO_RENDER", name)

    # 4 turntable views, elevated 3/4 from each corner; then front silhouette + closeup
    r = max(size * 1.55, height * 2.0)
    hz = cz + max(size * 0.62, height * 0.55)
    target = (cx, cy, cz)
    for i, ang in enumerate((35, 125, 215, 305)):
        a = math.radians(ang)
        shot("turn_%d" % (i + 1), (cx + r * math.sin(a), cy - r * math.cos(a), hz), target,
             45.0)
    # front elevation: from the side of the declared main facade (+Y for PY, -Y for NY)
    faces = [f.upper() for f in (b.get("faces") or ["PY"])]
    d = size * 2.1
    if "NX" in faces and "PX" not in faces:
        shot("front", (cx - d, cy, cz + size * 0.06), (cx, cy, cz), 80.0)
        shot("back", (cx + d, cy, cz + size * 0.06), (cx, cy, cz), 80.0)
    elif "PX" in faces and "NX" not in faces:
        shot("front", (cx + d, cy, cz + size * 0.06), (cx, cy, cz), 80.0)
        shot("back", (cx - d, cy, cz + size * 0.06), (cx, cy, cz), 80.0)
    else:
        from_y = -1.0 if "NY" in faces and "PY" not in faces else 1.0
        shot("front", (cx, cy + from_y * d, cz + size * 0.06), (cx, cy, cz), 80.0)
        shot("back", (cx, cy - from_y * d, cz + size * 0.06), (cx, cy, cz), 80.0)
    # close-up detail at 70% height
    d = size * 0.85
    shot("detail", (cx + d * 0.55, cy - d, lo.z + (hi.z - lo.z) * 0.72),
         (cx, cy, lo.z + (hi.z - lo.z) * 0.68), 70.0)

    print("STUDIO_BBOX", json.dumps({"min": [round(v, 2) for v in lo],
                                     "max": [round(v, 2) for v in hi],
                                     "size": [round(hi.x - lo.x, 2), round(hi.y - lo.y, 2),
                                              round(hi.z - lo.z, 2)]}))
    print("STUDIO_DONE", out_dir)
    return {"min": list(lo), "max": list(hi), "out": out_dir}


if __name__ == "__main__":
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    module = None
    type_name = None
    out = None
    block = None
    i = 0
    while i < len(argv):
        if argv[i] == "--module":
            module = argv[i + 1]; i += 2
        elif argv[i] == "--type":
            type_name = argv[i + 1]; i += 2
        elif argv[i] == "--out":
            out = argv[i + 1]; i += 2
        elif argv[i] == "--block":
            block = json.loads(argv[i + 1]); i += 2
        elif argv[i] == "--block-id":
            import json as _json
            with open(os.path.join(ROOT, "tools", "plan.json"), encoding="utf-8") as fh:
                _plan = _json.load(fh)
            block = next((bb for bb in _plan["blocks"] if bb["id"] == argv[i + 1]), None)
            i += 2
        elif argv[i] == "--block-file":
            with open(argv[i + 1], encoding="utf-8-sig") as fh:
                block = json.load(fh)
            i += 2
        else:
            i += 1
    if not (module and type_name and out):
        raise SystemExit("usage: --module m --type t --out dir [--block json]")
    render_studio(module, type_name, out, block)
