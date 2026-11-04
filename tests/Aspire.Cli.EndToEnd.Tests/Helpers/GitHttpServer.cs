// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

namespace Aspire.Cli.EndToEnd.Tests.Helpers;

internal sealed class GitHttpServer(WebApplication application) : IAsyncDisposable
{
    public string RepositoryUrl => $"{application.Urls.Single()}/template.git";

    public static async Task<GitHttpServer> StartAsync(string directory, CancellationToken cancellationToken)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [], ContentRootPath = directory });
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        var application = builder.Build();
        application.MapGet("/template.git/info/refs", context => RunBackendAsync(context, directory));
        application.MapPost("/template.git/git-upload-pack", context => RunBackendAsync(context, directory));

        try
        {
            await application.StartAsync(cancellationToken);
            return new GitHttpServer(application);
        }
        catch
        {
            await application.DisposeAsync();
            throw;
        }
    }

    public ValueTask DisposeAsync() => application.DisposeAsync();

    private static async Task RunBackendAsync(HttpContext context, string directory)
    {
        var cancellationToken = context.RequestAborted;
        var startInfo = new ProcessStartInfo("git")
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        // Docker can create the fixture under a different UID. Trust only this test's bare repository.
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add($"safe.directory={Path.Combine(directory, "template.git")}");
        startInfo.ArgumentList.Add("http-backend");
        // Smart HTTP is required for the CLI's shallow fetches. These are the backend's CGI inputs:
        // https://git-scm.com/docs/git-http-backend#_environment
        startInfo.Environment["GIT_PROJECT_ROOT"] = directory;
        startInfo.Environment["GIT_HTTP_EXPORT_ALL"] = "1";
        startInfo.Environment["PATH_INFO"] = context.Request.Path.Value;
        startInfo.Environment["REQUEST_METHOD"] = context.Request.Method;
        startInfo.Environment["QUERY_STRING"] = context.Request.QueryString.Value?.TrimStart('?') ?? string.Empty;
        startInfo.Environment["CONTENT_TYPE"] = context.Request.ContentType ?? string.Empty;
        startInfo.Environment["HTTP_GIT_PROTOCOL"] = context.Request.Headers["Git-Protocol"].ToString();

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start the Git HTTP backend.");
        try
        {
            using var response = new MemoryStream();
            var responseTask = process.StandardOutput.BaseStream.CopyToAsync(response, cancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await context.Request.Body.CopyToAsync(process.StandardInput.BaseStream, cancellationToken);
            process.StandardInput.Close();
            await Task.WhenAll(responseTask, errorTask, process.WaitForExitAsync(cancellationToken));
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException($"Git HTTP backend exited with code {process.ExitCode}: {await errorTask}");
            }

            // CGI emits "Content-Type: ...\r\n...\r\n\r\n<binary pack data>". Buffer this small fixture's
            // response as bytes so decoding the headers cannot corrupt the Git payload.
            var bytes = response.ToArray();
            var headerEnd = bytes.AsSpan().IndexOf("\r\n\r\n"u8);
            if (headerEnd < 0)
            {
                throw new InvalidDataException("Git HTTP backend returned an incomplete CGI response.");
            }

            foreach (var header in Encoding.ASCII.GetString(bytes, 0, headerEnd).Split("\r\n"))
            {
                var separator = header.IndexOf(':');
                if (separator < 1)
                {
                    throw new InvalidDataException($"Git HTTP backend returned an invalid CGI header: {header}");
                }

                var name = header[..separator];
                var value = header[(separator + 1)..].Trim();
                if (name.Equals("Status", StringComparison.OrdinalIgnoreCase))
                {
                    // CGI uses a header such as "Status: 404 Not Found" for non-success responses.
                    context.Response.StatusCode = int.Parse(value.AsSpan(0, 3), CultureInfo.InvariantCulture);
                }
                else
                {
                    context.Response.Headers[name] = value;
                }
            }

            await context.Response.Body.WriteAsync(bytes.AsMemory(headerEnd + 4), cancellationToken);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }
        }
    }
}
