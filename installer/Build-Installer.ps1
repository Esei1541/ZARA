[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d+\.\d+\.\d+(?:\.\d+)?$')]
    [string]$Version,
    [string]$IsccPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$requiredSdk = (Get-Content (Join-Path $repoRoot 'global.json') -Raw | ConvertFrom-Json).sdk.version

function Find-DotNet {
    $candidates = @($env:ZARA_DOTNET)
    if ($env:LOCALAPPDATA) {
        $candidates += Join-Path $env:LOCALAPPDATA 'Zara\dotnet-sdk\dotnet.exe'
    }
    $command = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($command) { $candidates += $command.Source }
    foreach ($candidate in $candidates | Select-Object -Unique) {
        if (-not $candidate -or -not (Test-Path -LiteralPath $candidate -PathType Leaf)) { continue }
        $reportedVersion = & $candidate --version
        if ($LASTEXITCODE -eq 0 -and ($reportedVersion | Select-Object -First 1) -eq $requiredSdk) {
            return $candidate
        }
    }
    throw "The SDK required by global.json ($requiredSdk) is unavailable. Set ZARA_DOTNET."
}

function Find-Iscc {
    if ($IsccPath) {
        if (-not (Test-Path -LiteralPath $IsccPath -PathType Leaf)) { throw "ISCC not found: $IsccPath" }
        return (Resolve-Path -LiteralPath $IsccPath).Path
    }
    $command = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }
    foreach ($base in @(${env:ProgramFiles(x86)}, $env:ProgramFiles)) {
        if (-not $base) { continue }
        $candidate = Join-Path $base 'Inno Setup 6\ISCC.exe'
        if (Test-Path -LiteralPath $candidate -PathType Leaf) { return $candidate }
    }
    throw 'Inno Setup 6 is required. Supply -IsccPath or put ISCC.exe on PATH.'
}

Push-Location $repoRoot
try {
    $dotnet = Find-DotNet
    $compiler = Find-Iscc
    # Each invocation owns a new output tree; never erase another build's artifacts.
    $buildRoot = Join-Path $repoRoot ('artifacts\installer\' + $Version + '-' + [Guid]::NewGuid().ToString('N'))
    $payload = Join-Path $buildRoot 'payload'
    New-Item -ItemType Directory -Path $payload -Force | Out-Null
    foreach ($project in @('Zara.Desktop', 'Zara.Enforcement.Service')) {
        $projectPath = Join-Path $repoRoot "src\$project\$project.csproj"
        $projectOutput = Join-Path $buildRoot $project
        & $dotnet publish $projectPath -c Release -r win-x64 --self-contained true `
            -p:NuGetLockFilePath=obj/installer.packages.lock.json `
            -p:Version=$Version -p:DebugType=None -p:DebugSymbols=false `
            -p:PublishSingleFile=false -p:PublishTrimmed=false -warnaserror -o $projectOutput
        if ($LASTEXITCODE -ne 0) { throw "Publish failed for $project ($LASTEXITCODE)." }
        foreach ($file in Get-ChildItem -LiteralPath $projectOutput -File -Recurse) {
            if ($file.Extension -eq '.pdb') { continue }
            $relative = $file.FullName.Substring($projectOutput.Length).TrimStart('\')
            $destination = Join-Path $payload $relative
            if (Test-Path -LiteralPath $destination) {
                if ((Get-FileHash -LiteralPath $file.FullName).Hash -ne (Get-FileHash -LiteralPath $destination).Hash) {
                    throw "Published projects disagree on shared file: $relative"
                }
                continue
            }
            New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
            Copy-Item -LiteralPath $file.FullName -Destination $destination
        }
    }
    foreach ($required in @('Zara.Desktop.exe', 'Zara.Enforcement.Service.exe', 'coreclr.dll',
            'Presets\gwangyeom-sonata.txt', 'Presets\readymade-life.txt', 'Presets\wings.txt')) {
        if (-not (Test-Path -LiteralPath (Join-Path $payload $required) -PathType Leaf)) {
            throw "Incomplete publish payload: $required"
        }
    }
    $output = Join-Path $buildRoot 'setup'
    & $compiler "/DAppVersion=$Version" "/DPayloadDir=$payload" "/DOutputDir=$output" (Join-Path $PSScriptRoot 'Zara.iss')
    if ($LASTEXITCODE -ne 0) { throw "Installer compilation failed ($LASTEXITCODE)." }
    $installer = Join-Path $output "ZARA-$Version-win-x64-Setup.exe"
    if (-not (Test-Path -LiteralPath $installer -PathType Leaf)) { throw 'ISCC did not create the expected installer.' }
    Write-Output $installer
    Get-FileHash -LiteralPath $installer -Algorithm SHA256
}
finally { Pop-Location }
