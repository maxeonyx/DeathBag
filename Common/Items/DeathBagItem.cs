using System.Collections.Generic;
using System.IO;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using ReLogic.Content;
using Terraria;
using Terraria.Chat;
using Terraria.ID;
using Terraria.Localization;
using Terraria.ModLoader;
using Terraria.ModLoader.IO;
using DeathBag.Common;
using DeathBag.Common.NPCs;
using DeathBag.Common.Players;
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

    private static int _nextPlacementRequestId = 1;

    private bool _consumeAfterImmediatePlacement = true;
    private int _pendingPlacementRequestId;

    /// <summary>Separate texture for loadout bag items.</summary>
    private static Asset<Texture2D> _loadoutTexture;

    public override void SetStaticDefaults()
    {
        _loadoutTexture = ModContent.Request<Texture2D>("DeathBag/Common/Items/LoadoutBagItem", AssetRequestMode.ImmediateLoad);
    }

    public override void SetDefaults()
    {
        Item.width = 48;
        Item.height = 48;
        Item.maxStack = 1;
        Item.rare = ItemRarityID.Orange;
        Item.value = 0;
        Item.useStyle = ItemUseStyleID.Swing;
        Item.useTime = 15;
        Item.useAnimation = 15;
        Item.consumable = true;
        Item.noUseGraphic = true;
    }

    public override bool PreDrawInInventory(SpriteBatch spriteBatch, Vector2 position, Rectangle frame,
        Color drawColor, Color itemColor, Vector2 origin, float scale)
    {
        if (Kind != BagKind.Loadout || _loadoutTexture?.Value == null)
            return true; // use default texture

        spriteBatch.Draw(_loadoutTexture.Value, position, frame, drawColor, 0f, origin, scale, SpriteEffects.None, 0f);
        return false;
    }

    public override bool PreDrawInWorld(SpriteBatch spriteBatch, Color lightColor, Color alphaColor,
        ref float rotation, ref float scale, int whoAmI)
    {
        if (Kind != BagKind.Loadout || _loadoutTexture?.Value == null)
            return true;

        var texture = _loadoutTexture.Value;
        Vector2 drawPos = Item.position - Main.screenPosition + new Vector2(Item.width / 2f, Item.height / 2f);
        var drawOrigin = new Vector2(texture.Width / 2f, texture.Height / 2f);
        spriteBatch.Draw(texture, drawPos, null, lightColor, rotation, drawOrigin, scale, SpriteEffects.None, 0f);
        return false;
    }

    public override void ModifyTooltips(List<TooltipLine> tooltips)
    {
        tooltips.Add(new TooltipLine(Mod, "BagContents", $"{SavedInventory.Count} items"));

        bool isOwner = Main.LocalPlayer.name == OwnerName;
        if (isOwner)
        {
            if (Kind == BagKind.Loadout)
            {
                tooltips.Add(new TooltipLine(Mod, "BagPlace", "Place to use as a loadout")
                {
                    OverrideColor = Color.Gray,
                });
            }

            tooltips.Add(new TooltipLine(Mod, "BagEmpty", "Right-click to empty")
            {
                OverrideColor = Color.Gray,
            });
        }
        else
        {
            string name = string.IsNullOrEmpty(OwnerName) ? "the owner" : OwnerName;
            tooltips.Add(new TooltipLine(Mod, "BagReturn", $"Drop to return to {name}")
            {
                OverrideColor = Color.Gray,
            });
        }
    }

    public override bool CanUseItem(Player player)
    {
        // Only usable if it has contents and cursor is within tile placement range
        if (SavedInventory.Count == 0)
            return false;
        if (_pendingPlacementRequestId != 0)
            return false;

        Vector2 mouseWorld = Main.MouseWorld;
        float dist = Vector2.Distance(player.Center, mouseWorld);
        float maxRange = player.tileRangeX * 16f;
        return dist <= maxRange;
    }

    public override bool? UseItem(Player player)
    {
        Vector2 mouseWorld = Main.MouseWorld;

        // NewNPC treats coordinates as bottom-center, so offset upward
        // by half the bag height (48/2 = 24) so the bag's center lands at the click.
        Vector2 spawnPos = new(mouseWorld.X, mouseWorld.Y + 24f);

        if (Main.netMode == NetmodeID.MultiplayerClient)
        {
            int sourceSlot = FindCurrentInventorySlot(player);
            if (sourceSlot < 0)
            {
                Mod.Logger.Error("[DeathBag] Refused multiplayer bag placement because the source inventory slot could not be identified");
                Main.NewText("Could not safely place that bag right now.", Color.Yellow);
                return false;
            }

            _pendingPlacementRequestId = _nextPlacementRequestId++;
            _consumeAfterImmediatePlacement = false;
            DB.SendPlaceBagItemRequest(Mod, _pendingPlacementRequestId, sourceSlot, spawnPos.X, spawnPos.Y, BagPayloadHelper.FromItem(this));
            return true;
        }

        // Spawn the bag NPC at cursor position
        var modPlayer = player.GetModPlayer<Players.DeathBagPlayer>();
        modPlayer.SpawnBagNPC(spawnPos, SavedInventory, DeathLoadoutIndex, Kind, OwnerName);

        DB.LogBagContents(Mod, "placed bag via left-click", OwnerName, Kind, SavedInventory);
        SavedInventory = new();
        _consumeAfterImmediatePlacement = true;

        return true; // consumed
    }

    public override bool ConsumeItem(Player player)
    {
        return _consumeAfterImmediatePlacement;
    }

    internal void CancelPendingPlacement()
    {
        _pendingPlacementRequestId = 0;
        _consumeAfterImmediatePlacement = true;
    }

    internal bool TryConsumePendingPlacement(Player player, int slot)
    {
        if (_pendingPlacementRequestId == 0)
            return false;
        if (slot < 0 || slot >= SlotHelper.MainInventorySlotCount)
            return false;
        if (!ReferenceEquals(player.inventory[slot]?.ModItem, this))
            return false;

        DB.LogBagContents(Mod, "placed bag via left-click", OwnerName, Kind, SavedInventory);
        player.inventory[slot].TurnToAir();
        CancelPendingPlacement();
        return true;
    }

    internal static bool TryResolvePendingPlacement(Player player, int requestId, out DeathBagItem bagItem, out int slot)
    {
        for (int i = 0; i < SlotHelper.MainInventorySlotCount; i++)
        {
            if (player.inventory[i]?.ModItem is not DeathBagItem candidate)
                continue;
            if (candidate._pendingPlacementRequestId != requestId)
                continue;

            bagItem = candidate;
            slot = i;
            return true;
        }

        bagItem = null;
        slot = -1;
        return false;
    }

    private int FindCurrentInventorySlot(Player player)
    {
        for (int i = 0; i < SlotHelper.MainInventorySlotCount; i++)
        {
            if (ReferenceEquals(player.inventory[i]?.ModItem, this))
                return i;
        }

        return -1;
    }

    public override bool CanRightClick()
    {
        // Owner can right-click to dump contents into inventory
        return Main.LocalPlayer.name == OwnerName && SavedInventory.Count > 0;
    }

    public override void RightClick(Player player)
    {
        // Dump bag contents into inventory using GetItem for proper stacking/ammo/coin handling.
        // Any remainder that doesn't fit is dropped on the ground.
        DB.LogBagContents(Mod, "owner opened bag item", OwnerName, Kind, SavedInventory);

        // Prefer restoring to original slots when they are free, but never overwrite existing items.
        // Any items that can't go into their original slot fall back to GetItem placement.
        var modPlayer = player.GetModPlayer<DeathBagPlayer>();
        var leftovers = new List<Item>();
        foreach (var (slotIndex, savedItem) in SavedInventory)
        {
            Item toRestore = savedItem.Clone();
            if (slotIndex >= 0 && modPlayer.TryPlaceInSlotIfEmpty(slotIndex, toRestore))
                continue;
            leftovers.Add(toRestore);
        }

        foreach (Item toDump in leftovers)
        {
            Item remainder = player.GetItem(player.whoAmI, toDump, GetItemSettings.NPCEntityToPlayerInventorySettings);
            if (remainder is not null && !remainder.IsAir)
                player.QuickSpawnItem(player.GetSource_OpenItem(Item.type), remainder, remainder.stack);
        }

        // Sync slot state to server (armor/accessories/loadouts aren't covered by vanilla item pickup sync).
        if (Main.netMode == NetmodeID.MultiplayerClient)
        {
            int maxSlot = SlotHelper.GetMaxSyncEquipmentSlot(player);
            for (int slot = 0; slot < maxSlot; slot++)
                NetMessage.SendData(MessageID.SyncEquipment, -1, -1, null, player.whoAmI, slot);
        }

        SavedInventory.Clear();
        // Item is consumed by vanilla's right-click handling (stack decremented)
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

        if (!SpawnBagNPCFromItem())
            return; // NPC spawn failed — keep item alive to preserve inventory data

        // Log before removing item entity (data is now in the NPC)
        DB.LogBagContents(Mod, "item->NPC conversion", OwnerName, Kind, SavedInventory);

        // Remove the world item entity — active = false is how vanilla removes ground items.
        // TurnToAir() only zeroes type/stack (designed for inventory slots, not world entities).
        Item.active = false;
        if (Main.netMode == NetmodeID.Server)
            NetMessage.SendData(MessageID.SyncItem, -1, -1, null, Item.whoAmI);
    }

    /// <summary>
    /// Converts this bag item into a bag NPC at the item's position.
    /// Returns true if NPC was spawned successfully, false if it failed (NPC slots full).
    /// On failure, the caller should NOT destroy the item — data would be lost.
    /// </summary>
    private bool SpawnBagNPCFromItem()
    {
        int npcIndex = NPC.NewNPC(
            Terraria.Entity.GetSource_NaturalSpawn(),
            (int)Item.position.X + Item.width / 2,
            (int)Item.position.Y + Item.height / 2,
            ModContent.NPCType<DeathBagNPC>());

        if (npcIndex < 0 || npcIndex >= Main.maxNPCs)
        {
            Mod.Logger.Error($"[DeathBag] CRITICAL: Failed to spawn bag NPC from item (NewNPC={npcIndex}), keeping item alive to preserve data");
            return false;
        }

        NPC npc = Main.npc[npcIndex];
        if (npc.ModNPC is DeathBagNPC bagNPC)
        {
            BagPayloadHelper.ApplyToNPC(bagNPC, BagPayloadHelper.FromItem(this), ownerPlayerIndex: -1);
            bagNPC.ResolveOwnerIndex();
            npc.netUpdate = true;
            Mod.Logger.Info($"[DeathBag] Converted bag item back to NPC for {OwnerName} with {SavedInventory.Count} items, kind={Kind} (delivered by {CarrierName})");

            // Broadcast delivery message to all players
            string deliveryKind = DB.GetBagKindPromptLabel(Kind);
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

            return true;
        }

        Mod.Logger.Error($"[DeathBag] CRITICAL: Spawned NPC is not DeathBagNPC, keeping item alive to preserve data");
        return false;
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
        CancelPendingPlacement();
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

        // Restore display name (SetNameOverride doesn't persist through save/load)
        string kindName = DB.GetBagKindName(Kind);
        if (!string.IsNullOrEmpty(OwnerName))
            Item.SetNameOverride($"{OwnerName}'s {kindName}");
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
        CancelPendingPlacement();

        // Restore display name after net sync
        string kindName = DB.GetBagKindName(Kind);
        if (!string.IsNullOrEmpty(OwnerName))
            Item.SetNameOverride($"{OwnerName}'s {kindName}");
    }
}
