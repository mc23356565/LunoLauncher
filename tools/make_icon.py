"""生成 LunoLauncher 图标（现代化扁平风格）。

设计：圆角方形底 + 紫蓝渐变，白色月牙 + 右侧一颗小星点。
“Luno” 取月亮之意；月牙在 16px 下依然可辨认，也不会和 Minecraft 的方块图标混淆。

运行：python tools/make_icon.py
"""
import os
from PIL import Image, ImageDraw, ImageFilter, ImageChops

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(HERE, '..', 'src', 'LunoLauncher', 'Assets')
os.makedirs(OUT, exist_ok=True)

S = 1024                      # 设计尺寸，最后再缩小（超采样，边缘更干净）
RADIUS = int(S * 0.235)       # 圆角半径 ≈ squircle

# 渐变：左上偏紫，右下偏蓝
C1 = (108, 92, 246)           # #6C5CF6  violet
C2 = (59, 130, 246)           # #3B82F6  blue


def linear_gradient(size, top_left, bottom_right, angle_bias=0.65):
    """生成一张对角渐变图。"""
    w, h = size
    base = Image.new('RGB', (1, h))
    px = base.load()
    for y in range(h):
        t = y / max(1, h - 1)
        px[0, y] = tuple(int(top_left[i] + (bottom_right[i] - top_left[i]) * t) for i in range(3))
    grad = base.resize((w, h), Image.BILINEAR)
    # 再横向叠一层，让左上更亮一点
    horiz = Image.new('RGB', (w, 1))
    hp = horiz.load()
    for x in range(w):
        t = (x / max(1, w - 1)) * angle_bias
        hp[x, 0] = tuple(int(255 * 0) for _ in range(3))
    return grad


def rounded_mask(size, radius):
    m = Image.new('L', size, 0)
    d = ImageDraw.Draw(m)
    d.rounded_rectangle([0, 0, size[0] - 1, size[1] - 1], radius=radius, fill=255)
    return m


img = Image.new('RGBA', (S, S), (0, 0, 0, 0))

# --- 底：对角渐变 + 圆角遮罩 ---
grad = Image.new('RGBA', (S, S))
gp = grad.load()
for y in range(S):
    for x in range(S):
        t = (x / S * 0.55 + y / S * 0.45)
        gp[x, y] = (
            int(C1[0] + (C2[0] - C1[0]) * t),
            int(C1[1] + (C2[1] - C1[1]) * t),
            int(C1[2] + (C2[2] - C1[2]) * t),
            255,
        )
img.paste(grad, (0, 0), rounded_mask((S, S), RADIUS))

# --- 顶部一层柔和高光，制造体积感 ---
glow = Image.new('L', (S, S), 0)
gd = ImageDraw.Draw(glow)
gd.ellipse([-S * 0.25, -S * 0.75, S * 0.9, S * 0.45], fill=52)
glow = glow.filter(ImageFilter.GaussianBlur(S * 0.09))
white = Image.new('RGBA', (S, S), (255, 255, 255, 255))
img.paste(white, (0, 0), Image.composite(glow, Image.new('L', (S, S), 0), rounded_mask((S, S), RADIUS)))

# --- 月牙：大圆减去偏移圆 ---
cx, cy = S * 0.425, S * 0.525
r = S * 0.285
cut_dx, cut_dy = S * 0.128, -S * 0.050
cut_r = S * 0.258

moon_mask = Image.new('L', (S, S), 0)
md = ImageDraw.Draw(moon_mask)
md.ellipse([cx - r, cy - r, cx + r, cy + r], fill=255)
cut = Image.new('L', (S, S), 0)
cd = ImageDraw.Draw(cut)
cd.ellipse([cx - cut_r + cut_dx, cy - cut_r + cut_dy, cx + cut_r + cut_dx, cy + cut_r + cut_dy], fill=255)
moon_mask = ImageChops.subtract(moon_mask, cut)

img.paste(Image.new('RGBA', (S, S), (255, 255, 255, 255)), (0, 0), moon_mask)

# --- 星点：月牙右上方一颗小圆 ---
star_r = S * 0.036
sx, sy = S * 0.700, S * 0.300
sd = ImageDraw.Draw(img)
sd.ellipse([sx - star_r, sy - star_r, sx + star_r, sy + star_r], fill=(255, 255, 255, 235))

# --- 输出 ---
png = img.resize((256, 256), Image.LANCZOS)
png.save(os.path.join(OUT, 'icon.png'))

ico_sizes = [(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)]
img.resize((256, 256), Image.LANCZOS).save(os.path.join(OUT, 'icon.ico'), sizes=ico_sizes)
print('ok ->', OUT)
