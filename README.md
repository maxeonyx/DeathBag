# Death Bag

A Mediumcore quality-of-life mod for Terraria (tModLoader). On death, your items go into a persistent bag instead of scattering on the ground. Walk up to your bag to restore your inventory exactly how it was — slot positions, stacks, armor, accessories, and all.

## Why

Mediumcore Terraria is fun but the item recovery experience is miserable. You die, your stuff scatters everywhere, your teammates accidentally pick up your gear mid-fight, and you spend ten minutes re-sorting your inventory. Death Bag fixes all of that while keeping the stakes that make Mediumcore interesting.

## Features

**Death bags** — When you die, all your items are captured into a single bag entity at your death location instead of scattering. Walk near your bag and it magnets toward you, auto-restoring your full inventory layout. Items you picked up on the way back (copper tools, scrappy loot) get displaced, not lost.

**Slot-perfect restore** — Your hotbar, armor, accessories, and loadout positions are preserved exactly. No re-sorting.

**Persistent bags** — Bags are saved in the world file and survive server restarts. Die, log off, come back tomorrow — your bag is still there.

**Multiple bags** — Die twice without recovering? You get two bags. Each one is independent.

**Teammate support** — All players can see all bags. Non-owners can right-click a bag to pick it up as a portable item and carry it to its owner. Bag items can't be trashed or sold — they can only be dropped (which converts them back into a world bag).

**Loadout Station** — A craftable furniture piece (Wood + Bone + Gold Chest at a Work Bench). Right-click it to snapshot your current inventory into a portable loadout bag item. Drop it wherever you want — near the mine, at your base, by the arena. Right-click the placed bag to restore that loadout. Great for swapping between gear sets without manual inventory management.

**Coins stay with you** — Coins are excluded from death bags and left in your inventory, matching vanilla Mediumcore coin drop behavior.

## Bag Types

- **Death Bag** — created on death, contains your full inventory snapshot
- **Loadout Bag** — created at the Loadout Station, a voluntary gear snapshot (visually distinct with a blue-green tint)
- **Overflow Bag** — the displaced items from a restore, right-click in inventory to dump contents

## Installation

Subscribe via tModLoader's in-game mod browser, or build from source with `tModLoader`.

## Pairs Well With

- [Panic at Dawn](https://github.com/maxeonyx/PanicAtDawn) — survival horror co-op: sanity, no recall, die at dawn
- [Boss Hexes](https://github.com/maxeonyx/BossHexes) — random hex modifiers for boss fights

## Compatibility

Requires [tModLoader](https://store.steampowered.com/app/1281930/tModLoader/). Works in singleplayer and multiplayer. Only activates for Mediumcore characters — Softcore and Hardcore are unaffected.
