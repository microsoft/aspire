// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using Hex1b.Automation;

namespace Aspire.Cli.EndToEnd.Tests.Helpers;

/// <summary>
/// Serves one integration package through an authenticated loopback NuGet v3 feed inside the test container.
/// </summary>
internal sealed record AuthenticatedNuGetFeed(string ServiceIndexUrl, string RequestLogPath)
{
    public static async Task<AuthenticatedNuGetFeed> StartAsync(
        DirectoryInfo directory,
        string packageId,
        string packageVersion,
        Hex1bTerminalAutomator auto,
        SequenceCounter counter)
    {
        File.WriteAllText(Path.Combine(directory.FullName, "server.py"), ServerScript);
        File.WriteAllText(
            Path.Combine(directory.FullName, "package-metadata.json"),
            JsonSerializer.Serialize(new { id = packageId, version = packageVersion }));

        // The Docker terminal mounts the workspace at /workspace; host paths are only used
        // to write fixture files and read the readiness marker through that bind mount.
        var relativeDirectory = directory.Name;
        await auto.RunCommandAsync(
            $"python3 {relativeDirectory}/server.py >{relativeDirectory}/server.log 2>&1 & " +
            $"echo $! >{relativeDirectory}/server.pid; " +
            $"for i in $(seq 1 100); do [ -f {relativeDirectory}/ready.json ] && break; sleep 0.1; done; " +
            $"if [ ! -f {relativeDirectory}/ready.json ]; then cat {relativeDirectory}/server.log; (exit 1); fi",
            counter);

        using var ready = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory.FullName, "ready.json")));
        var source = ready.RootElement.GetProperty("source").GetString()
            ?? throw new InvalidOperationException("The NuGet feed readiness marker did not contain its source.");
        return new(source, Path.Combine(directory.FullName, "requests.jsonl"));
    }

    // The service index advertises search and flat-container resources as specified by:
    // https://learn.microsoft.com/nuget/api/service-index
    // https://learn.microsoft.com/nuget/api/search-query-service-resource#versioning
    // Every route requires the deliberately synthetic test-user:test-password credentials.
    private const string ServerScript = """
        import base64
        import http.server
        import json
        import pathlib
        import threading
        import urllib.parse
        import zipfile

        root = pathlib.Path(__file__).resolve().parent
        package = json.loads((root / "package-metadata.json").read_text())
        package_id = package["id"]
        package_version = package["version"]
        package_key = package_id.lower()
        package_path, = root.glob("*.nupkg")
        authorization = "Basic " + base64.b64encode(b"test-user:test-password").decode()
        log_lock = threading.Lock()

        class Handler(http.server.BaseHTTPRequestHandler):
            def do_GET(self):
                path = urllib.parse.urlsplit(self.path).path
                authenticated = self.headers.get("Authorization") == authorization
                with log_lock, (root / "requests.jsonl").open("a") as log:
                    log.write(json.dumps({"path": path, "authenticated": authenticated}) + "\n")

                if not authenticated:
                    self.send_response(401)
                    self.send_header("WWW-Authenticate", 'Basic realm="Aspire test feed"')
                    self.send_header("Content-Length", "0")
                    self.end_headers()
                    return

                if path == "/v3/index.json":
                    self.write_json({
                        "version": "3.0.0",
                        "resources": [
                            {"@id": base_url + "/search", "@type": "SearchQueryService"},
                            {"@id": base_url + "/flat/", "@type": "PackageBaseAddress/3.0.0"}
                        ]
                    })
                elif path == "/search":
                    self.write_json({
                        "totalHits": 1,
                        "data": [{
                            "id": package_id,
                            "version": package_version,
                            "description": "Authenticated integration source regression fixture",
                            "tags": ["aspire", "integration", "hosting", "polyglot"],
                            "totalDownloads": 1,
                            "versions": [{"version": package_version, "downloads": 1}]
                        }]
                    })
                elif path == f"/flat/{package_key}/index.json":
                    self.write_json({"versions": [package_version]})
                elif path == f"/flat/{package_key}/{package_version}/{package_key}.{package_version}.nupkg":
                    self.write_content(package_path.read_bytes(), "application/octet-stream")
                elif path == f"/flat/{package_key}/{package_version}/{package_key}.nuspec":
                    with zipfile.ZipFile(package_path) as archive:
                        name, = (name for name in archive.namelist() if name.endswith(".nuspec"))
                        self.write_content(archive.read(name), "application/xml")
                else:
                    self.send_error(404)

            def write_json(self, value):
                self.write_content(json.dumps(value).encode(), "application/json")

            def write_content(self, content, content_type):
                self.send_response(200)
                self.send_header("Content-Type", content_type)
                self.send_header("Content-Length", str(len(content)))
                self.end_headers()
                self.wfile.write(content)

        server = http.server.ThreadingHTTPServer(("127.0.0.1", 0), Handler)
        base_url = "http://127.0.0.1:" + str(server.server_address[1])
        (root / "ready.json").write_text(json.dumps({"source": base_url + "/v3/index.json"}))
        server.serve_forever()
        """;
}
