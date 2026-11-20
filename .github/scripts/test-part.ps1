# Writes a solution filter holding one part of the test projects, for the build-and-test jobs in ci.yml, which
# run the two parts on two runners at once.
#
# The engine part is every project in tests/ whose name starts with Super. or Rendition., and the examples part is
# every other one. A project is always in exactly one part, so a new test project runs without being listed
# anywhere. A project that isn't in Super.slnx stops the run, since the build wouldn't have built it.
param(
    [Parameter(Mandatory)][ValidateSet('engine', 'examples')][string]$Part,
    [Parameter(Mandatory)][string]$Out,
    [string]$Root = (Join-Path $PSScriptRoot '../..')
)

$ErrorActionPreference = 'Stop'

$solution = Get-Content (Join-Path $Root 'Super.slnx') -Raw
$projects = @()
foreach ($folder in Get-ChildItem (Join-Path $Root 'tests') -Directory | Sort-Object Name) {
    $name = $folder.Name
    if (-not (Test-Path (Join-Path $folder.FullName "$name.csproj"))) {
        continue
    }

    $path = "tests/$name/$name.csproj"
    if (-not $solution.Contains("`"$path`"")) {
        throw "$path isn't in Super.slnx, so the build skips it and its tests would never run. Add it to the solution."
    }

    $isEngine = $name -match '^(Super|Rendition)\.'
    if ($isEngine -eq ($Part -eq 'engine')) {
        $projects += $path
    }
}

if ($projects.Count -eq 0) {
    throw "The $Part part has no test projects."
}

# The paths in a solution filter count from the filter's own folder.
$outFolder = Split-Path -Parent ([IO.Path]::GetFullPath($Out))
$solutionPath = [IO.Path]::GetRelativePath($outFolder, (Join-Path $Root 'Super.slnx')).Replace('\', '/')
$relative = $projects | ForEach-Object { [IO.Path]::GetRelativePath($outFolder, (Join-Path $Root $_)).Replace('\', '/') }
@{ solution = @{ path = $solutionPath; projects = @($relative) } } | ConvertTo-Json -Depth 4 | Set-Content $Out

Write-Output "The $Part part runs $($projects.Count) test projects:"
$projects
