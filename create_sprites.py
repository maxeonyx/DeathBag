"""
Sprite processing for DeathBag mod.

Processes raw AI-generated PNGs into Terraria-ready sprites:
- deathbag-raw.png   -> Common/NPCs/DeathBagNPC.png (48x48, content ~32x40)
-                    -> Common/Items/DeathBagItem.png (24x24)
- loadoutbag-raw.png -> Common/NPCs/LoadoutBagNPC.png (48x48, content ~32x32)
-                    -> Common/Items/LoadoutBagItem.png (24x24)
- modicon-raw.png    -> icon.png (480x480, transparent holes filled)

Run from the DeathBag directory:
    python create_sprites.py
"""

from PIL import Image


def threshold_alpha(img):
    """Quantize alpha to fully opaque or fully transparent."""
    img = img.copy()
    px = img.load()
    w, h = img.size
    for y in range(h):
        for x in range(w):
            r, g, b, a = px[x, y]
            px[x, y] = (r, g, b, 255 if a >= 128 else 0)
    return img


def autocrop(img):
    """Threshold alpha, then crop to content bounding box."""
    img = threshold_alpha(img)
    bbox = img.getbbox()
    if bbox is None:
        raise ValueError("Image is fully transparent")
    return img.crop(bbox)


def pad_to_square(img):
    """Pad image to a square canvas, centering the content."""
    w, h = img.size
    size = max(w, h)
    return center_on_canvas(img, size, size)


def scale_to_fill(img, target_size, padding=1):
    """Scale image to fill target_size with padding on each side.
    Content is scaled to (target_size - 2*padding) on its larger axis."""
    content_size = target_size - 2 * padding
    w, h = img.size
    scale = content_size / max(w, h)
    new_w = max(1, int(w * scale))
    new_h = max(1, int(h * scale))
    scaled = img.resize((new_w, new_h), Image.LANCZOS)
    return center_on_canvas(scaled, target_size, target_size)


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


def process_bag_sprite(raw_path, npc_path, item_path,
                       canvas_size=48, item_size=32, max_colors=15):
    """Process a raw bag image into NPC and item sprites."""
    raw = Image.open(raw_path).convert("RGBA")
    cropped = autocrop(raw)
    squared = pad_to_square(cropped)
    print(f"  Cropped: {cropped.size}, squared: {squared.size}")

    # NPC sprite: fill canvas with 1px padding
    npc_scaled = scale_to_fill(squared, canvas_size, padding=1)
    npc_sprite = quantize_colors(npc_scaled, max_colors=max_colors)
    npc_sprite.save(npc_path)
    print(f"  NPC sprite: {npc_sprite.size} -> {npc_path}")

    # Item sprite: same size as NPC, fill canvas with 1px padding
    item_scaled = scale_to_fill(squared, item_size, padding=1)
    item_sprite = quantize_colors(item_scaled, max_colors=max_colors)
    item_sprite.save(item_path)
    print(f"  Item sprite: {item_sprite.size} -> {item_path}")


def process_tile_sprite(raw_path, tile_path, item_path,
                        tile_width=3, tile_height=3, item_size=32, max_colors=15):
    """Process a raw tile image into a tile sprite sheet and item sprite.

    Tile sprite sheets use 16px tiles with 2px padding between them.
    The last row is 18px tall (extends into ground), others are 16px.
    """
    raw = Image.open(raw_path).convert("RGBA")
    cropped = autocrop(raw)
    squared = pad_to_square(cropped)
    print(f"  Cropped: {cropped.size}, squared: {squared.size}")

    # Tile sheet dimensions: each column is 16+2=18px, each row is 16+2=18px
    # (last row height is 18 in CoordinateHeights but same padding in sprite)
    sheet_w = tile_width * 16 + (tile_width - 1) * 2
    sheet_h = tile_height * 16 + (tile_height - 1) * 2
    print(f"  Tile sheet: {sheet_w}x{sheet_h}")

    # Scale content to fit the actual tile area (excluding padding)
    content_w = tile_width * 16
    content_h = tile_height * 16
    content_scaled = scale_to_fill(squared, content_w, padding=0)
    # Crop to exact content size (scale_to_fill centers on a square canvas)
    cx = (content_scaled.width - content_w) // 2
    cy = (content_scaled.height - content_h) // 2
    content_cropped = content_scaled.crop((cx, cy, cx + content_w, cy + content_h))
    content_clean = quantize_colors(content_cropped, max_colors=max_colors)

    # Build tile sheet by inserting 2px transparent gaps
    sheet = Image.new("RGBA", (sheet_w, sheet_h), (0, 0, 0, 0))
    for ty in range(tile_height):
        for tx in range(tile_width):
            # Source region from content
            src_x = tx * 16
            src_y = ty * 16
            tile_piece = content_clean.crop((src_x, src_y, src_x + 16, src_y + 16))
            # Destination in sheet (with 2px gaps)
            dst_x = tx * 18
            dst_y = ty * 18
            sheet.paste(tile_piece, (dst_x, dst_y))

    sheet.save(tile_path)
    print(f"  Tile sprite: {sheet.size} -> {tile_path}")

    # Item sprite
    item_scaled = scale_to_fill(squared, item_size, padding=1)
    item_sprite = quantize_colors(item_scaled, max_colors=max_colors)
    item_sprite.save(item_path)
    print(f"  Item sprite: {item_sprite.size} -> {item_path}")


def process_mod_icon(raw_path, icon_path, target_size=480):
    """Process raw mod icon into 480x480 with transparent holes filled."""
    raw = Image.open(raw_path).convert("RGBA")
    cropped = autocrop(raw)
    squared = pad_to_square(cropped)
    print(f"  Cropped: {cropped.size}, squared: {squared.size}")

    # Scale to fill target with 1px padding
    scaled = scale_to_fill(squared, target_size, padding=1)
    print(f"  Scaled to: {scaled.size}")

    # Fill transparent holes
    filled = fill_transparent_holes(scaled)
    filled.save(icon_path)
    print(f"  Icon: {filled.size} -> {icon_path}")


if __name__ == "__main__":
    print("Processing death bag...")
    process_bag_sprite(
        "deathbag-raw.png",
        "Common/NPCs/DeathBagNPC.png",
        "Common/Items/DeathBagItem.png",
        max_colors=15,
    )

    print("\nProcessing loadout bag...")
    process_bag_sprite(
        "loadoutbag-raw.png",
        "Common/NPCs/LoadoutBagNPC.png",
        "Common/Items/LoadoutBagItem.png",
        max_colors=10,
    )

    print("\nProcessing loadout station...")
    process_tile_sprite(
        "loadoutstation-raw.png",
        "Common/Tiles/LoadoutStationTile.png",
        "Common/Items/LoadoutStationItem.png",
    )

    print("\nProcessing mod icon...")
    process_mod_icon(
        "modicon-raw.png",
        "icon.png",
    )

    print("\nDone! All sprites generated.")
