#pragma once

// The game as the iOS port's Swift code sees it, through Swift's C++ interoperability (module.modulemap names this
// header ChronicleBridge). It is kept to plain types, so the Swift compiler parses none of the game's own headers.

#include <cstdint>
#include <string>

namespace ct::ios {

// The on-screen gamepad: an active-high button word and stick deflections from -1 to 1 (InputTouchOverlayState).
struct TouchState {
    std::uint16_t buttons = 0;
    float         left_x = 0;
    float         left_y = 0;
    float         right_x = 0;
    float         right_y = 0;
};

// Pad 0 takes this while TouchOverlayVisible().
void SetTouchOverlay(const TouchState &state);
// No gamepad, keyboard or mouse connected and no key or mouse button held.
bool TouchOverlayVisible();
// The libpad bit of each on-screen button: Up, Down, Left, Right, Cross, Circle, Square, Triangle, L1, R1, L2, R2,
// Select, Start, L3, R3.
std::uint16_t ButtonBit(int index);

// The DualSense actuator signal (dualsense_haptics.hpp): interleaved left and right samples at HapticsRate().
int  HapticsRate();
void HapticsRender(float *left_right, int frames);

// The launcher's text in its language (launcher/model.hpp), with "{}" where a value goes.
std::string Text(const std::string &key);
// The device's preferred language ("es-ES"), used until one is chosen in the launcher.
void        SetSystemLanguage(const std::string &code);
int         LanguageCount();
int         LanguageIndex();
std::string LanguageName(int index);
void        SetLanguageIndex(int index);

// The APP SETTINGS rows the launcher can step (no bindings, which wait for a key in the game), in its language.
struct SettingInfo {
    int         page = 0;
    int         row = 0;
    std::string page_name;
    std::string label;
    std::string help;
    std::string value;
};

int         SettingCount();
SettingInfo SettingAt(int index);
enum class StepResult {
    Unchanged,
    Saved,
    NotSaved
};
// Steps the row as Left (-1) and Right (1) do in the game, and saves config.json.
StepResult SettingsStep(int page, int row, int direction);

// The saves, as RefreshSaves last listed them.
struct SaveInfo {
    std::string file; // The handle the other calls take.
    std::string path; // Where it is, to share it.
    std::string title;
    std::string detail;
    std::string modified;
    bool        readable = false;
};

int         RefreshSaves();
SaveInfo    SaveAt(int index);
bool        SavesProtected();
// Each returns an empty string when it worked, otherwise the error to show.
std::string DeleteSave(const std::string &file);
std::string DuplicateSave(const std::string &file);
std::string ImportSave(const std::string &source);
// ChronicleTwo's saves that can be copied in; 0 for none.
int LegacySaveCount();

struct Outcome {
    bool        ok = false;
    std::string message;
};

// Copies ChronicleTwo's saves and album in, leaving its folder as it is.
Outcome ImportLegacy();
// Converts the PS2 saves of a PCSX2 memory card, a .psu or a save folder in, leaving the source as it is.
Outcome ImportPs2(const std::string &source);

// The memory card album's photos, as RefreshPhotos last listed them, each written as a PNG to show and share.
struct PhotoInfo {
    int         card = 0;
    int         slot = 0;
    bool        hd = false;
    std::string png;
};

int         RefreshPhotos();
PhotoInfo   PhotoAt(int index);
std::string DeletePhoto(int card, int slot);

// Game data discovery and background extraction. ExtractBegin refuses a disc other than Dark Chronicle PAL, which
// ExtractPoll then reports as its failure.
void LeaveNote();
bool DataMissing(const std::string &root);
// The disc image found in Documents or its Inbox, or empty.
std::string DiscImage();
void        ExtractBegin(const std::string &image, const std::string &root);

struct Extraction {
    bool        running = false;
    float       fraction = 0;
    std::string stage;
    std::string failure;
};

Extraction ExtractPoll();

} // namespace ct::ios

extern "C" {
/**
 * Runs the iOS launcher on the main thread until current game data is ready and Play is selected.
 */
void IosLauncherRunAt(const char *data_root);

/**
 * Attaches the touch gamepad to the game window while no physical input device is active.
 */
void IosTouchOverlayStart();

/**
 * Starts the Core Haptics worker if needed and reports whether the DualSense actuators are ready.
 */
bool IosHapticsReady();
}
