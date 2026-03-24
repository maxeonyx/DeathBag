# Death Bag — Vision

Mediumcore QoL mod for co-op Terraria. On death, items go into a persistent bag instead of scattering. Restoring preserves exact inventory layout.

## Terraria Situations

These are the realities of co-op Mediumcore Terraria that DeathBag exists to address. These don't change — they're just how the game is.

### 1. Death and recovery

You die. Your gear is where you fell. You want it safe and back exactly how it was. You don't want to lose the scrappy stuff you picked up on the way back either.

### 2. Dying on the way back

You die multiple times getting back to your real gear. You eventually recover it. There's junk scattered from those intermediate deaths. You want your good stuff without hassle, don't want to lose the other stuff, but don't want to stop and deal with each little pile of items along the way.

### 3. Your partner dies

Their stuff is everywhere. You don't want to accidentally pick it up mid-fight. If they can't make it back, you want to grab their stuff and bring it to them as one unit without dealing with their items.

### 4. Gearing up for different activities

You want to swap your full inventory — hotbar, main inventory, everything — between different setups without manual shuffling.

### 5. Leaving your gear

You died but you can't get back right now — maybe you need to log off, maybe you need to do something else first, maybe it's just going to take a while. Your gear is still out there. You want to be confident it'll be there whenever you're ready to go get it, no matter how long that takes.

### 6. Carrying your partner's stuff

Your partner died and can't make it back. You pick up their stuff to bring it to them. You don't want to deal with their items — you don't want to sort through them, you don't want them mixed into your inventory, you shouldn't even be tempted to use them. You just want to carry the whole thing as one unit and hand it back.

## DeathBag Situations

These are situations that arise specifically from playing with the DeathBag mod. They inform design decisions.

### 1. Walking back to your bag

Your inventory is restored when you reach your bag, but you picked up scrappy stuff on the way back. You don't want to lose it, but you don't want to deal with it right now either.

### 2. Multiple bags in a chaotic area

You died a few times in the same area. There are several bags around. You got your real gear back from one of them. You don't want the others to mess up your inventory, but you don't want to lose them either.

### 3. Bags in your inventory

Your own bags — you want to know what's in them, deal with them quickly, and it should be obvious what your options are. You shouldn't accidentally trash one that has good stuff — you should have to open it first. Your partner's bags — you really can't do anything with them except bring them back. You shouldn't be able to destroy them at all.

### 4. Opening a bag in your inventory

You're back at base and you've got a bag of scrappy stuff in your inventory. You want to grab some things out of it — maybe there were some good potions in there, maybe some ore. You don't want the bag to replace your current inventory. You just want the stuff out of it.

### 5. Too many bags

You've been playing for a while and you've accumulated several bags. Your inventory is getting cluttered. Ideally you shouldn't have ended up with this many bags in the first place — something earlier in the flow should have kept things simpler.

### 6. Interacting with bags in the world

There are a couple of bags sitting on the ground near each other — maybe yours, maybe your partner's. You want to interact with the right one. You want to know whose bag is whose and what's in each one before you commit to anything.

### 7. Stashing backup gear

You want a spare set of gear ready for when you die — not copper tools, real equipment. You don't want to manually sort items into a chest and pull them out one by one. You want to snapshot your current setup instantly and have it ready to grab.

### 8. Reusing backup gear

You died and grabbed your backup gear to go recover your bag. You got your real stuff back. Now you want your backup set to be ready again for next time — you don't want to have to rearrange and re-stash it every time you use it.

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

### 10. Bag Item Protection

Bag items contain irreplaceable inventory and must not be accidentally destroyed. Bag items cannot be trashed or sold:

- **Ctrl+click trash/sell:** Drops the bag on the ground instead of trashing/selling
- **Manual click-to-trash:** If a bag item ends up in the trash slot (by any path), it ejects to the ground next tick
- **Shop sell:** Blocked entirely — item stays on cursor
- **Dropping on the ground is always allowed** — that's how bags convert back to NPC form

### 11. Bag Item Tooltips

Bag item tooltips are concise. The item name already contains the owner and bag type ("Max's Death Bag" or "Max's Loadout"), so tooltips avoid redundancy.

**Death bag, owner** (rare edge case — e.g. retrieved from a chest):
```
Max's Death Bag
12 items
Right-click to empty
```

**Death bag, non-owner:**
```
Max's Death Bag
12 items
Drop to return to Max
```

**Loadout bag, owner** (the primary use case — just created at station):
```
Max's Loadout
12 items
Place to use as a loadout
Right-click to empty
```

**Loadout bag, non-owner:**
```
Max's Loadout
12 items
Drop to return to Max
```

Key distinctions:
- Loadout bags emphasize **placement** as the primary action (place in world → right-click NPC to restore)
- "Right-click to empty" communicates this is a destructive dump, not a careful restore
- "Drop to return to [name]" for non-owners — simple, no special delivery mechanic

## Stretch Goals

### Death Bag Restore Swaps Current Inventory Into a Bag

> "Right now, when you pick up a death bag, it works really well. But your loadout bag is kind of used up at that point. And you've got to do a whole bunch of inventory management to get your loadout bag back in a nice state."

When restoring a death bag, the player's current inventory (the scrappy stuff they picked up after dying) should be packaged into a bag item in their inventory -- not just displaced/dropped. This makes the flow:

1. Die -- death bag spawns with your gear
2. Respawn with copper tools, pick up some junk on the way back
3. Right-click death bag -- your gear is restored, the junk goes into a new bag item in your inventory
4. The junk bag can be right-clicked in inventory to open/dump it (like a boss bag or grab bag), rather than restoring it as a loadout (which would replace your real gear again)

**Key requirements:**
- Current inventory -> bag item in inventory
- Overflow bags should never drop on the ground; if one would otherwise be created with no room, prefer ordinary inventory/void-bag absorption for displaced junk first
- Overflow bags are conceptually part of the player's current inventory — whenever inventory is captured (on death OR at the loadout station), overflow bag contents should be absorbed into the new bag rather than persisting as a separate nested bag. They should not appear as a separate bag-within-a-bag.
- The junk/overflow bag needs a right-click-in-inventory mechanic to open it without triggering a full restore (dump contents into inventory normally)
- This is distinct from the loadout bag restore flow -- overflow bags are disposable junk bags, not saved loadouts

### "Shitty" Death Bags (confirmed pain point)

Another common flow is: die on the way back to recover your real bag, leaving behind a tiny death bag with just a few junk items / partial emergency gear. If you later miss that bag, recover your real gear, and only then restore the tiny bag, the current exact-restore behavior is awful: it wipes your good inventory back to the tiny junk snapshot and causes extra displacement/fiddling.

In chaotic situations (invasions, boss aftermath, repeated recovery scrambles), multiple of these tiny bags can accumulate. Then the failure mode becomes much worse: you recover your real gear, but while moving through the area you repeatedly snap onto tiny death bags that overwrite your current inventory state, forcing repeated swap/fiddle/recover loops and often causing another death. This can cascade into a wipe/die/repeat cycle.

This kind of bag is technically a death bag, but it behaves more like junk/overflow in practice.

**Design goal:** tiny junk-only "shitty death bags" should not punish the player with a destructive full restore once they already have a meaningful inventory again.

**Important implication:** auto-restoring exact-snapshot death bags on contact is unsafe when many low-value bags can accumulate in the same area.

Another framing from playtesting: auto-restore is generally very nice when the bag inventory is clearly *better* than the player's current inventory, and very bad when it is clearly *worse*. The hard part is deciding whether "better" can be inferred reliably enough.

This suggests a possible future direction:
- Keep auto-restore for obviously high-value / primary recovery bags
- Avoid auto-restore for obviously low-value / junky bags
- Or expose the choice explicitly when the system cannot confidently tell

Promising heuristic directions from playtesting:
- Compare item count in the bag versus the player's current inventory
- Compare favorited-item count, which may be a better proxy for "meaningful current setup" than raw item count
- Explore combinations of simple heuristics before attempting anything too smart or opaque

Current preferred heuristic shape:
- First compare favorited-item count in the bag versus the player's current inventory
- On tie, compare total item count
- On tie again, do **not** auto-restore

When a death bag does not auto-restore under this heuristic, it should behave like a loadout bag instead of forcing immediate exact restore. In practice that means the owner should get the same three options:
- `Restore`
- `Pick Up`
- `Get Items`

If a bag presents an owner action menu/chat UI, that UI should also summarize the bag contents well enough for the player to make the right choice without guessing. At minimum, it should show a useful contents summary rather than just the bag name.

Preferred contents summary shape:
- The first three hotbar items
- What armor/equipment the bag contains
- How many loadouts contain any items
- A compact count of remaining miscellaneous items (e.g. `and 12 other items`)

Potential solution directions:
- Detect and classify some death bags as effectively junk/overflow-like rather than exact-restore bags
- Or give death bags with a currently meaningful player inventory a safer interaction path than immediate exact restore
- Or otherwise ensure that restoring a tiny junk death bag after recovering real gear does not wipe the player's good inventory state

### Coins

Coins should not be bagged on death. Coins-only death bags are not part of the intended experience and contribute to the "shitty death bag" problem.

Current design preference: leave coins in the player's inventory on death rather than placing them into the death bag.

If this ever changes, match vanilla Mediumcore intentionally and explicitly rather than accidentally, but the current preferred behavior is: **coins stay with the player**.

### Loadout Bag Station (CONFIRMED — building now)

> "You have a loadout station and you right click on it, it turns your current inventory, minus any death bags or loadout bags, into a loadout bag item in your inventory. You can then drop that in the world somewhere."

> "Loadout bags, you have to right click on them to pick them up. They don't automatically pick up. And the player who owns them right clicks on them only, but the player who doesn't own them right clicks on them and has to go through the dialogue."

A craftable furniture item (dungeon-tier recipe — mid game). Right-click it to snapshot your current inventory (excluding death bags and loadout bags) into a **DeathBagItem in the player's inventory** (not an NPC). The player can then drop the item in the world wherever they want — it converts to a loadout bag NPC on the ground (same as any dropped DeathBagItem).

**Creation flow:** Station → DeathBagItem in inventory → player drops it → converts to loadout bag NPC.

**Key differences from death bags:**
- **Created as an inventory item** — player chooses where to place it by dropping
- **No magnet pull, no auto-pickup** — owner must right-click the NPC to restore
- **Owner interaction should be a menu, not instant restore** — loadout bag NPCs should offer `Restore`, `Pick Up`, and `Get Items`
- **Different color** — visually distinct from death bags (blue-green tint)
- **Created voluntarily** — not by dying
- **Multiple allowed** — e.g. stash a "mining loadout" near the mine and a "building loadout" near base
- **Same persistence** — survives server restarts, saved to world file

Everything else is identical: same restore logic, same non-owner pickup flow, same push-apart physics.

**Chat/UI note:** the bag NPC chat should not expose irrelevant vanilla NPC options like `Happiness`.

### Lava Penalty + Lavaproof Bag

Currently bags save everything. The plan is to eventually:
1. Add lava death penalty: white-rarity (level 0) items are destroyed when dying in lava, matching vanilla Mediumcore behavior
2. Add a "Lavaproof Bag" upgrade item that prevents this — tied into the lava tech tree (Lava Charm, Obsidian Skull, Lava Waders, etc.)

### Left-Click Bag Placement

Currently dropping a bag item places it at the player's feet. Left-clicking with a bag on the cursor should place it at the cursor's world position (like placing a tile/block), giving precise control over where the bag NPC spawns.

## Open Questions

- **What "inventory" means exactly** — does it include armor slots, accessory slots, ammo slots, coins, or just the main inventory? Needs clarification or a sensible default (probably: everything Mediumcore normally drops).
