#include <SDL3/SDL_platform.h>

#ifdef SDL_PLATFORM_IOS

#include "opencloud_bridge/rust/lib.h"

#include <algorithm>
#include <array>
#include <cctype>
#include <chrono>
#include <cstdlib>
#include <exception>
#include <filesystem>
#include <format>
#include <fstream>
#include <future>
#include <thread>
#include <utility>
#include <vector>

#include "audio/synth.hpp"
#include "dualsense_haptics.hpp"
#include "gfx/gfx.hpp"
#include "ios/bridge.hpp"
#include "launcher/model.hpp"
#include "log/log.hpp"
#include "platform/firstrun.hpp"
#include "platform/input.hpp"
#include "platform/paths.hpp"
#include "save_slots.hpp"

namespace ct::ios {
namespace fs = std::filesystem;

namespace {

std::future<std::string> extraction_worker;
std::string              extraction_failure;

constexpr std::array<std::uint16_t, 16> kButtons = {
    kInputUp, kInputDown, kInputLeft, kInputRight, kInputCross, kInputCircle, kInputSquare, kInputTriangle,
    kInputL1, kInputR1, kInputL2, kInputR2, kInputSelect, kInputStart, kInputL3, kInputR3};

std::vector<launcher::Save> saves;
std::vector<PhotoInfo>      photos;

std::string Fill(std::string text, const std::string &value) {
    if (const auto at = text.find("{}"); at != std::string::npos) {
        text.replace(at, 2, value);
    }
    return text;
}

} // namespace

void SetTouchOverlay(const TouchState &state) {
    InputSetTouchOverlay({.buttons = state.buttons, .left_x = state.left_x, .left_y = state.left_y, .right_x = state.right_x, .right_y = state.right_y});
}

bool TouchOverlayVisible() { return InputTouchOverlayVisible(); }

std::uint16_t ButtonBit(int index) {
    return index >= 0 && std::cmp_less(index, kButtons.size()) ? kButtons[index] : 0;
}

int HapticsRate() { return audio::kSpuRate; }

void HapticsRender(float *left_right, int frames) { ::HapticsRender(left_right, frames); }

std::string Text(const std::string &key) { return launcher::Text(key); }

void SetSystemLanguage(const std::string &code) { launcher::SetSystemLanguage(code); }

int LanguageCount() { return launcher::kLanguageCount; }

int LanguageIndex() { return static_cast<int>(launcher::CurrentLanguage()) - 1; }

std::string LanguageName(int index) { return launcher::LanguageName(launcher::LanguageAt(index)); }

void SetLanguageIndex(int index) { launcher::SetLanguage(launcher::LanguageAt(index)); }

int SettingCount() { return static_cast<int>(launcher::Settings().size()); }

SettingInfo SettingAt(int index) {
    const auto settings = launcher::Settings();
    if (index < 0 || std::cmp_greater_equal(index, settings.size())) {
        return {};
    }
    const launcher::Setting &setting = settings[index];
    return {.page = setting.page, .row = setting.row, .page_name = setting.page_name, .label = setting.label, .help = setting.help, .value = setting.value};
}

StepResult SettingsStep(int page, int row, int direction) {
    switch (launcher::StepSetting(page, row, direction)) {
        case launcher::StepResult::Saved:
            return StepResult::Saved;
        case launcher::StepResult::NotSaved:
            return StepResult::NotSaved;
        default:
            return StepResult::Unchanged;
    }
}

int RefreshSaves() {
    saves = launcher::Saves();
    return static_cast<int>(saves.size());
}

SaveInfo SaveAt(int index) {
    if (index < 0 || std::cmp_greater_equal(index, saves.size())) {
        return {};
    }
    const launcher::Save &save = saves[index];
    SaveInfo info{.file = save.file, .path = PathsDisplay(SaveSlotsRoot() / PathsFromUtf8(save.file)), .modified = save.modified, .readable = save.readable};
    info.title = save.autosave ? launcher::Text("saves.autosave") : Fill(launcher::Text("saves.slot"), std::to_string(save.number + 1));
    if (save.readable) {
        const std::uint64_t minutes = save.play_seconds / 60;
        info.detail = std::format("{}  ·  {} {}:{:02}", save.chapter, launcher::Text("saves.play_time"), minutes / 60, minutes % 60);
    } else {
        info.detail = save.status;
    }
    return info;
}

bool SavesProtected() { return launcher::SavesProtected(); }

std::string DeleteSave(const std::string &file) { return launcher::DeleteSave(file); }

std::string DuplicateSave(const std::string &file) { return launcher::DuplicateSave(file); }

std::string ImportSave(const std::string &source) { return launcher::ImportSave(source); }

int LegacySaveCount() { return launcher::LegacySaveCount(); }

Outcome ImportLegacy() {
    int               imported = 0;
    const std::string error = launcher::ImportLegacy(imported);
    if (!error.empty()) {
        return {.message = error};
    }
    return {.ok = true, .message = Fill(launcher::Text("saves.legacy_done"), std::to_string(imported))};
}

Outcome ImportPs2(const std::string &source) {
    std::string       report;
    const std::string error = launcher::ImportPs2(source, report);
    if (!error.empty()) {
        return {.message = error};
    }
    return {.ok = true, .message = report};
}

int RefreshPhotos() {
    photos.clear();
    const char *home = std::getenv("HOME");
    if (home == nullptr) {
        return 0;
    }
    // Written afresh each time: a deleted photo's slot can hold another one next time.
    const fs::path  folder = fs::path(home) / "Library" / "Caches" / "album";
    std::error_code error;
    fs::remove_all(folder, error);
    fs::create_directories(folder, error);
    for (const launcher::Photo &photo : launcher::Photos()) {
        const fs::path png = folder / std::format("dark-cloud-2-album-{}-{:02}.png", photo.card + 1, photo.slot + 1);
        if (gfx::WritePng(png, photo.rgba.data(), photo.width, photo.height)) {
            photos.push_back({.card = photo.card, .slot = photo.slot, .hd = photo.hd, .png = png.string()});
        }
    }
    return static_cast<int>(photos.size());
}

PhotoInfo PhotoAt(int index) {
    return index >= 0 && std::cmp_less(index, photos.size()) ? photos[index] : PhotoInfo{};
}

std::string DeletePhoto(int card, int slot) { return launcher::DeletePhoto(card, slot); }

bool DataMissing(const std::string &root) { return FirstRunDataMissing(root); }

void LeaveNote() {
    const char *home = std::getenv("HOME");
    if (home == nullptr) {
        return;
    }
    std::error_code error;
    const fs::path  documents = fs::path(home) / "Documents";
    fs::create_directories(documents, error);
    const fs::path note = documents / "Copy the Dark Chronicle ISO here.txt";
    if (!fs::exists(note, error)) {
        std::ofstream(note) << "Copy your Dark Chronicle disc image (.iso) into this folder, then open the game.\n";
    }
}

std::string DiscImage() {
    const char *home = std::getenv("HOME");
    if (home == nullptr) {
        return {};
    }
    for (const fs::path &folder : {fs::path(home) / "Documents", fs::path(home) / "Documents" / "Inbox",
                                   fs::path(home) / "Inbox"}) {
        std::error_code error;
        for (const fs::directory_entry &entry : fs::directory_iterator(folder, error)) {
            std::string extension = entry.path().extension().string();
            std::ranges::transform(extension, extension.begin(), [](unsigned char c) { return std::tolower(c); });
            if (extension == ".iso" && entry.is_regular_file(error)) {
                return entry.path().string();
            }
        }
    }
    return {};
}

void ExtractBegin(const std::string &image, const std::string &root) {
    if (extraction_worker.valid() && extraction_worker.wait_for(std::chrono::seconds(0)) != std::future_status::ready) {
        return;
    }
    extraction_failure.clear();
    try {
        const std::string serial(kazarov::disc_serial(image));
        if (!serial.empty() && serial != "SCES_511.90") {
            extraction_failure = Fill(launcher::Text("data.not_pal"), serial);
            CT_LOG_ERROR(Main, "Game data setup: {} is {}, not Dark Chronicle PAL", PathsDisplay(image), serial);
            return;
        }
    } catch (const std::exception &error) {
        extraction_failure = error.what();
        return;
    }
    CT_LOG_INFO(Main, "Extracting game data from {}", PathsDisplay(image));
    extraction_worker = {};
    try {
        extraction_worker = std::async(std::launch::async, [](const std::string &image, const std::string &root) {
        try {
            const unsigned jobs = std::clamp(std::thread::hardware_concurrency(), 1u, 2u);
            opencloud::extract_tracked(image, root, jobs);
            return FirstRunDataMissing(root) ? std::string("The extraction finished without current game data.") : std::string{};
        } catch (const std::exception &error) {
            return std::string(error.what());
        } }, image, root);
    } catch (const std::exception &error) {
        extraction_failure = error.what();
    }
}

Extraction ExtractPoll() {
    if (extraction_worker.valid()) {
        if (extraction_worker.wait_for(std::chrono::seconds(0)) != std::future_status::ready) {
            const auto status = opencloud::extract_status();
            return {.running = true, .fraction = std::clamp(status.fraction, 0.0f, 1.0f), .stage = std::string(status.stage)};
        }
        extraction_failure = extraction_worker.get();
        if (!extraction_failure.empty()) {
            CT_LOG_ERROR(Main, "Game data setup: {}", extraction_failure);
        }
    }
    return {.failure = extraction_failure};
}

} // namespace ct::ios

#endif
