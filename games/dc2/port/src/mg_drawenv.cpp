#include "common.h"

#include <cstring>

#include "mg_drawenv.hpp"

PORT_OVERRIDE mgCDrawEnv &mgCDrawEnv::operator=(mgCDrawEnv &source) {
    // Host objects need only eight-byte alignment. Copy the same four quadwords without
    // promising the compiler the sixteen-byte alignment of a u_long128 pointer.
    static_assert(sizeof(mgCDrawEnv) == 64);
    if (this != &source) {
        std::memcpy(this, &source, sizeof(*this));
    }
    return *this;
}
