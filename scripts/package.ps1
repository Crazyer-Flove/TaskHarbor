$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
Push-Location $projectRoot
try {
    node -e "eval(require('fs').readFileSync('scripts/audit-source.cjs','utf8'))"
    if ($LASTEXITCODE -ne 0) { throw 'Source audit failed' }
    $manifest = Get-Content -LiteralPath 'dist\release.json' -Raw | ConvertFrom-Json
    $version = ([version]$manifest.version).ToString(3)
    if ((Get-FileHash -LiteralPath 'dist\TaskHarbor.exe' -Algorithm SHA256).Hash -ne $manifest.sha256) { throw 'Release executable does not match its manifest' }
    $stage = Join-Path $projectRoot ('build\package-' + [Guid]::NewGuid().ToString('N'))
    if (-not ([IO.Path]::GetFullPath($stage)).StartsWith($projectRoot + '\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Staging path outside project' }
    $source = Join-Path $stage 'source\TaskHarbor'
    $runtime = Join-Path $stage 'runtime'
    New-Item -ItemType Directory -Path $source,$runtime -Force | Out-Null
    $rootFiles = @('README.md','README.zh-CN.md','LICENSE','THIRD_PARTY_NOTICES.md','CONTRIBUTING.md','SECURITY.md','CHANGELOG.md','Build.cmd','.gitignore','.gitattributes','.editorconfig','design-qa.md')
    foreach ($file in $rootFiles) { Copy-Item -LiteralPath $file -Destination $source }
    foreach ($dir in @('src','web','assets','scripts','tests','docs','examples','.github')) { Copy-Item -LiteralPath $dir -Destination $source -Recurse }
    foreach ($file in @('dist\TaskHarbor.exe','dist\release.json','README.md','README.zh-CN.md','LICENSE','THIRD_PARTY_NOTICES.md')) { Copy-Item -LiteralPath $file -Destination $runtime }
    Copy-Item -LiteralPath 'docs' -Destination $runtime -Recurse
    if (-not (Test-Path -LiteralPath 'build\refs\WEBVIEW2-LICENSE.txt')) { throw 'Restore the SDK first; its redistribution license must accompany the binary.' }
    Copy-Item -LiteralPath 'build\refs\WEBVIEW2-LICENSE.txt' -Destination $runtime
    $runtimeZip = Join-Path $projectRoot ('dist\TaskHarbor-' + $version + '-windows-x64.zip')
    $sourceZip = Join-Path $projectRoot ('dist\TaskHarbor-' + $version + '-source.zip')
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    # ZipFile includes dotfiles (.github/.gitignore) that Compress-Archive can omit.
    foreach ($archivePath in @($runtimeZip,$sourceZip)) { if (Test-Path -LiteralPath $archivePath) { Remove-Item -LiteralPath $archivePath } }
    [IO.Compression.ZipFile]::CreateFromDirectory($runtime,$runtimeZip)
    [IO.Compression.ZipFile]::CreateFromDirectory((Split-Path -Parent $source),$sourceZip)
    $lines = foreach ($file in @('dist\TaskHarbor.exe',$runtimeZip,$sourceZip)) { (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant() + '  ' + (Split-Path -Leaf $file) }
    $lines | Set-Content -LiteralPath 'dist\SHA256SUMS.txt' -Encoding ascii
    Write-Host "Runtime: $runtimeZip"
    Write-Host "Source:  $sourceZip"
} finally { Pop-Location }
