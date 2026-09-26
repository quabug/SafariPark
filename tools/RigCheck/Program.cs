using System.Numerics;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Paradise.Assets.Gltf;
using Paradise.Animation;
using Paradise.Animation.Offline;
using Paradise.BLOB;

var name = args.Length > 0 ? args[0] : "cat.glb";
var bytes = File.ReadAllBytes(Path.Combine("../../SafariPark.Core/Assets", name));
var jsonLen = BitConverter.ToInt32(bytes, 12);
var json = System.Text.Json.JsonDocument.Parse(bytes.AsMemory(20, jsonLen));
bool dirty = json.RootElement.TryGetProperty("buffers", out var buf) && buf.EnumerateArray().Any(b2 => b2.TryGetProperty("uri", out _));
byte[] data = bytes;
if (dirty)
{
    using var ms = new MemoryStream();
    using var w = new System.Text.Json.Utf8JsonWriter(ms);
    Strip(json.RootElement, w); w.Flush();
    var nj = ms.ToArray();
    while (nj.Length % 4 != 0) { Array.Resize(ref nj, nj.Length + 1); nj[^1] = 32; }
    var outLen = 12 + 8 + nj.Length + (bytes.Length - 20 - jsonLen);
    var o = new byte[outLen];
    bytes.AsSpan(0, 8).CopyTo(o);
    BitConverter.GetBytes(outLen).CopyTo(o, 8);
    BitConverter.GetBytes(nj.Length).CopyTo(o, 12);
    BitConverter.GetBytes(0x4E4F534A).CopyTo(o, 16);
    nj.CopyTo(o, 20);
    bytes.AsSpan(20 + jsonLen).CopyTo(o.AsSpan(20 + nj.Length));
    data = o;
}
var asset = GltfSceneReader.Read(data);
Console.WriteLine($"{name}: instances={asset.Instances.Length} skins={asset.Skins.Length} anims={asset.Animations.Length} nodes={asset.Nodes.Length}");

// JointOrder: depth-first over node tree
var children = new List<int>[asset.Nodes.Length];
for (var i = 0; i < children.Length; i++) children[i] = [];
var roots = new List<int>();
for (var i = 0; i < asset.Nodes.Length; i++)
{
    var p = asset.Nodes[i].ParentIndex;
    if (p < 0) roots.Add(i); else children[p].Add(i);
}
var jointOf = new int[asset.Nodes.Length]; Array.Fill(jointOf, -1);
var next = 0; var stack = new Stack<int>();
for (var r = roots.Count - 1; r >= 0; r--) stack.Push(roots[r]);
while (stack.Count > 0) { var n = stack.Pop(); jointOf[n] = next++; for (var c = children[n].Count - 1; c >= 0; c--) stack.Push(children[n][c]); }

// skeleton
var count = asset.Nodes.Length;
var names = new string[count]; var parents2 = new short[count]; var poses = new JointPose[count];
for (var node = 0; node < count; node++)
{
    var j = jointOf[node];
    names[j] = asset.Nodes[node].Name ?? "";
    parents2[j] = asset.Nodes[node].ParentIndex < 0 ? SkeletonBlob.NoParent : (short)jointOf[asset.Nodes[node].ParentIndex];
    poses[j] = new JointPose(asset.Nodes[node].RestTranslation, asset.Nodes[node].RestRotation, asset.Nodes[node].RestScale).WithNormalizedRotation();
}
var skeleton = SkeletonBlob.Create(names, parents2, poses);

// clips
var clips = new List<(string Name, NativeBlobAssetReference<AnimationBlob> Clip)>();
foreach (var clip in asset.Animations)
{
    var cname = clip.Name ?? "x"; var bar = cname.LastIndexOf('|'); if (bar >= 0) cname = cname[(bar + 1)..];
    var channels = clip.Channels.Select(c => new ClipChannelData(
        c.NodeIndex >= 0 ? jointOf[c.NodeIndex] : 0,
        c.Path switch { GltfAnimationPath.Translation => ChannelPath.Translation, GltfAnimationPath.Rotation => ChannelPath.Rotation, _ => ChannelPath.Scale },
        c.Step, c.Times, c.Values)).ToList();
    var raw = ClipConverter.ToRaw(new ClipData(cname, channels), ref skeleton.Value);
    clips.Add((cname, AnimationBuilder.Build(raw, iframeInterval: 1f)));
}
Console.WriteLine("clips: " + string.Join(", ", clips.Select(c => c.Name)));

// play Idle, evaluate
using var player = new AnimationPlayer(skeleton);
var idle = clips.FirstOrDefault(c => c.Name == "Idle").Clip;
if (idle is not null) player.Play(idle, 0f, true);
player.Advance(0.016f); player.Evaluate();
var models = player.ModelMatrices;
Console.WriteLine("model[0] (root):");
var m0 = models[0];
Console.WriteLine($"  {m0.M11:F3} {m0.M12:F3} {m0.M13:F3} {m0.M14:F3}");
Console.WriteLine($"  {m0.M41:F3} {m0.M42:F3} {m0.M43:F3} {m0.M44:F3}");

// palette for instance
var inst = asset.Instances[0];
var skin = asset.Skins[inst.SkinIndex];
var joints = skin.JointNodes.Select(n => jointOf[n]).ToArray();
var palette = new Matrix4x4[joints.Length];
SkinningPalette.Compute(models, joints, skin.InverseBindMatrices, -1, palette);
var prim = asset.Meshes[inst.MeshIndex].Primitives[0];
var jw = prim.JointsWeights!;
// skin vertex 0 + centroid over all verts
var acc3 = Vector3.Zero; var vmin = new Vector3(1e9f); var vmax = new Vector3(-1e9f);
for (var v = 0; v < prim.VertexCount; v++)
{
    var vo = v * GltfPrimitive.FloatsPerVertex;
    var p = new Vector3(prim.Vertices[vo], prim.Vertices[vo + 1], prim.Vertices[vo + 2]);
    var so = v * GltfPrimitive.SkinFloatsPerVertex;
    var acc = Vector3.Zero;
    for (var k = 0; k < 4; k++)
    {
        var wt = jw[so + 4 + k]; if (wt <= 0) continue;
        acc += Vector3.Transform(p, palette[(int)jw[so + k]]) * wt;
    }
    acc3 += acc / prim.VertexCount;
    vmin = Vector3.Min(vmin, acc); vmax = Vector3.Max(vmax, acc);
}
Console.WriteLine($"skinned centroid {acc3}  bbox {vmin} .. {vmax}");
foreach (var c in clips) c.Clip.Dispose();
skeleton.Dispose();

static void Strip(System.Text.Json.JsonElement el, System.Text.Json.Utf8JsonWriter w)
{
    if (el.ValueKind == System.Text.Json.JsonValueKind.Object)
    {
        w.WriteStartObject();
        foreach (var p in el.EnumerateObject())
        {
            if (p.NameEquals("uri") && p.Value.ValueKind == System.Text.Json.JsonValueKind.String && p.Value.GetString()!.StartsWith("data:")) continue;
            w.WritePropertyName(p.Name); Strip(p.Value, w);
        }
        w.WriteEndObject();
    }
    else if (el.ValueKind == System.Text.Json.JsonValueKind.Array)
    { w.WriteStartArray(); foreach (var i in el.EnumerateArray()) Strip(i, w); w.WriteEndArray(); }
    else el.WriteTo(w);
}

// --- texture sanity: transcode every embedded image and report sizes ---
Console.WriteLine("== images ==");
foreach (var img in asset.Images)
{
    var t = Paradise.Assets.Textures.Ktx2Transcoder.TranscodeToRgba32(img.Bytes, Paradise.Assets.Textures.CompressedTextureUsage.ColorSrgb);
    Console.WriteLine($"  image '{img.GetType().Name}' {img.Bytes.Length}B ktx2={Paradise.Assets.Textures.Ktx2Transcoder.IsKtx2(img.Bytes)} -> {(t.IsEmpty ? "EMPTY" : $"{t.Width}x{t.Height} {t.Format} mips={t.MipLevels.Length}")}");
}

// --- material sanity ---
Console.WriteLine("== materials ==");
foreach (var m in asset.Materials)
    Console.WriteLine($"  '{m.Name}' baseImg={m.BaseColorImage} color={m.BaseColorFactor} alpha={m.AlphaMode}");

// dump first image as PNG
if (asset.Images.Length > 0)
{
    var t = Paradise.Assets.Textures.Ktx2Transcoder.TranscodeToRgba32(asset.Images[0].Bytes, Paradise.Assets.Textures.CompressedTextureUsage.ColorSrgb);
    if (!t.IsEmpty)
    {
        var mip = t.MipLevels[0];
        var px = new byte[mip.Length];
        t.Data.AsSpan(mip.Offset, mip.Length).CopyTo(px);
        // RGBA -> write PGM-ish: dump a 4x4 grid of sampled colors
        for (var y = 0; y < 512; y += 128)
        {
            var line = "";
            for (var x = 0; x < 512; x += 128)
            {
                var o = (y * 512 + x) * 4;
                line += $"#{px[o]:X2}{px[o+1]:X2}{px[o+2]:X2} ";
            }
            Console.WriteLine("  row " + line);
        }
        File.WriteAllBytes("cat_rgba.bin", px);
        Console.WriteLine($"  wrote cat_rgba.bin {t.Width}x{t.Height}");
    }
}

// primitive attribute flags + uv spread
var p0 = asset.Meshes[asset.Instances[0].MeshIndex].Primitives[0];
Console.WriteLine($"flags: normals={p0.HasNormals} uv={p0.HasTexCoords} tangent={p0.HasTangents}");
var umin=9f; var umax=-9f; var vmin2=9f; var vmax2=-9f;
for (var v=0; v<p0.VertexCount; v++){ var o=v*GltfPrimitive.FloatsPerVertex; umin=Math.Min(umin,p0.Vertices[o+6]); umax=Math.Max(umax,p0.Vertices[o+6]); vmin2=Math.Min(vmin2,p0.Vertices[o+7]); vmax2=Math.Max(vmax2,p0.Vertices[o+7]); }
Console.WriteLine($"uv range u[{umin:F3}..{umax:F3}] v[{vmin2:F3}..{vmax2:F3}]");

// sample the texel the cat UVs land on
if (File.Exists("cat_rgba.bin"))
{
    var px = File.ReadAllBytes("cat_rgba.bin");
    var u = 0.5f; var v2 = 0.844f;
    var x = (int)(u * 512); var y = (int)(v2 * 512);
    var o = (y * 512 + x) * 4;
    Console.WriteLine($"texel@({x},{y}) = #{px[o]:X2}{px[o+1]:X2}{px[o+2]:X2}");
}

// write cat_rgba.bin as png
if (File.Exists("cat_rgba.bin"))
{
    var px = File.ReadAllBytes("cat_rgba.bin");
    using var img2 = SixLabors.ImageSharp.Image.LoadPixelData<SixLabors.ImageSharp.PixelFormats.Rgba32>(px, 512, 512);
    img2.Save("cat_tex.png", new SixLabors.ImageSharp.Formats.Png.PngEncoder());
    Console.WriteLine("cat_tex.png written");
}
