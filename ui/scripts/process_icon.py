from PIL import Image
from pathlib import Path
from collections import deque

src = Path(
    r"C:\Users\adars\.cursor\projects\c-Users-adars-OneDrive-Documents-personal-projects-work-tree\assets"
    r"\c__Users_adars_AppData_Roaming_Cursor_User_workspaceStorage_dc940949709551dabbaacc7891178f1c_"
    r"images_worktree-favicon-017ab6ab-1895-4b4a-9a98-8ccf5f8a27f1.png"
)
img = Image.open(src).convert("RGBA")
w, h = img.size
pixels = img.load()


def is_outer_bg(r, g, b, a):
    if a == 0:
        return True
    # Outside canvas is near-black / dark gray (not green-tinted).
    mx = max(r, g, b)
    if mx <= 45 and (g - r) < 12 and (g - b) < 12:
        return True
    if mx <= 70 and abs(r - g) <= 6 and abs(g - b) <= 6 and abs(r - b) <= 6:
        return True
    return False


# Flood-fill from image borders through outer background → transparent.
visited = [[False] * w for _ in range(h)]
q = deque()
for x in range(w):
    q.append((x, 0))
    q.append((x, h - 1))
for y in range(h):
    q.append((0, y))
    q.append((w - 1, y))

while q:
    x, y = q.popleft()
    if x < 0 or y < 0 or x >= w or y >= h or visited[y][x]:
        continue
    visited[y][x] = True
    r, g, b, a = pixels[x, y]
    if not is_outer_bg(r, g, b, a):
        continue
    pixels[x, y] = (0, 0, 0, 0)
    q.append((x + 1, y))
    q.append((x - 1, y))
    q.append((x, y + 1))
    q.append((x, y - 1))

# Soften remaining dark fringe adjacent to transparency.
for y in range(h):
    for x in range(w):
        r, g, b, a = pixels[x, y]
        if a == 0:
            continue
        if not (r < 55 and g < 55 and b < 55 and (g - r) < 10):
            continue
        for dx, dy in ((1, 0), (-1, 0), (0, 1), (0, -1)):
            nx, ny = x + dx, y + dy
            if 0 <= nx < w and 0 <= ny < h and pixels[nx, ny][3] == 0:
                pixels[x, y] = (0, 0, 0, 0)
                break

bbox = img.getbbox()
print("bbox", bbox)
if bbox:
    pad = 2
    l, t, rgt, bot = bbox
    img = img.crop((max(0, l - pad), max(0, t - pad), min(w, rgt + pad), min(h, bot + pad)))

# Make square with transparent padding for clean favicon scaling.
cw, ch = img.size
side = max(cw, ch)
square = Image.new("RGBA", (side, side), (0, 0, 0, 0))
square.paste(img, ((side - cw) // 2, (side - ch) // 2), img)
img = square
print("final", img.size)

root = Path(r"c:\Users\adars\OneDrive\Documents\personal_projects\work-tree\ui")
out_icon = root / "src" / "assets" / "worktree-icon.png"
out_fav = root / "public" / "favicon.png"
img.save(out_icon, "PNG")
img.save(out_fav, "PNG")
for size in (16, 32, 64, 180):
    img.resize((size, size), Image.Resampling.LANCZOS).save(
        root / "public" / f"favicon-{size}.png", "PNG"
    )

px = list(img.getdata())
trans = sum(1 for p in px if p[3] == 0)
near_black = sum(1 for p in px if p[3] > 0 and p[0] < 30 and p[1] < 30 and p[2] < 30)
print("transparent", trans, "near_black_opaque", near_black, "corners", [img.getpixel(c) for c in [(0, 0), (side - 1, 0)]])
