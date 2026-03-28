# Decompiled Game Documentation

Reverse-engineered documentation of Slay the Spire 2 internals, organized by game system. Each document records the game version it was analyzed against — findings may change across updates.

**Current game version**: `v0.99.1`

## Index

| Area | File | Key Types | Version |
|------|------|-----------|---------|
| Multiplayer & Networking | [multiplayer.md](multiplayer.md) | NetHostGameService, NetClientData, NetMessageBus, INetGameService, NetHost, StartRunLobby, LobbyPlayer, RunLobby, IPlayerCollection, ActionQueueSynchronizer, ActionQueueSet, CombatManager, PlayerCmd, GameAction, PlayCardAction, EndPlayerTurnAction, CombatStateSynchronizer, PlayerChoiceSynchronizer | v0.99.1 |
