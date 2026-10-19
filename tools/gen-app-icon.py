"""Draws the Pulsenotch app icon (a notch with an equalizer, neon glow underneath) and writes
assets/pulsenotch.ico plus assets/pulsenotch-512.png. Needs Pillow.   python tools/gen-app-icon.py"""
import io
import os
import struct

from PIL import Image, ImageChops, ImageDraw, ImageFilter

ROOT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..")
OUT = os.path.join(ROOT, "assets")

CYAN, VIOLET, MAGENTA = (0x22, 0xE4, 0xFF), (0x8B, 0x5C, 0xFF), (0xFF, 0x3D, 0xCB)
BASE = (0x0A, 0x0A, 0x12)
S = 4  # supersampling; the design grid is 512 units


def lerp(a, b, t):
    return tuple(round(a[i] + (b[i] - a[i]) * t) for i in range(3))


def neon(t):
    return lerp(CYAN, VIOLET, t * 2) if t < 0.5 else lerp(VIOLET, MAGENTA, (t - 0.5) * 2)


def bezier(p0, p1, p2, n=24):
    return [((1 - t) ** 2 * p0[0] + 2 * (1 - t) * t * p1[0] + t * t * p2[0],
             (1 - t) ** 2 * p0[1] + 2 * (1 - t) * t * p1[1] + t * t * p2[1])
            for t in (i / n for i in range(n + 1))]


def arc(cx, cy, r, a0, a1, n=24):
    import math
    return [(cx + r * math.cos(math.radians(a0 + (a1 - a0) * i / n)),
             cy + r * math.sin(math.radians(a0 + (a1 - a0) * i / n))) for i in range(n + 1)]


def notch_points(small):
    """Notch silhouette hanging from the top edge, with the concave 'ear' fillets."""
    if small:  # simpler, chunkier shape for tiny sizes
        pts = bezier((72, 0), (126, 0), (126, 54))
        pts += [(126, 160)] + arc(182, 160, 56, 180, 90)[1:] + arc(330, 160, 56, 90, 0)[1:]
        pts += [(386, 54)] + bezier((386, 54), (386, 0), (440, 0))[1:]
    else:
        pts = bezier((92, 0), (136, 0), (136, 44))
        pts += [(136, 150)] + arc(188, 150, 52, 180, 90)[1:] + arc(324, 150, 52, 90, 0)[1:]
        pts += [(376, 44)] + bezier((376, 44), (376, 0), (420, 0))[1:]
    return pts


def bars(small):
    if small:  # three fat bars survive 16 px, five thin ones do not
        return [(176, 120, 64), (256, 170, 64), (336, 120, 64)], 64
    return [(176, 40, 22), (216, 78, 22), (256, 116, 22), (296, 78, 22), (336, 40, 22)], 22


def render(px):
    small = px <= 32
    n = 512 * S
    sc = lambda v: v * S  # noqa: E731

    img = Image.new("RGBA", (n, n), BASE + (255,))

    # neon glow low in the icon
    glow = Image.new("RGBA", (n, n), (0, 0, 0, 0))
    gd = ImageDraw.Draw(glow)
    for cx, cy, r, col, a in ((110, 400, 140, CYAN, 190), (270, 450, 160, VIOLET, 215), (430, 360, 120, MAGENTA, 178)):
        gd.ellipse([sc(cx - r), sc(cy - r), sc(cx + r), sc(cy + r)], fill=col + (a,))
    glow = glow.filter(ImageFilter.GaussianBlur(40 * S))
    img = Image.alpha_composite(img, glow)

    d = ImageDraw.Draw(img, "RGBA")
    pts = [(sc(x), sc(y)) for x, y in notch_points(small)]
    d.polygon(pts, fill=(0, 0, 0, 255))
    if not small:  # faint rim so the black notch separates from the dark base
        rim = Image.new("RGBA", (n, n), (0, 0, 0, 0))
        ImageDraw.Draw(rim).line(pts + [pts[0]], fill=(255, 255, 255, 26), width=2 * S, joint="curve")
        img = Image.alpha_composite(img, rim)

    # equalizer bars, one horizontal gradient across all of them
    items, width = bars(small)
    x_lo, x_hi = items[0][0] - width / 2, items[-1][0] + width / 2
    grad = Image.new("RGBA", (n, n), (0, 0, 0, 0))
    gp = ImageDraw.Draw(grad)
    for x in range(int(sc(x_lo)), int(sc(x_hi)) + 1):
        gp.line([(x, 0), (x, n)], fill=neon((x - sc(x_lo)) / max(1, sc(x_hi - x_lo))) + (255,))
    mask = Image.new("L", (n, n), 0)
    md = ImageDraw.Draw(mask)
    cy = 112 if not small else 100
    for cx, h, w in items:
        md.rounded_rectangle([sc(cx - w / 2), sc(cy - h / 2), sc(cx + w / 2), sc(cy + h / 2)], radius=sc(w / 2), fill=255)
    img.paste(grad, (0, 0), mask)

    # rounded-square outline of the icon
    corner = Image.new("L", (n, n), 0)
    ImageDraw.Draw(corner).rounded_rectangle([0, 0, n - 1, n - 1], radius=sc(112), fill=255)
    img.putalpha(ImageChops.multiply(img.getchannel("A"), corner))
    return img.resize((px, px), Image.LANCZOS)


def write_ico(path, sizes):
    blobs = []
    for s in sizes:
        buf = io.BytesIO()
        render(s).save(buf, "PNG")
        blobs.append((s, buf.getvalue()))
    out = struct.pack("<HHH", 0, 1, len(blobs))
    offset = 6 + 16 * len(blobs)
    for s, data in blobs:
        out += struct.pack("<BBBBHHII", s % 256, s % 256, 0, 0, 1, 32, len(data), offset)
        offset += len(data)
    with open(path, "wb") as f:
        f.write(out + b"".join(d for _, d in blobs))


if __name__ == "__main__":
    os.makedirs(OUT, exist_ok=True)
    write_ico(os.path.join(OUT, "pulsenotch.ico"), [16, 20, 24, 32, 40, 48, 64, 128, 256])
    render(512).save(os.path.join(OUT, "pulsenotch-512.png"))
    sheet = Image.new("RGBA", (512 + 30 + 256, 512), (255, 255, 255, 255))
    sheet.paste(render(512), (0, 0))
    y = 0
    for s in (128, 64, 48, 32, 24, 16):
        sheet.paste(render(s), (542, y), render(s))
        y += s + 12
    sheet.save(os.path.join(OUT, "_preview.png"))
    print("wrote", OUT)
