#:sdk Microsoft.NET.Sdk
var input = await Console.In.ReadToEndAsync();
var name = args.Length > 0 ? args[0] : "unknown";

Console.WriteLine($"csharp-argument-{name}");
Console.WriteLine($"csharp-stdin-{input.Trim()}");
