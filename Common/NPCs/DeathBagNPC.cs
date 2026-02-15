using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;
using Terraria.UI.Chat;

namespace DeathBag.Common.NPCs;

/// <summary>
/// The bag entity that appears at the player's death location.
/// A friendly, stationary, immortal ModNPC with direct right-click interaction (no chat UI).
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

    /// <summary>Whether the local player's mouse is currently over this NPC.</summary>
    private bool _mouseHovering;

    public override void SetStaticDefaults()
    {
        Main.npcFrameCount[Type] = 1;

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
        NPC.velocity.X = 0f;
        PushApartFromOtherBags();

        // Client-side: handle hover and right-click interaction
        if (Main.netMode == NetmodeID.Server)
            return;

        _mouseHovering = false;

        // Check if local player's mouse is over this NPC
        Vector2 mouseWorld = Main.MouseWorld;
        Rectangle hitbox = NPC.Hitbox;

        if (!hitbox.Contains(mouseWorld.ToPoint()))
            return;

        // Check player is close enough to interact (same range as NPC chat: ~192 pixels / 12 tiles)
        Player player = Main.LocalPlayer;
        float dist = Vector2.Distance(player.Center, NPC.Center);
        if (dist > 192f)
            return;

        _mouseHovering = true;

        // Show hover text
        bool isOwner = player.whoAmI == OwnerPlayerIndex;
        string hoverText = isOwner
            ? $"{OwnerName}'s Bag [Right-click to restore]"
            : $"{OwnerName}'s Bag";

        Main.instance.MouseText(hoverText);

        // Consume the right-click so Terraria doesn't try other interactions
        if (Main.mouseRight && Main.mouseRightRelease)
        {
            Main.mouseRightRelease = false;

            if (isOwner)
            {
                Mod.Logger.Info($"[DeathBag] Right-click restore triggered by {player.name}");
                player.GetModPlayer<Players.DeathBagPlayer>().RestoreFromBag(this);
            }
            else
            {
                Main.NewText($"This is {OwnerName}'s bag.", Color.Yellow);
            }
        }
    }

    // Disable chat UI entirely — interaction is handled in AI()
    public override bool CanChat() => false;

    public override bool CheckActive()
    {
        return false;
    }

    public override void PostDraw(SpriteBatch spriteBatch, Vector2 screenPos, Color drawColor)
    {
        // Draw owner name above the bag when hovering
        if (!_mouseHovering)
            return;

        Vector2 namePos = NPC.Top - screenPos + new Vector2(0, -16);
        string name = OwnerName;
        Vector2 textSize = ChatManager.GetStringSize(Terraria.GameContent.FontAssets.MouseText.Value, name, Vector2.One);
        namePos.X -= textSize.X / 2f;

        ChatManager.DrawColorCodedStringWithShadow(
            spriteBatch,
            Terraria.GameContent.FontAssets.MouseText.Value,
            name,
            namePos,
            Color.White,
            0f,
            Vector2.Zero,
            Vector2.One);
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
            float distBetween = diff.Length();

            if (distBetween < pushRadius && distBetween > 0.01f)
            {
                Vector2 push = Vector2.Normalize(diff) * pushStrength * (1f - distBetween / pushRadius);
                NPC.position += push;
            }
        }
    }
}
