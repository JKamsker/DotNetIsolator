using MessagePack;
using MessagePack.Resolvers;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace DotNetIsolator.Internal;

internal static class MessagePackCompatibility
{
    public static readonly MessagePackSerializerOptions GuestToHostCallOptions = CreateOptions(
        CompositeResolver.Create(GeneratedResolver.Instance, BuiltinResolver.Instance));

    public static byte[] SerializeTypeless<T>(T value)
        => ObjectGraphSerializer.SerializeWithType(value);

    public static object? DeserializeTypeless(ReadOnlyMemory<byte> value)
        => ObjectGraphSerializer.DeserializeWithType(value);

    public static object? DeserializeTypeless(Stream stream)
        => ObjectGraphSerializer.DeserializeWithType(stream);

    public static byte[] SerializeObject(Type declaredType, object? value)
        => ObjectGraphSerializer.Serialize(declaredType, value);

    public static T? DeserializeObject<T>(ReadOnlyMemory<byte> value)
        => (T?)ObjectGraphSerializer.Deserialize(typeof(T), value);

    public static T? DeserializeObject<T>(Stream stream)
        => (T?)ObjectGraphSerializer.Deserialize(typeof(T), stream);

    public static object? DeserializeObject(Type declaredType, ReadOnlyMemory<byte> value)
        => ObjectGraphSerializer.Deserialize(declaredType, value);

    private static MessagePackSerializerOptions CreateOptions(IFormatterResolver resolver)
    {
        // MessagePack 3.x initializes MessagePackSecurity with RandomNumberGenerator.Create().
        // That API is unsupported in the .NET WASI runtime, so build equivalent trusted options
        // without touching MessagePack's static defaults.
        var options = (MessagePackSerializerOptions)RuntimeHelpers.GetUninitializedObject(typeof(MessagePackSerializerOptions));
        SetField(typeof(MessagePackSerializerOptions), options, "<Resolver>k__BackingField", resolver);
        SetField(typeof(MessagePackSerializerOptions), options, "<Compression>k__BackingField", MessagePackCompression.None);
        SetField(typeof(MessagePackSerializerOptions), options, "<CompressionMinLength>k__BackingField", 64);
        SetField(typeof(MessagePackSerializerOptions), options, "<SuggestedContiguousMemorySize>k__BackingField", 1024 * 1024);
        SetField(typeof(MessagePackSerializerOptions), options, "<OldSpec>k__BackingField", null);
        SetField(typeof(MessagePackSerializerOptions), options, "<OmitAssemblyVersion>k__BackingField", false);
        SetField(typeof(MessagePackSerializerOptions), options, "<AllowAssemblyVersionMismatch>k__BackingField", false);
        SetField(typeof(MessagePackSerializerOptions), options, "<Security>k__BackingField", CreateTrustedSecurity());
        SetField(typeof(MessagePackSerializerOptions), options, "<SequencePool>k__BackingField", new SequencePool());
        return options;
    }

    private static MessagePackSecurity CreateTrustedSecurity()
    {
        var security = (MessagePackSecurity)RuntimeHelpers.GetUninitializedObject(typeof(MessagePackSecurity));
        SetField(typeof(MessagePackSecurity), security, "<HashCollisionResistant>k__BackingField", false);
        SetField(typeof(MessagePackSecurity), security, "<MaximumObjectGraphDepth>k__BackingField", int.MaxValue);
        return security;
    }

    private static void SetField(
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.NonPublicFields)] Type targetType,
        object target,
        string fieldName,
        object? value)
    {
        var field = targetType.GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(targetType.FullName, fieldName);

        field.SetValue(target, value);
    }
}
