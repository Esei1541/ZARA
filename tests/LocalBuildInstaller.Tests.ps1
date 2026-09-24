$ErrorActionPreference = 'Stop'

$script:repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$script:commonScriptPath = Join-Path $script:repoRoot 'installer\LocalBuild.Common.ps1'
$script:localScriptPath = Join-Path $script:repoRoot 'installer\Build-LocalInstaller.ps1'
$script:builderScriptPath = Join-Path $script:repoRoot 'installer\Build-Installer.ps1'
$script:issPath = Join-Path $script:repoRoot 'installer\Zara.iss'
. $script:commonScriptPath
. $script:localScriptPath

Describe 'Local build identity helpers' {
    It 'removes only a leading date prefix and replaces invalid branch characters' {
        $createdAt = [DateTimeOffset]::Parse('2026-09-23T14:15:16+09:00')

        Get-ZaraLocalBuildIdBase $createdAt '1.0.1' '260923-feature/local builds' |
            Should Be '260923-141516-1.0.1-feature-local-builds'
        Get-ZaraLocalBuildIdBase $createdAt '1.0.1' 'feature-260923-local' |
            Should Be '260923-141516-1.0.1-feature-260923-local'
    }

    It 'adds a two-digit suffix when a build identity already exists' {
        $root = Join-Path $TestDrive 'builds'
        New-Item -ItemType Directory -Path (Join-Path $root '260923-141516-1.0.1-feature') -Force | Out-Null

        $reserved = New-ZaraLocalBuildDirectory $root '260923-141516-1.0.1-feature'

        $reserved.BuildId | Should Be '260923-141516-1.0.1-feature-01'
        Test-Path -LiteralPath $reserved.Path -PathType Container | Should Be $true
    }

    It 'treats a nonignored untracked source file as dirty' {
        $repository = Join-Path $TestDrive 'repository'
        New-Item -ItemType Directory -Path $repository | Out-Null
        & git -C $repository init --quiet
        Set-Content -LiteralPath (Join-Path $repository 'tracked.txt') -Value 'tracked' -Encoding UTF8
        & git -C $repository add tracked.txt
        & git -C $repository -c user.name=ZaraTest -c user.email=zara@example.invalid commit --quiet -m 'fix: 시험 제목' -m '본문은 표시하지 않음'
        $originalOutputEncoding = [Console]::OutputEncoding
        try {
            [Console]::OutputEncoding = [Text.Encoding]::GetEncoding(437)
            $identity = Get-ZaraLocalBuildGitIdentity $repository
            [Console]::OutputEncoding.CodePage | Should Be 437
        }
        finally {
            [Console]::OutputEncoding = $originalOutputEncoding
        }
        $identity.IsClean | Should Be $true
        $identity.CommitSubject | Should Be 'fix: 시험 제목'

        Set-Content -LiteralPath (Join-Path $repository 'NewSource.cs') -Value 'class NewSource {}' -Encoding UTF8

        (Get-ZaraLocalBuildGitIdentity $repository).IsClean | Should Be $false
    }
}

Describe 'Build-LocalInstaller.ps1 completion contract' {
    It 'registers a Release installer without adding an installed local identity' {
        $buildId = '260924-141516-1.1.0-release'
        $buildDirectory = Join-Path $TestDrive $buildId
        New-Item -ItemType Directory -Path $buildDirectory | Out-Null
        $installer = Join-Path $buildDirectory ($buildId + '.exe')
        [IO.File]::WriteAllBytes($installer, [byte[]](1, 2, 3, 4))

        Publish-ZaraLocalBuildManifest -BuildDirectory $buildDirectory -BuildId $buildId `
            -VersionName '1.1.0' -Configuration Release -CreatedAt '2026-09-24T14:15:16+09:00' `
            -Branch '260924-release' -Commit ('a' * 40) | Should Be $installer

        $manifest = Get-Content -LiteralPath (Join-Path $buildDirectory 'build.json') -Raw | ConvertFrom-Json
        $manifest.configuration | Should Be 'Release'
        $manifest.installerSha256 | Should Be (Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash.ToLowerInvariant()
        Test-Path -LiteralPath (Join-Path $buildDirectory 'local-build.json') | Should Be $false
    }

    It 'publishes build.json only after the installer exists with the exact external schema' {
        $buildDirectory = Join-Path $TestDrive 'complete'
        New-Item -ItemType Directory -Path $buildDirectory | Out-Null
        $buildId = '260923-141516-1.0.1-local-build'
        $installer = Join-Path $buildDirectory ($buildId + '.exe')
        [IO.File]::WriteAllBytes($installer, [byte[]](1, 2, 3, 4))

        $publishedInstaller = Publish-ZaraLocalBuildManifest -BuildDirectory $buildDirectory `
            -BuildId $buildId -VersionName '1.0.1' -Configuration Staging `
            -CreatedAt '2026-09-23T14:15:16+09:00' -Branch '260923-local-build' -Commit ('a' * 40) `
            -CommitSubject 'fix: 시험 제목'
        $manifest = Get-Content -LiteralPath (Join-Path $buildDirectory 'build.json') -Raw -Encoding UTF8 | ConvertFrom-Json

        $publishedInstaller | Should Be $installer
        ($manifest.psobject.Properties.Name -join ',') | Should Be 'schemaVersion,buildId,versionName,configuration,createdAt,branch,commit,commitSubject,installerFileName,installerSha256'
        $manifest.schemaVersion | Should Be 1
        $manifest.configuration | Should Be 'Staging'
        $manifest.commit | Should Be ('a' * 40)
        $manifest.commitSubject | Should Be 'fix: 시험 제목'
        $manifest.installerFileName | Should Be ([IO.Path]::GetFileName($installer))
        $manifest.installerSha256 | Should Match '^[0-9a-f]{64}$'
        [DateTimeOffset]::Parse($manifest.createdAt) | Out-Null
    }

    It 'does not publish build.json when the installer is missing' {
        New-Item -ItemType Directory -Path (Join-Path $TestDrive 'incomplete') | Out-Null
        $thrown = $false
        try {
            Publish-ZaraLocalBuildManifest -BuildDirectory (Join-Path $TestDrive 'incomplete') -BuildId '260923-141516-1.0.1-local-build' -VersionName '1.0.1' -Configuration Staging -CreatedAt '2026-09-23T14:15:16+09:00' -Branch '260923-local-build' -Commit ('a' * 40)
        }
        catch {
            $thrown = $true
        }

        $thrown | Should Be $true
        Test-Path -LiteralPath (Join-Path (Join-Path $TestDrive 'incomplete') 'build.json') | Should Be $false
    }
}

Describe 'Installer configuration contracts' {
    It 'writes only the local payload identity fields' {
        $tokens = $null
        $errors = $null
        $ast = [Management.Automation.Language.Parser]::ParseFile($script:builderScriptPath, [ref]$tokens, [ref]$errors)
        @($errors).Count | Should Be 0
        $functions = @($ast.FindAll({
                    param($node)
                    $node -is [Management.Automation.Language.FunctionDefinitionAst] -and
                    $node.Name -eq 'Write-LocalBuildIdentity'
                }, $true))
        $functionPath = Join-Path $TestDrive 'Write-LocalBuildIdentity.ps1'
        Set-Content -LiteralPath $functionPath -Value $functions[0].Extent.Text -Encoding UTF8
        . $functionPath
        $path = Join-Path $TestDrive 'local-build.json'

        Write-LocalBuildIdentity ([ordered]@{
                schemaVersion = 1
                buildId = '260923-141516-1.0.1-feature'
                versionName = '1.0.1'
                configuration = 'Staging'
                createdAt = '2026-09-23T14:15:16+09:00'
                branch = '260923-feature'
                commit = 'A' * 40
                commitSubject = 'fix: 시험 제목'
                buildsDirectory = 'G:\repo\artifacts\local-builds'
                installerFileName = 'excluded.exe'
                installerSha256 = 'f' * 64
            }) $path
        $identity = Get-Content -LiteralPath $path -Raw -Encoding UTF8 | ConvertFrom-Json

        ($identity.psobject.Properties.Name -join ',') |
            Should Be 'schemaVersion,buildId,versionName,configuration,createdAt,branch,commit,buildsDirectory,commitSubject'
        $identity.commit | Should Be ('a' * 40)
        $identity.commitSubject | Should Be 'fix: 시험 제목'
    }

    It 'reads Version when later conditional property groups do not define it' {
        $fixture = Join-Path $TestDrive 'version-fixture'
        New-Item -ItemType Directory -Path $fixture | Out-Null
        @'
<Project>
  <PropertyGroup>
    <Version>2.3.4</Version>
  </PropertyGroup>
  <PropertyGroup Condition="'$(Configuration)' == 'Staging'">
    <Optimize>true</Optimize>
  </PropertyGroup>
</Project>
'@ | Set-Content -LiteralPath (Join-Path $fixture 'Directory.Build.props') -Encoding UTF8

        Get-ZaraProductVersion $fixture | Should Be '2.3.4'
    }
}
