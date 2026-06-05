// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable CS0618 // Type or member is obsolete
#pragma warning disable ASPIREPIPELINES001

using System.Text.Json.Nodes;
using Aspire.Hosting.Tests.Helpers;
using Aspire.Hosting.Utils;
using Azure.Provisioning.KeyVault;
using Json.Schema;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.Tests.Schema;

public class SchemaTests
{
    public static TheoryData<string, Action<IDistributedApplicationBuilder>> ApplicationSamples
    {
        get
        {
            var data = new TheoryData<string, Action<IDistributedApplicationBuilder>>
            {
                { "BasicParameter", (IDistributedApplicationBuilder builder) =>
                    {
                        builder.AddParameter("foo");
                    }
                },

                { "BasicSecretParameter", (IDistributedApplicationBuilder builder) =>
                    {
                        builder.AddParameter("foo", secret: true);
                    }
                },

                { "FormatterParameter", (IDistributedApplicationBuilder builder) =>
                    {
                      // A Redis password is formatted in the Uri connection property.
                        var resourceWithFormatterParameter = builder.AddRedis("redis1", password: builder.AddParameter("pass", secret: true));
                        builder.AddRedis("redis2").WithReference(resourceWithFormatterParameter);
                    }
                },

                { "ConnectionStringParameter", (IDistributedApplicationBuilder builder) =>
                    {
                        builder.AddConnectionString("foo");
                    }
                },

                { "BasicContainer", (IDistributedApplicationBuilder builder) =>
                    {
                        builder.AddRedis("redis");
                    }
                },

                { "ContainerWithBuild", (IDistributedApplicationBuilder builder) =>
                    {
                        var tempPath = Path.GetTempPath();
                        var tempContextPath = Path.Combine(tempPath, Path.GetRandomFileName());
                        Directory.CreateDirectory(tempContextPath);
                        var tempDockerfilePath = Path.Combine(tempContextPath, "Dockerfile");
                        File.WriteAllText(tempDockerfilePath, "does not need to be valid dockerfile content here");

                        builder.AddContainer("mycontainer", "myimage").WithDockerfile(tempContextPath);
                    }
                },

                { "ContainerWithBuildAndBuildArgs", (IDistributedApplicationBuilder builder) =>
                    {
                        var tempPath = Path.GetTempPath();
                        var tempContextPath = Path.Combine(tempPath, Path.GetRandomFileName());
                        Directory.CreateDirectory(tempContextPath);
                        var tempDockerfilePath = Path.Combine(tempContextPath, "Dockerfile");
                        File.WriteAllText(tempDockerfilePath, "does not need to be valid dockerfile content here");

                        var p = builder.AddParameter("p");
                        builder.AddContainer("mycontainer", "myimage")
                               .WithDockerfile(tempContextPath)
                               .WithBuildArg("stringArg", "a string")
                               .WithBuildArg("intArg", 42)
                               .WithBuildArg("boolArg", true)
                               .WithBuildArg("parameterArg", p);
                    }
                },

                { "ContainerWithBuildAndSecretBuildArgs", (IDistributedApplicationBuilder builder) =>
                    {
                        var tempPath = Path.GetTempPath();
                        var tempContextPath = Path.Combine(tempPath, Path.GetRandomFileName());
                        Directory.CreateDirectory(tempContextPath);
                        var tempDockerfilePath = Path.Combine(tempContextPath, "Dockerfile");
                        File.WriteAllText(tempDockerfilePath, "does not need to be valid dockerfile content here");

                        var p = builder.AddParameter("p", secret: true);
                        builder.AddContainer("mycontainer", "myimage")
                               .WithDockerfile(tempContextPath)
                               .WithBuildSecret("secretArg", p);
                    }
                },

                { "ContainerWithVolume", (IDistributedApplicationBuilder builder) =>
                    {
                        builder.AddRedis("redis").WithDataVolume();
                    }
                },

                { "ContainerWithBindMount", (IDistributedApplicationBuilder builder) =>
                    {
                        builder.AddRedis("redis").WithBindMount("source", "target");
                    }
                },

                { "BasicContainerWithConnectionString", (IDistributedApplicationBuilder builder) =>
                    {
                        builder.AddPostgres("postgres");
                    }
                },

                { "BasicDockerfile", (IDistributedApplicationBuilder builder) =>
                    {
                        var tempContextPath = Directory.CreateTempSubdirectory().FullName;
                        var tempDockerfilePath = Path.Combine(tempContextPath, "Dockerfile");
                        File.WriteAllText(tempDockerfilePath, "does not need to be valid dockerfile content here");

                        builder.AddExecutable(name:"foo", command: "bar", workingDirectory: tempContextPath, "one", "two", "three").PublishAsDockerFile();
                    }
                },

                { "ContainerWithContainerRuntimeArgs", (IDistributedApplicationBuilder builder) =>
                    {
                        builder.AddContainer("foo", "bar").WithContainerRuntimeArgs("one", "two", "three");
                    }
                },

                { "BasicProject", (IDistributedApplicationBuilder builder) =>
                    {
                        builder.AddProject<Projects.ServiceA>("project");
                    }
                },

                { "BasicExecutable", (IDistributedApplicationBuilder builder) =>
                    {
                        builder.AddExecutable("executable", "hellworld", "foo", "arg1", "arg2");
                    }
                },

                { "VanillaProjectBasedContainerApp", (IDistributedApplicationBuilder builder) =>
                    {
                        builder.AddAzureContainerAppEnvironment("env");

                        builder.AddProject<Projects.ServiceA>("project")
                               .PublishAsAzureContainerApp((_, _) => { });

                    }
                },

                { "CustomizedProjectBasedContainerApp", (IDistributedApplicationBuilder builder) =>
                    {
                        builder.AddAzureContainerAppEnvironment("env");

                        var minReplicas = builder.AddParameter("minReplicas");

                        builder.AddProject<Projects.ServiceA>("project")
                               .PublishAsAzureContainerApp((infrastructure, app) =>
                               {
                                   app.Template.Scale.MinReplicas = minReplicas.AsProvisioningParameter(infrastructure);
                               });

                    }
                },

                { "VanillaContainerBasedContainerApp", (IDistributedApplicationBuilder builder) =>
                    {
                        builder.AddAzureContainerAppEnvironment("env");

                        builder.AddContainer("mycontainer", "myimage")
                               .PublishAsAzureContainerApp((_, _) => { });

                    }
                },

                { "CustomizedContainerBasedContainerApp", (IDistributedApplicationBuilder builder) =>
                    {
                        builder.AddAzureContainerAppEnvironment("env");

                        var minReplicas = builder.AddParameter("minReplicas");

                        builder.AddContainer("mycontainer", "myimage")
                               .PublishAsAzureContainerApp((infrastructure, app) =>
                               {
                                   app.Template.Scale.MinReplicas = minReplicas.AsProvisioningParameter(infrastructure);
                               });

                    }
                },

                { "VanillaBicepResource", (IDistributedApplicationBuilder builder) =>
                    {
                        builder.AddAzureInfrastructure("infrastructure", infrastructure =>
                        {
                            var kv = KeyVaultService.FromExisting("doesnotexist");
                            infrastructure.Add(kv);
                        });
                    }
                },
            };

            return data;
        }
    }

    private static JsonSchema? s_schema;

    private static JsonSchema GetSchema()
    {
        if (s_schema == null)
        {
            var relativePath = Path.Combine("Schema", "aspire-8.0.json");
            var schemaPath = Path.GetFullPath(relativePath);
            s_schema = JsonSchema.FromFile(schemaPath);
        }

        return s_schema;
    }

    [Theory]
    [MemberData(nameof(ApplicationSamples))]
    public void ValidateApplicationSamples(string testCaseName, Action<IDistributedApplicationBuilder> configurator)
    {
        var manifestDir = Directory.CreateTempSubdirectory(testCaseName).FullName;
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputPath: Path.Combine(manifestDir, "not-used.json"), step: "publish-json-manifest");

        // Register the JsonDocumentManifestStore to store the manifest in memory
        var manifestStore = new JsonDocumentManifestStore();
        builder.Services.AddSingleton(manifestStore);

        // Add the JSON manifest publishing pipeline step
        builder.Pipeline.AddJsonDocumentManifestPublishing();

        configurator(builder);

        using var program = builder.Build();

        program.Run();

        var manifestDocument = manifestStore.ManifestDocument;
        var manifestText = manifestDocument.RootElement.ToString();
        AssertValid(manifestText);
    }

    [Fact]
    public void SchemaRejectsEmptyManifest()
    {
        var manifestText = """
            {
            }
            """;

        AssertInvalid(manifestText);
    }

    [Fact]
    public void ManifestAcceptsUnknownResourceType()
    {
        var manifestText = """
            {
              "resources": {
                "aresource": {
                  "type": "not.a.valid.resource"
                }
              }
            }
            """;

        AssertValid(manifestText);
    }

    [Fact]
    public void ManifestAcceptsAnnotatedStrings()
    {
        var manifestText = """
            {
              "resources": {
                  "cache-password-uri-encoded": {
                  "type": "annotated.string",
                  "value": "{cache-password.value}",
                  "filter": "uri"
                }
              }
            }
            """;

        AssertValid(manifestText);
    }

    [Fact]
    public void ManifestWithUnsupportedFilterIsRejected()
    {
        var manifestText = """
            {
              "resources": {
                  "cache-password-uri-encoded": {
                  "type": "annotated.string",
                  "value": "{cache-password.value}",
                  "filter": "uri2"
                }
              }
            }
            """;

        AssertInvalid(manifestText);
    }

    [Fact]
    public void ManifestWithContainerResourceWithMissingImageIsRejected()
    {
        var manifestText = """
            {
              "resources": {
                "mycontainer": {
                  "type": "container.v0"
                }
              }
            }
            """;

        AssertInvalid(manifestText);
    }

    [Fact]
    public void ManifestWithValue0ResourceWithConnectionStringAndValueIsRejectedIsRejected()
    {
        var manifestText = """
            {
              "resources": {
                "valueresource": {
                  "type": "value.v0",
                  "connectionString": "{valueresource.value}",
                  "value": "this.should.not.be.here"
                }
              }
            }
            """;

        AssertInvalid(manifestText);
    }

    [Fact]
    public void InvalidBicepResourceFailsValidationToProveItIsntBeingIgnored()
    {
        var manifestText = """
            {
              "resources": {
                "invalidbicepresource": {
                  "type": "azure.bicep.v0",
                  "invalidproperty": "invalidvalue"
                }
              }
            }
            """;

        AssertInvalid(manifestText);
    }

    [Fact]
    public void ManifestWithContainerResourceAndNoEnvOrBindingsIsAccepted()
    {
        var manifestText = """
            {
              "resources": {
                "mycontainer": {
                  "type": "container.v0",
                  "image": "myimage:latest"
                }
              }
            }
            """;

        AssertValid(manifestText);
    }

    [Fact]
    public void ManifestWithContainerV0ResourceAndBuildFieldIsRejected()
    {
        var manifestText = """
            {
              "resources": {
                "mycontainer": {
                  "type": "container.v0",
                  "image": "myimage:latest",
                  "build": {
                    "context": "relativepath",
                    "dockerfile": "relativepath/Dockerfile"
                  }
                }
              }
            }
            """;

        AssertInvalid(manifestText);
    }

    [Fact]
    public void ManifestWithContainerV1ResourceWithImageAndBuildFieldIsRejected()
    {
        var manifestText = """
            {
              "resources": {
                "mycontainer": {
                  "type": "container.v1",
                  "image": "myimage:latest",
                  "build": {
                    "context": "relativepath",
                    "dockerfile": "relativepath/Dockerfile"
                  }
                }
              }
            }
            """;

        AssertInvalid(manifestText);
    }

    [Fact]
    public void ManifestWithContainerV1ResourceAndBuildFieldIsAccepted()
    {
        var manifestText = """
            {
              "resources": {
                "mycontainer": {
                  "type": "container.v1",
                  "build": {
                    "context": "relativepath",
                    "dockerfile": "relativepath/Dockerfile"
                  }
                }
              }
            }
            """;

        AssertValid(manifestText);
    }

    [Fact]
    public void ManifestWithDockerfileV0ResourceAndBuildFieldAndArgsIsAccepted()
    {
        var manifestText = """
            {
              "resources": {
                "mycontainer": {
                  "type": "dockerfile.v0",
                  "context": "relativepath",
                  "path": "relativepath/Dockerfile",
                  "buildArgs": {
                    "ARG1": "an arg"
                  }
                }
              }
            }
            """;

        AssertValid(manifestText);
    }

    [Fact]
    public void ManifestWithContainerV1ResourceAndBuildFieldAndArgsIsAccepted()
    {
        var manifestText = """
            {
              "resources": {
                "mycontainer": {
                  "type": "container.v1",
                  "build": {
                    "context": "relativepath",
                    "dockerfile": "relativepath/Dockerfile",
                    "args": {
                      "ARG1": "an arg"
                    }
                  }
                }
              }
            }
            """;

        AssertValid(manifestText);
    }

    [Fact]
    public void ManifestWithContainerV1ResourceAndImageFieldIsAccepted()
    {
        var manifestText = """
            {
              "resources": {
                "mycontainer": {
                  "type": "container.v1",
                  "image": "myimage:latest"
                }
              }
            }
            """;

        AssertValid(manifestText);
    }

    [Fact]
    public void ManifestWithProjectResourceAndNoEnvOrBindingsIsAccepted()
    {
        var manifestText = """
            {
              "resources": {
                "myapp": {
                  "type": "project.v0",
                  "path": "path/to/project/project.csproj"
                }
              }
            }
            """;

        AssertValid(manifestText);
    }

    [Fact]
    public void BicepManifestIsAccepted()
    {
        // The reason this large test is here is that when submitting the positive test cases to SchemaStore.org
        // I found this one was failing. So I'm including it here.
        var manifestText = """
            {
              "resources": {
                "administratorLogin": {
                  "inputs": {
                    "value": {
                      "type": "string"
                    }
                  },
                  "type": "parameter.v0",
                  "value": "{administratorLogin.inputs.value}"
                },
                "administratorLoginPassword": {
                  "inputs": {
                    "value": {
                      "secret": true,
                      "type": "string",
                      "default": {
                        "generate": {
                          "minLength": 16,
                          "lower": false,
                          "upper": false,
                          "numeric": false,
                          "special": false,
                          "minLower": 1,
                          "minUpper": 2,
                          "minNumeric": 3,
                          "minSpecial": 4
                        }
                      }
                    }
                  },
                  "type": "parameter.v0",
                  "value": "{administratorLoginPassword.inputs.value}"
                },
                "administratorName": {
                  "inputs": {
                    "value": {
                      "secret": true,
                      "type": "string",
                      "default": {
                        "value": "David"
                      }
                    }
                  },
                  "type": "parameter.v0",
                  "value": "{administratorName.inputs.value}"
                },
                "ai": {
                  "connectionString": "{ai.outputs.appInsightsConnectionString}",
                  "params": {
                    "logAnalyticsWorkspaceId": "{lawkspc.outputs.logAnalyticsWorkspaceId}"
                  },
                  "path": "ai.module.bicep",
                  "type": "azure.bicep.v0"
                },
                "aiwithoutlaw": {
                  "connectionString": "{aiwithoutlaw.outputs.appInsightsConnectionString}",
                  "params": {
                    "logAnalyticsWorkspaceId": ""
                  },
                  "path": "aiwithoutlaw.module.bicep",
                  "type": "azure.bicep.v0"
                },
                "api": {
                  "bindings": {
                    "http": {
                      "external": true,
                      "protocol": "tcp",
                      "scheme": "http",
                      "transport": "http"
                    },
                    "https": {
                      "external": true,
                      "protocol": "tcp",
                      "scheme": "https",
                      "transport": "http"
                    }
                  },
                  "env": {
                    "APPLICATIONINSIGHTS_CONNECTION_STRING": "{ai.connectionString}",
                    "ASPNETCORE_FORWARDEDHEADERS_ENABLED": "true",
                    "ConnectionStrings__appConfig": "{appConfig.connectionString}",
                    "ConnectionStrings__blob": "{blob.connectionString}",
                    "ConnectionStrings__cosmos": "{cosmos.connectionString}",
                    "ConnectionStrings__db": "{db.connectionString}",
                    "ConnectionStrings__db2": "{db2.connectionString}",
                    "ConnectionStrings__kv3": "{kv3.connectionString}",
                    "ConnectionStrings__queue": "{queue.connectionString}",
                    "ConnectionStrings__redis": "{redis.connectionString}",
                    "ConnectionStrings__sb": "{sb.connectionString}",
                    "ConnectionStrings__signalr": "{signalr.connectionString}",
                    "ConnectionStrings__table": "{table.connectionString}",
                    "OTEL_DOTNET_EXPERIMENTAL_OTLP_RETRY": "in_memory",
                    "bicepValue0": "{test.outputs.val0}",
                    "bicepValue1": "{test.outputs.val1}",
                    "bicepValue_test": "{test.outputs.test}"
                  },
                  "path": "../BicepSample.ApiService/BicepSample.ApiService.csproj",
                  "type": "project.v0"
                },
                "appConfig": {
                  "connectionString": "{appConfig.outputs.appConfigEndpoint}",
                  "params": {
                    "principalId": "",
                    "principalType": "",
                    "sku": "standard"
                  },
                  "path": "appConfig.module.bicep",
                  "type": "azure.bicep.v0"
                },
                "blob": {
                  "connectionString": "{storage.outputs.blobEndpoint}",
                  "type": "value.v0"
                },
                "cosmos": {
                  "connectionString": "{cosmos.secretOutputs.connectionString}",
                  "params": {
                    "keyVaultName": ""
                  },
                  "path": "cosmos.module.bicep",
                  "type": "azure.bicep.v0"
                },
                "db": {
                  "connectionString": "{sql.connectionString};Database=db",
                  "type": "value.v0"
                },
                "db2": {
                  "connectionString": "{postgres2.connectionString};Database=db2",
                  "type": "value.v0"
                },
                "kv3": {
                  "connectionString": "{kv3.outputs.vaultUri}",
                  "params": {
                    "principalId": "",
                    "principalType": ""
                  },
                  "path": "kv3.module.bicep",
                  "type": "azure.bicep.v0"
                },
                "lawkspc": {
                  "path": "lawkspc.module.bicep",
                  "type": "azure.bicep.v0"
                },
                "postgres2": {
                  "connectionString": "{postgres2.secretOutputs.connectionString}",
                  "params": {
                    "administratorLogin": "{administratorLogin.value}",
                    "administratorLoginPassword": "{administratorLoginPassword.value}",
                    "keyVaultName": ""
                  },
                  "path": "postgres2.module.bicep",
                  "type": "azure.bicep.v0"
                },
                "queue": {
                  "connectionString": "{storage.outputs.queueEndpoint}",
                  "type": "value.v0"
                },
                "redis": {
                  "connectionString": "{redis.secretOutputs.connectionString}",
                  "params": {
                    "keyVaultName": ""
                  },
                  "path": "redis.module.bicep",
                  "type": "azure.bicep.v0"
                },
                "sb": {
                  "connectionString": "{sb.outputs.serviceBusEndpoint}",
                  "params": {
                    "principalId": "",
                    "principalType": ""
                  },
                  "path": "sb.module.bicep",
                  "type": "azure.bicep.v0"
                },
                "signalr": {
                  "connectionString": "Endpoint=https://{signalr.outputs.hostName};AuthType=azure",
                  "params": {
                    "principalId": "",
                    "principalType": ""
                  },
                  "path": "signalr.module.bicep",
                  "type": "azure.bicep.v0"
                },
                "sql": {
                  "connectionString": "Server=tcp:{sql.outputs.sqlServerFqdn},1433;Encrypt=True;Authentication=\u0022Active Directory Default\u0022",
                  "params": {
                    "principalId": "",
                    "principalName": ""
                  },
                  "path": "sql.module.bicep",
                  "type": "azure.bicep.v0"
                },
                "storage": {
                  "params": {
                    "principalId": "",
                    "principalType": ""
                  },
                  "path": "storage.module.bicep",
                  "type": "azure.bicep.v0"
                },
                "table": {
                  "connectionString": "{storage.outputs.tableEndpoint}",
                  "type": "value.v0"
                },
                "test": {
                  "params": {
                    "p2": "{test0.outputs.val0}",
                    "test": "{val.value}",
                    "values": ["one", "two"]
                  },
                  "path": "test.bicep",
                  "type": "azure.bicep.v0"
                },
                "test0": {
                  "path": "test0.module.bicep",
                  "type": "azure.bicep.v0"
                },
                "val": {
                  "inputs": {
                    "value": {
                      "type": "string"
                    }
                  },
                  "type": "parameter.v0",
                  "value": "{val.inputs.value}"
                }
              }
            }

            """;

        AssertValid(manifestText);
    }

    [Fact]
    public void BothDefaultGenerateAndValueAreMutuallyExclusive()
    {
        // Trying to us both 'generate' and 'value' in the same parameter should be rejected.
        var manifestText = """
            {
              "resources": {
                "foo": {
                  "inputs": {
                    "value": {
                      "secret": true,
                      "type": "string",
                      "default": {
                        "generate": {
                          "minLength": 16
                        },
                        "value": "some value"
                      }
                    }
                  },
                  "type": "parameter.v0",
                  "value": "{foo.inputs.value}"
                }
              }
            }

            """;

        AssertInvalid(manifestText);
    }

    private static void AssertValid(string manifestText)
    {
        var manifestJson = JsonNode.Parse(manifestText);
        var schema = GetSchema();
        var results = schema.Evaluate(manifestJson);

        if (!results.IsValid)
        {
            var errorMessages = results.Details.Where(x => x.HasErrors).SelectMany(e => e.Errors!).Select(e => e.Value);
            Assert.True(results.IsValid, string.Join(Environment.NewLine, errorMessages ?? ["Schema failed validation with no errors"]));
        }
    }

    private static void AssertInvalid(string manifestText)
    {
        var manifestJson = JsonNode.Parse(manifestText);
        var schema = GetSchema();
        var results = schema.Evaluate(manifestJson);

        Assert.False(results.IsValid);
    }
}
