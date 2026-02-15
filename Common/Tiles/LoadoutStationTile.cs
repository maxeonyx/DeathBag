using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using Terraria.Localization;
using Terraria.ModLoader;
using Terraria.ObjectData;
using DeathBag.Common.Items;
using DeathBag.Common.NPCs;
using DeathBag.Common.Players;

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

        DustType = DustID.Bone;

        AddMapEntry(new Color(120, 140, 200), Language.GetText("Loadout Station"));

        // 2x2 furniture
        TileObjectData.newTile.CopyFrom(TileObjectData.Style2x2);
        TileObjectData.newTile.CoordinateHeights = new[] { 16, 18 };
        TileObjectData.addTile(Type);
    }

    public override bool RightClick(int i, int j)
    {
        if (Main.netMode == NetmodeID.Server)
            return false;

        Player player = Main.LocalPlayer;
        var modPlayer = player.GetModPlayer<DeathBagPlayer>();

        // Snapshot current inventory, excluding bag items and copper tools
        var snapshot = modPlayer.SnapshotInventory();
        snapshot.RemoveAll(entry =>
            entry.Item.ModItem is DeathBagItem
            || entry.Item.type == ItemID.CopperShortsword
            || entry.Item.type == ItemID.CopperPickaxe
            || entry.Item.type == ItemID.CopperAxe);

        if (snapshot.Count == 0)
        {
            Main.NewText("Nothing to store!", Color.Yellow);
            return true;
        }

        int loadoutIndex = player.CurrentLoadoutIndex;

        // Find an empty inventory slot for the bag item
        int emptySlot = -1;
        for (int s = 0; s < 50; s++)
        {
            if (player.inventory[s] == null || player.inventory[s].IsAir)
            {
                emptySlot = s;
                break;
            }
        }

        if (emptySlot < 0)
        {
            Main.NewText("No room in your inventory for a loadout bag!", Color.Yellow);
            return true;
        }

        // Clear the snapshotted items from inventory (keep bag items)
        ClearSnapshotFromInventory(player, snapshot);

        // Create loadout bag item in inventory
        var item = new Item();
        item.SetDefaults(ModContent.ItemType<DeathBagItem>());
        if (item.ModItem is DeathBagItem bagItem)
        {
            bagItem.Kind = BagKind.Loadout;
            bagItem.OwnerName = player.name;
            bagItem.DeathLoadoutIndex = loadoutIndex;
            bagItem.SavedInventory = snapshot;
            bagItem.CarrierName = player.name;
        }
        item.SetNameOverride($"{player.name}'s Loadout");
        player.inventory[emptySlot] = item;

        // Sync the new item slot to server
        if (Main.netMode == NetmodeID.MultiplayerClient)
            NetMessage.SendData(MessageID.SyncEquipment, -1, -1, null, player.whoAmI, emptySlot);

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

        // Slot constants match DeathBagPlayer
        const int slotArmor = 59;
        const int slotDye = 79;
        const int slotMiscEquips = 89;
        const int slotMiscDyes = 94;
        const int slotLoadoutsStart = 99;
        const int loadoutSize = 30;

        void ClearArray(Item[] array, int baseSlot)
        {
            for (int i = 0; i < array.Length; i++)
            {
                if (slotsToClear.Contains(baseSlot + i))
                    array[i] = new Item();
            }
        }

        ClearArray(player.inventory, 0);
        ClearArray(player.armor, slotArmor);
        ClearArray(player.dye, slotDye);
        ClearArray(player.miscEquips, slotMiscEquips);
        ClearArray(player.miscDyes, slotMiscDyes);

        for (int l = 0; l < player.Loadouts.Length; l++)
        {
            if (l == player.CurrentLoadoutIndex)
                continue;
            int loadoutBase = slotLoadoutsStart + l * loadoutSize;
            ClearArray(player.Loadouts[l].Armor, loadoutBase);
            ClearArray(player.Loadouts[l].Dye, loadoutBase + 20);
        }

        // Sync cleared slots to server
        if (Main.netMode == NetmodeID.MultiplayerClient)
        {
            int maxSlot = slotLoadoutsStart + player.Loadouts.Length * loadoutSize;
            for (int i = 0; i < maxSlot; i++)
            {
                if (slotsToClear.Contains(i))
                    NetMessage.SendData(MessageID.SyncEquipment, -1, -1, null, player.whoAmI, i);
            }
        }
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
