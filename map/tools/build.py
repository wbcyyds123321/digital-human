"""build.py — assemble the Soviet Blue-Hour microdistrict.

Usage:
    blender -b --factory-startup --python tools/build.py [-- --no-render]

Creates map/SovietBlueHour_beauty.blend + map/scene_manifest.json + map/build_stats.json.
Placeholder boxes are used for module types whose builder file is missing (so the
site/look pipeline can be tested before all builders land).
"""
from __future__ import annotations

import importlib
import json
import math
import os
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import bpy

import kit
import look
import sitekit

ROOT = kit.ROOT
MAP_DIR = os.path.join(ROOT, "map")
os.makedirs(MAP_DIR, exist_ok=True)

PLAN = json.load(open(os.path.join(ROOT, "tools", "plan.json"), encoding="utf-8"))

MODULES = [
    ("mod_residential", "TYPES"),
    ("mod_civic", "TYPES"),
    ("mod_industrial", "TYPES"),
    ("mod_landmark_monument", "TYPES"),
    ("mod_landmark_towers", "TYPES"),
    ("mod_landmark_modern", "TYPES"),
]


def load_module(name, attr):
    try:
        mod = importlib.import_module(name)
        return ({}, getattr(mod, attr, {}))[1]
    except Exception as e:  # noqa: BLE001
        print("MODULE_MISSING", name, repr(e)[:200])
        return {}


def block_height(b):
    t = b.get("type", "")
    floors = b.get("floors", 5)
    if t == "khrush_panel" or t == "khrush_brick" or t == "khrush_arch" or t == "khrush_mod":
        return 0.0 + floors * 2.72 + 1.0
    if t == "brezhnevka_9":
        return floors * 2.8 + 1.4
    if t == "boiler":
        return 9.0
    if t == "dk":
        return 12.0
    if t == "school":
        return 11.0
    if t == "kindergarten":
        return 8.0
    if t == "shop":
        return 9.0
    if t == "gym":
        return 11.5
    if t == "clinic":
        return 8.0
    if t == "garages":
        return 3.2
    if t == "banya":
        return 12.0
    if t == "market":
        return 8.0
    if t == "cinema":
        return 11.0
    if t == "dombyta":
        return 8.0
    if t == "bakery":
        return 6.0
    if t == "stadium_stand":
        return 4.0
    if t == "frame":
        return 14.0
    if t == "kiosk":
        return 3.0
    LM_HEIGHTS = {
        "statue_motherland": 34.0, "relief_wall": 6.5, "metro_pavilion": 9.0,
        "vysotka_main": 52.0, "vysotka_hotel": 42.0, "vysotka_ministry": 38.0,
        "institute_sino": 24.0, "museum_colonnade": 22.0, "institute_robotics": 16.0,
        "ministry_transport": 28.0, "sanatorium_druzhba": 12.0,
        "bus_station_ufo": 10.0, "bus_station_hex": 12.0,
    }
    if t in LM_HEIGHTS:
        return float(b.get("height", LM_HEIGHTS[t]))
    return floors * 3.0


def fallback_building(parent, b, rng, ctx):
    """Simple placeholder so the scene reads correctly before modules land."""
    x0, y0, x1, y1 = b["rect"]
    h = block_height(b)
    wall = kit.mat_solid("FB_WALL", "#8E969A", rough=0.9)
    plinth = kit.mat_solid("FB_PLINTH", kit.PAL["plinth"], rough=0.95)
    wm = ctx["wmats"]
    floors = max(1, int(h / 2.8))
    for face in ("PY", "NY", "PX", "NX"):
        try:
            kit.facade_wall(parent, f"{b['id']}_{face}", b["rect"], face, 0.0, h, wall, rng,
                            floors=floors, floor_h=h / floors, win_w=1.35, win_h=1.4,
                            spacing=3.0, wmats=wm, margin=1.2, lit_ratio=0.1,
                            thickness=0.32, plinth_h=1.0, plinth_mat=plinth,
                            balcony_cols=[], joints=False)
        except Exception as e:  # noqa: BLE001
            print("FB_WALL_FAIL", b["id"], face, repr(e)[:160])
    kit.roof_flat(parent, f"{b['id']}_roof", b["rect"], h, kit.mat_solid("FB_ROOF", "#3C4246", rough=0.9))
    kit.parapet(parent, f"{b['id']}_parapet", b["rect"], h, 0.6, 0.2, wall)


def add_entrance_lights(blocks, coll):
    """One warm porch light per main entrance of key buildings (cheap)."""
    key_types = {"dk", "school", "kindergarten", "clinic", "shop", "gym", "khrush_arch",
                 "brezhnevka_9", "banya", "market", "cinema", "dombyta", "bakery"}
    n = 0
    for b in blocks:
        if b["type"] not in key_types:
            continue
        x0, y0, x1, y1 = b["rect"]
        cx, cy = (x0 + x1) / 2, (y0 + y1) / 2
        faces = b.get("faces") or ["NY"]
        for face in faces[:1]:
            if face == "PY":
                pos = (cx, y1 + 1.3, 2.7)
            elif face == "NY":
                pos = (cx, y0 - 1.3, 2.7)
            elif face == "PX":
                pos = (x1 + 1.3, cy, 2.7)
            else:
                pos = (x0 - 1.3, cy, 2.7)
            look.add_point(bpy.context.scene, f"PORCHLIGHT_{b['id']}", pos,
                           40.0 + 30.0 * (b["type"] in ("dk", "school")),
                           color="#FFC078", radius=0.2)
            n += 1
    print("PORCH_LIGHTS", n)


def build_lamps(lamps):
    shadow_budget = 10
    for i, lamp in enumerate(lamps):
        pos = lamp["pos"]
        kind = lamp.get("kind", "street")
        energy = {"street": 800.0, "plaza": 330.0, "court": 300.0,
                  "park": 180.0}.get(kind, 700.0)
        sh = i < shadow_budget
        col = {"street": "#FFA64D", "plaza": "#FFC98A", "court": "#FFC078",
               "park": "#FFD2A0"}.get(kind, "#FFC98A")
        look.add_point(bpy.context.scene, f"LAMP_LIGHT_{i:03d}", pos, energy,
                       color=col, radius=0.3, shadow=sh)
    print("LAMP_LIGHTS", len(lamps))


def add_plaza_fill():
    # soft cool fill over the plaza so the monument reads
    look.add_spot(bpy.context.scene, "FILL_PLAZA", (0, -80, 40), (0, -44, 0),
                  2600, color="#8FA8CC", angle=70, blend=0.8)



def add_monument_floodlights():
    """Cold-white floodlights aimed up at the Motherland statue."""
    scn = bpy.context.scene
    for i, (pos, target, energy) in enumerate([
        ((-11, 135.5, 3.8), (0, 144, 26), 5200),
        ((11, 135.5, 3.8), (0, 144, 26), 5200),
        ((0, 132.0, 4.4), (0, 143, 30), 3800),
    ]):
        look.add_spot(scn, "MONUMENT_FLOOD_%d" % i, pos, target, energy=energy,
                      color="#D6E2F2", angle=42.0, blend=0.35, shadow=False)
    print("MONUMENT_FLOODLIGHTS 3")



def add_landmark_lights():
    """Subtle accent lighting for the Round-4 landmarks (floodlights + warm entrances)."""
    scn = bpy.context.scene

    def spot(name, pos, target, energy, color, radius=0.8):
        look.add_spot(scn, name, pos, target, energy=energy, color=color,
                      radius=radius, shadow=False)

    def glow(name, pos, energy, color, radius=0.6):
        look.add_point(scn, name, pos, energy=energy, color=color, radius=radius,
                       shadow=False)

    # Stalinist towers: cold-white facade washes from the ground
    for name, pos, tgt, e in (
        ("MSU", (150, 20, 4), (186, 55, 42), 26000),
        ("UKRAINA", (150, -2, 3), (185, 13, 30), 14000),
        ("MFA", (150, -44, 3), (186, -19, 24), 14000),
    ):
        look.add_spot(scn, "LM_FLOOD_" + name, pos, tgt, energy=e,
                      color="#D9E4F4", angle=55.0, blend=0.4, shadow=False)
    # metro pavilion: warm entrance + interior glow
    glow("LM_METRO_GLOW", (148, 51, 3.0), 900.0, "#FFC98A", 0.7)
    look.add_spot(scn, "LM_FLOOD_METRO", (128, 52, 2.2), (148, 50, 5.5), 5000,
                  color="#FFE0B8", angle=45.0, blend=0.4, shadow=False)
    # UFO station: under-disc ring + pylon
    glow("LM_UFO_UNDER", (-61, -123, 3.2), 1400.0, "#FFD2A0", 1.2)
    glow("LM_UFO_TOP", (-61, -123, 8.2), 300.0, "#FF6A5A", 0.3)
    # hex station: warm waiting hall
    glow("LM_HEX_GLOW", (48, -124, 3.0), 900.0, "#FFC98A", 1.0)
    # Druzhba: mosaic band wash
    look.add_spot(scn, "LM_FLOOD_DRUZHBA", (138, -120, 2.0), (146, -98, 6.0), 5000,
                  color="#FFD9B0", angle=60.0, blend=0.4, shadow=False)
    # institutes / ministry entrances
    glow("LM_ROBOTICS_ENTRY", (140, 103, 3.0), 650.0, "#FFD2A0", 0.6)
    glow("LM_MOT_ENTRY", (186, -49, 3.0), 650.0, "#DCE6F2", 0.6)
    glow("LM_HIT_ENTRY", (140, 32, 3.0), 650.0, "#FFD2A0", 0.6)
    glow("LM_MUSEUM_ENTRY", (142, 74, 3.0), 650.0, "#FFE0B8", 0.6)
    glow("LM_BREST_LIGHT", (-32, 124, 2.2), 300.0, "#FFD2A0", 0.5)
    print("LANDMARK_LIGHTS 13")


def main():
    t0 = time.time()
    kit.reset_scene()
    scn = bpy.context.scene
    rng = kit.RNG(2024)

    coll_root = kit.collection("SOVIET_BLUEHOUR")
    coll_site = kit.collection("SITE", coll_root)
    coll_bld = kit.collection("BUILDINGS", coll_root)
    coll_props = kit.collection("PROPS", coll_root)
    coll_veh = kit.collection("VEHICLES", coll_root)

    ctx = {"plan": PLAN, "wmats": kit.win_materials()}

    # ---- site ----
    site_root = kit.group("SITE_ROOT", coll_site)
    sitekit.build(site_root, PLAN, rng)

    # ---- buildings ----
    module_types = {}
    for mod_name, attr in MODULES:
        module_types.update(load_module(mod_name, attr))
    blocks = PLAN["blocks"]
    manifest = []
    total_types = {}
    for b in blocks:
        bdir = kit.group(f"BLD_{b['id']}", coll_bld)
        bdir["sbh_id"] = b["id"]
        bdir["sbh_cn"] = b.get("cn", "")
        bdir["sbh_type"] = b["type"]
        rng_b = kit.RNG(b.get("seed", 1))
        fn = module_types.get(b["type"])
        if fn is None:
            fallback_building(bdir, b, rng_b, ctx)
            print("FALLBACK", b["id"], b["type"])
        else:
            try:
                stats = fn(bdir, b, rng_b, ctx)
                print("BUILT", b["id"], b["type"], stats if isinstance(stats, dict) else "")
            except Exception as e:  # noqa: BLE001
                import traceback
                traceback.print_exc()
                print("BUILD_FAIL", b["id"], b["type"], repr(e)[:200])
                fallback_building(bdir, b, rng_b, ctx)
        if b.get("z"):
            try:
                bdir.location.z = float(b["z"])
            except Exception:
                pass
        total_types[b["type"]] = total_types.get(b["type"], 0) + 1
        x0, y0, x1, y1 = b["rect"]
        cx, cy = (x0 + x1) / 2, (y0 + y1) / 2
        manifest.append({
            "id": b["id"], "cn": b.get("cn", ""), "type": b["type"],
            "floors": b.get("floors", 5), "faces": b.get("faces", []),
            "rect": b["rect"], "center_blender": [cx, cy],
            "center_unity": [cx, 0.0, -cy],
            "height_m": round(block_height(b), 2),
            "footprint_m2": round((x1 - x0) * (y1 - y0), 1),
        })

    # ---- props ----
    lamps = []
    props_root = kit.group("PROPS_ROOT", coll_props)
    try:
        props = importlib.import_module("props")
        res = props.build(props_root, PLAN, kit.RNG(77), ctx)
        if isinstance(res, dict):
            lamps = res.get("lamps", [])
        print("PROPS", res if not isinstance(res, dict) else {k: (len(v) if isinstance(v, list) else v) for k, v in res.items()})
    except Exception as e:  # noqa: BLE001
        import traceback
        traceback.print_exc()
        print("PROPS_MISSING", repr(e)[:200])

    # ---- vehicles ----
    veh_root = kit.group("VEHICLES_ROOT", coll_veh)
    try:
        veh = importlib.import_module("vehicles")
        res = veh.build(veh_root, PLAN, kit.RNG(88), ctx)
        print("VEHICLES", res)
    except Exception as e:  # noqa: BLE001
        import traceback
        traceback.print_exc()
        print("VEHICLES_MISSING", repr(e)[:200])

    # ---- weathering material pass (all buildings) ----
    try:
        import weathering
        weathering.apply(coll_bld, PLAN)
    except Exception as e:  # noqa: BLE001
        import traceback
        traceback.print_exc()
        print("WEATHERING_FAIL", repr(e)[:200])

    # ---- lift puddles above the road/sidewalk surfaces (props placed them at z=0.012) ----
    lifted = 0
    for o in bpy.data.objects:
        if o.type != "MESH":
            continue
        ln = o.name.lower()
        if "puddle" in ln or "wetpatch" in ln or "wet_patch" in ln:
            o.location.z += 0.047
            lifted += 1
    print("PUDDLES_LIFTED", lifted)
    # make puddles read as wet mirrors
    for m in bpy.data.materials:
        ln = m.name.lower()
        if ("puddle" in ln or "wet" in ln) and m.use_nodes:
            b = m.node_tree.nodes.get("Principled BSDF")
            if b:
                try:
                    b.inputs["Roughness"].default_value = 0.06
                    b.inputs["Metallic"].default_value = 0.55
                except Exception:
                    pass

    # ---- tram rails are site; catenary comes from vehicles ----

    # ---- look ----
    look.make_sky(scn, strength=0.85, colors={
        "zenith": "#08101E", "upper": "#102138", "mid": "#1B3A66",
        "horizon": "#36527A", "band": "#24354D"})
    look.make_world(scn, ambient_strength=0.72, volume_density=0.0)
    look.make_ground_fog(scn, (-520, -440, 520, 440), height=16.0, density=0.0048)
    # keep the giant render-only helpers (sky dome / fog volume) out of the viewport:
    # in Solid/Wireframe they would read as huge grey slabs covering the buildings.
    # hide_viewport disables viewport display only; F12 renders still include them.
    for _nm in ("SKY_DOME", "GROUND_FOG"):
        _o = bpy.data.objects.get(_nm)
        if _o:
            _o.hide_viewport = True
    look.add_sun(scn, strength=1.05, angle_deg=16)
    look.add_sky_fill(scn, intensity=800.0)
    build_lamps(lamps)
    add_entrance_lights(blocks, coll_root)
    add_plaza_fill()
    add_monument_floodlights()
    add_landmark_lights()

    look.setup_eevee(scn, samples=48, volumetric_samples=96, tile="4")
    look.setup_color(scn, exposure=1.35, view_transform="Khronos PBR Neutral")
    look.setup_compositor(scn, bloom_strength=0.3, bloom_threshold=1.0,
                          bloom_size=0.7, saturation=1.0, contrast=0.0)

    # ---- cameras ----
    look.add_camera(scn, "CAM_HERO", (205, -195, 85), (0, 10, 12), 38)
    look.add_camera(scn, "CAM_HERO2", (-212, -214, 90), (-10, -14, 10), 30)
    look.add_camera(scn, "CAM_TOP", (0, 0, 300), (0, 0.1, 0), 50)
    top = bpy.data.objects["CAM_TOP"]
    top.data.type = "ORTHO"
    top.data.ortho_scale = 430
    look.add_camera(scn, "CAM_PLAZA", (-8.5, -64.5, 2.1), (3, -33, 9.5), 25)
    look.add_camera(scn, "CAM_DK", (14, -46, 2.6), (0, -30, 8.5), 28)
    look.add_camera(scn, "CAM_AVE", (-52, -71.5, 1.75), (30, -80, 3.5), 28)
    look.add_camera(scn, "CAM_COURT_W", (-57.6, -37.5, 1.75), (-96, -47, 4.0), 20)
    look.add_camera(scn, "CAM_COURT_E", (60.5, -39, 1.75), (96, -46, 4.0), 20)
    look.add_camera(scn, "CAM_ARCH", (-81, -8.0, 1.85), (-81, -36, 3.0), 24)
    look.add_camera(scn, "CAM_SCHOOL", (36, -26, 16), (26, 16, 6.5), 32)
    look.add_camera(scn, "CAM_BOILER", (-70, 8.0, 2.4), (-101, 31, 18.0), 26)
    look.add_camera(scn, "CAM_STADIUM", (56, 74, 12.0), (88, 42, 4.0), 26)
    look.add_camera(scn, "CAM_GARAGES", (-62, 0.0, 9.0), (-62, 22, 2.0), 26)
    look.add_camera(scn, "CAM_TOWER", (-70, -70.0, 1.9), (-43, -61, 13.0), 22)
    look.add_camera(scn, "CAM_W2_COURT", (-121.5, -44.0, 1.8), (-147, -46, 4.5), 22)
    look.add_camera(scn, "CAM_NORTH_PARK", (-78, 74, 16), (2, 104, 1.5), 30)
    look.add_camera(scn, "CAM_SOUTH_ST", (62, -76, 2.8), (34, -98, 7.0), 26)
    look.add_camera(scn, "CAM_MARKET", (118.5, -53, 1.9), (143, -53, 6.0), 22)
    look.add_camera(scn, "CAM_BANYA", (-122.5, 21.0, 2.2), (-150, 33, 9.5), 24)
    look.add_camera(scn, "CAM_ESPLANADE", (34, 112, 2.8), (-2, 134, 11.0), 22)
    look.add_camera(scn, "CAM_MOTHERLAND", (34, 110, 4.2), (-6, 140, 15.0), 26)
    look.add_camera(scn, "CAM_EAST_Q", (116, 10, 3.0), (146, 44, 12.0), 26)
    look.add_camera(scn, "CAM_VYSOTKA", (96, 128, 34), (188, 12, 26.0), 34)
    look.add_camera(scn, "CAM_TERMINAL", (-62, -142, 3.0), (-62, -124, 6.0), 20)
    look.add_camera(scn, "CAM_METRO", (118, 44, 2.6), (150, 52, 6.0), 22)
    look.add_camera(scn, "CAM_DRUZHBA", (112, -78, 3.0), (150, -100, 7.0), 26)
    look.add_camera(scn, "CAM_LM_MOTHERLAND", (30, 108, 3.5), (0, 140, 17.0), 30)
    look.add_camera(scn, "CAM_LM_MSU", (116, 62, 20.0), (188, 46, 38.0), 28)
    look.add_camera(scn, "CAM_LM_UKRAINA", (148, -2, 9.0), (186, 13, 30.0), 32)
    look.add_camera(scn, "CAM_LM_MFA", (148, -42, 9.0), (187, -19, 26.0), 32)
    look.add_camera(scn, "CAM_LM_METRO", (120, 52, 2.2), (150, 50, 5.5), 24)
    look.add_camera(scn, "CAM_LM_BREST", (-18, 112, 3.0), (-32, 126, 3.5), 32)
    look.add_camera(scn, "CAM_LM_UFO", (-38, -142, 4.0), (-64, -123, 7.0), 22)
    look.add_camera(scn, "CAM_LM_HEX", (26, -148, 4.5), (46, -123, 7.5), 22)
    look.add_camera(scn, "CAM_LM_ROBOTICS", (176, 124, 26.0), (146, 102, 8.0), 30)
    look.add_camera(scn, "CAM_LM_MOT", (156, -64, 9.0), (190, -46, 17.0), 26)
    look.add_camera(scn, "CAM_LM_HIT", (108, 10, 6.0), (146, 32, 14.0), 30)
    look.add_camera(scn, "CAM_LM_MUSEUM", (88, 88, 16.0), (146, 74, 12.0), 28)
    look.add_camera(scn, "CAM_LM_DRUZHBA", (130, -122, 3.2), (146, -100, 7.5), 26)
    scn.camera = bpy.data.objects["CAM_HERO"]

    # ---- stats + manifest ----
    objects = [o for o in bpy.data.objects if o.type in ("MESH", "FONT")]
    tris = 0
    for o in objects:
        if o.type == "MESH":
            tris += sum(max(0, len(p.vertices) - 2) for p in o.data.polygons)
    stats = {
        "objects": len(bpy.data.objects),
        "meshes": len([o for o in bpy.data.objects if o.type == "MESH"]),
        "tris": tris,
        "blocks": len(blocks),
        "block_types": total_types,
        "props": len([o for o in bpy.data.objects if o.parent and o.parent.name == "PROPS_ROOT"]),
        "build_seconds": round(time.time() - t0, 1),
        "blender": bpy.app.version_string,
    }
    json.dump(manifest, open(os.path.join(MAP_DIR, "scene_manifest.json"), "w",
                             encoding="utf-8"), ensure_ascii=False, indent=2)
    json.dump(stats, open(os.path.join(MAP_DIR, "build_stats.json"), "w",
                          encoding="utf-8"), ensure_ascii=False, indent=2)
    print("STATS", json.dumps(stats, ensure_ascii=False))

    out_blend = os.path.join(MAP_DIR, "SovietBlueHour_beauty.blend")
    bpy.ops.wm.save_as_mainfile(filepath=out_blend)
    print("SAVED", out_blend)


if __name__ == "__main__":
    main()
