namespace DotNetIsolator.Internal;

internal static class ObjectGraphObjectSerializer
{
    private const int PositionalObjectMarker = -1;

    public static void Write(BinaryWriter writer, Type type, object value, int depth)
    {
        var members = ObjectGraphTypes.GetSerializableMembers(type);
        writer.Write(PositionalObjectMarker);
        writer.Write(members.Length);

        foreach (var member in members)
        {
            ObjectGraphSerializer.WriteValue(writer, member.Type, member.GetValue(value), depth + 1);
        }
    }

    public static object Read(BinaryReader reader, Type type, int depth)
    {
        var result = ObjectGraphTypes.CreateObject(type);
        var members = ObjectGraphTypes.GetSerializableMembers(type);
        var count = reader.ReadInt32();

        return count == PositionalObjectMarker
            ? ReadPositional(reader, result, members, depth)
            : ReadNamed(reader, result, members, count, depth);
    }

    private static object ReadPositional(BinaryReader reader, object result, SerializableMember[] members, int depth)
    {
        var count = reader.ReadInt32();
        if (count != members.Length)
        {
            throw new InvalidOperationException("Serialized object member count does not match the expected type.");
        }

        foreach (var member in members)
        {
            var value = ObjectGraphSerializer.ReadValue(reader, member.Type, depth + 1);
            if (IsAssignable(member.Type, value))
            {
                member.SetValue(result, value);
            }
        }

        return result;
    }

    private static object ReadNamed(BinaryReader reader, object result, SerializableMember[] members, int count, int depth)
    {
        ObjectGraphPrimitiveCollections.EnsureCount(count);
        var membersByName = members.ToDictionary(m => m.Name, StringComparer.Ordinal);
        for (var i = 0; i < count; i++)
        {
            var memberName = reader.ReadString();
            var serializedType = ObjectGraphTypes.ReadType(reader);
            var value = ObjectGraphSerializer.ReadValue(reader, serializedType, depth + 1);
            if (membersByName.TryGetValue(memberName, out var member) && IsAssignable(member.Type, value))
            {
                member.SetValue(result, value);
            }
        }

        return result;
    }

    private static bool IsAssignable(Type type, object? value)
        => value is null || ObjectGraphTypes.UnwrapNullable(type).IsInstanceOfType(value);
}
