using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Multiplayer.Messages.Game;
using MegaCrit.Sts2.Core.Multiplayer.Messages.Lobby;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Multiplayer.Transport;
using MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect;
using MegaCrit.Sts2.Core.Platform;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Unlocks;

namespace STS2_MCP;

public static partial class McpMod
{
    // Ghost peer ID — arbitrary unique ulong outside the Steam ID range
    internal const ulong GhostPeerId = 0xDEAD_BEEF_CAFE_0001;

    // Active ghost players keyed by peer ID
    private static readonly Dictionary<ulong, Player> _ghostPlayers = new();

    // Set of ghost peer IDs for fast Harmony patch lookup
    private static readonly HashSet<ulong> _ghostPeerIds = new();

    /// <summary>
    /// Find the lobby NetService by walking the Godot scene tree and checking
    /// for any node with a Lobby property (StartRunLobby). Works for all lobby
    /// types (character select, custom run, etc.).
    /// </summary>
    private static INetGameService? FindLobbyNetService()
    {
        var tree = Engine.GetMainLoop() as SceneTree;
        if (tree?.Root == null) return null;

        return FindLobbyInTree(tree.Root);
    }

    private static INetGameService? FindLobbyInTree(Node node)
    {
        // Check if this node has a Lobby property of type StartRunLobby
        var lobbyProp = node.GetType().GetProperty("Lobby",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (lobbyProp != null && typeof(StartRunLobby).IsAssignableFrom(lobbyProp.PropertyType))
        {
            var lobby = lobbyProp.GetValue(node) as StartRunLobby;
            if (lobby?.NetService != null)
                return lobby.NetService;
        }

        foreach (var child in node.GetChildren())
        {
            var result = FindLobbyInTree(child);
            if (result != null) return result;
        }
        return null;
    }

    /// <summary>
    /// Dump top-level scene tree node types for debugging lobby detection.
    /// </summary>
    private static string DiagnosticSceneTreeDump()
    {
        try
        {
            var tree = Engine.GetMainLoop() as SceneTree;
            if (tree?.Root == null) return "no scene tree";

            var names = new List<string>();
            CollectNodeTypes(tree.Root, names, 0, maxDepth: 3);
            return string.Join(", ", names);
        }
        catch (Exception ex)
        {
            return $"error: {ex.Message}";
        }
    }

    private static void CollectNodeTypes(Node node, List<string> names, int depth, int maxDepth)
    {
        names.Add($"{node.GetType().Name}({node.Name})");
        if (depth >= maxDepth || names.Count > 50) return;
        foreach (var child in node.GetChildren())
            CollectNodeTypes(child, names, depth + 1, maxDepth);
    }

    /// <summary>
    /// Harmony patch: no-op SendMessageToClient for ghost peer IDs.
    /// Prevents crashes from sending to non-existent network connections.
    /// </summary>
    [HarmonyPatch]
    private static class GhostPeerTransportPatch
    {
        static IEnumerable<MethodBase> TargetMethods()
        {
            var netHostType = typeof(NetHost);
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try { types = assembly.GetTypes(); }
                catch { continue; }

                foreach (var type in types)
                {
                    if (type.IsSubclassOf(netHostType) && !type.IsAbstract)
                    {
                        var method = type.GetMethod("SendMessageToClient",
                            BindingFlags.Public | BindingFlags.Instance);
                        if (method != null)
                            yield return method;
                    }
                }
            }
        }

        static bool Prefix(ulong peerId)
        {
            return !_ghostPeerIds.Contains(peerId);
        }
    }

    /// <summary>
    /// Harmony patch: prevent disconnecting ghost peers.
    /// </summary>
    [HarmonyPatch]
    private static class GhostPeerDisconnectPatch
    {
        static IEnumerable<MethodBase> TargetMethods()
        {
            var netHostType = typeof(NetHost);
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try { types = assembly.GetTypes(); }
                catch { continue; }

                foreach (var type in types)
                {
                    if (type.IsSubclassOf(netHostType) && !type.IsAbstract)
                    {
                        var method = type.GetMethod("DisconnectClient",
                            BindingFlags.Public | BindingFlags.Instance);
                        if (method != null)
                            yield return method;
                    }
                }
            }
        }

        static bool Prefix(ulong peerId)
        {
            return !_ghostPeerIds.Contains(peerId);
        }
    }

    /// <summary>
    /// Harmony postfix: return "Claude" as the player name for ghost peer IDs.
    /// </summary>
    [HarmonyPatch(typeof(PlatformUtil), "GetPlayerName", new Type[] { typeof(PlatformType), typeof(ulong) })]
    private static class GhostPlayerNamePatch
    {
        static bool Prefix(ulong playerId, ref string __result)
        {
            if (_ghostPeerIds.Contains(playerId))
            {
                __result = "Claude";
                return false; // skip original
            }
            return true;
        }
    }

    /// <summary>
    /// Harmony postfix: after CombatStateSynchronizer.StartSync(), inject ghost player data
    /// so the sync doesn't hang waiting for the ghost's SyncPlayerDataMessage.
    /// </summary>
    [HarmonyPatch(typeof(CombatStateSynchronizer), "StartSync")]
    private static class GhostCombatSyncPatch
    {
        static void Postfix()
        {
            try { InjectGhostCombatSync(); }
            catch (Exception ex) { GD.PrintErr($"[STS2 MCP] Ghost combat sync injection failed: {ex}"); }
        }
    }

    /// <summary>
    /// Add a ghost peer to the current multiplayer lobby.
    /// Must be called while in a lobby (before run starts) and as the host.
    /// </summary>
    internal static Dictionary<string, object?> AddGhostPeer(string characterName)
    {
        if (RunManager.Instance == null)
            return Error("RunManager.Instance is null — game not fully initialized");

        // During lobby phase, RunManager.NetService is null — find it via scene tree
        var netService = RunManager.Instance.NetService ?? FindLobbyNetService();
        if (netService == null)
        {
            // Diagnostic: dump scene tree node types to help debug
            var diag = DiagnosticSceneTreeDump();
            return Error($"NetService is null — lobby not found in scene tree. Nodes: {diag}");
        }

        if (!netService.Type.IsMultiplayer())
            return Error($"Not in a multiplayer session (NetService.Type = {netService.Type})");

        if (netService.Type != NetGameType.Host)
            return Error("Only the host can add ghost peers");

        if (_ghostPeerIds.Contains(GhostPeerId))
            return Error("Ghost peer is already active");

        CharacterModel? character = ResolveCharacterModel(characterName);
        if (character == null)
            return Error($"Unknown character: {characterName}. Valid: ironclad, silent, defect, regent");

        var hostService = (NetHostGameService)netService;

        // Step 1: Register ghost as a connected peer
        _ghostPeerIds.Add(GhostPeerId);
        hostService.OnPeerConnected(GhostPeerId);

        // Step 2: Simulate lobby join via serialized message
        var joinMsg = new ClientLobbyJoinRequestMessage
        {
            unlockState = UnlockState.all.ToSerializable(),
            maxAscensionUnlocked = 20
        };
        FeedMessageToHost(hostService, GhostPeerId, joinMsg);

        // Step 3: Change character from default (Ironclad) if needed
        if (characterName.ToLower() != "ironclad")
        {
            var charMsg = new LobbyPlayerChangedCharacterMessage
            {
                character = character
            };
            FeedMessageToHost(hostService, GhostPeerId, charMsg);
        }

        // Step 4: Ready up
        var readyMsg = new LobbyPlayerSetReadyMessage
        {
            ready = true
        };
        FeedMessageToHost(hostService, GhostPeerId, readyMsg);

        GD.Print($"[STS2 MCP] Ghost peer added: {characterName} (ID: {GhostPeerId})");

        return new Dictionary<string, object?>
        {
            ["status"] = "ok",
            ["message"] = $"Ghost peer added as {characterName}",
            ["ghost_id"] = GhostPeerId
        };
    }

    /// <summary>
    /// Feed a serialized message to the host as if it came from a specific peer.
    /// </summary>
    private static void FeedMessageToHost<T>(NetHostGameService hostService, ulong senderId, T message) where T : INetMessage
    {
        var busField = typeof(NetHostGameService).GetField("_messageBus",
            BindingFlags.NonPublic | BindingFlags.Instance);
        var messageBus = (NetMessageBus)busField!.GetValue(hostService)!;

        int length;
        byte[] bytes = messageBus.SerializeMessage(senderId, message, out length);

        byte[] packet = new byte[length];
        Array.Copy(bytes, packet, length);

        hostService.OnPacketReceived(senderId, packet, message.Mode, message.Mode.ToChannelId());
    }

    /// <summary>
    /// Inject ghost player's SyncPlayerDataMessage into the combat sync flow.
    /// Called automatically via Harmony postfix on StartSync().
    /// </summary>
    private static void InjectGhostCombatSync()
    {
        // Ensure ghost players are registered (they may not be yet at first sync)
        if (_ghostPlayers.Count == 0 && _ghostPeerIds.Count > 0)
            RegisterGhostPlayers();

        if (_ghostPlayers.Count == 0) return;

        var hostService = RunManager.Instance.NetService as NetHostGameService;
        if (hostService == null) return;

        foreach (var kvp in _ghostPlayers)
        {
            var syncMsg = new SyncPlayerDataMessage
            {
                player = kvp.Value.ToSerializable()
            };
            FeedMessageToHost(hostService, kvp.Key, syncMsg);
        }
    }

    /// <summary>
    /// Called when a multiplayer run starts to look up and register ghost Player objects.
    /// The lobby flow already created them — we just need to find them in RunState.Players.
    /// </summary>
    internal static void RegisterGhostPlayers()
    {
        if (_ghostPeerIds.Count == 0) return;

        var runState = RunManager.Instance.DebugOnlyGetState();
        if (runState == null) return;

        foreach (var peerId in _ghostPeerIds)
        {
            var player = runState.GetPlayer(peerId);
            if (player != null)
            {
                _ghostPlayers[peerId] = player;
                GD.Print($"[STS2 MCP] Ghost player registered for run: {player.Character.Title} (NetId: {peerId})");
            }
            else
            {
                GD.PrintErr($"[STS2 MCP] Ghost peer {peerId} not found in RunState.Players after run start!");
            }
        }
    }

    /// <summary>
    /// Remove the ghost peer and clean up.
    /// </summary>
    internal static Dictionary<string, object?> RemoveGhostPeer()
    {
        if (!_ghostPeerIds.Contains(GhostPeerId))
            return Error("No ghost peer is active");

        // If in an active run, simulate a disconnect so the game stops waiting for the ghost
        if (RunManager.Instance.IsInProgress)
        {
            var hostService = RunManager.Instance.NetService as NetHostGameService;
            if (hostService != null)
            {
                // Temporarily remove from ghost IDs so the DisconnectClient patch doesn't block
                _ghostPeerIds.Remove(GhostPeerId);
                try
                {
                    // Use reflection to construct NetErrorInfo(NetError.Quit, true)
                    // since the types are in an ambiguous namespace
                    var netErrorType = hostService.GetType().Assembly.GetType("MegaCrit.Sts2.Core.Multiplayer.NetError")
                        ?? hostService.GetType().Assembly.GetTypes().First(t => t.Name == "NetError" && t.IsEnum);
                    var netErrorInfoType = hostService.GetType().Assembly.GetTypes().First(t => t.Name == "NetErrorInfo");
                    var quitValue = Enum.Parse(netErrorType, "Quit");
                    var errorInfo = Activator.CreateInstance(netErrorInfoType, quitValue, true);
                    var disconnectMethod = hostService.GetType().GetMethod("OnPeerDisconnected",
                        BindingFlags.Public | BindingFlags.Instance);
                    disconnectMethod!.Invoke(hostService, new object[] { GhostPeerId, errorInfo! });
                    GD.Print($"[STS2 MCP] Ghost peer disconnected from active run");
                }
                catch (Exception ex)
                {
                    GD.PrintErr($"[STS2 MCP] Ghost disconnect failed: {ex.Message}");
                }
            }
        }

        _ghostPlayers.Remove(GhostPeerId);
        _ghostPeerIds.Remove(GhostPeerId);

        GD.Print($"[STS2 MCP] Ghost peer removed (ID: {GhostPeerId})");

        return new Dictionary<string, object?>
        {
            ["status"] = "ok",
            ["message"] = "Ghost peer removed and disconnected"
        };
    }

    internal static Player? GetGhostPlayer()
    {
        _ghostPlayers.TryGetValue(GhostPeerId, out var player);
        return player;
    }

    internal static bool HasGhostPeer() => _ghostPeerIds.Contains(GhostPeerId);

    private static CharacterModel? ResolveCharacterModel(string name)
    {
        return name.ToLower() switch
        {
            "ironclad" => ModelDb.Character<Ironclad>(),
            "silent" => ModelDb.Character<Silent>(),
            "defect" => ModelDb.Character<Defect>(),
            "regent" => ModelDb.Character<Regent>(),
            _ => null
        };
    }
}
