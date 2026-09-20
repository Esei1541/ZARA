[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('Prepare', 'Start', 'Complete', 'Rollback', 'Uninstall')]
    [string]$Action,
    [Parameter(Mandatory = $true)]
    [string]$InstallDirectory,
    [ValidatePattern('^[0-9a-fA-F]{64}$')]
    [string]$TransactionId
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$serviceName = 'ZARA.Enforcement'
$target = [IO.Path]::GetFullPath($InstallDirectory).TrimEnd('\')
$expectedTarget = Join-Path ([Environment]::GetFolderPath('ProgramFiles')) 'ZARA'
$backupRoot = Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) 'ZARA\InstallerBackup'
$statePath = Join-Path $backupRoot 'state.json'
$serviceExe = Join-Path $target 'Zara.Enforcement.Service.exe'
$desktopExe = Join-Path $target 'Zara.Desktop.exe'

function Assert-NoReparsePoint([string]$Path) {
    $current = [IO.Path]::GetFullPath($Path)
    while ($current) {
        if (Test-Path -LiteralPath $current) {
            $item = Get-Item -LiteralPath $current -Force
            if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) {
                throw "A linked installation or backup path is not allowed: $current"
            }
        }
        $current = Split-Path -Parent $current
    }
}

function Get-InstallationService {
    $service = Get-CimInstance Win32_Service -Filter "Name='$serviceName'"
    if ($null -eq $service) { return $null }
    $imagePath = $service.PathName.Trim()
    if ($imagePath -ne ('"' + $serviceExe + '"') -and $imagePath -ne $serviceExe) {
        throw "The service belongs to another executable: $imagePath"
    }
    if ($service.StartName -notin @('LocalSystem', 'NT AUTHORITY\SYSTEM') -or $service.StartMode -ne 'Auto') {
        throw 'The existing service account or startup mode differs from this installation.'
    }
    return $service
}

function Invoke-ServiceCommand([string[]]$Arguments) {
    & "$env:SystemRoot\System32\sc.exe" @Arguments | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "Service operation failed ($LASTEXITCODE): $($Arguments[0])" }
}

function Remove-InstallationService {
    if ($null -eq (Get-InstallationService)) { return }
    Invoke-ServiceCommand @('delete', $serviceName)
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    while ($null -ne (Get-CimInstance Win32_Service -Filter "Name='$serviceName'")) {
        if ([DateTime]::UtcNow -ge $deadline) { throw 'Windows has not finished removing the ZARA service.' }
        Start-Sleep -Milliseconds 250
    }
}

function Assert-RecoveryComplete {
    $root = 'HKLM:\SOFTWARE\ZARA\Recovery\TaskManagerRestriction'
    if (-not (Test-Path -LiteralPath $root)) { return }
    foreach ($key in Get-ChildItem -LiteralPath $root) {
        $record = $key.GetValue('Record')
        if ($null -eq $record) { continue }
        $data = $record | ConvertFrom-Json
        if (-not $data.InstallDirectory) { throw 'A Task Manager recovery record could not be identified.' }
        if ([IO.Path]::GetFullPath($data.InstallDirectory).TrimEnd('\') -eq $target) {
            throw 'ZARA has not completed Task Manager policy recovery. Program files were not released for replacement.'
        }
    }
}

function Stop-InstallationService([switch]$ForRollback) {
    $service = Get-InstallationService
    if ($null -ne $service -and $service.State -ne 'Stopped') {
        $controller = Get-Service -Name $serviceName
        try {
            if ($service.State -ne 'Stop Pending') { $controller.Stop() }
            $controller.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(45))
        }
        finally { $controller.Dispose() }
        $service = Get-InstallationService
    }
    if ($null -ne $service -and ($service.State -ne 'Stopped' -or
            (-not $ForRollback -and ($service.ExitCode -ne 0 -or $service.ServiceSpecificExitCode -ne 0)))) {
        throw 'The ZARA service did not stop cleanly. File replacement or removal is blocked.'
    }
    Assert-RecoveryComplete
}

function Stop-InstalledDesktop {
    foreach ($process in @(Get-Process -Name 'Zara.Desktop' -ErrorAction SilentlyContinue)) {
        try {
            # Force a handle for this exact process before inspecting and terminating it.
            $null = $process.Handle
            if (-not $process.Path) { throw 'A Desktop executable path could not be verified.' }
            if ([IO.Path]::GetFullPath($process.Path) -ne $desktopExe) { continue }
            $process.Kill()
            if (-not $process.WaitForExit(15000)) { throw 'The installed Desktop did not exit.' }
        }
        catch [InvalidOperationException] {
            if (-not $process.HasExited) { throw }
        }
        finally { $process.Dispose() }
    }
}

function Start-InstallationService {
    $service = Get-InstallationService
    if ($null -eq $service) { throw 'The installation service is missing.' }
    Start-Service -Name $serviceName -ErrorAction Stop
    $controller = Get-Service -Name $serviceName
    try { $controller.WaitForStatus('Running', [TimeSpan]::FromSeconds(30)) }
    finally { $controller.Dispose() }
    # SCM can report Running before the service creates its first generation.
    Start-Sleep -Seconds 2
    $service = Get-InstallationService
    if ($service.State -ne 'Running' -or $service.ExitCode -ne 0) { throw 'The installed service failed during startup.' }
}

function Save-State($State) {
    $State | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $statePath -Encoding UTF8
}

function Read-State {
    $state = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
    if ($state.InstallDirectory -ne $target -or $state.TransactionId -ne $TransactionId) {
        throw 'The backup belongs to a different installation transaction.'
    }
    return $state
}

function Remove-Backup {
    # This is the only recursive deletion. Require the fixed, non-linked backup directory and a matching journal.
    Assert-NoReparsePoint $backupRoot
    $null = Read-State
    foreach ($item in Get-ChildItem -LiteralPath $backupRoot -Force -Recurse) {
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'The backup contains a linked entry.' }
    }
    Remove-Item -LiteralPath $backupRoot -Recurse -Force
}

try {
    if (-not [Environment]::Is64BitProcess -or $target -ne $expectedTarget) {
        throw 'Installation requires 64-bit Windows PowerShell and the fixed Program Files\ZARA directory.'
    }
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Administrator permission is required.'
    }
    Assert-NoReparsePoint $target
    Assert-NoReparsePoint $backupRoot
    if ($Action -ne 'Uninstall' -and -not $TransactionId) { throw 'An installation transaction identity is required.' }

    switch ($Action) {
        'Prepare' {
            if (Test-Path -LiteralPath $backupRoot) { throw "An earlier installation backup needs recovery: $backupRoot" }
            $service = Get-InstallationService
            $files = @()
            if (Test-Path -LiteralPath $target) {
                $items = @(Get-ChildItem -LiteralPath $target -Force -Recurse)
                foreach ($item in $items) {
                    if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'The installation contains a linked entry.' }
                }
                $files = @($items | Where-Object { -not $_.PSIsContainer })
                if ($files.Count -gt 0 -and (-not (Test-Path -LiteralPath $desktopExe) -or -not (Test-Path -LiteralPath $serviceExe))) {
                    throw 'The target folder could not be identified as a ZARA installation.'
                }
            }
            New-Item -ItemType Directory -Path $backupRoot -Force | Out-Null
            & "$env:SystemRoot\System32\icacls.exe" $backupRoot /inheritance:r /grant:r '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' | Out-Host
            if ($LASTEXITCODE -ne 0) { throw 'Could not protect the installation backup.' }
            $state = [pscustomobject]@{
                InstallDirectory = $target
                TransactionId = $TransactionId
                HadService = ($null -ne $service)
                WasRunning = ($null -ne $service -and $service.State -ne 'Stopped')
                Prepared = $false
                Files = @($files | ForEach-Object { $_.FullName.Substring($target.Length).TrimStart('\') })
            }
            # Publish ownership before copying so a failed backup can be removed by this transaction only.
            Save-State $state
            foreach ($file in $files) {
                $relative = $file.FullName.Substring($target.Length).TrimStart('\')
                $destination = Join-Path (Join-Path $backupRoot 'files') $relative
                New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
                Copy-Item -LiteralPath $file.FullName -Destination $destination
            }
            Stop-InstallationService
            Stop-InstalledDesktop
            $state.Prepared = $true
            Save-State $state
            # These are the known legacy symbols, not a wildcard over user-owned files.
            foreach ($assembly in @('Zara.Core', 'Zara.Application', 'Zara.Supervision.Contracts',
                    'Zara.Infrastructure.Windows', 'Zara.Desktop', 'Zara.Enforcement.Service')) {
                $symbols = Join-Path $target ($assembly + '.pdb')
                if (Test-Path -LiteralPath $symbols -PathType Leaf) { Remove-Item -LiteralPath $symbols -Force }
            }
        }
        'Start' {
            $state = Read-State
            if (-not $state.Prepared) { throw 'Installation was not prepared.' }
            $service = Get-InstallationService
            if ($null -eq $service) {
                Invoke-ServiceCommand @('create', $serviceName, 'binPath=', ('"' + $serviceExe + '"'), 'start=', 'auto', 'obj=', 'LocalSystem', 'DisplayName=', 'ZARA Enforcement')
                Invoke-ServiceCommand @('failure', $serviceName, 'reset=', '0', 'actions=', 'restart/1000/restart/5000/restart/30000')
                Invoke-ServiceCommand @('failureflag', $serviceName, '1')
            }
            Start-InstallationService
        }
        'Complete' { Remove-Backup }
        'Rollback' {
            if (-not (Test-Path -LiteralPath $statePath)) { return }
            $state = Read-State
            if ($state.Prepared) {
                # A failed new executable may have a nonzero SCM exit code; its recovery journal
                # must still be clear before any original program file is restored.
                Stop-InstallationService -ForRollback
                Stop-InstalledDesktop
                if (-not $state.HadService -and $null -ne (Get-InstallationService)) {
                    Remove-InstallationService
                }
                foreach ($relative in $state.Files) {
                    $destination = [IO.Path]::GetFullPath((Join-Path $target $relative))
                    if (-not $destination.StartsWith($target + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid backup file path.' }
                    $source = Join-Path (Join-Path $backupRoot 'files') $relative
                    New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
                    Copy-Item -LiteralPath $source -Destination $destination -Force
                }
            }
            if ($state.HadService -and $state.WasRunning) { Start-InstallationService }
            Remove-Backup
        }
        'Uninstall' {
            if (Test-Path -LiteralPath $backupRoot) { throw 'An unfinished installation must be recovered before uninstalling.' }
            Stop-InstallationService
            Stop-InstalledDesktop
            Remove-InstallationService
        }
    }
    exit 0
}
catch {
    [Console]::Error.WriteLine($_.Exception.Message)
    exit 1
}
