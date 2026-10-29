#include <gtest/gtest.h>

#include <algorithm>
#include <array>
#include <cstdint>
#include <cstdlib>
#include <cstring>
#include <filesystem>
#include <optional>
#include <span>
#include <string>
#include <string_view>
#include <utility>
#include <vector>

#include "dataread_port.hpp"
#include "runscript.hpp"
#include "script_text.hpp"

namespace {

/**
 * Reads a retail asset from either the published archive or an extracted data tree.
 */
std::optional<std::vector<unsigned char>> PublishedFile(std::string_view key) {
    const char *data = std::getenv("DC2_DATA");
    if (!data) {
        return std::nullopt;
    }
    DataReadIndex(std::filesystem::path(data));
    const auto                 file = DataReadFind(std::string(key).c_str());
    std::vector<unsigned char> bytes;
    if (!file || !vfs::Read(*file, bytes)) {
        return std::nullopt;
    }
    return bytes;
}

constexpr std::string_view kKey = "map/s/s17/s17_5.stb";
constexpr std::size_t      kCode = 0xA8;
constexpr std::size_t      kStrings = 0x50000;

/**
 * Writes an unaligned operand into a script fixture.
 */
void Write32(std::vector<unsigned char> &bytes, std::size_t offset, std::int32_t value) {
    std::memcpy(bytes.data() + offset, &value, sizeof(value));
}

/**
 * Reads an unaligned operand from a script fixture.
 */
std::int32_t Read32(const std::vector<unsigned char> &bytes, std::size_t offset) {
    std::int32_t value{};
    std::memcpy(&value, bytes.data() + offset, sizeof(value));
    return value;
}

/**
 * Builds a script containing every anchored retail string.
 */
std::vector<unsigned char> RetailScript() {
    std::vector<unsigned char> bytes(kStrings);
    std::memcpy(bytes.data(), "SB2", 4);
    Write32(bytes, 8, static_cast<std::int32_t>(kCode));
    for (const script_text::Line &line : script_text::Find(kKey)) {
        const std::size_t at = bytes.size();
        bytes.insert(bytes.end(), line.retail, line.retail + std::strlen(line.retail) + 1);
        Write32(bytes, line.push, RS_OP_PUSH_CONST);
        Write32(bytes, line.push + 4, RS_CONST_STR);
        Write32(bytes, line.push + 8, static_cast<std::int32_t>(at - kCode));
    }
    return bytes;
}

/**
 * Reads the bounded string referenced by a fixture instruction.
 */
std::string Pushed(const std::vector<unsigned char> &bytes, std::size_t size, const script_text::Line &line) {
    const std::size_t at = kCode + static_cast<std::size_t>(Read32(bytes, line.push + 8));
    const auto        end = std::find(bytes.begin() + static_cast<std::ptrdiff_t>(at), bytes.begin() + static_cast<std::ptrdiff_t>(size), 0);
    EXPECT_NE(end, bytes.begin() + static_cast<std::ptrdiff_t>(size));
    return {bytes.begin() + static_cast<std::ptrdiff_t>(at), end};
}

/**
 * Counts the widest display line while treating encoded glyphs as one character.
 */
std::size_t LongestLine(std::string_view text) {
    std::size_t longest = 0;
    std::size_t current = 0;
    for (std::size_t i = 0; i < text.size(); ++i) {
        if (text[i] == '\n' || text.substr(i).starts_with("<page>")) {
            longest = std::max(longest, current);
            current = 0;
            i += text[i] == '\n' ? 0 : 5;
            continue;
        }
        if (text.substr(i).starts_with("[UNI")) {
            i += 8;
        }
        ++current;
    }
    return std::max(longest, current);
}

} // namespace

TEST(ScriptText, SpanishSphedaScriptPushesSpanishText) {
    auto              bytes = RetailScript();
    const std::size_t size = bytes.size();
    const std::size_t extra = script_text::Extra(kKey);
    EXPECT_GT(extra, 0u);
    bytes.resize(size + extra, 0xCC);

    const std::size_t patched = script_text::Patch(kKey, bytes.data(), size);
    EXPECT_EQ(patched, size + extra);
    for (const script_text::Line &line : script_text::Find(kKey)) {
        EXPECT_EQ(Pushed(bytes, patched, line), line.text);
    }
}

TEST(ScriptText, KeyIgnoresCaseAndSeparators) {
    EXPECT_EQ(script_text::Find("MAP\\S\\S17\\S17_5.STB").size(), script_text::Find(kKey).size());
    EXPECT_TRUE(script_text::Find("map/s/s17/s17_4.stb").empty());
    EXPECT_EQ(script_text::Extra("map/s/s17/s17_1.stb"), 0u);
}

TEST(ScriptText, ScriptThatDoesNotMatchIsLeftUnchanged) {
    auto bytes = RetailScript();
    // A datapack or another release with different text in one line.
    const script_text::Line &last = script_text::Find(kKey).back();
    const std::size_t        at = kCode + static_cast<std::size_t>(Read32(bytes, last.push + 8));
    bytes[at] = 'X';
    const auto before = bytes;
    const auto size = bytes.size();
    bytes.resize(size + script_text::Extra(kKey));

    EXPECT_EQ(script_text::Patch(kKey, bytes.data(), size), size);
    EXPECT_TRUE(std::equal(before.begin(), before.end(), bytes.begin()));
}

TEST(ScriptText, OtherScriptsAreLeftUnchanged) {
    auto       bytes = RetailScript();
    const auto before = bytes;
    EXPECT_EQ(script_text::Patch("map/s/s17/s17_4.stb", bytes.data(), bytes.size()), bytes.size());
    EXPECT_EQ(bytes, before);
}

TEST(ScriptText, SpanishLinesFitTheMessageWindow) {
    // The widest retail line in these windows, English "certain direction at the time of impact,".
    for (const script_text::Line &line : script_text::Find(kKey)) {
        EXPECT_LE(LongestLine(line.text), 40u) << line.text;
    }
}

TEST(ScriptText, ExtractedSpanishSphedaScriptMatches) {
    auto file = PublishedFile(kKey);
    if (!file) {
        GTEST_SKIP() << "DC2_DATA supplies the retail scripts";
    }
    auto              bytes = std::move(*file);
    const std::size_t size = bytes.size();
    bytes.resize(size + script_text::Extra(kKey));

    ASSERT_EQ(script_text::Patch(kKey, bytes.data(), size), bytes.size());
    for (const script_text::Line &line : script_text::Find(kKey)) {
        EXPECT_EQ(Pushed(bytes, bytes.size(), line), line.text);
    }
}

TEST(ScriptText, SpanishJumpGateMenusListEveryDestination) {
    // The script offers five destinations in the first f04 menu and four in the others.
    const std::array<std::pair<std::string_view, std::size_t>, 3> menus{
        {{"map/f/f04/f04_5.stb", 5}, {"map/f/f04/f04_5.stb", 4}, {"map/s/s30/s30_5.stb", 4}}
    };
    const std::array<std::span<const script_text::Line>, 3> lines{
        script_text::Find(menus[0].first).first(1), script_text::Find(menus[1].first).subspan(1), script_text::Find(menus[2].first)};
    for (std::size_t i = 0; i < menus.size(); ++i) {
        ASSERT_EQ(lines[i].size(), 1u) << menus[i].first;
        const std::string_view text = lines[i].front().text;
        EXPECT_EQ(static_cast<std::size_t>(std::count(text.begin(), text.end(), '\n')), menus[i].second) << text;
        EXPECT_FALSE(text.ends_with('\n')) << text;
        EXPECT_LE(LongestLine(text), 40u) << text;
    }
    EXPECT_TRUE(script_text::Find("map/f/f04/f04_1.stb").empty());
    EXPECT_TRUE(script_text::Find("map/s/s30/s30_4.stb").empty());
}

TEST(ScriptText, ExtractedSpanishJumpGateScriptsMatch) {
    for (const std::string_view key : {"map/f/f04/f04_5.stb", "map/s/s30/s30_5.stb"}) {
        auto file = PublishedFile(key);
        if (!file) {
            GTEST_SKIP() << "DC2_DATA supplies the retail scripts";
        }
        auto              bytes = std::move(*file);
        const std::size_t size = bytes.size();
        bytes.resize(size + script_text::Extra(key));

        ASSERT_EQ(script_text::Patch(key, bytes.data(), size), bytes.size()) << key;
        const auto code = static_cast<std::size_t>(Read32(bytes, 8));
        for (const script_text::Line &line : script_text::Find(key)) {
            const std::size_t at = code + static_cast<std::size_t>(Read32(bytes, line.push + 8));
            EXPECT_EQ(std::string(reinterpret_cast<const char *>(bytes.data() + at)), line.text) << key;
        }
    }
}

TEST(ScriptText, SpanishSphedaLessonTextIsCorrected) {
    constexpr std::string_view key = "event/e300/e314/e314_5.txt";
    std::string                text = "@3140050\r\nNo tienes que acordarte de nada m\xe1s.\r\n@3140055\r\nDe azul a rojo, de negro a azul. "
                                      "As\xed.\r\n@3140080\r\nuna pared o algo y cambiar\xe1 el color a negro.\r\n";
    const std::size_t          size = text.size();
    EXPECT_EQ(script_text::Extra(key), 0u);

    const std::size_t patched = script_text::Patch(key, reinterpret_cast<unsigned char *>(text.data()), size);
    const std::string expected = "@3140050\r\nTienes que acordarte de otra cosa m\xe1s.\r\n@3140055\r\nDe azul a rojo, de rojo a azul. "
                                 "As\xed.\r\n@3140080\r\nuna pared o algo y cambiar\xe1 el color.\r\n";
    ASSERT_EQ(patched, expected.size());
    EXPECT_EQ(text.substr(0, patched), expected);
    EXPECT_EQ(text.find_first_not_of('\0', patched), std::string::npos);
}

TEST(ScriptText, EventTextWithoutTheRetailLinesIsLeftUnchanged) {
    std::string       text = "@3140050\r\nNo tienes que acordarte de nada m\xe1s.\r\n";
    const std::string before = text;
    EXPECT_EQ(script_text::Patch("event/e300/e314/e314_5.txt", reinterpret_cast<unsigned char *>(text.data()), text.size()), text.size());
    EXPECT_EQ(text, before);
}

TEST(ScriptText, ExtractedSpanishSphedaLessonMatches) {
    constexpr std::string_view key = "event/e300/e314/e314_5.txt";
    auto                       file = PublishedFile(key);
    if (!file) {
        GTEST_SKIP() << "DC2_DATA supplies the retail event text";
    }
    auto bytes = std::move(*file);
    EXPECT_EQ(script_text::Patch(key, bytes.data(), bytes.size()), bytes.size() - 7);
}

TEST(ScriptText, InvalidStringOffsetsLeaveTheEntireScriptUnchanged) {
    for (const auto offset : {-1, 0x7fffffff}) {
        auto       bytes = RetailScript();
        const auto size = bytes.size();
        bytes.resize(size + script_text::Extra(kKey));
        Write32(bytes, script_text::Find(kKey).back().push + offsetof(vmcode_t, arg2), offset);
        const auto before = bytes;
        EXPECT_EQ(script_text::Patch(kKey, bytes.data(), size), size);
        EXPECT_EQ(bytes, before);
    }
}
