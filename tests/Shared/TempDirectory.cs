// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

public sealed class TestTempDirectory : IDisposable
{
    public string Path { get; } = Directory.CreateTempSubdirectory(".aspire-tests").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch { } // Ignore errors during cleanup
    }
}
