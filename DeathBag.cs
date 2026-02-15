using System.Collections.Generic;
using System.IO;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;
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
            default:
                Logger.Warn($"[DeathBag] Unknown packet type: {msgType}");
                break;
        }
    }

    private void HandleBagCreated(BinaryReader reader, int whoAmI)
    {
        float x = reader.ReadSingle();
        float y = reader.ReadSingle();
        string ownerName = reader.ReadString();
        int ownerIndex = reader.ReadInt32();
        int deathLoadoutIndex = reader.ReadInt32();
        var inventory = ReadInventory(reader);

        if (Main.netMode != NetmodeID.Server)
            return;

        Logger.Info($"[DeathBag] Server: BagCreated from {ownerName} with {inventory.Count} items at ({x:F0}, {y:F0}), loadout {deathLoadoutIndex}");

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
            bagNPC.OwnerName = ownerName;
            bagNPC.OwnerPlayerIndex = ownerIndex;
            bagNPC.DeathLoadoutIndex = deathLoadoutIndex;
            bagNPC.SavedInventory = inventory;
            npc.GivenName = $"{ownerName}'s Death Bag";

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
            && Main.npc[npcIndex].ModNPC is DeathBagNPC)
        {
            Logger.Info($"[DeathBag] Server: removing bag NPC (index {npcIndex})");
            Main.npc[npcIndex].active = false;
            Main.npc[npcIndex].netUpdate = true;
            // Sync the NPC removal to all clients
            NetMessage.SendData(MessageID.SyncNPC, -1, -1, null, npcIndex);
        }
    }

    /// <summary>
    /// Sends BagCreated packet from client to server.
    /// </summary>
    internal static void SendBagCreated(Mod mod, float x, float y, string ownerName, int ownerIndex,
        int deathLoadoutIndex, List<(int SlotIndex, Item Item)> inventory)
    {
        var packet = mod.GetPacket();
        packet.Write((byte)MessageType.BagCreated);
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

    // === Shared inventory serialization (used by packets and SendExtraAI) ===

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
