namespace FoxSense.Core;

/// <summary>
/// All offsets verified by C++ fuzzer (2026-04-24 dump).
/// Source: offsets.hpp + client_dll.hpp
/// </summary>
public static class Offsets
{
    // ── Base offsets (relative to client.dll) ──
    public const int dwEntityList            = 0x24CED50;
    public const int dwLocalPlayerController = 0x2308520;
    public const int dwViewMatrix            = 0x232EAC0;
    public const int dwLocalPlayerPawn       = 0x20547A0;
    public const int dwViewAngles            = 0x233E408;

    // ── Controller ──
    public const int m_hPawn          = 0x6BC;   // Primary (verified)
    public const int m_hPawn_Fallback = 0x904;   // Secondary (verified)
    public const int m_iszPlayerName  = 0x6F0;  // char[128]

    // ── Pawn ──
    public const int m_iHealth         = 0x34C;
    public const int m_iTeamNum        = 0x3EB;  // uint8
    public const int m_vOldOrigin      = 0x1390;
    public const int m_pGameSceneNode  = 0x330;
    public const int m_ArmorValue      = 0x1C74;

    // ── Scene node ──
    public const int m_vecAbsOrigin = 0xC8;
    public const int m_modelState   = 0x150;
    // Bone array: gameSceneNode + m_modelState + 0x80
    public const int BONE_POS_OFFSET = 0; // Position at start of CTransform

    // ── Entity list constants ──
    public const int ENTITY_SPACING   = 0x70;
    public const int BONE_STRIDE      = 32;
    public const float PLAYER_HEIGHT  = 65f;

    // ── Bone indices ──
    public const int BONE_HEAD       = 6;
    public const int BONE_NECK       = 5;
    public const int BONE_SPINE      = 4;
    public const int BONE_PELVIS     = 0;
    public const int BONE_SHOULDER_L = 8;
    public const int BONE_ELBOW_L    = 9;
    public const int BONE_HAND_L     = 10;
    public const int BONE_SHOULDER_R = 13;
    public const int BONE_ELBOW_R    = 14;
    public const int BONE_HAND_R     = 15;
    public const int BONE_HIP_L      = 22;
    public const int BONE_KNEE_L     = 23;
    public const int BONE_FOOT_L     = 24;
    public const int BONE_HIP_R      = 25;
    public const int BONE_KNEE_R     = 26;
    public const int BONE_FOOT_R     = 27;

    public static readonly (int From, int To)[] BoneConnections =
    {
        (BONE_HEAD, BONE_NECK),
        (BONE_NECK, BONE_SPINE),
        (BONE_SPINE, BONE_PELVIS),
        (BONE_SPINE, BONE_SHOULDER_L),
        (BONE_SHOULDER_L, BONE_ELBOW_L),
        (BONE_ELBOW_L, BONE_HAND_L),
        (BONE_SPINE, BONE_SHOULDER_R),
        (BONE_SHOULDER_R, BONE_ELBOW_R),
        (BONE_ELBOW_R, BONE_HAND_R),
        (BONE_PELVIS, BONE_HIP_L),
        (BONE_HIP_L, BONE_KNEE_L),
        (BONE_KNEE_L, BONE_FOOT_L),
        (BONE_PELVIS, BONE_HIP_R),
        (BONE_HIP_R, BONE_KNEE_R),
        (BONE_KNEE_R, BONE_FOOT_R),
    };
}
