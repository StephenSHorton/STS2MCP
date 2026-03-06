# STS2 MCP

An HTTP API mod for **Slay the Spire 2** that exposes the game state and allows external control via REST endpoints. Designed as a bridge for AI agents, bots, and tooling.

## Features

- **GET** `/api/v1/singleplayer` — Returns the current game state (battle details, player stats, enemy intents, cards in hand, relics, potions, etc.)
- **POST** `/api/v1/singleplayer` — Execute actions (play cards, end turn)
- Supports `json` and `markdown` response formats
- Generates stable entity IDs for targeting (e.g., `jaw_worm_0`)
- Thread-safe: HTTP requests are bridged to the game's main thread for safe state access and action execution

## Requirements

- [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0) (for building)
- Slay the Spire 2 (Steam)

## Building

```bash
# Default (assumes D:\SteamLibrary\steamapps\common\Slay the Spire 2)
dotnet build -c Release -o out

# Custom game path
dotnet build -c Release -o out -p:STS2GameDir="C:\path\to\Slay the Spire 2"
```

You also need to create a `.pck` file containing `mod_manifest.json`. See the `tools/PckPacker` project in the parent repo, or use any Godot PCK packer:

```bash
dotnet run --project ../tools/PckPacker/PckPacker.csproj -- out/STS2_MCP.pck "" mod_manifest.json
```

## Installation

Copy `STS2_MCP.dll` and `STS2_MCP.pck` to `<game_install>/mods/`.

Enable mods in the game settings (a consent dialog will appear on first launch with mods present).

## API Reference

### `GET /api/v1/singleplayer`

Query parameters:
| Parameter | Values | Default | Description |
|-----------|--------|---------|-------------|
| `format`  | `json`, `markdown` | `json` | Response format |

Returns the current game state. The `state_type` field indicates the screen:
- `battle` / `monster` / `elite` / `boss` — In combat (full battle state returned)
- `event`, `map`, `shop`, `rest_site`, `treasure` — Other screens (stub for now)
- `menu` — No run in progress

**Battle state includes:**
- Player: HP, block, energy, gold, character, powers, relics, potions, hand (with card details), pile counts, orbs
- Enemies: entity_id, name, HP, block, powers, intents with labels

### `POST /api/v1/singleplayer`

**Play a card:**
```json
{
  "action": "play_card",
  "card_index": 0,
  "target": "jaw_worm_0"
}
```
- `card_index`: 0-based index in hand (from GET response)
- `target`: entity_id of the target (required for `AnyEnemy` cards, omit for self-targeting/AoE cards)

**End turn:**
```json
{
  "action": "end_turn"
}
```

### Error responses

All errors return:
```json
{
  "status": "error",
  "error": "Description of what went wrong"
}
```

## Roadmap

- [ ] Event interaction
- [ ] Map navigation / path selection
- [ ] Shop purchases
- [ ] Rest site actions (rest/upgrade)
- [ ] Potion usage via API
- [ ] Reward screen (card pick, skip)
- [ ] Locale parameter support
- [ ] MCP bridge server (stdio transport for Claude Code / AI agent integration)

## License

MIT
