using System.Runtime.InteropServices;

namespace FoxSense.Core;

[StructLayout(LayoutKind.Sequential)]
public struct Vector3
{
    public float X, Y, Z;

    public Vector3(float x, float y, float z) { X = x; Y = y; Z = z; }

    public readonly bool IsZero => X == 0f && Y == 0f && Z == 0f;

    public readonly float DistanceTo(Vector3 other)
    {
        float dx = X - other.X, dy = Y - other.Y, dz = Z - other.Z;
        return MathF.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    public static Vector3 operator +(Vector3 a, Vector3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static Vector3 operator -(Vector3 a, Vector3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
}

[StructLayout(LayoutKind.Sequential)]
public struct ViewMatrix
{
    public float M11, M12, M13, M14;
    public float M21, M22, M23, M24;
    public float M31, M32, M33, M34;
    public float M41, M42, M43, M44;

    public readonly bool WorldToScreen(Vector3 world, out Vector3 screen, int width, int height)
    {
        screen = default;
        float w = M41 * world.X + M42 * world.Y + M43 * world.Z + M44;
        if (w < 0.001f) return false;

        float invW = 1.0f / w;
        float halfW = width * 0.5f;
        float halfH = height * 0.5f;

        screen.X = halfW + (M11 * world.X + M12 * world.Y + M13 * world.Z + M14) * invW * halfW;
        screen.Y = halfH - (M21 * world.X + M22 * world.Y + M23 * world.Z + M24) * invW * halfH;
        return true;
    }
}
