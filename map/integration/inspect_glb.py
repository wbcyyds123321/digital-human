import json, struct, sys, os, collections

GLB = os.environ.get("SBH_GLB", os.path.join(os.path.dirname(__file__), "..", "map", "SovietBlueHour_game.glb"))

with open(GLB, 'rb') as f:
    magic, version, length = struct.unpack('<4sII', f.read(12))
    assert magic == b'glTF', magic
    clen, ctype = struct.unpack('<II', f.read(8))
    json_bytes = f.read(clen)

doc = json.loads(json_bytes.decode('utf-8'))
print('glb version', version, 'json bytes', clen)
for key in ('scenes', 'nodes', 'meshes', 'materials', 'textures', 'images', 'accessors', 'bufferViews'):
    print(key, len(doc.get(key, [])))

# asset info
print('asset:', doc.get('asset'))

# node transforms / hierarchy
nodes = doc.get('nodes', [])
roots = set(range(len(nodes)))
for i, n in enumerate(nodes):
    for c in n.get('children', []):
        roots.discard(c)
print('\nroot nodes:', [(i, nodes[i].get('name')) for i in sorted(roots)])

# look for scene offset / translation on roots
for i in sorted(roots):
    n = nodes[i]
    print('root', i, n.get('name'), 'T=', n.get('translation'), 'R=', n.get('rotation'), 'S=', n.get('scale'), 'M=', n.get('matrix'))

# compute rough bbox from accessor min/max of positions per mesh, with node transforms ignored first
mesh_bbox = {}
for i, n in enumerate(nodes):
    for p in n.get('children', []) if False else []:
        pass
# simpler: use mesh index of node
for i, n in enumerate(nodes):
    mi = n.get('mesh')
    if mi is None:
        continue
    mesh = doc['meshes'][mi]
    mn = [1e30, 1e30, 1e30]
    mx = [-1e30, -1e30, -1e30]
    for prim in mesh.get('primitives', []):
        acc = doc['accessors'][prim['attributes']['POSITION']]
        if 'min' in acc and 'max' in acc:
            for k in range(3):
                mn[k] = min(mn[k], acc['min'][k])
                mx[k] = max(mx[k], acc['max'][k])
    mesh_bbox[i] = (n.get('name'), mn, mx)

allmn = [1e30]*3
allmx = [-1e30]*3
for name, mn, mx in mesh_bbox.values():
    for k in range(3):
        allmn[k] = min(allmn[k], mn[k]); allmx[k] = max(allmx[k], mx[k])
print('\nrough local bbox (no node transforms):', allmn, allmx)

# material stats
mats = doc.get('materials', [])
stat = collections.Counter()
for m in mats:
    pbr = m.get('pbrMetallicRoughness', {})
    if 'baseColorTexture' in pbr: stat['baseColorTex'] += 1
    if 'metallicRoughnessTexture' in pbr: stat['mrTex'] += 1
    if 'baseColorFactor' in pbr: stat['baseColorFactor'] += 1
    if 'normalTexture' in m: stat['normalTex'] += 1
    if 'emissiveTexture' in m: stat['emissiveTex'] += 1
    if 'emissiveFactor' in m: stat['emissiveFactor'] += 1
    e = m.get('extensions', {})
    if 'KHR_materials_emissive_strength' in e: stat['emissiveStrength'] += 1
    if 'alphaMode' in m: stat['alphaMode:' + m['alphaMode']] += 1
    stat['doubleSided' if m.get('doubleSided') else 'singleSided'] += 1
print('\nmaterial stats:', dict(stat))
# emissive strength values
es = [m.get('extensions', {}).get('KHR_materials_emissive_strength', {}).get('emissiveStrength') for m in mats]
es = [x for x in es if x is not None]
print('emissive strength count', len(es), 'min', min(es) if es else None, 'max', max(es) if es else None)

# sample material names
print('\nsample materials:')
for m in mats[:15]:
    print(' -', m.get('name'), 'emissive=', m.get('emissiveFactor'), 'strength=', m.get('extensions', {}).get('KHR_materials_emissive_strength'))

# images
imgs = doc.get('images', [])
print('\nimages', len(imgs))
for im in imgs[:5]:
    print(' -', im.get('name'), im.get('mimeType'), 'bufferView' in im)

# total texture byte size
total = 0
for im in imgs:
    bv = im.get('bufferView')
    if bv is not None:
        total += doc['bufferViews'][bv].get('byteLength', 0)
print('texture bytes total', total / 1e6, 'MB')

# primitive/material count per mesh
print('\nmeshes:')
for i, m in enumerate(doc['meshes']):
    names = [mats[p['material']].get('name') if 'material' in p else None for p in m.get('primitives', [])]
    uniq = len(set(n for n in names if n))
    print(' %2d %-18s prims=%4d uniq_mat=%3d' % (i, m.get('name'), len(m.get('primitives', [])), uniq))
