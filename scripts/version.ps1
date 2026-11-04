#Requires -Version 7
<#
.SYNOPSIS
  D86: the version a build carries. It is the push hook's tag on the current commit (v1.4.0 -> "1.4.0"), so the
  installed app, the zip and the GitHub release all say the same number.
  A build of a commit that isn't pushed yet, or of local changes, is "<next minor>-dev" (1.5.0-dev): newer than the last
  release and older than the next, so it never claims a released number.
#>
$repo = Split-Path $PSScriptRoot -Parent

function Releases([string[]] $tags) {
    $tags | Where-Object { $_ -match '^v\d+\.\d+\.\d+$' } | ForEach-Object { [version]$_.TrimStart('v') } | Sort-Object
}

$dirty = git -C $repo status --porcelain -- src assets
$here = Releases @(git -C $repo tag --points-at HEAD) | Select-Object -Last 1
if ($here -and -not $dirty) { return "$here" }

$last = Releases @(git -C $repo tag --merged HEAD) | Select-Object -Last 1
if ($last) { return "$($last.Major).$($last.Minor + 1).0-dev" }

# No tags at all (a fresh clone without them): the project file's version.
$proj = Join-Path $repo 'src/GlobalAutocorrect.App/GlobalAutocorrect.App.csproj'
([xml](Get-Content $proj)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
