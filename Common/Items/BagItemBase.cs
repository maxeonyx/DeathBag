using System.Collections.Generic;
using System.IO;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;
using Terraria.ModLoader.IO;
using DeathBag.Common;
using DeathBag.Common.NPCs;
using DeathBag.Common.Players;
using DB = DeathBag.DeathBag;

namespace DeathBag.Common.Items;

public abstract class BagItemBase : ModItem
{
    internal const int CursorSlotSentinel = -1;

    public string OwnerName = "";
    public int DeathLoadoutIndex;
    public string CarrierName = "";
    public List<(int SlotIndex, Item Item)> SavedInventory = new();

    private static int _nextPlacementRequestId = 1;

    private bool _consumeAfterImmediatePlacement = true;

    public abstract BagKind Kind { get; }

    protected virtual bool CanPlaceByUse => false;

    protected virtual bool ConvertsToNPCWhenDropped => false;

    public override void SetDefaults()
    {
        Item.width = 48;
        Item.height = 48;
        Item.maxStack = 1;
        Item.rare = ItemRarityID.Orange;
        Item.value = 0;
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

    public override bool CanRightClick()
    {
        return !Main.LocalPlayer.GetModPlayer<DeathBagPlayer>().HasPendingBagPlacement && Main.LocalPlayer.name == OwnerName && SavedInventory.Count > 0;
    }

    public override void RightClick(Player player)
    {
        DB.LogBagContents(Mod, "owner opened bag item", OwnerName, Kind, SavedInventory);

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

        if (Main.netMode == NetmodeID.MultiplayerClient)
        {
            int maxSlot = SlotHelper.GetMaxSyncEquipmentSlot(player);
            for (int slot = 0; slot < maxSlot; slot++)
                NetMessage.SendData(MessageID.SyncEquipment, -1, -1, null, player.whoAmI, slot);
        }

        SavedInventory.Clear();
    }

    public override bool ConsumeItem(Player player)
    {
        bool shouldConsume = SavedInventory.Count == 0 || _consumeAfterImmediatePlacement;
        Item mouseItem = Main.mouseItem;
        Item slot58Item = player.inventory.Length > 58 ? player.inventory[58] : null;
        bool sameAsMouse = ReferenceEquals(Item, mouseItem);
        Mod.Logger.Info($"[DeathBag] ConsumeItem: netMode={Main.netMode}, itemHash={Item.GetHashCode()}, modHash={GetHashCode()}, selectedItem={player.selectedItem}, mouseType={mouseItem?.type ?? -1}, mouseAir={mouseItem?.IsAir != false}, mouseHash={mouseItem?.GetHashCode() ?? 0}, slot58Type={slot58Item?.type ?? -1}, slot58Air={slot58Item?.IsAir != false}, slot58Hash={slot58Item?.GetHashCode() ?? 0}, sameAsMouse={sameAsMouse}, shouldConsume={shouldConsume}, reasonSavedInventoryEmpty={SavedInventory.Count == 0}, reasonConsumeAfterImmediatePlacement={_consumeAfterImmediatePlacement}, pending={player.GetModPlayer<DeathBagPlayer>().DescribePendingBagPlacement()}");
        return shouldConsume;
    }

    public override bool CanUseItem(Player player)
    {
        bool canUse = CanPlaceByUse && CanPlaceBag(player);
        Mod.Logger.Info($"[DeathBag] CanUseItem: canUse={canUse}, canPlaceByUse={CanPlaceByUse}, itemHash={Item.GetHashCode()}, modHash={GetHashCode()}, pending={player.GetModPlayer<DeathBagPlayer>().DescribePendingBagPlacement()}, mouseItemHash={Main.mouseItem?.GetHashCode() ?? 0}, mouseModHash={Main.mouseItem?.ModItem?.GetHashCode() ?? 0}");
        return canUse;
    }

    public override bool? UseItem(Player player)
    {
        if (!CanPlaceByUse)
            return base.UseItem(player);

        Mod.Logger.Info($"[DeathBag] UseItem: netMode={Main.netMode}, itemHash={Item.GetHashCode()}, modHash={GetHashCode()}, mouseItemHash={Main.mouseItem?.GetHashCode() ?? 0}, mouseModHash={Main.mouseItem?.ModItem?.GetHashCode() ?? 0}, pendingBefore={player.GetModPlayer<DeathBagPlayer>().DescribePendingBagPlacement()}");

        return PlaceBag(player);
    }

    public override bool CanPickup(Player player)
    {
        return base.CanPickup(player);
    }

    public override void Update(ref float gravity, ref float maxFallSpeed)
    {
        if (!ConvertsToNPCWhenDropped || Main.netMode == NetmodeID.MultiplayerClient || SavedInventory.Count == 0)
            return;

        if (!SpawnBagNPCFromItem())
            return;

        DB.LogBagContents(Mod, "item->NPC conversion", OwnerName, Kind, SavedInventory);
        Item.active = false;
        if (Main.netMode == NetmodeID.Server)
            NetMessage.SendData(MessageID.SyncItem, -1, -1, null, Item.whoAmI);
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
        BagPayloadHelper.ApplyToItem(this, BagPayloadHelper.ReadPayload(tag));
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
        BagPayloadHelper.ApplyToItem(this, new BagPayload
        {
            Kind = (BagKind)reader.ReadByte(),
            OwnerName = reader.ReadString(),
            DeathLoadoutIndex = reader.ReadInt32(),
            CarrierName = reader.ReadString(),
            SavedInventory = DB.ReadInventory(reader),
        });
    }

    protected bool CanPlaceBag(Player player)
    {
        var modPlayer = player.GetModPlayer<DeathBagPlayer>();
        if (!CanPlaceByUse || SavedInventory.Count == 0 || modPlayer.HasPendingBagPlacement)
        {
            Mod.Logger.Info($"[DeathBag] CanPlaceBag: blocked netMode={Main.netMode}, canPlaceByUse={CanPlaceByUse}, savedInventoryCount={SavedInventory.Count}, hasPending={modPlayer.HasPendingBagPlacement}, pending={modPlayer.DescribePendingBagPlacement()}, itemHash={Item.GetHashCode()}, modHash={GetHashCode()}");
            return false;
        }

        Vector2 mouseWorld = Main.MouseWorld;
        float dist = Vector2.Distance(player.Center, mouseWorld);
        float maxRange = Player.tileRangeX * 16f;
        bool inRange = dist <= maxRange;
        Mod.Logger.Info($"[DeathBag] CanPlaceBag: allowedCheck netMode={Main.netMode}, inRange={inRange}, dist={dist:F1}, maxRange={maxRange:F1}, itemHash={Item.GetHashCode()}, modHash={GetHashCode()}, pending={modPlayer.DescribePendingBagPlacement()}");
        return inRange;
    }

    protected bool? PlaceBag(Player player)
    {
        Vector2 mouseWorld = Main.MouseWorld;
        Vector2 spawnPos = new(mouseWorld.X, mouseWorld.Y + 24f);
        Mod.Logger.Info($"[DeathBag] PlaceBag: start netMode={Main.netMode}, spawnPos=({spawnPos.X:F1},{spawnPos.Y:F1}), itemHash={Item.GetHashCode()}, modHash={GetHashCode()}, mouseItemHash={Main.mouseItem?.GetHashCode() ?? 0}, mouseModHash={Main.mouseItem?.ModItem?.GetHashCode() ?? 0}, pendingBefore={player.GetModPlayer<DeathBagPlayer>().DescribePendingBagPlacement()}");

        if (Main.netMode == NetmodeID.MultiplayerClient)
        {
            int sourceSlot = FindCurrentInventorySlot(player);
            Mod.Logger.Info($"[DeathBag] PlaceBag: multiplayer sourceSlot={sourceSlot}, sourceKind={(sourceSlot == CursorSlotSentinel ? "Cursor" : "Inventory")}, itemHash={Item.GetHashCode()}, modHash={GetHashCode()}");
            if (sourceSlot < CursorSlotSentinel)
            {
                Mod.Logger.Error("[DeathBag] Refused multiplayer bag placement because the source inventory slot could not be identified");
                Main.NewText("Could not safely place that bag right now.", Color.Yellow);
                return false;
            }
            int requestId = _nextPlacementRequestId++;
            BagPayload payload = BagPayloadHelper.FromItem(this);
            Mod.Logger.Info($"[DeathBag] PlaceBag: creating pending placement requestId={requestId}, sourceSlot={sourceSlot}, sourceKind={(sourceSlot == CursorSlotSentinel ? "Cursor" : "Inventory")}, payloadOwner={payload.OwnerName}, payloadKind={payload.Kind}, payloadItems={payload.SavedInventory.Count}, itemHash={Item.GetHashCode()}, modHash={GetHashCode()}");
            player.GetModPlayer<DeathBagPlayer>().BeginPendingBagPlacement(requestId, sourceSlot, payload);
            _consumeAfterImmediatePlacement = false;
            DB.SendPlaceBagItemRequest(Mod, requestId, sourceSlot, spawnPos.X, spawnPos.Y, payload);
            return true;
        }

        var modPlayer = player.GetModPlayer<DeathBagPlayer>();
        modPlayer.SpawnBagNPC(spawnPos, SavedInventory, DeathLoadoutIndex, Kind, OwnerName);

        DB.LogBagContents(Mod, "placed bag via left-click", OwnerName, Kind, SavedInventory);
        SavedInventory = new();
        _consumeAfterImmediatePlacement = true;

        return true;
    }

    protected int FindCurrentInventorySlot(Player player)
    {
        BagPayload payload = BagPayloadHelper.FromItem(this);
        Mod.Logger.Info($"[DeathBag] FindCurrentInventorySlot: start itemHash={Item.GetHashCode()}, modHash={GetHashCode()}, selectedItem={player.selectedItem}, mouseItemHash={Main.mouseItem?.GetHashCode() ?? 0}, mouseModHash={Main.mouseItem?.ModItem?.GetHashCode() ?? 0}");

        if (DB.IsMatchingBagItem(Main.mouseItem, payload))
        {
            Mod.Logger.Info($"[DeathBag] FindCurrentInventorySlot: matched cursor itemHash={Main.mouseItem?.GetHashCode() ?? 0}, mouseModHash={Main.mouseItem?.ModItem?.GetHashCode() ?? 0}");
            return CursorSlotSentinel;
        }

        int matchedSelectedSlot = FindMatchingInventorySlot(player, player.selectedItem, payload);
        if (matchedSelectedSlot >= 0)
        {
            Mod.Logger.Info($"[DeathBag] FindCurrentInventorySlot: matched selected slot {matchedSelectedSlot}, itemHash={player.inventory[matchedSelectedSlot]?.GetHashCode() ?? 0}, modHash={player.inventory[matchedSelectedSlot]?.ModItem?.GetHashCode() ?? 0}");
            return matchedSelectedSlot;
        }

        int matchedSlot = DB.FindMatchingBagItemSlot(player, payload);
        Mod.Logger.Info($"[DeathBag] FindCurrentInventorySlot: fallback matched slot {matchedSlot}");
        return matchedSlot;
    }

    private static int FindMatchingInventorySlot(Player player, int slot, BagPayload payload)
    {
        if (slot < 0 || slot >= SlotHelper.MainInventorySlotCount)
            return -1;

        return DB.IsMatchingBagItem(player.inventory[slot], payload) ? slot : -1;
    }

    private bool SpawnBagNPCFromItem()
    {
        int npcIndex = NPC.NewNPC(
            Terraria.Entity.GetSource_NaturalSpawn(),
            (int)Item.position.X + Item.width / 2,
            (int)Item.position.Y + Item.height / 2,
            ModContent.NPCType<DeathBagNPC>());

        if (npcIndex < 0 || npcIndex >= Main.maxNPCs)
        {
            Mod.Logger.Error("[DeathBag] CRITICAL: Failed to spawn bag NPC from item (keeping item alive to preserve data)");
            return false;
        }

        NPC npc = Main.npc[npcIndex];
        if (npc.ModNPC is not DeathBagNPC bagNPC)
        {
            Mod.Logger.Error("[DeathBag] CRITICAL: Spawned NPC is not DeathBagNPC, keeping item alive to preserve data");
            return false;
        }

        BagPayloadHelper.ApplyToNPC(bagNPC, BagPayloadHelper.FromItem(this), ownerPlayerIndex: -1);
        bagNPC.ResolveOwnerIndex();
        npc.netUpdate = true;
        Mod.Logger.Info($"[DeathBag] Converted bag item back to NPC for {OwnerName} with {SavedInventory.Count} items, kind={Kind} (delivered by {CarrierName})");

        string deliveryKind = DB.GetBagKindPromptLabel(Kind);
        if (!string.IsNullOrEmpty(CarrierName) && Main.netMode == NetmodeID.Server)
        {
            Terraria.Chat.ChatHelper.BroadcastChatMessage(
                Terraria.Localization.NetworkText.FromLiteral($"{CarrierName} dropped {OwnerName}'s {deliveryKind} nearby."),
                Color.LightGreen);
        }
        else if (!string.IsNullOrEmpty(CarrierName) && Main.netMode == NetmodeID.SinglePlayer)
        {
            Main.NewText($"{CarrierName} dropped {OwnerName}'s {deliveryKind} nearby.", Color.LightGreen);
        }

        return true;
    }
}
