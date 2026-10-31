// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Reflection;
using Xunit;

namespace Infrastructure.Tests;

/// <summary>
/// Serializes test classes that build tools/Aspire.TestTools out of process. Concurrent builds write the
/// same artifacts/bin/Aspire.TestTools outputs (for example Aspire.TestTools.deps.json), and one of them
/// fails with "The process cannot access the file ... because it is being used by another process".
/// </summary>
[CollectionDefinition(Name)]
public sealed class AspireTestToolsBuildCollection
{
    public const string Name = "AspireTestToolsBuild";
}

public class AspireTestToolsBuildCollectionTests
{
    [Theory]
    [InlineData(typeof(GenerateTestSummaryToolTests))] // Fixture runs 'dotnet build' on a project that references Aspire.TestTools.
    [InlineData(typeof(DownloadFailingJobLogsToolTests))] // Runs 'dotnet run' on a file-based app with '#:project ../Aspire.TestTools'.
    public void TestClassesThatBuildAspireTestToolsShareACollection(Type testClass)
    {
        var collection = testClass.GetCustomAttribute<CollectionAttribute>();

        Assert.True(collection is not null, $"{testClass.Name} builds Aspire.TestTools and must be in the '{AspireTestToolsBuildCollection.Name}' collection.");
        Assert.Equal(AspireTestToolsBuildCollection.Name, collection.Name);
    }
}
