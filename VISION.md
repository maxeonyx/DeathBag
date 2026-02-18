# Death Bag — Vision

Mediumcore QoL mod for co-op Terraria. On death, items go into a persistent bag instead of scattering. Restoring preserves exact inventory layout.

## Confirmed Stories

### 1. Core Death Capture

A Mediumcore player dies mid-adventure. Instead of items scattering everywhere (forcing teammates to avoid picking them up, and the player to re-sort everything), the mod intercepts the normal Mediumcore item drop, prevents it entirely, and spawns a bag entity at the death location. The bag contains a snapshot of the full inventory with exact slot positions and stack sizes.

Players can have multiple bags in the world (die multiple times without recovering previous bags).

Only activates for Mediumcore characters. Softcore and Hardcore are unaffected.

### 2. Bag Restore (Plan-Then-Execute)

The owning player right-clicks their bag. The mod builds a complete plan before mutating anything:

1. Start with two inventories: `saved[]` (from bag, with exact slot positions) and `current[]` (what the player has now — copper tools, items picked up on the way back, etc.)
2. Build `result[]`: saved items reclaim their exact original slots
3. Re-absorb `current[]` items into `result[]` using normal pickup logic — stacking onto existing stacks first (e.g. torches merge), then filling empty slots
4. Anything from `current[]` that truly can't fit goes to `toDrop[]`
5. Execute atomically in a single tick: overwrite inventory with `result[]`, drop `toDrop[]`

The bag disappears after restore. The client informs the server that the bag is gone and about any overflow drops.

**Key principle:** Death bag items always win their slots. Current adventure items are the ones that get displaced/dropped. Use Terraria's internal item manipulation APIs where possible for native behavior, but ensure the entire operation completes within one tick — no intermediate states where items could be picked up mid-swap.

### 3. Bag Visibility & Interaction

All players can see all bags in the world. Hovering over a bag shows the owner's player name. Only the owning player can right-click to restore. Other players' right-clicks do nothing (or show a message like "This is {PlayerName}'s bag").

### 4. Bag Physics

Bags that overlap push apart slightly so each is individually clickable. This matters when:
- A player dies multiple times in the same spot
- Two players die near each other
- A loadout bag station is placed near a death location (stretch goal)

### 5. Bag Persistence

Bags are saved in world data (via `TagCompound`, same pattern as BossHexes hex persistence). When a player logs out, their bags despawn from the world as entities but remain in saved data. When they log back in, bags respawn at their saved positions.

Bags are keyed to the player who created them.

### 6. Multiplayer Sync

Inventory is client-authoritative, so the bag restore operation runs on the client. The client:
- Computes the plan and executes the swap in one tick
- Informs the server: "this bag is gone" + "these overflow items were dropped"

The server needs to:
- Know when bags are created (on player death) and removed (on restore)
- Keep world data in sync
- Broadcast bag entity presence/removal to other clients
- Send full bag state to newly joining players

Exact packet design TBD — depends on what entity type the bag ends up being.

### 7. Edge Cases

- **Empty inventory on death:** No bag spawns (nothing to save). Unlikely but possible.
- **Death bag items always win:** Current items overflow, not saved items.
- **Lava death:** Bag saves everything regardless (for now — see stretch goals).
- **Bag at inaccessible location:** Bag spawns at death position even if it's in lava/void. That's Mediumcore life.

### 8. Bag Visuals

- Death bags and loadout bags should be visually distinct (different color tint — loadout bags are blue-green)
- Bags bob gently in place — visual only, hitbox stays fixed at the center of the bob motion
- **Hitbox sizing:** 8 game-pixels larger than the rendered sprite in each direction (top, bottom, left, right). The sprite content is 16x21 texture pixels, which renders at 2x scale (32x42 game pixels), so the hitbox is 48x58. Centered on the sprite center.
- **Drawing:** Both bag kinds use custom `PreDraw` that centers the texture on `NPC.Center + DrawOffsetY`. Vanilla's default NPC draw is NOT used (it doesn't center correctly with oversized hitboxes).
- **Hover text:** Uses vanilla's `GivenName` system (same as Guide, Nurse, etc.) — do NOT use manual `MouseText` calls, they fight with vanilla's hover. `GivenName` is kept in sync via `UpdateGivenName()` in `AI()`.
- **Hover text format:** "PlayerName's Death Bag" or "PlayerName's Loadout"

### 9. Non-Owner Bag Pickup

Non-owners can right-click any bag to pick it up as an inventory item (DeathBagItem). This works for both death bags and loadout bags:

> "I'm imagining this. Immediately prior to death on a Medium Core character, my current inventory gets converted to a deathbag item in my inventory, and then Medium Core drops it. And then when it gets dropped, it converts to a deathbag NPC item entity."

> "Can we however ideally use the default item pickup and default item dropping for the death bag items?"

> "Actually I think maybe deathbags should never be items on the ground. Maybe we should always convert them to deathbag NPCs as you had before."

When the carrier drops a bag item (right-click drop), it converts back to a normal bag NPC — identical to death-spawned or station-spawned bags, no special delivery logic.

## Stretch Goals

### Death Bag Restore Swaps Current Inventory Into a Bag

> "Right now, when you pick up a death bag, it works really well. But your loadout bag is kind of used up at that point. And you've got to do a whole bunch of inventory management to get your loadout bag back in a nice state."

When restoring a death bag, the player's current inventory (the scrappy stuff they picked up after dying) should be packaged into a bag item in their inventory -- not just displaced/dropped. This makes the flow:

1. Die -- death bag spawns with your gear
2. Respawn with copper tools, pick up some junk on the way back
3. Right-click death bag -- your gear is restored, the junk goes into a new bag item in your inventory
4. The junk bag can be right-clicked in inventory to open/dump it (like a boss bag or grab bag), rather than restoring it as a loadout (which would replace your real gear again)

**Key requirements:**
- Current inventory -> bag item in inventory (or dropped if no space)
- The junk bag needs a right-click-in-inventory mechanic to open it without triggering a full restore (dump contents on the ground or into inventory normally)
- This is distinct from the loadout bag restore flow -- junk bags are disposable, not a saved loadout

### Loadout Bag Station (CONFIRMED — building now)

> "You have a loadout station and you right click on it, it turns your current inventory, minus any death bags or loadout bags, into a loadout bag item in your inventory. You can then drop that in the world somewhere."

> "Loadout bags, you have to right click on them to pick them up. They don't automatically pick up. And the player who owns them right clicks on them only, but the player who doesn't own them right clicks on them and has to go through the dialogue."

A craftable furniture item (dungeon-tier recipe — mid game). Right-click it to snapshot your current inventory (excluding death bags and loadout bags) into a **DeathBagItem in the player's inventory** (not an NPC). The player can then drop the item in the world wherever they want — it converts to a loadout bag NPC on the ground (same as any dropped DeathBagItem).

**Creation flow:** Station → DeathBagItem in inventory → player drops it → converts to loadout bag NPC.

**Key differences from death bags:**
- **Created as an inventory item** — player chooses where to place it by dropping
- **No magnet pull, no auto-pickup** — owner must right-click the NPC to restore
- **Owner right-click = instant restore** — no chat UI, no confirmation dialogue. Only non-owners see the chat UI confirmation.
- **Different color** — visually distinct from death bags (blue-green tint)
- **Created voluntarily** — not by dying
- **Multiple allowed** — e.g. stash a "mining loadout" near the mine and a "building loadout" near base
- **Same persistence** — survives server restarts, saved to world file

Everything else is identical: same restore logic, same non-owner pickup flow, same push-apart physics.

### Lava Penalty + Lavaproof Bag

Currently bags save everything. The plan is to eventually:
1. Add lava death penalty: white-rarity (level 0) items are destroyed when dying in lava, matching vanilla Mediumcore behavior
2. Add a "Lavaproof Bag" upgrade item that prevents this — tied into the lava tech tree (Lava Charm, Obsidian Skull, Lava Waders, etc.)

### DefaultTools (separate mod idea)

Instead of spawning with copper tool items, tool slots have a special background showing the copper tool. The slot behaves as a copper tool when empty — it's a default, not an item. Partially redundant with Death Bag but still useful for secondary loadout management.

## Open Questions

- **What "inventory" means exactly** — does it include armor slots, accessory slots, ammo slots, coins, or just the main inventory? Needs clarification or a sensible default (probably: everything Mediumcore normally drops).
