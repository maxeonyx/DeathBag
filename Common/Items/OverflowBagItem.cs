using Terraria;
using DeathBag.Common.NPCs;

namespace DeathBag.Common.Items;

public sealed class OverflowBagItem : BagItemBase
{
    public override BagKind Kind => BagKind.Overflow;

    public override void UpdateInventory(Player player)
    {
        Item.favorited = true;
    }

    public override bool CanUseItem(Player player)
    {
        return false;
    }
}
