using Wasmtime;

namespace DotNetIsolator;

internal sealed class IsolatedRuntimeExports
{
    private IsolatedRuntimeExports(
        Memory memory,
        Func<int, int> malloc,
        Action<int> free,
        Func<int, int, int, int, int> instantiateDotNetClass,
        Func<int, int, int, int, int, int, int> lookupDotNetMethod,
        Func<int, int, int> deserializeAsDotNetObject,
        Func<int, int, int, int, int, int> invokeInt32Method,
        Action<int> invokeDotNetMethod,
        Action<int> releaseObject,
        Action start)
    {
        Memory = memory;
        Malloc = malloc;
        Free = free;
        InstantiateDotNetClass = instantiateDotNetClass;
        LookupDotNetMethod = lookupDotNetMethod;
        DeserializeAsDotNetObject = deserializeAsDotNetObject;
        InvokeInt32Method = invokeInt32Method;
        InvokeDotNetMethod = invokeDotNetMethod;
        ReleaseObject = releaseObject;
        Start = start;
    }

    public Memory Memory { get; }
    public Func<int, int> Malloc { get; }
    public Action<int> Free { get; }
    public Func<int, int, int, int, int> InstantiateDotNetClass { get; }
    public Func<int, int, int, int, int, int, int> LookupDotNetMethod { get; }
    public Func<int, int, int> DeserializeAsDotNetObject { get; }
    public Func<int, int, int, int, int, int> InvokeInt32Method { get; }
    public Action<int> InvokeDotNetMethod { get; }
    public Action<int> ReleaseObject { get; }
    public Action Start { get; }

    public static IsolatedRuntimeExports Bind(Instance instance)
    {
        var memory = instance.GetMemory("memory")
            ?? throw new InvalidOperationException("Couldn't find memory 'memory'");
        var malloc = instance.GetFunction<int, int>("malloc")
            ?? throw new InvalidOperationException("Missing required export 'malloc'");
        var free = instance.GetAction<int>("free")
            ?? throw new InvalidOperationException("Missing required export 'free'");
        var instantiateDotNetClass = instance.GetFunction<int, int, int, int, int>("dotnetisolator_instantiate_class")
            ?? throw new InvalidOperationException("Missing required export 'dotnetisolator_instantiate_class'");
        var lookupDotNetMethod = instance.GetFunction<int, int, int, int, int, int, int>("dotnetisolator_lookup_method")
            ?? throw new InvalidOperationException("Missing required export 'dotnetisolator_lookup_method'");
        var deserializeAsDotNetObject = instance.GetFunction<int, int, int>("dotnetisolator_deserialize_object")
            ?? throw new InvalidOperationException("Missing required export 'dotnetisolator_deserialize_object'");
        var invokeInt32Method = instance.GetFunction<int, int, int, int, int, int>("dotnetisolator_invoke_i32_i32")
            ?? throw new InvalidOperationException("Missing required export 'dotnetisolator_invoke_i32_i32'");
        var invokeDotNetMethod = instance.GetAction<int>("dotnetisolator_invoke_method")
            ?? throw new InvalidOperationException("Missing required export 'dotnetisolator_invoke_method'");
        var releaseObject = instance.GetAction<int>("dotnetisolator_release_object")
            ?? throw new InvalidOperationException("Missing required export 'dotnetisolator_release_object'");
        var start = instance.GetAction("_start")
            ?? throw new InvalidOperationException("Couldn't find export '_start'");

        return new IsolatedRuntimeExports(
            memory,
            malloc,
            free,
            instantiateDotNetClass,
            lookupDotNetMethod,
            deserializeAsDotNetObject,
            invokeInt32Method,
            invokeDotNetMethod,
            releaseObject,
            start);
    }
}
