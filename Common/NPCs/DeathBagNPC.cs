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
/// Whether this bag was created by death or by a loadout station.
/// Controls magnet/auto-pickup behavior and color.
/// </summary>
public enum BagKind : byte
{
    Death = 0,
    Loadout = 1,
}

/// <summary>
/// Bag entity that floats in the world holding a player's inventory snapshot.
/// Death bags: auto-pickup with magnet pull for owner.
/// Loadout bags: owner must right-click (no magnet, no auto-pickup).
/// Non-owners always right-click with chat UI confirmation.
/// </summary>
public sealed class DeathBagNPC : ModNPC
{
    /// <summary>Whether this is a death bag or loadout bag.</summary>
    public BagKind Kind = BagKind.Death;

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

    /// <summary>
    /// Name of the player who carried this bag as an item and dropped it.
    /// Empty if the bag was never picked up by a non-owner.
    /// Used for delivery chat messages.
    /// </summary>
    public string DeliveredBy = "";

    /// <summary>Visual-only bob offset (pixels). Applied in draw, not to NPC.position.</summary>
    private float _bobOffset;

    /// <summary>Whether the local player is hovering and in range — set in AI(), read in PostDraw.</summary>
    private bool _showActionHint;

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
        NPC.width = 48;
        NPC.height = 48;
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
        // Visual-only bob — each bag gets a unique phase offset so they don't bob in sync
        // DrawOffsetY = 0 centers sprite on hitbox center (used by our custom PreDraw)
        _bobOffset = (float)Math.Sin(Main.GameUpdateCount * 0.04f + NPC.whoAmI * 1.7f) * 1.5f;
        DrawOffsetY = _bobOffset;

        // Apply friction so bags settle after push-apart, but don't drift forever
        NPC.velocity *= 0.9f;
        PushApartFromOtherBags();

        // Continuously resolve owner index from name (handles join/leave, index changes)
        ResolveOwnerIndex();

        // Keep GivenName in sync (vanilla uses it for hover text)
        UpdateGivenName();

        // Client-side only: handle magnet pull for owner, owner right-click restore for loadout bags
        if (Main.netMode == NetmodeID.Server)
            return;

        Player localPlayer = Main.LocalPlayer;
        bool isOwner = OwnerPlayerIndex >= 0 && localPlayer.whoAmI == OwnerPlayerIndex;
        float dist = Vector2.Distance(localPlayer.Center, NPC.Center);

        _showActionHint = false;

        // === OWNER: death bags auto-pickup with magnet pull ===
        if (isOwner && !localPlayer.dead && Kind == BagKind.Death)
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

        // === OWNER: loadout bags — right-click to restore (no chat UI) ===
        if (isOwner && !localPlayer.dead && Kind == BagKind.Loadout)
        {
            Vector2 mouseWorld = Main.MouseWorld;
            if (NPC.Hitbox.Contains(mouseWorld.ToPoint()) && dist <= 192f
                && Main.mouseRight && Main.mouseRightRelease)
            {
                Main.mouseRightRelease = false;
                localPlayer.GetModPlayer<Players.DeathBagPlayer>().RestoreFromBag(this);
            }
        }

        // Show action hint when hovering and in range (read by PostDraw)
        if (dist <= 192f)
        {
            Vector2 mouseWorld = Main.MouseWorld;
            if (NPC.Hitbox.Contains(mouseWorld.ToPoint()))
                _showActionHint = true;
        }
    }

    // Right-click interaction: non-owners pick up, owner restores (loadout) or auto-picks-up (death)
    public override bool CanChat()
    {
        Player localPlayer = Main.LocalPlayer;
        bool isOwner = OwnerPlayerIndex >= 0 && localPlayer.whoAmI == OwnerPlayerIndex;

        // Death bags: owner auto-picks up via magnet — no chat needed
        // Loadout bags: owner right-click restores instantly in AI() — no chat needed
        if (isOwner)
            return false;

        // Non-owners: always right-click with confirmation (both kinds)
        return true;
    }

    public override string GetChat()
    {
        // Only non-owners reach here (CanChat returns false for owner)
        string name = string.IsNullOrEmpty(OwnerName) ? "Unknown Player" : OwnerName;
        return $"Pick up {name}'s {(Kind == BagKind.Loadout ? "loadout" : "bag")}?";
    }

    public override void SetChatButtons(ref string button1, ref string button2)
    {
        button1 = "Pick Up";
    }

    public override void OnChatButtonClicked(bool firstButton, ref string shopName)
    {
        if (!firstButton)
            return;

        // Only non-owners reach here (CanChat returns false for owner)
        Player localPlayer = Main.LocalPlayer;

        // Non-owner: pick up as inventory item
        if (Main.netMode == NetmodeID.MultiplayerClient)
        {
            DB.SendBagToItem(Mod, NPC.whoAmI, localPlayer.name);
        }
        else
        {
            // Singleplayer: place item directly in player's inventory
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
                Main.NewText("No room in your inventory!", Color.Yellow);
                return;
            }

            string kindName = Kind == BagKind.Loadout ? "Loadout" : "Death Bag";
            var item = new Item();
            item.SetDefaults(ModContent.ItemType<Items.DeathBagItem>());
            if (item.ModItem is Items.DeathBagItem bagItem)
            {
                bagItem.Kind = Kind;
                bagItem.OwnerName = OwnerName;
                bagItem.DeathLoadoutIndex = DeathLoadoutIndex;
                bagItem.SavedInventory = SavedInventory;
                bagItem.CarrierName = localPlayer.name;
            }
            item.SetNameOverride($"{OwnerName}'s {kindName}");
            localPlayer.inventory[emptySlot] = item;

            NPC.active = false;

            Mod.Logger.Info($"[DeathBag] {localPlayer.name} picked up {OwnerName}'s {kindName} ({SavedInventory.Count} items)");
            Main.NewText($"Picked up {OwnerName}'s {kindName}.", Color.Green);
        }

        Main.npcChatText = "";
    }

    public override bool CheckActive()
    {
        return false;
    }

    public override void SendExtraAI(BinaryWriter writer)
    {
        writer.Write((byte)Kind);
        writer.Write(OwnerName ?? "");
        writer.Write(OwnerPlayerIndex);
        writer.Write(DeathLoadoutIndex);
        writer.Write(DeliveredBy ?? "");
        DB.WriteInventory(writer, SavedInventory);
    }

    public override void ReceiveExtraAI(BinaryReader reader)
    {
        Kind = (BagKind)reader.ReadByte();
        OwnerName = reader.ReadString();
        OwnerPlayerIndex = reader.ReadInt32();
        DeathLoadoutIndex = reader.ReadInt32();
        DeliveredBy = reader.ReadString();
        SavedInventory = DB.ReadInventory(reader);

        UpdateGivenName();
    }

    public override bool PreDraw(SpriteBatch spriteBatch, Vector2 screenPos, Color drawColor)
    {
        if (Kind == BagKind.Loadout)
        {
            // Tint loadout bags blue-green to distinguish from death bags
            drawColor = new Color(140, 180, 255) * (drawColor.A / 255f);
        }

        var texture = Terraria.GameContent.TextureAssets.Npc[NPC.type].Value;
        Vector2 drawPos = NPC.position - screenPos + new Vector2(NPC.width / 2f, NPC.height / 2f + DrawOffsetY);
        var origin = new Vector2(texture.Width / 2f, texture.Height / 2f);
        spriteBatch.Draw(texture, drawPos, null, drawColor, 0f, origin, 1f, SpriteEffects.None, 0f);
        return false; // skip default draw for both kinds
    }

    public override void PostDraw(SpriteBatch spriteBatch, Vector2 screenPos, Color drawColor)
    {
        if (!_showActionHint)
            return;

        Player localPlayer = Main.LocalPlayer;
        bool isOwner = OwnerPlayerIndex >= 0 && localPlayer.whoAmI == OwnerPlayerIndex;

        string hint;
        if (isOwner && Kind == BagKind.Loadout)
            hint = "[Right-click to restore]";
        else if (isOwner)
            return; // Death bag owner: auto-pickup, no hint needed
        else
            hint = "[Right-click to pick up]";

        var font = Terraria.GameContent.FontAssets.MouseText.Value;
        Vector2 textSize = ChatManager.GetStringSize(font, hint, Vector2.One);

        // Position above the sprite center
        Vector2 textPos = NPC.Center - screenPos + new Vector2(0, -24f - textSize.Y + DrawOffsetY);
        textPos.X -= textSize.X / 2f;

        ChatManager.DrawColorCodedStringWithShadow(
            spriteBatch, font, hint, textPos,
            new Color(200, 200, 200), 0f, Vector2.Zero, Vector2.One);
    }

    internal void ResolveOwnerIndex()
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

    /// <summary>Keep GivenName in sync with Kind/OwnerName so vanilla's hover text is correct.</summary>
    private void UpdateGivenName()
    {
        string kindLabel = Kind == BagKind.Loadout ? "Loadout" : "Death Bag";
        string expected = string.IsNullOrEmpty(OwnerName)
            ? $"Unknown Player's {kindLabel}"
            : $"{OwnerName}'s {kindLabel}";
        if (NPC.GivenName != expected)
            NPC.GivenName = expected;
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
