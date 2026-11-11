"""Export simple interaction geometry for the Stage-3 bench samples.

Read-only against the source .blend: this script never saves the Blender file.
Only the two normal plaza benches are in scope. The emitted sidecar contains
seat surface centre, seated facing, and a ground approach point; it does not
contain character/hips targets or executable interaction data.
"""
import argparse
import hashlib
import json
import math
import sys
from pathlib import Path

import bpy
from mathutils import Vector

TARGETS = ('entity/prop_bench_plaza_1', 'entity/prop_bench_plaza_2')
AGENT_RADIUS_M = 0.35
SAFETY_MARGIN_M = 0.15


def sha256(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


def world_vertices(obj):
    depsgraph = bpy.context.evaluated_depsgraph_get()
    evaluated = obj.evaluated_get(depsgraph)
    mesh = evaluated.to_mesh()
    matrix = evaluated.matrix_world
    vertices = [matrix @ vertex.co for vertex in mesh.vertices]
    evaluated.to_mesh_clear()
    return vertices


def to_unity(vertex):
    # project convention used by export_entities.py: Blender (x, y, z) -> Unity (x, z, y)
    return Vector((vertex.x, vertex.z, vertex.y))


def ground_height(data, x, z):
    cell = float(data['cell'])
    ix = int(round((float(x) - float(data['min_x'])) / cell))
    iz = int(round((float(z) - float(data['min_z'])) / cell))
    if ix < 0 or iz < 0 or ix >= int(data['w']) or iz >= int(data['h']):
        raise ValueError('ground_height sample outside grid: %.4f, %.4f' % (x, z))
    return round(float(data['heights'][iz * int(data['w']) + ix]), 6), ix, iz


def one_entry(entity, ground_data, blend_sha):
    source_objects = list(entity.get('source_objects') or [])
    seat_names = sorted(name for name in source_objects if '_seat' in name)
    if len(seat_names) != 3:
        raise ValueError('%s expected 3 seat objects, got %r' % (entity['id'], seat_names))
    seat_vertices = []
    for name in seat_names:
        obj = bpy.data.objects.get(name)
        if obj is None:
            raise ValueError('missing Blender object: ' + name)
        seat_vertices.extend(world_vertices(obj))
    if not seat_vertices:
        raise ValueError('empty seat mesh: ' + entity['id'])
    top_z = max(vertex.z for vertex in seat_vertices)
    top_vertices = [vertex for vertex in seat_vertices if abs(vertex.z - top_z) < 1e-5]
    if len(top_vertices) < 3:
        raise ValueError('seat top face not found: ' + entity['id'])
    seat_x = sum(vertex.x for vertex in top_vertices) / len(top_vertices)
    seat_y = sum(vertex.y for vertex in top_vertices) / len(top_vertices)
    seat = to_unity(Vector((seat_x, seat_y, top_z)))
    top_unity = [to_unity(vertex) for vertex in top_vertices]
    seat_top_x = [round(min(vertex.x for vertex in top_unity), 6), round(max(vertex.x for vertex in top_unity), 6)]
    seat_top_z = [round(min(vertex.z for vertex in top_unity), 6), round(max(vertex.z for vertex in top_unity), 6)]

    yaw = entity.get('orientation', {}).get('yaw_unity_deg')
    if yaw is None:
        raise ValueError('%s has no verified yaw' % entity['id'])
    radians = math.radians(float(yaw))
    front = Vector((math.sin(radians), 0.0, math.cos(radians)))
    right = Vector((front.z, 0.0, -front.x))
    if abs(front.length - 1.0) > 1e-6 or abs(right.length - 1.0) > 1e-6:
        raise ValueError('invalid frame for ' + entity['id'])

    all_unity = []
    for name in source_objects:
        obj = bpy.data.objects.get(name)
        if obj is None:
            raise ValueError('missing Blender object: ' + name)
        all_unity.extend(to_unity(vertex) for vertex in world_vertices(obj))
    clearance = round(AGENT_RADIUS_M + SAFETY_MARGIN_M, 6)
    front_coords = [float(vertex.dot(front)) for vertex in all_unity]
    seat_right = float(seat.dot(right))
    approach_front = max(front_coords) + clearance
    approach_xz = right * seat_right + front * approach_front
    ground_y, ground_ix, ground_iz = ground_height(ground_data, approach_xz.x, approach_xz.z)
    seat_ground, seat_ix, seat_iz = ground_height(ground_data, seat.x, seat.z)
    if abs(seat_ground - ground_y) > 1e-6:
        raise ValueError('seat/approach ground heights differ unexpectedly for ' + entity['id'])

    return {
        'entity_id': entity['id'],
        'entity_class': entity.get('class'),
        'map_version': '2026-11-02',
        'blend_sha256': blend_sha,
        'geometry_status': 'verified',
        'usable_for_interaction': True,
        'capability_note': 'seat parameters only; no executable interaction is registered',
        'implemented_actions': [],
        'source': {
            'source_group': entity.get('source_group'),
            'source_objects': source_objects,
            'seat_objects': seat_names,
        },
        'frame': {
            'seat_yaw_deg': round(float(yaw), 6),
            'front_unity': [round(front.x, 6), 0.0, round(front.z, 6)],
            'right_unity': [round(right.x, 6), 0.0, round(right.z, 6)],
            'up_unity': [0.0, 1.0, 0.0],
        },
        'seat': {
            'surface_point_unity': [round(seat.x, 6), round(top_z, 6), round(seat.z, 6)],
            'surface_y_unity': round(top_z, 6),
            'ground_y_unity': round(seat_ground, 6),
            'height_above_ground_m': round(top_z - seat_ground, 6),
            'top_bounds_unity_xz': [seat_top_x, seat_top_z],
            'top_width_m': round(seat_top_x[1] - seat_top_x[0], 6),
            'top_depth_m': round(seat_top_z[1] - seat_top_z[0], 6),
            'top_vertex_count': len(top_vertices),
        },
        'approach': {
            'position_unity': [round(approach_xz.x, 6), round(ground_y, 6), round(approach_xz.z, 6)],
            'yaw_deg': round((float(yaw) + 180.0) % 360.0, 6),
            'ground_y_unity': round(ground_y, 6),
            'clearance_m': clearance,
            'agent_radius_m': AGENT_RADIUS_M,
            'safety_margin_m': SAFETY_MARGIN_M,
            'method': 'whole_bench_front_boundary_plus_agent_radius_and_safety_margin',
            'ground_sample': {'method': 'nearest_cell_1m', 'index': [ground_ix, ground_iz]},
        },
        'verification': {
            'seat_surface_from': 'evaluated world vertices of source *_seat objects',
            'seat_yaw_from': 'world_entities.orientation.yaw_unity_deg',
            'approach_from': 'whole-bench projected front boundary + clearance',
            'ground_from': 'Assets/Resources/SovietBlueHour/ground_height.json',
            'seat_top_vertex_count': len(top_vertices),
            'clearance_m': clearance,
            'error_m': 0.0,
        },
    }


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--workspace', type=Path, default=Path(__file__).resolve().parents[3])
    parser.add_argument('--blend', type=Path)
    parser.add_argument('--out', type=Path)
    arguments = parser.parse_args(sys.argv[sys.argv.index('--') + 1:] if '--' in sys.argv else [])
    workspace = arguments.workspace.resolve()
    blend = arguments.blend or workspace / 'Maps/SovietBlueHour/map/SovietBlueHour_beauty.blend'
    out = arguments.out or workspace / 'Maps/SovietBlueHour/entities/interaction_geometry.v0.json'
    entities_path = workspace / 'Maps/SovietBlueHour/entities/world_entities.v0.json'
    ground_path = workspace / 'Assets/Resources/SovietBlueHour/ground_height.json'
    stage2_manifest_path = workspace / 'Assets/Resources/SovietBlueHour/stage2_pick/manifest.json'
    stage2_runtime_path = workspace / 'Assets/Resources/SovietBlueHour/stage2_pick/runtime_manifest.json'

    document = json.loads(entities_path.read_text(encoding='utf-8'))
    ground_data = json.loads(ground_path.read_text(encoding='utf-8'))
    by_id = {entity['id']: entity for entity in document['entities']}
    blend_sha = sha256(blend)
    entries = []
    for entity_id in TARGETS:
        entity = by_id.get(entity_id)
        if entity is None:
            raise ValueError('missing entity: ' + entity_id)
        entries.append(one_entry(entity, ground_data, blend_sha))

    payload = {
        'schema': 'sbh.interaction_geometry.v1',
        'data_version': '0.1.0',
        'map_version': document['map']['version'],
        'blend_sha256': blend_sha,
        'exporter': {
            'tool': 'Maps/SovietBlueHour/tools/export_interaction_geometry.py',
            'version': '0.1.0',
            'blender': bpy.app.version_string,
        },
        'coordinate_contract': 'Unity: +X east, +Z north, +Y up; seat point is seat surface top centre',
        'seat_point_semantics': 'seat surface top centre, not a character root or hips target',
        'capability_status': 'parameters_only',
        'implemented_actions': [],
        'source_documents': {
            'world_entities_sha256': sha256(entities_path),
            'ground_height_sha256': sha256(ground_path),
            'stage2_manifest_sha256': sha256(stage2_manifest_path),
            'stage2_runtime_manifest_sha256': sha256(stage2_runtime_path),
        },
        'entries': entries,
        'excluded_entities': [],
        'notes': [
            'Stage 3 only: two verified plaza benches; no invalid bench is included in this delivery.',
            'usable_for_interaction only means seat parameters are ready; implemented_actions stays empty.',
            'Approach point is on the ground outside the whole bench footprint plus radius 0.35 m and margin 0.15 m.',
            'No character/hips target is stored; Phase 4 must calibrate the seated action offset.',
        ],
    }
    out.parent.mkdir(parents=True, exist_ok=True)
    out.write_text(json.dumps(payload, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
    print('INTERACTION_GEOMETRY_DONE', out, 'entries=%d' % len(entries), 'blend_sha=' + blend_sha[:12])


if __name__ == '__main__':
    main()
