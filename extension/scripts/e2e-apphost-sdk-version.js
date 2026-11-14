'use strict';

function selectAppHostSdkVersion(cliVersion, availablePackageVersions) {
  return availablePackageVersions.includes(cliVersion)
    ? cliVersion
    : availablePackageVersions[0] ?? cliVersion;
}

module.exports = { selectAppHostSdkVersion };
