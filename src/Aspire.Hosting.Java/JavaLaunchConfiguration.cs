// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Serialization;
using Aspire.Hosting.ApplicationModel;

#pragma warning disable ASPIREEXTENSION001 // Launch configuration types are experimental.

namespace Aspire.Hosting.Java;

/// <summary>
/// The launch configuration handed to an IDE so it can run or debug a Java application itself,
/// rather than Aspire starting the process.
/// </summary>
/// <remarks>
/// <para>
/// The IDE always starts a JVM directly, even when the resource would otherwise be launched through
/// <c>mvnw spring-boot:run</c> or <c>gradlew bootRun</c>. Those wrappers fork a second JVM, so a debugger
/// attached to the wrapper process would never see application code. The build-tool invocation is declared
/// with <c>WithLaunchToolArgs(..., ownedByLaunchConfigurationType: "java")</c>, which drops those arguments
/// for exactly this launch configuration type while keeping them for normal process execution.
/// </para>
/// <para>
/// The property names match the <c>vscjava.vscode-java-debug</c> launch schema after the extension
/// translates them, and mirror the keys consumed by <c>extension/src/debugger/languages/java.ts</c>.
/// See https://github.com/microsoft/vscode-java-debug/blob/main/Configuration.md.
/// </para>
/// </remarks>
internal sealed class JavaLaunchConfiguration() : ExecutableLaunchConfiguration("java")
{
    /// <summary>
    /// The debug request type. Java applications are always launched by the IDE, never attached to.
    /// </summary>
    [JsonPropertyName("request")]
    public string Request { get; set; } = "launch";

    /// <summary>
    /// The absolute Java executable selected by the resource model. Omitted when the resource uses the
    /// ambient <c>java</c> command or launches through a build tool.
    /// </summary>
    [JsonPropertyName("java_exec")]
    public string? JavaExec { get; set; }

    /// <summary>
    /// The working directory of the Java project. The IDE uses it as the debug session's working
    /// directory and to scope main class resolution to this resource's project.
    /// </summary>
    [JsonPropertyName("working_directory")]
    public string WorkingDirectory { get; set; } = string.Empty;

    /// <summary>
    /// The fully qualified main class, optionally prefixed with a Java module name
    /// (<c>[module/]com.example.App</c>), or the path of the <c>.java</c> source file declaring
    /// <c>main</c>. When omitted the IDE resolves the main class itself, scoped to
    /// <see cref="ProjectName"/> when one was supplied.
    /// </summary>
    /// <remarks>
    /// A JAR path is deliberately not accepted here. The debug adapter documents this attribute as
    /// "the fully qualified class name (e.g. [java module name/]com.xyz.MainApp) or the java file path
    /// of the program entry", so it never opens an archive to read <c>Main-Class</c>. An executable JAR
    /// belongs in <see cref="ClassPaths"/> with its manifest's <c>Main-Class</c> sent here.
    /// </remarks>
    [JsonPropertyName("main_class")]
    public string? MainClass { get; set; }

    /// <summary>
    /// The name the IDE's Java tooling imported this resource's project under, used to scope main class
    /// resolution.
    /// </summary>
    /// <remarks>
    /// Sent whether or not <see cref="MainClass"/> is known, because the adapter needs the scope in
    /// both cases. Without a main class it searches every project in the workspace, finds one in each
    /// Java resource, and prompts the user to choose on every launch. With a main class it still
    /// searches the whole workspace, and fails the launch with "Main class ... isn't unique in the
    /// workspace" if the class is visible through more than one project. Scoping the search to a single
    /// project resolves both.
    /// </remarks>
    [JsonPropertyName("project_name")]
    public string? ProjectName { get; set; }

    /// <summary>
    /// Classpath entries the IDE should launch the JVM with. Empty when the IDE should resolve the
    /// classpath from the project itself, which is the normal case for a Maven or Gradle project.
    /// </summary>
    /// <remarks>
    /// This is how a prebuilt JAR is debugged: the archive goes on the classpath and its manifest's
    /// <c>Main-Class</c> becomes <see cref="MainClass"/>. Without it the adapter would fall back to
    /// resolving the class from a language-server project that does not contain the JAR's classes.
    /// </remarks>
    [JsonPropertyName("class_paths")]
    public string[]? ClassPaths { get; set; }

    /// <summary>
    /// The build tool that owns the project, used by the IDE to refresh the project's classpath
    /// before starting a session. One of <c>maven</c>, <c>gradle</c>, or <see langword="null"/> when the
    /// application runs from a prebuilt JAR.
    /// </summary>
    [JsonPropertyName("build_tool")]
    public string? BuildTool { get; set; }
}
