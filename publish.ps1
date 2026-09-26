<#
.SYNOPSIS
    Builds the EasyRadioLink release downloads.

.DESCRIPTION
    Publishes the client, the server with window and the command-line server for Windows and Linux (the version is
    read from Directory.Build.props) and puts them into three package folders in dist\. Every package also contains
    README.txt (packaging\README.txt), LICENSE.txt (LICENSE) and THIRD-PARTY-NOTICES.txt.

        EasyRadioLink-Client-<version>\           client (win-x64, framework-dependent single file + native DLLs)
        EasyRadioLink-Server-<version>-Windows\   Server\        server with window (win-x64, framework-dependent)
                                                  CommandLine\   command-line server (win-x64, self-contained)
        EasyRadioLink-Server-<version>-Linux\     command-line server (linux-x64, self-contained)

    With -Zip these become the three release downloads:

        dist\EasyRadioLink-Client-<version>.zip            top folder EasyRadioLink-Client-<version>\
        dist\EasyRadioLink-Server-<version>-Windows.zip    top folder EasyRadioLink-Server-<version>\
        dist\EasyRadioLink-Server-<version>-Linux.tar.gz   top folder EasyRadioLink-Server-<version>/ (executable 0755)

    The .tar.gz needs PowerShell 7.3 or newer (System.Formats.Tar). In Windows PowerShell 5.1 the Linux download is
    created as EasyRadioLink-Server-<version>-Linux.zip instead, which cannot carry the executable bit.

    With -Installer the installer package dist\EasyRadioLink-<version>\ is built as well (EasyRadioLink-Setup.exe,
    Client\, Server\, ServerCommandLine-Windows\, ServerCommandLine-Linux\, the texts and the Microsoft Visual C++
    Redistributable). It is not part of the release downloads and is never zipped.

    Requirements: .NET 10 SDK (see global.json). Signing needs signtool.exe from the Windows SDK and a code signing
    certificate in the certificate store. Runs in Windows PowerShell 5.1 and PowerShell 7.

.PARAMETER Zip
    Also create the three release downloads (.zip / .tar.gz) from the package folders.

.PARAMETER Installer
    Also build the installer package dist\EasyRadioLink-<version>\ with EasyRadioLink-Setup.exe.

.PARAMETER Sign
    Sign all executables with signtool. Requires -CertSubject. Default: unsigned.

.PARAMETER CertSubject
    Subject name of the code signing certificate (signtool /n).

.PARAMETER TimestampUrl
    RFC 3161 timestamp server used when signing.

.PARAMETER SignToolPath
    Full path of signtool.exe. Default: signtool.exe from PATH or the newest Windows 10/11 SDK.

.PARAMETER NoVcRedist
    With -Installer: do not download VC_redist.x64.exe (offline build). The setup then skips the runtime installation.

.PARAMETER VcRedistUrl
    Download URL of the Microsoft Visual C++ Redistributable (x64), used with -Installer.

.EXAMPLE
    .\publish.ps1 -Zip

.EXAMPLE
    .\publish.ps1 -Zip -Sign -CertSubject "Your Name"

.EXAMPLE
    .\publish.ps1 -Installer
#>
[CmdletBinding()]
param(
    [switch]$Zip,
    [switch]$Installer,
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

# The command-line server uses the invariant culture anyway; invariant globalization also means the self-contained
# Linux build does not need libicu on the host.
$cliPublishArgs = @("-p:InvariantGlobalization=true")

# Texts in every package: source in the repository -> file name in the package.
$packageTexts = @(
    @{ Source = "packaging\README.txt"; Name = "README.txt" },
    @{ Source = "LICENSE"; Name = "LICENSE.txt" },
    @{ Source = "THIRD-PARTY-NOTICES.txt"; Name = "THIRD-PARTY-NOTICES.txt" }
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
        [switch]$SelfContained,
        [string[]]$ExtraArguments = @()
    )

    $projectPath = Join-Path $repoRoot $Project
    if (-not (Test-Path -LiteralPath $projectPath)) {
        throw "Project not found: $projectPath"
    }

    Remove-PathIfExists $Output
    $selfContainedArg = if ($SelfContained) { "--self-contained" } else { "--no-self-contained" }
    Invoke-Dotnet (@("publish", $projectPath, "--runtime", $Runtime, "--output", $Output, $selfContainedArg) +
        $commonPublishArgs + $ExtraArguments)
}

function Assert-FilesExist([string]$Directory, [string[]]$RelativePaths) {
    foreach ($relative in $RelativePaths) {
        $path = Join-Path $Directory $relative
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            throw "Expected file is missing: $path"
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

# Copies the content of $Source (not the folder itself) into $Destination.
function Copy-DirectoryContent([string]$Source, [string]$Destination) {
    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    Get-ChildItem -LiteralPath $Source -Force | Copy-Item -Destination $Destination -Recurse -Force
}

# Copies README.txt, LICENSE.txt and THIRD-PARTY-NOTICES.txt into a package, with CRLF line endings for Windows
# (Notepad) or LF for Linux.
function Add-PackageTexts([string]$Directory, [switch]$UnixLineEndings) {
    foreach ($packageText in $packageTexts) {
        $text = [System.IO.File]::ReadAllText((Join-Path $repoRoot $packageText.Source)) -replace "`r`n", "`n"
        if (-not $UnixLineEndings) {
            $text = $text -replace "`n", "`r`n"
        }

        [System.IO.File]::WriteAllText((Join-Path $Directory $packageText.Name), $text,
            (New-Object System.Text.UTF8Encoding($false)))
    }
}

function New-PackageZip([string]$SourceDirectory, [string]$ZipPath, [string]$RootName) {
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem

    Remove-PathIfExists $ZipPath
    $sourceFull = (Resolve-Path -LiteralPath $SourceDirectory).ProviderPath.TrimEnd('\', '/')

    # Built entry by entry so the entry names always use "/" (Compress-Archive in Windows PowerShell 5.1 writes "\",
    # which breaks extraction on Linux). All entries are inside the top folder $RootName.
    $zipStream = [System.IO.File]::Open($ZipPath, [System.IO.FileMode]::CreateNew)
    try {
        $archive = New-Object System.IO.Compression.ZipArchive($zipStream, [System.IO.Compression.ZipArchiveMode]::Create)
        try {
            foreach ($file in Get-ChildItem -LiteralPath $sourceFull -Recurse -File) {
                $relative = $file.FullName.Substring($sourceFull.Length).TrimStart('\', '/') -replace '\\', '/'
                $entryName = "$RootName/$relative"
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

# True if this PowerShell can write tar archives (System.Formats.Tar: PowerShell 7.3+ / .NET 7+). Loads the assembly.
function Test-TarSupport {
    try {
        [void][System.Reflection.Assembly]::Load("System.Formats.Tar")
    }
    catch {
        return $false
    }

    return $null -ne ("System.Formats.Tar.TarWriter" -as [type])
}

# Creates a .tar.gz with all entries inside the top folder $RootName. The Unix permissions are set explicitly: 0755 for
# folders and the files named in $Executables (relative paths), 0644 for all other files. Needs Test-TarSupport first.
function New-PackageTarGz([string]$SourceDirectory, [string]$TarGzPath, [string]$RootName, [string[]]$Executables) {
    Add-Type -AssemblyName System.IO.Compression

    Remove-PathIfExists $TarGzPath
    $sourceFull = (Resolve-Path -LiteralPath $SourceDirectory).ProviderPath.TrimEnd('\', '/')
    $directoryMode = [System.IO.UnixFileMode]493 # 0755
    $executableMode = [System.IO.UnixFileMode]493 # 0755
    $fileMode = [System.IO.UnixFileMode]420 # 0644

    $fileStream = [System.IO.File]::Open($TarGzPath, [System.IO.FileMode]::CreateNew)
    try {
        $gzip = New-Object System.IO.Compression.GZipStream($fileStream, [System.IO.Compression.CompressionLevel]::Optimal)
        try {
            $tar = New-Object System.Formats.Tar.TarWriter($gzip, [System.Formats.Tar.TarEntryFormat]::Pax, $true)
            try {
                $rootEntry = [System.Formats.Tar.PaxTarEntry]::new([System.Formats.Tar.TarEntryType]::Directory, "$RootName/")
                $rootEntry.Mode = $directoryMode
                $rootEntry.ModificationTime = [DateTimeOffset](Get-Item -LiteralPath $sourceFull).LastWriteTimeUtc
                $tar.WriteEntry($rootEntry)

                foreach ($item in Get-ChildItem -LiteralPath $sourceFull -Recurse | Sort-Object FullName) {
                    $relative = $item.FullName.Substring($sourceFull.Length).TrimStart('\', '/') -replace '\\', '/'
                    if ($item.PSIsContainer) {
                        $entry = [System.Formats.Tar.PaxTarEntry]::new([System.Formats.Tar.TarEntryType]::Directory,
                            "$RootName/$relative/")
                        $entry.Mode = $directoryMode
                        $entry.ModificationTime = [DateTimeOffset]$item.LastWriteTimeUtc
                        $tar.WriteEntry($entry)
                        continue
                    }

                    $entry = [System.Formats.Tar.PaxTarEntry]::new([System.Formats.Tar.TarEntryType]::RegularFile,
                        "$RootName/$relative")
                    $entry.Mode = if ($Executables -contains $relative) { $executableMode } else { $fileMode }
                    $entry.ModificationTime = [DateTimeOffset]$item.LastWriteTimeUtc
                    $data = [System.IO.File]::OpenRead($item.FullName)
                    try {
                        $entry.DataStream = $data
                        $tar.WriteEntry($entry)
                    }
                    finally {
                        $data.Dispose()
                    }
                }
            }
            finally {
                $tar.Dispose()
            }
        }
        finally {
            $gzip.Dispose()
        }
    }
    finally {
        $fileStream.Dispose()
    }
}

# ---------------------------------------------------------------------------------------------------------------------

if ($Sign -and [string]::IsNullOrWhiteSpace($CertSubject)) {
    throw "-Sign requires -CertSubject `"<certificate subject name>`"."
}
if (-not $Sign -and -not [string]::IsNullOrWhiteSpace($CertSubject)) {
    Write-Warning "-CertSubject is ignored without -Sign."
}
if ($NoVcRedist -and -not $Installer) {
    Write-Warning "-NoVcRedist is ignored without -Installer (only the installer package contains VC_redist.x64.exe)."
}

if ($null -eq (Get-Command "dotnet" -ErrorAction SilentlyContinue)) {
    throw "The .NET SDK (dotnet) was not found. Install the .NET 10 SDK."
}

foreach ($packageText in $packageTexts) {
    Assert-FilesExist $repoRoot @($packageText.Source)
}

$version = Get-ProductVersion
$clientName = "EasyRadioLink-Client-$version"
$serverName = "EasyRadioLink-Server-$version"

$clientDir = Join-Path $distRoot $clientName
$serverWindowsDir = Join-Path $distRoot "$serverName-Windows"
$serverLinuxDir = Join-Path $distRoot "$serverName-Linux"
$installerDir = Join-Path $distRoot "EasyRadioLink-$version"

$clientZip = Join-Path $distRoot "$clientName.zip"
$serverWindowsZip = Join-Path $distRoot "$serverName-Windows.zip"
$serverLinuxTarGz = Join-Path $distRoot "$serverName-Linux.tar.gz"
$serverLinuxZip = Join-Path $distRoot "$serverName-Linux.zip"

$stagingDir = Join-Path $distRoot ".staging"
$clientBuild = Join-Path $stagingDir "Client"
$serverBuild = Join-Path $stagingDir "Server"
$cliWindowsBuild = Join-Path $stagingDir "CommandLine-Windows"
$cliLinuxBuild = Join-Path $stagingDir "CommandLine-Linux"
$setupBuild = Join-Path $stagingDir "Setup"

Write-Host "EasyRadioLink $version" -ForegroundColor Cyan
Write-Host "Output folder:  $distRoot"
if ($Sign) { Write-Host "Signing:        yes ($CertSubject)" } else { Write-Host "Signing:        no" }
if ($Zip) { Write-Host "Archives:       yes" } else { Write-Host "Archives:       no" }
if ($Installer) { Write-Host "Installer:      yes" } else { Write-Host "Installer:      no" }

$signTool = $null
if ($Sign) {
    $signTool = Find-SignTool
    Write-Host "SignTool:       $signTool"
}

# Start clean. Also removes the outputs of older versions of this script (one zip with a .sha256 file).
$previousOutputs = @($stagingDir, $clientDir, $serverWindowsDir, $serverLinuxDir, $clientZip, $serverWindowsZip,
    $serverLinuxTarGz, $serverLinuxZip, (Join-Path $distRoot "EasyRadioLink-$version.zip"),
    (Join-Path $distRoot "EasyRadioLink-$version.zip.sha256"))
if ($Installer) {
    $previousOutputs += $installerDir
}
foreach ($path in $previousOutputs) {
    Remove-PathIfExists $path
}
New-Item -ItemType Directory -Path $stagingDir -Force | Out-Null

# Client
Write-Step "Publishing the client (win-x64)"
Publish-Project -Project "EasyRadioLink.Client\EasyRadioLink.Client.csproj" -Runtime "win-x64" -Output $clientBuild
Remove-TopLevelFiles $clientBuild @("*.so", "*.config", "*.pdb")
Invoke-FlattenNativeLibraries $clientBuild

# Server (window)
Write-Step "Publishing the server (win-x64)"
Publish-Project -Project "EasyRadioLink.Server\EasyRadioLink.Server.csproj" -Runtime "win-x64" -Output $serverBuild
Remove-TopLevelFiles $serverBuild @("*.so", "*.config", "*.pdb")
Invoke-FlattenNativeLibraries $serverBuild

# Command-line server - Windows
Write-Step "Publishing the command-line server (win-x64, self-contained)"
Publish-Project -Project "EasyRadioLink.Server.Cli\EasyRadioLink.Server.Cli.csproj" -Runtime "win-x64" `
    -Output $cliWindowsBuild -SelfContained -ExtraArguments $cliPublishArgs
Remove-TopLevelFiles $cliWindowsBuild @("*.so", "*.pdb")

# Command-line server - Linux
Write-Step "Publishing the command-line server (linux-x64, self-contained)"
Publish-Project -Project "EasyRadioLink.Server.Cli\EasyRadioLink.Server.Cli.csproj" -Runtime "linux-x64" `
    -Output $cliLinuxBuild -SelfContained -ExtraArguments $cliPublishArgs
Remove-TopLevelFiles $cliLinuxBuild @("*.dll", "*.pdb")

# Setup (installer package only)
if ($Installer) {
    Write-Step "Publishing the setup (win-x64)"
    Publish-Project -Project "EasyRadioLink.Installer\EasyRadioLink.Installer.csproj" -Runtime "win-x64" -Output $setupBuild
}

# Sanity check: everything the users (and the setup) rely on is there.
Assert-FilesExist $stagingDir @(
    "Client\EasyRadioLink.exe",
    "Client\radios.json",
    "Client\opus.dll",
    "Client\speexdsp.dll",
    "Server\EasyRadioLink.Server.exe",
    "CommandLine-Windows\EasyRadioLink.Server.Cli.exe",
    "CommandLine-Linux\EasyRadioLink.Server.Cli"
)
if ($Installer) {
    Assert-FilesExist $stagingDir @("Setup\EasyRadioLink-Setup.exe")
}

# Signing (before the files are copied into the packages)
if ($Sign) {
    Write-Step "Signing executables"
    $filesToSign = @(Get-ChildItem -LiteralPath $stagingDir -Recurse -File -Filter "*.exe")

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

# Packages
Write-Step "Assembling the packages"
Copy-DirectoryContent $clientBuild $clientDir
Add-PackageTexts $clientDir

Copy-DirectoryContent $serverBuild (Join-Path $serverWindowsDir "Server")
Copy-DirectoryContent $cliWindowsBuild (Join-Path $serverWindowsDir "CommandLine")
Add-PackageTexts $serverWindowsDir

Copy-DirectoryContent $cliLinuxBuild $serverLinuxDir
Add-PackageTexts $serverLinuxDir -UnixLineEndings

if ($Installer) {
    Write-Step "Assembling the installer package"
    Copy-DirectoryContent $clientBuild (Join-Path $installerDir "Client")
    Copy-DirectoryContent $serverBuild (Join-Path $installerDir "Server")
    Copy-DirectoryContent $cliWindowsBuild (Join-Path $installerDir "ServerCommandLine-Windows")
    Copy-DirectoryContent $cliLinuxBuild (Join-Path $installerDir "ServerCommandLine-Linux")
    Copy-Item -LiteralPath (Join-Path $setupBuild "EasyRadioLink-Setup.exe") -Destination $installerDir
    Add-PackageTexts $installerDir

    # Visual C++ runtime (opus.dll and WebRtcVad.dll need VCRUNTIME140.dll)
    if ($NoVcRedist) {
        Write-Warning "VC_redist.x64.exe not added (-NoVcRedist). The setup will skip the Visual C++ runtime."
    }
    else {
        Write-Step "Downloading the Microsoft Visual C++ Redistributable (x64)"
        [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
        Invoke-WebRequest -Uri $VcRedistUrl -OutFile (Join-Path $installerDir "VC_redist.x64.exe") -UseBasicParsing
    }
}

Remove-PathIfExists $stagingDir

# Release downloads
$outputs = @($clientDir, $serverWindowsDir, $serverLinuxDir)
if ($Installer) {
    $outputs += $installerDir
}

if ($Zip) {
    Write-Step "Creating the release downloads"
    New-PackageZip -SourceDirectory $clientDir -ZipPath $clientZip -RootName $clientName
    New-PackageZip -SourceDirectory $serverWindowsDir -ZipPath $serverWindowsZip -RootName $serverName
    $outputs = @($clientZip, $serverWindowsZip)

    if (Test-TarSupport) {
        New-PackageTarGz -SourceDirectory $serverLinuxDir -TarGzPath $serverLinuxTarGz -RootName $serverName `
            -Executables @("EasyRadioLink.Server.Cli")
        $outputs += $serverLinuxTarGz
    }
    else {
        Write-Warning ("PowerShell $($PSVersionTable.PSVersion) cannot write .tar.gz files (PowerShell 7.3 or newer " +
            "can). The Linux download is created as $(Split-Path -Leaf $serverLinuxZip) instead: a zip file does not " +
            "keep the executable bit, so run 'chmod +x EasyRadioLink.Server.Cli' after extracting it.")
        New-PackageZip -SourceDirectory $serverLinuxDir -ZipPath $serverLinuxZip -RootName $serverName
        $outputs += $serverLinuxZip
    }

    if ($Installer) {
        $outputs += $installerDir
    }
}

Write-Host ""
Write-Host "Done:" -ForegroundColor Green
foreach ($output in $outputs) {
    if (Test-Path -LiteralPath $output -PathType Leaf) {
        Write-Host ("  {0}  ({1:N1} MB)" -f $output, ((Get-Item -LiteralPath $output).Length / 1MB))
    }
    else {
        Write-Host "  $output\"
    }
}
