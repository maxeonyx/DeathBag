using System.IO;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;

namespace DeathBag;

public sealed class DeathBag : Mod
{
    internal enum MessageType : byte
    {
        // TODO: Define packet types during implementation
        // Likely candidates:
        // - BagCreated (client -> server -> all clients): a player died, bag spawned
        // - BagRestored (client -> server -> all clients): a player picked up their bag
        // - SyncBags (server -> client): full bag state on player join
    }

    public override void HandlePacket(BinaryReader reader, int whoAmI)
    {
        var msgType = (MessageType)reader.ReadByte();

        switch (msgType)
        {
            // TODO: Implement packet handling
        }
    }
}
