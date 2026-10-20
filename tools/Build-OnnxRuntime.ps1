param(
    [Parameter(Mandatory)][string]$AndroidSdk,
    [Parameter(Mandatory)][string]$AndroidNdk,
    [string]$Python = 'python',
    [int]$Jobs = [Math]::Max(1, [Environment]::ProcessorCount / 2)
)

# Explicit dependency build, never invoked by the app's normal MSBuild targets.
# Requires Python, Git, CMake, Ninja, Rust, protoc and an Android NDK (r28+).
$ErrorActionPreference = 'Stop'
$androidRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$work = Join-Path $androidRoot '.native-build'
$destination = Join-Path $androidRoot 'native/onnx/arm64-v8a'
$licenses = Join-Path $androidRoot 'native/onnx/licenses'

function Invoke-Native([string]$Command, [string[]]$Arguments) {
    & $Command @Arguments | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "$Command failed with exit code $LASTEXITCODE" }
}

foreach ($command in @($Python, 'git', 'cmake', 'ninja', 'cargo', 'rustup', 'protoc')) {
    if (!(Get-Command $command -ErrorAction SilentlyContinue)) { throw "Required tool missing: $command" }
}
$AndroidSdk = (Resolve-Path -LiteralPath $AndroidSdk).Path
$AndroidNdk = (Resolve-Path -LiteralPath $AndroidNdk).Path
$hostTag = if ($IsWindows) { 'windows-x86_64' } elseif ($IsLinux) { 'linux-x86_64' } else { 'darwin-x86_64' }
$toolchain = Join-Path $AndroidNdk "toolchains/llvm/prebuilt/$hostTag/bin"
$clang = Join-Path $toolchain $(if ($IsWindows) { 'aarch64-linux-android29-clang.cmd' } else { 'aarch64-linux-android29-clang' })
if (!(Test-Path -LiteralPath $clang)) { throw 'Android arm64 API 29 compiler missing.' }
$ndkVersion = Get-Content (Join-Path $AndroidNdk 'source.properties') | Select-String 'Pkg.Revision\s*=\s*(\d+)'
if (!$ndkVersion -or [int]$ndkVersion.Matches[0].Groups[1].Value -lt 28) { throw 'Use Android NDK r28 or newer for 16 KiB page compatibility.' }
New-Item -ItemType Directory -Force -Path $work, $destination, $licenses | Out-Null

function Get-Source([string]$Name, [string]$Url, [string]$Commit) {
    $checkout = Join-Path $work $Name
    if (!(Test-Path -LiteralPath $checkout)) {
        Invoke-Native git @('clone', '--filter=blob:none', '--no-checkout', $Url, $checkout)
    }
    else {
        $changes = & git -C $checkout status --porcelain
        if ($LASTEXITCODE -ne 0 -or $changes) { throw "Native source checkout must be clean: $Name" }
    }
    Invoke-Native git @('-C', $checkout, 'fetch', 'origin', $Commit, '--depth=1')
    Invoke-Native git @('-C', $checkout, 'checkout', '--detach', $Commit)
    Invoke-Native git @('-C', $checkout, 'submodule', 'update', '--init', '--recursive', '--depth=1')
    return $checkout
}

$ort = Get-Source 'onnxruntime' 'https://github.com/microsoft/onnxruntime.git' '8c546c37b43caaca1fa25db430dab94b901cf277'
$bindings = Get-Source 'tokenizers-dotnet' 'https://github.com/IgnaciodelaTorreArias/Tokenizers.HuggingFace.DotNet.git' '9743e169ba445a90f473475311a0a7b4afa8147c'
$tokenizerSource = Join-Path $work 'tokenizers-android-src'
$tokenizerTarget = Join-Path $bindings 'tokenizers_proto/target'
New-Item -ItemType Directory -Force -Path $tokenizerSource | Out-Null
foreach ($name in @('src', 'protos', 'Cargo.toml', 'Cargo.lock', 'build.rs')) {
    Copy-Item -LiteralPath (Join-Path $bindings "tokenizers_proto/$name") -Destination $tokenizerSource -Recurse -Force
}
# build.rs runs on the host. Its cfg(target_os) must not add a Windows
# resource library when the actual Cargo target is Android.
$buildScript = Join-Path $tokenizerSource 'build.rs'
$buildText = [IO.File]::ReadAllText($buildScript).Replace("`r`n", "`n")
$windowsGuard = '#[cfg(target_os = "windows")]' + "`n    {"
$targetGuard = '#[cfg(target_os = "windows")]' + "`n    if std::env::var(`"CARGO_CFG_TARGET_OS`").as_deref() == Ok(`"windows`") {"
if (!$buildText.Contains($windowsGuard)) { throw 'Pinned tokenizer build.rs no longer matches the Android target patch.' }
[IO.File]::WriteAllText($buildScript, $buildText.Replace($windowsGuard, $targetGuard).Replace('let mut res =', 'let res ='))
$output = Join-Path $work 'ort-arm64'
$deviceDiscovery = Join-Path $androidRoot 'native/onnx/AndroidDeviceDiscovery.cmake'
Invoke-Native $Python @(
    (Join-Path $ort 'tools/ci_build/build.py'), '--build_dir', $output,
    '--config', 'Release', '--update', '--build', '--skip_tests', '--parallel', "$Jobs",
    '--android', '--android_sdk_path', $AndroidSdk, '--android_ndk_path', $AndroidNdk,
    '--android_abi', 'arm64-v8a', '--android_api', '29', '--cmake_generator', 'Ninja',
    '--build_shared_lib', '--use_webgpu', 'shared_lib',
    '--cmake_extra_defines', 'onnxruntime_BUILD_UNIT_TESTS=OFF',
    "CMAKE_PROJECT_onnxruntime_INCLUDE=$deviceDiscovery",
    'DAWN_ENABLE_VULKAN=ON',
    'onnxruntime_BUILD_DAWN_SHARED_LIBRARY=OFF', 'CMAKE_SHARED_LINKER_FLAGS=-Wl,-z,max-page-size=16384'
)

Invoke-Native rustup @('target', 'add', 'aarch64-linux-android')
$savedEnvironment = @{}
$nativeEnvironment = @{
    CARGO_TARGET_AARCH64_LINUX_ANDROID_LINKER = $clang
    CC_aarch64_linux_android = $clang
    CXX_aarch64_linux_android = Join-Path $toolchain $(if ($IsWindows) { 'aarch64-linux-android29-clang++.cmd' } else { 'aarch64-linux-android29-clang++' })
    AR_aarch64_linux_android = Join-Path $toolchain $(if ($IsWindows) { 'llvm-ar.exe' } else { 'llvm-ar' })
    CARGO_TARGET_AARCH64_LINUX_ANDROID_RUSTFLAGS = '-C link-arg=-Wl,-z,max-page-size=16384'
    CARGO_TARGET_DIR = $tokenizerTarget
}
try {
    foreach ($key in $nativeEnvironment.Keys) {
        $savedEnvironment[$key] = [Environment]::GetEnvironmentVariable($key)
        [Environment]::SetEnvironmentVariable($key, $nativeEnvironment[$key])
    }
    Invoke-Native cargo @('build', '--manifest-path', (Join-Path $tokenizerSource 'Cargo.toml'), '--release', '--target', 'aarch64-linux-android', '--locked', '--jobs', "$Jobs")
}
finally {
    foreach ($key in $savedEnvironment.Keys) { [Environment]::SetEnvironmentVariable($key, $savedEnvironment[$key]) }
}

foreach ($library in @('libonnxruntime.so', 'libonnxruntime_providers_webgpu.so')) {
    $file = Get-ChildItem -LiteralPath (Join-Path $output 'Release') -Filter $library -Recurse -File | Select-Object -First 1
    if (!$file) { throw "Native build did not produce $library" }
    Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $destination $library)
}
Copy-Item -LiteralPath (Join-Path $bindings 'tokenizers_proto/target/aarch64-linux-android/release/libtokenizers_proto.so') -Destination $destination
Copy-Item -LiteralPath (Join-Path $AndroidNdk "toolchains/llvm/prebuilt/$hostTag/sysroot/usr/lib/aarch64-linux-android/libc++_shared.so") -Destination $destination
$strip = Join-Path $toolchain $(if ($IsWindows) { 'llvm-strip.exe' } else { 'llvm-strip' })
foreach ($library in Get-ChildItem -LiteralPath $destination -Filter '*.so' -File) {
    Invoke-Native $strip @('--strip-unneeded', $library.FullName)
}
Copy-Item -LiteralPath (Join-Path $ort 'LICENSE') -Destination (Join-Path $licenses 'ONNXRuntime-LICENSE.txt')
Copy-Item -LiteralPath (Join-Path $ort 'ThirdPartyNotices.txt') -Destination (Join-Path $licenses 'ONNXRuntime-ThirdPartyNotices.txt')
Copy-Item -LiteralPath (Join-Path $bindings 'LICENSE.txt') -Destination (Join-Path $licenses 'Tokenizers-DotNet-LICENSE.txt')
Copy-Item -LiteralPath (Join-Path $bindings 'tokenizers_proto/LICENSE') -Destination (Join-Path $licenses 'Tokenizers-Proto-LICENSE.txt')
Write-Host 'Android ONNX native dependencies ready.'
