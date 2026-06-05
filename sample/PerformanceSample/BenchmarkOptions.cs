namespace PerformanceSample;

internal sealed record BenchmarkOptions(
    int HostIterations,
    int IsolatedIterations,
    int ZeroArgIterations,
    int PayloadIterations,
    int StartupIterations,
    int ConcurrentHosts,
    string CacheDirectory,
    bool ClearCache,
    bool ClearCacheBetweenScenarios)
{
    private const int DefaultHostIterations = 10_000_000;
    private const int DefaultIsolatedIterations = 2_000;
    private const int DefaultZeroArgIterations = 2_000;
    private const int DefaultPayloadIterations = 200;
    private const int DefaultStartupIterations = 5;
    private const int DefaultConcurrentHosts = 8;

    public const string Usage =
        """
        Usage:
          dotnet run -c Release --project sample\PerformanceSample\PerformanceSample.csproj -- [options]

        Options:
          --host-iterations <n>      Direct host calls to measure. Default: 10000000.
          --isolated-iterations <n>  Isolated warm-runtime calls to measure. Default: 2000.
          --zero-arg-iterations <n>  Zero-arg int return calls to measure. Default: 2000.
          --generic-iterations <n>   Alias for --zero-arg-iterations.
          --payload-iterations <n>   Payload-return calls to measure. Default: 200.
          --startup-iterations <n>   Startup samples per scenario. Default: 5.
          --concurrent-hosts <n>     Parallel warm-cache hosts to construct. Default: 8.
          --cache-directory <path>   Module cache directory. Default: output/module-cache.
          --keep-cache               Reuse the cache directory from previous runs.
          --help                     Show this help.
        """;

    public static BenchmarkOptions Parse(string[] args)
    {
        var options = new BenchmarkOptions(
            DefaultHostIterations,
            DefaultIsolatedIterations,
            DefaultZeroArgIterations,
            DefaultPayloadIterations,
            DefaultStartupIterations,
            DefaultConcurrentHosts,
            Path.Combine(AppContext.BaseDirectory, "module-cache"),
            ClearCache: true,
            ClearCacheBetweenScenarios: true);

        for (var i = 0; i < args.Length; i++)
        {
            options = args[i] switch
            {
                "--host-iterations" => options with { HostIterations = ParsePositiveInt(args, ref i) },
                "--isolated-iterations" => options with { IsolatedIterations = ParsePositiveInt(args, ref i) },
                "--zero-arg-iterations" or "--generic-iterations" => options with { ZeroArgIterations = ParsePositiveInt(args, ref i) },
                "--payload-iterations" => options with { PayloadIterations = ParsePositiveInt(args, ref i) },
                "--startup-iterations" => options with { StartupIterations = ParsePositiveInt(args, ref i) },
                "--concurrent-hosts" => options with { ConcurrentHosts = ParsePositiveInt(args, ref i) },
                "--cache-directory" => options with { CacheDirectory = ParseString(args, ref i) },
                "--keep-cache" => options with { ClearCache = false, ClearCacheBetweenScenarios = false },
                "--help" or "-h" => throw new HelpRequestedException(),
                _ => throw new ArgumentException($"Unknown argument '{args[i]}'."),
            };
        }

        return options;
    }

    private static int ParsePositiveInt(string[] args, ref int index)
    {
        var optionName = args[index];
        var value = ParseString(args, ref index);
        if (!int.TryParse(value, out var result) || result <= 0)
        {
            throw new ArgumentException($"Expected a positive integer after '{optionName}'.");
        }

        return result;
    }

    private static string ParseString(string[] args, ref int index)
    {
        var optionName = args[index];
        if (++index >= args.Length)
        {
            throw new ArgumentException($"Missing value after '{optionName}'.");
        }

        return args[index];
    }
}

internal sealed class HelpRequestedException : Exception;
