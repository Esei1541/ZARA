$ErrorActionPreference = 'Stop'

$script:manageScriptPath = (Resolve-Path (Join-Path $PSScriptRoot '..\installer\Manage-Installation.ps1')).Path

function New-TestAccessRule {
    param(
        [Parameter(Mandatory = $true)][string]$Sid,
        [Parameter(Mandatory = $true)][System.Security.AccessControl.FileSystemRights]$Rights,
        [System.Security.AccessControl.PropagationFlags]$PropagationFlags = [System.Security.AccessControl.PropagationFlags]::None
    )

    $identity = [System.Security.Principal.SecurityIdentifier]::new($Sid)
    $inheritanceFlags = [System.Security.AccessControl.InheritanceFlags]::ContainerInherit -bor
        [System.Security.AccessControl.InheritanceFlags]::ObjectInherit
    return [System.Security.AccessControl.FileSystemAccessRule]::new(
        $identity,
        $Rights,
        $inheritanceFlags,
        $PropagationFlags,
        [System.Security.AccessControl.AccessControlType]::Allow)
}

function New-TestAcl {
    param(
        [Parameter(Mandatory = $true)][string]$OwnerSid,
        [System.Security.AccessControl.FileSystemAccessRule[]]$Rules = @()
    )

    $acl = [System.Security.AccessControl.DirectorySecurity]::new()
    $acl.SetOwner([System.Security.Principal.SecurityIdentifier]::new($OwnerSid))
    foreach ($rule in @($Rules)) {
        $acl.AddAccessRule($rule)
    }
    return $acl
}

function Set-TestManagementContext {
    param([Parameter(Mandatory = $true)][string]$Root)

    $script:target = [System.IO.Path]::GetFullPath((Join-Path (Join-Path $Root 'missing-parent') 'ZARA'))
    $script:backupRoot = Join-Path $Root 'backup'
    $script:statePath = Join-Path $script:backupRoot 'state.json'
    $script:serviceName = 'ZARA.Enforcement'
    $script:serviceExe = Join-Path $script:target 'Zara.Enforcement.Service.exe'
    $script:desktopExe = Join-Path $script:target 'Zara.Desktop.exe'
    $script:TransactionId = 'a' * 64
}

Describe 'Manage-Installation.ps1 functions' {
    BeforeAll {
        $tokens = $null
        $parserErrors = $null
        $script:manageAst = [System.Management.Automation.Language.Parser]::ParseFile(
            $script:manageScriptPath,
            [ref]$tokens,
            [ref]$parserErrors)
        $script:parserErrors = @($parserErrors)
        $script:functionAsts = @($script:manageAst.FindAll({
                param($node)
                $node -is [System.Management.Automation.Language.FunctionDefinitionAst]
            }, $true))
        $script:functionOnlyPath = Join-Path $TestDrive 'Manage-Installation.functions.ps1'

        if ($script:parserErrors.Count -eq 0) {
            $functionText = ($script:functionAsts | ForEach-Object { $_.Extent.Text }) -join [Environment]::NewLine
            Set-Content -LiteralPath $script:functionOnlyPath -Value $functionText -Encoding UTF8
            . $script:functionOnlyPath
        }
    }

    BeforeEach {
        Set-TestManagementContext -Root $TestDrive
    }

    It 'has no PowerShell parser errors and exposes function definitions' {
        $script:parserErrors.Count | Should Be 0
        $script:functionAsts.Count | Should BeGreaterThan 0
        Test-Path -LiteralPath $script:functionOnlyPath -PathType Leaf | Should Be $true
    }

    Context 'Get-InstallationTarget' {
        It 'accepts local user paths with spaces and Korean characters and normalizes the trailing separator' {
            Get-InstallationTarget 'C:\ZARA' | Should Be 'C:\ZARA'
            $korean = [string]::Concat([char]0xD55C, [char]0xAD6D, [char]0xC5B4)
            $path = 'C:\Users\Test User\' + $korean + ' ZARA\'
            Get-InstallationTarget $path |
                Should Be ('C:\Users\Test User\' + $korean + ' ZARA')
        }

        It 'rejects relative, UNC, root, and backup-overlapping paths' {
            { Get-InstallationTarget 'ZARA' } | Should Throw
            { Get-InstallationTarget '\\server\share\ZARA' } | Should Throw
            { Get-InstallationTarget 'C:\' } | Should Throw
            { Get-InstallationTarget $script:backupRoot } | Should Throw
            { Get-InstallationTarget (Join-Path $script:backupRoot 'child') } | Should Throw
            { Get-InstallationTarget (Split-Path -Parent $script:backupRoot) } | Should Throw
        }
    }

    Context 'Assert-ProtectedDirectory' {
        It 'accepts System and Administrators ownership with their full control and Users read-execute' {
            $rules = @(
                (New-TestAccessRule 'S-1-5-18' ([System.Security.AccessControl.FileSystemRights]::FullControl)),
                (New-TestAccessRule 'S-1-5-32-544' ([System.Security.AccessControl.FileSystemRights]::FullControl)),
                (New-TestAccessRule 'S-1-5-32-545' ([System.Security.AccessControl.FileSystemRights]::ReadAndExecute))
            )
            $script:mockAcl = New-TestAcl 'S-1-5-32-544' $rules
            Mock Get-Acl { $script:mockAcl }

            { Assert-ProtectedDirectory $script:target -InstallationRoot } | Should Not Throw
        }

        It 'accepts TrustedInstaller ownership' {
            $trustedInstaller = 'S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464'
            $script:mockAcl = New-TestAcl $trustedInstaller
            Mock Get-Acl { $script:mockAcl }

            { Assert-ProtectedDirectory $script:target } | Should Not Throw
        }

        It 'rejects an ordinary user as owner' {
            $script:mockAcl = New-TestAcl 'S-1-5-21-1000-1000-1000-1001'
            Mock Get-Acl { $script:mockAcl }

            { Assert-ProtectedDirectory $script:target } | Should Throw
        }

        It 'rejects Users write access on the installation root' {
            $rules = @(New-TestAccessRule 'S-1-5-32-545' ([System.Security.AccessControl.FileSystemRights]::Write))
            $script:mockAcl = New-TestAcl 'S-1-5-32-544' $rules
            Mock Get-Acl { $script:mockAcl }

            { Assert-ProtectedDirectory $script:target -InstallationRoot } | Should Throw
        }

        It 'rejects Users DeleteSubdirectoriesAndFiles access on an ancestor' {
            $rules = @(New-TestAccessRule 'S-1-5-32-545' ([System.Security.AccessControl.FileSystemRights]::DeleteSubdirectoriesAndFiles))
            $script:mockAcl = New-TestAcl 'S-1-5-32-544' $rules
            Mock Get-Acl { $script:mockAcl }

            { Assert-ProtectedDirectory (Split-Path -Parent $script:target) } | Should Throw
        }

        It 'rejects Users Delete access on a non-root ancestor' {
            $rules = @(New-TestAccessRule 'S-1-5-32-545' ([System.Security.AccessControl.FileSystemRights]::Delete))
            $script:mockAcl = New-TestAcl 'S-1-5-32-544' $rules
            Mock Get-Acl { $script:mockAcl }

            { Assert-ProtectedDirectory (Split-Path -Parent $script:target) } | Should Throw
        }

        It 'accepts root Users Modify access when DeleteSubdirectoriesAndFiles is absent' {
            $rights = [System.Security.AccessControl.FileSystemRights](([int][System.Security.AccessControl.FileSystemRights]::Modify) -band
                (-bnot [int][System.Security.AccessControl.FileSystemRights]::DeleteSubdirectoriesAndFiles))
            $rules = @(New-TestAccessRule 'S-1-5-32-545' $rights)
            $script:mockAcl = New-TestAcl 'S-1-5-32-544' $rules
            Mock Get-Acl { $script:mockAcl }

            { Assert-ProtectedDirectory 'C:\' } | Should Not Throw
        }

        It 'ignores InheritOnly Users access when checking an ancestor directory' {
            $rules = @(
                (New-TestAccessRule 'S-1-5-32-545' ([System.Security.AccessControl.FileSystemRights]::FullControl) `
                    -PropagationFlags ([System.Security.AccessControl.PropagationFlags]::InheritOnly))
            )
            $script:mockAcl = New-TestAcl 'S-1-5-32-544' $rules
            Mock Get-Acl { $script:mockAcl }

            { Assert-ProtectedDirectory (Split-Path -Parent $script:target) } | Should Not Throw
        }

        It 'rejects InheritOnly Users access on the installation root because it reaches child files' {
            $rules = @(
                (New-TestAccessRule 'S-1-5-32-545' ([System.Security.AccessControl.FileSystemRights]::FullControl) `
                    -PropagationFlags ([System.Security.AccessControl.PropagationFlags]::InheritOnly))
            )
            $script:mockAcl = New-TestAcl 'S-1-5-32-544' $rules
            Mock Get-Acl { $script:mockAcl }

            { Assert-ProtectedDirectory $script:target -InstallationRoot } | Should Throw
        }
    }

    Context 'Get-InstallationService' {
        It 'accepts a service whose quoted image path matches the selected target' {
            $service = [pscustomobject]@{
                PathName = '"' + $script:serviceExe + '"'
                StartName = 'LocalSystem'
                StartMode = 'Auto'
            }
            Mock Get-CimInstance { $service }

            (Get-InstallationService).PathName | Should Be $service.PathName
        }

        It 'rejects a service pointing to another executable' {
            $service = [pscustomobject]@{
                PathName = 'C:\Other\Zara.Enforcement.Service.exe'
                StartName = 'LocalSystem'
                StartMode = 'Auto'
            }
            Mock Get-CimInstance { $service }

            { Get-InstallationService } | Should Throw
        }

        It 'rejects a service with a non-LocalSystem account' {
            $service = [pscustomobject]@{
                PathName = $script:serviceExe
                StartName = 'NetworkService'
                StartMode = 'Auto'
            }
            Mock Get-CimInstance { $service }

            { Get-InstallationService } | Should Throw
        }

        It 'rejects a service with a non-Auto start mode' {
            $service = [pscustomobject]@{
                PathName = $script:serviceExe
                StartName = 'LocalSystem'
                StartMode = 'Manual'
            }
            Mock Get-CimInstance { $service }

            { Get-InstallationService } | Should Throw
        }
    }

    Context 'Read-State' {
        It 'reads a journal for the selected target and transaction' {
            New-Item -ItemType Directory -Path $script:backupRoot -Force | Out-Null
            [pscustomobject]@{
                InstallDirectory = $script:target
                TransactionId = $script:TransactionId
                Prepared = $false
            } | ConvertTo-Json | Set-Content -LiteralPath $script:statePath -Encoding UTF8

            (Read-State).InstallDirectory | Should Be $script:target
        }

        It 'rejects a journal for another target' {
            New-Item -ItemType Directory -Path $script:backupRoot -Force | Out-Null
            [pscustomobject]@{
                InstallDirectory = (Join-Path $TestDrive 'other-target')
                TransactionId = $script:TransactionId
            } | ConvertTo-Json | Set-Content -LiteralPath $script:statePath -Encoding UTF8

            { Read-State } | Should Throw
        }

        It 'rejects a journal for another transaction' {
            New-Item -ItemType Directory -Path $script:backupRoot -Force | Out-Null
            [pscustomobject]@{
                InstallDirectory = $script:target
                TransactionId = ('b' * 64)
            } | ConvertTo-Json | Set-Content -LiteralPath $script:statePath -Encoding UTF8

            { Read-State } | Should Throw
        }
    }

    Context 'Remove-CreatedInstallationDirectories' {
        It 'accepts an empty created directory journal' {
            { Remove-CreatedInstallationDirectories ([pscustomobject]@{
                        CreatedDirectories = @()
                    }) } | Should Not Throw
        }

        It 'accepts an empty directory list when creating installation directories' {
            { New-InstallationDirectories @() } | Should Not Throw
        }

        It 'removes empty parent and child directories in leaf-first order' {
            $parent = Split-Path -Parent $script:target
            New-Item -ItemType Directory -Path $script:target -Force | Out-Null

            Remove-CreatedInstallationDirectories ([pscustomobject]@{
                    CreatedDirectories = @($script:target, $parent)
                })

            Test-Path -LiteralPath $script:target | Should Be $false
            Test-Path -LiteralPath $parent | Should Be $false
        }

        It 'preserves a created directory containing a file' {
            $directory = $script:target
            New-Item -ItemType Directory -Path $directory -Force | Out-Null
            Set-Content -LiteralPath (Join-Path $directory 'keep.txt') -Value 'keep' -Encoding UTF8

            Remove-CreatedInstallationDirectories ([pscustomobject]@{
                    CreatedDirectories = @($directory)
                })

            Test-Path -LiteralPath $directory -PathType Container | Should Be $true
            Test-Path -LiteralPath (Join-Path $directory 'keep.txt') -PathType Leaf | Should Be $true
        }

        It 'rejects a created directory outside the target' {
            $outside = Join-Path $TestDrive 'outside'

            { Remove-CreatedInstallationDirectories ([pscustomobject]@{
                        CreatedDirectories = @($outside)
                    }) } | Should Throw
        }

        It 'rejects a drive root in the created directory journal' {
            { Remove-CreatedInstallationDirectories ([pscustomobject]@{
                        CreatedDirectories = @([System.IO.Path]::GetPathRoot($script:target))
                    }) } | Should Throw
        }
    }
}
