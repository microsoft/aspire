// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Azure.Azd.Tests;

public class AzdEnvironmentReaderTests
{
    [Fact]
    public void ReadsDefaultEnvironmentFromConfig()
    {
        using var sample = SampleAzdProject.Create();

        var environment = AzdEnvironmentReader.Read(sample.Root.FullName);

        Assert.NotNull(environment);
        Assert.Equal("dev", environment.Name);
        Assert.Equal("eastus2", environment.Location);
        Assert.Equal("00000000-0000-0000-0000-000000000000", environment.SubscriptionId);
        Assert.Equal("rg-contoso-dev", environment.ResourceGroup);
        Assert.Equal("11111111-1111-1111-1111-111111111111", environment.PrincipalId);
        Assert.Equal("22222222-2222-2222-2222-222222222222", environment.TenantId);
        Assert.Equal("User", environment.PrincipalType);
    }

    [Fact]
    public void StripsQuotesAndIgnoresComments()
    {
        using var sample = SampleAzdProject.Create();

        var environment = AzdEnvironmentReader.Read(sample.Root.FullName);

        Assert.NotNull(environment);
        // The value is quoted in the .env file; quotes must be removed.
        Assert.Equal("https://web.example.com", environment.GetValueOrDefault("SERVICE_WEB_ENDPOINT_URL"));
        // Comment lines must not become entries.
        Assert.DoesNotContain(environment.Values.Keys, k => k.StartsWith('#'));
    }

    [Fact]
    public void UnescapesJsonQuotedValues()
    {
        using var sample = SampleAzdProject.Create();

        var environment = AzdEnvironmentReader.Read(sample.Root.FullName);

        Assert.NotNull(environment);
        // azd/godotenv writes the value as AZURE_TAGS="{\"env\":\"dev\",\"team\":\"contoso\"}"; the
        // embedded escaped quotes must be unescaped, not left in the value.
        Assert.Equal("{\"env\":\"dev\",\"team\":\"contoso\"}", environment.GetValueOrDefault("AZURE_TAGS"));
    }

    [Fact]
    public void ReadsExplicitlyNamedEnvironment()
    {
        using var sample = SampleAzdProject.Create();

        var environment = AzdEnvironmentReader.Read(sample.Root.FullName, "prod");

        Assert.NotNull(environment);
        Assert.Equal("prod", environment.Name);
        Assert.Equal("westus3", environment.Location);
    }

    [Fact]
    public void ReturnsNullWhenNoAzureDirectory()
    {
        var emptyDir = Directory.CreateTempSubdirectory("azd-empty-");
        try
        {
            Assert.Null(AzdEnvironmentReader.Read(emptyDir.FullName));
        }
        finally
        {
            emptyDir.Delete(recursive: true);
        }
    }
}
