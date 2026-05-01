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
    public const int m_hPawn_Fallback = 0x904;   // CHandle<C_CSPlayerPawn>  (m_hPlayerPawn)
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
    public const int BONE_STRIDE       = 32;     // sizeof(CTransform) = 32 bytes
    public const float PLAYER_HEIGHT   = 65f;    // Estimated head height from feet

    // ── Entity list traversal ──
    public const int ENTITY_SPACING = 0x70;

    // ═══════════════════════════════════════════════════════════════
    //  BONE INDICES — Post-April-2026 (animgraph_2_beta)
    //  A new spine bone was inserted at index 5, shifting all
    //  subsequent indices by +1.
    // ═══════════════════════════════════════════════════════════════
    public const int BONE_PELVIS     = 0;
    public const int BONE_SPINE_0    = 1;
    public const int BONE_SPINE_1    = 2;
    public const int BONE_SPINE_2    = 3;
    public const int BONE_SPINE_3    = 4;
    public const int BONE_SPINE_4    = 5;  // NEW in April 2026
    public const int BONE_NECK       = 6;  // was 5
    public const int BONE_HEAD       = 7;  // was 6

    public const int BONE_SHOULDER_L = 8;
    public const int BONE_ELBOW_L    = 9;
    public const int BONE_HAND_L     = 10;

    public const int BONE_SHOULDER_R = 13;
    public const int BONE_ELBOW_R    = 14;
    public const int BONE_HAND_R     = 15;

    public const int BONE_HIP_L     = 22;
    public const int BONE_KNEE_L    = 23;
    public const int BONE_FOOT_L    = 24;

    public const int BONE_HIP_R     = 25;
    public const int BONE_KNEE_R    = 26;
    public const int BONE_FOOT_R    = 27;

    /// <summary>
    /// Anatomically correct bone connections for skeleton ESP.
    /// Draws: spine chain → head, both arms, both legs.
    /// </summary>
    public static readonly (int From, int To)[] BoneConnections =
    {
        // ── Spine chain ──
        (BONE_PELVIS,  BONE_SPINE_1),
        (BONE_SPINE_1, BONE_SPINE_3),
        (BONE_SPINE_3, BONE_SPINE_4),
        (BONE_SPINE_4, BONE_NECK),
        (BONE_NECK,    BONE_HEAD),

        // ── Left arm ──
        (BONE_SPINE_4, BONE_SHOULDER_L),
        (BONE_SHOULDER_L, BONE_ELBOW_L),
        (BONE_ELBOW_L, BONE_HAND_L),

        // ── Right arm ──
        (BONE_SPINE_4, BONE_SHOULDER_R),
        (BONE_SHOULDER_R, BONE_ELBOW_R),
        (BONE_ELBOW_R, BONE_HAND_R),

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
