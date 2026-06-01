// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Radius.Annotations;
using Aspire.Hosting.Radius.CloudProviders;

namespace Aspire.Hosting.Radius.Tests.CloudProviders;

public class WithAwsProviderTests
{
    private const string ValidAccount = "123456789012";
    private const string ValidRegion = "us-west-2";
    private const string ValidArn = "arn:aws:iam::123456789012:role/radius-irsa";

    [Fact]
    public void WithAwsProvider_AccessKey_HappyPath_PopulatesAnnotation()
    {
        var builder = DistributedApplication.CreateBuilder();
        var keyId = builder.AddParameter("awsKeyId", "AKIAEXAMPLE", secret: true);
        var keySecret = builder.AddParameter("awsKeySecret", "AKIA-XYZ-secret", secret: true);
        var env = builder.AddRadiusEnvironment("radius");

        var returned = env.WithAwsProvider(ValidAccount, ValidRegion,
            aws => aws.WithAccessKey(keyId, keySecret));

        Assert.Same(env, returned);
        var ann = env.Resource.Annotations.OfType<RadiusCloudProvidersAnnotation>().Single();
        Assert.NotNull(ann.Aws);
        Assert.Equal(ValidAccount, ann.Aws!.AccountId);
        Assert.Equal(ValidRegion, ann.Aws.Region);
        var ak = Assert.IsType<AwsRadiusCredential.AccessKey>(ann.Aws.Credential);
        Assert.Same(keyId, ak.AccessKeyId);
        Assert.Same(keySecret, ak.SecretAccessKey);
    }

    [Fact]
    public void WithAwsProvider_Irsa_HappyPath_PopulatesAnnotation()
    {
        var builder = DistributedApplication.CreateBuilder();
        var env = builder.AddRadiusEnvironment("radius");

        env.WithAwsProvider(ValidAccount, ValidRegion, aws => aws.WithIrsa(ValidArn));

        var ann = env.Resource.Annotations.OfType<RadiusCloudProvidersAnnotation>().Single();
        var irsa = Assert.IsType<AwsRadiusCredential.Irsa>(ann.Aws!.Credential);
        Assert.Equal(ValidArn, irsa.IamRoleArn);
    }

    [Fact]
    public void WithAwsProvider_InvalidAccountId_Throws()
    {
        var builder = DistributedApplication.CreateBuilder();
        var env = builder.AddRadiusEnvironment("radius");

        var ex = Assert.Throws<ArgumentException>(() => env.WithAwsProvider(
            "12345", ValidRegion, aws => aws.WithIrsa(ValidArn)));
        Assert.Equal("accountId", ex.ParamName);
    }

    [Fact]
    public void WithAwsProvider_EmptyRegion_Throws()
    {
        var builder = DistributedApplication.CreateBuilder();
        var env = builder.AddRadiusEnvironment("radius");

        var ex = Assert.Throws<ArgumentException>(() => env.WithAwsProvider(
            ValidAccount, "", aws => aws.WithIrsa(ValidArn)));
        Assert.Equal("region", ex.ParamName);
    }

    [Fact]
    public void WithAwsProvider_NullConfigure_Throws()
    {
        var builder = DistributedApplication.CreateBuilder();
        var env = builder.AddRadiusEnvironment("radius");

        Assert.Throws<ArgumentNullException>(() => env.WithAwsProvider(
            ValidAccount, ValidRegion, null!));
    }

    [Fact]
    public void WithAwsProvider_CallbackWithoutCredential_Throws()
    {
        var builder = DistributedApplication.CreateBuilder();
        var env = builder.AddRadiusEnvironment("radius");

        var ex = Assert.Throws<InvalidOperationException>(() => env.WithAwsProvider(
            ValidAccount, ValidRegion, _ => { }));
        Assert.Contains("ASPIRERADIUS010", ex.Message);
    }

    [Fact]
    public void WithAccessKey_NullKeyId_Throws()
    {
        var builder = DistributedApplication.CreateBuilder();
        var keySecret = builder.AddParameter("k", "v", secret: true);
        var env = builder.AddRadiusEnvironment("radius");

        Assert.Throws<ArgumentNullException>(() => env.WithAwsProvider(
            ValidAccount, ValidRegion, aws => aws.WithAccessKey(null!, keySecret)));
    }

    [Fact]
    public void WithAccessKey_NullSecret_Throws()
    {
        var builder = DistributedApplication.CreateBuilder();
        var keyId = builder.AddParameter("k", "v", secret: true);
        var env = builder.AddRadiusEnvironment("radius");

        Assert.Throws<ArgumentNullException>(() => env.WithAwsProvider(
            ValidAccount, ValidRegion, aws => aws.WithAccessKey(keyId, null!)));
    }

    [Fact]
    public void WithIrsa_MalformedArn_Throws()
    {
        var builder = DistributedApplication.CreateBuilder();
        var env = builder.AddRadiusEnvironment("radius");

        var ex = Assert.Throws<ArgumentException>(() => env.WithAwsProvider(
            ValidAccount, ValidRegion, aws => aws.WithIrsa("not-an-arn")));
        Assert.Equal("iamRoleArn", ex.ParamName);
    }

    [Fact]
    public void WithAwsProvider_UnknownRegionAccepted()
    {
        var builder = DistributedApplication.CreateBuilder();
        var env = builder.AddRadiusEnvironment("radius");

        env.WithAwsProvider(ValidAccount, "mars-central-1", aws => aws.WithIrsa(ValidArn));

        var ann = env.Resource.Annotations.OfType<RadiusCloudProvidersAnnotation>().Single();
        Assert.Equal("mars-central-1", ann.Aws!.Region);
    }
}
