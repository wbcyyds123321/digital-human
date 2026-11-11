"""Validate interaction_geometry.v0.json without touching Unity or Blender source files."""
import argparse
import csv
import hashlib
import json
import math
import sys
from pathlib import Path

TOL = 1e-4


def sha256(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


def sample_ground(data, x, z):
    cell = float(data['cell'])
    ix = int(round((float(x) - float(data['min_x'])) / cell))
    iz = int(round((float(z) - float(data['min_z'])) / cell))
    if ix < 0 or iz < 0 or ix >= int(data['w']) or iz >= int(data['h']):
        raise ValueError('outside ground grid')
    return round(float(data['heights'][iz * int(data['w']) + ix]), 6), ix, iz


def check(results, name, passed, detail=''):
    results.append({'name': name, 'passed': bool(passed), 'detail': detail})


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--workspace', type=Path, default=Path(__file__).resolve().parents[3])
    parser.add_argument('--geometry', type=Path)
    parser.add_argument('--out', type=Path)
    args = parser.parse_args()
    workspace = args.workspace.resolve()
    geometry_path = args.geometry or workspace / 'Maps/SovietBlueHour/entities/interaction_geometry.v0.json'
    out_dir = args.out or workspace / 'Maps/SovietBlueHour/entities'
    sidecar = json.loads(geometry_path.read_text(encoding='utf-8'))
    entities_doc = json.loads((workspace / 'Maps/SovietBlueHour/entities/world_entities.v0.json').read_text(encoding='utf-8'))
    ground = json.loads((workspace / 'Assets/Resources/SovietBlueHour/ground_height.json').read_text(encoding='utf-8'))
    collision = json.loads((workspace / 'Assets/Resources/SovietBlueHour/collision.json').read_text(encoding='utf-8'))
    entities = {entity['id']: entity for entity in entities_doc['entities']}
    results = []

    check(results, 'schema', sidecar.get('schema') == 'sbh.interaction_geometry.v1', str(sidecar.get('schema')))
    check(results, 'capability_parameters_only',
          sidecar.get('capability_status') == 'parameters_only' and sidecar.get('implemented_actions') == [],
          str(sidecar.get('capability_status')))
    check(results, 'two_entries', len(sidecar.get('entries', [])) == 2, str(len(sidecar.get('entries', []))))
    check(results, 'implemented_actions_empty', all(not entry.get('implemented_actions') for entry in sidecar['entries']),
          'no executable actions')
    check(results, 'blend_sha_matches_inventory',
          sidecar.get('blend_sha256') == entities_doc['map']['blend_sha256'],
          sidecar.get('blend_sha256'))
    check(results, 'world_entities_hash_matches',
          sidecar['source_documents']['world_entities_sha256'] == sha256(workspace / 'Maps/SovietBlueHour/entities/world_entities.v0.json'),
          sidecar['source_documents']['world_entities_sha256'])
    check(results, 'ground_height_hash_matches',
          sidecar['source_documents']['ground_height_sha256'] == sha256(workspace / 'Assets/Resources/SovietBlueHour/ground_height.json'),
          sidecar['source_documents']['ground_height_sha256'])
    check(results, 'stage2_manifest_hash_matches',
          sidecar['source_documents']['stage2_manifest_sha256'] == sha256(workspace / 'Assets/Resources/SovietBlueHour/stage2_pick/manifest.json'),
          sidecar['source_documents']['stage2_manifest_sha256'])
    check(results, 'stage2_runtime_manifest_hash_matches',
          sidecar['source_documents']['stage2_runtime_manifest_sha256'] == sha256(workspace / 'Assets/Resources/SovietBlueHour/stage2_pick/runtime_manifest.json'),
          sidecar['source_documents']['stage2_runtime_manifest_sha256'])
    evidence_dir = out_dir / 'stage3_evidence'
    annotations = sorted(str(path.relative_to(out_dir)) for path in evidence_dir.glob('*_annotated.png'))
    check(results, 'two_annotated_images', len(annotations) == 2, ','.join(annotations))
    repro_path = evidence_dir / 'repro_check.json'
    repro = json.loads(repro_path.read_text(encoding='utf-8')) if repro_path.exists() else {}
    check(results, 'duplicate_export_deterministic',
          bool(repro.get('identical')) and repro.get('first_sha256') == sha256(geometry_path),
          str(repro.get('first_sha256')))

    local_signatures = []
    for entry in sidecar['entries']:
        entity_id = entry['entity_id']
        entity = entities.get(entity_id)
        if entity is None:
            check(results, entity_id + ':entity_exists', False, 'missing');
            continue
        check(results, entity_id + ':source_group', entry['source']['source_group'] == entity['source_group'], entry['source']['source_group'])
        check(results, entity_id + ':source_objects',
              sorted(entry['source']['source_objects']) == sorted(entity['source_objects']), 'object lists equal')
        check(results, entity_id + ':class', entry['entity_class'] == entity['class'], entity['class'])
        check(results, entity_id + ':status_verified', entry['geometry_status'] == 'verified', entry['geometry_status'])
        check(results, entity_id + ':usable_true', entry['usable_for_interaction'] is True, 'parameters ready only')
        check(results, entity_id + ':implemented_actions_empty', entry['implemented_actions'] == [], 'empty')
        seat = entry['seat']
        approach = entry['approach']
        seat_x, seat_y, seat_z = seat['surface_point_unity']
        check(results, entity_id + ':seat_top_y', abs(seat_y - seat['surface_y_unity']) <= TOL, str(seat_y))
        check(results, entity_id + ':seat_inside_top_bounds',
              seat['top_bounds_unity_xz'][0][0] - TOL <= seat_x <= seat['top_bounds_unity_xz'][0][1] + TOL and
              seat['top_bounds_unity_xz'][1][0] - TOL <= seat_z <= seat['top_bounds_unity_xz'][1][1] + TOL,
              'seat point is within the measured top face')
        ground_y, gx, gz = sample_ground(ground, seat_x, seat_z)
        check(results, entity_id + ':seat_ground', abs(ground_y - seat['ground_y_unity']) <= TOL,
              '%s vs %s' % (ground_y, seat['ground_y_unity']))
        check(results, entity_id + ':seat_height_above_ground',
              abs((seat['surface_y_unity'] - seat['ground_y_unity']) - seat['height_above_ground_m']) <= TOL,
              str(seat['height_above_ground_m']))
        yaw = entry['frame']['seat_yaw_deg']
        front = entry['frame']['front_unity']; right = entry['frame']['right_unity']; up = entry['frame']['up_unity']
        dot = abs(front[0] * right[0] + front[1] * right[1] + front[2] * right[2])
        check(results, entity_id + ':frame_orthogonal', dot <= 1e-6 and up == [0.0, 1.0, 0.0],
              'front.right=%s' % dot)
        check(results, entity_id + ':yaw_180', abs(yaw - 180.0) <= TOL, str(yaw))
        ax, ay, az = approach['position_unity']
        check(results, entity_id + ':approach_ground', abs(ay - approach['ground_y_unity']) <= TOL, str(ay))
        check(results, entity_id + ':approach_clearance',
              approach['clearance_m'] >= approach['agent_radius_m'] + approach['safety_margin_m'] - TOL,
              str(approach['clearance_m']))
        bbox = entity['bbox']['world_aabb_unity']
        inside_bench = bbox[0] - TOL <= ax <= bbox[3] + TOL and bbox[2] - TOL <= az <= bbox[5] + TOL
        check(results, entity_id + ':approach_outside_bench_bbox', not inside_bench,
              'bbox x[%s,%s] z[%s,%s]' % (bbox[0], bbox[3], bbox[2], bbox[5]))
        distance_front = abs(az - bbox[2]) if az <= bbox[2] else abs(az - bbox[5])
        check(results, entity_id + ':approach_has_clearance_from_bbox', distance_front + TOL >= approach['clearance_m'],
              'distance %.4f, clearance %.4f' % (distance_front, approach['clearance_m']))
        blocked = []
        for box in collision['boxes']:
            if box.get('kind') == 'ground':
                continue
            mn = box['min']; mx = box['max']
            if mn[0] <= ax <= mx[0] and mn[1] <= ay <= mx[1] and mn[2] <= az <= mx[2]:
                blocked.append(box.get('id', '?'))
        check(results, entity_id + ':approach_not_inside_collision', not blocked, ','.join(blocked) or 'clear')
        center = entity['center']['unity']
        local_signatures.append((
            round(seat_x - center[0], 4), round(seat_z - center[2], 4),
            round(approach['position_unity'][0] - center[0], 4), round(az - center[2], 4),
            round(seat['top_width_m'], 3), round(seat['top_depth_m'], 3),
            round(yaw, 3),
        ))
    if len(local_signatures) == 2:
        check(results, 'same_local_geometry', local_signatures[0] == local_signatures[1],
              '%s vs %s' % (local_signatures[0], local_signatures[1]))

    passed = all(item['passed'] for item in results)
    report = {
        'schema': 'sbh.stage3.interaction_geometry_report.v1',
        'passed': passed,
        'checks': results,
        'pass_count': sum(1 for item in results if item['passed']),
        'fail_count': sum(1 for item in results if not item['passed']),
        'geometry_sha256': sha256(geometry_path),
        'targets': [entry['entity_id'] for entry in sidecar['entries']],
        'annotations': annotations,
        'source_map': 'interaction_source_map.csv',
        'reproducibility': repro,
        'visual_review': 'annotated images show the actual source mesh with seat point, seat facing arrow and approach point; inspected directly by the agent (vision.js is independently unavailable).',
        'delivery': ['seat surface point', 'seat yaw/front', 'ground approach point'],
        'limitations': [
            'no character/hips target; Phase 4 must calibrate the seated action offset',
            'no executable interaction; implemented_actions remains empty',
            'no invalid bench in this minimal delivery; abnormal scan does not block delivery',
            'ground height is sampled from the 1 m grid at the seat/approach point',
            'world_entities.ground.unity_y is 0.0 for these samples, but the actual ground_height.json cells are 0.15 m; the sidecar uses 0.15 m',
        ],
    }
    out_dir.mkdir(parents=True, exist_ok=True)
    (out_dir / 'interaction_geometry_report.json').write_text(json.dumps(report, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')

    md = ['# Stage 3 · interaction geometry validation', '',
          '- Result: **%s**' % ('PASS' if passed else 'FAIL'),
          '- Checks: **%d pass / %d fail**' % (report['pass_count'], report['fail_count']),
          '- Geometry SHA256: `%s`' % report['geometry_sha256'], '',
          '| Check | Result | Detail |', '|---|---|---|']
    for item in results:
        md.append('| %s | %s | %s |' % (item['name'], 'PASS' if item['passed'] else 'FAIL', item['detail'].replace('|', '\\|')))
    md += ['', '## Values', '']
    for entry in sidecar['entries']:
        seat = entry['seat']; approach = entry['approach']
        md += ['### ' + entry['entity_id'], '',
               '- seat surface centre: `%s`' % seat['surface_point_unity'],
               '- seat yaw: `%s°`' % entry['frame']['seat_yaw_deg'],
               '- ground at seat/approach: `%s m`' % seat['ground_y_unity'],
               '- seat height above ground: `%s m`' % seat['height_above_ground_m'],
               '- approach: `%s`, yaw `%s°`, clearance `%s m`' % (approach['position_unity'], approach['yaw_deg'], approach['clearance_m']),
               '- usable_for_interaction: `%s` (parameters only; implemented_actions=[])' % entry['usable_for_interaction'], '']
    md += ['', '## Annotation images', ''] + ['- `%s`' % path for path in annotations]
    md += ['', '## Visual review', '', report['visual_review']]
    md += ['', '## Limitations', ''] + ['- ' + line for line in report['limitations']]
    (out_dir / 'interaction_geometry_report.md').write_text('\n'.join(md) + '\n', encoding='utf-8')

    csv_path = out_dir / 'interaction_source_map.csv'
    with csv_path.open('w', encoding='utf-8', newline='') as stream:
        writer = csv.writer(stream)
        writer.writerow(['entity_id', 'source_group', 'seat_objects', 'seat_surface_x', 'seat_surface_y', 'seat_surface_z', 'seat_yaw_deg', 'approach_x', 'approach_y', 'approach_z', 'approach_yaw_deg', 'ground_y', 'clearance_m', 'geometry_status', 'usable_for_interaction'])
        for entry in sidecar['entries']:
            seat = entry['seat']; approach = entry['approach']
            writer.writerow([entry['entity_id'], entry['source']['source_group'], ';'.join(entry['source']['seat_objects']),
                             seat['surface_point_unity'][0], seat['surface_point_unity'][1], seat['surface_point_unity'][2],
                             entry['frame']['seat_yaw_deg'], approach['position_unity'][0], approach['position_unity'][1],
                             approach['position_unity'][2], approach['yaw_deg'], approach['ground_y_unity'],
                             approach['clearance_m'], entry['geometry_status'], entry['usable_for_interaction']])
    print('INTERACTION_GEOMETRY_VALIDATE', 'PASS' if passed else 'FAIL', report['pass_count'], '/', len(results))
    if not passed:
        for item in results:
            if not item['passed']:
                print('FAIL', item['name'], item['detail'])
        return 1
    return 0


if __name__ == '__main__':
    sys.exit(main())
