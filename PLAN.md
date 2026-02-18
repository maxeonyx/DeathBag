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

## Sprite & Icon Processing (BLOCKS PUBLISHING)

Raw AI-generated PNGs have been added to the repo root:
- `deathbag-raw.png` -- death bag sprite
- `loadoutbag-raw.png` -- loadout bag sprite  
- `modicon-raw.png` -- mod icon (required for publishing to mod browser)

**Processing needed for all three:**
- Crop transparent border (auto-crop to content bounds)
- Scale down to appropriate Terraria sprite size (bag sprites should match existing `DeathBagNPC.png` dimensions; mod icon should be 80x80)
- Review pixel alignment -- AI-generated art may have anti-aliasing or sub-pixel artifacts that look wrong at Terraria's pixel scale. May need manual cleanup or quantization.
- Save processed versions to their final paths (do NOT overwrite the raw files)

**Mod icon specific:**
- Fill any transparent holes within the opaque square region (the content area should be fully opaque, only the outside should be transparent)

**Output paths:**
- Bag sprites -> `Common/NPCs/DeathBagNPC.png` (death bag) and a loadout bag equivalent
- Mod icon -> `icon.png` (root of mod, tModLoader convention)

## Test Scenarios

No automated test framework — tModLoader mods are tested manually in-game.

- Die on Mediumcore -> bag appears, no items scattered
- Right-click bag -> inventory restored to exact slots
- Die with torches, pick up more torches, restore -> stacks merge correctly
- Die twice -> two bags, each restorable independently
- Multiplayer: see teammate's bag, can't interact, hover shows name
- Log out and back in -> bags reappear
- Die on Softcore -> no bag (mod inactive)
