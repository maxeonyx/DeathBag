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

        if (_deathSnapshot.Count == 0)
        {
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

        SpawnBagNPC(Player.Center, snapshot);
    }

    /// <summary>
    /// Restores inventory from a bag using plan-then-execute pattern.
    /// Called from DeathBagNPC.OnChatButtonClicked on the owning client.
    /// </summary>
    public void RestoreFromBag(DeathBagNPC bag)
    {
        if (bag.SavedInventory.Count == 0)
            return;

        // === PLAN PHASE (no mutations) ===

        int totalSlots = Player.inventory.Length;
        Item[] result = new Item[totalSlots];

        // Initialize result with empty items
        for (int i = 0; i < totalSlots; i++)
        {
            result[i] = new Item();
        }

        // Step 1: Saved items reclaim their exact slots
        foreach (var (slotIndex, savedItem) in bag.SavedInventory)
        {
            if (slotIndex >= 0 && slotIndex < totalSlots)
            {
                result[slotIndex] = savedItem.Clone();
            }
        }

        // Step 2: Collect current items that need re-absorption
        var currentItems = new List<Item>();
        for (int i = 0; i < totalSlots; i++)
        {
            Item current = Player.inventory[i];
            if (current is not null && !current.IsAir)
                currentItems.Add(current.Clone());
        }

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
            }
        }

        // === EXECUTE PHASE (single tick) ===

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

        // Remove the bag NPC
        NPC npc = bag.NPC;
        npc.active = false;
        npc.netUpdate = true;

        // Sync inventory to server
        if (Main.netMode == NetmodeID.MultiplayerClient)
        {
            for (int i = 0; i < totalSlots; i++)
            {
                NetMessage.SendData(MessageID.SyncEquipment, -1, -1, null, Player.whoAmI, i);
            }
        }

        Main.NewText("Inventory restored!", Color.Green);
    }

    private List<(int SlotIndex, Item Item)> SnapshotInventory()
    {
        var snapshot = new List<(int, Item)>();

        for (int i = 0; i < Player.inventory.Length; i++)
        {
            Item item = Player.inventory[i];
            if (item is not null && !item.IsAir)
                snapshot.Add((i, item.Clone()));
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
            return;

        NPC npc = Main.npc[npcIndex];
        if (npc.ModNPC is DeathBagNPC bagNPC)
        {
            bagNPC.OwnerPlayerIndex = Player.whoAmI;
            bagNPC.OwnerName = Player.name;
            bagNPC.SavedInventory = inventory;
        }

        // In multiplayer, the NPC spawn is synced automatically by tModLoader
        // but we may need to send custom data (owner, inventory) via packet
        // TODO: Send BagCreated packet with inventory data for multiplayer
    }
}
