using System.Numerics;

namespace SafariPark;

/// <summary>Animal kinds in the park; every entry but <see cref="Duck"/> maps to a GLB
/// rig under <c>Assets/</c> — the duck stays procedural (no animated bird GLB in the set).</summary>
public enum Species : byte
{
    Cat,
    Deer,
    Stag,
    Horse,
    Alpaca,
    Fox,
    Wolf,
    Dog,
    Rabbit,
    Ranger,
    Duck,
}

/// <summary>Per-species gameplay and presentation attributes.</summary>
/// <param name="Model">GLB filename under Assets/, or null for the procedural species.</param>
/// <param name="Height">Target bind-pose height in meters; rigs normalize to this.</param>
/// <param name="WalkSpeed">Nominal AI wander speed, m/s.</param>
/// <param name="RunSpeed">Player sprint speed, m/s.</param>
/// <param name="GaitSplit">Speed above which the Run clip replaces Walk.</param>
/// <param name="CameraDistance">Third-person follow distance.</param>
/// <param name="CameraHeight">Follow target height above the feet.</param>
/// <param name="PickRadius">Click-pick radius in world units.</param>
/// <param name="HalfHeight">Approximate half-height for picking and hit tests.</param>
/// <param name="Clips">Canonical state → source clip name; null falls back by substring.</param>
/// <param name="IdleVariants">Extra clips mixed into idle rotation (grazing, fidgeting).</param>
public sealed record SpeciesInfo(
    string Name,
    string ChineseName,
    string? Model,
    float Height,
    float WalkSpeed,
    float RunSpeed,
    float GaitSplit,
    float CameraDistance,
    float CameraHeight,
    float PickRadius,
    float HalfHeight,
    bool Water = false,
    IReadOnlyDictionary<AnimKey, string>? Clips = null,
    string[]? IdleVariants = null,
    float YawOffset = MathF.PI); // authored Quaternius/dook rigs face +Z; the game faces −Z

/// <summary>Canonical animation states the driver picks from.</summary>
public enum AnimKey : byte { Idle, Walk, Run, Jump, Death, Hit, Eat }

/// <summary>Species table. Model null = procedural box body (see <see cref="SpeciesCatalog.BuildMesh"/>).</summary>
public static class SpeciesCatalog
{
    private static readonly IReadOnlyDictionary<AnimKey, string> CatClips =
        new Dictionary<AnimKey, string>
        {
            [AnimKey.Idle] = "Idle", [AnimKey.Walk] = "Walk", [AnimKey.Run] = "Run",
            [AnimKey.Jump] = "Jump_Loop", [AnimKey.Death] = "Death",
            [AnimKey.Hit] = "Headbutt", [AnimKey.Eat] = "Idle_Eating",
        };
    private static readonly IReadOnlyDictionary<AnimKey, string> DeerClips =
        new Dictionary<AnimKey, string>
        {
            [AnimKey.Idle] = "Idle", [AnimKey.Walk] = "Walk", [AnimKey.Run] = "Gallop",
            [AnimKey.Jump] = "Gallop_Jump", [AnimKey.Death] = "Death",
            [AnimKey.Hit] = "Idle_HitReact1", [AnimKey.Eat] = "Eating",
        };
    private static readonly IReadOnlyDictionary<AnimKey, string> FoxClips =
        new Dictionary<AnimKey, string>
        {
            [AnimKey.Idle] = "Idle", [AnimKey.Walk] = "Walk", [AnimKey.Run] = "Gallop",
            [AnimKey.Jump] = "Gallop_Jump", [AnimKey.Death] = "Death",
            [AnimKey.Hit] = "Idle_HitReact1", [AnimKey.Eat] = "Eating",
        };
    private static readonly IReadOnlyDictionary<AnimKey, string> HumanClips =
        new Dictionary<AnimKey, string>
        {
            [AnimKey.Idle] = "Idle", [AnimKey.Walk] = "Walk", [AnimKey.Run] = "Run",
            [AnimKey.Jump] = "Roll", [AnimKey.Death] = "Death",
            [AnimKey.Hit] = "RecieveHit", [AnimKey.Eat] = "PickUp",
        };
    private static readonly IReadOnlyDictionary<AnimKey, string> RabbitClips =
        new Dictionary<AnimKey, string>
        {
            [AnimKey.Idle] = "Bunny_idle", [AnimKey.Walk] = "Bunny_walk",
            [AnimKey.Run] = "Bunny_walk",
        };

    public static readonly SpeciesInfo[] All =
    [
        new("Cat", "橘猫", "cat.glb", Height: 0.55f, WalkSpeed: 1.6f, RunSpeed: 6.5f,
            GaitSplit: 2.4f, CameraDistance: 3.0f, CameraHeight: 0.35f,
            PickRadius: 0.5f, HalfHeight: 0.30f, Clips: CatClips),
        new("Deer", "梅花鹿", "deer.glb", Height: 1.55f, WalkSpeed: 2.4f, RunSpeed: 8.0f,
            GaitSplit: 3.0f, CameraDistance: 5.5f, CameraHeight: 1.3f,
            PickRadius: 1.1f, HalfHeight: 1.4f, Clips: DeerClips,
            IdleVariants: ["Eating", "Idle_2", "Idle_Headlow"]),
        new("Stag", "麋鹿", "stag.glb", Height: 1.85f, WalkSpeed: 2.2f, RunSpeed: 8.2f,
            GaitSplit: 3.0f, CameraDistance: 6.0f, CameraHeight: 1.5f,
            PickRadius: 1.2f, HalfHeight: 1.5f, Clips: DeerClips,
            IdleVariants: ["Eating", "Idle_2", "Idle_Headlow"]),
        new("Horse", "野马", "horse.glb", Height: 1.9f, WalkSpeed: 2.4f, RunSpeed: 9.0f,
            GaitSplit: 3.2f, CameraDistance: 6.5f, CameraHeight: 1.6f,
            PickRadius: 1.3f, HalfHeight: 1.6f, Clips: DeerClips,
            IdleVariants: ["Eating", "Idle_2"]),
        new("Alpaca", "羊驼", "alpaca.glb", Height: 1.7f, WalkSpeed: 1.8f, RunSpeed: 7.0f,
            GaitSplit: 2.8f, CameraDistance: 5.5f, CameraHeight: 1.4f,
            PickRadius: 1.1f, HalfHeight: 1.4f, Clips: DeerClips,
            IdleVariants: ["Eating", "Idle_2"]),
        new("Fox", "狐狸", "fox.glb", Height: 0.8f, WalkSpeed: 2.3f, RunSpeed: 7.5f,
            GaitSplit: 2.8f, CameraDistance: 4.2f, CameraHeight: 0.6f,
            PickRadius: 0.7f, HalfHeight: 0.55f, Clips: FoxClips,
            IdleVariants: ["Idle_2", "Idle_2_HeadLow"]),
        new("Wolf", "狼", "wolf.glb", Height: 0.95f, WalkSpeed: 2.5f, RunSpeed: 8.0f,
            GaitSplit: 2.8f, CameraDistance: 4.6f, CameraHeight: 0.7f,
            PickRadius: 0.8f, HalfHeight: 0.6f, Clips: FoxClips,
            IdleVariants: ["Idle_2", "Idle_2_HeadLow"]),
        new("Dog", "柴犬", "husky.glb", Height: 0.85f, WalkSpeed: 2.2f, RunSpeed: 7.5f,
            GaitSplit: 2.8f, CameraDistance: 4.2f, CameraHeight: 0.6f,
            PickRadius: 0.7f, HalfHeight: 0.55f, Clips: FoxClips,
            IdleVariants: ["Idle_2", "Idle_2_HeadLow"]),
        new("Rabbit", "兔子", "rabbit.glb", Height: 0.45f, WalkSpeed: 1.7f, RunSpeed: 7.0f,
            GaitSplit: 2.0f, CameraDistance: 3.0f, CameraHeight: 0.4f,
            PickRadius: 0.45f, HalfHeight: 0.30f, Clips: RabbitClips),
        new("Ranger", "饲养员", "ranger.glb", Height: 1.8f, WalkSpeed: 1.5f, RunSpeed: 3.5f,
            GaitSplit: 2.0f, CameraDistance: 5.5f, CameraHeight: 1.5f,
            PickRadius: 0.6f, HalfHeight: 0.85f, Clips: HumanClips,
            IdleVariants: ["Attacking_Idle"]),
        new("Duck", "鸭子", null, Height: 0.4f, WalkSpeed: 1.0f, RunSpeed: 3.5f,
            GaitSplit: 1.8f, CameraDistance: 4.0f, CameraHeight: 0.4f,
            PickRadius: 0.5f, HalfHeight: 0.35f, Water: true, YawOffset: 0f),
    ];

    public static SpeciesInfo Of(Species s) => All[(int)s];

    /// <summary>Material slots for the procedural duck's parts.</summary>
    public static class Mat
    {
        public const int Body = 0;
        public const int Detail = 1;
        public const int Accent = 2;
        public const int Beak = 3;
    }

    /// <summary>The procedural body used only for <see cref="Species.Duck"/> — every other
    /// species renders a GLB rig. Forward −Z, feet on y = 0.</summary>
    public static (float[] Vertices, uint[] Indices, int Material)[] BuildMesh(Species species)
    {
        var b = new MeshBuilder();
        if (species == Species.Duck)
        {
            b.AddBox(Mat.Body, new Vector3(0.11f, 0.095f, 0.17f), new Vector3(0f, 0.13f, 0.02f));
            b.AddBox(Mat.Body, new Vector3(0.10f, 0.05f, 0.11f), new Vector3(0f, 0.19f, 0.04f));
            b.AddBox(Mat.Detail, new Vector3(0.065f, 0.065f, 0.065f), new Vector3(0f, 0.30f, -0.13f));
            b.AddBox(Mat.Beak, new Vector3(0.038f, 0.015f, 0.05f), new Vector3(0f, 0.285f, -0.20f));
            b.AddBox(Mat.Accent, new Vector3(0.02f, 0.025f, 0.10f), new Vector3(0f, 0.245f, -0.10f));
            b.AddBox(Mat.Body, new Vector3(0.05f, 0.045f, 0.06f), new Vector3(0f, 0.20f, 0.20f), rotX: -0.45f);
            b.AddBox(Mat.Beak, new Vector3(0.045f, 0.01f, 0.055f), new Vector3(0.04f, 0.008f, -0.02f));
            b.AddBox(Mat.Beak, new Vector3(0.045f, 0.01f, 0.055f), new Vector3(-0.04f, 0.008f, -0.02f));
        }
        return b.Build();
    }
}
