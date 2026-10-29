#pragma once

#include "runscript.hpp"

// Replaces the vector callbacks in the scene interpreter's private event table.
void ReplaceEventVectorCallbacks(int (**table)(RS_STACKDATA *, int));
