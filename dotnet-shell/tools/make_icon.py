#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""DiskScope 应用图标生成器 —— 全新设计，与上游 WinDirStat 的"饼图 + 树"没有任何关系。

母题就是这款软件本身：一块 squarified 区块图，其中一格被点亮。

- 瓦片取色沿用 src/WdsShell.Core/Layout/ExtensionPalette.cs 的公式
  （hue = i * 137.508 % 360，s=0.62，v=0.85；目录按层级向白浅化 t = min(depth,4) * 0.12），
  所以图标里那几块颜色，和真跑起来时右侧区块图用的是同一套色。
- 右下角那块灰的是 <Free Space> 伪节点。
- 左上大块的白芯 + 黑底环 + 蓝色外发光，抄的是 TreemapControl.DrawSelectionHighlight
  那套选中态（高亮色 #63B9FF = 深色主题的 TreemapHighlightColor，光晕 5 圈参数同 GlowRings）。
- "<7px 的块整块填高亮色"这条上游规矩反过来用在图标上：<=24px 档把选中格直接涂成实心高亮蓝。

尺寸策略：每档独立渲染，不是从母图缩放。>=40px 用完整 11 块布局；<=32px 换 3 块的简化布局
（块大、缝粗、无光晕）—— 16px 下放 11 块只会糊成一团灰；三块刻意做成非对称切分，
均分 2x2 会撞微软四方块的脸。

用法：
    python tools/make_icon.py            # 写 src/WdsShell.App/Assets/{app.ico, app-master.png}
    python tools/make_icon.py --preview  # 另导各尺寸明/暗底对照表到 .iconcheck/（分档 PNG 也落在那儿）
"""
import argparse
import io
import struct
import sys
from pathlib import Path

from PIL import Image, ImageDraw, ImageFilter

ROOT = Path(__file__).resolve().parent.parent
ASSETS = ROOT / "src" / "WdsShell.App" / "Assets"
CHECK = ROOT / ".iconcheck"

SIZES = [16, 20, 24, 32, 40, 48, 64, 80, 96, 128, 256]
MASTER = 1024
SS = 4  # 超采样：画完再 LANCZOS 缩回，圆角和细描边才不会毛

BASE_TOP = (35, 35, 41)
BASE_BOTTOM = (19, 19, 23)      # 比区块图底色 #1B1B1F 略深，让色块浮起来
EDGE = (255, 255, 255, 96)      # 外描边：深色任务栏上全靠它勾出轮廓，压暗一档就看不见边了
HIGHLIGHT = (99, 185, 255)      # #63B9FF
FREE_SPACE = (104, 104, 114)    # 上游是 #3F3F46，暗底板上看不见，提亮两档才读得出"这是一块灰砖"

INSET = 0.100   # 区块图相对圆角方形内缩多少
GAP = 0.014     # 瓦片缝（真 UI 里只有 1px/575px≈0.17%，图标必须放大才读得出）
TILE_R = 0.010  # 瓦片圆角
BASE_R = 0.205  # 底板圆角

# (x0, y0, x1, y1, 色序 i, 层级 depth)，归一化到内容区，无缝无重叠
FULL_TILES = [
    (0.00, 0.52, 0.28, 1.00, 5, 2),
    (0.28, 0.52, 0.46, 0.78, 6, 3),
    (0.28, 0.78, 0.46, 1.00, 7, 3),
    (0.46, 0.00, 0.76, 0.30, 2, 1),
    (0.46, 0.30, 0.72, 0.62, 4, 1),
    (0.46, 0.62, 0.72, 1.00, 8, 2),
    (0.76, 0.00, 1.00, 0.30, 3, 2),
    (0.72, 0.30, 1.00, 0.55, 9, 3),
    (0.72, 0.55, 1.00, 0.78, 10, 4),
]
SELECTED = (0.00, 0.00, 0.46, 0.52, 1, 0)  # 左上大块
FREE_TILE = (0.72, 0.78, 1.00, 1.00)

SIMPLE_TILES = [
    (0.52, 0.00, 1.00, 0.56, 2, 1),
    (0.52, 0.56, 1.00, 1.00, 5, 2),
]
SIMPLE_SELECTED = (0.00, 0.00, 0.52, 1.00, 1, 0)

# 同 TreemapControl.GlowRings（厚度按 575px 区域给的值，这里换算成画布比例）
GLOW_RINGS = [(12 / 575, 18), (9 / 575, 32), (6.5 / 575, 50), (4 / 575, 72), (2.5 / 575, 100)]


def palette(i: int, depth: int):
    """ExtensionPalette.GetColor + GetDirectoryTint 的 Python 复刻。"""
    h = (i * 137.508) % 360.0
    s, v = 0.62, 0.85
    c = v * s
    x = c * (1 - abs((h / 60.0) % 2 - 1))
    m = v - c
    if h < 60:
        r, g, b = c, x, 0.0
    elif h < 120:
        r, g, b = x, c, 0.0
    elif h < 180:
        r, g, b = 0.0, c, x
    elif h < 240:
        r, g, b = 0.0, x, c
    elif h < 300:
        r, g, b = x, 0.0, c
    else:
        r, g, b = c, 0.0, x
    r, g, b = (int((q + m) * 255) for q in (r, g, b))
    t = min(depth, 4) * 0.12
    return round(r * (1 - t) + 255 * t), round(g * (1 - t) + 255 * t), round(b * (1 - t) + 255 * t)


def tile_rect(box, tile, gap):
    x0, y0, x1, y1 = box
    w = x1 - x0
    return [x0 + tile[0] * w + gap / 2, y0 + tile[1] * w + gap / 2,
            x0 + tile[2] * w - gap / 2, y0 + tile[3] * w - gap / 2]


def inflate(rc, d):
    return [rc[0] - d, rc[1] - d, rc[2] + d, rc[3] + d]


def render(px: int) -> Image.Image:
    big = px * SS
    simple = px <= 32
    glow = px >= 40
    solid_selected = px <= 24

    img = Image.new("RGBA", (big, big), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)

    margin = 0.015 * big
    side = big - 2 * margin
    base = [margin, margin, margin + side, margin + side]
    rad = TILE_R * side
    gap = GAP * side * (1.7 if simple else 1.0)

    # 底板：竖直渐变 + 圆角蒙版
    grad = Image.new("RGBA", (big, big), (0, 0, 0, 0))
    gd = ImageDraw.Draw(grad)
    for y in range(big):
        t = y / max(1, big - 1)
        col = tuple(round(a + (b - a) * t) for a, b in zip(BASE_TOP, BASE_BOTTOM)) + (255,)
        gd.line([(0, y), (big, y)], fill=col)
    mask = Image.new("L", (big, big), 0)
    ImageDraw.Draw(mask).rounded_rectangle(base, radius=BASE_R * side, fill=255)
    grad.putalpha(mask)
    img.alpha_composite(grad)

    cs = side * (1 - 2 * INSET)
    box = [margin + INSET * side, margin + INSET * side,
           margin + INSET * side + cs, margin + INSET * side + cs]
    sel = SIMPLE_SELECTED if simple else SELECTED
    tiles = (SIMPLE_TILES if simple else FULL_TILES) + [sel]
    free = None if simple else FREE_TILE

    if glow:
        rc = tile_rect(box, sel, gap)
        halo = Image.new("RGBA", (big, big), (0, 0, 0, 0))
        hd = ImageDraw.Draw(halo)
        # 大档位上把光晕摊开摊亮：GlowRings 那组参数是按 575px 的实机区域定的，
        # 直接照搬到 1024 母图上只够勾出一条 1% 宽的边，远看等于没发光
        spread = 2.2 if px >= 96 else (1.6 if px >= 64 else 1.0)
        boost = 1.7 if px >= 64 else 1.0
        for th, a in GLOW_RINGS:  # 自外向内：宽而淡的先画，窄而亮的盖在上面
            t = th * side * spread
            hd.rounded_rectangle(inflate(rc, t / 2), radius=rad + t / 2,
                                 outline=HIGHLIGHT + (min(255, round(a * boost)),),
                                 width=max(SS, round(t)))
        img.alpha_composite(halo.filter(ImageFilter.GaussianBlur(0.005 * side)))

    for tile in tiles:
        rc = tile_rect(box, tile, gap)
        if tile is sel and solid_selected:
            col = HIGHLIGHT + (255,)  # 极小档：整块填高亮色，同上游对 <7px 块的处理
        else:
            col = palette(tile[4], tile[5]) + (255,)
        d.rounded_rectangle(rc, radius=rad, fill=col)

    if free is not None:
        d.rounded_rectangle(tile_rect(box, free, gap), radius=rad, fill=FREE_SPACE + (255,))

    # 选中态硬边：黑底环 + 白芯环。所有线宽都按超采样空间算，
    # 下限必须是 SS（=成品 1px），否则缩到 16px 后整条边会被平均掉。
    rc = tile_rect(box, sel, gap)
    w_out = max(SS + 2, round(0.0075 * side))
    w_core = max(SS, round(0.0055 * side))
    d.rounded_rectangle(inflate(rc, w_out), radius=rad + w_out,
                        outline=(0, 0, 0, 175), width=w_out)
    d.rounded_rectangle(inflate(rc, w_core / 2), radius=rad + w_core / 2,
                        outline=(255, 255, 255, 255), width=w_core)

    w = max(SS, round(0.006 * side))
    d.rounded_rectangle(base, radius=BASE_R * side, outline=EDGE, width=w)

    return img.resize((px, px), Image.LANCZOS)


def bmp_entry(im: Image.Image) -> bytes:
    """>=48px 之外（<=48）走 BMP 内嵌：32bpp BGRA 自下而上 + AND 掩码，老 shell 也认。"""
    w, h = im.size
    px = im.tobytes()
    body = bytearray()
    for y in range(h - 1, -1, -1):
        row = bytearray()
        for x in range(w):
            r, g, b, a = px[(y * w + x) * 4:(y * w + x) * 4 + 4]
            row += bytes((b, g, r, a))
        body += row
    stride = ((w + 31) // 32) * 4
    mask = bytearray()
    for y in range(h - 1, -1, -1):
        line = bytearray(stride)
        for x in range(w):
            if px[(y * w + x) * 4 + 3] < 128:
                line[x // 8] |= 0x80 >> (x % 8)
        mask += line
    hdr = struct.pack("<IiiHHIIiiII", 40, w, h * 2, 1, 32, 0, len(body) + len(mask), 0, 0, 0, 0)
    return hdr + bytes(body) + bytes(mask)


def png_entry(im: Image.Image) -> bytes:
    buf = io.BytesIO()
    im.save(buf, "PNG", optimize=True)
    return buf.getvalue()


def build_ico(images, path: Path):
    entries = [(im, (bmp_entry(im) if im.size[0] <= 48 else png_entry(im))) for im in images]
    dir_block = bytearray()
    blob = bytearray()
    offset = 6 + 16 * len(entries)
    for im, data in entries:
        w, h = im.size
        dir_block += struct.pack("<BBBBHHII", w % 256, h % 256, 0, 0, 1, 32, len(data), offset + len(blob))
        blob += data
    path.write_bytes(struct.pack("<HHH", 0, 1, len(entries)) + bytes(dir_block) + bytes(blob))


def preview_sheet(images, path: Path):
    cell = max(SIZES)
    pad = 16
    cols = len(SIZES)
    strips = []
    for bg in [(240, 240, 243), (28, 28, 32)]:
        sheet = Image.new("RGB", (cols * (cell + pad) + pad, cell + pad), bg)
        for k, im in enumerate(images):
            sheet.paste(im, (pad + k * (cell + pad) + (cell - im.size[0]) // 2,
                             pad + (cell - im.size[1]) // 2), im)
        strips.append(sheet)
    combo = Image.new("RGB", (strips[0].width, sum(s.height for s in strips) + pad))
    y = 0
    for s in strips:
        combo.paste(s, (0, y))
        y += s.height + pad
    path.parent.mkdir(exist_ok=True)
    combo.save(path)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--preview", action="store_true")
    args = ap.parse_args()

    ASSETS.mkdir(parents=True, exist_ok=True)
    CHECK.mkdir(exist_ok=True)
    images = [render(px) for px in SIZES]
    # 分档 PNG 只是给人核对用的中间产物，不进仓库；程序里只嵌 app.ico（Avalonia 会自己挑帧）
    for im in images:
        im.save(CHECK / f"app-{im.size[0]}.png")
    build_ico(images, ASSETS / "app.ico")
    render(MASTER).save(ASSETS / "app-master.png")

    print(f"ico = {(ASSETS / 'app.ico').stat().st_size} bytes，档位 = {SIZES}", file=sys.stderr)
    print("色序 = " + ", ".join(f"{i}:{'#%02X%02X%02X' % palette(i, d)}" for _, _, _, _, i, d in
                                (FULL_TILES + [SELECTED])), file=sys.stderr)
    if args.preview:
        preview_sheet(images, CHECK / "preview.png")
        print(f"preview -> {CHECK / 'preview.png'}", file=sys.stderr)


if __name__ == "__main__":
    main()
