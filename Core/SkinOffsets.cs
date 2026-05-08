namespace FoxSense.Core;

/// <summary>
/// All skin-changer-specific offsets.
/// Source: sezzyaep/CS2-OFFSETS (latest)
/// 
/// Class mapping for m_AttributeManager:
///   C_EconEntity = 0x1180 ← USE THIS (weapon base class)
///   C_PlantedC4 = 0x11C0
///   C_Chicken = 0x13B8
/// </summary>
public static class SkinOffsets
{
    // ═══════════════════════════════════════════════════
    //  ENGINE2 (for ForceFullUpdate)
    // ═══════════════════════════════════════════════════
    public static int dwNetworkGameClient = 0x9090C0;
    public static int dwNetworkGameClient_deltaTick = 0x24C;

    // ═══════════════════════════════════════════════════
    //  WEAPON SERVICES (from C_BasePlayerPawn)
    // ═══════════════════════════════════════════════════
    public static int m_pWeaponServices = 0x11E0;
    public static int m_hMyWeapons = 0x48;
    public static int m_hActiveWeapon = 0x60;
    public static int m_pClippingWeapon = 0x14F8;

    // ═══════════════════════════════════════════════════
    //  C_EconEntity (weapon entity)
    // ═══════════════════════════════════════════════════
    public static int m_AttributeManager = 0x1180;
    public static int m_nFallbackPaintKit = 0x1658;
    public static int m_nFallbackSeed = 0x165C;
    public static int m_flFallbackWear = 0x1660;
    public static int m_nFallbackStatTrak = 0x1664;
    public static int m_OriginalOwnerXuidLow = 0x1650;

    // ═══════════════════════════════════════════════════
    //  C_EconItemView (inside m_AttributeManager + m_Item)
    // ═══════════════════════════════════════════════════
    public static int m_Item = 0x50;
    public static int m_iItemDefinitionIndex = 0x1BA;
    public static int m_iItemIDHigh = 0x1D0;
    public static int m_iAccountID = 0x1D8;
    public static int m_iEntityQuality = 0x1BC;
    public static int m_bInitialized = 0x1E8;
    public static int m_AttributeList = 0x208;
    public static int m_NetworkedDynamicAttributes = 0x280;
    public static int m_Attributes = 0x8;
    public static int m_szCustomName = 0x2F8;
    public static int m_szCustomNameOverride = 0x399;

    // ═══════════════════════════════════════════════════
    //  GLOVES (C_CSPlayerPawn)
    // ═══════════════════════════════════════════════════
    public static int m_EconGloves = 0x1658;
    public static int m_bNeedToReApplyGloves = 0x1655;

    // ═══════════════════════════════════════════════════
    //  SCENE NODE / MODEL
    // ═══════════════════════════════════════════════════
    public static int m_pGameSceneNode_skin = 0x330;
    public static int m_modelState_skin = 0x150;
    public static int m_MeshGroupMask = 0x1C8;
    public static int m_nSubclassID = 0x380;
    public static int m_hOwnerEntity = 0x520;

    // ═══════════════════════════════════════════════════
    //  ENTITY IDENTITY
    // ═══════════════════════════════════════════════════
    public static int m_pEntity = 0x10;
    public static int m_flags = 0x30;

    // ═══════════════════════════════════════════════════
    //  INVENTORY
    // ═══════════════════════════════════════════════════
    public static int m_pInventoryServices = 0x810;
    public static int m_unMusicID = 0x44;
}
