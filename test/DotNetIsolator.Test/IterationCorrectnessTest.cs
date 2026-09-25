using DotNetIsolator.Test;
using Xunit;

namespace DotNetIsolator;

public sealed class IterationCorrectnessTest : IDisposable
{
    private readonly IsolatedRuntime _runtime = new(SharedHost.Instance);

    [Fact]
    public void GenericCallbackBuffersSurviveNestedSerializationAndSizeChanges()
    {
        _runtime.RegisterCallback("inner", (string value) => value + "!");
        _runtime.RegisterCallback("outer", (Payload value) => new Payload
        {
            Data = value.Data,
            OnRead = () => Assert.Equal("nested!", _runtime.Invoke(() => DotNetIsolatorHost.Invoke<string>("inner", "nested"))),
        });
        Assert.Equal(32_768, _runtime.Invoke(() =>
        {
            var result = DotNetIsolatorHost.Invoke<Payload>("outer", new Payload { Data = new byte[32_768] });
            return result.Data.Length;
        }));
        Assert.Equal(new byte[] { 3, 4 }, _runtime.Invoke(() => DotNetIsolatorHost.Invoke<Payload>("outer", new Payload { Data = new byte[] { 3, 4 } }).Data));
        Assert.Empty(_runtime.Invoke(() => DotNetIsolatorHost.Invoke<Payload>("outer", new Payload { Data = Array.Empty<byte>() }).Data));
    }

    [Fact]
    public void FailedCallbackSerializationReleasesBorrowedWriters()
    {
        var first = true;
        _runtime.RegisterCallback("result", (string value) => new Payload
        {
            Data = new byte[] { 7 },
            OnRead = () => { if (first) { first = false; throw new Exception("serializer secret"); } },
        });
        Assert.DoesNotContain("serializer secret", Assert.Throws<IsolatedException>(() => _runtime.Invoke(() => DotNetIsolatorHost.Invoke<Payload>("result", "fail"))).Message);
        Assert.Equal(new byte[] { 7 }, _runtime.Invoke(() => DotNetIsolatorHost.Invoke<Payload>("result", "succeed").Data));
    }

    [Fact]
    public void BatchLoopHandlesNarrowElementsMixedSizesAndWideBits()
    {
        using var target = _runtime.CreateObject<Target>();
        var chars = target.FindMethod(nameof(Target.CharIdentity), 1);
        Assert.Equal(new[] { '\0', '\ud800', '\uffff' }, chars.InvokeBatch<char, char>(target, new[] { '\0', '\ud800', '\uffff' }));
        var bools = target.FindMethod(nameof(Target.Not), 1);
        Assert.Equal(new[] { false, true, false }, bools.InvokeBatch<bool, bool>(target, new[] { true, false, true }));
        var widen = target.FindMethod(nameof(Target.Widen), 1);
        Assert.Equal(new[] { -1L, 0L, 254L }, widen.InvokeBatch<byte, long>(target, new byte[] { 0, 1, 255 }));
        var bits = target.FindMethod(nameof(Target.DoubleBits), 1);
        var patterns = new[] { long.MinValue, 0x7ff8000000001234L, long.MaxValue };
        Assert.Equal(patterns, bits.InvokeBatch<double, long>(target, patterns.Select(BitConverter.Int64BitsToDouble).ToArray()));
        Assert.Empty(widen.InvokeBatch<byte, long>(target, Array.Empty<byte>()));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(256)]
    [InlineData(4100)]
    public void NonPrimitiveCollectionsRoundtripAcrossInitialCapacityLimit(int count)
    {
        using var target = _runtime.CreateObject<Target>();
        var values = Enumerable.Range(0, count).Select(i => i % 3 == 0 ? null : $"value-{i}").ToList();
        var result = target.Invoke<List<string?>, List<string?>>(nameof(Target.List), values);
        Assert.Equal(values, result);
        Assert.NotSame(values, result);
        var dictionary = Enumerable.Range(0, count).ToDictionary(i => $"key-{i}", i => values[i]);
        var copied = target.Invoke<Dictionary<string, string?>, Dictionary<string, string?>>(nameof(Target.Dictionary), dictionary);
        Assert.Equal(dictionary.OrderBy(x => x.Key), copied.OrderBy(x => x.Key));
    }

    [Fact]
    public void GenericOnlyReadOnlyDictionaryEnumerationKeepsItsWireFormat()
    {
        _runtime.RegisterCallback("readonly", () => (IReadOnlyDictionary<string, int>)new GenericOnlyMap());
        var values = _runtime.Invoke(() =>
        {
            var map = DotNetIsolatorHost.Invoke<IReadOnlyDictionary<string, int>>("readonly");
            return new[] { map.Count, map["one"], map["two"] };
        });
        Assert.Equal(new[] { 2, 1, 2 }, values);
    }

    [Fact]
    public void StringCollectionsAndNativeResultsKeepUtf8AndNullSemantics()
    {
        using var target = _runtime.CreateObject<Target>();
        string?[] input = { null, "", "a\0b", "λ😀", "\ud800x\udc00", new string('λ', 500) };
        var expected = input.Select(x => x is null ? null : System.Text.Encoding.UTF8.GetString(System.Text.Encoding.UTF8.GetBytes(x))).ToArray();
        Assert.Equal(expected, target.Invoke<string?[], string?[]>(nameof(Target.Strings), input));
        Assert.Null(target.Invoke<string?>(nameof(Target.NullText)));
        Assert.Equal("", target.Invoke<string>(nameof(Target.EmptyText)));
        Assert.Equal("\ufffdx\ufffdλ😀\0", target.Invoke<string>(nameof(Target.UnicodeText)));
        Assert.Equal(new string('λ', 16384), target.Invoke<string>(nameof(Target.LongText)));
    }

    [Fact]
    public void StringLeafCodecPreservesDepthChecksAndExistingWireBytes()
    {
        var codec = typeof(IsolatedRuntime).Assembly.GetType("DotNetIsolator.Internal.ObjectGraphCollections")!;
        var write = codec.GetMethod("WriteCollection")!;
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        string?[] values = { null, "", "λ😀" };
        write.Invoke(null, new object[] { writer, values, typeof(string), 0 });
        using var expected = new MemoryStream();
        using (var expectedWriter = new BinaryWriter(expected, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            expectedWriter.Write(3);
            expectedWriter.Write(false);
            expectedWriter.Write(true); expectedWriter.Write("");
            expectedWriter.Write(true); expectedWriter.Write("λ😀");
        }
        Assert.Equal(expected.ToArray(), stream.ToArray());
        var failure = Assert.Throws<System.Reflection.TargetInvocationException>(() => write.Invoke(null, new object[] { writer, values, typeof(string), 128 }));
        Assert.IsType<InvalidOperationException>(failure.InnerException);
        // An empty collection has no leaf at depth + 1.
        write.Invoke(null, new object[] { writer, Array.Empty<string>(), typeof(string), 128 });
        using var reader = new BinaryReader(new MemoryStream(expected.ToArray()));
        var read = codec.GetMethod("ReadCollection")!;
        failure = Assert.Throws<System.Reflection.TargetInvocationException>(() => read.Invoke(null, new object[] { reader, typeof(string[]), typeof(string), 128 }));
        Assert.IsType<InvalidOperationException>(failure.InnerException);
    }

    [Fact]
    public void NativeStringArgumentsHandleNullUnicodeScalarArrayAndVoidResults()
    {
        using var target = _runtime.CreateObject<Target>();
        foreach (var text in new string?[] { null, "", "a\0b", "λ😀", "\ud800x\udc00", new string('λ', 1024) })
        {
            var expected = text is null ? null : System.Text.Encoding.UTF8.GetString(System.Text.Encoding.UTF8.GetBytes(text));
            Assert.Equal(expected, target.Invoke<string?, string?>(nameof(Target.EchoText), text));
            Assert.Equal(expected?.Length ?? -1, target.Invoke<string?, int>(nameof(Target.TextCount), text));
            target.InvokeVoid(nameof(Target.ConsumeText), text);
            Assert.Equal(expected?.Length ?? -1, target.Invoke<int>(nameof(Target.Consumed)));
        }
        Assert.Equal(System.Text.Encoding.UTF8.GetBytes("λ\0"), target.Invoke<string, byte[]>(nameof(Target.TextBytes), "λ\0"));
        Assert.Throws<IsolatedException>(() => target.Invoke<string, bool>(nameof(Target.TextCount), "abc"));
        Assert.Contains("text failure", Assert.Throws<IsolatedException>(() => target.Invoke<string, string>(nameof(Target.ThrowText), "abc")).Message);
        Assert.Equal("ok", target.Invoke<string, string>(nameof(Target.EchoText), "ok"));
    }

    [Fact]
    public void StringArgumentsKeepStaticInterfaceAsyncAndNestedCallsWorking()
    {
        using var target = _runtime.CreateObject<Target>();
        var method = _runtime.GetMethod(typeof(Target), nameof(Target.StaticText), 1);
        Assert.Equal("static", method.Invoke<string, string>(null, "static"));
        Assert.Equal("object:input", target.Invoke<string, string>(nameof(Target.ObjectText), "input"));
        Assert.Equal("async", target.Invoke<string, string>(nameof(Target.AsyncText), "async"));
        _runtime.RegisterCallback("nested-text", (string text) =>
        {
            _runtime.Invoke(() => GC.Collect());
            return target.Invoke<string, string>(nameof(Target.EchoText), text) + "!";
        });
        Assert.Equal("nested!", target.Invoke<string, string>(nameof(Target.NestedText), "nested"));
    }

    [Fact]
    public void BatchDestinationSupportsReuseOverlapAndFailureWithoutPartialWrites()
    {
        using var target = _runtime.CreateObject<Target>();
        var method = target.FindMethod(nameof(Target.Accumulate), 1);
        var destination = new[] { -1, -1, -1, 99 };
        method.InvokeBatch<int, int>(target, new[] { 1, 2, 3 }, destination);
        Assert.Equal(new[] { 1, 3, 6, 99 }, destination);
        Assert.Throws<ArgumentException>(() => method.InvokeBatch<int, int>(target, new[] { 10, 20 }, new int[1]));
        Assert.Equal(6, target.Invoke<int>(nameof(Target.Consumed)));
        Assert.Throws<IsolatedException>(() => method.InvokeBatch<int, int>(target, new[] { 1, -1, 100 }, destination));
        Assert.Equal(new[] { 1, 3, 6, 99 }, destination);
        Assert.Equal(7, target.Invoke<int>(nameof(Target.Consumed)));
        var overlap = new[] { 1, 2, 3 };
        method.InvokeBatch<int, int>(target, overlap, overlap);
        Assert.Equal(new[] { 8, 10, 13 }, overlap);
        method.InvokeBatch<int, int>(target, Array.Empty<int>(), destination);
        Assert.Equal(new[] { 1, 3, 6, 99 }, destination);
    }

    public sealed class GenericOnlyMap : IReadOnlyDictionary<string, int>
    {
        private readonly Dictionary<string, int> _values = new() { ["one"] = 1, ["two"] = 2 };
        public int this[string key] => _values[key];
        public IEnumerable<string> Keys => _values.Keys;
        public IEnumerable<int> Values => _values.Values;
        public int Count => _values.Count;
        public bool ContainsKey(string key) => _values.ContainsKey(key);
        public bool TryGetValue(string key, out int value) => _values.TryGetValue(key, out value);
        public IEnumerator<KeyValuePair<string, int>> GetEnumerator() => _values.GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    public sealed class Payload
    {
        private byte[] _data = Array.Empty<byte>();
        [NonSerialized] public Action? OnRead;
        public byte[] Data { get { OnRead?.Invoke(); return _data; } set => _data = value; }
    }

    public sealed class Target
    {
        private int _consumed;
        public string? EchoText(string? text) => text;
        public int TextCount(string? text) => text?.Length ?? -1;
        public void ConsumeText(string? text) => _consumed = text?.Length ?? -1;
        public int Consumed() => _consumed;
        public int Accumulate(int value) { if (value < 0) throw new InvalidOperationException("batch failure"); return _consumed += value; }
        public byte[] TextBytes(string text) => System.Text.Encoding.UTF8.GetBytes(text);
        public string ThrowText(string text) => throw new InvalidOperationException("text failure");
        public static string StaticText(string text) => text;
        public string ObjectText(object text) => "object:" + text;
        public async Task<string> AsyncText(string text) { await Task.Yield(); return text; }
        public string NestedText(string text) => DotNetIsolatorHost.Invoke<string>("nested-text", text);
        public string?[] Strings(string?[] values) => values;
        public string? NullText() => null;
        public string EmptyText() => "";
        public string UnicodeText() => "\ud800x\udc00λ😀\0";
        public string LongText() { var text = new string('λ', 16384); GC.Collect(); return text; }
        public char CharIdentity(char value) => value;
        public bool Not(bool value) => !value;
        public long Widen(byte value) => value - 1L;
        public long DoubleBits(double value) => BitConverter.DoubleToInt64Bits(value);
        public List<string?> List(List<string?> values) => values;
        public Dictionary<string, string?> Dictionary(Dictionary<string, string?> values) => values;
    }

    public void Dispose() => _runtime.Dispose();
}
