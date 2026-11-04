"""Robust top-down map render: EEVEE with volumes disabled, fallback to Workbench."""
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import bpy

import kit
import look

ROOT = kit.ROOT
OUT = os.path.join(ROOT, "renders", "final")
os.makedirs(OUT, exist_ok=True)
scn = bpy.context.scene

# remove render-only volumetric helpers entirely (ortho + volumes hangs EEVEE here)
for name in ("GROUND_FOG", "SKY_DOME"):
    o = bpy.data.objects.get(name)
    if o:
        bpy.data.objects.remove(o, do_unlink=True)
for m in list(bpy.data.materials):
    if m.name in ("FOG_MAT", "SKY_MAT"):
        bpy.data.materials.remove(m)

# no volumetric shadows, fewer lights with shadows
scn.eevee.use_volumetric_shadows = False
scn.eevee.volumetric_end = 1.0
for o in bpy.data.objects:
    if o.type == "LIGHT":
        o.data.use_shadow = False
sun = bpy.data.objects.get("SUN_MOON")
if sun:
    sun.data.energy = 4.0
    sun.data.angle = 0.2
scn.view_settings.exposure = 1.9

cam = bpy.data.objects.get("CAM_TOP")
if cam:
    cam.data.ortho_scale = 442
    scn.camera = cam
scn.render.resolution_x = 1600
scn.render.resolution_y = 1200
scn.eevee.taa_render_samples = 16
scn.render.filepath = os.path.join(OUT, "CAM_TOP_map.png")
try:
    bpy.ops.render.render(write_still=True)
    print("RENDERED_EEVEE", scn.render.filepath)
except Exception as e:  # noqa: BLE001
    print("EEVEE_FAIL", repr(e)[:200])

# Workbench fallback / bonus clay plan
scn.render.engine = "BLENDER_WORKBENCH"
sh = scn.display.shading
sh.light = "STUDIO"
sh.color_type = "MATERIAL"
sh.show_shadows = True
sh.show_cavity = True
sh.cavity_type = "BOTH"
scn.render.filepath = os.path.join(OUT, "CAM_TOP_clay.png")
bpy.ops.render.render(write_still=True)
print("RENDERED_WORKBENCH", scn.render.filepath)
print("DONE")
