using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;
using Terraria.UI.Chat;
using DB = DeathBag.DeathBag;

namespace DeathBag.Common.NPCs;

/// <summary>
/// The bag entity that appears at the player's death location.
/// Owner's bag: auto-pickup like an item (magnet pull + restore on contact).
/// Other players' bags: right-click with chat UI confirmation.
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

    /// <summary>Which loadout was active when the player died. Needed to restore items to correct loadout arrays.</summary>
    public int DeathLoadoutIndex;

    /// <summary>Whether the local player's mouse is currently over this NPC.</summary>
    private bool _mouseHovering;

    // Item magnet constants (matches vanilla item pickup behavior)
    private const float MagnetRange = 224f;  // ~14 tiles — range at which bag starts pulling toward owner
    private const float PickupRange = 32f;   // contact range — triggers restore
    private const float MagnetSpeed = 12f;   // pull speed in pixels/frame

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
        NPC.width = 40;
        NPC.height = 40;
        NPC.aiStyle = -1;
        NPC.damage = 0;
        NPC.defense = 0;
        NPC.lifeMax = 1;
        NPC.HitSound = null;
        NPC.DeathSound = null;
        NPC.knockBackResist = 0f;
        NPC.noGravity = true;
        NPC.noTileCollide = true;
        NPC.dontTakeDamage = true;
        NPC.immortal = true;
        NPC.netAlways = true;
    }

    public override void AI()
    {
        // Gentle hover bob (position-based, not velocity-based, so friction doesn't interfere)
        // Each bag gets a unique phase offset from whoAmI so they don't bob in sync
        NPC.position.Y += (float)Math.Sin(Main.GameUpdateCount * 0.04f + NPC.whoAmI * 1.7f) * 0.3f;

        // Apply friction so bags settle after push-apart, but don't drift forever
        NPC.velocity *= 0.9f;
        PushApartFromOtherBags();

        // Continuously resolve owner index from name (handles join/leave, index changes)
        ResolveOwnerIndex();

        // Client-side only: handle magnet pull for owner, hover for others
        if (Main.netMode == NetmodeID.Server)
            return;

        _mouseHovering = false;

        Player localPlayer = Main.LocalPlayer;
        bool isOwner = OwnerPlayerIndex >= 0 && localPlayer.whoAmI == OwnerPlayerIndex;
        float dist = Vector2.Distance(localPlayer.Center, NPC.Center);

        // === OWNER: item-like magnet pull + auto-restore on contact ===
        if (isOwner && !localPlayer.dead)
        {
            if (dist < PickupRange)
            {
                Mod.Logger.Info($"[DeathBag] Auto-pickup triggered for {localPlayer.name}");
                localPlayer.GetModPlayer<Players.DeathBagPlayer>().RestoreFromBag(this);
                return;
            }

            if (dist < MagnetRange)
            {
                Vector2 direction = localPlayer.Center - NPC.Center;
                if (direction.Length() > 1f)
                {
                    direction.Normalize();
                    NPC.velocity = direction * MagnetSpeed;
                }
            }
        }

        // === HOVER TEXT (both owner and non-owner) ===
        Vector2 mouseWorld = Main.MouseWorld;
        if (!NPC.Hitbox.Contains(mouseWorld.ToPoint()))
            return;

        if (dist > 192f)
            return;

        _mouseHovering = true;

        if (isOwner)
            Main.instance.MouseText($"{OwnerName}'s Bag");
        else
        {
            string label = string.IsNullOrEmpty(OwnerName) ? "Unknown Player's Bag" : $"{OwnerName}'s Bag";
            if (OwnerPlayerIndex >= 0)
                label += " [Right-click to deliver]";
            Main.instance.MouseText(label);
        }
    }

    // Non-owners can right-click to open chat UI for confirmation
    public override bool CanChat()
    {
        Player localPlayer = Main.LocalPlayer;
        // Owner picks up automatically — no chat needed
        if (OwnerPlayerIndex >= 0 && localPlayer.whoAmI == OwnerPlayerIndex)
            return false;
        // Non-owners can chat to deliver (only if owner is online)
        return OwnerPlayerIndex >= 0;
    }

    public override string GetChat()
    {
        string name = string.IsNullOrEmpty(OwnerName) ? "Unknown Player" : OwnerName;
        return $"Deliver {name}'s bag to them?";
    }

    public override void SetChatButtons(ref string button1, ref string button2)
    {
        button1 = "Deliver";
    }

    public override void OnChatButtonClicked(bool firstButton, ref string shopName)
    {
        if (!firstButton)
            return;

        Player localPlayer = Main.LocalPlayer;

        // Find the owner player
        if (OwnerPlayerIndex < 0 || OwnerPlayerIndex >= Main.maxPlayers)
        {
            Main.NewText($"{OwnerName} is not in the game.", Color.Yellow);
            return;
        }

        Player owner = Main.player[OwnerPlayerIndex];
        if (owner == null || !owner.active)
        {
            Main.NewText($"{OwnerName} is not in the game.", Color.Yellow);
            return;
        }

        // In singleplayer this shouldn't happen (you'd be the owner), but handle it
        // In multiplayer, the restore runs on the owner's client — we need packets for that.
        // For now (singleplayer-only), run restore directly.
        if (Main.netMode == NetmodeID.SinglePlayer)
        {
            owner.GetModPlayer<Players.DeathBagPlayer>().RestoreFromBag(this);
        }
        else
        {
            // TODO: Send packet to owner's client to trigger restore
            Main.NewText("Multiplayer bag delivery not yet implemented.", Color.Yellow);
        }

        // Close chat UI
        Main.npcChatText = "";
    }

    public override bool CheckActive()
    {
        return false;
    }

    public override void SendExtraAI(BinaryWriter writer)
    {
        writer.Write(OwnerName ?? "");
        writer.Write(OwnerPlayerIndex);
        writer.Write(DeathLoadoutIndex);
        DB.WriteInventory(writer, SavedInventory);
    }

    public override void ReceiveExtraAI(BinaryReader reader)
    {
        OwnerName = reader.ReadString();
        OwnerPlayerIndex = reader.ReadInt32();
        DeathLoadoutIndex = reader.ReadInt32();
        SavedInventory = DB.ReadInventory(reader);

        if (!string.IsNullOrEmpty(OwnerName))
            NPC.GivenName = $"{OwnerName}'s Death Bag";
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

    private void ResolveOwnerIndex()
    {
        // If we have a name, try to find the matching player
        if (string.IsNullOrEmpty(OwnerName))
            return;

        // Check if current index is still valid
        if (OwnerPlayerIndex >= 0 && OwnerPlayerIndex < Main.maxPlayers)
        {
            var p = Main.player[OwnerPlayerIndex];
            if (p?.active == true && p.name == OwnerName)
                return; // Still valid
        }

        // Search for player by name
        OwnerPlayerIndex = -1;
        for (int i = 0; i < Main.maxPlayers; i++)
        {
            var p = Main.player[i];
            if (p?.active == true && p.name == OwnerName)
            {
                OwnerPlayerIndex = i;
                return;
            }
        }
    }

    private void PushApartFromOtherBags()
    {
        const float pushRadius = 48f;
        const float pushStrength = 1.5f;

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
                NPC.velocity.X += push.X;
            }
        }
    }
}
