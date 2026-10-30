#pragma once

// The files a PS2 wrote to its memory card hold the save image and the photo album in the PS2's layout. It is the
// host's but for USER_PICTURE_INFO's image pointer: four bytes at the end of the PS2's 0x18-byte record, eight after
// padding here. Everything ahead of the first photo is laid out alike, so the photos sit at the same offset in both.

#include <cstddef>
#include <cstring>
#include <span>

#include "userdata.hpp"

/** Size of USER_PICTURE_INFO on the PS2. */
inline constexpr std::size_t kPs2PictureSize = 0x18;
/** The record's fields ahead of its image pointer, laid out alike on both. */
inline constexpr std::size_t kPs2PictureFields = 0x14;
static_assert(offsetof(USER_PICTURE_INFO, unk_c) + sizeof(USER_PICTURE_INFO::unk_c) == kPs2PictureFields);

/** Size of SAVEDATA_FORMAT and of CDC2AlbumData on the PS2. */
inline constexpr std::size_t kPs2SaveImageSize = 0x659C0;
inline constexpr std::size_t kPs2AlbumSize = 0x64CB0;

/** The host's size of a record whose PS2 size is ps2_size and which holds count photos. */
constexpr std::size_t Ps2HostSize(std::size_t ps2_size, std::size_t count) {
    return ps2_size + count * (sizeof(USER_PICTURE_INFO) - kPs2PictureSize);
}

/**
 * Lays a PS2 record out as the host's: the bytes ahead of the count photos at offset photos, each photo's fields with
 * a null image, then the rest. host must be Ps2HostSize(ps2.size(), count) bytes.
 */
inline void Ps2PicturesToHost(std::span<const unsigned char> ps2, std::span<unsigned char> host, std::size_t photos,
                              std::size_t count) {
    const std::size_t ps2_tail = photos + count * kPs2PictureSize;
    const std::size_t host_tail = photos + count * sizeof(USER_PICTURE_INFO);
    std::memcpy(host.data(), ps2.data(), photos);
    for (std::size_t i = 0; i < count; ++i) {
        unsigned char *to = host.data() + photos + i * sizeof(USER_PICTURE_INFO);
        std::memset(to, 0, sizeof(USER_PICTURE_INFO));
        std::memcpy(to, ps2.data() + photos + i * kPs2PictureSize, kPs2PictureFields);
    }
    std::memcpy(host.data() + host_tail, ps2.data() + ps2_tail, ps2.size() - ps2_tail);
}
