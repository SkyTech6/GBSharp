/*
 * GB# runtime shim for GBDK-2020.
 *
 * Every [Native] member of GBSharp.Framework names a symbol in this file or in
 * GBDK itself. The shim exists so the compiler never has to template C
 * fragments into its output: where a framework member is not a one-to-one GBDK
 * call, the adaptation lives here as ordinary C that can be read, diffed and
 * compiled on its own.
 *
 * Everything here is bare `inline`, matching GBDK's own headers, so it costs
 * nothing beyond the operation it wraps. That "costs nothing" is a promise the
 * framework's whole design rests on, and it is load-bearing here:
 *
 *   Do not change these to `static inline`. It looks more correct: it is the
 *   portable C99 spelling for a header, and GB# emits several translation units
 *   that all include this file, but on SDCC 4.5.0's sm83 port `static inline`
 *   means "emit a real out-of-line function with internal linkage, and never
 *   inline it". Measured on Samples/Hello: every one of these wrappers becomes
 *   a CALL, and all 22 are emitted into every object whether called or not,
 *   costing 242 bytes of bank 0 for a program that uses nine of them. Bare
 *   `inline` inlines them and emits nothing for the rest.
 *
 * Multiple translation units including this header link correctly with the
 * pinned toolchain; there is an integration test that would catch it if that
 * ever stopped being true.
 */

#ifndef GBS_RUNTIME_H
#define GBS_RUNTIME_H

#include <gb/gb.h>
#include <gb/cgb.h>   /* gb.h does not pull this in; the colour palettes live here. */
#include <gb/metasprites.h>
#include <stdint.h>

/* -------------------------------------------------------------------------
 * Display
 * ------------------------------------------------------------------------- */

inline void gbs_display_on(void) {
    DISPLAY_ON;
}

inline void gbs_display_off(void) {
    DISPLAY_OFF;
}

inline void gbs_show_sprites(void) {
    SHOW_SPRITES;
}

inline void gbs_show_background(void) {
    SHOW_BKG;
}

inline void gbs_hide_sprites(void)    { HIDE_SPRITES; }
inline void gbs_hide_background(void) { HIDE_BKG; }
inline void gbs_show_window(void)     { SHOW_WIN; }
inline void gbs_hide_window(void)     { HIDE_WIN; }
inline void gbs_sprites_8x16(void)    { SPRITES_8x16; }
inline void gbs_sprites_8x8(void)     { SPRITES_8x8; }

/* -------------------------------------------------------------------------
 * Frame timing
 * ------------------------------------------------------------------------- */

inline void gbs_wait_vblank(void) {
    vsync();
}

inline void gbs_halt(void) {
    wait_vbl_done();
}

/* -------------------------------------------------------------------------
 * Input
 *
 * Each of these samples the joypad. Testing several buttons therefore costs
 * several reads; Input.Read() exists for code that cares.
 * ------------------------------------------------------------------------- */

inline uint8_t gbs_input_right(void)  { return (joypad() & J_RIGHT)  != 0; }
inline uint8_t gbs_input_left(void)   { return (joypad() & J_LEFT)   != 0; }
inline uint8_t gbs_input_up(void)     { return (joypad() & J_UP)     != 0; }
inline uint8_t gbs_input_down(void)   { return (joypad() & J_DOWN)   != 0; }
inline uint8_t gbs_input_a(void)      { return (joypad() & J_A)      != 0; }
inline uint8_t gbs_input_b(void)      { return (joypad() & J_B)      != 0; }
inline uint8_t gbs_input_start(void)  { return (joypad() & J_START)  != 0; }
inline uint8_t gbs_input_select(void) { return (joypad() & J_SELECT) != 0; }

/* -------------------------------------------------------------------------
 * Sprites
 *
 * Reads and writes go through shadow OAM, which GBDK DMAs into real OAM each
 * VBlank. Writing a coordinate directly is what makes Sprites[n].X assignable
 * at the cost of a single byte store.
 * ------------------------------------------------------------------------- */

inline void gbs_sprite_move(uint8_t id, uint8_t x, uint8_t y) {
    move_sprite(id, x, y);
}

inline void gbs_sprite_set_tile(uint8_t id, uint8_t tile) {
    set_sprite_tile(id, tile);
}

inline void gbs_sprite_hide(uint8_t id) {
    move_sprite(id, 0, 0);
}

inline uint8_t gbs_sprite_get_x(uint8_t id) {
    return shadow_OAM[id].x;
}

inline uint8_t gbs_sprite_get_y(uint8_t id) {
    return shadow_OAM[id].y;
}

inline uint8_t gbs_sprite_get_tile(uint8_t id) {
    return shadow_OAM[id].tile;
}

inline void gbs_sprite_set_x(uint8_t id, uint8_t x) {
    shadow_OAM[id].x = x;
}

inline void gbs_sprite_set_y(uint8_t id, uint8_t y) {
    shadow_OAM[id].y = y;
}

/* GBDK 4.5.0 declares hide_sprite(nb) but no hide_sprites_range, despite the
   latter being named in gb.h's own comments, so this is a real loop. */
inline void gbs_sprites_hide_all(void) {
    for (uint8_t i = 0; i < 40; i++) {
        shadow_OAM[i].y = 0;
    }
}

inline void gbs_sprite_load_tiles(uint8_t first, uint8_t count, const uint8_t *data) {
    set_sprite_data(first, count, data);
}

inline uint8_t gbs_sprite_get_flip_x(uint8_t id) { return (shadow_OAM[id].prop & S_FLIPX) != 0; }
inline uint8_t gbs_sprite_get_flip_y(uint8_t id) { return (shadow_OAM[id].prop & S_FLIPY) != 0; }
inline uint8_t gbs_sprite_get_priority(uint8_t id) { return (shadow_OAM[id].prop & S_PRIORITY) != 0; }
inline uint8_t gbs_sprite_get_dmg_palette(uint8_t id) { return (shadow_OAM[id].prop & S_PALETTE) != 0; }

/* The low three bits are the CGB palette; S_PALETTE (bit 4) is the DMG one.
   They are different bits and mean different things, which is why the framework
   exposes them as two properties rather than one. */
inline uint8_t gbs_sprite_get_palette(uint8_t id) { return shadow_OAM[id].prop & 0x07U; }

inline void gbs_sprite_set_flip_x(uint8_t id, uint8_t on) {
    if (on) shadow_OAM[id].prop |= S_FLIPX; else shadow_OAM[id].prop &= (uint8_t)~S_FLIPX;
}

inline void gbs_sprite_set_flip_y(uint8_t id, uint8_t on) {
    if (on) shadow_OAM[id].prop |= S_FLIPY; else shadow_OAM[id].prop &= (uint8_t)~S_FLIPY;
}

inline void gbs_sprite_set_priority(uint8_t id, uint8_t on) {
    if (on) shadow_OAM[id].prop |= S_PRIORITY; else shadow_OAM[id].prop &= (uint8_t)~S_PRIORITY;
}

inline void gbs_sprite_set_dmg_palette(uint8_t id, uint8_t on) {
    if (on) shadow_OAM[id].prop |= S_PALETTE; else shadow_OAM[id].prop &= (uint8_t)~S_PALETTE;
}

inline void gbs_sprite_set_palette(uint8_t id, uint8_t palette) {
    shadow_OAM[id].prop = (uint8_t)((shadow_OAM[id].prop & 0xF8U) | (palette & 0x07U));
}

/* -------------------------------------------------------------------------
 * Background and window
 *
 * The background and window share one tile region; sprites have their own.
 * ------------------------------------------------------------------------- */

inline void gbs_bkg_load_tiles(uint8_t first, uint8_t count, const uint8_t *data) {
    set_bkg_data(first, count, data);
}

inline void gbs_win_load_tiles(uint8_t first, uint8_t count, const uint8_t *data) {
    set_win_data(first, count, data);
}

inline void gbs_bkg_load_map(uint8_t x, uint8_t y, uint8_t w, uint8_t h, const uint8_t *map) {
    set_bkg_tiles(x, y, w, h, map);
}

inline void gbs_win_load_map(uint8_t x, uint8_t y, uint8_t w, uint8_t h, const uint8_t *map) {
    set_win_tiles(x, y, w, h, map);
}

/* Does nothing on DMG: set_bkg_attributes handles the VRAM bank switch and the
   hardware ignores the second bank entirely. */
inline void gbs_bkg_load_attributes(uint8_t x, uint8_t y, uint8_t w, uint8_t h, const uint8_t *attr) {
    set_bkg_attributes(x, y, w, h, attr);
}

/* GBDK has no set_win_attributes: the window and background share the same
   VBK-selected attribute plane, so this is set_bkg_attributes' own VBK dance
   with set_win_tiles in place of set_bkg_tiles. Does nothing on DMG, same as
   gbs_bkg_load_attributes above. */
inline void gbs_win_load_attributes(uint8_t x, uint8_t y, uint8_t w, uint8_t h, const uint8_t *attr) {
    VBK_REG = VBK_ATTRIBUTES;
    set_win_tiles(x, y, w, h, attr);
    VBK_REG = VBK_TILES;
}

/* set_bkg_tile_xy returns the VRAM address it wrote; the framework declares
   these void rather than have the IR carry a return type nothing reads. */
inline void gbs_bkg_set_tile(uint8_t x, uint8_t y, uint8_t tile) { set_bkg_tile_xy(x, y, tile); }
inline void gbs_win_set_tile(uint8_t x, uint8_t y, uint8_t tile) { set_win_tile_xy(x, y, tile); }

inline uint8_t gbs_bkg_get_scroll_x(void)      { return SCX_REG; }
inline uint8_t gbs_bkg_get_scroll_y(void)      { return SCY_REG; }
inline void    gbs_bkg_set_scroll_x(uint8_t x) { SCX_REG = x; }
inline void    gbs_bkg_set_scroll_y(uint8_t y) { SCY_REG = y; }

inline uint8_t gbs_win_get_x(void)      { return WX_REG; }
inline uint8_t gbs_win_get_y(void)      { return WY_REG; }
inline void    gbs_win_set_x(uint8_t x) { WX_REG = x; }
inline void    gbs_win_set_y(uint8_t y) { WY_REG = y; }

/* -------------------------------------------------------------------------
 * Converted assets
 *
 * The compiler expands one C# argument into this parameter list: the pointers
 * to the asset's ROM tables, then the sizes, then the bank holding them. The
 * order is a contract with AssetBindings.Materialize: change one and the other
 * has to change with it.
 *
 * A DMG build passes null for the colour tables, and a DMG machine running a
 * colour build skips them at runtime, so one call covers both.
 *
 * A bank of 0 means the data is resident and no switch is needed, so a program
 * that never banks anything pays nothing for this parameter.
 *
 * These two are defined in gbs_runtime.c rather than here, and are NONBANKED.
 * They switch banks, and code performing a switch has to stay mapped while it
 * runs: banked code lives at 0x4000-0x7FFF, which is the window the switch
 * replaces. That also rules out 'inline': inlining one of these into a banked
 * caller would put the switch back in the window it unmaps.
 * ------------------------------------------------------------------------- */

void gbs_background_load(
    const uint8_t *tiles, const uint8_t *map, const uint8_t *attributes,
    const uint16_t *palettes, uint8_t tile_count,
    uint8_t width, uint8_t height, uint8_t palette_count,
    uint8_t bank) NONBANKED;

/* Same argument shape as gbs_background_load (TileMapShape in
   AssetSignature.cs), loading onto the window layer instead. */
void gbs_window_load(
    const uint8_t *tiles, const uint8_t *map, const uint8_t *attributes,
    const uint16_t *palettes, uint8_t tile_count,
    uint8_t width, uint8_t height, uint8_t palette_count,
    uint8_t bank) NONBANKED;

void gbs_sprite_load(
    const uint8_t *tiles, const uint16_t *palettes,
    uint8_t tile_count, uint8_t palette_count,
    uint8_t bank) NONBANKED;

/* Copies a window of a map larger than the hardware's 32x32 into VRAM. The
   asset's own width is argument 6, which is what makes the source rows stride
   correctly: a submap has to know how wide the map it came from is. */
void gbs_background_draw_region(
    const uint8_t *tiles, const uint8_t *map, const uint8_t *attributes,
    const uint16_t *palettes, uint8_t tile_count,
    uint8_t map_width, uint8_t map_height, uint8_t palette_count,
    uint8_t bank,
    uint8_t dest_x, uint8_t dest_y, uint8_t width, uint8_t height,
    uint8_t source_x, uint8_t source_y) NONBANKED;

/* Switches the mapped ROM bank. See GB.Banking.Switch. */
void gbs_bank_switch(uint8_t bank) NONBANKED;

/* -------------------------------------------------------------------------
 * Metasprites
 *
 * A [Metasprite] field expands to the same tiles-and-palettes prefix as a
 * [Sprite] one, plus a pointer to the packed metasprite_t frame records, a
 * pointer to the per-frame offset table, and the frame count.
 *
 * gbs_metasprite_load uploads the shared tiles and palettes once, like
 * gbs_sprite_load, and ignores the frame data. gbs_metasprite_move and its
 * flip variants ignore the tiles and palettes - those were already uploaded -
 * and use the frame data to find one frame's entries before handing them to
 * GBDK's move_metasprite_ex family. Both shapes come from the one binding: see
 * AssetSignature's remarks on why this argument list is a superset.
 * ------------------------------------------------------------------------- */

void gbs_metasprite_load(
    const uint8_t *tiles, const uint16_t *palettes,
    const uint8_t *frames, const uint8_t *frame_offsets,
    uint8_t tile_count, uint8_t palette_count, uint8_t frame_count,
    uint8_t bank) NONBANKED;

uint8_t gbs_metasprite_move(
    const uint8_t *tiles, const uint16_t *palettes,
    const uint8_t *frames, const uint8_t *frame_offsets,
    uint8_t tile_count, uint8_t palette_count, uint8_t frame_count,
    uint8_t bank,
    uint8_t frame, uint8_t base_tile, uint8_t base_sprite, uint8_t x, uint8_t y) NONBANKED;

uint8_t gbs_metasprite_move_flip_x(
    const uint8_t *tiles, const uint16_t *palettes,
    const uint8_t *frames, const uint8_t *frame_offsets,
    uint8_t tile_count, uint8_t palette_count, uint8_t frame_count,
    uint8_t bank,
    uint8_t frame, uint8_t base_tile, uint8_t base_sprite, uint8_t x, uint8_t y) NONBANKED;

uint8_t gbs_metasprite_move_flip_y(
    const uint8_t *tiles, const uint16_t *palettes,
    const uint8_t *frames, const uint8_t *frame_offsets,
    uint8_t tile_count, uint8_t palette_count, uint8_t frame_count,
    uint8_t bank,
    uint8_t frame, uint8_t base_tile, uint8_t base_sprite, uint8_t x, uint8_t y) NONBANKED;

uint8_t gbs_metasprite_move_flip_xy(
    const uint8_t *tiles, const uint16_t *palettes,
    const uint8_t *frames, const uint8_t *frame_offsets,
    uint8_t tile_count, uint8_t palette_count, uint8_t frame_count,
    uint8_t bank,
    uint8_t frame, uint8_t base_tile, uint8_t base_sprite, uint8_t x, uint8_t y) NONBANKED;

/* -------------------------------------------------------------------------
 * Fonts
 *
 * A [Font] field expands to the same tiles-plus-bank prefix as a [Sprite] one,
 * with a glyph table in place of palettes: (tiles, glyph_table, tile_count,
 * bank). Text.Load and Text.Draw share that expansion plus first_tile - the
 * same "superset shape shared by two calls" pattern as the metasprite pair
 * above. gbs_font_load ignores glyph_table (only Draw reads it); gbs_font_draw
 * ignores tiles and tile_count (the glyphs are already in VRAM by the time
 * anything is drawn).
 *
 * There is no cursor and no scrolling here, deliberately: gbs_font_draw is one
 * pass over a caller-given byte range, at a caller-given cell. GBDK's own
 * console.h and font.h add exactly the state GB# does not want to hide.
 * ------------------------------------------------------------------------- */

void gbs_font_load(
    const uint8_t *tiles, const uint8_t *glyph_table, uint8_t tile_count, uint8_t bank,
    uint8_t first_tile) NONBANKED;

/* length is uint8_t, matching every other count in this file: a screen row is
   at most 32 tiles, so a byte is not a limitation here the way it would be for
   an arbitrary binary blob (see gbs_data_read). */
void gbs_font_draw(
    const uint8_t *tiles, const uint8_t *glyph_table, uint8_t tile_count, uint8_t bank,
    uint8_t first_tile, uint8_t x, uint8_t y, uint8_t length, const uint8_t *text) NONBANKED;

/* Same shape as gbs_font_draw (both share [Font]'s FontShape expansion),
   writing to the window map with set_win_tile_xy instead. */
void gbs_win_font_draw(
    const uint8_t *tiles, const uint8_t *glyph_table, uint8_t tile_count, uint8_t bank,
    uint8_t first_tile, uint8_t x, uint8_t y, uint8_t length, const uint8_t *text) NONBANKED;

/* -------------------------------------------------------------------------
 * Binary assets
 *
 * A [Binary] field expands to a pointer, a length and a bank. The length is
 * uint16_t because a data file exceeds 255 bytes immediately, which is the case
 * the image kinds never reach.
 *
 * The read is NONBANKED and out of line for the same reason as the loaders: it
 * may have to map the data in first.
 * ------------------------------------------------------------------------- */

inline uint16_t gbs_data_length(const uint8_t *data, uint16_t length, uint8_t bank) {
    (void)data; (void)bank;
    return length;
}

uint8_t gbs_data_read(const uint8_t *data, uint16_t length, uint8_t bank, uint16_t index) NONBANKED;

/* -------------------------------------------------------------------------
 * Palettes
 * ------------------------------------------------------------------------- */

inline uint8_t gbs_is_color(void) { return DEVICE_SUPPORTS_COLOR; }

inline void gbs_set_bkg_shades(uint8_t c0, uint8_t c1, uint8_t c2, uint8_t c3) {
    BGP_REG = DMG_PALETTE(c0, c1, c2, c3);
}

inline void gbs_set_sprite_shades(uint8_t palette, uint8_t c0, uint8_t c1, uint8_t c2, uint8_t c3) {
    if (palette) OBP1_REG = DMG_PALETTE(c0, c1, c2, c3);
    else         OBP0_REG = DMG_PALETTE(c0, c1, c2, c3);
}

inline uint8_t gbs_get_bgp(void)          { return BGP_REG; }
inline void    gbs_set_bgp(uint8_t value) { BGP_REG = value; }

inline uint16_t gbs_rgb(uint8_t r, uint8_t g, uint8_t b) { return RGB(r, g, b); }

/* palette_color_t is a typedef for uint16_t, so the cast is documentation. */
inline void gbs_set_bkg_palette(uint8_t first, uint8_t count, const uint16_t *colors) {
    set_bkg_palette(first, count, (const palette_color_t *)colors);
}

inline void gbs_set_sprite_palette(uint8_t first, uint8_t count, const uint16_t *colors) {
    set_sprite_palette(first, count, (const palette_color_t *)colors);
}

/* -------------------------------------------------------------------------
 * Audio
 *
 * Register-level only. Nothing here sequences anything: a note starts and
 * plays until it is stopped.
 * ------------------------------------------------------------------------- */

inline void gbs_audio_on(void) {
    NR52_REG = 0x80U;   /* power on; every other register is ignored until this */
    NR51_REG = 0xFFU;   /* all four channels to both speakers */
    NR50_REG = 0x77U;   /* full volume both sides */
}

inline void gbs_audio_off(void) { NR52_REG = 0x00U; }

inline void gbs_audio_master_volume(uint8_t left, uint8_t right) {
    NR50_REG = (uint8_t)(((left & 0x07U) << 4) | (right & 0x07U));
}

inline void gbs_audio_routing(uint8_t mask) { NR51_REG = mask; }

/* volume is the envelope's starting level, 0-15; 0 is silence. Bit 7 of the
   high frequency register is the trigger that restarts the note. */
inline void gbs_audio_tone(uint8_t channel, uint16_t period, uint8_t volume, uint8_t duty) {
    if (channel == 1U) {
        NR10_REG = 0x00U;                                   /* no sweep */
        NR11_REG = (uint8_t)(duty & 0xC0U);
        NR12_REG = (uint8_t)((volume & 0x0FU) << 4);
        NR13_REG = (uint8_t)(period & 0xFFU);
        NR14_REG = (uint8_t)(0x80U | ((period >> 8) & 0x07U));
    } else if (channel == 2U) {
        NR21_REG = (uint8_t)(duty & 0xC0U);
        NR22_REG = (uint8_t)((volume & 0x0FU) << 4);
        NR23_REG = (uint8_t)(period & 0xFFU);
        NR24_REG = (uint8_t)(0x80U | ((period >> 8) & 0x07U));
    }
}

inline void gbs_audio_noise(uint8_t volume, uint8_t period) {
    NR41_REG = 0x00U;
    NR42_REG = (uint8_t)((volume & 0x0FU) << 4);
    NR43_REG = period;
    NR44_REG = 0x80U;
}

inline void gbs_audio_stop(uint8_t channel) {
    if      (channel == 1U) NR12_REG = 0x00U;
    else if (channel == 2U) NR22_REG = 0x00U;
    else if (channel == 3U) NR32_REG = 0x00U;
    else                    NR42_REG = 0x00U;
}

#endif /* GBS_RUNTIME_H */
