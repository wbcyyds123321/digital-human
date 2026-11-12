"""export_stage2_pick_data.py -- Stage 2, step 1 (READ ONLY).

Exports the four pick samples as triangle proxies + the curated occluder set +
solved test poses for the crosshair prototype.  Opens the beauty .blend without
saving it; writes only new files under Assets/Resources/SovietBlueHour/stage2_pick/
and a report under Maps/SovietBlueHour/entities/.

Run:
  blender --background "Maps/SovietBlueHour/map/SovietBlueHour_beauty.blend" \
    --python-exit-code 1 --python "Maps/SovietBlueHour/tools/export_stage2_pick_data.py" -- \
    --workspace "<unity-project-root>"
"""
import bpy
import hashlib
import json
import math
import os
import struct
import sys

import numpy as np

# ---------------------------------------------------------------------------
# paths / args
# ---------------------------------------------------------------------------
def parse_args():
    argv = sys.argv
    argv = argv[argv.index('--') + 1:] if '--' in argv else []
    out = {'workspace': None, 'out': None, 'report_dir': None}
    i = 0
    while i < len(argv):
        if argv[i] == '--workspace' and i + 1 < len(argv):
            out['workspace'] = argv[i + 1].replace('\\', '/'); i += 2
        elif argv[i] == '--out' and i + 1 < len(argv):
            out['out'] = argv[i + 1].replace('\\', '/'); i += 2
        elif argv[i] == '--report-dir' and i + 1 < len(argv):
            out['report_dir'] = argv[i + 1].replace('\\', '/'); i += 2
        else:
            i += 1
    return out


ARGS = parse_args()
SCRIPT = os.path.abspath(__file__)
WS = ARGS['workspace'] or os.path.abspath(os.path.join(os.path.dirname(SCRIPT), '..', '..', '..'))
ENT_DIR = os.path.join(WS, 'Maps', 'SovietBlueHour', 'entities')
REPORT_DIR = ARGS['report_dir'] or ENT_DIR
PACK_DIR = ARGS['out'] or os.path.join(WS, 'Assets', 'Resources', 'SovietBlueHour', 'stage2_pick')
REPORT_JSON = os.path.join(REPORT_DIR, 'stage2_step1_report.json')
REPORT_MD = os.path.join(REPORT_DIR, 'stage2_step1_report.md')
os.makedirs(PACK_DIR, exist_ok=True)
os.makedirs(REPORT_DIR, exist_ok=True)

ENT_JSON = os.path.join(ENT_DIR, 'world_entities.v0.json')
COLLISION_JSON = os.path.join(WS, 'Assets', 'Resources', 'SovietBlueHour', 'collision.json')
GROUND_JSON = os.path.join(WS, 'Assets', 'Resources', 'SovietBlueHour', 'ground_height.json')
PLAN_JSON = os.path.join(WS, 'Maps', 'SovietBlueHour', 'tools', 'plan.json')

SAMPLE_IDS = [
    'entity/prop_bench_plaza_1',
    'entity/prop_bench_plaza_2',
    'entity/veh_laz695_01',
    'entity/bld_w_k3',
]
# collision ids that belong to the sample building and must NOT pre-occlude it
BL_W_K3_COLLISION_IDS = {'W_K3_W', 'W_K3_E', 'W_K3_LINTEL'}
# decorative / batched scatter classes: never occluders
NO_OCCLUDER_CLASSES = {
    'weeds', 'litter', 'leaf_pile', 'puddle', 'wet_patch', 'ground_marking', 'forest_trail',
    'stumps_bollards', 'reeds', 'manhole', 'park_prop', 'terminal_prop', 'utility_poles',
    'sign', 'banner', 'taxi_stand', 'bus_sign', 'market_stand',
}
OCCLUDER_CLASSES = {
    'tree', 'street_lamp', 'vehicle', 'bench', 'fence', 'hedge', 'playground', 'bus_stop',
    'dumpster', 'planter', 'billboard', 'poster_board', 'notice_board', 'pavilion',
    'eternal_flame', 'plaque', 'transformer_box', 'mailbox', 'payphone', 'hydrant',
    'bike_rack', 'guard_box', 'fallen_log', 'laundry_frame', 'metro_prop', 'flag',
}
OCCLUDER_MAX_HORIZONTAL = 20.0
EYE_HEIGHT = 1.64


def load_json(p):
    with open(p, encoding='utf-8') as f:
        return json.load(f)


def sha_file(p):
    h = hashlib.sha256()
    with open(p, 'rb') as f:
        for block in iter(lambda: f.read(1 << 20), b''):
            h.update(block)
    return h.hexdigest()


def u3(blender_vec):
    """Blender (x,y,z) -> Unity (x,y,z)."""
    return [round(float(blender_vec[0]), 4), round(float(blender_vec[2]), 4), round(float(blender_vec[1]), 4)]


DOC = load_json(ENT_JSON)
BY_ID = {e['id']: e for e in DOC['entities']}
COLL = load_json(COLLISION_JSON)
GROUND = load_json(GROUND_JSON)
PLAN = load_json(PLAN_JSON)


# ---------------------------------------------------------------------------
# ground / geometry helpers
# ---------------------------------------------------------------------------
def ground_y(x, z):
    min_x, min_z, cell = GROUND['min_x'], GROUND['min_z'], GROUND['cell']
    w, h = GROUND['w'], GROUND['h']
    ix = int(round((x - min_x) / cell))
    iz = int(round((z - min_z) / cell))
    if ix < 0 or iz < 0 or ix >= w or iz >= h:
        return 0.0
    return float(GROUND['heights'][iz * w + ix])


def eye_at(x, z):
    return [float(x), ground_y(x, z) + EYE_HEIGHT, float(z)]


def ray_box(origin, direction, box, eps=1e-6):
    tmin, tmax = 0.0, 1e18
    for i in range(3):
        mn, mx = box['min'][i], box['max'][i]
        if abs(direction[i]) < eps:
            if origin[i] < mn or origin[i] > mx:
                return None
        else:
            t1 = (mn - origin[i]) / direction[i]
            t2 = (mx - origin[i]) / direction[i]
            if t1 > t2:
                t1, t2 = t2, t1
            tmin = max(tmin, t1)
            tmax = min(tmax, t2)
            if tmin > tmax:
                return None
    return tmin


def ray_triangle(origin, direction, a, b, c, eps=1e-8):
    e1 = (b[0] - a[0], b[1] - a[1], b[2] - a[2])
    e2 = (c[0] - a[0], c[1] - a[1], c[2] - a[2])
    h = (direction[1] * e2[2] - direction[2] * e2[1],
         direction[2] * e2[0] - direction[0] * e2[2],
         direction[0] * e2[1] - direction[1] * e2[0])
    det = e1[0] * h[0] + e1[1] * h[1] + e1[2] * h[2]
    if abs(det) < eps:
        return None
    inv = 1.0 / det
    s = (origin[0] - a[0], origin[1] - a[1], origin[2] - a[2])
    u = (s[0] * h[0] + s[1] * h[1] + s[2] * h[2]) * inv
    if u < -1e-6 or u > 1.0 + 1e-6:
        return None
    q = (s[1] * e1[2] - s[2] * e1[1],
         s[2] * e1[0] - s[0] * e1[2],
         s[0] * e1[1] - s[1] * e1[0])
    v = (direction[0] * q[0] + direction[1] * q[1] + direction[2] * q[2]) * inv
    if v < -1e-6 or u + v > 1.0 + 1e-6:
        return None
    t = (e2[0] * q[0] + e2[1] * q[1] + e2[2] * q[2]) * inv
    return t if t > 1e-6 else None


def proxy_hit_ex(proxy, origin, direction):
    """Return (t, triangle_index) of the nearest triangle, or None."""
    verts, tris = proxy['verts'], proxy['tris']
    best = None
    for ti, i in enumerate(range(0, len(tris), 3)):
        a = verts[tris[i] * 3:tris[i] * 3 + 3]
        b = verts[tris[i + 1] * 3:tris[i + 1] * 3 + 3]
        c = verts[tris[i + 2] * 3:tris[i + 2] * 3 + 3]
        t = ray_triangle(origin, direction, a, b, c)
        if t is not None and (best is None or t < best[0]):
            best = (t, ti)
    return best


def proxy_hit(proxy, origin, direction):
    r = proxy_hit_ex(proxy, origin, direction)
    return None if r is None else r[0]


def normalize(v):
    ln = math.sqrt(v[0] * v[0] + v[1] * v[1] + v[2] * v[2])
    if ln < 1e-9:
        return None
    return (v[0] / ln, v[1] / ln, v[2] / ln)


# ---------------------------------------------------------------------------
# 1) sample triangle proxies (read-only from the open beauty scene)
# ---------------------------------------------------------------------------
def export_proxy(entity):
    objs = [bpy.data.objects[n] for n in entity['source_objects'] if n in bpy.data.objects]
    missing = len(entity['source_objects']) - len(objs)
    verts, tris, tri_parts = [], [], []
    parts, part_ids = [], {}
    base = 0
    mod_count = 0
    use_material = (entity['class'] == 'vehicle')

    def part_id(label):
        if label not in part_ids:
            part_ids[label] = len(parts)
            parts.append(label)
        return part_ids[label]

    for obj in objs:
        if len(obj.modifiers) > 0:
            mod_count += 1
        mesh = obj.data
        mesh.calc_loop_triangles()
        mw = obj.matrix_world
        vcount = len(mesh.vertices)
        materials = [s.material.name if (s and s.material) else None for s in obj.material_slots]
        for v in mesh.vertices:
            verts.extend(u3(mw @ v.co))
        for tri in mesh.loop_triangles:
            a, b, c = tri.vertices
            tris.extend((base + a, base + b, base + c))
            mat = None
            if use_material and materials:
                mi = mesh.polygons[tri.polygon_index].material_index
                if 0 <= mi < len(materials):
                    mat = materials[mi]
            tri_parts.append(part_id(mat if (use_material and mat) else obj.name))
        base += vcount
    if not tris:
        raise RuntimeError('no triangles for ' + entity['id'])
    arr_v = np.asarray(verts, dtype='<f4')
    arr_t = np.asarray(tris, dtype='<i4')
    arr_p = np.asarray(tri_parts, dtype='<i4')
    slug = entity['id'].split('/')[-1]
    fname = 'proxy_' + slug + '.bytes'
    with open(os.path.join(PACK_DIR, fname), 'wb') as f:
        f.write(arr_v.tobytes())
        f.write(arr_t.tobytes())
        f.write(arr_p.tobytes())
    flat = np.asarray(verts, dtype=np.float64).reshape(-1, 3)
    bmin = flat.min(axis=0).tolist()
    bmax = flat.max(axis=0).tolist()
    eb = entity['bbox']['world_aabb_unity']
    err = max(max(abs(bmin[i] - eb[i]), abs(bmax[i] - eb[i + 3])) for i in range(3))
    return {
        'file': fname,
        'verts': int(len(verts) // 3),
        'tris': int(len(tris) // 3),
        'parts': parts,
        'bytes': int(arr_v.nbytes + arr_t.nbytes + arr_p.nbytes),
        'sha256': sha_file(os.path.join(PACK_DIR, fname)),
        'bbox_unity': [round(v, 4) for v in bmin] + [round(v, 4) for v in bmax],
        'bbox_error_m': round(float(err), 6),
        'bbox_error_note': '4-decimal source-space check; decoded float32 differs by ~1e-6 m',
        'source_objects': len(objs),
        'missing_source_objects': missing,
        'objects_with_modifiers': mod_count,
    }, {'verts': verts, 'tris': tris, 'tri_parts': tri_parts, 'parts': parts}


SAMPLES = []
PROXIES = {}
for sid in SAMPLE_IDS:
    ent = BY_ID.get(sid)
    if ent is None:
        raise RuntimeError('sample entity missing: ' + sid)
    meta, proxy = export_proxy(ent)
    SAMPLES.append({
        'id': ent['id'],
        'class': ent['class'],
        'name_zh': ent['name_zh'],
        'center_unity': ent['center']['unity'],
        'bbox_unity': ent['bbox']['world_aabb_unity'],
        'orientation': ent['orientation'].get('yaw_unity_deg'),
        'proxy': meta,
    })
    PROXIES[sid] = proxy
    print('SAMPLE', sid, 'tris', meta['tris'], 'verts', meta['verts'], 'bbox_error', meta['bbox_error_m'])

# ---------------------------------------------------------------------------
# 2) curated occluder set
# ---------------------------------------------------------------------------
OCCLUDERS = []
for b in COLL['boxes']:
    if b['kind'] == 'ground':
        continue
    if b['id'] in BL_W_K3_COLLISION_IDS:
        continue
    OCCLUDERS.append({'id': b['id'], 'method': 'raw_collision_box', 'class_or_kind': b['kind'],
                      'min': [round(float(v), 4) for v in b['min']],
                      'max': [round(float(v), 4) for v in b['max']]})
entity_occluders = 0
for e in DOC['entities']:
    if e['id'] in SAMPLE_IDS or e['class'] == 'building':
        continue
    if e['class'] not in OCCLUDER_CLASSES or e['class'] in NO_OCCLUDER_CLASSES:
        continue
    bb = e['bbox']['world_aabb_unity']
    sx, sz = bb[3] - bb[0], bb[5] - bb[2]
    if max(sx, sz) > OCCLUDER_MAX_HORIZONTAL:
        continue
    OCCLUDERS.append({'id': e['id'], 'method': 'entity_aabb', 'class_or_kind': e['class'],
                      'min': [round(bb[0], 4), round(bb[1], 4), round(bb[2], 4)],
                      'max': [round(bb[3], 4), round(bb[4], 4), round(bb[5], 4)]})
    entity_occluders += 1
print('OCCLUDERS', len(OCCLUDERS), 'collision+entity', entity_occluders)


def nearest_occluder(origin, direction, tmax=1e9):
    best = None
    for b in OCCLUDERS:
        t = ray_box(origin, direction, b)
        if t is None or t > tmax:
            continue
        if best is None or t < best[0]:
            best = (t, b['id'], b['method'])
    return best


def inside_collision(p):
    for b in COLL['boxes']:
        if b['kind'] == 'ground':
            continue
        if all(b['min'][i] <= p[i] <= b['max'][i] for i in range(3)):
            return b['id']
    return None


def inside_water_xz(x, z):
    for b in COLL['boxes']:
        if b['kind'] != 'water':
            continue
        if b['min'][0] <= x <= b['max'][0] and b['min'][2] <= z <= b['max'][2]:
            return True
    return False


def valid_eye(x, z):
    p = eye_at(x, z)
    if inside_collision(p):
        return False
    if inside_water_xz(x, z):
        return False
    if x < -196 or x > 200 or z < -145.5 or z > 152.4:
        return False
    return True


# ---------------------------------------------------------------------------
# 3) solve test poses
# ---------------------------------------------------------------------------
def make_targets_from_proxy(proxy, want_heights, sample_count=5):
    """Pick proxy vertices spread over height bands as aim targets."""
    verts = proxy['verts']
    out = []
    for lo, hi in want_heights:
        cand = []
        for i in range(0, len(verts), 3):
            if lo <= verts[i + 1] <= hi:
                cand.append((verts[i], verts[i + 1], verts[i + 2]))
        if cand:
            step = max(1, len(cand) // sample_count)
            out.extend(cand[::step][:sample_count])
    return out


def solve_pose_around(sample, targets, min_d=4.0, max_d=14.0, step=1.5, side=None):
    cx, cy, cz = sample['center_unity']
    best = None
    r = int(math.ceil(max_d / step))
    for dx in range(-r, r + 1):
        for dz in range(-r, r + 1):
            x = cx + dx * step
            z = cz + dz * step
            d_flat = math.hypot(x - cx, z - cz)
            if d_flat < min_d or d_flat > max_d:
                continue
            if side == 'south' and z > cz - 1:
                continue
            if side == 'north' and z < cz + 1:
                continue
            if not valid_eye(x, z):
                continue
            eye = eye_at(x, z)
            visible = 0
            checks = []
            for tp in targets:
                dv = normalize((tp[0] - eye[0], tp[1] - eye[1], tp[2] - eye[2]))
                if dv is None:
                    continue
                hit = proxy_hit(PROXIES[sample['id']], eye, dv)
                occ = nearest_occluder(eye, dv)
                ok = hit is not None and (occ is None or occ[0] > hit - 0.001)
                checks.append({'target': [round(v, 2) for v in tp], 'proxy_t': None if hit is None else round(hit, 3),
                               'occluder': None if occ is None else [round(occ[0], 3), occ[1]], 'ok': ok})
                visible += 1 if ok else 0
            if visible < max(3, len(targets) - 1):
                continue
            score = d_flat - visible * 0.5
            if best is None or score < best[0]:
                best = (score, (x, z), visible, checks)
    return best


def pose_record(pid, label, mode, eye, look_at, sample_ids, validation, note=''):
    d = normalize((look_at[0] - eye[0], look_at[1] - eye[1], look_at[2] - eye[2]))
    yaw = math.degrees(math.atan2(d[0], d[2])) % 360.0 if d else None
    pitch = math.degrees(math.asin(max(-1.0, min(1.0, d[1])))) if d else None
    return {
        'id': pid, 'label': label, 'mode': mode,
        'eye_position_unity': [round(v, 4) for v in eye],
        'look_at_unity': [round(v, 4) for v in look_at],
        'yaw_deg': None if yaw is None else round(yaw, 2),
        'pitch_deg': None if pitch is None else round(pitch, 2),
        'expected_sample_ids': sample_ids,
        'validation': validation,
        'note': note,
    }


# P0: character eye follow (no fixed look; actual gaze decides)
SPAWN = load_json(os.path.join(WS, 'Assets', 'Resources', 'SovietBlueHour', 'scene_config.json'))['spawn']
POSES = [{
    'id': 'P0_character_eye',
    'label': '角色实际视线（跟随 EyeCamera，无手动偏移）',
    'mode': 'follow_eye',
    'eye_position_unity': None,
    'look_at_unity': None,
    'yaw_deg': None, 'pitch_deg': None,
    'expected_sample_ids': [],
    'validation': {'note': 'no assumption: if the current gaze does not see a sample, the result must be no_target'},
    'note': 'P0 must show no_target when the actual gaze misses; rotating the test view switches to test_view.',
}]

# P1: test view at spawn eye, looking at the two plaza benches
bench_eye = eye_at(-14.0, -62.0)
bench_targets = []
for sid in ('entity/prop_bench_plaza_1', 'entity/prop_bench_plaza_2'):
    bench_targets.append(BY_ID[sid]['center']['unity'])
p1_checks = []
for tp in bench_targets:
    dv = normalize((tp[0] - bench_eye[0], tp[1] - bench_eye[1], tp[2] - bench_eye[2]))
    sid = 'entity/prop_bench_plaza_1' if tp == bench_targets[0] else 'entity/prop_bench_plaza_2'
    hit = proxy_hit(PROXIES[sid], bench_eye, dv)
    occ = nearest_occluder(bench_eye, dv)
    p1_checks.append({'id': sid, 'proxy_t': None if hit is None else round(hit, 3),
                      'occluder': None if occ is None else [round(occ[0], 3), occ[1]],
                      'ok': hit is not None and (occ is None or occ[0] > hit - 0.001)})
# choose look_at = midpoint of the two bench centers' visible direction
look = [(bench_targets[0][i] + bench_targets[1][i]) / 2 for i in range(3)]
POSES.append(pose_record('P1_bench_test_view', '测试视角：出生眼位看向广场两椅', 'test_view',
                         bench_eye, look, ['entity/prop_bench_plaza_1', 'entity/prop_bench_plaza_2'],
                         {'bench_checks': p1_checks}, 'test-only view; not character gaze'))

# P2: bus test pose
bus = BY_ID['entity/veh_laz695_01']
bus_sample = next(s for s in SAMPLES if s['id'] == bus['id'])
bus_targets = make_targets_from_proxy(PROXIES[bus['id']], [(0.5, 1.2), (1.2, 2.2)], 4)
best = solve_pose_around(bus_sample, bus_targets, min_d=5.0, max_d=16.0, step=2.0)
if best is None:
    raise RuntimeError('no valid bus test pose found')
_, (bx, bz), vis, checks = best
bus_eye = eye_at(bx, bz)
POSES.append(pose_record('P2_bus_test_view', '测试视角：公交车旁', 'test_view',
                         bus_eye, bus['center']['unity'], ['entity/veh_laz695_01'],
                         {'visible_targets': vis, 'target_total': len(bus_targets), 'checks': checks},
                         'test-only view; solved for clear line of sight'))

# P3: arch test pose (south side of W_K3), lintel hit + gap pass
arch = BY_ID['entity/bld_w_k3']
arch_eye_candidates = []
lintel_box = next(b for b in COLL['boxes'] if b['id'] == 'W_K3_LINTEL')
lintel_targets = []
for y in (4.0, 5.5, 8.0, 11.0):
    lintel_targets.append([(lintel_box['min'][0] + lintel_box['max'][0]) / 2, y, lintel_box['min'][2] - 0.3])
gap_targets = [[-81.0, 1.2, -22.5], [-81.0, 2.0, -22.5]]
best_arch = None
for x in range(-94, -68, 2):
    for z in range(-54, -38, 2):
        if not valid_eye(x, z):
            continue
        eye = eye_at(x, z)
        d_flat = math.hypot(x - (-81.0), z - (-28.0))
        if d_flat < 12 or d_flat > 26:
            continue
        # prefer a moderate look-up pitch for the lintel target
        look_vec = normalize((lintel_targets[1][0] - eye[0], lintel_targets[1][1] - eye[1],
                              lintel_targets[1][2] - eye[2]))
        if look_vec is None:
            continue
        pitch = math.degrees(math.asin(max(-1.0, min(1.0, look_vec[1]))))
        lintel_ok = 0
        lintel_checks = []
        for tp in lintel_targets:
            dv = normalize((tp[0] - eye[0], tp[1] - eye[1], tp[2] - eye[2]))
            if dv is None:
                continue
            hit = proxy_hit(PROXIES[arch['id']], eye, dv)
            occ = nearest_occluder(eye, dv)
            ok = hit is not None and (occ is None or occ[0] > hit - 0.001)
            lintel_checks.append({'target': tp, 'proxy_t': None if hit is None else round(hit, 3),
                                  'occluder': None if occ is None else [round(occ[0], 3), occ[1]], 'ok': ok})
            lintel_ok += 1 if ok else 0
        if lintel_ok < 2:
            continue
        gap_checks = []
        gap_ok = 0
        for tp in gap_targets:
            dv = normalize((tp[0] - eye[0], tp[1] - eye[1], tp[2] - eye[2]))
            if dv is None:
                continue
            dist = math.sqrt(sum((tp[i] - eye[i]) ** 2 for i in range(3)))
            hit = proxy_hit(PROXIES[arch['id']], eye, dv)
            occ = nearest_occluder(eye, dv, tmax=dist)
            ok = (hit is None or hit > dist + 0.001) and (occ is None or occ[0] > dist - 0.001)
            gap_checks.append({'target': tp, 'proxy_t': None if hit is None else round(hit, 3),
                               'occluder': None if occ is None else [round(occ[0], 3), occ[1]],
                               'target_distance': round(dist, 3), 'ok': ok})
            gap_ok += 1 if ok else 0
        if gap_ok < 2:
            continue
        score = d_flat + max(0.0, pitch - 30.0) * 0.8
        if best_arch is None or score < best_arch[0]:
            best_arch = (score, (x, z), lintel_ok, gap_ok, lintel_checks, gap_checks)
if best_arch is None:
    raise RuntimeError('no valid arch test pose found')
_, (ax, az), lintel_ok, gap_ok, lintel_checks, gap_checks = best_arch
arch_eye = eye_at(ax, az)
POSES.append(pose_record('P3_arch_test_view', '测试视角：过关楼南侧（门洞/门楣）', 'test_view',
                         arch_eye, lintel_targets[1], ['entity/bld_w_k3'],
                         {'lintel_ok': lintel_ok, 'gap_ok': gap_ok,
                          'lintel_checks': lintel_checks, 'gap_checks': gap_checks},
                         'test-only view; aim at lintel selects building; aim through gap passes'))

# ---------------------------------------------------------------------------
# 3b) machine-usable acceptance targets (exact ray cases, part labels)
# ---------------------------------------------------------------------------
POSE_BY_ID = {p['id']: p for p in POSES}


def tri_centroid(proxy, ti):
    idx = proxy['tris'][ti * 3:ti * 3 + 3]
    pts = [proxy['verts'][i * 3:i * 3 + 3] for i in idx]
    return [sum(p[k] for p in pts) / 3.0 for k in range(3)]


def tri_part(proxy, ti):
    return proxy['parts'][proxy['tri_parts'][ti]]


_target_counter = [0]


def add_target(pose, **kw):
    _target_counter[0] += 1
    kw.setdefault('id', '%s::%s::%02d' % (pose['id'], kw.get('kind', '?'), _target_counter[0]))
    pose.setdefault('targets', []).append(kw)


def cast_from(eye, point, proxy):
    d = normalize((point[0] - eye[0], point[1] - eye[1], point[2] - eye[2]))
    if d is None:
        return None, None, None
    hit = proxy_hit_ex(proxy, eye, d)
    occ = nearest_occluder(eye, d)
    return d, hit, occ


def nearest_all(eye, d):
    """Nearest real triangle across all four samples: (t, sample_id, tri_index)."""
    best = None
    for sid, proxy in PROXIES.items():
        r = proxy_hit_ex(proxy, eye, d)
        if r is not None and (best is None or r[0] < best[0]):
            best = (r[0], sid, r[1])
    return best


def surface_point(eye, d, t):
    return [round(eye[i] + d[i] * t, 6) for i in range(3)]


def no_hit_before(eye, point, maxd):
    d = normalize((point[0] - eye[0], point[1] - eye[1], point[2] - eye[2]))
    if d is None:
        return None
    h = nearest_all(eye, d)
    if h is not None and h[0] <= maxd + 0.02:
        return None
    return d


# P1: benches + an empty point (nearest real hit must be the named bench)
p1 = POSE_BY_ID['P1_bench_test_view']
eye1 = p1['eye_position_unity']
for sid in ('entity/prop_bench_plaza_1', 'entity/prop_bench_plaza_2'):
    target = BY_ID[sid]['center']['unity']
    d = normalize((target[0] - eye1[0], target[1] - eye1[1], target[2] - eye1[2]))
    h = nearest_all(eye1, d) if d else None
    if h is None or h[1] != sid:
        continue
    occ = nearest_occluder(eye1, d)
    if occ is not None and occ[0] < h[0] - 0.001:
        continue
    add_target(p1, kind='bench', sample_id=sid, direction=[round(v, 9) for v in d],
               point=surface_point(eye1, d, h[0]), expected_status='hit',
               note='P1 fixed spawn eye; global nearest real hit is this bench')
bench2 = BY_ID['entity/prop_bench_plaza_2']['center']['unity']
empty_pt = [bench2[0], bench2[1] + 2.0, bench2[2]]
dist_empty = math.sqrt(sum((empty_pt[i] - eye1[i]) ** 2 for i in range(3)))
d = no_hit_before(eye1, empty_pt, dist_empty)
if d is not None:
    add_target(p1, kind='empty', sample_id=None, direction=[round(v, 9) for v in d],
               point=[round(v, 6) for v in empty_pt], expected_status='no_target',
               note='above the bench: no real triangle before the point')

# P2: bus wheel / window / body targets, bbox gap and outside
p2 = POSE_BY_ID['P2_bus_test_view']
eye2 = p2['eye_position_unity']
bus_proxy = PROXIES['entity/veh_laz695_01']
bus_id = 'entity/veh_laz695_01'


def bus_category(label):
    u = label.upper()
    if 'TIRE' in u or 'HUB' in u:
        return 'wheel'
    if 'GLASS' in u:
        return 'window'
    if 'BUS_CREAM' in u or 'PAINT' in u:
        return 'body'
    return None


bus_counts = {'wheel': 0, 'window': 0, 'body': 0}
for ti in range(len(bus_proxy['tris']) // 3):
    label = tri_part(bus_proxy, ti)
    cat = bus_category(label)
    if cat is None or bus_counts[cat] >= 2:
        continue
    c = tri_centroid(bus_proxy, ti)
    d = normalize((c[0] - eye2[0], c[1] - eye2[1], c[2] - eye2[2]))
    h = nearest_all(eye2, d) if d else None
    if h is None or h[1] != bus_id or h[2] != ti:
        continue
    occ = nearest_occluder(eye2, d)
    if occ is not None and occ[0] < h[0] - 0.001:
        continue
    if h[0] > 35:
        continue
    add_target(p2, kind='bus_part', sample_id=bus_id, part_class=cat,
               part_label=label, direction=[round(v, 9) for v in d],
               point=surface_point(eye2, d, h[0]), expected_status='hit',
               note='global nearest real hit triangle belongs to ' + cat)
    bus_counts[cat] += 1
    if all(v >= 2 for v in bus_counts.values()):
        break

bus_bb = BY_ID[bus_id]['bbox']['world_aabb_unity']
gap_found = 0
for fx in (0.25, 0.5, 0.75):
    for fy in (0.25, 0.5, 0.75):
        for fz in (0.25, 0.5, 0.75):
            pt = [bus_bb[0] + (bus_bb[3] - bus_bb[0]) * fx,
                  bus_bb[1] + (bus_bb[4] - bus_bb[1]) * fy,
                  bus_bb[2] + (bus_bb[5] - bus_bb[2]) * fz]
            dist = math.sqrt(sum((pt[i] - eye2[i]) ** 2 for i in range(3)))
            d = no_hit_before(eye2, pt, dist)
            if d is not None:
                add_target(p2, kind='bbox_gap', sample_id=None, direction=[round(v, 9) for v in d],
                           point=[round(v, 6) for v in pt], expected_status='no_target',
                           note='sample bbox is crossed by the ray but no triangle is')
                gap_found += 1
                break
        if gap_found >= 2:
            break
    if gap_found >= 2:
        break
if gap_found == 0:
    outside_pt = [bus_bb[0] + 0.5 * (bus_bb[3] - bus_bb[0]), bus_bb[4] + 1.5,
                  bus_bb[2] + 0.5 * (bus_bb[5] - bus_bb[2])]
    dist = math.sqrt(sum((outside_pt[i] - eye2[i]) ** 2 for i in range(3)))
    d = no_hit_before(eye2, outside_pt, dist)
    if d is not None:
        add_target(p2, kind='outside', sample_id=None, direction=[round(v, 9) for v in d],
                   point=[round(v, 6) for v in outside_pt], expected_status='no_target',
                   note='outside the bus silhouette')

# P3: lintel real triangles + gap pass
p3 = POSE_BY_ID['P3_arch_test_view']
eye3 = p3['eye_position_unity']
arch_proxy = PROXIES['entity/bld_w_k3']
arch_id = 'entity/bld_w_k3'
lintel_added = 0
for ti in range(len(arch_proxy['tris']) // 3):
    c = tri_centroid(arch_proxy, ti)
    if not (lintel_box['min'][0] <= c[0] <= lintel_box['max'][0] and
            lintel_box['min'][1] <= c[1] <= lintel_box['max'][1] and
            lintel_box['min'][2] <= c[2] <= lintel_box['max'][2]):
        continue
    d = normalize((c[0] - eye3[0], c[1] - eye3[1], c[2] - eye3[2]))
    h = nearest_all(eye3, d) if d else None
    if h is None or h[1] != arch_id or h[2] != ti:
        continue
    occ = nearest_occluder(eye3, d)
    if occ is not None and occ[0] < h[0] - 0.001:
        continue
    add_target(p3, kind='lintel', sample_id=arch_id, direction=[round(v, 9) for v in d],
               point=surface_point(eye3, d, h[0]), expected_status='hit',
               note='first real hit triangle is inside W_K3_LINTEL box')
    lintel_added += 1
    if lintel_added >= 3:
        break
for tp in gap_targets:
    d = normalize((tp[0] - eye3[0], tp[1] - eye3[1], tp[2] - eye3[2]))
    if d is None:
        continue
    dist = math.sqrt(sum((tp[i] - eye3[i]) ** 2 for i in range(3)))
    h = nearest_all(eye3, d)
    occ = nearest_occluder(eye3, d, tmax=dist)
    if (h is not None and h[0] <= dist + 0.001) or (occ is not None and occ[0] <= dist - 0.001):
        continue
    add_target(p3, kind='gap', sample_id=None, direction=[round(v, 9) for v in d],
               point=[round(v, 6) for v in tp], expected_status='no_target',
               note='ray passes through the W_K3 doorway under the lintel')

# bbox-hit but triangle-miss case (search all pose eyes/samples)
bbox_gap_found = 0
for pose in POSES:
    eye = pose.get('eye_position_unity')
    if not eye:
        continue
    for sample in SAMPLES:
        if bbox_gap_found >= 2:
            break
        bb = sample['bbox_unity']
        for fx in (0.2, 0.35, 0.5, 0.65, 0.8):
            for fy in (0.2, 0.4, 0.6, 0.8):
                for fz in (0.2, 0.35, 0.5, 0.65, 0.8):
                    pt = [bb[0] + (bb[3] - bb[0]) * fx,
                          bb[1] + (bb[4] - bb[1]) * fy,
                          bb[2] + (bb[5] - bb[2]) * fz]
                    dist = math.sqrt(sum((pt[i] - eye[i]) ** 2 for i in range(3)))
                    d = no_hit_before(eye, pt, dist)
                    if d is None:
                        continue
                    add_target(pose, kind='bbox_gap', sample_id=sample['id'],
                               direction=[round(v, 9) for v in d],
                               point=[round(v, 6) for v in pt], expected_status='no_target',
                               note='sample AABB is crossed by this ray but no triangle is hit')
                    bbox_gap_found += 1
                    break
                if bbox_gap_found >= 2:
                    break
            if bbox_gap_found >= 2:
                break

# real foreground occlusion: global nearest sample must sit behind the occluder
blocked_added = 0
for pose in POSES:
    eye = pose.get('eye_position_unity')
    if not eye:
        continue
    for sample in SAMPLES:
        proxy = PROXIES[sample['id']]
        tri_count = len(proxy['tris']) // 3
        step = max(1, tri_count // 300)
        for ti in range(0, tri_count, step):
            c = tri_centroid(proxy, ti)
            d = normalize((c[0] - eye[0], c[1] - eye[1], c[2] - eye[2]))
            if d is None:
                continue
            h = nearest_all(eye, d)
            if h is None:
                continue
            occ = nearest_occluder(eye, d)
            if occ is None or occ[0] >= h[0] - 0.001:
                continue
            add_target(pose, kind='blocked', sample_id=h[1],
                       part_label=tri_part(PROXIES[h[1]], h[2]),
                       occluder_id=occ[1], occluder_method=occ[2],
                       direction=[round(v, 9) for v in d],
                       point=surface_point(eye, d, h[0]), expected_status='blocked',
                       note='global nearest sample is behind a nearer occluder')
            blocked_added += 1
            break
        if blocked_added >= 3:
            break
    if blocked_added >= 3:
        break

# attach a deterministic max distance to every acceptance ray
for pose in POSES:
    eye = pose.get('eye_position_unity')
    if not eye:
        continue
    for t in pose.get('targets', []):
        if t.get('direction') is None or t.get('point') is None:
            continue
        dist = math.sqrt(sum((t['point'][i] - eye[i]) ** 2 for i in range(3)))
        t['max_distance'] = round(dist + (0.5 if t.get('expected_status') == 'hit' else 0.02), 4)

sample_hash = {s['id']: s['proxy']['sha256'] for s in SAMPLES}
manifest = {
    'schema': 'sbh.stage2.pick.v1',
    'data_version': '0.1.2',
    'generated_by': 'Maps/SovietBlueHour/tools/export_stage2_pick_data.py',
    'generated_at': __import__('datetime').datetime.now().astimezone().isoformat(timespec='seconds'),
    'coordinate_contract': {
        'world': 'Unity: +Y up, +Z north, +X east; blender=(x,y,z) -> unity=(x,z,y)',
        'proxy': 'proxy_*.bytes = little-endian float32 xyz positions, then int32 triangle indices (3 per triangle), '
                 'then int32 part id per triangle; counts/parts in proxy meta',
        'ray': 'world-space normalized direction; camera snapshot supplies origin',
    },
    'map': {
        'id': 'SovietBlueHour', 'version': '2026-11-02',
        'blend_sha256': sha_file(bpy.data.filepath),
        'blend_size': os.path.getsize(bpy.data.filepath),
        'entities_json_sha256': sha_file(ENT_JSON),
        'collision_json_sha256': sha_file(COLLISION_JSON),
        'ground_height_json_sha256': sha_file(GROUND_JSON),
        'plan_json_sha256': sha_file(PLAN_JSON),
    },
    'samples': SAMPLES,
    'occluders': OCCLUDERS,
    'occluder_rules': {
        'excluded_sample_collision_ids': sorted(BL_W_K3_COLLISION_IDS),
        'excluded_sample_entity_ids': SAMPLE_IDS,
        'excluded_classes': sorted(NO_OCCLUDER_CLASSES),
        'max_horizontal_extent_m': OCCLUDER_MAX_HORIZONTAL,
        'note': '20m is a prototype heuristic for merged scatter objects, not a universal correctness rule; '
                'every pick records occluder_id + method; false blocks are refined by occluder id.',
    },
    'poses': POSES,
    'p0': {'mode': 'follow_eye', 'spawn_root_unity': [SPAWN['x'], SPAWN['y'], SPAWN['z']],
           'note': 'actual EyeCamera pose at runtime; P0 has no fixed look direction and no visibility assumption'},
    'summary': {
        'sample_count': len(SAMPLES),
        'proxy_tris_total': sum(s['proxy']['tris'] for s in SAMPLES),
        'occluder_count': len(OCCLUDERS),
        'entity_occluders': entity_occluders,
        'collision_occluders': len(OCCLUDERS) - entity_occluders,
        'pose_count': len(POSES),
    },
}
target_kinds = {}
for p in POSES:
    for t in p.get('targets', []):
        target_kinds[t['kind']] = target_kinds.get(t['kind'], 0) + 1
    if p.get('targets'):
        print('TARGETS', p['id'], {k: sum(1 for t in p['targets'] if t['kind'] == k) for k in
                                   sorted({t['kind'] for t in p['targets']})})
manifest['summary']['acceptance_targets'] = target_kinds
manifest['summary']['proxy_bytes'] = sum(s['proxy']['bytes'] for s in SAMPLES)

runtime_poses = []
for p in POSES:
    runtime_poses.append({
        'id': p['id'], 'label': p.get('label', ''), 'mode': p['mode'],
        'eye_position_unity': p.get('eye_position_unity'),
        'look_at_unity': p.get('look_at_unity'),
        'yaw_deg': p.get('yaw_deg'), 'pitch_deg': p.get('pitch_deg'),
        'targets': p.get('targets', []),
        'note': p.get('note', ''),
    })
runtime_samples = []
for s in SAMPLES:
    runtime_samples.append({
        'id': s['id'], 'entity_class': s['class'], 'name_zh': s['name_zh'],
        'center_unity': s['center_unity'], 'bbox_unity': s['bbox_unity'],
        'proxy': s['proxy'],
    })
runtime = {
    'schema': 'sbh.stage2.pick.runtime.v1',
    'data_version': manifest['data_version'],
    'map': manifest['map'],
    'samples': runtime_samples,
    'occluders': OCCLUDERS,
    'occluder_rules': manifest['occluder_rules'],
    'poses': runtime_poses,
    'p0': manifest['p0'],
    'summary': manifest['summary'],
}
runtime_path = os.path.join(PACK_DIR, 'runtime_manifest.json')
with open(runtime_path, 'w', encoding='utf-8') as f:
    json.dump(runtime, f, ensure_ascii=False, indent=1)
manifest['runtime_manifest'] = {
    'file': 'runtime_manifest.json',
    'bytes': os.path.getsize(runtime_path),
    'sha256': sha_file(runtime_path),
}
with open(os.path.join(PACK_DIR, 'manifest.json'), 'w', encoding='utf-8') as f:
    json.dump(manifest, f, ensure_ascii=False, indent=1)

report = {
    'schema': 'sbh.stage2.step1.report.v1',
    'manifest': os.path.relpath(os.path.join(PACK_DIR, 'manifest.json'), WS).replace('\\', '/'),
    'manifest_sha256': sha_file(os.path.join(PACK_DIR, 'manifest.json')),
    'samples': SAMPLES,
    'occluders': manifest['summary'],
    'poses': POSES,
    'notes': [
        'sample own entity AABBs and collision boxes W_K3_W/E/LINTEL are excluded from occluders',
        'other buildings keep raw segmented collision boxes including arch_lintel',
        'proxy binary = float32 verts + int32 tri indices + int32 per-triangle part ids; bus part labels are material names',
        'test poses solved by this script; P1 is the fixed spawn eye (validated, not solved)',
        'bbox_error_m is a 4-decimal source-space check; decoded float32 positions differ by ~1e-6 m',
        'no .blend save, no scene/prefab/runtime changes',
    ],
}
with open(REPORT_JSON, 'w', encoding='utf-8') as f:
    json.dump(report, f, ensure_ascii=False, indent=1)

lines = ['# Stage 2 step 1 precheck', '',
         '| sample | class | tris | bbox error m | proxy sha256 |', '|---|---|---:|---:|---|']
for s in SAMPLES:
    lines.append('| %s | %s | %d | %.6f | `%s` |' % (s['id'], s['class'], s['proxy']['tris'],
                                                     s['proxy']['bbox_error_m'], s['proxy']['sha256'][:16]))
lines += ['', 'Occluders: %d (collision %d / entity %d), samples excluded from occluders.' %
          (manifest['summary']['occluder_count'], manifest['summary']['collision_occluders'],
           manifest['summary']['entity_occluders'])]
lines += ['', 'Acceptance targets: %s' % json.dumps(manifest['summary']['acceptance_targets'], ensure_ascii=False)]
lines += ['', 'Runtime manifest: `%s` sha256=`%s`' % (manifest['runtime_manifest']['file'],
                                                       manifest['runtime_manifest']['sha256'][:16])]
lines += ['', '## test poses', '', '| pose | mode | eye (Unity) | look at | validation | targets |',
          '|---|---|---|---|---|---|']
for p in POSES:
    v = p.get('validation', {})
    brief = '; '.join('%s=%s' % (k, v[k]) for k in ('visible_targets', 'lintel_ok', 'gap_ok') if k in v)
    lines.append('| %s | %s | %s | %s | %s | %d |' % (p['id'], p['mode'],
                p.get('eye_position_unity'), p.get('look_at_unity'), brief or 'n/a', len(p.get('targets', []))))
lines += ['', 'Manifest: `%s`' % report['manifest']]
lines += ['', '## 说明', '',
          '- `bbox error m` 列是导出前四位小数源空间检查差值；解码 float32 后约 1.5e-6m 级差，不是绝对零误差。',
          '- 代理二进制布局：float32 顶点 xyz → int32 三角形索引 → int32 每三角形 part id；part 名见 proxy.parts。',
          '- P1 是固定出生眼位验证，不是搜索求解；中心准星不必命中，验收逐椅瞄准。',
          '- 遮挡比较统一使用 1mm 容差；样本自身 AABB 与 W_K3_W/E/LINTEL 排除。',
          '- 本次导出未保存 .blend；未修改场景/Prefab/现有脚本。']
with open(REPORT_MD, 'w', encoding='utf-8') as f:
    f.write('\n'.join(lines) + '\n')

print('STAGE2_STEP1_DONE samples=%d occluders=%d poses=%d pack=%s' %
      (len(SAMPLES), len(OCCLUDERS), len(POSES), PACK_DIR))
