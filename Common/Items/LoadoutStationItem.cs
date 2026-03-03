using Terraria;
using Terraria.ID;
using Terraria.ModLoader;
using DeathBag.Common.Tiles;

namespace DeathBag.Common.Items;

/// <summary>
/// Placeable item for the Loadout Station furniture tile.
/// Underground-tier recipe — early/mid game.
/// </summary>
public sealed class LoadoutStationItem : ModItem
{
    public override void SetDefaults()
    {
        Item.DefaultToPlaceableTile(ModContent.TileType<LoadoutStationTile>());
        Item.width = 48;
        Item.height = 48;
        Item.value = Item.buyPrice(gold: 5);
        Item.rare = ItemRarityID.Green;
    }

    public override void AddRecipes()
    {
        // Requires an underground Gold Chest (NOT the Pirate Invasion Golden Chest)
        CreateRecipe()
            .AddIngredient(ItemID.Wood, 20)
            .AddIngredient(ItemID.Bone, 25)
            .AddIngredient(ItemID.GoldChest)
            .AddTile(TileID.WorkBenches)
            .Register();
    }
}
