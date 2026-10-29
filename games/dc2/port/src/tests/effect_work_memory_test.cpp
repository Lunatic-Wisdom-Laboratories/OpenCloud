#include <gtest/gtest.h>

#include <cstring>

#include "character.hpp"
#include "effscript.hpp"
#include "mg_memory.hpp"
#include "platform/arena.hpp"

namespace {
class EffectWorkMemory : public testing::Test {
protected:
    void SetUp() override {
        storage = reinterpret_cast<u_long128 *>(ArenaBlock(this, 4096 * sizeof(u_long128)));
        memory.SetHeapMem(storage, 4096);
        character.Initialize();
        std::memcpy(program.magic, "SB2", 3);
        base.base_no = 7;
        base.texb = 10;
        base.chara = &character;
        base.script = reinterpret_cast<char *>(&program);
        base.work_size = 0x7D + 0x24;
        manager.base[0] = &base;
        manager.base_num = 1;
        manager.SetWorkBuffer(&memory);
    }

    u_long128 *Fragment(int size = 164) {
        u_long128 *first = memory.StartStackMode(MG_STACK_MODE_FIT, size);
        EXPECT_NE(first, nullptr);
        memory.Alloc(size);
        memory.EndStackMode();
        u_long128 *barrier = memory.StartStackMode(MG_STACK_MODE_FIT, 4);
        EXPECT_NE(barrier, nullptr);
        memory.Alloc(4);
        memory.EndStackMode();
        std::memset(barrier, 0xA5, 4 * sizeof(u_long128));
        memory.Free(first);
        return barrier;
    }

    u_long128       *storage = nullptr;
    mgCMemory        memory;
    CCharacter2      character;
    RS_PROG_HEADER   program{};
    EFF_SPT_BASE     base{};
    CEffectScriptMan manager;
};
} // namespace

TEST_F(EffectWorkMemory, SkipsAGapThatOnlyFitsTheRetailObjectBudget) {
    u_long128   *barrier = Fragment();
    _EFF_SCRIPT *effect = manager.CreateEffSpt(7, 0, 1);
    ASSERT_NE(effect, nullptr);
    EXPECT_GT(effect->work, barrier);
    EXPECT_EQ(effect->run.stack_num, 64);
    EXPECT_EQ(effect->run.call_num, 2);
    EXPECT_EQ(effect->slot, 0);
    EXPECT_EQ(manager.slot[0][0], effect);
    const auto *guard = reinterpret_cast<const unsigned char *>(barrier);
    for (int i = 0; i < 4 * sizeof(u_long128); ++i) {
        EXPECT_EQ(guard[i], 0xA5);
    }
}

TEST_F(EffectWorkMemory, RepeatedEffectsKeepTextureOrderAndOwnerSlots) {
    EFF_SPT_BASE second = base;
    EFF_SPT_BASE third = base;
    second.base_no = 8;
    second.texb = 3;
    third.base_no = 9;
    third.texb = 8;
    manager.base[1] = &second;
    manager.base[2] = &third;
    manager.base_num = 3;
    _EFF_SCRIPT *a = manager.CreateEffSpt(7, 0, 1);
    _EFF_SCRIPT *b = manager.CreateEffSpt(8, 0, 1);
    _EFF_SCRIPT *c = manager.CreateEffSpt(9, 0, 1);
    ASSERT_NE(a, nullptr);
    ASSERT_NE(b, nullptr);
    ASSERT_NE(c, nullptr);
    EXPECT_EQ(manager.head, b);
    EXPECT_EQ(b->next, c);
    EXPECT_EQ(c->prev, b);
    EXPECT_EQ(c->next, a);
    EXPECT_EQ(a->prev, c);
    EXPECT_EQ(manager.tail, a);
    EXPECT_EQ(manager.now, c);
    EXPECT_EQ(manager.slot[0][0], a);
    EXPECT_EQ(manager.slot[0][1], b);
    EXPECT_EQ(manager.slot[0][2], c);
    manager.ClearEffectFromChrid(0);
    EXPECT_EQ(manager.head, nullptr);
    EXPECT_EQ(manager.tail, nullptr);
    EXPECT_EQ(manager.slot[0][0], nullptr);
    EXPECT_EQ(manager.slot[0][1], nullptr);
    EXPECT_EQ(manager.slot[0][2], nullptr);
}

TEST_F(EffectWorkMemory, SecondaryCharactersSkipTheUndersizedFirstGap) {
    u_long128  *barrier = Fragment(128);
    _EFF_SCRIPT script{};
    script.chara = &character;
    ASSERT_TRUE(manager.AssignCharacter(&script, 1));
    EXPECT_GT(script.sub_chara_work, barrier);
    ASSERT_NE(script.sub_chara[0], nullptr);
    const auto *guard = reinterpret_cast<const unsigned char *>(barrier);
    for (int i = 0; i < 4 * sizeof(u_long128); ++i) {
        EXPECT_EQ(guard[i], 0xA5);
    }
}

TEST_F(EffectWorkMemory, SecondaryCharactersReserveAllCopies) {
    // This gap fits all retail object budgets but not all native copies.
    u_long128  *barrier = Fragment(EFF_SPT_SUB_CHARA_MAX * 0x68 + 4);
    _EFF_SCRIPT script{};
    script.chara = &character;
    ASSERT_TRUE(manager.AssignCharacter(&script, EFF_SPT_SUB_CHARA_MAX));
    EXPECT_GT(script.sub_chara_work, barrier);
    for (int i = 0; i < EFF_SPT_SUB_CHARA_MAX; ++i) {
        ASSERT_NE(script.sub_chara[i], nullptr);
        if (i != 0) {
            EXPECT_GE(reinterpret_cast<char *>(script.sub_chara[i]) -
                          reinterpret_cast<char *>(script.sub_chara[i - 1]),
                      static_cast<std::ptrdiff_t>(sizeof(CCharacter2)));
        }
    }
}

TEST_F(EffectWorkMemory, SecondaryCharacterExhaustionReportsTheNativeBudget) {
    ASSERT_NE(memory.StartStackMode(MG_STACK_MODE_FIT, 1), nullptr);
    ASSERT_NE(memory.Alloc(memory.stGetSize() - 1), nullptr);
    memory.EndStackMode();
    _EFF_SCRIPT script{};
    script.chara = &character;
    const int required = (sizeof(CCharacter2) + 15) / 16 + character.GetCopySize() + 4;
    testing::internal::CaptureStdout();
    EXPECT_FALSE(manager.AssignCharacter(&script, 1));
    const auto output = testing::internal::GetCapturedStdout();
    EXPECT_NE(output.find("es work max!! (assign character[" + std::to_string(required) + "])"), std::string::npos);
}
