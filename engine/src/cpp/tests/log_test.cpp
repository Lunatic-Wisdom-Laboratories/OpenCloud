#include <gtest/gtest.h>

#include <cstdio>
#include <cstdlib>
#include <string>
#include <string_view>
#include <vector>

#include "opencloud/log.hpp"

namespace {

struct Line {
    oc::LogLevel level;
    std::string  subsystem;
    std::string  message;
};

std::vector<Line> g_lines;

void RecordLine(oc::LogLevel level, std::string_view subsystem, std::string_view message) {
    g_lines.push_back({level, std::string(subsystem), std::string(message)});
}

// Reports the hook's arguments on stderr, where a death test reads them, and returns.
void PrintingHook(std::string_view subsystem, std::string_view message, bool unimplemented) {
    std::fprintf(stderr, "hook: %.*s; %.*s; %s\n", static_cast<int>(subsystem.size()), subsystem.data(),
                 static_cast<int>(message.size()), message.data(), unimplemented ? "unimplemented" : "fatal");
}

// Fails fatally again from inside the hook: the inner Fatal must return without re-entering it.
void ReenteringHook(std::string_view, std::string_view, bool) {
    static int entries = 0;
    entries++;
    if (entries > 1) {
        std::_Exit(4);
    }
    oc::Fatal("again", "inside the hook", /*unimplemented=*/false);
    std::_Exit(3);
}

} // namespace

TEST(OpenCloudLog, SinkReceivesEachLine) {
    g_lines.clear();
    oc::LogSink previous = oc::SetLogSink(RecordLine);
    oc::Log(oc::LogLevel::Warning, "audio", "no playback device");
    oc::Logf(oc::LogLevel::Info, "gfx", "{} pipelines ready in {:.2f} s", 12, 0.5);
    EXPECT_EQ(oc::SetLogSink(previous), RecordLine);

    ASSERT_EQ(g_lines.size(), 2U);
    EXPECT_EQ(g_lines[0].level, oc::LogLevel::Warning);
    EXPECT_EQ(g_lines[0].subsystem, "audio");
    EXPECT_EQ(g_lines[0].message, "no playback device");
    EXPECT_EQ(g_lines[1].level, oc::LogLevel::Info);
    EXPECT_EQ(g_lines[1].subsystem, "gfx");
    EXPECT_EQ(g_lines[1].message, "12 pipelines ready in 0.50 s");
}

TEST(OpenCloudLog, WithoutASinkLinesGoToStderr) {
    oc::LogSink previous = oc::SetLogSink(nullptr);
    testing::internal::CaptureStderr();
    oc::Log(oc::LogLevel::Error, "font", "cannot read a.ttf");
    std::string written = testing::internal::GetCapturedStderr();
    oc::SetLogSink(previous);
    EXPECT_EQ(written, "font: cannot read a.ttf\n");
}

TEST(OpenCloudLogDeathTest, AbortCallsTheHookThenAborts) {
    EXPECT_DEATH(
        {
            oc::SetLogSink(nullptr);
            oc::SetFatalHook(PrintingHook);
            oc::Abort("arena Test", "allocation overflow, 2 of 1 quadwords");
        },
        "hook: arena Test; allocation overflow, 2 of 1 quadwords; fatal");
}

TEST(OpenCloudLogDeathTest, FatalMarksUnimplementedCode) {
    EXPECT_DEATH(
        {
            oc::SetLogSink(nullptr);
            oc::SetFatalHook(PrintingHook);
            oc::Fatal("a.cpp:3", "Func: PlayStation 2 code is not implemented on PC", /*unimplemented=*/true);
            std::abort();
        },
        "hook: a.cpp:3; Func: PlayStation 2 code is not implemented on PC; unimplemented");
}

TEST(OpenCloudLogDeathTest, AFatalInsideTheHookDoesNotReenterIt) {
    EXPECT_EXIT(
        {
            oc::SetLogSink(nullptr);
            oc::SetFatalHook(ReenteringHook);
            oc::Abort("first", "failure");
        },
        testing::ExitedWithCode(3), "again: inside the hook");
}
