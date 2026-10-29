#pragma once

#include "gamepad_port.hpp"
static int read_pad(PAD_STATUS *status, int port, int slot);

// Keep the retail connection/setup state machine in the native frame update.
int PortReadPad(PAD_STATUS *status, int port, int slot) {
    return read_pad(status, port, slot);
}
