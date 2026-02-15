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

    public override void LoadWorldData(TagCompound tag)
    {
        if (!tag.ContainsKey("deathBags"))
            return;

        var bagList = tag.GetList<TagCompound>("deathBags");

        foreach (TagCompound bagTag in bagList)
        {
            float x = bagTag.GetFloat("x");
            float y = bagTag.GetFloat("y");
            int ownerIndex = bagTag.GetInt("ownerIndex");
            string ownerName = bagTag.GetString("ownerName");

            int npcIndex = NPC.NewNPC(
                Terraria.Entity.GetSource_NaturalSpawn(),
                (int)x, (int)y,
                ModContent.NPCType<DeathBagNPC>());

            if (npcIndex < 0 || npcIndex >= Main.maxNPCs)
                continue;

            NPC npc = Main.npc[npcIndex];
            if (npc.ModNPC is not DeathBagNPC bagNPC)
                continue;

            bagNPC.OwnerPlayerIndex = ownerIndex;
            bagNPC.OwnerName = ownerName;

            // Deserialize inventory
            if (bagTag.ContainsKey("items"))
            {
                var itemList = bagTag.GetList<TagCompound>("items");
                foreach (TagCompound itemTag in itemList)
                {
                    int slot = itemTag.GetInt("slot");
                    Item item = ItemIO.Load(itemTag.GetCompound("item"));
                    bagNPC.SavedInventory.Add((slot, item));
                }
            }
        }
    }

    public override void OnWorldLoad()
    {
        // Clean state on world load (LoadWorldData re-populates)
    }
}
