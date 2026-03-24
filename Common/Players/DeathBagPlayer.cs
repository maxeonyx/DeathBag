using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.DataStructures;
using Terraria.ID;
using Terraria.ModLoader;
using DB = DeathBag.DeathBag;
using DeathBag.Common;
using DeathBag.Common.NPCs;

namespace DeathBag.Common.Players;

public sealed class DeathBagPlayer : ModPlayer
{
    // Sentinel slot index used to represent the cursor item (Main.mouseItem).
    // This is not a real inventory slot, so restore code must handle it specially.
    private const int CursorSlotSentinel = -1;
    internal const int CursorInventorySlot = 58;

    internal enum PendingPlacementSourceKind : byte
    {
        InventorySlot = 0,
        Cursor = 1,
    }

    internal sealed class PendingBagPlacement
    {
        public int RequestId;
        public PendingPlacementSourceKind SourceKind;
        public int SourceSlot;
        public BagPayload Payload = new();
    }

    /// <summary>
    /// Snapshot taken at moment of death, before vanilla drops items.
    /// Null when no snapshot is pending.
    /// </summary>
    private List<(int SlotIndex, Item Item)>? _deathSnapshot;

    /// <summary>
    /// Which loadout was active at time of death. Used to restore the correct loadout.
    /// </summary>
    private int _deathLoadoutIndex;

    private PendingBagPlacement? _pendingBagPlacement;

    internal bool HasPendingBagPlacement => _pendingBagPlacement is not null;

    internal void BeginPendingBagPlacement(int requestId, int sourceSlot, BagPayload payload)
    {
        _pendingBagPlacement = new PendingBagPlacement
        {
            RequestId = requestId,
            SourceKind = sourceSlot == CursorSlotSentinel ? PendingPlacementSourceKind.Cursor : PendingPlacementSourceKind.InventorySlot,
            SourceSlot = sourceSlot,
            Payload = new BagPayload
            {
                Kind = payload.Kind,
                OwnerName = payload.OwnerName,
                DeathLoadoutIndex = payload.DeathLoadoutIndex,
                CarrierName = payload.CarrierName,
                SavedInventory = DB.CloneInventory(payload.SavedInventory),
            },
        };
    }

    internal bool TryGetPendingBagPlacement(int requestId, out PendingBagPlacement pendingPlacement)
    {
        if (_pendingBagPlacement is not null && _pendingBagPlacement.RequestId == requestId)
        {
            pendingPlacement = _pendingBagPlacement;
            return true;
        }

        pendingPlacement = null;
        return false;
    }

    internal void ClearPendingBagPlacement()
    {
        _pendingBagPlacement = null;
    }

    internal bool TryConsumePendingBagPlacement(PendingBagPlacement pendingPlacement)
    {
        if (_pendingBagPlacement is null || !ReferenceEquals(_pendingBagPlacement, pendingPlacement))
            return false;

        Item sourceItem = pendingPlacement.SourceKind == PendingPlacementSourceKind.Cursor
            ? (Player.inventory.Length > CursorInventorySlot ? Player.inventory[CursorInventorySlot] : null)
            : (pendingPlacement.SourceSlot >= 0 && pendingPlacement.SourceSlot < SlotHelper.MainInventorySlotCount
                ? Player.inventory[pendingPlacement.SourceSlot]
                : null);

        bool consumed = pendingPlacement.SourceKind switch
        {
            PendingPlacementSourceKind.Cursor => sourceItem is not null
                && DB.IsMatchingBagItem(sourceItem, pendingPlacement.Payload),
            PendingPlacementSourceKind.InventorySlot => pendingPlacement.SourceSlot >= 0
                && pendingPlacement.SourceSlot < SlotHelper.MainInventorySlotCount
                && DB.IsMatchingBagItem(Player.inventory[pendingPlacement.SourceSlot], pendingPlacement.Payload),
            _ => false,
        };

        if (!consumed)
            return false;

        DB.LogBagContents(Mod, "placed bag via left-click", pendingPlacement.Payload.OwnerName, pendingPlacement.Payload.Kind, pendingPlacement.Payload.SavedInventory);

        if (pendingPlacement.SourceKind == PendingPlacementSourceKind.Cursor)
        {
            Player.inventory[CursorInventorySlot].TurnToAir();
            Main.mouseItem = new Item();
        }
        else
        {
            Player.inventory[pendingPlacement.SourceSlot].TurnToAir();
        }

        ClearPendingBagPlacement();
        return true;
    }

    /// <summary>Copper starter tools — never worth bagging.</summary>
    private static readonly HashSet<int> CopperTools = new()
    {
        ItemID.CopperShortsword,
        ItemID.CopperPickaxe,
        ItemID.CopperAxe,
    };

    private static readonly HashSet<int> CoinTypes = new()
    {
        ItemID.CopperCoin,
        ItemID.SilverCoin,
        ItemID.GoldCoin,
        ItemID.PlatinumCoin,
    };

    private static bool IsCoin(Item item)
    {
        return item is not null && !item.IsAir && CoinTypes.Contains(item.type);
    }

    private static bool IsPreservedDuringRestore(Item item)
    {
        if (item is null || item.IsAir)
            return false;
        if (IsCoin(item))
            return true;
        if (item.ModItem is Items.BagItemBase)
            return true;
        if (CopperTools.Contains(item.type))
            return true;
        return false;
    }

    private List<(int SlotIndex, Item Item)> ExtractPreservedItemsFromMainInventory()
    {
        // Bag items and coins live in Player.inventory, including coin/ammo/cursor-adjacent slots.
        var extracted = new List<(int, Item)>();
        for (int i = 0; i < Player.inventory.Length; i++)
        {
            Item item = Player.inventory[i];
            if (IsPreservedDuringRestore(item))
            {
                extracted.Add((i, item.Clone()));
                Player.inventory[i] = new Item();
            }
        }
        return extracted;
    }

    private void ReinsertPreservedItemsIntoMainInventory(List<(int SlotIndex, Item Item)> extracted)
    {
        foreach (var (slotIndex, savedItem) in extracted)
        {
            Item item = savedItem.Clone();

            // Try original slot first (keeps UI layout stable when possible)
            if (slotIndex >= 0 && slotIndex < Player.inventory.Length)
            {
                Item cur = Player.inventory[slotIndex];
                if (cur is null || cur.IsAir)
                {
                    Player.inventory[slotIndex] = item;
                    continue;
                }
            }

            // Fall back to normal inventory placement. Any remainder drops.
            Item remainder = Player.GetItem(Player.whoAmI, item, GetItemSettings.NPCEntityToPlayerInventorySettings);
            if (remainder is not null && !remainder.IsAir)
                Player.QuickSpawnItem(Player.GetSource_Misc("DeathBagPreservedReinsert"), remainder, remainder.stack);
        }
    }

    private static void AppendItemsToBag(Item bagItem, List<(int SlotIndex, Item Item)> items)
    {
        if (bagItem.ModItem is not Items.BagItemBase modBag)
            return;

        modBag.SavedInventory.AddRange(items);
        if (!string.IsNullOrEmpty(modBag.OwnerName))
            bagItem.SetNameOverride($"{modBag.OwnerName}'s {DB.GetBagKindName(modBag.Kind)}");
    }

    private Item CreateBagItem(BagKind kind, List<(int SlotIndex, Item Item)> savedInventory)
    {
        var payload = new BagPayload
        {
            Kind = kind,
            OwnerName = Player.name,
            DeathLoadoutIndex = Player.CurrentLoadoutIndex,
            SavedInventory = savedInventory,
        };

        return BagPayloadHelper.CreateBagItem(payload);
    }

    private static bool IsOverflowBagItem(Item item)
    {
        if (item?.ModItem is not Items.BagItemBase bagItem)
            return false;

        return bagItem.Kind == BagKind.Overflow;
    }

    private Item? FindExistingOverflowBag(List<(int SlotIndex, Item Item)> preserved)
    {
        for (int i = 0; i < SlotHelper.MainInventorySlotCount; i++)
        {
            if (IsOverflowBagItem(Player.inventory[i]))
                return Player.inventory[i];
        }

        foreach (var (_, item) in preserved)
        {
            if (IsOverflowBagItem(item))
                return item;
        }

        return null;
    }

    private int CountPreservedBagItemsNeedingNewSlots(List<(int SlotIndex, Item Item)> preserved)
    {
        int count = 0;
        foreach (var (slotIndex, item) in preserved)
        {
            if (item.ModItem is not Items.BagItemBase)
                continue;
            if (slotIndex < 0 || slotIndex >= SlotHelper.MainInventorySlotCount)
            {
                count++;
                continue;
            }
            if (Player.inventory[slotIndex] is not null && !Player.inventory[slotIndex].IsAir)
                count++;
        }
        return count;
    }

    private void RestoreDisplacedMainInventoryItems(List<(int SlotIndex, Item Item)> displacedItems)
    {
        foreach (var (slotIndex, displacedItem) in displacedItems)
            Player.inventory[slotIndex] = displacedItem.Clone();
    }

    private bool TryFreeSlotsForCarryoverBag(List<(int SlotIndex, Item Item)> preserved)
    {
        Item? existingOverflowBag = FindExistingOverflowBag(preserved);
        int preservedBagSlotsNeeded = CountPreservedBagItemsNeedingNewSlots(preserved);
        int neededFreedSlots = 1 + preservedBagSlotsNeeded;
        if (existingOverflowBag is null)
            neededFreedSlots++;
        var displacedItems = new List<(int SlotIndex, Item Item)>();

        for (int slot = SlotHelper.MainInventorySlotCount - 1; slot >= 0 && displacedItems.Count < neededFreedSlots; slot--)
        {
            Item candidate = Player.inventory[slot];
            if (candidate is null || candidate.IsAir || candidate.favorited)
                continue;

            displacedItems.Add((slot, candidate.Clone()));
            Player.inventory[slot] = new Item();
        }

        if (displacedItems.Count < neededFreedSlots)
        {
            RestoreDisplacedMainInventoryItems(displacedItems);
            Mod.Logger.Info("[DeathBag] Could not free enough non-favorited main inventory slots to keep carryover loadout bag in inventory");
            return false;
        }

        if (existingOverflowBag is not null)
        {
            AppendItemsToBag(existingOverflowBag, displacedItems);
            Mod.Logger.Info($"[DeathBag] Appended {displacedItems.Count} displaced item(s) into existing overflow bag");
            return true;
        }

        Item overflowBagItem = CreateBagItem(BagKind.Overflow, displacedItems);
        Item overflowRemainder = Player.GetItem(Player.whoAmI, overflowBagItem, GetItemSettings.NPCEntityToPlayerInventorySettings);
        if (overflowRemainder is null || overflowRemainder.IsAir)
        {
            Mod.Logger.Info($"[DeathBag] Created overflow bag with {displacedItems.Count} displaced item(s)");
            return true;
        }

        Mod.Logger.Warn("[DeathBag] Failed to place overflow bag item after freeing slots; dropping displaced items instead");
        foreach (var (_, displacedItem) in displacedItems)
            Player.QuickSpawnItem(Player.GetSource_Misc("DeathBagOverflowFallback"), displacedItem, displacedItem.stack);

        return true;
    }

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

        // Include cursor item (item on the mouse). Vanilla can clear it on death and our
        // inventory clear prevents it from being dropped, so we must capture it explicitly.
        // We store it with a sentinel slot index and handle it during restore via GetItem.
        bool capturedCursorItem = false;
        if (Main.mouseItem is not null && !Main.mouseItem.IsAir)
        {
            Item cursor = Main.mouseItem;
            bool isCopperTool = CopperTools.Contains(cursor.type);
            bool isBagItem = cursor.ModItem is Items.BagItemBase;
            bool isCoin = IsCoin(cursor);
            if (!isCopperTool && !isBagItem && !isCoin)
            {
                _deathSnapshot.Add((CursorSlotSentinel, cursor.Clone()));
                capturedCursorItem = true;
                Mod.Logger.Info($"[DeathBag] PreKill: captured cursor item {cursor.Name} x{cursor.stack}");
            }
        }

        // Filter out copper tools, bag items, and coins — these are never worth bagging
        _deathSnapshot.RemoveAll(entry =>
            CopperTools.Contains(entry.Item.type) || entry.Item.ModItem is Items.BagItemBase || IsCoin(entry.Item));

        Mod.Logger.Info($"[DeathBag] PreKill: snapshotted {_deathSnapshot.Count} items for {Player.name} (after filtering copper tools + bag items + coins)");

        if (_deathSnapshot.Count == 0)
        {
            Mod.Logger.Info("[DeathBag] PreKill: empty inventory, no bag will spawn");
            _deathSnapshot = null;
            return true;
        }

        // If we captured the cursor item for bagging, clear it now so it can't remain on the cursor
        // while also being saved into the bag snapshot.
        if (capturedCursorItem)
            Main.mouseItem = new Item();

        // Clear inventory so vanilla's DropItems finds nothing to scatter —
        // EXCEPT carried bag items, copper tools, and coins, which vanilla will drop naturally.
        ClearInventory(preserveBagItems: true, preserveCopperTools: true, preserveCoins: true);

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

    /// <summary>
    /// Prevents bag items from being sold at NPC shops.
    /// The item stays on the cursor — player can drop it normally instead.
    /// </summary>
    public override bool CanSellItem(NPC npc, Item[] shopInventory, Item item)
    {
        if (item?.ModItem is Items.BagItemBase)
            return false;

        return base.CanSellItem(npc, shopInventory, item);
    }

    public override void PreUpdate()
    {
        if (Player.whoAmI != Main.myPlayer)
            return;

        // Safety net: if a bag item ends up in the trash slot, drop it instead.
        // Runs at the start of the tick (before input processing) so there's no
        // window to interact with the bag while it's in the trash slot.
        if (Player.trashItem?.ModItem is Items.BagItemBase)
        {
            Player.QuickSpawnItem(Player.GetSource_Misc("DeathBagDrop"), Player.trashItem, Player.trashItem.stack);
            Player.trashItem.TurnToAir();
        }
    }

    /// <summary>
    /// Restores inventory from a bag NPC:
    /// 1. Snapshot current inventory (excluding copper tools and bag items)
    /// 2. If snapshot is non-empty, create a loadout bag item holding that snapshot
    /// 3. Clear inventory (preserving copper tools and bag items)
    /// 4. Place bag's saved items into their exact original slots
    /// 5. Place the carryover loadout bag item into an empty inventory slot (or drop if no room)
    /// 6. Sync all slots to server
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

        // IMPORTANT: copper tools and bag items are preserved, but bag restore writes exact slot indices.
        // If we leave preserved items in place, they can be overwritten (data loss). Extract them first.
        var preserved = ExtractPreservedItemsFromMainInventory();

        var currentSnapshot = SnapshotInventory();
        currentSnapshot.RemoveAll(entry =>
            CopperTools.Contains(entry.Item.type) || entry.Item.ModItem is Items.BagItemBase || IsCoin(entry.Item));

        Mod.Logger.Info($"[DeathBag] Current inventory snapshot: {currentSnapshot.Count} items (after filtering copper tools + bag items + coins)");

        // Current carried inventory becomes a loadout bag item.
        Item? carryoverBagItem = null;
        if (currentSnapshot.Count > 0)
        {
            carryoverBagItem = CreateBagItem(BagKind.Loadout, currentSnapshot);
            Mod.Logger.Info($"[DeathBag] Created carryover loadout bag item with {currentSnapshot.Count} items");
        }

        ClearInventory(preserveBagItems: false, preserveCopperTools: false);

        foreach (var (slotIndex, savedItem) in bag.SavedInventory)
        {
            Item item = savedItem.Clone();
            if (slotIndex < 0)
            {
                Item remainder = Player.GetItem(Player.whoAmI, item, GetItemSettings.NPCEntityToPlayerInventorySettings);
                if (remainder is not null && !remainder.IsAir)
                    Player.QuickSpawnItem(Player.GetSource_Misc("DeathBagRestoreCursor"), remainder, remainder.stack);
                continue;
            }

            SetSlotByIndex(slotIndex, item);
        }

        Mod.Logger.Info($"[DeathBag] Placed {bag.SavedInventory.Count} bag items into inventory");

        if (carryoverBagItem != null)
        {
            bool needsOverflowHelp = true;
            for (int i = 0; i < SlotHelper.MainInventorySlotCount; i++)
            {
                if (Player.inventory[i] is null || Player.inventory[i].IsAir)
                {
                    needsOverflowHelp = false;
                    break;
                }
            }

            if (needsOverflowHelp)
            {
                Mod.Logger.Info("[DeathBag] Main inventory is full after restore; attempting overflow compaction for carryover loadout bag");
                TryFreeSlotsForCarryoverBag(preserved);
            }

            Item remainder = Player.GetItem(Player.whoAmI, carryoverBagItem, GetItemSettings.NPCEntityToPlayerInventorySettings);
            if (remainder is not null && !remainder.IsAir)
            {
                Player.QuickSpawnItem(Player.GetSource_Misc("DeathBagLoadout"), remainder, remainder.stack);
                Mod.Logger.Info("[DeathBag] No room for carryover loadout bag item — dropped on ground");
            }
            else
            {
                Mod.Logger.Info("[DeathBag] Placed carryover loadout bag item in inventory");
            }
        }

        ReinsertPreservedItemsIntoMainInventory(preserved);

        // Log contents before clearing (disaster recovery)
        DB.LogBagContents(Mod, "owner restore", bag.OwnerName, bag.Kind, bag.SavedInventory);

        // Make the NPC immediately non-interactable on this client so no second interaction path
        // can run while we wait for the server/client removal sync to arrive.
        int restoredNpcIndex = bag.NPC.whoAmI;
        bag.InteractionLocked = true;
        bag.SavedInventory.Clear();
        bag.NPC.velocity = Vector2.Zero;

        if (Main.netMode == NetmodeID.MultiplayerClient)
        {
            DB.SendBagRemoved(Mod, restoredNpcIndex);
        }
        else
        {
            bag.NPC.active = false;
        }

        // === 6. SYNC all slots to server ===
        if (Main.netMode == NetmodeID.MultiplayerClient)
        {
            // Sync every slot in the full range — the clear step emptied slots
            // the server still thinks are occupied, so we must sync everything.
            int maxSlot = SlotHelper.GetMaxSyncEquipmentSlot(Player);
            for (int slot = 0; slot < maxSlot; slot++)
                NetMessage.SendData(MessageID.SyncEquipment, -1, -1, null, Player.whoAmI, slot);
        }

        Main.NewText("Inventory restored!", Color.Green);
        if (!string.IsNullOrEmpty(bag.DeliveredBy))
            Main.NewText($"Delivered by {bag.DeliveredBy}!", Color.LightGreen);
        Mod.Logger.Info("[DeathBag] Restore complete");
    }

    /// <summary>
    /// Attempts to place an item into the exact slot index, but only if that slot is currently empty.
    /// Returns true if placed, false if the slot was occupied or unsupported.
    /// </summary>
    internal bool TryPlaceInSlotIfEmpty(int slot, Item item)
    {
        return SlotHelper.TryPlaceInSlotIfEmpty(Player, slot, item);
    }

    private void SetSlotByIndex(int slot, Item item)
    {
        SlotHelper.TrySetSlot(Player, slot, item);
    }

    internal List<(int SlotIndex, Item Item)> SnapshotInventory()
    {
        var snapshot = new List<(int, Item)>();

        void Snap(Item[] array, int baseSlot, string label)
        {
            for (int i = 0; i < array.Length; i++)
            {
                Item item = array[i];
                if (item is not null && !item.IsAir && !IsCoin(item))
                {
                    snapshot.Add((baseSlot + i, item.Clone()));
                    Mod.Logger.Debug($"[DeathBag] Snapshot {label}[{i}] (slot {baseSlot + i}): {item.Name} x{item.stack}");
                }
            }
        }

        Mod.Logger.Info($"[DeathBag] Snapshot: CurrentLoadoutIndex={Player.CurrentLoadoutIndex}, Loadouts.Length={Player.Loadouts.Length}");
        Snap(Player.inventory, 0, "inventory");
        Snap(Player.armor, SlotHelper.SlotArmor, "armor");
        Snap(Player.dye, SlotHelper.SlotDye, "dye");
        Snap(Player.miscEquips, SlotHelper.SlotMiscEquips, "miscEquips");
        Snap(Player.miscDyes, SlotHelper.SlotMiscDyes, "miscDyes");

        // Inactive loadouts (active loadout's items are already in Player.armor/dye)
        for (int l = 0; l < Player.Loadouts.Length; l++)
        {
            if (l == Player.CurrentLoadoutIndex)
                continue;

            int loadoutBase = SlotHelper.SlotLoadoutsStart + l * SlotHelper.LoadoutSize;
            Snap(Player.Loadouts[l].Armor, loadoutBase, $"loadout{l}.Armor");
            Snap(Player.Loadouts[l].Dye, loadoutBase + SlotHelper.LoadoutArmorSlotCount, $"loadout{l}.Dye");
        }

        return snapshot;
    }

    private void ClearInventory(bool preserveBagItems = false, bool preserveCopperTools = false, bool preserveCoins = false)
    {
        void Clear(Item[] array)
        {
            for (int i = 0; i < array.Length; i++)
            {
                if (preserveBagItems && array[i]?.ModItem is Items.BagItemBase)
                    continue;
                if (preserveCopperTools && array[i] is not null && CopperTools.Contains(array[i].type))
                    continue;
                if (preserveCoins && IsCoin(array[i]))
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
            var payload = new BagPayload
            {
                Kind = kind,
                OwnerName = ownerName,
                DeathLoadoutIndex = deathLoadoutIndex,
                SavedInventory = inventory,
            };

            BagPayloadHelper.ApplyToNPC(bagNPC, payload, ownerPlayerIndex: -1); // Will be resolved by ResolveOwnerIndex
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
