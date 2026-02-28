# Death Bag — Implementation Guide

## Project Structure

Follow the same conventions as PanicAtDawn and BossHexes:

```
DeathBag/
  DeathBag.cs                    <- Main Mod class (packet handling)
  build.txt                      <- tModLoader metadata
  description.txt                <- Mod browser description
  VISION.md                      <- Requirements and stories (read this first!)
  Common/
    Config/
      DeathBagConfig.cs          <- ModConfig (server-side, ConfigScope.ServerSide)
    NPCs/
      DeathBagNPC.cs             <- ModNPC (bag entity — drawing, interaction, physics)
    Players/
      DeathBagPlayer.cs          <- ModPlayer (death interception, restore logic)
    Systems/
      DeathBagState.cs           <- ModSystem (world save/load, bag entity management)
    # Add more as needed: GlobalItems/, UI/, etc.
  Assets/                        <- Sprites/textures for bag entity
```

## Conventions (match existing mods)

- **File-scoped namespaces:** `namespace DeathBag.Common.Players;`
- **Sealed classes** for all tModLoader hook classes
- **PascalCase** everything (files, classes, methods, public fields)
- **Private fields:** `_camelCase`
- **Config:** `ConfigScope.ServerSide`, fields with `[DefaultValue]` and `[Range]` attributes
- **Packet helpers:** `internal static` methods on the Mod class
- **MessageType:** `internal enum MessageType : byte` inside the Mod class

## Authority Model

Read the Architecture section in the workspace `AGENTS.md` — it explains Terraria's client/server authority split in detail.

**For Death Bag specifically:**
- Inventory is **client-authoritative** — the restore operation runs on the owning client
- Bag data is **world state** — saved via `TagCompound` in `SaveWorldData`/`LoadWorldData`
- The client informs the server when a bag is created or restored
- The server broadcasts bag presence to all clients

## Architecture

**Bag entity type: ModNPC.** See `PLAN.md` for the full rationale, research, implementation order, and test scenarios.

Key components:

| Class | Role |
|---|---|
| `DeathBagNPC : ModNPC` | Bag entity — drawing, interaction, position, sync |
| `DeathBagPlayer : ModPlayer` | Death interception (`Kill` hook), inventory snapshot, restore logic (client-side) |
| `DeathBagState : ModSystem` | Persistence — save/load bag data + inventory to world TagCompound, re-spawn NPCs on load |

## Implementation Workflow

1. **Read VISION.md** for requirements, **PLAN.md** for architecture decisions and test scenarios
2. **One feature at a time** — see implementation order in `PLAN.md`
3. **Commit after each feature** — small, working increments
4. **Deploy and test in-game** after each feature: `& "$env:USERPROFILE\terraria-mods\deploy.ps1" -Mod DeathBag`
5. **Update VISION.md** when you resolve open questions or discover new ones

## State Migration (CRITICAL)

**This mod is in use on a real world.** Any change to persisted state — `SaveWorldData`/`LoadWorldData` in `DeathBagState.cs`, `SaveData`/`LoadData` in `DeathBagItem.cs`, or `SendExtraAI`/`ReceiveExtraAI` in `DeathBagNPC.cs` — must handle migration from the previous format. Bags exist in the world save file and must not be lost on mod update.

Rules:
- **Never remove or rename a `TagCompound` key** without migration code that reads the old key
- **New fields must have sensible defaults** when loaded from old saves that lack them (already done for `DeathLoadoutIndex` and `CarrierName`)
- **Binary packet format changes** (`SendExtraAI`/`ReceiveExtraAI`, `WriteInventory`/`ReadInventory`) require version coordination — both server and all clients must be on the same mod version. Document breaking packet changes in commit messages.

## Bag Data Safety (CRITICAL)

**Bags contain irreplaceable player inventory. Data loss is the worst possible bug.**

Every code path that touches bag state must be reviewed against these rules:

1. **Never destroy a bag before its contents are confirmed safe.** Setting `NPC.active = false` or clearing `SavedInventory` is irreversible. The contents must already be in the player's inventory, in a new bag item, or in the world save before the original is removed.

2. **Check preconditions before side effects.** If an operation can fail (e.g. "no room in inventory"), validate that condition *first*, before modifying any state. Never: check space -> destroy bag -> create item. Always: check space -> create item -> destroy bag.

3. **Audit every code path that sets `NPC.active = false`, calls `SavedInventory.Clear()`, or removes a bag from world state.** Each one must have a clear answer to: "where did the inventory data go?"

4. **Multiplayer packet handlers are especially dangerous.** The server might remove a bag NPC based on a client request, but the client's inventory operation could fail. Prefer client-authoritative flows where the client confirms success before the server removes the bag.

5. **When adding new features, grep for all bag destruction points** (`NPC.active = false`, `SavedInventory.Clear()`, `Item.active = false`, `TurnToAir`) and verify each one still has a safe data path under the new feature's conditions.
