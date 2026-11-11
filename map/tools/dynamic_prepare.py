"""Blender-side preparation of dynamic entities for the game export.

Called by export_game.py:
    strip_render_only()
    dynamic_prepare.capture_world()      # before helper empties are deleted
    join_block_meshes()
    dynamic_prepare.build()              # split flame, wrap vehicles/tram/flame
    merge_site_props_vehicles()          # skips dynamic meshes

Result: the game GLB keeps each dynamic entity under a DYN_* empty at the world origin
while the static merge groups (SITE/PROPS/VEHICLES) no longer contain those meshes.
"""
from __future__ import annotations

import json
import os

import bpy

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.dirname(
    os.path.abspath(__file__)))))


def _sha(path):
    import hashlib
    return hashlib.sha256(open(path, "rb").read()).hexdigest()


class DynamicPrepare:
    def __init__(self, root=None):
        self.root = root or ROOT
        self.sources_path = os.path.join(self.root, "Maps", "SovietBlueHour", "entities",
                                         "dynamic_sources.v1.json")
        self.sources = None
        self.world_before = {}
        self.dynamic_names = set()
        self.wrappers = {}
        self.pivots = {}
        self.split_objects = {}
        self._log = []

    def log(self, *parts):
        line = " ".join(str(p) for p in parts)
        self._log.append(line)
        print("[dynamic]", line, flush=True)

    def load(self):
        if self.sources is not None:
            return
        with open(self.sources_path, encoding="utf-8") as fh:
            self.sources = json.load(fh)

    # ------------------------------------------------------------------ step 1
    def capture_world(self):
        self.load()
        wanted = set(self.sources["exclude_from_static_merge"])
        for obj in bpy.data.objects:
            if obj.name in wanted:
                self.world_before[obj.name] = obj.matrix_world.copy()
        self.log("captured world matrices for", len(self.world_before), "dynamic sources")

    # ------------------------------------------------------------------ step 2
    def _split_flame(self):
        spec = self.sources["flame_split"]
        src_name = spec["source_object"]
        base_name = spec["base_name"]
        fire_name = spec["fire_name"]
        orig = bpy.data.objects.get(src_name)
        if orig is None or orig.type != "MESH":
            self.log("WARN flame source missing", src_name)
            return
        fire_mats = {m.lower() for m in spec["fire_materials"]}
        collections = list(orig.users_collection)
        world = self.world_before.get(src_name, orig.matrix_world.copy())

        def clone(name):
            obj = orig.copy()
            obj.data = orig.data.copy()
            obj.name = name
            obj.data.name = name
            for coll in collections:
                coll.objects.link(obj)
            return obj

        base = clone(base_name)
        fire = clone(fire_name)

        def keep_faces(obj, keep_fire):
            import bmesh
            me = obj.data
            bm = bmesh.new()
            bm.from_mesh(me)
            mat_names = [m.name.lower() if m else "" for m in me.materials]
            doomed = []
            for face in bm.faces:
                index = face.material_index
                mat_name = mat_names[index] if 0 <= index < len(mat_names) else ""
                is_fire = any(fm in mat_name for fm in fire_mats)
                if is_fire != keep_fire:
                    doomed.append(face)
            if doomed:
                bmesh.ops.delete(bm, geom=doomed, context="FACES")
            bm.to_mesh(me)
            bm.free()
            me.update()
            return len(doomed)

        removed_base = keep_faces(base, False)
        removed_fire = keep_faces(fire, True)
        for obj in (base, fire):
            obj.parent = None
            obj.matrix_world = world
        bpy.data.objects.remove(orig, do_unlink=True)
        self.dynamic_names.add(base_name)
        self.dynamic_names.add(fire_name)
        self.split_objects = {"base": base_name, "fire": fire_name}
        self.log("flame split", src_name, "->", base_name, fire_name,
                 "removed faces base/fire", removed_base, removed_fire,
                 "fire materials", sorted(fire_mats))

    def _pivot_blender(self, wrapper_def):
        from mathutils import Vector
        p = wrapper_def.get("pivot_unity") or [0.0, 0.0, 0.0]
        # unity (x, y, z) -> blender (x, z, y)
        return Vector((float(p[0]), float(p[2]), float(p[1])))

    def _make_wrapper(self, wrapper_def):
        from mathutils import Matrix
        node = wrapper_def["node"]
        empty = bpy.data.objects.get(node)
        if empty is None:
            empty = bpy.data.objects.new(node, None)
            bpy.context.scene.collection.objects.link(empty)
        pivot = self._pivot_blender(wrapper_def)
        empty.matrix_world = Matrix.Translation(pivot)
        self.wrappers[node] = empty
        self.pivots[node] = pivot
        return empty

    def _attach(self, obj, wrapper):
        from mathutils import Matrix
        world = self.world_before.get(obj.name)
        if world is None:
            world = obj.matrix_world.copy()
        obj.parent = None
        obj.matrix_world = world
        obj.parent = wrapper
        obj.matrix_parent_inverse = Matrix.Translation(-self.pivots[wrapper.name])
        obj.matrix_world = world

    def build(self):
        self.load()
        self._split_flame()
        for wrapper_def in self.sources["wrappers"]:
            node = wrapper_def["node"]
            self._make_wrapper(wrapper_def)
            for src in wrapper_def.get("sources", []):
                obj = bpy.data.objects.get(src)
                if obj is None:
                    self.log("WARN dynamic source missing", src)
                    continue
                self._attach(obj, self.wrappers[node])
                self.dynamic_names.add(obj.name)
        # keep the tram's extra nodes attached to the tram wrapper
        tram = next(w for w in self.sources["wrappers"] if "tram" in w["node"])
        tram_wrapper = self.wrappers[tram["node"]]
        for extra_node in (tram.get("extra_nodes") or {}).values():
            obj = bpy.data.objects.get(extra_node)
            if obj is None:
                continue
            self._attach(obj, tram_wrapper)
            self.dynamic_names.add(obj.name)
        self.log("dynamic meshes", len(self.dynamic_names), "wrappers", len(self.wrappers))

    def should_merge_skip(self, obj):
        if obj.name in self.dynamic_names:
            return True
        if obj.parent is not None and obj.parent.name.startswith("DYN_"):
            return True
        return False


INSTANCE = DynamicPrepare()


def configure(root):
    INSTANCE.root = root
    INSTANCE.sources_path = os.path.join(root, "Maps", "SovietBlueHour", "entities",
                                         "dynamic_sources.v1.json")
    INSTANCE.sources = None


def capture_world():
    INSTANCE.capture_world()


def build():
    INSTANCE.build()
    return INSTANCE


def skip(obj):
    return INSTANCE.should_merge_skip(obj)


def summary():
    return {
        "dynamic_meshes": sorted(INSTANCE.dynamic_names),
        "wrappers": sorted(INSTANCE.wrappers.keys()),
        "flame_split": INSTANCE.split_objects,
        "log": INSTANCE._log,
    }
