# List-ExplorerColumns.ps1
#
# Dumps every built-in "Details" column name that Windows Explorer knows about
# on this PC, so you can cross-reference against SolidWorks custom property
# names to see which ones might "collide" the way "Description" does.
#
# Safe to run: read-only, no admin rights, nothing installed or changed.
#
# USAGE:
#   1. Right-click this file -> Run with PowerShell
#      (or open PowerShell and run:  .\List-ExplorerColumns.ps1)
#   2. If Windows blocks it as an unsigned script, run this once in an elevated
#      PowerShell window, then try again:
#        Set-ExecutionPolicy -Scope CurrentUser RemoteSigned
#   3. It writes ExplorerColumns.csv next to this script.
#
# HOW IT WORKS:
#   The Shell.Application COM object can report the *name* of any detail
#   column by index (0, 1, 2, ...) for a given folder view. There's no single
#   API call that returns "the whole list" directly, so this just walks a wide
#   range of indexes and keeps whichever ones come back with a real name.
#   Column 0-9 or so are always the well-known Name/Size/Type/Date-modified
#   ones; everything past that is the long tail we care about (Description,
#   Comments, Company, Category, Subject, etc.).

$shell = New-Object -ComObject Shell.Application

# Any real folder works as the "context" for asking column names -
# your Documents folder is a safe, always-present choice.
$folder = $shell.NameSpace([Environment]::GetFolderPath('MyDocuments'))

$results = New-Object System.Collections.Generic.List[object]

for ($i = 0; $i -lt 400; $i++) {
    $name = $folder.GetDetailsOf($folder.Items, $i)
    if ([string]::IsNullOrWhiteSpace($name)) { continue }
    $results.Add([PSCustomObject]@{ Index = $i; ColumnName = $name })
}

$outFile = Join-Path $PSScriptRoot 'ExplorerColumns.csv'
$results | Export-Csv -Path $outFile -NoTypeInformation -Encoding UTF8

Write-Host "Found $($results.Count) named columns out of 400 indexes checked."
Write-Host "Saved to: $outFile"
Write-Host ""
Write-Host "Open that CSV and look for plain-English names that could plausibly"
Write-Host "match a SolidWorks custom property name you'd actually use, e.g."
Write-Host "'Status', 'Owner', 'Rating', 'Location', 'Manager'. Set a SolidWorks"
Write-Host "custom property with that exact name and see if the matching Explorer"
Write-Host "column picks it up."
