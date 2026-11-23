param(
    [string]$Variant
)

$ErrorActionPreference = "Stop"

$dotnetTargetSyncTrayzor = "net10.0-windows10.0.17763.0"
. "$PSScriptRoot\helpers\get-arch.ps1"
$arch = Get-Arch
$dotnetArch = switch ($arch) {
    "amd64" { "win-x64" }
    "arm64" { "win-arm64" }
    default { throw "Unknown architecture: $arch" }
}
$nativePlatform = switch ($arch) {
    "amd64" { "x64" }
    "arm64" { "ARM64" }
}
$syncthingExe = ".\syncthing\syncthing.exe"
$shellExtensionProject = ".\src\SyncTrayzor.ShellExtension\SyncTrayzor.ShellExtension.vcxproj"
$shellExtensionDll = ".\src\SyncTrayzor.ShellExtension\bin\$nativePlatform\Release\SyncTrayzor.ShellExtension.dll"
$publishDir = ".\src\SyncTrayzor\bin\Release\$dotnetTargetSyncTrayzor\$dotnetArch\publish"
$mergedDir = ".\dist"

Write-Host "Building SyncTrayzor for $Variant"

# Clean publish dir first
if (Test-Path $publishDir) {
    Remove-Item $publishDir -Recurse -Force
}

dotnet publish -c Release -p:DebugType=None -p:DebugSymbols=false -p:SelfContained=true -p:AppConfigVariant=$Variant src/SyncTrayzor/SyncTrayzor.csproj
if ($LASTEXITCODE -ne 0) {
    Write-Error "Failed to build SyncTrayzor. Exiting."
    exit $LASTEXITCODE
}

# The Explorer shell extension is native code, built with MSBuild from Visual Studio (C++ workload).
# Set SYNCTRAYZOR_SKIP_SHELL_EXTENSION=1 to build without it (the Explorer integration then isn't available).
$skipShellExtension = $env:SYNCTRAYZOR_SKIP_SHELL_EXTENSION -eq "1"
if ($skipShellExtension) {
    Write-Warning "Not building the Explorer shell extension (SYNCTRAYZOR_SKIP_SHELL_EXTENSION=1)"
}
else {
    $vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
    $msbuild = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild -find "MSBuild\**\Bin\MSBuild.exe" | Select-Object -First 1
    if (-not $msbuild) {
        Write-Error "MSBuild not found. Install Visual Studio with the 'Desktop development with C++' workload, or set SYNCTRAYZOR_SKIP_SHELL_EXTENSION=1."
        exit 1
    }
    & $msbuild $shellExtensionProject -nologo -v:minimal -p:Configuration=Release -p:Platform=$nativePlatform
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Failed to build the shell extension. Exiting."
        exit $LASTEXITCODE
    }
}

# Remove and recreate merged directory
if (Test-Path $mergedDir) {
    Remove-Item $mergedDir -Recurse -Force
}
New-Item -ItemType Directory -Path $mergedDir | Out-Null
Copy-Item "$publishDir\*" $mergedDir -Recurse -Force

$additionalFiles = @(
    $syncthingExe,
    # Also include VC++ runtime files for systems that do not have it
    "C:\Windows\System32\msvcp140.dll",
    "C:\Windows\System32\vcruntime140.dll",
    "C:\Windows\System32\vcruntime140_1.dll"
)
if (-not $skipShellExtension) {
    $additionalFiles += $shellExtensionDll
}
foreach ($file in $additionalFiles) {
    if (Test-Path $file) {
        Copy-Item $file $mergedDir -Force
    }
    else {
        Write-Error "File not found: $file"
        exit 1
    }
}
