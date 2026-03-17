using System.Collections.Generic;
using Terraria;
using Terraria.ModLoader;
using Terraria.ModLoader.IO;
using DeathBag.Common.Items;
using DeathBag.Common.NPCs;
using DB = DeathBag.DeathBag;

namespace DeathBag.Common;

internal sealed class BagPayload
{
    public BagKind Kind = BagKind.Death;
    public string OwnerName = "";
    public int DeathLoadoutIndex;
    public string CarrierName = "";
    public List<(int SlotIndex, Item Item)> SavedInventory = new();
}

internal static class BagPayloadHelper
{
    public static BagPayload FromItem(BagItemBase bagItem)
    {
        return new BagPayload
        {
            Kind = bagItem.Kind,
            OwnerName = bagItem.OwnerName,
            DeathLoadoutIndex = bagItem.DeathLoadoutIndex,
            CarrierName = bagItem.CarrierName,
            SavedInventory = DB.CloneInventory(bagItem.SavedInventory),
        };
    }

    public static BagPayload FromNPC(DeathBagNPC bagNPC)
    {
        return new BagPayload
        {
            Kind = bagNPC.Kind,
            OwnerName = bagNPC.OwnerName,
            DeathLoadoutIndex = bagNPC.DeathLoadoutIndex,
            CarrierName = bagNPC.DeliveredBy,
            SavedInventory = DB.CloneInventory(bagNPC.SavedInventory),
        };
    }

    public static Item CreateBagItem(BagPayload payload)
    {
        Item item = new();
        item.SetDefaults(GetItemType(payload.Kind));
        if (item.ModItem is BagItemBase bagItem)
            ApplyToItem(bagItem, payload);
        return item;
    }

    public static void ApplyToItem(BagItemBase bagItem, BagPayload payload)
    {
        bagItem.OwnerName = payload.OwnerName;
        bagItem.DeathLoadoutIndex = payload.DeathLoadoutIndex;
        bagItem.CarrierName = payload.CarrierName;
        bagItem.SavedInventory = DB.CloneInventory(payload.SavedInventory);

        if (!string.IsNullOrEmpty(payload.OwnerName))
            bagItem.Item.SetNameOverride($"{payload.OwnerName}'s {DB.GetBagKindName(payload.Kind)}");
    }

    public static void ConvertItemInPlace(Item item, BagPayload payload)
    {
        item.SetDefaults(GetItemType(payload.Kind));
        if (item.ModItem is not BagItemBase bagItem)
            return;

        ApplyToItem(bagItem, payload);
    }

    public static bool TryMigrateLegacyItemInPlace(Item item)
    {
        if (item?.ModItem is not DeathBagItem legacyItem)
            return false;

        ConvertItemInPlace(item, legacyItem.ToPayload());
        return true;
    }

    public static BagPayload ReadPayload(TagCompound tag)
    {
        return new BagPayload
        {
            Kind = tag.ContainsKey("kind") ? (BagKind)tag.GetByte("kind") : BagKind.Death,
            OwnerName = tag.GetString("ownerName"),
            DeathLoadoutIndex = tag.ContainsKey("deathLoadout") ? tag.GetInt("deathLoadout") : 0,
            CarrierName = tag.ContainsKey("carrierName") ? tag.GetString("carrierName") : "",
            SavedInventory = ReadSavedInventory(tag),
        };
    }

    public static List<(int SlotIndex, Item Item)> ReadSavedInventory(TagCompound tag)
    {
        var savedInventory = new List<(int SlotIndex, Item Item)>();
        if (!tag.ContainsKey("items"))
            return savedInventory;

        var itemList = tag.GetList<TagCompound>("items");
        foreach (TagCompound itemTag in itemList)
        {
            int slot = itemTag.GetInt("slot");
            Item item = ItemIO.Load(itemTag.GetCompound("item"));
            savedInventory.Add((slot, item));
        }

        return savedInventory;
    }

    public static int GetItemType(BagKind kind)
    {
        return kind switch
        {
            BagKind.Loadout => ModContent.ItemType<LoadoutBagItem>(),
            BagKind.Overflow => ModContent.ItemType<OverflowBagItem>(),
            _ => ModContent.ItemType<PortableDeathBagItem>(),
        };
    }

    public static void ApplyToNPC(DeathBagNPC bagNPC, BagPayload payload, int ownerPlayerIndex)
    {
        bagNPC.Kind = payload.Kind;
        bagNPC.OwnerPlayerIndex = ownerPlayerIndex;
        bagNPC.OwnerName = payload.OwnerName;
        bagNPC.DeathLoadoutIndex = payload.DeathLoadoutIndex;
        bagNPC.DeliveredBy = payload.CarrierName;
        bagNPC.SavedInventory = DB.CloneInventory(payload.SavedInventory);
    }
}
