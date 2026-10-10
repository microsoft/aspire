// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Immutable;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Aspire.Hosting.Java;

/// <summary>
/// Determines which Java release a project targets, so the generated container image builds and runs it
/// on a matching JDK and JRE.
/// </summary>
/// <remarks>
/// Detection is best effort and never fails: an unreadable or unrecognised build file falls back to
/// <see cref="DefaultJavaVersion"/>. Callers can always override the images with
/// <c>WithDockerfileBaseImage</c>.
/// </remarks>
internal static partial class JavaVersionDetector
{
    /// <summary>
    /// The Java release used when a project's build files declare none. Chosen as a broadly supported
    /// long-term support release, which is what current Spring Boot and Quarkus releases target by
    /// default, rather than the newest one.
    /// </summary>
    internal const string DefaultJavaVersion = "21";

    /// <param name="appDirectory">The application directory to read build files from.</param>
    /// <param name="tool">
    /// The build tool that actually builds the application, when it is known. A directory can hold both a
    /// POM and a Gradle script — a repository part-way through a migration, for example — and the two can
    /// name different releases. Reading the tool that does not run would tag the image for a release the
    /// application was never compiled for, which surfaces at runtime as
    /// <c>UnsupportedClassVersionError</c> rather than as a build failure. This is an ordering rather than
    /// a filter: Gradle projects often leave the release to a toolchain block that is not read here, and a
    /// sibling POM is still better evidence than the default.
    /// </param>
    /// <param name="buildArgs">The resolved arguments passed to the selected build tool, when known.</param>
    public static string Detect(string appDirectory, JavaBuildTool? tool = null, string[]? buildArgs = null)
    {
        string?[] candidates = tool is JavaBuildTool.Gradle
            ?
            [
                DetectFromGradle(Path.Combine(appDirectory, "build.gradle")),
                DetectFromGradle(Path.Combine(appDirectory, "build.gradle.kts")),
                DetectFromPom(Path.Combine(appDirectory, "pom.xml"), buildArgs: null),
            ]
            :
            [
                DetectFromPom(
                    Path.Combine(appDirectory, "pom.xml"),
                    tool is JavaBuildTool.Maven ? buildArgs : null),
                DetectFromGradle(Path.Combine(appDirectory, "build.gradle")),
                DetectFromGradle(Path.Combine(appDirectory, "build.gradle.kts")),
            ];

        return Array.Find(candidates, candidate => candidate is not null) ?? DefaultJavaVersion;
    }

    /// <summary>
    /// Reads the target release from a Maven POM.
    /// </summary>
    /// <remarks>
    /// Three spellings are common and all appear in Spring Initializr output:
    /// <code language="xml">
    /// &lt;properties&gt;
    ///   &lt;java.version&gt;21&lt;/java.version&gt;
    ///   &lt;maven.compiler.release&gt;21&lt;/maven.compiler.release&gt;
    /// &lt;/properties&gt;
    /// &lt;!-- or, on the compiler plugin itself --&gt;
    /// &lt;configuration&gt;&lt;release&gt;21&lt;/release&gt;&lt;/configuration&gt;
    /// </code>
    /// Legacy POMs write <c>1.8</c> rather than <c>8</c>; both map to the <c>8</c> image tag.
    /// Property references such as <c>&lt;release&gt;${java.version}&lt;/release&gt;</c> are not expanded —
    /// the literal properties are checked first, so the reference is only reached when nothing else matched.
    /// </remarks>
    private static string? DetectFromPom(string pomPath, string[]? buildArgs)
    {
        if (!File.Exists(pomPath))
        {
            return null;
        }

        XDocument document;
        try
        {
            document = XDocument.Load(pomPath);
        }
        catch (Exception ex) when (ex is System.Xml.XmlException or IOException or UnauthorizedAccessException)
        {
            // A malformed or unreadable POM is the build tool's problem to report, not a reason to fail
            // publishing before the container build has even started.
            return null;
        }

        // Element names are matched without their namespace so both the Maven 4 POM namespace and the
        // long-standing http://maven.apache.org/POM/4.0.0 namespace are handled.
        //
        // Every project element with a matching name is considered, not just the first: a POM
        // often declares <release>${java.version}</release> on the compiler plugin and a literal elsewhere,
        // and stopping at the unresolvable property reference would fall back to the default version instead.
        // Within each declaration kind, explicitly selected profile values are checked first. If none of
        // those profiles exist in this POM, active-by-default profile values are checked before the project's
        // base values because Maven applies them as overrides in its effective model. Other profile activation
        // rules are not evaluated because they can depend on the JDK, operating system, system properties,
        // files, or settings.
        // Ordered by how directly each one decides the bytecode version, most direct first, because the
        // runtime image has to be at least what the compiler actually emitted.
        //
        // java.version is last despite being the most recognisable. It is not a Maven property at all:
        // it works only because spring-boot-starter-parent maps it onto the real one with
        // <maven.compiler.release>${java.version}</maven.compiler.release>. A POM that sets both
        // java.version and maven.compiler.release overrides that mapping, so Maven compiles to the
        // latter and reading java.version would pick a runtime too old to load the classes.
        // https://docs.spring.io/spring-boot/maven-plugin/using.html
        var profiles = GetProfiles(document).ToArray();
        var profileSelection = ParseMavenProfileSelection(buildArgs);
        var explicitlyActivatedProfiles = profiles
            .Where(profile => GetProfileId(profile) is { } id
                && profileSelection.Activated.Contains(id)
                && !profileSelection.Deactivated.Contains(id))
            .ToArray();
        var activeProfiles = explicitlyActivatedProfiles.Length > 0
            ? explicitlyActivatedProfiles
            : profiles
                .Where(profile => IsActiveByDefaultProfile(profile)
                    && (GetProfileId(profile) is not { } id || !profileSelection.Deactivated.Contains(id)))
                .ToArray();

        foreach (var (name, mustBePluginConfiguration) in ((string, bool)[])
        [
            // Explicit plugin configuration beats the property that merely supplies the parameter's
            // default, and release beats target within the plugin.
            // https://maven.apache.org/plugins/maven-compiler-plugin/compile-mojo.html
            //
            // <release> and <target> are only meaningful inside the compiler plugin's <configuration>.
            // Matched merely by having a <configuration> parent they would also pick up unrelated
            // plugins: maven-antrun-plugin's canonical configuration is literally
            // <configuration><target>...</target></configuration>, holding Ant XML rather than a Java
            // release, and any plugin is free to name a <release> of its own.
            ("release", true),
            ("maven.compiler.release", false),
            ("target", true),
            ("maven.compiler.target", false),
            ("java.version", false),
        ])
        {
            IEnumerable<XElement>[] candidateGroups =
            [
                // Maven merges active profiles in declaration order, with later profile values taking precedence.
                activeProfiles.SelectMany(profile => profile.Descendants()).Reverse(),
                document.Descendants().Where(element => !IsInProfile(element)),
            ];

            foreach (var candidateElements in candidateGroups)
            {
                foreach (var element in candidateElements.Where(e => string.Equals(e.Name.LocalName, name, StringComparison.Ordinal)))
                {
                    if (mustBePluginConfiguration && !IsCompilerPluginConfiguration(element.Parent))
                    {
                        continue;
                    }

                    if (Normalize(element.Value) is { } version)
                    {
                        return version;
                    }
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Reads the profiles declared directly by this Maven project.
    /// </summary>
    private static IEnumerable<XElement> GetProfiles(XDocument document) =>
        document.Root?
            .Elements()
            .Where(element => string.Equals(element.Name.LocalName, "profiles", StringComparison.Ordinal))
            .SelectMany(element => element.Elements().Where(IsProfile))
        ?? [];

    /// <summary>
    /// Reads a Maven profile's direct <c>&lt;id&gt;</c> child.
    /// </summary>
    private static string? GetProfileId(XElement profile) =>
        profile.Elements()
            .FirstOrDefault(element => string.Equals(element.Name.LocalName, "id", StringComparison.Ordinal))
            ?.Value.Trim() is { Length: > 0 } id
                ? id
                : null;

    /// <summary>
    /// Reads Maven's explicit profile activation and deactivation arguments.
    /// </summary>
    private static MavenProfileSelection ParseMavenProfileSelection(string[]? buildArgs)
    {
        var activated = new HashSet<string>(StringComparer.Ordinal);
        var deactivated = new HashSet<string>(StringComparer.Ordinal);

        if (buildArgs is null)
        {
            return new MavenProfileSelection(activated, deactivated);
        }

        for (var index = 0; index < buildArgs.Length; index++)
        {
            var argument = buildArgs[index];
            string? expression = argument switch
            {
                "-P" or "--activate-profiles" when index + 1 < buildArgs.Length => buildArgs[++index],
                _ when argument.StartsWith("-P=", StringComparison.Ordinal) => argument[3..],
                _ when argument.StartsWith("-P", StringComparison.Ordinal) => argument[2..],
                _ when argument.StartsWith("--activate-profiles=", StringComparison.Ordinal) =>
                    argument["--activate-profiles=".Length..],
                _ => null,
            };

            if (expression is null)
            {
                continue;
            }

            // Maven accepts `-Pprod`, `-P prod`, `-P=prod`, `--activate-profiles prod`, and
            // `--activate-profiles=prod`. Each value can be comma-separated, `!`/`-` deactivates a
            // profile, and Maven 4's `?` prefix makes an activation optional. Parse only these argv shapes
            // so malformed or unrelated switches remain Maven's responsibility.
            // https://maven.apache.org/guides/introduction/introduction-to-profiles.html
            foreach (var item in expression.Split(','))
            {
                var profileExpression = item.Trim();
                if (profileExpression.Length == 0)
                {
                    continue;
                }

                var isDeactivation = profileExpression[0] is '!' or '-';
                if (isDeactivation)
                {
                    profileExpression = profileExpression[1..];
                }

                if (profileExpression.Length > 0 && profileExpression[0] == '?')
                {
                    profileExpression = profileExpression[1..];
                }

                if (profileExpression.Length == 0)
                {
                    continue;
                }

                (isDeactivation ? deactivated : activated).Add(profileExpression);
            }
        }

        return new MavenProfileSelection(activated, deactivated);
    }

    /// <summary>
    /// Determines whether a Maven profile is active by default.
    /// </summary>
    /// <remarks>
    /// Maven expresses this activation as:
    /// <code>
    /// &lt;profile&gt;
    ///   &lt;activation&gt;&lt;activeByDefault&gt;true&lt;/activeByDefault&gt;&lt;/activation&gt;
    /// &lt;/profile&gt;
    /// </code>
    /// Other activation forms are not evaluated because they can depend on the JDK, operating system,
    /// system properties, files, or command-line profile selection.
    /// </remarks>
    private static bool IsActiveByDefaultProfile(XElement profile)
    {
        var activation = profile.Elements().FirstOrDefault(e =>
            string.Equals(e.Name.LocalName, "activation", StringComparison.Ordinal));

        return activation?.Elements().Any(e =>
            string.Equals(e.Name.LocalName, "activeByDefault", StringComparison.Ordinal)
            && bool.TryParse(e.Value.Trim(), out var activeByDefault)
            && activeByDefault) is true;
    }

    private static bool IsInProfile(XElement element) => element.Ancestors().Any(IsProfile);

    private static bool IsProfile(XElement element) =>
        string.Equals(element.Name.LocalName, "profile", StringComparison.Ordinal);

    private readonly record struct MavenProfileSelection(
        HashSet<string> Activated,
        HashSet<string> Deactivated);

    /// <summary>
    /// Determines whether an element is a <c>&lt;configuration&gt;</c> belonging to
    /// <c>maven-compiler-plugin</c>.
    /// </summary>
    /// <remarks>
    /// Both places a compiler configuration can appear are accepted, because both really set the release:
    /// <code>
    /// &lt;plugin&gt;
    ///   &lt;artifactId&gt;maven-compiler-plugin&lt;/artifactId&gt;
    ///   &lt;configuration&gt;&lt;release&gt;21&lt;/release&gt;&lt;/configuration&gt;      &lt;!-- plugin level --&gt;
    ///   &lt;executions&gt;&lt;execution&gt;
    ///     &lt;configuration&gt;&lt;release&gt;21&lt;/release&gt;&lt;/configuration&gt;    &lt;!-- execution level --&gt;
    ///   &lt;/execution&gt;&lt;/executions&gt;
    /// &lt;/plugin&gt;
    /// </code>
    /// The plugin is identified by <c>artifactId</c> alone: <c>groupId</c> defaults to
    /// <c>org.apache.maven.plugins</c> and is routinely omitted for the core plugins.
    /// See https://maven.apache.org/plugins/maven-compiler-plugin/compile-mojo.html.
    /// </remarks>
    private static bool IsCompilerPluginConfiguration(XElement? configuration)
    {
        if (!string.Equals(configuration?.Name.LocalName, "configuration", StringComparison.Ordinal))
        {
            return false;
        }

        for (var ancestor = configuration!.Parent; ancestor is not null; ancestor = ancestor.Parent)
        {
            if (!string.Equals(ancestor.Name.LocalName, "plugin", StringComparison.Ordinal))
            {
                continue;
            }

            return ancestor.Elements().Any(e =>
                string.Equals(e.Name.LocalName, "artifactId", StringComparison.Ordinal)
                && string.Equals(e.Value.Trim(), "maven-compiler-plugin", StringComparison.Ordinal));
        }

        return false;
    }

    /// <summary>
    /// Reads the target release from a Gradle build script.
    /// </summary>
    /// <remarks>
    /// Groovy and Kotlin DSL spellings that appear in Spring Initializr and Gradle's own documentation:
    /// <code>
    /// java { toolchain { languageVersion = JavaLanguageVersion.of(21) } }
    /// java.sourceCompatibility = JavaVersion.VERSION_21
    /// sourceCompatibility = '17'
    /// targetCompatibility = 1.8
    /// </code>
    /// Toolchain declarations take precedence over compatibility settings. Within each supported category,
    /// the final assignment is used because Gradle scripts can reassign the same setting. This remains a
    /// best-effort textual detector rather than a complete evaluation of the Gradle model.
    /// </remarks>
    private static string? DetectFromGradle(string buildScriptPath)
    {
        if (!File.Exists(buildScriptPath))
        {
            return null;
        }

        string contents;
        try
        {
            contents = File.ReadAllText(buildScriptPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        var script = StripComments(contents);
        var foreignProjectBlocks = FindForeignProjectBlocks(script);

        var assignment = LastActiveToolchainMatch(script, foreignProjectBlocks)
            ?? LastActiveMatch(TargetCompatibilityRegex(), script, excludedBlocks: foreignProjectBlocks)
            ?? LastActiveMatch(SourceCompatibilityRegex(), script, excludedBlocks: foreignProjectBlocks);

        return Normalize(assignment?.Groups["version"].Value.Replace('_', '.'));
    }

    /// <summary>
    /// Finds the final toolchain assignment that configures the application Java extension.
    /// </summary>
    private static Match? LastActiveToolchainMatch(
        GradleScript script,
        ImmutableArray<GradleBlock> foreignProjectBlocks)
    {
        var lastMatch = LastActiveMatch(DirectToolchainRegex(), script, excludedBlocks: foreignProjectBlocks);
        // Groovy also accepts a qualified top-level block:
        //   java.toolchain { languageVersion = JavaLanguageVersion.of(25) }
        // FindNamedBlocks balances the block and limits this spelling to the script's top level, so a
        // similarly shaped foreign project or task block cannot override the application toolchain.
        foreach (var toolchainBlock in FindNamedBlocks(script, "java.toolchain", 0, script.Text.Length))
        {
            var match = LastActiveMatch(
                LanguageVersionRegex(),
                script,
                toolchainBlock.ContentStart,
                toolchainBlock.ContentEnd,
                foreignProjectBlocks);

            if (match is not null && (lastMatch is null || match.Index > lastMatch.Index))
            {
                lastMatch = match;
            }
        }

        var applicationBlocks = FindNamedBlocks(script, "java", 0, script.Text.Length, directOnly: false)
            .Concat(FindConfiguredJavaBlocks(script, 0, script.Text.Length))
            .Where(block => !IsInsideAnyBlock(block.ContentStart, foreignProjectBlocks))
            .OrderBy(block => block.ContentStart);

        foreach (var javaBlock in applicationBlocks)
        {
            var scopedMatch = LastActiveMatch(
                ScopedToolchainRegex(),
                script,
                javaBlock.ContentStart,
                javaBlock.ContentEnd,
                foreignProjectBlocks);
            if (scopedMatch is not null && (lastMatch is null || scopedMatch.Index > lastMatch.Index))
            {
                lastMatch = scopedMatch;
            }

            foreach (var toolchainBlock in FindNamedBlocks(script, "toolchain", javaBlock.ContentStart, javaBlock.ContentEnd))
            {
                var match = LastActiveMatch(
                    LanguageVersionRegex(),
                    script,
                    toolchainBlock.ContentStart,
                    toolchainBlock.ContentEnd,
                    foreignProjectBlocks);

                if (match is not null && (lastMatch is null || match.Index > lastMatch.Index))
                {
                    lastMatch = match;
                }
            }
        }

        return lastMatch;
    }

    /// <summary>
    /// The last match that starts outside a string literal, or <c>null</c> when every match is inside one.
    /// </summary>
    /// <remarks>
    /// The patterns run over the raw script rather than a parsed model, so a version-shaped fragment quoted
    /// inside a string would otherwise be read as a declaration and win by appearing first:
    /// <code>
    /// println("JavaLanguageVersion.of(17)")
    /// java { toolchain { languageVersion = JavaLanguageVersion.of(21) } }
    /// </code>
    /// That selects a Java 17 image for a Java 21 application, which fails at runtime with
    /// <c>UnsupportedClassVersionError</c> rather than at publish time.
    /// <para>
    /// Only the match's start is tested, never the whole match. A declaration always begins outside the
    /// quotes while its value may legitimately sit inside them — <c>sourceCompatibility = '17'</c> is the
    /// ordinary Groovy spelling — so rejecting matches that merely overlap a literal would stop detecting
    /// the quoted form that <see cref="SourceCompatibilityRegex"/> exists to read.
    /// </para>
    /// </remarks>
    private static Match? LastActiveMatch(
        Regex regex,
        GradleScript script,
        int start = 0,
        int? end = null,
        ImmutableArray<GradleBlock> excludedBlocks = default)
    {
        Match? lastActiveMatch = null;
        var endOffset = end ?? script.Text.Length;

        for (var match = regex.Match(script.Text, start); match.Success && match.Index < endOffset; match = match.NextMatch())
        {
            if (match.Index + match.Length <= endOffset
                && !script.IsInsideStringLiteral(match.Index)
                && !IsInsideAnyBlock(match.Index, excludedBlocks)
                && !HasForeignProjectReceiver(script, match.Index))
            {
                lastActiveMatch = match;
            }
        }

        return lastActiveMatch;
    }

    private static ImmutableArray<GradleBlock> FindForeignProjectBlocks(GradleScript script)
    {
        var blocks = ImmutableArray.CreateBuilder<GradleBlock>();

        // Exclude scopes that configure a different project:
        //   project(":legacy") { java { ... } }
        //   subprojects { targetCompatibility = JavaVersion.VERSION_17 }
        //   configure(subprojects) { sourceCompatibility = JavaVersion.VERSION_17 }
        // Keep allprojects { ... } because Gradle applies it to the current/root project too.
        // https://docs.gradle.org/current/userguide/multi_project_builds.html
        for (var index = 0; index < script.Text.Length; index++)
        {
            if (script.IsInsideStringLiteral(index))
            {
                continue;
            }

            var openingBrace = -1;
            if (IsIdentifierAt(script.Text, "subprojects", index))
            {
                openingBrace = SkipWhitespace(script.Text, index + "subprojects".Length);
            }
            else if (IsIdentifierAt(script.Text, "configure", index))
            {
                var openingParenthesis = SkipWhitespace(script.Text, index + "configure".Length);
                if (openingParenthesis >= script.Text.Length || script.Text[openingParenthesis] is not '(')
                {
                    continue;
                }

                var closingParenthesis = FindClosingParenthesis(script, openingParenthesis);
                if (closingParenthesis < 0)
                {
                    break;
                }

                var configuredTarget = script.Text.AsSpan(
                    openingParenthesis + 1,
                    closingParenthesis - openingParenthesis - 1).Trim();
                if (!configuredTarget.Equals("subprojects", StringComparison.Ordinal))
                {
                    continue;
                }

                openingBrace = SkipWhitespace(script.Text, closingParenthesis + 1);
            }
            else if (IsIdentifierAt(script.Text, "project", index))
            {
                var openingParenthesis = SkipWhitespace(script.Text, index + "project".Length);
                if (openingParenthesis >= script.Text.Length || script.Text[openingParenthesis] is not '(')
                {
                    continue;
                }

                var closingParenthesis = FindClosingParenthesis(script, openingParenthesis);
                if (closingParenthesis < 0)
                {
                    break;
                }

                openingBrace = SkipWhitespace(script.Text, closingParenthesis + 1);
            }

            if (openingBrace < 0
                || openingBrace >= script.Text.Length
                || script.Text[openingBrace] is not '{')
            {
                continue;
            }

            var closingBrace = FindClosingBrace(script, openingBrace, script.Text.Length);
            if (closingBrace < 0)
            {
                break;
            }

            blocks.Add(new GradleBlock(openingBrace + 1, closingBrace));
            index = closingBrace;
        }

        return blocks.ToImmutable();
    }

    private static bool HasForeignProjectReceiver(GradleScript script, int memberOffset)
    {
        // Gradle lets a build configure a single foreign project without opening a block:
        //   project(":legacy").sourceCompatibility = JavaVersion.VERSION_17
        //   project(":legacy").targetCompatibility = JavaVersion.VERSION_17
        //   project(":legacy").java.toolchain.languageVersion = JavaLanguageVersion.of(17)
        // Only suppress a supported declaration when its matched receiver immediately follows project(...);
        // unrelated statements later in the script still belong to the current project.
        // https://docs.gradle.org/current/userguide/multi_project_builds.html
        for (var index = 0; index < memberOffset; index++)
        {
            if (script.IsInsideStringLiteral(index) || !IsIdentifierAt(script.Text, "project", index))
            {
                continue;
            }

            var openingParenthesis = SkipWhitespace(script.Text, index + "project".Length);
            if (openingParenthesis >= script.Text.Length || script.Text[openingParenthesis] is not '(')
            {
                continue;
            }

            var closingParenthesis = FindClosingParenthesis(script, openingParenthesis);
            if (closingParenthesis < 0 || closingParenthesis >= memberOffset)
            {
                continue;
            }

            var dot = SkipWhitespace(script.Text, closingParenthesis + 1);
            if (dot >= memberOffset || script.Text[dot] is not '.')
            {
                index = closingParenthesis;
                continue;
            }

            if (SkipWhitespace(script.Text, dot + 1) == memberOffset)
            {
                return true;
            }

            index = closingParenthesis;
        }

        return false;
    }

    private static int FindClosingParenthesis(GradleScript script, int openingParenthesis)
    {
        var depth = 1;

        for (var index = openingParenthesis + 1; index < script.Text.Length; index++)
        {
            if (script.IsInsideStringLiteral(index))
            {
                continue;
            }

            if (script.Text[index] is '(')
            {
                depth++;
            }
            else if (script.Text[index] is ')' && --depth == 0)
            {
                return index;
            }
        }

        return -1;
    }

    private static int SkipWhitespace(string text, int start)
    {
        while (start < text.Length && char.IsWhiteSpace(text[start]))
        {
            start++;
        }

        return start;
    }

    private static bool IsInsideAnyBlock(int offset, ImmutableArray<GradleBlock> blocks) =>
        !blocks.IsDefaultOrEmpty
        && blocks.Any(block => offset >= block.ContentStart && offset < block.ContentEnd);

    /// <summary>
    /// Finds named Gradle blocks at the top level of the specified script range.
    /// </summary>
    private static ImmutableArray<GradleBlock> FindNamedBlocks(
        GradleScript script,
        string name,
        int start,
        int end,
        bool directOnly = true)
    {
        var blocks = ImmutableArray.CreateBuilder<GradleBlock>();
        var depth = 0;

        for (var index = start; index < end; index++)
        {
            if (script.IsInsideStringLiteral(index))
            {
                continue;
            }

            if (script.Text[index] is '{')
            {
                depth++;
                continue;
            }

            if (script.Text[index] is '}')
            {
                depth--;
                continue;
            }

            if ((directOnly ? depth != 0 : depth < 0) || !IsIdentifierAt(script.Text, name, index))
            {
                continue;
            }

            var openingBrace = index + name.Length;
            while (openingBrace < end && char.IsWhiteSpace(script.Text[openingBrace]))
            {
                openingBrace++;
            }

            if (openingBrace >= end || script.Text[openingBrace] is not '{')
            {
                continue;
            }

            var closingBrace = FindClosingBrace(script, openingBrace, end);
            if (closingBrace < 0)
            {
                break;
            }

            blocks.Add(new GradleBlock(openingBrace + 1, closingBrace));
            index = closingBrace;
        }

        return blocks.ToImmutable();
    }

    private static ImmutableArray<GradleBlock> FindConfiguredJavaBlocks(
        GradleScript script,
        int start,
        int end)
    {
        var blocks = ImmutableArray.CreateBuilder<GradleBlock>();

        foreach (Match match in ConfiguredJavaPluginExtensionRegex().Matches(script.Text))
        {
            if (match.Index < start
                || match.Index + match.Length >= end
                || script.IsInsideStringLiteral(match.Index))
            {
                continue;
            }

            var openingBrace = match.Index + match.Length;
            while (openingBrace < end && char.IsWhiteSpace(script.Text[openingBrace]))
            {
                openingBrace++;
            }

            if (openingBrace >= end || script.Text[openingBrace] is not '{')
            {
                continue;
            }

            var closingBrace = FindClosingBrace(script, openingBrace, end);
            if (closingBrace >= 0)
            {
                blocks.Add(new GradleBlock(openingBrace + 1, closingBrace));
            }
        }

        return blocks.ToImmutable();
    }

    /// <summary>
    /// Finds the brace that closes the block beginning at <paramref name="openingBrace"/>.
    /// </summary>
    private static int FindClosingBrace(GradleScript script, int openingBrace, int end)
    {
        var depth = 1;

        for (var index = openingBrace + 1; index < end; index++)
        {
            if (script.IsInsideStringLiteral(index))
            {
                continue;
            }

            if (script.Text[index] is '{')
            {
                depth++;
            }
            else if (script.Text[index] is '}' && --depth == 0)
            {
                return index;
            }
        }

        return -1;
    }

    /// <summary>
    /// Determines whether <paramref name="name"/> is a complete identifier at <paramref name="offset"/>.
    /// </summary>
    private static bool IsIdentifierAt(string text, string name, int offset)
    {
        if (!text.AsSpan(offset).StartsWith(name, StringComparison.Ordinal)
            || offset > 0 && (IsGradleIdentifierCharacter(text[offset - 1]) || text[offset - 1] is '.'))
        {
            return false;
        }

        var end = offset + name.Length;
        return end >= text.Length || !IsGradleIdentifierCharacter(text[end]);
    }

    private static bool IsGradleIdentifierCharacter(char value) =>
        char.IsLetterOrDigit(value) || value is '_' or '$';

    /// <summary>
    /// The interior bounds of a balanced Gradle configuration block.
    /// </summary>
    private readonly record struct GradleBlock(int ContentStart, int ContentEnd);

    /// <summary>
    /// A Gradle build script with its comments removed, and the string literals that survived located.
    /// </summary>
    /// <param name="Text">The script with every comment blanked out.</param>
    /// <param name="StringSpans">
    /// The half-open interiors of the string literals in <paramref name="Text"/>, ascending and
    /// non-overlapping, which lets a lookup binary search rather than rescan.
    /// </param>
    private readonly record struct GradleScript(string Text, ImmutableArray<Range> StringSpans)
    {
        internal bool IsInsideStringLiteral(int offset)
        {
            // Ascending and non-overlapping, so the last span starting at or before the offset is the only
            // one that can contain it.
            var low = 0;
            var high = StringSpans.Length - 1;
            var candidate = -1;

            while (low <= high)
            {
                var middle = low + ((high - low) / 2);

                if (StringSpans[middle].Start.Value <= offset)
                {
                    candidate = middle;
                    low = middle + 1;
                }
                else
                {
                    high = middle - 1;
                }
            }

            return candidate >= 0 && offset < StringSpans[candidate].End.Value;
        }
    }

    /// <summary>
    /// Blanks out line and block comments so a commented-out setting cannot be mistaken for an active one,
    /// and locates the string literals that survive so a quoted one cannot be either.
    /// </summary>
    /// <remarks>
    /// The version patterns are applied to the raw script rather than a parsed model, so without this a
    /// leftover line wins over the setting that is actually in effect:
    /// <code>
    /// java {
    ///     toolchain {
    ///         // languageVersion = JavaLanguageVersion.of(17)
    ///         languageVersion = JavaLanguageVersion.of(21)
    ///     }
    /// }
    /// </code>
    /// String literals are tracked so that the <c>//</c> inside a repository URL, and any <c>/*</c> inside
    /// a string, are not treated as comment starts — the latter would otherwise swallow the rest of the
    /// file. Single, double, and triple-quoted forms are recognized, covering both the Groovy and the
    /// Kotlin DSL. Groovy slashy and dollar-slashy strings are also tracked because braces in their regular
    /// expressions must not change the application-block depth.
    /// </remarks>
    private static GradleScript StripComments(string contents)
    {
        var builder = new StringBuilder(contents.Length);
        var spans = ImmutableArray.CreateBuilder<Range>();
        var index = 0;

        while (index < contents.Length)
        {
            var current = contents[index];

            if (current is '$'
                && index + 1 < contents.Length
                && contents[index + 1] is '/')
            {
                builder.Append("$/");
                index += 2;
                var interiorStart = builder.Length;
                var terminated = false;

                while (index < contents.Length)
                {
                    if (contents[index] is '$'
                        && index + 1 < contents.Length
                        && contents[index + 1] is '$' or '/')
                    {
                        builder.Append(contents, index, 2);
                        index += 2;
                        continue;
                    }

                    if (contents.AsSpan(index).StartsWith("/$", StringComparison.Ordinal))
                    {
                        spans.Add(new Range(interiorStart, builder.Length));
                        builder.Append("/$");
                        index += 2;
                        terminated = true;
                        break;
                    }

                    builder.Append(contents[index]);
                    index++;
                }

                if (!terminated)
                {
                    spans.Add(new Range(interiorStart, builder.Length));
                }

                continue;
            }

            if (current is '/' && index + 1 < contents.Length)
            {
                if (contents[index + 1] is '/')
                {
                    while (index < contents.Length && contents[index] is not ('\n' or '\r'))
                    {
                        index++;
                    }

                    continue;
                }

                if (contents[index + 1] is '*')
                {
                    var end = contents.IndexOf("*/", index + 2, StringComparison.Ordinal);
                    index = end < 0 ? contents.Length : end + 2;
                    // A newline stands in for the comment so that the text on either side of a block
                    // comment cannot be joined into a single line and match as one declaration.
                    builder.Append('\n');
                    continue;
                }

                if (IsSlashyStringStart(contents, index))
                {
                    builder.Append('/');
                    index++;
                    var interiorStart = builder.Length;
                    var terminated = false;

                    while (index < contents.Length)
                    {
                        if (contents[index] is '\\' && index + 1 < contents.Length)
                        {
                            builder.Append(contents, index, 2);
                            index += 2;
                            continue;
                        }

                        if (contents[index] is '/')
                        {
                            spans.Add(new Range(interiorStart, builder.Length));
                            builder.Append('/');
                            index++;
                            terminated = true;
                            break;
                        }

                        builder.Append(contents[index]);
                        index++;
                    }

                    if (!terminated)
                    {
                        spans.Add(new Range(interiorStart, builder.Length));
                    }

                    continue;
                }
            }

            if (current is '"' or '\'')
            {
                var delimiter = new string(current, contents.AsSpan(index).StartsWith(new string(current, 3), StringComparison.Ordinal) ? 3 : 1);
                builder.Append(delimiter);
                index += delimiter.Length;

                // Recorded in output coordinates, and covering only the interior: a declaration starts
                // outside the quotes, so keeping the delimiters active is what lets the value of
                // sourceCompatibility = '17' stay readable while its surroundings stay inert.
                var interiorStart = builder.Length;

                // The literal's contents are copied through unchanged - only comments are removed - so a
                // version written as a quoted string, such as sourceCompatibility = '17', still matches.
                while (index < contents.Length)
                {
                    if (contents[index] is '\\' && index + 1 < contents.Length)
                    {
                        builder.Append(contents, index, 2);
                        index += 2;
                        continue;
                    }

                    if (contents.AsSpan(index).StartsWith(delimiter, StringComparison.Ordinal))
                    {
                        spans.Add(new Range(interiorStart, builder.Length));
                        builder.Append(delimiter);
                        index += delimiter.Length;
                        break;
                    }

                    builder.Append(contents[index]);
                    index++;
                }

                // An unterminated literal runs to the end of the file, which is what the Groovy and Kotlin
                // compilers see too. Closing it at the last character keeps the rest of the script inert
                // rather than letting a stray quote re-activate it.
                if (index >= contents.Length && (spans.Count == 0 || spans[^1].End.Value != builder.Length))
                {
                    spans.Add(new Range(interiorStart, builder.Length));
                }

                continue;
            }

            builder.Append(current);
            index++;
        }

        return new GradleScript(builder.ToString(), spans.ToImmutable());
    }

    private static bool IsSlashyStringStart(string contents, int slashIndex)
    {
        var index = slashIndex - 1;
        while (index >= 0 && char.IsWhiteSpace(contents[index]))
        {
            index--;
        }

        if (index < 0)
        {
            return true;
        }

        if (contents[index] is '=' or '(' or '[' or '{' or ',' or ':' or ';'
            or '!' or '&' or '|' or '?' or '+' or '-' or '*' or '%' or '^' or '~' or '<' or '>')
        {
            return true;
        }

        if (!IsGradleIdentifierCharacter(contents[index]))
        {
            return false;
        }

        var wordEnd = index + 1;
        while (index >= 0 && IsGradleIdentifierCharacter(contents[index]))
        {
            index--;
        }

        var word = contents.AsSpan(index + 1, wordEnd - index - 1);
        return word is "assert" or "case" or "in" or "return" or "throw" or "yield";
    }

    /// <summary>
    /// Maps a declared release to the numeric form used in container image tags.
    /// </summary>
    /// <remarks>
    /// Java 8 and earlier are written <c>1.8</c> in build files but tagged <c>8</c> in images
    /// (<c>eclipse-temurin:8-jre</c>). Anything that is not a plain release number is rejected so a
    /// property reference such as <c>${java.version}</c> cannot end up inside a <c>FROM</c> instruction.
    /// </remarks>
    private static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        value = value.Trim();

        if (value.StartsWith("1.", StringComparison.Ordinal))
        {
            value = value[2..];
        }

        return value.Length > 0 && value.All(char.IsAsciiDigit) ? value : null;
    }

    // Matches direct application assignments outside a block:
    // java.toolchain.languageVersion = JavaLanguageVersion.of(21)
    // java.toolchain.languageVersion.set(JavaLanguageVersion.of(21))
    [GeneratedRegex(@"(?<![\w$.])java\s*\.\s*toolchain\s*\.\s*languageVersion\s*(?:=|\.set\()\s*JavaLanguageVersion\.of\(\s*(?<version>\d+)\s*\)")]
    private static partial Regex DirectToolchainRegex();

    [GeneratedRegex(@"(?<![\w$.])toolchain\s*\.\s*languageVersion\s*(?:=|\.set\()\s*JavaLanguageVersion\.of\(\s*(?<version>\d+)\s*\)")]
    private static partial Regex ScopedToolchainRegex();

    [GeneratedRegex(@"(?<![\w$.])(?:extensions\s*\.\s*)?configure\s*<\s*(?:org\.gradle\.api\.plugins\.)?JavaPluginExtension\s*>")]
    private static partial Regex ConfiguredJavaPluginExtensionRegex();

    // Applied only inside a balanced java { toolchain { ... } } scope.
    [GeneratedRegex(@"\blanguageVersion\s*(?:=|\.set\()\s*JavaLanguageVersion\.of\(\s*(?<version>\d+)\s*\)")]
    private static partial Regex LanguageVersionRegex();

    // Matches JavaVersion.VERSION_21, VERSION_1_8, and numeric/string compatibility forms.
    [GeneratedRegex(@"\btargetCompatibility\s*(?:=|\.set\()\s*(?:JavaVersion\.VERSION_(?<version>\d+(?:_\d+)?)|['""]?(?<version>\d+(?:\.\d+)?)['""]?)")]
    private static partial Regex TargetCompatibilityRegex();

    [GeneratedRegex(@"\bsourceCompatibility\s*(?:=|\.set\()\s*(?:JavaVersion\.VERSION_(?<version>\d+(?:_\d+)?)|['""]?(?<version>\d+(?:\.\d+)?)['""]?)")]
    private static partial Regex SourceCompatibilityRegex();
}
