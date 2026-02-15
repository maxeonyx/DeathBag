using System.Collections.Generic;
using System.IO;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.Chat;
using Terraria.ID;
using Terraria.Localization;
using Terraria.ModLoader;
using Terraria.ModLoader.IO;
using DeathBag.Common.NPCs;
using DB = DeathBag.DeathBag;

namespace DeathBag.Common.Items;

/// <summary>
/// Portable bag item — created when a non-owner picks up someone's bag NPC.
/// When dropped on the ground, converts back to a bag NPC.
/// </summary>
public sealed class DeathBagItem : ModItem
{
    /// <summary>Whether this is a death bag or loadout bag.</summary>
    public BagKind Kind = BagKind.Death;

    /// <summary>Player name of the bag's owner.</summary>
    public string OwnerName = "";

    /// <summary>Which loadout was active when the owner died.</summary>
    public int DeathLoadoutIndex;

    /// <summary>Name of the player carrying this bag item.</summary>
    public string CarrierName = "";

    /// <summary>Saved inventory snapshot carried by this bag.</summary>
    public List<(int SlotIndex, Item Item)> SavedInventory = new();

    public override void SetDefaults()
    {
        Item.width = 24;
        Item.height = 24;
        Item.maxStack = 1;
        Item.rare = ItemRarityID.Orange;
        Item.value = 0;
        // Not usable, not placeable, not consumable
        Item.useStyle = ItemUseStyleID.None;
        Item.noUseGraphic = true;
    }

    public override void ModifyTooltips(List<TooltipLine> tooltips)
    {
        string owner = string.IsNullOrEmpty(OwnerName) ? "Unknown Player" : OwnerName;
        string kindLabel = Kind == BagKind.Loadout ? "Loadout" : "Death Bag";
        tooltips.Add(new TooltipLine(Mod, "BagKind", kindLabel));
        tooltips.Add(new TooltipLine(Mod, "BagOwner", $"Contains {owner}'s items ({SavedInventory.Count} items)"));
        tooltips.Add(new TooltipLine(Mod, "BagHint", "Drop near the owner to deliver")
        {
            OverrideColor = Color.Gray,
        });
    }

    /// <summary>
    /// Called every tick while this item is on the ground as a world entity.
    /// Converts back to a bag NPC immediately.
    /// Server-authoritative: only runs the conversion on server/singleplayer.
    /// </summary>
    public override void Update(ref float gravity, ref float maxFallSpeed)
    {
        // Items on the ground are server-authoritative — server runs Update and syncs
        if (Main.netMode == NetmodeID.MultiplayerClient)
            return;

        if (SavedInventory.Count == 0)
            return;

        SpawnBagNPCFromItem();

        // Remove the world item entity — active = false is how vanilla removes ground items.
        // TurnToAir() only zeroes type/stack (designed for inventory slots, not world entities).
        Item.active = false;
        if (Main.netMode == NetmodeID.Server)
            NetMessage.SendData(MessageID.SyncItem, -1, -1, null, Item.whoAmI);
    }

    private void SpawnBagNPCFromItem()
    {
        int npcIndex = NPC.NewNPC(
            Terraria.Entity.GetSource_NaturalSpawn(),
            (int)Item.position.X + Item.width / 2,
            (int)Item.position.Y + Item.height / 2,
            ModContent.NPCType<DeathBagNPC>());

        if (npcIndex < 0 || npcIndex >= Main.maxNPCs)
        {
            Mod.Logger.Error("[DeathBag] Failed to spawn bag NPC from item");
            return;
        }

        NPC npc = Main.npc[npcIndex];
        if (npc.ModNPC is DeathBagNPC bagNPC)
        {
            bagNPC.Kind = Kind;
            bagNPC.OwnerName = OwnerName;
            bagNPC.DeathLoadoutIndex = DeathLoadoutIndex;
            bagNPC.SavedInventory = SavedInventory;
            bagNPC.DeliveredBy = CarrierName;
            bagNPC.ResolveOwnerIndex();
            npc.netUpdate = true;
            Mod.Logger.Info($"[DeathBag] Converted bag item back to NPC for {OwnerName} with {SavedInventory.Count} items, kind={Kind} (delivered by {CarrierName})");

            // Broadcast delivery message to all players
            string deliveryKind = Kind == BagKind.Loadout ? "loadout" : "bag";
            if (!string.IsNullOrEmpty(CarrierName) && Main.netMode == NetmodeID.Server)
            {
                ChatHelper.BroadcastChatMessage(
                    NetworkText.FromLiteral($"{CarrierName} dropped {OwnerName}'s {deliveryKind} nearby."),
                    Color.LightGreen);
            }
            else if (!string.IsNullOrEmpty(CarrierName) && Main.netMode == NetmodeID.SinglePlayer)
            {
                Main.NewText($"{CarrierName} dropped {OwnerName}'s {deliveryKind} nearby.", Color.LightGreen);
            }
        }
    }

    public override void SaveData(TagCompound tag)
    {
        tag["kind"] = (byte)Kind;
        tag["ownerName"] = OwnerName;
        tag["deathLoadout"] = DeathLoadoutIndex;
        tag["carrierName"] = CarrierName;

        var itemList = new List<TagCompound>();
        foreach (var (slotIndex, item) in SavedInventory)
        {
            itemList.Add(new TagCompound
            {
                ["slot"] = slotIndex,
                ["item"] = ItemIO.Save(item),
            });
        }
        tag["items"] = itemList;
    }

    public override void LoadData(TagCompound tag)
    {
        Kind = tag.ContainsKey("kind") ? (BagKind)tag.GetByte("kind") : BagKind.Death;
        OwnerName = tag.GetString("ownerName");
        DeathLoadoutIndex = tag.ContainsKey("deathLoadout") ? tag.GetInt("deathLoadout") : 0;
        CarrierName = tag.ContainsKey("carrierName") ? tag.GetString("carrierName") : "";

        SavedInventory = new List<(int, Item)>();
        if (tag.ContainsKey("items"))
        {
            var itemList = tag.GetList<TagCompound>("items");
            foreach (TagCompound itemTag in itemList)
            {
                int slot = itemTag.GetInt("slot");
                Item item = ItemIO.Load(itemTag.GetCompound("item"));
                SavedInventory.Add((slot, item));
            }
        }
    }

    public override void NetSend(BinaryWriter writer)
    {
        writer.Write((byte)Kind);
        writer.Write(OwnerName ?? "");
        writer.Write(DeathLoadoutIndex);
        writer.Write(CarrierName ?? "");
        DB.WriteInventory(writer, SavedInventory);
    }

    public override void NetReceive(BinaryReader reader)
    {
        Kind = (BagKind)reader.ReadByte();
        OwnerName = reader.ReadString();
        DeathLoadoutIndex = reader.ReadInt32();
        CarrierName = reader.ReadString();
        SavedInventory = DB.ReadInventory(reader);
    }
}
