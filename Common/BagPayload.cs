using System.Collections.Generic;
using Terraria;
using Terraria.ModLoader;
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
    public static BagPayload FromItem(DeathBagItem bagItem)
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
        item.SetDefaults(ModContent.ItemType<DeathBagItem>());
        if (item.ModItem is DeathBagItem bagItem)
            ApplyToItem(bagItem, payload);
        return item;
    }

    public static void ApplyToItem(DeathBagItem bagItem, BagPayload payload)
    {
        bagItem.Kind = payload.Kind;
        bagItem.OwnerName = payload.OwnerName;
        bagItem.DeathLoadoutIndex = payload.DeathLoadoutIndex;
        bagItem.CarrierName = payload.CarrierName;
        bagItem.SavedInventory = DB.CloneInventory(payload.SavedInventory);

        if (!string.IsNullOrEmpty(payload.OwnerName))
            bagItem.Item.SetNameOverride($"{payload.OwnerName}'s {DB.GetBagKindName(payload.Kind)}");
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
