#include "common.h"

#include <algorithm>
#include <cstdlib>
#include <cstring>

#include "editevent.hpp"
#include "editexception.hpp"
#include "event_func.hpp"
#include "mapparts.hpp"
#include "mglib.hpp"
#include "scenesnd.hpp"
#include "sound.hpp"

namespace {
int  next_thunder;
int  fade_frames;
int  sound_frames;
bool sound_pending;
} // namespace

PORT_OVERRIDE void InitS51Thunder() {
    next_thunder = 60;
    fade_frames = 0;
    sound_frames = 0;
    sound_pending = false;
}

PORT_OVERRIDE void S51Thunder(CScene *scene) {
    const char *name = scene->GetMapName(scene->active_map);
    if (name == nullptr || std::strcmp(name, "s51") != 0) {
        return;
    }
    if (next_thunder == 0) {
        fade_frames = 40;
        next_thunder = std::rand() % 150 + 10;
        sound_pending = true;
        sound_frames = std::rand() % 20;
        if (next_thunder < sound_frames) {
            sound_frames = 1;
        }
    }
    if (sound_pending && --sound_frames <= 0) {
        sound_frames = 0;
        sound_pending = false;
        sndSePlay(EdEventInfo.snd_id[4], std::rand() % 4 + 0x15, 0);
    }
    --next_thunder;
    fade_frames = std::max(0, fade_frames - 1);
    const float fade = static_cast<float>(fade_frames) / 40.0f;
    CMap       *map = scene->GetMap(scene->active_map);
    float       ratio[MAP_TIME_BAND_NUM]{};
    if (map == nullptr || map->GetTimeLightingRatio(ratio) < 2) {
        return;
    }
    ratio[0] = 1.0f - fade;
    ratio[1] = fade;
    CMapLightingInfo info{};
    map->GetLightInfo(&info, ratio, 2);
    mgSetLight(info.light_dir, info.light_color);
    mgSetAmbient(info.ambient);
    for (int i = 0; i < 2; ++i) {
        CMapParts *parts = map->GetPlaceParts(i == 0 ? const_cast<char *>("p05_s5102-0")
                                                     : const_cast<char *>("p11_s5102-0"));
        if (parts == nullptr) {
            continue;
        }
        parts->show = 1;
        for (CList<CMapPiece> *node = parts->piece_list; node != nullptr; node = node->next) {
            node->data.fade = 1;
            node->data.fade_alpha = fade;
        }
    }
}
