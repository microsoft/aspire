#:package Microsoft.Extensions.FileSystemGlobbing
#:package System.CommandLine

using System.Collections.Concurrent;
using System.CommandLine;
using Microsoft.Extensions.FileSystemGlobbing;
using Microsoft.Extensions.FileSystemGlobbing.Abstractions;

var filesOption = new Option<string[]>("--files")
{
    Description = "One or more file paths, directory paths, or glob patterns",
    AllowMultipleArgumentsPerToken = true,
    Required = true
};

var replacementsOption = new Option<string[]>("--replacements")
{
    Description = "Pairs of find/replace text values",
    AllowMultipleArgumentsPerToken = true,
    Required = true
};

var rootCommand = new RootCommand
{
    filesOption,
    replacementsOption
};

rootCommand.SetAction(result =>
{
    try
    {
        var paths = result.GetValue<string[]>(filesOption) ?? Array.Empty<string>();
        var replacementArgs = result.GetValue<string[]>(replacementsOption) ?? Array.Empty<string>();

        // Validate and parse replacements
        if (replacementArgs.Length == 0)
        {
            Console.Error.WriteLine("Error: No replacements provided. Use --replacements to specify find/replace pairs.");
            return 1;
        }

        if (replacementArgs.Length % 2 != 0)
        {
            Console.Error.WriteLine($"Error: Replacement arguments must be provided in pairs (find, replace).");
            Console.Error.WriteLine($"       Received {replacementArgs.Length} arguments after --replacements.");
            return 1;
        }

        var replacements = new List<(string Find, string Replace)>();
        for (var i = 0; i < replacementArgs.Length; i += 2)
        {
            var find = replacementArgs[i];
            var replace = replacementArgs[i + 1];

            if (string.IsNullOrEmpty(find))
            {
                Console.Error.WriteLine($"Error: Find text at position {i + 1} in --replacements cannot be empty.");
                return 1;
            }

            replacements.Add((find, replace));
        }

        Console.WriteLine($"Paths: {paths.Length}");
        foreach (var path in paths)
        {
            Console.WriteLine($"  '{path}'");
        }
        Console.WriteLine($"Replacements: {replacements.Count}");
        foreach (var (find, replace) in replacements)
        {
            Console.WriteLine($"  '{find}' -> '{replace}'");
        }
        Console.WriteLine();

        var filesToProcess = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var matcher = new Matcher();
        var hasGlobPatterns = false;

        foreach (var pathValue in paths)
        {
            if (File.Exists(pathValue))
            {
                // If it's a direct file path add it as is
                filesToProcess.Add(Path.GetFullPath(pathValue));
            }
            else if (Directory.Exists(pathValue))
            {
                // If it's a directory, include all files within it
                matcher.AddInclude(Path.Combine(pathValue, "**/*"));
                hasGlobPatterns = true;
            }
            else if (pathValue.Contains('*') || pathValue.Contains('?'))
            {
                matcher.AddInclude(pathValue);
                hasGlobPatterns = true;
            }
        }

        var currentDirectory = Directory.GetCurrentDirectory();

        if (hasGlobPatterns)
        {
            // Collect files from glob matching
            var directoryInfo = new DirectoryInfoWrapper(new DirectoryInfo(currentDirectory));
            var matchResult = matcher.Execute(directoryInfo);

            foreach (var file in matchResult.Files)
            {
                filesToProcess.Add(Path.GetFullPath(Path.Combine(currentDirectory, file.Path)));
            }
        }

        if (filesToProcess.Count == 0)
        {
            Console.WriteLine("No files matched the provided paths.");
            return 0;
        }

        Console.WriteLine($"Found {filesToProcess.Count} file(s) matching the provided paths.");
        Console.WriteLine();

        var processedCount = 0;
        var modifiedCount = 0;
        var errorCount = 0;
        var errors = new ConcurrentBag<(string File, string Error)>();

        Parallel.ForEach(filesToProcess, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, filePath =>
        {
            try
            {
                var content = File.ReadAllText(filePath);
                var originalContent = content;

                foreach (var (find, replace) in replacements)
                {
                    content = content.Replace(find, replace, StringComparison.Ordinal);
                }

                if (!string.Equals(content, originalContent, StringComparison.Ordinal))
                {
                    File.WriteAllText(filePath, content);
                    Interlocked.Increment(ref modifiedCount);
                    Console.WriteLine($"Modified: {Path.GetRelativePath(currentDirectory, filePath)}");
                }

                Interlocked.Increment(ref processedCount);
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref errorCount);
                errors.Add((filePath, ex.Message));
            }
        });

        Console.WriteLine();
        Console.WriteLine($"Processed: {processedCount} file(s)");
        Console.WriteLine($"Modified:  {modifiedCount} file(s)");

        if (errorCount > 0)
        {
            Console.WriteLine($"Errors:    {errorCount} file(s)");
            Console.WriteLine();
            Console.Error.WriteLine("Errors encountered:");
            foreach (var (file, error) in errors)
            {
                Console.Error.WriteLine($"  {Path.GetRelativePath(currentDirectory, file)}: {error}");
            }
            return 1;
        }

        return 0;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Unexpected error: {ex.Message}");
        return 1;
    }
});

return rootCommand.Parse(args).Invoke();
