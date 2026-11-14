'use strict';

function selectAppHostSdkVersion(cliVersion, availablePackageVersions) {
  return availablePackageVersions.includes(cliVersion)
    ? cliVersion
    : availablePackageVersions[0] ?? cliVersion;
}

function resolveAppHostSdkVersion(versionResult, getAvailablePackageVersions, getFallbackVersion, environment = process.env) {
  const overrideVersion = environment.ASPIRE_EXTENSION_E2E_APPHOST_SDK_VERSION;
  if (overrideVersion) {
    return { version: overrideVersion, usedLocalVersion: false };
  }

  const availablePackageVersions = getAvailablePackageVersions();
  if (versionResult.status === 0) {
    const cliVersion = versionResult.stdout.trim().split('+')[0];
    if (/^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$/.test(cliVersion)) {
      const version = selectAppHostSdkVersion(cliVersion, availablePackageVersions);
      return { version, usedLocalVersion: version !== cliVersion };
    }
  }

  return { version: getFallbackVersion(), usedLocalVersion: false };
}

function createBrowserDebuggerAppHostProject(appHostSdkVersion) {
  return `<Project Sdk="Aspire.AppHost.Sdk/${appHostSdkVersion}">

  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net11.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="../StandaloneClient/StandaloneClient.csproj" />
    <ProjectReference Include="../HostedGlobal/HostedGlobal/HostedGlobal.csproj" />
    <ProjectReference Include="../HostedGlobal/HostedGlobal.Client/HostedGlobal.Client.csproj" />
    <ProjectReference Include="../HostedPerPage/HostedPerPage/HostedPerPage.csproj" />
    <ProjectReference Include="../HostedPerPage/HostedPerPage.Client/HostedPerPage.Client.csproj" />
    <PackageReference Include="Aspire.Hosting.Blazor" Version="${appHostSdkVersion}" />
    <PackageReference Include="Aspire.Hosting.Dotnet" Version="${appHostSdkVersion}" />
  </ItemGroup>

</Project>
`;
}

module.exports = { createBrowserDebuggerAppHostProject, resolveAppHostSdkVersion };
