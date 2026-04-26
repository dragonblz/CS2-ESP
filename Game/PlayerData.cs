namespace FoxSense.Game;

using FoxSense.Core;

public struct PlayerData
{
    public long PawnAddress;
    public int Health;
    public int Team;
    public string Name;

    // World positions
    public Vector3 FeetPos;
    public Vector3 HeadPos;

    // Screen positions
    public Vector3 ScreenFeet;
    public Vector3 ScreenHead;
    public bool OnScreen;

    // Bones (screen space)
    public const int MAX_BONES = 28;
    public Vector3[] BoneScreen;
    public bool[] BoneValid;

    public readonly float BoxHeight => MathF.Abs(ScreenFeet.Y - ScreenHead.Y);
    public readonly float BoxWidth => BoxHeight * 0.45f;
    public readonly float BoxX => ScreenHead.X - BoxWidth / 2f;
    public readonly float BoxY => ScreenHead.Y;
}
