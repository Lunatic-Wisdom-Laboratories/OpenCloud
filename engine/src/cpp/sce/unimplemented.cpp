#include "opencloud/port.h"

#include <cstdlib>
#include <format>

#include "opencloud/log.hpp"

[[noreturn]] void Ps2Unimplemented(const char *function, const char *file, int line) {
    oc::Fatal(std::format("{}:{}", file, line), std::format("{}: PlayStation 2 code is not implemented on PC", function),
              /*unimplemented=*/true);
    std::abort();
}
