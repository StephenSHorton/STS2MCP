using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Entities.Potions;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.MonsterMoves.Intents;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace STS2_MCP;

[ModInitializer("Initialize")]
public static class McpMod
{
    public const string Version = "0.1.0";

    private static HttpListener? _listener;
    private static Thread? _serverThread;
    private static readonly ConcurrentQueue<Action> _mainThreadQueue = new();
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    public static void Initialize()
    {
        try
        {
            // Connect to main thread process frame for action execution
            var tree = (SceneTree)Engine.GetMainLoop();
            tree.Connect(SceneTree.SignalName.ProcessFrame, Callable.From(ProcessMainThreadQueue));

            _listener = new HttpListener();
            _listener.Prefixes.Add("http://localhost:15526/");
            _listener.Start();

            _serverThread = new Thread(ServerLoop)
            {
                IsBackground = true,
                Name = "STS2_MCP_Server"
            };
            _serverThread.Start();

            GD.Print($"[STS2 MCP] v{Version} server started on http://localhost:15526/");
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[STS2 MCP] Failed to start: {ex}");
        }
    }

    private static void ProcessMainThreadQueue()
    {
        int processed = 0;
        while (_mainThreadQueue.TryDequeue(out var action) && processed < 10)
        {
            try { action(); }
            catch (Exception ex) { GD.PrintErr($"[STS2 MCP] Main thread action error: {ex}"); }
            processed++;
        }
    }

    private static Task<T> RunOnMainThread<T>(Func<T> func)
    {
        var tcs = new TaskCompletionSource<T>();
        _mainThreadQueue.Enqueue(() =>
        {
            try { tcs.SetResult(func()); }
            catch (Exception ex) { tcs.SetException(ex); }
        });
        return tcs.Task;
    }

    private static Task RunOnMainThread(Action action)
    {
        var tcs = new TaskCompletionSource<bool>();
        _mainThreadQueue.Enqueue(() =>
        {
            try { action(); tcs.SetResult(true); }
            catch (Exception ex) { tcs.SetException(ex); }
        });
        return tcs.Task;
    }

    private static void ServerLoop()
    {
        while (_listener?.IsListening == true)
        {
            try
            {
                var context = _listener.GetContext();
                // Handle each request asynchronously so we don't block the listener
                ThreadPool.QueueUserWorkItem(_ => HandleRequest(context));
            }
            catch (HttpListenerException) { break; }
            catch (ObjectDisposedException) { break; }
        }
    }

    private static void HandleRequest(HttpListenerContext context)
    {
        try
        {
            var request = context.Request;
            var response = context.Response;
            response.Headers.Add("Access-Control-Allow-Origin", "*");
            response.Headers.Add("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
            response.Headers.Add("Access-Control-Allow-Headers", "Content-Type");

            if (request.HttpMethod == "OPTIONS")
            {
                response.StatusCode = 204;
                response.Close();
                return;
            }

            string path = request.Url?.AbsolutePath ?? "/";

            if (path == "/")
            {
                SendJson(response, new { message = $"Hello from STS2 MCP v{Version}", status = "ok" });
            }
            else if (path == "/api/v1/singleplayer")
            {
                if (request.HttpMethod == "GET")
                    HandleGetState(request, response);
                else if (request.HttpMethod == "POST")
                    HandlePostAction(request, response);
                else
                    SendError(response, 405, "Method not allowed");
            }
            else
            {
                SendError(response, 404, "Not found");
            }
        }
        catch (Exception ex)
        {
            try
            {
                SendError(context.Response, 500, $"Internal error: {ex.Message}");
            }
            catch { /* response may already be closed */ }
        }
    }

    // ==================== GET STATE ====================

    private static void HandleGetState(HttpListenerRequest request, HttpListenerResponse response)
    {
        string format = request.QueryString["format"] ?? "json";
        // locale parameter accepted but currently uses game's active locale
        // string locale = request.QueryString["locale"] ?? "eng";

        try
        {
            var stateTask = RunOnMainThread(() => BuildGameState());
            var state = stateTask.GetAwaiter().GetResult();

            if (format == "markdown")
            {
                string md = FormatAsMarkdown(state);
                SendText(response, md, "text/markdown");
            }
            else
            {
                SendJson(response, state);
            }
        }
        catch (Exception ex)
        {
            SendError(response, 500, $"Failed to read game state: {ex.Message}");
        }
    }

    private static Dictionary<string, object?> BuildGameState()
    {
        var result = new Dictionary<string, object?>();

        if (!RunManager.Instance.IsInProgress)
        {
            result["state_type"] = "menu";
            result["message"] = "No run in progress. Player is in the main menu.";
            return result;
        }

        var runState = RunManager.Instance.DebugOnlyGetState();
        if (runState == null)
        {
            result["state_type"] = "unknown";
            return result;
        }

        var currentRoom = runState.CurrentRoom;
        if (currentRoom is CombatRoom combatRoom)
        {
            result["state_type"] = combatRoom.RoomType.ToString().ToLower(); // monster, elite, boss
            result["battle"] = BuildBattleState(runState, combatRoom);
        }
        else if (currentRoom is EventRoom)
        {
            result["state_type"] = "event";
            result["message"] = "Player is in an event. Event interaction not yet implemented.";
        }
        else if (currentRoom is MapRoom)
        {
            result["state_type"] = "map";
            result["message"] = "Player is viewing the map.";
        }
        else if (currentRoom is MerchantRoom)
        {
            result["state_type"] = "shop";
            result["message"] = "Player is in the shop.";
        }
        else if (currentRoom is RestSiteRoom)
        {
            result["state_type"] = "rest_site";
            result["message"] = "Player is at a rest site.";
        }
        else if (currentRoom is TreasureRoom)
        {
            result["state_type"] = "treasure";
            result["message"] = "Player is in a treasure room.";
        }
        else
        {
            result["state_type"] = "unknown";
            result["room_type"] = currentRoom?.GetType().Name;
        }

        // Common run info
        result["run"] = new Dictionary<string, object?>
        {
            ["act"] = runState.CurrentActIndex + 1,
            ["floor"] = runState.TotalFloor,
            ["ascension"] = runState.AscensionLevel
        };

        return result;
    }

    private static Dictionary<string, object?> BuildBattleState(RunState runState, CombatRoom combatRoom)
    {
        var combatState = CombatManager.Instance.DebugOnlyGetState();
        var battle = new Dictionary<string, object?>();

        if (combatState == null)
        {
            battle["error"] = "Combat state unavailable";
            return battle;
        }

        battle["round"] = combatState.RoundNumber;
        battle["turn"] = combatState.CurrentSide.ToString().ToLower();
        battle["is_play_phase"] = CombatManager.Instance.IsPlayPhase;

        // Player state
        var player = LocalContext.GetMe(runState);
        if (player != null)
        {
            battle["player"] = BuildPlayerState(player);
        }

        // Enemies
        var enemies = new List<Dictionary<string, object?>>();
        var entityCounts = new Dictionary<string, int>();
        foreach (var creature in combatState.Enemies)
        {
            if (creature.IsAlive)
            {
                enemies.Add(BuildEnemyState(creature, entityCounts));
            }
        }
        battle["enemies"] = enemies;

        return battle;
    }

    private static Dictionary<string, object?> BuildPlayerState(Player player)
    {
        var state = new Dictionary<string, object?>();
        var creature = player.Creature;
        var combatState = player.PlayerCombatState;

        state["character"] = SafeGetText(() => player.Character.Title);
        state["hp"] = creature.CurrentHp;
        state["max_hp"] = creature.MaxHp;
        state["block"] = creature.Block;

        if (combatState != null)
        {
            state["energy"] = combatState.Energy;
            state["max_energy"] = combatState.MaxEnergy;

            // Hand
            var hand = new List<Dictionary<string, object?>>();
            int cardIndex = 0;
            foreach (var card in combatState.Hand.Cards)
            {
                hand.Add(BuildCardState(card, cardIndex));
                cardIndex++;
            }
            state["hand"] = hand;

            // Pile counts
            state["draw_pile_count"] = combatState.DrawPile.Cards.Count;
            state["discard_pile_count"] = combatState.DiscardPile.Cards.Count;
            state["exhaust_pile_count"] = combatState.ExhaustPile.Cards.Count;

            // Orbs
            if (combatState.OrbQueue.Orbs.Count > 0)
            {
                var orbs = new List<Dictionary<string, object?>>();
                foreach (var orb in combatState.OrbQueue.Orbs)
                {
                    orbs.Add(new Dictionary<string, object?>
                    {
                        ["id"] = orb.Id.Entry,
                        ["name"] = SafeGetText(() => orb.Id.Entry) // orbs may not have LocString easily
                    });
                }
                state["orbs"] = orbs;
            }
        }

        state["gold"] = player.Gold;

        // Powers (status effects)
        state["powers"] = BuildPowersState(creature);

        // Relics
        var relics = new List<Dictionary<string, object?>>();
        foreach (var relic in player.Relics)
        {
            relics.Add(new Dictionary<string, object?>
            {
                ["id"] = relic.Id.Entry,
                ["name"] = SafeGetText(() => relic.Title),
                ["description"] = SafeGetText(() => relic.DynamicDescription),
                ["counter"] = relic.ShowCounter ? relic.DisplayAmount : null
            });
        }
        state["relics"] = relics;

        // Potions
        var potions = new List<Dictionary<string, object?>>();
        int slotIndex = 0;
        foreach (var potion in player.PotionSlots)
        {
            if (potion != null)
            {
                potions.Add(new Dictionary<string, object?>
                {
                    ["id"] = potion.Id.Entry,
                    ["name"] = SafeGetText(() => potion.Title),
                    ["description"] = SafeGetText(() => potion.DynamicDescription),
                    ["slot"] = slotIndex,
                    ["can_use_in_combat"] = potion.Usage == PotionUsage.CombatOnly || potion.Usage == PotionUsage.AnyTime
                });
            }
            slotIndex++;
        }
        state["potions"] = potions;

        return state;
    }

    private static Dictionary<string, object?> BuildCardState(CardModel card, int index)
    {
        string costDisplay;
        if (card.EnergyCost.CostsX)
            costDisplay = "X";
        else
        {
            int cost = card.EnergyCost.GetAmountToSpend();
            costDisplay = cost.ToString();
        }

        card.CanPlay(out var unplayableReason, out _);

        return new Dictionary<string, object?>
        {
            ["index"] = index,
            ["id"] = card.Id.Entry,
            ["name"] = card.Title,
            ["type"] = card.Type.ToString(),
            ["cost"] = costDisplay,
            ["description"] = SafeGetText(() => card.Description),
            ["target_type"] = card.TargetType.ToString(),
            ["can_play"] = unplayableReason == UnplayableReason.None,
            ["unplayable_reason"] = unplayableReason != UnplayableReason.None ? unplayableReason.ToString() : null,
            ["is_upgraded"] = card.IsUpgraded,
            ["keywords"] = card.Keywords.Where(k => k != CardKeyword.None).Select(k => k.ToString()).ToList()
        };
    }

    private static Dictionary<string, object?> BuildEnemyState(Creature creature, Dictionary<string, int> entityCounts)
    {
        var monster = creature.Monster;
        string baseId = monster?.Id.Entry ?? "unknown";

        // Generate entity_id like "jaw_worm_0"
        if (!entityCounts.TryGetValue(baseId, out int count))
            count = 0;
        entityCounts[baseId] = count + 1;
        string entityId = $"{baseId}_{count}";

        var state = new Dictionary<string, object?>
        {
            ["entity_id"] = entityId,
            ["combat_id"] = creature.CombatId,
            ["name"] = SafeGetText(() => monster?.Title),
            ["hp"] = creature.CurrentHp,
            ["max_hp"] = creature.MaxHp,
            ["block"] = creature.Block,
            ["powers"] = BuildPowersState(creature)
        };

        // Intents
        if (monster?.NextMove is MoveState moveState)
        {
            var intents = new List<Dictionary<string, object?>>();
            foreach (var intent in moveState.Intents)
            {
                var intentData = new Dictionary<string, object?>
                {
                    ["type"] = intent.IntentType.ToString()
                };
                try
                {
                    var targets = creature.CombatState?.PlayerCreatures;
                    if (targets != null)
                    {
                        string label = intent.GetIntentLabel(targets, creature).GetFormattedText();
                        intentData["label"] = StripRichTextTags(label);
                    }
                }
                catch { /* intent label may fail for some types */ }
                intents.Add(intentData);
            }
            state["intents"] = intents;
        }

        return state;
    }

    private static List<Dictionary<string, object?>> BuildPowersState(Creature creature)
    {
        var powers = new List<Dictionary<string, object?>>();
        foreach (var power in creature.Powers)
        {
            if (!power.IsVisible) continue;
            powers.Add(new Dictionary<string, object?>
            {
                ["id"] = power.Id.Entry,
                ["name"] = SafeGetText(() => power.Title),
                ["amount"] = power.DisplayAmount,
                ["type"] = power.Type.ToString(),
                ["description"] = SafeGetText(() => power.SmartDescription)
            });
        }
        return powers;
    }

    // ==================== POST ACTION ====================

    private static void HandlePostAction(HttpListenerRequest request, HttpListenerResponse response)
    {
        string body;
        using (var reader = new StreamReader(request.InputStream, request.ContentEncoding))
            body = reader.ReadToEnd();

        Dictionary<string, JsonElement>? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(body);
        }
        catch
        {
            SendError(response, 400, "Invalid JSON");
            return;
        }

        if (parsed == null || !parsed.TryGetValue("action", out var actionElem))
        {
            SendError(response, 400, "Missing 'action' field");
            return;
        }

        string action = actionElem.GetString() ?? "";

        try
        {
            var resultTask = RunOnMainThread(() => ExecuteAction(action, parsed));
            var result = resultTask.GetAwaiter().GetResult();
            SendJson(response, result);
        }
        catch (Exception ex)
        {
            SendError(response, 500, $"Action failed: {ex.Message}");
        }
    }

    private static Dictionary<string, object?> ExecuteAction(string action, Dictionary<string, JsonElement> data)
    {
        if (!RunManager.Instance.IsInProgress)
            return Error("No run in progress");

        if (!CombatManager.Instance.IsInProgress)
            return Error("Not in combat");

        if (!CombatManager.Instance.IsPlayPhase)
            return Error("Not in play phase — cannot act during enemy turn");

        var runState = RunManager.Instance.DebugOnlyGetState()!;
        var player = LocalContext.GetMe(runState);
        if (player == null)
            return Error("Could not find local player");

        return action switch
        {
            "play_card" => ExecutePlayCard(player, data),
            "end_turn" => ExecuteEndTurn(player),
            _ => Error($"Unknown action: {action}")
        };
    }

    private static Dictionary<string, object?> ExecutePlayCard(Player player, Dictionary<string, JsonElement> data)
    {
        if (CombatManager.Instance.PlayerActionsDisabled)
            return Error("Player actions are currently disabled");

        var combatState = player.Creature.CombatState;
        if (combatState == null)
            return Error("No combat state");

        // Get card by index in hand
        if (!data.TryGetValue("card_index", out var indexElem))
            return Error("Missing 'card_index'");

        int cardIndex = indexElem.GetInt32();
        var hand = player.PlayerCombatState?.Hand;
        if (hand == null)
            return Error("No hand available");

        if (cardIndex < 0 || cardIndex >= hand.Cards.Count)
            return Error($"card_index {cardIndex} out of range (hand has {hand.Cards.Count} cards)");

        var card = hand.Cards[cardIndex];

        if (!card.CanPlay(out var reason, out _))
            return Error($"Card '{card.Title}' cannot be played: {reason}");

        // Resolve target
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

        // Play the card
        TaskHelper.RunSafely(CardCmd.AutoPlay(new BlockingPlayerChoiceContext(), card, target));

        return new Dictionary<string, object?>
        {
            ["status"] = "ok",
            ["message"] = $"Playing '{card.Title}'" + (target != null ? $" targeting {SafeGetText(() => target.Monster?.Title) ?? "target"}" : "")
        };
    }

    private static Dictionary<string, object?> ExecuteEndTurn(Player player)
    {
        if (CombatManager.Instance.PlayerActionsDisabled)
            return Error("Player actions are currently disabled (turn may already be ending)");

        PlayerCmd.EndTurn(player, canBackOut: false);

        return new Dictionary<string, object?>
        {
            ["status"] = "ok",
            ["message"] = "Ending turn"
        };
    }

    private static Creature? ResolveTarget(CombatState combatState, string entityId)
    {
        // Try to match by entity_id pattern: "model_entry_N"
        // First try matching by combat_id if it's a pure number
        if (uint.TryParse(entityId, out uint combatId))
            return combatState.GetCreature(combatId);

        // Match by entity_id pattern (e.g., "jaw_worm_0")
        // We rebuild the entity IDs the same way as BuildEnemyState
        var entityCounts = new Dictionary<string, int>();
        foreach (var creature in combatState.Enemies)
        {
            if (!creature.IsAlive) continue;
            string baseId = creature.Monster?.Id.Entry ?? "unknown";
            if (!entityCounts.TryGetValue(baseId, out int count))
                count = 0;
            entityCounts[baseId] = count + 1;
            string generatedId = $"{baseId}_{count}";

            if (generatedId == entityId)
                return creature;
        }

        return null;
    }

    // ==================== MARKDOWN FORMATTING ====================

    private static string FormatAsMarkdown(Dictionary<string, object?> state)
    {
        var sb = new StringBuilder();
        string stateType = state.TryGetValue("state_type", out var st) ? st?.ToString() ?? "unknown" : "unknown";

        sb.AppendLine($"# Game State: {stateType}");
        sb.AppendLine();

        if (state.TryGetValue("run", out var runObj) && runObj is Dictionary<string, object?> run)
        {
            sb.AppendLine($"**Act {run["act"]}** | Floor {run["floor"]} | Ascension {run["ascension"]}");
            sb.AppendLine();
        }

        if (state.TryGetValue("message", out var msg) && msg != null)
        {
            sb.AppendLine(msg.ToString());
            return sb.ToString();
        }

        if (state.TryGetValue("battle", out var battleObj) && battleObj is Dictionary<string, object?> battle)
        {
            FormatBattleMarkdown(sb, battle);
        }

        return sb.ToString();
    }

    private static void FormatBattleMarkdown(StringBuilder sb, Dictionary<string, object?> battle)
    {
        sb.AppendLine($"**Round {battle["round"]}** | Turn: {battle["turn"]} | Play Phase: {battle["is_play_phase"]}");
        sb.AppendLine();

        if (battle.TryGetValue("player", out var playerObj) && playerObj is Dictionary<string, object?> player)
        {
            sb.AppendLine("## Player");
            sb.AppendLine($"**{player["character"]}** — HP: {player["hp"]}/{player["max_hp"]} | Block: {player["block"]} | Energy: {player["energy"]}/{player["max_energy"]} | Gold: {player["gold"]}");
            sb.AppendLine();

            FormatListSection(sb, "Powers", player, "powers", p => $"- **{p["name"]}** ({p["amount"]}): {p["description"]}");
            FormatListSection(sb, "Relics", player, "relics", r =>
            {
                string counter = r.TryGetValue("counter", out var c) && c != null ? $" [{c}]" : "";
                return $"- **{r["name"]}**{counter}: {r["description"]}";
            });
            FormatListSection(sb, "Potions", player, "potions", p => $"- [{p["slot"]}] **{p["name"]}**: {p["description"]}");

            if (player.TryGetValue("hand", out var handObj) && handObj is List<Dictionary<string, object?>> hand && hand.Count > 0)
            {
                sb.AppendLine("### Hand");
                foreach (var card in hand)
                {
                    string playable = card["can_play"] is true ? "✓" : "✗";
                    string keywords = card.TryGetValue("keywords", out var kw) && kw is List<string> kwList && kwList.Count > 0
                        ? $" [{string.Join(", ", kwList)}]" : "";
                    sb.AppendLine($"- [{card["index"]}] **{card["name"]}** ({card["cost"]} energy) [{card["type"]}] {playable}{keywords} — {card["description"]} (target: {card["target_type"]})");
                }
                sb.AppendLine();
            }

            sb.AppendLine($"Draw: {player["draw_pile_count"]} | Discard: {player["discard_pile_count"]} | Exhaust: {player["exhaust_pile_count"]}");
            sb.AppendLine();
        }

        if (battle.TryGetValue("enemies", out var enemiesObj) && enemiesObj is List<Dictionary<string, object?>> enemies && enemies.Count > 0)
        {
            sb.AppendLine("## Enemies");
            foreach (var enemy in enemies)
            {
                sb.AppendLine($"### {enemy["name"]} (`{enemy["entity_id"]}`)");
                sb.AppendLine($"HP: {enemy["hp"]}/{enemy["max_hp"]} | Block: {enemy["block"]}");

                if (enemy.TryGetValue("intents", out var intentsObj) && intentsObj is List<Dictionary<string, object?>> intents && intents.Count > 0)
                {
                    sb.Append("**Intent:** ");
                    sb.AppendLine(string.Join(", ", intents.Select(i =>
                    {
                        string label = i.TryGetValue("label", out var l) && l != null ? $" — {l}" : "";
                        return $"{i["type"]}{label}";
                    })));
                }

                FormatListSection(sb, "Powers", enemy, "powers", p => $"  - **{p["name"]}** ({p["amount"]}): {p["description"]}");
                sb.AppendLine();
            }
        }
    }

    private static void FormatListSection(StringBuilder sb, string title, Dictionary<string, object?> parent, string key,
        Func<Dictionary<string, object?>, string> formatter)
    {
        if (parent.TryGetValue(key, out var listObj) && listObj is List<Dictionary<string, object?>> list && list.Count > 0)
        {
            sb.AppendLine($"### {title}");
            foreach (var item in list)
                sb.AppendLine(formatter(item));
            sb.AppendLine();
        }
    }

    // ==================== HELPERS ====================

    private static string? SafeGetText(Func<object?> getter)
    {
        try
        {
            var result = getter();
            if (result == null) return null;
            // If it's a LocString, call GetFormattedText
            if (result is MegaCrit.Sts2.Core.Localization.LocString locString)
                return StripRichTextTags(locString.GetFormattedText());
            return result.ToString();
        }
        catch { return null; }
    }

    private static string StripRichTextTags(string text)
    {
        // Remove BBCode-style tags like [color=red], [/color], [img]...[/img], etc.
        var sb = new StringBuilder();
        int i = 0;
        while (i < text.Length)
        {
            if (text[i] == '[')
            {
                int end = text.IndexOf(']', i);
                if (end >= 0) { i = end + 1; continue; }
            }
            sb.Append(text[i]);
            i++;
        }
        return sb.ToString();
    }

    private static void SendJson(HttpListenerResponse response, object data)
    {
        string json = JsonSerializer.Serialize(data, _jsonOptions);
        byte[] buffer = Encoding.UTF8.GetBytes(json);
        response.ContentType = "application/json; charset=utf-8";
        response.ContentLength64 = buffer.Length;
        response.OutputStream.Write(buffer, 0, buffer.Length);
        response.Close();
    }

    private static void SendText(HttpListenerResponse response, string text, string contentType = "text/plain")
    {
        byte[] buffer = Encoding.UTF8.GetBytes(text);
        response.ContentType = $"{contentType}; charset=utf-8";
        response.ContentLength64 = buffer.Length;
        response.OutputStream.Write(buffer, 0, buffer.Length);
        response.Close();
    }

    private static void SendError(HttpListenerResponse response, int statusCode, string message)
    {
        response.StatusCode = statusCode;
        SendJson(response, new Dictionary<string, object?> { ["error"] = message });
    }

    private static Dictionary<string, object?> Error(string message)
    {
        return new Dictionary<string, object?> { ["status"] = "error", ["error"] = message };
    }
}
