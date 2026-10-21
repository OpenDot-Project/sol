param([string]$LlvmRoot = $env:LLVM_ROOT)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path $PSScriptRoot -Parent
if (-not $LlvmRoot) { throw 'LLVM_ROOT or -LlvmRoot is required.' }
$LlvmRoot = [IO.Path]::GetFullPath($LlvmRoot)
$version = & (Join-Path $LlvmRoot 'bin/llvm-config.exe') --version
if ($LASTEXITCODE -ne 0 -or $version -ne '23.1.3') { throw 'The native backend requires LLVM 23.1.3.' }
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
$vs = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vs) { throw 'MSVC C++ and C++/CLI tools are required.' }
$msbuild = Join-Path $vs 'MSBuild/Current/Bin/MSBuild.exe'
$dotnet = (Get-Command dotnet).Source
& $dotnet build (Join-Path $root 'src/managed/OpenDot.Sol.Cli/OpenDot.Sol.Cli.csproj') -c Release -p:UseSharedCompilation=false
if ($LASTEXITCODE -ne 0) { throw 'Managed backend build failed.' }
$packs = Join-Path (Split-Path $dotnet -Parent) 'packs'
$reference = Get-ChildItem (Join-Path $packs 'Microsoft.NETCore.App.Ref') -Directory | Where-Object Name -Like '10.*' | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
if (-not $reference) { throw '.NET 10 reference packs are missing.' }
$toolsets = Get-ChildItem (Join-Path $vs 'MSBuild/Microsoft/VC') -Directory | Sort-Object Name -Descending
$toolset = $null
foreach ($platform in $toolsets) {
    $directory = Join-Path $platform.FullName 'Platforms/x64/PlatformToolsets'
    if (Test-Path $directory) { $toolset = Get-ChildItem $directory -Directory | Where-Object Name -In @('v145', 'v143') | Sort-Object Name -Descending | Select-Object -First 1 }
    if ($toolset) { break }
}
if (-not $toolset) { throw 'A qualified MSVC platform toolset is required.' }
$properties = @('/nologo', '/nodeReuse:false', '/p:Configuration=Release', '/p:Platform=x64', "/p:PlatformToolset=$($toolset.Name)")
$properties += @('/p:EnableCppCoreCheck=true', '/p:RunCodeAnalysis=true', '/p:CodeAnalysisRuleSet=CppCoreCheckRules.ruleset')
& (Join-Path $PSScriptRoot 'install-llvm-native-deps.ps1') -LlvmRoot $LlvmRoot
& $msbuild (Join-Path $root 'src/native/llvm-native/llvm-native.vcxproj') @properties "/p:LLVMRoot=$LlvmRoot"
if ($LASTEXITCODE -ne 0) { throw 'LLVM C++ native component build failed.' }
& $msbuild (Join-Path $root 'src/native/llvm-bridge/llvm-bridge.vcxproj') @properties "/p:LLVMRoot=$LlvmRoot" "/p:DotNetPackRoot=$packs" "/p:ReferencePackVersion=$($reference.Name)"
if ($LASTEXITCODE -ne 0) { throw 'C++/CLI LLVM bridge build failed.' }
& $msbuild (Join-Path $root 'src/native/runtime/runtime.vcxproj') @properties
if ($LASTEXITCODE -ne 0) { throw 'Dynamic SolRT build failed.' }
& $msbuild (Join-Path $root 'src/native/runtime/runtime.vcxproj') @properties /p:StaticRuntime=true
if ($LASTEXITCODE -ne 0) { throw 'Static SolRT build failed.' }
foreach ($static in @('false', 'true')) {
    & $msbuild (Join-Path $root 'src/native/exec-host/exec-host.vcxproj') @properties "/p:LLVMRoot=$LlvmRoot" "/p:StaticRuntime=$static"
    if ($LASTEXITCODE -ne 0) { throw 'Native ORC host build failed.' }
}
$output = Join-Path $root 'out/native/backend'
New-Item -ItemType Directory -Path $output -Force | Out-Null
$legacy = Join-Path $output 'LLVM-C.dll'
if (Test-Path -LiteralPath $legacy) { Remove-Item -LiteralPath $legacy }
Copy-Item -LiteralPath (Join-Path $root 'out/native/llvm-bridge/OpenDot.Sol.LlvmBridge.dll') -Destination $output
Copy-Item -LiteralPath (Join-Path $root 'out/native/llvm-native/OpenDot.Sol.LlvmNative.dll') -Destination $output
Copy-Item -LiteralPath (Join-Path $packs "Microsoft.NETCore.App.Host.win-x64/$($reference.Name)/runtimes/win-x64/native/ijwhost.dll") -Destination $output
foreach ($name in @('lld-link.exe', 'llvm-lib.exe')) { Copy-Item -LiteralPath (Join-Path $LlvmRoot "bin/$name") -Destination $output }
Copy-Item -LiteralPath (Join-Path $root 'out/native/exec-host/dynamic/sol-exec-host.exe') -Destination $output
Copy-Item -LiteralPath (Join-Path $root 'out/native/exec-host/static/sol-exec-host.exe') -Destination (Join-Path $output 'sol-exec-host-static.exe')
foreach ($name in @('SolRT.dll', 'SolRT.lib')) { Copy-Item -LiteralPath (Join-Path $root "out/native/runtime/dynamic/$name") -Destination $output }
Copy-Item -LiteralPath (Join-Path $root 'out/native/runtime/static/SolRT.lib') -Destination (Join-Path $output 'SolRT-static.lib')
$toolsVersion = (Get-Content (Join-Path $vs 'VC/Auxiliary/Build/Microsoft.VCToolsVersion.default.txt') -Raw).Trim()
$sdkRoot = (Get-ItemProperty 'HKLM:/SOFTWARE/Microsoft/Windows Kits/Installed Roots').KitsRoot10
$sdk = Get-ChildItem (Join-Path $sdkRoot 'Lib') -Directory | Where-Object { Test-Path (Join-Path $_.FullName 'um/x64/kernel32.lib') } | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
if (-not $sdk) { throw 'Windows SDK x64 libraries are missing.' }
$linkPaths = @((Join-Path $vs "VC/Tools/MSVC/$toolsVersion/lib/x64"), (Join-Path $sdk.FullName 'um/x64'), (Join-Path $sdk.FullName 'ucrt/x64'))
$linkPaths | Set-Content -LiteralPath (Join-Path $output 'link-paths.txt') -Encoding utf8NoBOM
Write-Host "Qualified native backend: $output"
