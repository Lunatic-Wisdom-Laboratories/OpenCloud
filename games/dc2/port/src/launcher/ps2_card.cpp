#include "launcher/ps2_card.hpp"

#include <algorithm>
#include <cctype>
#include <cstring>
#include <fstream>
#include <optional>
#include <set>
#include <string_view>
#include <system_error>

namespace launcher {
namespace fs = std::filesystem;

namespace {

// Larger files are none of the forms read here: an 8 MB card with its ECC bytes is the largest.
constexpr std::uintmax_t kMaxSource = std::uintmax_t{64} << 20;
constexpr std::size_t kEntrySize = 512; // A directory entry, on a card and in a .psu.
constexpr std::string_view kCardMagic = "Sony PS2 Memory Card Format ";

// Directory entry modes (the PS2's libmc).
constexpr std::uint16_t kModeFile = 0x0010;
constexpr std::uint16_t kModeDirectory = 0x0020;
constexpr std::uint16_t kModeExists = 0x8000;

constexpr std::uint32_t kChainEnd = 0xFFFFFFFF;
constexpr std::uint32_t kAllocated = 0x80000000;

std::uint16_t U16(const std::uint8_t *at) { return static_cast<std::uint16_t>(at[0] | (at[1] << 8)); }

std::uint32_t U32(const std::uint8_t *at) {
    return static_cast<std::uint32_t>(at[0]) | (static_cast<std::uint32_t>(at[1]) << 8) |
           (static_cast<std::uint32_t>(at[2]) << 16) | (static_cast<std::uint32_t>(at[3]) << 24);
}

// A directory entry: 0x00 mode, 0x04 length (bytes, or entries for a directory), 0x10 first cluster, 0x40 name.
struct Entry {
    std::uint16_t mode = 0;
    std::uint32_t length = 0;
    std::uint32_t cluster = 0;
    std::string   name;

    [[nodiscard]] bool Exists() const { return (mode & kModeExists) != 0; }

    [[nodiscard]] bool Directory() const { return (mode & kModeDirectory) != 0; }

    [[nodiscard]] bool File() const { return (mode & kModeFile) != 0; }
};

Entry ReadEntry(const std::uint8_t *at) {
    Entry       entry{.mode = U16(at), .length = U32(at + 0x04), .cluster = U32(at + 0x10)};
    const auto *name = reinterpret_cast<const char *>(at + 0x40);
    entry.name.assign(name, std::find(name, name + 32, '\0'));
    return entry;
}

// A save's name is one path component.
bool PlainName(const std::string &name) {
    return !name.empty() && name != "." && name != ".." && name.find_first_of("/\\:") == std::string::npos;
}

std::string Utf8(const fs::path &path) {
    const auto text = path.u8string();
    return {text.begin(), text.end()};
}

bool ReadFile(const fs::path &path, std::vector<std::uint8_t> &bytes) {
    std::error_code error;
    const auto      size = fs::file_size(path, error);
    if (error || size > kMaxSource) {
        return false;
    }
    std::ifstream file(path, std::ios::binary);
    bytes.resize(static_cast<std::size_t>(size));
    return file && file.read(reinterpret_cast<char *>(bytes.data()), static_cast<std::streamsize>(bytes.size()));
}

// PCSX2's memory card: the PS2's file system, a page at a time with or without the 16 ECC bytes after each 512.
class Card {
public:
    explicit Card(const std::vector<std::uint8_t> &bytes) : bytes_(bytes) {}

    bool Open() {
        if (bytes_.size() < 0x154 || std::memcmp(bytes_.data(), kCardMagic.data(), kCardMagic.size()) != 0) {
            return false;
        }
        const std::uint8_t *super = bytes_.data();
        page_size_ = U16(super + 0x28);
        pages_per_cluster_ = U16(super + 0x2A);
        clusters_ = U32(super + 0x30);
        alloc_offset_ = U32(super + 0x34);
        alloc_end_ = U32(super + 0x38);
        root_ = U32(super + 0x3C);
        for (int i = 0; i < 32; ++i) {
            indirect_[i] = U32(super + 0x50 + i * 4);
        }
        if (page_size_ < kEntrySize || page_size_ > 0x2000 || pages_per_cluster_ == 0 || pages_per_cluster_ > 16 ||
            clusters_ == 0 || alloc_end_ == 0) {
            return false;
        }
        cluster_size_ = std::size_t{page_size_} * pages_per_cluster_;
        // The ECC bytes, 4 for each 128 of a page, are there when the file is large enough to hold them.
        const std::size_t pages = std::size_t{clusters_} * pages_per_cluster_;
        const std::size_t spare = (page_size_ + 127) / 128 * 4;
        raw_page_ = bytes_.size() >= pages * (page_size_ + spare) ? page_size_ + spare : page_size_;
        return bytes_.size() >= pages * page_size_;
    }

    // The root directory's entries.
    std::optional<std::vector<Entry> > Root() {
        auto data = Chain(root_, std::nullopt);
        if (!data || data->size() < kEntrySize) {
            return std::nullopt;
        }
        // The root's "." entry counts the root's entries.
        return Entries(*data, ReadEntry(data->data()).length);
    }

    std::optional<std::vector<Entry> > Directory(const Entry &directory) {
        auto data = Chain(directory.cluster, std::nullopt);
        if (!data) {
            return std::nullopt;
        }
        return Entries(*data, directory.length);
    }

    std::optional<std::vector<std::uint8_t> > File(const Entry &file) {
        if (file.length == 0) {
            return std::vector<std::uint8_t>{};
        }
        return Chain(file.cluster, file.length);
    }

private:
    // One cluster by its number on the card.
    bool Cluster(std::uint32_t number, std::uint8_t *to) const {
        if (number >= clusters_) {
            return false;
        }
        for (std::size_t page = 0; page < pages_per_cluster_; ++page) {
            const std::size_t at = (std::size_t{number} * pages_per_cluster_ + page) * raw_page_;
            if (at + page_size_ > bytes_.size()) {
                return false;
            }
            std::memcpy(to + page * page_size_, bytes_.data() + at, page_size_);
        }
        return true;
    }

    // The FAT's entry for allocatable cluster n, through the indirect FAT clusters the superblock lists.
    std::optional<std::uint32_t> Fat(std::uint32_t n) {
        const std::size_t per_cluster = cluster_size_ / 4;
        const std::size_t fat_cluster = n / per_cluster;
        const std::size_t indirect = fat_cluster / per_cluster;
        if (indirect >= 32) {
            return std::nullopt;
        }
        std::vector<std::uint8_t> buffer(cluster_size_);
        if (!Cluster(indirect_[indirect], buffer.data())) {
            return std::nullopt;
        }
        const std::uint32_t fat = U32(buffer.data() + (fat_cluster % per_cluster) * 4);
        if (!Cluster(fat, buffer.data())) {
            return std::nullopt;
        }
        return U32(buffer.data() + (n % per_cluster) * 4);
    }

    // The clusters of a chain from its first, allocatable, cluster: length bytes of it, or all of it.
    std::optional<std::vector<std::uint8_t> > Chain(std::uint32_t first, std::optional<std::size_t> length) {
        std::vector<std::uint8_t> data;
        std::set<std::uint32_t>   seen;
        std::uint32_t             cluster = first;
        while (true) {
            if (cluster >= alloc_end_ || !seen.insert(cluster).second) {
                return std::nullopt;
            }
            const std::size_t at = data.size();
            data.resize(at + cluster_size_);
            if (!Cluster(alloc_offset_ + cluster, data.data() + at)) {
                return std::nullopt;
            }
            if (length && data.size() >= *length) {
                data.resize(*length);
                return data;
            }
            const auto next = Fat(cluster);
            if (!next) {
                return std::nullopt;
            }
            if (*next == kChainEnd) {
                break;
            }
            if ((*next & kAllocated) == 0) {
                return std::nullopt;
            }
            cluster = *next & ~kAllocated;
        }
        if (length) {
            return std::nullopt; // The chain ended before the file did.
        }
        return data;
    }

    static std::optional<std::vector<Entry> > Entries(const std::vector<std::uint8_t> &data, std::uint32_t count) {
        if (count > data.size() / kEntrySize) {
            return std::nullopt;
        }
        std::vector<Entry> entries;
        for (std::size_t i = 0; i < count; ++i) {
            entries.push_back(ReadEntry(data.data() + i * kEntrySize));
        }
        return entries;
    }

    const std::vector<std::uint8_t> &bytes_;
    std::uint16_t                    page_size_ = 0;
    std::uint16_t                    pages_per_cluster_ = 0;
    std::uint32_t                    clusters_ = 0;
    std::uint32_t                    alloc_offset_ = 0;
    std::uint32_t                    alloc_end_ = 0;
    std::uint32_t                    root_ = 0;
    std::uint32_t                    indirect_[32] = {};
    std::size_t                      cluster_size_ = 0;
    std::size_t                      raw_page_ = 0;
};

bool LooksLikePsu(const std::vector<std::uint8_t> &psu) {
    if (psu.size() < kEntrySize * 3) {
        return false;
    }
    const Entry directory = ReadEntry(psu.data());
    const Entry dot = ReadEntry(psu.data() + kEntrySize);
    const Entry dotdot = ReadEntry(psu.data() + kEntrySize * 2);
    return directory.Directory() && dot.Directory() && dotdot.Directory() && directory.length >= 2 &&
           dot.name == "." && dotdot.name == "..";
}

// A save folder on disk: its files, not its subfolders.
Ps2CardResult ReadFolder(const fs::path &path, std::vector<Ps2Folder> &folders) {
    Ps2Folder       folder{.name = Utf8(path.filename())};
    std::error_code error;
    for (const auto &entry : fs::directory_iterator(path, error)) {
        std::error_code kind;
        if (!entry.is_regular_file(kind)) {
            continue;
        }
        Ps2File file{.name = Utf8(entry.path().filename())};
        if (!ReadFile(entry.path(), file.bytes)) {
            return Ps2CardResult::Unreadable;
        }
        folder.files.push_back(std::move(file));
    }
    if (error) {
        return Ps2CardResult::Unreadable;
    }
    folders.push_back(std::move(folder));
    return Ps2CardResult::Read;
}

Ps2CardResult ReadSourceFile(const fs::path &path, const Ps2FolderFilter &wanted, std::vector<Ps2Folder> &folders) {
    std::vector<std::uint8_t> bytes;
    if (!ReadFile(path, bytes)) {
        return Ps2CardResult::Unreadable;
    }
    if (bytes.size() >= kCardMagic.size() && std::memcmp(bytes.data(), kCardMagic.data(), kCardMagic.size()) == 0) {
        return Ps2ReadCard(bytes, wanted, folders);
    }
    return Ps2ReadPsu(bytes, wanted, folders);
}

} // namespace

Ps2CardResult Ps2ReadCard(const std::vector<std::uint8_t> &card, const Ps2FolderFilter &wanted,
                          std::vector<Ps2Folder> &folders) {
    Card reader(card);
    if (!reader.Open()) {
        return Ps2CardResult::Unknown;
    }
    const auto root = reader.Root();
    if (!root) {
        return Ps2CardResult::Damaged;
    }
    for (const Entry &entry : *root) {
        if (!entry.Exists() || !entry.Directory() || !PlainName(entry.name) || !wanted(entry.name)) {
            continue;
        }
        const auto entries = reader.Directory(entry);
        if (!entries) {
            return Ps2CardResult::Damaged;
        }
        Ps2Folder folder{.name = entry.name};
        for (const Entry &file : *entries) {
            if (!file.Exists() || !file.File() || !PlainName(file.name)) {
                continue;
            }
            auto bytes = reader.File(file);
            if (!bytes) {
                return Ps2CardResult::Damaged;
            }
            folder.files.push_back({.name = file.name, .bytes = std::move(*bytes)});
        }
        folders.push_back(std::move(folder));
    }
    return Ps2CardResult::Read;
}

Ps2CardResult Ps2ReadPsu(const std::vector<std::uint8_t> &psu, const Ps2FolderFilter &wanted,
                         std::vector<Ps2Folder> &folders) {
    if (!LooksLikePsu(psu)) {
        return Ps2CardResult::Unknown;
    }
    const Entry directory = ReadEntry(psu.data());
    if (!PlainName(directory.name)) {
        return Ps2CardResult::Unknown;
    }
    if (!wanted(directory.name)) {
        return Ps2CardResult::Read;
    }
    // After the folder's entry, "." and "..", each file's entry and its bytes, padded to a whole 1 KB cluster.
    constexpr std::size_t kCluster = 1024;
    Ps2Folder             folder{.name = directory.name};
    std::size_t           at = kEntrySize * 3;
    for (std::uint32_t i = 2; i < directory.length; ++i) {
        if (psu.size() - at < kEntrySize) {
            return Ps2CardResult::Damaged;
        }
        const Entry file = ReadEntry(psu.data() + at);
        at += kEntrySize;
        if (!file.File() || psu.size() - at < file.length) {
            return Ps2CardResult::Damaged;
        }
        if (PlainName(file.name)) {
            folder.files.push_back({.name = file.name,
                                    .bytes = std::vector<std::uint8_t>(psu.begin() + static_cast<std::ptrdiff_t>(at),
                                                                       psu.begin() +
                                                                           static_cast<std::ptrdiff_t>(at + file.length))});
        }
        at += (file.length + kCluster - 1) / kCluster * kCluster;
        at = std::min(at, psu.size());
    }
    folders.push_back(std::move(folder));
    return Ps2CardResult::Read;
}

Ps2CardResult Ps2ReadSource(const fs::path &source, const Ps2FolderFilter &wanted, std::vector<Ps2Folder> &folders) {
    std::error_code error;
    if (fs::is_regular_file(source, error)) {
        return ReadSourceFile(source, wanted, folders);
    }
    if (!fs::is_directory(source, error)) {
        return Ps2CardResult::Unreadable;
    }
    // A save folder holds a file of its own name.
    const std::string name = Utf8(source.filename());
    if (fs::is_regular_file(source / source.filename(), error)) {
        return wanted(name) ? ReadFolder(source, folders) : Ps2CardResult::Read;
    }
    // Otherwise a folder of them: PCSX2's folder memory card, or saves and cards kept together.
    bool known = false;
    for (const auto &entry : fs::directory_iterator(source, error)) {
        std::error_code   kind;
        const std::string child = Utf8(entry.path().filename());
        if (entry.is_directory(kind)) {
            if (fs::is_regular_file(entry.path() / entry.path().filename(), kind)) {
                known = true;
                if (wanted(child) && ReadFolder(entry.path(), folders) != Ps2CardResult::Read) {
                    return Ps2CardResult::Unreadable;
                }
            }
            continue;
        }
        std::string extension = Utf8(entry.path().extension());
        std::ranges::transform(extension, extension.begin(), [](unsigned char c) { return static_cast<char>(std::tolower(c)); });
        if (extension == ".ps2" || extension == ".psu") {
            const Ps2CardResult result = ReadSourceFile(entry.path(), wanted, folders);
            if (result == Ps2CardResult::Unreadable || result == Ps2CardResult::Damaged) {
                return result;
            }
            known |= result == Ps2CardResult::Read;
        }
    }
    if (error) {
        return Ps2CardResult::Unreadable;
    }
    return known ? Ps2CardResult::Read : Ps2CardResult::Unknown;
}

} // namespace launcher
