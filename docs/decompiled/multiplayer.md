# Multiplayer & Networking

> Game version: `v0.99.1` | Last updated: 2026-03-28

## Overview

STS2 multiplayer uses a host-authoritative, peer-to-peer architecture. One player hosts, others connect as clients. Steam P2P is the primary transport, with ENet available as a debug/dev fallback. There is no dedicated server.

## Key Types

### `NetGameType` (Enum)

- **Values**: `None`, `Singleplayer`, `Host`, `Client`, `Replay`
- **`IsMultiplayer()`**: Extension method returning true for `Host` and `Client` only

### `INetGameService` (Interface)

Central abstraction for all networking. Four implementations:

| Implementation | Role | NetId | Notes |
|---|---|---|---|
| `NetSingleplayerGameService` | No-op stub | Always `1` | All `SendMessage` calls are no-ops |
| `NetHostGameService` | Server/host | Player's ID | Wraps `NetHost`, manages `_connectedPeers` as `NetClientData` |
| `NetClientGameService` | Client | Assigned | Wraps `NetClient`, can only send to host |
| `NetReplayGameService` | Replay playback | — | Used for combat replays |

### `NetHostGameService` (Full Architecture)

```
Fields:
  _netHost: NetHost?                    — underlying transport
  _messageBus: NetMessageBus            — serialization/dispatch
  _qualityTracker: NetQualityTracker    — connection quality monitoring
  _connectedPeers: List<NetClientData>  — all connected clients

Properties:
  IsConnected: bool          — _netHost?.IsConnected
  ConnectedPeers: IReadOnlyList<NetClientData>
  NetId: ulong               — from _netHost.NetId
  NetHost: NetHost?          — exposes the transport
  Type: NetGameType          — always NetGameType.Host

Events:
  Disconnected: Action<NetErrorInfo>?
  ClientConnected: Action<ulong>?
  ClientDisconnected: Action<ulong, NetErrorInfo>?
```

**Key methods:**

- `OnPeerConnected(ulong peerId)` — Adds `NetClientData { peerId, readyForBroadcasting=false }` to `_connectedPeers`, notifies quality tracker, fires `ClientConnected` event
- `SetPeerReadyForBroadcasting(ulong peerId)` — Marks peer as ready to receive broadcasts. Without this, `SendMessage<T>(T)` (broadcast) skips the peer
- `SendMessage<T>(T, ulong peerId)` — Serializes via `_messageBus` and sends to specific peer via `_netHost.SendMessageToClient`
- `SendMessage<T>(T)` — Broadcasts to all peers where `readyForBroadcasting == true`
- `OnPacketReceived(senderId, bytes, mode, channel)` — Deserializes via `_messageBus.TryDeserializeMessage`, rebroadcasts if `ShouldBroadcast`, dispatches to handlers via `_messageBus.SendMessageToAllHandlers`
- `OnPeerDisconnected(ulong peerId, NetErrorInfo)` — Removes from `_connectedPeers`, fires `ClientDisconnected`

### `NetClientData` (Struct)

```csharp
public struct NetClientData {
    public ulong peerId;
    public bool readyForBroadcasting;
}
```

Trivially simple — just an ID and a broadcast flag.

### `NetHost` (Abstract Base)

```csharp
public abstract class NetHost {
    protected INetHostHandler _handler;
    abstract IEnumerable<ulong> ConnectedPeerIds { get; }
    abstract bool IsConnected { get; }
    abstract ulong NetId { get; }
    abstract void Update();
    abstract void SetHostIsClosed(bool isClosed);
    abstract void SendMessageToClient(ulong peerId, byte[] bytes, int length, NetTransferMode mode, int channel = 0);
    abstract void SendMessageToAll(byte[] bytes, int length, NetTransferMode mode, int channel = 0);
    abstract void DisconnectClient(ulong peerId, NetError reason, bool now = false);
    abstract void StopHost(NetError reason, bool now = false);
    abstract string? GetRawLobbyIdentifier();
}
```

Transport implementations:

**Steam P2P (Production):**
- `SteamHost` — Creates friends-only lobby via `SteamMatchmaking`, P2P listen socket via `SteamNetworkingSockets.CreateListenSocketP2P`. Validates joiners via `IsInLobby()`. NetId = `SteamUser.GetSteamID().m_SteamID`.
- `SteamClient` — Joins Steam lobby, connects P2P to lobby owner. Can connect via friend's Steam ID or lobby ID.
- Timeouts: 20 seconds for both initial and connected timeouts.

**ENet (Debug/Dev):**
- `ENetHost` — Binds `0.0.0.0:33771` (hardcoded in debug UI). NetId always `1`. Uses Godot's `ENetConnection`.
- `ENetClient` — Connects to IP:port. NetId passed in by caller. Uses Godot's `ENetPacketPeer`.

### `NetMessageBus`

Handles message serialization and handler dispatch.

**Serialization format:** `[messageTypeId: 1 byte][senderId: 8 bytes ulong][...payload...]`

```
Key methods:
  SerializeMessage<T>(senderId, message, out length) → byte[]
  TryDeserializeMessage(bytes, out message, out overrideSenderId) → bool
  SendMessageToAllHandlers(message, senderId)                    — dispatches to registered handlers
  RegisterMessageHandler<T>(handler)                             — T must be concrete INetMessage
  UnregisterMessageHandler<T>(handler)
```

- `TryDeserializeMessage` reads the first byte as message type ID, maps via `MessageTypes.TryGetMessageType` to a `Type`, instantiates via `Activator.CreateInstance`, deserializes payload
- The `overrideSenderId` extracted from the packet can differ from the transport-level `senderId` (used for host relaying client messages)

### `INetMessage` (Interface)

```csharp
public interface INetMessage : IPacketSerializable {
    bool ShouldBroadcast { get; }       // if true, host rebroadcasts to other peers
    NetTransferMode Mode { get; }       // Reliable, Unreliable, etc.
    LogLevel LogLevel { get; }
}
```

### `JoinFlow`

Handles the full client connection handshake:

1. `Begin(initializer, sceneTree)` — accepts `SteamClientConnectionInitializer` or `ENetClientConnectionInitializer`
2. Initializer creates transport client, calls `gameService.Initialize(client, platform)`
3. Host sends `InitialGameInfoMessage` (version, mod list, model ID hash, session state, game mode)
4. Version/mod/hash checks performed
5. Branches based on `RunSessionState`:
   - **InLobby**: `ClientLobbyJoinRequestMessage` → `ClientLobbyJoinResponseMessage`
   - **InLoadedLobby**: `ClientLoadJoinRequestMessage` → `ClientLoadJoinResponseMessage`
   - **Running**: `ClientRejoinRequestMessage` → `ClientRejoinResponseMessage`

## Lobby System

### `LobbyPlayer` (Struct)

```csharp
public struct LobbyPlayer : IPacketSerializable {
    public ulong id;                              // NetId of the player
    public int slotId;                            // 0-3 (serialized with 2 bits)
    public CharacterModel character;              // defaults to Ironclad on join
    public SerializableUnlockState unlockState;
    public int maxMultiplayerAscensionUnlocked;
    public bool isReady;
}
```

**Note:** `slotId` uses 2-bit serialization → max 4 slots (0-3). The `playersInLobby` list uses 3-bit count → max 8 entries.

### `StartRunLobby`

Pre-game lobby for new runs. The host creates this, clients receive state via messages.

**Constructor** registers handlers for: `ClientLobbyJoinRequestMessage`, `ClientLoadJoinRequestMessage`, `ClientRejoinRequestMessage`, `PlayerJoinedMessage`, `PlayerLeftMessage`, `LobbyPlayerChangedCharacterMessage`, `LobbyAscensionChangedMessage`, `LobbySeedChangedMessage`, `LobbyModifiersChangedMessage`, `LobbyPlayerSetReadyMessage`, `LobbyBeginRunMessage`.

If `NetService.Type == Host`, also subscribes to `ClientConnected` and `ClientDisconnected` events on the `INetHostGameService`.

**Key methods:**

- **`TryAddPlayerInFirstAvailableSlot(unlockState, maxAscension, playerId)`** — Iterates slots 0..MaxPlayers, finds first unoccupied, creates `LobbyPlayer` with `character = ModelDb.Character<Ironclad>()` (default), appends to `Players` list. Returns null if lobby is full.

- **`AddLocalHostPlayer(unlocks, maxMultiplayerAscension)`** — Calls `TryAddPlayerInFirstAvailableSlot` with `NetService.NetId` as the player ID. Host-only.

- **`HandleClientLobbyJoinRequestMessage(message, senderId)`** — The join flow:
  1. Checks `Players.Count < MaxPlayers` (disconnects with `LobbyFull` if not)
  2. Calls `TryAddPlayerInFirstAvailableSlot(message.unlockState, message.maxAscensionUnlocked, senderId)`
  3. Sends `ClientLobbyJoinResponseMessage` to the joining client (contains full player list, ascension, seed, modifiers)
  4. Calls `SetPeerReadyForBroadcasting(senderId)` — **critical** for enabling broadcasts
  5. Broadcasts `PlayerJoinedMessage` to all other peers
  6. Fires `PlayerConnected` event

- **`BeginRun(seed, modifiers)`** — Host-only:
  1. Creates `LobbyBeginRunMessage` with `Players` list, seed, modifiers, act1
  2. Broadcasts to all clients
  3. Calls `LobbyListener.BeginRun(seed, acts, modifiers)` to start the run
  4. Closes lobby via `NetHost.SetHostIsClosed(true)`

### `LobbyBeginRunMessage`

```csharp
public struct LobbyBeginRunMessage : INetMessage {
    public List<LobbyPlayer>? playersInLobby;    // final player roster
    public string seed;
    public List<SerializableModifier> modifiers;
    public string act1;
    public bool ShouldBroadcast => true;         // host relays to all
    public NetTransferMode Mode => NetTransferMode.Reliable;
}
```

### `RunLobby`

In-game lobby for reconnection/disconnection during active runs.

**Tracking:** Uses `HashSet<ulong> _connectedPlayerIds` separately from `IPlayerCollection`. A player can be in the roster but disconnected (allows rejoin).

**Key methods:**

- **`OnConnectedToClientAsHost(playerId)`** — Checks `_playerCollection.GetPlayer(playerId)`. If not in run roster, sends failure message and disconnects. Otherwise sends `InitialGameInfoMessage` with `sessionState = Running`.

- **`HandleClientRejoinRequestMessage(message, senderId)`** — Validates via `_playerCollection.GetPlayer(senderId)`, sends `ClientRejoinResponseMessage` (contains serialized run + combat state), calls `SetPeerReadyForBroadcasting`, broadcasts `PlayerRejoinedMessage`, adds to `_connectedPlayerIds`.

- **`OnDisconnectedFromClientAsHost(playerId, info)`** — Removes from `_connectedPlayerIds`, broadcasts `PlayerLeftMessage`. Does NOT remove from player roster (allows rejoin).

### `IPlayerCollection` (Interface)

```csharp
public interface IPlayerCollection {
    IReadOnlyList<Player> Players { get; }
    int GetPlayerSlotIndex(Player player);
    Player? GetPlayer(ulong netId);     // lookup by NetId
}
```

## Player Creation & Injection

### `Player.CreateForNewRun(CharacterModel, UnlockState, ulong netId)`

Creates a fresh player with starting inventory. The `netId` is the Steam ID for Steam transport, or a manually-assigned value for ENet/debug.

### `RunState.AddPlayerDebug(Player player, int index)`

Injects a player directly into an active run without networking:

```
1. Inserts player into _players list at index (or appends if index < 0)
2. player.InitializeSeed(Rng.StringSeed)
3. Registers all cards via card.AfterCreated()
4. Sets player.RunState = this
5. Adds each card via AddCard(card, player)
6. Creates PlayerMapPointHistoryEntry
7. If run in progress: populates relic grab bag, applies ascension effects
```

### `RunState.CreateForNewRun(players, acts, modifiers, ascension, seed)`

Normal run creation:
1. Creates `RunRngSet` from seed
2. `CreateShared()` stores players in `_players`
3. Sets each `player.RunState = runState`
4. Registers all cards, calls `player.InitializeSeed(seed)` (seeded with `hash(seed) + NetId`)

## Action System

### Network Action Types

11 network action subtypes (source-generated from `INetAction`):

| NetAction | GameAction | Purpose |
|---|---|---|
| `NetPlayCardAction` | `PlayCardAction` | Play a card (with card ref, model ID, target) |
| `NetEndPlayerTurnAction` | `EndPlayerTurnAction` | Submit end-turn vote (carries combat round) |
| `NetUndoEndPlayerTurnAction` | `UndoEndPlayerTurnAction` | Retract end-turn vote |
| `NetUsePotionAction` | — | Use a potion |
| `NetDiscardPotionGameAction` | — | Discard a potion |
| `NetMoveToMapCoordAction` | — | Move to map node |
| `NetVoteForMapCoordAction` | — | Vote for map node (multiplayer) |
| `NetVoteToMoveToNextActAction` | — | Vote to advance to next act |
| `NetPickRelicAction` | — | Pick a relic |
| `NetReadyToBeginEnemyTurnAction` | `ReadyToBeginEnemyTurnAction` | Phase-two sync point |
| `NetConsoleCmdGameAction` | — | Debug console command |

### `ActionQueueSet` — Per-Player Queues

Maintains one action queue per player with **monotonically increasing action IDs** (`_nextId`).

- `GetReadyAction()` picks the action with the **lowest ID** across all player queues → deterministic ordering regardless of submission timing
- Queues can be paused (enemy turn) or set to cancel incoming player actions (end-of-turn)
- `PauseActionForPlayerChoice()` pauses mid-execution, optionally cancels queued `PlayCardAction`s for that player

### `ActionQueueSynchronizer` — Network Layer

**Host-authoritative model:**
- **Client** → sends `RequestEnqueueActionMessage` to host
- **Host** → validates, assigns ID via `EnqueueAction()`, broadcasts `ActionEnqueuedMessage` to all clients
- **Singleplayer/Host** → enqueue directly, no network round-trip

**Combat state management via `SetCombatState()`:**
- `NotInCombat`: cancels deferred actions, unpauses
- `PlayPhase`: enqueues deferred waiting actions, unpauses
- `EndTurnPhaseOne`: starts cancelling player-driven actions
- `NotPlayPhase`: pauses all player queues

**Deferred actions**: `CombatPlayPhaseOnly` actions during `NotPlayPhase` are stored in `_requestedActionsWaitingForPlayerTurn` and auto-enqueued when `PlayPhase` starts.

### `GameAction` (Abstract Base)

State machine: `None → WaitingForExecution → Executing → [GatheringPlayerChoice → ReadyToResumeExecuting →] Finished | Canceled`

Key properties:
- `OwnerId: ulong` — the player's NetId
- `ActionType: GameActionType` — `None | Combat | CombatPlayPhaseOnly | NonCombat | Any`
- `Id: int` — globally unique sequential ID assigned on enqueue
- `ToNetAction(): INetAction` — serializes for network transmission

### `PlayCardAction`

- `ActionType = CombatPlayPhaseOnly`
- Carries `NetCombatCard` (serialized card ref), `CardModelId`, optional `TargetId`
- Execution: resolves card model, waits for target creature (10s timeout via `GetCreatureAsync`), validates playability, spends energy/stars, calls `card.OnPlayWrapper()`
- Can pause for player choice mid-execution (e.g., card requires additional selection)

### `EndPlayerTurnAction`

- `ActionType = CombatPlayPhaseOnly`
- Carries `_combatRound` — guards against stale round (only ends turn if round matches current)
- Execution: calls `PlayerCmd.EndTurn(player, canBackOut: true)`

## Combat System (Multiplayer)

### `CombatManager` — The Combat Singleton

**Two-phase turn end:**

1. **Phase One** (`AfterAllPlayersReadyToEndTurn`):
   - Sets `ActionSynchronizerCombatState.EndTurnPhaseOne` → cancels queued player actions
   - Waits for queue to drain
   - Executes `EndPlayerTurnPhaseOneInternal()` → fires `BeforeTurnEnd` hooks, discards hand, processes ethereal cards
   - Each player enqueues `ReadyToBeginEnemyTurnAction`

2. **Phase Two** (`AfterAllPlayersReadyToBeginEnemyTurn`):
   - Sets `ActionSynchronizerCombatState.NotPlayPhase` → pauses player queues
   - Executes `EndPlayerTurnPhaseTwoInternal()` → flushes hand to discard, card retention
   - Switches sides, starts enemy turn

**Ready tracking:**
```csharp
private HashSet<Player> _playersReadyToEndTurn;
private HashSet<Player> _playersReadyToBeginEnemyTurn;
private Lock _playerReadyLock;

AllPlayersReadyToEndTurn() → _playersReadyToEndTurn.Count == _state.Players.Count && CurrentSide == Player
IsPlayerReadyToEndTurn(player) → _playersReadyToEndTurn.Contains(player)
```

**Monster HP scaling:** `CombatState.CreateCreature()` calls `creature.ScaleMonsterHpForMultiplayer(Encounter, Players.Count, ActIndex)`.

### `CombatStateSynchronizer` (Fully Analyzed)

Pre-combat state sync. Called at every room transition (`EnterMapPointInternal`, `EnterRoomDebug`, etc.).

**Sync protocol:**
1. `StartSync()` — each peer serializes own `Player` → `SyncPlayerDataMessage` (broadcast, `ShouldBroadcast=true`)
2. Each peer also stores own data locally in `_syncData[player.NetId]`
3. Host-only: also sends `SyncRngMessage` (run RNG + shared relic grab bag), `ShouldBroadcast=false`
4. `CheckSyncCompleted()` — resolves when: every ID in `_runLobby.ConnectedPlayerIds` has an entry in `_syncData` AND `_rngSet != null`
5. `WaitForSync()` — awaits the `TaskCompletionSource`, then applies data:
   - For each non-local synced player: `player.SyncWithSerializedPlayer(data)`
   - Clients load host's RNG and shared relic grab bag

**Tracking:** Uses `_runLobby.ConnectedPlayerIds` (a `HashSet<ulong>`) — NOT `ConnectedPeers` on the net service. If a peer disconnects during sync (`RemotePlayerDisconnected` event), `CheckSyncCompleted` re-evaluates and can complete without the disconnected peer.

**Ghost peer requirement:** **BLOCKING.** Ghost must provide a `SyncPlayerDataMessage` with its `SerializablePlayer` data. Without it, `CheckSyncCompleted` loops forever waiting for the ghost's ID. The ghost's `Player.ToSerializable()` provides the data.

### `ChecksumTracker` (Fully Analyzed)

Validates game state consistency between host and clients during combat.

**What is checksummed:** `NetFullCombatState.FromRun()` — captures ALL combat state:
- All creatures (HP, maxHP, block, powers with amounts)
- All players (energy, stars, gold, character ID)
- Per-player: all card piles (hand, draw, discard, exhaust, play) with full card state
- Per-player: potions, relics (with serialized props), orbs
- Per-player: full RNG set (seed + all counters), odds set, relic grab bag
- Global run RNG (seed + all counters)
- `nextChoiceIds` from `PlayerChoiceSynchronizer`
- `lastExecutedHookId` and `lastExecutedActionId`

Serialized via `PacketWriter`, hashed with **XxHash32**.

**When checksums are computed:**
- After every game action executes (except `EndPlayerTurnAction` and `ReadyToBeginEnemyTurnAction`)
- After player/enemy turn start/end
- Exiting deterministic event rooms
- Exiting rest site rooms

**Flow (asymmetric):**
- **Client** sends `ChecksumDataMessage` to host after generating each checksum
- **Host** compares locally. If host hasn't computed that ID yet, queues the remote checksum
- Last 20 checksums retained

**On mismatch — PUNITIVE:**
1. Host logs error, sends `StateDivergenceMessage` (with full state dump) to divergent client
2. Client receives it, reports to **Sentry**, sends its own state dump back
3. **Host disconnects the client** via `DisconnectClient(senderId, NetError.StateDivergence)`
4. Both sides show error popups
5. Client's `RunManager` abandons the run and returns to main menu

**Cannot be disabled.** No config flag. `TestMode.IsOn` suppresses logging/Sentry but NOT the comparison or disconnect.

**Ghost peer implication — SAFE.** Since we're running on the **host**, the ghost player's state is computed and mutated entirely within the host process. The ghost's state is always identical to what the host sees — there is no network transmission or separate game instance that could diverge. Checksums only compare host vs. remote clients. The ghost IS the host's state. Real clients may send checksums that include the ghost player's state, and those will match because the host broadcasted the same actions to clients via `ActionEnqueuedMessage`.

**Important:** If a real client diverges, the client gets disconnected — not the ghost. The ghost can never diverge because it doesn't have a separate state. No patching needed.

### `ActionQueueSynchronizer` (Fully Analyzed)

**EnqueueAction does NOT validate OwnerId.** The private `EnqueueAction(action, actionOwnerId)` method:
1. If host: broadcasts `ActionEnqueuedMessage` with the `actionOwnerId`
2. Calls `_actionQueueSet.EnqueueWithoutSynchronizing(action)`
3. The queue routes based on `action.OwnerId` (baked into the `GameAction` object)
4. No cross-check between the parameter and the action's inherent owner

**Host can enqueue for any player.** Through `RequestEnqueue()`:
- Host/Singleplayer path calls `EnqueueAction(action, _netService.NetId)` directly — the `_netService.NetId` is passed for the broadcast message, but the action itself carries its own `OwnerId`
- When receiving client requests via `HandleRequestEnqueueActionMessage`, the host uses `senderId` from the transport layer to construct the `GameAction` via `NetActionToGameAction(action, senderId)` → `_playerCollection.GetPlayer(senderId)` → `action.ToGameAction(player)`

**Ghost peer approach:** Create `GameAction` objects with the ghost's `Player` object as owner, then call `RequestEnqueue()`. Since we're on the host, it takes the `Singleplayer/Host` path and enqueues directly + broadcasts to real clients. The ghost's `Player` must exist in `_playerCollection`.

**Action ID assignment:** Monotonically incrementing `uint _nextId` in `ActionQueueSet`. Assigned at enqueue time. `GetReadyAction()` picks lowest ID across all player queues for deterministic ordering.

### `PlayerChoiceSynchronizer`

Mid-action choice sync (e.g., target selection during card play):
- Local choices broadcast via `PlayerChoiceMessage`
- Remote choices awaited via `WaitForRemoteChoice()` → `Task<PlayerChoiceResult>`
- Handles out-of-order receipt (choice arrives before we start waiting)
- Per-player sequential `choiceId`s

**Ghost peer:** If a ghost's card requires a mid-play choice, the ghost must provide the choice. Since the ghost runs on the host, we can call the choice synchronizer directly.

## Synchronizers (Complete List)

| Synchronizer | Purpose | Ghost Must Act? | Blocking? |
|---|---|---|---|
| `ActionQueueSynchronizer` | Action ordering & execution | Yes (play cards, end turn) | Yes |
| `CombatStateSynchronizer` | Pre-combat player state sync | Yes (send `SyncPlayerDataMessage`) | Yes — hangs at `WaitForSync` |
| `MapSelectionSynchronizer` | Map node vote tracking | Yes (send map vote) | Yes — hangs until all vote |
| `EventSynchronizer` | Shared event option votes; access via `RunManager.Instance.EventSynchronizer` | Yes (shared events only) | Yes — hangs on shared events |
| `TreasureRoomRelicSynchronizer` | Treasure relic bidding | Yes (pick a relic) | Yes — hangs until all bid |
| `ActChangeSynchronizer` | Act transition voting | Yes (vote to advance) | Yes — hangs until all vote |
| `ChecksumTracker` | State divergence detection | No — ghost IS host state | Safe (no separate state) |
| `RewardSynchronizer` | Reward claims | No | No gating |
| `RestSiteSynchronizer` | Rest site choices | No (independent) | No gating |
| `PlayerChoiceSynchronizer` | Mid-action choice sync | If ghost's card needs choice | Per-action |
| `PeerInputSynchronizer` | Cursor/hover sync (cosmetic) | No | No — cosmetic only |

**Common pattern:** All blocking synchronizers use `List<T?>` indexed by player slot (`_playerCollection.GetPlayerSlotIndex(player)`) with `.All(x => x.HasValue)`. Player count comes from `_runState.Players` or `_playerCollection.Players`.

## Local Player Identification

- `LocalContext.GetMe(RunState)` → `Player` or null
- `LocalContext.IsMe(Player)` → bool
- Used throughout the mod to distinguish local vs remote players

## Ghost Peer Testing Findings

### Lobby Initialization & NetService Lifecycle

`RunManager.Instance.NetService` is **NULL during the lobby phase**. The `NetHostGameService` instance lives at `NCharacterSelectScreen.Lobby.NetService` (from the `StartRunLobby` object). It only gets copied to `RunManager.Instance.NetService` when `SetUpNewMultiPlayer()` is called at run start.

For custom run lobbies, `NCharacterSelectScreen` may not be in the scene tree. The reliable approach is to **walk the Godot scene tree** and find any node with a `Lobby` property of type `StartRunLobby`:

```
SceneTree → Root → ... → any Node with property Lobby : StartRunLobby → .NetService
```

This is the only reliable way to access the `NetHostGameService` during the pre-run lobby phase.

### Player Name Resolution

Player names are resolved via `PlatformUtil.GetPlayerName(PlatformType, ulong playerId)`, which calls `SteamFriends.GetFriendPersonaName`. For ghost peers with fake Steam IDs, this returns empty or "unknown" since the ID doesn't correspond to a real Steam account.

**Fix:** Harmony-patch `PlatformUtil.GetPlayerName` to return a custom name when the `playerId` matches the ghost peer's fake ID.

### EventSynchronizer Details

**Access:** `RunManager.Instance.EventSynchronizer`

**Key methods:**

- **`ChooseLocalOption(int)`** — Hardwired to `LocalPlayer`. Not usable for ghost peers; only works for the actual local player.
- **`ChooseOptionForEvent(Player, int)`** (private) — For non-shared events. Requires reflection to call for a ghost player.
- **`PlayerVotedForSharedOptionIndex(Player, uint optionIndex, uint pageIndex)`** (private) — For shared events. Requires reflection to call for a ghost player.

**Proper network sync approach:** Rather than using reflection on private methods, the ghost should use `FeedMessageToHost` with the appropriate message types. This ensures real clients receive the broadcast:

- **`OptionIndexChosenMessage`** — namespace `MegaCrit.Sts2.Core.Multiplayer.Messages.Game.Sync`
  - Fields: `OptionIndexType type`, `uint optionIndex`, `RunLocation location`
  - Used for non-shared events and rest sites

- **`VotedForSharedEventOptionMessage`** — for shared event voting

- **`OptionIndexType`** enum: `Event`, `RestSite`

### MapSelectionSynchronizer Details

**Access:** `RunManager.Instance.MapSelectionSynchronizer`

**Key method:** `PlayerVotedForMapCoord(Player, RunLocation source, MapVote? destination)`

**Proper ghost approach:** Create a `VoteForMapCoordAction(ghost, source, mapVote)` and enqueue via `ActionQueueSynchronizer.RequestEnqueue()`. This ensures the vote is broadcast to all clients.

**`MapVote` struct:**
```csharp
public struct MapVote {
    public int mapGenerationCount;  // must match MapSelectionSynchronizer.MapGenerationCount
    public MapCoord coord;
}
```

The `mapGenerationCount` field **must** match `MapSelectionSynchronizer.MapGenerationCount` or the vote will be rejected as stale.

### TreasureRoomRelicSynchronizer

Relic selection in treasure rooms. Ghost picks a relic via `PickRelicAction(Player, int relicIndex)` enqueued through `ActionQueueSynchronizer.RequestEnqueue()`.

### ActChangeSynchronizer

Act transition voting. Ghost votes to advance via `VoteToMoveToNextActAction(Player)` enqueued through `ActionQueueSynchronizer.RequestEnqueue()`.

### PlayerActionsDisabled

`CombatManager.Instance.PlayerActionsDisabled` is a **UI-level flag** for the local player. It is NOT a game-logic constraint — it controls whether the local player's UI input is accepted.

Ghost peers should **bypass this check entirely** and let the `ActionQueueSynchronizer` handle action queueing. The synchronizer has its own combat state management (`SetCombatState`) that correctly pauses/unpauses player queues regardless of the UI flag.

## Ghost Peer Architecture (Complete Design)

### Goal
Inject an AI-controlled "ghost peer" into a real multiplayer game that syncs with all real clients, without requiring a second game instance.

### How It Works

Since the game is **host-authoritative**, we only need to fake the peer on the **host side**. Real clients receive state updates from the host and don't know (or care) how each peer is connected.

### Injection Steps

**Step 1: Register the ghost peer** (before or during lobby)
- Call `NetHostGameService.OnPeerConnected(GHOST_ID)` → adds to `_connectedPeers`, fires `ClientConnected`
- This triggers `StartRunLobby.OnConnectedToClientAsHost(GHOST_ID)`
- The lobby sends `InitialGameInfoMessage` to `GHOST_ID` via `SendMessageToClient` — **must be intercepted** at transport layer

**Step 2: Simulate lobby join**
- Feed a serialized `ClientLobbyJoinRequestMessage` into `NetHostGameService.OnPacketReceived(GHOST_ID, bytes, ...)` — or directly call the handler
- The lobby calls `TryAddPlayerInFirstAvailableSlot(unlockState, maxAscension, GHOST_ID)`
- The lobby sends `ClientLobbyJoinResponseMessage` back to `GHOST_ID` — **intercepted**
- The lobby calls `SetPeerReadyForBroadcasting(GHOST_ID)` — **critical**, enables broadcasts to ghost
- The lobby broadcasts `PlayerJoinedMessage` to real peers — they see the ghost as a real player

**Step 3: Intercept transport calls**
- Harmony-patch `NetHost.SendMessageToClient(peerId, ...)` to **no-op** when `peerId == GHOST_ID`
- Without this, the transport (SteamHost/ENetHost) will try to send data to a non-existent network peer and crash
- Alternative: also intercept and process response messages locally if needed

**Step 4: Set character and ready up**
- Simulate `LobbyPlayerChangedCharacterMessage` from ghost to pick a character
- Simulate `LobbyPlayerSetReadyMessage` from ghost to ready up
- Both via `OnPacketReceived` or direct handler calls

**Step 5: Run begins**
- Host calls `BeginRun()` — ghost is in the `Players` list
- `LobbyBeginRunMessage` is broadcast (including to real peers) — they see the ghost player
- `Player.CreateForNewRun(character, unlockState, GHOST_ID)` creates the ghost's `Player` object
- Ghost has a full deck, seed, inventory — appears 100% legitimate

**Step 6: AI control during gameplay**
- Since we're on the host, we have direct access to the ghost's `Player` object via `RunState.Players`
- Actions can be enqueued directly via `ActionQueueSynchronizer.RequestEnqueue()` (host path = no network round-trip, no OwnerId validation)
- The host automatically broadcasts `ActionEnqueuedMessage` to real clients
- New MCP endpoints: `ghost_play_card`, `ghost_end_turn`, `ghost_use_potion`, etc.

### Ghost Peer Obligations by Game Phase

| Phase | What Ghost Must Do | How |
|---|---|---|
| **Pre-combat sync** | Send `SyncPlayerDataMessage` with `ghostPlayer.ToSerializable()` | Feed to `CombatStateSynchronizer` via `OnSyncPlayerMessageReceived` or inject into `_syncData` directly |
| **Combat** | Play cards, use potions, end turn | Create `PlayCardAction`/`EndPlayerTurnAction` with ghost `Player`, call `RequestEnqueue()` |
| **End turn** | Submit `ReadyToBeginEnemyTurnAction` after phase-one | Automatically handled if `EndPlayerTurnAction` succeeds (it calls `PlayerCmd.EndTurn`) |
| **Map navigation** | Vote for a map node | Call `MapSelectionSynchronizer.PlayerVotedForMapCoord(ghostPlayer, ...)` or enqueue `VoteForMapCoordAction` |
| **Shared events** | Vote for an option | Feed `VotedForSharedEventOptionMessage` with ghost's choice |
| **Non-shared events** | Choose an option | Feed `OptionIndexChosenMessage` for ghost |
| **Treasure rooms** | Pick a relic | Enqueue `PickRelicAction` via `ActionQueueSynchronizer` |
| **Act transitions** | Vote to advance | Enqueue `VoteToMoveToNextActAction` via `ActionQueueSynchronizer` |
| **Rewards** | Claim or skip | Optional — no gating. Enqueue reward actions if desired |
| **Rest sites** | Choose option | Optional — no gating. Choose rest/upgrade/etc if desired |
| **Checksums** | Nothing | Ghost IS host state — no divergence possible |

### What Real Clients See
- A player that joined normally during lobby
- Picked a character, readied up
- Takes actions during combat (cards, potions, end turn)
- Votes on map, events, treasure
- Fully synchronized — indistinguishable from a human

### Confirmed Safe

- **ChecksumTracker**: Ghost cannot diverge — it IS the host's state. Only real remote clients can trigger `StateDivergence`. No patching needed.
- **ActionQueueSynchronizer**: `EnqueueAction` does not validate `OwnerId`. Host can enqueue actions for any player. Ghost actions get broadcast to clients automatically.
- **PeerInputSynchronizer**: Cosmetic only (cursor position, hover). No blocking. Ghost can skip this entirely — remote players just won't see a cursor for the ghost.

### Required Harmony Patches

1. **`NetHost.SendMessageToClient(peerId, ...)`** — No-op when `peerId == GHOST_ID`. Prevents crash from sending to non-existent network peer.
2. **`CombatStateSynchronizer` injection** — Either:
   a. Harmony-patch `OnSyncPlayerMessageReceived` to inject ghost's data, OR
   b. Directly set `_syncData[GHOST_ID]` via reflection after `StartSync()`
3. **`PlatformUtil.GetPlayerName`** — Return a custom display name for ghost peer IDs. Without this patch, `SteamFriends.GetFriendPersonaName` returns empty/unknown for fake Steam IDs.

**Note:** During the lobby phase, `RunManager.Instance.NetService` is NULL. The `NetHostGameService` must be accessed by walking the Godot scene tree to find a node with a `Lobby` property of type `StartRunLobby`, then reading `.NetService` from it. See "Lobby Initialization & NetService Lifecycle" above.

### Risks
- `SendMessageToClient` to ghost must be intercepted or it crashes the transport
- All blocking synchronizers (map, events, treasure, act change) will hang if ghost doesn't participate — AI must be responsive
- If AI is slow (waiting for MCP response), other players are blocked at voting gates
- Game updates may change internal APIs (version `v0.99.1` specific)
- `RunLobby.ConnectedPlayerIds` must include `GHOST_ID` for `CombatStateSynchronizer` to wait for it — verify ghost is added here during lobby setup
