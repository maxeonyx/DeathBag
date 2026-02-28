using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.DataStructures;
using Terraria.ID;
using Terraria.ModLoader;
using DB = DeathBag.DeathBag;
using DeathBag.Common.NPCs;

namespace DeathBag.Common.Players;

public sealed class DeathBagPlayer : ModPlayer
{
    /// <summary>
    /// Snapshot taken at moment of death, before vanilla drops items.
    /// Null when no snapshot is pending.
    /// </summary>
    private List<(int SlotIndex, Item Item)>? _deathSnapshot;

    /// <summary>
    /// Which loadout was active at time of death. Used to restore the correct loadout.
    /// </summary>
    private int _deathLoadoutIndex;

    /// <summary>
    /// NPC index to remove next tick (deferred to avoid crashing chat UI).
    /// -1 means nothing pending.
    /// </summary>
    private int _pendingBagRemoval = -1;

    /// <summary>Copper starter tools — never worth bagging.</summary>
    private static readonly HashSet<int> CopperTools = new()
    {
        ItemID.CopperShortsword,
        ItemID.CopperPickaxe,
        ItemID.CopperAxe,
    };

    public override bool PreKill(double damage, int hitDirection, bool pvp, ref bool playSound,
        ref bool genDust, ref PlayerDeathReason damageSource)
    {
        // Only intercept for Mediumcore characters, on the owning client
        if (Player.difficulty != PlayerDifficultyID.MediumCore)
            return true;
        if (Player.whoAmI != Main.myPlayer)
            return true;

        // Snapshot the full inventory before death clears it
        _deathSnapshot = SnapshotInventory();
        _deathLoadoutIndex = Player.CurrentLoadoutIndex;

        // Filter out copper tools and bag items — these are never worth bagging
        _deathSnapshot.RemoveAll(entry =>
            CopperTools.Contains(entry.Item.type) || entry.Item.ModItem is Items.DeathBagItem);

        Mod.Logger.Info($"[DeathBag] PreKill: snapshotted {_deathSnapshot.Count} items for {Player.name} (after filtering copper tools + bag items)");

        // If only coins remain, let vanilla scatter them — not worth a bag
        bool onlyCoins = _deathSnapshot.Count > 0 && _deathSnapshot.TrueForAll(entry => entry.Item.IsACoin);
        if (_deathSnapshot.Count == 0 || onlyCoins)
        {
            if (onlyCoins)
                Mod.Logger.Info("[DeathBag] PreKill: only coins, letting vanilla drop them");
            else
                Mod.Logger.Info("[DeathBag] PreKill: empty inventory, no bag will spawn");
            _deathSnapshot = null;
            return true;
        }

        // Clear inventory so vanilla's DropItems finds nothing to scatter —
        // EXCEPT DeathBagItems (carried bags) and copper tools, which vanilla will drop naturally.
        ClearInventory(preserveBagItems: true, preserveCopperTools: true);

        return true;
    }

    public override void Kill(double damage, int hitDirection, bool pvp, PlayerDeathReason damageSource)
    {
        if (_deathSnapshot == null)
            return;
        if (Player.whoAmI != Main.myPlayer)
            return;

        var snapshot = _deathSnapshot;
        _deathSnapshot = null;

        // Spawn carrier's own bag (if anything remains)
        if (snapshot.Count > 0)
        {
            Mod.Logger.Info($"[DeathBag] Kill: spawning bag with {snapshot.Count} items at ({Player.Center.X:F0}, {Player.Center.Y:F0}), loadout {_deathLoadoutIndex}");
            SpawnBagNPC(Player.Center, snapshot, _deathLoadoutIndex, BagKind.Death);
        }
    }

    public override void PostUpdate()
    {
        // Deferred bag removal — can't deactivate NPC during chat click handler
        // because GUIChatDrawInner still references it that frame.
        if (_pendingBagRemoval >= 0 && Player.whoAmI == Main.myPlayer)
        {
            int npcIndex = _pendingBagRemoval;
            _pendingBagRemoval = -1;

            if (npcIndex < Main.maxNPCs && Main.npc[npcIndex].active)
            {
                Mod.Logger.Info($"[DeathBag] Removing bag NPC (index {npcIndex}) on deferred tick");

                // Log if NPC still has inventory data (it shouldn't — RestoreFromBag clears it)
                if (Main.npc[npcIndex].ModNPC is DeathBagNPC bagNPC && bagNPC.SavedInventory.Count > 0)
                    Mod.Logger.Warn($"[DeathBag] WARNING: bag NPC {npcIndex} still has {bagNPC.SavedInventory.Count} items at removal time!");

                if (Main.netMode == NetmodeID.MultiplayerClient)
                {
                    // Tell server to remove the NPC
                    DB.SendBagRemoved(Mod, npcIndex);
                }
                else
                {
                    // Singleplayer: remove directly
                    Main.npc[npcIndex].active = false;
                }
            }
        }
    }

    /// <summary>
    /// Restores inventory from a bag. Three phases:
    /// 1. Gather: read bag contents + current player state into dictionaries
    /// 2. Compute: bag items claim their slots, displaced items re-absorb into inventory
    /// 3. Apply: write only changed slots, drop overflow
    /// </summary>
    public void RestoreFromBag(DeathBagNPC bag)
    {
        if (bag.SavedInventory.Count == 0)
        {
            Mod.Logger.Warn("[DeathBag] RestoreFromBag: bag has no items, skipping");
            return;
        }

        Mod.Logger.Info($"[DeathBag] RestoreFromBag: restoring {bag.SavedInventory.Count} saved items for {Player.name}");

        // Switch to the loadout that was active at death, so slot indices map to the correct arrays.
        if (Player.CurrentLoadoutIndex != bag.DeathLoadoutIndex)
        {
            Mod.Logger.Info($"[DeathBag] Switching loadout {Player.CurrentLoadoutIndex} -> {bag.DeathLoadoutIndex} to match death state");
            Player.TrySwitchingLoadout(bag.DeathLoadoutIndex);
        }

        // === 1. GATHER (read-only) ===

        // Bag contents: slot -> item
        var bagItems = new Dictionary<int, Item>();
        foreach (var (slotIndex, savedItem) in bag.SavedInventory)
            bagItems[slotIndex] = savedItem.Clone();

        // Current player state: slot -> item (all slots, including empty)
        var current = new Dictionary<int, Item>();
        void GatherArray(Item[] array, int baseSlot)
        {
            for (int idx = 0; idx < array.Length; idx++)
                current[baseSlot + idx] = array[idx]?.Clone() ?? new Item();
        }
        GatherArray(Player.inventory, 0);
        GatherArray(Player.armor, SlotArmor);
        GatherArray(Player.dye, SlotDye);
        GatherArray(Player.miscEquips, SlotMiscEquips);
        GatherArray(Player.miscDyes, SlotMiscDyes);
        for (int l = 0; l < Player.Loadouts.Length; l++)
        {
            if (l == Player.CurrentLoadoutIndex)
                continue;
            int loadoutBase = SlotLoadoutsStart + l * LoadoutSize;
            GatherArray(Player.Loadouts[l].Armor, loadoutBase);
            GatherArray(Player.Loadouts[l].Dye, loadoutBase + 20);
        }

        Mod.Logger.Info($"[DeathBag] Gather: {bagItems.Count} bag items, {current.Count} current slots");

        // === 2. COMPUTE (pure — builds result + overflow from gathered data) ===

        var (result, toDrop) = ComputeRestore(bagItems, current);

        // === 3. APPLY (single tick — only write changed slots) ===

        Mod.Logger.Info($"[DeathBag] Applying: {result.Count} changed slots, {toDrop.Count} overflow items");

        void ApplyArray(Item[] array, int baseSlot)
        {
            for (int idx = 0; idx < array.Length; idx++)
            {
                int slot = baseSlot + idx;
                if (result.TryGetValue(slot, out Item item))
                    array[idx] = item;
                // else: slot not in result — leave it untouched
            }
        }
        ApplyArray(Player.inventory, 0);
        ApplyArray(Player.armor, SlotArmor);
        ApplyArray(Player.dye, SlotDye);
        ApplyArray(Player.miscEquips, SlotMiscEquips);
        ApplyArray(Player.miscDyes, SlotMiscDyes);
        for (int l = 0; l < Player.Loadouts.Length; l++)
        {
            if (l == Player.CurrentLoadoutIndex)
                continue;
            int loadoutBase = SlotLoadoutsStart + l * LoadoutSize;
            ApplyArray(Player.Loadouts[l].Armor, loadoutBase);
            ApplyArray(Player.Loadouts[l].Dye, loadoutBase + 20);
        }

        // Drop overflow items
        foreach (Item drop in toDrop)
            Player.QuickSpawnItem(Player.GetSource_Misc("DeathBagOverflow"), drop, drop.stack);

        // Defer NPC removal to next tick (removing mid-frame can crash)
        _pendingBagRemoval = bag.NPC.whoAmI;

        // Log contents before clearing (disaster recovery)
        DB.LogBagContents(Mod, "owner restore", bag.OwnerName, bag.Kind, bag.SavedInventory);

        // Mark bag as consumed immediately so AI() can't trigger a second restore
        bag.SavedInventory.Clear();

        // Sync only changed slots to server
        if (Main.netMode == NetmodeID.MultiplayerClient)
        {
            foreach (int slot in result.Keys)
                NetMessage.SendData(MessageID.SyncEquipment, -1, -1, null, Player.whoAmI, slot);
        }

        Main.NewText("Inventory restored!", Color.Green);
        if (!string.IsNullOrEmpty(bag.DeliveredBy))
            Main.NewText($"Delivered by {bag.DeliveredBy}!", Color.LightGreen);
        Mod.Logger.Info("[DeathBag] Restore complete");
    }

    /// <summary>
    /// Pure computation: given bag items and current player state, compute which slots
    /// change and what overflows.
    ///
    /// Algorithm:
    /// 1. Bag items claim their exact slots. Any current item in a contested slot is displaced.
    /// 2. Displaced items are re-absorbed: stack onto existing items, then fill empty inventory slots.
    /// 3. Anything that can't fit overflows.
    ///
    /// Returns only the slots that change — untouched slots are not in the result.
    /// </summary>
    private (Dictionary<int, Item> ChangedSlots, List<Item> Overflow) ComputeRestore(
        Dictionary<int, Item> bagItems,
        Dictionary<int, Item> current)
    {
        var changed = new Dictionary<int, Item>();
        var displaced = new List<Item>();

        // Step 1: bag items claim their slots, displacing current occupants
        foreach (var (slot, bagItem) in bagItems)
        {
            if (current.TryGetValue(slot, out Item currentItem) && currentItem is not null && !currentItem.IsAir)
                displaced.Add(currentItem);

            changed[slot] = bagItem;
        }

        Mod.Logger.Info($"[DeathBag] Compute: {changed.Count} bag items placed, {displaced.Count} displaced items");

        // Step 2: re-absorb displaced items into inventory slots (0-58) only
        int invSlots = 59;
        var overflow = new List<Item>();

        foreach (Item item in displaced)
        {
            int remaining = item.stack;

            // Try stacking onto matching items in inventory range (bag items or existing)
            for (int s = 0; s < invSlots && remaining > 0; s++)
            {
                // Check changed slots first, then fall back to current
                Item target;
                if (changed.TryGetValue(s, out Item changedItem))
                    target = changedItem;
                else if (current.TryGetValue(s, out Item currentItem))
                    target = currentItem;
                else
                    continue;

                if (target is null || target.IsAir)
                    continue;
                if (target.type != item.type || target.stack >= target.maxStack)
                    continue;

                int canAdd = Math.Min(remaining, target.maxStack - target.stack);
                target.stack += canAdd;
                remaining -= canAdd;

                // If we modified a current slot (not already in changed), mark it changed
                if (!changed.ContainsKey(s))
                    changed[s] = target;
            }

            // Try placing in empty inventory slots
            for (int s = 0; s < invSlots && remaining > 0; s++)
            {
                // Slot is occupied if it's in changed or (not in changed and non-empty in current)
                if (changed.ContainsKey(s))
                    continue;
                if (current.TryGetValue(s, out Item cur) && cur is not null && !cur.IsAir)
                    continue;

                // Respect slot type restrictions
                bool slotAccepts;
                if (s >= 54 && s <= 57)
                    slotAccepts = item.IsACoin;
                else if (s >= 50 && s <= 53)
                    slotAccepts = item.ammo != 0 || item.bait > 0;
                else
                    slotAccepts = true;

                if (!slotAccepts)
                    continue;

                Item placed = item.Clone();
                placed.stack = remaining;
                changed[s] = placed;
                remaining = 0;
            }

            if (remaining > 0)
            {
                Item ovf = item.Clone();
                ovf.stack = remaining;
                overflow.Add(ovf);
                Mod.Logger.Info($"[DeathBag] Overflow: {ovf.Name} x{remaining}");
            }
        }

        return (changed, overflow);
    }

    /// <summary>
    /// Slot index ranges matching SyncEquipment conventions:
    ///   0-58:   Player.inventory
    ///   59-78:  Player.armor (armor, accessories, vanity)
    ///   79-88:  Player.dye
    ///   89-93:  Player.miscEquips
    ///   94-98:  Player.miscDyes
    ///   99+:    Loadout slots (each loadout: 20 armor + 10 dye = 30 slots)
    ///           Loadout 0 = 99-128, Loadout 1 = 129-158, Loadout 2 = 159-188
    /// </summary>
    private const int SlotArmor = 59;
    private const int SlotDye = 79;
    private const int SlotMiscEquips = 89;
    private const int SlotMiscDyes = 94;
    private const int SlotLoadoutsStart = 99;
    private const int LoadoutSize = 30; // 20 armor + 10 dye per loadout

    internal List<(int SlotIndex, Item Item)> SnapshotInventory()
    {
        var snapshot = new List<(int, Item)>();

        void Snap(Item[] array, int baseSlot, string label)
        {
            for (int i = 0; i < array.Length; i++)
            {
                Item item = array[i];
                if (item is not null && !item.IsAir)
                {
                    snapshot.Add((baseSlot + i, item.Clone()));
                    Mod.Logger.Debug($"[DeathBag] Snapshot {label}[{i}] (slot {baseSlot + i}): {item.Name} x{item.stack}");
                }
            }
        }

        Mod.Logger.Info($"[DeathBag] Snapshot: CurrentLoadoutIndex={Player.CurrentLoadoutIndex}, Loadouts.Length={Player.Loadouts.Length}");
        Snap(Player.inventory, 0, "inventory");
        Snap(Player.armor, SlotArmor, "armor");
        Snap(Player.dye, SlotDye, "dye");
        Snap(Player.miscEquips, SlotMiscEquips, "miscEquips");
        Snap(Player.miscDyes, SlotMiscDyes, "miscDyes");

        // Inactive loadouts (active loadout's items are already in Player.armor/dye)
        for (int l = 0; l < Player.Loadouts.Length; l++)
        {
            if (l == Player.CurrentLoadoutIndex)
                continue;

            int loadoutBase = SlotLoadoutsStart + l * LoadoutSize;
            Snap(Player.Loadouts[l].Armor, loadoutBase, $"loadout{l}.Armor");
            Snap(Player.Loadouts[l].Dye, loadoutBase + 20, $"loadout{l}.Dye");
        }

        return snapshot;
    }

    private void ClearInventory(bool preserveBagItems = false, bool preserveCopperTools = false)
    {
        void Clear(Item[] array)
        {
            for (int i = 0; i < array.Length; i++)
            {
                if (preserveBagItems && array[i]?.ModItem is Items.DeathBagItem)
                    continue;
                if (preserveCopperTools && array[i] is not null && CopperTools.Contains(array[i].type))
                    continue;
                array[i] = new Item();
            }
        }

        Clear(Player.inventory);
        Clear(Player.armor);
        Clear(Player.dye);
        Clear(Player.miscEquips);
        Clear(Player.miscDyes);

        for (int l = 0; l < Player.Loadouts.Length; l++)
        {
            if (l == Player.CurrentLoadoutIndex)
                continue;

            Clear(Player.Loadouts[l].Armor);
            Clear(Player.Loadouts[l].Dye);
        }
    }

    internal void SpawnBagNPC(Vector2 position, List<(int SlotIndex, Item Item)> inventory, int deathLoadoutIndex,
        BagKind kind, string? ownerNameOverride = null)
    {
        string ownerName = ownerNameOverride ?? Player.name;

        if (Main.netMode == NetmodeID.MultiplayerClient)
        {
            // Send packet to server — server spawns the NPC and syncs to all clients
            DB.SendBagCreated(Mod, kind, position.X, position.Y, ownerName, Player.whoAmI, deathLoadoutIndex, inventory);
            Mod.Logger.Info($"[DeathBag] Sent BagCreated packet for {ownerName} with {inventory.Count} items, kind={kind}");
            return;
        }

        // Singleplayer or server (host-and-play): spawn directly
        int npcIndex = NPC.NewNPC(
            Player.GetSource_Death(),
            (int)position.X,
            (int)position.Y,
            ModContent.NPCType<DeathBagNPC>());

        if (npcIndex < 0 || npcIndex >= Main.maxNPCs)
        {
            Mod.Logger.Error($"[DeathBag] CRITICAL: Failed to spawn bag NPC (NewNPC={npcIndex}), {inventory.Count} items LOST for {ownerName}!");
            DB.LogBagContents(Mod, "FAILED SpawnBagNPC (NPC slot full)", ownerName, kind, inventory);
            return;
        }

        NPC npc = Main.npc[npcIndex];
        if (npc.ModNPC is DeathBagNPC bagNPC)
        {
            bagNPC.Kind = kind;
            bagNPC.OwnerPlayerIndex = -1; // Will be resolved by ResolveOwnerIndex
            bagNPC.OwnerName = ownerName;
            bagNPC.DeathLoadoutIndex = deathLoadoutIndex;
            bagNPC.SavedInventory = inventory;
            npc.netUpdate = true;
            bagNPC.ResolveOwnerIndex();
            Mod.Logger.Info($"[DeathBag] Bag NPC spawned (index {npcIndex}) for {ownerName} with {inventory.Count} items, kind={kind}, loadout {deathLoadoutIndex}");
            DB.LogBagContents(Mod, "SpawnBagNPC (SP/host)", ownerName, kind, inventory);
        }
        else
        {
            Mod.Logger.Error($"[DeathBag] Spawned NPC at index {npcIndex} is not DeathBagNPC (type: {npc.ModNPC?.GetType().Name ?? "null"})");
        }
    }
}
