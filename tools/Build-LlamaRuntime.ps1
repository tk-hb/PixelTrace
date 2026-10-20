param(
    [Parameter(Mandatory)][string]$AndroidNdk,
    [Parameter(Mandatory)][string]$VulkanSdk,
    [Parameter(Mandatory)][string]$SpirvHeadersConfig,
    [int]$Jobs = [Math]::Max(1, [Environment]::ProcessorCount / 2)
)

# Explicit native dependency build; never run by a normal app build.
# VulkanSdk provides host glslc and current Vulkan headers. SpirvHeadersConfig
# is the directory containing SPIRV-HeadersConfig.cmake (an installed package).
# Windows also requires an MSVC developer shell for the host shader generator.
$ErrorActionPreference = 'Stop'
$androidRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$work = Join-Path $androidRoot '.native-build'
$source = Join-Path $work 'llama.cpp'
$output = Join-Path $work 'llama-arm64'
$destination = Join-Path $androidRoot 'native/llama/arm64-v8a'
$licenses = Join-Path $androidRoot 'native/llama/licenses'
$revision = 'c0fd78626405c6d9b934efef0ec7291fcc20e3d6'

function Invoke-Native([string]$Command, [string[]]$Arguments) {
    & $Command @Arguments | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "$Command failed with exit code $LASTEXITCODE" }
}

foreach ($command in @('git', 'cmake', 'ninja')) {
    if (!(Get-Command $command -ErrorAction SilentlyContinue)) { throw "Required tool missing: $command" }
}
$AndroidNdk = (Resolve-Path -LiteralPath $AndroidNdk).Path
$VulkanSdk = (Resolve-Path -LiteralPath $VulkanSdk).Path
$SpirvHeadersConfig = (Resolve-Path -LiteralPath $SpirvHeadersConfig).Path
$hostTag = if ($IsWindows) { 'windows-x86_64' } elseif ($IsLinux) { 'linux-x86_64' } else { 'darwin-x86_64' }
$suffix = if ($IsWindows) { '.exe' } else { '' }
$glslc = Join-Path $VulkanSdk $(if ($IsWindows) { 'Bin/glslc.exe' } else { 'bin/glslc' })
$vulkanHeaders = Join-Path $VulkanSdk $(if ($IsWindows) { 'Include' } else { 'include' })
$toolchain = Join-Path $AndroidNdk "toolchains/llvm/prebuilt/$hostTag"
$loader = Join-Path $toolchain 'sysroot/usr/lib/aarch64-linux-android/29/libvulkan.so'
$ndkVersion = Get-Content (Join-Path $AndroidNdk 'source.properties') | Select-String 'Pkg.Revision\s*=\s*(\d+)'
if (!$ndkVersion -or [int]$ndkVersion.Matches[0].Groups[1].Value -lt 28) { throw 'Android NDK r28+ is required for 16 KiB page compatibility.' }
foreach ($path in @($glslc, $loader, (Join-Path $vulkanHeaders 'vulkan/vulkan.hpp'), (Join-Path $SpirvHeadersConfig 'SPIRV-HeadersConfig.cmake'))) {
    if (!(Test-Path -LiteralPath $path)) { throw "Required build input missing: $([IO.Path]::GetFileName($path))" }
}
# Share the app's existing Android C++ runtime; never package a duplicate SONAME.
$runtime = Join-Path $androidRoot 'native/onnx/arm64-v8a/libc++_shared.so'
if (!(Test-Path -LiteralPath $runtime)) { throw 'Prepare the existing ONNX native runtime (including libc++_shared.so) first, using the same Android NDK.' }
New-Item -ItemType Directory -Force -Path $work, $destination, $licenses | Out-Null
if (!(Test-Path -LiteralPath $source)) {
    Invoke-Native git @('clone', '--filter=blob:none', '--no-checkout', 'https://github.com/ggml-org/llama.cpp.git', $source)
}
$changes = & git -C $source status --porcelain
if ($LASTEXITCODE -ne 0 -or $changes) { throw 'llama.cpp source checkout must be clean.' }
Invoke-Native git @('-C', $source, 'fetch', 'origin', $revision, '--depth=1')
Invoke-Native git @('-C', $source, 'checkout', '--detach', $revision)
Invoke-Native cmake @(
    '-S', (Join-Path $androidRoot 'native/llama'), '-B', $output, '-G', 'Ninja',
    "-DLLAMA_SOURCE_DIR=$source", "-DCMAKE_TOOLCHAIN_FILE=$AndroidNdk/build/cmake/android.toolchain.cmake",
    '-DANDROID_ABI=arm64-v8a', '-DANDROID_PLATFORM=android-29', '-DANDROID_STL=c++_shared',
    '-DCMAKE_BUILD_TYPE=Release', '-DCMAKE_SHARED_LINKER_FLAGS=-Wl,-z,max-page-size=16384',
    "-DVulkan_GLSLC_EXECUTABLE=$glslc", "-DVulkan_INCLUDE_DIR=$vulkanHeaders", "-DVulkan_LIBRARY=$loader",
    "-DSPIRV-Headers_DIR=$SpirvHeadersConfig"
)
Invoke-Native cmake @('--build', $output, '--config', 'Release', '--target', 'pixeltrace_llama', '--parallel', "$Jobs")
$library = Join-Path $destination 'libpixeltrace_llama.so'
Copy-Item -LiteralPath (Join-Path $output 'libpixeltrace_llama.so') -Destination $library -Force
Invoke-Native (Join-Path $toolchain "bin/llvm-strip$suffix") @('--strip-unneeded', $library)
Copy-Item -LiteralPath (Join-Path $source 'LICENSE') -Destination (Join-Path $licenses 'llama.cpp-LICENSE.txt') -Force
# mtmd statically includes these vendored libraries; preserve their source notices.
foreach ($vendor in @('stb', 'miniaudio', 'hash', 'sheredom')) {
    $directory = Join-Path $licenses $vendor
    New-Item -ItemType Directory -Force -Path $directory | Out-Null
    $vendorRoot = Join-Path $source "vendor/$vendor"
    $files = Get-ChildItem -LiteralPath $vendorRoot -File -Recurse
    foreach ($file in $files) {
        $target = Join-Path $directory ([IO.Path]::GetRelativePath($vendorRoot, $file.FullName))
        New-Item -ItemType Directory -Force -Path ([IO.Path]::GetDirectoryName($target)) | Out-Null
        if ($file.Name -match 'LICENSE|COPYING|NOTICE') {
            Copy-Item -LiteralPath $file.FullName -Destination $target -Force
        }
        elseif ($file.Extension -in @('.h', '.hpp', '.c', '.cpp')) {
            $comments = [regex]::Matches([IO.File]::ReadAllText($file.FullName), '/\*[\s\S]*?\*/') |
                Where-Object { $_.Value -match '(?i)copyright|permission is hereby granted|public domain|ALTERNATIVE.*LICENSE' }
            if ($comments) {
                [IO.File]::WriteAllText(($target + '-notices.txt'), (($comments | ForEach-Object Value) -join "`n`n"))
            }
        }
    }
}
Write-Host 'llama.cpp Vulkan runtime ready.'
Write-Host 'BF16 GGUF weights are preserved. Kernel arithmetic depends on Vulkan device support; no model precision conversion is performed.'
