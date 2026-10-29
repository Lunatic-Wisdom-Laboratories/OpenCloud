#include "common.h"

#include <gtest/gtest.h>

#include <array>
#include <cmath>
#include <cstddef>
#include <cstdlib>
#include <cstring>
#include <initializer_list>
#include <new>

#include "character_motion.hpp"
#include "gameutil.hpp"
#include "mg_frame.hpp"
#include "mg_memory.hpp"
#include "mg_memory_port.hpp"
#include "mg_visual.hpp"
#include "platform/arena.hpp"

namespace {

/**
 * One effect frame with a material, under a root whose frame list the motion keys index.
 */
struct EffectModel {
    mgCFrame                  root;
    mgCFrame                  effect;
    std::array<mgCFrame *, 2> frames{&root, &effect};
    mgCFrameAttr              attr;
    mgMaterial                material{};
    mgCVisualMDT              visual;

    EffectModel() {
        root.frame_list = frames.data();
        root.frame_num = 1;
        material.diffuse[0] = material.diffuse[1] = material.diffuse[2] = material.diffuse[3] = 1.0f;
        visual.material = &material;
        visual.material_num = 1;
        effect.SetVisual(&visual);
        effect.attr = &attr;
    }
};

/**
 * Key list of one channel driving frame 1's first material, on game frames 0, 1, 2...
 */
struct Channel {
    sceVu0FVECTOR values[8]{};
    u32           frames[8]{};
    Mot_List      list{};

    Channel(int type, std::initializer_list<float> keys) {
        int index = 0;
        for (const float key : keys) {
            values[index][0] = values[index][1] = values[index][2] = key;
            values[index][3] = 0.0f;
            frames[index] = index;
            ++index;
        }
        list.frame = 1;
        list.target = 0;
        list.type = type;
        list.key_count = static_cast<u32>(keys.size());
        list.values = values;
        list.key_frames = frames;
    }
};

// Data that reused a released arena; read as a channel pointer it faults, as 0x8100000081 did in the crash report.
constexpr unsigned char kReused = 0x81;

constexpr int         kQuadwords = 256;
constexpr std::size_t kBytes = static_cast<std::size_t>(kQuadwords) * 16;
constexpr int         kListQuadwords = static_cast<int>((sizeof(Mot_List) + 15) / 16);

/**
 * One motion set laid out as its arena holds it: the anchor, then the channel list of its only action.
 */
struct MotionSet {
    Mot_List       anchor{};
    Mot_List       channel{};
    tagMOTION_TYPE motion{};

    explicit MotionSet(bool with_channel) {
        motion.motion_list = &anchor;
        character_motion::Action idle{.name = "idle", .duration = 10, .base = 0, .head = with_channel ? &channel : nullptr};
        character_motion::Register(&anchor, {idle}, {});
    }
};

/**
 * One motion set allocated from an arena as BuildMotion allocates it: the channel list of its only action,
 * then the anchor at the end of the run.
 */
struct ArenaMotionSet {
    const std::byte *storage;  /**< Start of the allocation run. */
    Mot_List        *channel;  /**< Channels of the only action. */
    Mot_List        *anchor;   /**< Registry anchor following the channels. */
    tagMOTION_TYPE   motion{}; /**< Motion descriptor used by playback. */

    explicit ArenaMotionSet(mgCMemory &arena)
        : storage(reinterpret_cast<const std::byte *>(arena.stGetTop())),
          channel(::new (arena.Alloc(kListQuadwords)) Mot_List{}),
          anchor(::new (arena.Alloc(kListQuadwords)) Mot_List{}) {
        anchor->frame = -1;
        motion.motion_list = anchor;
        character_motion::Action idle{.name = "idle", .duration = 10, .base = 0, .head = channel};
        character_motion::Register(anchor, {idle}, {storage, reinterpret_cast<const std::byte *>(arena.stGetTop())});
    }
};

u_long128 *ArenaBuffer() {
    static char key;
    auto       *buffer = reinterpret_cast<u_long128 *>(ArenaBlock(&key, kBytes));
    std::memset(buffer, 0, kBytes);
    return buffer;
}

class CharacterMotion : public ::testing::Test {
protected:
    void TearDown() override { character_motion::Clear(); }
};

// A sliced action's material channels end on its last frame, where the game's interpolation has no later key;
// an effect that fades out must keep its last key there rather than divide zero by zero into NaN.
TEST_F(CharacterMotion, MaterialAlphaHoldsItsLastKeyAtAndPastTheActionEnd) {
    EffectModel    model;
    Channel        transparency(MOTION_KEY_MATERIAL_ALPHA, {0.0f, 0.5f, 0.9f});
    tagMOTION_TYPE motion{};
    motion.motion_list = &transparency.list;
    character_motion::Register(motion.motion_list, {
                                                       {.name = "get_item", .duration = 2.0f, .base = 0, .head = &transparency.list}
    },
                               {});

    character_motion::Pose(model.root, motion, 1.5f, nullptr);
    EXPECT_NEAR(model.material.diffuse[3], 1.0f - 0.7f, 1e-5f);

    for (const float frame : {2.0f, 3.0f, 10.0f}) {
        model.material.diffuse[3] = 1.0f;
        character_motion::Pose(model.root, motion, frame, nullptr);
        ASSERT_FALSE(std::isnan(model.material.diffuse[3])) << "frame " << frame;
        EXPECT_NEAR(model.material.diffuse[3], 1.0f - 0.9f, 1e-5f) << "frame " << frame;
    }
}

TEST_F(CharacterMotion, MaterialColourHoldsItsLastKeyAtTheActionEnd) {
    EffectModel    model;
    Channel        colour(MOTION_KEY_MATERIAL_COLOR, {1.0f, 0.25f});
    tagMOTION_TYPE motion{};
    motion.motion_list = &colour.list;
    character_motion::Register(motion.motion_list, {
                                                       {.name = "flash", .duration = 1.0f, .base = 0, .head = &colour.list}
    },
                               {});

    character_motion::Pose(model.root, motion, 1.0f, nullptr);
    for (int component = 0; component < 3; ++component) {
        EXPECT_NEAR(model.material.diffuse[component], 0.25f, 1e-5f);
    }
}

// The blend into a different action poses the target's auxiliary channels unblended, through the same end hold.
TEST_F(CharacterMotion, BlendIntoAnActionEndKeepsItsMaterialAlpha) {
    EffectModel    model;
    Channel        first(MOTION_KEY_MATERIAL_ALPHA, {0.0f, 0.0f});
    Channel        second(MOTION_KEY_MATERIAL_ALPHA, {0.2f, 0.8f});
    tagMOTION_TYPE motion{};
    motion.motion_list = &first.list;
    character_motion::Register(motion.motion_list, {
                                                       {.name = "start", .duration = 1.0f, .base = 0, .head = &first.list },
                                                       {.name = "end",   .duration = 1.0f, .base = 3, .head = &second.list},
    },
                               {});

    character_motion::Pose(model.root, motion, 0.0f, nullptr);
    character_motion::Blend(model.root, motion, 0.0f, 4.0f, 0.5f, nullptr);
    ASSERT_FALSE(std::isnan(model.material.diffuse[3]));
    EXPECT_NEAR(model.material.diffuse[3], 1.0f - 0.8f, 1e-5f);
}

TEST_F(CharacterMotion, ReleasedMotionSetStopsResolving) {
    MotionSet set(true);
    EXPECT_EQ(character_motion::Actions(set.motion).size(), 1u);
    const Mot_List anchor = set.anchor;
    std::memset(&set.anchor, kReused, sizeof(set.anchor));
    EXPECT_TRUE(character_motion::Actions(set.motion).empty());
    // The rejected set is erased, not just hidden: restoring the stamp does not bring it back.
    set.anchor = anchor;
    EXPECT_TRUE(character_motion::Actions(set.motion).empty());
}

// Memory the arena hands out after the set's run belongs to someone else; the set stays registered.
TEST_F(CharacterMotion, AllocationPastMotionStorageKeepsSet) {
    mgCMemory arena;
    arena.stSetBuffer(ArenaBuffer(), kQuadwords);
    ArenaMotionSet set(arena);
    std::memset(arena.Alloc(kListQuadwords), kReused, sizeof(Mot_List));
    EXPECT_EQ(character_motion::Actions(set.motion).size(), 1u);
}

TEST_F(CharacterMotion, EmptyOverwriteInsideMotionStorageKeepsSet) {
    mgCMemory arena;
    arena.stSetBuffer(ArenaBuffer(), kQuadwords);
    ArenaMotionSet set(arena);
    const auto    *inside = reinterpret_cast<const std::byte *>(set.channel) + 1;
    character_motion::ForgetStorage(inside, inside);
    EXPECT_EQ(character_motion::Actions(set.motion).size(), 1u);
}

// A stage reset rewinds the arena and hands its front out again, over the channels; the anchor at the end of the
// run keeps its bytes, stamp included, but the set must stop resolving.
TEST_F(CharacterMotion, ResetArenaReusingChannelsReleasesSet) {
    mgCMemory arena;
    arena.stSetBuffer(ArenaBuffer(), kQuadwords);
    ArenaMotionSet                          set(arena);
    std::array<std::byte, sizeof(Mot_List)> anchor{};
    std::memcpy(anchor.data(), set.anchor, anchor.size());
    arena.stReset();
    std::memset(arena.Alloc(kListQuadwords), kReused, sizeof(Mot_List));
    ASSERT_EQ(std::memcmp(reinterpret_cast<const std::byte *>(set.anchor), anchor.data(), anchor.size()), 0);
    EXPECT_TRUE(character_motion::Actions(set.motion).empty());
}

// A heap block freed and claimed again by the next stack mode releases the sets it held.
TEST_F(CharacterMotion, FreedHeapBlockReusedReleasesSet) {
    mgCMemory arena;
    arena.SetHeapMem(ArenaBuffer(), kQuadwords);
    u_long128 *block = arena.StartStackMode(MG_STACK_MODE_FIRST, 0);
    ASSERT_NE(block, nullptr);
    ArenaMotionSet set(arena);
    arena.EndStackMode();
    arena.Free(block);
    ASSERT_EQ(arena.StartStackMode(MG_STACK_MODE_FIRST, 0), block);
    std::memset(arena.Alloc(kListQuadwords), kReused, sizeof(Mot_List));
    EXPECT_TRUE(character_motion::Actions(set.motion).empty());
    arena.EndStackMode();
}

// A town map change rewinds the character buffer and reserves the controlled character's memory again while the
// character stays loaded there; its motion set must keep resolving.
TEST_F(CharacterMotion, KeptReservationAfterRewindKeepsSet) {
    mgCMemory arena;
    arena.stSetBuffer(ArenaBuffer(), kQuadwords);
    ArenaMotionSet set(arena);
    const int      used = arena.stGetUsed();
    arena.stack_used = 0;
    ASSERT_NE(MemoryAllocKept(arena, used), nullptr);
    EXPECT_EQ(character_motion::Actions(set.motion).size(), 1u);
    // The set is still tracked, so handing the memory out for new data releases it.
    arena.stReset();
    std::memset(arena.Alloc(kListQuadwords), kReused, sizeof(Mot_List));
    EXPECT_TRUE(character_motion::Actions(set.motion).empty());
}

// A heap set up over a buffer outside the arenas imports it into an arena root, and binding the same buffer again
// maps the same root and copies the buffer over it. The copy covers the buffer's own size, not the root's headroom,
// so it overwrites channels inside that size while an anchor in the headroom keeps its bytes.
TEST_F(CharacterMotion, RebindingHeapBufferReleasesSetInsideTheImport) {
    alignas(16) static u_long128 external[kQuadwords];
    std::memset(external, kReused, sizeof(external));
    mgCMemory arena;
    arena.SetHeapMem(external, kQuadwords);
    u_long128 *block = arena.StartStackMode(MG_STACK_MODE_FIRST, 0);
    ASSERT_NE(block, nullptr);
    const auto *storage = reinterpret_cast<const std::byte *>(arena.stGetTop());
    auto       *channel = ::new (arena.Alloc(kListQuadwords)) Mot_List{};
    arena.Alloc(kQuadwords);
    auto *anchor = ::new (arena.Alloc(kListQuadwords)) Mot_List{};
    ASSERT_GE(reinterpret_cast<u_long128 *>(anchor) - block, kQuadwords);
    tagMOTION_TYPE motion{};
    motion.motion_list = anchor;
    character_motion::Action idle{.name = "idle", .duration = 10, .base = 0, .head = channel};
    character_motion::Register(anchor, {idle}, {storage, reinterpret_cast<const std::byte *>(arena.stGetTop())});
    arena.EndStackMode();
    std::array<std::byte, sizeof(Mot_List)> kept{};
    std::memcpy(kept.data(), anchor, kept.size());

    arena.SetHeapMem(external, kQuadwords);
    ASSERT_EQ(std::memcmp(reinterpret_cast<const std::byte *>(anchor), kept.data(), kept.size()), 0);
    ASSERT_EQ(reinterpret_cast<const unsigned char *>(channel)[0], kReused);
    EXPECT_TRUE(character_motion::Actions(motion).empty());
}

// An effect script copies its character's frame tree into work memory that the next script reuses; the new
// tree must not blend from the action the previous tree at that address posed, whose channels are gone.
TEST(CharacterMotionDeathTest, BlendIgnoresPoseOfFrameTreePreviouslyAtSameAddress) {
    EXPECT_EXIT(([] {
                    character_motion::Clear();
                    alignas(mgCFrame) unsigned char storage[sizeof(mgCFrame)];
                    auto                           *root = new (storage) mgCFrame;
                    MotionSet                       previous(true);
                    character_motion::Pose(*root, previous.motion, 5, nullptr);
                    // The arena reset reuses the channel; the anchor keeps its stamp, so only the new tree's
                    // construction can drop the old pose.
                    std::memset(&previous.channel, kReused, sizeof(previous.channel));
                    root = new (storage) mgCFrame;
                    MotionSet next(false);
                    character_motion::Blend(*root, next.motion, 5, 5, .5f, nullptr);
                    std::exit(0);
                }()),
                testing::ExitedWithCode(0), "");
}

// A frame tree that outlives the motion set it last posed, released by an arena reset without Forget, must
// not blend from that set's channels.
TEST(CharacterMotionDeathTest, BlendIgnoresPoseFromReleasedMotionSet) {
    EXPECT_EXIT(([] {
                    character_motion::Clear();
                    mgCFrame  root;
                    MotionSet previous(true);
                    character_motion::Pose(root, previous.motion, 5, nullptr);
                    std::memset(&previous.anchor, kReused, sizeof(previous.anchor));
                    std::memset(&previous.channel, kReused, sizeof(previous.channel));
                    MotionSet next(false);
                    character_motion::Blend(root, next.motion, 5, 5, .5f, nullptr);
                    std::exit(0);
                }()),
                testing::ExitedWithCode(0), "");
}

// The frame tree survives, so nothing drops its recorded pose; the arena reset that handed the front of the motion
// run out again overwrote the channels but left the anchor, stamp included, intact.
TEST(CharacterMotionDeathTest, SurvivingFrameIgnoresPoseFromResetArenaWithIntactAnchor) {
    EXPECT_EXIT(([] {
                    character_motion::Clear();
                    mgCMemory arena;
                    arena.stSetBuffer(ArenaBuffer(), kQuadwords);
                    mgCFrame       root;
                    ArenaMotionSet previous(arena);
                    character_motion::Pose(root, previous.motion, 5, nullptr);
                    arena.stReset();
                    std::memset(arena.Alloc(kListQuadwords), kReused, sizeof(Mot_List));
                    MotionSet next(false);
                    character_motion::Blend(root, next.motion, 5, 5, .5f, nullptr);
                    std::exit(0);
                }()),
                testing::ExitedWithCode(0), "");
}

} // namespace
