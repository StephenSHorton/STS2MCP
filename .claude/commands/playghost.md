Control the AI ghost peer in a Slay the Spire 2 multiplayer game using the ghost MCP tools (`ghost_*`).

## Setup
1. Read `AGENTS.md` for general strategy and MCP calling tips.
2. Read `GUIDE.md` for hero-specific strategies. If the ghost's hero isn't covered, adapt.
3. Call `ghost_get_state(format="markdown")` to see the current state from the ghost's perspective.

## Adding the Ghost
If no ghost is active yet, call `ghost_add(character)` while in the multiplayer lobby.
Valid characters: ironclad, silent, defect, regent.
The ghost auto-joins the lobby, picks the character, and readies up.

## Gameplay Loop

The ghost is a full multiplayer player. It MUST participate in all voting gates or the game hangs for everyone.

### Combat
- Call `ghost_get_state()` to see the ghost's hand, energy, enemies, and status.
- Play cards with `ghost_play_card(card_index, target?)` — play from right-to-left to avoid index shifts.
- Use potions with `ghost_use_potion(slot, target?)` — use buff potions BEFORE playing cards.
- End turn with `ghost_end_turn()` — this is a vote; the turn ends when ALL players submit.
- Undo with `ghost_undo_end_turn()` if you need to play more cards.

### Map (REQUIRED — blocks without ghost vote)
- Call `ghost_get_state()` to see the map.
- Vote with `ghost_map_vote(node_index)` — choose the same node as the host/team for fastest travel.

### Events (REQUIRED for shared events)
- Call `ghost_get_state()` to see event options.
- Choose with `ghost_event_choose(option_index)`.

### Treasure (REQUIRED — blocks without ghost bid)
- Pick a relic with `ghost_treasure_pick(relic_index)`.

### Rest Sites (optional but recommended)
- Choose with `ghost_rest_choose(option_index)` — heal if below 80% HP, otherwise upgrade.

### Rewards (optional)
- Claim with `ghost_rewards_claim(reward_index)`.
- Pick a card with `ghost_rewards_pick_card(card_index)` or skip with `ghost_rewards_skip_card()`.
- Proceed with `ghost_proceed()`.

## Important Rules
- **Always re-check `ghost_get_state()` after playing cards** — hand indices shift.
- **ALWAYS vote/bid at gates** (map, shared events, treasure) — the game hangs for ALL players if the ghost doesn't participate.
- **Be fast** — real human players are waiting for the ghost to act. Don't deliberate too long.
- The ghost shares enemies with all players but has its own hand, deck, energy, relics, and potions.
- Use `format: "json"` in combat for precise data, `format: "markdown"` for overview screens.

## Learning & Updating
- After each boss is defeated, review what worked and update `GUIDE.md` with ghost-specific insights.
