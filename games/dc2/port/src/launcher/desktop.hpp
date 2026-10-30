#pragma once

#include <filesystem>

/**
 * Shows the desktop launcher until the player starts Dark Cloud 2, with current game data at root (true), or closes
 * it (false). Throws when its window cannot open.
 */
bool LauncherRunDesktop(const std::filesystem::path &root);
