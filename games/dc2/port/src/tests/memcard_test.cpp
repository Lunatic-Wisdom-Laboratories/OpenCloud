#include <gtest/gtest.h>

#include <algorithm>
#include <cstddef>
#include <filesystem>
#include <string>
#include <vector>

#include "memcard.hpp"
#include "platform/paths.hpp"
#include "platform_fixture.hpp"

namespace fs = std::filesystem;

void MakeMemoryCardFileName(int slot, char *path);
void MakeMemoryCardAlbumName(char *name, int append_again);

namespace {

constexpr int kOpenRead = 0x0001;
constexpr int kOpenCreateWrite = 0x0202;
constexpr int kSeekSet = 0;
constexpr char kCardDir[] = "/BESCES-51190dkcl0";
constexpr char kCardFile[] = "/BESCES-51190dkcl0/BESCES-51190dkcl0";
constexpr char kCardFilter[] = "/BESCES-51190dkcl0/*";
constexpr std::size_t kImageSize = sizeof(SAVEDATA_FORMAT);
constexpr int kIncompleteOffset = 0x45;
constexpr std::size_t kWriteChunk = 0xC00;
constexpr int kReadChunk = 0x1000;

std::vector<unsigned char> Image() {
    std::vector<unsigned char> image(kImageSize);
    for (std::size_t i = 0; i < image.size(); ++i) {
        image[i] = static_cast<unsigned char>(i * 131 + (i >> 8) * 7);
    }
    return image;
}

// The port finishes each command inside the call that issues it, so the game's
// sceMcSync(MC_NOWAIT) poll reports 1 with the command's function number and result.
int Done(int function) {
    int cmd = -1;
    int result = 0;
    EXPECT_EQ(sceMcSync(MC_NOWAIT, &cmd, &result), 1);
    EXPECT_EQ(cmd, function);
    return result;
}

int Open(const std::string &name, int flags) {
    EXPECT_EQ(sceMcOpen(0, 1, name.c_str(), flags), 0);
    return Done(sceMcFuncNoOpen);
}

void WriteAll(int fd, const std::vector<unsigned char> &image) {
    for (std::size_t offset = 0; offset < image.size(); offset += kWriteChunk) {
        const int chunk = static_cast<int>(std::min(kWriteChunk, image.size() - offset));
        ASSERT_EQ(sceMcWrite(fd, const_cast<unsigned char *>(image.data()) + offset, chunk), 0);
        ASSERT_EQ(Done(sceMcFuncNoWrite), chunk);
    }
}

void ReadAll(int fd, std::vector<unsigned char> &out) {
    std::vector<unsigned char> buffer(kReadChunk);
    out.clear();
    for (;;) {
        ASSERT_EQ(sceMcRead(fd, buffer.data(), kReadChunk), 0);
        const int got = Done(sceMcFuncNoRead);
        ASSERT_GE(got, 0);
        out.insert(out.end(), buffer.begin(), buffer.begin() + got);
        if (got < kReadChunk) {
            return;
        }
    }
}

void Close(int fd) {
    ASSERT_EQ(sceMcClose(fd), 0);
    ASSERT_EQ(Done(sceMcFuncNoClose), 0);
}

void MakeCardDirectory() {
    ASSERT_EQ(sceMcMkdir(0, 1, kCardDir), 0);
    ASSERT_EQ(Done(sceMcFuncNoMkdir), 0);
}

void RunUntilIdle(CMemoryCardManager &manager, int function) {
    manager.SetFuncNo(function);
    for (int i = 0; i < 64 && manager.GetFuncNo() != MC_FUNC_IDLE; ++i) {
        manager.Step();
    }
    ASSERT_EQ(manager.GetFuncNo(), MC_FUNC_IDLE);
}

class Memcard : public testing::Test {
protected:
    void SetUp() override {
        root_ = fs::temp_directory_path() / ("dc_memcard_test_" + std::to_string(dc::test::ProcessId()));
        fs::remove_all(root_);
        fs::create_directories(root_);
        PathsSetSaveRoot(root_);
        ASSERT_EQ(sceMcInit(), sceMcIniSucceed);
    }

    void TearDown() override {
        PathsReset();
        std::error_code error;
        fs::remove_all(root_, error);
    }

private:
    fs::path root_;
};

} // namespace

TEST_F(Memcard, CardNamesAreTheGamesDirectoryAndFile) {
    char path[0x80];
    MakeMemoryCardFileName(0, path);
    EXPECT_STREQ(path, kCardFile);
    MakeMemoryCardFileName(1, path);
    EXPECT_STREQ(path, "/BESCES-51190dkcl1/BESCES-51190dkcl1");

    char album[0x40];
    MakeMemoryCardAlbumName(album, 0);
    EXPECT_STREQ(album, "/BESCES-51190dc2album");
    MakeMemoryCardAlbumName(album, 1);
    EXPECT_STREQ(album, "/BESCES-51190dc2album/BESCES-51190dc2album");
}

TEST_F(Memcard, SaveImageReadsBackAndIsListedWithItsSize) {
    int type = 0;
    int free_size = 0;
    int formatted = 0;
    ASSERT_EQ(sceMcGetInfo(0, 1, &type, &free_size, &formatted), 0);
    ASSERT_EQ(Done(sceMcFuncNoCardInfo), sceMcResSucceed);
    EXPECT_EQ(type, sceMcTypePS2);
    EXPECT_EQ(formatted, 1);
    EXPECT_GT(free_size * 1024, static_cast<int>(kImageSize));

    MakeCardDirectory();
    int fd = Open(kCardFile, kOpenCreateWrite);
    ASSERT_GE(fd, 0);
    const std::vector<unsigned char> image = Image();
    WriteAll(fd, image);
    ASSERT_EQ(sceMcFlush(fd), 0);
    ASSERT_EQ(Done(sceMcFuncNoFlush), 0);
    Close(fd);

    sceMcTblGetDir table[MC_DIR_TABLE_NUM] = {};
    ASSERT_EQ(sceMcGetDir(0, 1, kCardFilter, 0, MC_DIR_TABLE_NUM, table), 0);
    const int entries = Done(sceMcFuncNoGetDir);
    ASSERT_EQ(entries, 3);
    const sceMcTblGetDir *save = nullptr;
    for (int i = 0; i < entries; ++i) {
        if (std::string(table[i].entry_name) == "BESCES-51190dkcl0") {
            save = &table[i];
        }
    }
    ASSERT_NE(save, nullptr);
    EXPECT_EQ(save->file_size_byte, kImageSize);
    EXPECT_NE(save->attr_file & 0x0010, 0);

    fd = Open(kCardFile, kOpenRead);
    ASSERT_GE(fd, 0);
    std::vector<unsigned char> back;
    ReadAll(fd, back);
    Close(fd);
    EXPECT_EQ(back, image);
}

// The save finish sets the incomplete byte back to zero through the descriptor that wrote the file,
// and a file that still has it set is hidden from the load menu.
TEST_F(Memcard, SaveFinishClearsIncompleteByteAtOffset0x45) {
    MakeCardDirectory();
    int fd = Open(kCardFile, kOpenCreateWrite);
    ASSERT_GE(fd, 0);
    std::vector<unsigned char> image = Image();
    image[kIncompleteOffset] = 1;
    WriteAll(fd, image);

    ASSERT_EQ(sceMcSeek(fd, kIncompleteOffset, kSeekSet), 0);
    Done(sceMcFuncNoSeek);
    unsigned char cleared = 0;
    ASSERT_EQ(sceMcWrite(fd, &cleared, 1), 0);
    ASSERT_EQ(Done(sceMcFuncNoWrite), 1);
    Close(fd);

    image[kIncompleteOffset] = 0;
    fd = Open(kCardFile, kOpenRead);
    ASSERT_GE(fd, 0);
    std::vector<unsigned char> back;
    ReadAll(fd, back);
    Close(fd);
    ASSERT_EQ(back.size(), kImageSize);
    EXPECT_EQ(back[kIncompleteOffset], 0);
    EXPECT_EQ(back, image);
}

TEST_F(Memcard, ManagerSearchReadsFormattedCard) {
    CMemoryCardManager manager;
    manager.port = 0;
    RunUntilIdle(manager, MC_FUNC_SEARCH_TYPE);

    EXPECT_EQ(manager.error.code, MC_ERROR_NONE);
    EXPECT_EQ(manager.card[0].present, 1);
    EXPECT_EQ(manager.card[0].type, sceMcTypePS2);
    EXPECT_EQ(manager.card[0].formatted, 1);
    EXPECT_EQ(manager.card[0].format_change, 1);
    EXPECT_GT(manager.card[0].free_size * 1024, static_cast<int>(kImageSize));
}

TEST_F(Memcard, ManagerFormatAndUnformatReportCardState) {
    CMemoryCardManager manager;
    manager.port = 0;

    RunUntilIdle(manager, MC_FUNC_FORMAT);
    EXPECT_EQ(manager.error.code, MC_ERROR_NONE);
    EXPECT_EQ(manager.card[0].present, 1);
    EXPECT_EQ(manager.card[0].type, sceMcTypePS2);
    EXPECT_EQ(manager.card[0].formatted, 1);

    RunUntilIdle(manager, MC_FUNC_UNFORMAT);
    EXPECT_EQ(manager.error.code, MC_ERROR_NONE);
    EXPECT_EQ(manager.card[0].formatted, 0);

    RunUntilIdle(manager, MC_FUNC_SEARCH_TYPE);
    EXPECT_EQ(manager.card[0].present, 1);
    EXPECT_EQ(manager.card[0].formatted, 0);
    EXPECT_EQ(manager.card[0].result, sceMcResNoFormat);
}
