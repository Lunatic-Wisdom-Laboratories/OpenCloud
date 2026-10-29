#include "common.h"

#include "event.hpp"
#include "event_func.hpp"
#include "mainloop.hpp"
#include "nowload.hpp"
#include "nowload_port.hpp"
#include "padcontrol.hpp"
#include "platform/config.hpp"
#include "stubs/nowload.hpp"
#include "test_hooks.hpp"

PORT_OVERRIDE void NowLoadingBarSteEnd() {
    PortProgBarCnt = PortLoadInfo.step_count;
    PortProgBarWidthStep = GameFastLoad() ? 1.0f : 0.05f;
}

bool PortSkipIntro() { return ConfigGet().skip_intro; }

// PC setting game.start_skips_cutscenes: Start ends a skippable drama scene at once instead of opening the
// pause screen that offers Triangle to skip it. Other pauses, and a pause from a lost controller, are unchanged.
PORT_OVERRIDE int PauseStart(PAUSE_INFO *info) {
    const int paused = PortRetailPauseStart(info);
    if (!paused || info->event_skip != 1 || !ConfigGet().start_skips_cutscenes || !PadCtrl.Btn(PAD_BTN_PAUSE)) {
        return paused;
    }
    // Nothing has been paused yet, so dropping the request needs no sound to be resumed.
    PauseCancel();
    if (EdEventInfo.skip_state == EVENT_SKIP_ENABLED) {
        SkipEventStart();
    }
    return 0;
}
