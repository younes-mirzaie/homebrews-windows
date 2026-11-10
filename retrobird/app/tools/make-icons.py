"""Draws Retrobird's app bar icons (white on transparent, as Windows Phone expects).
Run: python3 make-icons.py ../Retrobird/Assets   (needs Pillow)"""
import math
import os
import sys

from PIL import Image, ImageDraw

out = sys.argv[1]
S = 4                      # supersampling
W = (255, 255, 255, 255)


def canvas(size):
    img = Image.new("RGBA", (size * S, size * S), (0, 0, 0, 0))
    return img, ImageDraw.Draw(img)


def save(img, size, path):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    img.resize((size, size), Image.LANCZOS).save(path)


def P(*pts):
    return [(x * S, y * S) for x, y in pts]


def line(d, pts, w=3):
    d.line(P(*pts), fill=W, width=int(w * S), joint="curve")
    for x, y in pts:   # round caps
        r = w * S / 2
        d.ellipse((x * S - r, y * S - r, x * S + r, y * S + r), fill=W)


def arc(d, cx, cy, r, a0, a1, w=3):
    pts = [(cx + r * math.cos(math.radians(a)), cy + r * math.sin(math.radians(a)))
           for a in range(int(a0), int(a1) + 1, 3)]
    line(d, pts, w)


def arrowhead(d, x, y, ang, size=7):
    a = math.radians(ang)
    p1 = (x + size * math.cos(a + 2.5), y + size * math.sin(a + 2.5))
    p2 = (x + size * math.cos(a - 2.5), y + size * math.sin(a - 2.5))
    line(d, [p1, (x, y), p2])


# App bar icons: 76x76 canvas, glyph inside the central ~32px (the system draws the circle).
def appbar(name, draw):
    img, d = canvas(76)
    draw(d)
    save(img, 76, os.path.join(out, "AppBar", name + ".png"))


appbar("add", lambda d: (line(d, [(38, 24), (38, 52)]), line(d, [(24, 38), (52, 38)])))
appbar("check", lambda d: line(d, [(25, 39), (34, 48), (52, 29)]))


def refresh(d):
    arc(d, 38, 38, 13, -60, 250)
    arrowhead(d, 38 + 13 * math.cos(math.radians(-60)), 38 + 13 * math.sin(math.radians(-60)), 30)


appbar("refresh", refresh)
appbar("folder", lambda d: line(d, [(24, 30), (24, 50), (52, 50), (52, 33), (37, 33), (34, 28), (24, 28), (24, 30)]))


def reply(d, two=False):
    off = 6 if two else 0
    line(d, [(26 + off, 38), (52, 38)]) if False else None
    arc(d, 40 + off, 48, 12, 180 + 90, 360, 3)
    line(d, [(40 + off, 36), (28 + off, 36)])
    arrowhead(d, 26 + off, 36, 180)
    if two:
        arrowhead(d, 24, 36, 180)


appbar("reply", lambda d: (line(d, [(27, 36), (42, 36)]), arc(d, 42, 46, 10, 270, 360), arrowhead(d, 27, 36, 180)))
appbar("replyall", lambda d: (line(d, [(31, 36), (44, 36)]), arc(d, 44, 46, 10, 270, 360), arrowhead(d, 31, 36, 180),
                             arrowhead(d, 23, 36, 180)))
appbar("forward", lambda d: (line(d, [(49, 36), (34, 36)]), arc(d, 34, 46, 10, 180, 270), arrowhead(d, 49, 36, 0)))
appbar("delete", lambda d: (line(d, [(27, 30), (49, 30)]), line(d, [(34, 30), (34, 26), (42, 26), (42, 30)]),
                            line(d, [(29, 30), (31, 52), (45, 52), (47, 30)]), line(d, [(36, 35), (36, 47)]),
                            line(d, [(40, 35), (40, 47)])))
appbar("send", lambda d: (line(d, [(24, 38), (52, 26), (44, 52), (36, 41), (24, 38)]), line(d, [(36, 41), (52, 26)])))
appbar("cc", lambda d: (arc(d, 32, 38, 8, 40, 320), arc(d, 48, 38, 8, 40, 320)))


# Tiles, app icon, store logo and splash come from Younes's art: render-art.js.
print("app bar icons written to", out)
