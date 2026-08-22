// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aspire.Shared;
using Microsoft.Extensions.Logging;
using Semver;

namespace Aspire.Cli.Scaffolding;

/// <summary>
/// Merges scaffold-generated package.json with an existing one on disk.
/// Handles script name conflicts by adding Aspire-specific scripts under the <c>aspire:</c>
/// namespace prefix, and creates convenience aliases for non-conflicting names.
/// </summary>
internal static class PackageJsonMerger
{
    private const string ScriptsKey = "scripts";
    private const string DependenciesKey = "dependencies";
    private const string DevDependenciesKey = "devDependencies";
    private const string EnginesKey = "engines";
    private const string EnginesNodeKey = "node";
    private const string AspirePrefix = "aspire:";
    private const string TypeScriptPackage = "typescript";
    private const string TypeScriptEslintPackage = "typescript-eslint";
    private const string OverridesKey = "overrides";
    private const string SelfOverrideKey = ".";
    private const string AspireLintScriptName = "aspire:lint";

    /// <summary>
    /// The lowest TypeScript version typescript-eslint 8.58.0 refuses, from its peer range
    /// <c>typescript: "&gt;=4.8.4 &lt;6.1.0"</c>. Kept next to the scaffold floor in
    /// TypeScriptLanguageSupport, which pins <c>typescript-eslint: "8.58.0"</c>.
    /// </summary>
    private static readonly SemVersion s_firstUnsupportedTypeScript = SemVersion.Parse("6.1.0", SemVersionStyles.Strict);

    // package.json standard uses 2-space indentation. These options produce output
    // consistent with npm init / npm install formatting conventions.
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        IndentSize = 2
    };

    private static readonly JsonDocumentOptions s_jsonDocumentOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    /// <summary>
    /// Merges scaffold-generated package.json content with existing content.
    /// Preserves all existing properties and scripts. Scaffold scripts that conflict
    /// with existing names are added under the <c>aspire:</c> prefix. Existing scripts,
    /// including <c>aspire:</c>-prefixed scripts, are preserved. Non-conflicting
    /// <c>aspire:X</c> scripts get a convenience alias <c>X</c> pointing to
    /// <c>{toolchain} run aspire:X</c>.
    /// </summary>
    /// <returns>The merged package.json content as a JSON string.</returns>
    internal static string Merge(string existingContent, string scaffoldContent, ILogger logger, string toolchainCommand = "npm")
    {
        if (string.IsNullOrWhiteSpace(existingContent))
        {
            return scaffoldContent;
        }

        // Phase 1: Parse inputs. If either fails, return scaffold as-is.
        JsonObject? existingJson;
        JsonObject? scaffoldJson;
        try
        {
            existingJson = JsonNode.Parse(existingContent, documentOptions: s_jsonDocumentOptions) as JsonObject;
            scaffoldJson = JsonNode.Parse(scaffoldContent, documentOptions: s_jsonDocumentOptions) as JsonObject;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to parse package.json content, using scaffold output as-is.");
            return scaffoldContent;
        }

        if (existingJson is null || scaffoldJson is null)
        {
            return scaffoldContent;
        }

        // Phase 2: Merge. If merge fails, return scaffold as-is.
        try
        {
            MergeObjects(existingJson, scaffoldJson, logger, toolchainCommand);
            return existingJson.ToJsonString(s_jsonOptions);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to merge package.json content, using scaffold output as-is.");
            return scaffoldContent;
        }
    }

    /// <summary>
    /// Merges all top-level properties from scaffold into existing.
    /// Scripts get special conflict-aware handling, dependency sections use semver-aware merging,
    /// and everything else uses deep merge.
    /// </summary>
    private static void MergeObjects(JsonObject existing, JsonObject scaffold, ILogger logger, string toolchainCommand)
    {
        // Captured before merging: whether the lint toolchain is ours to withdraw. A project that
        // already depends on typescript-eslint owns that choice, and removing it would be a
        // destructive edit to a dependency `aspire init` did not introduce.
        var projectAlreadyLinted = FindDependencyVersion(existing, TypeScriptEslintPackage) is not null;
        var originalScriptNames = existing[ScriptsKey] is JsonObject originalScripts
            ? originalScripts.Select(script => script.Key).ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);

        // Handle scripts separately with conflict-aware logic
        var scaffoldScripts = scaffold[ScriptsKey]?.AsObject();
        if (scaffoldScripts is not null)
        {
            var existingScripts = EnsureObject(existing, ScriptsKey, logger);
            MergeScripts(existingScripts, scaffoldScripts, toolchainCommand);
        }

        // Handle dependency sections with semver-aware merging. A project that already declares
        // typescript-eslint keeps its own spec verbatim, but only while this merge also leaves its
        // compiler alone. The two constraints move together:
        //
        //   - Compiler untouched (the project is already past the scaffold's TypeScript, e.g. on
        //     TypeScript 7): narrowing a range such as `^8.57.1` to the scaffold's exact 8.58.0
        //     removes the project's ability to resolve a future 8.x that supports that compiler, and
        //     8.58.0 peers `typescript: >=4.8.4 <6.1.0`, so the rewrite turns an install that
        //     resolved into the ERESOLVE this merger exists to avoid.
        //   - Compiler upgraded (e.g. 5.9.3 to the scaffold's 6.0.3): the project's linter spec was
        //     chosen against the old compiler and may not admit the new one - 8.57.1 peers
        //     `typescript: <6.0.0` - so leaving it alone would be the thing that breaks the install.
        //     Whoever moves the compiler owns the linter that has to match it.
        var scaffoldTypeScript = FindDependencyVersion(scaffold, TypeScriptPackage);
        var projectTypeScript = FindDependencyVersion(existing, TypeScriptPackage);
        var compilerIsUnchanged = projectTypeScript is not null
            && (scaffoldTypeScript is null || !NpmVersionHelper.ShouldUpgrade(projectTypeScript, scaffoldTypeScript));
        var projectOwnedPackage = projectAlreadyLinted && compilerIsUnchanged ? TypeScriptEslintPackage : null;
        var rewrittenSpecs = new Dictionary<string, string>(StringComparer.Ordinal);
        MergeDependencySection(existing, scaffold, DependenciesKey, logger, projectOwnedPackage, rewrittenSpecs);
        MergeDependencySection(existing, scaffold, DevDependenciesKey, logger, projectOwnedPackage, rewrittenSpecs);
        if (projectAlreadyLinted && !compilerIsUnchanged)
        {
            ReconcileProjectLinterWithUpgradedCompiler(existing, scaffold, logger, rewrittenSpecs);
        }

        ReconcileNpmOverrides(existing, rewrittenSpecs, logger);

        // Handle engines with overwrite semantics for "node" — since the user is running
        // "aspire init", we enforce our Node version constraint (required for ESLint 10
        // and TypeScript tooling compatibility). Other engines sub-keys are preserved.
        MergeEngines(existing, scaffold, logger);

        // Deep merge everything else (scalars, nested objects).
        // Array properties (e.g., "keywords") are preserved from existing — the scaffold
        // echoes the original arrays unchanged, so the existing value is always correct.
        foreach (var (key, sourceValue) in scaffold)
        {
            if (key is ScriptsKey or DependenciesKey or DevDependenciesKey or EnginesKey || sourceValue is null)
            {
                continue;
            }

            var targetValue = existing[key];

            if (targetValue is null)
            {
                // Property only in scaffold — add it (including arrays from scaffold-only)
                existing[key] = sourceValue.DeepClone();
            }
            else if (targetValue is JsonObject targetObj && sourceValue is JsonObject sourceObj)
            {
                DeepMerge(targetObj, sourceObj);
            }
            // Arrays and scalar values in existing are preserved
        }

        if (!projectAlreadyLinted)
        {
            RemoveLintToolchainWhenTypeScriptIsTooNew(existing, logger, originalScriptNames);
        }
    }

    /// <summary>
    /// Withdraws the scaffolded typescript-eslint lint toolchain when the project's TypeScript
    /// cannot be proven compatible with the version typescript-eslint supports, so the merged
    /// manifest still installs.
    /// </summary>
    /// <remarks>
    /// Dependency merging keeps whichever version is newer, so a project already on TypeScript 7
    /// keeps it while the scaffold contributes typescript-eslint. typescript-eslint 8.58.0 peers
    /// `typescript: ">=4.8.4 &lt;6.1.0"`, so the combination is unsatisfiable and `npm install` fails
    /// with ERESOLVE — the exact failure `aspire init` is supposed to avoid.
    ///
    /// Downgrading the project's compiler is not an option: TypeScript 7 is the native compiler and
    /// the user chose it. Npm dependency specs can also be tags, aliases, workspace references, or
    /// open-ended ranges; unless the merger can prove the spec is inside the peer range, it must
    /// fail closed and avoid adding a dependency pair npm may reject. The AppHost only needs `tsc`
    /// to build, so the lint rules are what gets dropped. eslint.config.mjs is still written, and
    /// starts working once typescript-eslint supports TypeScript 7 and the dependency is added back.
    /// </remarks>
    private static void RemoveLintToolchainWhenTypeScriptIsTooNew(JsonObject existing, ILogger logger, HashSet<string> originalScriptNames)
    {
        var typeScriptVersion = FindDependencyVersion(existing, TypeScriptPackage);
        if (typeScriptVersion is null)
        {
            return;
        }

        if (IsTypeScriptVersionKnownSupported(typeScriptVersion))
        {
            return;
        }

        if (RemoveDependency(existing, TypeScriptEslintPackage))
        {
            logger.LogWarning(
                "Skipped adding {Package} because this project uses TypeScript version spec '{Version}', which is outside or cannot be verified against the range {Package} supports. The AppHost lint script was not added.",
                TypeScriptEslintPackage,
                typeScriptVersion,
                TypeScriptEslintPackage);
        }

        // The scaffolded eslint.config.mjs enables @typescript-eslint/no-floating-promises, so
        // without the dependency the script would fail on every run. Remove only scripts this merge
        // introduced; a brownfield project's own aspire:lint script may not use typescript-eslint,
        // and the merge contract is to preserve existing scripts even when their values mention the
        // scaffold lint script name.
        if (existing[ScriptsKey] is not JsonObject scripts)
        {
            return;
        }

        foreach (var scriptName in scripts
            .Where(script => !originalScriptNames.Contains(script.Key) &&
                (script.Key == AspireLintScriptName ||
                GetStringValue(script.Value)?.Contains(AspireLintScriptName, StringComparison.Ordinal) == true))
            .Select(script => script.Key)
            .ToArray())
        {
            scripts.Remove(scriptName);
        }
    }

    /// <summary>
    /// True only when every version the spec can resolve to is one typescript-eslint supports.
    /// </summary>
    /// <remarks>
    /// The spec's lower bound is not enough: `^6.0.3` reads as satisfied by 6.0.3, but npm resolves
    /// a caret range to the newest match, so it installs 6.1.0 or later the moment one is published
    /// and typescript-eslint's `&lt;6.1.0` peer turns into ERESOLVE. What matters is the first
    /// version the range excludes.
    ///
    /// Only the forms whose upper bound follows from the spec alone are considered - an exact
    /// version, a caret range and a tilde range, per
    /// <see href="https://github.com/npm/node-semver#ranges"/>. Comparators, unions, hyphen ranges,
    /// x-ranges, dist-tags and aliases have no bound this can prove, and prereleases resolve by
    /// their own rules, so all of them fail closed.
    ///
    /// A literal that omits components is an x-range in disguise and widens the bound: `6` is
    /// `6.x.x` and `~6` is <c>&gt;=6.0.0 &lt;7.0.0</c>, both of which reach 6.1.0, where `6.0` and
    /// `~6.0` stop below it. <see cref="SemVersionStyles.Any"/> fills the missing components with
    /// zero and so cannot tell those apart, which is why the bound is computed from how many
    /// components the literal actually spells out.
    /// </remarks>
    private static bool IsTypeScriptVersionKnownSupported(string typeScriptVersion)
    {
        var trimmed = typeScriptVersion.Trim();

        var (rangeOperator, literal) = trimmed switch
        {
            ['^', .. var rest] => ('^', rest.TrimStart()),
            ['~', .. var rest] => ('~', rest.TrimStart()),
            ['=', .. var rest] => ('\0', rest.TrimStart()),
            _ => ('\0', trimmed),
        };

        if (!SemVersion.TryParse(literal, SemVersionStyles.Any, out var lowest) ||
            lowest is null ||
            lowest.IsPrerelease)
        {
            return false;
        }

        // The first version the range can no longer resolve to, following npm's x-range, tilde and
        // caret rules. An omitted component is a wildcard, so the bound is set by the last component
        // the literal names rather than by the zero the parser substituted for it.
        // https://github.com/npm/node-semver#x-ranges-12x-1x-12-
        var firstExcluded = (rangeOperator, ComponentCount(literal)) switch
        {
            // A caret with no minor to pin - `^6`, and `^0` alike - is just `6.x`.
            ('^', 1) => new SemVersion(lowest.Major + 1, 0, 0),
            ('^', _) when lowest.Major > 0 => new SemVersion(lowest.Major + 1, 0, 0),
            // Below 1.0.0 a caret pins the leftmost non-zero component instead: `^0.2.3` stops below
            // 0.3.0, and `^0.0.3` below 0.0.4.
            ('^', 3) when lowest.Minor == 0 => new SemVersion(0, 0, lowest.Patch + 1),
            ('^', _) => new SemVersion(0, lowest.Minor + 1, 0),
            // `~6` has no minor to pin either, so it stops below the next major; `~6.0` and `~6.0.3`
            // both stop below the next minor.
            ('~', 1) => new SemVersion(lowest.Major + 1, 0, 0),
            ('~', _) => new SemVersion(lowest.Major, lowest.Minor + 1, 0),
            // A bare or `=` literal is an x-range over whatever it omits, and resolves only to
            // itself once all three components are present.
            (_, 1) => new SemVersion(lowest.Major + 1, 0, 0),
            (_, 2) => new SemVersion(lowest.Major, lowest.Minor + 1, 0),
            _ => new SemVersion(lowest.Major, lowest.Minor, lowest.Patch + 1),
        };

        return SemVersion.ComparePrecedence(firstExcluded, s_firstUnsupportedTypeScript) <= 0;
    }

    /// <summary>
    /// How many numeric components a version literal spells out, so a partial literal can be given
    /// the npm range bound it really has instead of the zero-filled one the parser produced.
    /// </summary>
    /// <remarks>
    /// Only the core is counted. Prerelease and build metadata are separated by <c>-</c> and
    /// <c>+</c> and can contain dots of their own, and neither widens the range.
    /// </remarks>
    private static int ComponentCount(string literal)
    {
        var core = literal.AsSpan();
        var metadata = core.IndexOfAny('-', '+');
        if (metadata >= 0)
        {
            core = core[..metadata];
        }

        return core.Count('.') + 1;
    }

    private static string? FindDependencyVersion(JsonObject packageJson, string packageName)
    {
        // A malformed package.json can have a non-object here — `"dependencies": ["express"]` is
        // real enough that MergeObjects repairs it. Indexing a JsonArray by name throws, and this
        // runs before that repair, so match the section shape instead of assuming it.
        return GetStringValue((packageJson[DependenciesKey] as JsonObject)?[packageName]) ??
            GetStringValue((packageJson[DevDependenciesKey] as JsonObject)?[packageName]);
    }

    private static bool RemoveDependency(JsonObject packageJson, string packageName)
    {
        var removed = false;

        foreach (var sectionName in new[] { DependenciesKey, DevDependenciesKey })
        {
            if (packageJson[sectionName] is JsonObject section)
            {
                removed |= section.Remove(packageName);
            }
        }

        return removed;
    }

    private static string? GetStringValue(JsonNode? node)
    {
        return node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
    }

    /// <summary>
    /// Merges scaffold scripts into existing scripts with conflict-aware handling.
    /// </summary>
    /// <remarks>
    /// For each scaffold script:
    /// <list type="bullet">
    /// <item>Already <c>aspire:</c> prefixed → added only when missing</item>
    /// <item>Not prefixed, conflicts with existing → added as <c>aspire:{name}</c></item>
    /// <item>Not prefixed, no conflict → added with the original name</item>
    /// </list>
    /// After processing, for each <c>aspire:X</c> script where no non-prefixed <c>X</c> exists,
    /// a convenience alias is added: <c>"X": "{toolchain} run aspire:X"</c>.
    /// </remarks>
    internal static void MergeScripts(JsonObject existingScripts, JsonObject scaffoldScripts, string toolchainCommand = "npm")
    {
        foreach (var (name, value) in scaffoldScripts)
        {
            if (value is not JsonValue scriptValue || !scriptValue.TryGetValue<string>(out var command))
            {
                continue;
            }

            if (name.StartsWith(AspirePrefix, StringComparison.Ordinal))
            {
                existingScripts[name] ??= command;
            }
            else if (existingScripts[name] is not null)
            {
                // Conflict — add under aspire: prefix
                existingScripts[$"{AspirePrefix}{name}"] = command;
            }
            else
            {
                // No conflict — add with original name
                existingScripts[name] = command;
            }
        }

        // Add convenience aliases for aspire: scripts that have no non-prefixed equivalent
        AddConvenienceAliases(existingScripts, toolchainCommand);
    }

    /// <summary>
    /// For each <c>aspire:X</c> script, if no script named <c>X</c> exists,
    /// adds <c>"X": "{toolchain} run aspire:X"</c> as a convenience alias.
    /// </summary>
    private static void AddConvenienceAliases(JsonObject scripts, string toolchainCommand)
    {
        var normalizedToolchainCommand = string.IsNullOrWhiteSpace(toolchainCommand) ? "npm" : toolchainCommand;

        // Collect aspire: keys first to avoid modifying during enumeration
        var aspireScripts = new List<(string unprefixed, string prefixed)>();
        foreach (var (name, _) in scripts)
        {
            if (name.StartsWith(AspirePrefix, StringComparison.Ordinal))
            {
                var unprefixed = name[AspirePrefix.Length..];
                if (unprefixed.Length > 0)
                {
                    aspireScripts.Add((unprefixed, name));
                }
            }
        }

        foreach (var (unprefixed, prefixed) in aspireScripts)
        {
            if (scripts[unprefixed] is null)
            {
                scripts[unprefixed] = $"{normalizedToolchainCommand} run {prefixed}";
            }
        }
    }

    /// <summary>
    /// Re-points npm <c>overrides</c> entries at the specs this merge just rewrote.
    /// </summary>
    /// <remarks>
    /// npm rejects an override for a package the manifest depends on directly unless the two specs
    /// are identical, and it does so before any peer resolution: a manifest with
    /// <c>devDependencies.typescript: "^5.9.3"</c> and <c>overrides.typescript: "^5.9.3"</c> is
    /// valid until the merge moves the direct spec to the scaffold's 6.0.3, at which point
    /// <c>npm install</c> fails with EOVERRIDE. Only npm enforces this - Yarn <c>resolutions</c> and
    /// pnpm overrides deliberately allow a divergent spec - so only npm's section is reconciled.
    /// See <see href="https://docs.npmjs.com/cli/v11/configuring-npm/package-json#overrides"/>.
    /// </remarks>
    private static void ReconcileNpmOverrides(JsonObject existing, Dictionary<string, string> rewrittenSpecs, ILogger logger)
    {
        if (rewrittenSpecs.Count == 0 || existing[OverridesKey] is not JsonObject overrides)
        {
            return;
        }

        foreach (var (packageName, rewrittenSpec) in rewrittenSpecs)
        {
            // A string entry is the spec for the package itself. An object entry is a nested
            // override tree, but its "." key - when present - is also a spec for the package
            // itself, and npm compares it against the direct dependency exactly the same way:
            //
            //   "devDependencies": { "typescript": "6.0.3" },
            //   "overrides": { "typescript": { ".": "^5.9.3", "some-dep": "1.0.0" } }
            //
            // still fails with `npm error code EOVERRIDE / Override for typescript@6.0.3 conflicts
            // with direct dependency`. An object entry without a "." key only scopes that package's
            // own dependencies and is left alone.
            var entry = overrides[packageName];
            var nestedOverrides = entry as JsonObject;
            if ((GetStringValue(entry) ?? GetStringValue(nestedOverrides?[SelfOverrideKey])) is not { } overriddenSpec ||
                string.Equals(overriddenSpec, rewrittenSpec, StringComparison.Ordinal))
            {
                continue;
            }

            // Rewrite in place so a nested tree sitting beside the "." key survives.
            if (nestedOverrides is null)
            {
                overrides[packageName] = rewrittenSpec;
            }
            else
            {
                nestedOverrides[SelfOverrideKey] = rewrittenSpec;
            }

            logger.LogWarning(
                "Updated the '{Package}' entry in overrides from '{OverriddenVersion}' to '{RewrittenVersion}' to match the upgraded direct dependency, because npm rejects an override that does not match a direct dependency.",
                packageName,
                overriddenSpec,
                rewrittenSpec);
        }
    }

    /// <summary>
    /// Replaces a project linter range only when it cannot resolve to the scaffold floor or newer.
    /// </summary>
    /// <remarks>
    /// <see cref="NpmVersionHelper.ShouldUpgrade"/> compares lower bounds and fails closed on any
    /// spec it cannot reduce to a single version, which is the right default everywhere else: an
    /// unparseable spec is usually a deliberate choice worth preserving. Here it is the wrong one.
    /// A project on TypeScript 5.9.3 with <c>typescript-eslint: "&gt;=8.57.1 &lt;8.58.0"</c> loses
    /// the upgrade - the range is a comparator pair, so no lower bound comes out of it - and keeps a
    /// linter that resolves to 8.57.x, whose <c>typescript: &lt;6.0.0</c> peer the freshly upgraded
    /// 6.0.3 compiler no longer satisfies. Conversely, <c>&gt;=8.60.0 &lt;8.66.0</c> is already
    /// entirely above the scaffold's 8.58.0 floor and must not be downgraded to it.
    ///
    /// Only stable comparator sets and wildcard ranges have their normalized bounds inspected.
    /// Opaque references, malformed ranges, and unsupported range forms remain project-owned:
    /// replacing one without proving it cannot reach the floor would be destructive.
    ///
    /// The removal pass is no help either: it only runs for a project that had no linter, so a
    /// brownfield project that did have one has nothing left to catch this.
    /// </remarks>
    private static void ReconcileProjectLinterWithUpgradedCompiler(JsonObject existing, JsonObject scaffold, ILogger logger, Dictionary<string, string> rewrittenSpecs)
    {
        var scaffoldLinter = FindDependencyVersion(scaffold, TypeScriptEslintPackage);
        var projectLinter = FindDependencyVersion(existing, TypeScriptEslintPackage);
        if (scaffoldLinter is null || projectLinter is null || projectLinter == scaffoldLinter)
        {
            return;
        }

        if (!NpmVersionHelper.TryParseNpmVersion(scaffoldLinter, out var scaffoldFloor) ||
            CanRangeResolveAtOrAboveFloor(projectLinter, scaffoldFloor) is not false)
        {
            return;
        }

        var section = (existing[DependenciesKey] as JsonObject)?[TypeScriptEslintPackage] is not null
            ? DependenciesKey
            : DevDependenciesKey;

        ((JsonObject)existing[section]!)[TypeScriptEslintPackage] = scaffoldLinter;
        // Assigned rather than TryAdd: this runs after the floor merge and is the last word on the
        // spec, so an override has to follow this value and not an earlier one.
        rewrittenSpecs[TypeScriptEslintPackage] = scaffoldLinter;

        logger.LogWarning(
            "Replaced the '{Package}' semver range '{ExistingVersion}' with '{DesiredVersion}' because this merge upgraded TypeScript and the existing range cannot resolve to the required package floor.",
            TypeScriptEslintPackage,
            projectLinter,
            scaffoldLinter);
    }

    /// <summary>
    /// Determines whether a focused npm range can resolve to the floor or a newer version.
    /// Returns <see langword="null"/> when the range form is unsupported or malformed.
    /// </summary>
    /// <remarks>
    /// The focused forms are stable comparator sets such as <c>&gt;=8.60.0 &lt;8.66.0</c> and
    /// simple x-ranges such as <c>8.57.x</c>. Other npm range forms fail safe because replacing a
    /// project-owned spec requires proof that it cannot reach the floor.
    /// See <see href="https://github.com/npm/node-semver#ranges"/>.
    /// </remarks>
    private static bool? CanRangeResolveAtOrAboveFloor(string versionRange, SemVersion floor)
    {
        var trimmed = versionRange.Trim();
        if (!IsStableComparatorSet(trimmed) &&
            !IsWildcardRange(trimmed))
        {
            return null;
        }

        if (!SemVersionRange.TryParseNpm(trimmed, out var parsedRange) ||
            parsedRange is null)
        {
            return null;
        }

        // SemVersionRange normalizes each comparator set into a non-empty interval. Such an
        // interval intersects [floor, infinity) exactly when it is unbounded above, ends above the
        // floor, or includes the floor as its upper endpoint.
        foreach (var range in parsedRange)
        {
            if (range.End is null)
            {
                return true;
            }

            var endComparedToFloor = SemVersion.ComparePrecedence(range.End, floor);
            if (endComparedToFloor > 0 ||
                (endComparedToFloor == 0 && range.EndInclusive))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Recognizes whitespace-separated comparator segments with stable, complete versions.
    /// </summary>
    private static bool IsStableComparatorSet(string versionRange)
    {
        var segments = versionRange.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            return false;
        }

        foreach (var segment in segments)
        {
            var literal = segment.AsSpan();
            if (literal.StartsWith(">=", StringComparison.Ordinal) ||
                literal.StartsWith("<=", StringComparison.Ordinal))
            {
                literal = literal[2..];
            }
            else if (literal is ['>' or '<' or '=', .. var remainder])
            {
                literal = remainder;
            }

            if (literal.IsEmpty ||
                !SemVersion.TryParse(literal.ToString(), SemVersionStyles.Strict, out var version) ||
                version is null ||
                version.IsPrerelease)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Recognizes npm x-ranges such as <c>8.x</c> and <c>8.57.*</c>.
    /// </summary>
    private static bool IsWildcardRange(string versionRange)
    {
        var components = versionRange.Split('.');
        if (components.Length is < 1 or > 3)
        {
            return false;
        }

        var foundWildcard = false;
        foreach (var component in components)
        {
            if (component is "x" or "X" or "*")
            {
                foundWildcard = true;
            }
            else if (foundWildcard ||
                component.Length == 0 ||
                !component.All(char.IsAsciiDigit))
            {
                return false;
            }
        }

        return foundWildcard;
    }

    /// <summary>
    /// Merges a dependency section (e.g., "dependencies", "devDependencies") from scaffold into existing
    /// using semver-aware comparison. New packages are added; existing packages are upgraded only when
    /// the scaffold specifies a newer version. Unparseable version ranges (union ranges, workspace
    /// references, etc.) are preserved as-is.
    /// </summary>
    /// <remarks>
    /// Every direct spec this writes is recorded in <paramref name="rewrittenSpecs"/>, additions
    /// included: npm compares an <c>overrides</c> entry against whatever direct spec the manifest
    /// ends up with, and does not care whether that spec was upgraded or introduced here.
    /// </remarks>
    private static void MergeDependencySection(JsonObject existing, JsonObject scaffold, string sectionName, ILogger logger, string? projectOwnedPackage = null, Dictionary<string, string>? rewrittenSpecs = null)
    {
        var scaffoldDeps = scaffold[sectionName]?.AsObject();
        if (scaffoldDeps is null)
        {
            return;
        }

        var existingDeps = EnsureObject(existing, sectionName, logger);

        foreach (var (packageName, versionNode) in scaffoldDeps)
        {
            if (versionNode is not JsonValue desiredValue || !desiredValue.TryGetValue<string>(out var desiredVersion))
            {
                continue;
            }

            // Whatever spec the project already chose for this package stays, in whichever section
            // it chose. Skipping the whole entry - not just the upgrade - also stops
            // TryMergeExistingDependency from moving a runtime dependency the project owns.
            if (packageName == projectOwnedPackage)
            {
                continue;
            }

            var existingVersionNode = existingDeps[packageName];
            if (existingVersionNode is null)
            {
                // Preserve brownfield package shape: if a scaffolded devDependency already exists
                // as a runtime dependency, upgrade it in place instead of duplicating it.
                if (sectionName == DevDependenciesKey &&
                    TryMergeExistingDependency(existing, DependenciesKey, packageName, desiredVersion, rewrittenSpecs))
                {
                    continue;
                }

                existingDeps[packageName] = desiredVersion;
                rewrittenSpecs?.TryAdd(packageName, desiredVersion);
            }
            else
            {
                if (existingVersionNode is JsonValue existingValue
                    && existingValue.TryGetValue<string>(out var existingVersion)
                    && NpmVersionHelper.ShouldUpgrade(existingVersion, desiredVersion))
                {
                    existingDeps[packageName] = desiredVersion;
                    rewrittenSpecs?.TryAdd(packageName, desiredVersion);
                }
            }
        }
    }

    private static bool TryMergeExistingDependency(JsonObject existing, string sectionName, string packageName, string desiredVersion, Dictionary<string, string>? rewrittenSpecs = null)
    {
        if (existing[sectionName] is not JsonObject existingDeps)
        {
            return false;
        }

        var existingVersionNode = existingDeps[packageName];
        if (existingVersionNode is null)
        {
            return false;
        }

        if (existingVersionNode is JsonValue existingValue
            && existingValue.TryGetValue<string>(out var existingVersion)
            && NpmVersionHelper.ShouldUpgrade(existingVersion, desiredVersion))
        {
            existingDeps[packageName] = desiredVersion;
            rewrittenSpecs?.TryAdd(packageName, desiredVersion);
        }

        return true;
    }

    /// <summary>
    /// Merges the <c>engines</c> section from scaffold into existing. The <c>engines.node</c>
    /// constraint is always overwritten by the scaffold's value because <c>aspire init</c> requires
    /// specific Node.js versions for ESLint 10 and TypeScript tooling compatibility. Other
    /// <c>engines</c> sub-keys (e.g., <c>npm</c>) are preserved from the existing package.json.
    /// </summary>
    private static void MergeEngines(JsonObject existing, JsonObject scaffold, ILogger logger)
    {
        var scaffoldEngines = scaffold[EnginesKey]?.AsObject();
        if (scaffoldEngines is null)
        {
            return;
        }

        var existingEngines = EnsureObject(existing, EnginesKey, logger);

        foreach (var (key, value) in scaffoldEngines)
        {
            if (value is null)
            {
                continue;
            }

            if (key == EnginesNodeKey)
            {
                // Always overwrite engines.node — Aspire requires specific Node versions
                existingEngines[key] = value.DeepClone();
            }
            else if (existingEngines[key] is null)
            {
                existingEngines[key] = value.DeepClone();
            }
            // Other existing engine constraints are preserved
        }
    }

    /// <summary>
    /// Deep merges properties from source into target. Existing target values are preserved.
    /// For nested objects, recursively merges. Scalar values in target are never overwritten.
    /// </summary>
    internal static void DeepMerge(JsonObject target, JsonObject source)
    {
        foreach (var (key, sourceValue) in source)
        {
            if (sourceValue is null)
            {
                continue;
            }

            var targetValue = target[key];

            if (targetValue is null)
            {
                target[key] = sourceValue.DeepClone();
            }
            else if (targetValue is JsonObject targetObj && sourceValue is JsonObject sourceObj)
            {
                DeepMerge(targetObj, sourceObj);
            }
            // Scalar values in target are preserved
        }
    }

    private static JsonObject EnsureObject(JsonObject parent, string propertyName, ILogger logger)
    {
        if (parent[propertyName] is JsonObject obj)
        {
            return obj;
        }

        if (parent[propertyName] is not null)
        {
            logger.LogWarning(
                "Replacing non-object '{PropertyName}' value with an empty object. The original value will be lost.",
                propertyName);
        }

        obj = new JsonObject();
        parent[propertyName] = obj;
        return obj;
    }
}
