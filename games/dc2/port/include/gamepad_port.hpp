#pragma once
struct PAD_STATUS;

// Calls the retained retail connection/setup state machine.
int PortReadPad(PAD_STATUS *status, int port, int slot);
