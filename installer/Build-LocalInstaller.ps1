[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Staging')]
    [string]$Configuration = 'Staging',
    [string]$BuildsDirectory,
    [string]$IsccPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'LocalBuild.Common.ps1')

function Get-ZaraProductVersion {
    param([Parameter(Mandatory = $true)][string]$WorktreeRoot)

    [xml]$props = Get-Content -LiteralPath (Join-Path $WorktreeRoot 'Directory.Build.props') -Raw
    $versionNodes = @($props.SelectNodes('/Project/PropertyGroup/Version'))
    if ($versionNodes.Count -ne 1 -or [string]$versionNodes[0].InnerText -notmatch '^\d+\.\d+\.\d+(?:\.\d+)?$') {
        throw 'Directory.Build.props must contain one numeric Version.'
    }
    return [string]$versionNodes[0].InnerText
}

function Invoke-ZaraInstallerPackaging {
    param(
        [Parameter(Mandatory = $true)][string]$VersionName,
        [Parameter(Mandatory = $true)][string]$ConfigurationName,
        [Parameter(Mandatory = $true)][string]$DestinationDirectory,
        [Parameter(Mandatory = $true)][string]$BaseFileName,
        [Parameter(Mandatory = $true)][System.Collections.IDictionary]$Identity,
        [string]$CompilerPath
    )

    $parameters = @{
        Version = $VersionName
        Configuration = $ConfigurationName
        OutputDirectory = $DestinationDirectory
        OutputBaseFileName = $BaseFileName
        LocalBuildIdentity = $Identity
    }
    if ($CompilerPath) { $parameters.IsccPath = $CompilerPath }
    & (Join-Path $PSScriptRoot 'Build-Installer.ps1') @parameters | Out-Host
}

function Invoke-BuildLocalInstaller {
    $worktreeRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
    $initialIdentity = Get-ZaraLocalBuildGitIdentity $worktreeRoot
    if (-not $initialIdentity.IsClean) {
        throw 'All nonignored source changes must be committed before creating a local installer.'
    }

    $versionName = Get-ZaraProductVersion $worktreeRoot
    $resolvedBuildsDirectory = if ($BuildsDirectory) {
        [IO.Path]::GetFullPath($BuildsDirectory)
    }
    else {
        Join-Path $initialIdentity.RepositoryRoot 'artifacts\local-builds'
    }
    $createdAt = [DateTimeOffset]::Now
    $buildIdBase = Get-ZaraLocalBuildIdBase $createdAt $versionName $initialIdentity.Branch
    $reservation = New-ZaraLocalBuildDirectory $resolvedBuildsDirectory $buildIdBase
    $localIdentity = [ordered]@{
        schemaVersion = 1
        buildId = $reservation.BuildId
        versionName = $versionName
        configuration = $Configuration
        createdAt = $createdAt.ToString('o')
        branch = $initialIdentity.Branch
        commit = $initialIdentity.Commit
        buildsDirectory = [IO.Path]::GetFullPath($resolvedBuildsDirectory)
    }

    Invoke-ZaraInstallerPackaging -VersionName $versionName -ConfigurationName $Configuration `
        -DestinationDirectory $reservation.Path -BaseFileName $reservation.BuildId `
        -Identity $localIdentity -CompilerPath $IsccPath

    $finalIdentity = Get-ZaraLocalBuildGitIdentity $worktreeRoot
    if (-not $finalIdentity.IsClean -or $finalIdentity.Commit -ne $initialIdentity.Commit) {
        throw 'Source or HEAD changed while the installer was being created. build.json was not published.'
    }

    return Publish-ZaraLocalBuildManifest -BuildDirectory $reservation.Path -BuildId $reservation.BuildId `
        -VersionName $versionName -Configuration $Configuration -CreatedAt $createdAt.ToString('o') `
        -Branch $initialIdentity.Branch -Commit $initialIdentity.Commit
}

if ($MyInvocation.InvocationName -ne '.') {
    Invoke-BuildLocalInstaller
}
