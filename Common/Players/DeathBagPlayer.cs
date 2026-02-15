using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.DataStructures;
using Terraria.ID;
using Terraria.ModLoader;
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
    /// NPC index to remove next tick (deferred to avoid crashing chat UI).
    /// -1 means nothing pending.
    /// </summary>
    private int _pendingBagRemoval = -1;

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

        Mod.Logger.Info($"[DeathBag] PreKill: snapshotted {_deathSnapshot.Count} items for {Player.name}");

        if (_deathSnapshot.Count == 0)
        {
            Mod.Logger.Info("[DeathBag] PreKill: empty inventory, no bag will spawn");
            _deathSnapshot = null;
            return true;
        }

        // Clear inventory so vanilla's DropItems finds nothing to scatter
        ClearInventory();

        return true;
    }

    public override void Kill(double damage, int hitDirection, bool pvp, PlayerDeathReason damageSource)
    {
        if (_deathSnapshot == null)
            return;
        if (Player.whoAmI != Main.myPlayer)
            return;

        // Spawn bag NPC at death location
        var snapshot = _deathSnapshot;
        _deathSnapshot = null;

        Mod.Logger.Info($"[DeathBag] Kill: spawning bag with {snapshot.Count} items at ({Player.Center.X:F0}, {Player.Center.Y:F0})");
        SpawnBagNPC(Player.Center, snapshot);
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
                Main.npc[npcIndex].active = false;
                Main.npc[npcIndex].netUpdate = true;
            }
        }
    }

    /// <summary>
    /// Restores inventory from a bag using plan-then-execute pattern.
    /// Called from DeathBagNPC.AI() on right-click by the owning client.
    /// </summary>
    public void RestoreFromBag(DeathBagNPC bag)
    {
        if (bag.SavedInventory.Count == 0)
        {
            Mod.Logger.Warn("[DeathBag] RestoreFromBag: bag has no items, skipping");
            return;
        }

        Mod.Logger.Info($"[DeathBag] RestoreFromBag: restoring {bag.SavedInventory.Count} saved items for {Player.name}");

        // === PLAN PHASE (no mutations) ===

        int totalSlots = Player.inventory.Length;
        Mod.Logger.Info($"[DeathBag] Player inventory has {totalSlots} slots");

        Item[] result = new Item[totalSlots];

        // Initialize result with empty items
        for (int i = 0; i < totalSlots; i++)
        {
            result[i] = new Item();
        }

        // Step 1: Saved items reclaim their exact slots
        int restoredCount = 0;
        int skippedCount = 0;
        foreach (var (slotIndex, savedItem) in bag.SavedInventory)
        {
            if (slotIndex >= 0 && slotIndex < totalSlots)
            {
                result[slotIndex] = savedItem.Clone();
                restoredCount++;
            }
            else
            {
                Mod.Logger.Warn($"[DeathBag] Saved item '{savedItem.Name}' has out-of-range slot {slotIndex} (max {totalSlots - 1}), dropping as overflow");
                skippedCount++;
            }
        }
        Mod.Logger.Info($"[DeathBag] Step 1: {restoredCount} items reclaimed slots, {skippedCount} skipped (out of range)");

        // Step 2: Collect current items that need re-absorption
        var currentItems = new List<Item>();
        for (int i = 0; i < totalSlots; i++)
        {
            Item current = Player.inventory[i];
            if (current is not null && !current.IsAir)
                currentItems.Add(current.Clone());
        }
        Mod.Logger.Info($"[DeathBag] Step 2: {currentItems.Count} current items to re-absorb");

        // Step 3: Re-absorb current items — stack first, then empty slots
        var toDrop = new List<Item>();

        foreach (Item current in currentItems)
        {
            int remaining = current.stack;

            // Try stacking onto matching items in result
            for (int i = 0; i < totalSlots && remaining > 0; i++)
            {
                Item target = result[i];
                if (target.IsAir || target.type != current.type)
                    continue;
                if (target.stack >= target.maxStack)
                    continue;

                int canAdd = Math.Min(remaining, target.maxStack - target.stack);
                target.stack += canAdd;
                remaining -= canAdd;
            }

            // Try placing in empty slots
            for (int i = 0; i < totalSlots && remaining > 0; i++)
            {
                if (!result[i].IsAir)
                    continue;

                Item placed = current.Clone();
                placed.stack = remaining;
                result[i] = placed;
                remaining = 0;
            }

            // Overflow
            if (remaining > 0)
            {
                Item overflow = current.Clone();
                overflow.stack = remaining;
                toDrop.Add(overflow);
                Mod.Logger.Info($"[DeathBag] Overflow: {overflow.Name} x{remaining}");
            }
        }

        // === EXECUTE PHASE (single tick) ===

        Mod.Logger.Info($"[DeathBag] Executing restore: {totalSlots} slots, {toDrop.Count} overflow items");

        // Overwrite inventory atomically
        for (int i = 0; i < totalSlots; i++)
        {
            Player.inventory[i] = result[i];
        }

        // Drop overflow items
        foreach (Item drop in toDrop)
        {
            Player.QuickSpawnItem(Player.GetSource_Misc("DeathBagOverflow"), drop, drop.stack);
        }

        // Defer NPC removal to next tick (removing mid-frame can crash)
        _pendingBagRemoval = bag.NPC.whoAmI;

        // Sync inventory to server
        if (Main.netMode == NetmodeID.MultiplayerClient)
        {
            for (int i = 0; i < totalSlots; i++)
            {
                NetMessage.SendData(MessageID.SyncEquipment, -1, -1, null, Player.whoAmI, i);
            }
        }

        Main.NewText("Inventory restored!", Color.Green);
        Mod.Logger.Info("[DeathBag] Restore complete");
    }

    private List<(int SlotIndex, Item Item)> SnapshotInventory()
    {
        var snapshot = new List<(int, Item)>();

        for (int i = 0; i < Player.inventory.Length; i++)
        {
            Item item = Player.inventory[i];
            if (item is not null && !item.IsAir)
            {
                snapshot.Add((i, item.Clone()));
                Mod.Logger.Debug($"[DeathBag] Snapshot slot {i}: {item.Name} x{item.stack}");
            }
        }

        return snapshot;
    }

    private void ClearInventory()
    {
        for (int i = 0; i < Player.inventory.Length; i++)
        {
            Player.inventory[i] = new Item();
        }
    }

    private void SpawnBagNPC(Vector2 position, List<(int SlotIndex, Item Item)> inventory)
    {
        if (Main.netMode == NetmodeID.Server)
            return;

        int npcIndex = NPC.NewNPC(
            Player.GetSource_Death(),
            (int)position.X,
            (int)position.Y,
            ModContent.NPCType<DeathBagNPC>());

        if (npcIndex < 0 || npcIndex >= Main.maxNPCs)
        {
            Mod.Logger.Error($"[DeathBag] Failed to spawn bag NPC: NewNPC returned {npcIndex}");
            return;
        }

        NPC npc = Main.npc[npcIndex];
        if (npc.ModNPC is DeathBagNPC bagNPC)
        {
            bagNPC.OwnerPlayerIndex = Player.whoAmI;
            bagNPC.OwnerName = Player.name;
            bagNPC.SavedInventory = inventory;
            npc.GivenName = $"{Player.name}'s Death Bag";
            Mod.Logger.Info($"[DeathBag] Bag NPC spawned (index {npcIndex}) for {Player.name} with {inventory.Count} items");
        }
        else
        {
            Mod.Logger.Error($"[DeathBag] Spawned NPC at index {npcIndex} is not DeathBagNPC (type: {npc.ModNPC?.GetType().Name ?? "null"})");
        }
    }
}
