#include "common.h"

#include "charasetup.hpp"
#include "mg_memory.hpp"

// The seventh element is retail's BuffTempData, which SetupMainUnit reaches as stacks[6] by link adjacency on the PS2.
PORT_OVERRIDE mgCMemory BaseCharacter[CHARA_MEM_STACK_NUM];
