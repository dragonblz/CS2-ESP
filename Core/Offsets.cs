namespace FoxSense.Core;

/// <summary>
/// All offsets verified against cs2-dumper (2026-04-30 dump).
/// Source: offsets.hpp + client_dll.hpp
/// Bone indices updated for post-April-2026 animgraph_2_beta build.
/// </summary>
public static class Offsets
{
    // ── Base offsets (relative to client.dll, offsets.hpp 2026-04-30) ──
    public const int dwEntityList            = 0x24D1DF0;
    public const int dwLocalPlayerController = 0x230B5D0;
    public const int dwViewMatrix            = 0x2331B30;
    public const int dwLocalPlayerPawn       = 0x2057720;
    public const int dwViewAngles            = 0x2341488;

    // ── Controller (CCSPlayerController) ──
    public const int m_hPawn          = 0x6BC;   // CHandle<C_BasePlayerPawn>
    public const int m_hPawn_Fallback = 0x904;   // CHandle<C_CSPlayerPawn>
    public const int m_iszPlayerName  = 0x6F0;   // char[128]

    // ── Pawn (C_CSPlayerPawn) ──
    public const int m_iHealth         = 0x34C;  // int32
    public const int m_iTeamNum        = 0x3EB;  // uint8
    public const int m_vOldOrigin      = 0x1390; // Vector
    public const int m_pGameSceneNode  = 0x330;  // CGameSceneNode*
    public const int m_ArmorValue      = 0x1C7C; // int32

    // ── Scene node / skeleton ──
    public const int m_vecAbsOrigin    = 0xC8;   // VectorWS (in CGameSceneNode)
    public const int m_modelState      = 0x150;  // CModelState (in CSkeletonInstance)
    public const int BONE_ARRAY_OFFSET = 0x80;   // Bone array ptr within CModelState
    public const int BONE_STRIDE       = 32;     // sizeof(CTransform)
    public const float PLAYER_HEIGHT   = 65f;    // Estimated head height from feet

    // ── Entity list traversal ──
    public const int ENTITY_SPACING = 0x70;

    // ═══════════════════════════════════════════════════════════════
    //  BONE INDICES — animgraph_2_beta (April 2026)
    //  Verified from community research. Root bone is now index 0,
    //  pelvis moved to 1, and legs moved to 17-22 range.
    // ═══════════════════════════════════════════════════════════════
    public const int BONE_ORIGIN     = 0;
    public const int BONE_PELVIS     = 1;
    public const int BONE_SPINE_0    = 2;
    public const int BONE_SPINE_1    = 3;
    public const int BONE_SPINE_2    = 4;
    public const int BONE_NECK       = 6;
    public const int BONE_HEAD       = 7;

    public const int BONE_SHOULDER_L = 9;
    public const int BONE_ELBOW_L    = 10;
    public const int BONE_HAND_L     = 11;

    public const int BONE_SHOULDER_R = 13;
    public const int BONE_ELBOW_R    = 14;
    public const int BONE_HAND_R     = 15;

    public const int BONE_HIP_L     = 17;
    public const int BONE_KNEE_L    = 18;
    public const int BONE_FOOT_L    = 19;

    public const int BONE_HIP_R     = 20;
    public const int BONE_KNEE_R    = 21;
    public const int BONE_FOOT_R    = 22;

    public const int BONE_CHEST     = 23;  // Upper chest / sternum

    /// <summary>
    /// Anatomically correct bone connections for skeleton ESP.
    /// Uses the animgraph_2_beta bone layout.
    /// Spine: PELVIS → SPINE1 → SPINE2 → CHEST → NECK → HEAD
    /// Arms branch from NECK, legs branch from PELVIS.
    /// </summary>
    public static readonly (int From, int To)[] BoneConnections =
    {
        // ── Spine chain ──
        (BONE_PELVIS,  BONE_SPINE_1),
        (BONE_SPINE_1, BONE_SPINE_2),
        (BONE_SPINE_2, BONE_CHEST),
        (BONE_CHEST,   BONE_NECK),
        (BONE_NECK,    BONE_HEAD),

        // ── Left arm (from neck/chest area) ──
        (BONE_NECK,       BONE_SHOULDER_L),
        (BONE_SHOULDER_L, BONE_ELBOW_L),
        (BONE_ELBOW_L,    BONE_HAND_L),

        // ── Right arm (from neck/chest area) ──
        (BONE_NECK,       BONE_SHOULDER_R),
        (BONE_SHOULDER_R, BONE_ELBOW_R),
        (BONE_ELBOW_R,    BONE_HAND_R),

        // ── Left leg ──
        (BONE_PELVIS, BONE_HIP_L),
        (BONE_HIP_L,  BONE_KNEE_L),
        (BONE_KNEE_L,  BONE_FOOT_L),

        // ── Right leg ──
        (BONE_PELVIS, BONE_HIP_R),
        (BONE_HIP_R,  BONE_KNEE_R),
        (BONE_KNEE_R,  BONE_FOOT_R),
    };
}
