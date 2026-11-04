"""look.py — blue-hour sky, fog, lighting, compositor and render setup.

All functions are deterministic and headless-safe. The sky dome + fog volumes are
render-only objects (collection RENDER_ONLY) and must be excluded from game exports.
"""
from __future__ import annotations

import math
import os

import bpy
from mathutils import Vector

import kit

# blue-hour palette (sRGB)
SKY_ZENITH = "#0A1220"
SKY_UPPER = "#132541"
SKY_MID = "#28486E"
SKY_HORIZON = "#6E8EB0"
SKY_BAND = "#93A9BE"
FOG_COLOR = "#2C4058"
AMBIENT = "#22344E"
SUN_COLOR = "#9DB8DD"
LAMP_COLOR = "#FFB268"
PORCH_COLOR = "#FFC07A"


def _collection(scene, name):
    coll = bpy.data.collections.get(name)
    if coll is None:
        coll = bpy.data.collections.new(name)
        scene.collection.children.link(coll)
    return coll


def make_sky(scene, radius=700.0, colors=None, strength=1.0):
    """Gradient sky dome: pale ant-twilight band at the horizon, indigo zenith."""
    colors = colors or {}
    zenith = colors.get("zenith", SKY_ZENITH)
    upper = colors.get("upper", SKY_UPPER)
    mid = colors.get("mid", SKY_MID)
    horizon = colors.get("horizon", SKY_HORIZON)
    band = colors.get("band", SKY_BAND)
    coll = _collection(scene, "RENDER_ONLY")
    dome = bpy.data.meshes.get("SKY_DOME")
    if dome is None:
        import bmesh
        bm = bmesh.new()
        bmesh.ops.create_uvsphere(bm, u_segments=64, v_segments=32, radius=radius)
        bmesh.ops.reverse_faces(bm, faces=list(bm.faces))
        dome = bpy.data.meshes.new("SKY_DOME")
        bm.to_mesh(dome)
        bm.free()
    mat = bpy.data.materials.get("SKY_MAT")
    if mat is None:
        mat = bpy.data.materials.new("SKY_MAT")
        mat.use_nodes = True
        nt = mat.node_tree
        for n in list(nt.nodes):
            nt.nodes.remove(n)
        out = nt.nodes.new("ShaderNodeOutputMaterial")
        em = nt.nodes.new("ShaderNodeEmission")
        ramp = nt.nodes.new("ShaderNodeValToRGB")
        tc = nt.nodes.new("ShaderNodeTexCoord")
        sep = nt.nodes.new("ShaderNodeSeparateXYZ")
        em.inputs["Strength"].default_value = strength
        nt.links.new(tc.outputs["Generated"], sep.inputs["Vector"])
        nt.links.new(sep.outputs["Z"], ramp.inputs["Fac"])
        stops = [(0.0, band), (0.50, horizon), (0.63, mid),
                 (0.82, upper), (1.0, zenith)]
        ramp.color_ramp.elements[0].position = stops[0][0]
        ramp.color_ramp.elements[0].color = kit.srgb(stops[0][1])
        ramp.color_ramp.elements[1].position = stops[-1][0]
        ramp.color_ramp.elements[1].color = kit.srgb(stops[-1][1])
        for pos, col in stops[1:-1]:
            el = ramp.color_ramp.elements.new(pos)
            el.color = kit.srgb(col)
        nt.links.new(ramp.outputs["Color"], em.inputs["Color"])
        nt.links.new(em.outputs["Emission"], out.inputs["Surface"])
    obj = bpy.data.objects.new("SKY_DOME", dome)
    coll.objects.link(obj)
    obj.data.materials.append(mat)
    obj.hide_select = True
    obj.visible_shadow = False
    return obj


def make_world(scene, ambient_strength=0.55, volume_density=0.0,
               volume_color=FOG_COLOR):
    world = bpy.data.worlds.get("SBH_WORLD") or bpy.data.worlds.new("SBH_WORLD")
    world.use_nodes = True
    scene.world = world
    nt = world.node_tree
    for n in list(nt.nodes):
        nt.nodes.remove(n)
    out = nt.nodes.new("ShaderNodeOutputWorld")
    bg = nt.nodes.new("ShaderNodeBackground")
    bg.inputs["Color"].default_value = kit.srgb(AMBIENT)
    bg.inputs["Strength"].default_value = ambient_strength
    vol = nt.nodes.new("ShaderNodeVolumeScatter")
    vol.inputs["Color"].default_value = kit.srgb(volume_color)
    vol.inputs["Density"].default_value = volume_density
    vol.inputs["Anisotropy"].default_value = 0.45
    nt.links.new(bg.outputs[0], out.inputs["Surface"])
    nt.links.new(vol.outputs[0], out.inputs["Volume"])
    return world


def make_ground_fog(scene, rect=(-140, -110, 140, 110), height=4.5, density=0.03,
                    emission=0.02):
    """Volumetric ground mist with density falling off with height (Principled Volume).

    EEVEE volumes receive very little light in this scene, so a small emission term keeps the
    haze luminous like real pre-dawn fog (VolumeEmission node does not exist in Blender 5.2).
    """
    x0, y0, x1, y1 = rect
    coll = _collection(scene, "RENDER_ONLY")
    mesh = bpy.data.meshes.new("GROUND_FOG")
    hx, hy = (x1 - x0) / 2, (y1 - y0) / 2
    pts = [(-hx, -hy, 0), (hx, -hy, 0), (hx, hy, 0), (-hx, hy, 0),
           (-hx, -hy, height), (hx, -hy, height), (hx, hy, height), (-hx, hy, height)]
    faces = [(0, 1, 2, 3), (7, 6, 5, 4), (0, 4, 5, 1), (1, 5, 6, 2), (2, 6, 7, 3), (3, 7, 4, 0)]
    mesh.from_pydata(pts, [], faces)
    mesh.update()
    obj = bpy.data.objects.new("GROUND_FOG", mesh)
    coll.objects.link(obj)
    obj.location = ((x0 + x1) / 2, (y0 + y1) / 2, 0)
    mat = bpy.data.materials.new("FOG_MAT")
    mat.use_nodes = True
    nt = mat.node_tree
    for n in list(nt.nodes):
        nt.nodes.remove(n)
    out = nt.nodes.new("ShaderNodeOutputMaterial")
    pv = nt.nodes.new("ShaderNodeVolumePrincipled")
    pv.inputs["Color"].default_value = kit.srgb(FOG_COLOR)
    pv.inputs["Anisotropy"].default_value = 0.25
    try:
        pv.inputs["Emission Color"].default_value = kit.srgb(FOG_COLOR)
        pv.inputs["Absorption Color"].default_value = (0, 0, 0, 1)
    except Exception:
        pass
    tc = nt.nodes.new("ShaderNodeTexCoord")
    sep = nt.nodes.new("ShaderNodeSeparateXYZ")
    mr = nt.nodes.new("ShaderNodeMapRange")
    mul = nt.nodes.new("ShaderNodeMath")
    mul.operation = "MULTIPLY"
    mul.inputs[1].default_value = density
    mr.inputs["From Min"].default_value = 0.0
    mr.inputs["From Max"].default_value = 1.0
    mr.inputs["To Min"].default_value = 1.0
    mr.inputs["To Max"].default_value = 0.0
    nt.links.new(tc.outputs["Generated"], sep.inputs["Vector"])
    nt.links.new(sep.outputs["Z"], mr.inputs["Value"])
    nt.links.new(mr.outputs["Result"], mul.inputs[0])
    nt.links.new(mul.outputs[0], pv.inputs["Density"])
    # emission follows the same height gradient so the fog slab has no hard top edge
    msq = nt.nodes.new("ShaderNodeMath")
    msq.operation = "MULTIPLY"
    nt.links.new(mr.outputs["Result"], msq.inputs[0])
    nt.links.new(mr.outputs["Result"], msq.inputs[1])
    emul = nt.nodes.new("ShaderNodeMath")
    emul.operation = "MULTIPLY"
    emul.inputs[1].default_value = emission
    nt.links.new(msq.outputs[0], emul.inputs[0])
    try:
        nt.links.new(emul.outputs[0], pv.inputs["Emission Strength"])
    except Exception:
        pass
    nt.links.new(pv.outputs[0], out.inputs["Volume"])
    mesh.materials.append(mat)
    obj.hide_select = True
    obj.visible_shadow = False
    return obj


def add_sun(scene, strength=2.6, angle_deg=14.0, color=SUN_COLOR,
            rot=(math.radians(64), 0, math.radians(-28))):
    data = bpy.data.lights.new("SUN_MOON", "SUN")
    data.energy = strength
    data.color = kit.srgb(color)[:3]
    data.angle = math.radians(angle_deg)
    obj = bpy.data.objects.new("SUN_MOON", data)
    _collection(scene, "LIGHTS").objects.link(obj)
    obj.rotation_euler = rot
    return obj


def add_sky_fill(scene, intensity=260.0, color=AMBIENT):
    """Large soft area lights standing in for the bright sky dome (EEVEE ambient)."""
    coll = _collection(scene, "LIGHTS")
    specs = [
        ("FILL_UP", (0, 0, 90), (0, 0, 0), 220),
        ("FILL_S", (0, -160, 40), (0, 0, 0), 300),
        ("FILL_E", (160, 0, 40), (0, 0, 0), 240),
        ("FILL_W", (-160, 0, 40), (0, 0, 0), 240),
    ]
    for name, loc, target, size in specs:
        data = bpy.data.lights.new(name, "AREA")
        data.energy = intensity
        data.color = kit.srgb(color)[:3]
        data.shape = "SQUARE"
        data.size = size
        obj = bpy.data.objects.new(name, data)
        coll.objects.link(obj)
        obj.location = loc
        d = Vector(target) - Vector(loc)
        obj.rotation_mode = "QUATERNION"
        obj.rotation_quaternion = d.to_track_quat("-Z", "Y")


def add_point(scene, name, pos, energy=1200.0, color=LAMP_COLOR, radius=0.4,
              shadow=False):
    data = bpy.data.lights.new(name, "POINT")
    data.energy = energy
    data.color = kit.srgb(color)[:3]
    data.shadow_soft_size = radius
    data.use_shadow = shadow
    obj = bpy.data.objects.new(name, data)
    _collection(scene, "LIGHTS").objects.link(obj)
    obj.location = pos
    return obj


def add_spot(scene, name, pos, target, energy=800.0, color=PORCH_COLOR,
             angle=60.0, blend=0.4, shadow=False):
    data = bpy.data.lights.new(name, "SPOT")
    data.energy = energy
    data.color = kit.srgb(color)[:3]
    data.spot_size = math.radians(angle)
    data.spot_blend = blend
    data.use_shadow = shadow
    obj = bpy.data.objects.new(name, data)
    _collection(scene, "LIGHTS").objects.link(obj)
    obj.location = pos
    d = Vector(target) - Vector(pos)
    obj.rotation_mode = "QUATERNION"
    obj.rotation_quaternion = d.to_track_quat("-Z", "Y")
    return obj


def setup_eevee(scene, samples=48, volumetric_samples=96, tile="4"):
    scene.render.engine = "BLENDER_EEVEE"
    ee = scene.eevee
    ee.taa_render_samples = samples
    ee.taa_samples = max(16, samples // 2)
    ee.use_raytracing = True
    ee.ray_tracing_options.resolution_scale = "2"
    ee.use_volumetric_shadows = True
    ee.volumetric_tile_size = tile
    ee.volumetric_samples = volumetric_samples
    ee.volumetric_start = 0.5
    ee.volumetric_end = 320.0
    ee.use_shadows = True
    ee.shadow_pool_size = "2048"
    ee.use_fast_gi = True
    scene.render.film_transparent = False
    scene.render.image_settings.file_format = "PNG"
    scene.render.image_settings.color_depth = "8"
    scene.render.resolution_percentage = 100


def setup_color(scene, exposure=1.8, view_transform="AgX"):
    vs = scene.view_settings
    try:
        vs.view_transform = view_transform
    except Exception:  # noqa: BLE001
        pass
    vs.exposure = exposure
    vs.gamma = 1.0
    try:
        vs.look = "None"
    except Exception:  # noqa: BLE001
        pass


def setup_compositor(scene, bloom_strength=0.6, bloom_threshold=0.7,
                     bloom_size=0.8, exposure=0.0, saturation=0.78, contrast=0.0,
                     vignette=0.35):
    scene.use_nodes = True
    ng = bpy.data.node_groups.get("SBH_COMP")
    if ng is None:
        ng = bpy.data.node_groups.new("SBH_COMP", "CompositorNodeTree")
    for n in list(ng.nodes):
        ng.nodes.remove(n)
    scene.compositing_node_group = ng
    ng.interface.clear()
    ng.interface.new_socket(name="Image", in_out="OUTPUT", socket_type="NodeSocketColor")
    rl = ng.nodes.new("CompositorNodeRLayers")
    rl.location = (-600, 0)
    glare = ng.nodes.new("CompositorNodeGlare")
    glare.location = (-380, 0)
    glare.inputs["Type"].default_value = "Bloom"
    glare.inputs["Quality"].default_value = "High"
    glare.inputs["Threshold"].default_value = bloom_threshold
    glare.inputs["Strength"].default_value = bloom_strength
    glare.inputs["Size"].default_value = bloom_size
    # cold colour grade: slightly blue lift, cold gain
    cb = ng.nodes.new("CompositorNodeColorBalance")
    cb.location = (-160, 0)
    try:
        cb.inputs["Type"].default_value = "Lift/Gamma/Gain"
        lift = next(i for i in cb.inputs if i.name == "Lift" and i.type == "RGBA")
        gain = next(i for i in cb.inputs if i.name == "Gain" and i.type == "RGBA")
        lift.default_value = (1.0, 1.0, 1.0, 1.0)
        gain.default_value = (1.0, 1.0, 0.98, 1.0)
    except Exception:  # noqa: BLE001
        pass
    exp = ng.nodes.new("CompositorNodeExposure")
    exp.location = (60, 0)
    exp.inputs["Exposure"].default_value = exposure
    out = ng.nodes.new("NodeGroupOutput")
    out.location = (280, 0)
    ng.links.new(rl.outputs["Image"], glare.inputs["Image"])
    # colour balance disabled: its Lift washed the whole frame milky in this build
    hs = ng.nodes.new("CompositorNodeHueSat")
    hs.location = (-20, 0)
    try:
        hs.inputs["Saturation"].default_value = saturation
    except Exception:
        pass
    ng.links.new(glare.outputs["Image"], hs.inputs["Image"])
    ng.links.new(hs.outputs["Image"], exp.inputs["Image"])
    ng.links.new(exp.outputs["Image"], out.inputs["Image"])
    return ng


def add_camera(scene, name, loc, target, lens=35.0, dof=None, fstop=8.0):
    data = bpy.data.cameras.new(name)
    data.lens = lens
    data.sensor_width = 36.0
    data.clip_start = 0.3
    data.clip_end = 2000.0
    if dof:
        data.dof.use_dof = True
        data.dof.focus_distance = dof
        data.dof.aperture_fstop = fstop
    obj = bpy.data.objects.new(name, data)
    _collection(scene, "CAMERAS").objects.link(obj)
    obj.location = loc
    d = Vector(target) - Vector(loc)
    obj.rotation_mode = "QUATERNION"
    obj.rotation_quaternion = d.to_track_quat("-Z", "Y")
    return obj


def render_still(scene, camera, path, res=(1600, 900), samples=64):
    scene.camera = camera
    scene.render.resolution_x = res[0]
    scene.render.resolution_y = res[1]
    scene.render.filepath = path
    bpy.ops.render.render(write_still=True)
    print("RENDERED", path)
    return path

