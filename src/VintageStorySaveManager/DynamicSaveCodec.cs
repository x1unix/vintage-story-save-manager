using System.Reflection;
using System.Runtime.Loader;

namespace VintageStorySaveManager;

internal sealed class DynamicSaveCodec : IDisposable
{
    private readonly string _gameDirectory;
    private readonly AssemblyLoadContext _loadContext;
    private readonly Type _saveGameType;
    private readonly MethodInfo _deserialize;
    private readonly MethodInfo _serialize;
    private readonly MemberInfo _worldName;
    private readonly MemberInfo _totalSecondsPlayed;
    private readonly MemberInfo? _createdGameVersion;
    private readonly MemberInfo? _lastSavedGameVersion;
    private readonly MemberInfo? _lastPlayed;
    private bool _disposed;

    public DynamicSaveCodec(string gameDirectory)
    {
        _gameDirectory = gameDirectory;
        _loadContext = AssemblyLoadContext.Default;
        _loadContext.Resolving += ResolveAssembly;

        try
        {
            var protobuf = LoadRequired("Lib/protobuf-net.dll");
            var assemblies = new List<Assembly>
            {
                LoadRequired("VintagestoryAPI.dll"),
                LoadRequired("VintagestoryLib.dll")
            };

            LoadOptional("Mods/VSEssentials.dll", assemblies);
            LoadOptional("VintagestoryServer.dll", assemblies);

            _saveGameType = assemblies
                .Select(assembly => assembly.GetType("SaveGame", throwOnError: false, ignoreCase: false))
                .FirstOrDefault(type => type is not null)
                ?? FindTypeByName(assemblies, "SaveGame")
                ?? throw new UserFacingException("The installed game assemblies do not expose the SaveGame protobuf type.");

            var serializer = protobuf.GetType("ProtoBuf.Serializer+NonGeneric", throwOnError: true)!;
            _deserialize = serializer.GetMethod(
                "Deserialize",
                BindingFlags.Public | BindingFlags.Static,
                binder: null,
                types: [typeof(Type), typeof(Stream)],
                modifiers: null)
                ?? throw new UserFacingException("The installed protobuf-net does not expose Serializer.NonGeneric.Deserialize(Type, Stream).");
            _serialize = serializer.GetMethod(
                "Serialize",
                BindingFlags.Public | BindingFlags.Static,
                binder: null,
                types: [typeof(Stream), typeof(object)],
                modifiers: null)
                ?? throw new UserFacingException("The installed protobuf-net does not expose Serializer.NonGeneric.Serialize(Stream, object).");

            _worldName = RequiredMember("WorldName");
            _totalSecondsPlayed = RequiredMember("TotalSecondsPlayed");
            _createdGameVersion = FindMember("CreatedGameVersion");
            _lastSavedGameVersion = FindMember("LastSavedGameVersion");
            _lastPlayed = FindMember("LastPlayed");
        }
        catch
        {
            _loadContext.Resolving -= ResolveAssembly;
            throw;
        }
    }

    public SaveMetadata Read(ReadOnlySpan<byte> payload)
    {
        var save = Deserialize(payload);
        return new SaveMetadata(
            GetValue(_worldName, save)?.ToString() ?? "<unnamed>",
            Convert.ToInt32(GetValue(_totalSecondsPlayed, save) ?? 0),
            GetValue(_createdGameVersion, save)?.ToString(),
            GetValue(_lastSavedGameVersion, save)?.ToString(),
            GetValue(_lastPlayed, save)?.ToString());
    }

    public byte[] SetWorldName(ReadOnlySpan<byte> payload, string worldName)
    {
        if (string.IsNullOrWhiteSpace(worldName))
        {
            throw new UserFacingException("World name cannot be empty.");
        }

        var save = Deserialize(payload);
        SetValue(_worldName, save, worldName.Trim());

        using var output = new MemoryStream(payload.Length);
        Invoke(_serialize, null, output, save);
        return output.ToArray();
    }

    private object Deserialize(ReadOnlySpan<byte> payload)
    {
        using var stream = new MemoryStream(payload.ToArray(), writable: false);
        return Invoke(_deserialize, null, _saveGameType, stream)
            ?? throw new UserFacingException("The game returned an empty SaveGame object.");
    }

    private object? Invoke(MethodInfo method, object? target, params object?[] parameters)
    {
        try
        {
            return method.Invoke(target, parameters);
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            throw new UserFacingException(
                $"Vintage Story could not decode the save metadata: {exception.InnerException.Message}",
                exception.InnerException);
        }
    }

    private Assembly? ResolveAssembly(AssemblyLoadContext context, AssemblyName name)
    {
        var fileName = $"{name.Name}.dll";
        foreach (var subdirectory in new[] { "", "Lib", "Mods" })
        {
            var candidate = Path.Combine(_gameDirectory, subdirectory, fileName);
            if (File.Exists(candidate))
            {
                return context.LoadFromAssemblyPath(Path.GetFullPath(candidate));
            }
        }

        return null;
    }

    private Assembly LoadRequired(string relativePath)
    {
        var path = Path.Combine(_gameDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(path))
        {
            throw new UserFacingException($"Required Vintage Story assembly is missing: {path}");
        }

        var name = AssemblyName.GetAssemblyName(path);
        return _loadContext.Assemblies.FirstOrDefault(a => AssemblyName.ReferenceMatchesDefinition(a.GetName(), name))
            ?? _loadContext.LoadFromAssemblyPath(Path.GetFullPath(path));
    }

    private void LoadOptional(string relativePath, ICollection<Assembly> assemblies)
    {
        var path = Path.Combine(_gameDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar));
        if (File.Exists(path))
        {
            assemblies.Add(LoadRequired(relativePath));
        }
    }

    private static Type? FindTypeByName(IEnumerable<Assembly> assemblies, string name)
    {
        foreach (var assembly in assemblies)
        {
            try
            {
                var type = assembly.GetTypes().FirstOrDefault(candidate => candidate.Name == name);
                if (type is not null)
                {
                    return type;
                }
            }
            catch (ReflectionTypeLoadException exception)
            {
                var type = exception.Types.FirstOrDefault(candidate => candidate?.Name == name);
                if (type is not null)
                {
                    return type;
                }
            }
        }

        return null;
    }

    private MemberInfo? FindMember(string name) =>
        _saveGameType.GetProperty(name, BindingFlags.Instance | BindingFlags.Public) ??
        (MemberInfo?)_saveGameType.GetField(name, BindingFlags.Instance | BindingFlags.Public);

    private MemberInfo RequiredMember(string name) =>
        FindMember(name) ?? throw new UserFacingException($"The installed SaveGame type does not expose {name}.");

    private static object? GetValue(MemberInfo? member, object instance) => member switch
    {
        PropertyInfo property => property.GetValue(instance),
        FieldInfo field => field.GetValue(instance),
        null => null,
        _ => throw new InvalidOperationException($"Unsupported member type: {member.MemberType}")
    };

    private static void SetValue(MemberInfo member, object instance, object? value)
    {
        switch (member)
        {
            case PropertyInfo property:
                property.SetValue(instance, value);
                break;
            case FieldInfo field:
                field.SetValue(instance, value);
                break;
            default:
                throw new InvalidOperationException($"Unsupported member type: {member.MemberType}");
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _loadContext.Resolving -= ResolveAssembly;
        _disposed = true;
    }
}
