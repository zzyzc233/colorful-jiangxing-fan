# -*- coding: utf-8 -*-
"""生成《将星风扇管家》介绍视频：1280x720 @30fps，输出 release/intro.mp4"""
import math
import subprocess
from PIL import Image, ImageDraw, ImageFont

W, H, FPS = 1280, 720, 30
OUT = r"D:\x15-fan-control\release\intro.mp4"

BG = (24, 24, 24)
PANEL = (33, 33, 33)
GRID = (60, 60, 60)
GOLD = (230, 200, 40)
GOLD_DIM = (160, 140, 30)
WHITE = (242, 242, 242)
GRAY = (158, 158, 158)
DGRAY = (110, 110, 110)
GREEN = (87, 187, 110)
RED = (240, 98, 90)
BLUE = (96, 165, 250)

F_BOLD = "C:/Windows/Fonts/msyhbd.ttc"
F_REG = "C:/Windows/Fonts/msyh.ttc"
_font_cache = {}


def font(size, bold=False):
    key = (size, bold)
    if key not in _font_cache:
        _font_cache[key] = ImageFont.truetype(F_BOLD if bold else F_REG, size)
    return _font_cache[key]


def ease_out(t):
    t = min(max(t, 0.0), 1.0)
    return 1 - (1 - t) ** 3


def ease_io(t):
    t = min(max(t, 0.0), 1.0)
    return 4 * t * t * t if t < 0.5 else 1 - ((-2 * t + 2) ** 3) / 2


def clamp01(t):
    return min(max(t, 0.0), 1.0)


def seg(t, a, b):
    """t(0-1) 在 [a,b] 子区间的归一化进度"""
    return clamp01((t - a) / (b - a))


def text_layer(text, fnt, color, opacity=1.0):
    tmp = Image.new("RGBA", (10, 10))
    tb = ImageDraw.Draw(tmp).textbbox((0, 0), text, font=fnt)
    w, h = max(1, tb[2] - tb[0] + 12), max(1, tb[3] - tb[1] + 12)
    layer = Image.new("RGBA", (w, h), (0, 0, 0, 0))
    ImageDraw.Draw(layer).text((6 - tb[0], 6 - tb[1]), text, font=fnt, fill=color + (255,))
    if opacity < 0.999:
        a = layer.getchannel("A").point(lambda v: int(v * opacity))
        layer.putalpha(a)
    return layer


def paste_center(base, layer, cx, cy, dy=0, dx=0):
    base.alpha_composite(layer, (int(cx - layer.width / 2 + dx), int(cy - layer.height / 2 + dy)))


def fade_slide(base, text, fnt, color, cx, cy, p, slide=24, opacity=1.0):
    e = ease_out(p)
    paste_center(base, text_layer(text, fnt, color, opacity * min(1, p * 2)), cx, cy, dy=int((1 - e) * slide))


def base_frame():
    img = Image.new("RGB", (W, H), BG)
    return img.convert("RGBA")


def draw_curve_panel(img, box, progress, highlight_labels):
    """box=(x,y,w,h)：绘制仿应用的曲线面板，progress 0-1 控制曲线绘制进度"""
    x, y, w, h = box
    x1, y1 = x + w, y + h
    layer = Image.new("RGBA", (W, H), (0, 0, 0, 0))
    d = ImageDraw.Draw(layer)
    d.rounded_rectangle((x, y, x1, y1), 10, fill=PANEL + (255,), outline=GRID + (255,), width=1)
    pad_l, pad_r, pad_t, pad_b = 52, 22, 22, 34
    gx0, gx1 = x + pad_l, x + w - pad_r
    gy0, gy1 = y + pad_t, y + h - pad_b

    f_small = font(15)
    for pct in range(0, 101, 20):
        yy = gy1 - (gy1 - gy0) * pct / 100
        d.line((gx0, yy, gx1, yy), fill=GRID + (255,), width=1)
        d.text((x + 10, yy - 8), f"{pct}%", font=f_small, fill=GRAY + (255,))
    for t in (40, 55, 70, 85, 100):
        xx = gx0 + (gx1 - gx0) * (t - 40) / 60
        d.line((xx, gy0, xx, gy1), fill=GRID + (255,), width=1)
        lbl = f"{t}°"
        tw = d.textlength(lbl, font=f_small)
        d.text((xx - tw / 2, gy1 + 6), lbl, font=f_small, fill=GRAY + (255,))

    pts = [(40, 28), (50, 29), (70, 30), (75, 33), (80, 38), (85, 48), (90, 62), (95, 80), (100, 100)]
    px = [gx0 + (gx1 - gx0) * (t - 40) / 60 for t, _ in pts]
    py = [gy1 - (gy1 - gy0) * dd / 100 for _, dd in pts]

    total = sum(math.dist((px[i], py[i]), (px[i + 1], py[i + 1])) for i in range(len(pts) - 1))
    draw_len = total * ease_io(progress)
    acc = 0.0
    line_pts = [(px[0], py[0])]
    for i in range(len(pts) - 1):
        seg_len = math.dist((px[i], py[i]), (px[i + 1], py[i + 1]))
        if acc + seg_len <= draw_len:
            line_pts.append((px[i + 1], py[i + 1]))
            acc += seg_len
        else:
            r = (draw_len - acc) / seg_len
            line_pts.append((px[i] + (px[i + 1] - px[i]) * r, py[i] + (py[i + 1] - py[i]) * r))
            break
    if len(line_pts) >= 2:
        d.line(line_pts, fill=GOLD + (255,), width=5, joint="curve")

    arrived = len(line_pts) - 1
    for i, (xx, yy) in enumerate(zip(px, py)):
        if i > arrived:
            break
        if i == 0 or i == len(pts) - 1:
            d.ellipse((xx - 6, yy - 6, xx + 6, yy + 6), outline=(200, 200, 200, 255), width=2)
        else:
            r = 8 if i == arrived else 7
            d.ellipse((xx - r, yy - r, xx + r, yy + r), fill=GOLD + (255,))

    if highlight_labels is not None and arrived >= 2:
        lx, ly = px[2], py[2]
        d.text((lx - 30, ly + 14), "70° 30%", font=font(16, True), fill=GOLD + (255,))
    img.alpha_composite(layer)


# ---------- 场景 ----------

def scene_title(img, t):
    d = ImageDraw.Draw(img)
    e = ease_out(t * 2)
    curve_progress = seg(t, 0.25, 1.0)
    draw_curve_panel(img, (140, 430, 1000, 260), curve_progress * 0.55, None)
    fade_slide(img, "将星风扇管家", font(64, True), WHITE, W / 2, 190, seg(t, 0.0, 0.45), slide=30)
    fade_slide(img, "七彩虹将星笔记本 · 风扇曲线管理工具", font(26), GRAY, W / 2, 280, seg(t, 0.25, 0.7))
    fade_slide(img, "v0.5 · 开源 MIT · 免安装", font(20), GOLD, W / 2, 340, seg(t, 0.45, 0.85))
    return img


def scene_pain(img, t):
    fade_slide(img, "还在忍受原厂风扇策略？", font(46, True), WHITE, W / 2, 120, seg(t, 0.0, 0.25))
    items = [
        ("一打游戏，CPU 就冲上 98°C", RED),
        ("待机风扇转个不停，噪音烦人", RED),
        ("退出游戏，风扇还全速狂转好几秒", RED),
    ]
    y0 = 240
    for i, (txt, c) in enumerate(items):
        p = seg(t, 0.2 + i * 0.18, 0.38 + i * 0.18)
        if p <= 0:
            continue
        e = ease_out(p)
        layer = Image.new("RGBA", (W, H), (0, 0, 0, 0))
        d = ImageDraw.Draw(layer)
        bx, by = 260, y0 + i * 110
        d.rounded_rectangle((bx, by, bx + 760, by + 76), 12, fill=PANEL + (int(255 * min(1, p * 2)),))
        d.ellipse((bx + 26, by + 26, bx + 50, by + 50), fill=c + (255,))
        d.text((bx + 74, by + 20), txt, font=font(26), fill=WHITE + (255,))
        img.alpha_composite(layer, (int((1 - e) * -40), 0))
    fade_slide(img, "该有的样子：温度稳、噪音低、关键时刻才提速", font(22), GREEN, W / 2, 620, seg(t, 0.75, 1.0))
    return img


def scene_curve(img, t):
    fade_slide(img, "多点自定义风扇曲线", font(42, True), WHITE, W / 2, 70, seg(t, 0.0, 0.2))
    prog = seg(t, 0.1, 0.55)
    draw_curve_panel(img, (240, 130, 800, 400), prog, True)
    caps = [
        (0.45, 0.6, "CPU / GPU 独立 · 最多 8 个节点"),
        (0.6, 0.75, "拖动调整 · 双击加点 · 右键删点"),
        (0.78, 0.95, "松手自动保存，重启不丢"),
    ]
    y = 560
    for a, b, txt in caps:
        p = seg(t, a, b)
        if p > 0:
            fade_slide(img, txt, font(26), GOLD if p < 1 else WHITE, W / 2, y, p, slide=14)
            y += 46
    return img


def scene_safety(img, t):
    fade_slide(img, "三层安全兜底", font(46, True), WHITE, W / 2, 110, seg(t, 0.0, 0.22))
    items = [
        ("97°C 过热", "自动切全速散热", GOLD),
        ("回落 4 秒", "自动恢复曲线", GREEN),
        ("程序崩了", "固件照样把风扇拉满", BLUE),
    ]
    bw, gap = 350, 40
    x0 = (W - bw * 3 - gap * 2) / 2
    for i, (a, b, c) in enumerate(items):
        p = seg(t, 0.22 + i * 0.16, 0.42 + i * 0.16)
        if p <= 0:
            continue
        e = ease_out(p)
        layer = Image.new("RGBA", (W, H), (0, 0, 0, 0))
        d = ImageDraw.Draw(layer)
        bx = x0 + i * (bw + gap)
        by = 230
        d.rounded_rectangle((bx, by, bx + bw, by + 190), 14, fill=PANEL + (int(255 * min(1, p * 2)),),
                            outline=c + (200,), width=2)
        d.text((bx + bw / 2, by + 40), a, font=font(30, True), fill=c + (255,), anchor="mm")
        d.text((bx + bw / 2, by + 105), b, font=font(22), fill=WHITE + (255,), anchor="mm")
        img.alpha_composite(layer, (0, int((1 - e) * 30)))
    fade_slide(img, "启动即快照 CC3.0 调度，退出时自动复原原厂策略", font(22), GRAY, W / 2, 520, seg(t, 0.72, 0.95))
    fade_slide(img, "曲线末端固定 100°C → 100%，极端情况固件直接拉满", font(22), GRAY, W / 2, 575, seg(t, 0.82, 1.0))
    return img


def scene_temp(img, t):
    fade_slide(img, "温度显示与游戏加加同源", font(44, True), WHITE, W / 2, 100, seg(t, 0.0, 0.22))
    e1 = ease_out(seg(t, 0.2, 0.5))
    if e1 > 0:
        layer = Image.new("RGBA", (W, H), (0, 0, 0, 0))
        d = ImageDraw.Draw(layer)
        d.rounded_rectangle((180, 190, 620, 380), 14, fill=PANEL + (int(255 * e1 * 255),))
        d.text((400, 245), f"{int(60 + 5 * e1)}°C", font=font(58, True), fill=GREEN + (255,), anchor="mm")
        d.text((400, 330), "CPU 结温 · 与游戏加加同口径", font=font(20), fill=GRAY + (255,), anchor="mm")
        img.alpha_composite(layer, (int((1 - e1) * -30), 0))
    e2 = ease_out(seg(t, 0.45, 0.7))
    if e2 > 0:
        layer = Image.new("RGBA", (W, H), (0, 0, 0, 0))
        d = ImageDraw.Draw(layer)
        d.rounded_rectangle((660, 190, 1100, 380), 14, fill=PANEL + (int(255 * e2 * 255),))
        d.text((880, 245), f"（EC {int(70 + 5 * e2)}°）", font=font(44, True), fill=GOLD + (255,), anchor="mm")
        d.text((880, 330), "风扇固件实际依据的温度", font=font(20), fill=GRAY + (255,), anchor="mm")
        img.alpha_composite(layer, (int((1 - e2) * 30), 0))
    fade_slide(img, "GPU 走驱动官方接口 · 独显休眠自动回退", font(24), WHITE, W / 2, 450, seg(t, 0.68, 0.85))
    fade_slide(img, "曲线温度轴已自动补偿两者约 10°C 的口径差", font(24), GREEN, W / 2, 515, seg(t, 0.8, 1.0))
    return img


def scene_usage(img, t):
    if t < 0.62:
        fade_slide(img, "三步上手", font(46, True), WHITE, W / 2, 120, seg(t, 0.0, 0.25))
        steps = ["① 解压到任意文件夹", "② 管理员运行 FanSilencer.exe", "③ 拖曲线，点「应用曲线」"]
        for i, s in enumerate(steps):
            p = seg(t, 0.2 + i * 0.13, 0.4 + i * 0.13)
            if p > 0:
                fade_slide(img, s, font(30), WHITE if i < 2 else GOLD, W / 2, 260 + i * 90, p, slide=18)
    else:
        p = seg(t, 0.62, 1.0)
        e = ease_out(seg(t, 0.62, 0.8))
        fade_slide(img, "将星风扇管家", font(50, True), WHITE, W / 2, 190, seg(t, 0.62, 0.78))
        fade_slide(img, "让风扇该静的时候静，该快的时候快", font(24), GRAY, W / 2, 275, seg(t, 0.7, 0.85))
        fade_slide(img, "github.com/zzyzc233/colorful-jiangxing-fan", font(26, True), GOLD, W / 2, 380, seg(t, 0.78, 0.92))
        fade_slide(img, "Releases 下载 · 开源 MIT · 免费使用", font(20), GRAY, W / 2, 445, seg(t, 0.85, 1.0))
    return img


SCENES = [
    (135, scene_title),   # 4.5s
    (165, scene_pain),    # 5.5s
    (300, scene_curve),   # 10s
    (180, scene_safety),  # 6s
    (150, scene_temp),    # 5s
    (180, scene_usage),   # 6s
]


def main():
    total = sum(n for n, _ in SCENES)
    print(f"总帧数 {total}（{total / FPS:.1f} 秒）")
    proc = subprocess.Popen(
        ["ffmpeg", "-y", "-f", "rawvideo", "-pix_fmt", "rgb24", "-s", f"{W}x{H}",
         "-r", str(FPS), "-i", "-", "-c:v", "libx264", "-preset", "medium",
         "-crf", "20", "-pix_fmt", "yuv420p", "-movflags", "+faststart", OUT],
        stdin=subprocess.PIPE, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)

    idx = 0
    for n, fn in SCENES:
        for f in range(n):
            t = f / max(1, n - 1)
            img = fn(base_frame(), t)
            proc.stdin.write(img.convert("RGB").tobytes())
            idx += 1
            if idx % 150 == 0:
                print(f"  {idx}/{total}")
    proc.stdin.close()
    proc.wait()
    print("完成:", OUT)


if __name__ == "__main__":
    main()
