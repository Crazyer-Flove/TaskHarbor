param([switch]$NoRestore, [string]$OutputDirectory = 'dist')
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
Push-Location $projectRoot
try {
    if (-not $NoRestore) { & (Join-Path $PSScriptRoot 'restore.ps1'); if ($LASTEXITCODE -ne 0) { throw 'SDK restore failed' } }
    $framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
    $compiler = Join-Path $framework 'csc.exe'
    if (-not (Test-Path -LiteralPath $compiler)) { throw 'Windows x64 with .NET Framework 4.6.2+ is required.' }
    $outDir = [IO.Path]::GetFullPath((Join-Path $projectRoot $OutputDirectory))
    if (-not $outDir.StartsWith($projectRoot + [IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) { throw 'Build output must be inside the project directory.' }
    New-Item -ItemType Directory -Path $outDir,'build' -Force | Out-Null
    $temporary = Join-Path $outDir 'TaskHarbor.new.exe'
    $final = Join-Path $outDir 'TaskHarbor.exe'
    $argsList = @('/nologo','/target:winexe','/platform:x64','/optimize+','/langversion:5',"/out:$temporary",'/win32icon:assets\icon.ico','/resource:assets\icon.ico,TaskHarbor.icon.ico')
    foreach ($dll in @('System.dll','System.Core.dll','System.Drawing.dll','System.Windows.Forms.dll','System.Web.Extensions.dll','System.Security.dll')) { $argsList += '/r:' + (Join-Path $framework $dll) }
    foreach ($dll in @('Microsoft.Web.WebView2.Core.dll','Microsoft.Web.WebView2.WinForms.dll')) { $argsList += '/r:' + (Join-Path $projectRoot ('build\refs\' + $dll)) }
    foreach ($dll in @('Microsoft.Web.WebView2.Core.dll','Microsoft.Web.WebView2.WinForms.dll','WebView2Loader.dll')) { $argsList += '/resource:build\refs\' + $dll + ',' + $dll }
    foreach ($asset in @('panel.html','panel.css','panel.js','model.js','brand.png')) { $argsList += '/resource:web\' + $asset + ',TaskHarbor.web.' + $asset }
    $sources = Get-ChildItem -LiteralPath 'src' -Filter '*.cs' | Sort-Object Name
    $utf8Bom = New-Object Text.UTF8Encoding $true
    foreach ($source in $sources) {
        [IO.File]::WriteAllText($source.FullName,[IO.File]::ReadAllText($source.FullName,[Text.Encoding]::UTF8),$utf8Bom)
        $argsList += $source.FullName
    }
    $compileOutput = & $compiler @argsList 2>&1
    $compileOutput | Out-File -LiteralPath 'build\compile.log' -Encoding utf8
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $temporary)) { $compileOutput | Write-Host; throw 'Build failed. Existing release was preserved.' }
    if (Test-Path -LiteralPath $final) { Move-Item -LiteralPath $final -Destination (Join-Path $outDir ('TaskHarbor.previous-' + [DateTime]::Now.ToString('yyyyMMddHHmmssfff') + '.exe')) -Force }
    Move-Item -LiteralPath $temporary -Destination $final -Force
    $info = [ordered]@{ product='TaskHarbor'; version=[Diagnostics.FileVersionInfo]::GetVersionInfo($final).FileVersion; platform='windows-x64'; file='TaskHarbor.exe'; sha256=(Get-FileHash -LiteralPath $final -Algorithm SHA256).Hash; sizeBytes=(Get-Item -LiteralPath $final).Length; sdkVersion='1.0.4191.47' }
    $info | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $outDir 'release.json') -Encoding utf8
    Write-Host ('Built: ' + $final)
    Write-Host ('Version: ' + $info.version + '  SHA256: ' + $info.sha256)
} finally { Pop-Location }
