namespace FoxSense.Game;

using FoxSense.Core;

/// <summary>
/// Resolves controller → pawn using the verified handle math.
/// </summary>
public static class EntityResolver
{
    public static long GetListEntry(Memory mem, long entityList)
        => mem.Read<long>(entityList + 0x10);

    public static long GetController(Memory mem, long listEntry, int index)
        => mem.Read<long>(listEntry + Offsets.ENTITY_SPACING * index);

    public static long ResolvePawn(Memory mem, long entityList, int handle)
    {
        if (handle == 0 || handle == -1) return 0;

        long entry = mem.Read<long>(
            entityList + 0x8 * ((handle & 0x7FFF) >> 9) + 0x10);
        if (entry == 0) return 0;

        return mem.Read<long>(
            entry + Offsets.ENTITY_SPACING * (handle & 0x1FF));
    }

    public static int ReadPawnHandle(Memory mem, long controller)
    {
        int handle = mem.Read<int>(controller + Offsets.m_hPawn);
        if (handle == 0 || handle == -1)
            handle = mem.Read<int>(controller + Offsets.m_hPawn_Fallback);
        return handle;
    }
}
