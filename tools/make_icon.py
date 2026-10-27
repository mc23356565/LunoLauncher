"""生成启动器图标（像素风等轴方块）。运行：python tools/make_icon.py"""
from PIL import Image, ImageDraw
import os

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(HERE, '..', 'src', 'OpenLauncher', 'Assets')
os.makedirs(OUT, exist_ok=True)

N = 32  # 像素画布
img = Image.new('RGBA', (N, N), (0, 0, 0, 0))
px = img.load()

# 等轴方块：顶面 / 左面 / 右面
top = [(0x7C, 0xD9, 0x5B), (0x5F, 0xC0, 0x45)]
left = [(0x9A, 0x6B, 0x45), (0x86, 0x5A, 0x38)]
right = [(0x6E, 0x4A, 0x2E), (0x5C, 0x3D, 0x25)]
grass_l = [(0x5F, 0xC0, 0x45), (0x4C, 0xA6, 0x37)]
grass_r = [(0x48, 0x9C, 0x33), (0x3B, 0x85, 0x2A)]

cx, cy = 16, 9   # 顶面中心
w = 13           # 半宽
import random
random.seed(7)

def put(x, y, c):
    if 0 <= x < N and 0 <= y < N:
        px[x, y] = c + (255,)

for y in range(N):
    for x in range(N):
        dx, dy = x - cx + 0.5, y - cy + 0.5
        # 顶面菱形：|dx|/w + |dy|/(w/2) <= 1
        if abs(dx) / w + abs(dy) / (w / 2) <= 1.0:
            put(x, y, top[(x * 3 + y * 5 + random.randint(0, 2)) % 2])
        else:
            # 侧面：在菱形下半边缘往下延伸 h 像素
            h = 13
            if -w <= dx < 0:
                edge = cy + (w + dx) / 2  # 左下边
                if edge <= y + 0.5 <= edge + h:
                    depth = y + 0.5 - edge
                    pal = grass_l if depth < 3.2 else left
                    put(x, y, pal[(x * 7 + y * 3 + random.randint(0, 1)) % 2])
            elif 0 <= dx <= w:
                edge = cy + (w - dx) / 2
                if edge <= y + 0.5 <= edge + h:
                    depth = y + 0.5 - edge
                    pal = grass_r if depth < 3.2 else right
                    put(x, y, pal[(x * 5 + y * 7 + random.randint(0, 1)) % 2])

# 去中心化标记：顶面上三个相连的白点（节点网）
nodes = [(16, 5), (10, 11), (22, 11)]
d = ImageDraw.Draw(img)
for a in range(3):
    for b in range(a + 1, 3):
        d.line([nodes[a], nodes[b]], fill=(255, 255, 255, 170), width=1)
for (x, y) in nodes:
    d.rectangle([x - 1, y - 1, x, y], fill=(255, 255, 255, 255))

big = img.resize((256, 256), Image.NEAREST)
big.save(os.path.join(OUT, 'icon.png'))
sizes = [(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)]
big.save(os.path.join(OUT, 'icon.ico'), sizes=sizes)
print('ok', OUT)
