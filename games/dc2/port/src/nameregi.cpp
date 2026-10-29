#include "common.h"

#include <cstring>
#include <string>

#include "mainloop.hpp"
#include "menucommon.hpp"
#include "nameregi.hpp"
#include "platform/input.hpp"
#include "snd_mngr.hpp"
#include "stubs/nameregi.hpp"

namespace {

/**
 * Reports whether an ASCII character is available on the non-Japanese name-entry grids.
 */
bool OnGrid(char c) {
    return (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') ||
           (c != 0 && std::strchr("!?\"#$&'()*+-%,.:;<=>@[]_{}|/ ", c) != nullptr);
}

/** Identifies the name-entry command that accepts the entered name. */
constexpr int kCommandOk = 11;

/**
 * Applies a typed character or editing command to the active name.
 */
void Type(CNameRegiMenu &menu, char c) {
    const int max = NameRegistMax;
    if (c == '\b') {
        // The Back button (event 0x64).
        MenuSePlay(SYSTEM_SE_CANCEL);
        if (menu.name_pos != 0) {
            for (int index = menu.name_pos; index < max; index++) {
                menu.name[index - 1] = menu.name[index];
            }
            menu.name[max - 1] = 0;
            menu.button_flash[7] = 8;
            menu.name_pos = menu.name_pos > 0 ? menu.name_pos - 1 : 0;
            menu.caret_cnt = 0x28;
        }
        return;
    }
    if (c == '\x7f') {
        // The Delete button (event 0x6E).
        for (int index = menu.name_pos; index < max; index++) {
            menu.name[index] = menu.name[index + 1];
        }
        menu.name[max] = 0;
        menu.button_flash[8] = 8;
        menu.caret_cnt = 0x28;
        MenuSePlay(SYSTEM_SE_CANCEL);
        return;
    }
    if (c == '\r') {
        // Return presses Cross (InputSetTextEntry); on the OK button that accepts the name.
        menu.key_arg_no = 0;
        menu.command_pos = kCommandOk;
        return;
    }
    if (!OnGrid(c)) {
        return;
    }
    // A character picked on the grid (event 5): the caret's cell takes it and the caret moves on.
    for (int index = 0; index < menu.name_pos; index++) {
        if (menu.name[index] == 0) {
            menu.name[index] = 0x20;
        }
    }
    menu.name[menu.name_pos] = c;
    menu.name_pos += 1;
    if (max <= menu.name_pos) {
        menu.name_pos = max - 1;
    }
    menu.caret_cnt = 0x28;
    MenuSePlay(SYSTEM_SE_DECIDE);
}

} // namespace

PORT_OVERRIDE int NameRegistKey() {
    CNameRegiMenu &menu = *NameRegiMenuPtr;
    // Japanese names are typed in Shift-JIS from the kana grids; the other languages store ASCII.
    const bool typing = LanguageCode > 0 && menu.mode == NAMEREGI_MODE_INPUT && menu.message_open == 0;
    InputSetTextEntry(typing);
    if (typing) {
        for (char c : InputTakeText()) {
            Type(menu, c);
        }
    }
    return menu.KeyStep();
}
