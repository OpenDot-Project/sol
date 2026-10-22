param([string]$OutputDirectory = 'out/packages')
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path $PSScriptRoot -Parent
Push-Location $root
try {
    [xml]$props = Get-Content -LiteralPath Directory.Build.props -Raw
    $version = [string]$props.Project.PropertyGroup.Version
    $runtime = Get-Content -LiteralPath eng/runtime.json -Raw | ConvertFrom-Json
    $name = "sol-$version-windows-x86_64"
    $stage = Join-Path $root "out/stage/$name-$([guid]::NewGuid().ToString('N'))/$name"
    foreach ($directory in @('bin', 'managed', 'runtime', 'licenses')) {
        New-Item -ItemType Directory -Path (Join-Path $stage $directory) -Force | Out-Null
    }
    & dotnet publish src/managed/OpenDot.Sol.Cli/OpenDot.Sol.Cli.csproj --configuration Release --no-restore -p:UseAppHost=false -p:UseSharedCompilation=false --output (Join-Path $stage 'managed')
    if ($LASTEXITCODE -ne 0) { throw 'Managed publish failed.' }

    & (Join-Path $PSScriptRoot 'build-launcher.ps1')
    Copy-Item -LiteralPath out/native/launcher/solc.exe -Destination (Join-Path $stage 'bin/solc.exe')

    $cache = Join-Path $root 'out/downloads'
    New-Item -ItemType Directory -Path $cache -Force | Out-Null
    $zip = Join-Path $cache "dotnet-runtime-$($runtime.version)-win-x64.zip"
    if (-not (Test-Path -LiteralPath $zip)) { Invoke-WebRequest $runtime.url -OutFile $zip }
    if ((Get-FileHash -LiteralPath $zip -Algorithm SHA512).Hash -ne $runtime.sha512) { throw 'Private runtime SHA-512 mismatch.' }
    Expand-Archive -LiteralPath $zip -DestinationPath (Join-Path $stage 'runtime')
    foreach ($file in @('LICENSE', 'LICENSE-LLVM-exception', 'NOTICE')) {
        Copy-Item -LiteralPath $file -Destination (Join-Path $stage "licenses/$file")
    }
    Copy-Item -LiteralPath CHANGELOG.md -Destination (Join-Path $stage 'CHANGELOG.md')

    $launcher = Join-Path $stage 'bin/solc.exe'
    $reported = & $launcher --version
    if ($LASTEXITCODE -ne 0 -or $reported -ne "solc $version") { throw 'Private runtime launcher smoke failed.' }
    $smoke = Join-Path $stage 'smoke.cst.json'
    & $launcher tests/fixtures/main.sol --emit=cst -o $smoke
    if ($LASTEXITCODE -ne 0) { throw 'Packaged CST smoke failed.' }
    Remove-Item -LiteralPath $smoke
    $astSmoke = Join-Path $stage 'smoke.ast.json'
    & $launcher tests/fixtures/declarations.sol --emit=ast -o $astSmoke
    if ($LASTEXITCODE -ne 0) { throw 'Packaged AST smoke failed.' }
    $ast = Get-Content -LiteralPath $astSmoke -Raw | ConvertFrom-Json
    if ($ast.schema -ne 1 -or $ast.module -ne 'fixtures.models') { throw 'Packaged AST content mismatch.' }
    Remove-Item -LiteralPath $astSmoke

    $firSmoke = Join-Path $stage 'smoke.fir.json'
    $nativeSample = 'spec/examples/native/FirstProgram.sol'
    & $launcher $nativeSample --emit=fir -o $firSmoke
    if ($LASTEXITCODE -ne 0) { throw 'Packaged FIR smoke failed.' }
    $fir = Get-Content -LiteralPath $firSmoke -Raw | ConvertFrom-Json
    if ($fir.schema -ne 1 -or -not $fir.verified) { throw 'Packaged FIR content mismatch.' }
    Remove-Item -LiteralPath $firSmoke
    # This component contains the frontend and private CLR, without native dependencies.
    # An installed development backend must not make the frontend package smoke pass.
    $previousBackend = $env:SOLC_NATIVE_ROOT
    try {
        $env:SOLC_NATIVE_ROOT = Join-Path $stage 'managed/native'
        $llvmSmoke = Join-Path $stage 'smoke.ll'
        $llvmDiagnostic = & $launcher $nativeSample --emit=llvm-ir -o $llvmSmoke 2>&1
        if ($LASTEXITCODE -ne 1 -or "$llvmDiagnostic" -notmatch 'native backend is not installed' -or (Test-Path -LiteralPath $llvmSmoke)) {
            throw 'Frontend-only package must reject LLVM output without producing an artifact.'
        }
        # GitHub's pwsh wrapper forwards LASTEXITCODE; the validated rejection is success here.
        $global:LASTEXITCODE = 0
    } finally { $env:SOLC_NATIVE_ROOT = $previousBackend }

    $inventory = @(Get-ChildItem -LiteralPath $stage -File -Recurse | Sort-Object FullName | ForEach-Object {
        $relative = [IO.Path]::GetRelativePath($stage, $_.FullName).Replace('\', '/')
        if ($relative -match '(^|/)(\.codex|\.dotnet|obj)(/|$)') { throw 'Private path in package.' }
        @{ path = $relative; size = $_.Length; sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
    })
    $manifest = @{ schema_version = 1; product = 'solc'; version = $version; target = 'windows-x86_64'; private_runtime = $runtime.version; capabilities = @('cst-frontend', 'structural-ast', 'verified-fir-subset'); files = $inventory }
    $manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $stage 'component.json') -Encoding utf8NoBOM
    $output = [IO.Path]::GetFullPath((Join-Path $root $OutputDirectory))
    New-Item -ItemType Directory -Path $output -Force | Out-Null
    $archive = Join-Path $output "$name.zip"
    if (Test-Path -LiteralPath $archive) { throw "Refusing to overwrite a versioned archive: $archive" }
    $stream = [IO.File]::Open($archive, [IO.FileMode]::CreateNew)
    $zipArchive = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($file in Get-ChildItem -LiteralPath $stage -Recurse -File | Sort-Object FullName) {
            $relative = [IO.Path]::GetRelativePath($stage, $file.FullName).Replace('\', '/')
            $entry = $zipArchive.CreateEntry("$name/$relative", [IO.Compression.CompressionLevel]::Optimal)
            $entry.LastWriteTime = [DateTimeOffset]::new(1980, 1, 1, 0, 0, 0, [TimeSpan]::Zero)
            $input = [IO.File]::OpenRead($file.FullName)
            $target = $entry.Open()
            try { $input.CopyTo($target) } finally { $target.Dispose(); $input.Dispose() }
        }
    } finally { $zipArchive.Dispose(); $stream.Dispose() }
    $sha = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
    "$sha  $name.zip" | Set-Content -LiteralPath "$archive.sha256" -Encoding ascii
    Write-Host "Package verified: $archive"
} finally { Pop-Location }
