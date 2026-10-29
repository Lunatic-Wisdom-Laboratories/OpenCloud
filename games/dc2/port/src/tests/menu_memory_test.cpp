#include <gtest/gtest.h>

#include <cstdint>

#include "menuchr.hpp"
#include "platform/arena.hpp"

TEST(MenuMemory, TailEndsAtParentBoundaryAfterAlignment) {
    constexpr int capacity = 0x20000;
    static char   key;
    auto         *base = reinterpret_cast<u_long128 *>(ArenaBlock(&key, (capacity + 4) * sizeof(u_long128)));
    for (int character : {USER_CHARA_MAX, USER_CHARA_MONICA, USER_CHARA_ROBO, USER_CHARA_MONSTER}) {
        for (int offset = 0; offset < 4; ++offset) {
            for (int used = 0; used < 4; ++used) {
                SCOPED_TRACE(testing::Message() << character << ":" << offset << ":" << used);
                mgCMemory pool;
                mgCMemory tail;
                mgCMemory buffers[MENU_CHARA_LOAD_MAX];
                pool.stSetBuffer(base + offset, capacity);
                pool.stack_used = used;
                MenuMemoryAdjust(&pool, &tail, buffers, character);
                EXPECT_EQ(tail.stack + tail.stack_size, pool.stack + pool.stack_size);
                EXPECT_EQ(tail.stack_used, 0);
                EXPECT_EQ(tail.lock, 0);
                EXPECT_EQ(reinterpret_cast<std::uintptr_t>(buffers[0].stack) % 64, 0u);
                EXPECT_GE(tail.stack, pool.stack + used);
                EXPECT_LE(tail.stack, pool.stack + pool.stack_size);
            }
        }
    }
}
