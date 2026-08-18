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
    public static string Detect(string appDirectory, JavaBuildTool? tool = null)
    {
        string?[] candidates = tool is JavaBuildTool.Gradle
            ?
            [
                DetectFromGradle(Path.Combine(appDirectory, "build.gradle")),
                DetectFromGradle(Path.Combine(appDirectory, "build.gradle.kts")),
                DetectFromPom(Path.Combine(appDirectory, "pom.xml")),
            ]
            :
            [
                DetectFromPom(Path.Combine(appDirectory, "pom.xml")),
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
    private static string? DetectFromPom(string pomPath)
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
        // Every element with a matching name is considered, not just the first: a POM often declares
        // <release>${java.version}</release> on the compiler plugin and a literal elsewhere, and stopping
        // at the unresolvable property reference would fall back to the default version instead.
        // Ordered by how directly each one decides the bytecode version, most direct first, because the
        // runtime image has to be at least what the compiler actually emitted.
        //
        // java.version is last despite being the most recognisable. It is not a Maven property at all:
        // it works only because spring-boot-starter-parent maps it onto the real one with
        // <maven.compiler.release>${java.version}</maven.compiler.release>. A POM that sets both
        // java.version and maven.compiler.release overrides that mapping, so Maven compiles to the
        // latter and reading java.version would pick a runtime too old to load the classes.
        // https://docs.spring.io/spring-boot/maven-plugin/using.html
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
            foreach (var element in document.Descendants().Where(e => string.Equals(e.Name.LocalName, name, StringComparison.Ordinal)))
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

        return null;
    }

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
    /// The toolchain is checked first because it pins the JDK Gradle actually compiles with, whereas
    /// source/target compatibility only constrain the bytecode level.
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

        if (FirstActiveMatch(ToolchainRegex(), script) is { } toolchain)
        {
            return Normalize(toolchain.Groups[1].Value);
        }

        if (FirstActiveMatch(JavaVersionEnumRegex(), script) is { } enumMatch)
        {
            return Normalize(enumMatch.Groups[1].Value.Replace('_', '.'));
        }

        if (FirstActiveMatch(CompatibilityRegex(), script) is { } compatibility)
        {
            return Normalize(compatibility.Groups[1].Value);
        }

        return null;
    }

    /// <summary>
    /// The first match that starts outside a string literal, or <c>null</c> when every match is inside one.
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
    /// the quoted form that <see cref="CompatibilityRegex"/> exists to read.
    /// </para>
    /// </remarks>
    private static Match? FirstActiveMatch(Regex regex, GradleScript script)
    {
        for (var match = regex.Match(script.Text); match.Success; match = match.NextMatch())
        {
            if (!script.IsInsideStringLiteral(match.Index))
            {
                return match;
            }
        }

        return null;
    }

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
    /// Kotlin DSL. Groovy's slashy strings (<c>/pattern/</c>) are not recognized; they do not appear in the
    /// toolchain or compatibility declarations this reads.
    /// </remarks>
    private static GradleScript StripComments(string contents)
    {
        var builder = new StringBuilder(contents.Length);
        var spans = ImmutableArray.CreateBuilder<Range>();
        var index = 0;

        while (index < contents.Length)
        {
            var current = contents[index];

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

    // Matches: languageVersion = JavaLanguageVersion.of(21)  and  languageVersion.set(JavaLanguageVersion.of(21))
    [GeneratedRegex(@"JavaLanguageVersion\.of\(\s*(\d+)\s*\)")]
    private static partial Regex ToolchainRegex();

    // Matches: sourceCompatibility = JavaVersion.VERSION_21  and  VERSION_1_8
    [GeneratedRegex(@"JavaVersion\.VERSION_(\d+(?:_\d+)?)")]
    private static partial Regex JavaVersionEnumRegex();

    // Matches: sourceCompatibility = '17'   targetCompatibility = 17   sourceCompatibility = "1.8"
    [GeneratedRegex(@"(?:source|target)Compatibility\s*(?:=|\.set\()\s*['""]?(\d+(?:\.\d+)?)['""]?")]
    private static partial Regex CompatibilityRegex();
}
