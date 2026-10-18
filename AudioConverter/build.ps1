param([switch]$DownloadEngine)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$projectRoot = $PSScriptRoot
$vendorRoot = Join-Path $projectRoot 'vendor'
$releaseRoot = Join-Path $projectRoot 'release'
$appRoot = Join-Path $releaseRoot 'AudioConverter'
$toolsRoot = Join-Path $appRoot 'tools'
$archivePath = Join-Path $vendorRoot 'ffmpeg-release-essentials.zip'
$checksumPath = $archivePath + '.sha256'
New-Item -ItemType Directory -Path $vendorRoot, $releaseRoot, $appRoot, $toolsRoot -Force | Out-Null

if ($DownloadEngine) {
    Invoke-WebRequest -UseBasicParsing -Uri 'https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip.sha256' -OutFile $checksumPath
    Invoke-WebRequest -UseBasicParsing -Uri 'https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip' -OutFile $archivePath
}
if (!(Test-Path -LiteralPath $archivePath) -or !(Test-Path -LiteralPath $checksumPath)) {
    throw 'Engine ZIP/checksum missing. Run build.ps1 -DownloadEngine once with network access.'
}
$expected = [regex]::Match((Get-Content -LiteralPath $checksumPath -Raw), '(?i)\b[0-9a-f]{64}\b').Value
$actual = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash
if (!$expected -or $actual -ine $expected) { throw 'FFmpeg archive SHA-256 does not match the publisher checksum.' }

Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::OpenRead($archivePath)
$engineArchiveRoot = ''
try {
    foreach ($name in @('ffmpeg.exe', 'ffprobe.exe', 'LICENSE', 'README.txt')) {
        $entry = $archive.Entries | Where-Object { $_.Name -eq $name } | Select-Object -First 1
        if (!$entry) { throw "Missing archive entry: $name" }
        [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, (Join-Path $toolsRoot $name), $true)
        if ($name -eq 'ffmpeg.exe') { $engineArchiveRoot = ($entry.FullName -split '/')[0] }
    }
} finally { $archive.Dispose() }

$compiler = Join-Path ([Environment]::GetFolderPath('Windows')) 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (!(Test-Path -LiteralPath $compiler)) { throw '.NET Framework 4.8 compiler was not found.' }
$exePath = Join-Path $appRoot 'AudioConverter.exe'
& $compiler /nologo /target:winexe /platform:x64 /optimize+ /codepage:65001 `
    "/out:$exePath" "/win32manifest:$(Join-Path $projectRoot 'src\app.manifest')" `
    "/win32icon:$(Join-Path $projectRoot 'src\app.ico')" `
    /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Web.Extensions.dll `
    (Join-Path $projectRoot 'src\Program.cs') (Join-Path $projectRoot 'src\ConversionEngine.cs')
if ($LASTEXITCODE -ne 0) { throw 'C# compilation failed.' }
Copy-Item -LiteralPath (Join-Path $projectRoot 'src\App.config') -Destination ($exePath + '.config')
Copy-Item -LiteralPath (Join-Path $projectRoot 'README.md') -Destination (Join-Path $appRoot 'README.md')
Copy-Item -LiteralPath (Join-Path $projectRoot 'LICENSE') -Destination (Join-Path $appRoot 'LICENSE')
$versionText = (& (Join-Path $toolsRoot 'ffmpeg.exe') -version 2>&1 | Out-String)
if ($LASTEXITCODE -ne 0) { throw 'Bundled FFmpeg could not run.' }
$engineHash = (Get-FileHash -LiteralPath (Join-Path $toolsRoot 'ffmpeg.exe') -Algorithm SHA256).Hash
$probeHash = (Get-FileHash -LiteralPath (Join-Path $toolsRoot 'ffprobe.exe') -Algorithm SHA256).Hash
@"
Archive: $engineArchiveRoot
Downloaded from: https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip
Archive SHA-256: $actual
ffmpeg.exe SHA-256: $engineHash
ffprobe.exe SHA-256: $probeHash

$versionText
"@ | Set-Content -LiteralPath (Join-Path $toolsRoot 'BUILD-INFO.txt') -Encoding UTF8
@"
Audio Converter runs FFmpeg/FFprobe as separate executables.
The converter application source is available in AudioConverter-source.zip.
Application license: LICENSE (MIT).

FFmpeg binaries: Gyan Windows Essentials, $engineArchiveRoot
Binary distributor: https://www.gyan.dev/ffmpeg/builds/
Upstream download page: https://ffmpeg.org/download.html
Upstream source: https://git.ffmpeg.org/ffmpeg.git
Source releases: https://ffmpeg.org/releases/
Distributor build sources: https://github.com/GyanD/codexffmpeg
FFmpeg and included libraries are distributed under their own licenses.
See tools/LICENSE, tools/README.txt and tools/BUILD-INFO.txt.
For the exact FFmpeg revision and configuration, see BUILD-INFO.txt.
Keep these notices and the tools directory with the application.
"@ | Set-Content -LiteralPath (Join-Path $appRoot 'THIRD-PARTY-NOTICES.txt') -Encoding UTF8

$portablePath = Join-Path $releaseRoot 'AudioConverter-portable.zip'
if (Test-Path -LiteralPath $portablePath) { Remove-Item -LiteralPath $portablePath }
[IO.Compression.ZipFile]::CreateFromDirectory($appRoot, $portablePath, [IO.Compression.CompressionLevel]::Optimal, $true)

$sourcePath = Join-Path $releaseRoot 'AudioConverter-source.zip'
if (Test-Path -LiteralPath $sourcePath) { Remove-Item -LiteralPath $sourcePath }
$sourceArchive = [IO.Compression.ZipFile]::Open($sourcePath, [IO.Compression.ZipArchiveMode]::Create)
try {
    $sources = @('README.md', 'LICENSE', '.gitignore', 'build.ps1', 'make_icon.py',
        'src\Program.cs', 'src\ConversionEngine.cs', 'src\App.config', 'src\app.manifest', 'src\app.ico',
        'tests\IntegrationTests.cs', 'tests\run-tests.ps1')
    foreach ($relative in $sources) {
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($sourceArchive, (Join-Path $projectRoot $relative), ($relative -replace '\\', '/')) | Out-Null
    }
} finally { $sourceArchive.Dispose() }
Get-FileHash -LiteralPath $exePath, $portablePath, $sourcePath -Algorithm SHA256 |
    ForEach-Object { $_.Hash + '  ' + [IO.Path]::GetFileName($_.Path) } |
    Set-Content -LiteralPath (Join-Path $releaseRoot 'SHA256SUMS.txt') -Encoding ASCII
Write-Host "Built: $exePath"
Write-Host "Portable: $portablePath"
