// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Tests.Utils;

public static class DockerfileUtils
{
    public const string HelloWorldDockerfile = $$"""
        FROM mcr.microsoft.com/cbl-mariner/base/nginx:1.22 AS builder
        ARG MESSAGE=aspire!
        RUN mkdir -p /usr/share/nginx/html
        RUN echo !!!CACHEBUSTER!!! > /usr/share/nginx/html/cachebuster.txt
        RUN echo ${MESSAGE} > /usr/share/nginx/html/aspire.html

        FROM mcr.microsoft.com/cbl-mariner/base/nginx:1.22 AS runner
        ARG MESSAGE
        RUN mkdir -p /usr/share/nginx/html
        COPY --from=builder /usr/share/nginx/html/cachebuster.txt /usr/share/nginx/html
        COPY --from=builder /usr/share/nginx/html/aspire.html /usr/share/nginx/html
        """;

    public const string HelloWorldDockerfileWithSecrets = $$"""
        FROM mcr.microsoft.com/cbl-mariner/base/nginx:1.22 AS builder
        ARG MESSAGE=aspire!
        RUN mkdir -p /usr/share/nginx/html
        RUN echo !!!CACHEBUSTER!!! > /usr/share/nginx/html/cachebuster.txt
        RUN echo ${MESSAGE} > /usr/share/nginx/html/aspire.html

        FROM mcr.microsoft.com/cbl-mariner/base/nginx:1.22 AS runner
        ARG MESSAGE
        RUN mkdir -p /usr/share/nginx/html
        COPY --from=builder /usr/share/nginx/html/cachebuster.txt /usr/share/nginx/html
        COPY --from=builder /usr/share/nginx/html/aspire.html /usr/share/nginx/html
        RUN --mount=type=secret,id=ENV_SECRET cp /run/secrets/ENV_SECRET /usr/share/nginx/html/ENV_SECRET.txt
        RUN chmod -R 777 /usr/share/nginx/html
        """;

    public static async Task<TemporaryDockerfileContext> CreateTemporaryDockerfileAsync(string dockerfileName = "Dockerfile", bool createDockerfile = true, bool includeSecrets = false)
    {
        var tempDirectory = new TestTempDirectory();

        try
        {
            var tempContextPath = tempDirectory.Path;
            var tempDockerfilePath = Path.Combine(tempContextPath, dockerfileName);

            if (createDockerfile)
            {
                var dockerfileTemplate = includeSecrets ? HelloWorldDockerfileWithSecrets : HelloWorldDockerfile;
                // We apply this random value to the Dockerfile to make sure that we get a clean
                // build each time with no possible caching.
                var cacheBuster = Guid.NewGuid();
                var dockerfileContent = dockerfileTemplate.Replace("!!!CACHEBUSTER!!!", cacheBuster.ToString());

                await File.WriteAllTextAsync(tempDockerfilePath, dockerfileContent);
            }

            return new TemporaryDockerfileContext(tempDirectory, tempDockerfilePath);
        }
        catch
        {
            tempDirectory.Dispose();
            throw;
        }
    }
}

public sealed class TemporaryDockerfileContext(TestTempDirectory tempDirectory, string dockerfilePath) : IDisposable
{
    private readonly TestTempDirectory _tempDirectory = tempDirectory;

    public string ContextPath => _tempDirectory.Path;

    public string DockerfilePath { get; } = dockerfilePath;

    public void Dispose() => _tempDirectory.Dispose();
}
