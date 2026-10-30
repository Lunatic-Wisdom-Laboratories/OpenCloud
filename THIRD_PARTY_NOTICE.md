# Third-party notice

OpenCloud is written from scratch. No source code from the projects below is included in or
derived into this repository. Their published knowledge (file format descriptions, reverse
engineering notes and the behaviour they document) was used, with permission, as a reference
while reimplementing the game in Godot.

## ChronicleTwo

A decompilation of Dark Chronicle / Dark Cloud 2 for the PlayStation 2, and a native port built
from it, by TheMoonPeople.

- https://github.com/TheMoonPeople/ChronicleTwo

Used as a reference for the retail file formats (disc file index, PACK archives, TIM2/IMG
textures, MDS/MDT models, weights, bind poses, motions, configuration scripts, maps and skies)
and for how the game reads and uses them.

## Kazarov

The shared build system of TheMoonPeople's PS2 decompilation projects, including their disc
extraction and texture conversion tools.

- https://github.com/TheMoonPeople/Kazarov
- Copyright (c) 2026 TheMoonPeople, MIT License

Used as a reference for extracting the retail disc into open formats.

## Dark Cloud 2 / Dark Chronicle

Dark Cloud 2 (Dark Chronicle) is © Level-5 / Sony Interactive Entertainment. This repository
contains no game data; users convert their own copy with `opencloud-converter`.
