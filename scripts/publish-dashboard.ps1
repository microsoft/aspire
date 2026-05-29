param(
    [string]$Registry = "h3econtainerregistry.azurecr.io",
    [string]$Repository = "aspire-dashboard"
)

# Parse version from eng/Versions.props
[xml]$props = Get-Content "$PSScriptRoot/../eng/Versions.props"
$major = $props.Project.PropertyGroup.MajorVersion
$minor = $props.Project.PropertyGroup.MinorVersion
$patch = $props.Project.PropertyGroup.PatchVersion
$preRelease = $props.Project.PropertyGroup.PreReleaseVersionLabel
$tag = "$major.$minor.$patch-$preRelease"

Write-Host "Building and pushing: $Registry/$Repository`:$tag" -ForegroundColor Cyan

# Publish
dotnet publish "$PSScriptRoot/../src/Aspire.Dashboard/Aspire.Dashboard.csproj" -c Release -r linux-x64 --self-contained false -o "$PSScriptRoot/../src/Aspire.Dashboard/publish"
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

# Docker build
docker build -t "$Registry/${Repository}:$tag" "$PSScriptRoot/../src/Aspire.Dashboard"
if ($LASTEXITCODE -ne 0) { throw "docker build failed" }

# ACR login and push
az acr login --name ($Registry -replace '\.azurecr\.io$','')
if ($LASTEXITCODE -ne 0) { throw "az acr login failed" }

docker push "$Registry/${Repository}:$tag"
if ($LASTEXITCODE -ne 0) { throw "docker push failed" }

Write-Host "Successfully pushed: $Registry/$Repository`:$tag" -ForegroundColor Green
