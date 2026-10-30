#pragma once

#include <cstdint>

#include "rust/cxx.h"

namespace opencloud {

// Defined by the bridge (launcher_host.rs), which includes this header first.
struct LauncherSetting;
struct LauncherSave;
struct LauncherPhoto;

/**
 *
 * What the desktop launcher (launcher.rs) asks of the game it starts: its text, settings, saves and photo album. The
 * game implements it; the launcher calls it on the main thread only. Calls that change something return an empty
 * string when they worked, otherwise the error to show.
 *
 */
class LauncherHost {
public:
    virtual ~LauncherHost() = default;

    virtual rust::String text(rust::Str key) const = 0;
    /** The launcher's language as an index into its languages, 0 to language_count() - 1. */
    virtual std::int32_t language() const = 0;
    virtual std::int32_t language_count() const = 0;
    virtual rust::String language_name(std::int32_t index) const = 0;
    virtual void         set_language(std::int32_t index) const = 0;

    /** Where extraction writes the game data, and whether current data is there. */
    virtual rust::String data_root() const = 0;
    virtual bool         data_ready() const = 0;

    virtual rust::Vec<LauncherSetting> settings() const = 0;
    /** 0: unchanged, 1: saved, 2: applied but config.json could not be written. */
    virtual std::int32_t step_setting(std::int32_t page, std::int32_t row, std::int32_t direction) const = 0;

    virtual rust::Vec<LauncherSave> saves() const = 0;
    virtual bool                    saves_protected() const = 0;
    virtual rust::String            saves_folder() const = 0;
    virtual rust::String            delete_save(rust::Str file) const = 0;
    virtual rust::String            duplicate_save(rust::Str file) const = 0;
    virtual rust::String            export_save(rust::Str file, rust::Str destination) const = 0;
    virtual rust::String            import_save(rust::Str source) const = 0;
    virtual std::int32_t            legacy_save_count() const = 0;
    virtual rust::String            legacy_folder() const = 0;
    /** Copies the legacy saves in; imported is how many files were copied. */
    virtual rust::String import_legacy(std::int32_t &imported) const = 0;
    /** The legacy data packs folder (empty without one), its size, and where they go; the launcher copies them. */
    virtual rust::String  legacy_datapacks_folder() const = 0;
    virtual std::uint64_t legacy_datapack_bytes() const = 0;
    virtual rust::String  datapacks_folder() const = 0;
    /** Empty when bytes more fit beside the data, otherwise the text to show. */
    virtual rust::String datapacks_room(std::uint64_t bytes) const = 0;

    virtual rust::Vec<LauncherPhoto> photos() const = 0;
    virtual rust::String             export_photo(std::int32_t card, std::int32_t slot, rust::Str destination) const = 0;
    virtual rust::String             delete_photo(std::int32_t card, std::int32_t slot) const = 0;
};

} // namespace opencloud
