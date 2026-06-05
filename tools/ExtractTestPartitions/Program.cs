// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Reflection;

if (args.Length < 4 || args[0] != "--assembly-path" || args[2] != "--output-file")
{
    Console.Error.WriteLine("Usage: ExtractTestPartitions --assembly-path <path> --output-file <path>");
    return 1;
}

var assemblyPath = args[1];
var outputFile = args[3];

ExtractPartitions(assemblyPath, outputFile);
return 0;

static void ExtractPartitions(string assemblyPath, string outputFile)
{
    if (!File.Exists(assemblyPath))
    {
        Console.Error.WriteLine($"Error: Assembly file not found: {assemblyPath}");
        Environment.Exit(1);
    }

    var partitions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    try
    {
        // Load the assembly using Assembly.LoadFrom
        // We need to set up an assembly resolve handler for dependencies
        var assemblyDirectory = Path.GetDirectoryName(assemblyPath);
        if (string.IsNullOrEmpty(assemblyDirectory))
        {
            Console.Error.WriteLine($"Error: Unable to determine directory for assembly: {assemblyPath}");
            Environment.Exit(1);
        }

        AppDomain.CurrentDomain.AssemblyResolve += (sender, args) =>
        {
            var assemblyName = new AssemblyName(args.Name);
            var dllPath = Path.Combine(assemblyDirectory, assemblyName.Name + ".dll");
            if (File.Exists(dllPath))
            {
                return Assembly.LoadFrom(dllPath);
            }
            return null;
        };

        var assembly = Assembly.LoadFrom(assemblyPath);
        Console.WriteLine($"Loaded assembly: {assembly.FullName}");

        // Iterate through all types in the assembly
        Type[] types;
        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            // Some types couldn't be loaded due to missing dependencies
            // Use the types that did load
            types = ex.Types.Where(t => t is not null).Cast<Type>().ToArray();
            Console.WriteLine($"** Some types could not be loaded. Loaded {types.Length} types successfully.");
        }

        foreach (var type in types)
        {
            // Only detect [Trait("Partition", "name")] attributes for splitting.
            // xUnit [Collection] attributes are for shared fixtures, not CI splitting.
            var attributes = type.GetCustomAttributesData();

            foreach (var attr in attributes)
            {
                var attrTypeName = attr.AttributeType.FullName ?? attr.AttributeType.Name;

                // Check for Trait attribute with Partition key
                if (!attrTypeName.EndsWith(".TraitAttribute") && attrTypeName != "TraitAttribute")
                {
                    continue;
                }

                if (attr.ConstructorArguments.Count < 2)
                {
                    continue;
                }

                var key = attr.ConstructorArguments[0].Value as string;
                var value = attr.ConstructorArguments[1].Value as string;

                if (key?.Equals("Partition", StringComparison.OrdinalIgnoreCase) == true &&
                    !string.IsNullOrWhiteSpace(value))
                {
                    partitions.Add(value);
                    Console.WriteLine($"Found Trait Partition: {value} on {type.Name}");
                }
            }
        }

        Console.WriteLine($"Total unique partitions found: {partitions.Count}");

        // Write partitions to output file
        var outputDir = Path.GetDirectoryName(outputFile);
        if (!string.IsNullOrEmpty(outputDir) && !Directory.Exists(outputDir))
        {
            Directory.CreateDirectory(outputDir);
        }

        if (partitions.Count > 0)
        {
            File.WriteAllLines(outputFile, partitions.OrderBy(p => p, StringComparer.OrdinalIgnoreCase));
            Console.WriteLine($"Partitions written to: {outputFile}");
        }
        else
        {
            Console.WriteLine("No partitions found. Not creating output file.");
        }
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Error extracting partitions: {ex.Message}");
        Console.Error.WriteLine($"Stack trace: {ex.StackTrace}");
        Environment.Exit(1);
    }
}
