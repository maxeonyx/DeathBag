"""
Sprite processing for DeathBag mod.

Processes raw AI-generated PNGs into Terraria-ready sprites:
- deathbag-raw.png   -> Common/NPCs/DeathBagNPC.png (48x48, content ~32x40)
-                    -> Common/Items/DeathBagItem.png (24x24)
- loadoutbag-raw.png -> Common/NPCs/LoadoutBagNPC.png (48x48, content ~32x32)
-                    -> Common/Items/LoadoutBagItem.png (24x24)
- modicon-raw.png    -> icon.png (80x80, transparent holes filled)

Run from the DeathBag directory:
    python create_sprites.py
"""

from PIL import Image


def autocrop(img):
    """Crop to content bounding box (non-transparent pixels)."""
    bbox = img.getbbox()
    if bbox is None:
        raise ValueError("Image is fully transparent")
    return img.crop(bbox)


def scale_to_fit(img, max_w, max_h):
    """Scale image to fit within max_w x max_h, maintaining aspect ratio.
    Uses LANCZOS for high-quality downscaling."""
    w, h = img.size
    scale = min(max_w / w, max_h / h)
    new_w = int(w * scale)
    new_h = int(h * scale)
    return img.resize((new_w, new_h), Image.LANCZOS)


def center_on_canvas(img, canvas_w, canvas_h):
    """Center image on a transparent canvas of the given size."""
    canvas = Image.new("RGBA", (canvas_w, canvas_h), (0, 0, 0, 0))
    x = (canvas_w - img.width) // 2
    y = (canvas_h - img.height) // 2
    canvas.paste(img, (x, y), img)
    return canvas


def fill_transparent_holes(img):
    """Fill transparent holes within the content region.

    Finds the content bounding box, then fills any transparent pixels
    inside it that are surrounded by opaque pixels (flood fill from edges
    to find exterior transparent pixels, everything else gets filled).
    """
    img = img.copy()
    pixels = img.load()
    w, h = img.size

    # Find content bounding box
    bbox = img.getbbox()
    if bbox is None:
        return img
    left, top, right, bottom = bbox

    # Flood fill from edges of bbox to find exterior transparent pixels
    exterior = set()
    queue = []

    # Seed from all edges of the full image (not just bbox)
    for x in range(w):
        for y in [0, h - 1]:
            if pixels[x, y][3] < 128:
                if (x, y) not in exterior:
                    exterior.add((x, y))
                    queue.append((x, y))
    for y in range(h):
        for x in [0, w - 1]:
            if pixels[x, y][3] < 128:
                if (x, y) not in exterior:
                    exterior.add((x, y))
                    queue.append((x, y))

    # BFS flood fill through transparent pixels
    while queue:
        cx, cy = queue.pop()
        for dx, dy in [(-1, 0), (1, 0), (0, -1), (0, 1)]:
            nx, ny = cx + dx, cy + dy
            if 0 <= nx < w and 0 <= ny < h and (nx, ny) not in exterior:
                if pixels[nx, ny][3] < 128:
                    exterior.add((nx, ny))
                    queue.append((nx, ny))

    # Fill interior transparent pixels by averaging neighboring opaque pixels
    for y in range(top, bottom):
        for x in range(left, right):
            if pixels[x, y][3] < 128 and (x, y) not in exterior:
                # Average nearby opaque pixel colors
                r_sum, g_sum, b_sum, count = 0, 0, 0, 0
                for dx in range(-2, 3):
                    for dy in range(-2, 3):
                        nx, ny = x + dx, y + dy
                        if 0 <= nx < w and 0 <= ny < h and pixels[nx, ny][3] >= 128:
                            r_sum += pixels[nx, ny][0]
                            g_sum += pixels[nx, ny][1]
                            b_sum += pixels[nx, ny][2]
                            count += 1
                if count > 0:
                    pixels[x, y] = (r_sum // count, g_sum // count, b_sum // count, 255)
                else:
                    pixels[x, y] = (0, 0, 0, 255)

    return img


def quantize_colors(img, max_colors=32):
    """Reduce color count for cleaner pixel-art look.
    Preserves alpha channel."""
    if img.mode != "RGBA":
        img = img.convert("RGBA")

    # Split alpha
    r, g, b, a = img.split()

    # Quantize RGB
    rgb = Image.merge("RGB", (r, g, b))
    rgb_q = rgb.quantize(colors=max_colors, method=Image.MEDIANCUT, dither=Image.NONE)
    rgb_back = rgb_q.convert("RGB")

    # Recombine with original alpha (thresholded to fully opaque/transparent)
    a_thresh = a.point(lambda p: 255 if p >= 128 else 0)
    return Image.merge("RGBA", (*rgb_back.split(), a_thresh))


def process_bag_sprite(raw_path, npc_path, item_path, content_max_w, content_max_h,
                       canvas_size=48, item_size=24, max_colors=32):
    """Process a raw bag image into NPC and item sprites."""
    raw = Image.open(raw_path).convert("RGBA")
    cropped = autocrop(raw)
    print(f"  Cropped: {cropped.size}")

    # Scale to fit content area
    scaled = scale_to_fit(cropped, content_max_w, content_max_h)
    print(f"  Scaled to: {scaled.size}")

    # Quantize for clean pixel art
    clean = quantize_colors(scaled, max_colors=max_colors)

    # NPC sprite: center on canvas
    npc_sprite = center_on_canvas(clean, canvas_size, canvas_size)
    npc_sprite.save(npc_path)
    print(f"  NPC sprite: {npc_sprite.size} -> {npc_path}")

    # Item sprite: scale content to fit item size with some padding
    item_content = scale_to_fit(cropped, item_size - 4, item_size - 4)
    item_clean = quantize_colors(item_content, max_colors=max_colors)
    item_sprite = center_on_canvas(item_clean, item_size, item_size)
    item_sprite.save(item_path)
    print(f"  Item sprite: {item_sprite.size} -> {item_path}")


def process_mod_icon(raw_path, icon_path, target_size=80):
    """Process raw mod icon into 80x80 with transparent holes filled."""
    raw = Image.open(raw_path).convert("RGBA")
    cropped = autocrop(raw)
    print(f"  Cropped: {cropped.size}")

    # Scale to fit target
    scaled = scale_to_fit(cropped, target_size, target_size)
    print(f"  Scaled to: {scaled.size}")

    # Fill transparent holes
    filled = fill_transparent_holes(scaled)

    # Center on target canvas
    icon = center_on_canvas(filled, target_size, target_size)
    icon.save(icon_path)
    print(f"  Icon: {icon.size} -> {icon_path}")


if __name__ == "__main__":
    # Death bag: taller than wide (aspect ~0.8), content fits ~32x40 in 48x48
    print("Processing death bag...")
    process_bag_sprite(
        "deathbag-raw.png",
        "Common/NPCs/DeathBagNPC.png",
        "Common/Items/DeathBagItem.png",
        content_max_w=32,
        content_max_h=40,
    )

    # Loadout bag: roughly square (aspect ~0.98), content fits ~32x32 in 48x48
    print("\nProcessing loadout bag...")
    process_bag_sprite(
        "loadoutbag-raw.png",
        "Common/NPCs/LoadoutBagNPC.png",
        "Common/Items/LoadoutBagItem.png",  # new — currently doesn't exist separately
        content_max_w=32,
        content_max_h=32,
    )

    # Mod icon: 80x80
    print("\nProcessing mod icon...")
    process_mod_icon(
        "modicon-raw.png",
        "icon.png",
    )

    print("\nDone! All sprites generated.")
