using System.Collections.Generic;
using Terraria;
using Terraria.ModLoader;
using Terraria.ModLoader.IO;
using DeathBag.Common.NPCs;

namespace DeathBag.Common.Systems;

/// <summary>
/// Manages bag persistence via world save/load.
/// Source of truth for bag data — NPC entities are the live representation.
/// </summary>
public sealed class DeathBagState : ModSystem
{
    public override void SaveWorldData(TagCompound tag)
    {
        var bagList = new List<TagCompound>();

        for (int i = 0; i < Main.maxNPCs; i++)
        {
            NPC npc = Main.npc[i];
            if (!npc.active || npc.ModNPC is not DeathBagNPC bag)
                continue;

            var bagTag = new TagCompound
            {
                ["x"] = npc.position.X,
                ["y"] = npc.position.Y,
                ["ownerIndex"] = bag.OwnerPlayerIndex,
                ["ownerName"] = bag.OwnerName,
            };

            // Serialize inventory
            var itemList = new List<TagCompound>();
            foreach (var (slotIndex, item) in bag.SavedInventory)
            {
                itemList.Add(new TagCompound
                {
                    ["slot"] = slotIndex,
                    ["item"] = ItemIO.Save(item),
                });
            }
            bagTag["items"] = itemList;

            bagList.Add(bagTag);
        }

        tag["deathBags"] = bagList;
    }

    /// <summary>
    /// Loaded bag data, held until players join so we can resolve owner names to indices.
    /// NPC spawning is deferred to PostWorldGen or when players are available.
    /// </summary>
    private static readonly List<SavedBagData> _pendingBags = new();

    internal class SavedBagData
    {
        public float X, Y;
        public string OwnerName = "";
        public List<(int SlotIndex, Item Item)> Inventory = new();
    }

    public override void LoadWorldData(TagCompound tag)
    {
        _pendingBags.Clear();

        if (!tag.ContainsKey("deathBags"))
            return;

        var bagList = tag.GetList<TagCompound>("deathBags");

        foreach (TagCompound bagTag in bagList)
        {
            var data = new SavedBagData
            {
                X = bagTag.GetFloat("x"),
                Y = bagTag.GetFloat("y"),
                OwnerName = bagTag.GetString("ownerName"),
            };

            if (bagTag.ContainsKey("items"))
            {
                var itemList = bagTag.GetList<TagCompound>("items");
                foreach (TagCompound itemTag in itemList)
                {
                    int slot = itemTag.GetInt("slot");
                    Item item = ItemIO.Load(itemTag.GetCompound("item"));
                    data.Inventory.Add((slot, item));
                }
            }

            _pendingBags.Add(data);
        }
    }

    /// <summary>
    /// Spawns bag NPCs from loaded data. Runs each tick until all bags are spawned.
    /// Deferred because LoadWorldData runs before players are connected.
    /// </summary>
    public override void PostUpdateWorld()
    {
        if (_pendingBags.Count == 0)
            return;

        // Only spawn on server/singleplayer
        if (Main.netMode == NetmodeID.MultiplayerClient)
            return;

        foreach (var data in _pendingBags)
        {
            int npcIndex = NPC.NewNPC(
                Terraria.Entity.GetSource_NaturalSpawn(),
                (int)data.X, (int)data.Y,
                ModContent.NPCType<DeathBagNPC>());

            if (npcIndex < 0 || npcIndex >= Main.maxNPCs)
                continue;

            NPC npc = Main.npc[npcIndex];
            if (npc.ModNPC is not DeathBagNPC bagNPC)
                continue;

            bagNPC.OwnerName = data.OwnerName;
            bagNPC.OwnerPlayerIndex = ResolvePlayerIndex(data.OwnerName);
            bagNPC.SavedInventory = data.Inventory;
            npc.GivenName = $"{data.OwnerName}'s Death Bag";
            npc.netUpdate = true;
        }

        _pendingBags.Clear();
    }

    /// <summary>
    /// Find the current player index for a player name. Returns -1 if not found (offline).
    /// </summary>
    private static int ResolvePlayerIndex(string playerName)
    {
        for (int i = 0; i < Main.maxPlayers; i++)
        {
            var p = Main.player[i];
            if (p?.active == true && p.name == playerName)
                return i;
        }
        return -1;
    }

    public override void OnWorldLoad()
    {
        // Clean state on world load (LoadWorldData re-populates)
    }
}
