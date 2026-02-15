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

        var snapshot = _deathSnapshot;
        _deathSnapshot = null;

        // Extract any carried bag items (someone else's bags) — these become separate bag NPCs,
        // not nested inside the carrier's bag.
        var carriedBags = new List<Items.DeathBagItem>();
        snapshot.RemoveAll(entry =>
        {
            if (entry.Item.ModItem is Items.DeathBagItem bagItem)
            {
                carriedBags.Add(bagItem);
                Mod.Logger.Info($"[DeathBag] Extracting carried bag for {bagItem.OwnerName} from death snapshot");
                return true;
            }
            return false;
        });

        // Spawn carried bags as separate bag NPCs at death location
        foreach (var carriedBag in carriedBags)
        {
            SpawnBagNPC(Player.Center, carriedBag.SavedInventory, carriedBag.DeathLoadoutIndex, carriedBag.OwnerName);
        }

        // Spawn carrier's own bag (if anything remains)
        if (snapshot.Count > 0)
        {
            Mod.Logger.Info($"[DeathBag] Kill: spawning bag with {snapshot.Count} items at ({Player.Center.X:F0}, {Player.Center.Y:F0}), loadout {_deathLoadoutIndex}");
            SpawnBagNPC(Player.Center, snapshot, _deathLoadoutIndex);
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

        // Switch to the loadout that was active at death, so slot indices map to the correct arrays.
        // TrySwitchingLoadout is a no-op if already on the correct loadout.
        if (Player.CurrentLoadoutIndex != bag.DeathLoadoutIndex)
        {
            Mod.Logger.Info($"[DeathBag] Switching loadout {Player.CurrentLoadoutIndex} -> {bag.DeathLoadoutIndex} to match death state");
            Player.TrySwitchingLoadout(bag.DeathLoadoutIndex);
        }

        // === PLAN PHASE (no mutations) ===

        // Build result map: unified slot index -> item
        // Saved items reclaim their exact slots; equipment slots (59+) are never
        // used for re-absorption overflow — only inventory slots (0-58) absorb.
        var result = new Dictionary<int, Item>();

        foreach (var (slotIndex, savedItem) in bag.SavedInventory)
        {
            result[slotIndex] = savedItem.Clone();
        }
        Mod.Logger.Info($"[DeathBag] Step 1: {result.Count} saved items reclaim their slots");

        // Collect current items from ALL arrays that need re-absorption
        var currentItems = new List<Item>();

        void CollectCurrent(Item[] array, int baseSlot, string label)
        {
            for (int i = 0; i < array.Length; i++)
            {
                Item item = array[i];
                if (item is not null && !item.IsAir)
                {
                    currentItems.Add(item.Clone());
                    Mod.Logger.Debug($"[DeathBag] Collect {label}[{i}] (slot {baseSlot + i}): {item.Name} x{item.stack}");
                }
            }
        }

        CollectCurrent(Player.inventory, 0, "inventory");
        CollectCurrent(Player.armor, SlotArmor, "armor");
        CollectCurrent(Player.dye, SlotDye, "dye");
        CollectCurrent(Player.miscEquips, SlotMiscEquips, "miscEquips");
        CollectCurrent(Player.miscDyes, SlotMiscDyes, "miscDyes");

        for (int l = 0; l < Player.Loadouts.Length; l++)
        {
            if (l == Player.CurrentLoadoutIndex)
                continue;
            int loadoutBase = SlotLoadoutsStart + l * LoadoutSize;
            CollectCurrent(Player.Loadouts[l].Armor, loadoutBase, $"loadout{l}.Armor");
            CollectCurrent(Player.Loadouts[l].Dye, loadoutBase + 20, $"loadout{l}.Dye");
        }

        Mod.Logger.Info($"[DeathBag] Step 2: {currentItems.Count} current items to re-absorb");

        // Deduplicate copper starter tools — these respawn on death and pile up.
        // If the saved inventory already has one, discard the current copy.
        var copperTools = new HashSet<int>
        {
            ItemID.CopperShortsword,
            ItemID.CopperPickaxe,
            ItemID.CopperAxe,
        };

        var savedTypes = new HashSet<int>();
        foreach (var (_, savedItem) in bag.SavedInventory)
            savedTypes.Add(savedItem.type);

        currentItems.RemoveAll(item =>
        {
            if (copperTools.Contains(item.type) && savedTypes.Contains(item.type))
            {
                Mod.Logger.Info($"[DeathBag] Dedup: discarding extra {item.Name}");
                return true;
            }
            return false;
        });

        // Re-absorb current items into inventory slots (0-58) only.
        // Equipment slots are exact-position — we don't stuff adventure pickups into armor slots.
        int invSlots = Player.inventory.Length; // 59
        var toDrop = new List<Item>();

        foreach (Item current in currentItems)
        {
            int remaining = current.stack;

            // Try stacking onto matching items already in inventory result slots
            for (int i = 0; i < invSlots && remaining > 0; i++)
            {
                if (!result.TryGetValue(i, out Item target) || target.IsAir)
                    continue;
                if (target.type != current.type || target.stack >= target.maxStack)
                    continue;

                int canAdd = Math.Min(remaining, target.maxStack - target.stack);
                target.stack += canAdd;
                remaining -= canAdd;
            }

            // Try placing in empty inventory slots, respecting slot type restrictions:
            // Slots 50-53: ammo only (item.ammo != 0 || item.bait != 0)
            // Slots 54-57: coins only (item.IsACoin)
            // Slots 0-49, 58: anything
            for (int i = 0; i < invSlots && remaining > 0; i++)
            {
                if (result.ContainsKey(i))
                    continue;

                bool slotAccepts;
                if (i >= 54 && i <= 57)
                    slotAccepts = current.IsACoin;
                else if (i >= 50 && i <= 53)
                    slotAccepts = current.ammo != 0 || current.bait > 0;
                else
                    slotAccepts = true;

                if (!slotAccepts)
                    continue;

                Item placed = current.Clone();
                placed.stack = remaining;
                result[i] = placed;
                remaining = 0;
            }

            if (remaining > 0)
            {
                Item overflow = current.Clone();
                overflow.stack = remaining;
                toDrop.Add(overflow);
                Mod.Logger.Info($"[DeathBag] Overflow: {overflow.Name} x{remaining}");
            }
        }

        // === EXECUTE PHASE (single tick) ===

        Mod.Logger.Info($"[DeathBag] Executing restore: {result.Count} slots filled, {toDrop.Count} overflow items");

        // Write results back to the correct arrays
        void WriteBack(Item[] array, int baseSlot)
        {
            for (int i = 0; i < array.Length; i++)
            {
                int slot = baseSlot + i;
                array[i] = result.TryGetValue(slot, out Item item) ? item : new Item();
            }
        }

        WriteBack(Player.inventory, 0);
        WriteBack(Player.armor, SlotArmor);
        WriteBack(Player.dye, SlotDye);
        WriteBack(Player.miscEquips, SlotMiscEquips);
        WriteBack(Player.miscDyes, SlotMiscDyes);

        for (int l = 0; l < Player.Loadouts.Length; l++)
        {
            if (l == Player.CurrentLoadoutIndex)
                continue;
            int loadoutBase = SlotLoadoutsStart + l * LoadoutSize;
            WriteBack(Player.Loadouts[l].Armor, loadoutBase);
            WriteBack(Player.Loadouts[l].Dye, loadoutBase + 20);
        }

        // Drop overflow items
        foreach (Item drop in toDrop)
        {
            Player.QuickSpawnItem(Player.GetSource_Misc("DeathBagOverflow"), drop, drop.stack);
        }

        // Defer NPC removal to next tick (removing mid-frame can crash)
        _pendingBagRemoval = bag.NPC.whoAmI;

        // Mark bag as consumed immediately so AI() can't trigger a second restore
        bag.SavedInventory.Clear();

        // Sync all equipment slots to server
        if (Main.netMode == NetmodeID.MultiplayerClient)
        {
            // Highest possible slot: loadout 2 dye end = 99 + 2*30 + 10 = 189
            int maxSlot = SlotLoadoutsStart + Player.Loadouts.Length * LoadoutSize;
            for (int i = 0; i < maxSlot; i++)
            {
                NetMessage.SendData(MessageID.SyncEquipment, -1, -1, null, Player.whoAmI, i);
            }
        }

        Main.NewText("Inventory restored!", Color.Green);
        if (!string.IsNullOrEmpty(bag.DeliveredBy))
        {
            Main.NewText($"Delivered by {bag.DeliveredBy}!", Color.LightGreen);
        }
        Mod.Logger.Info("[DeathBag] Restore complete");
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

    private List<(int SlotIndex, Item Item)> SnapshotInventory()
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

    private void ClearInventory()
    {
        void Clear(Item[] array)
        {
            for (int i = 0; i < array.Length; i++)
                array[i] = new Item();
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

    private void SpawnBagNPC(Vector2 position, List<(int SlotIndex, Item Item)> inventory, int deathLoadoutIndex,
        string? ownerNameOverride = null)
    {
        string ownerName = ownerNameOverride ?? Player.name;

        if (Main.netMode == NetmodeID.MultiplayerClient)
        {
            // Send packet to server — server spawns the NPC and syncs to all clients
            DB.SendBagCreated(Mod, position.X, position.Y, ownerName, Player.whoAmI, deathLoadoutIndex, inventory);
            Mod.Logger.Info($"[DeathBag] Sent BagCreated packet for {ownerName} with {inventory.Count} items");
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
            Mod.Logger.Error($"[DeathBag] Failed to spawn bag NPC: NewNPC returned {npcIndex}");
            return;
        }

        NPC npc = Main.npc[npcIndex];
        if (npc.ModNPC is DeathBagNPC bagNPC)
        {
            bagNPC.OwnerPlayerIndex = -1; // Will be resolved by ResolveOwnerIndex
            bagNPC.OwnerName = ownerName;
            bagNPC.DeathLoadoutIndex = deathLoadoutIndex;
            bagNPC.SavedInventory = inventory;
            npc.GivenName = $"{ownerName}'s Death Bag";
            npc.netUpdate = true;
            bagNPC.ResolveOwnerIndex();
            Mod.Logger.Info($"[DeathBag] Bag NPC spawned (index {npcIndex}) for {ownerName} with {inventory.Count} items, loadout {deathLoadoutIndex}");
        }
        else
        {
            Mod.Logger.Error($"[DeathBag] Spawned NPC at index {npcIndex} is not DeathBagNPC (type: {npc.ModNPC?.GetType().Name ?? "null"})");
        }
    }
}
