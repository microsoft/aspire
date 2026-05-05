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

Template end-to-end coverage lives in *[repo_root]/tests/Aspire.Cli.EndToEnd.Tests/*. For local runs, follow the directions in *[repo_root]/tests/Aspire.Cli.EndToEnd.Tests/README.md*.
