using System.Numerics;

using Paradise.Rendering.Pbr;

namespace SafariPark;

/// <summary>Accumulates box/quad parts into interleaved PBR vertex buffers, grouped by
/// material index so each group uploads as one <see cref="PbrPrimitive"/>.</summary>
/// <remarks>Animals and scenery are low-poly box assemblages; every part is a transformed
/// unit cube. Parts are keyed by material slot, merged per slot on <see cref="Build"/>.</remarks>
public sealed class MeshBuilder
{
    private const int FloatsPerVertex = 12; // pos3 / normal3 / uv2 / tangent4

    private readonly List<(int Material, float[] Vertices, uint[] Indices)> _parts = [];

    /// <summary>Adds an axis-aligned box part.</summary>
    /// <param name="material">Material slot resolved by the caller's material table.</param>
    /// <param name="halfExtents">Box half-size per axis.</param>
    /// <param name="center">Box center in local space.</param>
    /// <param name="rotZ">Optional roll around Z (rad), for angled necks and horns.</param>
    /// <param name="rotX">Optional pitch around X (rad).</param>
    /// <param name="rotY">Optional yaw around Y (rad).</param>
    public MeshBuilder AddBox(int material, Vector3 halfExtents, Vector3 center,
        float rotZ = 0f, float rotX = 0f, float rotY = 0f)
    {
        var model =
            Matrix4x4.CreateScale(halfExtents * 2f) *
            (rotZ != 0f ? Matrix4x4.CreateRotationZ(rotZ) : Matrix4x4.Identity) *
            (rotX != 0f ? Matrix4x4.CreateRotationX(rotX) : Matrix4x4.Identity) *
            (rotY != 0f ? Matrix4x4.CreateRotationY(rotY) : Matrix4x4.Identity) *
            Matrix4x4.CreateTranslation(center);

        var (srcV, srcI) = Procedural.UnitCube();
        var vertices = new float[srcV.Length];
        var normalM = PbrMath.NormalMatrix(in model);

        for (int i = 0; i < srcV.Length; i += FloatsPerVertex)
        {
            var pos = Vector3.Transform(new Vector3(srcV[i], srcV[i + 1], srcV[i + 2]), model);
            var nrm = Vector3.TransformNormal(new Vector3(srcV[i + 3], srcV[i + 4], srcV[i + 5]), normalM);
            var tng = Vector3.TransformNormal(new Vector3(srcV[i + 8], srcV[i + 9], srcV[i + 10]), model);
            vertices[i + 0] = pos.X; vertices[i + 1] = pos.Y; vertices[i + 2] = pos.Z;
            vertices[i + 3] = nrm.X; vertices[i + 4] = nrm.Y; vertices[i + 5] = nrm.Z;
            vertices[i + 6] = srcV[i + 6]; vertices[i + 7] = srcV[i + 7];
            vertices[i + 8] = tng.X; vertices[i + 9] = tng.Y; vertices[i + 10] = tng.Z; vertices[i + 11] = srcV[i + 11];
        }
        _parts.Add((material, vertices, srcI));
        return this;
    }

    /// <summary>Adds a caller-built mesh (disc, terrain patch) to a material group verbatim.</summary>
    public MeshBuilder AddRaw(int material, float[] vertices, uint[] indices)
    {
        _parts.Add((material, vertices, indices));
        return this;
    }

    /// <summary>Merges all parts per material slot into one vertex/index buffer each.</summary>
    /// <returns>One (vertices, indices, materialSlot) triple per distinct material used.</returns>
    public (float[] Vertices, uint[] Indices, int Material)[] Build()
    {
        var groups = new Dictionary<int, (List<float> V, List<uint> I)>();
        foreach (var (material, vertices, indices) in _parts)
        {
            if (!groups.TryGetValue(material, out var g)) groups[material] = g = ([], []);
            uint baseIndex = (uint)(g.V.Count / FloatsPerVertex);
            g.V.AddRange(vertices);
            foreach (var index in indices) g.I.Add(baseIndex + index);
        }

        var result = new (float[], uint[], int)[groups.Count];
        var i = 0;
        foreach (var (material, g) in groups)
            result[i++] = (g.V.ToArray(), g.I.ToArray(), material);
        return result;
    }
}
