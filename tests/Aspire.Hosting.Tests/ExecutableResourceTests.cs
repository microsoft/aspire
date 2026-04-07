// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Tests.Utils;
using Aspire.Hosting.Utils;
using Microsoft.AspNetCore.InternalTesting;

namespace Aspire.Hosting.Tests;

[Trait("Partition", "2")]
public class ExecutableResourceTests
{
    [Fact]
    public async Task AddExecutableWithArgs()
    {
        var appBuilder = DistributedApplication.CreateBuilder();

        var testResource = new TestResource("test", "connectionString");
        var testResource2 = new TestResource("test2", "anotherConnectionString");

        var exe1 = appBuilder.AddExecutable("e1", "ruby", ".", "app.rb")
            .WithEndpoint("ep", e =>
            {
                e.UriScheme = "http";
                e.AllocatedEndpoint = new(e, "localhost", 1234);
            });

        var exe2 = appBuilder.AddExecutable("e2", "python", ".", "app.py", exe1.GetEndpoint("ep"))
             .WithEndpoint("ep", e =>
             {
                 e.UriScheme = "http";
                 e.AllocatedEndpoint = new(e, "localhost", 5678);
             })
             .WithArgs("arg1", testResource)
             .WithArgs(context =>
             {
                 context.Args.Add("arg2");
                 context.Args.Add(exe1.GetEndpoint("ep"));
                 context.Args.Add(testResource2);
                 context.Args.Add(((IResourceWithEndpoints)context.Resource).GetEndpoint("ep"));
             });

        using var app = appBuilder.Build();

        var args = await ArgumentEvaluator.GetArgumentListAsync(exe2.Resource).DefaultTimeout();

        Assert.Collection(args,
            arg => Assert.Equal("app.py", arg),
            arg => Assert.Equal("http://localhost:1234", arg),
            arg => Assert.Equal("arg1", arg),
            arg => Assert.Equal("connectionString", arg),
            arg => Assert.Equal("arg2", arg),
            arg => Assert.Equal("http://localhost:1234", arg),
            arg => Assert.Equal("anotherConnectionString", arg),
            arg => Assert.Equal("http://localhost:5678", arg)
            );

        Assert.True(exe2.Resource.TryGetAnnotationsOfType<ResourceRelationshipAnnotation>(out var relationships));
        // We don't yet process relationships set via the callbacks
        // so we don't see the testResource2 nor exe1
        Assert.Collection(relationships,
            r =>
            {
                Assert.Equal("Reference", r.Type);
                Assert.Same(testResource, r.Resource);
            });

        var manifest = await ManifestUtils.GetManifest(exe2.Resource).DefaultTimeout();

        // Note: resource working directory is <repo-root>\tests\Aspire.Hosting.Tests
        // Manifest directory is <repo-root>\artifacts\bin\Aspire.Hosting.Tests\Debug\net8.0
        var expectedManifest =
        """
        {
          "type": "executable.v0",
          "workingDirectory": "../../../../../tests/Aspire.Hosting.Tests",
          "command": "python",
          "args": [
            "app.py",
            "{e1.bindings.ep.url}",
            "arg1",
            "{test.connectionString}",
            "arg2",
            "{e1.bindings.ep.url}",
            "{test2.connectionString}",
            "{e2.bindings.ep.url}"
          ],
          "bindings": {
            "ep": {
              "scheme": "http",
              "protocol": "tcp",
              "transport": "http",
              "targetPort": 8000
            }
          }
        }
        """;

        Assert.Equal(expectedManifest, manifest.ToString());
    }

    [Fact]
    public void ExecutableResourceNullCommand()
        => Assert.Throws<ArgumentNullException>("command", () => new ExecutableResource("name", command: null!, workingDirectory: "."));

    [Fact]
    public void ExecutableResourceEmptyCommand()
        => Assert.Throws<ArgumentException>("command", () => new ExecutableResource("name", command: "", workingDirectory: "."));

    [Fact]
    public void ExecutableResourceNullWorkingDirectory()
        => Assert.Throws<ArgumentNullException>("workingDirectory", () => new ExecutableResource("name", command: "cmd", workingDirectory: null!));

    [Fact]
    public void ExecutableResourceEmptyWorkingDirectory()
    {
        var er = new ExecutableResource("name", command: "cmd", workingDirectory: "");
        Assert.Empty(er.WorkingDirectory);
    }

    private sealed class TestResource(string name, string connectionString) : Resource(name), IResourceWithConnectionString
    {
        public ReferenceExpression ConnectionStringExpression =>
            ReferenceExpression.Create($"{connectionString}");
    }
}
