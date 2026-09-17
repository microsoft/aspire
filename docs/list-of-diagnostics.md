# List of Diagnostics Produced by Aspire

## MSBuild Warnings

| Diagnostic ID | Severity | Description | Location |
| ------------- | -------- | ----------- | -------- |
| `ASPIRE001` | Warning | The '\[ProjectLanguage\]' language isn't fully supported by Aspire - some code generation targets will not run, so will require manual authoring. | [src/Aspire.Hosting.AppHost/build/Aspire.Hosting.AppHost.in.targets](../src/Aspire.Hosting.AppHost/build/Aspire.Hosting.AppHost.in.targets) |
| `ASPIRE002` | Warning | '\[ProjectName\]' is an Aspire AppHost project but necessary dependencies aren't present. Are you missing an Aspire.Hosting.AppHost PackageReference? | [src/Aspire.Hosting.Sdk/SDK/Sdk.in.targets](../src/Aspire.Hosting.Sdk/SDK/Sdk.in.targets) |
| `ASPIRE003` | Warning | '\[ProjectName\]' is an Aspire AppHost project that requires Visual Studio version 17.10 or above to work correctly. You are using version $(MSBuildVersion). | [src/Aspire.Hosting.Sdk/SDK/Sdk.in.targets](../src/Aspire.Hosting.Sdk/SDK/Sdk.in.targets) |
| `ASPIRE004` | Warning | '\[ProjectName\]' is referenced by an Aspire Host project, but it is not an executable. Did you mean to set IsAspireProjectResource=&quot;false&quot;? | [src/Aspire.Hosting.AppHost/build/Aspire.Hosting.AppHost.in.targets](../src/Aspire.Hosting.AppHost/build/Aspire.Hosting.AppHost.in.targets) |
| `ASPIRE005` | Error | (Deprecated) This diagnostic is no longer used. | |
| `ASPIRE007` | Error | '\[ProjectName\]' project requires a reference to &quot;Aspire.AppHost.Sdk&quot; with version &quot;9.0.0&quot; or greater to work correctly. Please add the following line after the Project declaration `<Sdk Name=Aspire.AppHost.Sdk" Version="9.0.0" />`. | [src/Aspire.Hosting.AppHost/build/Aspire.Hosting.AppHost.in.targets](../src/Aspire.Hosting.AppHost/build/Aspire.Hosting.AppHost.in.targets) |
| `ASPIRE008` | Error | '\[ProjectName\]' project requires GenerateAssemblyInfo to be enabled. The Aspire AppHost relies on assembly metadata attributes to locate required dependencies. Please remove &lt;GenerateAssemblyInfo&gt;false&lt;/GenerateAssemblyInfo&gt; from your project file or set it to true. | [src/Aspire.Hosting.AppHost/build/Aspire.Hosting.AppHost.in.targets](../src/Aspire.Hosting.AppHost/build/Aspire.Hosting.AppHost.in.targets) |
| `ASPIRE009` | Error | '\[ProjectName\]' is configured to use the Aspire CLI bundle, but the bundle could not be resolved. | [src/Aspire.Hosting.AppHost/build/Aspire.Hosting.AppHost.in.targets](../src/Aspire.Hosting.AppHost/build/Aspire.Hosting.AppHost.in.targets) |
| `ASPIRE010` | Warning | '\[ProjectName\]' is configured with AspireUseCliBundle=false. Some Aspire features require the Aspire CLI bundle. Set AspireUseCliBundle=true to enable those features, or suppress ASPIRE010 to continue without the bundle. See https://aka.ms/aspire/diagnostics/aspire010 for more information. | [src/Aspire.Hosting.AppHost/build/Aspire.Hosting.AppHost.in.targets](../src/Aspire.Hosting.AppHost/build/Aspire.Hosting.AppHost.in.targets) |
| `ASPIRE011` | Error | '\[ProjectName\]' is configured to invoke the Aspire CLI through DNX, but the dnx command could not be found on PATH. | [src/Aspire.Hosting.AppHost/build/Aspire.Hosting.AppHost.in.targets](../src/Aspire.Hosting.AppHost/build/Aspire.Hosting.AppHost.in.targets) |

Setting `AspireUseCliBundle=true` enables CLI delegation. `AspireCliInvocationMode=Path` uses `aspire` from `PATH`, falling back to the `Aspire.Cli` version paired with the AppHost SDK through DNX when a compatible command is unavailable. `AspireCliInvocationMode=Dnx` invokes the unversioned `Aspire.Cli` package through DNX so an in-scope tool manifest is honored, or the latest package is used when no manifest applies. `AspireCliInvocationMode=DnxPinned` invokes the exact `Aspire.Cli` version paired with `Aspire.AppHost.Sdk`.

## Analyzer Warnings

| Diagnostic ID | Severity | Description | Location |
| ------------- | -------- | ----------- | -------- |
| `ASPIRE006` | Error | Application model items must have valid names | [src/Aspire.Hosting.Analyzers/AppHostAnalyzer.Diagnostics.cs](../src/Aspire.Hosting.Analyzers/AppHostAnalyzer.Diagnostics.cs) |
