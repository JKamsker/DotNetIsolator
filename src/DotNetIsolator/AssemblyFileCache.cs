using System.Collections.Concurrent;

namespace DotNetIsolator;

internal static class AssemblyFileCache
{
    private static readonly ConcurrentDictionary<FileCacheKey, byte[]> Files = new();

    public static byte[]? ReadAllBytesIfExists(string path)
    {
        var fileInfo = new FileInfo(path);
        if (!fileInfo.Exists)
        {
            return null;
        }

        var cacheKey = new FileCacheKey(
            fileInfo.FullName,
            fileInfo.Length,
            fileInfo.LastWriteTimeUtc.Ticks);

        return Files.GetOrAdd(cacheKey, static key => File.ReadAllBytes(key.Path));
    }

    private readonly record struct FileCacheKey(string Path, long Length, long LastWriteTimeUtcTicks);
}
