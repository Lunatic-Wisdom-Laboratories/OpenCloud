#include "common.h"

#include <SDL3/SDL.h>

#include <algorithm>
#include <array>
#include <string>
#include <string_view>

#include "font.hpp"
#include "gamepad.hpp"
#include "mainloop.hpp"
#include "menu_mouse.hpp"
#include "menucommon.hpp"
#include "menudraw.hpp"
#include "mglib.hpp"
#include "options/rows.hpp"
#include "platform/config.hpp"
#include "quit_game.hpp"
#include "snd_mngr.hpp"

namespace quitgame {
namespace {

#ifdef SDL_PLATFORM_IOS
constexpr bool kAvailable = false;
#else
constexpr bool kAvailable = true;
#endif

struct Text {
    std::string_view entry;
    std::string_view question;
    std::string_view unsaved; // the Triangle menu's warning, under the question
    std::string_view yes;
    std::string_view no;
};

// By LanguageCode: Japanese (0) has no text of its own and takes the English.
constexpr std::array<Text, 6> kText = {{
    {"Quit Game", "Quit the game?", "Progress since your last save\nwill be lost.", "Yes", "No"},
    {"Quit Game", "Quit the game?", "Progress since your last save\nwill be lost.", "Yes", "No"},
    {"Quitter le jeu", "Quitter le jeu ?", "La progression depuis la dernière\nsauvegarde sera perdue.", "Oui", "Non"},
    {"Spiel beenden", "Spiel beenden?", "Der Fortschritt seit dem letzten\nSpeichern geht verloren.", "Ja", "Nein"},
    {"Esci dal gioco", "Uscire dal gioco?", "I progressi dall'ultimo salvataggio\nandranno persi.", "Sì", "No"},
    {"Salir del juego", "¿Salir del juego?", "Se perderá el progreso desde\nla última vez que guardaste.", "Sí", "No"},
}};

constexpr int kEntrySize = 13;
constexpr int kTextSize = 15;
constexpr int kChoiceWidth = 72;
constexpr int kChoiceHeight = 23;
constexpr int kChoiceGap = 24;
constexpr int kFadeStep = 16;

struct Rect {
    int x = 0, y = 0, w = 0, h = 0;

    [[nodiscard]] bool Contains(float px, float py) const { return px >= x && px < x + w && py >= y && py < y + h; }
};

struct State {
    Place place = Place::Title;
    bool  asking = false;
    bool  quitting = false;
    bool  yes = false; // No is the default answer
    int   fade = 0;    // the entry's alpha, 0 to 128
};

State g_state;

const Text &Strings() {
    return kText[LanguageCode >= 0 && LanguageCode < static_cast<int>(kText.size()) ? LanguageCode : 1];
}

// The PAL font takes accented letters as Latin-1 bytes (GetAlphabeticalFontNo_uc).
std::string Latin1(std::string_view utf8) {
    std::string out;
    for (std::size_t i = 0; i < utf8.size(); ++i) {
        const auto lead = static_cast<unsigned char>(utf8[i]);
        if (lead >= 0xC0 && lead < 0xE0 && i + 1 < utf8.size()) {
            out += static_cast<char>(((lead & 0x1F) << 6) | (static_cast<unsigned char>(utf8[++i]) & 0x3F));
        } else {
            out += static_cast<char>(lead);
        }
    }
    return out;
}

void SetUp(CFont &font, int size) {
    font.SetDrawSize(size, size + 4);
    font.SetClearance(size, size + 5);
    font.SetFuchi(FUCHI_SHADOW_BLACK);
}

int TextWidth(std::string &text, int size) {
    CFont font;
    SetUp(font, size);
    int w = 0;
    int h = 0;
    font.CalcDrawWH(text.data(), &w, &h);
    return w;
}

void DrawText(std::string &text, int x, int y, int size, bool gold, int alpha) {
    CFont font;
    SetUp(font, size);
    font.SetColor(gold ? 128 : 118, gold ? 108 : 118, gold ? 64 : 118, 128);
    font.alpha = alpha;
    font.DrawDirect(text.data(), x, y);
}

void Fill(const Rect &rect, int alpha, int r, int g, int b) {
    DrawMenuFillBox(static_cast<float>(rect.x), static_cast<float>(rect.y), static_cast<float>(rect.w),
                    static_cast<float>(rect.h), alpha, r, g, b);
}

// The port's plate (options/draw.cpp), at an alpha.
void Plate(const Rect &rect, bool selected, int alpha) {
    Fill(rect, alpha, selected ? 108 : 65, selected ? 83 : 48, selected ? 38 : 28);
    Fill({rect.x + 1, rect.y + 1, rect.w - 2, rect.h - 2}, alpha, selected ? 55 : 36, selected ? 42 : 27,
         selected ? 28 : 20);
    if (selected) {
        Fill({rect.x + 2, rect.y + rect.h - 2, rect.w - 4, 1}, alpha, 124, 106, 66);
    }
}

std::string EntryLabel() {
    return options::ButtonName("select", ConfigGet()) + ": " + Latin1(Strings().entry);
}

// The title's entry sits under its menu rows (title.cpp's TitleModeDraw); the Triangle menu's at its top right, where
// the Options page has PC SETTINGS.
Rect EntryRect(Place place) {
    std::string label = EntryLabel();
    const int   w = TextWidth(label, kEntrySize) + 14;
    if (place == Place::Title) {
        return {(mgScreenWidth - w) / 2, 346, w, 25};
    }
    return {444 - w, 51, w, 25};
}

struct Layout {
    Rect box;
    Rect yes;
    Rect no;
    int  text_y = 0;
};

std::string Question() {
    std::string text = Latin1(Strings().question);
    if (g_state.place == Place::GameMenu) {
        text += "\n";
        text += Latin1(Strings().unsaved);
    }
    return text;
}

Layout Arrange(std::string &question) {
    const int lines = static_cast<int>(std::count(question.begin(), question.end(), '\n')) + 1;
    const int text_w = TextWidth(question, kTextSize);
    const int w = std::clamp(text_w + 40, 2 * kChoiceWidth + kChoiceGap + 40, mgScreenWidth - 16);
    const int h = 20 + lines * (kTextSize + 5) + 14 + kChoiceHeight + 16;
    Layout    layout;
    layout.box = {(mgScreenWidth - w) / 2, (mgScreenHeight - h) / 2, w, h};
    layout.text_y = layout.box.y + 18;
    const int choice_y = layout.box.y + h - 16 - kChoiceHeight;
    const int choice_x = (mgScreenWidth - 2 * kChoiceWidth - kChoiceGap) / 2;
    layout.yes = {choice_x, choice_y, kChoiceWidth, kChoiceHeight};
    layout.no = {choice_x + kChoiceWidth + kChoiceGap, choice_y, kChoiceWidth, kChoiceHeight};
    return layout;
}

void Close() {
    g_state.asking = false;
    MenuSePlay(SYSTEM_SE_CANCEL);
}

// As closing the window does: WindowPollEvents takes the event and the process ends there.
void Quit() {
    g_state.quitting = true;
    MenuSePlay(SYSTEM_SE_DECIDE);
    SDL_Event event{};
    event.type = SDL_EVENT_QUIT;
    SDL_PushEvent(&event);
}

void Choose(bool yes) {
    if (g_state.yes != yes) {
        g_state.yes = yes;
        MenuSePlay(SYSTEM_SE_CURSOR);
    }
}

void Ask() {
    const auto &pad = GamePad__2;
    // The pad alone: the menu mouse also arrives as pad presses, and the question reads its clicks itself.
    const int   pressed = pad.pad[0].button & ~pad.previous_pad[0].button;
    const int   confirm = LanguageCode > 0 ? PAD_CROSS : PAD_CIRCLE;
    const int   cancel = LanguageCode > 0 ? PAD_CIRCLE : PAD_CROSS;
    const auto &mouse = MenuMouseTake();
    MenuMouseSetButtons(0);
    if (g_state.quitting) {
        return;
    }
    std::string question = Question();
    const auto  layout = Arrange(question);
    if (mouse.pointing && mouse.moved) {
        if (layout.yes.Contains(mouse.x, mouse.y)) {
            Choose(true);
        } else if (layout.no.Contains(mouse.x, mouse.y)) {
            Choose(false);
        }
    }
    if (mouse.clicked & 2) {
        Close();
    } else if (mouse.clicked & 1) {
        if (layout.yes.Contains(mouse.x, mouse.y)) {
            Quit();
        } else if (layout.no.Contains(mouse.x, mouse.y)) {
            Close();
        }
    } else if (pressed & (PAD_LEFT | PAD_RIGHT | PAD_UP | PAD_DOWN)) {
        Choose(!g_state.yes);
    } else if (pressed & confirm) {
        if (g_state.yes) {
            Quit();
        } else {
            Close();
        }
    } else if (pressed & (cancel | PAD_SELECT)) {
        Close();
    }
}

} // namespace

bool Run(Place place, bool ready) {
    if (!kAvailable) {
        return false;
    }
    if (g_state.place != place) {
        g_state = {.place = place};
    }
    if (g_state.asking) {
        g_state.fade = 128;
        Ask();
        return true;
    }
    g_state.fade = std::clamp(g_state.fade + (ready ? kFadeStep : -kFadeStep), 0, 128);
    if (!ready) {
        return false;
    }
    const auto &pad = GamePad__2;
    const auto &mouse = MenuMouseTake();
    const bool  select = (pad.pad[0].button & ~pad.previous_pad[0].button) & PAD_SELECT;
    const bool  click = (mouse.clicked & 1) && mouse.pointing && EntryRect(place).Contains(mouse.x, mouse.y);
    if (!select && !click) {
        return false;
    }
    if (click) {
        MenuMouseSetButtons(0);
    }
    g_state.asking = true;
    g_state.yes = false;
    MenuSePlay(SYSTEM_SE_DECIDE);
    return true;
}

void Draw(Place place) {
    if (!kAvailable || g_state.place != place || g_state.fade <= 0) {
        return;
    }
    ResetMenuScissor();
    if (!g_state.asking) {
        const Rect  entry = EntryRect(place);
        const auto &mouse = MenuMouseTake();
        const bool  over = mouse.pointing && entry.Contains(mouse.x, mouse.y);
        Plate(entry, over, g_state.fade);
        std::string label = EntryLabel();
        DrawText(label, entry.x + 7, entry.y + 4, kEntrySize, true, g_state.fade);
        return;
    }
    // Dim the menu underneath, then the question in a gold-edged window with its two answers.
    Fill({0, 0, mgScreenWidth, mgScreenHeight}, 72, 0, 0, 0);
    std::string question = Question();
    const auto  layout = Arrange(question);
    Fill(layout.box, 128, 124, 106, 66);
    Fill({layout.box.x + 1, layout.box.y + 1, layout.box.w - 2, layout.box.h - 2}, 128, 18, 17, 16);
    std::size_t start = 0;
    int         y = layout.text_y;
    while (start <= question.size()) {
        const auto  end = question.find('\n', start);
        std::string line = question.substr(start, end == std::string::npos ? std::string::npos : end - start);
        DrawText(line, (mgScreenWidth - TextWidth(line, kTextSize)) / 2, y, kTextSize, start == 0, 128);
        y += kTextSize + 5;
        if (end == std::string::npos) {
            break;
        }
        start = end + 1;
    }
    for (const bool yes : {true, false}) {
        const Rect &rect = yes ? layout.yes : layout.no;
        const bool  selected = g_state.yes == yes;
        Plate(rect, selected, 128);
        std::string label = Latin1(yes ? Strings().yes : Strings().no);
        DrawText(label, rect.x + (rect.w - TextWidth(label, kTextSize)) / 2, rect.y + 3, kTextSize, selected, 128);
        if (selected) {
            std::string cursor = ">";
            DrawText(cursor, rect.x - 12, rect.y + 3, kTextSize, true, 128);
        }
    }
}

bool Asking() { return kAvailable && g_state.asking; }

} // namespace quitgame
