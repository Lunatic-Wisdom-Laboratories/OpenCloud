#include "common.h"

#include "mg_visual.hpp"

// The port never sends VIF1 packets; the builders that call this draw through gfx, so nothing reaches here.
void SendDMA(void *packet, int size) {
}
