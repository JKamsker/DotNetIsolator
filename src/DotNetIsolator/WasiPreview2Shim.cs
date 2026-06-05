using Wasmtime;

namespace DotNetIsolator;

internal static class WasiPreview2Shim
{
    private const string ImportPrefix = "wasi:";
    private const string Preview1ModuleName = "wasi_snapshot_preview1";
    private const int WasiErrnoBadFileDescriptor = 8;

    public static void DefineMissingImports(Linker linker, Module module)
    {
        var definedFunctions = new HashSet<(string Module, string Name)>();

        foreach (var functionImport in module.Imports.OfType<FunctionImport>())
        {
            if (!ShouldDefineImport(functionImport))
            {
                continue;
            }

            if (!definedFunctions.Add((functionImport.ModuleName, functionImport.Name)))
            {
                continue;
            }

            var parameterKinds = functionImport.Parameters.ToArray();
            var resultKinds = functionImport.Results.ToArray();
            var returnsBadFileDescriptor = IsAdapterBadFileDescriptorImport(functionImport);
            var trapOnCall = !returnsBadFileDescriptor && ShouldTrapOnCall(functionImport);
            var importName = $"{functionImport.ModuleName}::{functionImport.Name}";

            linker.DefineFunction(
                functionImport.ModuleName,
                functionImport.Name,
                (_, _, results) =>
                {
                    if (trapOnCall)
                    {
                        throw new NotSupportedException(
                            $"The WASI Preview 2 import '{importName}' is not supported by DotNetIsolator's core-module host.");
                    }

                    if (returnsBadFileDescriptor)
                    {
                        WriteInt32Result(WasiErrnoBadFileDescriptor, results);
                    }
                    else
                    {
                        WriteDefaultResults(resultKinds, results);
                    }
                },
                parameterKinds,
                resultKinds);
        }
    }

    private static bool ShouldDefineImport(FunctionImport functionImport)
        => functionImport.ModuleName.StartsWith(ImportPrefix, StringComparison.Ordinal)
            || IsAdapterBadFileDescriptorImport(functionImport);

    private static bool IsAdapterBadFileDescriptorImport(FunctionImport functionImport)
        => functionImport.ModuleName == Preview1ModuleName
            && functionImport.Name is "adapter_open_badfd" or "adapter_close_badfd";

    private static bool ShouldTrapOnCall(FunctionImport functionImport)
    {
        if (functionImport.Name.StartsWith("[resource-drop]", StringComparison.Ordinal))
        {
            return false;
        }

        return functionImport.ModuleName.StartsWith("wasi:http/", StringComparison.Ordinal)
            || functionImport.ModuleName.StartsWith("wasi:sockets/", StringComparison.Ordinal);
    }

    private static void WriteDefaultResults(IReadOnlyList<ValueKind> resultKinds, Span<ValueBox> results)
    {
        for (var i = 0; i < resultKinds.Count; i++)
        {
            results[i] = resultKinds[i] switch
            {
                ValueKind.Int32 => 0,
                ValueKind.Int64 => 0L,
                ValueKind.Float32 => 0f,
                ValueKind.Float64 => 0d,
                ValueKind.V128 => default(V128),
                _ => throw new NotSupportedException($"Cannot provide a default value for WASI import result kind '{resultKinds[i]}'."),
            };
        }
    }

    private static void WriteInt32Result(int value, Span<ValueBox> results)
    {
        if (results.Length != 1)
        {
            throw new InvalidOperationException("Expected exactly one i32 result.");
        }

        results[0] = value;
    }
}
