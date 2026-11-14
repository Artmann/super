# Checks a Super package the way a game outside this repository uses it. It copies the small game in
# packaging/nuget/smoke to a folder outside the repository, builds it against the .nupkg and runs its tests.
# The game loads cooked content and uses its own shader, so this fails when the package can't cook, has no
# shaders or is missing a library. With -Screenshot the game also draws a frame, which needs a GPU.
param(
    # The folder the .nupkg is in.
    [Parameter(Mandatory)][string]$Packages,
    # The version to check, such as 0.3.0.
    [Parameter(Mandatory)][string]$Version,
    # Where to save a picture of the game. Leave it out on a machine with no GPU.
    [string]$Screenshot
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

$packages = (Resolve-Path $Packages).Path
if (-not (Test-Path (Join-Path $packages "Super.$Version.nupkg"))) {
    throw "There's no Super.$Version.nupkg in $packages."
}

$temp = if ($env:RUNNER_TEMP) { $env:RUNNER_TEMP } else { [IO.Path]::GetTempPath() }
$work = Join-Path $temp "super-package-check"
if (Test-Path $work) {
    Remove-Item $work -Recurse -Force
}
Copy-Item (Join-Path $PSScriptRoot '..\..\packaging\nuget\smoke') $work -Recurse

# NuGet keeps each version it has unpacked and never looks at the .nupkg again, so a package packed twice
# under one version would be checked as it was the first time.
$unpacked = (dotnet nuget locals global-packages --list) -replace '^global-packages:\s*', ''
$stale = Join-Path $unpacked "super\$Version"
if (Test-Path $stale) {
    Remove-Item $stale -Recurse -Force
}

@"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="checked" value="$packages" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
  <packageSourceMapping>
    <packageSource key="checked">
      <package pattern="Super" />
    </packageSource>
    <packageSource key="nuget.org">
      <package pattern="*" />
    </packageSource>
  </packageSourceMapping>
</configuration>
"@ | Set-Content (Join-Path $work 'nuget.config')

$tests = Join-Path $work 'Spinner.Tests\Spinner.Tests.csproj'
dotnet build $tests -c Release "-p:SuperVersion=$Version"

# What the package has to put beside a game, and beside its tests.
foreach ($project in 'Spinner', 'Spinner.Tests') {
    $out = Join-Path $work "$project\bin\Release\net10.0"
    foreach ($file in 'shaders\gbuffer.frag.spv', 'shaders\glow.frag.spv', 'content', 'content.dev.json', 'Super.dll', 'Rendition.dll') {
        if (-not (Test-Path (Join-Path $out $file))) {
            throw "$project has no $file in its output, so the package didn't give it one."
        }
    }
}

dotnet test $tests -c Release --no-build "-p:SuperVersion=$Version"

if ($Screenshot) {
    $shot = [IO.Path]::GetFullPath($Screenshot)
    & (Join-Path $work 'Spinner\bin\Release\net10.0\Spinner.exe') --seed 1 --ticks 30 --screenshot $shot
    if (-not (Test-Path $shot)) {
        throw "The game didn't save $shot."
    }
}

Write-Output "Super $Version works as a package."
