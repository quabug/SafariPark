using System.Numerics;

namespace SafariPark;

/// <summary>Rolling forest floor heightfield shared by rendering, AI and the player.</summary>
/// <remarks>A single analytic function keeps every system sampling the same ground; a second
/// collision mesh would only drift apart. Coordinates follow the engine contract: RH, Y-up,
/// −Z forward, meters.</remarks>
public static class Terrain
{
    /// <summary>Half-extent of the playable clearing inside the treeline on both axes.</summary>
    public const float HalfSize = 80f;

    /// <summary>Pond position and radius; water sits at <see cref="WaterLevel"/>.</summary>
    public static readonly Vector3 PondCenter = new(10f, 0f, 12f);
    public const float PondRadius = 6.5f;
    public const float WaterLevel = -0.55f;

    /// <summary>Forest floor height at a world XZ point: gentle hills plus the pond bowl.</summary>
    public static float Height(float x, float z)
    {
        // Broad rolling mounds — large wavelengths stay walkable at a glance.
        float h =
            1.6f * MathF.Sin(x * 0.030f + 1.7f) * MathF.Cos(z * 0.024f - 0.6f) +
            0.7f * MathF.Sin(x * 0.075f - 0.9f) * MathF.Sin(z * 0.068f + 2.1f) +
            0.25f * MathF.Sin(x * 0.21f + z * 0.17f);

        // Pond bowl: smooth, steep-sided depression so the water plane reads as a lake.
        float dx = x - PondCenter.X, dz = z - PondCenter.Z;
        float d = MathF.Sqrt(dx * dx + dz * dz);
        if (d < PondRadius)
        {
            float t = 1f - d / PondRadius;
            h -= 2.2f * t * t * (3f - 2f * t); // smoothstep-shaped bowl, deepest -2.2 at center
        }
        return h;
    }

    public static float Height(Vector3 p) => Height(p.X, p.Z);

    /// <summary>Terrain surface normal from central differences — used to align nothing yet,
    /// but kept cheap for future slope checks.</summary>
    public static Vector3 Normal(float x, float z)
    {
        const float e = 0.35f;
        var dx = Height(x + e, z) - Height(x - e, z);
        var dz = Height(x, z + e) - Height(x, z - e);
        return Vector3.Normalize(new Vector3(-dx / (2f * e), 1f, -dz / (2f * e)));
    }

    /// <summary>True when a position is inside the pond footprint below the waterline —
    /// the walkability check animals run before accepting a wander target.</summary>
    public static bool IsWater(float x, float z)
    {
        float dx = x - PondCenter.X, dz = z - PondCenter.Z;
        return dx * dx + dz * dz < PondRadius * PondRadius * 0.92f * 0.92f;
    }

    /// <summary>Ground triangle mesh in the PBR vertex layout (pos3/normal3/uv2/tangent4).</summary>
    /// <param name="vertexColorFactor">Baked slope shading: darker on hollows, lighter on
    /// crests — the multiplier a plain grass material can't express without a texture.</param>
    /// <returns>Interleaved floats and triangle indices ready for UploadPrimitive.</returns>
    public static (float[] Vertices, uint[] Indices) BuildMesh(int resolution = 160)
    {
        int vertsPerSide = resolution + 1;
        var vertices = new float[vertsPerSide * vertsPerSide * 12];
        var indices = new uint[resolution * resolution * 6];

        var w = 0;
        for (int iz = 0; iz < vertsPerSide; iz++)
        {
            for (int ix = 0; ix < vertsPerSide; ix++)
            {
                float x = -HalfSize + HalfSize * 2f * ix / resolution;
                float z = -HalfSize + HalfSize * 2f * iz / resolution;
                float y = Height(x, z);
                var n = Normal(x, z);

                vertices[w++] = x; vertices[w++] = y; vertices[w++] = z;
                vertices[w++] = n.X; vertices[w++] = n.Y; vertices[w++] = n.Z;
                vertices[w++] = ix * 0.25f; vertices[w++] = iz * 0.25f;
                // Tangent along +X (east) — consistent with the heightfield's XZ grid.
                vertices[w++] = 1f; vertices[w++] = 0f; vertices[w++] = 0f; vertices[w++] = 1f;
            }
        }

        var i = 0;
        for (int iz = 0; iz < resolution; iz++)
        {
            for (int ix = 0; ix < resolution; ix++)
            {
                uint a = (uint)(iz * vertsPerSide + ix);
                uint b = a + 1;
                uint c = a + (uint)vertsPerSide;
                uint d = c + 1;
                indices[i++] = a; indices[i++] = c; indices[i++] = b;
                indices[i++] = b; indices[i++] = c; indices[i++] = d;
            }
        }
        return (vertices, indices);
    }
}
