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

## Implementation Workflow

1. **Read VISION.md thoroughly** before starting — it has confirmed user stories and open questions
2. **Research the entity type question first** — what tModLoader type should the bag be? This is the key architectural decision. Document your choice and reasoning.
3. **One feature at a time**, in this order:
   - Bag entity type + basic spawning (no inventory yet)
   - Death interception + inventory snapshot
   - Restore mechanic (plan-then-execute)
   - Multiplayer sync
   - Persistence (world save/load)
   - Bag visuals and hover text
   - Bag physics (push apart)
4. **Commit after each feature** — small, working increments
5. **Deploy and test in-game** after each feature: `& "$env:USERPROFILE\terraria-mods\deploy.ps1" -Mod DeathBag`
6. **Update VISION.md** when you resolve open questions or discover new ones

## Testing

No automated test framework — this is a tModLoader mod. Testing is manual, in-game.

Test scenarios to verify:
- Die on Mediumcore → bag appears, no items scattered
- Right-click bag → inventory restored to exact slots
- Die with torches, pick up more torches, restore → stacks merge correctly
- Die twice → two bags, each restorable independently
- Multiplayer: see teammate's bag, can't interact, hover shows name
- Log out and back in → bags reappear
- Die on Softcore → no bag (mod inactive)
