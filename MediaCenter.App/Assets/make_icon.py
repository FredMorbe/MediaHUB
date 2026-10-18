"""Erzeugt Assets/app.ico und Assets/logo.png (Symbol: K aus gefalteten Bändern im Stil des Netflix-N, Akzentfarbe der Palette "Signal").

Aufruf im Ordner Assets:  python make_icon.py
"""
import io
import os
import struct

from PIL import Image, ImageDraw

HERE = os.path.dirname(os.path.abspath(__file__))
SIZES = (16, 24, 32, 48, 64, 128, 256)
S = 2048  # Zeichnen in hoher Auflösung, danach verkleinern

# Akzent #22d3ee (Signal) in drei Helligkeiten: Fläche, Licht, Faltenschatten
FACE = (34, 211, 238)
LIGHT = (103, 232, 249)
SHADE = (8, 112, 140)
DEEP = (6, 78, 100)


def lerp(a, b, t):
    return tuple(round(a[i] + (b[i] - a[i]) * t) for i in range(3))


def fill_gradient(img: Image.Image, polygon, c0, c1, axis: str) -> None:
    """Füllt das Vieleck mit einem Verlauf von c0 nach c1 (axis: "x" von links nach rechts, "y" von oben nach unten)."""
    xs = [p[0] for p in polygon]
    ys = [p[1] for p in polygon]
    x0, y0, x1, y1 = min(xs), min(ys), max(xs), max(ys)
    layer = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    ld = ImageDraw.Draw(layer)
    steps = 256
    for i in range(steps):
        t = i / (steps - 1)
        color = lerp(c0, c1, t) + (255,)
        if axis == "x":
            ld.rectangle([x0 + (x1 - x0) * i / steps, y0, x0 + (x1 - x0) * (i + 1) / steps + 1, y1], fill=color)
        else:
            ld.rectangle([x0, y0 + (y1 - y0) * i / steps, x1, y0 + (y1 - y0) * (i + 1) / steps + 1], fill=color)
    mask = Image.new("L", (S, S), 0)
    ImageDraw.Draw(mask).polygon(polygon, fill=255)
    img.paste(layer, (0, 0), mask)


def master() -> Image.Image:
    img = Image.new("RGBA", (S, S), (0, 0, 0, 0))

    # Dunkle Kachel mit abgerundeten Ecken und leichtem Verlauf
    tile = Image.new("RGBA", (S, S))
    td = ImageDraw.Draw(tile)
    for y in range(S):
        t = y / (S - 1)
        td.line([(0, y), (S, y)], fill=lerp((16, 27, 44), (7, 13, 22), t) + (255,))
    mask = Image.new("L", (S, S), 0)
    ImageDraw.Draw(mask).rounded_rectangle([0.02 * S, 0.02 * S, 0.98 * S, 0.98 * S], radius=0.22 * S, fill=255)
    img.paste(tile, (0, 0), mask)

    # K-Maße: Kasten h hoch, b breit, mittig; Schaft t breit, Bänder w breit (waagerecht gemessen)
    h = 0.58 * S
    b = 0.56 * S
    left = (S - b) / 2
    top = (S - h) / 2
    tn = 0.30   # Schaftbreite, Anteil von b
    wn = 0.30   # Bandbreite, Anteil von b
    mid = 0.5   # hier laufen Arm und Bein am Schaft zusammen

    def pts(*points):
        return [(left + x * b, top + y * h) for x, y in points]

    # Schaft: oben hell, unten etwas dunkler
    fill_gradient(img, pts((0, 0), (tn, 0), (tn, 1), (0, 1)), LIGHT, FACE, "y")

    # Oberer Arm: Band von der Schaftmitte nach rechts oben, oben waagerecht abgeschnitten; zur Falte hin dunkler
    arm = pts((1 - wn, 0), (1, 0), (tn + wn, mid), (tn, mid))
    fill_gradient(img, arm, LIGHT, DEEP, "y")

    # Unteres Bein: Spiegelbild des Arms; an der Falte dunkel, nach unten heller
    leg = pts((tn, mid), (tn + wn, mid), (1, 1), (1 - wn, 1))
    fill_gradient(img, leg, DEEP, LIGHT, "y")

    # Falte am Schaft: dunkles Dreieck, wo beide Bänder ansetzen (wie die Falten im N)
    fold = pts((tn, mid - 0.16), (tn + 0.10, mid), (tn, mid + 0.16))
    fill_gradient(img, fold, FACE, DEEP, "x")
    return img


def write_ico(img: Image.Image, path: str) -> None:
    pngs = []
    for size in SIZES:
        buf = io.BytesIO()
        img.resize((size, size), Image.LANCZOS).save(buf, format="PNG")
        pngs.append((size, buf.getvalue()))
    out = bytearray(struct.pack("<HHH", 0, 1, len(pngs)))
    offset = 6 + 16 * len(pngs)
    for size, data in pngs:
        out += struct.pack("<BBBBHHII", size % 256, size % 256, 0, 0, 1, 32, len(data), offset)
        offset += len(data)
    for _, data in pngs:
        out += data
    with open(path, "wb") as f:
        f.write(out)


if __name__ == "__main__":
    big = master()
    big.resize((512, 512), Image.LANCZOS).save(os.path.join(HERE, "logo.png"))
    write_ico(big, os.path.join(HERE, "app.ico"))
    print("app.ico und logo.png geschrieben")
