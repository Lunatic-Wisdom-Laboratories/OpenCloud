#include "common.h"

#include "mg_texture.hpp"
#include "mglib.hpp"
#include "nd_meswin.hpp"
#include "gamepad.hpp"
#include "nowload_port.hpp"
#include "quit_game.hpp"
#include "stubs/title.hpp"

namespace {
// System message 102, "Reading memory card (PS2).", which retail shows while it scans both cards at boot.
constexpr int kReadingCardMessage = 0x66;
} // namespace

// The PC save folder needs no boot scan notice. The no-space and no-card notices still draw.
PORT_OVERRIDE void TitleMCCheckDraw() {
    if (TitleMCCheckMes != nullptr && TitleMCCheckMes->mes_no != kReadingCardMessage) {
        mgTexManager.ReloadTexture(0x46, static_cast<sceVif1Packet *>(nullptr));
        TitleMCCheckMes->Step();
        TitleMCCheckMes->DrawMesWin();
    }
}

// The copyright notice and the Level-5 logo movie, which game.skip_intro passes straight by.
PORT_OVERRIDE int TitleCopyRightStep() {
    return PortSkipIntro() ? 1 : PortRetailTitleCopyRightStep();
}

// QUIT GAME (quit_game.hpp) is offered while the title menu waits for a choice. While its question is asked, the
// title still runs and draws, with the pad locked so the menu underneath does not move.
PORT_OVERRIDE int TitleLoop() {
    const bool ready = TitleInfo != nullptr && TitleInfo->mode == TITLE_MODE_TITLE && TitlePhase == TITLE_PHASE_MENU &&
                       TitleMCCheckNow == 0;
    if (!quitgame::Run(quitgame::Place::Title, ready)) {
        return PortRetailTitleLoop();
    }
    const int lock = GamePad__2.key_lock;
    GamePad__2.key_lock = 1;
    const int result = PortRetailTitleLoop();
    GamePad__2.key_lock = lock;
    return result;
}

PORT_OVERRIDE void TitleModeDraw() {
    PortRetailTitleModeDraw();
    quitgame::Draw(quitgame::Place::Title);
}
