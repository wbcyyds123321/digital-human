"""preview_util.py — quick look + render helpers for module test scripts."""
from __future__ import annotations

import math
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import bpy

import kit
import look


def setup(scene=None, exposure=0.55, fog=True, ground=True, ground_size=240):
    scn = scene or bpy.context.scene
    look.make_sky(scn, strength=0.85, colors={
        "zenith": "#12203A", "upper": "#24406A", "mid": "#35527F",
        "horizon": "#4E6588", "band": "#68809F"})
    look.make_world(scn, ambient_strength=1.5, volume_density=0.0)
    if fog:
        look.make_ground_fog(scn, (-ground_size, -ground_size, ground_size, ground_size),
                             height=8.0, density=0.012)
    look.add_sun(scn, strength=1.15, angle_deg=14)
    look.add_sky_fill(scn, intensity=700.0)
    look.setup_eevee(scn, samples=24, volumetric_samples=48, tile="4")
    look.setup_color(scn, exposure=exposure)
    look.setup_compositor(scn, bloom_strength=0.55, bloom_threshold=0.9, bloom_size=0.85)
    if ground:
        root = bpy.data.objects.get("PREVIEW_GROUND")
        if root is None:
            mat = kit.mat_solid("PREVIEW_ASPHALT", "#30363E", rough=0.45, metallic=0.12)
            kit.ground_quad(None, "PREVIEW_GROUND", (-ground_size, -ground_size,
                                                      ground_size, ground_size), 0.0, mat, tile=6.0)
    return scn


def cameras(scene, positions, lens=32.0):
    cams = []
    for i, (name, loc, target) in enumerate(positions):
        cams.append(look.add_camera(scene, name, loc, target, lens))
    return cams


def render(blend_stem, object_focus=None):
    """Render two standard views of the current scene into out/test_<builder>/."""
    import subprocess
    return None


def quick_cams(scene, center=(0, 0, 0), span=60.0):
    """Two sensible cameras around a building centre: 3/4 + street level."""
    cx, cy, cz = center
    a = look.add_camera(scene, "CAM_34",
                        (cx + span * 0.85, cy - span * 0.95, span * 0.55),
                        (cx, cy, cz + 4.0), 34)
    b = look.add_camera(scene, "CAM_STREET",
                        (cx + span * 0.15, cy - span * 0.72, 1.7),
                        (cx, cy, cz + 3.5), 20)
    return [a, b]


def render_quick(scn, cams, out_dir, prefix="preview", res=(900, 506), samples=24):
    os.makedirs(out_dir, exist_ok=True)
    paths = []
    for i, cam in enumerate(cams):
        p = os.path.join(out_dir, f"{prefix}_{i}_{cam.name}.png")
        look.render_still(scn, cam, p, res, samples)
        paths.append(p)
    return paths
