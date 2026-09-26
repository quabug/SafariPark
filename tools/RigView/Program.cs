// Minimal skinned-rig render: one GLB at origin, headless WebGPU → PNG.
//   RigView alpaca.glb out.png [--anim Walk] [--frames N] [--scale 1]
using System.Numerics;
using Microsoft.Extensions.Logging.Abstractions;
using Paradise.Features;
using Paradise.Rendering.Pbr;
using Paradise.Rendering.WebGPU;
using SafariPark;

var glb = args[0];
var output = args.Length > 1 ? args[1] : "rigview.png";
var anim = ArgOf(args, "--anim");
var scale = ArgOf(args, "--scale") is { } s ? float.Parse(s) : 0f;
var frames = ArgOf(args, "--frames") is { } f ? int.Parse(f) : 60;
var noInst = args.Contains("--no-instancing");

var switches = new FeatureSwitches();
if (noInst) switches.Apply(FeatureOverrides.Parse("-rendering.instancing"));

using var renderer = WebGpuRenderer.CreateHeadless(640, 480, NullLogger.Instance);
using var pbr = new PbrRenderer(renderer, switches, 640, 480);
using var rig = GltfRig.Load(pbr, glb);
Console.WriteLine($"{glb}: parts={rig.Parts.Length} skins={rig.Skins.Length} clips=[{string.Join(", ", rig.Clips.Select(c => c.Name))}]");
{
    var rawMin = new Vector3(1e9f); var rawMax = new Vector3(-1e9f);
    foreach (var part in rig.Parts)
        foreach (var pr in part.Mesh.Primitives)
            if (pr.Skinned) { rawMin = Vector3.Min(rawMin, pr.LocalMin); rawMax = Vector3.Max(rawMax, pr.LocalMax); }
    Console.WriteLine($"rawVert y=[{rawMin.Y:F3}..{rawMax.Y:F3}] skinnedBounds y=[{rig.Min.Y:F3}..{rig.Max.Y:F3}]");
}

var scene = new PbrScene();
scene.Lights.Add(new PbrLight
{
    Direction = Vector3.Normalize(new Vector3(-0.4f, -0.8f, 0.45f)),
    Color = new Vector3(1f, 0.95f, 0.85f) * 3f,
    CastsShadows = false,
});
scene.Ambient = new PbrAmbient { Sky = new Vector3(0.4f, 0.5f, 0.55f), Ground = new Vector3(0.15f, 0.18f, 0.12f), Exposure = 1.2f };
scene.Tonemap = new PbrTonemap { Exposure = 1f };
scene.HasSkyBackground = true;
scene.SkyTopColor = new Vector3(0.2f, 0.35f, 0.5f);
scene.SkyHorizonColor = new Vector3(0.5f, 0.6f, 0.55f);
scene.SkyGroundHorizon = new Vector3(0.2f, 0.2f, 0.15f);
scene.SkyGroundBottom = new Vector3(0.05f, 0.06f, 0.05f);
if (noInst) scene.Instancing = new PbrInstancing { Enabled = false };

var rigScale = scale > 0 ? scale : 1f / Math.Max(0.01f, rig.Max.Y - rig.Min.Y);
var paletteNext = 0;
var visual = RigVisual.Create(rig, rigScale, ref paletteNext);
if (anim is { } an && rig.Clip(an) is { } clip && visual.Player is { } player)
    player.Play(clip);
visual.SetModel(Vector3.Zero, 0);
foreach (var p in visual.Parts) scene.Instances.Add(p.Instance);

var h = Math.Max(0.1f, rig.Max.Y - rig.Min.Y) * rigScale;
var eye = new Vector3(0f, h * 0.6f, h * 2.6f);
scene.Camera = new PbrCamera
{
    View = Matrix4x4.CreateLookAt(eye, new Vector3(0, h * 0.45f, 0), Vector3.UnitY),
    Projection = PbrMath.Perspective(MathF.PI / 3f, 640f / 480f, 0.05f, 200f),
    Position = eye,
};

for (var i = 0; i < frames; i++)
{
    visual.AdvanceAndPose(pbr, 1f / 60f);
    pbr.RenderFrame(scene);
}
var pixels = renderer.ReadbackColor(out var w, out var hh);
using var file = File.Create(output);
PngWriter.Write(file, new ColorReadback(pixels, w, hh), renderer.ColorFormat);
Console.WriteLine($"wrote {output} ({w}x{hh})");

static string? ArgOf(string[] a, string f)
{
    var i = Array.IndexOf(a, f);
    return i >= 0 && i + 1 < a.Length ? a[i + 1] : null;
}
