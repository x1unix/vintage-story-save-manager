using System.Reflection;
using System.Runtime.ExceptionServices;

namespace VintageStorySaveManager;

// Created by DynamicSaveCodec so repair uses the same installed assemblies and runtime as metadata reads.
internal sealed class DynamicRepairValidator : IRepairValidator
{
    private readonly MethodInfo _deserialize;
    private readonly Dictionary<string, Type> _types;
    private readonly object _pool;
    private readonly FieldInfo _dataPool;
    private readonly FieldInfo _chunkVersion;
    private readonly FieldInfo _savedVersion;
    private readonly MethodInfo _unpack;
    private readonly MethodInfo _disposeChunk;

    public DynamicRepairValidator(Assembly game, Assembly api, MethodInfo deserialize, MethodInfo serialize, string version)
    {
        try
        {
            var installedVersion = RequiredType(api, "Vintagestory.API.Config.GameVersion")
                .GetField("ShortGameVersion")?.GetValue(null)?.ToString();
            if (!string.Equals(version, installedVersion, StringComparison.Ordinal))
            {
                throw new UserFacingException($"Repair requires game {version}; the installed decoder is {installedVersion ?? "unknown"}. Set VSM_GAME_DIR to a matching installation.");
            }

            _deserialize = deserialize;
            _types = new Dictionary<string, Type>
            {
                ["mapregion"] = RequiredType(game, "Vintagestory.Server.ServerMapRegion"),
                ["mapchunk"] = RequiredType(game, "Vintagestory.Server.ServerMapChunk"),
                ["chunk"] = RequiredType(game, "Vintagestory.Server.ServerChunk")
            };
            var chunk = _types["chunk"];
            var worldChunk = RequiredType(game, "Vintagestory.Common.WorldChunk");
            _dataPool = RequiredField(worldChunk, "datapool");
            _chunkVersion = RequiredField(worldChunk, "chunkdataVersion");
            _savedVersion = RequiredField(chunk, "savedCompressionVersion");
            _unpack = chunk.GetMethod("Unpack", Type.EmptyTypes) ?? throw new MissingMethodException("WorldChunk.Unpack");
            _disposeChunk = chunk.GetMethod("Dispose", Type.EmptyTypes) ?? throw new MissingMethodException("WorldChunk.Dispose");
            var chunkSize = RequiredField(RequiredType(api, "Vintagestory.API.Config.GlobalConstants"), "ChunkSize").GetValue(null);
            _pool = Activator.CreateInstance(RequiredType(game, "Vintagestory.Common.ChunkDataPool"), [chunkSize, null])
                ?? throw new MissingMethodException("ChunkDataPool constructor");

            // Fail before inspecting user records if the installed serializer or decompressor cannot run.
            foreach (var (table, type) in _types)
            {
                if (table == "chunk")
                {
                    var create = type.GetMethod("CreateNew") ?? throw new MissingMethodException("ServerChunk.CreateNew");
                    var toBytes = type.GetMethod("ToBytes", Type.EmptyTypes) ?? throw new MissingMethodException("ServerChunk.ToBytes");
                    var sample = Call(create, null, _pool);
                    try
                    {
                        ValidateDecoded(table, (byte[])Call(toBytes, sample)!);
                    }
                    finally
                    {
                        Call(_disposeChunk, sample);
                    }

                    continue;
                }

                using var stream = new MemoryStream();
                Call(serialize, null, stream, Activator.CreateInstance(type, nonPublic: true));
                ValidateDecoded(table, stream.ToArray());
            }
        }
        catch (Exception exception) when (exception is not UserFacingException)
        {
            throw new UserFacingException($"The installed game does not support repair validation: {exception.Message}", exception);
        }
    }

    public string? Validate(string table, byte[]? payload)
    {
        if (payload is null || payload.Length == 0)
        {
            return "Empty payload.";
        }

        try
        {
            ValidateDecoded(table, payload);
            return null;
        }
        catch (Exception exception) when (IsDataError(exception))
        {
            return exception.Message;
        }
        catch (Exception exception)
        {
            throw new UserFacingException($"Repair validation could not run for {table}: {exception.Message}. No repair was published.", exception);
        }
    }

    private void ValidateDecoded(string table, byte[] payload)
    {
        using var stream = new MemoryStream(payload, writable: false);
        var instance = Call(_deserialize, null, _types[table], stream)
            ?? throw new InvalidDataException("The game decoder returned no record.");
        if (table != "chunk")
        {
            return;
        }

        _dataPool.SetValue(instance, _pool);
        _chunkVersion.SetValue(instance, _savedVersion.GetValue(instance));
        try
        {
            Call(_unpack, instance);
        }
        finally
        {
            Call(_disposeChunk, instance);
        }
    }

    private static bool IsDataError(Exception exception) =>
        exception.GetType().FullName == "ProtoBuf.ProtoException" ||
        exception is EndOfStreamException or InvalidDataException or OverflowException or
            ArgumentException or IndexOutOfRangeException;

    private static Type RequiredType(Assembly assembly, string name) =>
        assembly.GetType(name, throwOnError: true)!;

    private static FieldInfo RequiredField(Type type, string name) =>
        type.GetField(name, BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
        ?? throw new MissingFieldException(type.FullName, name);

    private static object? Call(MethodInfo method, object? target, params object?[] arguments)
    {
        try
        {
            return method.Invoke(target, arguments);
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
            throw;
        }
    }

    public void Dispose()
    {
        if (_pool is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }
}
