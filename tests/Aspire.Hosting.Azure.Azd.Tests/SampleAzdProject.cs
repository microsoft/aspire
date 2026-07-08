// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Azure.Azd.Tests;

/// <summary>
/// Writes a realistic azd project layout (azure.yaml + infra + .azure + service sources) to a
/// temporary directory so the importer can be exercised end-to-end against on-disk assets.
/// </summary>
internal sealed class SampleAzdProject : IDisposable
{
    private SampleAzdProject(DirectoryInfo root)
    {
        Root = root;
    }

    public DirectoryInfo Root { get; }

    public string AzureYamlPath => Path.Combine(Root.FullName, "azure.yaml");

    public static SampleAzdProject Create()
    {
        var root = Directory.CreateTempSubdirectory("azd-import-test-");
        var sample = new SampleAzdProject(root);
        sample.Write();
        return sample;
    }

    private void Write()
    {
        File.WriteAllText(AzureYamlPath, AzureYaml);

        // The .NET service points at a directory that contains a project file.
        WriteFile("src/web/web.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");

        // The container service builds from a Dockerfile relative to its project directory.
        WriteFile("src/api/Dockerfile", "FROM mcr.microsoft.com/dotnet/aspnet:8.0");

        // The non-.NET service has no project file but does have a Dockerfile to fall back to.
        WriteFile("src/legacy/Dockerfile", "FROM python:3.12-slim");

        // Existing infrastructure that must be preserved by the import.
        WriteFile("infra/main.bicep", "// existing azd infrastructure");
        WriteFile("infra/main.parameters.json", "{ \"parameters\": {} }");

        // azd environment state.
        WriteFile(".azure/config.json", "{ \"version\": 1, \"defaultEnvironment\": \"dev\" }");
        WriteFile(".azure/dev/.env", DevDotEnv);
        WriteFile(".azure/prod/.env", "AZURE_ENV_NAME=\"prod\"\nAZURE_LOCATION=\"westus3\"\n");
    }

    private void WriteFile(string relativePath, string contents)
    {
        var fullPath = Path.Combine(Root.FullName, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, contents);
    }

    public void Dispose()
    {
        try
        {
            Root.Delete(recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup; a leaked temp directory should not fail a test.
        }
    }

    private const string AzureYaml =
        """
        name: contoso-app
        metadata:
          template: contoso@1.0
        infra:
          provider: bicep
          path: infra
          module: main
        services:
          web:
            project: ./src/web
            language: dotnet
            host: containerapp
            uses:
              - cache
              - secrets
              - pg
            env:
              ASPNETCORE_ENVIRONMENT: Production
          api:
            project: ./src/api
            language: docker
            host: appservice
            docker:
              path: ./Dockerfile
              target: build
          legacy:
            project: ./src/legacy
            language: python
            host: function
        resources:
          cache:
            type: db.redis
          secrets:
            type: keyvault
            existing: true
          pg:
            type: db.postgres
          orders:
            type: db.cosmos
            containers:
              - name: items
                partitionKeys:
                  - /id
          sb:
            type: messaging.servicebus
            queues:
              - jobs
            topics:
              - events
          files:
            type: storage
            containers:
              - uploads
          search:
            type: ai.search
          openai:
            type: ai.openai.model
            model:
              name: gpt-4o
              version: "2024-08-06"
          db2:
            type: db.mysql
        """;

    private const string DevDotEnv =
        """
        AZURE_ENV_NAME="dev"
        AZURE_LOCATION="eastus2"
        AZURE_SUBSCRIPTION_ID="00000000-0000-0000-0000-000000000000"
        AZURE_RESOURCE_GROUP="rg-contoso-dev"
        AZURE_PRINCIPAL_ID="11111111-1111-1111-1111-111111111111"
        AZURE_TENANT_ID="22222222-2222-2222-2222-222222222222"
        AZURE_PRINCIPAL_TYPE="User"
        # a JSON-escaped value, as godotenv writes it
        AZURE_TAGS="{\"env\":\"dev\",\"team\":\"contoso\"}"
        # provisioning output
        SERVICE_WEB_ENDPOINT_URL="https://web.example.com"
        """;
}
