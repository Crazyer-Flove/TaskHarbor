param([string]$Version = '1.0.4191.47', [switch]$Force)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$refDir = Join-Path $projectRoot 'build\refs'
$required = @('Microsoft.Web.WebView2.Core.dll','Microsoft.Web.WebView2.WinForms.dll','WebView2Loader.dll')
if (-not $Force -and ($required | Where-Object { -not (Test-Path -LiteralPath (Join-Path $refDir $_)) }).Count -eq 0) { Write-Host 'WebView2 SDK already restored.'; exit 0 }
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$packageDir = Join-Path $projectRoot ('build\packages\Microsoft.Web.WebView2.' + $Version)
New-Item -ItemType Directory -Path $packageDir,$refDir -Force | Out-Null
$package = Join-Path $packageDir 'package.nupkg'
$url = 'https://api.nuget.org/v3-flatcontainer/microsoft.web.webview2/' + $Version + '/microsoft.web.webview2.' + $Version + '.nupkg'
Invoke-WebRequest -UseBasicParsing -Uri $url -OutFile $package
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::OpenRead($package)
try {
    $mapping = @{
        'lib/net462/Microsoft.Web.WebView2.Core.dll' = 'Microsoft.Web.WebView2.Core.dll'
        'lib/net462/Microsoft.Web.WebView2.WinForms.dll' = 'Microsoft.Web.WebView2.WinForms.dll'
        'runtimes/win-x64/native/WebView2Loader.dll' = 'WebView2Loader.dll'
    }
    foreach ($source in $mapping.Keys) {
        $entry = $archive.GetEntry($source)
        # SDK releases may expose .NET Framework assets as net45 rather than net462.
        if (-not $entry) { $entry = $archive.GetEntry($source.Replace('net462','net45')) }
        if (-not $entry) { throw "SDK file missing from package: $source" }
        [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, (Join-Path $refDir $mapping[$source]), $true)
    }
    foreach ($entry in $archive.Entries | Where-Object { $_.FullName -match '(^|/)(LICENSE|License)(\.txt|\.md)?$' }) {
        [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, (Join-Path $refDir 'WEBVIEW2-LICENSE.txt'), $true)
    }
} finally { $archive.Dispose() }
Write-Host "Restored Microsoft.Web.WebView2 $Version from NuGet."
exit 0
