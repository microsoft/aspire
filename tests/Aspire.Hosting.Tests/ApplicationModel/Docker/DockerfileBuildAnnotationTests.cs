// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREDOCKERFILEBUILDER001 // Type is for evaluation purposes only and is subject to change or removal in future updates

namespace Aspire.Hosting.Tests.ApplicationModel.Docker;

[Trait("Partition", "4")]
public class DockerfileBuildAnnotationTests
{
    [Fact]
    public void DockerfileBuildAnnotation_Constructor_CreatesAnnotation()
    {
        // Arrange & Act
        var annotation = new DockerfileBuildAnnotation("/path/to/context", "/path/to/Dockerfile", "stage");

        // Assert
        Assert.Equal("/path/to/context", annotation.ContextPath);
        Assert.Equal("/path/to/Dockerfile", annotation.DockerfilePath);
        Assert.Equal("stage", annotation.Stage);
        Assert.Empty(annotation.BuildArguments);
        Assert.Empty(annotation.BuildSecrets);
    }

    [Fact]
    public void DockerfileBuildAnnotation_NullStage_Allowed()
    {
        // Arrange & Act
        var annotation = new DockerfileBuildAnnotation("/context", "/dockerfile", null);

        // Assert
        Assert.Null(annotation.Stage);
        Assert.Equal("/context", annotation.ContextPath);
        Assert.Equal("/dockerfile", annotation.DockerfilePath);
    }

    [Fact]
    public void DockerfileBuildAnnotation_BuildArguments_Modifiable()
    {
        // Arrange
        var annotation = new DockerfileBuildAnnotation("/context", "/dockerfile", null);

        // Act
        annotation.BuildArguments["ARG1"] = "value1";
        annotation.BuildArguments["ARG2"] = null;

        // Assert
        Assert.Equal(2, annotation.BuildArguments.Count);
        Assert.Equal("value1", annotation.BuildArguments["ARG1"]);
        Assert.Null(annotation.BuildArguments["ARG2"]);
    }

    [Fact]
    public void DockerfileBuildAnnotation_BuildSecrets_Modifiable()
    {
        // Arrange
        var annotation = new DockerfileBuildAnnotation("/context", "/dockerfile", null);

        // Act
        annotation.BuildSecrets["SECRET1"] = "secret-value";
        annotation.BuildSecrets["SECRET2"] = 42;

        // Assert
        Assert.Equal(2, annotation.BuildSecrets.Count);
        Assert.Equal("secret-value", annotation.BuildSecrets["SECRET1"]);
        Assert.Equal(42, annotation.BuildSecrets["SECRET2"]);
    }

    [Fact]
    public async Task MaterializeDockerfileAsync_NoFactory_DoesNothing()
    {
        // Arrange
        var dockerfilePath = Path.Combine(Path.GetTempPath(), $"Dockerfile.{Guid.NewGuid()}");
        var annotation = new DockerfileBuildAnnotation("/context", dockerfilePath, null);
        var context = new DockerfileFactoryContext
        {
            Services = null!,
            Resource = null!,
            CancellationToken = CancellationToken.None
        };

        try
        {
            // Act
            await annotation.MaterializeDockerfileAsync(context, CancellationToken.None);

            // Assert
            Assert.False(File.Exists(dockerfilePath));
        }
        finally
        {
            if (File.Exists(dockerfilePath))
            {
                File.Delete(dockerfilePath);
            }
        }
    }

    [Fact]
    public async Task MaterializeDockerfileAsync_WithFactory_WritesDockerfile()
    {
        // Arrange
        var dockerfilePath = Path.Combine(Path.GetTempPath(), $"Dockerfile.{Guid.NewGuid()}");
        var expectedContent = "FROM alpine:latest\nRUN echo 'test'";
        var annotation = new DockerfileBuildAnnotation("/context", dockerfilePath, null)
        {
            DockerfileFactory = _ => Task.FromResult(expectedContent)
        };
        var context = new DockerfileFactoryContext
        {
            Services = null!,
            Resource = null!,
            CancellationToken = CancellationToken.None
        };

        try
        {
            // Act
            await annotation.MaterializeDockerfileAsync(context, CancellationToken.None);

            // Assert
            Assert.True(File.Exists(dockerfilePath));
            var actualContent = await File.ReadAllTextAsync(dockerfilePath);
            Assert.Equal(expectedContent, actualContent);
        }
        finally
        {
            if (File.Exists(dockerfilePath))
            {
                File.Delete(dockerfilePath);
            }
        }
    }

    [Fact]
    public async Task MaterializeDockerfileAsync_CalledTwice_WritesOnlyOnce()
    {
        // Arrange
        var dockerfilePath = Path.Combine(Path.GetTempPath(), $"Dockerfile.{Guid.NewGuid()}");
        var callCount = 0;
        var annotation = new DockerfileBuildAnnotation("/context", dockerfilePath, null)
        {
            DockerfileFactory = _ =>
            {
                Interlocked.Increment(ref callCount);
                return Task.FromResult("FROM alpine:latest");
            }
        };
        var context = new DockerfileFactoryContext
        {
            Services = null!,
            Resource = null!,
            CancellationToken = CancellationToken.None
        };

        try
        {
            // Act
            await annotation.MaterializeDockerfileAsync(context, CancellationToken.None);
            await annotation.MaterializeDockerfileAsync(context, CancellationToken.None);

            // Assert
            Assert.Equal(1, callCount);
            Assert.True(File.Exists(dockerfilePath));
        }
        finally
        {
            if (File.Exists(dockerfilePath))
            {
                File.Delete(dockerfilePath);
            }
        }
    }

    [Fact]
    public async Task MaterializeDockerfileAsync_ConcurrentCalls_InvokesFactoryOnlyOnce()
    {
        // Arrange
        var dockerfilePath = Path.Combine(Path.GetTempPath(), $"Dockerfile.{Guid.NewGuid()}");
        var callCount = 0;
        var annotation = new DockerfileBuildAnnotation("/context", dockerfilePath, null)
        {
            DockerfileFactory = async _ =>
            {
                Interlocked.Increment(ref callCount);
                await Task.Delay(10); // Simulate async work
                return "FROM alpine:latest";
            }
        };
        var context = new DockerfileFactoryContext
        {
            Services = null!,
            Resource = null!,
            CancellationToken = CancellationToken.None
        };

        try
        {
            // Act - Concurrently call MaterializeDockerfileAsync
            var tasks = Enumerable.Range(0, 10)
                .Select(_ => annotation.MaterializeDockerfileAsync(context, CancellationToken.None));

            await Task.WhenAll(tasks);

            // Assert
            Assert.Equal(1, callCount);
            Assert.True(File.Exists(dockerfilePath));
        }
        finally
        {
            if (File.Exists(dockerfilePath))
            {
                File.Delete(dockerfilePath);
            }
        }
    }
}