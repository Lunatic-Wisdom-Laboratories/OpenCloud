#include "common.h"

#include "dng_object.hpp"
#include "mg_texture.hpp"
#include "mglib.hpp"

PORT_OVERRIDE void CRocketLauncherMan::Initialize(mgCFrame *model, int texture_id, mgCTexture *texture) {
    for (auto &shot : rocket) {
        shot.Initialize();
        shot.model = model;
        shot.tex_block = texture_id;
        shot.trail_texture = texture;
    }
}

PORT_OVERRIDE void CRocketLauncherMan::Draw() {
    for (auto &shot : rocket) {
        mgTexManager.ReloadTexture(shot.tex_block, static_cast<sceVif1Packet *>(nullptr));
        shot.Draw();
    }
}

PORT_OVERRIDE void CLaserGunMan::Initialize(mgCFrame *model, int texture_id, mgCTexture *texture) {
    for (auto &shot : laser) {
        shot.Initialize();
        shot.model = model;
        shot.tex_block = texture_id;
        shot.trail_texture = texture;
    }
}

PORT_OVERRIDE void CLaserGunMan::Draw() {
    for (auto &shot : laser) {
        mgTexManager.ReloadTexture(shot.tex_block, static_cast<sceVif1Packet *>(nullptr));
        shot.Draw();
    }
}
