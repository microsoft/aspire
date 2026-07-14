#:sdk Microsoft.NET.Sdk

var projectFiles = Directory.EnumerateFiles(Directory.GetCurrentDirectory(), "*.csproj")
    .Select(Path.GetFileName)
    .OrderBy(static fileName => fileName);

foreach (var projectFile in projectFiles)
{
    Console.WriteLine(projectFile);
}

Console.Error.WriteLine("csharp-stderr-line");
Environment.Exit(4);
