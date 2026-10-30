# OpenCloud

Native Dark Cloud 2 (Dark Chronicle) engine for Godot 4 (.NET / C#), written from scratch and
structured for future cooperative play. No game assets are included, and the engine never reads
the original game files: a one-time converter turns the user's own disc into open formats
(glTF binary models, PNG textures, JSON documents) and the engine loads only those.

| Folder | What it is |
| --- | --- |
| [`opencloud-converter/`](opencloud-converter/README.md) | Godot (.NET) command-line tool: disc image, `DATA.HD4` or extracted folder → converted data folder or `data.zip` |
| [`opencloud-godot/`](opencloud-godot/README.md) | The engine |

```sh
godot --headless --path opencloud-converter -- --source="/path/to/Dark Chronicle.iso" --out=/path/to/opencloud-data
godot --path opencloud-godot -- --data=/path/to/opencloud-data
```

See [THIRD_PARTY_NOTICE.md](THIRD_PARTY_NOTICE.md) for the projects used as references.
