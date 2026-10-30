/* -------------------------------------------------------------------------
 * GB# runtime: the parts that cannot be a header.
 *
 * Everything in gbs_runtime.h is a bare 'inline' wrapper that disappears into
 * its caller. These cannot be, for one hardware reason: they switch the mapped
 * ROM bank, and the switchable window is 0x4000-0x7FFF, which is where banked
 * code executes from. A function that switches banks while running from that
 * window unmaps itself mid-instruction. So these are NONBANKED (placed in bank
 * 0, always mapped) and out of line, because inlining one into a banked caller
 * would put the switch back in the window it replaces.
 *
 * The cost is a fixed handful of bytes in bank 0 and one real call per load.
 * Loading a background is a level-transition operation, not a per-frame one.
 * ------------------------------------------------------------------------- */

#include "gbs_runtime.h"

/* Maps a bank, or does nothing when asked for the resident one. Bank 0 is
   always mapped, so 0 is how the compiler says "this data is not banked". */
static void gbs_map(uint8_t bank) NONBANKED
{
    if (bank) {
        SWITCH_ROM(bank);
    }
}

void gbs_bank_switch(uint8_t bank) NONBANKED
{
    SWITCH_ROM(bank);
}

void gbs_background_load(
    const uint8_t *tiles, const uint8_t *map, const uint8_t *attributes,
    const uint16_t *palettes, uint8_t tile_count,
    uint8_t width, uint8_t height, uint8_t palette_count,
    uint8_t bank) NONBANKED
{
    uint8_t previous = CURRENT_BANK;

    gbs_map(bank);

    set_bkg_data(0, tile_count, tiles);

    if (DEVICE_SUPPORTS_COLOR && palettes) {
        set_bkg_palette(0, palette_count, (const palette_color_t *)palettes);

        if (attributes) {
            set_bkg_attributes(0, 0, width, height, attributes);
        }
    }

    if (map) {
        set_bkg_tiles(0, 0, width, height, map);
    }

    /* Restored so a caller's own bank survives the call. Without this, loading
       banked art would silently change which bank the caller returns into. */
    gbs_map(previous);
}

/* Same shape as gbs_background_load - the two loaders share TileMapShape in
   AssetSignature.cs - writing to the window's tile map instead of the
   background's. Tile data and palettes are genuinely shared hardware (see
   Tiles.cs), so those two calls are identical to gbs_background_load's; only
   the map and attribute writes target set_win_tiles. */
void gbs_window_load(
    const uint8_t *tiles, const uint8_t *map, const uint8_t *attributes,
    const uint16_t *palettes, uint8_t tile_count,
    uint8_t width, uint8_t height, uint8_t palette_count,
    uint8_t bank) NONBANKED
{
    uint8_t previous = CURRENT_BANK;

    gbs_map(bank);

    set_win_data(0, tile_count, tiles);

    if (DEVICE_SUPPORTS_COLOR && palettes) {
        set_bkg_palette(0, palette_count, (const palette_color_t *)palettes);

        if (attributes) {
            VBK_REG = VBK_ATTRIBUTES;
            set_win_tiles(0, 0, width, height, attributes);
            VBK_REG = VBK_TILES;
        }
    }

    if (map) {
        set_win_tiles(0, 0, width, height, map);
    }

    gbs_map(previous);
}

void gbs_background_draw_region(
    const uint8_t *tiles, const uint8_t *map, const uint8_t *attributes,
    const uint16_t *palettes, uint8_t tile_count,
    uint8_t map_width, uint8_t map_height, uint8_t palette_count,
    uint8_t bank,
    uint8_t dest_x, uint8_t dest_y, uint8_t width, uint8_t height,
    uint8_t source_x, uint8_t source_y) NONBANKED
{
    uint8_t previous = CURRENT_BANK;
    uint16_t offset = ((uint16_t)source_y * map_width) + source_x;

    (void)map_height;

    gbs_map(bank);

    /* Tiles and palettes are the whole set regardless of the window, because the
       window can move to anywhere in the map on the next call. */
    set_bkg_data(0, tile_count, tiles);

    if (DEVICE_SUPPORTS_COLOR && palettes) {
        set_bkg_palette(0, palette_count, (const palette_color_t *)palettes);

        if (attributes) {
            set_bkg_submap_attributes(dest_x, dest_y, width, height, attributes + offset, map_width);
        }
    }

    if (map) {
        /* The submap form differs from set_bkg_tiles by taking the source map's
           own width, which is what lets it stride past the part not being drawn. */
        set_bkg_submap(dest_x, dest_y, width, height, map + offset, map_width);
    }

    gbs_map(previous);
}

/* Uploads a font's glyph tiles at first_tile. glyph_table is this call's
   business, not this one's - Draw is the one that reads it - so it is
   ignored here, the same way gbs_metasprite_load ignores its frame data. */
void gbs_font_load(
    const uint8_t *tiles, const uint8_t *glyph_table, uint8_t tile_count, uint8_t bank,
    uint8_t first_tile) NONBANKED
{
    uint8_t previous = CURRENT_BANK;

    (void)glyph_table;

    gbs_map(bank);
    set_bkg_data(first_tile, tile_count, tiles);
    gbs_map(previous);
}

/* tiles and tile_count are ignored here: the glyphs are already in VRAM by
   the time anything is drawn, which is exactly what gbs_font_load was for. */
void gbs_font_draw(
    const uint8_t *tiles, const uint8_t *glyph_table, uint8_t tile_count, uint8_t bank,
    uint8_t first_tile, uint8_t x, uint8_t y, uint8_t length, const uint8_t *text) NONBANKED
{
    uint8_t previous = CURRENT_BANK;
    uint8_t i;

    (void)tiles;
    (void)tile_count;

    gbs_map(bank);

    for (i = 0; i < length; i++) {
        set_bkg_tile_xy((uint8_t)(x + i), y, (uint8_t)(first_tile + glyph_table[text[i]]));
    }

    gbs_map(previous);
}

/* Same shape as gbs_font_draw, at the window's own tile_xy setter. */
void gbs_win_font_draw(
    const uint8_t *tiles, const uint8_t *glyph_table, uint8_t tile_count, uint8_t bank,
    uint8_t first_tile, uint8_t x, uint8_t y, uint8_t length, const uint8_t *text) NONBANKED
{
    uint8_t previous = CURRENT_BANK;
    uint8_t i;

    (void)tiles;
    (void)tile_count;

    gbs_map(bank);

    for (i = 0; i < length; i++) {
        set_win_tile_xy((uint8_t)(x + i), y, (uint8_t)(first_tile + glyph_table[text[i]]));
    }

    gbs_map(previous);
}

uint8_t gbs_data_read(const uint8_t *data, uint16_t length, uint8_t bank, uint16_t index) NONBANKED
{
    uint8_t previous = CURRENT_BANK;
    uint8_t value;

    (void)length;

    gbs_map(bank);
    value = data[index];
    gbs_map(previous);

    return value;
}

void gbs_sprite_load(
    const uint8_t *tiles, const uint16_t *palettes,
    uint8_t tile_count, uint8_t palette_count,
    uint8_t bank) NONBANKED
{
    uint8_t previous = CURRENT_BANK;

    gbs_map(bank);

    set_sprite_data(0, tile_count, tiles);

    if (DEVICE_SUPPORTS_COLOR && palettes) {
        set_sprite_palette(0, palette_count, (const palette_color_t *)palettes);
    }

    gbs_map(previous);
}

/* Same upload as gbs_sprite_load; the frame data is this call's business, not
   the loader's, so it is ignored here. */
void gbs_metasprite_load(
    const uint8_t *tiles, const uint16_t *palettes,
    const uint8_t *frames, const uint8_t *frame_offsets,
    uint8_t tile_count, uint8_t palette_count, uint8_t frame_count,
    uint8_t bank) NONBANKED
{
    uint8_t previous = CURRENT_BANK;

    (void)frames;
    (void)frame_offsets;
    (void)frame_count;

    gbs_map(bank);

    set_sprite_data(0, tile_count, tiles);

    if (DEVICE_SUPPORTS_COLOR && palettes) {
        set_sprite_palette(0, palette_count, (const palette_color_t *)palettes);
    }

    gbs_map(previous);
}

/* frame_offsets[frame] is an entry index, not a byte offset, so adding it to a
   metasprite_t* strides by sizeof(metasprite_t) the way the pointer already
   promises - the frame's data needs no separate length: it is its own
   GBDK metasprite_end-terminated record, which move_metasprite_ex reads
   until it finds. base_prop is always 0: each sub-sprite already carries its
   own CGB palette and flip bits from conversion (see PngAssetCompiler), which
   GBDK's own docs call the alternate to a base_prop override. */
uint8_t gbs_metasprite_move(
    const uint8_t *tiles, const uint16_t *palettes,
    const uint8_t *frames, const uint8_t *frame_offsets,
    uint8_t tile_count, uint8_t palette_count, uint8_t frame_count,
    uint8_t bank,
    uint8_t frame, uint8_t base_tile, uint8_t base_sprite, uint8_t x, uint8_t y) NONBANKED
{
    uint8_t previous = CURRENT_BANK;
    uint8_t used;

    (void)tiles;
    (void)palettes;
    (void)tile_count;
    (void)palette_count;
    (void)frame_count;

    gbs_map(bank);
    used = move_metasprite_ex((const metasprite_t *)frames + frame_offsets[frame], base_tile, 0, base_sprite, x, y);
    gbs_map(previous);

    return used;
}

uint8_t gbs_metasprite_move_flip_x(
    const uint8_t *tiles, const uint16_t *palettes,
    const uint8_t *frames, const uint8_t *frame_offsets,
    uint8_t tile_count, uint8_t palette_count, uint8_t frame_count,
    uint8_t bank,
    uint8_t frame, uint8_t base_tile, uint8_t base_sprite, uint8_t x, uint8_t y) NONBANKED
{
    uint8_t previous = CURRENT_BANK;
    uint8_t used;

    (void)tiles;
    (void)palettes;
    (void)tile_count;
    (void)palette_count;
    (void)frame_count;

    gbs_map(bank);
    used = move_metasprite_flipx((const metasprite_t *)frames + frame_offsets[frame], base_tile, 0, base_sprite, x, y);
    gbs_map(previous);

    return used;
}

uint8_t gbs_metasprite_move_flip_y(
    const uint8_t *tiles, const uint16_t *palettes,
    const uint8_t *frames, const uint8_t *frame_offsets,
    uint8_t tile_count, uint8_t palette_count, uint8_t frame_count,
    uint8_t bank,
    uint8_t frame, uint8_t base_tile, uint8_t base_sprite, uint8_t x, uint8_t y) NONBANKED
{
    uint8_t previous = CURRENT_BANK;
    uint8_t used;

    (void)tiles;
    (void)palettes;
    (void)tile_count;
    (void)palette_count;
    (void)frame_count;

    gbs_map(bank);
    used = move_metasprite_flipy((const metasprite_t *)frames + frame_offsets[frame], base_tile, 0, base_sprite, x, y);
    gbs_map(previous);

    return used;
}

uint8_t gbs_metasprite_move_flip_xy(
    const uint8_t *tiles, const uint16_t *palettes,
    const uint8_t *frames, const uint8_t *frame_offsets,
    uint8_t tile_count, uint8_t palette_count, uint8_t frame_count,
    uint8_t bank,
    uint8_t frame, uint8_t base_tile, uint8_t base_sprite, uint8_t x, uint8_t y) NONBANKED
{
    uint8_t previous = CURRENT_BANK;
    uint8_t used;

    (void)tiles;
    (void)palettes;
    (void)tile_count;
    (void)palette_count;
    (void)frame_count;

    gbs_map(bank);
    used = move_metasprite_flipxy((const metasprite_t *)frames + frame_offsets[frame], base_tile, 0, base_sprite, x, y);
    gbs_map(previous);

    return used;
}

/* Explicit placements use 16-bit sums before narrowing. Dynamic invalid input is a
   no-op, never a partially applied/wrapped placement. No allocator is involved. */
static uint8_t gbs_placement_valid(uint8_t tile_count, uint8_t palette_count,
    uint8_t first_tile, uint8_t first_palette) NONBANKED
{
    return (uint16_t)first_tile + tile_count <= 256u &&
        first_palette < 8u && (uint16_t)first_palette + palette_count <= 8u;
}

void gbs_background_load_graphics(
    const uint8_t *tiles, const uint8_t *map, const uint8_t *attributes,
    const uint16_t *palettes, uint8_t tile_count,
    uint8_t map_width, uint8_t map_height, uint8_t palette_count, uint8_t bank,
    uint8_t first_tile, uint8_t first_palette) NONBANKED
{
    uint8_t previous, previous_vbk;
    (void)map; (void)attributes; (void)map_width; (void)map_height;
    if (!gbs_placement_valid(tile_count, palette_count, first_tile, first_palette)) return;
    previous = CURRENT_BANK;
    previous_vbk = VBK_REG;
    if (DEVICE_SUPPORTS_COLOR) VBK_REG = VBK_TILES;
    gbs_map(bank);
    if (tile_count) set_bkg_data(first_tile, tile_count, tiles);
    if (DEVICE_SUPPORTS_COLOR && palettes && palette_count)
        set_bkg_palette(first_palette, palette_count, (const palette_color_t *)palettes);
    gbs_map(previous);
    if (DEVICE_SUPPORTS_COLOR) VBK_REG = previous_vbk;
}

void gbs_background_copy_region(
    const uint8_t *tiles, const uint8_t *map, const uint8_t *attributes,
    const uint16_t *palettes, uint8_t tile_count,
    uint8_t map_width, uint8_t map_height, uint8_t palette_count, uint8_t bank,
    uint8_t dest_x, uint8_t dest_y, uint8_t width, uint8_t height,
    uint8_t source_x, uint8_t source_y, uint8_t first_tile, uint8_t first_palette) NONBANKED
{
    uint8_t previous, previous_vbk, row, col;
    uint8_t buffer[32];
    uint16_t offset;
    (void)tiles; (void)palettes;
    if (!map || !width || !height || width > 32u || height > 32u ||
        (uint16_t)source_x + width > map_width ||
        (uint16_t)source_y + height > map_height ||
        !gbs_placement_valid(tile_count, palette_count, first_tile, first_palette)) return;
    previous = CURRENT_BANK;
    previous_vbk = VBK_REG;
    if (DEVICE_SUPPORTS_COLOR) VBK_REG = VBK_TILES;
    gbs_map(bank);
    for (row = 0; row < height; ++row) {
        offset = (uint16_t)(source_y + row) * map_width + source_x;
        for (col = 0; col < width; ++col) buffer[col] = map[offset + col] + first_tile;
        set_bkg_tiles(dest_x & 31u, (dest_y + row) & 31u, width, 1, buffer);
        if (DEVICE_SUPPORTS_COLOR && attributes) {
            uint8_t previous_vbk = VBK_REG;
            for (col = 0; col < width; ++col) {
                uint8_t attr = attributes[offset + col];
                buffer[col] = (attr & 0xf8u) | ((attr & 7u) + first_palette);
            }
            VBK_REG = VBK_ATTRIBUTES;
            set_bkg_tiles(dest_x & 31u, (dest_y + row) & 31u, width, 1, buffer);
            VBK_REG = previous_vbk;
        }
    }
    gbs_map(previous);
    if (DEVICE_SUPPORTS_COLOR) VBK_REG = previous_vbk;
}

void gbs_background_load_at(
    const uint8_t *tiles, const uint8_t *map, const uint8_t *attributes,
    const uint16_t *palettes, uint8_t tile_count,
    uint8_t map_width, uint8_t map_height, uint8_t palette_count, uint8_t bank,
    uint8_t first_tile, uint8_t first_palette) NONBANKED
{
    if (map_width > 32u || map_height > 32u ||
        !gbs_placement_valid(tile_count, palette_count, first_tile, first_palette)) return;
    gbs_background_load_graphics(tiles, map, attributes, palettes, tile_count, map_width, map_height, palette_count, bank, first_tile, first_palette);
    gbs_background_copy_region(tiles, map, attributes, palettes, tile_count, map_width, map_height, palette_count, bank, 0, 0, map_width, map_height, 0, 0, first_tile, first_palette);
}

void gbs_window_load_graphics(
    const uint8_t *tiles, const uint8_t *map, const uint8_t *attributes,
    const uint16_t *palettes, uint8_t tile_count,
    uint8_t map_width, uint8_t map_height, uint8_t palette_count, uint8_t bank,
    uint8_t first_tile, uint8_t first_palette) NONBANKED
{
    uint8_t previous, previous_vbk;
    (void)map; (void)attributes; (void)map_width; (void)map_height;
    if (!gbs_placement_valid(tile_count, palette_count, first_tile, first_palette)) return;
    previous = CURRENT_BANK;
    previous_vbk = VBK_REG;
    if (DEVICE_SUPPORTS_COLOR) VBK_REG = VBK_TILES;
    gbs_map(bank);
    if (tile_count) set_bkg_data(first_tile, tile_count, tiles);
    if (DEVICE_SUPPORTS_COLOR && palettes && palette_count)
        set_bkg_palette(first_palette, palette_count, (const palette_color_t *)palettes);
    gbs_map(previous);
    if (DEVICE_SUPPORTS_COLOR) VBK_REG = previous_vbk;
}

void gbs_window_copy_region(
    const uint8_t *tiles, const uint8_t *map, const uint8_t *attributes,
    const uint16_t *palettes, uint8_t tile_count,
    uint8_t map_width, uint8_t map_height, uint8_t palette_count, uint8_t bank,
    uint8_t dest_x, uint8_t dest_y, uint8_t width, uint8_t height,
    uint8_t source_x, uint8_t source_y, uint8_t first_tile, uint8_t first_palette) NONBANKED
{
    uint8_t previous, previous_vbk, row, col;
    uint8_t buffer[32];
    uint16_t offset;
    (void)tiles; (void)palettes;
    if (!map || !width || !height || width > 32u || height > 32u ||
        (uint16_t)source_x + width > map_width ||
        (uint16_t)source_y + height > map_height ||
        !gbs_placement_valid(tile_count, palette_count, first_tile, first_palette)) return;
    previous = CURRENT_BANK;
    previous_vbk = VBK_REG;
    if (DEVICE_SUPPORTS_COLOR) VBK_REG = VBK_TILES;
    gbs_map(bank);
    for (row = 0; row < height; ++row) {
        offset = (uint16_t)(source_y + row) * map_width + source_x;
        for (col = 0; col < width; ++col) buffer[col] = map[offset + col] + first_tile;
        set_win_tiles(dest_x & 31u, (dest_y + row) & 31u, width, 1, buffer);
        if (DEVICE_SUPPORTS_COLOR && attributes) {
            uint8_t previous_vbk = VBK_REG;
            for (col = 0; col < width; ++col) {
                uint8_t attr = attributes[offset + col];
                buffer[col] = (attr & 0xf8u) | ((attr & 7u) + first_palette);
            }
            VBK_REG = VBK_ATTRIBUTES;
            set_win_tiles(dest_x & 31u, (dest_y + row) & 31u, width, 1, buffer);
            VBK_REG = previous_vbk;
        }
    }
    gbs_map(previous);
    if (DEVICE_SUPPORTS_COLOR) VBK_REG = previous_vbk;
}

void gbs_window_load_at(
    const uint8_t *tiles, const uint8_t *map, const uint8_t *attributes,
    const uint16_t *palettes, uint8_t tile_count,
    uint8_t map_width, uint8_t map_height, uint8_t palette_count, uint8_t bank,
    uint8_t first_tile, uint8_t first_palette) NONBANKED
{
    if (map_width > 32u || map_height > 32u ||
        !gbs_placement_valid(tile_count, palette_count, first_tile, first_palette)) return;
    gbs_window_load_graphics(tiles, map, attributes, palettes, tile_count, map_width, map_height, palette_count, bank, first_tile, first_palette);
    gbs_window_copy_region(tiles, map, attributes, palettes, tile_count, map_width, map_height, palette_count, bank, 0, 0, map_width, map_height, 0, 0, first_tile, first_palette);
}

void gbs_sprite_load_at(
    const uint8_t *tiles, const uint16_t *palettes,
    uint8_t tile_count, uint8_t palette_count, uint8_t bank,
    uint8_t first_tile, uint8_t first_palette) NONBANKED
{
    uint8_t previous, previous_vbk;
    if (!gbs_placement_valid(tile_count, palette_count, first_tile, first_palette) ||
        ((LCDC_REG & 4u) && (first_tile & 1u))) return;
    previous = CURRENT_BANK;
    previous_vbk = VBK_REG;
    if (DEVICE_SUPPORTS_COLOR) VBK_REG = VBK_TILES;
    gbs_map(bank);
    if (tile_count) set_sprite_data(first_tile, tile_count, tiles);
    if (DEVICE_SUPPORTS_COLOR && palettes && palette_count)
        set_sprite_palette(first_palette, palette_count, (const palette_color_t *)palettes);
    gbs_map(previous);
    if (DEVICE_SUPPORTS_COLOR) VBK_REG = previous_vbk;
}

void gbs_metasprite_load_at(
    const uint8_t *tiles, const uint16_t *palettes,
    const uint8_t *frames, const uint8_t *frame_offsets,
    uint8_t tile_count, uint8_t palette_count, uint8_t frame_count, uint8_t bank,
    uint8_t first_tile, uint8_t first_palette) NONBANKED
{
    (void)frames; (void)frame_offsets; (void)frame_count;
    gbs_sprite_load_at(tiles, palettes, tile_count, palette_count, bank, first_tile, first_palette);
}

uint8_t gbs_metasprite_move_at(
    const uint8_t *tiles, const uint16_t *palettes,
    const uint8_t *frames, const uint8_t *frame_offsets,
    uint8_t tile_count, uint8_t palette_count, uint8_t frame_count, uint8_t bank,
    uint8_t frame, uint8_t base_tile, uint8_t base_sprite, uint8_t x, uint8_t y, uint8_t first_palette) NONBANKED
{
    uint8_t previous, count = 0, used, i;
    const metasprite_t *entries;
    (void)tiles; (void)palettes;
    if (frame >= frame_count || base_sprite >= 40u ||
        !gbs_placement_valid(tile_count, palette_count, base_tile, first_palette) ||
        ((LCDC_REG & 4u) && (base_tile & 1u))) return 0;
    previous = CURRENT_BANK;
    gbs_map(bank);
    entries = (const metasprite_t *)frames + frame_offsets[frame];
    while (entries[count].dy != -128) {
        if ((uint16_t)base_sprite + count >= 40u) { gbs_map(previous); return 0; }
        ++count;
    }
    used = move_metasprite_ex(entries, base_tile, 0, base_sprite, x, y);
    if (DEVICE_SUPPORTS_COLOR && first_palette) {
        for (i = 0; i < used; ++i) {
            uint8_t attr = get_sprite_prop(base_sprite + i);
            set_sprite_prop(base_sprite + i, (attr & 0xf8u) | ((attr & 7u) + first_palette));
        }
    }
    gbs_map(previous);
    return used;
}

uint8_t gbs_metasprite_move_flip_x_at(
    const uint8_t *tiles, const uint16_t *palettes,
    const uint8_t *frames, const uint8_t *frame_offsets,
    uint8_t tile_count, uint8_t palette_count, uint8_t frame_count, uint8_t bank,
    uint8_t frame, uint8_t base_tile, uint8_t base_sprite, uint8_t x, uint8_t y, uint8_t first_palette) NONBANKED
{
    uint8_t previous, count = 0, used, i;
    const metasprite_t *entries;
    (void)tiles; (void)palettes;
    if (frame >= frame_count || base_sprite >= 40u ||
        !gbs_placement_valid(tile_count, palette_count, base_tile, first_palette) ||
        ((LCDC_REG & 4u) && (base_tile & 1u))) return 0;
    previous = CURRENT_BANK;
    gbs_map(bank);
    entries = (const metasprite_t *)frames + frame_offsets[frame];
    while (entries[count].dy != -128) {
        if ((uint16_t)base_sprite + count >= 40u) { gbs_map(previous); return 0; }
        ++count;
    }
    used = move_metasprite_flipx(entries, base_tile, 0, base_sprite, x, y);
    if (DEVICE_SUPPORTS_COLOR && first_palette) {
        for (i = 0; i < used; ++i) {
            uint8_t attr = get_sprite_prop(base_sprite + i);
            set_sprite_prop(base_sprite + i, (attr & 0xf8u) | ((attr & 7u) + first_palette));
        }
    }
    gbs_map(previous);
    return used;
}

uint8_t gbs_metasprite_move_flip_y_at(
    const uint8_t *tiles, const uint16_t *palettes,
    const uint8_t *frames, const uint8_t *frame_offsets,
    uint8_t tile_count, uint8_t palette_count, uint8_t frame_count, uint8_t bank,
    uint8_t frame, uint8_t base_tile, uint8_t base_sprite, uint8_t x, uint8_t y, uint8_t first_palette) NONBANKED
{
    uint8_t previous, count = 0, used, i;
    const metasprite_t *entries;
    (void)tiles; (void)palettes;
    if (frame >= frame_count || base_sprite >= 40u ||
        !gbs_placement_valid(tile_count, palette_count, base_tile, first_palette) ||
        ((LCDC_REG & 4u) && (base_tile & 1u))) return 0;
    previous = CURRENT_BANK;
    gbs_map(bank);
    entries = (const metasprite_t *)frames + frame_offsets[frame];
    while (entries[count].dy != -128) {
        if ((uint16_t)base_sprite + count >= 40u) { gbs_map(previous); return 0; }
        ++count;
    }
    used = move_metasprite_flipy(entries, base_tile, 0, base_sprite, x, y);
    if (DEVICE_SUPPORTS_COLOR && first_palette) {
        for (i = 0; i < used; ++i) {
            uint8_t attr = get_sprite_prop(base_sprite + i);
            set_sprite_prop(base_sprite + i, (attr & 0xf8u) | ((attr & 7u) + first_palette));
        }
    }
    gbs_map(previous);
    return used;
}

uint8_t gbs_metasprite_move_flip_xy_at(
    const uint8_t *tiles, const uint16_t *palettes,
    const uint8_t *frames, const uint8_t *frame_offsets,
    uint8_t tile_count, uint8_t palette_count, uint8_t frame_count, uint8_t bank,
    uint8_t frame, uint8_t base_tile, uint8_t base_sprite, uint8_t x, uint8_t y, uint8_t first_palette) NONBANKED
{
    uint8_t previous, count = 0, used, i;
    const metasprite_t *entries;
    (void)tiles; (void)palettes;
    if (frame >= frame_count || base_sprite >= 40u ||
        !gbs_placement_valid(tile_count, palette_count, base_tile, first_palette) ||
        ((LCDC_REG & 4u) && (base_tile & 1u))) return 0;
    previous = CURRENT_BANK;
    gbs_map(bank);
    entries = (const metasprite_t *)frames + frame_offsets[frame];
    while (entries[count].dy != -128) {
        if ((uint16_t)base_sprite + count >= 40u) { gbs_map(previous); return 0; }
        ++count;
    }
    used = move_metasprite_flipxy(entries, base_tile, 0, base_sprite, x, y);
    if (DEVICE_SUPPORTS_COLOR && first_palette) {
        for (i = 0; i < used; ++i) {
            uint8_t attr = get_sprite_prop(base_sprite + i);
            set_sprite_prop(base_sprite + i, (attr & 0xf8u) | ((attr & 7u) + first_palette));
        }
    }
    gbs_map(previous);
    return used;
}
