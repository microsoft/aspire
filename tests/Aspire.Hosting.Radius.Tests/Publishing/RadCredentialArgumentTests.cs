// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Radius.Annotations;
using Aspire.Hosting.Radius.Publishing;

namespace Aspire.Hosting.Radius.Tests.Publishing;

public class RadCredentialArgumentTests
{
    private const string SubscriptionId = "11111111-1111-1111-1111-111111111111";
    private const string ResourceGroup = "rg-test";
    private const string TenantId = "22222222-2222-2222-2222-222222222222";
    private const string ClientId = "33333333-3333-3333-3333-333333333333";
    private const string Account = "123456789012";
    private const string Region = "us-west-2";
    private const string RoleArn = "arn:aws:iam::123456789012:role/radius-irsa";

    [Fact]
    public async Task AzureServicePrincipal_EmitsTwoPositionalTokens_NoNameFlag()
    {
        var builder = DistributedApplication.CreateBuilder();
        var secret = builder.AddParameter("azureClientSecret", "secret-XYZ", secret: true);
        var env = builder.AddRadiusEnvironment("radius");
        env.WithAzureProvider(SubscriptionId, ResourceGroup,
            azure => azure.WithServicePrincipal(TenantId, ClientId, secret));

        var args = await ResolveSingleAsync(env.Resource);

        Assert.Equal(
            new[]
            {
                "credential", "register", "azure", "sp",
                "--tenant-id", TenantId,
                "--client-id", ClientId,
                "--client-secret", "secret-XYZ",
            },
            args);
        Assert.DoesNotContain("--name", args);
    }

    [Fact]
    public async Task AzureWorkloadIdentity_EmitsTwoPositionalTokens_NoNameFlag()
    {
        var builder = DistributedApplication.CreateBuilder();
        var env = builder.AddRadiusEnvironment("radius");
        env.WithAzureProvider(SubscriptionId, ResourceGroup,
            azure => azure.WithWorkloadIdentity(ClientId, TenantId));

        var args = await ResolveSingleAsync(env.Resource);

        Assert.Equal(
            new[]
            {
                "credential", "register", "azure", "wi",
                "--client-id", ClientId,
                "--tenant-id", TenantId,
            },
            args);
        Assert.DoesNotContain("--name", args);
    }

    [Fact]
    public async Task AwsAccessKey_EmitsTwoPositionalTokens_NoNameFlag()
    {
        var builder = DistributedApplication.CreateBuilder();
        var keyId = builder.AddParameter("awsKeyId", "AKIAEXAMPLE", secret: true);
        var keySecret = builder.AddParameter("awsKeySecret", "AKIA-XYZ-secret", secret: true);
        var env = builder.AddRadiusEnvironment("radius");
        env.WithAwsProvider(Account, Region, aws => aws.WithAccessKey(keyId, keySecret));

        var args = await ResolveSingleAsync(env.Resource);

        Assert.Equal(
            new[]
            {
                "credential", "register", "aws", "access-key",
                "--access-key-id", "AKIAEXAMPLE",
                "--secret-access-key", "AKIA-XYZ-secret",
            },
            args);
        Assert.DoesNotContain("--name", args);
    }

    [Fact]
    public async Task AwsIrsa_EmitsTwoPositionalTokens_NoNameFlag()
    {
        var builder = DistributedApplication.CreateBuilder();
        var env = builder.AddRadiusEnvironment("radius");
        env.WithAwsProvider(Account, Region, aws => aws.WithIrsa(RoleArn));

        var args = await ResolveSingleAsync(env.Resource);

        Assert.Equal(
            new[]
            {
                "credential", "register", "aws", "irsa",
                "--iam-role", RoleArn,
            },
            args);
        Assert.DoesNotContain("--name", args);
    }

    private static async Task<IReadOnlyList<string>> ResolveSingleAsync(IResource resource)
    {
        var annotation = resource.Annotations.OfType<RadiusCloudProvidersAnnotation>().Single();
        var entry = RadCredentialRegisterStep.BuildEntries(annotation).Single();
        return await entry.ResolveArgumentsAsync(CancellationToken.None);
    }
}
