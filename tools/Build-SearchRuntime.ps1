param(
    [Parameter(Mandatory)][string]$AndroidNdk,
    [string]$Ninja = 'ninja',
    [string]$WorkDirectory,
    [string]$SourceDirectory,
    [string]$OutputDirectory,
    [int]$Jobs = [Math]::Max(1, [Environment]::ProcessorCount / 2)
)

$ErrorActionPreference = 'Stop'
$androidRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (!$WorkDirectory) { $WorkDirectory = Join-Path $androidRoot '.native-build/usearch' }
if (!$SourceDirectory) { $SourceDirectory = Join-Path $WorkDirectory 'source' }
if (!$OutputDirectory) { $OutputDirectory = Join-Path $androidRoot 'native/usearch/arm64-v8a' }
$revision = '1e8a19253138510f35ee2c68dc641e5b5c83c7b5'
$buildDirectory = Join-Path $WorkDirectory 'arm64'
$licenses = Join-Path $androidRoot 'native/usearch/licenses'

function Invoke-Native([string]$Command, [string[]]$Arguments) {
    & $Command @Arguments | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "$Command failed with exit code $LASTEXITCODE" }
}

$AndroidNdk = (Resolve-Path -LiteralPath $AndroidNdk).Path
$toolchain = Join-Path $AndroidNdk 'build/cmake/android.toolchain.cmake'
if (!(Test-Path -LiteralPath $toolchain)) { throw 'Android NDK toolchain not found.' }
$ndkVersion = Get-Content (Join-Path $AndroidNdk 'source.properties') | Select-String 'Pkg.Revision\s*=\s*(\d+)'
if (!$ndkVersion -or [int]$ndkVersion.Matches[0].Groups[1].Value -lt 28) { throw 'Android NDK r28 or newer is required.' }
$Ninja = (Get-Command $Ninja -ErrorAction Stop).Source
New-Item -ItemType Directory -Force -Path $WorkDirectory, $OutputDirectory, $licenses | Out-Null
if (!(Test-Path -LiteralPath (Join-Path $SourceDirectory '.git'))) {
    New-Item -ItemType Directory -Force -Path $SourceDirectory | Out-Null
    Invoke-Native 'git' @('-C', $SourceDirectory, 'init')
    Invoke-Native 'git' @('-C', $SourceDirectory, 'remote', 'add', 'origin', 'https://github.com/unum-cloud/USearch.git')
    Invoke-Native 'git' @('-C', $SourceDirectory, 'fetch', '--depth', '1', 'origin', $revision)
    Invoke-Native 'git' @('-C', $SourceDirectory, 'checkout', '--detach', 'FETCH_HEAD')
}
$actualRevision = & git -C $SourceDirectory rev-parse HEAD
if ($LASTEXITCODE -ne 0 -or $actualRevision.Trim() -ne $revision) { throw "USearch source must be at $revision" }
Invoke-Native 'git' @('-C', $SourceDirectory, 'submodule', 'update', '--init', '--depth', '1', 'numkong')

Invoke-Native 'cmake' @('-S', $SourceDirectory, '-B', $buildDirectory, '-G', 'Ninja',
    "-DCMAKE_MAKE_PROGRAM=$Ninja", "-DCMAKE_TOOLCHAIN_FILE=$toolchain",
    '-DANDROID_ABI=arm64-v8a', '-DANDROID_PLATFORM=android-29', '-DANDROID_STL=c++_shared',
    '-DCMAKE_BUILD_TYPE=Release', '-DCMAKE_SHARED_LINKER_FLAGS=-Wl,-z,max-page-size=16384 -Wl,-z,common-page-size=16384',
    '-DUSEARCH_BUILD_LIB_C=ON', '-DUSEARCH_USE_NUMKONG=ON', '-DUSEARCH_USE_OPENMP=OFF',
    '-DUSEARCH_BUILD_TEST_CPP=OFF', '-DUSEARCH_BUILD_TEST_C=OFF', '-DUSEARCH_BUILD_BENCH_CPP=OFF',
    '-DNK_BUILD_TEST=OFF', '-DNK_BUILD_BENCH=OFF', '-DNK_BUILD_SHARED_TEST=OFF')
Invoke-Native 'cmake' @('--build', $buildDirectory, '--target', 'usearch_c', '--parallel', $Jobs.ToString())
Copy-Item -LiteralPath (Join-Path $buildDirectory 'libusearch_c.so') -Destination $OutputDirectory -Force
Copy-Item -LiteralPath (Join-Path $buildDirectory 'numkong/libnumkong.so') -Destination $OutputDirectory -Force
$hostTag = if ($IsWindows) { 'windows-x86_64' } elseif ($IsLinux) { 'linux-x86_64' } else { 'darwin-x86_64' }
$strip = Join-Path $AndroidNdk "toolchains/llvm/prebuilt/$hostTag/bin/llvm-strip$(if ($IsWindows) { '.exe' })"
foreach ($library in 'libusearch_c.so', 'libnumkong.so') {
    Invoke-Native $strip @('--strip-unneeded', (Join-Path $OutputDirectory $library))
}
Copy-Item -LiteralPath (Join-Path $SourceDirectory 'LICENSE') -Destination (Join-Path $licenses 'USearch-LICENSE.txt') -Force
Copy-Item -LiteralPath (Join-Path $SourceDirectory 'numkong/LICENSE') -Destination (Join-Path $licenses 'NumKong-LICENSE.txt') -Force
Write-Output 'USearch FP32 runtime ready.'
