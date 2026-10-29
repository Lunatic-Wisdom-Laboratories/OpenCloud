#pragma once

#include <cstddef>
#include <cstdint>
#include <span>
#include <string_view>

namespace script_text {

/**
 * Describes a string correction anchored to a compiled script instruction.
 */
struct Line {
    std::uint32_t push;   /**< File offset of the PUSH_CONST instruction. */
    const char   *retail; /**< Expected null-terminated retail text. */
    const char   *text;   /**< Corrected null-terminated text. */
};

/**
 * Describes an exact replacement in a Latin-1 event message file.
 */
struct Edit {
    const char *retail; /**< Passage that must occur exactly once. */
    const char *text;   /**< Corrected passage. */
};

/**
 * Returns anchored corrections for a script, or an empty span for an unaffected asset.
 */
std::span<const Line> Find(std::string_view key);

/**
 * Returns exact passage corrections for an event message file.
 */
std::span<const Edit> Edits(std::string_view key);

/**
 * Returns the maximum additional bytes required to hold an asset's corrected text.
 */
std::size_t Extra(std::string_view key);

/**
 * Corrects matching PAL text and returns its actual size, leaving mismatched files unchanged.
 * The buffer must hold size plus Extra(key) bytes; shortened event text is zero-filled to size.
 */
std::size_t Patch(std::string_view key, unsigned char *bytes, std::size_t size);

} // namespace script_text
