# Writes the names of the cook cache's entries (records, objects and step results), sorted, to a file.
# CI lists them before and after the build to tell whether the cook added anything worth saving.
param(
    [Parameter(Mandatory)][string]$Cache,
    [Parameter(Mandatory)][string]$Out
)

$names = foreach ($part in 'records', 'objects', 'steps') {
    $folder = Join-Path $Cache $part
    if (Test-Path $folder) {
        Get-ChildItem $folder -Recurse -File | ForEach-Object { $_.FullName.Substring($Cache.Length) }
    }
}

[IO.File]::WriteAllLines($Out, [string[]]@($names | Sort-Object))
Write-Output "$(@($names).Count) cook cache entries."
