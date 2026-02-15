using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;

namespace DeathBag.Common.NPCs;

/// <summary>
/// The bag entity that appears at the player's death location.
/// A friendly, stationary, immortal ModNPC with right-click interaction.
/// </summary>
public sealed class DeathBagNPC : ModNPC
{
    /// <summary>Player index of the bag's owner.</summary>
    public int OwnerPlayerIndex = -1;

    /// <summary>Player name stored at creation time (for display when owner is offline).</summary>
    public string OwnerName = "";

    /// <summary>
    /// Saved inventory snapshot. Each entry is (slotIndex, item).
    /// Populated on death, consumed on restore.
    /// </summary>
    public List<(int SlotIndex, Item Item)> SavedInventory = new();

    public override void SetStaticDefaults()
    {
        Main.npcFrameCount[Type] = 1;

        // Prevent this NPC from appearing in the bestiary
        NPCID.Sets.NPCBestiaryDrawModifiers drawModifiers = new()
        {
            Hide = true,
        };
        NPCID.Sets.NPCBestiaryDrawOffset.Add(Type, drawModifiers);
    }

    public override void SetDefaults()
    {
        NPC.friendly = true;
        NPC.townNPC = false;
        NPC.width = 24;
        NPC.height = 24;
        NPC.aiStyle = -1;
        NPC.damage = 0;
        NPC.defense = 0;
        NPC.lifeMax = 1;
        NPC.HitSound = null;
        NPC.DeathSound = null;
        NPC.knockBackResist = 0f;
        NPC.noGravity = false;
        NPC.noTileCollide = false;
        NPC.dontTakeDamage = true;
        NPC.immortal = true;
        NPC.netAlways = true;
    }

    public override void AI()
    {
        // Stationary: kill all horizontal velocity, let gravity work
        NPC.velocity.X = 0f;

        // Push apart from other bag NPCs so they're individually clickable
        PushApartFromOtherBags();
    }

    public override bool CanChat() => true;

    public override string GetChat()
    {
        Player player = Main.LocalPlayer;
        if (player.whoAmI != OwnerPlayerIndex)
            return $"This is {OwnerName}'s bag.";

        return "Your death bag. Click to restore your inventory.";
    }

    public override void SetChatButtons(ref string button1, ref string button2)
    {
        Player player = Main.LocalPlayer;
        if (player.whoAmI == OwnerPlayerIndex)
            button1 = "Restore";
    }

    public override void OnChatButtonClicked(bool firstButton, ref string shopName)
    {
        if (!firstButton)
            return;

        Player player = Main.LocalPlayer;
        if (player.whoAmI != OwnerPlayerIndex)
            return;

        // Close the chat UI
        Main.npcChatText = "";
        Main.LocalPlayer.SetTalkNPC(-1);

        // Perform the restore
        player.GetModPlayer<Players.DeathBagPlayer>().RestoreFromBag(this);
    }

    public override bool CheckActive()
    {
        // Never despawn naturally
        return false;
    }

    private void PushApartFromOtherBags()
    {
        const float pushRadius = 32f;
        const float pushStrength = 0.5f;

        for (int i = 0; i < Main.maxNPCs; i++)
        {
            NPC other = Main.npc[i];
            if (!other.active || other.whoAmI == NPC.whoAmI || other.type != NPC.type)
                continue;

            Vector2 diff = NPC.Center - other.Center;
            float dist = diff.Length();

            if (dist < pushRadius && dist > 0.01f)
            {
                Vector2 push = Vector2.Normalize(diff) * pushStrength * (1f - dist / pushRadius);
                NPC.position += push;
            }
        }
    }
}
