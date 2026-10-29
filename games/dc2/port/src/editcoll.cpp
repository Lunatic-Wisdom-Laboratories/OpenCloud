#include "common.h"

#include "editcoll.hpp"
#include "vu0_lanes.hpp"

// Retail's status register keeps a sticky sign bit that the two XZ separations set; overlap means no
// separation came out negative.
int ClipBoxXZ(float *max_a, float *min_a, float *max_b, float *min_b) {
    const float separation[4] = {
        max_a[0] - min_b[0],
        max_a[2] - min_b[2],
        max_b[0] - min_a[0],
        max_b[2] - min_a[2],
    };

    for (float lane : separation) {
        if (vu0::SignSet(lane)) {
            return 0;
        }
    }

    return 1;
}
