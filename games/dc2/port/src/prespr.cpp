#include "common.h"

#include "prespr.hpp"

// Direct is the GS seam on PC; retail's raw quadword write through `write` has no buffer to land in.
PORT_OVERRIDE void CPreSprite::SetScirror(int x, int y, int w, int h) {
    const unsigned long long data = static_cast<unsigned long long>(x) | (static_cast<unsigned long long>(x + w) << 16) |
                                    (static_cast<unsigned long long>(y) << 32) |
                                    (static_cast<unsigned long long>(y + h) << 48);
    Direct(SCE_GS_SCISSOR_1, data);
}

PORT_OVERRIDE void CPreSprite::SetAlphaBlend(int mode) {
    switch (mode) {
        case MG_ALPHA_BLEND_NORMAL:
            Direct(SCE_GS_ALPHA_1, 0x44);
            break;
        case MG_ALPHA_BLEND_ADD:
            Direct(SCE_GS_ALPHA_1, 0x48);
            break;
        case MG_ALPHA_BLEND_SUB:
            Direct(SCE_GS_ALPHA_1, 0x42);
            break;
        case MG_ALPHA_BLEND_NONE:
            Direct(SCE_GS_ALPHA_1, 0x2AULL | (0x80ULL << 32));
            break;
    }
}
