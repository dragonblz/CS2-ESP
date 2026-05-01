using System.Runtime.InteropServices;
using FoxSense.Core;
using FoxSense.Game;

namespace FoxSense.Features;

/// <summary>
/// FOV-based soft aimbot with deceleration curve.
/// Uses mouse_event for input — standard Win32, no hooks.
/// </summary>
public sealed class SoftAim
{
    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    private static extern void mouse_event(uint flags, int dx, int dy, uint data, UIntPtr extra);

    private const uint MOUSEEVENTF_MOVE = 0x0001;

    public void Tick(GameState state, AimSettings settings, int screenW, int screenH)
    {
        if (!settings.Enabled || !state.InGame) return;
        if ((GetAsyncKeyState(settings.AimKey) & 0x8000) == 0) return;

        float centerX = screenW / 2f;
        float centerY = screenH / 2f;

        // Find the closest valid target within FOV
        var players = state.GetPlayers();
        float bestDist = float.MaxValue;
        Vector3 bestScreen = default;
        bool found = false;

        int boneId = settings.BoneTarget switch
        {
            BoneTarget.Neck => Offsets.BONE_NECK,
            BoneTarget.Chest => Offsets.BONE_CHEST,
            _ => Offsets.BONE_HEAD,
        };

        foreach (var p in players)
        {
            if (!p.OnScreen) continue;
            if (settings.EnemyOnly && p.Team == state.LocalTeam) continue;
            if (p.Health <= 0) continue;

            // Use bone position if available, otherwise use head projection
            Vector3 targetScreen;
            if (boneId < PlayerData.MAX_BONES && p.BoneValid[boneId])
                targetScreen = p.BoneScreen[boneId];
            else
                targetScreen = p.ScreenHead;

            float dx = targetScreen.X - centerX;
            float dy = targetScreen.Y - centerY;
            float dist = MathF.Sqrt(dx * dx + dy * dy);

            if (dist <= settings.Fov && dist < bestDist)
            {
                bestDist = dist;
                bestScreen = targetScreen;
                found = true;
            }
        }

        if (!found) return;

        // Calculate delta from crosshair
        float deltaX = bestScreen.X - centerX;
        float deltaY = bestScreen.Y - centerY;
        float distance = MathF.Sqrt(deltaX * deltaX + deltaY * deltaY);

        if (distance < 0.5f) return; // Already on target

        // Exponential convergence: move 1/smooth of the remaining distance.
        // At smooth=5, you move 20% per tick → converges quickly without overshoot.
        float factor = 1.0f / settings.Smooth;
        float rawX = deltaX * factor;
        float rawY = deltaY * factor;

        // Guarantee at least 1px movement so we don't stall near the target
        int moveX = (int)MathF.Round(rawX);
        int moveY = (int)MathF.Round(rawY);
        if (moveX == 0 && MathF.Abs(deltaX) > 0.5f) moveX = deltaX > 0 ? 1 : -1;
        if (moveY == 0 && MathF.Abs(deltaY) > 0.5f) moveY = deltaY > 0 ? 1 : -1;

        mouse_event(MOUSEEVENTF_MOVE, moveX, moveY, 0, UIntPtr.Zero);
    }
}

public enum BoneTarget { Head, Neck, Chest }

public class AimSettings
{
    public bool Enabled { get; set; }
    public bool EnemyOnly { get; set; } = true;
    public int AimKey { get; set; } = 0x01; // VK_LBUTTON
    public float Fov { get; set; } = 80f;
    public float Smooth { get; set; } = 5f; // 1 = instant, 15 = very slow
    public BoneTarget BoneTarget { get; set; } = BoneTarget.Head;
}
