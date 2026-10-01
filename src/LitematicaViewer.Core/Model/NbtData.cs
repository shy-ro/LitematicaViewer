using System.Collections.Immutable;

namespace LitematicaViewer.Core.Model;

// Domain-owned NBT tree. Keeping Poly.NBT DOM objects out of the public model lets renderers
// consume block-entity/entity data without depending on the parser implementation.
public abstract record NbtData
{
    public virtual string? AsString() => null;
    public virtual long? AsInt64() => null;
    public virtual double? AsDouble() => AsInt64();
}

public sealed record NbtInteger(long Value) : NbtData
{
    public override long? AsInt64() => Value;
}

public sealed record NbtFloating(double Value) : NbtData
{
    public override double? AsDouble() => Value;
}

public sealed record NbtText(string Value) : NbtData
{
    public override string AsString() => Value;
}

public sealed record NbtSequence(ImmutableArray<NbtData> Values) : NbtData;

public sealed record NbtBytes(ImmutableArray<byte> Values) : NbtData;

public sealed record NbtInts(ImmutableArray<int> Values) : NbtData;

public sealed record NbtLongs(ImmutableArray<long> Values) : NbtData;

public sealed record NbtMap(ImmutableDictionary<string, NbtData> Values) : NbtData
{
    public static NbtMap Empty { get; } = new(ImmutableDictionary<string, NbtData>.Empty);

    public bool TryGet(string key, out NbtData value) => Values.TryGetValue(key, out value!);

    public string? GetString(string key) => TryGet(key, out var value) ? value.AsString() : null;

    public long? GetInt64(string key) => TryGet(key, out var value) ? value.AsInt64() : null;

    public NbtMap? GetMap(string key) => TryGet(key, out var value) ? value as NbtMap : null;

    public NbtSequence? GetSequence(string key) => TryGet(key, out var value) ? value as NbtSequence : null;
}

public sealed record BlockEntityData(string Id, Vector3I Position, NbtMap Data);

public sealed record EntityData(string Id, System.Numerics.Vector3 Position, NbtMap Data);
