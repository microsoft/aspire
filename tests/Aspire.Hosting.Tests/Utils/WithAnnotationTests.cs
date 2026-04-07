// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Tests.Utils;

[Trait("Partition", "4")]
public class WithAnnotationTests
{
    [Fact]
    public void WithAnnotationWithTypeParameterAndNoExplicitBehaviorAppends()
    {
        var builder = DistributedApplication.CreateBuilder();
        var redis = builder.AddRedis("redis")
                           .WithAnnotation<DummyAnnotation>()
                           .WithAnnotation<DummyAnnotation>();

        var dummyAnnotations = redis.Resource.Annotations.OfType<DummyAnnotation>();

        Assert.Equal(2, dummyAnnotations.Count());
        Assert.NotEqual(dummyAnnotations.First(), dummyAnnotations.Last());
    }

    [Fact]
    public void WithAnnotationWithTypeParameterAndArgumentAndNoExplicitBehaviorAppends()
    {
        var builder = DistributedApplication.CreateBuilder();
        var redis = builder.AddRedis("redis")
                           .WithAnnotation<DummyAnnotation>(new DummyAnnotation())
                           .WithAnnotation<DummyAnnotation>(new DummyAnnotation());

        var dummyAnnotations = redis.Resource.Annotations.OfType<DummyAnnotation>();

        Assert.Equal(2, dummyAnnotations.Count());
        Assert.NotEqual(dummyAnnotations.First(), dummyAnnotations.Last());
    }

    [Fact]
    public void WithAnnotationWithTypeParameterAndArgumentAndAddReplaceBehaviorReplaces()
    {
        var builder = DistributedApplication.CreateBuilder();
        var redis = builder.AddRedis("redis").WithAnnotation<DummyAnnotation>();

        var firstAnnotation = redis.Resource.Annotations.OfType<DummyAnnotation>().Single();

        redis.WithAnnotation<DummyAnnotation>(ResourceAnnotationMutationBehavior.Replace);

        var secondAnnotation = redis.Resource.Annotations.OfType<DummyAnnotation>().Single();

        Assert.NotEqual(firstAnnotation, secondAnnotation);
    }
}

public class DummyAnnotation : IResourceAnnotation
{
}
