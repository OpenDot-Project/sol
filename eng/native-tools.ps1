param([ValidateSet('Cleanup', 'Inspect')][string]$Mode = 'Inspect')
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path $PSScriptRoot -Parent
if (-not $env:LLVM_ROOT) { throw 'LLVM_ROOT is required for native project inspection.' }
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
$vs = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vs) { throw 'MSVC is required for native project inspection.' }
$platforms = Get-ChildItem (Join-Path $vs 'MSBuild/Microsoft/VC') -Directory | Sort-Object Name -Descending
$toolset = $null
foreach ($platform in $platforms) {
    $path = Join-Path $platform.FullName 'Platforms/x64/PlatformToolsets'
    if (Test-Path $path) { $toolset = Get-ChildItem $path -Directory | Where-Object Name -In @('v145', 'v143') | Sort-Object Name -Descending | Select-Object -First 1 }
    if ($toolset) { break }
}
if (-not $toolset) { throw 'The native solution requires MSVC v143 or v145.' }
$packs = Join-Path (Split-Path (Get-Command dotnet).Source -Parent) 'packs'
$reference = Get-ChildItem (Join-Path $packs 'Microsoft.NETCore.App.Ref') -Directory | Where-Object Name -Like '10.*' | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
if (-not $reference) { throw '.NET 10 reference packs are missing.' }
$properties = "Configuration=Release;Platform=x64;PlatformToolset=$($toolset.Name);DotNetPackRoot=$packs;ReferencePackVersion=$($reference.Name);LLVMRoot=$env:LLVM_ROOT;TestHarness=true;StaticRuntime=true"
$sourceFiles = 'llvm-bridge/Compiler.cpp;llvm-bridge/NativeCompiler.cpp;llvm-bridge/NativeCompiler.h;runtime/SolRT.cpp;runtime/SolRT.h;runtime/Core.cpp;runtime/Core.h;runtime/Arrays.cpp;runtime/Gc.cpp;runtime/Gc.h;runtime/GcConfig.cpp;runtime/GcInternal.h;runtime/GcWorker.cpp;runtime/GcModel.cpp;runtime/NativeScope.h;runtime/Harness.cpp;runtime/GcHarness.cpp;exec-host/Main.cpp;launcher/main.cpp'
$arguments = @('src/native/Sol.Native.sln', '--no-build', '--no-updates', '--disable-settings-layers=GlobalAll;GlobalPerProduct;SolutionPersonal;ProjectPersonal', "--toolset-path=$(Join-Path $vs 'MSBuild/Current/Bin/MSBuild.exe')", "--properties=$properties", '--caches-home=out/jetbrains/cache', "--include=$sourceFiles")
Push-Location $root
try {
    New-Item -ItemType Directory -Path out/jetbrains -Force | Out-Null
    if ($Mode -eq 'Cleanup') {
        # Formatting only: do not apply semantic cleanup fixes to the ABI or managed adapter.
        & dotnet jb cleanupcode @arguments '--profile=Built-in: Reformat Code'
        if ($LASTEXITCODE -ne 0) { throw 'Native CleanupCode failed.' }
    } else {
        & dotnet jb inspectcode @arguments '--format=Sarif' '--output=out/jetbrains/native.sarif.json' '--severity=WARNING' '--jobs=2'
        if ($LASTEXITCODE -ne 0) { throw 'Native InspectCode failed.' }
        $report = Get-Content out/jetbrains/native.sarif.json -Raw | ConvertFrom-Json
        if ($report.version -ne '2.1.0' -or @($report.runs).Count -eq 0) { throw 'Invalid InspectCode SARIF output.' }
        $results = @($report.runs | ForEach-Object { $_.results })
        if ($results | Where-Object { $_.level -eq 'error' }) { throw 'Native InspectCode reported errors; see out/jetbrains/native.sarif.json.' }
        Write-Host "Native InspectCode completed: $($results.Count) warning/error findings."
    }
} finally { Pop-Location }
