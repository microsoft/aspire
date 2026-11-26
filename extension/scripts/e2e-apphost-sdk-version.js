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

// A package that sets SuppressFinalPackageVersion keeps its prerelease suffix when release branding
// stabilizes the others, so the same build produces, for example:
//   Aspire.Hosting.AppHost.17.0.1.nupkg
//   Aspire.Hosting.Blazor.17.0.1-ci.nupkg
// Pinning such a package to the AppHost version fails restore with NU1103, so for a stable SDK
// prefer the exact version and otherwise use a prerelease of the same major.minor.patch from
// the selected SDK's local package source. The caller must exclude unrelated cached sources.
//
// Prerelease builds, including PR builds (VersionSuffix=pr.<number>.g<sha>), give every
// package the same version as the SDK, so a prerelease SDK only accepts the exact match; a
// missing companion keeps the SDK pin so restore reports the missing artifact. For the same
// reason a same-patch `-pr.*` package is a leftover from another build, even in the same
// directory, and is never a valid fallback for a stable SDK.
function selectCompanionPackageVersion(appHostSdkVersion, availablePackageVersions) {
  if (availablePackageVersions.includes(appHostSdkVersion) || appHostSdkVersion.includes('-')) {
    return appHostSdkVersion;
  }

  const versionPrefix = appHostSdkVersion.split('-')[0];
  return availablePackageVersions.find(version => {
    const [prefix, prerelease] = splitVersion(version);
    return prefix === versionPrefix && !prerelease.startsWith('pr.');
  }) ?? appHostSdkVersion;
}

function splitVersion(version) {
  const separatorIndex = version.indexOf('-');
  return separatorIndex < 0
    ? [version, '']
    : [version.slice(0, separatorIndex), version.slice(separatorIndex + 1)];
}

function createBrowserDebuggerAppHostProject(appHostSdkVersion, blazorPackageVersion) {
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
    <PackageReference Include="Aspire.Hosting.Blazor" Version="${blazorPackageVersion}" />
    <PackageReference Include="Aspire.Hosting.Dotnet" Version="${appHostSdkVersion}" />
  </ItemGroup>

</Project>
`;
}

module.exports = { createBrowserDebuggerAppHostProject, resolveAppHostSdkVersion, selectCompanionPackageVersion };
