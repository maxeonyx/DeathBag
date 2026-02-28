using System.Collections.Generic;
using Terraria;
using Terraria.ID;
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
                ["kind"] = (byte)bag.Kind,
                ["x"] = npc.position.X,
                ["y"] = npc.position.Y,
                ["ownerIndex"] = bag.OwnerPlayerIndex,
                ["ownerName"] = bag.OwnerName,
                ["deathLoadout"] = bag.DeathLoadoutIndex,
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
        public BagKind Kind = BagKind.Death;
        public float X, Y;
        public string OwnerName = "";
        public int DeathLoadoutIndex;
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
                Kind = bagTag.ContainsKey("kind") ? (BagKind)bagTag.GetByte("kind") : BagKind.Death,
                X = bagTag.GetFloat("x"),
                Y = bagTag.GetFloat("y"),
                OwnerName = bagTag.GetString("ownerName"),
                DeathLoadoutIndex = bagTag.ContainsKey("deathLoadout") ? bagTag.GetInt("deathLoadout") : 0,
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

        // Spawn bags, keeping failed ones for retry next tick
        for (int i = _pendingBags.Count - 1; i >= 0; i--)
        {
            var data = _pendingBags[i];
            int npcIndex = NPC.NewNPC(
                Terraria.Entity.GetSource_NaturalSpawn(),
                (int)data.X, (int)data.Y,
                ModContent.NPCType<DeathBagNPC>());

            if (npcIndex < 0 || npcIndex >= Main.maxNPCs)
            {
                Mod.Logger.Warn($"[DeathBag] PostUpdateWorld: failed to spawn bag for {data.OwnerName} (NewNPC={npcIndex}), will retry next tick");
                continue; // Keep in pending list — retry next tick
            }

            NPC npc = Main.npc[npcIndex];
            if (npc.ModNPC is not DeathBagNPC bagNPC)
            {
                Mod.Logger.Error($"[DeathBag] PostUpdateWorld: spawned NPC is not DeathBagNPC, will retry next tick");
                continue;
            }

            bagNPC.Kind = data.Kind;
            bagNPC.OwnerName = data.OwnerName;
            bagNPC.OwnerPlayerIndex = ResolvePlayerIndex(data.OwnerName);
            bagNPC.SavedInventory = data.Inventory;
            bagNPC.DeathLoadoutIndex = data.DeathLoadoutIndex;
            npc.netUpdate = true;

            _pendingBags.RemoveAt(i); // Successfully spawned — remove from pending
        }
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
