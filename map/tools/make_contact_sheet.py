"""Contact sheet of the final renders (system Python + Pillow)."""
import os
import sys

from PIL import Image, ImageDraw, ImageFont

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SRC = os.path.join(ROOT, "renders", "final")
OUT = os.path.join(ROOT, "renders", "final_contact_sheet_r4.png")

ORDER = [
    ("CAM_HERO", "HERO 3/4  |  Soviet Blue-Hour Microdistrict (400x300 m)"),
    ("CAM_HERO2", "HERO 3/4 (opposite)"),
    ("CAM_TOP_map", "TOP-DOWN MASTER PLAN RENDER"),
    ("CAM_ESPLANADE", "MONUMENT ESPLANADE  |  steps + reflecting pools"),
    ("CAM_MOTHERLAND", "MOTHERLAND  |  esplanade + eternal flame"),
    ("CAM_LM_MOTHERLAND", "MOTHERLAND  |  rebuilt figure, 40 m"),
    ("CAM_VYSOTKA", "STALINIST VYSOTKAS  |  MSU / Ukraina / MFA"),
    ("CAM_LM_MSU", "MSU MAIN BUILDING  |  tiers + spire + star"),
    ("CAM_LM_UKRAINA", "HOTEL UKRAINA  |  ochre tower"),
    ("CAM_LM_MFA", "FOREIGN MINISTRY  |  granite fin slab"),
    ("CAM_EAST_Q", "EAST QUARTER  |  Sino-Soviet institute"),
    ("CAM_LM_MUSEUM", "MILITARY MUSEUM  |  colonnade + star tower"),
    ("CAM_LM_HIT", "SINO-SOVIET INSTITUTE  |  portico + hip roof"),
    ("CAM_LM_METRO", "METRO PAVILION  |  marble pylon hall"),
    ("CAM_LM_BREST", "BREST COURAGE RELIEF WALL"),
    ("CAM_NORTH_PARK", "NORTH PARK  |  pond + pavilion + towers"),
    ("CAM_DRUZHBA", "DRUZHBA SANATORIUM  |  curved mosaic arcade"),
    ("CAM_LM_DRUZHBA", "DRUZHBA  |  arcade close-up"),
    ("CAM_TERMINAL", "SOUTH TRANSIT TERMINAL  |  UFO + hexagon stations"),
    ("CAM_LM_UFO", "ARMENIAN UFO BUS STATION"),
    ("CAM_LM_HEX", "TAJIK HEXAGON STATION"),
    ("CAM_LM_ROBOTICS", "ROBOTICS INSTITUTE  |  shell + robot arm"),
    ("CAM_LM_MOT", "TRANSPORT MINISTRY  |  fins + relief"),
    ("CAM_PLAZA", "CENTRAL PLAZA  |  obelisk + House of Culture"),
    ("CAM_DK", "HOUSE OF CULTURE  |  mosaic + poster cases"),
    ("CAM_AVE", "LENIN AVENUE  |  tram + bus stop + catenary"),
    ("CAM_W2_COURT", "WEST-2 COURTYARD  |  sparse court lamp + rocket"),
    ("CAM_SOUTH_ST", "SOUTH STRIP  |  cinema ZARYA + marquee"),
    ("CAM_MARKET", "MARKET HALL  |  RYNOK"),
    ("CAM_BANYA", "PUBLIC BATHHOUSE  |  BANYA + chimney"),
    ("CAM_COURT_W", "WEST COURTYARD  |  khrushchyovka + Lada"),
    ("CAM_COURT_E", "EAST COURTYARD"),
    ("CAM_ARCH", "ARCHWAY PASSAGE (through-building shortcut)"),
    ("CAM_SCHOOL", "SCHOOL No.3"),
    ("CAM_BOILER", "BOILER HOUSE + 38 m CHIMNEY"),
    ("CAM_STADIUM", "STADIUM GRANDSTAND + FLOODLIGHTS"),
    ("CAM_GARAGES", "GARAGE COOPERATIVE"),
    ("CAM_TOWER", "9-STOREY PANEL TOWER"),
]

IMG_W, IMG_H = 640, 360
COLS = 3
PAD = 16
LABEL_H = 30


def font(size, bold=False):
    for p in (r"C:\Windows\Fonts\msyhbd.ttc" if bold else r"C:\Windows\Fonts\msyh.ttc",
              r"C:\Windows\Fonts\segoeuib.ttf" if bold else r"C:\Windows\Fonts\segoeui.ttf"):
        if os.path.exists(p):
            try:
                return ImageFont.truetype(p, size)
            except Exception:
                pass
    return ImageFont.load_default()


def main():
    items = []
    for name, caption in ORDER:
        p = os.path.join(SRC, name + ".png")
        if os.path.exists(p):
            items.append((p, caption))
    rows = (len(items) + COLS - 1) // COLS
    W = COLS * IMG_W + (COLS + 1) * PAD
    H = rows * (IMG_H + LABEL_H) + (rows + 1) * PAD + 70
    sheet = Image.new("RGB", (W, H), (14, 20, 30))
    d = ImageDraw.Draw(sheet)
    d.text((PAD, 18), "SOVIET BLUE-HOUR MICRODISTRICT — FINAL RENDERS / 苏式蓝调小区成品渲染",
           font=font(28, True), fill=(225, 232, 240))
    d.text((PAD, 48), "240 × 180 m · 48 buildings · Blender 5.2 EEVEE · deep pre-dawn blue hour",
           font=font(15), fill=(140, 152, 168))
    y = 74
    for i, (p, caption) in enumerate(items):
        col = i % COLS
        row = i // COLS
        x = PAD + col * (IMG_W + PAD)
        yy = y + row * (IMG_H + LABEL_H + PAD)
        im = Image.open(p).convert("RGB")
        im.thumbnail((IMG_W, IMG_H), Image.LANCZOS)
        sheet.paste(im, (x, yy))
        d.text((x, yy + IMG_H + 7), caption, font=font(13, True), fill=(200, 208, 218))
    sheet.save(OUT)
    print("SHEET", OUT, sheet.size)


if __name__ == "__main__":
    main()
