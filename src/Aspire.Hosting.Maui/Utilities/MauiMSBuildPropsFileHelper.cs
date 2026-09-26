// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;
using System.Xml.Linq;

namespace Aspire.Hosting.Maui.Utilities;

/// <summary>
/// Generates the temporary MSBuild <c>.props</c> files that carry user-supplied MSBuild properties
/// for a MAUI platform resource.
/// </summary>
/// <remarks>
/// This mirrors <see cref="MauiEnvironmentHelper"/>, which emits environment variables into a generated
/// <c>.targets</c> file. Here the user's build properties are written into a <c>.props</c> file that
/// is imported early via <c>CustomBeforeMicrosoftCommonProps</c>. Emitting the values into a file (rather
/// than passing <c>-p:</c> arguments) keeps them off the process command line and lets XML handle escaping,
/// so values containing <c>;</c>, <c>%</c>, or other special characters need no command-line encoding.
/// </remarks>
internal static class MauiMSBuildPropsFileHelper
{
    /// <summary>
    /// Writes the supplied MSBuild properties to a <c>.props</c> file and returns its path.
    /// </summary>
    /// <param name="directory">The directory to write the <c>.props</c> file into.</param>
    /// <param name="resourceName">The resource name, used to build a stable, descriptive file name.</param>
    /// <param name="properties">The MSBuild properties to emit.</param>
    /// <returns>The absolute path to the generated <c>.props</c> file.</returns>
    public static string WritePropsFile(string directory, string resourceName, IReadOnlyDictionary<string, string> properties)
    {
        var sanitizedName = MauiEnvironmentHelper.SanitizeFileName($"{resourceName}-build");
        var propsFilePath = Path.Combine(directory, $"{sanitizedName}.props");

        var content = GeneratePropsFileContent(properties);

        // Write only when the content changes so the file's timestamp stays stable across the pre-build
        // and the launch (and across restarts). A stable file keeps the launch's build inputs byte- and
        // timestamp-identical to the pre-build, so the launch's up-to-date check does not force a rebuild.
        // The file is tiny and on the build/launch path, so the synchronous read/write is negligible.
        if (!File.Exists(propsFilePath) || !string.Equals(File.ReadAllText(propsFilePath, Encoding.UTF8), content, StringComparison.Ordinal))
        {
            File.WriteAllText(propsFilePath, content, Encoding.UTF8);
        }

        return propsFilePath;
    }

    /// <summary>
    /// Generates the XML content of a <c>.props</c> file that sets the supplied MSBuild properties.
    /// </summary>
    internal static string GeneratePropsFileContent(IReadOnlyDictionary<string, string> properties)
    {
        var propertyGroup = new XElement("PropertyGroup");

        // Order the properties so the generated file is deterministic, matching the environment
        // targets file generation and making the output easy to diff.
        foreach (var (name, value) in properties.OrderBy(kvp => kvp.Key, StringComparer.Ordinal))
        {
            // XElement escapes the value as XML content, so scalar property values (including ones with
            // ';' or '%') are preserved literally without MSBuild command-line escaping.
            propertyGroup.Add(new XElement(name, value));
        }

        var projectElement = new XElement("Project", propertyGroup);
        var document = new XDocument(new XDeclaration("1.0", "utf-8", "yes"), projectElement);

        using var stringWriter = new StringWriter();
        document.Save(stringWriter);
        return stringWriter.ToString();
    }
}
