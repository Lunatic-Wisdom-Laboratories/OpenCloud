#pragma once

// What the launcher shows and changes before the game starts, for both its faces: the desktop window (engine
// launcher.rs, through desktop_host.cpp) and iOS's UIKit screens (ios/Launcher.swift, through ios/bridge.cpp). It is
// kept to plain types so either side can hold it. Nothing here needs the game's data or its main loop.

#include <cstdint>
#include <string>
#include <string_view>
#include <vector>

namespace launcher {

// The PAL disc's five languages, numbered as the game's LanguageCode (LANG_ENGLISH to LANG_SPANISH).
enum class Language {
    English = 1,
    French = 2,
    German = 3,
    Italian = 4,
    Spanish = 5,
};
inline constexpr int kLanguageCount = 5;

// The launcher's language: the one chosen in it, else the system's when it is one of the five, else English.
Language CurrentLanguage();
// Chooses and remembers the launcher's language (launcher.json beside the saves).
void SetLanguage(Language language);
// The system's preferred language as a BCP 47 or POSIX code ("es-ES", "fr_FR.UTF-8"), for hosts whose locale the C++
// runtime cannot see (iOS). Used only while nothing was chosen.
void SetSystemLanguage(std::string_view code);
// The language by its position, 0 to kLanguageCount - 1, and its name in itself ("Español").
Language    LanguageAt(int index);
std::string LanguageName(Language language);

// The launcher's own text for a key in the current language; English when a translation is missing.
std::string Text(std::string_view key);

// --- Settings: the APP SETTINGS / PC SETTINGS rows the launcher can step (no key bindings) ---

struct Setting {
    int         page = 0;
    int         row = 0;
    std::string page_name;
    std::string label;
    std::string help;
    std::string value;
};

std::vector<Setting> Settings();

enum class StepResult {
    Unchanged,
    Saved,
    NotSaved,
};

// Steps the row as Left (-1) and Right (1) do in the game, and saves config.json.
StepResult StepSetting(int page, int row, int direction);

// --- Saves ---

struct Save {
    std::string   file; // File name in the saves folder; the handle every other call takes.
    int           number = -1;
    bool          autosave = false;
    bool          readable = false;
    std::string   status;  // Why it cannot be read, when !readable.
    std::string   chapter; // "Chapter 3", "Prologue"...
    std::uint64_t play_seconds = 0;
    std::string   modified; // Local date and time of the last write, "2026-10-29 17:05".
};

std::vector<Save> Saves();
// The saves belong to ChronicleTwo's folder, which the launcher only reads: nothing there is deleted or replaced.
bool SavesProtected();
// The folder the saves are in, for display.
std::string SavesFolder();

// Each returns an empty string when it worked, otherwise the error to show.
std::string DeleteSave(const std::string &file);
std::string DuplicateSave(const std::string &file);
std::string ExportSave(const std::string &file, const std::string &destination);
std::string ImportSave(const std::string &source);

// Saves and album of a ChronicleTwo installation that can be copied in, never moved: 0 when there is none or it is
// the folder in use.
int         LegacySaveCount();
std::string LegacyFolder();
// Copies them in, keeping both when a number is taken; imported counts the files copied.
std::string ImportLegacy(int &imported);
// PS2 saves, from a PCSX2 memory card (.ps2, or a folder memory card), a .psu export or a save folder copied off a
// card. Saves are converted and added as new ones; the card's photo album and Spheda and Finny Frenzy records are
// copied only where this installation has none. The source is only read. Returns the error to show, or an empty
// string with report set to what was imported.
std::string ImportPs2(const std::string &source, std::string &report);
// ChronicleTwo's data packs (upscaled textures and the like), offered apart from the saves for their size: the folder
// to copy from (empty without one) and its size in bytes. The launcher copies them, keeping files already there.
std::string   LegacyDatapacksFolder();
std::uint64_t LegacyDatapackBytes();
// Where this installation loads data packs from.
std::string DatapacksFolder();
// Empty when there is room for bytes more in the data packs folder, otherwise the text to show.
std::string DatapacksRoom(std::uint64_t bytes);

// --- Photo album: the fifty photos the game keeps on a memory card, as the in-game Album shows them ---

struct Photo {
    int                       card = 0; // Memory card 0 or 1 the album is on.
    int                       slot = 0; // 0 to 49.
    bool                      hd = false;
    int                       width = 0;
    int                       height = 0;
    std::vector<std::uint8_t> rgba;
};

std::vector<Photo> Photos();
std::string        ExportPhoto(int card, int slot, const std::string &destination);
std::string        DeletePhoto(int card, int slot);

} // namespace launcher
