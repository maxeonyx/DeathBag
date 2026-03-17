# Death Bag — Implementation Guide

Read VISION.md first for situations and requirements. Read PLAN.md for architecture decisions.

## Authority Model

Read the Architecture section in the workspace `AGENTS.md` — it explains Terraria's client/server authority split in detail.

**For Death Bag specifically:**
- Inventory is **client-authoritative** — the restore operation runs on the owning client
- Bag data is **world state** — saved via `TagCompound` in `SaveWorldData`/`LoadWorldData`
- The client informs the server when a bag is created or restored
- The server broadcasts bag presence to all clients

## State Migration (CRITICAL)

**This mod is in use on a real world.** Any change to persisted state — `SaveWorldData`/`LoadWorldData`, `SaveData`/`LoadData`, or `SendExtraAI`/`ReceiveExtraAI` — must handle migration from the previous format. Bags exist in the world save file and must not be lost on mod update.

Rules:
- **Never remove or rename a `TagCompound` key** without migration code that reads the old key
- **New fields must have sensible defaults** when loaded from old saves that lack them
- **Binary packet format changes** require version coordination — both server and all clients must be on the same mod version. Document breaking packet changes in commit messages.

## Bag Data Safety (CRITICAL)

**Bags contain irreplaceable player inventory. Data loss is the worst possible bug.**

1. **Never destroy a bag before its contents are confirmed safe.** Setting `NPC.active = false` or clearing `SavedInventory` is irreversible. The contents must already be in the player's inventory, in a new bag item, or in the world save before the original is removed.

2. **Check preconditions before side effects.** If an operation can fail (e.g. "no room in inventory"), validate that condition *first*, before modifying any state. Never: check space -> destroy bag -> create item. Always: check space -> create item -> destroy bag.

3. **Multiplayer packet handlers are especially dangerous.** The server might remove a bag NPC based on a client request, but the client's inventory operation could fail. Prefer client-authoritative flows where the client confirms success before the server removes the bag.

4. **Log bag contents before any destruction.** Every code path that consumes or removes a bag must log its full contents to the mod log first. This is the disaster recovery mechanism — if a data loss bug exists, the log contains enough information to manually reconstruct the lost inventory.

5. **When adding new features, grep for all bag destruction points** (`NPC.active = false`, `SavedInventory.Clear()`, `Item.active = false`, `TurnToAir`) and verify each one still has a safe data path under the new feature's conditions.
