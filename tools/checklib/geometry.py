"""Rects ([x, y, width, height] in window pixels, as DevTools reports them) and pixels."""


def centre(rect):
    return rect[0] + rect[2] / 2, rect[1] + rect[3] / 2


def fully_inside(rect, window):
    return rect[0] >= 0 and rect[1] >= 0 and rect[0] + rect[2] <= window[0] and rect[1] + rect[3] <= window[1]


def overlaps(a, b, margin=0):
    return a[0] < b[0] + b[2] + margin and b[0] - margin < a[0] + a[2] and a[1] < b[1] + b[3] + margin and b[1] - margin < a[1] + a[3]


def share_covered(rect, cover):
    """How much of rect lies under cover (0 to 1)."""
    region = intersection(rect, cover)
    return (region[2] - region[0]) * (region[3] - region[1]) / (rect[2] * rect[3]) if region and rect[2] * rect[3] else 0.0


def intersection(a, b):
    x1, y1 = max(a[0], b[0]), max(a[1], b[1])
    x2, y2 = min(a[0] + a[2], b[0] + b[2]), min(a[1] + a[3], b[1] + b[3])
    return (x1, y1, x2, y2) if x2 - x1 > 4 and y2 - y1 > 4 else None


def box(rect):
    x, y, w, h = rect
    return int(x), int(y), int(x + w), int(y + h)


def hex_rgb(value):
    value = value.lstrip("#")
    return tuple(int(value[i:i + 2], 16) for i in (0, 2, 4))


def colour_share(image, box, colour, tolerance=40, exclude=()):
    """How much of box (x1, y1, x2, y2) is close to colour, leaving out the `exclude` rects."""
    x1, y1, x2, y2 = (int(v) for v in box)
    pixels = [image.getpixel((x, y)) for y in range(y1, y2) for x in range(x1, x2)
              if not any(r[0] <= x < r[0] + r[2] and r[1] <= y < r[1] + r[3] for r in exclude)]
    close = sum(1 for p in pixels if all(abs(p[i] - colour[i]) <= tolerance for i in range(3)))
    return close / max(1, len(pixels))
