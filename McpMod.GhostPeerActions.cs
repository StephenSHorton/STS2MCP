using System.Collections.Generic;
using System.Text.Json;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Multiplayer.Game;
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
            "map_vote" => ExecuteChooseMapNode(data),
            "event_choose" => ExecuteChooseEventOption(data),
            "treasure_pick" => ExecuteClaimTreasureRelic(data),
            "rest_choose" => ExecuteChooseRestOption(data),
            "claim_reward" => ExecuteClaimReward(data),
            "select_card_reward" => ExecuteSelectCardReward(data),
            "skip_card_reward" => ExecuteSkipCardReward(),
            "proceed" => ExecuteProceed(),
            "remove_ghost" => RemoveGhostPeer(),
            _ => Error($"Unknown ghost action: {action}")
        };
    }

    private static Dictionary<string, object?> ExecuteGhostPlayCard(Player ghost, Dictionary<string, JsonElement> data)
    {
        if (!CombatManager.Instance.IsInProgress)
            return Error("Not in combat");
        if (!CombatManager.Instance.IsPlayPhase)
            return Error("Not in play phase — cannot act during enemy turn");
        if (CombatManager.Instance.PlayerActionsDisabled)
            return Error("Player actions are currently disabled");
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
        if (CombatManager.Instance.PlayerActionsDisabled)
            return Error("Player actions are currently disabled");
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
}
