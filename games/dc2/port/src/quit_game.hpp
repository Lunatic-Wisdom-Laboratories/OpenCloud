#pragma once

// QUIT GAME, on the title menu and the Triangle menu of the desktop builds: an entry, chosen with Select or a click,
// and a Yes/No question before the game closes. iOS has none; its apps are closed by the system.
namespace quitgame {

enum class Place {
    Title,
    GameMenu
};

/**
 * Steps the entry and the question for this tick. ready says the menu underneath is waiting for a choice. Returns
 * true while they own the controls, so the menu underneath must not read the pad this tick.
 */
bool Run(Place place, bool ready);

/**
 * Draws the entry, and the question while it is asked, over the menu underneath.
 */
void Draw(Place place);

/**
 * Whether the question is showing.
 */
bool Asking();

} // namespace quitgame
