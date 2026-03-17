using System.Collections.Generic;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;
using Terraria.ModLoader.IO;
using Terraria.UI;
using DeathBag.Common.Items;
using DeathBag.Common.NPCs;
using DB = DeathBag.DeathBag;

namespace DeathBag.Common.Systems;

/// <summary>
/// Manages bag persistence via world save/load.
/// Source of truth for bag data — NPC entities are the live representation.
/// </summary>
public sealed class DeathBagState : ModSystem
{
    private static bool _pendingItemMigrationSweep;

    public override void Load()
    {
        On_ItemSlot.SellOrTrash += OnSellOrTrash;
    }

    public override void OnModUnload()
    {
        On_ItemSlot.SellOrTrash -= OnSellOrTrash;
    }

    public override void OnWorldLoad()
    {
        _pendingItemMigrationSweep = true;
    }

    /// <summary>
    /// Intercepts Ctrl+click sell/trash: drops the bag item on the ground instead.
    /// </summary>
    private static void OnSellOrTrash(On_ItemSlot.orig_SellOrTrash orig, Item[] inv, int context, int slot)
    {
        if (inv[slot]?.ModItem is BagItemBase)
        {
            DropBagItem(inv[slot]);
            inv[slot].TurnToAir();
            return;
        }

        orig(inv, context, slot);
    }

    /// <summary>
    /// Drops a bag item on the ground at the local player's position.
    /// The item's Update() will convert it to a bag NPC immediately.
    /// </summary>
    private static void DropBagItem(Item item)
    {
        Player player = Main.LocalPlayer;
        player.QuickSpawnItem(player.GetSource_Misc("DeathBagDrop"), item, item.stack);
    }

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
        _pendingItemMigrationSweep = true;

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
        if (_pendingItemMigrationSweep)
        {
            RunLegacyItemMigrationSweep();
            _pendingItemMigrationSweep = false;
        }

        if (_pendingBags.Count == 0)
            return;

        // Only spawn on server/singleplayer
        if (Main.netMode == NetmodeID.MultiplayerClient)
            return;

        // Spawn bags, keeping failed ones for retry next tick
        for (int i = _pendingBags.Count - 1; i >= 0; i--)
        {
            var data = _pendingBags[i];
            // NewNPC takes bottom-center coordinates, but we save top-left (npc.position).
            // Convert: bottom-center X = position.X + width/2, Y = position.Y + height.
            // Our bags are always 48x48 (SetDefaults).
            int spawnX = (int)data.X + 24;
            int spawnY = (int)data.Y + 48;
            int npcIndex = NPC.NewNPC(
                Terraria.Entity.GetSource_NaturalSpawn(),
                spawnX, spawnY,
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

            var payload = new BagPayload
            {
                Kind = data.Kind,
                OwnerName = data.OwnerName,
                DeathLoadoutIndex = data.DeathLoadoutIndex,
                SavedInventory = data.Inventory,
            };
            BagPayloadHelper.ApplyToNPC(bagNPC, payload, ResolvePlayerIndex(data.OwnerName));
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

    private static void RunLegacyItemMigrationSweep()
    {
        int migrated = 0;

        foreach (Player player in Main.player)
        {
            if (player is null || !player.active)
                continue;

            migrated += MigrateItemArray(player.inventory);
            migrated += MigrateItemArray(player.armor);
            migrated += MigrateItemArray(player.dye);
            migrated += MigrateItemArray(player.miscEquips);
            migrated += MigrateItemArray(player.miscDyes);

            foreach (var loadout in player.Loadouts)
            {
                migrated += MigrateItemArray(loadout.Armor);
                migrated += MigrateItemArray(loadout.Dye);
            }
        }

        foreach (Chest chest in Main.chest)
        {
            if (chest?.item is null)
                continue;
            migrated += MigrateItemArray(chest.item);
        }

        foreach (Item item in Main.item)
        {
            if (item is null || !item.active)
                continue;
            if (BagPayloadHelper.TryMigrateLegacyItemInPlace(item))
                migrated++;
        }

        if (migrated > 0)
            ModContent.GetInstance<DeathBag>().Logger.Info($"[DeathBag] Migrated {migrated} legacy bag item(s) to split item types");
    }

    private static int MigrateItemArray(Item[] items)
    {
        int migrated = 0;
        if (items is null)
            return migrated;

        for (int i = 0; i < items.Length; i++)
        {
            if (BagPayloadHelper.TryMigrateLegacyItemInPlace(items[i]))
                migrated++;
        }

        return migrated;
    }

}
