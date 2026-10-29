#include <gtest/gtest.h>

#include "present_timing.hpp"

namespace {
using Clock = PresentPacer::Clock;
using Action = PresentPacer::Action;
using namespace std::chrono_literals;
const auto origin = Clock::time_point{} + 1s;
} // namespace

TEST(PresentTiming, RenderTimeIsPartOfTheCapPeriod) {
    for (double rate : {60.0, 120.0, 240.0}) {
        PresentPacer pacer;
        auto         now = origin;
        const auto   period = std::chrono::duration_cast<Clock::duration>(std::chrono::duration<double>(1 / rate));
        ASSERT_TRUE(pacer.Present([&] { now += 2ms; return true; }, [&] { return now; }));
        const auto decision = pacer.Next(now, origin + 40ms, rate);
        EXPECT_EQ(decision.action, Action::Wait);
        EXPECT_EQ(decision.deadline, origin + period);
        EXPECT_EQ(pacer.Next(origin + period, origin + 40ms, rate).action, Action::Display);
    }
}

TEST(PresentTiming, SlowRenderAndStallSkipExpiredSlotsWithoutCatchUpBursts) {
    PresentPacer pacer;
    auto         now = origin;
    ASSERT_TRUE(pacer.Present([&] { now += 13ms; return true; }, [&] { return now; }));
    EXPECT_EQ(pacer.Next(now, origin + 40ms, 240).action, Action::Display);
    now += 200ms;
    const auto restarted = now;
    ASSERT_TRUE(pacer.Present([&] { now += 1ms; return true; }, [&] { return now; }));
    auto decision = pacer.Next(now, now + 40ms, 240);
    EXPECT_EQ(decision.action, Action::Wait);
    const auto period = std::chrono::duration_cast<Clock::duration>(std::chrono::duration<double>(1.0 / 240));
    EXPECT_EQ(decision.deadline, restarted + period);
}

TEST(PresentTiming, DueGameFieldWinsEvenWithoutADisplay) {
    PresentPacer pacer;
    auto         now = origin;
    ASSERT_TRUE(pacer.Present([] { return true; }, [&] { return now; }));
    EXPECT_EQ(pacer.Next(now, now + 2ms, 240).action, Action::Tick);
    EXPECT_EQ(pacer.Next(now + 20ms, now + 20ms, 0).action, Action::Tick);
    EXPECT_EQ(pacer.Next(now + 21ms, now + 20ms, 240).action, Action::Tick);
    EXPECT_EQ(pacer.Next(now + 20ms, now + 40ms, 0).action, Action::Display);
}

TEST(PresentTiming, UnlimitedAndFailedPresentDoNotAccumulateWaits) {
    PresentPacer pacer;
    auto         now = origin;
    ASSERT_TRUE(pacer.Present([&] { now += 2ms; return true; }, [&] { return now; }));
    EXPECT_EQ(pacer.Next(now, origin + 20ms, 0).action, Action::Display);
    now = origin + 10ms;
    EXPECT_FALSE(pacer.Present([] { return false; }, [&] { return now; }));
    EXPECT_EQ(pacer.Next(now, origin + 20ms, 240).action, Action::Display);
}

TEST(PresentTiming, AlphaSpansTheWholeOneOrTwoFieldGameFrame) {
    EXPECT_FLOAT_EQ(PresentFrameAlpha(0, .5, 1), .5f);
    EXPECT_FLOAT_EQ(PresentFrameAlpha(0, .5, 2), .25f);
    EXPECT_FLOAT_EQ(PresentFrameAlpha(1, .5, 2), .75f);
    EXPECT_FLOAT_EQ(PresentFrameAlpha(0, 0, 2), 0);
    EXPECT_LE(PresentFrameAlpha(3, .5, 2), 1);
}

TEST(PresentTiming, TinyCapsAreOneFpsAndZeroIsUnlimited) {
    PresentPacer pacer;
    ASSERT_TRUE(pacer.Present([] { return true; }, [] { return origin; }));
    for (double rate : {1e-12, 1e-300, 0.5, 1.0}) {
        auto decision = pacer.Next(origin + 1ms, origin + 2s, rate);
        EXPECT_EQ(decision.action, Action::Wait);
        EXPECT_EQ(decision.deadline, origin + 1s);
    }
    EXPECT_EQ(pacer.Next(origin + 1ms, origin + 2s, 0).action, Action::Display);
    const auto end = Clock::time_point::max() - 2ms;
    ASSERT_TRUE(pacer.Present([] { return true; }, [&] { return end; }));
    EXPECT_EQ(pacer.Next(end + 1ms, Clock::time_point::max(), 1e-12).action, Action::Tick);
}
