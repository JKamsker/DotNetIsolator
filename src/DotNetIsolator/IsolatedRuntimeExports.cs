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
        Action<int, int, int> invokeByteArrayMethod,
        Action<int, int, int> invokeBlittableArrayMethod,
        Func<int, int, long> invokeInt32MethodNoArgsPacked,
        Func<int, int, int, long> invokeInt32MethodPacked,
        Func<int, int, int> invokeVoidMethod,
        Func<int, int, int, int> invokeVoidMethodInt32,
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
        InvokeByteArrayMethod = invokeByteArrayMethod;
        InvokeBlittableArrayMethod = invokeBlittableArrayMethod;
        InvokeInt32MethodNoArgsPacked = invokeInt32MethodNoArgsPacked;
        InvokeInt32MethodPacked = invokeInt32MethodPacked;
        InvokeVoidMethod = invokeVoidMethod;
        InvokeVoidMethodInt32 = invokeVoidMethodInt32;
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
    public Action<int, int, int> InvokeByteArrayMethod { get; }
    public Action<int, int, int> InvokeBlittableArrayMethod { get; }
    public Func<int, int, long> InvokeInt32MethodNoArgsPacked { get; }
    public Func<int, int, int, long> InvokeInt32MethodPacked { get; }
    public Func<int, int, int> InvokeVoidMethod { get; }
    public Func<int, int, int, int> InvokeVoidMethodInt32 { get; }
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
        var invokeByteArrayMethod = instance.GetAction<int, int, int>("dotnetisolator_invoke_byte_array")
            ?? throw new InvalidOperationException("Missing required export 'dotnetisolator_invoke_byte_array'");
        var invokeBlittableArrayMethod = instance.GetAction<int, int, int>("dotnetisolator_invoke_blittable_array")
            ?? throw new InvalidOperationException("Missing required export 'dotnetisolator_invoke_blittable_array'");
        var invokeInt32MethodNoArgsPacked = instance.GetFunction<int, int, long>("dotnetisolator_invoke_i32_packed")
            ?? throw new InvalidOperationException("Missing required export 'dotnetisolator_invoke_i32_packed'");
        var invokeInt32MethodPacked = instance.GetFunction<int, int, int, long>("dotnetisolator_invoke_i32_i32_packed")
            ?? throw new InvalidOperationException("Missing required export 'dotnetisolator_invoke_i32_i32_packed'");
        var invokeVoidMethod = instance.GetFunction<int, int, int>("dotnetisolator_invoke_void")
            ?? throw new InvalidOperationException("Missing required export 'dotnetisolator_invoke_void'");
        var invokeVoidMethodInt32 = instance.GetFunction<int, int, int, int>("dotnetisolator_invoke_i32_void")
            ?? throw new InvalidOperationException("Missing required export 'dotnetisolator_invoke_i32_void'");
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
            invokeByteArrayMethod,
            invokeBlittableArrayMethod,
            invokeInt32MethodNoArgsPacked,
            invokeInt32MethodPacked,
            invokeVoidMethod,
            invokeVoidMethodInt32,
            invokeDotNetMethod,
            releaseObject,
            start);
    }
}
