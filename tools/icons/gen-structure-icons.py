"""Generate the 4 missing structure icons in the same 20x20 pixel-art style as
cubiomes-viewer's icon set (dark 1px outline, flat Minecraft-ish palette).

Output: dist/shots/icons/<name>.png   (RGBA, 20x20)
ASCII-only on purpose (no encoding surprises when running through PowerShell).
"""
from PIL import Image
import os

S = 20
HERE = os.path.dirname(os.path.abspath(__file__))
# 直接写回仓库里的资源目录；用完记得重编一次 exe（Resource 是编译期打包的）
OUT = os.path.normpath(os.path.join(HERE, "..", "..", "src", "LunoLauncher", "Assets", "structure-icons"))
os.makedirs(OUT, exist_ok=True)


def canvas():
    return Image.new("RGBA", (S, S), (0, 0, 0, 0))


def rect(im, x0, y0, x1, y1, c):
    for y in range(y0, y1 + 1):
        for x in range(x0, x1 + 1):
            if 0 <= x < S and 0 <= y < S:
                im.putpixel((x, y), c)


def tri(im, apex_x, apex_y, base_y, half, c):
    """solid triangle: apex at (apex_x, apex_y), base at y=base_y, half base width"""
    h = base_y - apex_y
    for y in range(apex_y, base_y + 1):
        t = (y - apex_y) / h if h else 1
        w = int(round(half * t))
        rect(im, apex_x - w, y, apex_x + w, y, c)


def outline(im, c):
    """draw a 1px dark border around every opaque pixel (matches the viewer style)"""
    src = im.copy()
    for y in range(S):
        for x in range(S):
            if src.getpixel((x, y))[3] != 0:
                continue
            near = False
            for dx, dy in ((1, 0), (-1, 0), (0, 1), (0, -1)):
                nx, ny = x + dx, y + dy
                if 0 <= nx < S and 0 <= ny < S and src.getpixel((nx, ny))[3] != 0:
                    near = True
                    break
            if near:
                im.putpixel((x, y), c)


def save(im, name):
    p = os.path.join(OUT, name + ".png")
    im.save(p)
    print("wrote", p)


# ---------------------------------------------------------------- feature
# 0 Feature: pre-1.13 "temple generation attempt" - cobblestone block with a
# yellow target marker in the middle (a candidate point on the old versions).
def make_feature():
    im = canvas()
    DARK = (58, 58, 56, 255)
    BASE = (138, 138, 134, 255)
    LIT = (168, 168, 162, 255)
    SH = (108, 108, 104, 255)
    rect(im, 2, 3, 17, 18, BASE)
    # cobble texture: a few stones
    rect(im, 3, 4, 7, 7, LIT)
    rect(im, 9, 4, 12, 6, SH)
    rect(im, 14, 4, 16, 8, LIT)
    rect(im, 3, 9, 5, 12, SH)
    rect(im, 12, 9, 16, 12, LIT)
    rect(im, 3, 14, 8, 17, SH)
    rect(im, 10, 14, 16, 17, LIT)
    rect(im, 6, 8, 11, 8, SH)
    # target marker: a hollow diamond ring so it reads as "candidate point"
    Y = (242, 199, 68, 255)
    YD = (168, 122, 24, 255)
    ring = [(9, 7), (10, 7), (8, 8), (11, 8), (7, 9), (12, 9),
            (7, 10), (12, 10), (8, 11), (11, 11), (9, 12), (10, 12)]
    for (x, y) in ring:
        im.putpixel((x, y), Y)
    for (x, y) in ((9, 9), (10, 9), (9, 10), (10, 10)):
        im.putpixel((x, y), Y)
    im.putpixel((9, 9), YD)
    im.putpixel((10, 10), YD)
    outline(im, DARK)
    save(im, "feature")


# ---------------------------------------------------------- nether fossil
# 20 Nether_Fossil: a bone (bone blocks), the classic rib-like fossil look.
def make_fossil():
    im = canvas()
    DARK = (46, 40, 30, 255)
    BONE = (232, 228, 210, 255)
    SH = (176, 172, 152, 255)

    # diagonal shaft from bottom-left to top-right (fills most of the canvas)
    pts = [(3, 16), (4, 15), (5, 14), (6, 13), (7, 12), (8, 11), (9, 10),
           (10, 9), (11, 8), (12, 7), (13, 6), (14, 5), (15, 4), (16, 3)]
    for (x, y) in pts:
        rect(im, x - 1, y, x + 1, y + 1, BONE)
    for (x, y) in pts[::2]:
        rect(im, x, y + 1, x + 1, y + 1, SH)

    # knobs (epiphyses) at both ends
    rect(im, 1, 14, 3, 16, BONE)
    rect(im, 2, 17, 4, 18, BONE)
    rect(im, 1, 16, 2, 17, SH)
    rect(im, 16, 1, 18, 3, BONE)
    rect(im, 15, 2, 17, 4, BONE)
    rect(im, 17, 3, 18, 4, SH)

    outline(im, DARK)
    save(im, "nether_fossil")


# ------------------------------------------------------------ end island
# 23 End_Island: a small floating end-stone island (pale top, dark underside).
def make_end_island():
    im = canvas()
    DARK = (48, 44, 32, 255)
    TOP = (224, 216, 174, 255)
    MID = (200, 194, 150, 255)
    UND = (120, 112, 84, 255)

    # island body: flat-ish top, tapering bottom (fills the canvas like the block icons)
    shape = [
        (5, 3, 16),
        (6, 2, 17),
        (7, 2, 17),
        (8, 3, 16),
        (9, 4, 15),
        (10, 5, 14),
        (11, 7, 12),
        (12, 8, 11),
    ]
    for (y, x0, x1) in shape:
        rect(im, x0, y, x1, y, TOP)
    # shading
    for (y, x0, x1) in shape[4:]:
        rect(im, x0, y, x1, y, MID)
    for (y, x0, x1) in shape[5:]:
        rect(im, x0, y, x1, y, UND)
    # a couple of specks so it reads as terrain
    rect(im, 6, 6, 7, 6, MID)
    rect(im, 12, 7, 13, 7, MID)
    rect(im, 8, 8, 9, 8, MID)
    rect(im, 6, 9, 6, 9, UND)
    rect(im, 13, 9, 13, 9, UND)

    outline(im, DARK)
    save(im, "end_island")


# --------------------------------------------------------- abandoned camp
# 26 Abandoned_Camp: a worn tent with a dark opening and a small campfire.
def make_abandoned_camp():
    im = canvas()
    DARK = (34, 28, 20, 255)
    CANVAS = (150, 128, 88, 255)
    CANVAS_D = (110, 92, 62, 255)
    CANVAS_L = (180, 158, 114, 255)
    HOLE = (36, 30, 22, 255)
    EMBER = (224, 138, 0, 255)
    EMBER2 = (250, 210, 90, 255)

    # tent: triangle with a dark entrance
    tri(im, 9, 4, 16, 7, CANVAS)
    # left half slightly darker (light from the right)
    for y in range(5, 17):
        t = (y - 4) / 12.0
        w = int(round(7 * t))
        if w >= 1:
            rect(im, 9 - w, y, 9 - w, y, CANVAS_L)
            rect(im, 9 - w + 1, y, 9, y, CANVAS_D)
    # entrance
    tri(im, 9, 9, 16, 2, HOLE)
    # pole tip
    rect(im, 9, 3, 9, 3, CANVAS_D)
    # campfire at the right side
    rect(im, 15, 15, 17, 16, EMBER)
    rect(im, 16, 14, 16, 14, EMBER2)
    rect(im, 15, 17, 17, 17, DARK)

    outline(im, DARK)
    save(im, "abandoned_camp")


make_feature()
make_fossil()
make_end_island()
make_abandoned_camp()

# contact sheet for review: new icons next to existing viewer ones
names = ["village", "fortress", "geode", "bastion", "chambers", "mineshaft"]
src = OUT
new = ["feature", "nether_fossil", "end_island", "abandoned_camp"]
K = 8
sheet = Image.new("RGBA", (S * K * (len(names) + len(new)), S * K), (40, 44, 52, 255))
for i, n in enumerate(names):
    p = os.path.join(src, n + ".png")
    if os.path.exists(p):
        sheet.alpha_composite(Image.open(p).convert("RGBA").resize((S * K, S * K), Image.NEAREST), (i * S * K, 0))
for j, n in enumerate(new):
    p = os.path.join(OUT, n + ".png")
    sheet.alpha_composite(Image.open(p).convert("RGBA").resize((S * K, S * K), Image.NEAREST),
                          ((len(names) + j) * S * K, 0))
sheet.convert("RGB").save(os.path.join(HERE, "_sheet.png"))
print("sheet ok")
