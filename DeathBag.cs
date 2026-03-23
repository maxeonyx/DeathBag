using System.Collections.Generic;
using System.IO;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;
using DeathBag.Common;
using DeathBag.Common.Items;
using DeathBag.Common.NPCs;

namespace DeathBag;

public sealed class DeathBag : Mod
{
    internal enum MessageType : byte
    {
        /// <summary>Client -> server: player died, create bag NPC with inventory.</summary>
        BagCreated,
        /// <summary>Client -> server: player restored their bag, remove it.</summary>
        BagRemoved,
        /// <summary>Client -> server: non-owner wants to pick up bag NPC.</summary>
        BagToItem,
        /// <summary>Server -> client: bag NPC removed, place this DeathBagItem in your inventory.</summary>
        BagToItemResponse,
        /// <summary>Client -> server: place a bag item into the world as a bag NPC.</summary>
        PlaceBagItemRequest,
        /// <summary>Server -> client: result of bag item placement request.</summary>
        PlaceBagItemResponse,
    }

    public override void HandlePacket(BinaryReader reader, int whoAmI)
    {
        var msgType = (MessageType)reader.ReadByte();

        switch (msgType)
        {
            case MessageType.BagCreated:
                HandleBagCreated(reader, whoAmI);
                break;
            case MessageType.BagRemoved:
                HandleBagRemoved(reader, whoAmI);
                break;
            case MessageType.BagToItem:
                HandleBagToItem(reader, whoAmI);
                break;
            case MessageType.BagToItemResponse:
                HandleBagToItemResponse(reader);
                break;
            case MessageType.PlaceBagItemRequest:
                HandlePlaceBagItemRequest(reader, whoAmI);
                break;
            case MessageType.PlaceBagItemResponse:
                HandlePlaceBagItemResponse(reader);
                break;
            default:
                Logger.Warn($"[DeathBag] Unknown packet type: {msgType}");
                break;
        }
    }

    internal static string GetBagKindName(BagKind kind)
    {
        return kind switch
        {
            BagKind.Loadout => "Loadout",
            BagKind.Overflow => "Overflow",
            _ => "Death Bag",
        };
    }

    internal static string GetBagKindPromptLabel(BagKind kind)
    {
        return kind switch
        {
            BagKind.Loadout => "loadout",
            BagKind.Overflow => "overflow",
            _ => "bag",
        };
    }

    internal static List<(int SlotIndex, Item Item)> CloneInventory(List<(int SlotIndex, Item Item)> inventory)
    {
        var clone = new List<(int SlotIndex, Item Item)>(inventory.Count);
        foreach (var (slotIndex, item) in inventory)
            clone.Add((slotIndex, item.Clone()));
        return clone;
    }

    internal static int FindMatchingBagItemSlot(Player player, string ownerName, BagKind kind, int deathLoadoutIndex,
        string carrierName, List<(int SlotIndex, Item Item)> inventory)
    {
        return FindMatchingBagItemSlot(player, ownerName, kind, deathLoadoutIndex, carrierName, inventory, excludedSlot: -1);
    }

    internal static int FindMatchingBagItemSlot(Player player, BagPayload payload, int excludedSlot = -1)
    {
        return FindMatchingBagItemSlot(player, payload.OwnerName, payload.Kind, payload.DeathLoadoutIndex,
            payload.CarrierName, payload.SavedInventory, excludedSlot);
    }

    internal static int FindNewMatchingBagItemSlot(Player player, string ownerName, BagKind kind, int deathLoadoutIndex,
        string carrierName, List<(int SlotIndex, Item Item)> inventory, HashSet<int> previouslyMatchingSlots)
    {
        for (int i = 0; i < 50; i++)
        {
            if (previouslyMatchingSlots.Contains(i))
                continue;
            if (!IsMatchingBagItem(player.inventory[i], ownerName, kind, deathLoadoutIndex, carrierName, inventory))
                continue;

            return i;
        }

        return -1;
    }

    internal static HashSet<int> FindMatchingBagItemSlots(Player player, string ownerName, BagKind kind, int deathLoadoutIndex,
        string carrierName, List<(int SlotIndex, Item Item)> inventory)
    {
        var slots = new HashSet<int>();
        for (int i = 0; i < 50; i++)
        {
            if (IsMatchingBagItem(player.inventory[i], ownerName, kind, deathLoadoutIndex, carrierName, inventory))
                slots.Add(i);
        }

        return slots;
    }

    internal static bool IsMatchingBagItem(Item item, BagPayload payload)
    {
        return IsMatchingBagItem(item, payload.OwnerName, payload.Kind, payload.DeathLoadoutIndex,
            payload.CarrierName, payload.SavedInventory);
    }

    private static int FindMatchingBagItemSlot(Player player, string ownerName, BagKind kind, int deathLoadoutIndex,
        string carrierName, List<(int SlotIndex, Item Item)> inventory, int excludedSlot)
    {
        for (int i = 0; i < 50; i++)
        {
            if (i == excludedSlot)
                continue;
            if (!IsMatchingBagItem(player.inventory[i], ownerName, kind, deathLoadoutIndex, carrierName, inventory))
                continue;

            return i;
        }

        return -1;
    }

    private static bool IsMatchingBagItem(Item item, string ownerName, BagKind kind, int deathLoadoutIndex,
        string carrierName, List<(int SlotIndex, Item Item)> inventory)
    {
        if (item?.ModItem is not BagItemBase bagItem)
            return false;

        if (bagItem.OwnerName != ownerName || bagItem.Kind != kind || bagItem.DeathLoadoutIndex != deathLoadoutIndex)
            return false;
        if (bagItem.CarrierName != carrierName)
            return false;
        if (!InventoryContentsMatch(bagItem.SavedInventory, inventory))
            return false;

        return true;
    }

    private static bool InventoryContentsMatch(List<(int SlotIndex, Item Item)> left, List<(int SlotIndex, Item Item)> right)
    {
        if (left.Count != right.Count)
            return false;

        for (int i = 0; i < left.Count; i++)
        {
            var (leftSlotIndex, leftItem) = left[i];
            var (rightSlotIndex, rightItem) = right[i];
            if (leftSlotIndex != rightSlotIndex)
                return false;
            if (leftItem.type != rightItem.type || leftItem.stack != rightItem.stack)
                return false;
            if (leftItem.prefix != rightItem.prefix || leftItem.favorited != rightItem.favorited)
                return false;
        }

        return true;
    }

    private void HandleBagCreated(BinaryReader reader, int whoAmI)
    {
        float x = reader.ReadSingle();
        float y = reader.ReadSingle();
        int ownerIndex = reader.ReadInt32();
        var payload = ReadBagPayload(reader);

        if (Main.netMode != NetmodeID.Server)
            return;

        string kindName = GetBagKindName(payload.Kind);
        Logger.Info($"[DeathBag] Server: BagCreated ({kindName}) from {payload.OwnerName} with {payload.SavedInventory.Count} items at ({x:F0}, {y:F0}), loadout {payload.DeathLoadoutIndex}");

        if (!TrySpawnBagNPC(x, y, payload, ownerIndex, Terraria.Entity.GetSource_NaturalSpawn(), out _))
            return;

        LogBagContents(this, "BagCreated (server)", payload.OwnerName, payload.Kind, payload.SavedInventory);
    }

    private void HandleBagRemoved(BinaryReader reader, int whoAmI)
    {
        int npcIndex = reader.ReadInt32();

        if (Main.netMode != NetmodeID.Server)
            return;

        if (npcIndex >= 0 && npcIndex < Main.maxNPCs && Main.npc[npcIndex].active
            && Main.npc[npcIndex].ModNPC is DeathBagNPC bagNPC)
        {
            LogBagContents(this, "BagRemoved", bagNPC.OwnerName, bagNPC.Kind, bagNPC.SavedInventory);
            Logger.Info($"[DeathBag] Server: removing bag NPC (index {npcIndex})");
            Main.npc[npcIndex].active = false;
            Main.npc[npcIndex].netUpdate = true;
            // Sync the NPC removal to all clients
            NetMessage.SendData(MessageID.SyncNPC, -1, -1, null, npcIndex);
        }
    }

    private void HandleBagToItem(BinaryReader reader, int whoAmI)
    {
        int npcIndex = reader.ReadInt32();
        string carrierName = reader.ReadString();

        if (Main.netMode != NetmodeID.Server)
            return;

        if (npcIndex < 0 || npcIndex >= Main.maxNPCs || !Main.npc[npcIndex].active
            || Main.npc[npcIndex].ModNPC is not DeathBagNPC bagNPC)
        {
            Logger.Warn($"[DeathBag] Server: BagToItem for invalid NPC index {npcIndex}");
            return;
        }

        Player requester = whoAmI >= 0 && whoAmI < Main.maxPlayers ? Main.player[whoAmI] : null;
        if (requester?.active == true && !string.IsNullOrEmpty(bagNPC.OwnerName) && requester.name == bagNPC.OwnerName)
        {
            Logger.Warn($"[DeathBag] Server: rejected BagToItem from owner {requester.name} for NPC {npcIndex}");
            return;
        }

        // Send bag data to the requesting client so it can place the item itself
        // (inventory is client-authoritative — server can't modify it)
        // NOTE: Do NOT remove the NPC here! The client will send BagRemoved
        // after it confirms the item was placed successfully. This prevents
        // data loss if the client has no inventory room.
        var response = GetPacket();
        response.Write((byte)MessageType.BagToItemResponse);
        response.Write(npcIndex); // client needs this to send BagRemoved on success
        var payload = BagPayloadHelper.FromNPC(bagNPC);
        payload.CarrierName = carrierName;
        WriteBagPayload(response, payload);
        response.Send(whoAmI); // to requesting client only

        string kindName = GetBagKindName(bagNPC.Kind);
        Logger.Info($"[DeathBag] Server: sent BagToItemResponse for {bagNPC.OwnerName}'s {kindName} to {carrierName} (player {whoAmI}), NPC kept alive pending confirmation");
    }

    private void HandleBagToItemResponse(BinaryReader reader)
    {
        int npcIndex = reader.ReadInt32();
        var payload = ReadBagPayload(reader);

        if (Main.netMode != NetmodeID.MultiplayerClient)
            return;

        Player localPlayer = Main.LocalPlayer;
        string kindName = GetBagKindName(payload.Kind);
        HashSet<int> previouslyMatchingSlots = FindMatchingBagItemSlots(localPlayer, payload.OwnerName, payload.Kind, payload.DeathLoadoutIndex, payload.CarrierName, payload.SavedInventory);

        // Create the bag item in our own inventory (client-authoritative)
        Item item = BagPayloadHelper.CreateBagItem(payload);

        Item remainder = localPlayer.GetItem(localPlayer.whoAmI, item, GetItemSettings.NPCEntityToPlayerInventorySettings);
        if (remainder is not null && !remainder.IsAir)
        {
            Logger.Warn($"[DeathBag] Client: no room for {payload.OwnerName}'s bag item — bag NPC preserved on server");
            Main.NewText("No room in your inventory!", Microsoft.Xna.Framework.Color.Yellow);
            // Do NOT send BagRemoved — the NPC stays alive on the server
            return;
        }

        // Item placed successfully — NOW tell the server to remove the bag NPC
        SendBagRemoved(this, npcIndex);

        LogBagContents(this, "non-owner pickup (MP client)", payload.OwnerName, payload.Kind, payload.SavedInventory);

        // Item placed successfully — sync the slot to server. Find which slot GetItem placed it in.
        int placedSlot = FindNewMatchingBagItemSlot(localPlayer, payload.OwnerName, payload.Kind, payload.DeathLoadoutIndex, payload.CarrierName, payload.SavedInventory, previouslyMatchingSlots);
        if (placedSlot >= 0)
            NetMessage.SendData(MessageID.SyncEquipment, -1, -1, null, localPlayer.whoAmI, placedSlot);

        Logger.Info($"[DeathBag] Client: placed {payload.OwnerName}'s {kindName} in inventory slot {placedSlot}, sent BagRemoved for NPC {npcIndex}");
        Main.NewText($"Picked up {payload.OwnerName}'s {kindName.ToLower()}.", Microsoft.Xna.Framework.Color.Green);
    }

    private void HandlePlaceBagItemRequest(BinaryReader reader, int whoAmI)
    {
        int requestId = reader.ReadInt32();
        int sourceSlot = reader.ReadInt32();
        float x = reader.ReadSingle();
        float y = reader.ReadSingle();
        var payload = ReadBagPayload(reader);

        if (Main.netMode != NetmodeID.Server)
            return;

        bool success = TrySpawnBagNPC(x, y, payload, ownerPlayerIndex: -1, Terraria.Entity.GetSource_NaturalSpawn(), out int npcIndex);

        var response = GetPacket();
        response.Write((byte)MessageType.PlaceBagItemResponse);
        response.Write(requestId);
        response.Write(sourceSlot);
        response.Write(success);
        response.Send(whoAmI);

        if (!success)
        {
            Logger.Warn($"[DeathBag] Server: bag item placement request {requestId} failed for {payload.OwnerName}");
            return;
        }

        LogBagContents(this, "bag item placed into world (server)", payload.OwnerName, payload.Kind, payload.SavedInventory);
        Logger.Info($"[DeathBag] Server: bag item placement request {requestId} succeeded for {payload.OwnerName} from slot {sourceSlot} to NPC {npcIndex}");
    }

    private void HandlePlaceBagItemResponse(BinaryReader reader)
    {
        int requestId = reader.ReadInt32();
        int sourceSlot = reader.ReadInt32();
        bool success = reader.ReadBoolean();

        if (Main.netMode != NetmodeID.MultiplayerClient)
            return;

        Player localPlayer = Main.LocalPlayer;
        var modPlayer = localPlayer.GetModPlayer<Common.Players.DeathBagPlayer>();
        if (!modPlayer.TryGetPendingBagPlacement(requestId, out var pendingPlacement))
        {
            Logger.Error($"[DeathBag] Client: placement response {requestId} arrived but no pending bag item was found");
            Main.NewText("Bag placement finished, but the source item could not be found safely.", Microsoft.Xna.Framework.Color.Yellow);
            return;
        }

        if (!success)
        {
            modPlayer.ClearPendingBagPlacement();
            Main.NewText("Could not place bag right now.", Microsoft.Xna.Framework.Color.Yellow);
            return;
        }

        int currentSlot = pendingPlacement.SourceKind == Common.Players.DeathBagPlayer.PendingPlacementSourceKind.Cursor
            ? -1
            : pendingPlacement.SourceSlot;
        if (currentSlot != sourceSlot)
            Logger.Warn($"[DeathBag] Client: pending bag placement request {requestId} moved from slot {sourceSlot} to {currentSlot} before confirmation");

        if (!modPlayer.TryConsumePendingBagPlacement(pendingPlacement))
        {
            Logger.Error($"[DeathBag] Client: failed to consume pending bag item for request {requestId} after confirmed placement");
            modPlayer.ClearPendingBagPlacement();
            Main.NewText("Bag was placed, but the source item could not be consumed safely.", Microsoft.Xna.Framework.Color.Yellow);
            return;
        }

        if (currentSlot >= 0)
            NetMessage.SendData(MessageID.SyncEquipment, -1, -1, null, localPlayer.whoAmI, currentSlot);
    }

    /// <summary>
    /// Sends BagCreated packet from client to server.
    /// </summary>
    internal static void SendBagCreated(Mod mod, BagKind kind, float x, float y, string ownerName, int ownerIndex,
        int deathLoadoutIndex, List<(int SlotIndex, Item Item)> inventory)
    {
        var packet = mod.GetPacket();
        packet.Write((byte)MessageType.BagCreated);
        packet.Write(x);
        packet.Write(y);
        packet.Write(ownerIndex);
        WriteBagPayload(packet, new BagPayload
        {
            Kind = kind,
            OwnerName = ownerName,
            DeathLoadoutIndex = deathLoadoutIndex,
            SavedInventory = inventory,
        });
        packet.Send(); // to server
    }

    /// <summary>
    /// Sends BagRemoved packet from client to server.
    /// </summary>
    internal static void SendBagRemoved(Mod mod, int npcIndex)
    {
        var packet = mod.GetPacket();
        packet.Write((byte)MessageType.BagRemoved);
        packet.Write(npcIndex);
        packet.Send(); // to server
    }

    /// <summary>
    /// Sends BagToItem packet from client to server.
    /// Server will remove the bag NPC and spawn a DeathBagItem world entity.
    /// </summary>
    internal static void SendBagToItem(Mod mod, int npcIndex, string carrierName)
    {
        var packet = mod.GetPacket();
        packet.Write((byte)MessageType.BagToItem);
        packet.Write(npcIndex);
        packet.Write(carrierName);
        packet.Send(); // to server
    }

    internal static void SendPlaceBagItemRequest(Mod mod, int requestId, int sourceSlot, float x, float y, BagPayload payload)
    {
        var packet = mod.GetPacket();
        packet.Write((byte)MessageType.PlaceBagItemRequest);
        packet.Write(requestId);
        packet.Write(sourceSlot);
        packet.Write(x);
        packet.Write(y);
        WriteBagPayload(packet, payload);
        packet.Send();
    }

    // === Shared inventory serialization (used by packets and SendExtraAI) ===

    /// <summary>
    /// Logs bag contents before destruction for disaster recovery.
    /// Compact format: one summary line + up to 20 items. If more, shows count of omitted.
    /// </summary>
    internal static void LogBagContents(Mod mod, string reason, string ownerName, BagKind kind,
        List<(int SlotIndex, Item Item)> inventory)
    {
        string kindName = GetBagKindName(kind);
        mod.Logger.Info($"[DeathBag] BAG CONSUMED ({reason}): {ownerName}'s {kindName}, {inventory.Count} items:");

        int logged = 0;
        const int maxLog = 20;
        foreach (var (slot, item) in inventory)
        {
            if (logged >= maxLog)
            {
                mod.Logger.Info($"[DeathBag]   ... and {inventory.Count - maxLog} more items");
                break;
            }
            mod.Logger.Info($"[DeathBag]   slot {slot}: {item.Name} x{item.stack} (id={item.type}, prefix={item.prefix})");
            logged++;
        }
    }

    internal static void WriteInventory(BinaryWriter writer, List<(int SlotIndex, Item Item)> inventory)
    {
        writer.Write(inventory.Count);
        foreach (var (slotIndex, item) in inventory)
        {
            writer.Write(slotIndex);
            writer.Write(item.type);
            writer.Write(item.stack);
            writer.Write((byte)item.prefix);
            writer.Write(item.favorited);
        }
    }

    internal static void WriteBagPayload(BinaryWriter writer, BagPayload payload)
    {
        writer.Write((byte)payload.Kind);
        writer.Write(payload.OwnerName ?? "");
        writer.Write(payload.DeathLoadoutIndex);
        writer.Write(payload.CarrierName ?? "");
        WriteInventory(writer, payload.SavedInventory);
    }

    internal static BagPayload ReadBagPayload(BinaryReader reader)
    {
        return new BagPayload
        {
            Kind = (BagKind)reader.ReadByte(),
            OwnerName = reader.ReadString(),
            DeathLoadoutIndex = reader.ReadInt32(),
            CarrierName = reader.ReadString(),
            SavedInventory = ReadInventory(reader),
        };
    }

    internal static List<(int SlotIndex, Item Item)> ReadInventory(BinaryReader reader)
    {
        int count = reader.ReadInt32();
        var inventory = new List<(int SlotIndex, Item Item)>(count);
        for (int i = 0; i < count; i++)
        {
            int slotIndex = reader.ReadInt32();
            int type = reader.ReadInt32();
            int stack = reader.ReadInt32();
            byte prefix = reader.ReadByte();
            bool favorited = reader.ReadBoolean();

            var item = new Item();
            item.SetDefaults(type);
            item.stack = stack;
            item.Prefix(prefix);
            item.favorited = favorited;
            inventory.Add((slotIndex, item));
        }
        return inventory;
    }

    private bool TrySpawnBagNPC(float x, float y, BagPayload payload, int ownerPlayerIndex, Terraria.DataStructures.IEntitySource source, out int npcIndex)
    {
        npcIndex = NPC.NewNPC(source, (int)x, (int)y, ModContent.NPCType<DeathBagNPC>());

        if (npcIndex < 0 || npcIndex >= Main.maxNPCs)
        {
            Logger.Error($"[DeathBag] CRITICAL: failed to spawn bag NPC (NewNPC={npcIndex}) for {payload.OwnerName} with {payload.SavedInventory.Count} items");
            LogBagContents(this, "FAILED bag NPC spawn", payload.OwnerName, payload.Kind, payload.SavedInventory);
            return false;
        }

        NPC npc = Main.npc[npcIndex];
        if (npc.ModNPC is not DeathBagNPC bagNPC)
        {
            Logger.Error($"[DeathBag] CRITICAL: spawned NPC at index {npcIndex} is not DeathBagNPC");
            LogBagContents(this, "FAILED bag NPC spawn (wrong NPC type)", payload.OwnerName, payload.Kind, payload.SavedInventory);
            return false;
        }

        BagPayloadHelper.ApplyToNPC(bagNPC, payload, ownerPlayerIndex);
        bagNPC.ResolveOwnerIndex();
        npc.netUpdate = true;
        return true;
    }
}
