#include "common.h"

#include <cstddef>
#include <cstring>

#include "funcpoint.hpp"
#include "mg_math.hpp"
#include "mg_memory.hpp"

namespace {
// The anonymous settings union ends where the following frame begins. Its
// pointer-bearing alternatives grow on the host; data[] is only the retail view.
constexpr std::size_t SettingsSize = offsetof(CFuncPoint, frame) - offsetof(CFuncPoint, data);
static_assert(SettingsSize >= sizeof(CFuncPoint::AnimeData));
} // namespace

PORT_OVERRIDE void CFuncPoint::Initialize() {
    name = nullptr;
    type = FUNC_POINT_NONE;
    unk_8 = flag_no = 0;
    enable = 1;
    start = end = 0.0f;
    std::memset(data, 0, SettingsSize);
    mgZeroVector(position);
    mgZeroVector(rotation);
    scale[0] = scale[1] = scale[2] = 1.0f;
    scale[3] = 0.0f;
}

PORT_OVERRIDE int CFuncPointMngr::Copy(CFuncPointMngr &dest, mgCMemory *stack) {
    dest.Initialize();
    for (int kind = 0; kind < FUNC_POINT_TYPE_NUM; ++kind) {
        for (auto *node = list[kind]; node != nullptr; node = node->next) {
            auto *copy = dest.Add(kind, stack);
            if (copy == nullptr) {
                break;
            }
            auto &source = node->data;
            copy->name = source.name;
            copy->type = source.type;
            copy->unk_8 = source.unk_8;
            copy->flag_no = source.flag_no;
            copy->enable = source.enable;
            copy->start = source.start;
            copy->end = source.end;
            std::memcpy(copy->data, source.data, SettingsSize);
            copy->frame = source.frame;
            sceVu0CopyVector(copy->position, source.position);
            sceVu0CopyVector(copy->rotation, source.rotation);
            sceVu0CopyVector(copy->scale, source.scale);
            copy->active = source.active;
        }
    }
    return 1;
}
