[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [switch]$Force
)

$ErrorActionPreference = "Stop"

# Resolve repo paths relative to this script. The script lives at
# Tools\Weva.Generators\ and publishes to
# Packages\com.wevaui\Runtime\Generators\.
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Definition
$RepoRoot  = Resolve-Path (Join-Path $ScriptDir "..\..")
$Csproj    = Join-Path $ScriptDir "Weva.Generators.csproj"
$BuildOut  = Join-Path $ScriptDir "bin\$Configuration\netstandard2.0\Weva.Generators.dll"
$PublishDir = Join-Path $RepoRoot "Packages\com.wevaui\Runtime\Generators"
$PublishDll = Join-Path $PublishDir "Weva.Generators.dll"
$PublishMeta = "$PublishDll.meta"

Write-Host "[weva] generator build:"
Write-Host "  csproj : $Csproj"
Write-Host "  output : $PublishDll"

if (-not (Test-Path $PublishDir)) {
    New-Item -ItemType Directory -Force -Path $PublishDir | Out-Null
}

# Idempotency: if the source files are unchanged AND the published DLL
# matches the freshly-built one byte-for-byte, skip the copy. We still
# always run dotnet build because the SDK already short-circuits when
# inputs are clean.
& dotnet build -c $Configuration $Csproj
if ($LASTEXITCODE -ne 0) {
    throw "dotnet build failed with exit code $LASTEXITCODE"
}

if (-not (Test-Path $BuildOut)) {
    throw "Build output not found: $BuildOut"
}

$shouldCopy = $true
if ((Test-Path $PublishDll) -and -not $Force.IsPresent) {
    $a = (Get-FileHash -Algorithm SHA256 $BuildOut).Hash
    $b = (Get-FileHash -Algorithm SHA256 $PublishDll).Hash
    if ($a -eq $b) {
        $shouldCopy = $false
        Write-Host "[weva] generator unchanged, skipping copy."
    }
}

if ($shouldCopy) {
    Copy-Item -Force $BuildOut $PublishDll
    Write-Host "[weva] copied $($BuildOut) -> $($PublishDll)"
}

# Always (re)write the .meta if missing. We do not regenerate it on every
# run because Unity assigns a stable GUID on first import; overwriting
# would break references.
if (-not (Test-Path $PublishMeta)) {
    $metaContent = @'
fileFormatVersion: 2
guid: 6f3b9c8a1d2e4f5a8b9c0d1e2f3a4b5c
PluginImporter:
  externalObjects: {}
  serializedVersion: 2
  iconMap: {}
  executionOrder: {}
  defineConstraints: []
  isPreloaded: 0
  isOverridable: 0
  isExplicitlyReferenced: 1
  validateReferences: 1
  platformData:
  - first:
      '': Any
    second:
      enabled: 0
      settings:
        Exclude Editor: 0
        Exclude Linux64: 0
        Exclude OSXUniversal: 0
        Exclude Win: 0
        Exclude Win64: 0
  - first:
      Any:
    second:
      enabled: 0
      settings: {}
  - first:
      Editor: Editor
    second:
      enabled: 0
      settings:
        DefaultValueInitialized: true
  userData: ''
  assetBundleName: ''
  assetBundleVariant: ''
  labels:
  - RoslynAnalyzer
'@
    Set-Content -Path $PublishMeta -Value $metaContent -Encoding utf8
    Write-Host "[weva] wrote $PublishMeta"
}

Write-Host "[weva] generator publish complete."
