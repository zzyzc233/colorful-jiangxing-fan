# -*- coding: utf-8 -*-
"""从 timeline.json 生成与配音逐句对齐的 SRT 字幕。"""
import json
import os

os.chdir(r"D:\x15-fan-control\release")
tl = json.load(open("assets/timeline.json", encoding="utf-8"))
scenes = tl["scenes"]

texts = [
    ["给将星笔记本写的风扇管理工具", "将星风扇管家"],
    ["多点自定义曲线", "拖动黄点调整转速 双击加点 右键删点", "松手自动保存 重启不丢", "温度到哪个区间 风扇就按你定的转速走"],
    ["CPU 和 GPU 两把风扇", "独立调节 互不干扰"],
    ["温度显示和游戏加加同口径", "括号里的 EC 温度 是风扇固件真正认的那一路", "比显示高十度左右 是设计 不是误差"],
    ["安全性做了三层", "过热自动全速 回落自动恢复", "程序就算崩了 固件也会把风扇拉满"],
    ["开源免费", "GitHub 搜 colorful jiangxing fan 地址在视频简介"],
]


def fmt(ms):
    h, ms = divmod(ms, 3600000)
    m, ms = divmod(ms, 60000)
    s, ms = divmod(ms, 1000)
    return f"{h:02d}:{m:02d}:{s:02d},{ms:03d}"


srt, idx, acc = [], 0, 0.0
for (frames, _kind, _arg), chunks in zip(scenes, texts):
    scene_dur = frames / 30
    tts_dur = scene_dur - 0.75
    total_chars = sum(len(c) for c in chunks)
    t = acc
    for c in chunks:
        d = tts_dur * len(c) / total_chars
        idx += 1
        srt.append(f"{idx}\n{fmt(int(t * 1000))} --> {fmt(int((t + d) * 1000))}\n{c}\n")
        t += d
    acc += scene_dur

open("subtitle.srt", "w", encoding="utf-8-sig").write("\n".join(srt))
print(f"{idx} 条字幕已生成")
