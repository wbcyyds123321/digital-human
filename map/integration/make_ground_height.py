"""从 SITE 网格烘焙“地面高度图”（Unity 实际坐标），给 GroundSnap 动态找地面用。

为什么需要：SovietBlueHour 地图的可行走面不是平的——
  - 广场/人行道/车道（SURF_GRANITE / SURF_CONCRETE / SURF_KERB / SURF_ASPHALT）在 y≈0.15~0.17；
  - 公园草地/泥土/沙地（SURF_GRASS / SURF_SOIL / SURF_SAND）在 y≈0.0~0.03；
  - 纪念台地/台阶在 y≈0.5（角色不可攀爬，按障碍处理）。
角色若固定 GroundY=0 站在广场上，脚会埋进地面约 15cm；固定 0.15 又会在草地上悬空。
所以烘焙一张 1m 网格高度图，运行时 GroundSnap 按角色所在位置取地面高度。

输出：Maps/SovietBlueHour/integration/ground_height.json
  { schema, min_x, min_z, cell, w, h, heights:[row-major, w*h] }
取“该点所有低面(<0.6m)三角形中最高”的值：可以同时正确处理
  - 铺装板盖在基础地面上（取铺装顶面）
  - 纪念台地/浮雕墙等高处三角形（>0.6m 忽略，仍取脚下地面）
"""
import json
import struct
import os

ROOT = os.environ.get("SBH_PROJECT_ROOT", ".")
MAP = os.path.join(ROOT, "Maps", "SovietBlueHour", "map")
OUT = os.path.join(ROOT, "Maps", "SovietBlueHour", "integration")
GLB = os.path.join(MAP, "SovietBlueHour_game.glb")

MIN_X, MAX_X = -200.0, 200.0
MIN_Z, MAX_Z = -152.0, 152.0
CELL = 1.0
W = int(round((MAX_X - MIN_X) / CELL)) + 1
H = int(round((MAX_Z - MIN_Z) / CELL)) + 1
LOW_MAX = 0.6   # 只把低于这个高度的面当作“可行走地面”

# ---------------------------------------------------------------- 读 GLB 的 SITE 三角形
f = open(GLB, "rb")
struct.unpack("<4sII", f.read(12))
clen, ctype = struct.unpack("<II", f.read(8))
js = f.read(clen)
blen, btype = struct.unpack("<II", f.read(8))
bin_data = f.read(blen)
f.close()
doc = json.loads(js.decode("utf-8"))

site = next(n for n in doc["nodes"] if n.get("name") == "SITE")
mesh = doc["meshes"][site["mesh"]]
ctypes = {5126: ("f", 4), 5123: ("H", 2), 5125: ("I", 4)}


def read_accessor(ai):
    acc = doc["accessors"][ai]
    bv = doc["bufferViews"][acc["bufferView"]]
    comp, size = ctypes[acc["componentType"]]
    ncomp = {"SCALAR": 1, "VEC2": 2, "VEC3": 3, "VEC4": 4}[acc["type"]]
    stride = bv.get("byteStride", size * ncomp)
    off = bv.get("byteOffset", 0) + acc.get("byteOffset", 0)
    return [struct.unpack_from("<" + comp * ncomp, bin_data, off + i * stride)
            for i in range(acc["count"])]


tris = []
for prim in mesh["primitives"]:
    pos = read_accessor(prim["attributes"]["POSITION"])
    idx = read_accessor(prim["indices"]) if "indices" in prim else None
    order = [i[0] for i in idx] if idx else list(range(len(pos)))
    for k in range(0, len(order) - 2, 3):
        # Unity 实际坐标 = (gltf.x, gltf.y(高度), -gltf.z)
        ps = [(pos[order[k + t]][0], -pos[order[k + t]][2], pos[order[k + t]][1]) for t in range(3)]
        tris.append(ps)

print("SITE triangles:", len(tris))

heights = [0.0] * (W * H)
covered = [False] * (W * H)

for ti, (a, b, c) in enumerate(tris):
    xs = [a[0], b[0], c[0]]
    zs = [a[1], b[1], c[1]]
    if max(a[2], b[2], c[2]) < -0.2:
        continue
    x0 = max(MIN_X, min(xs)); x1 = min(MAX_X, max(xs))
    z0 = max(MIN_Z, min(zs)); z1 = min(MAX_Z, max(zs))
    if x1 < x0 or z1 < z0:
        continue
    ix0 = int((x0 - MIN_X) / CELL); ix1 = int((x1 - MIN_X) / CELL)
    iz0 = int((z0 - MIN_Z) / CELL); iz1 = int((z1 - MIN_Z) / CELL)
    # 大三角形（地面板）跨越整张图，说明采样点很多；小三角形只覆盖几个格
    den = (b[1] - c[1]) * (a[0] - c[0]) + (c[0] - b[0]) * (a[1] - c[1])
    if abs(den) < 1e-12:
        continue
    for iz in range(max(0, iz0), min(H - 1, iz1) + 1):
        pz = MIN_Z + iz * CELL
        for ix in range(max(0, ix0), min(W - 1, ix1) + 1):
            px = MIN_X + ix * CELL
            w1 = ((b[1] - c[1]) * (px - c[0]) + (c[0] - b[0]) * (pz - c[1])) / den
            if w1 < -1e-6 or w1 > 1 + 1e-6:
                continue
            w2 = ((c[1] - a[1]) * (px - c[0]) + (a[0] - c[0]) * (pz - c[1])) / den
            if w2 < -1e-6 or w1 + w2 > 1 + 1e-6:
                continue
            w3 = 1.0 - w1 - w2
            y = w1 * a[2] + w2 * b[2] + w3 * c[2]
            if y > LOW_MAX or y < -1.0:
                continue
            k = iz * W + ix
            if y > heights[k]:
                heights[k] = y
            covered[k] = True

uncovered = sum(1 for c in covered if not c)
print("grid %dx%d, uncovered=%d" % (W, H, uncovered))
# 未覆盖的格子（理论上只有地图外）用最近的已覆盖值兜底：这里简单取 0
heights = [round(v, 3) for v in heights]

out = {
    "schema": "sbh.unity.ground_height.v1",
    "min_x": MIN_X, "min_z": MIN_Z,
    "cell": CELL, "w": W, "h": H,
    "heights": heights,
}
path = os.path.join(OUT, "ground_height.json")
json.dump(out, open(path, "w", encoding="utf-8"), ensure_ascii=False, separators=(",", ":"))
print("wrote", path, os.path.getsize(path) / 1e6, "MB")

# 基本统计（只统计有覆盖的格子）
vals = [heights[i] for i in range(len(heights)) if covered[i]]
vals.sort()
print("covered cells:", len(vals),
      "min %.3f p10 %.3f median %.3f p90 %.3f max %.3f" % (
          vals[0], vals[len(vals) // 10], vals[len(vals) // 2], vals[len(vals) * 9 // 10], vals[-1]))

# 抽查关键点
def at(x, z):
    ix = int(round((x - MIN_X) / CELL))
    iz = int(round((z - MIN_Z) / CELL))
    if 0 <= ix < W and 0 <= iz < H:
        return heights[iz * W + ix]
    return None

for p in [(0, -60), (-14, -62), (0, -72), (0, -40), (0, -56.5), (30, -90), (5, 91.5), (-188, 20)]:
    print("  ground @", p, "->", at(*p))
