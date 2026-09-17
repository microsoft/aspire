# Aspire Templates

Templates are located at *[repo_root]/src/Aspire.ProjectTemplates/*. The *Aspire.ProjectTemplates.csproj* file builds the template package. The template content is in the *templates* sub-directory, with each sub-directory beneath that representing a separate project template.

## Update instructions

Instructions to update the project templates for a new Aspire version.

### Updating template content

Each template contains content that has placeholders for many of the versions represented that get replaced by the build. Note that some dependency versions are still static.

For each template:

1. Updating the versions for non-Aspire packages referenced in all *.csproj* files isn't covered as part of this process. These package versions should be updated by our regular process for updating the versions of our dependencies.

### Updating localization files

Build the templates package project to ensure localization files are updated to match all changes by running `dotnet pack` on the *./src/Aspire.ProjectTemplates/Aspire.ProjectTemplates.csproj* project, e.g.:

```shell
dotnet pack ./src/Aspire.ProjectTemplates/Aspire.ProjectTemplates.csproj
```

### Updating tests

Template tests can be run using the standard test commands. You can follow the directions in *[repo_root]/tests/Aspire.Templates.Tests/README.md* to run them locally if desired, or simply send a PR and observe the test output there.

### Running generated test projects

MSTest and xUnit `v3mtp` templates produce Microsoft.Testing.Platform (MTP) executables. Run these tests with `dotnet run` from the generated test project directory. This works across the supported SDK versions without changing the containing solution's test runner.

With .NET 10 SDK or later, using `dotnet test` for MTP 2 projects instead requires opting into the native MTP runner in the solution's `global.json`. The legacy VSTest entry point is no longer supported for these projects. See [MTP integration with dotnet test](https://learn.microsoft.com/dotnet/core/testing/microsoft-testing-platform-integration-dotnet-test).

The NUnit and xUnit VSTest template variants continue to use `dotnet test`.
