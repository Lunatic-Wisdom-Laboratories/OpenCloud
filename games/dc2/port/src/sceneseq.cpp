#include "common.h"

#include <cmath>

#include "sceneseq.hpp"

namespace {

bool AdvanceSpline(C3DSpline &spline, float increment) {
    const int terminal = spline.key_num - 1;
    if (spline.now_key == terminal) {
        spline.now_frame = spline.key[terminal].frame + 100.0f;
        return false;
    }
    spline.now_frame += increment;
    while (spline.now_key < terminal &&
           spline.now_frame >= spline.key[spline.now_key].frame + spline.key[spline.now_key].length) {
        ++spline.now_key;
    }
    if (spline.now_key == terminal) {
        // The final key is an endpoint, with no interval to interpolate.
        for (int axis = 0; axis < 3; ++axis) {
            spline.now_pos[axis] = spline.key[terminal].d[axis];
        }
        return true;
    }

    const auto &key = spline.key[spline.now_key];
    const float time = (spline.now_frame - key.frame) / key.length;
    const float square = time * time;
    const float cube = square * time;
    for (int axis = 0; axis < 3; ++axis) {
        spline.now_pos[axis] = cube * key.a[axis] + square * key.b[axis] + time * key.c[axis] + key.d[axis];
    }
    return true;
}

} // namespace

PORT_OVERRIDE int C3DSpline::Step() {
    if (key_num < 2) {
        return 1;
    }
    return AdvanceSpline(*this, 1.0f) ? 0 : 1;
}

PORT_OVERRIDE int C3DSpline::StepS() {
    if (key_num < 2 || speed <= 0.0f) {
        return 1;
    }
    const float start[3] = {now_pos[0], now_pos[1], now_pos[2]};
    for (;;) {
        if (!AdvanceSpline(*this, 0.001f)) {
            return 1;
        }
        const float dx = now_pos[0] - start[0];
        const float dy = now_pos[1] - start[1];
        const float dz = now_pos[2] - start[2];
        if (std::sqrt(dx * dx + dy * dy + dz * dz) >= speed) {
            return 0;
        }
    }
}
