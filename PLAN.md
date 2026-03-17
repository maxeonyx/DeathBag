# Death Bag — Plan

## Entity Type Decision: ModNPC

Researched four candidates. ModNPC is the only entity type that provides right-click interaction, hover text, world position, multiplayer sync, and drawing out of the box.

### Why not the others

| Candidate | Fatal flaw |
|---|---|
| ModProjectile | No right-click hooks. No persistence. Temporary by design (`timeLeft` counts down). |
| ModItem (ground) | No right-click for ground items — only walk-over pickup. |
| Tile (like tombstones) | Tombstones are tiles in vanilla. But tiles snap to grid, can't overlap, need solid ground, can't push apart. |
| Custom ModSystem | Must reimplement drawing, mouse interaction, hover text, MP sync — everything. No benefit. |

### Prior art

An existing mod "Mediumcore Ghost Inventories" (by Kitronas) uses ModNPC for exactly this pattern — ghost NPC at death location holding inventory. It works but has multiplayer bugs. Our server-authoritative design should avoid those.

### ModNPC setup

- `friendly = true`, `townNPC = false`, `aiStyle = -1` (stationary)
- `dontTakeDamage = true`, `immortal = true`, `knockBackResist = 0f`
- `noGravity = false`, `noTileCollide = false` (falls to ground, collides with tiles)
- `netAlways = true` (synced to joining players)

### Right-click interaction

Use `CanChat()` to enable right-click. Gate by owner identity in `SetChatButtons`/`OnChatButtonClicked`. Show owner name via `GetChat()`. The chat UI is slightly awkward but it's the most reliable approach — `PreHoverInteract` may not exist in all tModLoader versions.

### Persistence

ModNPC.SaveData/LoadData only saves town NPCs. Use ModSystem.SaveWorldData/LoadWorldData instead (same TagCompound pattern as BossHexes). Re-spawn NPC entities from saved data on world load. ModSystem data is the source of truth; NPCs are the live representation.

### Push-apart physics

In `ModNPC.AI()`, iterate nearby bag NPCs, compute overlap, apply small separation velocity.

### NPC slot concern

Vanilla has 200 NPC slots max. Co-op with 2 players won't produce more than ~10 bags. Not a concern.

## Packets

| Packet | Direction | Purpose |
|---|---|---|
| BagCreated | Server -> all clients | Notify when a bag NPC spawns (on death) |
| BagRestored | Client -> server -> all clients | Notify when a bag is picked up |
| SyncBags | Server -> joining client | Full bag state for new players |

## Implementation Order

1. Bag NPC + basic spawning (no inventory yet)
2. Death interception + inventory snapshot
3. Restore mechanic (plan-then-execute)
4. Multiplayer sync
5. Persistence (world save/load)
6. Bag visuals and hover text
7. Bag physics (push apart)

## Sprite & Icon Processing — DONE

Processed by `create_sprites.py` (run from DeathBag directory, requires PIL):

| Raw source | Output | Size |
|---|---|---|
| `deathbag-raw.png` | `Common/NPCs/DeathBagNPC.png` | 48x48 |
| `deathbag-raw.png` | `Common/Items/DeathBagItem.png` | 48x48 |
| `loadoutbag-raw.png` | `Common/NPCs/LoadoutBagNPC.png` | 48x48 |
| `loadoutbag-raw.png` | `Common/Items/LoadoutBagItem.png` | 48x48 |
| `loadoutstation-raw.png` | `Common/Tiles/LoadoutStationTile.png` | 52x52 |
| `loadoutstation-raw.png` | `Common/Items/LoadoutStationItem.png` | 48x48 |
| `modicon-raw.png` | `icon.png` | 480x480 |

Pipeline: threshold alpha -> autocrop -> scale to half target (LANCZOS) -> quantize colors (32, MAXCOVERAGE) -> nearest-neighbor 2x upscale -> save. Mod icon also gets transparent hole filling.

Loadout bags use their own textures — both NPC and item.

## Known Bugs

### Non-owner pickup "no room" leaves NPC unclickable — FIXED
After right-clicking a non-owner bag NPC and getting "No room in your inventory!", the NPC was unclickable. Fixed by calling `SetTalkNPC(-1)` to properly close the chat UI (not just clearing `npcChatText`).

## Refactoring: Item Type Split

**Decision:** Split `DeathBagItem` into separate ModItem types. Keep one NPC type.

**Rationale:** A single ModItem with a `Kind` enum causes tModLoader to use the death bag texture in any rendering path that bypasses custom PreDraw hooks (e.g. quick-stack animation). It also means all bag item types share one set of item behaviors (quick-stack, deposit, etc.) with no clean way to differentiate. Separate types fix this structurally.

**New item types:**
- `PortableDeathBagItem` — death bag in inventory; own sprite, owner can dump contents
- `LoadoutBagItem` — loadout in inventory; blue-green sprite, owner can dump or place in world
- `OverflowBagItem` — junk from restore; inventory-only, no world placement, owner can dump

**Migration:** Old `DeathBagItem` stays as a legacy shim at the same type path. On load, reads `kind` byte and converts to the correct new type. Same TagCompound keys, same packet format.

**NPC stays unified:** `DeathBagNPC` with per-instance `Kind`. Behavior differences (magnet, auto-pickup) are shallow. No overflow NPCs in new code.

**Shared logic approach:** Abstract `BagItemBase : ModItem` base class with sealed leaf classes for each bag kind.

## Refactoring: SlotHelper Extraction

Slot-mapping logic (SyncEquipment index → Player array + offset) is duplicated in `SetSlotByIndex`, `TryPlaceInSlotIfEmpty`, and `ClearSnapshotFromInventory` across two files. Slot constants defined in three places.

Extract a single `SlotHelper` utility with slot constants and a generic mapping function.

## Refactoring: Bag Creation Centralization

Bag items/NPCs are constructed with field-by-field setup in 4+ places. Extract a `BagFactory` or similar helper for one-line bag creation.

## Completed

### Right-click to open own loadout bags in inventory — DONE
Owner can right-click a loadout bag item in their inventory to dump its contents into their inventory. Uses `Player.GetItem` with `NPCEntityToPlayerInventorySettings`.

### Death bag restore packages current inventory into a bag — DONE
Current inventory packaged into a bag item on restore. Simplified approach: snapshot, create bag item, clear, place saved items by exact slot, place bag item.

### Loadout station recipe — DONE
Added Wood x20 to loadout station recipe.

## Test Scenarios

No automated test framework — tModLoader mods are tested manually in-game.

- Die on Mediumcore -> bag appears, no items scattered
- Right-click bag -> inventory restored to exact slots
- Die with torches, pick up more torches, restore -> stacks merge correctly
- Die twice -> two bags, each restorable independently
- Multiplayer: see teammate's bag, can't interact, hover shows name
- Log out and back in -> bags reappear
- Die on Softcore -> no bag (mod inactive)
- Die, pick up junk, restore death bag -> junk goes into loadout bag item in inventory
- Die with empty inventory after death (only copper tools) -> restore with no loadout bag created
- Die, restore, right-click loadout bag item -> junk items enter inventory with proper stacking
- Die, restore with full inventory -> loadout bag item drops on ground, converts to NPC
- Multiplayer: restore death bag -> all inventory slots synced to server (no desync)
- Loadout bag NPC: right-click to restore -> current inventory packaged into loadout bag item
