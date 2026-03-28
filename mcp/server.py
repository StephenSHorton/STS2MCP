"""MCP server bridge for Slay the Spire 2.

Connects to the STS2_MCP mod's HTTP server and exposes game actions
as MCP tools for Claude Desktop / Claude Code.
"""

import argparse
import asyncio
import json
import sys
from typing import Callable

import httpx
from mcp.server.fastmcp import FastMCP

from run_logger import log_tool_call, log_decision

mcp = FastMCP("sts2")

# ---------------------------------------------------------------------------
# Tool mode registry — tags functions as "sp", "mp", or "shared" so they can
# be selectively registered based on the --mode launch flag.
# ---------------------------------------------------------------------------

_tool_registry: list[tuple[str, Callable, dict]] = []


def _register(mode: str, **kwargs):
    """Decorator that tags a function for deferred registration with mcp.add_tool().

    Args:
        mode: "sp" (singleplayer), "mp" (multiplayer), or "shared".
        **kwargs: Passed through to mcp.add_tool() (e.g. name, description).
    """
    def decorator(fn: Callable) -> Callable:
        _tool_registry.append((mode, fn, kwargs))
        return fn
    return decorator


sp = lambda **kw: _register("sp", **kw)
mp = lambda **kw: _register("mp", **kw)
gp = lambda **kw: _register("gp", **kw)
shared = lambda **kw: _register("shared", **kw)

_base_url: str = "http://localhost:15526"


def _sp_url() -> str:
    return f"{_base_url}/api/v1/singleplayer"


def _mp_url() -> str:
    return f"{_base_url}/api/v1/multiplayer"


def _gp_url() -> str:
    return f"{_base_url}/api/v1/ghost"


async def _get(params: dict | None = None) -> str:
    async with httpx.AsyncClient(timeout=10) as client:
        r = await client.get(_sp_url(), params=params)
        r.raise_for_status()
        log_tool_call("get_state", params or {}, r.text)
        return r.text


async def _post(body: dict) -> str:
    async with httpx.AsyncClient(timeout=10) as client:
        r = await client.post(_sp_url(), json=body)
        r.raise_for_status()
        log_tool_call(body.get("action", "unknown"), body, r.text)
        return r.text


async def _mp_get(params: dict | None = None) -> str:
    async with httpx.AsyncClient(timeout=10) as client:
        r = await client.get(_mp_url(), params=params)
        r.raise_for_status()
        log_tool_call("mp_get_state", params or {}, r.text)
        return r.text


async def _mp_post(body: dict) -> str:
    async with httpx.AsyncClient(timeout=10) as client:
        r = await client.post(_mp_url(), json=body)
        r.raise_for_status()
        log_tool_call("mp_" + body.get("action", "unknown"), body, r.text)
        return r.text


async def _gp_get(params: dict | None = None) -> str:
    async with httpx.AsyncClient(timeout=10) as client:
        r = await client.get(_gp_url(), params=params)
        r.raise_for_status()
        log_tool_call("ghost_get_state", params or {}, r.text)
        return r.text


async def _gp_post(body: dict) -> str:
    async with httpx.AsyncClient(timeout=10) as client:
        r = await client.post(_gp_url(), json=body)
        r.raise_for_status()
        log_tool_call("ghost_" + body.get("action", "unknown"), body, r.text)
        return r.text


def _handle_error(e: Exception) -> str:
    if isinstance(e, httpx.ConnectError):
        return "Error: Cannot connect to STS2_MCP mod. Is the game running with the mod enabled?"
    if isinstance(e, httpx.HTTPStatusError):
        return f"Error: HTTP {e.response.status_code} — {e.response.text}"
    return f"Error: {e}"


# ---------------------------------------------------------------------------
# Smart polling
# ---------------------------------------------------------------------------


async def _get_smart(params: dict | None = None, wait_for_player_turn: bool = True) -> str:
    """Get game state, optionally waiting until it's the player's turn.

    In combat, the game alternates between player and enemy turns. Calling
    get_state during the enemy turn returns a state the AI can't act on,
    wasting a tool call and tokens. This helper polls (up to ~8 seconds)
    until the state is actionable:
      - Play Phase: True (player's turn in combat)
      - A non-combat state (map, rewards, event, etc.)
      - "Combat ended" transition state
    """
    text = await _get(params)

    if not wait_for_player_turn:
        return text

    # Only poll if we're in a combat state that isn't actionable yet
    combat_keywords = ["# Game State: monster", "# Game State: elite", "# Game State: boss"]
    is_combat = any(kw in text for kw in combat_keywords)

    if is_combat and "Play Phase: False" in text:
        for _ in range(8):
            await asyncio.sleep(1.0)
            text = await _get(params)
            # Stop polling if: player turn, combat ended, or state changed to non-combat
            if "Play Phase: True" in text or "Combat ended" in text:
                break
            if not any(kw in text for kw in combat_keywords):
                break

    return text


# ---------------------------------------------------------------------------
# General
# ---------------------------------------------------------------------------


@sp()
async def get_game_state(format: str = "markdown") -> str:
    """Get the current Slay the Spire 2 game state.

    Returns the full game state including player stats, hand, enemies, potions, etc.
    The state_type field indicates the current screen (combat, map, event, shop, etc.).

    In combat, this automatically waits for the player's turn before returning,
    so you don't need to poll repeatedly during enemy turns.

    Args:
        format: "markdown" for human-readable output, "json" for structured data.
    """
    try:
        return await _get_smart({"format": format})
    except Exception as e:
        return _handle_error(e)


@sp()
async def use_potion(slot: int, target: str | None = None) -> str:
    """Use a potion from the player's potion slots.

    Works both during and outside of combat. Combat-only potions require an active battle.

    Args:
        slot: Potion slot index (as shown in game state).
        target: Entity ID of the target enemy (e.g. "JAW_WORM_0"). Required for enemy-targeted potions.
    """
    body: dict = {"action": "use_potion", "slot": slot}
    if target is not None:
        body["target"] = target
    try:
        return await _post(body)
    except Exception as e:
        return _handle_error(e)


@sp()
async def proceed_to_map() -> str:
    """Proceed from the current screen to the map.

    Works from: rewards screen, rest site, shop.
    Does NOT work for events — use event_choose_option() with the Proceed option's index.
    """
    try:
        return await _post({"action": "proceed"})
    except Exception as e:
        return _handle_error(e)


# ---------------------------------------------------------------------------
# Combat (state_type: monster / elite / boss)
# ---------------------------------------------------------------------------


@sp()
async def combat_play_card(card_index: int, target: str | None = None) -> str:
    """[Combat] Play a card from the player's hand.

    Args:
        card_index: Index of the card in hand (0-based, as shown in game state).
        target: Entity ID of the target enemy (e.g. "JAW_WORM_0"). Required for single-target cards.

    Note that the index can change as cards are played - playing a card will shift the indices of remaining cards in hand.
    Refer to the latest game state for accurate indices. New cards are drawn to the right, so playing cards from right to left can help maintain more stable indices for remaining cards.
    """
    body: dict = {"action": "play_card", "card_index": card_index}
    if target is not None:
        body["target"] = target
    try:
        return await _post(body)
    except Exception as e:
        return _handle_error(e)


@sp()
async def combat_end_turn() -> str:
    """[Combat] End the player's current turn."""
    try:
        return await _post({"action": "end_turn"})
    except Exception as e:
        return _handle_error(e)


# ---------------------------------------------------------------------------
# In-Combat Card Selection (state_type: hand_select)
# ---------------------------------------------------------------------------


@sp()
async def combat_select_card(card_index: int) -> str:
    """[Combat Selection] Select a card from hand during an in-combat card selection prompt.

    Used when a card effect asks you to select a card to exhaust, discard, etc.
    This is different from deck_select_card which handles out-of-combat card selection overlays.

    Args:
        card_index: 0-based index of the card in the selectable hand cards (as shown in game state).
    """
    try:
        return await _post({"action": "combat_select_card", "card_index": card_index})
    except Exception as e:
        return _handle_error(e)


@sp()
async def combat_confirm_selection() -> str:
    """[Combat Selection] Confirm the in-combat card selection.

    After selecting the required number of cards from hand (exhaust, discard, etc.),
    use this to confirm the selection. Only works when the confirm button is enabled.
    """
    try:
        return await _post({"action": "combat_confirm_selection"})
    except Exception as e:
        return _handle_error(e)


# ---------------------------------------------------------------------------
# Rewards (state_type: combat_rewards / card_reward)
# ---------------------------------------------------------------------------


@sp()
async def rewards_claim(reward_index: int) -> str:
    """[Rewards] Claim a reward from the post-combat rewards screen.

    Gold, potion, and relic rewards are claimed immediately.
    Card rewards open the card selection screen (state changes to card_reward).

    Args:
        reward_index: 0-based index of the reward on the rewards screen.

    Note that claiming a reward may change the indices of remaining rewards, so refer to the latest game state for accurate indices.
    Claiming from right to left can help maintain more stable indices for remaining rewards, as rewards will always shift left to fill in gaps.
    """
    try:
        return await _post({"action": "claim_reward", "index": reward_index})
    except Exception as e:
        return _handle_error(e)


@sp()
async def rewards_pick_card(card_index: int) -> str:
    """[Rewards] Select a card from the card reward selection screen.

    Args:
        card_index: 0-based index of the card to add to the deck.
    """
    try:
        return await _post({"action": "select_card_reward", "card_index": card_index})
    except Exception as e:
        return _handle_error(e)


@sp()
async def rewards_skip_card() -> str:
    """[Rewards] Skip the card reward without selecting a card."""
    try:
        return await _post({"action": "skip_card_reward"})
    except Exception as e:
        return _handle_error(e)


# ---------------------------------------------------------------------------
# Map (state_type: map)
# ---------------------------------------------------------------------------


@sp()
async def map_choose_node(node_index: int) -> str:
    """[Map] Choose a map node to travel to.

    Args:
        node_index: 0-based index of the node from the next_options list.
    """
    try:
        return await _post({"action": "choose_map_node", "index": node_index})
    except Exception as e:
        return _handle_error(e)


# ---------------------------------------------------------------------------
# Rest Site (state_type: rest_site)
# ---------------------------------------------------------------------------


@sp()
async def rest_choose_option(option_index: int) -> str:
    """[Rest Site] Choose a rest site option (rest, smith, etc.).

    Args:
        option_index: 0-based index of the option from the rest site state.
    """
    try:
        return await _post({"action": "choose_rest_option", "index": option_index})
    except Exception as e:
        return _handle_error(e)


# ---------------------------------------------------------------------------
# Shop (state_type: shop)
# ---------------------------------------------------------------------------


@sp()
async def shop_purchase(item_index: int) -> str:
    """[Shop] Purchase an item from the shop.

    Args:
        item_index: 0-based index of the item from the shop state.
    """
    try:
        return await _post({"action": "shop_purchase", "index": item_index})
    except Exception as e:
        return _handle_error(e)


# ---------------------------------------------------------------------------
# Event (state_type: event)
# ---------------------------------------------------------------------------


@sp()
async def event_choose_option(option_index: int) -> str:
    """[Event] Choose an event option.

    Works for both regular events and ancients (after dialogue ends).
    Also used to click the Proceed option after an event resolves.

    Args:
        option_index: 0-based index of the option from the current event state.
    """
    try:
        return await _post({"action": "choose_event_option", "index": option_index})
    except Exception as e:
        return _handle_error(e)


@sp()
async def event_advance_dialogue() -> str:
    """[Event] Advance ancient event dialogue.

    Click through dialogue text in ancient events. Call repeatedly until options appear.
    """
    try:
        return await _post({"action": "advance_dialogue"})
    except Exception as e:
        return _handle_error(e)


# ---------------------------------------------------------------------------
# Card Selection (state_type: card_select)
# ---------------------------------------------------------------------------


@sp()
async def deck_select_card(card_index: int) -> str:
    """[Card Selection] Select or deselect a card in the card selection screen.

    Used when the game asks you to choose cards from your deck (transform, upgrade,
    remove, discard) or pick a card from offered choices (potions, effects).

    For deck selections: toggles card selection. For choose-a-card: picks immediately.

    Args:
        card_index: 0-based index of the card (as shown in game state).
    """
    try:
        return await _post({"action": "select_card", "index": card_index})
    except Exception as e:
        return _handle_error(e)


@sp()
async def deck_confirm_selection() -> str:
    """[Card Selection] Confirm the current card selection.

    After selecting the required number of cards, use this to confirm.
    If a preview is showing (e.g., transform preview), this confirms the preview.
    Not needed for choose-a-card screens where picking is immediate.
    """
    try:
        return await _post({"action": "confirm_selection"})
    except Exception as e:
        return _handle_error(e)


@sp()
async def deck_cancel_selection() -> str:
    """[Card Selection] Cancel the current card selection.

    If a preview is showing, goes back to the selection grid.
    For choose-a-card screens, clicks the skip button (if available).
    Otherwise, closes the card selection screen (only if cancellation is allowed).
    """
    try:
        return await _post({"action": "cancel_selection"})
    except Exception as e:
        return _handle_error(e)


# ---------------------------------------------------------------------------
# Bundle Selection (state_type: bundle_select)
# ---------------------------------------------------------------------------


@sp()
async def bundle_select(bundle_index: int) -> str:
    """[Bundle Selection] Open a bundle preview.

    Args:
        bundle_index: 0-based index of the bundle.
    """
    try:
        return await _post({"action": "select_bundle", "index": bundle_index})
    except Exception as e:
        return _handle_error(e)


@sp()
async def bundle_confirm_selection() -> str:
    """[Bundle Selection] Confirm the currently previewed bundle."""
    try:
        return await _post({"action": "confirm_bundle_selection"})
    except Exception as e:
        return _handle_error(e)


@sp()
async def bundle_cancel_selection() -> str:
    """[Bundle Selection] Cancel the current bundle preview."""
    try:
        return await _post({"action": "cancel_bundle_selection"})
    except Exception as e:
        return _handle_error(e)


# ---------------------------------------------------------------------------
# Relic Selection (state_type: relic_select)
# ---------------------------------------------------------------------------


@sp()
async def relic_select(relic_index: int) -> str:
    """[Relic Selection] Select a relic from the relic selection screen.

    Used when the game offers a choice of relics (e.g., boss relic rewards).

    Args:
        relic_index: 0-based index of the relic (as shown in game state).
    """
    try:
        return await _post({"action": "select_relic", "index": relic_index})
    except Exception as e:
        return _handle_error(e)


@sp()
async def relic_skip() -> str:
    """[Relic Selection] Skip the relic selection without choosing a relic."""
    try:
        return await _post({"action": "skip_relic_selection"})
    except Exception as e:
        return _handle_error(e)


# ---------------------------------------------------------------------------
# Treasure (state_type: treasure)
# ---------------------------------------------------------------------------


@sp()
async def treasure_claim_relic(relic_index: int) -> str:
    """[Treasure] Claim a relic from the treasure chest.

    The chest is auto-opened when entering the treasure room.
    After claiming, use proceed_to_map() to continue.

    Args:
        relic_index: 0-based index of the relic (as shown in game state).
    """
    try:
        return await _post({"action": "claim_treasure_relic", "index": relic_index})
    except Exception as e:
        return _handle_error(e)


# ---------------------------------------------------------------------------
# Crystal Sphere (state_type: crystal_sphere)
# ---------------------------------------------------------------------------


@sp()
async def crystal_sphere_set_tool(tool: str) -> str:
    """[Crystal Sphere] Switch the active divination tool.

    Args:
        tool: Either "big" or "small".
    """
    try:
        return await _post({"action": "crystal_sphere_set_tool", "tool": tool})
    except Exception as e:
        return _handle_error(e)


@sp()
async def crystal_sphere_click_cell(x: int, y: int) -> str:
    """[Crystal Sphere] Click a hidden cell on the Crystal Sphere grid.

    Args:
        x: Cell x-coordinate.
        y: Cell y-coordinate.
    """
    try:
        return await _post({"action": "crystal_sphere_click_cell", "x": x, "y": y})
    except Exception as e:
        return _handle_error(e)


@sp()
async def crystal_sphere_proceed() -> str:
    """[Crystal Sphere] Continue after the Crystal Sphere minigame finishes."""
    try:
        return await _post({"action": "crystal_sphere_proceed"})
    except Exception as e:
        return _handle_error(e)


# ===========================================================================
# MULTIPLAYER tools — all route through /api/v1/multiplayer
# ===========================================================================


@mp()
async def mp_get_game_state(format: str = "markdown") -> str:
    """[Multiplayer] Get the current multiplayer game state.

    Returns full game state for ALL players: HP, powers, relics, potions,
    plus multiplayer-specific data: map votes, event votes, treasure bids,
    end-turn ready status. Only works during a multiplayer run.

    Args:
        format: "markdown" for human-readable output, "json" for structured data.
    """
    try:
        return await _mp_get({"format": format})
    except Exception as e:
        return _handle_error(e)


@mp()
async def mp_combat_play_card(card_index: int, target: str | None = None) -> str:
    """[Multiplayer Combat] Play a card from the local player's hand.

    Same as singleplayer combat_play_card but routed through the multiplayer
    endpoint for sync safety.

    Args:
        card_index: Index of the card in hand (0-based).
        target: Entity ID of the target enemy (e.g. "JAW_WORM_0"). Required for single-target cards.
    """
    body: dict = {"action": "play_card", "card_index": card_index}
    if target is not None:
        body["target"] = target
    try:
        return await _mp_post(body)
    except Exception as e:
        return _handle_error(e)


@mp()
async def mp_combat_end_turn() -> str:
    """[Multiplayer Combat] Submit end-turn vote.

    In multiplayer, ending the turn is a VOTE — the turn only ends when ALL
    players have submitted. Use mp_combat_undo_end_turn() to retract.
    """
    try:
        return await _mp_post({"action": "end_turn"})
    except Exception as e:
        return _handle_error(e)


@mp()
async def mp_combat_undo_end_turn() -> str:
    """[Multiplayer Combat] Retract end-turn vote.

    If you submitted end turn but want to play more cards, use this to undo.
    Only works if other players haven't all committed yet.
    """
    try:
        return await _mp_post({"action": "undo_end_turn"})
    except Exception as e:
        return _handle_error(e)


@mp()
async def mp_use_potion(slot: int, target: str | None = None) -> str:
    """[Multiplayer] Use a potion from the local player's potion slots.

    Args:
        slot: Potion slot index (as shown in game state).
        target: Entity ID of the target enemy. Required for enemy-targeted potions.
    """
    body: dict = {"action": "use_potion", "slot": slot}
    if target is not None:
        body["target"] = target
    try:
        return await _mp_post(body)
    except Exception as e:
        return _handle_error(e)


@mp()
async def mp_map_vote(node_index: int) -> str:
    """[Multiplayer Map] Vote for a map node to travel to.

    In multiplayer, map selection is a vote — travel happens when all players
    agree. Re-voting for the same node sends a ping to other players.

    Args:
        node_index: 0-based index of the node from the next_options list.
    """
    try:
        return await _mp_post({"action": "choose_map_node", "index": node_index})
    except Exception as e:
        return _handle_error(e)


@mp()
async def mp_event_choose_option(option_index: int) -> str:
    """[Multiplayer Event] Choose or vote for an event option.

    For shared events: this is a vote (resolves when all players vote).
    For individual events: immediate choice, same as singleplayer.

    Args:
        option_index: 0-based index of the option from the current event state.
    """
    try:
        return await _mp_post({"action": "choose_event_option", "index": option_index})
    except Exception as e:
        return _handle_error(e)


@mp()
async def mp_event_advance_dialogue() -> str:
    """[Multiplayer Event] Advance ancient event dialogue."""
    try:
        return await _mp_post({"action": "advance_dialogue"})
    except Exception as e:
        return _handle_error(e)


@mp()
async def mp_rest_choose_option(option_index: int) -> str:
    """[Multiplayer Rest Site] Choose a rest site option (rest, smith, etc.).

    Per-player choice — no voting needed.

    Args:
        option_index: 0-based index of the option.
    """
    try:
        return await _mp_post({"action": "choose_rest_option", "index": option_index})
    except Exception as e:
        return _handle_error(e)


@mp()
async def mp_shop_purchase(item_index: int) -> str:
    """[Multiplayer Shop] Purchase an item from the shop.

    Per-player inventory — no voting needed.

    Args:
        item_index: 0-based index of the item.
    """
    try:
        return await _mp_post({"action": "shop_purchase", "index": item_index})
    except Exception as e:
        return _handle_error(e)


@mp()
async def mp_rewards_claim(reward_index: int) -> str:
    """[Multiplayer Rewards] Claim a reward from the post-combat rewards screen.

    Args:
        reward_index: 0-based index of the reward.
    """
    try:
        return await _mp_post({"action": "claim_reward", "index": reward_index})
    except Exception as e:
        return _handle_error(e)


@mp()
async def mp_rewards_pick_card(card_index: int) -> str:
    """[Multiplayer Rewards] Select a card from the card reward screen.

    Args:
        card_index: 0-based index of the card to add to the deck.
    """
    try:
        return await _mp_post({"action": "select_card_reward", "card_index": card_index})
    except Exception as e:
        return _handle_error(e)


@mp()
async def mp_rewards_skip_card() -> str:
    """[Multiplayer Rewards] Skip the card reward."""
    try:
        return await _mp_post({"action": "skip_card_reward"})
    except Exception as e:
        return _handle_error(e)


@mp()
async def mp_proceed_to_map() -> str:
    """[Multiplayer] Proceed from the current screen to the map.

    Works from: rewards screen, rest site, shop.
    """
    try:
        return await _mp_post({"action": "proceed"})
    except Exception as e:
        return _handle_error(e)


@mp()
async def mp_deck_select_card(card_index: int) -> str:
    """[Multiplayer Card Selection] Select or deselect a card in the card selection screen.

    Args:
        card_index: 0-based index of the card.
    """
    try:
        return await _mp_post({"action": "select_card", "index": card_index})
    except Exception as e:
        return _handle_error(e)


@mp()
async def mp_deck_confirm_selection() -> str:
    """[Multiplayer Card Selection] Confirm the current card selection."""
    try:
        return await _mp_post({"action": "confirm_selection"})
    except Exception as e:
        return _handle_error(e)


@mp()
async def mp_deck_cancel_selection() -> str:
    """[Multiplayer Card Selection] Cancel the current card selection."""
    try:
        return await _mp_post({"action": "cancel_selection"})
    except Exception as e:
        return _handle_error(e)


@mp()
async def mp_bundle_select(bundle_index: int) -> str:
    """[Multiplayer Bundle Selection] Open a bundle preview.

    Args:
        bundle_index: 0-based index of the bundle.
    """
    try:
        return await _mp_post({"action": "select_bundle", "index": bundle_index})
    except Exception as e:
        return _handle_error(e)


@mp()
async def mp_bundle_confirm_selection() -> str:
    """[Multiplayer Bundle Selection] Confirm the currently previewed bundle."""
    try:
        return await _mp_post({"action": "confirm_bundle_selection"})
    except Exception as e:
        return _handle_error(e)


@mp()
async def mp_bundle_cancel_selection() -> str:
    """[Multiplayer Bundle Selection] Cancel the current bundle preview."""
    try:
        return await _mp_post({"action": "cancel_bundle_selection"})
    except Exception as e:
        return _handle_error(e)


@mp()
async def mp_combat_select_card(card_index: int) -> str:
    """[Multiplayer Combat Selection] Select a card from hand during in-combat card selection.

    Args:
        card_index: 0-based index of the card in the selectable hand cards.
    """
    try:
        return await _mp_post({"action": "combat_select_card", "card_index": card_index})
    except Exception as e:
        return _handle_error(e)


@mp()
async def mp_combat_confirm_selection() -> str:
    """[Multiplayer Combat Selection] Confirm the in-combat card selection."""
    try:
        return await _mp_post({"action": "combat_confirm_selection"})
    except Exception as e:
        return _handle_error(e)


@mp()
async def mp_relic_select(relic_index: int) -> str:
    """[Multiplayer Relic Selection] Select a relic (boss relic rewards).

    Args:
        relic_index: 0-based index of the relic.
    """
    try:
        return await _mp_post({"action": "select_relic", "index": relic_index})
    except Exception as e:
        return _handle_error(e)


@mp()
async def mp_relic_skip() -> str:
    """[Multiplayer Relic Selection] Skip the relic selection."""
    try:
        return await _mp_post({"action": "skip_relic_selection"})
    except Exception as e:
        return _handle_error(e)


@mp()
async def mp_treasure_claim_relic(relic_index: int) -> str:
    """[Multiplayer Treasure] Bid on / claim a relic from the treasure chest.

    In multiplayer, this is a bid — if multiple players pick the same relic,
    a "relic fight" determines the winner. Others get consolation prizes.

    Args:
        relic_index: 0-based index of the relic.
    """
    try:
        return await _mp_post({"action": "claim_treasure_relic", "index": relic_index})
    except Exception as e:
        return _handle_error(e)


@mp()
async def mp_crystal_sphere_set_tool(tool: str) -> str:
    """[Multiplayer Crystal Sphere] Switch the active divination tool.

    Args:
        tool: Either "big" or "small".
    """
    try:
        return await _mp_post({"action": "crystal_sphere_set_tool", "tool": tool})
    except Exception as e:
        return _handle_error(e)


@mp()
async def mp_crystal_sphere_click_cell(x: int, y: int) -> str:
    """[Multiplayer Crystal Sphere] Click a hidden cell on the Crystal Sphere grid.

    Args:
        x: Cell x-coordinate.
        y: Cell y-coordinate.
    """
    try:
        return await _mp_post({"action": "crystal_sphere_click_cell", "x": x, "y": y})
    except Exception as e:
        return _handle_error(e)


@mp()
async def mp_crystal_sphere_proceed() -> str:
    """[Multiplayer Crystal Sphere] Continue after the Crystal Sphere minigame finishes."""
    try:
        return await _mp_post({"action": "crystal_sphere_proceed"})
    except Exception as e:
        return _handle_error(e)


# ===========================================================================
# GHOST PEER tools — control AI ghost players via /api/v1/ghost
# ===========================================================================


@gp()
async def ghost_add(character: str = "ironclad") -> str:
    """[Ghost] Add an AI-controlled ghost player to the multiplayer lobby.

    Must be called while in a multiplayer lobby as the host, BEFORE the run starts.
    The ghost appears as a real player to all other connected clients.

    Args:
        character: Character for the ghost. One of: ironclad, silent, defect, regent.
    """
    try:
        return await _gp_post({"action": "add_ghost", "character": character})
    except Exception as e:
        return _handle_error(e)


@gp()
async def ghost_remove() -> str:
    """[Ghost] Remove the active ghost peer."""
    try:
        return await _gp_post({"action": "remove_ghost"})
    except Exception as e:
        return _handle_error(e)


@gp()
async def ghost_get_state(format: str = "markdown") -> str:
    """[Ghost] Get the game state from the ghost player's perspective.

    Shows the ghost's hand, energy, HP, relics, potions, and the current
    game screen (combat, map, event, etc.). Enemies are shared across players.

    Args:
        format: "markdown" for human-readable output, "json" for structured data.
    """
    try:
        return await _gp_get({"format": format})
    except Exception as e:
        return _handle_error(e)


@gp()
async def ghost_play_card(card_index: int, target: str | None = None) -> str:
    """[Ghost Combat] Play a card from the ghost player's hand.

    Args:
        card_index: Index of the card in the ghost's hand (0-based).
        target: Entity ID of the target enemy (e.g. "JAW_WORM_0"). Required for single-target cards.
    """
    body: dict = {"action": "play_card", "card_index": card_index}
    if target is not None:
        body["target"] = target
    try:
        return await _gp_post(body)
    except Exception as e:
        return _handle_error(e)


@gp()
async def ghost_end_turn() -> str:
    """[Ghost Combat] Submit end-turn vote for the ghost player.

    In multiplayer, the turn ends when ALL players (including the ghost) submit.
    """
    try:
        return await _gp_post({"action": "end_turn"})
    except Exception as e:
        return _handle_error(e)


@gp()
async def ghost_undo_end_turn() -> str:
    """[Ghost Combat] Retract the ghost's end-turn vote."""
    try:
        return await _gp_post({"action": "undo_end_turn"})
    except Exception as e:
        return _handle_error(e)


@gp()
async def ghost_use_potion(slot: int, target: str | None = None) -> str:
    """[Ghost] Use a potion from the ghost's potion slots.

    Args:
        slot: Potion slot index.
        target: Entity ID of the target enemy. Required for enemy-targeted potions.
    """
    body: dict = {"action": "use_potion", "slot": slot}
    if target is not None:
        body["target"] = target
    try:
        return await _gp_post(body)
    except Exception as e:
        return _handle_error(e)


@gp()
async def ghost_map_vote(node_index: int) -> str:
    """[Ghost Map] Vote for a map node on behalf of the ghost.

    The ghost MUST vote — map travel requires all players to agree.

    Args:
        node_index: 0-based index of the node from the next_options list.
    """
    try:
        return await _gp_post({"action": "map_vote", "index": node_index})
    except Exception as e:
        return _handle_error(e)


@gp()
async def ghost_event_choose(option_index: int) -> str:
    """[Ghost Event] Choose or vote for an event option on behalf of the ghost.

    For shared events: the ghost MUST vote or the game hangs.
    For individual events: immediate choice.

    Args:
        option_index: 0-based index of the event option.
    """
    try:
        return await _gp_post({"action": "event_choose", "index": option_index})
    except Exception as e:
        return _handle_error(e)


@gp()
async def ghost_treasure_pick(relic_index: int) -> str:
    """[Ghost Treasure] Pick a relic from the treasure chest on behalf of the ghost.

    The ghost MUST pick — treasure bidding requires all players to bid.

    Args:
        relic_index: 0-based index of the relic.
    """
    try:
        return await _gp_post({"action": "treasure_pick", "index": relic_index})
    except Exception as e:
        return _handle_error(e)


@gp()
async def ghost_rest_choose(option_index: int) -> str:
    """[Ghost Rest Site] Choose a rest site option for the ghost.

    Per-player choice — no voting needed.

    Args:
        option_index: 0-based index of the option.
    """
    try:
        return await _gp_post({"action": "rest_choose", "index": option_index})
    except Exception as e:
        return _handle_error(e)


@gp()
async def ghost_rewards_claim(reward_index: int) -> str:
    """[Ghost Rewards] Claim a reward for the ghost.

    Args:
        reward_index: 0-based index of the reward.
    """
    try:
        return await _gp_post({"action": "claim_reward", "index": reward_index})
    except Exception as e:
        return _handle_error(e)


@gp()
async def ghost_rewards_pick_card(card_index: int) -> str:
    """[Ghost Rewards] Select a card reward for the ghost.

    Args:
        card_index: 0-based index of the card.
    """
    try:
        return await _gp_post({"action": "select_card_reward", "card_index": card_index})
    except Exception as e:
        return _handle_error(e)


@gp()
async def ghost_rewards_skip_card() -> str:
    """[Ghost Rewards] Skip the card reward for the ghost."""
    try:
        return await _gp_post({"action": "skip_card_reward"})
    except Exception as e:
        return _handle_error(e)


@gp()
async def ghost_proceed() -> str:
    """[Ghost] Proceed from the current screen to the map for the ghost."""
    try:
        return await _gp_post({"action": "proceed"})
    except Exception as e:
        return _handle_error(e)


# ---------------------------------------------------------------------------
# Decision Logging
# ---------------------------------------------------------------------------


@shared()
async def log_agent_decision(decision: str) -> str:
    """Log your reasoning before a key decision.

    Call this BEFORE every significant action (playing cards, choosing a path,
    picking a card reward, etc.) to record why you chose that action.

    Args:
        decision: Your reasoning — what the situation is, what you considered, and why you chose this action.
                  Include a brief context label at the start (e.g. "Combat turn 3: ...", "Card reward: ...", "Map: ...").
    """
    log_decision("", decision)
    return "Decision logged."


def main():
    parser = argparse.ArgumentParser(description="STS2 MCP Server")
    parser.add_argument("--port", type=int, default=15526, help="Game HTTP server port")
    parser.add_argument("--host", type=str, default="localhost", help="Game HTTP server host")
    parser.add_argument(
        "--mode",
        choices=["singleplayer", "multiplayer", "ghost", "all"],
        default="all",
        help="Which tool set to load (default: all)",
    )
    args = parser.parse_args()

    global _base_url
    _base_url = f"http://{args.host}:{args.port}"

    # Register tools matching the selected mode
    allowed = {"shared"}
    if args.mode in ("singleplayer", "all"):
        allowed.add("sp")
    if args.mode in ("multiplayer", "all"):
        allowed.add("mp")
    if args.mode in ("ghost", "all"):
        allowed.add("gp")

    for mode, fn, kwargs in _tool_registry:
        if mode in allowed:
            mcp.add_tool(fn, **kwargs)

    mcp.run(transport="stdio")


if __name__ == "__main__":
    main()
