// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SharedProcessRunner = Aspire.TestTools.ProcessRunner;

namespace AnalyzeCiFailure.TestServices;

/// <summary>Exposes existing scripted GitHub fixtures through HTTP without permitting network requests.</summary>
internal sealed class GitHubFixtureHandler(
    string workingDirectory,
    IReadOnlyDictionary<string, string>? environment = null) : HttpMessageHandler
{
    private readonly Dictionary<string, HttpResponseMessage> _pages = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> _labels = new(StringComparer.Ordinal);
    private int _sequence;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var uri = request.RequestUri!;
        await File.AppendAllTextAsync(
            Path.Combine(workingDirectory, "http-calls.log"),
            $"{request.Method} {uri.PathAndQuery}{Environment.NewLine}",
            cancellationToken);
        if (_pages.Remove(uri.AbsoluteUri, out var cached))
        {
            return cached;
        }

        var endpoint = uri.PathAndQuery.TrimStart('/');
        var path = uri.AbsolutePath.TrimStart('/');
        var query = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(value => value.Split('=', count: 2))
            .ToDictionary(value => Uri.UnescapeDataString(value[0]), value => value.Length > 1 ? Uri.UnescapeDataString(value[1]) : "");
        var repository = string.Join('/', path.Split('/').Skip(1).Take(2));
        var body = request.Content is null ? null : JsonNode.Parse(await request.Content.ReadAsStringAsync(cancellationToken)) as JsonObject;
        await File.AppendAllTextAsync(
            Path.Combine(workingDirectory, "http-requests.jsonl"),
            new JsonObject
            {
                ["method"] = request.Method.Method,
                ["path"] = uri.AbsolutePath,
                ["body"] = body?.DeepClone(),
            }.ToJsonString() + Environment.NewLine,
            cancellationToken);
        var arguments = new List<string>();
        var paginated = false;
        var binary = false;
        var createdKind = "";
        var success = HttpStatusCode.OK;

        if (request.Method == HttpMethod.Get)
        {
            if (path.Contains("/labels/", StringComparison.Ordinal))
            {
                if (File.Exists(Path.Combine(workingDirectory, "existing-main-label")))
                {
                    return Response(HttpStatusCode.OK, """{"name":"main-ci-break","color":"000000"}""");
                }
                return Response(HttpStatusCode.NotFound, """{"message":"Not Found"}""");
            }
            paginated = path.EndsWith("/jobs", StringComparison.Ordinal) || path.EndsWith("/artifacts", StringComparison.Ordinal) ||
                path.EndsWith("/pulls", StringComparison.Ordinal) || path.EndsWith("/files", StringComparison.Ordinal) ||
                path.EndsWith("/comments", StringComparison.Ordinal) || path.EndsWith("/annotations", StringComparison.Ordinal) ||
                path.EndsWith("/issues", StringComparison.Ordinal) || path.Contains("/compare/", StringComparison.Ordinal);
            arguments.Add("api");
            if (path.Contains("/actions/workflows/", StringComparison.Ordinal) && path.EndsWith("/runs", StringComparison.Ordinal))
            {
                arguments.AddRange(["--method", "GET", path]);
                foreach (var (key, value) in query)
                {
                    arguments.AddRange(["-f", $"{key}={value}"]);
                }
            }
            else if (paginated)
            {
                if (!path.Contains("/compare/", StringComparison.Ordinal))
                {
                    arguments.AddRange(["--method", "GET"]);
                }
                arguments.AddRange(["--paginate", "--slurp"]);
                if (path.EndsWith("/issues", StringComparison.Ordinal) ||
                    path.EndsWith("/pulls", StringComparison.Ordinal) && query.ContainsKey("head"))
                {
                    arguments.Add(path);
                    foreach (var (key, value) in query)
                    {
                        arguments.AddRange(["-f", $"{key}={value}"]);
                    }
                }
                else
                {
                    arguments.Add(endpoint);
                }
            }
            else
            {
                arguments.Add(endpoint);
            }
            binary = path.EndsWith("/zip", StringComparison.Ordinal);
        }
        else if (path.EndsWith("/rerun-failed-jobs", StringComparison.Ordinal))
        {
            RequireMethod(request, HttpMethod.Post);
            arguments.AddRange(["api", endpoint, "--method", "POST"]);
            success = HttpStatusCode.Created;
        }
        else if (path.Contains("/labels", StringComparison.Ordinal))
        {
            if (path.Contains("/issues/", StringComparison.Ordinal))
            {
                RequireMethod(request, HttpMethod.Post);
                if (!_labels.TryGetValue(path, out var labels))
                {
                    _labels[path] = labels = [];
                }
                RequireMethod(request, path.EndsWith("/labels", StringComparison.Ordinal) ? HttpMethod.Post : HttpMethod.Patch);
                foreach (var label in body!["labels"]!.AsArray())
                {
                    labels.Add(label!.GetValue<string>());
                }
                return Response(HttpStatusCode.OK, new JsonArray(labels.Select(label => JsonValue.Create(label)).ToArray()).ToJsonString());
            }
            arguments.AddRange(["label", "create", body!["name"]!.GetValue<string>(), "--repo", repository,
                "--color", body["color"]!.GetValue<string>(), "--description", body["description"]!.GetValue<string>(), "--force"]);
            success = request.Method == HttpMethod.Post ? HttpStatusCode.Created : HttpStatusCode.OK;
        }
        else if (path.Contains("/issues/comments/", StringComparison.Ordinal))
        {
            RequireMethod(request, HttpMethod.Patch);
            arguments.AddRange(["api", "--method", "PATCH", endpoint, "--input", await WriteBodyAsync(body!, cancellationToken)]);
        }
        else if (path.EndsWith("/comments", StringComparison.Ordinal))
        {
            RequireMethod(request, HttpMethod.Post);
            var number = path.Split('/')[^2];
            arguments.AddRange(["pr", "comment", number, "--repo", repository, "--body-file",
                await WriteTextAsync(body!["body"]!.GetValue<string>(), cancellationToken)]);
            createdKind = "comment";
            success = HttpStatusCode.Created;
        }
        else if (path.EndsWith("/issues", StringComparison.Ordinal))
        {
            RequireMethod(request, HttpMethod.Post);
            arguments.AddRange(["issue", "create", "--repo", repository, "--title", body!["title"]!.GetValue<string>(),
                "--label", string.Join(',', body["labels"]!.AsArray().Select(label => label!.GetValue<string>())),
                "--body-file", await WriteTextAsync(body["body"]!.GetValue<string>(), cancellationToken)]);
            createdKind = "issue";
            success = HttpStatusCode.Created;
        }
        else
        {
            RequireMethod(request, HttpMethod.Patch);
            var number = path.Split('/')[^1];
            if (body!["state"]?.GetValue<string>() == "open")
            {
                arguments.AddRange(["issue", "reopen", number, "--repo", repository]);
            }
            else
            {
                arguments.AddRange(["issue", "edit", number, "--repo", repository]);
                if (body["body"] is { } text)
                {
                    arguments.AddRange(["--body-file", await WriteTextAsync(text.GetValue<string>(), cancellationToken)]);
                }
                if (body["title"] is { } title)
                {
                    arguments.AddRange(["--title", title.GetValue<string>()]);
                }
            }
        }

        var executable = FindFakeExecutable();
        var outputFile = binary ? await WriteTextAsync("", cancellationToken) : null;
        var result = await SharedProcessRunner.RunAsync(executable, arguments, workingDirectory, environment, outputFile);
        cancellationToken.ThrowIfCancellationRequested();
        if (binary && result.ExitCode == 0)
        {
            return new HttpResponseMessage(success) { Content = new ByteArrayContent(await File.ReadAllBytesAsync(outputFile!, cancellationToken)) };
        }
        if (path.EndsWith("/logs", StringComparison.Ordinal))
        {
            if (result.ExitCode == 0)
            {
                return Response(success, result.Output);
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new InterruptedFixtureStream(Encoding.UTF8.GetBytes(result.Output), result.Error)),
            };
        }
        if (result.ExitCode != 0)
        {
            var status = result.Error.Contains("502", StringComparison.Ordinal) ? HttpStatusCode.BadGateway : HttpStatusCode.InternalServerError;
            return Response(status, """{"message":"Scripted fixture failure"}""");
        }
        if (createdKind.Length > 0)
        {
            var url = result.Output.Trim();
            var digits = Regex.Match(url, createdKind == "issue" ? @"/issues/([0-9]+)$" : @"#issuecomment-([0-9]+)$").Groups[1].Value;
            var response = new JsonObject { ["html_url"] = url, [createdKind == "issue" ? "number" : "id"] = JsonNode.Parse(digits.Length == 0 ? "0" : digits) };
            return Response(success, response.ToJsonString());
        }
        if (request.Method != HttpMethod.Get)
        {
            return Response(success, result.Output.TrimStart().StartsWith('{') ? result.Output : "{}");
        }
        if (paginated && JsonNode.Parse(result.Output) is JsonArray pages && pages.Count > 0)
        {
            var responses = pages.Select(page => Response(success, page?.ToJsonString() ?? "null")).ToArray();
            for (var index = 1; index < responses.Length; index++)
            {
                var next = $"https://api.github.com/fixture-pages/{++_sequence}";
                responses[index - 1].Headers.TryAddWithoutValidation("Link", $"<{next}>; rel=\"next\"");
                _pages[next] = responses[index];
            }
            return responses[0];
        }
        return Response(success, result.Output);
    }

    private string FindFakeExecutable()
    {
        var paths = (environment?.GetValueOrDefault("PATH") ?? Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator);
        foreach (var directory in paths)
        {
            // CurrentDirectory can canonicalize /var to /private/var independently of PATH.
            // Execute only a fixture physically addressed through the owned workspace.
            var candidate = Path.Combine(workingDirectory, Path.GetFileName(directory), "gh");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }
        throw new InvalidOperationException("HTTP fixture execution requires a workspace-owned fake gh; real GitHub access is forbidden.");
    }

    private async Task<string> WriteBodyAsync(JsonObject body, CancellationToken cancellationToken)
        => await WriteTextAsync(body.ToJsonString(), cancellationToken);

    private async Task<string> WriteTextAsync(string text, CancellationToken cancellationToken)
    {
        var directory = Directory.CreateDirectory(Path.Combine(workingDirectory, "http-fixture-requests"));
        var file = Path.Combine(directory.FullName, $"{++_sequence}.request");
        await File.WriteAllTextAsync(file, text, cancellationToken);
        return file;
    }

    private static HttpResponseMessage Response(HttpStatusCode status, string text)
        => new(status) { Content = new StringContent(text, Encoding.UTF8, "application/json") };

    private static void RequireMethod(HttpRequestMessage request, HttpMethod expected)
    {
        if (request.Method != expected)
        {
            throw new InvalidOperationException($"Expected {expected} for {request.RequestUri!.AbsolutePath}, not {request.Method}.");
        }
    }

    /// <summary>Delivers fixture log bytes and then fails so production handling retains partial download evidence.</summary>
    private sealed class InterruptedFixtureStream(byte[] content, string message) : MemoryStream(content)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => Position < Length ? base.ReadAsync(buffer, cancellationToken) : ValueTask.FromException<int>(new IOException(message));
    }
}
