"""Offline shaded preview of geometry exported by Test-Model.ps1.

This draws the exact generated triangles; it does not simulate Valheim's shader,
textures, lighting or camera. Usage: python Render-Model.py geometry.json out.png
"""
import json
import sys
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw, ImageFont


def normalized(value):
    value = np.asarray(value, dtype=float)
    return value / np.linalg.norm(value)


def render(data, width, height, direction, playing=True):
    background = np.array([27, 30, 29], dtype=np.uint8)
    image = np.broadcast_to(background, (height, width, 3)).copy()
    depth = np.full((height, width), np.inf)
    forward = -normalized(direction)
    right = normalized(np.cross(forward, [0, 1, 0]))
    up = normalized(np.cross(right, forward))
    basis = np.array([right, up, forward]).T
    scale = min(width, height) / 1.65
    light = normalized([-0.6, 1, -0.8])
    for part in data["parts"]:
        if part["glow"] and not playing:
            continue
        vertices = np.asarray(part["vertices"])
        normals = np.asarray(part["normals"])
        projected = (vertices - [0, 0.67, 0]) @ basis
        projected[:, 0] = width / 2 + projected[:, 0] * scale
        projected[:, 1] = height / 2 - projected[:, 1] * scale
        for i in range(0, len(vertices), 3):
            normal = normals[i]
            if normal @ forward >= 0:
                continue
            a, b, c = projected[i : i + 3]
            ab, ac = b[:2] - a[:2], c[:2] - a[:2]
            area = ab[0] * ac[1] - ab[1] * ac[0]
            if abs(area) < 1e-5:
                continue
            x0 = max(0, int(np.floor(min(a[0], b[0], c[0]))))
            x1 = min(width - 1, int(np.ceil(max(a[0], b[0], c[0]))))
            y0 = max(0, int(np.floor(min(a[1], b[1], c[1]))))
            y1 = min(height - 1, int(np.ceil(max(a[1], b[1], c[1]))))
            if x0 > x1 or y0 > y1:
                continue
            y, x = np.mgrid[y0 : y1 + 1, x0 : x1 + 1]
            x = x + 0.5
            y = y + 0.5
            w0 = ((b[0] - x) * (c[1] - y) - (b[1] - y) * (c[0] - x)) / area
            w1 = ((c[0] - x) * (a[1] - y) - (c[1] - y) * (a[0] - x)) / area
            w2 = 1 - w0 - w1
            z = w0 * a[2] + w1 * b[2] + w2 * c[2]
            crop_depth = depth[y0 : y1 + 1, x0 : x1 + 1]
            mask = (w0 >= 0) & (w1 >= 0) & (w2 >= 0) & (z < crop_depth)
            shade = 0.68 + max(0.0, normal @ light) * 0.64
            if part["glow"]:
                shade = 1.4
            color = np.clip(np.asarray(part["color"]) * shade * 255, 0, 255).astype(np.uint8)
            crop_depth[mask] = z[mask]
            image[y0 : y1 + 1, x0 : x1 + 1][mask] = color
    return Image.fromarray(image)


def main():
    data = json.loads(Path(sys.argv[1]).read_text(encoding="utf-8-sig"))
    canvas = Image.new("RGB", (1200, 800), (27, 30, 29))
    canvas.paste(render(data, 730, 650, [1.8, 1.25, -2.6]), (0, 80))
    canvas.paste(render(data, 440, 300, [-1.6, 1, -2.8]), (750, 100))
    canvas.paste(render(data, 440, 300, [1.8, 1, 2.5], False), (750, 440))
    draw = ImageDraw.Draw(canvas)
    try:
        title = ImageFont.truetype("C:/Windows/Fonts/arialbd.ttf", 30)
        small = ImageFont.truetype("C:/Windows/Fonts/arial.ttf", 18)
    except OSError:
        title = small = ImageFont.load_default()
    draw.text((28, 20), "SKALD'S HORN / NORDIC RADIO", font=title, fill=(226, 185, 121))
    draw.text((770, 85), "Front / left", font=small, fill=(180, 179, 166))
    draw.text((770, 424), "Rear / switched off", font=small, fill=(180, 179, 166))
    draw.line((745, 80, 745, 740), fill=(70, 68, 57), width=1)
    draw.text((28, 744), f"Original production mesh: {data['triangles']} triangles | 7 shared materials | hollow bell", font=small, fill=(198, 188, 165))
    draw.text((28, 773), "Offline geometry preview. Final game appearance uses Valheim materials and lighting.", font=small, fill=(143, 148, 139))
    canvas.save(sys.argv[2])


if __name__ == "__main__":
    main()
