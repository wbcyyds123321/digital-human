"""Independently validate the entity inventory against the open, unsaved Blender source."""
import argparse
import collections
import csv
import hashlib
import json
import math
from pathlib import Path
import re
import sys

import bpy
import numpy as np


def read_json(path):
    return json.loads(path.read_text(encoding='utf-8'))


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def world_vertices(obj):
    coordinates = np.empty(len(obj.data.vertices) * 3, dtype=np.float64)
    obj.data.vertices.foreach_get('co', coordinates)
    matrix = np.array(obj.matrix_world, dtype=np.float64)
    return coordinates.reshape(-1, 3) @ matrix[:3, :3].T + matrix[:3, 3]


def angular_error(actual, expected):
    return abs((actual - expected + 180.0) % 360.0 - 180.0)


def seat_center(objects):
    centers, areas = [], []
    for obj in objects:
        for polygon in obj.data.polygons:
            material = obj.material_slots[polygon.material_index].material
            if material is None or 'WOOD' not in material.name.upper():
                continue
            center = obj.matrix_world @ polygon.center
            if abs(polygon.normal.z) > 0.95 and 0.42 < center.z < 0.48:
                centers.append(tuple(center))
                areas.append(polygon.area)
    return np.average(centers, axis=0, weights=areas) if centers else None


def expected_bench_yaw(entity, objects, plan):
    position = seat_center(objects)
    if position is None:
        return None, 'seat_surface_unavailable'
    group = entity['source_group']
    target = None
    source = None
    if group.startswith('PROP_BENCH_BENCH_'):
        zone_id = group[len('PROP_BENCH_'):].rsplit('_', 1)[0]
        zone = next((zone for zone in plan['prop_zones'] if zone['id'] == zone_id), None)
        if zone:
            rect = zone['rect']
            target = ((rect[0] + rect[2]) / 2, (rect[1] + rect[3]) / 2)
            source = 'props.py build_furniture: prop-zone center'
    elif group.startswith('PROP_PARK_BENCH_'):
        pond = next(zone for zone in plan['zones'] if zone['kind'] == 'water' and zone.get('ellipse'))
        rect = pond['rect']
        target = ((rect[0] + rect[2]) / 2, (rect[1] + rect[3]) / 2)
        source = 'props.py build_park: pond center'
    elif group.startswith('PROP_PAVILION_BENCH_'):
        pavilion = next(landmark for landmark in plan['landmarks'] if landmark['type'] == 'pavilion')
        target = pavilion['pos'][:2]
        source = 'props.py build_pavilion: pavilion center'
    elif group.startswith('PROP_TERMINAL_BENCH_'):
        return 180.0, 'props.py terminal benches: facing=0'
    elif group.startswith('PROP_BENCH_'):
        zone_id, index = group[len('PROP_BENCH_'):].rsplit('_', 1)
        zone = next((zone for zone in plan['zones'] if zone['id'] == zone_id), None)
        if zone and index in ('0', '1'):
            rect = zone['rect']
            wide = rect[2] - rect[0] >= rect[3] - rect[1]
            return ((0.0 if index == '0' else 180.0) if wide else
                    (90.0 if index == '0' else 270.0)), 'props.py courtyard candidate placement'
    if target is None:
        return None, 'generation_path_not_reconstructed'
    return math.degrees(math.atan2(target[0] - position[0], target[1] - position[1])) % 360, source


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--workspace', type=Path, default=Path(__file__).resolve().parents[3])
    parser.add_argument('--out', type=Path)
    arguments = parser.parse_args(sys.argv[sys.argv.index('--') + 1:] if '--' in sys.argv else [])
    root = arguments.workspace.resolve()
    output = arguments.out or root / 'Maps/SovietBlueHour/entities'
    document = read_json(output / 'world_entities.v0.json')
    entities = document['entities']
    plan = read_json(root / 'Maps/SovietBlueHour/tools/plan.json')
    nav = read_json(root / 'Maps/SovietBlueHour/map/nav_graph.json')
    runtime_nav = read_json(root / 'Assets/Resources/SovietBlueHour/nav_compact.json')
    nodes = {node['id']: node for node in nav['nodes']}
    meshes = {obj.name: obj for obj in bpy.data.objects if obj.type == 'MESH'}
    with (output / 'source_object_map.csv').open(encoding='utf-8-sig', newline='') as stream:
        source_rows = list(csv.DictReader(stream))
    checks = []

    def record(name, failures, checked):
        checks.append({'name': name, 'checked': checked, 'passed': not failures,
                       'failure_count': len(failures), 'failures': failures[:30]})

    source_names = [row['source_object'] for row in source_rows]
    identifiers = [entity['id'] for entity in entities]
    sources = [name for entity in entities for name in entity['source_objects']]
    by_id = {entity['id']: entity for entity in entities}
    record('unique_entity_ids', [name for name, count in collections.Counter(identifiers).items() if count > 1], len(identifiers))
    record('unique_source_attribution', [name for name, count in collections.Counter(sources).items() if count > 1], len(sources))
    record('source_mesh_coverage', sorted(set(meshes) ^ set(source_names)) +
           [name for name, count in collections.Counter(source_names).items() if count > 1], len(meshes))
    attribution_errors = []
    for row in source_rows:
        if row['status'] == 'entity':
            entity = by_id.get(row['entity_id'])
            if entity is None or row['source_object'] not in entity['source_objects']:
                attribution_errors.append(row['source_object'])
        elif row['status'] not in ('excluded', 'unclassified') or row['entity_id']:
            attribution_errors.append(row['source_object'])
    entity_rows = {(row['source_object'], row['entity_id']) for row in source_rows if row['status'] == 'entity'}
    attribution_errors.extend(name for entity in entities for name in entity['source_objects']
                              if (name, entity['id']) not in entity_rows)
    record('csv_json_attribution', attribution_errors, len(source_rows))
    hash_paths = {'blend_sha256': Path(bpy.data.filepath),
                  'plan_sha256': root / 'Maps/SovietBlueHour/tools/plan.json',
                  'scene_manifest_sha256': root / 'Maps/SovietBlueHour/map/scene_manifest.json',
                  'nav_graph_sha256': root / 'Maps/SovietBlueHour/map/nav_graph.json',
                  'exporter_sha256': root / 'Maps/SovietBlueHour/tools/export_entities.py'}
    hashes = {key: digest(path) for key, path in hash_paths.items()}
    record('source_hashes', [key for key, value in hashes.items() if document['map'].get(key) != value], len(hashes))
    dependencies = document['map'].get('generator_dependencies_sha256', {})
    record('generator_dependency_hashes', [name for name, expected in dependencies.items()
           if digest(root / 'Maps/SovietBlueHour/tools' / name) != expected], len(dependencies))
    integrity = document.get('read_only_integrity', {})
    record('source_before_after_integrity', [] if integrity.get('unchanged') and
           integrity.get('before_sha256') == integrity.get('after_sha256') else ['missing_or_changed'], 4)
    bounds_errors, obb_errors, coordinate_rows, bench_rows = [], [], [], []
    obb_fit_errors, obb_rows = [], []
    actual_bounds = {}
    obb_count = 0
    for entity in entities:
        objects = [meshes[name] for name in entity['source_objects'] if name in meshes]
        if len(objects) != len(entity['source_objects']) or not objects:
            bounds_errors.append({'id': entity['id'], 'error': 'source_mesh_missing'})
            continue
        vertices = np.concatenate([world_vertices(obj) for obj in objects])
        minimum, maximum = vertices.min(axis=0), vertices.max(axis=0)
        center = (minimum + maximum) / 2
        expected = center[[0, 2, 1]]
        expected_bounds = np.concatenate((minimum[[0, 2, 1]], maximum[[0, 2, 1]]))
        actual_bounds[entity['id']] = expected_bounds
        error = float(np.max(np.abs(expected - entity['center']['unity'])))
        bounds_error = float(np.max(np.abs(expected_bounds - entity['bbox']['world_aabb_unity'])))
        blender_error = float(np.max(np.abs(np.array([minimum, maximum]) - entity['bbox']['world_aabb_blender'])))
        blender_center_error = float(np.max(np.abs(center - entity['center']['blender'])))
        if max(error, bounds_error, blender_error, blender_center_error) > 0.001:
            bounds_errors.append({'id': entity['id'], 'center_error_m': error, 'bounds_error_m': bounds_error,
                                  'blender_bounds_error_m': blender_error})
        coordinate_rows.append({'id': entity['id'], 'class': entity['class'], 'expected_unity': expected.tolist(),
                                'exported_unity': entity['center']['unity'], 'max_axis_error_m': error,
                                'bounds_error_m': bounds_error})
        obb = entity['bbox']['obb']
        if obb:
            obb_count += 1
            yaw = math.radians(obb['yaw_unity_deg'])
            local = vertices - obb['center_blender']
            local = np.column_stack((local[:, 0] * math.cos(yaw) - local[:, 1] * math.sin(yaw),
                                     local[:, 0] * math.sin(yaw) + local[:, 1] * math.cos(yaw), local[:, 2]))
            overflow = float(np.max(np.abs(local) - np.array(obb['size_blender']) / 2))
            vertical_error = abs(obb['center_blender'][2] - center[2])
            if overflow > 0.001 or vertical_error > 0.001:
                obb_errors.append({'id': entity['id'], 'overflow_m': overflow, 'vertical_center_error_m': vertical_error})
            local_minimum, local_maximum = local.min(axis=0), local.max(axis=0)
            local_center_error = float(np.max(np.abs((local_minimum + local_maximum) / 2)))
            expected_size = local_maximum - local_minimum
            size_error = float(np.max(np.abs(expected_size - obb['size_blender'])))
            fit = {'id': entity['id'], 'local_center_error_m': local_center_error,
                   'size_error_m': size_error, 'expected_size_blender': expected_size.tolist(),
                   'exported_size_blender': obb['size_blender']}
            obb_rows.append(fit)
            if local_center_error > 0.01 or size_error > 0.01:
                obb_fit_errors.append(fit)
        if entity['class'] == 'bench' and (entity['orientation']['source'] or '').startswith('geometric'):
            expected_yaw, source = expected_bench_yaw(entity, objects, plan)
            exported_yaw = entity['orientation']['yaw_unity_deg']
            difference = angular_error(exported_yaw, expected_yaw) if expected_yaw is not None and exported_yaw is not None else None
            bench_rows.append({'id': entity['id'], 'expected_yaw': expected_yaw, 'exported_yaw': exported_yaw,
                               'error_deg': difference, 'expected_source': source})
    record('vertex_bounds_and_centers_1mm', bounds_errors, len(entities))
    record('obb_vertex_containment_and_center_1mm', obb_errors, obb_count)
    record('obb_local_center_and_tightness_1cm', obb_fit_errors, obb_count)
    entrance_errors, entrance_count = [], 0
    for entity in entities:
        for candidate in entity['entrance_candidates']:
            entrance_count += 1
            node = nodes[candidate['nav_node_id']]
            expected = [node['x'], node['z'], node['y']]
            runtime = runtime_nav['nodes'][candidate['nav_node_id']]
            error = max(abs(actual - desired) for actual, desired in zip(candidate['position_unity'], expected))
            runtime_error = max(abs(runtime[axis] - expected[index]) for index, axis in enumerate(('x', 'y', 'z')))
            if error > 0.001 or runtime_error > 0.001 or node['name'] != entity['source_group']:
                entrance_errors.append({'id': entity['id'], 'node': node['id'], 'error_m': error,
                                         'runtime_error_m': runtime_error})
    record('entrances_raw_and_runtime_coordinates_1mm', entrance_errors, entrance_count)
    grouping_errors = [entity['id'] for entity in entities
                       if entity['source_group'].startswith('STALL') or
                       re.match(r'^PROP_LAMP_.+_box$', entity['source_group']) or
                       re.match(r'^PROP_FENCE_.+_[hv]\d+$', entity['source_group']) or
                       (entity['source_group'].startswith('PROP_STAND_') and entity['class'] != 'notice_board')]
    record('repaired_semantics_and_part_grouping', grouping_errors, len(entities))
    migration_path = output / 'id_migration.v0.1.0_to_v0.1.1.json'
    migration_summary = None
    if migration_path.exists():
        migration = read_json(migration_path)
        migration_summary = migration['summary']
        migration_ids = [row['old_id'] for row in migration['entities']]
        migration_errors = [name for name, count in collections.Counter(migration_ids).items() if count > 1]
        migration_errors.extend(target for row in migration['entities'] for target in row['new_ids'] if target not in by_id)
        migration_errors.extend(row['old_id'] for row in migration['entities']
                                if row['status'] == 'excluded' and row['new_ids'])
        if sum(migration_summary.values()) != len(migration_ids):
            migration_errors.append('summary_count_mismatch')
        record('id_migration_targets_and_coverage', migration_errors, len(migration_ids))
    comparable = [row for row in bench_rows if row['exported_yaw'] is not None]
    record('geometric_bench_yaw_vs_generation_paths_0.05deg',
           [row for row in comparable if row['error_deg'] is None or row['error_deg'] > 0.05], len(comparable))
    probe_path = root / 'Logs/map_import/geometry_probe.json'
    probe_rows = []
    if probe_path.exists():
        probe = read_json(probe_path)
        for name in ('BAKERY', 'BANYA', 'MSU_MAIN', 'MOTHERLAND', 'CLINIC'):
            imported = next((node for node in probe['nodes'] if node['name'].startswith('BLD_' + name + '.')), None)
            entity_id = 'entity/bld_' + name.lower()
            if imported and entity_id in actual_bounds:
                observed = [imported[key] for key in ('minX', 'minY', 'minZ', 'maxX', 'maxY', 'maxZ')]
                probe_rows.append({'id': entity_id, 'max_bounds_error_m': float(np.max(np.abs(actual_bounds[entity_id] - observed)))})
        record('existing_unity_import_probe_1cm', [row for row in probe_rows if row['max_bounds_error_m'] > 0.01], len(probe_rows))
    sample_ids = []
    sample_path = output / 'orientation_sample_check.csv'
    if sample_path.exists():
        with sample_path.open(encoding='utf-8-sig', newline='') as stream:
            sample_ids = [row['id'] for row in csv.DictReader(stream) if row['id'] in by_id]
    if not any(by_id[name]['class'] == 'notice_board' for name in sample_ids):
        notice = next(entity['id'] for entity in entities if entity['class'] == 'notice_board')
        if len(sample_ids) >= 20:
            sample_ids[-1] = notice
        else:
            sample_ids.append(notice)
    for row in coordinate_rows:
        if len(sample_ids) >= 20:
            break
        if row['id'] not in sample_ids:
            sample_ids.append(row['id'])
    coordinates = {row['id']: row for row in coordinate_rows}
    report = {'schema': 'sbh.world.entities.validation.v1', 'data_version': document['data_version'],
              'passed': all(check['passed'] for check in checks), 'checks': checks,
              'source_hashes': hashes, 'inventory_sha256': digest(output / 'world_entities.v0.json'),
              'validator_sha256': digest(Path(__file__)), 'obb_fit': obb_rows,
              'all_coordinates': coordinate_rows, 'coordinate_samples': [coordinates[name] for name in sample_ids[:20]],
              'unity_import_probe': probe_rows, 'geometric_bench_orientation': bench_rows,
              'migration_summary': migration_summary,
              'bench_summary': {'comparable': len(comparable), 'max_error_deg': max((row['error_deg'] or 0 for row in comparable), default=0),
                                'null_yaw': sum(row['exported_yaw'] is None for row in bench_rows)},
              'visual_validation': 'independent review pending: vision.js returned HTTP 403 AllocationQuota.FreeTierOnly; '
                                   'user-supplied DeepSeek review reports malformed rotated benches; see the Stage 0-1 contract; no visual pass claimed'}
    (output / 'validation_report.json').write_text(json.dumps(report, ensure_ascii=False, indent=1), encoding='utf-8')
    lines = ['# 世界实体独立验收', '', '独立读取 Blender 源顶点、原始导航和现有 Unity 导入探针；不导入导出器、不保存源场景。', '',
             '| 检查 | 数量 | 结果 |', '|---|---:|---|']
    lines.extend('| %s | %d | %s |' % (check['name'], check['checked'], '通过' if check['passed'] else '失败 %d' % check['failure_count']) for check in checks)
    lines.extend(['', '## 20 条独立坐标核验', '', '期望来自源网格真实顶点；单位为米，阈值 0.001m。', '',
                  '| ID | 期望 Unity (x,y,z) | 导出 Unity (x,y,z) | 最大轴误差 m |', '|---|---|---|---:|'])
    lines.extend('| `%s` | %s | %s | %.8f |' % (row['id'], [round(value, 6) for value in row['expected_unity']],
                  row['exported_unity'], row['max_axis_error_m']) for row in report['coordinate_samples'])
    lines.extend(['', '## 几何长椅独立朝向', '', '期望采用生成器的朝向目标及座面材质面片中心；未使用导出器整椅包围盒或其推断朝向。', '',
                  '| ID | 生成路径期望° | 导出° | 误差° | 来源 |', '|---|---:|---:|---:|---|'])
    lines.extend('| `%s` | %s | %s | %s | %s |' % (row['id'], row['expected_yaw'], row['exported_yaw'], row['error_deg'], row['expected_source']) for row in bench_rows)
    lines.extend(['', '## OBB 贴合检查', '', '在每个导出 yaw 的局部坐标系中，检查真实顶点范围的中心与尺寸；三个轴的误差均不得超过 0.01m。', '',
                  '| ID | 最大中心误差 m | 最大尺寸误差 m |', '|---|---:|---:|'])
    lines.extend('| `%s` | %.8f | %.8f |' % (row['id'], row['local_center_error_m'], row['size_error_m']) for row in obb_rows)
    lines.extend(['', '本代理独立视觉验收待完成：vision.js 因免费额度耗尽返回 403。用户提供的 DeepSeek 复验称已查看参考图，发现旋转长椅源几何错位；代码核对确认生成器漏转板件。此记录不等于画面验收通过。', '',
                  '完整差异、哈希及现有 Unity 导入探针对照见 validation_report.json。'])
    (output / 'validation_report.md').write_text('\n'.join(lines) + '\n', encoding='utf-8')
    print('ENTITY_VALIDATION', json.dumps({'passed': report['passed'], 'checks': [{key: check[key] for key in ('name', 'checked', 'failure_count')} for check in checks]}, ensure_ascii=False))
    if not report['passed']:
        raise SystemExit(1)


if __name__ == '__main__':
    main()
