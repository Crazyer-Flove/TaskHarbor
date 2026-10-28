$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
Push-Location $projectRoot
try {
    node -e "eval(require('fs').readFileSync('tests/model.test.cjs','utf8'))"
    if ($LASTEXITCODE -ne 0) { throw 'Domain checks failed' }
    if (Test-Path 'tests\ui.test.cjs') { node -e "eval(require('fs').readFileSync('tests/ui.test.cjs','utf8'))"; if ($LASTEXITCODE -ne 0) { throw 'UI behavior checks failed' } }
    $framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
    New-Item -ItemType Directory -Path 'build' -Force | Out-Null
    $target = Join-Path $projectRoot 'build\NativeTests.exe'
    $argsList = @('/nologo','/target:exe','/platform:x64','/langversion:5',"/out:$target",'/main:TaskHarbor.NativeTests')
    foreach ($dll in @('System.dll','System.Drawing.dll','System.Web.Extensions.dll','System.Windows.Forms.dll','System.Security.dll')) { $argsList += '/r:' + (Join-Path $framework $dll) }
    $argsList += @('src\Store.cs','src\AiClient.cs','src\DesktopServices.cs','src\WebAssets.cs','tests\NativeTests.cs')
    foreach ($source in @('src\Store.cs','src\AiClient.cs','src\DesktopServices.cs','src\WebAssets.cs','tests\NativeTests.cs')) { $absolute = Join-Path $projectRoot $source; [IO.File]::WriteAllText($absolute,[IO.File]::ReadAllText($absolute),[Text.UTF8Encoding]::new($true)) }
    & (Join-Path $framework 'csc.exe') @argsList
    if ($LASTEXITCODE -ne 0) { throw 'Native test compilation failed' }
    & $target (Join-Path $projectRoot ('build\test-data-' + [Guid]::NewGuid().ToString('N')))
    if ($LASTEXITCODE -ne 0) { throw 'Native tests failed' }
} finally { Pop-Location }
