$ErrorActionPreference = 'Stop'
$root = 'D:\x15-fan-control'
$proj = Join-Path $root 'src\FanSilencer\FanSilencer.csproj'
$stage = Join-Path $root 'release\stage'

# user-scope .NET SDK (not on PATH)
$dotnet = "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe"
if (-not (Test-Path $dotnet)) { $dotnet = 'dotnet' }

& $dotnet publish $proj -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:DebugType=none -o $stage
if ($LASTEXITCODE -ne 0) { throw "publish failed: $LASTEXITCODE" }

Copy-Item 'C:\Program Files (x86)\ControlCenter\InsydeDCHU.dll' $stage -Force
Copy-Item (Join-Path $root 'app\config.json') $stage -Force
Copy-Item (Join-Path $root 'app\curve.json') $stage -Force
Get-ChildItem (Join-Path $root 'release') -Filter '*.txt' | ForEach-Object { Copy-Item $_.FullName $stage -Force }

$zip = Join-Path $root 'release\dist.zip'
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip -Force
Write-Output '---- stage ----'
Get-ChildItem $stage | ForEach-Object { Write-Output ("{0}  {1} KB" -f $_.Name, [int]($_.Length / 1KB)) }
Write-Output ("zip: {0} ({1:N1} MB)" -f $zip, ((Get-Item $zip).Length / 1MB))
