"""render.py — render named cameras from the built blend.

Usage:
    blender -b map/SovietBlueHour_beauty.blend --python tools/render.py -- \
        --cams CAM_HERO,CAM_TOP --out renders --res 1600x900 --samples 64
    blender -b ... --python tools/render.py -- --cams all --preset preview
"""
from __future__ import annotations

import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import bpy

import look

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DEFAULT_CAMS = ["CAM_HERO", "CAM_HERO2", "CAM_TOP", "CAM_PLAZA", "CAM_DK", "CAM_AVE",
                "CAM_COURT_W", "CAM_COURT_E", "CAM_ARCH", "CAM_SCHOOL", "CAM_BOILER",
                "CAM_STADIUM", "CAM_GARAGES", "CAM_TOWER", "CAM_W2_COURT", "CAM_NORTH_PARK",
                "CAM_SOUTH_ST", "CAM_MARKET", "CAM_BANYA", "CAM_ESPLANADE", "CAM_MOTHERLAND",
                "CAM_EAST_Q", "CAM_VYSOTKA", "CAM_TERMINAL", "CAM_METRO", "CAM_DRUZHBA", "CAM_LM_MOTHERLAND", "CAM_LM_MSU", "CAM_LM_UKRAINA",
                "CAM_LM_MFA", "CAM_LM_METRO", "CAM_LM_BREST", "CAM_LM_UFO",
                "CAM_LM_HEX", "CAM_LM_ROBOTICS", "CAM_LM_MOT", "CAM_LM_HIT", "CAM_LM_MUSEUM",
                "CAM_LM_DRUZHBA"]


def parse_args():
    argv = sys.argv
    argv = argv[argv.index("--") + 1:] if "--" in argv else []
    args = {"cams": "CAM_HERO", "out": os.path.join(ROOT, "renders"),
            "res": "1600x900", "samples": 64, "exposure": None, "tag": "",
            "sun": None, "fog": None}
    i = 0
    while i < len(argv):
        a = argv[i]
        if a.startswith("--"):
            k = a[2:]
            v = argv[i + 1] if i + 1 < len(argv) else "1"
            args[k] = v
            i += 2
        else:
            i += 1
    return args


def main():
    args = parse_args()
    scn = bpy.context.scene
    os.makedirs(args["out"], exist_ok=True)
    w, h = [int(v) for v in args["res"].lower().split("x")]
    samples = int(args["samples"])
    cams = DEFAULT_CAMS if args["cams"] in ("all", "*") else [c.strip() for c in args["cams"].split(",")]
    if args["exposure"]:
        scn.view_settings.exposure = float(args["exposure"])
    if args["sun"]:
        bpy.data.objects["SUN_MOON"].data.energy = float(args["sun"])
    if args["fog"]:
        mat = bpy.data.materials.get("FOG_MAT")
        if mat:
            for n in mat.node_tree.nodes:
                if n.type == "MATH" and n.operation == "MULTIPLY":
                    n.inputs[1].default_value = float(args["fog"])
    if hasattr(scn, "eevee"):
        scn.eevee.taa_render_samples = samples
        scn.eevee.volumetric_samples = max(16, samples)
        scn.eevee.volumetric_tile_size = "8"
    if args.get("fast", "0") in ("1", "true", "yes"):
        for _o in bpy.data.objects:
            if _o.type == "LIGHT" and _o.name != "SUN_MOON":
                _o.data.use_shadow = False
        scn.eevee.use_raytracing = False
        scn.eevee.use_volumetric_shadows = False
    tag = ("_" + args["tag"]) if args["tag"] else ""
    for name in cams:
        cam = bpy.data.objects.get(name)
        if cam is None:
            print("CAM_MISSING", name)
            continue
        path = os.path.join(args["out"], f"{name}{tag}.png")
        look.render_still(scn, cam, path, (w, h), samples)


if __name__ == "__main__":
    main()
