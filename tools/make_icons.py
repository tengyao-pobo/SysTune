"""Generates SysTune icons (app + light/dark tray variants). Run: python3 tools/make_icons.py"""
from PIL import Image, ImageDraw, ImageFilter
import os

OUT = os.path.join(os.path.dirname(__file__), "..", "src", "SysTune.App", "Assets")
os.makedirs(OUT, exist_ok=True)
SS = 4  # supersampling


def lerp(a, b, t):
    return tuple(int(a[i] + (b[i] - a[i]) * t) for i in range(len(a)))


def bars(draw, size, box, colors, heights, radius_ratio=0.5):
    x0, y0, x1, y1 = box
    n = len(heights)
    gap = (x1 - x0) * 0.16
    w = ((x1 - x0) - gap * (n - 1)) / n
    for i, h in enumerate(heights):
        bx = x0 + i * (w + gap)
        top = y1 - (y1 - y0) * h
        r = w * radius_ratio
        draw.rounded_rectangle([bx, top, bx + w, y1], radius=r, fill=colors[i])


def app_icon(px):
    s = px * SS
    img = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    # soft shadow
    shadow = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    ImageDraw.Draw(shadow).rounded_rectangle([s * 0.08, s * 0.11, s * 0.92, s * 0.95], radius=s * 0.22, fill=(0, 0, 0, 110))
    img.alpha_composite(shadow.filter(ImageFilter.GaussianBlur(s * 0.03)))
    # body gradient
    body = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    grad = Image.new("RGBA", (s, s))
    gd = ImageDraw.Draw(grad)
    for y in range(s):
        gd.line([(0, y), (s, y)], fill=lerp((40, 46, 58, 255), (14, 16, 21, 255), y / s))
    mask = Image.new("L", (s, s), 0)
    ImageDraw.Draw(mask).rounded_rectangle([s * 0.06, s * 0.06, s * 0.94, s * 0.94], radius=s * 0.22, fill=255)
    body.paste(grad, (0, 0), mask)
    img.alpha_composite(body)
    d = ImageDraw.Draw(img)
    # glass highlight + hairline
    d.rounded_rectangle([s * 0.06, s * 0.06, s * 0.94, s * 0.94], radius=s * 0.22, outline=(255, 255, 255, 40), width=max(1, s // 96))
    hl = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    ImageDraw.Draw(hl).rounded_rectangle([s * 0.10, s * 0.09, s * 0.90, s * 0.45], radius=s * 0.18, fill=(255, 255, 255, 18))
    img.alpha_composite(hl)
    # equalizer bars: green (music) → cyan (system)
    cols = [(30, 215, 96, 255), (52, 211, 153, 255), (90, 200, 250, 255), (96, 165, 250, 255)]
    bars(d, s, (s * 0.24, s * 0.26, s * 0.76, s * 0.74), cols, [0.45, 0.85, 0.62, 1.0])
    return img.resize((px, px), Image.LANCZOS)


def tray_icon(px, color):
    s = px * SS
    img = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    bars(d, s, (s * 0.12, s * 0.12, s * 0.88, s * 0.88), [color] * 4, [0.45, 0.85, 0.62, 1.0], 0.5)
    return img.resize((px, px), Image.LANCZOS)


def save_ico(name, frames):
    frames = sorted(frames, key=lambda f: -f.size[0])
    frames[0].save(os.path.join(OUT, name), format="ICO", sizes=[f.size for f in frames], append_images=frames[1:])


sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256]
save_ico("SysTune.ico", [app_icon(p) for p in sizes])
save_ico("SysTune-tray-dark.ico", [tray_icon(p, (255, 255, 255, 255)) for p in [16, 20, 24, 32, 48, 64]])
save_ico("SysTune-tray-light.ico", [tray_icon(p, (20, 23, 28, 255)) for p in [16, 20, 24, 32, 48, 64]])
app_icon(256).save(os.path.join(os.path.dirname(__file__), "..", "docs", "icon.png")) if os.path.isdir(os.path.join(os.path.dirname(__file__), "..", "docs")) else None
print("icons written to", os.path.abspath(OUT))
