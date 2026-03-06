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

from PIL import Image, ImageFilter


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
    rgb_q = rgb.quantize(colors=max_colors, method=Image.MAXCOVERAGE, dither=Image.NONE)
    rgb_back = rgb_q.convert("RGB")

    # Recombine with original alpha (thresholded to fully opaque/transparent)
    a_thresh = a.point(lambda p: 255 if p >= 128 else 0)
    return Image.merge("RGBA", (*rgb_back.split(), a_thresh))


def stylize_background_game_pixels(img, max_colors=32):
    """Clean up a tiny background image before final nearest upscale.

    Runs entirely at the bottleneck size (game-pixel grid) to improve edge readability
    after pixelation without globally shifting the image look.
    """
    if img.mode != "RGBA":
        img = img.convert("RGBA")

    rgb = img.convert("RGB")

    # Conservative palette reduction first.
    rgb_q = rgb.quantize(
        colors=max_colors, method=Image.MAXCOVERAGE, dither=Image.NONE
    ).convert("RGB")

    # Subtle major-edge darkening only (no global contrast/saturation changes).
    edges = rgb_q.filter(ImageFilter.FIND_EDGES).convert("L")
    edge_mask = edges.point(lambda p: 44 if p >= 100 else 0)
    rgb_rgba = rgb_q.convert("RGBA")
    edge_overlay = Image.new("RGBA", rgb_rgba.size, (0, 0, 0, 0))
    edge_overlay.putalpha(edge_mask)
    rgb_rgba.alpha_composite(edge_overlay)

    a = img.split()[3].point(lambda p: 255 if p >= 128 else 0)
    return Image.merge("RGBA", (*rgb_rgba.convert("RGB").split(), a))


def normalize_outer_border_colors(img, dark_color=(36, 27, 24)):
    """Unify only the outermost border ring to a consistent dark tone.

    Keeps mossy green pixels on the border, but normalizes brown/grey border noise.
    Operates on the small game-pixel image before nearest-neighbor upscale.
    """
    if img.mode != "RGBA":
        img = img.convert("RGBA")

    out = img.copy()
    px = out.load()
    w, h = out.size

    def is_greenish(r, g, b):
        return g >= r + 10 and g >= b + 10 and g >= 60

    def maybe_normalize(x, y):
        r, g, b, a = px[x, y]
        if a < 128:
            return
        if is_greenish(r, g, b):
            return
        px[x, y] = (dark_color[0], dark_color[1], dark_color[2], 255)

    for x in range(w):
        maybe_normalize(x, 0)
        maybe_normalize(x, h - 1)
    for y in range(h):
        maybe_normalize(0, y)
        maybe_normalize(w - 1, y)

    return out


def process_bag_sprite(raw_path, npc_path, item_path, canvas_size=48, max_colors=32):
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
    print(
        f"  NPC sprite: {half_canvas}x{half_canvas} -> 2x -> {npc_sprite.size} -> {npc_path}"
    )

    # Item sprite: same as NPC
    item_scaled = scale_to_fill(squared, half_canvas, padding=1)
    item_quantized = quantize_colors(item_scaled, max_colors=max_colors)
    item_sprite = double_pixels(item_quantized)
    item_sprite.save(item_path)
    print(
        f"  Item sprite: {half_canvas}x{half_canvas} -> 2x -> {item_sprite.size} -> {item_path}"
    )


def process_tile_sprite(
    raw_path,
    tile_path,
    item_path,
    highlight_path=None,
    tile_width=3,
    tile_height=3,
    item_size=48,
    max_colors=32,
):
    """Process a raw tile image into a tile sprite sheet and item sprite.

    Tile sprite sheets use 16px tiles with 2px padding between them.
    Works at half resolution then doubles pixels to match Terraria's
    2x2 game-pixel aesthetic. Each tile cell is 8x8 game-pixels (16x16 real).

    If highlight_path is given, also generates a smart-interact highlight
    texture (white border pixels) from the continuous half-res content,
    before slicing into tile cells.
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
            tile_piece = content_clean.crop(
                (src_x, src_y, src_x + half_tile, src_y + half_tile)
            )
            dst_x = tx * (half_tile + half_gap)
            dst_y = ty * (half_tile + half_gap)
            half_sheet.paste(tile_piece, (dst_x, dst_y))

    # Double pixels to final size
    sheet = double_pixels(half_sheet)
    sheet.save(tile_path)
    print(
        f"  Tile sprite: {half_sheet_w}x{half_sheet_h} -> 2x -> {sheet.size} -> {tile_path}"
    )

    # Highlight texture: border detection on continuous half-res, then same slice+double
    if highlight_path is not None:
        half_highlight = border_mask(content_clean)
        half_highlight_sheet = Image.new(
            "RGBA", (half_sheet_w, half_sheet_h), (0, 0, 0, 0)
        )
        for ty in range(tile_height):
            for tx in range(tile_width):
                src_x = tx * half_tile
                src_y = ty * half_tile
                piece = half_highlight.crop(
                    (src_x, src_y, src_x + half_tile, src_y + half_tile)
                )
                dst_x = tx * (half_tile + half_gap)
                dst_y = ty * (half_tile + half_gap)
                half_highlight_sheet.paste(piece, (dst_x, dst_y))
        highlight_sheet = double_pixels(half_highlight_sheet)
        highlight_sheet.save(highlight_path)
        print(
            f"  Highlight: {half_sheet_w}x{half_sheet_h} -> 2x -> {highlight_sheet.size} -> {highlight_path}"
        )

    # Item sprite: half res then double
    half_item = item_size // 2
    item_scaled = scale_to_fill(pad_to_square(cropped), half_item, padding=1)
    item_quantized = quantize_colors(item_scaled, max_colors=max_colors)
    item_sprite = double_pixels(item_quantized)
    item_sprite.save(item_path)
    print(
        f"  Item sprite: {half_item}x{half_item} -> 2x -> {item_sprite.size} -> {item_path}"
    )


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


def process_mod_icon_pixelated(
    bg_path,
    bag_path,
    icon_path,
    icon_game_pixels=48,
    target_size=480,
    max_colors_bg=32,
    max_colors_bag=32,
    bag_fraction=0.5,
):
    """Generate a Terraria-style pixelated mod icon.

    Terraria sprites in this repo are authored at "game pixel" resolution and then
    doubled 2x with nearest-neighbor. The DeathBag sprite is 48x48 real pixels,
    meaning it is 24x24 game pixels.

    This icon pipeline:
    - Background: downscale to icon_game_pixels grid, then upscale with nearest
    - Bag: keep the in-game sprite as-is (no downsampling), and upscale with nearest
      to occupy bag_fraction of the final icon width
    """
    if target_size % icon_game_pixels != 0:
        raise ValueError("target_size must be divisible by icon_game_pixels")
    if not (0.1 <= bag_fraction <= 0.9):
        raise ValueError("bag_fraction must be between 0.1 and 0.9")

    # Background: fit to square and downscale to game-pixel grid
    bg = Image.open(bg_path).convert("RGBA")
    bg = pad_to_square(autocrop(bg))

    bg_gp = bg.resize((icon_game_pixels, icon_game_pixels), Image.LANCZOS)
    bg_gp = stylize_background_game_pixels(bg_gp, max_colors=max_colors_bg)
    bg_gp = normalize_outer_border_colors(bg_gp)

    # Upscale background to final icon
    scale = target_size // icon_game_pixels
    icon = bg_gp.resize(
        (icon_game_pixels * scale, icon_game_pixels * scale), Image.NEAREST
    )

    # Bag: keep the sprite as-is, upscale to desired fraction of final width
    bag = Image.open(bag_path).convert("RGBA")
    bag = quantize_colors(bag, max_colors=max_colors_bag)
    bag_target = int(target_size * bag_fraction)
    bag_scale = max(1, bag_target // bag.width)
    bag_scaled = bag.resize(
        (bag.width * bag_scale, bag.height * bag_scale), Image.NEAREST
    )

    ox = (target_size - bag_scaled.width) // 2
    oy = (target_size - bag_scaled.height) // 2
    icon.alpha_composite(bag_scaled, (ox, oy))

    icon.save(icon_path)
    print(f"  Pixel icon: bg={icon_game_pixels}gp bag={bag_scaled.size} -> {icon_path}")


def border_mask(img):
    """Return an image with white border pixels of opaque regions.

    Works on a continuous image (no spritesheet gaps). An opaque pixel is
    a border pixel if any 4-connected neighbor is transparent or out of bounds.
    """
    px = img.load()
    w, h = img.size
    mask = Image.new("RGBA", (w, h), (0, 0, 0, 0))
    mpx = mask.load()

    for y in range(h):
        for x in range(w):
            if px[x, y][3] >= 128:
                is_border = False
                for dx, dy in [(-1, 0), (1, 0), (0, -1), (0, 1)]:
                    nx, ny = x + dx, y + dy
                    if nx < 0 or nx >= w or ny < 0 or ny >= h:
                        is_border = True
                        break
                    if px[nx, ny][3] < 128:
                        is_border = True
                        break
                if is_border:
                    mpx[x, y] = (255, 255, 255, 255)

    return mask


if __name__ == "__main__":
    print("Processing death bag...")
    process_bag_sprite(
        "deathbag-raw.png",
        "Common/NPCs/DeathBagNPC.png",
        "Common/Items/DeathBagItem.png",
    )

    print("\nProcessing loadout bag...")
    process_bag_sprite(
        "loadoutbag-raw.png",
        "Common/NPCs/LoadoutBagNPC.png",
        "Common/Items/LoadoutBagItem.png",
    )

    print("\nProcessing loadout station...")
    process_tile_sprite(
        "loadoutstation-raw.png",
        "Common/Tiles/LoadoutStationTile.png",
        "Common/Items/LoadoutStationItem.png",
        highlight_path="Common/Tiles/LoadoutStationTile_Highlight.png",
    )

    print("\nProcessing mod icon...")
    process_mod_icon_pixelated(
        "modicon-bg.png",
        "Common/NPCs/DeathBagNPC.png",
        "icon.png",
    )

    print("\nDone! All sprites generated.")
