#include <gtest/gtest.h>

#include <cstddef>
#include <cstdint>
#include <cstring>
#include <memory>

#include "mg_drawenv.hpp"

namespace {

// This is the natural host placement of a draw environment after an eight-byte member.
struct alignas(16) OffsetDrawEnv {
    std::uint64_t prefix = 0;
    mgCDrawEnv    env;
};

static_assert(alignof(mgCDrawEnv) == 8);
static_assert(offsetof(OffsetDrawEnv, env) == 8);
static_assert(sizeof(mgCDrawEnv) == 64);

} // namespace

TEST(MgDrawEnvCopy, CopiesAtNaturalEightByteAlignment) {
    alignas(16) mgCDrawEnv source;
    auto                   destination = std::make_unique<OffsetDrawEnv>();
    ASSERT_EQ(reinterpret_cast<std::uintptr_t>(&source) % 16, 0u);
    ASSERT_EQ(reinterpret_cast<std::uintptr_t>(&destination->env) % 16, 8u);
    unsigned char pattern[sizeof(mgCDrawEnv)];
    for (std::size_t i = 0; i < sizeof pattern; ++i) {
        pattern[i] = static_cast<unsigned char>(i * 17 + 3);
    }
    std::memcpy(&source, pattern, sizeof pattern);
    mgCDrawEnv &result = (destination->env = source);
    EXPECT_EQ(&result, &destination->env);
    EXPECT_EQ(std::memcmp(&destination->env, pattern, sizeof pattern), 0);
    EXPECT_EQ(destination->prefix, 0u);
}

TEST(MgDrawEnvCopy, SelfAssignmentPreservesAllBytes) {
    OffsetDrawEnv value;
    unsigned char before[sizeof(mgCDrawEnv)];
    std::memcpy(before, &value.env, sizeof before);
    mgCDrawEnv &alias = value.env;
    mgCDrawEnv &result = (value.env = alias);
    EXPECT_EQ(&result, &value.env);
    EXPECT_EQ(std::memcmp(&value.env, before, sizeof before), 0);
}
