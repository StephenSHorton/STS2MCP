using System;
using System.Collections.Generic;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace STS2_MCP;

public static partial class McpMod
{
    /// <summary>
    /// Build game state from the ghost player's perspective.
    /// Mirrors BuildMultiplayerGameState but centered on the ghost.
    /// </summary>
    private static Dictionary<string, object?> BuildGhostState()
    {
        var ghost = GetGhostPlayer();
        if (ghost == null)
            return Error("No ghost peer is active");

        if (!RunManager.Instance.IsInProgress)
            return Error("No run in progress");

        var runState = RunManager.Instance.DebugOnlyGetState()!;

        var result = new Dictionary<string, object?>
        {
            ["game_mode"] = "ghost_peer",
            ["ghost_character"] = ghost.Character.Title,
            ["ghost_alive"] = ghost.Creature.IsAlive
        };

        // Ghost's slot index in RunState.Players
        for (int i = 0; i < runState.Players.Count; i++)
        {
            if (runState.Players[i] == ghost)
            {
                result["ghost_player_slot"] = i;
                break;
            }
        }

        // Room-specific state
        var currentRoom = runState.CurrentRoom;

        if (currentRoom is CombatRoom combatRoom && CombatManager.Instance.IsInProgress)
        {
            result["state_type"] = combatRoom.RoomType.ToString().ToLower();
            result["combat"] = BuildGhostCombatState(ghost, runState);
        }
        else if (currentRoom is MapRoom)
        {
            result["state_type"] = "map";
            result["map"] = BuildMapState(runState);
        }
        else if (currentRoom is EventRoom)
        {
            result["state_type"] = "event";
            result["event"] = BuildGhostEventState(ghost);
        }
        else if (currentRoom is MerchantRoom)
        {
            result["state_type"] = "shop";
        }
        else if (currentRoom is RestSiteRoom)
        {
            result["state_type"] = "rest_site";
        }
        else if (currentRoom is TreasureRoom)
        {
            result["state_type"] = "treasure";
        }
        else
        {
            result["state_type"] = currentRoom != null ? "unknown" : "menu";
        }

        // Run info
        result["run"] = new Dictionary<string, object?>
        {
            ["act"] = runState.CurrentActIndex + 1,
            ["floor"] = runState.TotalFloor,
            ["ascension"] = runState.AscensionLevel
        };

        // All players summary
        result["players"] = BuildAllPlayersState(runState);

        return result;
    }

    /// <summary>
    /// Build event state from the ghost's perspective using EventSynchronizer.
    /// </summary>
    private static Dictionary<string, object?> BuildGhostEventState(MegaCrit.Sts2.Core.Entities.Players.Player ghost)
    {
        var eventInfo = new Dictionary<string, object?>();
        try
        {
            var eventSync = RunManager.Instance.EventSynchronizer;
            if (eventSync == null)
            {
                eventInfo["error"] = "EventSynchronizer not available";
                return eventInfo;
            }

            var eventModel = eventSync.GetEventForPlayer(ghost);
            if (eventModel == null)
            {
                eventInfo["error"] = "No event for ghost player";
                return eventInfo;
            }

            eventInfo["name"] = SafeGetText(() => eventModel.Title) ?? "Unknown Event";
            eventInfo["is_shared"] = eventSync.IsShared;

            var options = new List<Dictionary<string, object?>>();
            if (eventModel.CurrentOptions != null)
            {
                for (int i = 0; i < eventModel.CurrentOptions.Count; i++)
                {
                    var opt = eventModel.CurrentOptions[i];
                    options.Add(new Dictionary<string, object?>
                    {
                        ["index"] = i,
                        ["text"] = SafeGetText(() => opt.Title) ?? $"Option {i}",
                        ["description"] = SafeGetText(() => opt.Description) ?? "",
                        ["is_locked"] = opt.IsLocked,
                        ["is_proceed"] = opt.IsProceed
                    });
                }
            }
            eventInfo["options"] = options;
        }
        catch (Exception ex)
        {
            eventInfo["error"] = $"Failed to read event state: {ex.Message}";
        }
        return eventInfo;
    }

    /// <summary>
    /// Build combat state showing the ghost's hand, energy, piles, plus shared enemy state.
    /// </summary>
    private static Dictionary<string, object?> BuildGhostCombatState(MegaCrit.Sts2.Core.Entities.Players.Player ghost, RunState runState)
    {
        var combatState = CombatManager.Instance.DebugOnlyGetState();
        if (combatState == null)
            return new Dictionary<string, object?> { ["error"] = "Combat state unavailable" };

        var battle = new Dictionary<string, object?>
        {
            ["round"] = combatState.RoundNumber,
            ["turn"] = combatState.CurrentSide.ToString().ToLower(),
            ["is_play_phase"] = CombatManager.Instance.IsPlayPhase,
            ["ghost_ready_to_end_turn"] = CombatManager.Instance.IsPlayerReadyToEndTurn(ghost),
            ["all_players_ready"] = CombatManager.Instance.AllPlayersReadyToEndTurn()
        };

        // Ghost's full combat data (same structure as BuildPlayerState for local player)
        var ghostState = BuildPlayerState(ghost);
        ghostState["is_local"] = false;
        ghostState["is_ghost"] = true;
        ghostState["is_alive"] = ghost.Creature.IsAlive;
        ghostState["is_ready_to_end_turn"] = CombatManager.Instance.IsPlayerReadyToEndTurn(ghost);
        battle["ghost"] = ghostState;

        // All players with their status
        var players = new List<Dictionary<string, object?>>();
        foreach (var player in runState.Players)
        {
            bool isLocal = LocalContext.IsMe(player);
            bool isGhost = player == ghost;
            var playerState = (isLocal || isGhost) ? BuildPlayerState(player) : BuildPlayerStateSummary(player);
            playerState["is_local"] = isLocal;
            playerState["is_ghost"] = isGhost;
            playerState["is_alive"] = player.Creature.IsAlive;
            playerState["is_ready_to_end_turn"] = CombatManager.Instance.IsPlayerReadyToEndTurn(player);
            players.Add(playerState);
        }
        battle["players"] = players;

        // Enemies (shared across all players)
        var enemies = new List<Dictionary<string, object?>>();
        var entityCounts = new Dictionary<string, int>();
        foreach (var creature in combatState.Enemies)
        {
            if (creature.IsAlive)
                enemies.Add(BuildEnemyState(creature, entityCounts));
        }
        battle["enemies"] = enemies;

        return battle;
    }
}
