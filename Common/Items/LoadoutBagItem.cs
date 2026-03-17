using Terraria;
using Terraria.ID;
using DeathBag.Common.NPCs;

namespace DeathBag.Common.Items;

public sealed class LoadoutBagItem : BagItemBase
{
    public override string Texture => "DeathBag/Common/Items/LoadoutBagItem";

    public override BagKind Kind => BagKind.Loadout;

    protected override bool CanPlaceByUse => true;

    protected override bool ConvertsToNPCWhenDropped => true;

    public override void SetDefaults()
    {
        base.SetDefaults();
        Item.useStyle = ItemUseStyleID.Swing;
        Item.useTime = 15;
        Item.useAnimation = 15;
        Item.consumable = true;
        Item.noUseGraphic = true;
    }

}
