using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;

namespace DotNetIsolator.Internal;

#pragma warning disable IL2057, IL2067, IL2070
// This serializer intentionally reflects over dynamically loaded guest/host types.
// DotNetIsolator is not trim-safe because isolated code assemblies are resolved at runtime.
internal static class ObjectGraphTypes
{
    private static readonly ConcurrentDictionary<Type, SerializableMember[]> MemberCache = new();

    public static SerializableMember[] GetSerializableMembers(Type type)
        => MemberCache.GetOrAdd(type, CreateSerializableMembers);

    public static Type ReadType(BinaryReader reader)
        => Type.GetType(reader.ReadString(), throwOnError: true)!;

    public static void WriteType(BinaryWriter writer, Type type)
        => writer.Write(type.AssemblyQualifiedName ?? throw new InvalidOperationException($"Type '{type}' has no assembly-qualified name."));

    public static bool NeedsRuntimeType(Type type)
        => type == typeof(object) || type.IsAbstract || type.IsInterface;

    public static Type UnwrapNullable(Type type)
        => Nullable.GetUnderlyingType(type) ?? type;

    public static bool TryGetDictionaryTypes(Type type, out Type keyType, out Type valueType)
    {
        var dictionaryType = FindGenericInterface(type, typeof(IDictionary<,>))
            ?? FindGenericInterface(type, typeof(IReadOnlyDictionary<,>));

        if (dictionaryType is null)
        {
            keyType = typeof(object);
            valueType = typeof(object);
            return false;
        }

        var arguments = dictionaryType.GetGenericArguments();
        keyType = arguments[0];
        valueType = arguments[1];
        return true;
    }

    public static bool TryGetCollectionElementType(Type type, out Type elementType)
    {
        if (type.IsArray)
        {
            elementType = type.GetElementType()!;
            return true;
        }

        var enumerableType = FindGenericInterface(type, typeof(IEnumerable<>));
        if (enumerableType is null || type == typeof(string))
        {
            elementType = typeof(object);
            return false;
        }

        elementType = enumerableType.GetGenericArguments()[0];
        return true;
    }

    public static object CreateObject(Type type)
        => Activator.CreateInstance(type, nonPublic: true)
            ?? throw new InvalidOperationException($"Could not create an instance of '{type.FullName}'.");

    private static Type? FindGenericInterface(Type type, Type genericTypeDefinition)
    {
        if (type.IsGenericType && type.GetGenericTypeDefinition() == genericTypeDefinition)
        {
            return type;
        }

        return type.GetInterfaces()
            .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == genericTypeDefinition);
    }

    private static SerializableMember[] CreateSerializableMembers(Type type)
    {
        var properties = type.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(p => p.GetIndexParameters().Length == 0 && p.GetMethod is not null && p.SetMethod is not null)
            .Select(p => new SerializableMember(p.Name, p.PropertyType, p))
            .ToArray();

        var propertyBackingFields = properties
            .Select(p => $"<{p.Name}>k__BackingField")
            .ToHashSet(StringComparer.Ordinal);

        var fields = type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(f => f.GetCustomAttribute<NonSerializedAttribute>() is null && !propertyBackingFields.Contains(f.Name))
            .Select(f => new SerializableMember(f.Name, f.FieldType, f));

        return fields.Concat(properties)
            .OrderBy(m => m.Name, StringComparer.Ordinal)
            .ToArray();
    }
}

internal sealed class SerializableMember
{
    private readonly FieldInfo? _field;
    private readonly PropertyInfo? _property;

    public SerializableMember(string name, Type type, FieldInfo field)
    {
        Name = name;
        Type = type;
        _field = field;
    }

    public SerializableMember(string name, Type type, PropertyInfo property)
    {
        Name = name;
        Type = type;
        _property = property;
    }

    public string Name { get; }

    public Type Type { get; }

    public object? GetValue(object target)
        => _field is not null ? _field.GetValue(target) : _property!.GetValue(target);

    public void SetValue(object target, object? value)
    {
        if (_field is not null)
        {
            _field.SetValue(target, value);
        }
        else
        {
            _property!.SetValue(target, value);
        }
    }
}
