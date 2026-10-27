#requires -Version 7.0
param(
    [ValidateSet('Build','Test','DesktopTest','Publish')][string]$Mode = 'Build',
    [string]$DotnetPath,
    [string]$OutputDirectory = (Join-Path $PSScriptRoot 'artifacts/publish/win-x64'),
    [string]$LibraryPath = (Join-Path $PSScriptRoot 'examples'),
    [switch]$Offline
)
$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
if (-not $DotnetPath) {
    $localSdk = Join-Path $PSScriptRoot '.tools/dotnet/dotnet.exe'
    if (Test-Path -LiteralPath $localSdk -PathType Leaf) { $DotnetPath = $localSdk }
    else { $command = Get-Command dotnet -ErrorAction SilentlyContinue; if ($command) { $DotnetPath = $command.Source } }
}
if (-not $DotnetPath -or !(Test-Path -LiteralPath $DotnetPath -PathType Leaf)) {
    throw '需要 .NET 8 SDK。请安装 SDK 或通过 -DotnetPath 指定已有的 dotnet.exe。'
}
$DotnetPath = [IO.Path]::GetFullPath($DotnetPath)

function Invoke-Dotnet([string[]]$Arguments) {
    & $DotnetPath @Arguments
    if ($LASTEXITCODE -ne 0) { throw ('dotnet 命令失败，退出码：' + $LASTEXITCODE) }
}

Push-Location $PSScriptRoot
try {
    $sdkVersion = & $DotnetPath --version
    if ($LASTEXITCODE -ne 0 -or $sdkVersion -notmatch '^8\.') {
        throw '当前目录需要 .NET 8 SDK（global.json）。安装 Desktop Runtime 不能代替 SDK；也可指定 -DotnetPath。'
    }
    $restoreArguments = @()
    if ($Offline) {
        $offlineConfig = Join-Path $PSScriptRoot 'artifacts/offline.NuGet.Config'
        New-Item -ItemType Directory -Path (Split-Path -Parent $offlineConfig) -Force | Out-Null
        [IO.File]::WriteAllText($offlineConfig, '<configuration><packageSources><clear /></packageSources></configuration>')
        $restoreArguments = @('--configfile', $offlineConfig)
    }
    $desktopProject = Join-Path $PSScriptRoot 'src/Harmonica.Desktop/Harmonica.Desktop.csproj'
    if ($Mode -eq 'Build') {
        Invoke-Dotnet (@('build', $desktopProject, '-c', 'Release', '--nologo') + $restoreArguments)
    }
    elseif ($Mode -eq 'Test') {
        Invoke-Dotnet (@('build', (Join-Path $PSScriptRoot 'tests/Harmonica.Tests/Harmonica.Tests.csproj'), '-c', 'Release', '--nologo') + $restoreArguments)
        Invoke-Dotnet @((Join-Path $PSScriptRoot '.build/Harmonica.Tests/bin/Release/net8.0/Harmonica.Tests.dll'), [IO.Path]::GetFullPath($LibraryPath))
    }
    elseif ($Mode -eq 'DesktopTest') {
        # 运行器总是使用无网络包源、数据隔离和默认真实输入禁用的副本。
        & (Join-Path $PSScriptRoot 'scripts/Run-DesktopRegression.ps1') -Dotnet $DotnetPath -WorkRoot (Join-Path ([IO.Path]::GetTempPath()) 'HarmonicaStudio-tests')
    }
    else {
        $output = [IO.Path]::GetFullPath($OutputDirectory)
        if (Test-Path -LiteralPath $output) { throw '发布输出目录已存在。请选择全新目录，禁止覆盖并混用旧包。' }
        $parent = [IO.Path]::GetDirectoryName($output)
        if ([string]::IsNullOrWhiteSpace($parent)) { throw '发布目录必须具有有效的父目录。' }
        New-Item -ItemType Directory -Path $parent -Force | Out-Null
        $staging = Join-Path $parent ('.publish-' + [Guid]::NewGuid().ToString('N'))
        Invoke-Dotnet (@('publish', $desktopProject, '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '-p:PublishSingleFile=false', '-p:DebugType=None', '-p:DebugSymbols=false', '-o', $staging, '--nologo') + $restoreArguments)
        $runtimeConfig = Get-Content -LiteralPath (Join-Path $staging 'HarmonicaStudio.runtimeconfig.json') -Raw | ConvertFrom-Json
        $frameworks = @($runtimeConfig.runtimeOptions.includedFrameworks)
        if ($runtimeConfig.runtimeOptions.frameworks -or $frameworks.Count -ne 2) { throw '发布配置不是预期的自包含 .NET + Windows Desktop 包。' }
        foreach ($required in @('HarmonicaStudio.exe','hostfxr.dll','hostpolicy.dll','coreclr.dll','PresentationFramework.dll')) {
            if (!(Test-Path -LiteralPath (Join-Path $staging $required) -PathType Leaf)) { throw ('发布包缺少：' + $required) }
        }
        New-Item -ItemType Directory -Path (Join-Path $staging '曲库') -Force | Out-Null
        Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'examples') -Filter '*.json' -File | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $staging '曲库') }
        foreach ($file in @('README.md','LICENSE','THIRD-PARTY.md')) { Copy-Item -LiteralPath (Join-Path $PSScriptRoot $file) -Destination $staging }
        $packageRoot = $env:NUGET_PACKAGES
        if (-not $packageRoot) { $packageRoot = Join-Path ([Environment]::GetFolderPath('UserProfile')) '.nuget/packages' }
        foreach ($framework in $frameworks) {
            if ($framework.name -notin @('Microsoft.NETCore.App','Microsoft.WindowsDesktop.App')) { throw '发布包含未知框架。' }
            $package = $framework.name.ToLowerInvariant() + '.runtime.win-x64'
            $packagePath = Join-Path (Join-Path $packageRoot $package) $framework.version
            $notices = @(Get-ChildItem -LiteralPath $packagePath -File | Where-Object { $_.Name -match '(?i)license|notice' })
            if ($notices.Count -eq 0) { throw ('缺少运行时许可证：' + $package) }
            $licenseDirectory = Join-Path (Join-Path $staging '许可证') $framework.name
            New-Item -ItemType Directory -Path $licenseDirectory -Force | Out-Null
            foreach ($notice in $notices) { Copy-Item -LiteralPath $notice.FullName -Destination $licenseDirectory }
        }
        # 只移动本次随机暂存目录到同一父目录下的明确输出路径，不删除旧包。
        $resolvedStaging = [IO.Path]::GetFullPath($staging)
        if ([IO.Path]::GetDirectoryName($resolvedStaging) -ne $parent -or [IO.Path]::GetDirectoryName($output) -ne $parent) { throw '发布暂存目录边界检查失败。' }
        [IO.Directory]::Move($resolvedStaging, $output)
        Write-Host ('已发布完整自包含目录：' + $output)
    }
}
finally { Pop-Location }
