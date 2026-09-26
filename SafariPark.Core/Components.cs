using System.Numerics;
using System.Runtime.InteropServices;

using Paradise.ECS;

namespace SafariPark;

/// <summary>What this animal is — index into <see cref="SpeciesCatalog"/>.</summary>
[Guid("B0F4A1D2-3C4E-4A5B-9C6D-7E8F9A0B1C01")]
[Component]
public partial struct AnimalKind
{
    public Species Species;
    public int NameIndex; // roster position used for HUD labels ("羚羊 3")
}

/// <summary>Where the animal stands and which way it faces (radians, Y-up).</summary>
[Guid("B0F4A1D2-3C4E-4A5B-9C6D-7E8F9A0B1C02")]
[Component]
public partial struct AnimalPose
{
    public Vector3 Position;
    public float Yaw;
}

/// <summary>Vertical physics: height above terrain and its rate — supports the jump arc.</summary>
[Guid("B0F4A1D2-3C4E-4A5B-9C6D-7E8F9A0B1C03")]
[Component]
public partial struct AnimalAir
{
    public float Height;
    public float VerticalVelocity;
    public bool Grounded;
}

/// <summary>Current motion: heading, target speed, and whether the animal is moving.</summary>
[Guid("B0F4A1D2-3C4E-4A5B-9C6D-7E8F9A0B1C04")]
[Component]
public partial struct AnimalMotion
{
    public Vector3 Velocity;
    public float Speed;
    public bool Moving;
}

/// <summary>Wander-AI state machine timer and chosen destination.</summary>
[Guid("B0F4A1D2-3C4E-4A5B-9C6D-7E8F9A0B1C05")]
[Component]
public partial struct AnimalBrain
{
    public WanderState State;
    public float Timer;
    public Vector3 Destination;
    public float TurnBias;
}

/// <summary>The single animal under player control; at most one entity carries it.</summary>
[Guid("B0F4A1D2-3C4E-4A5B-9C6D-7E8F9A0B1C10")]
[Tag]
public partial struct PlayerControlled;

/// <summary>AI wander states.</summary>
public enum WanderState : byte
{
    Idle,   // standing/grazing
    Turn,   // rotating toward Destination
    Walk,   // traveling to Destination
}
