using System.Collections.Generic;
using System.IO;
using Terraria;
using Terraria.ModLoader;
using Terraria.ModLoader.IO;
using DeathBag.Common;
using DeathBag.Common.NPCs;
using DB = DeathBag.DeathBag;

namespace DeathBag.Common.Items;

public sealed class DeathBagItem : ModItem
{
    public BagKind Kind = BagKind.Death;
    public string OwnerName = "";
    public int DeathLoadoutIndex;
    public string CarrierName = "";
    public List<(int SlotIndex, Item Item)> SavedInventory = new();

    public override string Texture => "DeathBag/Common/Items/DeathBagItem";

    internal BagPayload ToPayload()
    {
        return new BagPayload
        {
            Kind = Kind,
            OwnerName = OwnerName,
            DeathLoadoutIndex = DeathLoadoutIndex,
            CarrierName = CarrierName,
            SavedInventory = DB.CloneInventory(SavedInventory),
        };
    }

    public override void SetDefaults()
    {
        Item.width = 48;
        Item.height = 48;
        Item.maxStack = 1;
    }

    public override void LoadData(TagCompound tag)
    {
        Kind = tag.ContainsKey("kind") ? (BagKind)tag.GetByte("kind") : BagKind.Death;
        OwnerName = tag.GetString("ownerName");
        DeathLoadoutIndex = tag.ContainsKey("deathLoadout") ? tag.GetInt("deathLoadout") : 0;
        CarrierName = tag.ContainsKey("carrierName") ? tag.GetString("carrierName") : "";
        SavedInventory = BagPayloadHelper.ReadSavedInventory(tag);
        BagPayloadHelper.ConvertItemInPlace(Item, ToPayload());
    }

    public override void NetReceive(BinaryReader reader)
    {
        Kind = (BagKind)reader.ReadByte();
        OwnerName = reader.ReadString();
        DeathLoadoutIndex = reader.ReadInt32();
        CarrierName = reader.ReadString();
        SavedInventory = DB.ReadInventory(reader);
        BagPayloadHelper.ConvertItemInPlace(Item, ToPayload());
    }
}
