#!/usr/bin/env python3
"""Expose otherwise inaccessible function-local types to native layout tests.

Copy declarations from their owning source, rather than maintaining a second
hand-written mirror that could keep passing after the actual local changes.
"""
import pathlib
import re
import sys

root = pathlib.Path(sys.argv[1])
output = pathlib.Path(sys.argv[2])
character = (root / "ps2/src/character.cpp").read_text()
menus = (root / "ps2/src/menusys.cpp").read_text()
outline = re.search(r"struct OutlineCopy\s*\{.*?\n    \};", character, re.S)
menu = menus.split("int CBaseMenuClass::MenuItemCommandSelect(", 1)[1]
target = re.search(r"^\s*([^;\n]+\btarget(?:\[[^;]+\])?);", menu, re.M)
if outline is None or target is None:
    raise SystemExit("Local layout declarations changed; update the probe extraction")
output.write_text('#pragma once\n#include "gamedata.hpp"\n#include "outline.hpp"\nnamespace LocalLayoutProbe {\n' + outline.group(0) +
                  "\ninline auto TargetSize() { " + target.group(1) +
                  "; return sizeof(target); }\n}\n")
