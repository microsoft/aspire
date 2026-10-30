// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Reflection.PortableExecutable;
using Aspire.Hosting.Native.CodeGeneration;

namespace Aspire.Hosting.Native.Core.Tests;

public class NativeContractCompilationTests
{
    [Fact]
    public void RuntimeReferences_ContainOnlyManagedAssemblies()
    {
        var paths = NativeContractCompilation.GetRuntimeAssemblyPaths().ToArray();
        Assert.Contains(typeof(object).Assembly.Location, paths);
        Assert.Contains(typeof(Task).Assembly.Location, paths);
        foreach (var path in paths)
        {
            using var stream = File.OpenRead(path);
            using var reader = new PEReader(stream);
            Assert.True(reader.HasMetadata, $"Native DLL selected as a compiler reference: {path}");
        }
    }
}
