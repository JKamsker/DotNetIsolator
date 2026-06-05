using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using Wasmtime;

namespace DotNetIsolator;

internal static class PrecompiledModuleCache
{
    private const string CacheVersion = "v1";
    private const string FileExtension = ".cwasm";
    private static readonly ConcurrentDictionary<string, string> ModuleHashes = new();
    private static readonly ConcurrentDictionary<string, object> CacheLocks = new();

    public static Module LoadOrCompile(Engine engine, string modulePath, IsolatedRuntimeHostOptions options)
    {
        if (!options.UsePrecompiledModuleCache)
        {
            return Module.FromFile(engine, modulePath);
        }

        var cachePath = GetCachePath(modulePath, options);
        var cachedModule = TryLoadFromCache(engine, modulePath, cachePath);
        if (cachedModule is not null)
        {
            return cachedModule;
        }

        var cacheLock = CacheLocks.GetOrAdd(cachePath, _ => new object());
        lock (cacheLock)
        {
            cachedModule = TryLoadFromCache(engine, modulePath, cachePath);
            if (cachedModule is not null)
            {
                return cachedModule;
            }

            var module = Module.FromFile(engine, modulePath);
            TryStoreInCache(module, cachePath);
            return module;
        }
    }

    private static Module? TryLoadFromCache(Engine engine, string modulePath, string cachePath)
    {
        if (!File.Exists(cachePath))
        {
            return null;
        }

        try
        {
            return Module.DeserializeFile(engine, Path.GetFileName(modulePath), cachePath);
        }
        catch (Exception ex) when (IsCacheFailure(ex))
        {
            TryDelete(cachePath);
            return null;
        }
    }

    private static void TryStoreInCache(Module module, string cachePath)
    {
        var tempPath = $"{cachePath}.{Guid.NewGuid():N}.tmp";

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
            File.WriteAllBytes(tempPath, module.Serialize());
            File.Move(tempPath, cachePath, overwrite: true);
        }
        catch (Exception ex) when (IsCacheFailure(ex))
        {
            // Cache writes are opportunistic; the compiled module is still usable.
        }
        finally
        {
            TryDelete(tempPath);
        }
    }

    private static string GetCachePath(string modulePath, IsolatedRuntimeHostOptions options)
    {
        var cacheDirectory = string.IsNullOrWhiteSpace(options.PrecompiledModuleCacheDirectory)
            ? Path.Combine(Path.GetTempPath(), "DotNetIsolator", "wasmtime-module-cache")
            : options.PrecompiledModuleCacheDirectory;

        return Path.Combine(cacheDirectory, $"{ComputeCacheKey(modulePath, options)}{FileExtension}");
    }

    private static string ComputeCacheKey(string modulePath, IsolatedRuntimeHostOptions options)
    {
        var moduleHash = GetModuleHash(modulePath);
        var wasmtimeAssemblyName = typeof(Module).Assembly.GetName();

        var keyMaterial = string.Join('\n',
            CacheVersion,
            moduleHash,
            wasmtimeAssemblyName.Name,
            wasmtimeAssemblyName.Version?.ToString(),
            RuntimeInformation.RuntimeIdentifier,
            RuntimeInformation.ProcessArchitecture,
            options.UseMemoryInitCopyOnWrite,
            options.UsePoolingAllocator,
            options.PoolingInstanceCapacity,
            options.PoolingMemoryCapacity,
            options.PoolingTableCapacity,
            options.PoolingMaxMemorySize,
            options.PoolingMaxTableElements);

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(keyMaterial))).ToLowerInvariant();
    }

    private static bool IsCacheFailure(Exception ex)
        => ex is IOException
            or UnauthorizedAccessException
            or NotSupportedException
            or SecurityException
            or WasmtimeException;

    private static string GetModuleHash(string modulePath)
    {
        var fileInfo = new FileInfo(modulePath);
        var cacheKey = string.Join('\n',
            fileInfo.FullName,
            fileInfo.Length,
            fileInfo.LastWriteTimeUtc.Ticks);

        return ModuleHashes.GetOrAdd(cacheKey, _ =>
        {
            using var stream = File.OpenRead(modulePath);
            return Convert.ToHexString(SHA256.HashData(stream));
        });
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (IsCacheFailure(ex))
        {
            // Leftover temp files do not affect correctness.
        }
    }
}
