# Draws src/Assets/grab.cur (the closed hand shown while dragging an All games column).
# Usage: python tools/make-grab-cursor.py src/Assets/grab.cur   (needs Pillow)
import struct, io, sys
from PIL import Image, ImageDraw, ImageFilter

S = 256  # drawing scale (canvas 256 -> cursor 32: 8x)
SCALE = 0.7  # hand size within the cursor
def fist(stroke, fill):
    im = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    d = ImageDraw.Draw(im)
    # shapes in a 32x32 design grid, scaled by 8
    k = S / 32
    def rr(x0, y0, x1, y1, r, **kw): d.rounded_rectangle([x0*k, y0*k, x1*k, y1*k], r*k, **kw)
    def body(grow, color):
        g = grow
        # palm
        rr(7-g, 11-g, 26+g, 27+g, 5+g, fill=color)
        # knuckles (four fingers curled)
        for x in (9.5, 13.5, 17.5, 21.5):
            rr(x-g, 7-g, x+4.4+g, 15+g, 2.2+g, fill=color)
        # thumb along the left
        rr(5-g, 12.5-g, 10+g, 21+g, 2.5+g, fill=color)
    body(2.3, stroke)
    body(0, fill)
    # finger separations
    w = 1.9 * k
    for x in (13.7, 17.7, 21.7):
        d.line([x*k, 9.5*k, x*k, 15*k], fill=stroke, width=round(w))
    # thumb crease
    d.line([10*k, 14*k, 10*k, 19.5*k], fill=stroke, width=round(w))
    return im

def frames():
    big = fist((0, 0, 0, 255), (255, 255, 255, 255))
    # Shrink the hand to about the size of Windows' own hand pointer, centred on the hotspot
    f = SCALE
    small = big.resize((round(S * f), round(S * f)), Image.LANCZOS)
    big = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    big.alpha_composite(small, (round((S - small.width) / 2), round((S - small.height) / 2)))
    out = []
    for size in (32, 48, 64):
        out.append(big.resize((size, size), Image.LANCZOS))
    return out

def write_cur(path, imgs, hot=(16, 16)):
    entries, datas = [], []
    for im in imgs:
        w, h = im.size
        # 32-bit BGRA DIB, bottom-up, height doubled, plus AND mask
        px = im.load()
        rows = []
        for y in range(h - 1, -1, -1):
            row = bytearray()
            for x in range(w):
                r, g, b, a = px[x, y]
                row += bytes((b, g, r, a))
            rows.append(bytes(row))
        xor = b"".join(rows)
        mask_stride = ((w + 31) // 32) * 4
        andm = bytes(mask_stride * h)   # all 0: alpha decides
        hdr = struct.pack("<IiiHHIIiiII", 40, w, h * 2, 1, 32, 0, len(xor) + len(andm), 0, 0, 0, 0)
        data = hdr + xor + andm
        hx, hy = round(hot[0] * w / 32), round(hot[1] * h / 32)
        entries.append((w, h, hx, hy, len(data)))
        datas.append(data)
    off = 6 + 16 * len(imgs)
    buf = io.BytesIO()
    buf.write(struct.pack("<HHH", 0, 2, len(imgs)))
    for (w, h, hx, hy, n) in entries:
        buf.write(struct.pack("<BBBBHHII", w % 256, h % 256, 0, 0, hx, hy, n, off)); off += n
    for dt in datas: buf.write(dt)
    open(path, "wb").write(buf.getvalue())

imgs = frames()
write_cur(sys.argv[1], imgs)
