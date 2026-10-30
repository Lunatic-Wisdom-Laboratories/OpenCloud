#include <SDL3/SDL_platform.h>

// iOS has its own launcher in UIKit (ios/Launcher.swift).
#ifndef SDL_PLATFORM_IOS

#include "opencloud_bridge/rust/launcher_host.h"

#include <cstdint>
#include <filesystem>
#include <string>
#include <string_view>
#include <utility>

#include "launcher/desktop.hpp"
#include "launcher/model.hpp"
#include "platform/firstrun.hpp"
#include "platform/paths.hpp"

namespace {

std::string Std(rust::Str text) { return std::string(text); }

// The launcher's questions, answered by the model.
class Host final : public opencloud::LauncherHost {
public:
    explicit Host(std::filesystem::path root) : root_(std::move(root)) {}

    rust::String text(rust::Str key) const override {
        return launcher::Text(std::string_view(key.data(), key.size()));
    }

    std::int32_t language() const override { return static_cast<std::int32_t>(launcher::CurrentLanguage()) - 1; }

    std::int32_t language_count() const override { return launcher::kLanguageCount; }

    rust::String language_name(std::int32_t index) const override {
        return launcher::LanguageName(launcher::LanguageAt(index));
    }

    void set_language(std::int32_t index) const override { launcher::SetLanguage(launcher::LanguageAt(index)); }

    rust::String data_root() const override { return PathsDisplay(root_); }

    bool data_ready() const override { return !FirstRunDataMissing(root_); }

    rust::Vec<opencloud::LauncherSetting> settings() const override {
        rust::Vec<opencloud::LauncherSetting> rows;
        for (const launcher::Setting &setting : launcher::Settings()) {
            rows.push_back({setting.page, setting.row, setting.page_name, setting.label, setting.help, setting.value});
        }
        return rows;
    }

    std::int32_t step_setting(std::int32_t page, std::int32_t row, std::int32_t direction) const override {
        return static_cast<std::int32_t>(launcher::StepSetting(page, row, direction));
    }

    rust::Vec<opencloud::LauncherSave> saves() const override {
        rust::Vec<opencloud::LauncherSave> saves;
        for (const launcher::Save &save : launcher::Saves()) {
            saves.push_back({save.file, save.number, save.autosave, save.readable, save.status, save.chapter,
                             save.play_seconds, save.modified});
        }
        return saves;
    }

    bool saves_protected() const override { return launcher::SavesProtected(); }

    rust::String saves_folder() const override { return launcher::SavesFolder(); }

    rust::String delete_save(rust::Str file) const override { return launcher::DeleteSave(Std(file)); }

    rust::String duplicate_save(rust::Str file) const override { return launcher::DuplicateSave(Std(file)); }

    rust::String export_save(rust::Str file, rust::Str destination) const override {
        return launcher::ExportSave(Std(file), Std(destination));
    }

    rust::String import_save(rust::Str source) const override { return launcher::ImportSave(Std(source)); }

    std::int32_t legacy_save_count() const override { return launcher::LegacySaveCount(); }

    rust::String legacy_folder() const override { return launcher::LegacyFolder(); }

    rust::String import_legacy(std::int32_t &imported) const override {
        int count = 0;
        std::string error = launcher::ImportLegacy(count);
        imported = count;
        return error;
    }

    rust::String legacy_datapacks_folder() const override { return launcher::LegacyDatapacksFolder(); }

    std::uint64_t legacy_datapack_bytes() const override { return launcher::LegacyDatapackBytes(); }

    rust::String datapacks_folder() const override { return launcher::DatapacksFolder(); }

    rust::String datapacks_room(std::uint64_t bytes) const override { return launcher::DatapacksRoom(bytes); }

    rust::Vec<opencloud::LauncherPhoto> photos() const override {
        rust::Vec<opencloud::LauncherPhoto> photos;
        for (const launcher::Photo &photo : launcher::Photos()) {
            rust::Vec<std::uint8_t> rgba;
            rgba.reserve(photo.rgba.size());
            for (const std::uint8_t byte : photo.rgba) {
                rgba.push_back(byte);
            }
            photos.push_back({photo.card, photo.slot, photo.hd, static_cast<std::uint32_t>(photo.width),
                              static_cast<std::uint32_t>(photo.height), std::move(rgba)});
        }
        return photos;
    }

    rust::String export_photo(std::int32_t card, std::int32_t slot, rust::Str destination) const override {
        return launcher::ExportPhoto(card, slot, Std(destination));
    }

    rust::String delete_photo(std::int32_t card, std::int32_t slot) const override {
        return launcher::DeletePhoto(card, slot);
    }

private:
    std::filesystem::path root_;
};

} // namespace

bool LauncherRunDesktop(const std::filesystem::path &root) {
    const Host host(root);
    return opencloud::run_launcher("OpenCloud", host);
}

#endif
