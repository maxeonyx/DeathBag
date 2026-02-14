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

- Randomized visual elements per bag so you can tell them apart at a glance
- Details left to implementation agent
- Death bags and loadout bags (stretch) should be visually distinct

## Stretch Goals

### Loadout Bag Station

A craftable furniture item. Right-click it to dump your current inventory into a new bag (same entity as a death bag but with different styling). Useful for loadout management.

### Lava Penalty + Lavaproof Bag

Currently bags save everything. The plan is to eventually:
1. Add lava death penalty: white-rarity (level 0) items are destroyed when dying in lava, matching vanilla Mediumcore behavior
2. Add a "Lavaproof Bag" upgrade item that prevents this — tied into the lava tech tree (Lava Charm, Obsidian Skull, Lava Waders, etc.)

### DefaultTools (separate mod idea)

Instead of spawning with copper tool items, tool slots have a special background showing the copper tool. The slot behaves as a copper tool when empty — it's a default, not an item. Partially redundant with Death Bag but still useful for secondary loadout management.

## Open Questions

- **What tModLoader entity type should the bag be?** Needs: persistence, not a block, right-clickable, world position, drawable, per-player interaction control. Candidates: ModNPC (has right-click support), ModProjectile (temporary, probably wrong), custom ModSystem drawing (no entity physics). Implementation agent should research this.
- **Exact packet design** depends on entity type choice.
- **What "inventory" means exactly** — does it include armor slots, accessory slots, ammo slots, coins, or just the main inventory? Needs clarification or a sensible default (probably: everything Mediumcore normally drops).
