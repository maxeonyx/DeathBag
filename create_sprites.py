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


def double_pixels(img):
    """Scale image 2x with nearest-neighbor to match Terraria's 2x2 pixel aesthetic.

    Process at half resolution first, then call this to double up.
    Every 'game pixel' becomes a 2x2 block of screen pixels.
    """
    w, h = img.size
    return img.resize((w * 2, h * 2), Image.NEAREST)


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
    """Process a raw bag image into NPC and item sprites.

    Works at half resolution then doubles pixels (nearest-neighbor 2x)
    to match Terraria's 2x2 game-pixel aesthetic.
    """
    raw = Image.open(raw_path).convert("RGBA")
    cropped = autocrop(raw)
    squared = pad_to_square(cropped)
    print(f"  Cropped: {cropped.size}, squared: {squared.size}")

    # NPC sprite: process at half size, then double
    half_canvas = canvas_size // 2
    npc_scaled = scale_to_fill(squared, half_canvas, padding=1)
    npc_quantized = quantize_colors(npc_scaled, max_colors=max_colors)
    npc_sprite = double_pixels(npc_quantized)
    npc_sprite.save(npc_path)
    print(f"  NPC sprite: {half_canvas}x{half_canvas} -> 2x -> {npc_sprite.size} -> {npc_path}")

    # Item sprite: process at half size, then double
    half_item = item_size // 2
    item_scaled = scale_to_fill(squared, half_item, padding=1)
    item_quantized = quantize_colors(item_scaled, max_colors=max_colors)
    item_sprite = double_pixels(item_quantized)
    item_sprite.save(item_path)
    print(f"  Item sprite: {half_item}x{half_item} -> 2x -> {item_sprite.size} -> {item_path}")


def process_tile_sprite(raw_path, tile_path, item_path,
                        tile_width=3, tile_height=3, item_size=32, max_colors=15):
    """Process a raw tile image into a tile sprite sheet and item sprite.

    Tile sprite sheets use 16px tiles with 2px padding between them.
    Works at half resolution then doubles pixels to match Terraria's
    2x2 game-pixel aesthetic. Each tile cell is 8x8 game-pixels (16x16 real).
    """
    raw = Image.open(raw_path).convert("RGBA")
    cropped = autocrop(raw)
    print(f"  Cropped: {cropped.size}")

    # Work at half resolution: each tile is 8px, gaps are 1px
    half_tile = 8
    half_gap = 1
    half_content_w = tile_width * half_tile
    half_content_h = tile_height * half_tile

    # Stretch content to fill tile area exactly (no padding, no square)
    content_half = cropped.resize((half_content_w, half_content_h), Image.LANCZOS)
    content_clean = quantize_colors(content_half, max_colors=max_colors)

    # Build half-res tile sheet with 1px gaps
    half_sheet_w = tile_width * half_tile + (tile_width - 1) * half_gap
    half_sheet_h = tile_height * half_tile + (tile_height - 1) * half_gap
    half_sheet = Image.new("RGBA", (half_sheet_w, half_sheet_h), (0, 0, 0, 0))
    for ty in range(tile_height):
        for tx in range(tile_width):
            src_x = tx * half_tile
            src_y = ty * half_tile
            tile_piece = content_clean.crop((src_x, src_y, src_x + half_tile, src_y + half_tile))
            dst_x = tx * (half_tile + half_gap)
            dst_y = ty * (half_tile + half_gap)
            half_sheet.paste(tile_piece, (dst_x, dst_y))

    # Double pixels to final size
    sheet = double_pixels(half_sheet)
    sheet.save(tile_path)
    print(f"  Tile sprite: {half_sheet_w}x{half_sheet_h} -> 2x -> {sheet.size} -> {tile_path}")

    # Item sprite: half res then double
    half_item = item_size // 2
    item_scaled = scale_to_fill(pad_to_square(cropped), half_item, padding=1)
    item_quantized = quantize_colors(item_scaled, max_colors=max_colors)
    item_sprite = double_pixels(item_quantized)
    item_sprite.save(item_path)
    print(f"  Item sprite: {half_item}x{half_item} -> 2x -> {item_sprite.size} -> {item_path}")


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
        max_colors=15,
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
