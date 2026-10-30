#pragma once

// Reads the save folders of a PS2 memory card from the forms players keep them in: PCSX2's memory card file (.ps2,
// with or without its ECC bytes) or its folder memory card, an EMS export (.psu, as mymc and uLaunchELF write it), or
// a save folder copied off a card. Every source is only read.

#include <cstdint>
#include <filesystem>
#include <functional>
#include <string>
#include <vector>

namespace launcher {

struct Ps2File {
    std::string               name;
    std::vector<std::uint8_t> bytes;
};

// One directory of the card's root: a save and its icons.
struct Ps2Folder {
    std::string          name;
    std::vector<Ps2File> files;
};

enum class Ps2CardResult {
    Read,       // Read; folders may still be empty.
    Unreadable, // The file or folder could not be opened or read.
    Unknown,    // Not a memory card, an export or a save folder.
    Damaged,    // A memory card whose file system is broken.
};

// Which root folders to read whole; the others are skipped without reading their files.
using Ps2FolderFilter = std::function<bool(const std::string &name)>;

// Reads the save folders at source: a memory card file, a .psu, a save folder, or a folder of any of these (PCSX2's
// folder memory card is one: a folder per save).
Ps2CardResult Ps2ReadSource(const std::filesystem::path &source, const Ps2FolderFilter &wanted,
                            std::vector<Ps2Folder> &folders);

// The same from bytes already read, for the file forms.
Ps2CardResult Ps2ReadCard(const std::vector<std::uint8_t> &card, const Ps2FolderFilter &wanted,
                          std::vector<Ps2Folder> &folders);
Ps2CardResult Ps2ReadPsu(const std::vector<std::uint8_t> &psu, const Ps2FolderFilter &wanted,
                         std::vector<Ps2Folder> &folders);

} // namespace launcher
