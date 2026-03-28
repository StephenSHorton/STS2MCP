using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Messages.Game.Sync;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Runs;

namespace STS2_MCP;

public static partial class McpMod
{
    private static Dictionary<string, object?> ExecuteGhostAction(string action, Dictionary<string, JsonElement> data)
    {
        if (!RunManager.Instance.IsInProgress)
            return Error("No run in progress");

        if (!RunManager.Instance.NetService.Type.IsMultiplayer())
            return Error("Not in a multiplayer run");

        var ghost = GetGhostPlayer();
        if (ghost == null)
        {
            // If in a run but ghost not registered yet, try to register
            RegisterGhostPlayers();
            ghost = GetGhostPlayer();
            if (ghost == null)
                return Error("No ghost peer is active. Use 'add_ghost' in the lobby first.");
        }

        return action switch
        {
            "get_state" => BuildGhostState(),
            "play_card" => ExecuteGhostPlayCard(ghost, data),
            "end_turn" => ExecuteGhostEndTurn(ghost),
            "undo_end_turn" => ExecuteGhostUndoEndTurn(ghost),
            "use_potion" => ExecuteUsePotion(ghost, data),
            "map_vote" => ExecuteGhostMapVote(ghost, data),
            "event_choose" => ExecuteGhostEventChoose(ghost, data),
            "treasure_pick" => ExecuteGhostTreasurePick(ghost, data),
            "rest_choose" => ExecuteGhostRestChoose(ghost, data),
            "act_vote" => ExecuteGhostActVote(ghost),
            "claim_reward" => ExecuteClaimReward(data),
            "select_card_reward" => ExecuteSelectCardReward(data),
            "skip_card_reward" => ExecuteSkipCardReward(),
            "proceed" => ExecuteProceed(),
            "remove_ghost" => RemoveGhostPeer(),
            _ => Error($"Unknown ghost action: {action}")
        };
    }

    // ── Combat actions (already work via ActionQueueSynchronizer) ──────────

    private static Dictionary<string, object?> ExecuteGhostPlayCard(Player ghost, Dictionary<string, JsonElement> data)
    {
        if (!CombatManager.Instance.IsInProgress)
            return Error("Not in combat");
        if (!CombatManager.Instance.IsPlayPhase)
            return Error("Not in play phase — cannot act during enemy turn");
        // Note: PlayerActionsDisabled is a UI-level flag for the local player.
        // Ghost bypasses it — the ActionQueueSynchronizer handles queueing properly.
        if (!ghost.Creature.IsAlive)
            return Error("Ghost player is dead — cannot play cards");

        var combatState = ghost.Creature.CombatState;
        if (combatState == null)
            return Error("No combat state");

        if (!data.TryGetValue("card_index", out var indexElem))
            return Error("Missing 'card_index'");

        int cardIndex = indexElem.GetInt32();
        var hand = ghost.PlayerCombatState?.Hand;
        if (hand == null)
            return Error("Ghost has no hand available");

        if (cardIndex < 0 || cardIndex >= hand.Cards.Count)
            return Error($"card_index {cardIndex} out of range (ghost hand has {hand.Cards.Count} cards)");

        var card = hand.Cards[cardIndex];

        if (!card.CanPlay(out var reason, out _))
            return Error($"Card '{card.Title}' cannot be played: {reason}");

        Creature? target = null;
        if (card.TargetType == TargetType.AnyEnemy)
        {
            if (!data.TryGetValue("target", out var targetElem))
                return Error("Card requires a target. Provide 'target' with an entity_id.");

            string targetId = targetElem.GetString() ?? "";
            target = ResolveTarget(combatState, targetId);
            if (target == null)
                return Error($"Target '{targetId}' not found among alive enemies");
        }

        RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(new PlayCardAction(card, target));

        return new Dictionary<string, object?>
        {
            ["status"] = "ok",
            ["message"] = $"Ghost playing '{card.Title}'" +
                (target != null ? $" targeting {SafeGetText(() => target.Monster?.Title) ?? "target"}" : "")
        };
    }

    private static Dictionary<string, object?> ExecuteGhostEndTurn(Player ghost)
    {
        if (!CombatManager.Instance.IsInProgress)
            return Error("Not in combat");
        if (!CombatManager.Instance.IsPlayPhase)
            return Error("Not in play phase");
        if (!ghost.Creature.IsAlive)
            return Error("Ghost player is dead");
        if (CombatManager.Instance.IsPlayerReadyToEndTurn(ghost))
            return Error("Ghost already submitted end turn — use 'undo_end_turn' to retract");

        var combatState = ghost.Creature.CombatState;
        if (combatState == null)
            return Error("No combat state");

        RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(
            new EndPlayerTurnAction(ghost, combatState.RoundNumber));

        return new Dictionary<string, object?>
        {
            ["status"] = "ok",
            ["message"] = "Ghost submitted end turn (waiting for other players)"
        };
    }

    private static Dictionary<string, object?> ExecuteGhostUndoEndTurn(Player ghost)
    {
        if (!CombatManager.Instance.IsInProgress)
            return Error("Not in combat");
        if (!CombatManager.Instance.IsPlayPhase)
            return Error("Not in play phase");
        if (!ghost.Creature.IsAlive)
            return Error("Ghost player is dead");
        if (!CombatManager.Instance.IsPlayerReadyToEndTurn(ghost))
            return Error("Ghost has not submitted end turn — nothing to undo");

        var combatState = ghost.Creature.CombatState;
        if (combatState == null)
            return Error("No combat state");

        RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(
            new UndoEndPlayerTurnAction(ghost, combatState.RoundNumber));

        return new Dictionary<string, object?>
        {
            ["status"] = "ok",
            ["message"] = "Ghost undid end turn"
        };
    }

    // ── Map voting (bypasses NMapScreen UI) ───────────────────────────────

    private static Dictionary<string, object?> ExecuteGhostMapVote(Player ghost, Dictionary<string, JsonElement> data)
    {
        if (!data.TryGetValue("index", out var indexElem))
            return Error("Missing 'index'");
        int nodeIndex = indexElem.GetInt32();

        var runState = RunManager.Instance.DebugOnlyGetState()!;

        // Find travelable map points from the map screen (shared UI, read-only access)
        var mapScreen = NMapScreen.Instance;
        if (mapScreen == null || !mapScreen.IsOpen)
            return Error("Map screen is not open");

        var travelable = FindAll<NMapPoint>(mapScreen)
            .Where(mp => mp.State == MapPointState.Travelable && mp.Point != null)
            .OrderBy(mp => mp.Point!.coord.col)
            .ToList();

        if (travelable.Count == 0)
            return Error("No travelable map nodes available");
        if (nodeIndex < 0 || nodeIndex >= travelable.Count)
            return Error($"node_index {nodeIndex} out of range ({travelable.Count} options)");

        var targetPoint = travelable[nodeIndex].Point!;

        // Build the vote — same as NMapScreen.OnMapPointSelectedLocally but for the ghost player
        var source = new RunLocation(runState.CurrentMapCoord, runState.CurrentActIndex);
        var mapVote = default(MapVote);
        mapVote.coord = targetPoint.coord;
        mapVote.mapGenerationCount = RunManager.Instance.MapSelectionSynchronizer.MapGenerationCount;

        var action = new VoteForMapCoordAction(ghost, source, mapVote);
        RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(action);

        return new Dictionary<string, object?>
        {
            ["status"] = "ok",
            ["message"] = $"Ghost voted for {targetPoint.PointType} at ({targetPoint.coord.col},{targetPoint.coord.row})"
        };
    }

    // ── Event choosing (via FeedMessageToHost for proper network broadcast) ──

    private static Dictionary<string, object?> ExecuteGhostEventChoose(Player ghost, Dictionary<string, JsonElement> data)
    {
        if (!data.TryGetValue("index", out var indexElem))
            return Error("Missing 'index'");
        int optionIndex = indexElem.GetInt32();

        var eventSync = RunManager.Instance.EventSynchronizer;
        if (eventSync == null)
            return Error("EventSynchronizer not available");

        var hostService = RunManager.Instance.NetService as NetHostGameService;
        if (hostService == null)
            return Error("Not on host");

        var runState = RunManager.Instance.DebugOnlyGetState()!;
        var location = new RunLocation(runState.CurrentMapCoord, runState.CurrentActIndex);

        if (eventSync.IsShared)
        {
            // Shared event — feed VotedForSharedEventOptionMessage so host counts the vote
            // and broadcasts to clients
            var pageField = typeof(EventSynchronizer).GetField("_pageIndex",
                BindingFlags.NonPublic | BindingFlags.Instance);
            uint pageIndex = pageField != null ? (uint)pageField.GetValue(eventSync)! : 0;

            var msg = new VotedForSharedEventOptionMessage
            {
                optionIndex = (uint)optionIndex,
                pageIndex = pageIndex,
                location = location
            };
            FeedMessageToHost(hostService, GhostPeerId, msg);

            return new Dictionary<string, object?>
            {
                ["status"] = "ok",
                ["message"] = $"Ghost voted for shared event option {optionIndex}"
            };
        }
        else
        {
            // Non-shared event — feed OptionIndexChosenMessage so host processes it
            // and broadcasts to clients (prevents state divergence)
            var msg = new OptionIndexChosenMessage
            {
                type = OptionIndexType.Event,
                optionIndex = (uint)optionIndex,
                location = location
            };
            FeedMessageToHost(hostService, GhostPeerId, msg);

            return new Dictionary<string, object?>
            {
                ["status"] = "ok",
                ["message"] = $"Ghost chose event option {optionIndex}"
            };
        }
    }

    // ── Treasure relic picking (bypasses UI) ──────────────────────────────

    private static Dictionary<string, object?> ExecuteGhostTreasurePick(Player ghost, Dictionary<string, JsonElement> data)
    {
        if (!data.TryGetValue("index", out var indexElem))
            return Error("Missing 'index'");
        int relicIndex = indexElem.GetInt32();

        var action = new PickRelicAction(ghost, relicIndex);
        RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(action);

        return new Dictionary<string, object?>
        {
            ["status"] = "ok",
            ["message"] = $"Ghost picking treasure relic {relicIndex}"
        };
    }

    // ── Rest site choosing (via FeedMessageToHost for proper network broadcast) ──

    private static Dictionary<string, object?> ExecuteGhostRestChoose(Player ghost, Dictionary<string, JsonElement> data)
    {
        if (!data.TryGetValue("index", out var indexElem))
            return Error("Missing 'index'");
        int optionIndex = indexElem.GetInt32();

        var hostService = RunManager.Instance.NetService as NetHostGameService;
        if (hostService == null)
            return Error("Not on host");

        var runState = RunManager.Instance.DebugOnlyGetState()!;
        var msg = new OptionIndexChosenMessage
        {
            type = OptionIndexType.RestSite,
            optionIndex = (uint)optionIndex,
            location = new RunLocation(runState.CurrentMapCoord, runState.CurrentActIndex)
        };
        FeedMessageToHost(hostService, GhostPeerId, msg);

        return new Dictionary<string, object?>
        {
            ["status"] = "ok",
            ["message"] = $"Ghost chose rest site option {optionIndex}"
        };
    }

    // ── Act transition voting (bypasses UI) ───────────────────────────────

    private static Dictionary<string, object?> ExecuteGhostActVote(Player ghost)
    {
        var action = new VoteToMoveToNextActAction(ghost);
        RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(action);

        return new Dictionary<string, object?>
        {
            ["status"] = "ok",
            ["message"] = "Ghost voted to move to next act"
        };
    }
}
