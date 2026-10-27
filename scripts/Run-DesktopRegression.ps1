param(
    [Parameter(Mandatory=$true)][string]$Dotnet,
    [Parameter(Mandatory=$true)][string]$WorkRoot
)
$ErrorActionPreference = 'Stop'
$source = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$run = Join-Path ([IO.Path]::GetFullPath($WorkRoot)) ('desktop-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0,8))
$sourcePrefix = $source.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
if ($run.StartsWith($sourcePrefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'WorkRoot 不能在源码目录内，拒绝递归复制。' }
if (!(Test-Path -LiteralPath $Dotnet -PathType Leaf)) { throw '请提供现有 dotnet.exe 路径。' }
$copy = Join-Path $run 'source'
$sourceFiles = @(Get-ChildItem -LiteralPath $source -File -Recurse -Force | Where-Object { $_.FullName -notmatch '\\(\.build|bin|obj|\.git|\.tools|artifacts|local-notes|\.vs)\\' })
New-Item -ItemType Directory -Force -Path $copy | Out-Null
$sourceFiles | ForEach-Object {
    $target = Join-Path $copy $_.FullName.Substring($source.Length+1)
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $target) | Out-Null
    Copy-Item -LiteralPath $_.FullName -Destination $target
}
# 只改测试副本的环境边界；生产默认路径及默认输入行为不变。
$nativePath = Join-Path $copy 'src\Harmonica.Desktop\NativeInput.cs'
$native = [IO.File]::ReadAllText($nativePath)
$api = '(inputs => SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<WinInput>()))'
if ([regex]::Matches($native, [regex]::Escape($api)).Count -ne 1) { throw '默认输入补丁不匹配，拒绝运行。' }
[IO.File]::WriteAllText($nativePath, $native.Replace($api, '(inputs => throw new IOException("REAL INPUT DISABLED IN FIX TEST"))'))
$storagePath = Join-Path $copy 'src\Harmonica.Desktop\Storage.cs'
$storage = [IO.File]::ReadAllText($storagePath)
$oldData = 'Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HarmonicaStudio")'
if ([regex]::Matches($storage, [regex]::Escape($oldData)).Count -ne 1) { throw '数据隔离补丁不匹配，拒绝运行。' }
$storage = $storage.Replace($oldData, 'Environment.GetEnvironmentVariable("HARMONICA_FIX_DATA") ?? throw new InvalidOperationException("Isolated data required")')
$oldLibrary = 'Path.Combine(AppContext.BaseDirectory, "曲库")'
if ([regex]::Matches($storage, [regex]::Escape($oldLibrary)).Count -ne 1) { throw '曲库隔离补丁不匹配，拒绝运行。' }
$storage = $storage.Replace($oldLibrary, 'Path.Combine(DataRoot, "library")')
[IO.File]::WriteAllText($storagePath, $storage)
[IO.File]::WriteAllText((Join-Path $copy 'NuGet.Config'), '<configuration><packageSources><clear /></packageSources></configuration>')
$previous = $env:HARMONICA_FIX_DATA
$result = [pscustomobject]@{Run=$run;BuildExit=$null;TestExit=$null;Data=(Join-Path $run 'data')}
try {
    $env:HARMONICA_FIX_DATA = Join-Path $run 'data'
    & $Dotnet build (Join-Path $copy 'tests\Harmonica.Desktop.Tests\Harmonica.Desktop.Tests.csproj') -c Release --configfile (Join-Path $copy 'NuGet.Config') *> (Join-Path $run 'build.log')
    $buildCode = $LASTEXITCODE
    $result.BuildExit = $buildCode
    $result | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $run 'exit-codes.json') -Encoding utf8
    if ($buildCode -ne 0) { throw "隔离测试构建失败：$run\build.log" }
    & $Dotnet (Join-Path $copy '.build\Harmonica.Desktop.Tests\bin\Release\net8.0-windows\Harmonica.Desktop.Tests.dll') *> (Join-Path $run 'tests.jsonl')
    $testCode = $LASTEXITCODE
    $result.TestExit = $testCode
    $result | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $run 'exit-codes.json') -Encoding utf8
    Get-Content -LiteralPath (Join-Path $run 'tests.jsonl')
    Write-Host "隔离运行目录：$run"
    if ($testCode -ne 0) { throw '桌面回归失败，见上述目录。' }
}
finally { $env:HARMONICA_FIX_DATA = $previous }
