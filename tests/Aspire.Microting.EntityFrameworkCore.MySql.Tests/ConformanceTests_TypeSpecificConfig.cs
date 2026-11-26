// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.TestUtilities;
using Aspire.MySqlConnector.Tests;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Aspire.Microting.EntityFrameworkCore.MySql.Tests;

public class ConformanceTests_TypeSpecificConfig : ConformanceTests
{
    public ConformanceTests_TypeSpecificConfig(MySqlContainerFixture containerFixture, ITestOutputHelper? output = null) : base(containerFixture, output)
    {
    }

    protected override void PopulateConfiguration(ConfigurationManager configuration, string? key = null)
        => configuration.AddInMemoryCollection(new KeyValuePair<string, string?>[2]
        {
            new($"Aspire:Microting:EntityFrameworkCore:MySql:{typeof(TestDbContext).Name}:ConnectionString", ConnectionString),
            new($"Aspire:Microting:EntityFrameworkCore:MySql:{typeof(TestDbContext).Name}:ServerVersion", ServerVersion)
        });
}
