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
