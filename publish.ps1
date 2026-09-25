<#
.SYNOPSIS
    Builds the EasyRadioLink release package.

.DESCRIPTION
    Publishes the client, the server (with window), the command-line server for Windows and Linux and the setup into
    dist\EasyRadioLink-<version>\ (the version is read from Directory.Build.props), adds README.txt, LICENSE.txt and the
    Microsoft Visual C++ Redistributable, optionally signs the executables and optionally creates
    dist\EasyRadioLink-<version>.zip.

    Package layout:
        EasyRadioLink-Setup.exe                                   installer / uninstaller
        README.txt, LICENSE.txt                                   end-user readme (packaging\README.txt), GPL-3.0 licence
        VC_redist.x64.exe                                         Microsoft Visual C++ runtime (installed by the setup)
        Client\EasyRadioLink.exe                                  client (win-x64, framework-dependent single file)
        Server\EasyRadioLink.Server.exe                           server with window (win-x64, framework-dependent single file)
        ServerCommandLine-Windows\EasyRadioLink.Server.Cli.exe    command-line server (win-x64, self-contained)
        ServerCommandLine-Linux\EasyRadioLink.Server.Cli          command-line server (linux-x64, self-contained)

    Requirements: .NET 10 SDK (see global.json). Signing needs signtool.exe from the Windows SDK and a code signing
    certificate in the certificate store.

.PARAMETER Zip
    Also create dist\EasyRadioLink-<version>.zip (plus a .sha256 file) from the package folder.

.PARAMETER Sign
    Sign all executables of the package with signtool. Requires -CertSubject. Default: unsigned.

.PARAMETER CertSubject
    Subject name of the code signing certificate (signtool /n).

.PARAMETER TimestampUrl
    RFC 3161 timestamp server used when signing.

.PARAMETER SignToolPath
    Full path of signtool.exe. Default: signtool.exe from PATH or the newest Windows 10/11 SDK.

.PARAMETER NoVcRedist
    Do not download VC_redist.x64.exe (offline build). The setup then skips the runtime installation.

.PARAMETER VcRedistUrl
    Download URL of the Microsoft Visual C++ Redistributable (x64).

.EXAMPLE
    .\publish.ps1 -Zip

.EXAMPLE
    .\publish.ps1 -Zip -Sign -CertSubject "Your Name"
#>
[CmdletBinding()]
param(
    [switch]$Zip,
    [switch]$Sign,
    [string]$CertSubject,
    [string]$TimestampUrl = "http://timestamp.digicert.com",
    [string]$SignToolPath,
    [switch]$NoVcRedist,
    [string]$VcRedistUrl = "https://aka.ms/vs/17/release/vc_redist.x64.exe"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
# Invoke-WebRequest is extremely slow with the progress bar in Windows PowerShell 5.1.
$ProgressPreference = "SilentlyContinue"

$repoRoot = $PSScriptRoot
$distRoot = Join-Path $repoRoot "dist"

# Common publish parameters (same as the upstream release builds).
$commonPublishArgs = @(
    "--configuration", "Release",
    "--nologo",
    "-p:PublishReadyToRun=true",
    "-p:PublishSingleFile=true",
    "-p:DebugType=None",
    "-p:DebugSymbols=false",
    "-p:IncludeSourceRevisionInInformationalVersion=false" # no git hash in the product version
)

function Write-Step([string]$Message) {
    Write-Host ""
    Write-Host "==> $Message" -ForegroundColor Green
}

function Invoke-Dotnet([string[]]$Arguments) {
    Write-Host "dotnet $($Arguments -join ' ')" -ForegroundColor DarkGray
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($Arguments[0]) failed with exit code $LASTEXITCODE."
    }
}

function Get-ProductVersion {
    $propsPath = Join-Path $repoRoot "Directory.Build.props"
    if (-not (Test-Path -LiteralPath $propsPath)) {
        throw "Directory.Build.props not found at $propsPath."
    }

    [xml]$props = Get-Content -LiteralPath $propsPath -Raw
    $node = $props.SelectSingleNode("/Project/PropertyGroup/Version")
    if ($null -eq $node -or [string]::IsNullOrWhiteSpace($node.InnerText)) {
        throw "No <Version> found in $propsPath."
    }

    return $node.InnerText.Trim()
}

function Remove-PathIfExists([string]$Path) {
    if (Test-Path -LiteralPath $Path) {
        Remove-Item -LiteralPath $Path -Recurse -Force
    }
}

# Deletes files matching the filters directly inside $Directory (not recursive).
function Remove-TopLevelFiles([string]$Directory, [string[]]$Filters) {
    foreach ($filter in $Filters) {
        Get-ChildItem -LiteralPath $Directory -Filter $filter -File | Remove-Item -Force
    }
}

# Moves native libraries from runtimes\win-x64\native next to the executable and removes the runtimes folder.
function Invoke-FlattenNativeLibraries([string]$Directory) {
    $nativeDir = Join-Path $Directory "runtimes\win-x64\native"
    if (Test-Path -LiteralPath $nativeDir) {
        Get-ChildItem -LiteralPath $nativeDir -Filter "*.dll" -File |
            Copy-Item -Destination $Directory -Force
    }

    Remove-PathIfExists (Join-Path $Directory "runtimes")
}

function Publish-Project {
    param(
        [Parameter(Mandatory = $true)][string]$Project,
        [Parameter(Mandatory = $true)][string]$Runtime,
        [Parameter(Mandatory = $true)][string]$Output,
        [switch]$SelfContained
    )

    $projectPath = Join-Path $repoRoot $Project
    if (-not (Test-Path -LiteralPath $projectPath)) {
        throw "Project not found: $projectPath"
    }

    Remove-PathIfExists $Output
    $selfContainedArg = if ($SelfContained) { "--self-contained" } else { "--no-self-contained" }
    Invoke-Dotnet (@("publish", $projectPath, "--runtime", $Runtime, "--output", $Output, $selfContainedArg) + $commonPublishArgs)
}

function Assert-FilesExist([string]$Directory, [string[]]$RelativePaths) {
    foreach ($relative in $RelativePaths) {
        $path = Join-Path $Directory $relative
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            throw "Expected package file is missing: $relative"
        }
    }
}

function Find-SignTool {
    if ($SignToolPath) {
        if (-not (Test-Path -LiteralPath $SignToolPath -PathType Leaf)) {
            throw "signtool.exe not found at $SignToolPath."
        }
        return $SignToolPath
    }

    $command = Get-Command "signtool.exe" -ErrorAction SilentlyContinue
    if ($null -ne $command) {
        return $command.Source
    }

    $kitsBin = Join-Path ${env:ProgramFiles(x86)} "Windows Kits\10\bin"
    if (Test-Path -LiteralPath $kitsBin) {
        $candidate = Get-ChildItem -LiteralPath $kitsBin -Directory |
            Where-Object { $_.Name -match '^\d+\.\d+\.\d+\.\d+$' } |
            Sort-Object { [version]$_.Name } -Descending |
            ForEach-Object { Join-Path $_.FullName "x64\signtool.exe" } |
            Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } |
            Select-Object -First 1
        if ($null -ne $candidate) {
            return $candidate
        }
    }

    throw "signtool.exe not found. Install the Windows SDK or pass -SignToolPath."
}

function New-PackageZip([string]$SourceDirectory, [string]$ZipPath) {
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem

    Remove-PathIfExists $ZipPath
    $baseName = Split-Path -Leaf $SourceDirectory
    $sourceFull = (Resolve-Path -LiteralPath $SourceDirectory).ProviderPath.TrimEnd('\', '/')

    # Built entry by entry so the entry names always use "/" (Compress-Archive in Windows PowerShell 5.1 writes "\",
    # which breaks extraction on Linux). The zip contains the package folder itself.
    $zipStream = [System.IO.File]::Open($ZipPath, [System.IO.FileMode]::CreateNew)
    try {
        $archive = New-Object System.IO.Compression.ZipArchive($zipStream, [System.IO.Compression.ZipArchiveMode]::Create)
        try {
            foreach ($file in Get-ChildItem -LiteralPath $sourceFull -Recurse -File) {
                $relative = $file.FullName.Substring($sourceFull.Length).TrimStart('\', '/') -replace '\\', '/'
                $entryName = "$baseName/$relative"
                [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
                    $archive, $file.FullName, $entryName, [System.IO.Compression.CompressionLevel]::Optimal)
            }
        }
        finally {
            $archive.Dispose()
        }
    }
    finally {
        $zipStream.Dispose()
    }
}

# ---------------------------------------------------------------------------------------------------------------------

if ($Sign -and [string]::IsNullOrWhiteSpace($CertSubject)) {
    throw "-Sign requires -CertSubject `"<certificate subject name>`"."
}
if (-not $Sign -and -not [string]::IsNullOrWhiteSpace($CertSubject)) {
    Write-Warning "-CertSubject is ignored without -Sign."
}

if ($null -eq (Get-Command "dotnet" -ErrorAction SilentlyContinue)) {
    throw "The .NET SDK (dotnet) was not found. Install the .NET 10 SDK."
}

$version = Get-ProductVersion
$packageName = "EasyRadioLink-$version"
$packageDir = Join-Path $distRoot $packageName
$stagingDir = Join-Path $distRoot ".staging"

Write-Host "EasyRadioLink $version" -ForegroundColor Cyan
Write-Host "Package folder: $packageDir"
if ($Sign) { Write-Host "Signing:        yes ($CertSubject)" } else { Write-Host "Signing:        no" }
if ($Zip) { Write-Host "Zip:            yes" } else { Write-Host "Zip:            no" }

$signTool = $null
if ($Sign) {
    $signTool = Find-SignTool
    Write-Host "SignTool:       $signTool"
}

Remove-PathIfExists $packageDir
Remove-PathIfExists $stagingDir
New-Item -ItemType Directory -Path $packageDir -Force | Out-Null
New-Item -ItemType Directory -Path $stagingDir -Force | Out-Null

# Client
Write-Step "Publishing the client (win-x64)"
$clientDir = Join-Path $packageDir "Client"
Publish-Project -Project "EasyRadioLink.Client\EasyRadioLink.Client.csproj" -Runtime "win-x64" -Output $clientDir
Remove-TopLevelFiles $clientDir @("*.so", "*.config", "*.pdb")
Invoke-FlattenNativeLibraries $clientDir

# Server (window)
Write-Step "Publishing the server (win-x64)"
$serverDir = Join-Path $packageDir "Server"
Publish-Project -Project "EasyRadioLink.Server\EasyRadioLink.Server.csproj" -Runtime "win-x64" -Output $serverDir
Remove-TopLevelFiles $serverDir @("*.so", "*.config", "*.pdb")
Invoke-FlattenNativeLibraries $serverDir

# Command-line server - Windows
Write-Step "Publishing the command-line server (win-x64, self-contained)"
$cliWindowsDir = Join-Path $packageDir "ServerCommandLine-Windows"
Publish-Project -Project "EasyRadioLink.Server.Cli\EasyRadioLink.Server.Cli.csproj" -Runtime "win-x64" -Output $cliWindowsDir -SelfContained
Remove-TopLevelFiles $cliWindowsDir @("*.so", "*.pdb")

# Command-line server - Linux
Write-Step "Publishing the command-line server (linux-x64, self-contained)"
$cliLinuxDir = Join-Path $packageDir "ServerCommandLine-Linux"
Publish-Project -Project "EasyRadioLink.Server.Cli\EasyRadioLink.Server.Cli.csproj" -Runtime "linux-x64" -Output $cliLinuxDir -SelfContained
Remove-TopLevelFiles $cliLinuxDir @("*.dll", "*.pdb")

# Setup
Write-Step "Publishing the setup (win-x64)"
$installerStaging = Join-Path $stagingDir "Installer"
Publish-Project -Project "EasyRadioLink.Installer\EasyRadioLink.Installer.csproj" -Runtime "win-x64" -Output $installerStaging
Copy-Item -LiteralPath (Join-Path $installerStaging "EasyRadioLink-Setup.exe") -Destination $packageDir
Remove-PathIfExists $stagingDir

# Texts
Write-Step "Adding README.txt and LICENSE.txt"
Copy-Item -LiteralPath (Join-Path $repoRoot "packaging\README.txt") -Destination (Join-Path $packageDir "README.txt")
Copy-Item -LiteralPath (Join-Path $repoRoot "LICENSE") -Destination (Join-Path $packageDir "LICENSE.txt")
foreach ($notices in @("THIRD-PARTY-NOTICES.txt", "THIRD-PARTY-NOTICES.md")) {
    $noticesPath = Join-Path $repoRoot $notices
    if (Test-Path -LiteralPath $noticesPath) {
        Copy-Item -LiteralPath $noticesPath -Destination (Join-Path $packageDir "THIRD-PARTY-NOTICES.txt")
        break
    }
}

# Visual C++ runtime (opus.dll needs VCRUNTIME140.dll)
if ($NoVcRedist) {
    Write-Warning "VC_redist.x64.exe not added (-NoVcRedist). The setup will skip the Visual C++ runtime."
}
else {
    Write-Step "Downloading the Microsoft Visual C++ Redistributable (x64)"
    [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
    Invoke-WebRequest -Uri $VcRedistUrl -OutFile (Join-Path $packageDir "VC_redist.x64.exe") -UseBasicParsing
}

# Sanity check: everything the setup and the users rely on is there.
Assert-FilesExist $packageDir @(
    "EasyRadioLink-Setup.exe",
    "README.txt",
    "LICENSE.txt",
    "Client\EasyRadioLink.exe",
    "Client\radios.json",
    "Client\opus.dll",
    "Client\speexdsp.dll",
    "Server\EasyRadioLink.Server.exe",
    "ServerCommandLine-Windows\EasyRadioLink.Server.Cli.exe",
    "ServerCommandLine-Linux\EasyRadioLink.Server.Cli"
)

# Signing
if ($Sign) {
    Write-Step "Signing executables"
    $filesToSign = @(Get-ChildItem -LiteralPath $packageDir -Recurse -File -Filter "*.exe" |
        Where-Object { $_.Name -ne "VC_redist.x64.exe" })

    foreach ($file in $filesToSign) {
        Write-Host "Signing $($file.FullName)"
        & $signTool sign /a /n $CertSubject /fd sha256 /tr $TimestampUrl /td sha256 $file.FullName
        if ($LASTEXITCODE -ne 0) {
            throw "Signing failed for $($file.FullName) (signtool exit code $LASTEXITCODE)."
        }
    }
}
else {
    Write-Host ""
    Write-Host "Executables are not signed (use -Sign -CertSubject `"<name>`" to sign)." -ForegroundColor Yellow
}

# Zip
if ($Zip) {
    Write-Step "Creating the zip archive"
    $zipPath = Join-Path $distRoot "$packageName.zip"
    New-PackageZip -SourceDirectory $packageDir -ZipPath $zipPath

    $hash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
    Set-Content -LiteralPath "$zipPath.sha256" -Value "$hash  $packageName.zip" -Encoding Ascii
    Write-Host "Created $zipPath"
    Write-Host "SHA256  $hash"
}

Write-Host ""
Write-Host "Done. Package: $packageDir" -ForegroundColor Green
