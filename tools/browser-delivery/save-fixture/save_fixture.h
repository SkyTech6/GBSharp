#ifndef GBS_BROWSER_SAVE_FIXTURE_H
#define GBS_BROWSER_SAVE_FIXTURE_H
#include <gb/gb.h>

static uint8_t fixture_previous;

static void fixture_init(void) {
    volatile uint8_t *ram = (volatile uint8_t *)0xa000;
    uint8_t tile[16];
    uint8_t i;
    DISPLAY_OFF;
    ENABLE_RAM;
    SWITCH_RAM(0);
    if (ram[0] != 0x47 || ram[1] != 0x42 || ram[2] != 0x53) {
        ram[0] = 0x47; ram[1] = 0x42; ram[2] = 0x53; ram[3] = 0;
    }
    for (i = 0; i < 16; i++) tile[i] = (i & 1) ? 0 : 0xaa;
    set_bkg_data(0, 1, tile);
    fill_bkg_rect(0, 0, 20, 18, 0);
    BGP_REG = 0xe4;
    SHOW_BKG;
    DISPLAY_ON;
}

static void fixture_tick(void) {
    uint8_t buttons = joypad();
    volatile uint8_t *ram = (volatile uint8_t *)0xa000;
    if ((buttons & J_A) && !(fixture_previous & J_A)) ram[3]++;
    fixture_previous = buttons;
}
#endif
