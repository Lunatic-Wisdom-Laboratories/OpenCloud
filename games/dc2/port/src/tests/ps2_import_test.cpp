#include <gtest/gtest.h>

#include <cstdint>
#include <cstring>
#include <memory>
#include <string>
#include <vector>

#include "launcher/ps2_card.hpp"
#include "ps2_save_layout.hpp"
#include "save_json.hpp"
#include "save_slots.hpp"
#include "userdata.hpp"

namespace {

using Bytes = std::vector<std::uint8_t>;
using launcher::Ps2CardResult;
using launcher::Ps2Folder;

bool All(const std::string &) { return true; }

void Put16(Bytes &bytes, std::size_t at, std::uint16_t value) {
    bytes[at] = static_cast<std::uint8_t>(value);
    bytes[at + 1] = static_cast<std::uint8_t>(value >> 8);
}

void Put32(Bytes &bytes, std::size_t at, std::uint32_t value) {
    for (int i = 0; i < 4; ++i) {
        bytes[at + i] = static_cast<std::uint8_t>(value >> (8 * i));
    }
}

void PutEntry(Bytes &bytes, std::size_t at, std::uint16_t mode, std::uint32_t length, std::uint32_t cluster,
              const std::string &name) {
    Put16(bytes, at, mode);
    Put32(bytes, at + 0x04, length);
    Put32(bytes, at + 0x10, cluster);
    std::memcpy(bytes.data() + at + 0x40, name.data(), name.size());
}

Bytes Pattern(std::size_t size, std::uint8_t seed) {
    Bytes bytes(size);
    for (std::size_t i = 0; i < size; ++i) {
        bytes[i] = static_cast<std::uint8_t>(seed + i * 7);
    }
    return bytes;
}

constexpr std::uint16_t kDirectory = 0x8427;
constexpr std::uint16_t kFile = 0x8497;

// A small card: 512-byte pages, two to a cluster, the FAT and its indirect cluster ahead of the allocatable clusters,
// a root holding one save folder whose file spans three clusters out of order.
Bytes MakeCard(bool ecc, const Bytes &payload) {
    constexpr std::size_t kPage = 512, kClusterSize = 1024, kClusters = 64, kAllocOffset = 8;
    const std::size_t     raw_page = ecc ? kPage + 16 : kPage;
    Bytes                 clusters(kClusters * kClusterSize, 0xFF);
    auto                  cluster = [&](std::size_t n) { return n * kClusterSize; };
    std::memset(clusters.data(), 0, kClusterSize);
    std::memcpy(clusters.data(), "Sony PS2 Memory Card Format 1.2.0.0", 35);
    Put16(clusters, 0x28, kPage);
    Put16(clusters, 0x2A, 2);
    Put16(clusters, 0x2C, 16);
    Put32(clusters, 0x30, kClusters);
    Put32(clusters, 0x34, kAllocOffset);
    Put32(clusters, 0x38, kClusters - kAllocOffset);
    Put32(clusters, 0x3C, 0); // Root at allocatable cluster 0.
    Put32(clusters, 0x50, 2); // Indirect FAT cluster.
    // The indirect cluster names the FAT cluster, 3.
    std::memset(clusters.data() + cluster(2), 0, kClusterSize);
    Put32(clusters, cluster(2), 3);
    // Root 0 -> end; folder 1 -> end; file 4 -> 2 -> 5 -> end.
    std::memset(clusters.data() + cluster(3), 0xFF, kClusterSize);
    auto fat = [&](std::uint32_t n, std::uint32_t next) { Put32(clusters, cluster(3) + n * 4, next); };
    fat(0, 0xFFFFFFFF);
    fat(1, 0xFFFFFFFF);
    fat(4, 0x80000002);
    fat(2, 0x80000005);
    fat(5, 0xFFFFFFFF);
    const std::size_t root = cluster(kAllocOffset + 0), folder = cluster(kAllocOffset + 1);
    std::memset(clusters.data() + root, 0, kClusterSize);
    PutEntry(clusters, root, kDirectory, 2, 0, ".");
    PutEntry(clusters, root + 512, kDirectory, 2, 1, "BESCES-51190dkcl0");
    std::memset(clusters.data() + folder, 0, kClusterSize);
    PutEntry(clusters, folder, kDirectory, 0, 0, ".");
    PutEntry(clusters, folder + 512, kFile, static_cast<std::uint32_t>(payload.size()), 4, "BESCES-51190dkcl0");
    const std::size_t parts[] = {4, 2, 5};
    for (std::size_t i = 0; i * kClusterSize < payload.size(); ++i) {
        const std::size_t n = std::min(kClusterSize, payload.size() - i * kClusterSize);
        std::memcpy(clusters.data() + cluster(kAllocOffset + parts[i]), payload.data() + i * kClusterSize, n);
    }
    // Lay the clusters out a page at a time, with spare bytes after each when asked.
    Bytes card;
    for (std::size_t page = 0; page < kClusters * 2; ++page) {
        card.insert(card.end(), clusters.begin() + page * kPage, clusters.begin() + (page + 1) * kPage);
        card.resize(card.size() + raw_page - kPage, 0);
    }
    return card;
}

TEST(Ps2Card, ReadsAFolderAndItsFileThroughTheFat) {
    const Bytes payload = Pattern(2500, 3);
    for (const bool ecc : {false, true}) {
        std::vector<Ps2Folder> folders;
        ASSERT_EQ(launcher::Ps2ReadCard(MakeCard(ecc, payload), All, folders), Ps2CardResult::Read);
        ASSERT_EQ(folders.size(), 1u);
        EXPECT_EQ(folders[0].name, "BESCES-51190dkcl0");
        ASSERT_EQ(folders[0].files.size(), 1u);
        EXPECT_EQ(folders[0].files[0].name, "BESCES-51190dkcl0");
        EXPECT_EQ(folders[0].files[0].bytes, payload);
    }
}

TEST(Ps2Card, SkipsFoldersTheFilterRefuses) {
    std::vector<Ps2Folder> folders;
    EXPECT_EQ(launcher::Ps2ReadCard(MakeCard(false, Pattern(100, 1)), [](const std::string &) { return false; }, folders), Ps2CardResult::Read);
    EXPECT_TRUE(folders.empty());
}

TEST(Ps2Card, RefusesACardWhoseChainRunsOut) {
    Bytes card = MakeCard(false, Pattern(2500, 3));
    // The FAT entry of cluster 2 now ends the file one cluster early.
    Put32(card, 3 * 1024 + 2 * 4, 0xFFFFFFFF);
    std::vector<Ps2Folder> folders;
    EXPECT_EQ(launcher::Ps2ReadCard(card, All, folders), Ps2CardResult::Damaged);
}

TEST(Ps2Card, ReadsAPsuExport) {
    const Bytes data = Pattern(1500, 9), icon = Pattern(10, 1);
    Bytes       psu(512 * 3, 0);
    PutEntry(psu, 0, kDirectory, 4, 0, "BESCES-51190dkcl2");
    PutEntry(psu, 512, kDirectory, 0, 0, ".");
    PutEntry(psu, 1024, kDirectory, 0, 0, "..");
    for (const auto &[name, bytes] : {
             std::pair{std::string("BESCES-51190dkcl2"), data},
             std::pair{std::string("icon.sys"),          icon}
    }) {
        const std::size_t at = psu.size();
        psu.resize(at + 512, 0);
        PutEntry(psu, at, kFile, static_cast<std::uint32_t>(bytes.size()), 0, name);
        psu.insert(psu.end(), bytes.begin(), bytes.end());
        psu.resize(psu.size() + (1024 - bytes.size() % 1024) % 1024, 0);
    }
    std::vector<Ps2Folder> folders;
    ASSERT_EQ(launcher::Ps2ReadPsu(psu, All, folders), Ps2CardResult::Read);
    ASSERT_EQ(folders.size(), 1u);
    EXPECT_EQ(folders[0].name, "BESCES-51190dkcl2");
    ASSERT_EQ(folders[0].files.size(), 2u);
    EXPECT_EQ(folders[0].files[0].bytes, data);
    EXPECT_EQ(folders[0].files[1].bytes, icon);
    std::vector<Ps2Folder> none;
    EXPECT_EQ(launcher::Ps2ReadPsu(Pattern(4096, 0), All, none), Ps2CardResult::Unknown);
}

int Digit(const std::uint8_t *data, std::size_t size) {
    int sum = 0;
    for (std::size_t i = 0; i < size / 64; ++i) {
        sum += data[i * 64] % 255;
    }
    return sum;
}

class Ps2SaveTest : public testing::Test {
protected:
    std::unique_ptr<SAVEDATA_FORMAT> host = std::make_unique<SAVEDATA_FORMAT>();
    std::unique_ptr<SaveDocument>    document = std::make_unique<SaveDocument>();

    CInventUserData &Invent(SAVEDATA_FORMAT &image) { return *image.save_data.GetUserDataManager()->GetInventUserData(); }

    // The host image as a PS2 would have written it: each photo's pointer cut to four bytes, then the digits.
    Bytes ToPs2() {
        std::memcpy(host->version, "dc2Ver4", 8);
        const auto *bytes = reinterpret_cast<const std::uint8_t *>(host.get());
        const auto  photos = static_cast<std::size_t>(reinterpret_cast<const std::uint8_t *>(Invent(*host).photo) - bytes);
        constexpr std::size_t kPhotos = std::extent_v<decltype(CInventUserData::photo)>;
        Bytes                 ps2(kPs2SaveImageSize, 0);
        std::memcpy(ps2.data(), bytes, photos);
        for (std::size_t i = 0; i < kPhotos; ++i) {
            std::memcpy(ps2.data() + photos + i * kPs2PictureSize, bytes + photos + i * sizeof(USER_PICTURE_INFO),
                        kPs2PictureFields);
        }
        const std::size_t tail = photos + kPhotos * kPs2PictureSize;
        std::memcpy(ps2.data() + tail, bytes + photos + kPhotos * sizeof(USER_PICTURE_INFO), ps2.size() - tail);
        const std::uint8_t *data = ps2.data() + offsetof(SAVEDATA_FORMAT, save_data);
        Put32(ps2, offsetof(SAVEDATA_FORMAT, check_digit_half), Digit(data, sizeof(CSaveData) / 2));
        Put32(ps2, offsetof(SAVEDATA_FORMAT, check_digit), Digit(data, sizeof(CSaveData)));
        return ps2;
    }
};

TEST_F(Ps2SaveTest, ConvertsThePhotosAndWhatFollowsThem) {
    std::memset(static_cast<void *>(host.get()), 0, sizeof(*host));
    host->unique_counter = 77;
    host->save_data.play_time = 123456;
    host->save_data.game_progress = 9;
    Invent(*host).photo[3].used = 1;
    Invent(*host).photo[3].neta_id = 42;
    Invent(*host).photo[29].map_no = 7;
    Invent(*host).created_item[5].item_id = 321;
    Invent(*host).photo_work[2][10] = 0x7FFF;
    const Bytes ps2 = ToPs2();

    ASSERT_EQ(SaveSlotReadPs2(ps2, *document), SaveSlotState::Ready);
    EXPECT_EQ(document->image.unique_counter, 77u);
    EXPECT_EQ(document->image.save_data.play_time, 123456);
    EXPECT_EQ(document->image.save_data.game_progress, 9);
    EXPECT_EQ(Invent(document->image).photo[3].used, 1);
    EXPECT_EQ(Invent(document->image).photo[3].neta_id, 42);
    EXPECT_EQ(Invent(document->image).photo[3].image, nullptr);
    EXPECT_EQ(Invent(document->image).photo[29].map_no, 7);
    EXPECT_EQ(Invent(document->image).created_item[5].item_id, 321);
    EXPECT_EQ(Invent(document->image).photo_work[2][10], 0x7FFF);
    // It is a save the game reads back.
    auto again = std::make_unique<SaveDocument>();
    EXPECT_EQ(SaveJsonRead(SaveJsonWrite(*document), *again), SaveJsonError::None);
}

TEST_F(Ps2SaveTest, RefusesDamagedAndForeignFiles) {
    std::memset(static_cast<void *>(host.get()), 0, sizeof(*host));
    host->save_data.game_progress = 3;
    // Retail takes zero digits for none, so the image needs sampled bytes that give it some.
    host->save_data.bit_flag[0] = 0x55;
    Bytes ps2 = ToPs2();
    ps2[offsetof(SAVEDATA_FORMAT, save_data) + 64 * 5] ^= 0x10;
    EXPECT_EQ(SaveSlotReadPs2(ps2, *document), SaveSlotState::BadChecksum);
    ps2.pop_back();
    EXPECT_EQ(SaveSlotReadPs2(ps2, *document), SaveSlotState::WrongSize);
    Bytes incomplete = ToPs2();
    incomplete[offsetof(SAVEDATA_FORMAT, incomplete)] = 1;
    EXPECT_EQ(SaveSlotReadPs2(incomplete, *document), SaveSlotState::Incomplete);
}

} // namespace
