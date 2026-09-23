Set-StrictMode -Version Latest

function Invoke-ZaraGit {
    param(
        [Parameter(Mandatory = $true)][string]$WorktreeRoot,
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [switch]$AllowEmpty
    )

    $originalOutputEncoding = [Console]::OutputEncoding
    try {
        [Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
        $output = @(& git -C $WorktreeRoot @Arguments 2>&1)
    }
    finally {
        [Console]::OutputEncoding = $originalOutputEncoding
    }
    if ($LASTEXITCODE -ne 0) {
        throw "Git failed ($LASTEXITCODE): git $($Arguments -join ' ')`n$($output -join [Environment]::NewLine)"
    }
    $text = ($output | ForEach-Object { $_.ToString() }) -join [Environment]::NewLine
    if (-not $AllowEmpty -and [string]::IsNullOrWhiteSpace($text)) {
        throw "Git returned no value: git $($Arguments -join ' ')"
    }
    return $text.Trim()
}

function Get-ZaraLocalBuildGitIdentity {
    param([Parameter(Mandatory = $true)][string]$WorktreeRoot)

    $resolvedWorktree = (Resolve-Path -LiteralPath $WorktreeRoot).Path
    $commonDirectoryValue = Invoke-ZaraGit $resolvedWorktree @('rev-parse', '--git-common-dir')
    $commonDirectory = if ([IO.Path]::IsPathRooted($commonDirectoryValue)) {
        [IO.Path]::GetFullPath($commonDirectoryValue)
    }
    else {
        [IO.Path]::GetFullPath((Join-Path $resolvedWorktree $commonDirectoryValue))
    }
    if (-not (Test-Path -LiteralPath $commonDirectory -PathType Container)) {
        throw "Git common directory does not exist: $commonDirectory"
    }

    $commit = Invoke-ZaraGit $resolvedWorktree @('rev-parse', 'HEAD')
    if ($commit -notmatch '^[0-9a-fA-F]{40}$') {
        throw "Git returned an invalid commit: $commit"
    }
    $branch = Invoke-ZaraGit $resolvedWorktree @('branch', '--show-current') -AllowEmpty
    $subject = Invoke-ZaraGit $resolvedWorktree @('-c', 'i18n.logOutputEncoding=utf-8', 'log', '-1', '--format=%s', 'HEAD')
    if ([string]::IsNullOrWhiteSpace($branch)) {
        $branch = $commit.Substring(0, 12).ToLowerInvariant()
    }
    $sourceChanges = Invoke-ZaraGit $resolvedWorktree @('status', '--porcelain', '--untracked-files=normal') -AllowEmpty

    [pscustomobject]@{
        WorktreeRoot = $resolvedWorktree
        RepositoryRoot = [IO.Path]::GetFullPath((Split-Path -Parent $commonDirectory))
        GitCommonDirectory = $commonDirectory
        Branch = $branch
        Commit = $commit.ToLowerInvariant()
        CommitSubject = $subject
        IsClean = [string]::IsNullOrWhiteSpace($sourceChanges)
    }
}

function Get-ZaraLocalBuildIdBase {
    param(
        [Parameter(Mandatory = $true)][DateTimeOffset]$CreatedAt,
        [Parameter(Mandatory = $true)][string]$VersionName,
        [Parameter(Mandatory = $true)][string]$Branch
    )

    if ($VersionName -notmatch '^\d+\.\d+\.\d+(?:\.\d+)?$') {
        throw "Invalid numeric product version: $VersionName"
    }
    $branchPart = [regex]::Replace($Branch, '^\d{6}-', '')
    $branchPart = [regex]::Replace($branchPart, '[^\p{L}\p{Nd}._-]+', '-')
    if ([string]::IsNullOrWhiteSpace($branchPart)) {
        throw "The branch does not produce a usable build identity: $Branch"
    }
    return '{0}-{1}-{2}' -f $CreatedAt.ToString('yyMMdd-HHmmss'), $VersionName, $branchPart
}

function New-ZaraLocalBuildDirectory {
    param(
        [Parameter(Mandatory = $true)][string]$BuildsDirectory,
        [Parameter(Mandatory = $true)][string]$BuildIdBase
    )

    $root = [IO.Path]::GetFullPath($BuildsDirectory)
    New-Item -ItemType Directory -Path $root -Force | Out-Null
    for ($suffix = 0; ; $suffix++) {
        $buildId = if ($suffix -eq 0) { $BuildIdBase } else { '{0}-{1:D2}' -f $BuildIdBase, $suffix }
        $path = Join-Path $root $buildId
        try {
            $directory = New-Item -ItemType Directory -Path $path -ErrorAction Stop
            return [pscustomobject]@{ BuildId = $buildId; Path = $directory.FullName }
        }
        catch [IO.IOException] {
            if (-not (Test-Path -LiteralPath $path -PathType Container)) { throw }
        }
    }
}

function Write-ZaraJsonFile {
    param(
        [Parameter(Mandatory = $true)]$Value,
        [Parameter(Mandatory = $true)][string]$Path
    )

    $parent = Split-Path -Parent $Path
    New-Item -ItemType Directory -Path $parent -Force | Out-Null
    $temporary = Join-Path $parent ('.' + [IO.Path]::GetFileName($Path) + '.' + [Guid]::NewGuid().ToString('N') + '.tmp')
    try {
        $json = $Value | ConvertTo-Json -Depth 5
        [IO.File]::WriteAllText($temporary, $json + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
        Move-Item -LiteralPath $temporary -Destination $Path -ErrorAction Stop
    }
    finally {
        if (Test-Path -LiteralPath $temporary -PathType Leaf) {
            Remove-Item -LiteralPath $temporary -Force
        }
    }
}

function Publish-ZaraLocalBuildManifest {
    param(
        [Parameter(Mandatory = $true)][string]$BuildDirectory,
        [Parameter(Mandatory = $true)][string]$BuildId,
        [Parameter(Mandatory = $true)][string]$VersionName,
        [Parameter(Mandatory = $true)][ValidateSet('Debug', 'Staging')][string]$Configuration,
        [Parameter(Mandatory = $true)][string]$CreatedAt,
        [Parameter(Mandatory = $true)][string]$Branch,
        [Parameter(Mandatory = $true)][ValidatePattern('^[0-9a-fA-F]{40}$')][string]$Commit,
        [string]$CommitSubject
    )

    $installerFileName = $BuildId + '.exe'
    $installerPath = Join-Path $BuildDirectory $installerFileName
    if (-not (Test-Path -LiteralPath $installerPath -PathType Leaf)) {
        throw "The installer was not created: $installerPath"
    }
    $hash = (Get-FileHash -LiteralPath $installerPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $manifest = [ordered]@{
        schemaVersion = 1
        buildId = $BuildId
        versionName = $VersionName
        configuration = $Configuration
        createdAt = $CreatedAt
        branch = $Branch
        commit = $Commit.ToLowerInvariant()
        commitSubject = $CommitSubject
        installerFileName = $installerFileName
        installerSha256 = $hash
    }
    Write-ZaraJsonFile $manifest (Join-Path $BuildDirectory 'build.json')
    return $installerPath
}
