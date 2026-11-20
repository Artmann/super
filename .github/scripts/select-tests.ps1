# Picks the test projects closest to the files a pull request changed, for the quick job in ci.yml.
# This only chooses what to run first. The build-and-test jobs still run every test on every pull request.
#
# A file in tests/X runs X. A file in examples/X runs tests/X.Tests, and a file in src/X runs tests/X.Tests.
# Source with no test project of its own (Rendition, Super.Ecs, Super.Testing, Super.Build) runs Super.Tests,
# and so do the files every project builds with. The cook libraries and the cook tool run Super.Cook.Tests.
# Docs, the workflows and anything else run nothing here.
#
# Reads the changed paths from standard input, one a line, and writes the chosen project folders, one a line.
param(
    [string]$Root = (Join-Path $PSScriptRoot '../..')
)

$everyProject = @('Directory.Build.props', 'global.json', 'Super.slnx', 'src/Directory.Build.props', 'src/Shaders.targets')

function Find-TestProject([string]$name) {
    return Test-Path (Join-Path $Root "tests/$name/$name.csproj")
}

$chosen = [System.Collections.Generic.SortedSet[string]]::new([StringComparer]::Ordinal)

foreach ($line in @($input)) {
    $path = "$line".Trim().Replace('\', '/')
    if ($path -eq '') {
        continue
    }

    if ($everyProject -contains $path) {
        [void]$chosen.Add('tests/Super.Tests')
    }
    elseif ($path -match '^tests/([^/]+)/') {
        # A deleted test project has nothing left to run.
        if (Find-TestProject $Matches[1]) {
            [void]$chosen.Add("tests/$($Matches[1])")
        }
    }
    elseif ($path -match '^examples/([^/]+)/') {
        if (Find-TestProject "$($Matches[1]).Tests") {
            [void]$chosen.Add("tests/$($Matches[1]).Tests")
        }
    }
    elseif ($path -match '^src/Super\.Cook[^/]*/') {
        [void]$chosen.Add('tests/Super.Cook.Tests')
    }
    elseif ($path -match '^src/([^/]+)/') {
        if (Find-TestProject "$($Matches[1]).Tests") {
            [void]$chosen.Add("tests/$($Matches[1]).Tests")
        }
        else {
            [void]$chosen.Add('tests/Super.Tests')
        }
    }
    elseif ($path -match '^tools/') {
        [void]$chosen.Add('tests/Super.Cook.Tests')
    }
}

$chosen
