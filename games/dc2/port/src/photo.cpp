#include "common.h"

#include <algorithm>
#include <array>
#include <cmath>
#include <cstdint>
#include <cstring>
#include <numbers>
#include <optional>
#include <vector>

#include "inventmn.hpp"
#include "mainloop.hpp"
#include "mg_drawprim.hpp"
#include "mg_math.hpp"
#include "mg_texture.hpp"
#include "mg_texture_port.hpp"
#include "mglib.hpp"
#include "mglib_port.hpp"
#include "padcontrol.hpp"
#include "photo.hpp"
#include "photo_hd.hpp"
#include "snd_mngr.hpp"
#include "stubs/photo.hpp"
#include "userdata.hpp"

namespace {

/**
 * Bounds of the camera view or the captured image inside its texture.
 */
struct PhotoView {
    float left;   /**< Left edge in texture or screen units. */
    float right;  /**< Right edge in texture or screen units. */
    float top;    /**< Top edge in texture or screen units. */
    float bottom; /**< Bottom edge in texture or screen units. */
};

/** The image bounds saved by the latest shutter capture. */
std::optional<PhotoView> captured_area;

/**
 * Returns the visible scene bounds in frame-grab texels.
 */
PhotoView SceneView() {
    const auto extent = mgPortVisibleExtent(gfx::kMainTarget);
    return {.left = extent.left, .right = extent.right, .top = extent.top, .bottom = extent.bottom};
}

/**
 * Returns the viewfinder bounds in UI units.
 */
PhotoView OverlayView() {
    const auto extent = mgPortVisibleUiExtent(gfx::kMainTarget);
    return {.left = extent.left, .right = extent.right, .top = extent.top, .bottom = extent.bottom};
}

/**
 * Fits the scene inside a picture while preserving its aspect relative to the retail screen.
 */
PhotoView PictureArea(const PhotoView &scene, float width, float height) {
    const float aspect = (scene.right - scene.left) * static_cast<float>(mgScreenHeight) /
                         ((scene.bottom - scene.top) * static_cast<float>(mgScreenWidth));
    if (aspect > 1.001f) {
        const float bar = height * (1.0f - 1.0f / aspect) * 0.5f;
        return {.left = 0.0f, .right = width, .top = bar, .bottom = height - bar};
    }
    if (aspect < 0.999f) {
        const float bar = width * (1.0f - aspect) * 0.5f;
        return {.left = bar, .right = width - bar, .top = 0.0f, .bottom = height};
    }
    return {.left = 0.0f, .right = width, .top = 0.0f, .bottom = height};
}

} // namespace

/**
 * Updates photo input, shutter state and frame-boundary capture requests.
 */
PORT_OVERRIDE void LoopTakePhoto(CPadControl *pad, CInventUserData *user) {
    if (user == nullptr) {
        return;
    }
    // Capture at a higher resolution than retail's 64x64; mgStoreImage keeps that detail for the idea board.
    if (WorkTex != nullptr) {
        MgTextureSetPhysicalScale(WorkTex, kPhotoWorkScale);
    }
    if (TakePhotoMode == TAKE_PHOTO_AIM) {
        AddProj -= 10.0f * pad->Analog(PAD_ANALOG_RIGHT_Y);
        if (!(AddProj <= 200.0f)) {
            AddProj = 200.0f;
        }
        AddProj = std::max(AddProj, -200.0f);
        if (user->IsPhotoSpace(nullptr) && pad->Btn(PAD_BTN_ACTION_SQUARE)) {
            TakePhotoMode = TAKE_PHOTO_SHUTTER;
        }
        ShowTakePhotoCnt = std::max(0, ShowTakePhotoCnt - 1);
    }
    if (TakePhotoMode == TAKE_PHOTO_AFTERSHOT && --ShutterAnmCnt < 0) {
        ShutterAnmCnt = 0;
        TakePhotoMode = TAKE_PHOTO_AIM;
    }
    if (TakePhotoMode == TAKE_PHOTO_OPEN_MENU) {
        OpenMenu = 1;
        TakePhotoMode = TAKE_PHOTO_AIM;
    }
    if (TakePhotoMode == TAKE_PHOTO_SHUTTER && WorkTex != nullptr) {
        // Recording defers GPU work to the frame boundary. Request the capture
        // there so the following STORE frame receives this shot, including depth.
        std::vector<uint8_t> ignored;
        uint32_t             width = 0;
        uint32_t             height = 0;
        mgPortReadColor(mgPortRenderTarget(WorkTex), ignored, width, height);
        mgRect<int>               lens(252, 204, 260, 212);
        std::array<u_long128, 16> depth{};
        mgStoreZBuffImage(lens, depth.data());
    }
}

/**
 * Captures the visible scene and draws the viewfinder, shutter and latest-shot preview.
 */
PORT_OVERRIDE int DrawTakePhoto(USER_PICTURE_INFO *picture, float *distance) {
    if (WorkTex == nullptr || NowTakePhoto() == 0) {
        return 0;
    }

    mgCTextureManager *textures = &mgTexManager;
    textures->ReloadTexture(static_cast<int>(CameraTexb), static_cast<sceVif1Packet *>(nullptr));
    std::array<u_long128, 0x240> image{};
    std::array<u_long128, 0x40>  depth{};
    mgCDrawPrim                  prim;
    int                          taken = 0;

    if (TakePhotoMode == TAKE_PHOTO_STORE) {
        mgStoreImage(WorkTex, image.data());
        mgRect<int> area(252, 204, 260, 212);
        const int   count = mgStoreZBuffImage(area, depth.data()) * 4;
        const auto *pixel = reinterpret_cast<const u_int *>(depth.data());
        u_int       nearest = *pixel;
        for (int i = 0; i < count; ++i) {
            nearest = std::max(nearest, pixel[i]);
        }
        *distance = mgConvZBuffToDist(nearest);

        if (picture != nullptr) {
            std::memcpy(picture->image, image.data(), std::size_t{64} * 64 * sizeof(std::uint16_t));
            picture->used = 1;
            picture->is_new = true;
            sndSePlay(GetSystemSndID(), 11, 0);
            taken = 1;
        }

        ShowTakePhotoCnt = 120;
        TakePhotoMode = TAKE_PHOTO_AFTERSHOT;
        ShutterAnmCnt = 8;
    }

    if (TakePhotoMode == TAKE_PHOTO_SHUTTER) {
        const PhotoView scene = SceneView();
        mgCTexture      frame;
        mgGetFrameBuffer(&frame);
        mgCTexture     *target = WorkTex;
        const PhotoView area = PictureArea(scene, static_cast<float>(target->width), static_cast<float>(target->height));
        captured_area = area;
        mgSetPkFrameBuffer(target);
        mgCDrawPrim capture;
        capture.Initialize(nullptr, nullptr);
        capture.AlphaBlendEnable(0);
        capture.DepthTestEnable(0);
        capture.ZMask(MG_Z_MASK_MASKED);
        if (area.left > 0.0f || area.top > 0.0f) {
            capture.TextureMapEnable(0);
            capture.Begin(MG_PRIM_SPRITE);
            capture.Color(0, 0, 0, 128);
            capture.Vertex(0, 0, 0);
            capture.Vertex(target->width, target->height, 0);
            capture.End();
        }
        capture.TextureMapEnable(1);
        capture.Begin(MG_PRIM_SPRITE);
        capture.Texture(&frame);
        capture.Color(128, 128, 128, 128);
        // Texels past the grab's 0..width reach the sides of the frame, which a frame grab keeps.
        capture.TextureCrd4(static_cast<int>(std::lround(scene.left * 16.0f)), static_cast<int>(std::lround(scene.top * 16.0f)));
        capture.Vertex(area.left, area.top, 0.0f);
        capture.TextureCrd4(static_cast<int>(std::lround(scene.right * 16.0f)), static_cast<int>(std::lround(scene.bottom * 16.0f)));
        capture.Vertex(area.right, area.bottom, 0.0f);
        capture.End();
        mgSetPkFrameBuffer(-1, -1, -1, -1);
        TakePhotoMode = TAKE_PHOTO_STORE;
    }

    const PhotoView view = OverlayView();
    const float     left = view.left;
    const float     right = view.right;
    const float     top = view.top;
    const float     bottom = view.bottom;

    prim.Initialize(nullptr, nullptr);
    prim.DepthTestEnable(0);
    prim.AlphaBlendEnable(1);
    prim.AlphaBlend(MG_ALPHA_BLEND_NORMAL);
    prim.TextureMapEnable(1);
    prim.ZMask(MG_Z_MASK_MASKED);
    prim.Begin(MG_PRIM_SPRITE);
    prim.Color(255, 255, 255, 128);
    char camera_name[] = "camera";
    prim.Texture(textures->GetTexture(camera_name, -1));
    const int center_x = mgScreenWidth / 2;
    const int center_y = mgScreenHeight / 2;
    for (const int sx : {-1, 1}) {
        for (const int sy : {-1, 1}) {
            prim.TextureCrd(0x40, 1);
            prim.Vertex(center_x + sx * 0x40, center_y + sy * 0x40, 0);
            prim.TextureCrd(0x80, 0x40);
            prim.Vertex(center_x, center_y, 0);
        }
    }
    for (const int sx : {-1, 1}) {
        for (const int sy : {-1, 1}) {
            prim.TextureCrd(0, 0);
            prim.Vertex(center_x + sx * 0xD4, center_y + sy * 0xC4, 0);
            prim.TextureCrd(0x40, 0x40);
            prim.Vertex(center_x + sx * 0x94, center_y + sy * 0x84, 0);
        }
    }
    prim.End();
    prim.TextureMapEnable(0);
    prim.AlphaBlend(MG_ALPHA_BLEND_NORMAL);
    prim.AntiAliasing(1);

    if (TakePhotoMode == TAKE_PHOTO_AFTERSHOT) {
        const int     frame = 8 - ShutterAnmCnt;
        sceVu0FVECTOR center = {(left + right) * 0.5f, (top + bottom) * 0.5f, 0.0f, 1.0f};
        const float   radius = std::hypot((right - left) * 0.5f, (bottom - top) * 0.5f);
        float         shutter_angle = (HALF_PI * static_cast<float>(frame - 1)) / 4.0f;

        if (shutter_angle > HALF_PI) {
            shutter_angle = std::numbers::pi_v<float> - shutter_angle;
        }

        for (int i = 0; i < 12; i++) {
            const float   angle = mgAngleLimit(std::numbers::pi_v<float> + static_cast<float>(i) * (std::numbers::pi_v<float> / 6.0f));
            sceVu0FVECTOR position;
            sceVu0FVECTOR edge;
            sceVu0FVECTOR rotated;
            sceVu0FMATRIX matrix;
            mgZeroVector(position);
            mgZeroVector(edge);
            position[1] = -radius;
            edge[0] = 1.1f * radius;
            mgUnitMatrix(matrix);
            sceVu0RotMatrixZ(matrix, matrix, angle);
            sceVu0ApplyMatrix(position, matrix, position);
            sceVu0ApplyMatrix(edge, matrix, edge);
            mgAddVector(position, center);
            mgUnitMatrix(matrix);
            sceVu0RotMatrixZ(matrix, matrix, shutter_angle);
            sceVu0ApplyMatrix(rotated, matrix, edge);
            prim.Begin(MG_PRIM_TRIANGLE);
            prim.Color(0, 0, 0, 128);
            prim.Vertex(position[0], position[1], 0.0f);
            prim.Vertex(position[0] + edge[0], position[1] + edge[1], 0.0f);
            prim.Vertex(position[0] + rotated[0], position[1] + rotated[1], 0.0f);
            prim.End();
            prim.Begin(MG_PRIM_LINE);
            prim.Color(64, 64, 64, 64);
            prim.Vertex(position[0], position[1], 0.0f);
            prim.Vertex(position[0] + rotated[0], position[1] + rotated[1], 0.0f);
            prim.End();
        }
    }

    prim.TextureMapEnable(0);
    prim.AlphaBlend(MG_ALPHA_BLEND_NORMAL);
    prim.AntiAliasing(1);
    prim.Begin(MG_PRIM_SPRITE);
    prim.Color(0, 0, 0, 128);
    prim.Vertex(left, top, 0.0f);
    prim.Vertex(left + 16.0f, bottom, 0.0f);
    prim.Vertex(right - 16.0f, top, 0.0f);
    prim.Vertex(right, bottom, 0.0f);
    prim.Vertex(left, top, 0.0f);
    prim.Vertex(right, top + 16.0f, 0.0f);
    prim.Vertex(left, bottom - 16.0f, 0.0f);
    prim.Vertex(right, bottom, 0.0f);
    prim.End();

    // Corners 0..3: top left, bottom left, bottom right, top right.
    const float          arc_radius = 33.0f;
    constexpr std::array corner_angles = {HALF_PI, std::numbers::pi_v<float>, std::numbers::pi_v<float> + HALF_PI, 0.0f};
    for (int corner = 0; corner < 4; corner++) {
        const bool  on_right = corner >= 2;
        const bool  on_bottom = corner == 1 || corner == 2;
        const float corner_x = on_right ? right : left;
        const float corner_y = on_bottom ? bottom : top;
        const int   arc_x = static_cast<int>(on_right ? right - 48.0f : left + 48.0f);
        const int   arc_y = static_cast<int>(on_bottom ? bottom - 48.0f : top + 48.0f);
        float       angle = corner_angles[static_cast<std::size_t>(corner)];

        prim.Begin(MG_PRIM_TRIANGLE_FAN);
        prim.Color(0, 0, 0, 128);
        prim.Vertex(corner_x, corner_y, 0.0f);
        for (int step = 0; step < 9; step++) {
            const int x = static_cast<int>(static_cast<float>(arc_x) + arc_radius * cosf(angle));
            const int y = static_cast<int>(static_cast<float>(arc_y) + -arc_radius * sinf(angle));
            angle += 0.19634955f;
            prim.Vertex(x, y, 0);
        }
        prim.End();
    }

    if (ShowTakePhotoCnt > 0 && ShutterAnmCnt == 0) {
        int alpha = 128;
        if (ShowTakePhotoCnt < 30) {
            alpha = std::max(0, 128 - (30 - ShowTakePhotoCnt) * 5);
        }

        mgCTexture *texture = WorkTex;
        const int   y = mgScreenHeight - 126;
        const int   photo_width = texture->width * 3 / 2;
        const int   frame_height = texture->height * 24 / 2 / 10;
        // The preview shows the shot without the picture's bars, in the shape it was taken in.
        const PhotoView area =
            captured_area.value_or(PictureArea(SceneView(), static_cast<float>(texture->width), static_cast<float>(texture->height)));
        const int shot_width =
            static_cast<int>(std::lround(static_cast<float>(photo_width) * (area.right - area.left) / static_cast<float>(texture->width)));
        const int shot_height =
            static_cast<int>(std::lround(static_cast<float>(frame_height) * (area.bottom - area.top) / static_cast<float>(texture->height)));
        const int shot_x = 20 + (photo_width - shot_width) / 2;
        const int shot_y = y + (frame_height - shot_height);
        prim.TextureMapEnable(0);
        prim.Begin(MG_PRIM_SPRITE);
        prim.Color(32, 32, 32, alpha * 2 / 3);
        prim.Vertex(shot_x - 2, shot_y - 2, 0);
        prim.Vertex(shot_x + shot_width + 8, shot_y + shot_height + 8, 0);
        prim.Color(0, 0, 0, alpha);
        prim.Vertex(shot_x - 6, shot_y - 6, 0);
        prim.Vertex(shot_x + shot_width + 6, shot_y + shot_height + 6, 0);
        prim.Color(255, 255, 255, alpha);
        prim.Vertex(shot_x - 5, shot_y - 5, 0);
        prim.Vertex(shot_x + shot_width + 5, shot_y + shot_height + 5, 0);
        prim.End();
        prim.TextureMapEnable(1);
        prim.Bilinear(1);
        prim.AlphaTestEnable(0);
        prim.Begin(MG_PRIM_SPRITE);
        prim.Color(128, 128, 128, alpha);
        prim.Direct(SCE_GS_TEXA, 0x8000000080UL);
        prim.Texture(WorkTex);
        prim.TextureCrd4(static_cast<int>(std::lround(area.left * 16.0f)), static_cast<int>(std::lround(area.top * 16.0f)));
        prim.Vertex(shot_x, shot_y, 0);
        prim.TextureCrd4(static_cast<int>(std::lround(area.right * 16.0f)), static_cast<int>(std::lround(area.bottom * 16.0f)));
        prim.Vertex(shot_x + shot_width, shot_y + shot_height, 0);
        prim.End();
    }

    return taken;
}
