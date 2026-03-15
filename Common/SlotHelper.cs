using Terraria;

namespace DeathBag.Common;

internal static class SlotHelper
{
    public const int MainInventorySlotCount = 50;
    public const int SlotArmor = 59;
    public const int SlotDye = 79;
    public const int SlotMiscEquips = 89;
    public const int SlotMiscDyes = 94;
    public const int SlotLoadoutsStart = 99;
    public const int LoadoutArmorSlotCount = 20;
    public const int LoadoutSize = 30;

    public static int GetMaxSyncEquipmentSlot(Player player)
    {
        return SlotLoadoutsStart + player.Loadouts.Length * LoadoutSize;
    }

    public static bool TrySetSlot(Player player, int slotIndex, Item item)
    {
        if (!TryResolveSlot(player, slotIndex, out Item[] array, out int index))
            return false;

        array[index] = item;
        return true;
    }

    public static bool TryPlaceInSlotIfEmpty(Player player, int slotIndex, Item item)
    {
        if (!TryResolveSlot(player, slotIndex, out Item[] array, out int index))
            return false;

        Item current = array[index];
        if (current is not null && !current.IsAir)
            return false;

        array[index] = item;
        return true;
    }

    public static bool TryClearSlot(Player player, int slotIndex)
    {
        if (!TryResolveSlot(player, slotIndex, out Item[] array, out int index))
            return false;

        array[index] = new Item();
        return true;
    }

    private static bool TryResolveSlot(Player player, int slotIndex, out Item[] array, out int index)
    {
        if (slotIndex < 0)
        {
            array = null;
            index = -1;
            return false;
        }

        if (slotIndex < SlotArmor)
            return TryResolve(array: player.inventory, index: slotIndex, out array, out index);
        if (slotIndex < SlotDye)
            return TryResolve(array: player.armor, index: slotIndex - SlotArmor, out array, out index);
        if (slotIndex < SlotMiscEquips)
            return TryResolve(array: player.dye, index: slotIndex - SlotDye, out array, out index);
        if (slotIndex < SlotMiscDyes)
            return TryResolve(array: player.miscEquips, index: slotIndex - SlotMiscEquips, out array, out index);
        if (slotIndex < SlotLoadoutsStart)
            return TryResolve(array: player.miscDyes, index: slotIndex - SlotMiscDyes, out array, out index);

        int loadoutOffset = slotIndex - SlotLoadoutsStart;
        int loadoutIndex = loadoutOffset / LoadoutSize;
        int withinLoadout = loadoutOffset % LoadoutSize;

        if (loadoutIndex < 0 || loadoutIndex >= player.Loadouts.Length || loadoutIndex == player.CurrentLoadoutIndex)
        {
            array = null;
            index = -1;
            return false;
        }

        if (withinLoadout < LoadoutArmorSlotCount)
            return TryResolve(array: player.Loadouts[loadoutIndex].Armor, index: withinLoadout, out array, out index);

        return TryResolve(array: player.Loadouts[loadoutIndex].Dye, index: withinLoadout - LoadoutArmorSlotCount, out array, out index);
    }

    private static bool TryResolve(Item[] array, int index, out Item[] resolvedArray, out int resolvedIndex)
    {
        if (index < 0 || index >= array.Length)
        {
            resolvedArray = null;
            resolvedIndex = -1;
            return false;
        }

        resolvedArray = array;
        resolvedIndex = index;
        return true;
    }
}
