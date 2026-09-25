using DotNetIsolator.Test;
using Xunit;

namespace DotNetIsolator;

public sealed class FastPathImprovementsTest : IDisposable
{
    private readonly IsolatedRuntime _runtime = new(SharedHost.Instance);

    [Fact]
    public void MixedScalarAritiesPreserveValuesAndExceptions()
    {
        var obj = _runtime.CreateObject<Target>();
        Assert.Equal(-7L, obj.Invoke<int, long, long>(nameof(Target.Add2), -10, 3));
        Assert.Equal(4.5, obj.Invoke<short, double, byte, double>(nameof(Target.Add3), -3, 5.5, 2));
        Assert.Equal(ulong.MaxValue, obj.Invoke<bool, char, float, ulong, ulong>(nameof(Target.Pick4), true, '\ud800', float.NaN, ulong.MaxValue));
        Assert.Throws<IsolatedException>(() => obj.Invoke<int, double, long>(nameof(Target.Add2), 1, 2));
        Assert.Throws<IsolatedException>(() => obj.Invoke<int, long, double>(nameof(Target.Add2), 1, 2));
        Assert.Contains("scalar failure", Assert.Throws<IsolatedException>(() => obj.Invoke<long, double>(nameof(Target.ThrowScalar), 0)).Message);
        Assert.Contains("multi failure", Assert.Throws<IsolatedException>(() => obj.Invoke<int, int, int>(nameof(Target.Throw2), 1, 2)).Message);
        Assert.Equal(-7L, obj.Invoke<int, long, long>(nameof(Target.Add2), -10, 3));
    }

    [Fact]
    public void MultiValueReturnsPreserveAll64Bits()
    {
        var obj = _runtime.CreateObject<Target>();
        foreach (var bits in new[] { long.MinValue, long.MaxValue, -1L, 0L, 0x7ff8000000001234L })
        {
            var value = BitConverter.Int64BitsToDouble(bits);
            Assert.Equal(bits, BitConverter.DoubleToInt64Bits(obj.Invoke<double, double>(nameof(Target.Identity), value)));
        }
    }

    [Fact]
    public void MultipleScalarArgumentsSupportVoidAndStaticCalls()
    {
        var obj = _runtime.CreateObject<Target>();
        obj.InvokeVoid<int, long>(nameof(Target.Consume2), 2, 3);
        Assert.Equal(5, obj.Invoke<int>(nameof(Target.Count)));
        obj.InvokeVoid<int, double, short>(nameof(Target.Consume3), 2, 3, 4);
        Assert.Equal(9, obj.Invoke<int>(nameof(Target.Count)));
        obj.InvokeVoid<int, long, short, byte>(nameof(Target.Consume4), 2, 3, 4, 5);
        Assert.Equal(14, obj.Invoke<int>(nameof(Target.Count)));
        Assert.Throws<IsolatedException>(() => obj.InvokeVoid<int, long>(nameof(Target.Add2), 2, 3));
        var method = _runtime.GetMethod(typeof(Target), nameof(Target.StaticAdd), 2);
        Assert.Equal(7, method.Invoke<int, int, int>(null, 3, 4));
    }

    [Fact]
    public void CollectionsUseFreshStorageAndPreserveNullAndEmpty()
    {
        var obj = _runtime.CreateObject<Target>();
        var input = new[] { 1.5, 2.5 };
        Assert.Equal(4, obj.Invoke<double[], double>(nameof(Target.Sum), input));
        var result = obj.Invoke<double[], double[]>(nameof(Target.Mutate), input);
        Assert.Equal(new[] { 1.5, 2.5 }, input);
        Assert.Equal(new[] { 9.0, 2.5 }, result);
        Assert.Empty(obj.Invoke<double[], double[]>(nameof(Target.Mutate), Array.Empty<double>()));
        Assert.Null(obj.Invoke<double[], double[]?>(nameof(Target.NullArray), input));
        Assert.Equal(-1, obj.Invoke<double[]?, int>(nameof(Target.Length), null));
        Assert.Equal(new[] { 2L, 3L }, obj.Invoke<double[], long[]>(nameof(Target.Convert), input));
        obj.InvokeVoid(nameof(Target.Consume), input);
        Assert.Equal(2, obj.Invoke<int>(nameof(Target.Count)));
        Assert.Throws<IsolatedException>(() => obj.Invoke<double[], float>(nameof(Target.Sum), input));
        Assert.Throws<IsolatedException>(() => obj.Invoke<double[], long[]>(nameof(Target.Mutate), input));
        Assert.Throws<IsolatedException>(() => obj.InvokeVoid(nameof(Target.Sum), input));
    }

    [Fact]
    public void ListArgumentsSupportScalarArrayVoidAndGenericResults()
    {
        var obj = _runtime.CreateObject<Target>();
        var input = new List<double> { 1.5, 2.5 };
        Assert.Equal(4, obj.Invoke<List<double>, double>(nameof(Target.SumList), input));
        Assert.Equal(4, obj.Invoke<List<double>, double>(nameof(Target.SumEnumerable), input));
        Assert.Equal(new[] { 1.5, 2.5, 7.0 }, obj.Invoke<List<double>, double[]>(nameof(Target.GrowList), input));
        Assert.Equal(2, input.Count);
        obj.InvokeVoid(nameof(Target.ConsumeList), input);
        Assert.Equal(2, obj.Invoke<int>(nameof(Target.Count)));
        Assert.Equal("2", obj.Invoke<List<double>, string>(nameof(Target.DescribeList), input));
        Assert.Equal(0, obj.Invoke<List<double>, double>(nameof(Target.SumList), new()));
        Assert.Equal(-1, obj.Invoke<List<double>?, double>(nameof(Target.SumList), null));
        Assert.Throws<IsolatedException>(() => obj.Invoke<List<int>, double>(nameof(Target.SumList), new() { 1 }));
    }

    [Fact]
    public void BatchDelegatesRemainBoundToTheirTargetAndStopOnFailure()
    {
        var first = _runtime.CreateObject<Target>();
        var second = _runtime.CreateObject<Target>();
        var method = first.FindMethod(nameof(Target.Accumulate), 1);
        Assert.Equal(new[] { 1, 3, 6 }, method.InvokeBatch<int, int>(first, new[] { 1, 2, 3 }));
        Assert.Equal(new[] { 2, 5 }, method.InvokeBatch<int, int>(second, new[] { 2, 3 }));
        Assert.Equal(new[] { 10 }, method.InvokeBatch<int, int>(first, new[] { 4 }));
        Assert.Throws<IsolatedException>(() => method.InvokeBatch<int, int>(first, new[] { 1, -1, 100 }));
        Assert.Equal(11, first.Invoke<int>(nameof(Target.Count)));
        var staticMethod = _runtime.GetMethod(typeof(Target), nameof(Target.StaticScale), 1);
        Assert.Equal(new[] { 2.5, -5.0 }, staticMethod.InvokeBatch<double, double>(null, new[] { 1.0, -2.0 }));
    }

    [Fact]
    public void TypedCallbacksCacheNamesWithoutChangingSignaturesOrErrors()
    {
        _runtime.RegisterCallback("typed-λ", (double x) => x + .25);
        Assert.Equal(1.75, _runtime.Invoke(() => DotNetIsolatorHost.Invoke<double, double>("typed-λ", 1.5)));
        _runtime.RegisterCallback("other", (int x) => x + 3);
        Assert.Equal(2.75, _runtime.Invoke(() => DotNetIsolatorHost.Invoke<double, double>("typed-λ", 2.5)));
        Assert.Throws<IsolatedException>(() => _runtime.Invoke(() => DotNetIsolatorHost.Invoke<int, double>("typed-λ", 2)));
        _runtime.RegisterCallback("text", (string s) => s + "!");
        Assert.Equal("ok!", _runtime.Invoke(() => DotNetIsolatorHost.Invoke<string, string>("text", "ok")));
        Assert.Throws<IsolatedException>(() => _runtime.Invoke(() => DotNetIsolatorHost.Invoke<int, int>("later", 1)));
        _runtime.RegisterCallback("later", (int x) => x + 1);
        Assert.Equal(2, _runtime.Invoke(() => DotNetIsolatorHost.Invoke<int, int>("later", 1)));
        _runtime.RegisterCallback("secret", (Func<int, int>)(_ => throw new Exception("host secret")));
        Assert.DoesNotContain("host secret", Assert.Throws<IsolatedException>(() => _runtime.Invoke(() => DotNetIsolatorHost.Invoke<int, int>("secret", 1))).Message);
    }

    [Fact]
    public void TypedCallbackCodecCoversEveryPrimitiveKind()
    {
        _runtime.RegisterCallback("bool", (bool value) => value);
        Assert.Equal(true, _runtime.Invoke(() => DotNetIsolatorHost.Invoke<bool, bool>("bool", true)));
        _runtime.RegisterCallback("sbyte", (sbyte value) => value);
        Assert.Equal(sbyte.MinValue, _runtime.Invoke(() => DotNetIsolatorHost.Invoke<sbyte, sbyte>("sbyte", sbyte.MinValue)));
        _runtime.RegisterCallback("byte", (byte value) => value);
        Assert.Equal(byte.MaxValue, _runtime.Invoke(() => DotNetIsolatorHost.Invoke<byte, byte>("byte", byte.MaxValue)));
        _runtime.RegisterCallback("short", (short value) => value);
        Assert.Equal(short.MinValue, _runtime.Invoke(() => DotNetIsolatorHost.Invoke<short, short>("short", short.MinValue)));
        _runtime.RegisterCallback("ushort", (ushort value) => value);
        Assert.Equal(ushort.MaxValue, _runtime.Invoke(() => DotNetIsolatorHost.Invoke<ushort, ushort>("ushort", ushort.MaxValue)));
        _runtime.RegisterCallback("char", (char value) => value);
        Assert.Equal('\ud800', _runtime.Invoke(() => DotNetIsolatorHost.Invoke<char, char>("char", '\ud800')));
        _runtime.RegisterCallback("int", (int value) => value);
        Assert.Equal(int.MinValue, _runtime.Invoke(() => DotNetIsolatorHost.Invoke<int, int>("int", int.MinValue)));
        _runtime.RegisterCallback("uint", (uint value) => value);
        Assert.Equal(uint.MaxValue, _runtime.Invoke(() => DotNetIsolatorHost.Invoke<uint, uint>("uint", uint.MaxValue)));
        _runtime.RegisterCallback("long", (long value) => value);
        Assert.Equal(long.MinValue, _runtime.Invoke(() => DotNetIsolatorHost.Invoke<long, long>("long", long.MinValue)));
        _runtime.RegisterCallback("ulong", (ulong value) => value);
        Assert.Equal(ulong.MaxValue, _runtime.Invoke(() => DotNetIsolatorHost.Invoke<ulong, ulong>("ulong", ulong.MaxValue)));
        _runtime.RegisterCallback("float", (float value) => value);
        Assert.Equal(float.NegativeInfinity, _runtime.Invoke(() => DotNetIsolatorHost.Invoke<float, float>("float", float.NegativeInfinity)));
        _runtime.RegisterCallback("double", (double value) => value);
        Assert.Equal(double.NaN, _runtime.Invoke(() => DotNetIsolatorHost.Invoke<double, double>("double", double.NaN)));
    }

    [Fact]
    public void CallbackIdsBelongToEachRuntime()
    {
        using var other = new IsolatedRuntime(SharedHost.Instance);
        _runtime.RegisterCallback("same", (int x) => x + 1);
        other.RegisterCallback("padding", (int x) => -1);
        other.RegisterCallback("same", (int x) => x + 10);
        Assert.Equal(2, _runtime.Invoke(() => DotNetIsolatorHost.Invoke<int, int>("same", 1)));
        Assert.Equal(11, other.Invoke(() => DotNetIsolatorHost.Invoke<int, int>("same", 1)));
    }

    [Fact]
    public void CallbackIdsResetWithPooledRuntime()
    {
        using var host = new IsolatedRuntimeHost(new IsolatedRuntimeHostOptions
        {
            UseRuntimeMemorySnapshot = true,
            UseInstancePool = true,
            InstancePoolResetMode = InstancePoolResetMode.FullSnapshotRestore,
        }).WithBinDirectoryAssemblyLoader();
        using (var first = new IsolatedRuntime(host))
        {
            first.RegisterCallback("same", (int x) => x + 1);
            Assert.Equal(2, first.Invoke(() => DotNetIsolatorHost.Invoke<int, int>("same", 1)));
        }
        using var second = new IsolatedRuntime(host);
        second.RegisterCallback("padding", (int x) => -1);
        second.RegisterCallback("same", (int x) => x + 10);
        Assert.Equal(11, second.Invoke(() => DotNetIsolatorHost.Invoke<int, int>("same", 1)));
    }

    [Fact]
    public void SerializerLeaseSurvivesReentrantGuestSerialization()
    {
        var obj = _runtime.CreateObject<Target>();
        _runtime.RegisterCallback("serialize-again", (int _) =>
        {
            var nested = obj.Invoke<Payload>(nameof(Target.GetPayload));
            Assert.Equal(new[] { 4, 5, 6 }, nested.Values);
            return 0;
        });
        var result = obj.Invoke<ReentrantPayload>(nameof(Target.GetReentrantPayload));
        Assert.Equal("prefix", result.Name);
        Assert.Equal(new[] { 1, 2, 3 }, result.Values);
    }

    [Fact]
    public void ScalarTupleWrapperSupportsNestedCalls()
    {
        var obj = _runtime.CreateObject<Target>();
        _runtime.RegisterCallback("nested-scalar", (double x) => obj.Invoke<double, double>(nameof(Target.Identity), x + 1));
        Assert.Equal(4.5, obj.Invoke<double, double>(nameof(Target.CallScalar), 3.5));
    }

    [Fact]
    public void BorrowedSerializationUsesLogicalLengthAndIndependentArguments()
    {
        var obj = _runtime.CreateObject<Target>();
        foreach (var length in new[] { 10000, 1, 0, 5000, 3 })
        {
            var payload = new Payload { Values = Enumerable.Range(0, length).ToList(), Name = "first" };
            var second = new Payload { Values = new() { -1 }, Name = "second" };
            var result = obj.Invoke<Payload, Payload, Payload>(nameof(Target.Join), payload, second);
            Assert.Equal("firstsecond", result.Name);
            Assert.Equal(payload.Values.Concat(second.Values), result.Values);
        }
    }

    [Fact]
    public void GenericResultRemainsUsableAfterHostDeserializationFailure()
    {
        var obj = _runtime.CreateObject<Target>();
        Assert.ThrowsAny<Exception>(() => obj.Invoke<RejectingPayload>(nameof(Target.GetPayload)));
        Assert.Equal(new[] { 4, 5, 6 }, obj.Invoke<Payload>(nameof(Target.GetPayload)).Values);
    }

    public void Dispose() => _runtime.Dispose();

    public sealed class Payload
    {
        public List<int> Values { get; set; } = new();
        public string Name { get; set; } = "";
    }

    public sealed class ReentrantPayload
    {
        public static bool InGuest;
        private List<int> _values = new() { 1, 2, 3 };
        public string Name { get; set; } = "prefix";
        public List<int> Values
        {
            get
            {
                if (InGuest) DotNetIsolatorHost.Invoke<int, int>("serialize-again", 0);
                return _values;
            }
            set => _values = value;
        }
    }

    public sealed class RejectingPayload
    {
        public string Name { get; set; } = "";
        public List<int> Values
        {
            get => new();
            set => throw new InvalidOperationException("Reject deserialized values");
        }
    }

    public sealed class Target
    {
        private int _count;
        public long Add2(int a, long b) => a + b;
        public double Add3(short a, double b, byte c) => a + b + c;
        public ulong Pick4(bool a, char b, float c, ulong d) => a && b == '\ud800' && float.IsNaN(c) ? d : 0;
        public double Identity(double value) => value;
        public double ThrowScalar(long value) => throw new Exception("scalar failure");
        public int Throw2(int a, int b) => throw new Exception("multi failure");
        public double Sum(double[] values) => values.Sum();
        public double[] Mutate(double[] values) { if (values.Length > 0) values[0] = 9; return values; }
        public double[]? NullArray(double[] values) => null;
        public int Length(double[]? values) => values?.Length ?? -1;
        public long[] Convert(double[] values) => new[] { 2L, 3L };
        public void Consume(double[] values) => _count = values.Length;
        public int Count() => _count;
        public void Consume2(int a, long b) => _count = a + (int)b;
        public void Consume3(int a, double b, short c) => _count = a + (int)b + c;
        public void Consume4(int a, long b, short c, byte d) => _count = a + (int)b + c + d;
        public double SumEnumerable(IEnumerable<double> values) => values.Sum();
        public double SumList(List<double>? values) => values?.Sum() ?? -1;
        public double[] GrowList(List<double> values) { values.Add(7); return values.ToArray(); }
        public void ConsumeList(List<double> values) => _count = values.Count;
        public string DescribeList(List<double> values) => values.Count.ToString();
        public int Accumulate(int value) { if (value < 0) throw new Exception("batch failure"); return _count += value; }
        public static double StaticScale(double value) => value * 2.5;
        public static int StaticAdd(int a, int b) => a + b;
        public double CallScalar(double value) => DotNetIsolatorHost.Invoke<double, double>("nested-scalar", value);
        public Payload GetPayload() => new() { Values = new() { 4, 5, 6 } };
        public ReentrantPayload GetReentrantPayload() { ReentrantPayload.InGuest = true; return new(); }
        public Payload Join(Payload a, Payload b) => new() { Values = a.Values.Concat(b.Values).ToList(), Name = a.Name + b.Name };
    }
}
