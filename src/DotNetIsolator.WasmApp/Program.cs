// This entrypoint starts the .NET runtime and warms up serialization. The real
// work happens when the host uses exports to pass in assemblies and invoke code.

using DotNetIsolator.Internal;

AppContext.SetSwitch("System.Resources.UseSystemResourceKeys", true);
AppContext.SetSwitch("System.Globalization.Invariant", true);

// Warm up the serialization code paths.
var captured = new List<string> { "a", "b" };
var lambda = (string a, bool b) => { Console.WriteLine(a + b + captured.Count + System.Runtime.InteropServices.RuntimeInformation.OSArchitecture); };
var serialized2 = MessagePackCompatibility.SerializeTypeless(lambda.Target!);
var deserialized2 = DotNetIsolator.WasmApp.Serialization.Deserialize(serialized2);
