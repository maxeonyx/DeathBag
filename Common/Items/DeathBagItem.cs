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
/// Portable death bag item — created when a non-owner picks up someone's bag NPC.
/// When dropped on the ground, converts back to a bag NPC so the owner can auto-pickup.
/// </summary>
public sealed class DeathBagItem : ModItem
{
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

        SpawnBagNPCFromItem();
        Item.TurnToAir();
    }

    private void SpawnBagNPCFromItem()
    {
        int npcIndex = NPC.NewNPC(
            Entity.GetSource_NaturalSpawn(),
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
            bagNPC.OwnerName = OwnerName;
            bagNPC.DeathLoadoutIndex = DeathLoadoutIndex;
            bagNPC.SavedInventory = SavedInventory;
            bagNPC.DeliveredBy = CarrierName;
            bagNPC.ResolveOwnerIndex();
            npc.GivenName = $"{OwnerName}'s Death Bag";
            npc.netUpdate = true;
            Mod.Logger.Info($"[DeathBag] Converted bag item back to NPC for {OwnerName} with {SavedInventory.Count} items (delivered by {CarrierName})");

            // Broadcast delivery message to all players
            if (!string.IsNullOrEmpty(CarrierName) && Main.netMode == NetmodeID.Server)
            {
                ChatHelper.BroadcastChatMessage(
                    NetworkText.FromLiteral($"{CarrierName} dropped {OwnerName}'s bag nearby."),
                    Color.LightGreen);
            }
            else if (!string.IsNullOrEmpty(CarrierName) && Main.netMode == NetmodeID.SinglePlayer)
            {
                Main.NewText($"{CarrierName} dropped {OwnerName}'s bag nearby.", Color.LightGreen);
            }
        }
    }

    public override void SaveData(TagCompound tag)
    {
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
        writer.Write(OwnerName ?? "");
        writer.Write(DeathLoadoutIndex);
        writer.Write(CarrierName ?? "");
        DB.WriteInventory(writer, SavedInventory);
    }

    public override void NetReceive(BinaryReader reader)
    {
        OwnerName = reader.ReadString();
        DeathLoadoutIndex = reader.ReadInt32();
        CarrierName = reader.ReadString();
        SavedInventory = DB.ReadInventory(reader);
    }
}
