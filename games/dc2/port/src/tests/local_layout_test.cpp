#include <gtest/gtest.h>

#include <cstddef>

#include "gamedata.hpp"
#include "local_layout_probes.hpp"
#include "outline.hpp"

TEST(LocalLayout, ItemCommandTargetFitsTheTypedWrite) {
    EXPECT_GE(LocalLayoutProbe::TargetSize(), sizeof(CItemUseTarget));
}

TEST(LocalLayout, LodOutlineCopyMatchesEveryAccessedMember) {
    using LocalLayoutProbe::OutlineCopy;
    EXPECT_EQ(sizeof(OutlineCopy), sizeof(COutLineDraw));
    EXPECT_EQ(offsetof(OutlineCopy, next), offsetof(COutLineDraw, next));
    EXPECT_EQ(offsetof(OutlineCopy, box), offsetof(COutLineDraw, unk_10));
    EXPECT_EQ(offsetof(OutlineCopy, texture), offsetof(COutLineDraw, texture));
    EXPECT_EQ(offsetof(OutlineCopy, frame), offsetof(COutLineDraw, frame));
    EXPECT_EQ(offsetof(OutlineCopy, width), offsetof(COutLineDraw, width));
    EXPECT_EQ(offsetof(OutlineCopy, depth_from_pos), offsetof(COutLineDraw, depth_from_pos));
    EXPECT_EQ(offsetof(OutlineCopy, pos), offsetof(COutLineDraw, pos));
    EXPECT_EQ(offsetof(OutlineCopy, color), offsetof(COutLineDraw, color));
    EXPECT_EQ(offsetof(OutlineCopy, enable), offsetof(COutLineDraw, enable));
    EXPECT_EQ(offsetof(OutlineCopy, hide_edge), offsetof(COutLineDraw, hide_edge));
}
