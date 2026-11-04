#!/usr/bin/env python3
"""export_game.py -- game-ready export of the Soviet Blue-Hour beauty scene.

Run headless (the beauty blend is opened by Blender, never edited in place)::

    blender -b map/SovietBlueHour_beauty.blend --python tools/export_game.py

What it does
------------
1. Deletes render-only content: ``RENDER_ONLY`` (SKY_DOME, GROUND_FOG), ``LIGHTS``,
   ``CAMERAS`` collections + any leftover camera/light object.
2. Joins every mesh under each ``BLD_<id>`` empty into ONE mesh object ``BLD_<id>``
   (materials preserved, object transforms baked). FONT/CURVE objects are converted
   to mesh first. The helper empties are then deleted.
3. Merges ``SITE`` (474 objs) into one object, each ``PROP_*`` group into one object
   and ``VEHICLES`` (41 objs) into one, so the runtime scene is ~40 draw-call roots
   instead of 18k objects -- still organised in the four parentless collections
   SITE / BUILDINGS / PROPS / VEHICLES.
4. Saves ``map/SovietBlueHour_game.blend`` (the beauty blend stays untouched).
5. Exports ``map/SovietBlueHour_game.glb``  (glTF 2.0 binary, Y-up, metre scale, JPEG
   textures, no lights/cameras/animations) and
   ``map/SovietBlueHour_game.fbx`` (Unity-friendly: -Z forward / Y up, scale 1.0,
   FBX_SCALE_ALL, mesh only, embedded textures).
6. Parses the GLB back (JSON chunk + accessors) and prints object / mesh / material /
   triangle counts and file sizes, and writes ``map/game_export_report.json``.

Axis / scale truth (verified by re-import in tools/_test_export.py --stage validate):
* glTF/GLB: Blender +X -> glTF +X, Blender +Z -> glTF +Y, Blender +Y -> glTF -Z.
  That is the three.js expectation: ground plane XZ, up +Y, north = -Z.
* FBX with axis_forward='-Z', axis_up='Y', global_scale=1.0, FBX_SCALE_ALL:
  the file is Y-up and 1 unit = 1 metre; Unity imports it at Scale Factor 1 with
  Rotation 0 (no 0.01 scale needed). Blender re-import round-trips to the same
  world coordinates (checked numerically).
"""
from __future__ import annotations

import json
import math
import os
import struct
import sys
import time

import bpy

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
MAP = os.path.join(ROOT, "map")
GAME_BLEND = os.path.join(MAP, "SovietBlueHour_game.blend")
GAME_GLB = os.path.join(MAP, "SovietBlueHour_game.glb")
GAME_FBX = os.path.join(MAP, "SovietBlueHour_game.fbx")
REPORT = os.path.join(MAP, "game_export_report.json")

# Round 4: the post-material rebuild pushes the GLB over the 120 MB delivery budget.
# Keep the FBX full-res, but cap the GLB: first try a lower JPEG quality, then downscale
# every embedded image whose long side exceeds DOWNSCALE_MAX_PX.
MAX_GLB_MB = 130.0
GLB_JPEG_QUALITY = 90
GLB_FALLBACK_QUALITY = 82
DOWNSCALE_MAX_PX = 1024
DOWNSCALE_TO_PX = 512

T0 = time.time()
LOG = []


def log(*parts):
    msg = " ".join(str(p) for p in parts)
    line = "[%6.1fs] %s" % (time.time() - T0, msg)
    print(line, flush=True)
    LOG.append(line)


def tri_count():
    total = 0
    verts = 0
    for obj in bpy.data.objects:
        if obj.type != "MESH":
            continue
        me = obj.data
        verts += len(me.vertices)
        me.calc_loop_triangles()
        total += len(me.loop_triangles)
    return total, verts


# ----------------------------------------------------------------------------
# 1. strip render-only content
# ----------------------------------------------------------------------------
def strip_render_only():
    removed = []
    for name in ("RENDER_ONLY", "LIGHTS", "CAMERAS"):
        coll = bpy.data.collections.get(name)
        if coll is None:
            continue
        for obj in list(coll.objects):
            removed.append(obj.name)
            bpy.data.objects.remove(obj, do_unlink=True)
        bpy.data.collections.remove(coll)
    for obj in [o for o in bpy.data.objects if o.type in ("CAMERA", "LIGHT")]:
        removed.append(obj.name)
        bpy.data.objects.remove(obj, do_unlink=True)
    log("stripped render-only objects:", len(removed), removed[:4], "...")


# ----------------------------------------------------------------------------
# 2. join helpers
# ----------------------------------------------------------------------------
def convert_to_mesh(obj):
    """Convert FONT/CURVE/SURFACE/META to a mesh object (headless safe)."""
    if obj.type == "MESH":
        return obj
    dg = bpy.context.evaluated_depsgraph_get()
    ev = obj.evaluated_get(dg)
    me = bpy.data.meshes.new_from_object(ev)
    new = bpy.data.objects.new(obj.name + "_MESH", me)
    new.matrix_world = obj.matrix_world.copy()
    for slot in obj.material_slots:
        new.data.materials.append(slot.material)
    for coll in obj.users_collection:
        coll.objects.link(new)
    bpy.data.objects.remove(obj, do_unlink=True)
    return new


def join_group(objs, name):
    """Join meshes into one object, bake transforms, rename. Returns object or None."""
    objs = [convert_to_mesh(o) for o in objs if o.type != "EMPTY"]
    meshes = [o for o in objs if o.type == "MESH"]
    if not meshes:
        return None
    bpy.ops.object.select_all(action="DESELECT")
    active = max(meshes, key=lambda o: len(o.data.vertices))
    for o in meshes:
        o.select_set(True)
    bpy.context.view_layer.objects.active = active
    if len(meshes) > 1:
        bpy.ops.object.join()
    obj = bpy.context.view_layer.objects.active
    # bake object transform (all blocks were built in world coords under an empty)
    try:
        if obj.data.users > 1:
            obj.data = obj.data.copy()
        bpy.ops.object.transform_apply(location=True, rotation=True, scale=True)
    except Exception as exc:  # noqa: BLE001
        log("WARN transform_apply failed on", obj.name, exc)
    obj.name = name
    obj.data.name = name + "_MESH"
    # drop parent but keep world transform
    if obj.parent is not None:
        mw = obj.matrix_world.copy()
        obj.parent = None
        obj.matrix_world = mw
    return obj


def find_collection(name):
    return bpy.data.collections.get(name)


def join_block_meshes():
    """Join all children of every BLD_<id> empty into one object per block."""
    bld_roots = [o for o in bpy.data.objects if o.type == "EMPTY" and o.name.startswith("BLD_")]
    bld_roots.sort(key=lambda o: o.name)
    out = {}
    for empty in bld_roots:
        children = [o for o in empty.children_recursive if o.type != "EMPTY"]
        obj = join_group(children, empty.name)
        if obj is None:
            log("WARN block has no mesh:", empty.name)
            continue
        out[empty.name] = {"objects": len(children),
                           "verts": len(obj.data.vertices),
                           "mats": len(obj.material_slots)}
        log("joined block", empty.name, "from", len(children), "objects ->",
            len(obj.data.vertices), "verts,", len(obj.material_slots), "mat slots")
    # delete helper empties (parents first)
    empties = [o for o in bpy.data.objects if o.type == "EMPTY"]
    for e in empties:
        bpy.data.objects.remove(e, do_unlink=True)
    log("deleted", len(empties), "helper empties")
    return out


def merge_site_props_vehicles():
    """After the block join + empty sweep, merge SITE / PROPS / VEHICLES into one mesh each."""
    out = {}
    for coll_name, obj_name in (("SITE", "SITE"), ("PROPS", "PROPS"), ("VEHICLES", "VEHICLES")):
        objs = [o for o in bpy.data.objects
                if o.type != "EMPTY" and any(c.name == coll_name for c in o.users_collection)]
        if not objs:
            log("WARN nothing to merge in collection", coll_name)
            continue
        merged = join_group(objs, obj_name)
        if merged:
            out[obj_name] = {"objects": len(objs), "verts": len(merged.data.vertices),
                             "mats": len(merged.material_slots)}
            log("merged", coll_name, len(objs), "objects ->", len(merged.data.vertices),
                "verts,", len(merged.material_slots), "material slots")
    return out


def ensure_collection(obj, name):
    coll = find_collection(name)
    if coll is None:
        coll = bpy.data.collections.new(name)
        bpy.context.scene.collection.children.link(coll)
    for c in list(obj.users_collection):
        c.objects.unlink(obj)
    coll.objects.link(obj)


def tidy_collections():
    """Make sure BUILDINGS/SITE/PROPS/VEHICLES are top-level parentless collections."""
    scene_coll = bpy.context.scene.collection
    for name in ("SOVIET_BLUEHOUR",):
        old = bpy.data.collections.get(name)
        if old is not None:
            # move members up to the scene and drop the wrapper
            for child in list(old.children):
                scene_coll.children.link(child)
                old.children.unlink(child)
            for obj in list(old.objects):
                scene_coll.objects.link(obj)
                old.objects.unlink(obj)
            bpy.data.collections.remove(old)
    # drop empty collections
    for coll in list(bpy.data.collections):
        if len(coll.objects) == 0 and len(coll.children) == 0:
            bpy.data.collections.remove(coll)


# ----------------------------------------------------------------------------
# 3. save + export
# ----------------------------------------------------------------------------
def save_game_blend():
    bpy.ops.wm.save_as_mainfile(filepath=GAME_BLEND, copy=False, check_existing=False)
    log("saved", GAME_BLEND, "%.1f MB" % (os.path.getsize(GAME_BLEND) / 1e6))


def export_glb(quality=90):
    kwargs = dict(
        filepath=GAME_GLB,
        export_format="GLB",
        use_selection=False,
        use_visible=True,
        export_apply=True,
        export_yup=True,
        export_texcoords=True,
        export_normals=True,
        export_materials="EXPORT",
        export_cameras=False,
        export_lights=False,
        export_animations=False,
        export_extras=False,
        export_tangents=False,
        export_skins=False,
        export_morph=False,
    )
    # pick a compressed-but-compatible image format if offered
    props = bpy.ops.export_scene.gltf.get_rna_type().properties
    enum_items = []
    if "export_image_format" in props:
        enum_items = [i.identifier for i in props["export_image_format"].enum_items]
    if "JPEG" in enum_items:
        kwargs["export_image_format"] = "JPEG"
        kwargs["export_jpeg_quality"] = int(quality)
    if "export_image_quality" in props:
        kwargs["export_image_quality"] = int(quality)
    log("gltf image format option:", "JPEG" if kwargs.get("export_image_format") == "JPEG" else "AUTO",
        "(quality %d)" % int(quality))
    try:
        bpy.ops.export_scene.gltf(**kwargs)
    except TypeError as exc:
        log("WARN gltf kwargs rejected, retrying minimal:", exc)
        bpy.ops.export_scene.gltf(filepath=GAME_GLB, export_format="GLB",
                                  export_apply=True, export_yup=True,
                                  export_materials="EXPORT", export_cameras=False,
                                  export_lights=False, export_animations=False)
    log("exported", GAME_GLB, "%.1f MB" % (os.path.getsize(GAME_GLB) / 1e6))


def export_fbx():
    bpy.ops.export_scene.fbx(
        filepath=GAME_FBX,
        use_selection=False,
        use_visible=True,
        object_types={"MESH"},
        axis_forward="-Z",
        axis_up="Y",
        global_scale=1.0,
        apply_scale_options="FBX_SCALE_ALL",
        apply_unit_scale=True,
        bake_space_transform=False,
        use_mesh_modifiers=True,
        mesh_smooth_type="OFF",
        use_triangles=True,
        use_tspace=False,
        use_custom_props=False,
        path_mode="COPY",
        embed_textures=True,
    )
    log("exported", GAME_FBX, "%.1f MB" % (os.path.getsize(GAME_FBX) / 1e6))


def downscale_images(max_px=DOWNSCALE_MAX_PX, to_px=DOWNSCALE_TO_PX):
    """Round 4 GLB size guard: shrink every embedded image whose long side > max_px.

    FBX has already been written full-res at this point; this only affects the GLB.
    """
    count = 0
    for img in list(bpy.data.images):
        if img.type != "IMAGE" or img.size[0] == 0 or img.size[1] == 0:
            continue
        w, h = img.size
        long_side = max(w, h)
        if long_side <= max_px:
            continue
        scale = float(to_px) / float(long_side)
        nw = max(1, int(round(w * scale)))
        nh = max(1, int(round(h * scale)))
        try:
            img.scale(nw, nh)
            img.pack()
            count += 1
        except Exception as exc:      # pragma: no cover - defensive
            log("WARN could not downscale", img.name, repr(exc))
    log("downscaled %d images > %dpx to %dpx long side" % (count, max_px, to_px))
    return count


def export_glb_with_size_guard():
    """Export GLB and keep it <= MAX_GLB_MB (JPEG quality first, then image downscale)."""
    guard = {"max_mb": MAX_GLB_MB, "initial_mb": None, "final_mb": None,
             "action": "none", "jpeg_quality": GLB_JPEG_QUALITY,
             "images_downscaled": 0}
    export_glb(GLB_JPEG_QUALITY)
    size_mb = os.path.getsize(GAME_GLB) / 1e6
    guard["initial_mb"] = round(size_mb, 2)
    log("GLB size %.2f MB (budget %.0f MB)" % (size_mb, MAX_GLB_MB))
    if size_mb > MAX_GLB_MB:
        log("GLB over budget -> retry with JPEG q%d" % GLB_FALLBACK_QUALITY)
        export_glb(GLB_FALLBACK_QUALITY)
        size_mb = os.path.getsize(GAME_GLB) / 1e6
        guard["action"] = "jpeg-q%d" % GLB_FALLBACK_QUALITY
        guard["jpeg_quality"] = GLB_FALLBACK_QUALITY
    if size_mb > MAX_GLB_MB:
        guard["images_downscaled"] = downscale_images()
        log("GLB still over budget -> re-export with downscaled images, q88")
        export_glb(88)
        size_mb = os.path.getsize(GAME_GLB) / 1e6
        guard["action"] = "downscale>%dpx-to-%dpx+jpeg-q88" % (DOWNSCALE_MAX_PX, DOWNSCALE_TO_PX)
        guard["jpeg_quality"] = 88
    guard["final_mb"] = round(size_mb, 2)
    log("GLB final %.2f MB (guard action: %s)" % (size_mb, guard["action"]))
    return guard


# ----------------------------------------------------------------------------
# 4. GLB introspection
# ----------------------------------------------------------------------------
def describe_glb(path):
    with open(path, "rb") as fh:
        data = fh.read()
    magic, version, length = struct.unpack("<4sII", data[:12])
    assert magic == b"glTF", "not a GLB file"
    off = 12
    chunks = []
    while off < len(data):
        clen, ctype = struct.unpack("<I4s", data[off:off + 8])
        off += 8
        chunks.append((ctype, off, clen))
        off += clen
    js = json.loads(data[chunks[0][1]:chunks[0][1] + chunks[0][2]].decode("utf-8"))
    tris = 0
    prims = 0
    empty_meshes = []
    for mi, mesh in enumerate(js.get("meshes", [])):
        for prim in mesh.get("primitives", []):
            prims += 1
            acc = prim.get("indices")
            if acc is not None:
                tris += js["accessors"][acc]["count"] // 3
            else:
                empty_meshes.append(mesh.get("name"))
    verts = 0
    for acc in js.get("accessors", []):
        if acc.get("type") == "VEC3" and acc.get("componentType") == 5126:
            verts += acc.get("count", 0)
    nodes = js.get("nodes", [])
    node_names = [n.get("name", "") for n in nodes]
    bld_nodes = [n for n in node_names if n.startswith("BLD_")]
    images = js.get("images", [])
    textures = js.get("textures", [])
    materials = js.get("materials", [])
    return {
        "file_bytes": len(data),
        "file_mb": round(len(data) / 1e6, 2),
        "glb_version": version,
        "declared_length": length,
        "chunks": [(c[0].decode("ascii", "replace"), c[2]) for c in chunks],
        "nodes": len(nodes),
        "meshes": len(js.get("meshes", [])),
        "primitives": prims,
        "materials": len(materials),
        "images": len(images),
        "textures": len(textures),
        "accessors": len(js.get("accessors", [])),
        "triangles": tris,
        "position_verts": verts,
        "bld_nodes": len(bld_nodes),
        "node_names_sample": node_names[:30],
        "has_animations": bool(js.get("animations")),
        "has_cameras": bool(js.get("cameras")),
        "extensions_used": js.get("extensionsUsed", []),
        "empty_prim_meshes": empty_meshes[:5],
        "json_chunk_bytes": chunks[0][2],
        "bin_chunk_bytes": chunks[1][2] if len(chunks) > 1 else 0,
    }


# ----------------------------------------------------------------------------
# main
# ----------------------------------------------------------------------------
def main():
    if bpy.data.filepath == "" or "beauty" not in bpy.data.filepath.lower():
        # allow explicit invocation with --blend <path>
        argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
        raise SystemExit("Run as: blender -b map/SovietBlueHour_beauty.blend --python tools/export_game.py")
    log("source blend:", bpy.data.filepath)
    source_blend = bpy.data.filepath
    log("scene objects:", len(bpy.data.objects), "collections:", [c.name for c in bpy.data.collections])
    os.makedirs(MAP, exist_ok=True)

    tris_before, verts_before = tri_count()
    log("beauty tris=%d verts=%d" % (tris_before, verts_before))

    strip_render_only()
    blocks = join_block_meshes()
    groups = merge_site_props_vehicles()
    tidy_collections()
    bpy.ops.object.select_all(action="DESELECT")
    bpy.context.view_layer.objects.active = None

    bpy.data.orphans_purge(do_local_ids=True, do_linked_ids=True, do_recursive=True)
    tris_after, verts_after = tri_count()
    objects_after = len([o for o in bpy.data.objects if o.type == "MESH"])
    log("game scene: objects=%d tris=%d verts=%d materials=%d"
        % (objects_after, tris_after, verts_after, len(bpy.data.materials)))

    save_game_blend()
    # Round 4: FBX is written full-res first (kept as-is); the GLB then runs the size guard.
    export_fbx()
    glb_guard = export_glb_with_size_guard()

    glb = describe_glb(GAME_GLB)
    log("GLB nodes=%d meshes=%d prims=%d materials=%d images=%d tris=%d"
        % (glb["nodes"], glb["meshes"], glb["primitives"], glb["materials"],
           glb["images"], glb["triangles"]))
    log("GLB nodes starting BLD_:", glb["bld_nodes"], "| animations:", glb["has_animations"],
        "| cameras:", glb["has_cameras"])
    log("GLB empty prims:", glb["empty_prim_meshes"])
    log("extensions:", glb["extensions_used"])

    report = {
        "schema": "sbh.game_export.v1",
        "source_blend": source_blend,
        "game_blend": GAME_BLEND,
        "glb": GAME_GLB,
        "fbx": GAME_FBX,
        "sizes_mb": {
            "game_blend": round(os.path.getsize(GAME_BLEND) / 1e6, 2),
            "glb": round(os.path.getsize(GAME_GLB) / 1e6, 2),
            "fbx": round(os.path.getsize(GAME_FBX) / 1e6, 2),
        },
        "beauty": {"tris": tris_before, "verts": verts_before},
        "game": {"mesh_objects": objects_after, "tris": tris_after, "verts": verts_after,
                 "materials": len(bpy.data.materials)},
        "blocks": blocks,
        "merged_groups": groups,
        "glb": glb,
        "glb_size_guard": glb_guard,
        "fbx_settings": {
            "axis_forward": "-Z", "axis_up": "Y", "global_scale": 1.0,
            "apply_scale_options": "FBX_SCALE_ALL", "object_types": ["MESH"],
            "embed_textures": True, "bake_space_transform": False,
        },
        "collections": [c.name for c in bpy.data.collections],
        "log": LOG,
    }
    with open(REPORT, "w", encoding="utf-8") as fh:
        json.dump(report, fh, indent=1)
    log("wrote", REPORT)
    print("GAME_EXPORT_OK", json.dumps({
        "glb_mb": report["sizes_mb"]["glb"], "fbx_mb": report["sizes_mb"]["fbx"],
        "game_mb": report["sizes_mb"]["game_blend"],
        "mesh_objects": objects_after, "tris": tris_after,
        "glb_tris": glb["triangles"], "materials": glb["materials"],
        "nodes": glb["nodes"], "images": glb["images"],
    }))


main()
