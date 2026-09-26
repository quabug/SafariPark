using System.Numerics;

using Paradise.Features;
using Paradise.Rendering;
using Paradise.Rendering.Pbr;
using Paradise.Windowing;

namespace SafariPark;

/// <summary>The whole park: ECS animals, the PBR scene they render into, the third-person
/// camera, and the HUD. One instance owns the simulation for both windowed and headless
/// hosts; the host only pumps events and calls <see cref="Update"/> +
/// <see cref="RenderFrame"/>.</summary>
public sealed class SafariGame : IDisposable
{
    private readonly IRenderer _renderer;
    private readonly PbrRenderer _pbr;
    private readonly PbrScene _scene = new();
    private readonly ParkMaterials _materials;
    private readonly Func<string, byte[]> _readAsset;

    private readonly SharedWorld _sharedWorld;
    private readonly World _world;
    private readonly Random _random = new(20260925);

    // Visual handle per animal; index is the HUD roster order.
    private readonly List<AnimalVisual> _animals = [];
    private int _controlled = -1;

    // Joint-palette bump allocator: every skinned part instance owns a slice. Wanderers
    // come and go but the budget is wide enough that leases need not recycle.
    private int _paletteNext;

    /// <summary>QA kill switch: force skinned rigs onto the rigid path (bind pose) so a
    /// screenshot can isolate GPU skinning from upload/geometry bugs.</summary>
    public static bool NoSkinning;

    private uint _width;
    private uint _height;
    private float _elapsed;

    // Third-person camera: yaw/pitch orbit around the controlled animal.
    private float _camYaw = 0.9f;
    private float _camPitch = 0.32f;
    private float _camDistance = 6f;
    private Vector3 _camEye;
    private bool _camInitialized;
    private Matrix4x4 _view, _proj;

    // Click-vs-drag discrimination for left-button picking.
    private bool _lmbDown;
    private Vector2 _lmbDownPos;
    private float _lmbDownTime;
    private const float PickMaxDragPixels = 6f;
    private const float PickMaxSeconds = 0.35f;

    /// <summary>Keys currently held — fed by the host's event pump via <see cref="OnEvent"/>.</summary>
    private readonly HashSet<KeyboardKey> _keys = [];

    private Vector2 _moveInput;
    private bool _runHeld;
    private bool _jumpQueued;

    private sealed class AnimalVisual
    {
        public required Entity Entity;
        public required RigVisual Rig;
        public required Species Species;
        public required string Label;
        public float Flinch;       // hit-flinch seconds left
        public float IdleTimer;    // wander-idle seconds, for Eat/Idle2 picks

        public IEnumerable<PbrInstance> Instances => Rig.Parts.Select(p => p.Instance);
    }

    // ------------------------------------------------------------------ forest gameplay state

    // Tree trunks the cat can cling to + perch surfaces (tree crowns, stumps).
    private readonly List<Tree> _trees = [];
    private readonly List<Perch> _perches = [];

    // Roaming extras (extra critters and human visitors passing through).
    private readonly List<Wanderer> _wanderers = [];
    private float _wandererTimer = 6f;
    private const int MaxWanderers = 5;

    // Ground pickups + the carried/thrown one.
    private readonly List<DropItem> _items = [];
    private PbrInstance? _held;
    private int _heldType;
    private ThrownItem? _thrown;
    private float _itemTimer = 2f;
    private float _hitTimer;
    private const int MaxItems = 12;
    private const float PickupRadius = 0.6f;

    // Cat tree-climbing: the trunk being clung to, plus the climb flag itself.
    private Tree? _clingTree;

    private GltfRig[] _speciesRigs = [];
    private PbrMesh _duckMesh = new([]);
    private GltfRig[] _sceneryRigs = [];
    private PbrMesh[] _itemMeshes = [];

    private sealed class Tree
    {
        public required float X;
        public required float Z;
        public required float TrunkTop;   // max cling height (world Y)
        public required float PerchTop;   // crown surface Y the cat can sit on
        public required float PerchRadius;
        public required bool Climbable;
    }

    /// <summary>Flat standable surface — stump top or broad crown center.</summary>
    private struct Perch
    {
        public required float X;
        public required float Z;
        public required float Radius;
        public required float Top;
    }

    private sealed class Wanderer
    {
        public required Entity Entity;
        public required RigVisual Rig;
        public required Species Species;
        public float IdleTimer;
        public float LifeTimer;
        public bool Downed;        // knocked flat by a thrown item
        public float DownTimer;
    }

    private sealed class DropItem
    {
        public required PbrInstance Instance;
        public required int Type;
        public required Vector3 Pos;
    }

    private sealed class ThrownItem
    {
        public required PbrInstance Instance;
        public required int Type;
        public required Vector3 Pos;
        public required Vector3 Vel;
        public required float Spin;
    }

    /// <param name="renderer">Any engine renderer backend — WebGpuRenderer on desktop,
    /// BrowserRenderer on web.</param>
    /// <param name="switches">Engine feature switches; the host builds them once.</param>
    public SafariGame(IRenderer renderer, FeatureSwitches switches, uint width, uint height,
        Microsoft.Extensions.Logging.ILogger? logger = null, Func<string, byte[]>? readAsset = null)
    {
        _renderer = renderer;
        _width = Math.Max(1, width);
        _height = Math.Max(1, height);
        _pbr = new PbrRenderer(renderer, switches, _width, _height, logger: logger);
        _materials = new ParkMaterials(_pbr);
        _readAsset = readAsset ?? (name =>
        {
            // Assets sit beside the binary; fall back to CWD for runs from the project dir.
            var besideBinary = Path.Combine(AppContext.BaseDirectory, "Assets", name);
            return File.ReadAllBytes(File.Exists(besideBinary) ? besideBinary : Path.Combine("Assets", name));
        });

        _sharedWorld = SharedWorldFactory.Create();
        _world = _sharedWorld.CreateWorld();

        BuildEnvironment();
        BuildAnimals();
        Control(0);
    }
    private void BuildEnvironment()
    {
        var scenery = new MeshBuilder();
        // Slots index into the procedural mats below.
        const int sDirt = 5, sStump = 6;

        // Ground: the heightfield itself, shaded by slope.
        var (gv, gi) = Terrain.BuildMesh();
        _scene.Instances.Add(new PbrInstance
        {
            Mesh = new PbrMesh([_pbr.UploadPrimitive(gv, gi, _materials.ForestFloor)]),
            Model = Matrix4x4.Identity,
        });


        var rand = new Random(4242);

        // Scenery GLBs (Quaternius nature pack): one rig per asset, instanced everywhere.
        var pineRigs = LoadSceneryRigs("pine1.glb", "pine2.glb", "pine3.glb");
        var twistRigs = LoadSceneryRigs("twist1.glb", "twist2.glb", "twist3.glb");
        var rockRigs = LoadSceneryRigs("rock1.glb", "rock2.glb", "rock3.glb");
        var plantRigs = LoadSceneryRigs("plant.glb", "plantbig.glb");
        _sceneryRigs = [.. pineRigs, .. twistRigs, .. rockRigs, .. plantRigs];

        // Keep the origin clearing and the pond free of trunks so the cat and ducks spawn dry.
        bool OpenSpot(float x, float z) =>
            x * x + z * z > 4.5f * 4.5f && !Terrain.IsWater(x, z);

        void Place(GltfRig rig, float x, float z, float scale, float yaw)
        {
            var y = Terrain.Height(x, z);
            var model = Matrix4x4.CreateScale(scale) * Matrix4x4.CreateRotationY(yaw)
                * Matrix4x4.CreateTranslation(new Vector3(x, y - rig.Min.Y * scale, z));
            foreach (var part in rig.Parts)
                _scene.Instances.Add(new PbrInstance { Mesh = part.Mesh, Model = model });
        }

        // Conifers: Quaternius pines (climbable by the cat).
        void Pine(float x, float z, float scale)
        {
            var rig = pineRigs[rand.Next(pineRigs.Length)];
            Place(rig, x, z, scale, (float)rand.NextDouble() * MathF.Tau);
            float top = Terrain.Height(x, z) + rig.Max.Y * scale;
            _trees.Add(new Tree
            {
                X = x, Z = z, TrunkTop = top - 0.6f,
                PerchTop = top * 0.62f + Terrain.Height(x, z) * 0.38f,
                PerchRadius = 0.8f * scale, Climbable = true,
            });
            _perches.Add(new Perch { X = x, Z = z, Radius = 0.7f * scale, Top = top * 0.62f + Terrain.Height(x, z) * 0.38f });
        }

        // Twisted broadleafs — taller, also climbable.
        void Twist(float x, float z)
        {
            var rig = twistRigs[rand.Next(twistRigs.Length)];
            float scale = 0.45f + (float)rand.NextDouble() * 0.20f;
            Place(rig, x, z, scale, (float)rand.NextDouble() * MathF.Tau);
            float top = Terrain.Height(x, z) + rig.Max.Y * scale;
            _trees.Add(new Tree
            {
                X = x, Z = z, TrunkTop = top - 1.0f,
                PerchTop = Terrain.Height(x, z) + rig.Max.Y * scale * 0.55f,
                PerchRadius = 1.1f * scale, Climbable = true,
            });
            _perches.Add(new Perch { X = x, Z = z, Radius = 1.0f * scale, Top = Terrain.Height(x, z) + rig.Max.Y * scale * 0.55f });
        }

        // Dense forest interior: mostly pines, some twisted trees.
        for (int i = 0; i < 300; i++)
        {
            float x = (float)(rand.NextDouble() * 2 - 1) * (Terrain.HalfSize - 6f);
            float z = (float)(rand.NextDouble() * 2 - 1) * (Terrain.HalfSize - 6f);
            if (!OpenSpot(x, z)) { i--; continue; }
            if (rand.NextDouble() < 0.7f)
                Pine(x, z, 0.7f + (float)rand.NextDouble() * 0.45f);
            else
                Twist(x, z);
        }

        // Treeline wall hides the world edge.
        for (int i = 0; i < 110; i++)
        {
            float a = i / 110f * MathF.Tau + (float)rand.NextDouble() * 0.06f;
            float r = Terrain.HalfSize - 2.5f - (float)rand.NextDouble() * 4f;
            Pine(MathF.Cos(a) * r, MathF.Sin(a) * r, 1.0f + (float)rand.NextDouble() * 0.3f);
        }

        // Undergrowth: plants, rocks, and jumpable stumps (stumps stay procedural boxes —
        // no low stump GLB in the set, and the cat needs flat perches near the ground).
        for (int i = 0; i < 120; i++)
        {
            float x = (float)(rand.NextDouble() * 2 - 1) * (Terrain.HalfSize - 7f);
            float z = (float)(rand.NextDouble() * 2 - 1) * (Terrain.HalfSize - 7f);
            if (!OpenSpot(x, z)) continue;
            var rig = plantRigs[rand.Next(plantRigs.Length)];
            Place(rig, x, z, 0.55f + (float)rand.NextDouble() * 0.6f, (float)rand.NextDouble() * MathF.Tau);
        }
        for (int i = 0; i < 18; i++)
        {
            float x = (float)(rand.NextDouble() * 2 - 1) * (Terrain.HalfSize - 7f);
            float z = (float)(rand.NextDouble() * 2 - 1) * (Terrain.HalfSize - 7f);
            if (!OpenSpot(x, z)) continue;
            float y = Terrain.Height(x, z);
            float h = 0.35f + (float)rand.NextDouble() * 0.35f;
            float r = 0.22f + (float)rand.NextDouble() * 0.15f;
            scenery.AddBox(sStump, new Vector3(r, h / 2f, r), new Vector3(x, y + h / 2f, z),
                rotY: (float)rand.NextDouble() * MathF.PI);
            _perches.Add(new Perch { X = x, Z = z, Radius = r * 0.85f, Top = y + h });
        }
        for (int i = 0; i < 26; i++)
        {
            float x = (float)(rand.NextDouble() * 2 - 1) * (Terrain.HalfSize - 7f);
            float z = (float)(rand.NextDouble() * 2 - 1) * (Terrain.HalfSize - 7f);
            if (!OpenSpot(x, z)) continue;
            var rig = rockRigs[rand.Next(rockRigs.Length)];
            Place(rig, x, z, 0.4f + (float)rand.NextDouble() * 0.5f, (float)rand.NextDouble() * MathF.Tau);
        }
        for (int i = 0; i < 14; i++) // bare-earth patches under the canopy
        {
            float x = (float)(rand.NextDouble() * 2 - 1) * (Terrain.HalfSize - 12f);
            float z = (float)(rand.NextDouble() * 2 - 1) * (Terrain.HalfSize - 12f);
            if (Terrain.IsWater(x, z)) continue;
            scenery.AddBox(sDirt, new Vector3(1.4f + (float)rand.NextDouble(), 0.05f, 1.1f + (float)rand.NextDouble()),
                new Vector3(x, Terrain.Height(x, z) + 0.04f, z), rotY: (float)rand.NextDouble() * MathF.PI);
        }

        // Pond rim rocks.
        for (int i = 0; i < 10; i++)
        {
            float a = i / 10f * MathF.Tau;
            float x = Terrain.PondCenter.X + MathF.Cos(a) * (Terrain.PondRadius + 0.4f);
            float z = Terrain.PondCenter.Z + MathF.Sin(a) * (Terrain.PondRadius + 0.4f);
            var rig = rockRigs[rand.Next(rockRigs.Length)];
            Place(rig, x, z, 0.18f + (float)rand.NextDouble() * 0.12f, a + 0.3f);
        }

        foreach (var (v, idx, slot) in scenery.Build())
        {
            _scene.Instances.Add(new PbrInstance
            {
                Mesh = new PbrMesh([_pbr.UploadPrimitive(v, idx, slot == sDirt ? _materials.Dirt : _materials.Stump)]),
                Model = Matrix4x4.Identity,
            });
        }

        // Throwable pickups.
        _itemMeshes = BuildItemMeshes();

        // Water: a translucent disc over the pond bowl.
        var (wv, wi) = Disc(Terrain.PondCenter, Terrain.PondRadius * 0.97f, Terrain.WaterLevel, 48);
        _scene.Instances.Add(new PbrInstance
        {
            Mesh = new PbrMesh([_pbr.UploadPrimitive(wv, wi, _materials.Water)]),
            Model = Matrix4x4.Identity,
        });

        // Dimmed forest sun: dappled, cooler, still casting hard-enough shadows for depth.
        _scene.Lights.Add(new PbrLight
        {
            Type = PbrLightType.Directional,
            Direction = Vector3.Normalize(new Vector3(0.55f, 1.0f, 0.35f)),
            Color = new Vector3(0.95f, 0.92f, 0.80f),
            Intensity = 1.35f,
            CastsShadows = true,
            ShadowResolution = 2048,
            ShadowAngularDiameter = 0.5f,
        });
        _scene.Ambient = new PbrAmbient
        {
            Sky = new Vector3(0.15f, 0.19f, 0.24f),
            Equator = new Vector3(0.11f, 0.12f, 0.08f),
            Ground = new Vector3(0.05f, 0.06f, 0.04f),
        };
        _scene.Fog = new PbrFog
        {
            Enabled = true,
            Density = 0.011f,
            Color = new Vector3(0.34f, 0.42f, 0.38f),
            StartDistance = 18f,
            MaxDistance = 130f,
        };
        _scene.HasSkyBackground = true;
        _scene.SkyTopColor = new Vector3(0.18f, 0.32f, 0.42f);
        _scene.SkyHorizonColor = new Vector3(0.42f, 0.52f, 0.48f);
        _scene.SkyGroundHorizon = new Vector3(0.16f, 0.18f, 0.12f);
        _scene.SkyGroundBottom = new Vector3(0.07f, 0.08f, 0.05f);
        _scene.SkyReflections = true;
        _scene.SkySunEnabled = true;
        _scene.SkySunDirection = _scene.Lights[0].Direction;
        _scene.SkySunColorEnergy = new Vector3(0.95f, 0.92f, 0.80f) * 3f;
        _scene.Tonemap = new PbrTonemap { Exposure = 1.0f };
        _scene.Fxaa = new PbrFxaa { Enabled = true };
        _scene.Ssao = new PbrSsao { Enabled = true, Radius = 0.8f, Intensity = 1.4f };
        _scene.ContactShadows = new PbrContactShadows { Enabled = true };
    }

    /// <summary>Loads scenery GLBs: rigid rigs, no skeleton needed — Parts upload baked.</summary>
    private GltfRig[] LoadSceneryRigs(params string[] names)
    {
        var rigs = new GltfRig[names.Length];
        for (var i = 0; i < names.Length; i++)
            rigs[i] = GltfRig.Load(_pbr, _readAsset(names[i]), names[i]);
        return rigs;
    }



    /// <summary>Small throwable meshes: pine cone, acorn, stick, stone. Material comes from
    /// the meshes' single slot each.</summary>
    private PbrMesh[] BuildItemMeshes()
    {
        var mats = new[] { _materials.Cone, _materials.Cone, _materials.Stick, _materials.Rock };
        var meshes = new PbrMesh[4];
        // Pine cone: stubby egg-ish stack.
        var cone = new MeshBuilder()
            .AddBox(0, new Vector3(0.035f, 0.05f, 0.035f), Vector3.Zero)
            .AddBox(0, new Vector3(0.025f, 0.03f, 0.025f), new Vector3(0f, 0.07f, 0f));
        // Acorn: body plus a darker cap look (same material, thinner top box).
        var acorn = new MeshBuilder()
            .AddBox(0, new Vector3(0.03f, 0.035f, 0.03f), Vector3.Zero)
            .AddBox(0, new Vector3(0.038f, 0.015f, 0.038f), new Vector3(0f, 0.045f, 0f));
        // Stick.
        var stick = new MeshBuilder()
            .AddBox(0, new Vector3(0.018f, 0.018f, 0.16f), Vector3.Zero, rotY: 0.3f);
        // Stone: two overlapped lumps.
        var stone = new MeshBuilder()
            .AddBox(0, new Vector3(0.045f, 0.03f, 0.04f), Vector3.Zero, rotY: 0.4f)
            .AddBox(0, new Vector3(0.03f, 0.025f, 0.03f), new Vector3(0.02f, 0.03f, -0.01f));
        var builders = new[] { cone, acorn, stick, stone };
        for (int i = 0; i < meshes.Length; i++)
        {
            var parts = builders[i].Build();
            var primitives = new PbrPrimitive[parts.Length];
            for (int p = 0; p < parts.Length; p++)
                primitives[p] = _pbr.UploadPrimitive(parts[p].Vertices, parts[p].Indices, mats[i]);
            meshes[i] = new PbrMesh(primitives);
        }
        return meshes;
    }

    /// <summary>Flat horizontal disc mesh (the pond surface).</summary>
    private static (float[] Vertices, uint[] Indices) Disc(Vector3 center, float radius, float y, int segments)
    {
        var vertices = new float[(segments + 1) * 12];
        var indices = new uint[segments * 3];
        float[] cv = [center.X, y, center.Z, 0f, 1f, 0f, 0.5f, 0.5f, 1f, 0f, 0f, 1f];
        cv.CopyTo(vertices, 0);
        for (int s = 0; s < segments; s++)
        {
            float a = s / (float)segments * MathF.Tau;
            int o = (s + 1) * 12;
            vertices[o + 0] = center.X + MathF.Cos(a) * radius;
            vertices[o + 1] = y;
            vertices[o + 2] = center.Z + MathF.Sin(a) * radius;
            vertices[o + 3] = 0f; vertices[o + 4] = 1f; vertices[o + 5] = 0f;
            vertices[o + 6] = MathF.Cos(a) * 0.5f + 0.5f; vertices[o + 7] = MathF.Sin(a) * 0.5f + 0.5f;
            vertices[o + 8] = 1f; vertices[o + 9] = 0f; vertices[o + 10] = 0f; vertices[o + 11] = 1f;
            indices[s * 3 + 0] = 0;
            indices[s * 3 + 1] = (uint)(s + 1);
            indices[s * 3 + 2] = (uint)((s + 1) % segments + 1);
        }
        return (vertices, indices);
    }

    private void BuildAnimals()
    {
        // Upload each GLB rig once; every animal instance reuses the skeleton and buffers.
        _speciesRigs = new GltfRig?[SpeciesCatalog.All.Length].Select(r => r!).ToArray();
        for (var s = 0; s < _speciesRigs.Length; s++)
        {
            var model = SpeciesCatalog.All[s].Model;
            if (model is null) continue;
            _speciesRigs[s] = GltfRig.Load(_pbr, _readAsset(model), model);
        }
        // The duck's procedural box body — no animated bird GLB in the set.
        var duckParts = SpeciesCatalog.BuildMesh(Species.Duck);
        var duckPrimitives = new PbrPrimitive[duckParts.Length];
        for (var p = 0; p < duckParts.Length; p++)
            duckPrimitives[p] = _pbr.UploadPrimitive(duckParts[p].Vertices, duckParts[p].Indices,
                _materials.DuckCoat[duckParts[p].Material]);
        _duckMesh = new PbrMesh(duckPrimitives);

        (Species Species, int Count)[] roster =
        [
            (Species.Cat, 1),
            (Species.Deer, 2), (Species.Stag, 1), (Species.Alpaca, 2), (Species.Horse, 1),
            (Species.Fox, 2), (Species.Wolf, 1), (Species.Dog, 1),
            (Species.Rabbit, 3), (Species.Ranger, 1),
            (Species.Duck, 2),
        ];

        foreach (var (species, count) in roster)
        {
            var info = SpeciesCatalog.Of(species);
            var herd = PickSpawnCenter(species);
            for (int i = 0; i < count; i++)
            {
                var pos = species == Species.Cat
                    ? Vector3.Zero // the player's cat starts in the central clearing
                    : herd + new Vector3(
                        (float)(_random.NextDouble() * 2 - 1) * 6f, 0f,
                        (float)(_random.NextDouble() * 2 - 1) * 6f);
                // Water species belong in the pond; pull strays into the shallow rim.
                if (info.Water && !Terrain.IsWater(pos.X, pos.Z))
                {
                    var toPond = Terrain.PondCenter - pos; toPond.Y = 0f;
                    pos = Terrain.PondCenter - Vector3.Normalize(toPond) * (Terrain.PondRadius * 0.5f);
                }
                else if (!info.Water && Terrain.IsWater(pos.X, pos.Z))
                {
                    pos = herd; // back off to the dry herd anchor
                }
                pos.Y = Terrain.Height(pos.X, pos.Z);

                var entity = _world.CreateEntity(EntityBuilder.Create()
                    .Add(new AnimalKind { Species = species, NameIndex = _animals.Count + 1 })
                    .Add(new AnimalPose { Position = pos, Yaw = (float)_random.NextDouble() * MathF.Tau })
                    .Add(new AnimalAir { Grounded = true })
                    .Add(new AnimalMotion())
                    .Add(new AnimalBrain { State = WanderState.Idle, Timer = (float)_random.NextDouble() * 3f }));

                var rig = CreateVisual(species);
                _animals.Add(new AnimalVisual
                {
                    Entity = entity,
                    Rig = rig,
                    Species = species,
                    Label = $"{info.ChineseName} {_animals.Count + 1}",
                });
                foreach (var instance in _animals[^1].Instances)
                    _scene.Instances.Add(instance);
                PlayAnim(rig, info, AnimKey.Idle);
            }
        }

        _roster = _animals.Select(a => (a.Label, SpeciesCatalog.Of(a.Species).Name)).ToArray();
    }

    /// <summary>One placed animal: GLB rig scaled to the species' target height, or the
    /// procedural duck mesh wrapped in a rigid RigVisual.</summary>
    private RigVisual CreateVisual(Species species)
    {
        var info = SpeciesCatalog.Of(species);
        if (info.Model is null)
        {
            // Procedural duck: a rigid rig with a single part.
            var parts = new[] { new RigVisual.Part
            {
                Instance = new PbrInstance { Mesh = _duckMesh, JointOffset = -1 },
                JointOffset = -1, JointCount = 0, SkinIndex = -1,
            }};
            return new RigVisual
            {
                Rig = _speciesRigs[(int)species] ?? DuckRigPlaceholder(),
                Player = null, Parts = parts,
                Scale = info.Height / Math.Max(0.2f, info.HalfHeight * 2f),
                GroundOffset = 0f,
                YawOffset = 0f,
            };
        }

        var rig = _speciesRigs[(int)species];
        var scale = info.Height / Math.Max(0.05f, rig.Max.Y - rig.Min.Y);
        var visual = RigVisual.Create(rig, scale, ref _paletteNext);
        visual.YawOffset = info.YawOffset;
        return visual;
    }

    /// <summary>Procedural species share one fake rig holder so visuals stay uniform.</summary>
    private GltfRig DuckRigPlaceholder()
    {
        var existing = _speciesRigs[(int)Species.Duck];
        if (existing is not null) return existing;
        // Minimal rig: no skeleton, no clips — Parts unused by a procedural RigVisual.
        return _speciesRigs[(int)Species.Duck] = new GltfRig
        {
            Parts = [], Skins = [], Skeleton = null, Clips = [],
            Min = Vector3.Zero, Max = Vector3.UnitY * 0.4f, Path = "procedural-duck",
        };
    }

    /// <summary>Picks a clip for a state: the species' explicit mapping, else a name substring.</summary>
    private void PlayAnim(RigVisual rig, SpeciesInfo info, AnimKey key,
        bool loop = true, float fade = 0.22f)
    {
        if (rig.Player is null) return;
        string? name = null;
        if (info.Clips is { } map) map.TryGetValue(key, out name);
        var clip = name is null ? null : rig.Rig.Clip(name) ?? rig.Rig.Clip(DefaultClipNames(key));
        clip ??= rig.Rig.Clip(DefaultClipNames(key));
        if (clip is null) return;
        rig.Player.Play(clip, fadeSeconds: fade, loop: loop);
        rig.CurrentClip = name ?? DefaultClipNames(key);
    }

    private static string DefaultClipNames(AnimKey key) => key switch
    {
        AnimKey.Idle => "Idle", AnimKey.Walk => "Walk", AnimKey.Run => "Run",
        AnimKey.Jump => "Jump", AnimKey.Death => "Death", AnimKey.Hit => "HitReact",
        _ => "Eating",
    };

    /// <summary>Group anchor: ducks at the pond, others scattered across the floor.</summary>
    private Vector3 PickSpawnCenter(Species species)
    {
        var info = SpeciesCatalog.Of(species);
        for (int tries = 0; tries < 30; tries++)
        {
            float x = (float)(_random.NextDouble() * 2 - 1) * (Terrain.HalfSize - 12f);
            float z = (float)(_random.NextDouble() * 2 - 1) * (Terrain.HalfSize - 12f);
            if (Terrain.IsWater(x, z) == info.Water) return new Vector3(x, 0f, z);
        }
        return info.Water
            ? Terrain.PondCenter + new Vector3(Terrain.PondRadius * 0.6f, 0f, 0f)
            : Vector3.Zero;
    }

    // ------------------------------------------------------------------ input

    /// <summary>Forwards a window event to the game. The host calls this AFTER the UI input
    /// handler; UI-consumed events never reach here.</summary>
    public void OnEvent(in WindowEvent e)
    {
        switch (e.Kind)
        {
            case WindowEventKind.Button when e.Source == EventSource.Keyboard:
                if (e.Pressed) _keys.Add(e.KeyboardKey);
                else _keys.Remove(e.KeyboardKey);
                if (e.Pressed && e.KeyboardKey == KeyboardKey.Tab) Switch(1);
                else if (e.Pressed && e.KeyboardKey == KeyboardKey.Q) Switch(-1);
                else if (e.Pressed && e.KeyboardKey == KeyboardKey.Space) _jumpQueued = true;
                else if (e.Pressed && e.KeyboardKey == KeyboardKey.E) TryInteract();
                else if (e.Pressed && e.KeyboardKey == KeyboardKey.F) ThrowHeld();
                break;

            case WindowEventKind.Button when e.Source == EventSource.Mouse:

                if (e.PointerButton == PointerButton.Left)
                {
                    if (e.Pressed)
                    {
                        _lmbDown = true;
                        _lmbDownPos = new Vector2(e.X, e.Y);
                        _lmbDownTime = _elapsed;
                    }
                    else if (_lmbDown)
                    {
                        _lmbDown = false;
                        var moved = Vector2.Distance(_lmbDownPos, new Vector2(e.X, e.Y));
                        // Short, still press = pick; longer or moved = was a camera drag.
                        if (moved < PickMaxDragPixels && _elapsed - _lmbDownTime < PickMaxSeconds)
                            TryPick(new Vector2(e.X, e.Y));
                    }
                }
                break;

            case WindowEventKind.Scroll:
                _camDistance = Math.Clamp(_camDistance * (1f - e.Y * 0.12f), 2f, 30f);
                break;
        }
    }

    /// <summary>Pointer drag in pixels — PointerMove carries absolute positions, so the host
    /// diffs successive events and forwards the delta here.</summary>
    public void OnDrag(float dx, float dy)
    {
        if (!_lmbDown) return;
        _camYaw += dx * 0.01f;
        _camPitch = Math.Clamp(_camPitch + dy * 0.008f, -0.15f, 1.35f);
    }

    // ------------------------------------------------------------------ control

    /// <summary>Hand control to roster index <paramref name="index"/> (wraps).</summary>
    public void Control(int index)
    {
        if (_animals.Count == 0) return;
        index = (index % _animals.Count + _animals.Count) % _animals.Count;
        if (index == _controlled) return;

        if (_controlled >= 0)
        {
            var old = _animals[_controlled];
            if (_world.IsAlive(old.Entity))
            {
                _world.RemoveTag<PlayerControlled>(old.Entity);
                SetHighlight(old, 0f);
                _clingTree = null;
                // A carried item is left at the old animal's feet, not teleported with control.
                if (_held is not null)
                {
                    ref readonly var oldPose = ref _world.GetComponent<AnimalPose>(old.Entity);
                    var it = new DropItem
                    {
                        Instance = _held, Type = _heldType,
                        Pos = oldPose.Position + new Vector3(0f, 0.06f, 0f),
                    };
                    it.Instance.Model = Matrix4x4.CreateTranslation(it.Pos);
                    _items.Add(it);
                    _held = null;
                }
                // Hand the animal back to its wander brain.
                ref var brain = ref _world.GetComponent<AnimalBrain>(old.Entity);
                brain.State = WanderState.Idle;
                brain.Timer = 0.5f;
            }
        }

        _controlled = index;
        var animal = _animals[_controlled];
        _world.AddTag<PlayerControlled>(animal.Entity);
        SetHighlight(animal, 0.18f);
        ControlledChanged?.Invoke(index);
        _camDistance = SpeciesCatalog.Of(animal.Species).CameraDistance;
    }

    /// <summary>Cycle control by <paramref name="delta"/> through the roster.</summary>
    public void Switch(int delta) => Control(_controlled + delta);

    /// <summary>Jump control to an absolute roster index — used by --control for headless shots.</summary>
    public void ControlIndex(int index) => Control(index);

    /// <summary>Public pick entry for hosts that deliver clicks without a full event stream
    /// (the browser host).</summary>
    public void ClickPick(float x, float y) => TryPick(new Vector2(x, y));

    /// <summary>Left-click animal pick: ray vs a sphere at each animal's chest height.</summary>
    private void TryPick(Vector2 screen)
    {
        if (!PbrMath.TryScreenPointToRay(screen, new Vector2(_width, _height), _view * _proj,
            out var origin, out var dir)) return;

        int best = -1;
        float bestT = float.MaxValue;
        for (int i = 0; i < _animals.Count; i++)
        {
            var a = _animals[i];
            var info = SpeciesCatalog.Of(a.Species);
            ref readonly var pose = ref _world.GetComponent<AnimalPose>(a.Entity);
            var center = pose.Position + new Vector3(0f, info.HalfHeight * 0.8f, 0f);
            float t = Vector3.Dot(center - origin, dir);
            if (t < 0f || t > 150f) continue;
            float distSq = Vector3.DistanceSquared(origin + dir * t, center);
            float r = info.PickRadius;
            if (distSq < r * r && t < bestT) { bestT = t; best = i; }
        }
        if (best >= 0) Control(best);
    }

    // ------------------------------------------------------------------ update

    public void Update(float dt)
    {
        _elapsed += dt;
        dt = MathF.Min(dt, 0.1f);

        _moveInput = new Vector2(
            (_keys.Contains(KeyboardKey.D) ? 1f : 0f) - (_keys.Contains(KeyboardKey.A) ? 1f : 0f),
            (_keys.Contains(KeyboardKey.W) ? 1f : 0f) - (_keys.Contains(KeyboardKey.S) ? 1f : 0f));
        _runHeld = _keys.Contains(KeyboardKey.LeftShift) || _keys.Contains(KeyboardKey.RightShift);

        var controlledEntity = _controlled >= 0 ? _animals[_controlled].Entity : default;
        foreach (var visual in _animals)
        {
            ref var pose = ref _world.GetComponent<AnimalPose>(visual.Entity);
            ref var motion = ref _world.GetComponent<AnimalMotion>(visual.Entity);
            ref var air = ref _world.GetComponent<AnimalAir>(visual.Entity);
            var info = SpeciesCatalog.Of(visual.Species);

            bool isCatPlayer = visual.Entity.Equals(controlledEntity) && visual.Species == Species.Cat;
            if (visual.Entity.Equals(controlledEntity))
                TickPlayer(ref pose, ref motion, ref air, info, dt, isCatPlayer);
            else
                TickWander(visual.Entity, ref pose, ref motion, info, dt);

            bool clinging = isCatPlayer && _clingTree is not null;

            // Vertical: world-Y gravity against terrain or a perch platform.
            if (!clinging)
            {
                float support = SupportHeight(pose.Position.X, pose.Position.Y, pose.Position.Z);
                // Water species float at the waterline instead of resting on the pond floor.
                if (info.Water && support < Terrain.WaterLevel - 0.15f)
                    support = Terrain.WaterLevel - 0.15f;

                if (air.Grounded && pose.Position.Y > support + 0.15f)
                {
                    // Walked off a perch or slope lip — start falling from current height.
                    air.Grounded = false;
                    air.VerticalVelocity = 0f;
                }

                if (!air.Grounded)
                {
                    air.VerticalVelocity -= 22f * dt;
                    pose.Position.Y += air.VerticalVelocity * dt;
                    if (pose.Position.Y <= support)
                    {
                        pose.Position.Y = support;
                        air.VerticalVelocity = 0f;
                        air.Grounded = true;
                    }
                }
                else
                {
                    pose.Position.Y = support;
                }
            }
            // Per-frame state machine drives the skeleton; a downed wanderer keeps Death.
            UpdateAnim(visual.Rig, info, motion, air, clinging, dt, ref visual.IdleTimer);
            visual.Rig.AdvanceAndPose(_pbr, dt);
            visual.Rig.SetModel(pose.Position, pose.Yaw);

            // Hit-flinch: brief warm flash after a thrown item connects.
            if (visual.Flinch > 0f)
            {
                visual.Flinch = Math.Max(0f, visual.Flinch - dt);
                if (visual.Entity != controlledEntity)
                    SetHighlight(visual, 0.4f * visual.Flinch + 0.1f);
            }
        }

        TickWanderers(dt);
        TickItems(dt);
        TickProjectile(dt);
        if (_hitTimer > 0f) _hitTimer -= dt;

        _jumpQueued = false;
        UpdateCamera(dt);
    }

    /// <summary>Every part instance brightens together — skins and rigid bits alike.</summary>
    private static void SetHighlight(AnimalVisual visual, float value)
    {
        foreach (var part in visual.Rig.Parts) part.Instance.Highlight = value;
    }

    /// <summary>Locomotion clip driver: Jump while airborne/clinging, Walk/Run by speed,
    /// Idle on the spot — with grazing/fidget variants after a few idle seconds. A running
    /// one-shot (Hit, Death, an idle variant) owns the player until it finishes.</summary>
    private void UpdateAnim(RigVisual rig, SpeciesInfo info, AnimalMotion motion,
        AnimalAir air, bool clinging, float dt, ref float idleTimer)
    {
        if (rig.Player is null) return;
        if (rig.OneShot is not null)
        {
            if (!rig.Player.IsFinished) return;
            rig.OneShot = null;
            rig.CurrentClip = "";
        }

        idleTimer = motion.Moving ? 0f : idleTimer + dt;
        if (idleTimer > 5f && PlayIdleVariant(rig, info)) { idleTimer = 0f; return; }

        var key =
            clinging ? AnimKey.Idle :
            !air.Grounded ? AnimKey.Jump :
            motion.Moving ? (motion.Speed > info.GaitSplit ? AnimKey.Run : AnimKey.Walk) :
            AnimKey.Idle;
        var target = info.Clips is { } m && m.TryGetValue(key, out var n) ? n : DefaultClipNames(key);
        if (rig.CurrentClip == target) return;
        PlayAnim(rig, info, key);
    }

    /// <summary>Idle variety: a grazing or secondary idle clip as a one-shot, then back to
    /// plain Idle when it finishes.</summary>
    private bool PlayIdleVariant(RigVisual rig, SpeciesInfo info)
    {
        var pick = info.IdleVariants is { } vars && vars.Length > 0
            ? vars[_random.Next(vars.Length)]
            : null;
        if (pick is null || rig.Player is null) return false;
        var clip = rig.Rig.Clip(pick);
        if (clip is null) return false;
        rig.Player.Play(clip, fadeSeconds: 0.3f, loop: false);
        rig.OneShot = pick;
        rig.CurrentClip = pick;
        return true;
    }

    /// <summary>Hit-react one-shot (flinch) or Death for a downed wanderer.</summary>
    private void PlayOneShot(RigVisual rig, SpeciesInfo info, AnimKey key)
    {
        if (rig.Player is null) return;
        var name = info.Clips is { } m && m.TryGetValue(key, out var n) ? n : DefaultClipNames(key);
        var clip = rig.Rig.Clip(name);
        if (clip is null) return;
        rig.Player.Play(clip, fadeSeconds: 0.12f, loop: false);
        rig.OneShot = name;
        rig.CurrentClip = name;
    }


    /// <summary>Vertical support surface at a column: terrain, or a perch surface when the
    /// animal is at or above its top (tree crowns, stumps).</summary>
    private float SupportHeight(float x, float y, float z)
    {
        float support = Terrain.Height(x, z);
        foreach (var p in _perches)
        {
            float dx = x - p.X, dz = z - p.Z;
            if (dx * dx + dz * dz <= p.Radius * p.Radius &&
                y >= p.Top - 0.35f && p.Top > support)
                support = p.Top;
        }
        return support;
    }

    /// <summary>E — context action for the cat: release a cling, pick up a nearby throwable,
    /// or grab a climbable trunk. Other species ignore it.</summary>
    public void TryInteract()
    {
        if (_controlled < 0 || _animals[_controlled].Species != Species.Cat) return;
        ref var pose = ref _world.GetComponent<AnimalPose>(_animals[_controlled].Entity);
        ref var air = ref _world.GetComponent<AnimalAir>(_animals[_controlled].Entity);

        if (_clingTree is not null)
        {
            _clingTree = null; // gentle release; stays put, support check catches next frame
            return;
        }

        // Trunk first: beside a climbable tree E means climb, not pick-up — dropped items
        // litter the forest floor right where cats want to climb.
        for (int i = 0; i < _trees.Count; i++)
        {
            var t = _trees[i];
            if (!t.Climbable) continue;
            float dx = t.X - pose.Position.X, dz = t.Z - pose.Position.Z;
            if (dx * dx + dz * dz > 0.75f * 0.75f) continue;
            if (pose.Position.Y > t.TrunkTop + 0.3f) continue; // already above the cling band
            var outDir = new Vector3(pose.Position.X - t.X, 0f, pose.Position.Z - t.Z);
            if (outDir.LengthSquared() > 1e-5f) outDir = Vector3.Normalize(outDir);
            else outDir = Vector3.UnitX;
            _clingTree = t;
            air.Grounded = true;
            air.VerticalVelocity = 0f;
            pose.Position.X = t.X + outDir.X * 0.24f; // pinned just outside the bark
            pose.Position.Z = t.Z + outDir.Z * 0.24f;
            pose.Yaw = MathF.Atan2(pose.Position.X - t.X, pose.Position.Z - t.Z); // face the trunk (forward −Z)
            return;
        }

        if (_held is null)
        {
            // Grab a loose item within reach.
            for (int i = _items.Count - 1; i >= 0; i--)
            {
                var it = _items[i];
                if (Vector3.DistanceSquared(it.Pos, pose.Position) > PickupRadius * PickupRadius) continue;
                _held = it.Instance;
                _heldType = it.Type;
                _items.RemoveAt(i);
                return;
            }
        }
    }

    /// <summary>F — throw the held item along the camera aim. No-op when empty-handed.</summary>
    public void ThrowHeld()
    {
        if (_controlled < 0 || _animals[_controlled].Species != Species.Cat || _held is null) return;
        ref readonly var pose = ref _world.GetComponent<AnimalPose>(_animals[_controlled].Entity);

        // Camera eye sits at target + dist*(-sin(yaw), sin(pitch), -cos(yaw)), so the view
        // forward (target-eye) is (sin(yaw), -sin(pitch), cos(yaw)). A constant lob keeps
        // the cone at torso height out to ~5m instead of nose-diving into the turf.
        var aim = new Vector3(
            MathF.Sin(_camYaw) * MathF.Cos(_camPitch),
            0.6f - MathF.Sin(_camPitch),
            MathF.Cos(_camYaw) * MathF.Cos(_camPitch));
        aim = Vector3.Normalize(aim);

        _thrown = new ThrownItem
        {
            Instance = _held,
            Type = _heldType,
            Pos = pose.Position + new Vector3(0f, 0.45f, 0f) + Vector3.Normalize(aim) * 0.3f,
            Vel = Vector3.Normalize(aim) * 9f,
            Spin = _elapsed,
        };
        _held = null;
    }

    /// <summary>Spawns a loose item on the ground — debug/QA hook and the wandering spawn's
    /// internal path.</summary>
    public void SpawnItem(Vector3 pos, int type)
    {
        if (_items.Count >= MaxItems) return;
        var it = new DropItem
        {
            Instance = new PbrInstance { Mesh = _itemMeshes[type] },
            Type = type,
            Pos = new Vector3(pos.X, Terrain.Height(pos.X, pos.Z) + 0.06f, pos.Z),
        };
        it.Instance.Model = Matrix4x4.CreateRotationY((float)_random.NextDouble() * MathF.Tau)
            * Matrix4x4.CreateTranslation(it.Pos);
        _items.Add(it);
        _scene.Instances.Add(it.Instance);
    }

    /// <summary>Spawns an extra forest passer-by at the treeline — debug/QA hook.</summary>
    public void SpawnWanderer(Species species)
    {
        float a = (float)_random.NextDouble() * MathF.Tau;
        var pos = new Vector3(MathF.Cos(a) * (Terrain.HalfSize - 10f), 0f,
            MathF.Sin(a) * (Terrain.HalfSize - 10f));
        pos.Y = Terrain.Height(pos.X, pos.Z);
        AddWanderer(species, pos);
    }

    /// <summary>Drops a throwable just ahead of the controlled animal's feet — QA hook
    /// behind <c>DebugSpawnItem</c>.</summary>
    public void DebugSpawnItemAtPlayer(int type)
    {
        if (_controlled < 0) return;
        ref readonly var pose = ref _world.GetComponent<AnimalPose>(_animals[_controlled].Entity);
        var fwd = new Vector3(-MathF.Sin(pose.Yaw), 0f, -MathF.Cos(pose.Yaw));
        SpawnItem(pose.Position + fwd * 0.35f, Math.Clamp(type, 0, 3));
    }

    /// <summary>Moves the controlled animal to a world XZ point — QA hook.</summary>
    public void DebugTeleport(float x, float z)
    {
        if (_controlled < 0) return;
        ref var pose = ref _world.GetComponent<AnimalPose>(_animals[_controlled].Entity);
        ref var air = ref _world.GetComponent<AnimalAir>(_animals[_controlled].Entity);
        _clingTree = null;
        pose.Position.X = x;
        pose.Position.Z = z;
        pose.Position.Y = SupportHeight(x, Terrain.Height(x, z) + 1f, z);
        air.Grounded = true;
        air.VerticalVelocity = 0f;
    }

    /// <summary>Spawns a passer-by a few meters ahead of the controlled animal — QA hook.</summary>
    public void DebugSpawnNear(Species species)
    {
        if (_controlled < 0) return;
        ref readonly var pose = ref _world.GetComponent<AnimalPose>(_animals[_controlled].Entity);
        var fwd = new Vector3(-MathF.Sin(pose.Yaw), 0f, -MathF.Cos(pose.Yaw));
        var pos = pose.Position + fwd * 3.5f;
        pos.Y = Terrain.Height(pos.X, pos.Z);
        AddWanderer(species, pos);
    }

    /// <summary>Spawns a passer-by a few meters ahead of the player's VIEW — QA hook.</summary>
    public void DebugSpawnAhead(Species species)
    {
        if (_controlled < 0) return;
        ref readonly var pose = ref _world.GetComponent<AnimalPose>(_animals[_controlled].Entity);
        var fwd = new Vector3(MathF.Sin(_camYaw), 0f, MathF.Cos(_camYaw));
        var pos = pose.Position + fwd * 3.5f;
        pos.Y = Terrain.Height(pos.X, pos.Z);
        AddWanderer(species, pos);
    }

    /// <summary>Teleports the cat beside the nearest climbable trunk — QA hook.</summary>
    public void DebugTeleportToTree()
    {
        if (_controlled < 0 || _animals[_controlled].Species != Species.Cat) return;
        ref readonly var pose = ref _world.GetComponent<AnimalPose>(_animals[_controlled].Entity);
        Tree? best = null;
        float bestD = float.MaxValue;
        foreach (var t in _trees)
        {
            if (!t.Climbable) continue;
            float d = (t.X - pose.Position.X) * (t.X - pose.Position.X)
                + (t.Z - pose.Position.Z) * (t.Z - pose.Position.Z);
            if (d < bestD) { bestD = d; best = t; }
        }
        if (best is not null) DebugTeleport(best.X + 0.45f, best.Z);
    }

    private void AddWanderer(Species species, Vector3 pos)
    {
        var entity = _world.CreateEntity(EntityBuilder.Create()
            .Add(new AnimalKind { Species = species, NameIndex = 0 })
            .Add(new AnimalPose { Position = pos, Yaw = (float)_random.NextDouble() * MathF.Tau })
            .Add(new AnimalAir { Grounded = true })
            .Add(new AnimalMotion())
            .Add(new AnimalBrain { State = WanderState.Idle, Timer = 0.3f }));
        var rig = CreateVisual(species);
        var w = new Wanderer
        {
            Entity = entity,
            Rig = rig,
            Species = species,
            LifeTimer = 40f + (float)_random.NextDouble() * 30f,
        };
        _wanderers.Add(w);
        foreach (var part in rig.Parts)
            _scene.Instances.Add(part.Instance);
        PlayAnim(rig, SpeciesCatalog.Of(species), AnimKey.Idle);
    }

    /// <summary>JSON snapshot for QA: controlled position, cling/held state, counts.</summary>
    public string DebugCatState()
    {
        if (_controlled < 0) return "{}";
        ref readonly var pose = ref _world.GetComponent<AnimalPose>(_animals[_controlled].Entity);
        return $"{{\"x\":{pose.Position.X:F2},\"y\":{pose.Position.Y:F2},\"z\":{pose.Position.Z:F2}," +
            $"\"cling\":{(_clingTree is not null).ToString().ToLowerInvariant()},\"held\":{(_held is not null).ToString().ToLowerInvariant()}," +
            $"\"items\":{_items.Count},\"wanderers\":{_wanderers.Count}}}";
    }

    /// <summary>One line per roster animal: index, label, world position — QA dump.</summary>
    public string DebugAnimalPositions()
    {
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < _animals.Count; i++)
        {
            var a = _animals[i];
            ref readonly var pose = ref _world.GetComponent<AnimalPose>(a.Entity);
            sb.AppendLine($"{i} {a.Label} ({a.Species}) @ ({pose.Position.X:F1},{pose.Position.Y:F1},{pose.Position.Z:F1})");
        }
        return sb.ToString();
    }

    /// <summary>Per-animal skinned palette sample: part count, joint count, first two palette
    /// matrices — used to spot smear-inducing garbage (NaN / wild scale).</summary>
    public string DebugRigPalettes()
    {
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < _animals.Count; i++)
        {
            var a = _animals[i];
            var part = a.Rig.Parts.FirstOrDefault(p => p.SkinIndex >= 0);
            if (part is null || a.Rig.Player is null) continue;
            var models = a.Rig.Player.ModelMatrices;
            var skin = a.Rig.Rig.Skins[part.SkinIndex];
            var span = new System.Span<Matrix4x4>(new Matrix4x4[skin.Joints.Length]);
            Paradise.Animation.SkinningPalette.Compute(models, skin.Joints.AsSpan(),
                skin.InverseBinds.AsSpan(), -1, span);
            var m0 = span[0];
            sb.AppendLine($"{i} {a.Species} joints={skin.Joints.Length} offset={part.JointOffset} " +
                $"pal0=({m0.M11:F3},{m0.M41:F3},{m0.M42:F3},{m0.M43:F3}) " +
                $"scale={a.Rig.Scale:F3} ground={a.Rig.GroundOffset:F2}");
        }
        return sb.ToString();
    }

    /// <summary>Spawns, moves and retires forest passers-by: extra critters and the odd
    /// visitor who wanders a while and leaves again.</summary>
    private void TickWanderers(float dt)
    {
        _wandererTimer -= dt;
        if (_wandererTimer <= 0f && _wanderers.Count(w => !w.Downed) < MaxWanderers)
        {
            _wandererTimer = 7f + (float)_random.NextDouble() * 8f;
            SpawnWanderer(PickWandererSpecies());
        }

        for (int i = _wanderers.Count - 1; i >= 0; i--)
        {
            var w = _wanderers[i];
            ref var pose = ref _world.GetComponent<AnimalPose>(w.Entity);
            ref var motion = ref _world.GetComponent<AnimalMotion>(w.Entity);
            ref var air = ref _world.GetComponent<AnimalAir>(w.Entity);
            var info = SpeciesCatalog.Of(w.Species);

            if (w.Downed)
            {
                w.DownTimer -= dt;
                if (w.DownTimer <= 0f) { RemoveWanderer(i); continue; }
                w.Rig.AdvanceAndPose(_pbr, dt);
                w.Rig.SetModel(pose.Position, pose.Yaw);
                continue;
            }

            w.LifeTimer -= dt;
            if (w.LifeTimer <= 0f) { RemoveWanderer(i); continue; }

            TickWander(w.Entity, ref pose, ref motion, info, dt);

            float support = SupportHeight(pose.Position.X, pose.Position.Y, pose.Position.Z);
            if (air.Grounded && pose.Position.Y > support + 0.15f)
            {
                air.Grounded = false;
                air.VerticalVelocity = 0f;
            }
            if (!air.Grounded)
            {
                air.VerticalVelocity -= 22f * dt;
                pose.Position.Y += air.VerticalVelocity * dt;
                if (pose.Position.Y <= support)
                {
                    pose.Position.Y = support;
                    air.VerticalVelocity = 0f;
                    air.Grounded = true;
                }
            }
            else pose.Position.Y = support;

            UpdateAnim(w.Rig, info, motion, air, clinging: false, dt, ref w.IdleTimer);
            w.Rig.AdvanceAndPose(_pbr, dt);
            w.Rig.SetModel(pose.Position, pose.Yaw);
        }
    }

    private void RemoveWanderer(int index)
    {
        var w = _wanderers[index];
        foreach (var part in w.Rig.Parts)
            _scene.Instances.Remove(part.Instance);
        if (_world.IsAlive(w.Entity)) _world.Despawn(w.Entity);
        _wanderers.RemoveAt(index);
    }

    private Species PickWandererSpecies()
    {
        float roll = (float)_random.NextDouble();
        return roll switch
        {
            < 0.30f => Species.Ranger,
            < 0.50f => Species.Rabbit,
            < 0.65f => Species.Deer,
            < 0.78f => Species.Fox,
            < 0.90f => Species.Dog,
            _ => Species.Alpaca,
        };
    }

    /// <summary>Periodic item drops beneath random trees plus the held-item follow transform.</summary>
    private void TickItems(float dt)
    {
        _itemTimer -= dt;
        if (_itemTimer <= 0f && _items.Count < MaxItems && _trees.Count > 0)
        {
            _itemTimer = 5f + (float)_random.NextDouble() * 6f;
            var tree = _trees[_random.Next(_trees.Count)];
            float a = (float)_random.NextDouble() * MathF.Tau;
            float r = 0.6f + (float)_random.NextDouble() * 2.4f;
            var pos = new Vector3(tree.X + MathF.Cos(a) * r, 0f, tree.Z + MathF.Sin(a) * r);
            SpawnItem(pos, (int)(_random.NextDouble() * 3.6)); // cone/acorn/stick; stone rare
        }

        if (_held is not null && _controlled >= 0 && _animals[_controlled].Species == Species.Cat)
        {
            ref readonly var pose = ref _world.GetComponent<AnimalPose>(_animals[_controlled].Entity);
            var forward = new Vector3(-MathF.Sin(pose.Yaw), 0f, -MathF.Cos(pose.Yaw));
            _held.Model = Matrix4x4.CreateTranslation(
                pose.Position + forward * 0.22f + new Vector3(0f, 0.22f, 0f));
        }
    }

    /// <summary>Thrown item ballistics and hits: humans crumple, critters flinch.</summary>
    private void TickProjectile(float dt)
    {
        var t = _thrown;
        if (t is null) return;

        var from = t.Pos;
        t.Vel += new Vector3(0f, -13f * dt, 0f);
        t.Pos += t.Vel * dt;
        t.Spin += dt * 8f;

        // Swept test: substeps along this frame's travel so fast throws can't tunnel.
        static bool Hits(Vector3 from, Vector3 to, Vector3 feet, float radius, float height)
        {
            for (int s = 1; s <= 4; s++)
            {
                var p = Vector3.Lerp(from, to, s / 4f);
                float dx = p.X - feet.X, dz = p.Z - feet.Z;
                if (dx * dx + dz * dz < radius * radius &&
                    p.Y > feet.Y - 0.1f && p.Y < feet.Y + height + 0.2f)
                    return true;
            }
            return false;
        }

        // Hit a wanderer — humans crumple.
        for (int i = 0; i < _wanderers.Count; i++)
        {
            var w = _wanderers[i];
            if (w.Downed) continue;
            ref readonly var pose = ref _world.GetComponent<AnimalPose>(w.Entity);
            var info = SpeciesCatalog.Of(w.Species);
            if (!Hits(from, t.Pos, pose.Position, info.PickRadius * 1.4f, info.HalfHeight * 2f))
                continue;

            // Humans crumple; everything else staggers off.
            if (w.Species == Species.Ranger)
            {
                w.Downed = true;
                w.DownTimer = 6f;
                _hitTimer = 1.6f;
                PlayOneShot(w.Rig, info, AnimKey.Death);
                foreach (var part in w.Rig.Parts) part.Instance.Highlight = 0.5f;
            }
            else PlayOneShot(w.Rig, info, AnimKey.Hit);
            DropThrown(t);
            return;
        }


        // Hit a resident animal — it flinches and flashes.
        foreach (var a in _animals)
        {
            if (_controlled >= 0 && a.Entity.Equals(_animals[_controlled].Entity)) continue;
            ref readonly var pose = ref _world.GetComponent<AnimalPose>(a.Entity);
            var info = SpeciesCatalog.Of(a.Species);
            if (!Hits(from, t.Pos, pose.Position, info.PickRadius * 1.2f, info.HalfHeight * 2f))
                continue;
            a.Flinch = 0.8f;
            SetHighlight(a, 0.4f);
            PlayOneShot(a.Rig, info, AnimKey.Hit);
            DropThrown(t);
            return;
        }


        // Landed: becomes a loose item where it stops.
        if (t.Pos.Y <= SupportHeight(t.Pos.X, t.Pos.Y + 0.1f, t.Pos.Z) + 0.06f)
            DropThrown(t);
        else
            t.Instance.Model = Matrix4x4.CreateRotationY(t.Spin) * Matrix4x4.CreateTranslation(t.Pos);
    }

    /// <summary>Converts the projectile back into a loose pickup where it stopped.</summary>
    private void DropThrown(ThrownItem t)
    {
        var pos = t.Pos;
        pos.Y = Terrain.Height(pos.X, pos.Z) + 0.06f;
        var it = new DropItem { Instance = t.Instance, Type = t.Type, Pos = pos };
        it.Instance.Model = Matrix4x4.CreateTranslation(pos);
        _items.Add(it);
        _thrown = null;
    }


    /// <summary>Player-driven animal: camera-relative WASD, shift-run, space-jump; the cat
    /// additionally clings to trunks and climbs crowns.</summary>
    private void TickPlayer(ref AnimalPose pose, ref AnimalMotion motion, ref AnimalAir air,
        SpeciesInfo info, float dt, bool isCatPlayer)
    {
        // Cling mode: vertical climb only, no gravity, no horizontal walk.
        if (isCatPlayer && _clingTree is not null)
        {
            var t = _clingTree!;
            float climb = _moveInput.Y * 3.0f * dt;
            pose.Position.Y = Math.Clamp(pose.Position.Y + climb,
                Terrain.Height(t.X, t.Z) + 0.15f, t.TrunkTop);
            // Slightly offset out from the trunk axis so the model hugs the bark.
            var outDir = new Vector3(pose.Position.X - t.X, 0f, pose.Position.Z - t.Z);
            if (outDir.LengthSquared() > 1e-5f) outDir = Vector3.Normalize(outDir);
            pose.Position.X = t.X + outDir.X * 0.24f;
            pose.Position.Z = t.Z + outDir.Z * 0.24f;
            pose.Yaw = MathF.Atan2(pose.Position.X - t.X, pose.Position.Z - t.Z); // face trunk (forward −Z)

            // Reaching the crown perch pops the cat onto it.
            if (pose.Position.Y >= t.TrunkTop - 0.01f)
            {
                _clingTree = null;
                pose.Position.X = t.X;
                pose.Position.Z = t.Z;
                pose.Position.Y = t.PerchTop;
                air.Grounded = true;
                air.VerticalVelocity = 0f;
                air.Height = 0f;
            }

            // Space from a trunk = spring away from the bark.
            if (_jumpQueued)
            {
                _clingTree = null;
                pose.Position += outDir * 0.30f;
                air.Grounded = false;
                air.VerticalVelocity = 3.4f;
            }

            motion.Moving = MathF.Abs(_moveInput.Y) > 0f;
            motion.Speed = 0f;
            return;
        }

        if (_moveInput.LengthSquared() > 0f)
        {
            var dir = Vector2.Normalize(_moveInput);
            var fwd = new Vector3(MathF.Sin(_camYaw), 0f, MathF.Cos(_camYaw));
            var right = new Vector3(-fwd.Z, 0f, fwd.X);
            var move = Vector3.Normalize(fwd * dir.Y + right * dir.X);
            float speed = _runHeld ? info.RunSpeed : info.WalkSpeed * 1.6f;
            if (Terrain.IsWater(pose.Position.X, pose.Position.Z) && !info.Water)
                speed *= 0.45f; // wading

            pose.Yaw = TurnToward(pose.Yaw, MathF.Atan2(-move.X, -move.Z), 10f * dt);
            pose.Position += move * speed * dt;
            motion.Speed = speed;
            motion.Moving = true;
        }
        else
        {
            motion.Speed = 0f;
            motion.Moving = false;
        }

        if (_jumpQueued && air.Grounded)
        {
            // Cats launch higher, scaled by body size like everything else.
            air.VerticalVelocity = (isCatPlayer ? 5.6f : 4.5f) + info.HalfHeight * 1.2f;
            air.Grounded = false;
        }

        float lim = Terrain.HalfSize - 1.5f;
        pose.Position.X = Math.Clamp(pose.Position.X, -lim, lim);
        pose.Position.Z = Math.Clamp(pose.Position.Z, -lim, lim);
    }

    /// <summary>Wander AI: idle → pick destination → turn → walk; repeat. Land species
    /// reject targets inside the pond; ducks stay in and around it.</summary>
    private void TickWander(Entity entity, ref AnimalPose pose, ref AnimalMotion motion,
        SpeciesInfo info, float dt)
    {
        ref var brain = ref _world.GetComponent<AnimalBrain>(entity);
        brain.Timer -= dt;

        switch (brain.State)
        {
            case WanderState.Idle:
                motion.Moving = false;
                if (brain.Timer <= 0f)
                {
                    brain.Destination = PickWanderTarget(pose.Position, info);
                    brain.State = WanderState.Turn;
                    brain.Timer = 10f; // safety timeout for the leg
                }
                break;

            case WanderState.Turn:
            case WanderState.Walk:
            {
                var to = brain.Destination - pose.Position;
                to.Y = 0f;
                float dist = to.Length();
                if (dist < 0.8f || brain.Timer <= 0f)
                {
                    brain.State = WanderState.Idle;
                    brain.Timer = 1.5f + (float)_random.NextDouble() * 4f;
                    motion.Moving = false;
                    break;
                }
                float targetYaw = MathF.Atan2(-to.X, -to.Z);
                float delta = AngleDelta(pose.Yaw, targetYaw);
                pose.Yaw += Math.Clamp(delta, -2.2f * dt, 2.2f * dt);

                // Walk once roughly facing the target.
                if (MathF.Abs(delta) < 0.5f || brain.State == WanderState.Walk)
                {
                    brain.State = WanderState.Walk;
                    var fwd = new Vector3(-MathF.Sin(pose.Yaw), 0f, -MathF.Cos(pose.Yaw));
                    pose.Position += fwd * info.WalkSpeed * dt;
                    motion.Moving = true;
                }
                break;
            }
        }

        float lim = Terrain.HalfSize - 2f;
        pose.Position.X = Math.Clamp(pose.Position.X, -lim, lim);
        pose.Position.Z = Math.Clamp(pose.Position.Z, -lim, lim);
    }

    private Vector3 PickWanderTarget(Vector3 from, SpeciesInfo info)
    {
        for (int tries = 0; tries < 12; tries++)
        {
            float angle = (float)_random.NextDouble() * MathF.Tau;
            float range = 6f + (float)_random.NextDouble() * 18f;
            var target = from + new Vector3(MathF.Cos(angle) * range, 0f, MathF.Sin(angle) * range);
            target.X = Math.Clamp(target.X, -Terrain.HalfSize + 4f, Terrain.HalfSize - 4f);
            target.Z = Math.Clamp(target.Z, -Terrain.HalfSize + 4f, Terrain.HalfSize - 4f);
            if (Terrain.IsWater(target.X, target.Z) != info.Water) continue;
            return target;
        }
        return from; // boxed in — idle another cycle
    }

    // ------------------------------------------------------------------ camera

    private void UpdateCamera(float dt)
    {
        if (_controlled < 0) return;
        var visual = _animals[_controlled];
        ref readonly var pose = ref _world.GetComponent<AnimalPose>(visual.Entity);
        var info = SpeciesCatalog.Of(visual.Species);
        var target = pose.Position + new Vector3(0f, info.CameraHeight, 0f);

        var eye = target + new Vector3(
            _camDistance * MathF.Cos(_camPitch) * -MathF.Sin(_camYaw),
            _camDistance * MathF.Sin(_camPitch),
            _camDistance * MathF.Cos(_camPitch) * -MathF.Cos(_camYaw));

        // Keep the camera above terrain so hills don't swallow the view.
        float minY = Terrain.Height(eye.X, eye.Z) + 0.4f;
        if (eye.Y < minY) eye.Y = minY;

        float k = 1f - MathF.Exp(-10f * dt);
        _camEye = _camInitialized ? Vector3.Lerp(_camEye, eye, k) : eye;
        _camInitialized = true;

        _view = PbrMath.LookAt(_camEye, target, Vector3.UnitY);
        _proj = PbrMath.Perspective(MathF.PI / 3.2f, _width / (float)_height, 0.08f, 400f);
        _scene.Camera = new PbrCamera { View = _view, Projection = _proj, Position = _camEye };
    }

    // ------------------------------------------------------------------ render + HUD

    /// <summary>Record one frame: pushes timing into the scene and submits.</summary>
    public void RenderFrame()
    {
        _scene.DeltaSeconds = 1f / 60f;
        _scene.ElapsedSeconds = _elapsed;
        _pbr.RenderFrame(_scene);
    }

    /// <summary>Raised whenever the controlled animal changes (Tab/Q, click, HUD pick).</summary>
    /// <remarks>The int is the roster index; hosts refresh their roster highlight off it.</remarks>
    public event Action<int>? ControlledChanged;

    /// <summary>Index of the controlled animal in <see cref="Roster"/>, or -1.</summary>
    public int ControlledIndex => _controlled;

    /// <summary>Display label ("羚羊 3") and English species name per roster entry.</summary>
    public IReadOnlyList<(string Label, string Species)> Roster => _roster;

    private (string Label, string Species)[] _roster = [];

    /// <summary>Short status line for a HUD header: who's controlled and its speeds.</summary>
    public string HudStatus()
    {
        if (_controlled < 0) return string.Empty;
        var visual = _animals[_controlled];
        var info = SpeciesCatalog.Of(visual.Species);
        var s = $"正在控制: {visual.Label} ({info.Name}) · {info.WalkSpeed:F1}-{info.RunSpeed:F1} m/s";
        if (visual.Species == Species.Cat)
        {
            if (_clingTree is not null) s += " · 攀爬中";
            else if (_held is not null) s += " · 衔着东西";
            if (_hitTimer > 0f) s += " · 打中!";
        }
        return s;
    }

    /// <summary>Resize plumbing shared by windowed and headless paths.</summary>
    public void Resize(uint width, uint height)
    {
        _width = Math.Max(1, width);
        _height = Math.Max(1, height);
        _pbr.Resize(_width, _height);
    }

    public void Dispose()
    {
        foreach (var a in _animals) a.Rig.Dispose();
        foreach (var w in _wanderers) w.Rig.Dispose();
        foreach (var r in _speciesRigs) r?.Dispose();
        foreach (var r in _sceneryRigs) r.Dispose();
        _pbr.Dispose();
        _sharedWorld.Dispose();
    }

    // ------------------------------------------------------------------ math

    private static float AngleDelta(float from, float to)
    {
        float d = (to - from) % MathF.Tau;
        if (d > MathF.PI) d -= MathF.Tau;
        if (d < -MathF.PI) d += MathF.Tau;
        return d;
    }

    private static float TurnToward(float yaw, float target, float maxStep) =>
        yaw + Math.Clamp(AngleDelta(yaw, target), -maxStep, maxStep);
}
