using System.Collections.Generic;
using System.IO;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;
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
            default:
                Logger.Warn($"[DeathBag] Unknown packet type: {msgType}");
                break;
        }
    }

    private void HandleBagCreated(BinaryReader reader, int whoAmI)
    {
        var kind = (BagKind)reader.ReadByte();
        float x = reader.ReadSingle();
        float y = reader.ReadSingle();
        string ownerName = reader.ReadString();
        int ownerIndex = reader.ReadInt32();
        int deathLoadoutIndex = reader.ReadInt32();
        var inventory = ReadInventory(reader);

        if (Main.netMode != NetmodeID.Server)
            return;

        string kindName = kind == BagKind.Loadout ? "Loadout" : "Death Bag";
        Logger.Info($"[DeathBag] Server: BagCreated ({kindName}) from {ownerName} with {inventory.Count} items at ({x:F0}, {y:F0}), loadout {deathLoadoutIndex}");

        int npcIndex = NPC.NewNPC(
            Terraria.Entity.GetSource_NaturalSpawn(),
            (int)x, (int)y,
            ModContent.NPCType<DeathBagNPC>());

        if (npcIndex < 0 || npcIndex >= Main.maxNPCs)
        {
            Logger.Error($"[DeathBag] Server: Failed to spawn bag NPC, NewNPC returned {npcIndex}");
            return;
        }

        NPC npc = Main.npc[npcIndex];
        if (npc.ModNPC is DeathBagNPC bagNPC)
        {
            bagNPC.Kind = kind;
            bagNPC.OwnerName = ownerName;
            bagNPC.OwnerPlayerIndex = ownerIndex;
            bagNPC.DeathLoadoutIndex = deathLoadoutIndex;
            bagNPC.SavedInventory = inventory;

            // netAlways + netUpdate ensures vanilla syncs this NPC (with SendExtraAI data) to all clients
            npc.netUpdate = true;
        }
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

        // Send bag data to the requesting client so it can place the item itself
        // (inventory is client-authoritative — server can't modify it)
        // NOTE: Do NOT remove the NPC here! The client will send BagRemoved
        // after it confirms the item was placed successfully. This prevents
        // data loss if the client has no inventory room.
        var response = GetPacket();
        response.Write((byte)MessageType.BagToItemResponse);
        response.Write(npcIndex); // client needs this to send BagRemoved on success
        response.Write((byte)bagNPC.Kind);
        response.Write(bagNPC.OwnerName);
        response.Write(bagNPC.DeathLoadoutIndex);
        response.Write(carrierName);
        WriteInventory(response, bagNPC.SavedInventory);
        response.Send(whoAmI); // to requesting client only

        string kindName = bagNPC.Kind == BagKind.Loadout ? "Loadout" : "Death Bag";
        Logger.Info($"[DeathBag] Server: sent BagToItemResponse for {bagNPC.OwnerName}'s {kindName} to {carrierName} (player {whoAmI}), NPC kept alive pending confirmation");
    }

    private void HandleBagToItemResponse(BinaryReader reader)
    {
        int npcIndex = reader.ReadInt32();
        var kind = (BagKind)reader.ReadByte();
        string ownerName = reader.ReadString();
        int deathLoadoutIndex = reader.ReadInt32();
        string carrierName = reader.ReadString();
        var inventory = ReadInventory(reader);

        if (Main.netMode != NetmodeID.MultiplayerClient)
            return;

        Player localPlayer = Main.LocalPlayer;

        // Find empty inventory slot
        int emptySlot = -1;
        for (int i = 0; i < 50; i++)
        {
            if (localPlayer.inventory[i] == null || localPlayer.inventory[i].IsAir)
            {
                emptySlot = i;
                break;
            }
        }

        if (emptySlot < 0)
        {
            Logger.Warn($"[DeathBag] Client: no room for {ownerName}'s bag item — bag NPC preserved on server");
            Main.NewText("No room in your inventory!", Microsoft.Xna.Framework.Color.Yellow);
            // Do NOT send BagRemoved — the NPC stays alive on the server
            return;
        }

        string kindName = kind == BagKind.Loadout ? "Loadout" : "Death Bag";

        // Create the bag item in our own inventory (client-authoritative)
        var item = new Item();
        item.SetDefaults(ModContent.ItemType<DeathBagItem>());
        if (item.ModItem is DeathBagItem bagItem)
        {
            bagItem.Kind = kind;
            bagItem.OwnerName = ownerName;
            bagItem.DeathLoadoutIndex = deathLoadoutIndex;
            bagItem.SavedInventory = inventory;
            bagItem.CarrierName = carrierName;
        }
        item.SetNameOverride($"{ownerName}'s {kindName}");
        localPlayer.inventory[emptySlot] = item;

        // Item placed successfully — NOW tell the server to remove the bag NPC
        SendBagRemoved(this, npcIndex);

        // Sync the new item slot to server
        NetMessage.SendData(MessageID.SyncEquipment, -1, -1, null, localPlayer.whoAmI, emptySlot);

        Logger.Info($"[DeathBag] Client: placed {ownerName}'s {kindName} in inventory slot {emptySlot}, sent BagRemoved for NPC {npcIndex}");
        Main.NewText($"Picked up {ownerName}'s {kindName.ToLower()}.", Microsoft.Xna.Framework.Color.Green);
    }

    /// <summary>
    /// Sends BagCreated packet from client to server.
    /// </summary>
    internal static void SendBagCreated(Mod mod, BagKind kind, float x, float y, string ownerName, int ownerIndex,
        int deathLoadoutIndex, List<(int SlotIndex, Item Item)> inventory)
    {
        var packet = mod.GetPacket();
        packet.Write((byte)MessageType.BagCreated);
        packet.Write((byte)kind);
        packet.Write(x);
        packet.Write(y);
        packet.Write(ownerName);
        packet.Write(ownerIndex);
        packet.Write(deathLoadoutIndex);
        WriteInventory(packet, inventory);
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

    // === Shared inventory serialization (used by packets and SendExtraAI) ===

    /// <summary>
    /// Logs bag contents before destruction for disaster recovery.
    /// Compact format: one summary line + up to 20 items. If more, shows count of omitted.
    /// </summary>
    internal static void LogBagContents(Mod mod, string reason, string ownerName, BagKind kind,
        List<(int SlotIndex, Item Item)> inventory)
    {
        string kindName = kind == BagKind.Loadout ? "Loadout" : "Death Bag";
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
}
