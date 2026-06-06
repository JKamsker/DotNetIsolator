using MessagePack;
using MessagePack.Formatters;

namespace DotNetIsolator.Internal;

internal sealed class GuestToHostCallFormatter : IMessagePackFormatter<GuestToHostCall>
{
    public void Serialize(ref MessagePackWriter writer, GuestToHostCall value, MessagePackSerializerOptions options)
    {
        var args = value.Args ?? Array.Empty<byte[]?>();
        var argsLength = value.ArgsLength == 0 && args.Length != 0 ? args.Length : value.ArgsLength;
        if ((uint)argsLength > (uint)args.Length)
        {
            throw new InvalidOperationException("Guest-to-host call argument count exceeds the argument buffer length.");
        }

        writer.WriteArrayHeader(3);
        FormatterResolverExtensions.GetFormatterWithVerify<string>(options.Resolver)
            .Serialize(ref writer, value.CallbackName, options);
        WriteArgs(ref writer, args, argsLength, options);
        writer.Write(value.IsRawCall);
    }

    public GuestToHostCall Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options)
    {
        if (reader.TryReadNil())
        {
            throw new InvalidOperationException("typecode is null, struct not supported");
        }

        options.Security.DepthStep(ref reader);
        try
        {
            var length = reader.ReadArrayHeader();
            var result = new GuestToHostCall
            {
                Args = Array.Empty<byte[]?>(),
            };

            for (var i = 0; i < length; i++)
            {
                switch (i)
                {
                    case 0:
                        result.CallbackName = FormatterResolverExtensions.GetFormatterWithVerify<string>(options.Resolver)
                            .Deserialize(ref reader, options);
                        break;
                    case 1:
                        result.Args = ReadArgs(ref reader, options, out result.ArgsLength);
                        break;
                    case 2:
                        result.IsRawCall = reader.ReadBoolean();
                        break;
                    default:
                        reader.Skip();
                        break;
                }
            }

            return result;
        }
        finally
        {
            reader.Depth--;
        }
    }

    private static void WriteArgs(
        ref MessagePackWriter writer,
        byte[]?[] args,
        int argsLength,
        MessagePackSerializerOptions options)
    {
        var formatter = FormatterResolverExtensions.GetFormatterWithVerify<byte[]>(options.Resolver);
        writer.WriteArrayHeader(argsLength);
        for (var i = 0; i < argsLength; i++)
        {
            var arg = args[i];
            if (arg is null)
            {
                writer.WriteNil();
            }
            else
            {
                formatter.Serialize(ref writer, arg, options);
            }
        }
    }

    private static byte[]?[] ReadArgs(
        ref MessagePackReader reader,
        MessagePackSerializerOptions options,
        out int argsLength)
    {
        if (reader.TryReadNil())
        {
            argsLength = 0;
            return Array.Empty<byte[]?>();
        }

        options.Security.DepthStep(ref reader);
        try
        {
            argsLength = reader.ReadArrayHeader();
            if (argsLength == 0)
            {
                return Array.Empty<byte[]?>();
            }

            var args = new byte[]?[argsLength];
            var formatter = FormatterResolverExtensions.GetFormatterWithVerify<byte[]>(options.Resolver);
            for (var i = 0; i < args.Length; i++)
            {
                args[i] = reader.TryReadNil()
                    ? null
                    : formatter.Deserialize(ref reader, options);
            }

            return args;
        }
        finally
        {
            reader.Depth--;
        }
    }
}

internal sealed class GuestToHostCallResolver : IFormatterResolver
{
    public static readonly IFormatterResolver Instance = new GuestToHostCallResolver();

    private GuestToHostCallResolver()
    {
    }

    public IMessagePackFormatter<T>? GetFormatter<T>()
        => FormatterCache<T>.Formatter;

    private static class FormatterCache<T>
    {
        public static readonly IMessagePackFormatter<T>? Formatter =
            typeof(T) == typeof(GuestToHostCall)
                ? (IMessagePackFormatter<T>)(object)new GuestToHostCallFormatter()
                : null;
    }
}
