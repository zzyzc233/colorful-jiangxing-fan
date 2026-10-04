# -*- coding: utf-8 -*-
"""介绍视频 v2：真实截图 + Edge-TTS 配音。输出 release/intro-配音版.mp4"""
import math
import subprocess
from PIL import Image, ImageDraw, ImageFont, ImageFilter

W, H, FPS = 1280, 720, 30
OUT = r"D:\x15-fan-control\release\intro-配音版.mp4"
ASSETS = r"D:\x15-fan-control\release\assets"

BG = (24, 24, 24)
PANEL = (33, 33, 33)
GRID = (60, 60, 60)
GOLD = (230, 200, 40)
WHITE = (242, 242, 242)
GRAY = (158, 158, 158)
GREEN = (87, 187, 110)
BLUE = (96, 165, 250)

_font_cache = {}


def font(size, bold=False):
    key = (size, bold)
    if key not in _font_cache:
        _font_cache[key] = ImageFont.truetype(
            "C:/Windows/Fonts/msyhbd.ttc" if bold else "C:/Windows/Fonts/msyh.ttc", size)
    return _font_cache[key]


def ease_out(t):
    t = min(max(t, 0.0), 1.0)
    return 1 - (1 - t) ** 3


def clamp01(t):
    return min(max(t, 0.0), 1.0)


def seg(t, a, b):
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


def fade_slide(base, text, fnt, color, cx, cy, p, slide=20, opacity=1.0):
    e = ease_out(p)
    paste_center(base, text_layer(text, fnt, color, opacity * min(1, p * 2)), cx, cy, dy=int((1 - e) * slide))


def base_frame():
    return Image.new("RGBA", (W, H), BG + (255,))


def screenshot_layer(path, box_h=560, zoom=1.0):
    """截图 + 圆角 + 投影，按高度缩放（zoom>1 放大裁切做 Ken Burns）"""
    im = Image.open(path).convert("RGBA")
    scale = box_h / im.height
    im = im.resize((int(im.width * scale), box_h), Image.LANCZOS)
    if zoom > 1.0:
        cw, ch = int(im.width / zoom), int(im.height / zoom)
        x0 = (im.width - cw) // 2
        y0 = (im.height - ch) // 2
        im = im.crop((x0, y0, x0 + cw, y0 + ch)).resize((im.width, im.height), Image.LANCZOS)
    # 圆角蒙版
    mask = Image.new("L", im.size, 0)
    ImageDraw.Draw(mask).rounded_rectangle((0, 0, im.width - 1, im.height - 1), 12, fill=255)
    im.putalpha(mask)
    # 投影
    shadow = Image.new("RGBA", (im.width + 40, im.height + 40), (0, 0, 0, 0))
    ImageDraw.Draw(shadow).rounded_rectangle((20, 20, im.width + 20, im.height + 20), 14, fill=(0, 0, 0, 160))
    shadow = shadow.filter(ImageFilter.GaussianBlur(10))
    out = Image.new("RGBA", shadow.size, (0, 0, 0, 0))
    out.alpha_composite(shadow)
    out.alpha_composite(im, (20, 20))
    return out


def bullet_lines(img, lines, cx, y0, t, gap=54, fsize=24, start=0.1, step=0.14):
    for i, ln in enumerate(lines):
        p = seg(t, start + i * step, start + i * step + 0.12)
        if p > 0:
            fade_slide(img, ln, font(fsize), WHITE if i == 0 else GRAY, cx, y0 + i * gap, p, slide=16)


# ---------- 场景 ----------

def scene_title(img, t):
    fade_slide(img, "将星风扇管家", font(62, True), WHITE, W / 2, 200, seg(t, 0.0, 0.35), slide=28)
    fade_slide(img, "七彩虹将星笔记本 · 风扇曲线管理工具", font(25), GRAY, W / 2, 295, seg(t, 0.25, 0.6))
    fade_slide(img, "v0.5 · 开源 MIT · 免安装", font(20), GOLD, W / 2, 355, seg(t, 0.45, 0.8))
    return img


def scene_cpu(img, t, shot):
    z = 1.0 + 0.05 * clamp01(t / 1.0)
    shot_layer = screenshot_layer(shot, 600, z)
    img.alpha_composite(shot_layer, (int(W - shot_layer.width - 60), int((H - shot_layer.height) / 2)))
    fade_slide(img, "多点自定义风扇曲线", font(38, True), WHITE, 340, 130, seg(t, 0.0, 0.18))
    bullet_lines(img, [
        "拖动黄点 调整转速",
        "双击加点 · 右键删点",
        "松手自动保存 重启不丢",
        "温度到哪 风扇就按你定的走",
    ], 340, 220, t, start=0.15)
    return img


def scene_gpu(img, t, shot):
    z = 1.0 + 0.05 * clamp01(t / 1.0)
    shot_layer = screenshot_layer(shot, 600, z)
    img.alpha_composite(shot_layer, (int(W - shot_layer.width - 60), int((H - shot_layer.height) / 2)))
    fade_slide(img, "双风扇 独立调节", font(38, True), WHITE, 320, 150, seg(t, 0.0, 0.2))
    bullet_lines(img, [
        "CPU / GPU 各自一条曲线",
        "互不干扰",
        "GPU 休眠时温度自动回退",
    ], 320, 260, t, start=0.18)
    return img


def scene_temp(img, t, shot):
    z = 1.0 + 0.05 * clamp01(t / 1.0)
    shot_layer = screenshot_layer(shot, 600, z)
    img.alpha_composite(shot_layer, (60, int((H - shot_layer.height) / 2)))
    fade_slide(img, "温度与游戏加加同源", font(38, True), WHITE, 830, 120, seg(t, 0.0, 0.2))
    bullet_lines(img, [
        "主显示 = 软件传感器（游戏加加口径）",
        "括号内 EC = 风扇固件实际依据",
        "约 10°C 口径差已自动补偿",
    ], 830, 210, t, start=0.15)
    fade_slide(img, "GPU 走驱动官方接口", font(22), GREEN, 830, 420, seg(t, 0.55, 0.75))
    return img


def scene_safety(img, t):
    fade_slide(img, "三层安全兜底", font(44, True), WHITE, W / 2, 100, seg(t, 0.0, 0.2))
    cards = [
        ("97°C 过热", "自动切全速散热", GOLD),
        ("回落 4 秒", "自动恢复曲线", GREEN),
        ("程序崩溃", "固件照样拉满", BLUE),
    ]
    bw, gap = 350, 40
    x0 = (W - bw * 3 - gap * 2) / 2
    for i, (a, b, c) in enumerate(cards):
        p = seg(t, 0.15 + i * 0.14, 0.32 + i * 0.14)
        if p <= 0:
            continue
        e = ease_out(p)
        layer = Image.new("RGBA", (W, H), (0, 0, 0, 0))
        d = ImageDraw.Draw(layer)
        bx = x0 + i * (bw + gap)
        d.rounded_rectangle((bx, 210, bx + bw, 390), 14, fill=PANEL + (int(255 * min(1, p * 2)),),
                            outline=c + (200,), width=2)
        d.text((bx + bw / 2, 255), a, font=font(30, True), fill=c + (255,), anchor="mm")
        d.text((bx + bw / 2, 320), b, font=font(22), fill=WHITE + (255,), anchor="mm")
        img.alpha_composite(layer, (0, int((1 - e) * 30)))
    fade_slide(img, "启动快照 CC3.0 调度 · 退出自动复原 · 遥测失联自动重置", font(21), GRAY, W / 2, 480, seg(t, 0.6, 0.85))
    return img


def scene_end(img, t):
    fade_slide(img, "将星风扇管家", font(54, True), WHITE, W / 2, 210, seg(t, 0.0, 0.25))
    fade_slide(img, "让风扇该静的时候静 该快的时候快", font(24), GRAY, W / 2, 300, seg(t, 0.15, 0.4))
    fade_slide(img, "github.com/zzyzc233/colorful-jiangxing-fan", font(27, True), GOLD, W / 2, 400, seg(t, 0.35, 0.6))
    fade_slide(img, "Releases 下载 · 开源 MIT · 免费使用", font(20), GRAY, W / 2, 470, seg(t, 0.5, 0.75))
    return img


# ---------- 时间轴 ----------

import json
tl = json.load(open(r"D:\x15-fan-control\release\assets\timeline.json", encoding="utf-8"))
SCENES = tl["scenes"]  # [(dur_frames, kind, arg)]


def render():
    total = sum(s[0] for s in SCENES)
    print(f"总帧数 {total}（{total / FPS:.1f} 秒）")
    proc = subprocess.Popen(
        ["ffmpeg", "-y", "-f", "rawvideo", "-pix_fmt", "rgb24", "-s", f"{W}x{H}",
         "-r", str(FPS), "-i", "-", "-i", r"D:\x15-fan-control\release\assets\narration.m4a",
         "-c:v", "libx264", "-preset", "medium", "-crf", "20", "-pix_fmt", "yuv420p",
         "-c:a", "aac", "-b:a", "160k", "-shortest", "-movflags", "+faststart", OUT],
        stdin=subprocess.PIPE, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    idx = 0
    for dur, kind, arg in SCENES:
        for f in range(dur):
            t = f / max(1, dur - 1)
            img = base_frame()
            if kind == "title":
                img = scene_title(img, t)
            elif kind == "cpu":
                img = scene_cpu(img, t, arg)
            elif kind == "gpu":
                img = scene_gpu(img, t, arg)
            elif kind == "temp":
                img = scene_temp(img, t, arg)
            elif kind == "safety":
                img = scene_safety(img, t)
            elif kind == "end":
                img = scene_end(img, t)
            proc.stdin.write(img.convert("RGB").tobytes())
            idx += 1
            if idx % 200 == 0:
                print(f"  {idx}/{total}")
    proc.stdin.close()
    proc.wait()
    print("完成:", OUT)


if __name__ == "__main__":
    render()
