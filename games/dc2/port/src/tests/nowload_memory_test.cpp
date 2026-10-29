#include <gtest/gtest.h>

#include "dataread.hpp"
#include "nowload.hpp"
#include "nowload_port.hpp"

TEST(NowLoadingMemory, CopiesTheSourceStackStateEvenWhenTheImageIsMissing) {
    NowLoadingInfo        info;
    alignas(64) u_long128 storage[1024]{};
    info.memory.stSetBuffer(storage, 1024);
    info.memory.Alloc(7);
    info.step_count = 5;
    PortLoadInfo.memory.stack = nullptr;
    PortLoadInfo.memory.stack_used = 0;
    PortLoadInfo.memory.stack_size = 0;
    // Keep the image unavailable even when the suite has a real data root.
    char previous[256];
    GetCurrentDir(previous);
    char missing[] = "__missing_loading_memory_test__/";
    SetCurrentDir(missing);
    CreateNowLoading(&info);
    SetCurrentDir(previous);
    EXPECT_EQ(PortLoadInfo.memory.stack, info.memory.stack);
    EXPECT_EQ(PortLoadInfo.memory.stack_used, info.memory.stack_used);
    EXPECT_EQ(PortLoadInfo.memory.stack_size, info.memory.stack_size);
    EXPECT_EQ(PortLoadInfo.memory.stack_block, info.memory.stack_block);
    EXPECT_EQ(PortLoadInfo.memory.stGetTop(), info.memory.stGetTop());
}
