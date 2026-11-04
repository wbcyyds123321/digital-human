"""Draw the precise 2D district plan (system Python + Pillow; Blender Python has no PIL).

Usage:
    python make_plan.py            # -> map/district_plan.png
    python make_plan.py --check    # print layout stats
"""
from __future__ import annotations

import json
import os
import sys

from PIL import Image, ImageDraw, ImageFont

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
PLAN_PATH = os.path.join(ROOT, "tools", "plan.json")
OUT_PATH = os.path.join(ROOT, "map", "district_plan.png")

PX_PER_M = 5.4
MAP_W_M, MAP_H_M = 400, 300
MAP_PX_W, MAP_PX_H = int(MAP_W_M * PX_PER_M), int(MAP_H_M * PX_PER_M)
LEGEND_W = 560
PAD = 74
W = MAP_PX_W + LEGEND_W + PAD * 2
H = MAP_PX_H + PAD * 2 + 70

# cold blue-hour palette
C_BG = (14, 20, 30)
C_MAP_BG = (23, 32, 44)
C_LAWN = (34, 47, 52)
C_DIRT = (48, 48, 45)
C_ASPHALT = (44, 51, 62)
C_ASPHALT_WORN = (50, 55, 64)
C_WALK = (72, 78, 88)
C_PLAZA = (86, 90, 100)
C_PLAZA_JOINT = (72, 76, 86)
C_STADIUM = (40, 62, 48)
C_WATER = (26, 52, 66)
C_SPORT = (44, 58, 62)
C_PLAY = (52, 58, 56)
C_BUILD = {
    "khrush_panel": (98, 116, 118),
    "khrush_brick": (108, 104, 96),
    "khrush_arch": (92, 112, 116),
    "khrush_mod": (104, 118, 112),
    "brezhnevka_9": (82, 100, 112),
    "dk": (112, 108, 100),
    "school": (100, 110, 104),
    "kindergarten": (110, 116, 104),
    "boiler": (92, 82, 76),
    "shop": (104, 108, 100),
    "gym": (96, 104, 108),
    "clinic": (102, 110, 110),
    "garages": (78, 78, 80),
    "stadium_stand": (84, 88, 92),
    "frame": (70, 72, 78),
    "kiosk": (90, 90, 84),
    "banya": (96, 92, 88),
    "statue_motherland": (150, 150, 140),
    "relief_wall": (120, 116, 104),
    "metro_pavilion": (110, 104, 92),
    "vysotka_main": (128, 124, 108),
    "vysotka_hotel": (134, 120, 102),
    "vysotka_ministry": (120, 120, 116),
    "institute_sino": (150, 136, 108),
    "museum_colonnade": (140, 126, 104),
    "institute_robotics": (104, 116, 118),
    "ministry_transport": (108, 114, 120),
    "sanatorium_druzhba": (120, 128, 112),
    "bus_station_ufo": (124, 128, 132),
    "bus_station_hex": (128, 132, 122),
    "market": (92, 100, 94),
    "cinema": (110, 106, 96),
    "dombyta": (100, 108, 108),
    "bakery": (108, 102, 92),
}
C_OUTLINE = (26, 32, 42)
C_TEXT = (214, 222, 232)
C_TEXT_DIM = (140, 152, 166)
C_ACCENT = (222, 160, 92)
C_ROAD_LINE = (150, 158, 168)
C_TRAM = (170, 178, 188)
C_GREEN_EDGE = (44, 60, 62)


def load_font(size, bold=False):
    cands = [
        r"C:\Windows\Fonts\msyhbd.ttc" if bold else r"C:\Windows\Fonts\msyh.ttc",
        r"C:\Windows\Fonts\segoeuib.ttf" if bold else r"C:\Windows\Fonts\segoeui.ttf",
        r"C:\Windows\Fonts\arialbd.ttf" if bold else r"C:\Windows\Fonts\arial.ttf",
    ]
    for c in cands:
        if os.path.exists(c):
            try:
                return ImageFont.truetype(c, size)
            except Exception:
                pass
    return ImageFont.load_default()


def mx(x):
    return PAD + (x + MAP_W_M / 2) * PX_PER_M


def my(y):
    return PAD + (MAP_H_M / 2 - y) * PX_PER_M


def rect_px(r):
    x0, y0, x1, y1 = r
    return [mx(x0), my(y1), mx(x1), my(y0)]


def rect_center(r):
    return ((r[0] + r[2]) / 2, (r[1] + r[3]) / 2)


def main():
    plan = json.load(open(PLAN_PATH, encoding="utf-8"))
    os.makedirs(os.path.dirname(OUT_PATH), exist_ok=True)
    img = Image.new("RGB", (W, H), C_BG)
    d = ImageDraw.Draw(img, "RGBA")

    f_title = load_font(34, True)
    f_sub = load_font(19)
    f_label = load_font(15)
    f_small = load_font(13)
    f_tiny = load_font(11)
    f_legend = load_font(15)
    f_legend_h = load_font(19, True)

    # map plate
    d.rectangle([PAD - 6, PAD - 6, PAD + MAP_PX_W + 6, PAD + MAP_PX_H + 6],
                fill=C_MAP_BG, outline=(38, 48, 62), width=3)

    def fill_rect(r, color, outline=None, width=1, alpha=None):
        box = rect_px(r)
        if alpha is not None:
            layer = Image.new("RGBA", img.size, (0, 0, 0, 0))
            ImageDraw.Draw(layer).rectangle(box, fill=color + (alpha,))
            img.paste(Image.alpha_composite(img.convert("RGBA"), layer).convert("RGB"), (0, 0))
        else:
            d.rectangle(box, fill=color, outline=outline, width=width)

    # base ground (grass/dirt base)
    fill_rect([-120, -90, 120, 90], C_LAWN)

    # zones
    zone_layer = Image.new("RGBA", img.size, (0, 0, 0, 0))
    zd = ImageDraw.Draw(zone_layer)
    for z in plan["zones"]:
        kind = z["kind"]
        color = {
            "plaza": C_PLAZA, "terrace": C_PLAZA, "steps": C_WALK,
            "court": C_ASPHALT_WORN, "parking": (58, 64, 72), "lawn": C_LAWN,
            "dirt": C_DIRT, "playground": C_PLAY, "sport": C_SPORT,
            "stadium": C_STADIUM, "water": C_WATER,
        }.get(kind, C_LAWN)
        if kind == "water":
            zd.ellipse(rect_px(z["rect"]), fill=color + (255,))
        else:
            zd.rectangle(rect_px(z["rect"]), fill=color + (255,))
    img = Image.alpha_composite(img.convert("RGBA"), zone_layer).convert("RGB")
    d = ImageDraw.Draw(img, "RGBA")

    # plaza pavement joints
    plaza = next((z for z in plan["zones"] if z["id"] == "PLAZA"), None)
    if plaza:
        x0, y0, x1, y1 = plaza["rect"]
        for gx in range(int(x0), int(x1) + 1, 4):
            d.line([mx(gx), my(y0), mx(gx), my(y1)], fill=C_PLAZA_JOINT + (120,), width=1)
        for gy in range(int(y0), int(y1) + 1, 4):
            d.line([mx(x0), my(gy), mx(x1), my(gy)], fill=C_PLAZA_JOINT + (120,), width=1)

    # roads
    for road in plan["roads"]:
        r = road["rect"]
        d.rectangle(rect_px(r), fill=C_ASPHALT)
        kind = road["kind"]
        if kind == "avenue":
            # center dashes
            ymid = (r[1] + r[3]) / 2
            for x in range(int(r[0]), int(r[2]), 6):
                d.line([mx(x), my(ymid), mx(x + 3), my(ymid)], fill=C_ROAD_LINE + (160,), width=2)
        elif kind in ("drive", "street"):
            xmid = (r[0] + r[2]) / 2
            if r[2] - r[0] > r[3] - r[1]:
                ymid = (r[1] + r[3]) / 2
                for x in range(int(r[0]), int(r[2]), 7):
                    d.line([mx(x), my(ymid), mx(x + 3), my(ymid)], fill=C_ROAD_LINE + (90,), width=1)
            else:
                for y in range(int(r[1]), int(r[3]), 7):
                    d.line([mx(xmid), my(y), mx(xmid), my(y + 3)], fill=C_ROAD_LINE + (90,), width=1)

    # tram rails
    tram = plan.get("tram")
    if tram:
        for ty in tram["track_y"]:
            d.line([mx(-120), my(ty), mx(120), my(ty)], fill=C_TRAM, width=2)
            for tx in range(-120, 120, 4):
                d.line([mx(tx), my(ty - 0.9), mx(tx), my(ty + 0.9)], fill=(80, 86, 96, 160), width=2)
        if tram.get("stop_platform"):
            p = tram["stop_platform"]
            d.rectangle(rect_px(p), fill=(96, 100, 108), outline=(60, 66, 76), width=2)

    # sidewalks
    for sw in plan["sidewalks"]:
        d.rectangle(rect_px(sw["rect"]), fill=C_WALK)

    # paths
    for p in plan["paths"]:
        wpx = max(2, p["width"] * PX_PER_M)
        pts = [(mx(x), my(y)) for x, y in p["pts"]]
        d.line(pts, fill=(120, 112, 98), width=int(wpx), joint="curve")
        for (x, y) in pts:
            r = wpx / 2
            d.ellipse([x - r, y - r, x + r, y + r], fill=(120, 112, 98))

    # buildings
    for b in plan["blocks"]:
        r = b["rect"]
        base = C_BUILD.get(b["type"], (100, 108, 110))
        d.rectangle(rect_px(r), fill=base, outline=C_OUTLINE, width=2)
        # roof hatch
        x0, y0, x1, y1 = r
        if x1 - x0 > 8 and y1 - y0 > 8:
            for yy in range(int(y0) + 3, int(y1), 3):
                d.line([mx(x0 + 1.5), my(yy), mx(x1 - 1.5), my(yy)], fill=(0, 0, 0, 22), width=1)
        cx, cy = rect_center(r)
        # facade-facing arrows: interior -> facade
        for face in b.get("faces", []):
            if face == "PY":
                d.line([mx(cx), my(y1 - 2), mx(cx), my(y1 + 1.2)], fill=C_ACCENT + (230,), width=2)
            elif face == "NY":
                d.line([mx(cx), my(y0 + 2), mx(cx), my(y0 - 1.2)], fill=C_ACCENT + (230,), width=2)
            elif face == "PX":
                d.line([mx(x1 - 2), my(cy), mx(x1 + 1.2), my(cy)], fill=C_ACCENT + (230,), width=2)
            elif face == "NX":
                d.line([mx(x0 + 2), my(cy), mx(x0 - 1.2), my(cy)], fill=C_ACCENT + (230,), width=2)
        label = b["cn"]
        tw = d.textlength(label, font=f_label)
        cx_px, cy_px = mx(cx), my(cy)
        d.text((cx_px - tw / 2, cy_px - 8), label, font=f_label, fill=(235, 240, 245))
        sub = f"{b['type']} · {b.get('floors', '?')}F"
        tw2 = d.textlength(sub, font=f_tiny)
        d.text((cx_px - tw2 / 2, cy_px + 10), sub, font=f_tiny, fill=(170, 180, 192))

    # landmarks
    for lm in plan["landmarks"]:
        x, y = lm["pos"]
        px, py = mx(x), my(y)
        t = lm["type"]
        if t == "obelisk":
            d.ellipse([px - 9, py - 9, px + 9, py + 9], fill=C_ACCENT, outline=(40, 30, 20), width=2)
            d.line([px, py - 9, px, py - 26], fill=C_ACCENT, width=3)
            d.text((px + 12, py - 8), "方尖碑", font=f_small, fill=C_ACCENT)
        elif t == "fountain_dry":
            r = lm["radius"] * PX_PER_M
            d.ellipse([px - r, py - r, px + r, py + r], outline=(120, 126, 136), width=2)
        elif t == "flagpoles":
            for i in range(lm["count"]):
                fx = px + (i - 1) * 14
                d.line([fx, py, fx, py - 16], fill=(200, 208, 216), width=2)
        elif t == "bus_stop":
            d.rectangle([px - 14, py - 8, px + 14, py + 8], fill=(70, 110, 150), outline=(30, 40, 50))
            d.text((px + 18, py - 8), "公交站", font=f_small, fill=(170, 200, 230))
        elif t == "tram_stop":
            d.rectangle([px - 16, py - 6, px + 16, py + 6], fill=(70, 90, 120), outline=(30, 40, 50))
            d.text((px + 20, py - 6), "电车站", font=f_small, fill=(170, 190, 220))
        elif t == "pavilion":
            d.ellipse([px - 10, py - 10, px + 10, py + 10], outline=C_ACCENT, width=2)
            d.ellipse([px - 3, py - 3, px + 3, py + 3], fill=C_ACCENT)
            d.text((px + 13, py - 8), "公园凉亭", font=f_small, fill=C_ACCENT)
        elif t == "park_sign":
            d.rectangle([px - 10, py - 6, px + 10, py + 6], fill=(70, 96, 78),
                        outline=C_OUTLINE)
            d.text((px + 14, py - 6), "公园入口", font=f_small, fill=(160, 196, 168))
        elif t == "billboard":
            w, h = lm["size"]
            d.rectangle([px - w * PX_PER_M / 2, py - 3, px + w * PX_PER_M / 2, py + 3],
                        fill=(96, 72, 60), outline=(40, 32, 28))
        elif t == "poster_board":
            w, h = lm["size"]
            d.rectangle([px - w * PX_PER_M / 2, py - 2.5, px + w * PX_PER_M / 2, py + 2.5],
                        fill=(88, 96, 104), outline=(38, 44, 52))

    # trees from prop zones (dots only)
    for pz in plan["prop_zones"]:
        if pz["type"].endswith("_row") or pz["type"].endswith("_cluster"):
            x0, y0, x1, y1 = pz["rect"]
            if pz.get("step"):
                if x1 - x0 >= y1 - y0:
                    n = max(1, int((x1 - x0) / pz["step"]))
                    for i in range(n + 1):
                        x = x0 + (x1 - x0) * i / n
                        y = (y0 + y1) / 2
                        d.ellipse([mx(x) - 2.6, my(y) - 2.6, mx(x) + 2.6, my(y) + 2.6],
                                  fill=(54, 82, 66))
                else:
                    n = max(1, int((y1 - y0) / pz["step"]))
                    for i in range(n + 1):
                        y = y0 + (y1 - y0) * i / n
                        x = (x0 + x1) / 2
                        d.ellipse([mx(x) - 2.6, my(y) - 2.6, mx(x) + 2.6, my(y) + 2.6],
                                  fill=(54, 82, 66))
            else:
                count = pz.get("count", 4)
                for i in range(count):
                    fx = (i * 0.37 + 0.11) % 1.0
                    fy = (i * 0.61 + 0.23) % 1.0
                    x = x0 + (x1 - x0) * fx
                    y = y0 + (y1 - y0) * fy
                    d.ellipse([mx(x) - 2.6, my(y) - 2.6, mx(x) + 2.6, my(y) + 2.6],
                              fill=(54, 82, 66))

    # park / pond labels
    for z in plan["zones"]:
        if z["kind"] == "water":
            x0, y0, x1, y1 = z["rect"]
            cx, cy = (x0 + x1) / 2, (y0 + y1) / 2
            d.text((mx(cx) - 16, my(cy) - 7), z.get("cn", "水面"), font=f_small,
                   fill=(150, 190, 210))
        if z["id"] == "PARK_LAWN":
            d.text((mx(-70), my(106)), "北 公 园", font=load_font(22, True), fill=(110, 150, 120))

    # playground markers
    for pz in plan["prop_zones"]:
        if pz["type"] == "playground":
            x0, y0, x1, y1 = pz["rect"]
            cx, cy = (x0 + x1) / 2, (y0 + y1) / 2
            d.ellipse([mx(cx) - 5, my(cy) - 5, mx(cx) + 5, my(cy) + 5],
                      outline=(150, 120, 80), width=2)
            d.text((mx(cx) + 7, my(cy) - 6), "游乐", font=f_tiny, fill=(180, 150, 100))

    # frame title on map
    d.text((PAD + 18, PAD + 12), "苏式蓝调小区 · 详细总平面", font=load_font(26, True), fill=C_TEXT)
    d.text((PAD + 18, PAD + 46), f"SOVIET BLUE-HOUR MICRODISTRICT · MASTER PLAN · {MAP_W_M} x {MAP_H_M} m",
           font=load_font(14), fill=C_TEXT_DIM)

    # north arrow on map
    nx, ny = PAD + MAP_PX_W - 48, PAD + 58
    d.polygon([(nx, ny - 26), (nx - 10, ny), (nx, ny - 7), (nx + 10, ny)], fill=C_TEXT)
    d.text((nx - 6, ny + 4), "N", font=load_font(18, True), fill=C_TEXT)

    # ---------------- legend ----------------
    lx = PAD + MAP_PX_W + 44
    ly = PAD + 8
    d.text((lx, ly), "总平面图例 / LEGEND", font=f_legend_h, fill=C_TEXT)
    ly += 40
    type_cn = {
        "khrush_panel": "赫鲁晓夫楼（预制板）", "khrush_brick": "赫鲁晓夫楼（砖）",
        "khrush_arch": "赫鲁晓夫楼（拱门穿楼）", "khrush_mod": "改造赫鲁晓夫楼",
        "brezhnevka_9": "九层勃列日涅夫板楼", "dk": "文化宫",
        "school": "学校", "kindergarten": "幼儿园", "boiler": "锅炉房",
        "shop": "食品商店", "gym": "体育馆", "clinic": "门诊部",
        "garages": "车库合作社", "stadium_stand": "体育场看台",
        "frame": "烂尾楼", "kiosk": "售货亭",
        "banya": "公共浴场", "market": "市场", "cinema": "电影院",
        "dombyta": "生活服务楼", "bakery": "面包房",
        "statue_motherland": "祖国母亲雕像", "relief_wall": "勇气浮雕墙",
        "metro_pavilion": "地铁站亭", "vysotka_main": "国立大学主楼",
        "vysotka_hotel": "乌克兰饭店", "vysotka_ministry": "外交部大楼",
        "institute_sino": "中苏学堂", "museum_colonnade": "军事博物馆",
        "institute_robotics": "机器人研究院", "ministry_transport": "交通部大楼",
        "sanatorium_druzhba": "友谊疗养院", "bus_station_ufo": "飞碟车站",
        "bus_station_hex": "六边形车站",
    }
    for key, cn in type_cn.items():
        d.rectangle([lx, ly, lx + 18, ly + 12], fill=C_BUILD[key], outline=C_OUTLINE)
        d.text((lx + 26, ly - 2), f"{cn}", font=f_legend, fill=C_TEXT)
        ly += 22

    ly += 8
    d.text((lx, ly), "地面 / SURFACES", font=f_legend_h, fill=C_TEXT)
    ly += 34
    for color, name in [(C_ASPHALT, "沥青道路"), (C_WALK, "人行道"), (C_PLAZA, "广场花岗岩"),
                        (C_ASPHALT_WORN, "院落水泥地"), (C_LAWN, "草地/绿地"),
                        (C_DIRT, "泥地场院"), (C_SPORT, "运动场地"), (C_PLAY, "儿童游戏场"),
                        (C_WATER, "公园水面")]:
        d.rectangle([lx, ly, lx + 18, ly + 12], fill=color, outline=C_OUTLINE)
        d.text((lx + 26, ly - 2), name, font=f_legend, fill=C_TEXT)
        ly += 22

    ly += 8
    d.text((lx, ly), "符号 / SYMBOLS", font=f_legend_h, fill=C_TEXT)
    ly += 34
    d.rectangle([lx, ly, lx + 18, ly + 12], fill=(70, 110, 150), outline=C_OUTLINE)
    d.text((lx + 26, ly - 2), "公交站", font=f_legend, fill=C_TEXT); ly += 22
    d.rectangle([lx, ly, lx + 18, ly + 12], fill=(96, 72, 60), outline=C_OUTLINE)
    d.text((lx + 26, ly - 2), "广告牌 / 宣传栏", font=f_legend, fill=C_TEXT); ly += 22
    d.ellipse([lx + 4, ly + 2, lx + 14, ly + 12], fill=C_ACCENT)
    d.text((lx + 26, ly - 2), "方尖碑 / 广场焦点", font=f_legend, fill=C_TEXT); ly += 22
    d.ellipse([lx + 4, ly + 2, lx + 14, ly + 12], fill=(54, 82, 66))
    d.text((lx + 26, ly - 2), "树木（杨/桦/椴）", font=f_legend, fill=C_TEXT); ly += 22
    d.line([lx, ly + 6, lx + 18, ly + 6], fill=C_ACCENT, width=2)
    d.text((lx + 26, ly - 2), "临街/主入口朝向", font=f_legend, fill=C_TEXT); ly += 26

    # scale bar
    ly += 10
    scale_m = 50
    bar_w = scale_m * PX_PER_M
    d.rectangle([lx, ly, lx + bar_w, ly + 7], fill=(30, 36, 46), outline=C_TEXT)
    d.rectangle([lx, ly, lx + bar_w / 2, ly + 7], fill=C_TEXT)
    d.text((lx, ly + 14), "0", font=f_small, fill=C_TEXT_DIM)
    d.text((lx + bar_w / 2 - 10, ly + 14), "25", font=f_small, fill=C_TEXT_DIM)
    d.text((lx + bar_w - 20, ly + 14), "50 m", font=f_small, fill=C_TEXT_DIM)

    # stats
    ly += 56
    blocks = plan["blocks"]
    areas = sum((b["rect"][2] - b["rect"][0]) * (b["rect"][3] - b["rect"][1]) for b in blocks)
    base_area = MAP_W_M * MAP_H_M
    stats = [
        f"建筑：{len(blocks)} 栋",
        f"建筑基底：{areas:.0f} m² / 地块 {base_area:,} m²",
        f"道路：{len(plan['roads'])} 条 · 步行道 {len(plan['paths'])} 条",
        f"住宅楼：{sum(1 for b in blocks if b['type'].startswith('khrush') or b['type'].startswith('brezhnevka'))} 栋",
        "焦点：中央广场 / 文化宫 / 锅炉房烟囱",
    ]
    d.text((lx, ly), "指标 / STATS", font=f_legend_h, fill=C_TEXT); ly += 30
    for s in stats:
        d.text((lx, ly), s, font=f_small, fill=C_TEXT_DIM)
        ly += 20

    # footer
    d.text((PAD + 18, H - 44), "由 scripts/make_plan.py 生成 · 坐标单位：米 · Blender Z-up（Y 为北）· "
           "GPT-Image 生成小地图见 ref/A/site_plan.png", font=f_small, fill=(110, 122, 138))

    img.save(OUT_PATH)
    print("PLAN", OUT_PATH, img.size)
    # quick checks
    print("STATS blocks=", len(blocks), "roads=", len(plan["roads"]),
          "paths=", len(plan["paths"]), "zones=", len(plan["zones"]),
          "prop_zones=", len(plan["prop_zones"]))


if __name__ == "__main__":
    main()
