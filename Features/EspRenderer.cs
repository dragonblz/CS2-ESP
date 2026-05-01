using System.Windows;
using System.Windows.Media;
using FoxSense.Core;
using FoxSense.Game;

namespace FoxSense.Features;

/// <summary>
/// Production-quality ESP renderer using WPF DrawingContext.
/// All pens/brushes are frozen for thread safety and performance.
/// </summary>
public static class EspRenderer
{
    // ── Cached resources (allocated once, frozen) ──
    private static readonly Typeface Font = new("Segoe UI");
    private static readonly Pen OutlinePen;
    private static readonly Pen BoneShadowPen;
    private static readonly Brush ShadowBrush;

    static EspRenderer()
    {
        OutlinePen = new Pen(new SolidColorBrush(Color.FromArgb(160, 0, 0, 0)), 2.5);
        OutlinePen.Freeze();

        BoneShadowPen = new Pen(new SolidColorBrush(Color.FromArgb(80, 0, 0, 0)), 3.2);
        BoneShadowPen.Freeze();

        ShadowBrush = new SolidColorBrush(Color.FromArgb(180, 0, 0, 0));
        ShadowBrush.Freeze();
    }

    public static void Draw(DrawingContext dc, IReadOnlyList<PlayerData> players,
        EspSettings settings, int screenW, int screenH, Vector3 localPos, int localTeam)
    {
        foreach (var p in players)
        {
            if (!p.OnScreen || p.BoxHeight < 5f) continue;
            if (settings.EnemyOnly && p.Team == localTeam) continue;

            var color = settings.Color;
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            var pen = new Pen(brush, 1.5);
            pen.Freeze();

            if (settings.Box)
                DrawBox(dc, p, pen);

            if (settings.Skeleton)
                DrawSkeleton(dc, p, color);

            if (settings.HealthBar)
                DrawHealthBar(dc, p);

            if (settings.Names && !string.IsNullOrEmpty(p.Name))
                DrawName(dc, p, brush);

            if (settings.Distance)
                DrawDistance(dc, p, localPos, brush);

            if (settings.SnapLines)
                DrawSnapLine(dc, p, pen, screenW, screenH);
        }
    }

    public static void DrawFovCircle(DrawingContext dc, double fov,
        int screenW, int screenH, Color color)
    {
        var pen = new Pen(new SolidColorBrush(Color.FromArgb(50, color.R, color.G, color.B)), 1.2);
        pen.Freeze();
        dc.DrawEllipse(null, pen,
            new Point(screenW / 2.0, screenH / 2.0), fov, fov);
    }

    // ═══════════════════════════════════════════════════
    //  DRAWING PRIMITIVES
    // ═══════════════════════════════════════════════════

    private static void DrawBox(DrawingContext dc, PlayerData p, Pen pen)
    {
        var rect = new Rect(p.BoxX, p.BoxY, p.BoxWidth, p.BoxHeight);

        // Black outline for contrast
        dc.DrawRectangle(null, OutlinePen,
            new Rect(rect.X - 1, rect.Y - 1, rect.Width + 2, rect.Height + 2));
        dc.DrawRectangle(null, pen, rect);
    }

    /// <summary>
    /// Draws anatomically correct bone connections.
    /// Each connection is only drawn if BOTH endpoint bones are valid.
    /// Shadow lines are drawn behind for contrast against bright backgrounds.
    /// </summary>
    private static void DrawSkeleton(DrawingContext dc, PlayerData p, Color color)
    {
        var bonePen = new Pen(new SolidColorBrush(color), 1.6);
        bonePen.Freeze();

        int validCount = 0;
        foreach (var (from, to) in Offsets.BoneConnections)
        {
            if (from >= PlayerData.MAX_BONES || to >= PlayerData.MAX_BONES) continue;
            if (!p.BoneValid[from] || !p.BoneValid[to]) continue;

            var p1 = new Point(p.BoneScreen[from].X, p.BoneScreen[from].Y);
            var p2 = new Point(p.BoneScreen[to].X, p.BoneScreen[to].Y);

            // Reject obviously broken lines (> 500px between bones on screen)
            double lineLenSq = (p2.X - p1.X) * (p2.X - p1.X) + (p2.Y - p1.Y) * (p2.Y - p1.Y);
            if (lineLenSq > 500 * 500) continue;

            dc.DrawLine(BoneShadowPen, p1, p2); // Shadow
            dc.DrawLine(bonePen, p1, p2);        // Colored line
            validCount++;
        }

        // Draw head circle if we have a valid head bone and at least some skeleton lines
        if (validCount > 0 && Offsets.BONE_HEAD < PlayerData.MAX_BONES && p.BoneValid[Offsets.BONE_HEAD])
        {
            var headPt = new Point(p.BoneScreen[Offsets.BONE_HEAD].X, p.BoneScreen[Offsets.BONE_HEAD].Y);
            double radius = p.BoxHeight * 0.06; // Scale head circle with distance
            if (radius > 2 && radius < 40)
            {
                dc.DrawEllipse(null, bonePen, headPt, radius, radius);
            }
        }
    }

    private static void DrawHealthBar(DrawingContext dc, PlayerData p)
    {
        float barW = 3f;
        float barH = p.BoxHeight;
        float barX = p.BoxX - barW - 3f;
        float barY = p.BoxY;

        // Background
        dc.DrawRectangle(Brushes.Black, null,
            new Rect(barX - 1, barY - 1, barW + 2, barH + 2));

        // Health fill (green → red gradient)
        float ratio = Math.Clamp(p.Health / 100f, 0f, 1f);
        float fillH = barH * ratio;
        float fillY = barY + (barH - fillH);

        byte r = (byte)(255 * (1f - ratio));
        byte g = (byte)(255 * ratio);
        var hpBrush = new SolidColorBrush(Color.FromRgb(r, g, 0));
        hpBrush.Freeze();

        dc.DrawRectangle(hpBrush, null,
            new Rect(barX, fillY, barW, fillH));
    }

    private static void DrawName(DrawingContext dc, PlayerData p, Brush brush)
    {
        try
        {
            var text = new FormattedText(p.Name, System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, Font, 10, brush, 1.0);

            double x = p.ScreenHead.X - text.Width / 2;
            double y = p.ScreenHead.Y - text.Height - 4;

            var shadow = new FormattedText(p.Name, System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, Font, 10, ShadowBrush, 1.0);
            dc.DrawText(shadow, new Point(x + 1, y + 1));
            dc.DrawText(text, new Point(x, y));
        }
        catch { /* ignore rendering errors during transitions */ }
    }

    private static void DrawDistance(DrawingContext dc, PlayerData p, Vector3 localPos, Brush brush)
    {
        try
        {
            float dist = localPos.DistanceTo(p.FeetPos) / 100f;
            string label = $"{dist:F0}m";

            var text = new FormattedText(label, System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, Font, 9, brush, 1.0);

            dc.DrawText(text, new Point(
                p.ScreenFeet.X - text.Width / 2,
                p.ScreenFeet.Y + 4));
        }
        catch { /* ignore rendering errors during transitions */ }
    }

    private static void DrawSnapLine(DrawingContext dc, PlayerData p, Pen pen, int screenW, int screenH)
    {
        dc.DrawLine(pen,
            new Point(screenW / 2.0, screenH),
            new Point(p.ScreenFeet.X, p.ScreenFeet.Y));
    }
}

public class EspSettings
{
    public bool Enabled { get; set; } = true;
    public bool Box { get; set; } = true;
    public bool Skeleton { get; set; } = true;
    public bool HealthBar { get; set; } = true;
    public bool Names { get; set; } = true;
    public bool Distance { get; set; } = true;
    public bool SnapLines { get; set; }
    public bool EnemyOnly { get; set; } = true;
    public Color Color { get; set; } = Color.FromRgb(0xFF, 0x4B, 0x2B);
}
