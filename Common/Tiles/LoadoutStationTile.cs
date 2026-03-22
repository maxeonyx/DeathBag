using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using Terraria.Localization;
using Terraria.ModLoader;
using Terraria.ObjectData;
using Terraria.GameContent.ObjectInteractions;
using DeathBag.Common;
using DeathBag.Common.Items;
using DeathBag.Common.NPCs;
using DeathBag.Common.Players;
using DB = DeathBag.DeathBag;

namespace DeathBag.Common.Tiles;

/// <summary>
/// Loadout Station furniture tile. Right-click to snapshot your current inventory
/// (excluding bag items) into a loadout bag NPC at the player's position.
/// </summary>
public sealed class LoadoutStationTile : ModTile
{
    public override void SetStaticDefaults()
    {
        Main.tileFrameImportant[Type] = true;
        Main.tileNoAttach[Type] = true;
        Main.tileSolidTop[Type] = false;
        TileID.Sets.DisableSmartCursor[Type] = true;
        TileID.Sets.HasOutlines[Type] = true;

        DustType = DustID.Bone;

        AddMapEntry(new Color(120, 140, 200), Language.GetText("Loadout Station"));

        // 3x3 furniture
        TileObjectData.newTile.CopyFrom(TileObjectData.Style3x3);
        TileObjectData.newTile.CoordinateHeights = new[] { 16, 16, 18 };
        TileObjectData.addTile(Type);
    }

    public override bool RightClick(int i, int j)
    {
        if (Main.netMode == NetmodeID.Server)
            return false;

        // Don't activate if player is talking to an NPC (e.g. bag NPC on top of station)
        if (Main.LocalPlayer.talkNPC >= 0)
            return false;

        // Don't activate if a bag NPC is under the cursor (owner right-click is handled in AI)
        Vector2 mouseWorld = Main.MouseWorld;
        for (int n = 0; n < Main.maxNPCs; n++)
        {
            NPC npc = Main.npc[n];
            if (npc.active && npc.ModNPC is DeathBagNPC && npc.Hitbox.Contains(mouseWorld.ToPoint()))
                return false;
        }

        Player player = Main.LocalPlayer;
        var modPlayer = player.GetModPlayer<DeathBagPlayer>();

        // Snapshot current inventory, excluding bag items, copper tools, and coins
        var snapshot = modPlayer.SnapshotInventory();
        snapshot.RemoveAll(entry =>
            entry.Item.ModItem is BagItemBase
            || entry.Item.type == ItemID.CopperShortsword
            || entry.Item.type == ItemID.CopperPickaxe
            || entry.Item.type == ItemID.CopperAxe
            || entry.Item.type == ItemID.CopperCoin
            || entry.Item.type == ItemID.SilverCoin
            || entry.Item.type == ItemID.GoldCoin
            || entry.Item.type == ItemID.PlatinumCoin);

        // Cursor item dupe safety: the cursor item is effectively an inventory slot in Terraria.
        // Don't include it in loadout station snapshots unless we can atomically clear it too.
        // Slot 58 is the cursor/held-item slot.
        const int cursorSlotIndex = 58;
        bool removedCursorSlot = snapshot.RemoveAll(entry => entry.SlotIndex == cursorSlotIndex) > 0;
        if (removedCursorSlot)
            Mod.Logger.Info("[DeathBag] Loadout station: excluded cursor slot (58) from snapshot to prevent dupe");

        if (snapshot.Count == 0)
        {
            Main.NewText("Nothing to store!", Color.Yellow);
            return true;
        }

        int loadoutIndex = player.CurrentLoadoutIndex;
        var existingMatchingSlots = DB.FindMatchingBagItemSlots(player, player.name, BagKind.Loadout, loadoutIndex, player.name, snapshot);

        // Clear the snapshotted items from inventory (keep bag items)
        ClearSnapshotFromInventory(player, snapshot);

        // Create loadout bag item and place in inventory
        var payload = new BagPayload
        {
            Kind = BagKind.Loadout,
            OwnerName = player.name,
            DeathLoadoutIndex = loadoutIndex,
            CarrierName = player.name,
            SavedInventory = snapshot,
        };
        Item item = BagPayloadHelper.CreateBagItem(payload);

        Item remainder = player.GetItem(player.whoAmI, item, GetItemSettings.NPCEntityToPlayerInventorySettings);
        if (remainder is not null && !remainder.IsAir)
        {
            // This shouldn't happen — we just cleared inventory — but handle it safely
            player.QuickSpawnItem(player.GetSource_Misc("DeathBagLoadout"), remainder, remainder.stack);
            Mod.Logger.Warn("[DeathBag] Loadout station: no room after clear — dropped bag item");
        }

        // Sync the new item slot to server
        if (Main.netMode == NetmodeID.MultiplayerClient)
        {
            int placedSlot = DB.FindNewMatchingBagItemSlot(player, player.name, BagKind.Loadout, loadoutIndex, player.name, snapshot, existingMatchingSlots);
            if (placedSlot >= 0)
                NetMessage.SendData(MessageID.SyncEquipment, -1, -1, null, player.whoAmI, placedSlot);
        }

        Main.NewText($"Created loadout bag with {snapshot.Count} items.", new Color(140, 180, 255));
        Mod.Logger.Info($"[DeathBag] Loadout station: created bag item with {snapshot.Count} items for {player.name}, loadout {loadoutIndex}");

        return true;
    }

    /// <summary>
    /// Removes snapshotted items from the player's inventory. Items are identified by
    /// their exact slot index from the snapshot — we clear those slots.
    /// </summary>
    private static void ClearSnapshotFromInventory(Player player, List<(int SlotIndex, Item Item)> snapshot)
    {
        // Build set of slot indices to clear
        var slotsToClear = new HashSet<int>();
        foreach (var (slotIndex, _) in snapshot)
            slotsToClear.Add(slotIndex);

        foreach (int slotIndex in slotsToClear)
            SlotHelper.TryClearSlot(player, slotIndex);

        // Sync cleared slots to server
        if (Main.netMode == NetmodeID.MultiplayerClient)
        {
            int maxSlot = SlotHelper.GetMaxSyncEquipmentSlot(player);
            for (int i = 0; i < maxSlot; i++)
            {
                if (slotsToClear.Contains(i))
                    NetMessage.SendData(MessageID.SyncEquipment, -1, -1, null, player.whoAmI, i);
            }
        }
    }

    public override bool HasSmartInteract(int i, int j, SmartInteractScanSettings settings)
    {
        return true;
    }

    public override void NumDust(int i, int j, bool fail, ref int num)
    {
        num = fail ? 1 : 3;
    }

    public override void MouseOver(int i, int j)
    {
        Player player = Main.LocalPlayer;
        player.noThrow = 2;
        player.cursorItemIconEnabled = true;
        player.cursorItemIconID = ModContent.ItemType<LoadoutStationItem>();
    }
}
