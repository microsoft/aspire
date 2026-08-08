// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.IO.Enumeration;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;
using YamlDotNet.RepresentationModel;

namespace Infrastructure.Tests;

/// <summary>
/// Guards the npm-ecosystem lockfiles that ship in templates and test fixtures against acquiring
/// packages from outside the approved dotnet-public-npm feed.
/// </summary>
/// <remarks>
/// extension/scripts/validate-lockfile-registry.cjs performs the same check for extension/yarn.lock,
/// but it only covers that one file. The lockfiles guarded here are just as load-bearing: ts-starter
/// and py-starter ship theirs to users through `aspire new`, so a stray public-registry URL there
/// becomes a package every scaffolded app downloads from an unapproved source. The
/// tests/PolyglotAppHosts fixtures are installed in CI by
/// .github/workflows/polyglot-validation/test-typescript-playground.sh, which runs `npm install`,
/// `pnpm install --ignore-workspace`, `yarn install`, or `bun install` depending on the fixture, so
/// every package manager's lockfile is an acquisition path of its own.
///
/// This also catches a drift npm produces on its own. Azure Artifacts answers tarball requests with a
/// redirect to a CDN host, and npm records the redirect target rather than the configured registry:
///
///   "resolved": "https://ms-feed-17.pkgs.visualstudio.com/1es-public/_packaging/npm-public/npm/registry/typescript/-/typescript-6.0.3.tgz"
///
/// Only entries added or refreshed by a given `npm install` pick up that form, so a lockfile ends up
/// with a mix of hosts and reviewers see nothing obviously wrong. Rewrite the host back to the
/// canonical feed when regenerating a lockfile.
/// </remarks>
public class NpmLockfileRegistryTests
{
    private const string ApprovedFeedHost = "pkgs.dev.azure.com";

    // The trailing slash matters: without it "/dnceng/public/_packaging/dotnet-public-npm-evil/"
    // would satisfy the prefix test.
    private const string ApprovedFeedPathPrefix = "/dnceng/public/_packaging/dotnet-public-npm/";
    private const string ApprovedNpmRegistry = "https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet-public-npm/npm/registry/";

    /// <summary>
    /// A lockfile value is treated as a remote acquisition only when it names a scheme with an
    /// authority. That excludes the intra-lockfile references every format uses for local packages —
    /// npm's <c>"resolved": "node_modules/foo"</c>, Bun's <c>"pkg@workspace:packages/foo"</c>, and
    /// Yarn's <c>"pkg@npm:1.0.0"</c> — while still catching non-HTTP origins such as
    /// <c>git+ssh://git@github.com/...</c>, which a scheme-specific check would let through.
    /// </summary>
    private const string RemoteReferenceMarker = "://";

    /// <summary>
    /// Points every npm-ecosystem package manager at the approved feed. It is asserted on here,
    /// rather than only in the workflow, because it is the sole enforcement point for the acquisition
    /// paths whose lockfiles pin no tarball URLs.
    /// </summary>
    private const string PolyglotValidationDirectory = ".github/workflows/polyglot-validation";
    private const string RegistryEnvScriptName = "npm-registry-env.sh";
    private const string RegistryEnvScriptPath = $"{PolyglotValidationDirectory}/{RegistryEnvScriptName}";

    /// <summary>
    /// The shipped and fixture lockfiles are enumerated from known directories rather than by a
    /// repo-wide glob, so the set stays bounded. A repo-wide glob would pick up lockfiles under
    /// node_modules, build output, and scratch directories left behind by local runs.
    /// </summary>
    public static TheoryData<string> NpmLockfilePaths
    {
        get
        {
            var paths = new TheoryData<string>
            {
                Path.Combine("src", "Aspire.Cli", "Templating", "Templates", "ts-starter", "package-lock.json"),
                Path.Combine("src", "Aspire.Cli", "Templating", "Templates", "py-starter", "package-lock.json"),
                Path.Combine("tests", "Aspire.Hosting.CodeGeneration.TypeScript.JsTests", "package-lock.json"),
            };

            foreach (var lockfile in EnumeratePolyglotLockfiles("package-lock.json"))
            {
                paths.Add(lockfile);
            }

            return paths;
        }
    }

    public static TheoryData<string> BunLockfilePaths => ToTheoryData(EnumeratePolyglotLockfiles("bun.lock"));

    public static TheoryData<string> PnpmLockfilePaths => ToTheoryData(EnumeratePolyglotLockfiles("pnpm-lock.yaml"));

    public static TheoryData<string> YarnLockfilePaths => ToTheoryData(EnumeratePolyglotLockfiles("yarn.lock"));

    /// <summary>
    /// The lockfile names each package manager writes, and which the theories above parse.
    /// </summary>
    private static readonly string[] s_recognizedLockfileNames =
    [
        "package-lock.json",
        "bun.lock",
        "pnpm-lock.yaml",
        "yarn.lock",
    ];

    /// <summary>
    /// Lockfiles belonging to ecosystems this guard is not about. Named explicitly so that skipping
    /// them is a decision on the record rather than a pattern that happens not to match.
    /// </summary>
    private static readonly string[] s_ignoredLockfilePatterns = ["pylock.*.toml"];

    /// <summary>
    /// Fails when a fixture introduces a lockfile format none of the theories above parse.
    /// </summary>
    /// <remarks>
    /// The four theories each ask for a filename they already know how to read, so adding a package
    /// manager to the polyglot fixtures — Deno, or Bun's binary bun.lockb — would add an acquisition
    /// path that no theory enumerates and nothing would go red. That is the same failure this PR is
    /// about: a guard that silently stops covering what it is supposed to cover. Discovering
    /// lockfile-shaped files and requiring each to be claimed turns "unparsed" into a build break.
    /// </remarks>
    [Fact]
    public void PolyglotFixtures_ContainNoLockfileFormatThisGuardCannotParse()
    {
        var unrecognized = EnumeratePolyglotFiles()
            .Where(path => IsLockfileShaped(Path.GetFileName(path)))
            .Where(path => !s_recognizedLockfileNames.Contains(Path.GetFileName(path), StringComparer.Ordinal))
            .Select(path => Path.GetRelativePath(RepoRoot.Path, path).Replace(Path.DirectorySeparatorChar, '/'))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal([], unrecognized);
    }

    /// <summary>
    /// Fails when a polyglot fixture ships package-manager config that redirects acquisition.
    /// </summary>
    /// <remarks>
    /// npm-registry-env.sh verifies the environment from a directory owned by no project, because a
    /// package manager refuses to answer config questions inside a project claimed by a different
    /// one. That leaves per-project .npmrc and .yarnrc.yml unchecked at runtime, and project config
    /// outranks the environment for npm and pnpm, so a fixture could opt itself back onto the public
    /// registry. Asserting it here covers the half the preflight structurally cannot.
    /// </remarks>
    [Fact]
    public void PolyglotFixtures_DoNotOverrideTheRegistry()
    {
        // Matches an .npmrc `registry=` or `@scope:registry=` key and a .yarnrc.yml
        // `npmRegistryServer:` / `npmScopes.<scope>.npmRegistryServer:` key, in both cases capturing
        // everything after the delimiter so an unapproved value is reported rather than skipped.
        var registryKey = new Regex(
            @"^\s*(?<key>(@[^\s:]+:)?registry|npmRegistryServer|npmPublishRegistry)\s*[=:]\s*(?<value>\S+)\s*$",
            RegexOptions.Multiline);

        var overrides = EnumeratePolyglotFiles()
            .Where(path => Path.GetFileName(path) is ".npmrc" or ".yarnrc" or ".yarnrc.yml")
            .SelectMany(path => registryKey.Matches(File.ReadAllText(path))
                .Where(match => !IsApprovedFeedUrl(match.Groups["value"].Value.Trim('"', '\'')))
                .Select(match => $"{Path.GetRelativePath(RepoRoot.Path, path).Replace(Path.DirectorySeparatorChar, '/')} -> {match.Groups["key"].Value}={match.Groups["value"].Value}"))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal([], overrides);
    }

    /// <summary>
    /// A file is lockfile-shaped when its name ends in .lock/.lockb, or pairs a "lock" or
    /// "shrinkwrap" word with a data extension — package-lock.json, pnpm-lock.yaml,
    /// npm-shrinkwrap.json. Deliberately broader than the recognized set so a new format is caught.
    /// </summary>
    private static bool IsLockfileShaped(string fileName)
    {
        if (s_ignoredLockfilePatterns.Any(pattern => FileSystemName.MatchesSimpleExpression(pattern, fileName)))
        {
            return false;
        }

        if (fileName.EndsWith(".lock", StringComparison.OrdinalIgnoreCase) ||
            fileName.EndsWith(".lockb", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return (fileName.Contains("lock", StringComparison.OrdinalIgnoreCase) ||
                fileName.Contains("shrinkwrap", StringComparison.OrdinalIgnoreCase)) &&
               Path.GetExtension(fileName) is ".json" or ".yaml" or ".yml" or ".toml";
    }

    private static IEnumerable<string> EnumeratePolyglotFiles()
    {
        var polyglotRoot = Path.Combine(RepoRoot.Path, "tests", "PolyglotAppHosts");

        return Directory.EnumerateFiles(polyglotRoot, "*", SearchOption.AllDirectories)
            .Where(path => !path.Split(Path.DirectorySeparatorChar).Contains("node_modules", StringComparer.Ordinal));
    }

    /// <summary>
    /// Records which shipped lockfiles still resolve through an unapproved registry, so the gap is
    /// visible and bounded rather than implicit.
    /// </summary>
    /// <remarks>
    /// The theory above guards a named set, which cannot notice a lockfile that was never added to
    /// it. Every `package-lock.json` under src/ ships to users, so this discovers them from disk and
    /// pins both the exact set that is not yet on the approved feed and the exact origins each one is
    /// still allowed to use.
    ///
    /// Pinning the paths alone is not enough: an allow-list keyed only on file path would stay green
    /// if someone swapped registry.npmjs.org for an arbitrary external host, because the set of
    /// offending files would not change. Recording the origins means the only tolerated drift is the
    /// drift that already exists — a new host in any of these files fails immediately.
    ///
    /// These four are pre-existing and predate the lockfile guard. Normalizing them means regenerating
    /// against the approved feed, which changes what `aspire new` ships and is deliberately not
    /// bundled into this change. Normalizing one makes this fail until its entry is removed; adding a
    /// new unnormalized lockfile fails immediately.
    /// </remarks>
    [Fact]
    public void ShippedLockfiles_NotYetOnTheApprovedFeed_UseExactlyTheKnownOrigins()
    {
        var sourceRoot = Path.Combine(RepoRoot.Path, "src");

        var unnormalized = Directory.EnumerateFiles(sourceRoot, "package-lock.json", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}node_modules{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Select(path => (
                Path: Path.GetRelativePath(RepoRoot.Path, path).Replace(Path.DirectorySeparatorChar, '/'),
                Origins: OriginsOf(ScanNpmLockfile(File.ReadAllText(path)).Offenders)))
            .Where(entry => entry.Origins.Length > 0)
            .OrderBy(entry => entry.Path, StringComparer.Ordinal)
            .ToDictionary(entry => entry.Path, entry => entry.Origins, StringComparer.Ordinal);

        var expected = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["src/Aspire.Cli/Templating/Templates/java-starter/frontend/package-lock.json"] = ["https://registry.npmjs.org"],
            ["src/Aspire.Cli/Templating/Templates/py-starter/frontend/package-lock.json"] = ["https://registry.npmjs.org"],
            ["src/Aspire.Cli/Templating/Templates/ts-starter/frontend/package-lock.json"] = ["https://registry.npmjs.org"],
            ["src/Aspire.ProjectTemplates/templates/aspire-ts-cs-starter/frontend/package-lock.json"] = ["https://ms-feed-2.pkgs.visualstudio.com"],
        };

        Assert.Equal(expected.Keys.Order(StringComparer.Ordinal), unnormalized.Keys.Order(StringComparer.Ordinal));

        foreach (var (path, origins) in expected)
        {
            Assert.Equal(origins, unnormalized[path]);
        }
    }

    /// <summary>
    /// Reduces offending references to their distinct scheme+authority so the expectation records
    /// where a lockfile still resolves from without pinning every individual package URL.
    /// </summary>
    /// <remarks>
    /// Offenders are recorded as "&lt;entry name&gt; -&gt; &lt;url&gt;", for example:
    ///   node_modules/@emnapi/core -&gt; https://registry.npmjs.org/@emnapi/core/-/core-1.5.0.tgz
    /// The entry name can itself contain "-" and "&gt;", so split on the first " -&gt; " only.
    /// </remarks>
    private static string[] OriginsOf(IReadOnlyList<string> offenders)
    {
        const string ReferenceSeparator = " -> ";

        var origins = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var offender in offenders)
        {
            var separatorIndex = offender.IndexOf(ReferenceSeparator, StringComparison.Ordinal);
            var url = separatorIndex < 0 ? offender : offender[(separatorIndex + ReferenceSeparator.Length)..];

            // An offender that is not a well-formed absolute URI is still drift, so surface it
            // verbatim rather than dropping it and shrinking the recorded set.
            origins.Add(Uri.TryCreate(url, UriKind.Absolute, out var uri)
                ? uri.GetLeftPart(UriPartial.Authority)
                : url);
        }

        return [.. origins];
    }

    [Theory]
    [MemberData(nameof(NpmLockfilePaths))]
    public void NpmLockfile_ResolvesEveryPackageThroughTheApprovedFeed(string relativePath)
    {
        var scan = ScanNpmLockfile(ReadLockfile(relativePath));

        // A lockfile that parsed into zero remote references would pass the offender check
        // vacuously, which is exactly how this guard would rot without anyone noticing.
        Assert.NotEqual(0, scan.RemoteReferenceCount);
        Assert.Empty(scan.Offenders);
    }

    [Theory]
    [MemberData(nameof(BunLockfilePaths))]
    public void BunLockfile_ResolvesEveryPackageThroughTheApprovedFeed(string relativePath)
    {
        var scan = ScanBunLockfile(ReadLockfile(relativePath));

        Assert.NotEqual(0, scan.RemoteReferenceCount);
        Assert.Empty(scan.Offenders);
    }

    [Theory]
    [MemberData(nameof(PnpmLockfilePaths))]
    public void PnpmLockfile_ResolvesEveryPackageThroughTheApprovedFeed(string relativePath)
    {
        var scan = ScanPnpmLockfile(ReadLockfile(relativePath));

        Assert.NotEqual(0, scan.RemoteReferenceCount);
        Assert.Empty(scan.Offenders);
    }

    [Theory]
    [MemberData(nameof(YarnLockfilePaths))]
    public void YarnLockfile_ResolvesEveryPackageThroughTheApprovedFeed(string relativePath)
    {
        var contents = ReadLockfile(relativePath);

        // Unlike the other three formats, a Yarn Berry lockfile records no tarball host at all: the
        // registry comes from .yarnrc.yml / YARN_NPM_REGISTRY_SERVER at install time. So the
        // offender scan below is a tripwire for URL-pinned entries rather than the main assertion,
        // and the format check is what keeps it meaningful. Yarn Classic *does* pin an absolute
        // `resolved` URL per entry, and test-typescript-playground.sh refuses to run a Classic
        // lockfile, so a downgrade has to fail loudly here instead of silently changing where
        // packages come from.
        Assert.Equal(YarnLockfileFormat.Berry, GetYarnLockfileFormat(contents));

        Assert.Empty(ScanYarnLockfile(contents).Offenders);
    }

    /// <summary>
    /// A lockfile can only pin the feed for packages it already records, and two acquisition paths in
    /// the polyglot job are not covered by the theories above at all.
    /// tests/PolyglotAppHosts/Aspire.Hosting.Blazor/TypeScript has no lockfile, so `npm install`
    /// resolves everything remotely, and the Yarn Berry fixture's lockfile stores locators rather than
    /// tarball URLs, so Berry re-resolves through whatever registry is configured. For those, the
    /// install-time registry configuration is the only thing standing between CI and the public
    /// registry.
    /// </summary>
    /// <remarks>
    /// The repository-root .npmrc does not cover them: npm resolves project config from
    /// `localPrefix`, the nearest ancestor containing package.json or node_modules, which for every
    /// AppHost is the AppHost directory itself. Environment variables outrank project config, so
    /// exporting them is what actually reaches the AppHosts.
    /// </remarks>
    [Fact]
    public void RegistryEnvScript_ExportsTheApprovedRegistryForEveryPackageManager()
    {
        var script = ReadRepoFile(RegistryEnvScriptPath);

        // Each manager reads a different setting and they are not interchangeable — Yarn Berry
        // ignores npm_config_registry, and npm ignores YARN_NPM_REGISTRY_SERVER — so assert on the
        // whole set rather than on any one of them.
        Assert.Equal(
            new[]
            {
                "BUN_CONFIG_REGISTRY",
                "COREPACK_NPM_REGISTRY",
                "NPM_CONFIG_REGISTRY",
                "YARN_NPM_REGISTRY_SERVER",
                "npm_config_registry",
            },
            ExportedRegistryVariables(script));
    }

    /// <summary>
    /// Fails if the helper stops isolating npm from ambient user- and global-level config.
    /// </summary>
    /// <remarks>
    /// `registry` sets the default only. A `@scope:registry` key is a separate setting that wins for
    /// that scope, and neither npm_config_registry nor `npm --registry` overrides it. Measured with
    /// npm 11.4.2, a user-level `@types:registry=https://scoped.example.invalid/` sent
    /// `npm install @types/node` to that host while `npm config get registry` still reported the
    /// approved feed. The AppHosts install @types/* and @esbuild/*, so pointing both config paths at
    /// files the helper owns is what keeps an ambient scoped key from redirecting them.
    /// </remarks>
    [Fact]
    public void RegistryEnvScript_IsolatesNpmFromAmbientScopedRegistries()
    {
        var script = ReadRepoFile(RegistryEnvScriptPath);

        var isolatingExports = Regex.Matches(script, @"^export (?<name>NPM_CONFIG_(?:USERCONFIG|GLOBALCONFIG))=", RegexOptions.Multiline)
            .Select(match => match.Groups["name"].Value)
            .Order(StringComparer.Ordinal);

        Assert.Equal(new[] { "NPM_CONFIG_GLOBALCONFIG", "NPM_CONFIG_USERCONFIG" }, isolatingExports);

        // Isolation alone would not notice a scoped key from a source it does not control, so the
        // helper also enumerates what npm reports and fails on any scope off the approved feed.
        Assert.Contains("@[^:]+:registry", script, StringComparison.Ordinal);
    }

    [Fact]
    public void RegistryEnvScript_DefinesTheApprovedFeed()
    {
        var script = ReadRepoFile(RegistryEnvScriptPath);

        var match = Regex.Match(script, @"^APPROVED_NPM_REGISTRY=""(?<url>[^""]+)""$", RegexOptions.Multiline);

        Assert.True(match.Success, $"{RegistryEnvScriptPath} no longer defines the approved npm registry in the expected form.");
        Assert.Equal(ApprovedNpmRegistry, match.Groups["url"].Value);
        Assert.True(IsApprovedFeedUrl(match.Groups["url"].Value), $"NPM_REGISTRY is set from {match.Groups["url"].Value}, which is not the approved feed.");
    }

    [Fact]
    public void RegistryEnvScript_ComparesResolvedRegistriesAgainstTheCanonicalApprovedFeed()
    {
        var script = ReadRepoFile(RegistryEnvScriptPath);

        Assert.Contains($"APPROVED_NPM_REGISTRY=\"{ApprovedNpmRegistry}\"", script, StringComparison.Ordinal);
        Assert.Contains("NPM_REGISTRY=\"$APPROVED_NPM_REGISTRY\"", script, StringComparison.Ordinal);
        Assert.DoesNotContain("NPM_REGISTRY=\"${NPM_REGISTRY:-", script, StringComparison.Ordinal);

        var comparisonTargets = Regex.Matches(script, @"!= ""\$\{(?<target>[A-Za-z_][A-Za-z0-9_]*)%/\}""", RegexOptions.Multiline)
            .Select(match => match.Groups["target"].Value)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(["APPROVED_NPM_REGISTRY"], comparisonTargets);
    }

    /// <summary>
    /// Sourcing after the first acquisition would leave that acquisition on the ambient registry, so
    /// position is part of the guarantee rather than a style preference.
    /// </summary>
    [Theory]
    [MemberData(nameof(PackageAcquiringPolyglotScripts))]
    public void PolyglotScript_SourcesTheRegistryEnvBeforeAcquiringPackages(string scriptName)
    {
        var script = ReadRepoFile($"{PolyglotValidationDirectory}/{scriptName}");

        var sourceIndex = script.IndexOf($"/{RegistryEnvScriptName}\"", StringComparison.Ordinal);
        Assert.True(sourceIndex > 0, $"{scriptName} does not source {RegistryEnvScriptName}, so its installs use the ambient registry.");

        var firstAcquisitionIndex = FindFirstPackageAcquisition(script);
        Assert.True(
            sourceIndex < firstAcquisitionIndex,
            $"{scriptName} sources {RegistryEnvScriptName} at offset {sourceIndex}, after it first acquires packages at offset {firstAcquisitionIndex}.");
    }

    /// <summary>
    /// The guarantee belongs to the polyglot job rather than to any one script, so a newly added
    /// script that acquires npm packages has to be guarded too. Discovering the set from disk means
    /// that shows up as a failure here instead of as a silent gap.
    /// </summary>
    [Fact]
    public void PolyglotScripts_ThatAcquireNpmPackagesAreAllGuarded()
    {
        var directory = Path.Combine(RepoRoot.Path, PolyglotValidationDirectory);

        var acquiring = Directory.EnumerateFiles(directory, "*.sh")
            .Where(path => Path.GetFileName(path) != RegistryEnvScriptName)
            .Where(path => FindFirstPackageAcquisition(File.ReadAllText(path)) != int.MaxValue)
            .Select(Path.GetFileName)
            .OrderBy(name => name, StringComparer.Ordinal);

        Assert.Equal(PackageAcquiringScriptNames, acquiring);
    }

    /// <summary>
    /// A sourced helper only enforces anything if it is actually present where the script runs. The
    /// TypeScript image previously copied its scripts by name, so extracting a helper out of one of
    /// them left the image without it and the `source` failed at job time.
    /// </summary>
    /// <remarks>
    /// Every polyglot image is checked, not just the TypeScript one, because the images that still
    /// enumerate their scripts would hit the same failure the first time one of their scripts grows
    /// a sibling helper.
    /// </remarks>
    [Theory]
    [MemberData(nameof(PolyglotDockerfiles))]
    public void PolyglotValidationImage_ShipsEveryFileItsScriptsSource(string dockerfileName)
    {
        var copied = FilesCopiedIntoImage(ReadRepoFile($"{PolyglotValidationDirectory}/{dockerfileName}"));

        // Only the scripts the image actually ships can run in it, so they define what has to resolve.
        var missing = copied
            .Where(name => name.EndsWith(".sh", StringComparison.Ordinal))
            .SelectMany(name => SourcedFileNames(ReadRepoFile($"{PolyglotValidationDirectory}/{name}")))
            .Distinct()
            .Where(name => !copied.Contains(name))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(Array.Empty<string>(), missing);
    }

    /// <summary>
    /// Asserted separately from the theory above so that a regex that silently stops matching cannot
    /// make every image trivially pass.
    /// </summary>
    [Fact]
    public void TypeScriptValidationImage_ShipsTheRegistryEnvHelper()
    {
        var copied = FilesCopiedIntoImage(ReadRepoFile($"{PolyglotValidationDirectory}/Dockerfile.typescript"));

        var sourced = s_packageAcquiringScriptNames
            .SelectMany(name => SourcedFileNames(ReadRepoFile($"{PolyglotValidationDirectory}/{name}")))
            .Distinct()
            .ToArray();

        Assert.Equal(new[] { RegistryEnvScriptName }, sourced);
        Assert.Contains(RegistryEnvScriptName, copied);
    }

    public static TheoryData<string> PolyglotDockerfiles => ToTheoryData(
        Directory.EnumerateFiles(Path.Combine(RepoRoot.Path, PolyglotValidationDirectory), "Dockerfile.*")
            .Select(Path.GetFileName)
            .Where(name => name is not null)
            .Select(name => name!)
            .OrderBy(name => name, StringComparer.Ordinal));

    /// <summary>
    /// Resolves the file names a Dockerfile puts in the image, expanding `COPY *.sh` against the
    /// build context so a glob counts as covering every matching file rather than none.
    /// </summary>
    private static HashSet<string> FilesCopiedIntoImage(string dockerfile)
    {
        // COPY lines here are the simple `COPY <src> <dest>` form, e.g.
        //   COPY *.sh /scripts/
        //   COPY setup-local-cli.sh /scripts/setup-local-cli.sh
        var copied = new HashSet<string>(StringComparer.Ordinal);
        var contextFiles = Directory.EnumerateFiles(Path.Combine(RepoRoot.Path, PolyglotValidationDirectory))
            .Select(Path.GetFileName)
            .ToArray();

        foreach (Match match in Regex.Matches(dockerfile, @"^COPY\s+(?<source>\S+)\s+\S+\s*$", RegexOptions.Multiline))
        {
            var source = match.Groups["source"].Value;

            if (source.Contains('*'))
            {
                var pattern = "^" + Regex.Escape(source).Replace("\\*", ".*") + "$";
                foreach (var file in contextFiles.Where(file => file is not null && Regex.IsMatch(file, pattern)))
                {
                    copied.Add(file!);
                }

                continue;
            }

            copied.Add(source);
        }

        return copied;
    }

    private static IEnumerable<string> SourcedFileNames(string script)
    {
        // Sibling helpers are referenced through the `$(dirname "${BASH_SOURCE[0]}")/name.sh` idiom,
        // either sourced inline or assigned to a variable first so the script can check the file
        // exists before sourcing it:
        //
        //   NPM_REGISTRY_ENV="$(dirname "${BASH_SOURCE[0]}")/npm-registry-env.sh"
        //   source "$NPM_REGISTRY_ENV"
        //
        // Matching the idiom rather than the `source` keyword therefore catches both shapes; keying
        // off `source` alone would miss the indirect one and silently pass.
        var siblingReferences = Regex.Matches(script, @"\$\(dirname [^)]*\)/(?<name>[A-Za-z0-9._-]+\.sh)")
            .Select(match => match.Groups["name"].Value);

        var directSources = Regex.Matches(script, @"^(?:source|\.)\s+""?[^""\s]*/(?<name>[A-Za-z0-9._-]+\.sh)""?\s*$", RegexOptions.Multiline)
            .Select(match => match.Groups["name"].Value);

        return siblingReferences.Concat(directSources);
    }

    /// <summary>
    /// Commands that acquire packages from the npm ecosystem, either directly or through the
    /// TypeScript guest runtime. `aspire init --language typescript` installs the scaffolded AppHost's
    /// dependencies with the guest runtime's package manager and passes no --registry, so it acquires
    /// from the ambient registry just as a bare `npm install` would.
    ///
    /// Deliberately npm-specific: the Python, Go, Java, and Rust scripts also run `aspire init`, but
    /// their guest runtimes use pip, Go modules, Maven, and Cargo, which these variables do not
    /// configure.
    /// </summary>
    private static readonly string[] s_packageAcquisitionMarkers =
    [
        "npm install",
        "npm exec",
        "npx ",
        "pnpm install",
        "yarn install",
        "bun install",
        "bunx ",
        "aspire init --language typescript",
    ];

    private static readonly string[] s_packageAcquiringScriptNames =
    [
        "test-typescript-playground.sh",
        "test-typescript.sh",
    ];

    public static IEnumerable<string> PackageAcquiringScriptNames => s_packageAcquiringScriptNames;

    public static TheoryData<string> PackageAcquiringPolyglotScripts => ToTheoryData(s_packageAcquiringScriptNames);

    /// <summary>
    /// Returns <see cref="int.MaxValue"/> when the script acquires nothing, so callers can compare
    /// positions without special-casing the empty result.
    /// </summary>
    private static int FindFirstPackageAcquisition(string script)
    {
        // Comment lines describe these commands without running them — the explanation of why a
        // script sources the registry env names the very commands it is guarding — so they would
        // otherwise be found ahead of the real invocation. Blank them to the same width rather than
        // dropping them, so the returned offset still refers to a position in the original script.
        var executable = string.Join(
            '\n',
            script.Split('\n').Select(line => line.TrimStart().StartsWith('#') ? new string(' ', line.Length) : line));

        return s_packageAcquisitionMarkers
            .Select(marker => executable.IndexOf(marker, StringComparison.Ordinal))
            .Where(index => index >= 0)
            .DefaultIfEmpty(int.MaxValue)
            .Min();
    }

    private static SortedSet<string> ExportedRegistryVariables(string script)
    {
        // Matches `export npm_config_registry="$NPM_REGISTRY"`. Only exports assigned from
        // NPM_REGISTRY count: a hard-coded URL elsewhere would drift from the helper's single
        // exported source of truth.
        var matches = Regex.Matches(script, @"^export (?<name>[A-Za-z_][A-Za-z0-9_]*)=""\$NPM_REGISTRY""$", RegexOptions.Multiline);

        return new SortedSet<string>(matches.Select(match => match.Groups["name"].Value), StringComparer.Ordinal);
    }

    [Theory]
    // The canonical form every guarded lockfile uses today.
    [InlineData("https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet-public-npm/npm/registry/ms/-/ms-2.1.3.tgz", true)]
    // Uri lower-cases the host during canonicalization, so a shouted host is still the approved one.
    [InlineData("https://PKGS.DEV.AZURE.COM/dnceng/public/_packaging/dotnet-public-npm/npm/registry/ms/-/ms-2.1.3.tgz", true)]
    // Plaintext transport: the tarball is the same, but it arrives over a channel anyone on the path can rewrite.
    [InlineData("http://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet-public-npm/npm/registry/ms/-/ms-2.1.3.tgz", false)]
    // Approved-feed text pushed into the path of an attacker-controlled host.
    [InlineData("https://evil.example.com/pkgs.dev.azure.com/dnceng/public/_packaging/dotnet-public-npm/npm/registry/ms/-/ms-2.1.3.tgz", false)]
    // Approved-feed text pushed into the query string of an attacker-controlled host.
    [InlineData("https://evil.example.com/ms-2.1.3.tgz?from=https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet-public-npm/npm/registry/", false)]
    // Approved host and approved feed name, but a different Azure DevOps organization. The org is
    // the first path segment, so matching the host alone would accept every org on the service.
    [InlineData("https://pkgs.dev.azure.com/contoso/public/_packaging/dotnet-public-npm/npm/registry/ms/-/ms-2.1.3.tgz", false)]
    // Userinfo trick: everything before '@' is credentials, so the real host is evil.example.com.
    [InlineData("https://pkgs.dev.azure.com@evil.example.com/dnceng/public/_packaging/dotnet-public-npm/npm/registry/ms/-/ms-2.1.3.tgz", false)]
    // Suffix host spoof.
    [InlineData("https://pkgs.dev.azure.com.evil.example.com/dnceng/public/_packaging/dotnet-public-npm/npm/registry/ms/-/ms-2.1.3.tgz", false)]
    // Subdomain host spoof.
    [InlineData("https://evil.pkgs.dev.azure.com/dnceng/public/_packaging/dotnet-public-npm/npm/registry/ms/-/ms-2.1.3.tgz", false)]
    // Right host, wrong port: a proxy on 8443 is not the feed.
    [InlineData("https://pkgs.dev.azure.com:8443/dnceng/public/_packaging/dotnet-public-npm/npm/registry/ms/-/ms-2.1.3.tgz", false)]
    // Right host, different feed on the same organization.
    [InlineData("https://pkgs.dev.azure.com/dnceng/internal/_packaging/dotnet-public-npm/npm/registry/ms/-/ms-2.1.3.tgz", false)]
    // Feed name that merely starts with the approved one.
    [InlineData("https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet-public-npm-evil/npm/registry/ms/-/ms-2.1.3.tgz", false)]
    // Path traversal, raw and percent-encoded. Uri collapses both to /dnceng/evil/... before the prefix test runs.
    [InlineData("https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet-public-npm/../../../evil/ms-2.1.3.tgz", false)]
    [InlineData("https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet-public-npm/%2e%2e/%2e%2e/%2e%2e/evil/ms-2.1.3.tgz", false)]
    // The Azure Artifacts CDN redirect target npm records on its own; a different feed entirely.
    [InlineData("https://ms-feed-17.pkgs.visualstudio.com/1es-public/_packaging/npm-public/npm/registry/typescript/-/typescript-6.0.3.tgz", false)]
    // The public registry.
    [InlineData("https://registry.npmjs.org/ms/-/ms-2.1.3.tgz", false)]
    // Non-HTTP origins.
    [InlineData("git+ssh://git@github.com/someone/ms.git#0000000000000000000000000000000000000000", false)]
    [InlineData("file:///etc/ms-2.1.3.tgz", false)]
    public void IsApprovedFeedUrl_AcceptsOnlyTheExactHttpsFeedPrefix(string url, bool expected)
    {
        Assert.Equal(expected, IsApprovedFeedUrl(url));
    }

    [Fact]
    public void ScanNpmLockfile_FlagsUnapprovedResolvedUrls()
    {
        // Mirrors the shape npm writes: every dependency is keyed under "packages", the root project
        // is the empty-string key with no "resolved", and link/workspace entries resolve to a path on
        // disk instead of a registry.
        const string Lockfile = """
            {
              "name": "fixture",
              "lockfileVersion": 3,
              "packages": {
                "": { "name": "fixture", "version": "1.0.0" },
                "node_modules/approved": {
                  "version": "2.1.3",
                  "resolved": "https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet-public-npm/npm/registry/approved/-/approved-2.1.3.tgz"
                },
                "node_modules/insecure": {
                  "version": "2.1.3",
                  "resolved": "http://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet-public-npm/npm/registry/insecure/-/insecure-2.1.3.tgz"
                },
                "node_modules/spoofed": {
                  "version": "2.1.3",
                  "resolved": "https://evil.example.com/pkgs.dev.azure.com/dnceng/public/_packaging/dotnet-public-npm/npm/registry/spoofed/-/spoofed-2.1.3.tgz"
                },
                "node_modules/linked": { "resolved": "packages/linked", "link": true }
              }
            }
            """;

        var scan = ScanNpmLockfile(Lockfile);

        string[] expected =
        [
            "node_modules/insecure -> http://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet-public-npm/npm/registry/insecure/-/insecure-2.1.3.tgz",
            "node_modules/spoofed -> https://evil.example.com/pkgs.dev.azure.com/dnceng/public/_packaging/dotnet-public-npm/npm/registry/spoofed/-/spoofed-2.1.3.tgz",
        ];

        Assert.Equal(3, scan.RemoteReferenceCount);
        Assert.Equal(expected, scan.Offenders);
    }

    [Fact]
    public void ScanBunLockfile_FlagsUnapprovedTarballUrls()
    {
        // Mirrors the shape bun writes. The file is JSONC: object members carry trailing commas, so a
        // plain JSON reader rejects it. Registry entries are 4-element arrays
        // ["name@version", tarball, metadata, integrity]; workspace and git entries are shorter and
        // carry their reference in element 0 instead, which is why the scan checks every string
        // element rather than a fixed index.
        const string Lockfile = """
            {
              "lockfileVersion": 1,
              "workspaces": {
                "": {
                  "name": "fixture",
                },
              },
              "packages": {
                "approved": ["approved@2.1.3", "https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet-public-npm/npm/registry/approved/-/approved-2.1.3.tgz", {}, "sha512-AAAA"],
                "insecure": ["insecure@2.1.3", "http://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet-public-npm/npm/registry/insecure/-/insecure-2.1.3.tgz", {}, "sha512-BBBB"],
                "spoofed": ["spoofed@2.1.3", "https://evil.example.com/pkgs.dev.azure.com/dnceng/public/_packaging/dotnet-public-npm/npm/registry/spoofed/-/spoofed-2.1.3.tgz", {}, "sha512-CCCC"],
                "workspace-only": ["workspace-only@workspace:packages/workspace-only"],
              }
            }
            """;

        var scan = ScanBunLockfile(Lockfile);

        string[] expected =
        [
            "insecure -> http://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet-public-npm/npm/registry/insecure/-/insecure-2.1.3.tgz",
            "spoofed -> https://evil.example.com/pkgs.dev.azure.com/dnceng/public/_packaging/dotnet-public-npm/npm/registry/spoofed/-/spoofed-2.1.3.tgz",
        ];

        Assert.Equal(3, scan.RemoteReferenceCount);
        Assert.Equal(expected, scan.Offenders);
    }

    [Fact]
    public void ScanPnpmLockfile_FlagsUnapprovedTarballUrls()
    {
        // Mirrors the shape pnpm writes. Hosts appear only under `packages.<id>.resolution`, as a
        // flow mapping whose `tarball` member is a plain (unquoted) scalar:
        //
        //   resolution: {integrity: sha512-AAAA, tarball: https://host/path.tgz}
        //
        // `importers` and `snapshots` restate the same packages without any host, so scanning
        // `resolution` alone covers every acquisition without double-counting. A `resolution` can
        // also describe a git or direct-tarball dependency, so every scalar in it is checked, not
        // just `tarball`.
        const string Lockfile = """
            lockfileVersion: '9.0'

            importers:

              .:
                dependencies:
                  approved:
                    specifier: ^2.1.3
                    version: 2.1.3

            packages:

              approved@2.1.3:
                resolution: {integrity: sha512-AAAA, tarball: https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet-public-npm/npm/registry/approved/-/approved-2.1.3.tgz}

              insecure@2.1.3:
                resolution: {integrity: sha512-BBBB, tarball: http://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet-public-npm/npm/registry/insecure/-/insecure-2.1.3.tgz}

              spoofed@2.1.3:
                resolution: {integrity: sha512-CCCC, tarball: https://evil.example.com/pkgs.dev.azure.com/dnceng/public/_packaging/dotnet-public-npm/npm/registry/spoofed/-/spoofed-2.1.3.tgz}

              local@2.1.3:
                resolution: {directory: packages/local, type: directory}

            snapshots:

              approved@2.1.3: {}
            """;

        var scan = ScanPnpmLockfile(Lockfile);

        string[] expected =
        [
            "insecure@2.1.3 -> http://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet-public-npm/npm/registry/insecure/-/insecure-2.1.3.tgz",
            "spoofed@2.1.3 -> https://evil.example.com/pkgs.dev.azure.com/dnceng/public/_packaging/dotnet-public-npm/npm/registry/spoofed/-/spoofed-2.1.3.tgz",
        ];

        Assert.Equal(3, scan.RemoteReferenceCount);
        Assert.Equal(expected, scan.Offenders);
    }

    [Fact]
    public void ScanYarnLockfile_FlagsUnapprovedResolutionUrls()
    {
        // Mirrors the shape Yarn Berry writes. Berry lockfiles are YAML, and a registry dependency
        // records no host — `resolution: "approved@npm:2.1.3"` defers to the configured registry.
        // A dependency pinned to a URL in package.json is the one case where a host lands in the
        // file, and it shows up inside the `resolution` locator after the '@':
        //
        //   resolution: "spoofed@https://evil.example.com/spoofed-2.1.3.tgz"
        const string Lockfile = """
            # This file is generated by running "yarn install" inside your project.
            # Manual changes might be lost - proceed with caution!

            __metadata:
              version: 9
              cacheKey: 10c0

            "approved@npm:2.1.3":
              version: 2.1.3
              resolution: "approved@npm:2.1.3"
              checksum: 10c0/aaaa
              languageName: node
              linkType: hard

            "spoofed@https://evil.example.com/spoofed-2.1.3.tgz":
              version: 2.1.3
              resolution: "spoofed@https://evil.example.com/spoofed-2.1.3.tgz"
              checksum: 10c0/cccc
              languageName: node
              linkType: hard
            """;

        var scan = ScanYarnLockfile(Lockfile);

        // The descriptor key and the `resolution` locator hold the same URL, and the scan reports one
        // acquisition per distinct entry/URL pair rather than one per occurrence.
        string[] expected = ["spoofed@https://evil.example.com/spoofed-2.1.3.tgz -> https://evil.example.com/spoofed-2.1.3.tgz"];

        Assert.Equal(1, scan.RemoteReferenceCount);
        Assert.Equal(expected, scan.Offenders);
    }

    [Fact]
    public void GetYarnLockfileFormat_DetectsClassic()
    {
        // Yarn Classic is not YAML and pins an absolute `resolved` URL per entry. The polyglot script
        // refuses to install it, so the guard has to recognize the format rather than parse it.
        const string Lockfile = """
            # THIS IS AN AUTOGENERATED FILE. DO NOT EDIT THIS FILE DIRECTLY.
            # yarn lockfile v1

            approved@^2.1.3:
              version "2.1.3"
              resolved "https://registry.yarnpkg.com/approved/-/approved-2.1.3.tgz#0000"
            """;

        Assert.Equal(YarnLockfileFormat.Classic, GetYarnLockfileFormat(Lockfile));
    }

    /// <summary>
    /// Compares the parsed scheme, host, port, and path of <paramref name="url"/> against the
    /// approved feed. A substring search over the raw text is not enough: it accepts
    /// <c>http://pkgs.dev.azure.com/...</c>, which downloads over plaintext, and
    /// <c>https://evil.example.com/pkgs.dev.azure.com/...</c>, which puts the approved feed's text in
    /// the path of a host nobody approved. Both are packages fetched from an unapproved origin.
    /// </summary>
    private static bool IsApprovedFeedUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal))
        {
            return false;
        }

        // Uri.Host excludes userinfo, so "https://pkgs.dev.azure.com@evil.example.com/..." reports
        // the host as evil.example.com and is rejected here.
        if (!string.Equals(uri.Host, ApprovedFeedHost, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!uri.IsDefaultPort)
        {
            return false;
        }

        // AbsolutePath is canonicalized before this comparison: Uri collapses "../" segments and
        // decodes "%2e%2e" first, so "/dnceng/public/_packaging/dotnet-public-npm/%2e%2e/evil/x.tgz"
        // arrives here as "/dnceng/public/evil/x.tgz" and fails the prefix test.
        return uri.AbsolutePath.StartsWith(ApprovedFeedPathPrefix, StringComparison.Ordinal);
    }

    private static LockfileScan ScanNpmLockfile(string contents)
    {
        // lockfileVersion 2 and 3 both key every dependency under "packages"; the root project is the
        // empty-string key and has no "resolved" of its own.
        using var document = JsonDocument.Parse(contents);
        var packages = document.RootElement.GetProperty("packages");

        var scan = new LockfileScanBuilder();

        foreach (var package in packages.EnumerateObject())
        {
            if (!package.Value.TryGetProperty("resolved", out var resolved) || resolved.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            scan.Inspect(package.Name, resolved.GetString()!);
        }

        return scan.Build();
    }

    private static LockfileScan ScanBunLockfile(string contents)
    {
        // bun.lock is JSONC, not JSON: bun writes a trailing comma after every object member, so the
        // reader has to allow them. Registry entries look like
        //
        //   "ms": ["ms@2.1.3", "https://<feed>/npm/registry/ms/-/ms-2.1.3.tgz", {}, "sha512-6Flz..."],
        //
        // but workspace, git, and direct-tarball entries have fewer elements and carry their
        // reference in element 0, so every string element is inspected instead of a fixed index. The
        // integrity element never contains "://", so it is filtered out by the remote-reference test.
        var options = new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };
        using var document = JsonDocument.Parse(contents, options);
        var packages = document.RootElement.GetProperty("packages");

        var scan = new LockfileScanBuilder();

        foreach (var package in packages.EnumerateObject())
        {
            if (package.Value.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var element in package.Value.EnumerateArray())
            {
                if (element.ValueKind == JsonValueKind.String)
                {
                    scan.Inspect(package.Name, element.GetString()!);
                }
            }
        }

        return scan.Build();
    }

    private static LockfileScan ScanPnpmLockfile(string contents)
    {
        // pnpm records hosts only under `packages.<id>.resolution`:
        //
        //   packages:
        //     ms@2.1.3:
        //       resolution: {integrity: sha512-6Flz..., tarball: https://<feed>/npm/registry/ms/-/ms-2.1.3.tgz}
        //
        // `importers` and `snapshots` restate the same ids without a host, so `resolution` is the
        // complete set of acquisitions. A `resolution` can also be `{type: git, repo: ..., commit:
        // ...}` or `{directory: ..., type: directory}`, so every scalar in the mapping is inspected
        // rather than just `tarball`; the non-URL members never contain "://".
        var root = LoadYamlMapping(contents);

        var scan = new LockfileScanBuilder();

        if (root is not null && root.Children.TryGetValue(new YamlScalarNode("packages"), out var packagesNode) &&
            packagesNode is YamlMappingNode packages)
        {
            foreach (var (idNode, packageNode) in packages.Children)
            {
                if (packageNode is not YamlMappingNode package ||
                    !package.Children.TryGetValue(new YamlScalarNode("resolution"), out var resolutionNode) ||
                    resolutionNode is not YamlMappingNode resolution)
                {
                    continue;
                }

                var id = (idNode as YamlScalarNode)?.Value ?? idNode.ToString();
                foreach (var value in resolution.Children.Values.OfType<YamlScalarNode>())
                {
                    if (value.Value is { } scalar)
                    {
                        scan.Inspect(id, scalar);
                    }
                }
            }
        }

        return scan.Build();
    }

    private static LockfileScan ScanYarnLockfile(string contents)
    {
        // Yarn Berry lockfiles are YAML. Every top-level key is a descriptor and each entry carries a
        // `resolution` locator:
        //
        //   "typescript@npm:^6.0.3":
        //     version: 6.0.3
        //     resolution: "typescript@npm:6.0.3"
        //
        // A registry dependency names no host at all — `npm:` is a protocol Yarn resolves against the
        // registry configured in .yarnrc.yml, not a URL. Only a dependency pinned to a URL in
        // package.json puts a host in the file, and it lands inside the locator after the '@'. Both
        // the descriptor key and the entry's scalars are inspected so either form is caught.
        var root = LoadYamlMapping(contents);

        var scan = new LockfileScanBuilder();

        if (root is not null)
        {
            foreach (var (descriptorNode, entryNode) in root.Children)
            {
                var descriptor = (descriptorNode as YamlScalarNode)?.Value ?? descriptorNode.ToString();
                scan.Inspect(descriptor, descriptor);

                if (entryNode is YamlMappingNode entry)
                {
                    foreach (var value in entry.Children.Values.OfType<YamlScalarNode>())
                    {
                        if (value.Value is { } scalar)
                        {
                            scan.Inspect(descriptor, scalar);
                        }
                    }
                }
            }
        }

        return scan.Build();
    }

    private static YarnLockfileFormat GetYarnLockfileFormat(string contents)
    {
        // Classic announces itself in the banner Yarn 1 writes at the top of every lockfile:
        //
        //   # THIS IS AN AUTOGENERATED FILE. DO NOT EDIT THIS FILE DIRECTLY.
        //   # yarn lockfile v1
        //
        // This is the same marker test-typescript-playground.sh greps for before refusing to install.
        foreach (var line in contents.Split('\n').Take(5))
        {
            if (line.Trim().Equals("# yarn lockfile v1", StringComparison.OrdinalIgnoreCase))
            {
                return YarnLockfileFormat.Classic;
            }
        }

        return YarnLockfileFormat.Berry;
    }

    private static YamlMappingNode? LoadYamlMapping(string contents)
    {
        var stream = new YamlStream();
        stream.Load(new StringReader(contents));

        return stream.Documents.Count == 0 ? null : stream.Documents[0].RootNode as YamlMappingNode;
    }

    private static string ReadLockfile(string relativePath)
    {
        var lockfilePath = Path.Combine(RepoRoot.Path, relativePath);
        Assert.True(File.Exists(lockfilePath), $"{relativePath} does not exist. Update the lockfile theory data if it moved or was removed.");

        return File.ReadAllText(lockfilePath);
    }

    private static string ReadRepoFile(string relativePath)
    {
        var path = Path.Combine(RepoRoot.Path, relativePath);
        Assert.True(File.Exists(path), $"{relativePath} does not exist. Update this test if the file moved or was renamed.");

        return File.ReadAllText(path);
    }

    /// <summary>
    /// The polyglot validation fixtures install their declared toolchain before type-checking the
    /// generated API surface, so they acquire packages in CI just like the shipped templates do.
    /// Each fixture declares one package manager, so the four lockfile names partition the set.
    /// </summary>
    private static IEnumerable<string> EnumeratePolyglotLockfiles(string fileName)
    {
        var polyglotRoot = Path.Combine(RepoRoot.Path, "tests", "PolyglotAppHosts");

        return Directory.EnumerateFiles(polyglotRoot, fileName, SearchOption.AllDirectories)
            // A local `npm install` in a fixture leaves committed-looking lockfiles under
            // node_modules. Those are never checked in, so failing on them would only break local runs.
            .Where(path => !path.Split(Path.DirectorySeparatorChar).Contains("node_modules", StringComparer.Ordinal))
            .Select(path => Path.GetRelativePath(RepoRoot.Path, path))
            .Order(StringComparer.Ordinal);
    }

    private static TheoryData<string> ToTheoryData(IEnumerable<string> values)
    {
        var data = new TheoryData<string>();
        foreach (var value in values)
        {
            data.Add(value);
        }

        return data;
    }

    private sealed record LockfileScan(int RemoteReferenceCount, IReadOnlyList<string> Offenders);

    /// <summary>
    /// Accumulates the remote acquisitions a lockfile declares, de-duplicated because some formats
    /// state the same origin twice (a Yarn entry repeats its locator in the descriptor key).
    /// </summary>
    private sealed class LockfileScanBuilder
    {
        private readonly SortedSet<string> _remoteReferences = new(StringComparer.Ordinal);
        private readonly SortedSet<string> _offenders = new(StringComparer.Ordinal);

        /// <summary>
        /// Records <paramref name="value"/> as an offender when it names a remote origin that is not
        /// the approved feed. Values without an authority are intra-lockfile references and ignored.
        /// </summary>
        public void Inspect(string entryName, string value)
        {
            var marker = value.IndexOf(RemoteReferenceMarker, StringComparison.Ordinal);
            if (marker < 0)
            {
                return;
            }

            // Yarn embeds the URL inside a locator such as "spoofed@https://host/spoofed-2.1.3.tgz",
            // so the URL starts at the scheme rather than at index 0. Walk back from "://" over the
            // scheme characters RFC 3986 allows — ALPHA *( ALPHA / DIGIT / "+" / "-" / "." ) — to
            // find where it begins. See https://www.rfc-editor.org/rfc/rfc3986#section-3.1.
            var start = marker;
            while (start > 0 && (char.IsAsciiLetterOrDigit(value[start - 1]) || value[start - 1] is '+' or '-' or '.'))
            {
                start--;
            }

            var url = value[start..];
            var reference = $"{entryName} -> {url}";

            _remoteReferences.Add(reference);
            if (!IsApprovedFeedUrl(url))
            {
                _offenders.Add(reference);
            }
        }

        public LockfileScan Build() => new(_remoteReferences.Count, [.. _offenders]);
    }

    private enum YarnLockfileFormat
    {
        Berry,
        Classic,
    }
}
