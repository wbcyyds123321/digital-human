"""export_entities.py -- Stage 0-1 read-only entity inventory exporter.

Run (does NOT modify the .blend):
  blender --background "Maps/SovietBlueHour/map/SovietBlueHour_beauty.blend" \
          --python "Maps/SovietBlueHour/tools/export_entities.py" -- \
          --workspace "D:/unity programe/Become human"

Outputs (default <workspace>/Maps/SovietBlueHour/entities/):
  world_entities.v0.json      logical entity inventory
  source_object_map.csv       every source mesh -> entity / exclusion / unclassified
  grouping_report.md          human-readable grouping + validation report
  extraction_stats.json       machine-readable counts
  orientation_validation.json bench/vehicle orientation validation evidence

Coordinate contract:
  Blender: Z-up, +Y = north, +X = east.
  Unity  : Y-up, unity = (blender.x, blender.z, blender.y); +Z = north, +X = east.
  Yaw    : unity_yaw_deg = degrees(atan2(front_x_blender, front_y_blender))
           => 0 = north, 90 = east, 180 = south, 270 = west.
           Bench generator facing f (props.py:1897, facing 0 = looks south -Y)
           maps to unity_yaw = (180 - degrees(f)) mod 360; checked in validation.
"""
import bpy
import csv
import hashlib
import json
import math
import os
import re
import sys
import datetime

import numpy as np
from mathutils import Vector

# ---------------------------------------------------------------------------
# paths / args
# ---------------------------------------------------------------------------
def _args():
    argv = sys.argv
    if '--' in argv:
        argv = argv[argv.index('--') + 1:]
    else:
        argv = []
    out = {'workspace': None, 'out': None, 'previous': None}
    i = 0
    while i < len(argv):
        if argv[i] == '--workspace' and i + 1 < len(argv):
            out['workspace'] = argv[i + 1].replace('\\', '/')
            i += 2
        elif argv[i] == '--out' and i + 1 < len(argv):
            out['out'] = argv[i + 1].replace('\\', '/')
            i += 2
        elif argv[i] == '--previous' and i + 1 < len(argv):
            out['previous'] = argv[i + 1].replace('\\', '/')
            i += 2
        else:
            i += 1
    return out

A = _args()
WS = A['workspace'] or os.path.abspath(os.path.join(os.path.dirname(__file__), '..', '..', '..'))
MAP_DIR = os.path.join(WS, 'Maps', 'SovietBlueHour')
OUT_DIR = A['out'] or os.path.join(MAP_DIR, 'entities')
os.makedirs(OUT_DIR, exist_ok=True)

BLEND = bpy.data.filepath
if not BLEND:
    raise SystemExit('open the beauty .blend before running this script')

plan_path = os.path.join(MAP_DIR, 'tools', 'plan.json')
manifest_path = os.path.join(MAP_DIR, 'map', 'scene_manifest.json')
nav_path = os.path.join(MAP_DIR, 'map', 'nav_graph.json')


def load_json(p):
    with open(p, encoding='utf-8') as f:
        return json.load(f)


def sha256(p):
    h = hashlib.sha256()
    with open(p, 'rb') as f:
        for block in iter(lambda: f.read(1 << 20), b''):
            h.update(block)
    return h.hexdigest()


input_paths = {'blend': BLEND, 'plan': plan_path, 'manifest': manifest_path, 'nav': nav_path}
input_hashes_before = {name: sha256(path) for name, path in input_paths.items()}
plan = load_json(plan_path)
manifest = load_json(manifest_path)
nav = load_json(nav_path)
BLOCKS = {b['id']: b for b in plan['blocks']}
BLOCK_IDS = sorted(BLOCKS, key=len, reverse=True)
MANIFEST = {m['id']: m for m in manifest}
LANDMARKS = {l['id']: l for l in plan.get('landmarks', [])}

# ---------------------------------------------------------------------------
# coordinate helpers
# ---------------------------------------------------------------------------
def u(bx, by, bz):
    """Blender (x, y, z) -> Unity (x, y, z)."""
    return [round(bx, 4), round(bz, 4), round(by, 4)]


def bbox_u(bmin, bmax):
    """Blender AABB -> Unity AABB [minx, miny, minz, maxx, maxy, maxz]."""
    return [round(bmin[0], 4), round(bmin[2], 4), round(bmin[1], 4),
            round(bmax[0], 4), round(bmax[2], 4), round(bmax[1], 4)]


def yaw_from_front(fx, fy):
    """front vector in Blender XY (x=east, y=north) -> Unity yaw degrees in [0,360)."""
    v = math.degrees(math.atan2(fx, fy)) % 360.0
    if v >= 359.995:
        v = 0.0
    return round(v, 2)


FACING8 = ['北', '东北', '东', '东南', '南', '西南', '西', '西北']


def facing_zh(yaw):
    if yaw is None:
        return None
    return FACING8[int(((yaw + 22.5) % 360.0) // 45.0) % 8]


FACE_MAP = {'PX': '东', 'NX': '西', 'PY': '北', 'NY': '南'}

WEIGHT = {'KHRUSH': 1, 'PANEL': 1, 'BRICK': 1}

# ---------------------------------------------------------------------------
# name / class rules
# ---------------------------------------------------------------------------
PART_SUFFIX = re.compile(
    r'(?:_(?:base|globe|hood|pole|body|post|rim|lid|markings|txt|light|glass|door|frame|'
    r'seat|back|arm|head|lamp|cap|slab|shadow|glow|canvas|cloth|top|bottom|side|panel|roof|'
    r'leg|legs|wheel(?:[-\d]*)|tyre|hub|mirror|bumper|plate|grill|grille|window|windows|'
    r'dump\d+|bin|enclosure|pillar|pillarcap|post[-\d]*|face[b]?|lampbar|p\d+|back|'
    r'bench\d+|foot|feet|rail|rope|wire|bracket|plate[-\d]*))+$', re.I)

# multi-part families: (compiled regex, key builder)
GROUP_RULES = [
    (re.compile(r'^(PROP_BENCH_PLAZA_\d+)_(?:back\d+|leg[-\d]*|seat\d+)$', re.I), lambda m: m.group(1)),
    (re.compile(r'^(PROP_LAMP_.+?)_(?:base|globe|hood|pole|arm|head|box)$', re.I), lambda m: m.group(1)),
    (re.compile(r'^(PROP_BIN_\d+)_(?:body|post|rim)$', re.I), lambda m: m.group(1)),
    (re.compile(r'^(PROP_(?:PARK|TERMINAL)_BIN_[^_]+)_(?:body|post|rim)$', re.I), lambda m: m.group(1)),
    (re.compile(r'^(PROP_BUSSTOP_BUS_STOP(?:_2)?)(?:_BIN(?:_body|_post|_rim)?|_SHELTER|_SIGN)$', re.I), lambda m: m.group(1)),
    (re.compile(r'^(PROP_BILLBOARD_\d+)_(?:face|faceb|lampbar|panel|post.*)$', re.I), lambda m: m.group(1)),
    (re.compile(r'^(PROP_POSTER_BOARD_\d+?)_(?:back|p\d+|post.*)$', re.I), lambda m: m.group(1)),
    (re.compile(r'^(PROP_POSTERS_.+?)_(?:back|p\d+|post.*)$', re.I), lambda m: m.group(1)),
    (re.compile(r'^(PROP_DUMPS_.+?_dump\d+)_(?:body|lid|wheel.*)$', re.I), lambda m: m.group(1)),
    (re.compile(r'^(PROP_DUMPS_.+?)_bin_(?:body|post|rim)$', re.I), lambda m: m.group(1) + '_bin'),
    (re.compile(r'^(PROP_PARK_BOAT)(?:_ROPE_\d+)?$', re.I), lambda m: m.group(1)),
    (re.compile(r'^(PROP_PAYPHONE_\d+)_sign$', re.I), lambda m: m.group(1)),
    (re.compile(r'^(PROP_TRANSFORMER_\d+)_sign$', re.I), lambda m: m.group(1)),
    (re.compile(r'^(PROP_PAVILION)(?:\.\d+|_LANTERN)$', re.I), lambda m: m.group(1)),
    (re.compile(r'^(PROP_BANNER)(?:_CLOTH|_TXT_[EW])$', re.I), lambda m: m.group(1)),
    (re.compile(r'^(PROP_FENCE_.+?)_(?:pillar\w*|\d+_\d+|[hv]\d+)$', re.I), lambda m: m.group(1)),
    (re.compile(r'^(FLAG)_(?:base|cloth|pole)(\d+)$', re.I), lambda m: 'FLAG_' + m.group(2)),
    (re.compile(r'^(TRAMSHELTER)_(?:bench\d+|post_.*|roof)$', re.I), lambda m: m.group(1)),
]

FAMILIES = [
    ('PROP_BENCH', 'bench', '长椅', ['长椅', '长凳', '椅子', 'bench']),
    ('PROP_PARK_BENCH', 'bench', '长椅', ['长椅', '长凳', '公园长椅', 'bench']),
    ('PROP_PAVILION_BENCH', 'bench', '长椅', ['长椅', '凉亭长椅', 'bench']),
    ('PROP_TERMINAL_BENCH', 'bench', '长椅', ['长椅', '候车长椅', 'bench']),
    ('PROP_FOREST_BENCH', 'bench', '长椅', ['长椅', '林间长椅', 'bench']),
    ('PROP_TREE', 'tree', '树', ['树', '树木', 'tree']),
    ('PROP_LAMP', 'street_lamp', '路灯', ['路灯', '街灯', 'lamp']),
    ('PROP_FENCE', 'fence', '围栏', ['围栏', '铁栏', 'fence']),
    ('PROP_BIN', 'trash_bin', '垃圾桶', ['垃圾桶', '垃圾箱', 'bin']),
    ('PROP_PARK_BIN', 'trash_bin', '垃圾桶', ['垃圾桶', 'bin']),
    ('PROP_TERMINAL_BIN', 'trash_bin', '垃圾桶', ['垃圾桶', 'bin']),
    ('PROP_DUMPS', 'dumpster', '垃圾箱', ['垃圾箱', '大垃圾桶', 'dumpster']),
    ('PROP_BILLBOARD', 'billboard', '广告牌', ['广告牌', '招牌', 'billboard']),
    ('PROP_POSTERS', 'poster_board', '海报板', ['海报板', '海报', 'poster']),
    ('PROP_POSTER_BOARD', 'poster_board', '海报板', ['海报板', '海报', 'poster']),
    ('PROP_BUSSTOP', 'bus_stop', '公交站', ['公交站', '候车亭', '公交站牌', 'bus stop']),
    ('PROP_SIGN', 'sign', '指示牌', ['指示牌', '标示牌', '路牌', 'sign']),
    ('PROP_PLAY', 'playground', '游乐设施', ['游乐设施', '儿童游乐', 'playground']),
    ('PROP_PLANTER', 'planter', '花坛', ['花坛', '花箱', 'planter']),
    ('PROP_HEDGE', 'hedge', '绿篱', ['绿篱', '灌木', 'hedge']),
    ('PROP_WEEDS', 'weeds', '杂草', ['杂草', 'weed']),
    ('PROP_PAVILION', 'pavilion', '凉亭', ['凉亭', '亭子', 'pavilion']),
    ('PROP_LAUNDRY', 'laundry_frame', '晾衣架', ['晾衣架', '晒衣架', 'laundry']),
    ('PROP_METRO', 'metro_prop', '地铁附属', ['地铁', 'metro']),
    ('PROP_REEDS', 'reeds', '芦苇', ['芦苇', 'reeds']),
    ('PROP_UTIL', 'utility_poles', '电线杆组', ['电线杆', 'utility pole']),
    ('PROP_ETERNAL_FLAME', 'eternal_flame', '长明火', ['长明火', '纪念碑', 'eternal flame']),
    ('PROP_STATUE_PLAQUE', 'plaque', '纪念牌', ['纪念牌', '铭牌', 'plaque']),
    ('PROP_FOREST_LOG', 'fallen_log', '倒木', ['倒木', '树干', 'log']),
    ('PROP_FOREST_STUMPS', 'stumps_bollards', '树桩路桩', ['树桩', '路桩']),
    ('PROP_FOREST_TRAILS', 'forest_trail', '林间小径', ['小径', 'trail']),
    ('PROP_CAR', 'vehicle', '汽车', ['汽车', '小汽车', '轿车', 'car']),
    ('PROP_BANNER', 'banner', '横幅', ['横幅', '标语', 'banner']),
    ('PROP_LITTER', 'litter', '地面杂物', ['杂物', '垃圾', 'litter']),
    ('PROP_TRANSFORMER', 'transformer_box', '变压器箱', ['变压器', '配电箱', 'transformer']),
    ('PROP_PAYPHONE', 'payphone', '公用电话', ['公用电话', '电话亭', 'payphone']),
    ('PROP_FLAG_POLE', 'flagpole', '旗杆', ['旗杆', 'flagpole']),
    ('PROP_BIKE_RACK', 'bike_rack', '自行车架', ['自行车架', '单车架', 'bike rack']),
    ('PROP_GUARD', 'guard_box', '岗亭设施', ['岗亭', '门卫', 'guard']),
    ('PROP_MAILBOX', 'mailbox', '邮箱', ['邮箱', '邮筒', 'mailbox']),
    ('PROP_HYDRANT', 'hydrant', '消防栓', ['消防栓', 'hydrant']),
    ('PROP_NOTICE', 'notice_board', '告示板', ['告示板', '公告板', 'notice']),
    ('PROP_STAND', 'notice_board', '公告板', ['公告板', '告示板', '海报板', 'notice board']),
    ('PROP_TAXI', 'taxi_stand', '出租车候车点', ['出租车', '候车点', 'taxi']),
    ('PROP_LAZ_BUS', 'bus_sign', '公交线路牌', ['公交牌', '线路牌', 'bus sign']),
    ('PROP_LEAVES', 'leaf_pile', '落叶堆', ['落叶', 'leaves']),
    ('PROP_EAST_Q', 'ground_marking', '地面标线', ['标线', '地标', 'marking']),
    ('PROP_MANHOLES', 'manhole', '井盖', ['井盖', 'manhole']),
    ('PROP_PLAQUE', 'plaque', '铭牌', ['铭牌', 'plaques', 'plaque']),
    ('PROP_PUDDLES', 'puddle', '水洼', ['水洼', '积水', 'puddle']),
    ('PROP_WET', 'wet_patch', '湿痕', ['湿痕', 'wet patch']),
    ('PROP_PARK', 'park_prop', '公园小品', ['公园设施', 'park prop']),
    ('PROP_TERMINAL', 'terminal_prop', '车站设施', ['车站设施', 'terminal prop']),
    ('FLAG', 'flag', '旗帜', ['旗帜', '旗', 'flag']),
    ('TRAMSHELTER', 'tram_shelter', '有轨电车站台', ['电车站台', '站台雨棚', 'tram shelter']),
]

VEHICLE_CLASS = {
    'vaz2101': ('VAZ-2101 轿车', ['轿车', '小汽车', '日古利', 'vaz2101']),
    'gaz24': ('嘎斯-24 伏尔加', ['轿车', '伏尔加', 'gaz24']),
    'moskvich412': ('莫斯科人-412', ['轿车', '莫斯科人', 'moskvich412']),
    'uaz469': ('UAZ-469 越野车', ['越野车', 'uaz469']),
    'zil130': ('ZIL-130 卡车', ['卡车', '货车', 'zil130']),
    'laz695': ('LAZ-695 公交车', ['公交车', '巴士', 'laz695']),
}
TRAM_NAME = ('T3 有轨电车', ['有轨电车', '电车', 'tram'])

# generator-facing expectations (validation only; never used as the default rule)
BENCH_SPOTS = [
    (-92.0, -36.2, 0.0), (-79.0, -36.2, 0.0), (-92.0, -50.4, math.pi), (-79.0, -50.4, math.pi),
    (-57.0, -41.5, math.pi / 2), (-57.0, -45.0, math.pi / 2), (92.0, -36.2, 0.0),
    (79.0, -36.2, 0.0), (92.0, -50.4, math.pi), (79.0, -50.4, math.pi),
    (57.0, -41.5, -math.pi / 2), (57.0, -45.0, -math.pi / 2), (-18.0, -8.6, 0.0),
    (-6.0, -8.6, 0.0), (6.0, -8.6, 0.0), (18.0, -8.6, 0.0), (-29.0, 41.4, math.pi),
    (-12.5, 41.4, math.pi), (-31.0, 27.4, 0.0), (-10.0, 27.4, 0.0),
    (-46.0, -43.0, math.pi / 2), (46.0, -43.0, -math.pi / 2),
]
FOREST_BENCH_FACING = [math.pi / 2, -math.pi / 2, math.pi / 2]
# tag -> blender yaw degrees (vehicles.py placement tables); validation only
VEHICLE_YAW = {
    'W0': 180, 'W1': 0, 'W2': 180, 'W3': 180, 'W4': 0,
    'E0': 0, 'E1': 0, 'E2': 180, 'E3': 0, 'E4': 180,
    'S0': 180, 'S1': 0, 'S2': 180, 'D0': 0, 'D1': 180, 'D2': 0,
    'G0': 90, 'G1': 90, 'G2': 90,
    'WCOURT1': 90, 'WCOURT2': -90, 'ECOURT1': -90, 'ECOURT2': 90,
    'KERB1': 180, 'KERB2': 180, 'KERB3': 0, 'KERB4': 0, 'RACK': 0, 'ZIL': 180,
    'WRECK1': 0, 'WRECK2': 180, '01': 180, '02': 0,
}

# ---------------------------------------------------------------------------
# collection / object scan
# ---------------------------------------------------------------------------
def coll_names(o):
    return sorted({c.name for c in o.users_collection})


MESHES = [o for o in bpy.data.objects if o.type == 'MESH']
NON_MESH = [o for o in bpy.data.objects if o.type != 'MESH']


def world_aabb(objs):
    vertices = world_verts_many(objs)
    if not len(vertices):
        return None
    return vertices.min(axis=0).tolist(), vertices.max(axis=0).tolist()


def world_verts(o):
    coordinates = np.empty(len(o.data.vertices) * 3, dtype=np.float64)
    o.data.vertices.foreach_get('co', coordinates)
    matrix = np.array(o.matrix_world, dtype=np.float64)
    return coordinates.reshape(-1, 3) @ matrix[:3, :3].T + matrix[:3, 3]


def world_verts_many(objs):
    if len(objs) == 1:
        return world_verts(objs[0])
    return np.concatenate([world_verts(o) for o in objs], axis=0)


def poly_centroid_material(o, needles):
    names = [s.material.name if s and s.material else '' for s in o.material_slots]
    mw = o.matrix_world
    pts = []
    for poly in o.data.polygons:
        idx = poly.material_index
        if idx < 0 or idx >= len(names):
            continue
        mat = names[idx].upper()
        if any(n in mat for n in needles):
            pts.append((mw @ poly.center)[:])
    return np.array(pts) if pts else None


def slug(s):
    s = s.lower()
    s = re.sub(r'[^a-z0-9_.-]+', '_', s)
    return re.sub(r'_+', '_', s).strip('_.')


def family_of(name):
    for prefix, cls, cn, aliases in FAMILIES:
        if name.startswith(prefix):
            return prefix, cls, cn, aliases
    return None


def group_key(name):
    for rx, fn in GROUP_RULES:
        m = rx.match(name)
        if m:
            return fn(m)
    g = name
    for _ in range(6):
        ng = PART_SUFFIX.sub('', g)
        if ng == g:
            break
        g = ng
    return g


def compass_side(bb_u, x, z):
    """Which facade side a Unity point lies nearest to (for entrance candidates)."""
    x0, y0, z0, x1, y1, z1 = bb_u
    d = {'西': abs(x - x0), '东': abs(x1 - x), '南': abs(z - z0), '北': abs(z1 - z)}
    return min(d, key=d.get)


# ---------------------------------------------------------------------------
# orientation inference
# ---------------------------------------------------------------------------
def pca_axes(verts):
    pts = verts[:, :2]
    if len(pts) < 6:
        return None
    c = pts.mean(axis=0)
    cov = np.cov((pts - c).T)
    w, v = np.linalg.eigh(cov)
    order = np.argsort(w)[::-1]
    return c, v[:, order[0]], v[:, order[1]]


def connected_parts(obj):
    """Yield world vertices for each disconnected mesh part without editing the mesh."""
    adjacency = [[] for vertex in obj.data.vertices]
    for edge in obj.data.edges:
        first, second = edge.vertices
        adjacency[first].append(second)
        adjacency[second].append(first)
    remaining = set(range(len(adjacency)))
    vertices = world_verts(obj)
    while remaining:
        pending = [remaining.pop()]
        indices = []
        while pending:
            index = pending.pop()
            indices.append(index)
            for neighbor in adjacency[index]:
                if neighbor in remaining:
                    remaining.remove(neighbor)
                    pending.append(neighbor)
        yield vertices[indices]


def bench_orientation(objs):
    """Infer seated facing from separate seat and backrest board centers."""
    vertices = world_verts_many(objs)
    if len(vertices) < 12:
        return None
    bottom = float(vertices[:, 2].min())
    height = float(vertices[:, 2].max()) - bottom
    seats, backs = [], []
    for obj in objs:
        for part in connected_parts(obj):
            size = np.ptp(part, axis=0)
            center = part.mean(axis=0)
            if max(size[:2]) < 0.8 or size[2] > 0.20:
                continue
            elevation = center[2] - bottom
            if 0.35 <= elevation <= 0.60:
                seats.append(center)
            elif 0.60 < elevation <= 1.10:
                backs.append(center)
    if not seats or not backs:
        return {'yaw': None, 'source': 'geometric.seat_back_components',
                'confidence': 'ambiguous_backless' if seats and not backs else 'ambiguous',
                'long_axis_deg': None, 'backrest_dz': round(height, 3)}
    seat_center = np.mean(seats, axis=0)
    back_center = np.mean(backs, axis=0)
    front = seat_center[:2] - back_center[:2]
    length = float(np.linalg.norm(front))
    if length < 0.06:
        return {'yaw': None, 'source': 'geometric.seat_back_components',
                'confidence': 'ambiguous', 'long_axis_deg': None, 'backrest_dz': round(height, 3)}
    yaw = yaw_from_front(float(front[0]), float(front[1]))
    return {'yaw': yaw, 'source': 'geometric.seat_back_components', 'confidence': 'high',
            'long_axis_deg': None, 'backrest_dz': round(height, 3),
            'back_offset_m': round(length, 3)}


def vehicle_orientation(o):
    """Front direction from headlamp/taillamp material clusters."""
    head = poly_centroid_material(o, ('HEADLAMP',))
    tail = poly_centroid_material(o, ('TAILLAMP',))
    if head is None or tail is None or len(head) == 0 or len(tail) == 0:
        return {'yaw': None, 'source': 'geometric.head_tail_materials', 'confidence': 'missing_lamps',
                'long_axis_deg': None, 'axis_error_deg': None}
    f = head.mean(axis=0)[:2] - tail.mean(axis=0)[:2]
    ln = float(np.linalg.norm(f))
    if ln < 1e-4:
        return {'yaw': None, 'source': 'geometric.head_tail_materials', 'confidence': 'degenerate',
                'long_axis_deg': None, 'axis_error_deg': None}
    f /= ln
    ax = pca_axes(world_verts(o))
    axis_err = None
    long_deg = None
    if ax is not None:
        la = ax[1]
        long_deg = round((math.degrees(math.atan2(la[0], la[1])) + 360) % 360, 2)
        cosang = abs(float(np.dot(la, f)))
        axis_err = round(math.degrees(math.acos(min(1.0, cosang))), 2)
    yaw = yaw_from_front(float(f[0]), float(f[1]))
    conf = 'high'
    if axis_err is not None and axis_err > 10:
        conf = 'low_axis_mismatch'
    return {'yaw': yaw, 'source': 'geometric.head_tail_materials', 'confidence': conf,
            'long_axis_deg': long_deg, 'axis_error_deg': axis_err}


def obb_of(objs, yaw_deg):
    """OBB in the object's front frame (front = +local Y in world), Blender units."""
    f = math.radians(yaw_deg)
    fx, fy = math.sin(f), math.cos(f)
    right = np.array([fy, -fx])   # rotate front -90deg (yaw down)
    verts_all = world_verts_many(objs)
    verts = verts_all[:, :2]
    c = verts.mean(axis=0)
    d = verts - c
    a = d @ np.array([fx, fy])
    b = d @ right
    z = verts_all[:, 2]
    center = c + np.array([fx, fy]) * (a.min() + a.max()) / 2.0 + right * (b.min() + b.max()) / 2.0
    return {
        'center_blender': [round(float(center[0]), 4), round(float(center[1]), 4),
                           round(float((z.min() + z.max()) / 2.0), 4)],
        'size_blender': [round(float(b.max() - b.min()), 4), round(float(a.max() - a.min()), 4),
                         round(float(z.max() - z.min()), 4)],
        'yaw_unity_deg': round(float(yaw_deg), 2),
        'axis_note': 'size = [right, front, up] in the front frame; NOT world AABB + yaw',
    }


# ---------------------------------------------------------------------------
# entity build
# ---------------------------------------------------------------------------
entities = []
source_rows = []
unclassified = []
excluded_counts = {}
stats = {
    'collections': {},
    'entities_by_class': {},
}

# --- buildings -------------------------------------------------------------
def building_group(name):
    for bid in BLOCK_IDS:
        if name == bid or name.startswith(bid + '_'):
            return bid
    return None


bld_objs = {}
for o in MESHES:
    cs = coll_names(o)
    if 'BUILDINGS' in cs:
        bid = building_group(o.name)
        if bid:
            bld_objs.setdefault(bid, []).append(o)
        else:
            unclassified.append((o.name, 'BUILDINGS', 'no_block_prefix'))
            source_rows.append((o.name, 'BUILDINGS', 'unclassified', '', 'no_block_prefix', ''))

for bid, objs in sorted(bld_objs.items()):
    b = BLOCKS.get(bid, {})
    m = MANIFEST.get(bid, {})
    aabb = world_aabb(objs)
    if not aabb:
        continue
    bb_u = bbox_u(aabb[0], aabb[1])
    faces = b.get('faces') or m.get('faces') or []
    facades = [{'normal_blender': f, 'facing_zh': FACE_MAP.get(f, '?')} for f in faces]
    entrance_candidates = []
    for node in nav.get('nodes', []):
        if node.get('kind') == 'entrance' and node.get('name') == bid:
            pos = [round(float(node['x']), 3), round(float(node['z']), 3), round(float(node['y']), 3)]
            entrance_candidates.append({
                'nav_node_id': node.get('id'),
                'position_unity': pos,
                'facade_side': compass_side(bb_u, pos[0], pos[2]),
                'verified_real_door': False,
                'note': 'nav.py:358 proportional facade point offset 2.1m outward; candidate only',
            })
    eid = 'entity/' + slug('bld_' + bid)
    entities.append({
        'id': eid,
        'class': 'building',
        'kind': b.get('type') or m.get('type') or 'building',
        'name_zh': b.get('cn') or m.get('cn') or bid,
        'name_en': bid,
        'aliases': [bid, (b.get('cn') or m.get('cn') or bid), '建筑', '楼'],
        'selectable': True,
        'blocks_navigation': True,
        'implemented_actions': [],
        'affordance_candidates': [{'verb': 'look', 'status': 'candidate'}],
        'center': {'blender': [round((aabb[0][0] + aabb[1][0]) / 2, 4), round((aabb[0][1] + aabb[1][1]) / 2, 4),
                               round((aabb[0][2] + aabb[1][2]) / 2, 4)],
                   'unity': u((aabb[0][0] + aabb[1][0]) / 2, (aabb[0][1] + aabb[1][1]) / 2, (aabb[0][2] + aabb[1][2]) / 2)},
        'ground': {'blender_z': round(aabb[0][2], 4), 'unity_y': round(aabb[0][2], 4)},
        'bbox': {'world_aabb_blender': [[round(v, 4) for v in aabb[0]], [round(v, 4) for v in aabb[1]]],
                 'world_aabb_unity': bb_u, 'obb': None,
                 'note': 'multi-facade building: no single OBB; entrance candidates separate'},
        'anchor': {'center': u((aabb[0][0] + aabb[1][0]) / 2, (aabb[0][1] + aabb[1][1]) / 2, aabb[0][2]),
                   'approach': None, 'focus': None},
        'orientation': {'yaw_unity_deg': None, 'facing_zh': None, 'source': 'plan.faces',
                        'confidence': 'facade_list_only',
                        'facades': facades,
                        'note': 'buildings have multiple facades; no single yaw by design'},
        'entrance_candidates': entrance_candidates,
        'facades': facades,
        'source_collection': 'BUILDINGS',
        'source_group': bid,
        'source_objects': sorted(o.name for o in objs),
        'notes': '',
    })
    stats['entities_by_class']['building'] = stats['entities_by_class'].get('building', 0) + 1
    for o in objs:
        source_rows.append((o.name, 'BUILDINGS', 'entity', eid, '', bid))

# --- props / vehicles / site inclusions ------------------------------------
entity_parts = {}   # eid -> list
entity_meta = {}    # eid -> dict


def ensure_entity(key, collection, name_src):
    eid = 'entity/' + slug(key)
    if eid not in entity_meta:
        fam = family_of(key) or family_of(name_src)
        if fam:
            _, cls, cn, aliases = fam
        else:
            cls, cn, aliases = 'object', key, [key]
        if key.endswith('_bin') and cls == 'dumpster':
            cls, cn, aliases = 'trash_bin', '垃圾桶', ['垃圾桶', 'bin']
        entity_meta[eid] = {
            'key': key,
            'collection': collection,
            'class': cls,
            'name_zh': cn,
            'aliases': list(aliases),
            'source_name': name_src,
        }
        entity_parts[eid] = []
    entity_parts[eid].append(name_src)
    return eid


def vehicle_parts(name):
    if name.endswith('_dest') or _dest(name):
        return False
    return bool(re.match(r'^(?:VEH_(?:vaz2101|gaz24|moskvich412|uaz469|zil130|LAZ695)_.+|TRAM_T3_\d+)$', name))


def _dest(name):
    return '_dest' in name or 'route' in name.lower() or name in ('WIRE_OVERHEAD', 'POLE_UTILITY_AND_CATENARY')


for o in MESHES:
    cs = coll_names(o)
    name = o.name
    if 'BUILDINGS' in cs:
        continue
    if 'PROPS' in cs:
        key = group_key(name)
        ensure_entity(key, 'PROPS', name)
        source_rows.append((name, 'PROPS', 'entity', 'entity/' + slug(key), '', key))
        continue
    if 'VEHICLES' in cs:
        if vehicle_parts(name):
            m = re.match(r'^(?:VEH_([A-Za-z0-9]+)_(.+)|TRAM_T3_(\d+))$', name)
            kind = (m.group(1) if m and m.group(1) else 'tram')
            tag = (m.group(2) if m and m.group(2) else '01')
            key = ('veh_' + kind + '_' + tag).lower()
            eid = ensure_entity(key, 'VEHICLES', name)
            meta = entity_meta[eid]
            meta['vehicle_kind'] = kind
            meta['vehicle_tag'] = tag
            meta['class'] = 'vehicle'
            meta['name_zh'] = '汽车'
            meta['aliases'] = ['汽车', '车辆', 'car', 'vehicle']
            source_rows.append((name, 'VEHICLES', 'entity', eid, '', key))
        else:
            reason = 'vehicle_label_or_infrastructure'
            excluded_counts[reason] = excluded_counts.get(reason, 0) + 1
            source_rows.append((name, 'VEHICLES', 'excluded', '', reason, ''))
        continue
    if 'SITE' in cs:
        if name.startswith('STALL'):
            reason = 'parking_space_paint_marking'
            excluded_counts[reason] = excluded_counts.get(reason, 0) + 1
            source_rows.append((name, 'SITE', 'excluded', '', reason, ''))
        elif name.startswith('FLAG'):
            m = re.match(r'^FLAG_(?:base|cloth|pole)(\d+)$', name)
            key = 'FLAG_' + (m.group(1) if m else 'x')
            ensure_entity(key, 'SITE', name)
            source_rows.append((name, 'SITE', 'entity', 'entity/' + slug(key), '', key))
        elif name.startswith('TRAMSHELTER'):
            reason = 'part_grouping_ambiguous_needs_generator_metadata'
            excluded_counts[reason] = excluded_counts.get(reason, 0) + 1
            source_rows.append((name, 'SITE', 'excluded', '', reason, ''))
        else:
            reason = 'site_surface_or_infrastructure'
            excluded_counts[reason] = excluded_counts.get(reason, 0) + 1
            source_rows.append((name, 'SITE', 'excluded', '', reason, ''))
        continue
    if 'RENDER_ONLY' in cs:
        reason = 'render_only'
        excluded_counts[reason] = excluded_counts.get(reason, 0) + 1
        source_rows.append((name, 'RENDER_ONLY', 'excluded', '', reason, ''))
        continue
    unclassified.append((name, ','.join(cs), 'unknown_collection'))
    source_rows.append((name, ','.join(cs), 'unclassified', '', 'unknown_collection', ''))

# finalize props/site entities
def bench_expected(key):
    """Generator-table expected Unity yaw for bench paths whose facing is known."""
    m = re.match(r'^PROP_BENCH_(\d{2})$', key)
    if m:
        i = int(m.group(1))
        if i < len(BENCH_SPOTS):
            return ((180.0 - math.degrees(BENCH_SPOTS[i][2])) % 360.0, 'generator.props.BENCH_SPOTS[%d]' % i)
    if re.match(r'^PROP_BENCH_PLAZA_\d+$', key):
        return (180.0, 'generator.props.kit.bench facing=0')
    m = re.match(r'^PROP_FOREST_BENCH_(\d+)$', key)
    if m:
        i = int(m.group(1))
        if i < len(FOREST_BENCH_FACING):
            return ((180.0 - math.degrees(FOREST_BENCH_FACING[i])) % 360.0, 'generator.props forest bench table[%d]' % i)
    return (None, None)


for eid, parts in sorted(entity_parts.items()):
    meta = entity_meta[eid]
    objs = [bpy.data.objects[n] for n in parts if n in bpy.data.objects]
    aabb = world_aabb(objs)
    if not aabb:
        continue
    bb_u = bbox_u(aabb[0], aabb[1])
    cls = meta['class']
    name_zh = meta['name_zh']
    orientation = {'yaw_unity_deg': None, 'facing_zh': None, 'source': None,
                   'confidence': 'not_applicable', 'note': ''}
    obb = None
    bbox_note = None
    if cls == 'bench':
        inf = bench_orientation(objs)
        expected, exp_src = bench_expected(meta['key'])
        geom_check = None
        if inf and inf.get('yaw') is not None:
            geom_check = {'inferred_yaw': inf['yaw'], 'source': inf['source'],
                          'confidence': inf['confidence'],
                          'long_axis_deg': inf.get('long_axis_deg'),
                          'backrest_dz': inf.get('backrest_dz')}
        if expected is not None:
            if geom_check is not None:
                d = abs((geom_check['inferred_yaw'] - expected + 180.0) % 360.0 - 180.0)
                geom_check['error_deg'] = round(d, 2)
            orientation = {'yaw_unity_deg': round(expected, 2), 'facing_zh': facing_zh(expected),
                           'source': exp_src, 'confidence': 'verified_generator_table',
                           'note': 'front = seated facing; generator facing converted by (180 - facing_deg)',
                           'geometric_check': geom_check}
            obb = obb_of(objs, expected)
        elif inf and inf['yaw'] is not None:
            orientation = {'yaw_unity_deg': inf['yaw'], 'facing_zh': facing_zh(inf['yaw']),
                           'source': inf['source'], 'confidence': inf['confidence'],
                           'long_axis_deg': inf.get('long_axis_deg'),
                           'backrest_dz': inf.get('backrest_dz'), 'note': 'front = seated facing',
                           'geometric_check': geom_check}
            # Bench generation quirk: slats are built world-axis-aligned while legs are
            # rotated by facing, so the mesh has no clean oriented frame.  Only yaw is
            # reported; OBB is suppressed instead of emitting a wrong box.
            obb = None
            bbox_note = ('obb suppressed: bench slats are generated world-axis-aligned; '
                         'only yaw is inferred for this generation path')
        elif inf:
            orientation = {'yaw_unity_deg': None, 'facing_zh': None, 'source': inf['source'],
                           'confidence': inf['confidence'],
                           'note': 'backrest height ambiguous; no generator table for this path'}
    elif cls == 'vehicle' and len(objs) == 1:
        inf = vehicle_orientation(objs[0])
        if inf and inf['yaw'] is not None:
            orientation = {'yaw_unity_deg': inf['yaw'], 'facing_zh': facing_zh(inf['yaw']),
                           'source': inf['source'], 'confidence': inf['confidence'],
                           'long_axis_deg': inf.get('long_axis_deg'),
                           'axis_error_deg': inf.get('axis_error_deg'), 'note': 'front = headlamp side'}
            obb = obb_of(objs, inf['yaw'])
        elif inf:
            orientation = {'yaw_unity_deg': None, 'facing_zh': None, 'source': inf['source'],
                           'confidence': inf['confidence'], 'note': 'lamp materials missing'}
    else:
        # buildings with facade list already handled; others have no canonical front
        if cls == 'street_lamp':
            orientation['source'] = 'not_applicable'
            orientation['note'] = 'lamp arm direction not represented as front yet'
        else:
            orientation['source'] = 'not_applicable'

    if cls == 'vehicle':
        kind = meta.get('vehicle_kind', '')
        kind_l = str(kind).lower()
        if kind_l == 'tram':
            name_zh, aliases = TRAM_NAME
        elif kind_l in VEHICLE_CLASS:
            name_zh, aliases = VEHICLE_CLASS[kind_l]
        else:
            name_zh, aliases = name_zh or '汽车', ['汽车', 'car']
    else:
        aliases = meta['aliases']

    cands = []
    if cls == 'bench':
        cands = [{'verb': 'sit', 'status': 'candidate',
                  'note': 'seat height / approach pose must be recorded in stage 3'}]
    if cls in ('sign', 'billboard', 'poster_board', 'bus_stop', 'plaque', 'eternal_flame', 'flag'):
        cands = [{'verb': 'look', 'status': 'candidate'}, {'verb': 'read', 'status': 'candidate'}]
    else:
        cands = cands or [{'verb': 'look', 'status': 'candidate'}]

    eid_short = eid
    entities.append({
        'id': eid_short,
        'class': cls,
        'kind': meta['collection'].lower(),
        'name_zh': name_zh,
        'name_en': meta['source_name'],
        'aliases': list(dict.fromkeys(aliases + [name_zh])),
        'selectable': True,
        'blocks_navigation': False,
        'implemented_actions': [],
        'affordance_candidates': cands,
        'center': {'blender': [round((aabb[0][0] + aabb[1][0]) / 2, 4), round((aabb[0][1] + aabb[1][1]) / 2, 4),
                               round((aabb[0][2] + aabb[1][2]) / 2, 4)],
                   'unity': u((aabb[0][0] + aabb[1][0]) / 2, (aabb[0][1] + aabb[1][1]) / 2,
                              (aabb[0][2] + aabb[1][2]) / 2)},
        'ground': {'blender_z': round(aabb[0][2], 4), 'unity_y': round(aabb[0][2], 4)},
        'bbox': {'world_aabb_blender': [[round(v, 4) for v in aabb[0]], [round(v, 4) for v in aabb[1]]],
                 'world_aabb_unity': bb_u, 'obb': obb, 'note': bbox_note},
        'anchor': {'center': u((aabb[0][0] + aabb[1][0]) / 2, (aabb[0][1] + aabb[1][1]) / 2, aabb[0][2]),
                   'approach': None, 'focus': None},
        'orientation': orientation,
        'entrance_candidates': [],
        'facades': [],
        'source_collection': meta['collection'],
        'source_group': meta['key'],
        'source_objects': sorted(set(parts)),
        'notes': '',
    })
    stats['entities_by_class'][cls] = stats['entities_by_class'].get(cls, 0) + 1

# ---------------------------------------------------------------------------
# validation: exported yaw vs generator tables; geometric inference vs tables
# ---------------------------------------------------------------------------
bench_val = []
vehicle_val = []


def ang_err(a, b):
    if a is None or b is None:
        return None
    return round(abs((a - b + 180.0) % 360.0 - 180.0), 2)


def cmp_summary(rows, key):
    ok = [v for v in rows if v.get(key) is not None]
    if not ok:
        return {'comparable': 0}
    errs = [v[key] for v in ok]
    return {'comparable': len(ok), 'max_error_deg': round(max(errs), 2),
            'mean_error_deg': round(sum(errs) / len(errs), 2),
            'within_5deg': sum(1 for x in errs if x <= 5.0),
            'within_15deg': sum(1 for x in errs if x <= 15.0)}


for e in entities:
    if e['class'] != 'bench':
        continue
    key = e['source_group']
    expected, src = bench_expected(key)
    if expected is None:
        continue
    orient = e['orientation']
    gc = orient.get('geometric_check') or {}
    bench_val.append({
        'id': e['id'], 'source_group': key,
        'expected_yaw': round(expected, 2),
        'exported_yaw': orient.get('yaw_unity_deg'),
        'exported_source': orient.get('source'),
        'exported_error_deg': ang_err(orient.get('yaw_unity_deg'), expected),
        'geometric_inferred_yaw': gc.get('inferred_yaw'),
        'geometric_error_deg': gc.get('error_deg'),
        'generator_source': src,
    })

for e in entities:
    if e['class'] != 'vehicle':
        continue
    key = e['source_group']
    m = re.match(r'^veh_([a-z0-9]+)_(.+)$', key)
    if not m:
        continue
    kind, tag = m.group(1), m.group(2)
    tag_u = tag.upper()
    if kind == 'tram':
        expected = 270.0
        src = 'generator.vehicles.py tram yaw=180 (local front +X)'
    elif tag_u in VEHICLE_YAW:
        expected = round((90.0 - VEHICLE_YAW[tag_u]) % 360.0, 2)
        src = 'generator.vehicles.py tag=%s yaw=%s (local front +X)' % (tag_u, VEHICLE_YAW[tag_u])
    else:
        continue
    orient = e['orientation']
    vehicle_val.append({
        'id': e['id'], 'source_group': key,
        'expected_yaw': expected,
        'exported_yaw': orient.get('yaw_unity_deg'),
        'exported_error_deg': ang_err(orient.get('yaw_unity_deg'), expected),
        'axis_error_deg': orient.get('axis_error_deg'),
        'confidence': orient.get('confidence'),
        'generator_source': src,
    })


# ---------------------------------------------------------------------------
# write outputs
# ---------------------------------------------------------------------------
entities.sort(key=lambda e: e['id'])
ids = [e['id'] for e in entities]
assert len(ids) == len(set(ids)), 'duplicate entity ids: %d' % (len(ids) - len(set(ids)))

stats['entity_total'] = len(entities)
stats['mesh_total'] = len(MESHES)
stats['non_mesh_total'] = {t: sum(1 for o in NON_MESH if o.type == t) for t in
                           sorted({o.type for o in NON_MESH})}
stats['source_rows'] = {
    'entity': sum(1 for r in source_rows if r[2] == 'entity'),
    'excluded': sum(1 for r in source_rows if r[2] == 'excluded'),
    'unclassified': sum(1 for r in source_rows if r[2] == 'unclassified'),
}
stats['excluded_reasons'] = dict(sorted(excluded_counts.items()))
stats['unclassified'] = [x[0] for x in unclassified]
source_names = [row[0] for row in source_rows]
stats['duplicate_attribution'] = len(source_names) - len(set(source_names))
assert stats['duplicate_attribution'] == 0, 'a source mesh was assigned more than once'
assert set(source_names) == {o.name for o in MESHES}, 'source mesh coverage is incomplete'
stats['validation'] = {
    'bench': {
        'rows_with_generator_expected': len(bench_val),
        'exported_yaw_from_generator': sum(1 for r in bench_val
                                           if (r.get('exported_source') or '').startswith('generator')),
        'exported_error': cmp_summary(bench_val, 'exported_error_deg'),
        'geometric_vs_table': cmp_summary(bench_val, 'geometric_error_deg'),
    },
    'vehicle': {
        'rows_with_generator_expected': len(vehicle_val),
        'exported_error': cmp_summary(vehicle_val, 'exported_error_deg'),
        'axis_error': cmp_summary(vehicle_val, 'axis_error_deg'),
    },
}

doc = {
    'schema': 'sbh.world.entities.v0',
    'data_version': '0.1.1',
    'generated_at': datetime.datetime.now().astimezone().isoformat(timespec='seconds'),
    'generator': 'Maps/SovietBlueHour/tools/export_entities.py',
    'map': {
        'id': 'SovietBlueHour',
        'version': '2026-11-02',
        'blend': os.path.basename(BLEND),
        'blend_size': os.path.getsize(BLEND),
        'blend_sha256': sha256(BLEND),
        'plan_sha256': sha256(plan_path),
        'scene_manifest_sha256': sha256(manifest_path),
        'nav_graph_sha256': sha256(nav_path),
        'exporter_sha256': sha256(__file__),
        'generator_dependencies_sha256': {
            name: sha256(os.path.join(MAP_DIR, 'tools', name))
            for name in ('props.py', 'vehicles.py', 'kit.py', 'sitekit.py')
        },
    },
    'coordinate_contract': {
        'blender': 'Z-up, +Y = north, +X = east',
        'unity': 'Y-up, unity=(blender.x, blender.z, blender.y); +Z = north, +X = east',
        'yaw': 'unity_yaw_deg = degrees(atan2(front_x_blender, front_y_blender)); 0=north 90=east 180=south 270=west',
        'bbox': 'world_aabb_unity = [minx,miny,minz,maxx,maxy,maxz]',
        'obb': 'center_blender is a Blender world position; size_blender=[right,front,up] in the front frame; Unity size=[right,up,front]',
        'bench_generator_facing': 'props.py:1897 facing 0 = looks south (-Y) => unity_yaw = (180 - facing_deg) mod 360',
    },
    'schema_note': 'selectable / blocks_navigation / implemented_actions are independent; '
                   'affordance_candidates are annotations only and are NOT executable capabilities.',
    'stats': stats,
    'entities': entities,
}

input_hashes_after = {name: sha256(path) for name, path in input_paths.items()}
assert input_hashes_before == input_hashes_after, 'a source input changed during export'
doc['read_only_integrity'] = {'before_sha256': input_hashes_before,
                              'after_sha256': input_hashes_after, 'unchanged': True}

if A['previous']:
    previous = load_json(A['previous'])
    current_sources = {name: e['id'] for e in entities for name in e['source_objects']}
    dispositions = {row[0]: row[2:5] for row in source_rows}
    migration_rows = []
    for old in previous['entities']:
        targets = sorted({current_sources[name] for name in old['source_objects'] if name in current_sources})
        removed = [{'source_object': name, 'status': dispositions.get(name, ['missing'])[0],
                    'reason': dispositions.get(name, ['', '', 'missing_from_current_map'])[-1]}
                   for name in old['source_objects'] if name not in current_sources]
        status = ('retained' if targets == [old['id']] and not removed else
                  'merged' if len(targets) == 1 and not removed else
                  'excluded' if not targets else 'split_or_partial')
        migration_rows.append({'old_id': old['id'], 'new_ids': targets, 'status': status,
                               'excluded_sources': removed})
    migration = {'from_data_version': previous['data_version'], 'to_data_version': doc['data_version'],
                 'previous_json_sha256': sha256(A['previous']),
                 'previous_blend_sha256': previous['map']['blend_sha256'],
                 'current_blend_sha256': doc['map']['blend_sha256'],
                 'summary': {status: sum(row['status'] == status for row in migration_rows)
                             for status in sorted({row['status'] for row in migration_rows})},
                 'entities': migration_rows}
    migration_name = 'id_migration.v%s_to_v%s.json' % (previous['data_version'], doc['data_version'])
    with open(os.path.join(OUT_DIR, migration_name), 'w', encoding='utf-8') as f:
        json.dump(migration, f, ensure_ascii=False, indent=1)

with open(os.path.join(OUT_DIR, 'world_entities.v0.json'), 'w', encoding='utf-8') as f:
    json.dump(doc, f, ensure_ascii=False, indent=1)

with open(os.path.join(OUT_DIR, 'source_object_map.csv'), 'w', encoding='utf-8', newline='') as f:
    w = csv.writer(f)
    w.writerow(['source_object', 'collection', 'status', 'entity_id', 'reason_or_note', 'group_key'])
    for row in sorted(source_rows):
        w.writerow(row)

with open(os.path.join(OUT_DIR, 'extraction_stats.json'), 'w', encoding='utf-8') as f:
    json.dump(stats, f, ensure_ascii=False, indent=1)

with open(os.path.join(OUT_DIR, 'orientation_validation.json'), 'w', encoding='utf-8') as f:
    json.dump({'bench': bench_val, 'vehicle': vehicle_val,
               'summary': {
                   'bench': {
                       'rows_with_generator_expected': len(bench_val),
                       'exported_yaw_from_generator': sum(1 for r in bench_val
                                                          if (r.get('exported_source') or '').startswith('generator')),
                       'exported_error': cmp_summary(bench_val, 'exported_error_deg'),
                       'geometric_vs_table': cmp_summary(bench_val, 'geometric_error_deg'),
                   },
                   'vehicle': {
                       'rows_with_generator_expected': len(vehicle_val),
                       'exported_error': cmp_summary(vehicle_val, 'exported_error_deg'),
                       'axis_error': cmp_summary(vehicle_val, 'axis_error_deg'),
                   },
               }},
              f, ensure_ascii=False, indent=1)

# ---------------------------------------------------------------------------
# grouping report
# ---------------------------------------------------------------------------
def write_report():
    lines = []
    add = lines.append
    add('# SovietBlueHour 实体盘点报告（Stage 0-1，只读导出 v0）')
    add('')
    add('> 由 `Maps/SovietBlueHour/tools/export_entities.py` 从 `SovietBlueHour_beauty.blend` 只读导出；')
    add('> 未修改 .blend / 场景 / 运行时代码。坐标系与朝向契约见 `Docs/Architecture/WorldEntities_Stage01_2026-11-06.md`。')
    add('')
    add('## 1. 总量')
    add('')
    add('| 项 | 数量 |')
    add('|---|---|')
    add('| 源 mesh 对象 | %d |' % stats['mesh_total'])
    add('| 逻辑实体 | %d |' % stats['entity_total'])
    add('| 源 mesh → 实体 | %d |' % stats['source_rows']['entity'])
    add('| 源 mesh → 明确排除 | %d |' % stats['source_rows']['excluded'])
    add('| 源 mesh → 未分类 | %d |' % stats['source_rows']['unclassified'])
    add('| 非 mesh 对象 | %s |' % json.dumps(stats['non_mesh_total'], ensure_ascii=False))
    add('')
    add('实体类别分布：')
    add('')
    add('| class | 数量 |')
    add('|---|---|')
    for k, v in sorted(stats['entities_by_class'].items(), key=lambda kv: -kv[1]):
        add('| %s | %d |' % (k, v))
    add('')
    add('## 2. 归组规则与可追溯性')
    add('')
    add('- 每个 mesh 的去向都在 `source_object_map.csv`（entity / excluded / unclassified 三类，无第四种状态）。')
    add('- 建筑：按 plan.json 的 48 个 block id 做最长前缀归属；所有 BUILDINGS mesh 均有归属（见未分类清单）。')
    add('- 道具：按族 + 分件后缀规则归组（`PROP_LAMP_*_base/globe/pole` → 一根路灯；'
        '`PROP_BIN_*_body/post/rim` → 一个垃圾桶；公交站、广告牌、海报板、垃圾箱、围栏按各自规则归组，'
        '详见脚本 `GROUP_RULES`）。')
    add('- 车辆：`VEH_<kind>_<tag>` / `TRAM_T3_01` 一个对象即一辆车；`*_dest` 文字与杆线基础设施被排除。')
    add('- `STALL_*` 是停车位地面划线，按停车标线排除；`FLAG_*` 作为实体；`TRAMSHELTER_*` 分件归属不明确，明确排除并列出原因。')
    add('- 未分类清单（如实列出）：')
    if stats['unclassified']:
        for n in stats['unclassified'][:50]:
            add('  - %s' % n)
        if len(stats['unclassified']) > 50:
            add('  - ... 共 %d 条，见 source_object_map.csv' % len(stats['unclassified']))
    else:
        add('  - （无）')
    add('')
    add('## 3. 排除原因统计')
    add('')
    add('| reason | 数量 |')
    add('|---|---|')
    for k, v in stats['excluded_reasons'].items():
        add('| %s | %d |' % (k, v))
    add('')
    add('## 4. 朝向校验（生成器表 vs 导出值；几何法与生成器表独立比对）')
    add('')
    vb, vv = stats['validation']['bench'], stats['validation']['vehicle']
    add('**长椅**：%d 条有生成器期望；导出 yaw 直接采用生成器表（%d/%d），因此导出误差为 0；'
        '几何回推法（座板与靠背分件中心）作为独立交叉校验：' % (vb['rows_with_generator_expected'],
        vb['exported_yaw_from_generator'], vb['rows_with_generator_expected']))
    add('')
    g = vb['geometric_vs_table']
    if g.get('comparable'):
        add('- 几何法可比较 %d 条，最大误差 %s°，平均 %s°，≤5° %d 条，≤15° %d 条'
            % (g['comparable'], g['max_error_deg'], g['mean_error_deg'], g['within_5deg'], g['within_15deg']))
    else:
        add('- 几何法没有可比较条数')
    add('- 上述几何交叉校验来自已有生成器期望的长椅，不代表另外 44 条几何推断长椅已通过独立校验；见独立验收报告。')
    add('')
    add('**车辆**：%d 条有生成器期望（放置表 yaw，本地车头 = +X）；导出 yaw 来自头灯/尾灯材质聚类，与生成器表独立比对：'
        % vv['rows_with_generator_expected'])
    add('')
    e = vv['exported_error']
    if e.get('comparable'):
        add('- 可比较 %d 条，最大误差 %s°，平均 %s°，≤5° %d 条，≤15° %d 条'
            % (e['comparable'], e['max_error_deg'], e['mean_error_deg'], e['within_5deg'], e['within_15deg']))
    else:
        add('- 没有可比较条数（材质聚类失败见 orientation_validation.json 明细）')
    ax = vv['axis_error']
    if ax.get('comparable'):
        add('- 车头方向与车身长轴一致性：可比较 %d 条，最大偏差 %s°，平均 %s°'
            % (ax['comparable'], ax['max_error_deg'], ax['mean_error_deg']))
    add('')
    add('说明：长椅公式 `unity_yaw = (180 - facing_deg) mod 360` 只用于 `props.py` 的 `BENCH_SPOTS`、'
        '广场长椅（facing=0）、森林长椅表这三条生成路径；其他长椅（公园/凉亭/车站）走几何回推，'
        '回推失败的 `yaw_unity_deg = null`，不强行猜。')
    add('本版长椅共 %d 条：%d 条有 yaw（%d 生成器表 + %d 几何回推），%d 条无靠背（backless）为 null。'
        % (sum(1 for e in entities if e['class'] == 'bench'),
           sum(1 for e in entities if e['class'] == 'bench' and e['orientation'].get('yaw_unity_deg') is not None),
           vb['rows_with_generator_expected'],
           sum(1 for e in entities if e['class'] == 'bench'
               and (e['orientation'].get('source') or '').startswith('geometric')
               and e['orientation'].get('yaw_unity_deg') is not None),
           sum(1 for e in entities if e['class'] == 'bench' and e['orientation'].get('yaw_unity_deg') is None)))
    add('车辆的“正面”按类定义：本地车头 = +X，头部灯材质簇 → 世界前向量；该规则只对车辆生成路径成立，不外推到其他类别。')
    add('OBB 精度限制：只对“生成器表可验证的长椅”和车辆给出 OBB；几何回推的旋转长椅因生成器把坐板/靠背板做成世界轴对齐，'
        '没有干净的正交坐标系，**OBB 置空、只给 yaw**（字段 `bbox.note` 注明），避免输出错误包围盒。')
    add('路灯、树、围栏、垃圾桶等没有有意义的“正面”，朝向字段为 null（not_applicable）。')
    add('')
    add('## 5. 入口候选（entrance_candidates）')
    add('')
    add('来自原始 `map/nav_graph.json`（含建筑 `name` 归属）；`nav_compact.json` 已丢失名称，不能用于此用途。')
    add('这些点是沿立面按比例外偏 2.1m 的**导航候选**（`nav.py:358`），不是已验证的真实门口；字段 `verified_real_door=false`。')
    add('')
    add('| 建筑 | 入口候选数 |')
    add('|---|---|')
    for e in entities:
        if e['class'] == 'building' and e.get('entrance_candidates'):
            add('| %s (%s) | %d |' % (e['name_zh'], e['id'], len(e['entrance_candidates'])))
    add('')
    add('## 6. 本阶段未做')
    add('')
    add('- 未计算靠近点/座位点/交互姿态（stage 3 生成期 recorder）。')
    add('- 未接入 Unity 运行时，未新增障碍/碰撞体，未改场景与 prefab。')
    add('- 朝向只对长椅/车辆做推断与校验；路灯、树、围栏等无“正面”，`orientation.yaw_unity_deg = null`。')
    add('- 多面建筑只记录立面列表，不强行给单一 yaw。')
    add('')
    with open(os.path.join(OUT_DIR, 'grouping_report.md'), 'w', encoding='utf-8') as f:
        f.write('\n'.join(lines))


write_report()
print('EXPORT_DONE entities=%d mesh=%d out=%s' % (len(entities), len(MESHES), OUT_DIR))
