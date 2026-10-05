// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Radius.Annotations;
using Aspire.Hosting.Radius.CloudProviders;

namespace Aspire.Hosting.Radius.Tests.CloudProviders;

public class CredentialModeOverrideTests
{
    private const string SubId = "11111111-1111-1111-1111-111111111111";
    private const string Rg = "rg";
    private const string TenantId = "22222222-2222-2222-2222-222222222222";
    private const string ClientId = "33333333-3333-3333-3333-333333333333";
    private const string AccountId = "123456789012";
    private const string Region = "us-east-1";
    private const string Arn = "arn:aws:iam::123456789012:role/r";

    [Fact]
    public void Azure_WithServicePrincipal_ThenWithWorkloadIdentity_LastWriteWins()
    {
        var builder = DistributedApplication.CreateBuilder();
        var secret = builder.AddParameter("s", "v", secret: true);
        var env = builder.AddRadiusEnvironment("radius");

        env.WithAzureProvider(SubId, Rg, azure =>
        {
            azure.WithServicePrincipal(TenantId, ClientId, secret);
            azure.WithWorkloadIdentity(TenantId, ClientId);
        });

        var ann = env.Resource.Annotations.OfType<RadiusCloudProvidersAnnotation>().Single();
        Assert.IsType<AzureRadiusCredential.WorkloadIdentity>(ann.Azure!.Credential);
    }

    [Fact]
    public void Aws_WithAccessKey_ThenWithIrsa_LastWriteWins()
    {
        var builder = DistributedApplication.CreateBuilder();
        var keyId = builder.AddParameter("k", "v", secret: true);
        var keySecret = builder.AddParameter("ks", "v", secret: true);
        var env = builder.AddRadiusEnvironment("radius");

        env.WithAwsProvider(AccountId, Region, aws =>
        {
            aws.WithAccessKey(keyId, keySecret);
            aws.WithIrsa(Arn);
        });

        var ann = env.Resource.Annotations.OfType<RadiusCloudProvidersAnnotation>().Single();
        Assert.IsType<AwsRadiusCredential.Irsa>(ann.Aws!.Credential);
    }

    [Fact]
    public void Azure_TwoWithAzureProviderCalls_OnSameEnv_LastWriteWins()
    {
        var builder = DistributedApplication.CreateBuilder();
        var secret = builder.AddParameter("s", "v", secret: true);
        var env = builder.AddRadiusEnvironment("radius");
        const string SecondSub = "44444444-4444-4444-4444-444444444444";

        env.WithAzureProvider(SubId, Rg, azure => azure.WithServicePrincipal(TenantId, ClientId, secret));
        env.WithAzureProvider(SecondSub, "rg2", azure => azure.WithWorkloadIdentity(TenantId, ClientId));

        var ann = env.Resource.Annotations.OfType<RadiusCloudProvidersAnnotation>().Single();
        Assert.Equal(SecondSub, ann.Azure!.SubscriptionId);
        Assert.Equal("rg2", ann.Azure.ResourceGroup);
        Assert.IsType<AzureRadiusCredential.WorkloadIdentity>(ann.Azure.Credential);
    }
}
