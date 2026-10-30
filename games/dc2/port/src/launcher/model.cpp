#include "launcher/model.hpp"

#include <nlohmann/json.hpp>

#include <algorithm>
#include <cctype>
#include <chrono>
#include <ctime>
#include <cstdlib>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <memory>
#include <optional>
#include <set>
#include <sstream>
#include <system_error>
#include <utility>

#ifdef _WIN32
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
// The game has its own RECT (font.hpp).
#define RECT WindowsRect
#include <windows.h>
#undef RECT
#endif

#include "gfx/gfx.hpp"
#include "inventmn.hpp"
#include "log/log.hpp"
#include "options/rows.hpp"
#include "photo_hd.hpp"
#include "platform/config.hpp"
#include "platform/files.hpp"
#include "platform/paths.hpp"
#include "save_json.hpp"
#include "save_slots.hpp"

namespace launcher {
namespace fs = std::filesystem;

// launcher_text.cpp: the translations, by key.
const char *Translation(Language language, std::string_view key);

namespace {

constexpr char kSettingsFile[] = "launcher.json";
// ChronicleTwo's per-user folder, whose saves the launcher copies in and never changes.
constexpr char kLegacyApp[] = "chronicletwo";
constexpr char kAlbumName[] = "BESCES-51190dc2album";

std::optional<Language> chosen;
bool                    chosen_loaded = false;
std::string             system_code;

std::string Lower(std::string text) {
    std::ranges::transform(text, text.begin(), [](unsigned char c) { return static_cast<char>(std::tolower(c)); });
    return text;
}

std::optional<Language> FromCode(std::string code) {
    code = Lower(code.substr(0, 2));
    if (code == "en") {
        return Language::English;
    }
    if (code == "fr") {
        return Language::French;
    }
    if (code == "de") {
        return Language::German;
    }
    if (code == "it") {
        return Language::Italian;
    }
    if (code == "es") {
        return Language::Spanish;
    }
    return std::nullopt;
}

std::string SystemCode() {
    if (!system_code.empty()) {
        return system_code;
    }
#ifdef _WIN32
    wchar_t name[LOCALE_NAME_MAX_LENGTH] = {};
    if (GetUserDefaultLocaleName(name, LOCALE_NAME_MAX_LENGTH) > 0) {
        std::string code;
        for (const wchar_t *c = name; *c != 0 && code.size() < 16; ++c) {
            code.push_back(static_cast<char>(*c));
        }
        return code;
    }
#endif
    for (const char *variable : {"LC_ALL", "LC_MESSAGES", "LANG"}) {
        if (const char *value = std::getenv(variable); value != nullptr && *value != 0) {
            return value;
        }
    }
    return {};
}

fs::path SettingsPath() { return PathsSaveRoot() / kSettingsFile; }

void LoadChosen() {
    if (chosen_loaded) {
        return;
    }
    chosen_loaded = true;
    std::ifstream file(SettingsPath());
    if (!file) {
        return;
    }
    const auto json = nlohmann::json::parse(file, nullptr, false);
    if (json.is_object() && json.contains("language") && json["language"].is_string()) {
        chosen = FromCode(json["language"].get<std::string>());
    }
}

const char *CodeOf(Language language) {
    switch (language) {
        case Language::French:
            return "fr";
        case Language::German:
            return "de";
        case Language::Italian:
            return "it";
        case Language::Spanish:
            return "es";
        default:
            return "en";
    }
}

bool SameFolder(const fs::path &a, const fs::path &b) {
    std::error_code error;
    if (fs::equivalent(a, b, error)) {
        return true;
    }
    return Lower(PathsDisplay(fs::weakly_canonical(a, error))) == Lower(PathsDisplay(fs::weakly_canonical(b, error)));
}

// ChronicleTwo's saves folder on this system, whether or not it exists.
std::vector<fs::path> LegacyCandidates() {
    std::vector<fs::path> candidates;
    auto                  from = [](const char *variable) -> std::optional<fs::path> {
#ifdef _WIN32
        const std::wstring name(variable, variable + std::strlen(variable));
        const wchar_t     *value = _wgetenv(name.c_str());
        if (value != nullptr && *value != 0) {
            return fs::path(value);
        }
#else
        if (const char *value = std::getenv(variable); value != nullptr && *value != 0) {
            return fs::path(value);
        }
#endif
        return std::nullopt;
    };
    if (auto local = from("LOCALAPPDATA")) {
        candidates.push_back(*local / kLegacyApp / "saves");
    }
    if (auto home = from("HOME")) {
        candidates.push_back(*home / "Documents" / kLegacyApp / "saves");
        candidates.push_back(*home / ".local" / "share" / kLegacyApp / "saves");
    }
    if (auto xdg = from("XDG_DATA_HOME")) {
        candidates.push_back(*xdg / kLegacyApp / "saves");
    }
    return candidates;
}

std::optional<fs::path> LegacyRoot() {
    std::error_code error;
    for (const fs::path &candidate : LegacyCandidates()) {
        if (fs::is_directory(candidate / "saves", error) && !SameFolder(candidate, PathsSaveRoot())) {
            return candidate;
        }
    }
    return std::nullopt;
}

// A save handle names one file directly in the saves folder.
std::optional<fs::path> SavePath(const std::string &file) {
    if (file.empty() || file.find_first_of("/\\:") != std::string::npos || file == "." || file == "..") {
        return std::nullopt;
    }
    bool second = false;
    if (file != "autosave.json" && SaveSlotNumber(file, second) < 0) {
        return std::nullopt;
    }
    return SaveSlotsRoot() / PathsFromUtf8(file);
}

std::string ReadText(const fs::path &path) {
    std::ifstream      file(path, std::ios::binary);
    std::ostringstream text;
    text << file.rdbuf();
    return text.str();
}

std::string Chapter(int progress) {
    // As the load menu counts them (menu_save.cpp).
    const int chapter = progress == 100 ? 11 : progress >= 4 ? progress - 2 : progress >= 2 ? 1 : 0;
    if (chapter == 0) {
        return Text("save.prologue");
    }
    if (chapter >= 11) {
        return Text("save.cleared");
    }
    std::string text = Text("save.chapter");
    if (const auto at = text.find("{}"); at != std::string::npos) {
        text.replace(at, 2, std::to_string(chapter));
    }
    return text;
}

// The lowest save number neither the saves folder nor a legacy image already uses.
int FreeNumber(const fs::path &folder) {
    std::set<int>   used;
    std::error_code error;
    for (const auto &entry : fs::directory_iterator(folder, error)) {
        bool second = false;
        if (const int number = SaveSlotNumber(PathsDisplay(entry.path().filename()), second); number >= 0) {
            used.insert(number);
        }
    }
    int number = 0;
    while (used.contains(number)) {
        ++number;
    }
    return number;
}

std::string LocalTime(std::time_t time) {
    std::tm local{};
#ifdef _WIN32
    localtime_s(&local, &time);
#else
    localtime_r(&time, &local);
#endif
    char text[32];
    return std::strftime(text, sizeof text, "%Y-%m-%d %H:%M", &local) ? text : "";
}

std::string Failed(std::string_view key, const std::string &detail = {}) {
    std::string text = Text(key);
    if (!detail.empty()) {
        text += "\n" + detail;
    }
    return text;
}

// The album file as the game writes it (memcard.cpp's AlbumFile over CDC2AlbumData).
struct AlbumFile {
    u8   data[sizeof(CDC2AlbumData::photo_work)];
    char digit_data[sizeof(USER_PICTURE_INFO) * INVENT_ALBUM_PHOTO_NUM];
    int  checksum;
    int  trailer;
};
static_assert(sizeof(AlbumFile) <= sizeof(CDC2AlbumData));

fs::path AlbumPath(const fs::path &root, int card) {
    return root / (card ? "mc1" : "mc0") / kAlbumName / kAlbumName;
}

std::unique_ptr<CDC2AlbumData> ReadAlbum(int card) {
    const fs::path  path = AlbumPath(PathsSaveRoot(), card);
    std::error_code error;
    if (fs::file_size(path, error) != sizeof(CDC2AlbumData) || error) {
        return nullptr;
    }
    // Heap, not stack: it is 400 KB.
    auto album = std::unique_ptr<CDC2AlbumData>(static_cast<CDC2AlbumData *>(::operator new(sizeof(CDC2AlbumData))));
    if (FilesRead(path, album.get(), sizeof(CDC2AlbumData)) != sizeof(CDC2AlbumData)) {
        return nullptr;
    }
    return album;
}

int CheckDigit(const char *data, std::size_t size) {
    // MakeCheckDigit's mode 0: one byte of each 64.
    int sum = 0;
    for (std::size_t i = 0; i + 64 <= size; i += 64) {
        sum += static_cast<u8>(data[i]) % 255;
    }
    return sum;
}

std::vector<std::uint8_t> Decode16(const u8 *pixels) {
    std::vector<std::uint8_t> rgba(64 * 64 * 4);
    for (int i = 0; i < 64 * 64; ++i) {
        const std::uint32_t texel = pixels[i * 2] | (static_cast<std::uint32_t>(pixels[i * 2 + 1]) << 8);
        const std::uint32_t r = texel & 0x1F, g = (texel >> 5) & 0x1F, b = (texel >> 10) & 0x1F;
        rgba[i * 4 + 0] = static_cast<std::uint8_t>((r << 3) | (r >> 2));
        rgba[i * 4 + 1] = static_cast<std::uint8_t>((g << 3) | (g >> 2));
        rgba[i * 4 + 2] = static_cast<std::uint8_t>((b << 3) | (b >> 2));
        rgba[i * 4 + 3] = 0xFF;
    }
    return rgba;
}

Photo PhotoOf(const CDC2AlbumData &album, int card, int slot) {
    Photo photo{.card = card, .slot = slot};
    const u8 *pixels = reinterpret_cast<const u8 *>(album.photo_work[slot]);
    if (const auto *hd = PhotoHdFind(pixels, sizeof(album.photo_work[slot]))) {
        photo.hd = true;
        photo.width = photo.height = kPhotoHdSize;
        photo.rgba = *hd;
    } else {
        photo.width = photo.height = 64;
        photo.rgba = Decode16(pixels);
    }
    return photo;
}

// Copies one file in without replacing anything: false when the destination is taken or the copy failed.
bool CopyNew(const fs::path &from, const fs::path &to) {
    std::error_code error;
    if (fs::exists(to, error)) {
        return false;
    }
    fs::create_directories(to.parent_path(), error);
    return fs::copy_file(from, to, fs::copy_options::none, error) && !error;
}

} // namespace

Language CurrentLanguage() {
    LoadChosen();
    if (chosen) {
        return *chosen;
    }
    return FromCode(SystemCode()).value_or(Language::English);
}

void SetLanguage(Language language) {
    chosen = language;
    chosen_loaded = true;
    const std::string text = nlohmann::json{{"language", CodeOf(language)}}.dump(2) + "\n";
    if (FilesWrite(SettingsPath(), text.data(), text.size(), true) != FilesResult::kWritten) {
        CT_LOG_WARN(Main, "launcher: could not write {}", PathsDisplay(SettingsPath()));
    }
}

void SetSystemLanguage(std::string_view code) { system_code = code; }

Language LanguageAt(int index) { return static_cast<Language>(std::clamp(index, 0, kLanguageCount - 1) + 1); }

std::string LanguageName(Language language) {
    switch (language) {
        case Language::French:
            return "Français";
        case Language::German:
            return "Deutsch";
        case Language::Italian:
            return "Italiano";
        case Language::Spanish:
            return "Español";
        default:
            return "English";
    }
}

std::string Text(std::string_view key) {
    if (const char *text = Translation(CurrentLanguage(), key)) {
        return text;
    }
    if (const char *text = Translation(Language::English, key)) {
        return text;
    }
    return std::string(key);
}

// The game's own English is the fallback for a row or value the translations do not know.
static std::string TextOr(const std::string &key, const std::string &english) {
    if (const char *text = Translation(CurrentLanguage(), key)) {
        return text;
    }
    return english;
}

std::vector<Setting> Settings() {
    std::vector<Setting> settings;
    const auto           pages = options::Pages();
    const Config        &config = ConfigGet();
    for (int page = 0; std::cmp_less(page, pages.size()); ++page) {
        const std::string page_name = TextOr(std::string("page.") + pages[page].name, pages[page].name);
        for (int row = 0; std::cmp_less(row, pages[page].rows.size()); ++row) {
            const options::Row &entry = pages[page].rows[row];
            // Bindings wait for a key in the game, and the resolutions are listed once the game's window is up.
            if (!entry.step || !entry.action.empty() || entry.key == "video.width") {
                continue;
            }
            const std::string value = entry.value ? entry.value(config) : "";
            settings.push_back({.page = page,
                                .row = row,
                                .page_name = page_name,
                                .label = TextOr("setting." + entry.key, entry.label),
                                .help = TextOr("help." + entry.key, entry.help),
                                .value = TextOr("value." + value, value)});
        }
    }
    return settings;
}

StepResult StepSetting(int page, int row, int direction) {
    const auto pages = options::Pages();
    if (page < 0 || std::cmp_greater_equal(page, pages.size()) || row < 0 ||
        std::cmp_greater_equal(row, pages[page].rows.size())) {
        return StepResult::Unchanged;
    }
    Config config = ConfigGet();
    if (!options::StepRow(pages[page].rows[row], config, direction)) {
        return StepResult::Unchanged;
    }
    return ConfigChange(config) ? StepResult::Saved : StepResult::NotSaved;
}

std::vector<Save> Saves() {
    std::vector<Save> saves;
    std::error_code   error;
    for (const auto &entry : fs::directory_iterator(SaveSlotsRoot(), error)) {
        const std::string name = PathsDisplay(entry.path().filename());
        bool              second = false;
        const int         number = SaveSlotNumber(name, second);
        const bool        autosave = name == "autosave.json";
        if ((!autosave && number < 0) || entry.path().extension() != ".json" || !entry.is_regular_file(error)) {
            continue;
        }
        Save save{.file = name, .number = autosave ? -1 : number, .autosave = autosave};
        SAVEDATA_INFO info{};
        if (SaveJsonReadHeader(ReadText(entry.path()), info) == SaveJsonError::None) {
            save.readable = true;
            save.chapter = Chapter(info.progress);
            save.play_seconds = info.play_time / 50;
        } else {
            save.status = Text("save.unreadable");
        }
        const auto written = fs::last_write_time(entry.path(), error);
        if (!error) {
            // Through now on both clocks: clock_cast is missing from some standard libraries.
            const auto system = std::chrono::system_clock::now() +
                                std::chrono::duration_cast<std::chrono::system_clock::duration>(
                                    written - fs::file_time_type::clock::now());
            save.modified = LocalTime(std::chrono::system_clock::to_time_t(system));
        }
        saves.push_back(std::move(save));
    }
    std::ranges::sort(saves, [](const Save &a, const Save &b) {
        return std::pair(!a.autosave, a.number) < std::pair(!b.autosave, b.number);
    });
    return saves;
}

bool SavesProtected() {
    for (const fs::path &candidate : LegacyCandidates()) {
        if (SameFolder(candidate, PathsSaveRoot())) {
            return true;
        }
    }
    // Pointed at with --save or DC2_SAVE, ChronicleTwo's folder is still not the launcher's to change.
    for (const fs::path &part : PathsSaveRoot()) {
        if (Lower(PathsDisplay(part)) == kLegacyApp) {
            return true;
        }
    }
    return false;
}

std::string SavesFolder() { return PathsDisplay(SaveSlotsRoot()); }

std::string DeleteSave(const std::string &file) {
    if (SavesProtected()) {
        return Text("error.protected");
    }
    const auto path = SavePath(file);
    std::error_code error;
    if (!path || !fs::remove(*path, error) || error) {
        return Failed("error.delete", error.message());
    }
    CT_LOG_INFO(Main, "launcher: deleted save {}", file);
    return {};
}

std::string DuplicateSave(const std::string &file) {
    const auto path = SavePath(file);
    if (!path) {
        return Failed("error.copy");
    }
    const fs::path to = SaveSlotPath(FreeNumber(SaveSlotsRoot()));
    return CopyNew(*path, to) ? std::string{} : Failed("error.copy", PathsDisplay(to));
}

std::string ExportSave(const std::string &file, const std::string &destination) {
    const auto      path = SavePath(file);
    std::error_code error;
    if (!path || !fs::copy_file(*path, PathsFromUtf8(destination), fs::copy_options::overwrite_existing, error)) {
        return Failed("error.export", error.message());
    }
    return {};
}

std::string ImportSave(const std::string &source) {
    const fs::path from = PathsFromUtf8(source);
    SAVEDATA_INFO  info{};
    if (SaveJsonReadHeader(ReadText(from), info) != SaveJsonError::None) {
        return Text("error.not_a_save");
    }
    std::error_code error;
    fs::create_directories(SaveSlotsRoot(), error);
    const fs::path to = SaveSlotPath(FreeNumber(SaveSlotsRoot()));
    return CopyNew(from, to) ? std::string{} : Failed("error.import", PathsDisplay(to));
}

int LegacySaveCount() {
    const auto root = LegacyRoot();
    if (!root) {
        return 0;
    }
    int             count = 0;
    std::error_code error;
    for (const auto &entry : fs::directory_iterator(*root / "saves", error)) {
        count += entry.path().extension() == ".json";
    }
    return count;
}

std::string LegacyFolder() {
    const auto root = LegacyRoot();
    return root ? PathsDisplay(*root) : std::string{};
}

std::string ImportLegacy(int &imported) {
    imported = 0;
    const auto root = LegacyRoot();
    if (!root) {
        return Text("error.no_legacy");
    }
    std::error_code error;
    fs::create_directories(SaveSlotsRoot(), error);
    // Every file is copied; ChronicleTwo's folder is only read.
    for (const auto &entry : fs::directory_iterator(*root / "saves", error)) {
        if (entry.path().extension() != ".json" || !entry.is_regular_file()) {
            continue;
        }
        const std::string name = PathsDisplay(entry.path().filename());
        fs::path          to = SaveSlotsRoot() / entry.path().filename();
        if (fs::exists(to)) {
            if (name == "autosave.json") {
                continue;
            }
            to = SaveSlotPath(FreeNumber(SaveSlotsRoot()));
        }
        imported += CopyNew(entry.path(), to);
    }
    // The sharper photo copies, found again by name.
    for (const auto &entry : fs::directory_iterator(*root / "photos", error)) {
        CopyNew(entry.path(), PathsSaveRoot() / "photos" / entry.path().filename());
    }
    // An album only where this installation has none.
    for (int card = 0; card < 2; ++card) {
        if (fs::exists(AlbumPath(*root, card)) && !fs::exists(AlbumPath(PathsSaveRoot(), card))) {
            imported += CopyNew(AlbumPath(*root, card), AlbumPath(PathsSaveRoot(), card));
        }
    }
    CT_LOG_INFO(Main, "launcher: imported {} files from {}", imported, PathsDisplay(*root));
    return {};
}

// The data packs live beside the data folder (datapacks.cpp), so ChronicleTwo's beside its saves.
static fs::path DatapacksPath() { return PathsDataRoot().parent_path() / "datapacks"; }

static std::optional<fs::path> LegacyDatapacksPath() {
    const auto root = LegacyRoot();
    if (!root) {
        return std::nullopt;
    }
    fs::path        from = root->parent_path() / "datapacks";
    std::error_code error;
    if (!fs::is_directory(from, error) || SameFolder(from, DatapacksPath())) {
        return std::nullopt;
    }
    return from;
}

std::string DatapacksFolder() { return PathsDisplay(DatapacksPath()); }

std::string LegacyDatapacksFolder() {
    const auto from = LegacyDatapacksPath();
    return from ? PathsDisplay(*from) : std::string{};
}

std::uint64_t LegacyDatapackBytes() {
    const auto from = LegacyDatapacksPath();
    if (!from) {
        return 0;
    }
    std::uint64_t   bytes = 0;
    std::error_code error;
    for (auto it = fs::recursive_directory_iterator(*from, error); !error && it != fs::recursive_directory_iterator();
         it.increment(error)) {
        std::error_code entry;
        if (it->is_regular_file(entry)) {
            bytes += it->file_size(entry);
        }
    }
    return bytes;
}

std::string DatapacksRoom(std::uint64_t bytes) {
    const fs::path  to = DatapacksPath();
    std::error_code error;
    fs::create_directories(to, error);
    // The copy and a margin for the game's own writes.
    const auto space = fs::space(to, error);
    constexpr std::uint64_t kMargin = std::uint64_t{512} << 20;
    if (!error && space.available < bytes + kMargin) {
        const auto megabytes = [](std::uint64_t value) { return std::to_string((value + (1u << 20) - 1) >> 20) + " MB"; };
        return Failed("error.space", megabytes(bytes + kMargin) + " / " + megabytes(space.available));
    }
    return {};
}

std::vector<Photo> Photos() {
    std::vector<Photo> photos;
    for (int card = 0; card < 2; ++card) {
        const auto album = ReadAlbum(card);
        if (!album) {
            continue;
        }
        for (int slot = 0; slot < INVENT_ALBUM_PHOTO_NUM; ++slot) {
            if (album->photo[slot].used) {
                photos.push_back(PhotoOf(*album, card, slot));
            }
        }
    }
    return photos;
}

std::string ExportPhoto(int card, int slot, const std::string &destination) {
    const auto album = ReadAlbum(card);
    if (!album || slot < 0 || slot >= INVENT_ALBUM_PHOTO_NUM || !album->photo[slot].used) {
        return Failed("error.export");
    }
    const Photo photo = PhotoOf(*album, card, slot);
    if (!gfx::WritePng(PathsFromUtf8(destination), photo.rgba.data(), photo.width, photo.height)) {
        return Failed("error.export", destination);
    }
    return {};
}

std::string DeletePhoto(int card, int slot) {
    if (SavesProtected()) {
        return Text("error.protected");
    }
    auto album = ReadAlbum(card);
    if (!album || slot < 0 || slot >= INVENT_ALBUM_PHOTO_NUM) {
        return Failed("error.delete");
    }
    // As the in-game Album empties a slot (CDC2AlbumData::DeletePhotoData), then the check digits it writes.
    Init_USER_PICTURE_INFO(&album->photo[slot]);
    auto *file = reinterpret_cast<AlbumFile *>(album.get());
    file->checksum = file->trailer = CheckDigit(file->digit_data, sizeof(file->digit_data));
    if (FilesWrite(AlbumPath(PathsSaveRoot(), card), album.get(), sizeof(CDC2AlbumData), true) !=
        FilesResult::kWritten) {
        return Failed("error.delete");
    }
    return {};
}

} // namespace launcher
