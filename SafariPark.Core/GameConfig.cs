using Paradise.ECS;

namespace SafariPark;

/// <summary>ECS configuration for the park simulation.</summary>
/// <remarks>Marked [DefaultConfig] so the generator emits the `World`/`SharedWorldFactory`
/// aliases this file's companions use. Sizes follow the engine sample: a park holds tens of
/// entities, well inside these capacities.</remarks>
[DefaultConfig]
public readonly struct GameConfig : IConfig
{
    public GameConfig() { }

    public static int ChunkSize => 16 * 1024;
    public static int MaxMetaBlocks => 1024;
    public static int EntityIdByteSize => sizeof(int);

    public int DefaultEntityCapacity { get; init; } = 256;
    public int DefaultChunkCapacity { get; init; } = 64;
    public IAllocator ChunkAllocator { get; init; } = NativeMemoryAllocator.Shared;
    public IAllocator MetadataAllocator { get; init; } = NativeMemoryAllocator.Shared;
    public IAllocator LayoutAllocator { get; init; } = NativeMemoryAllocator.Shared;
}
