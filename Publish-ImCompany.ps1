$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot
$env:DOTNET_CLI_HOME = Join-Path $PSScriptRoot '.dotnet'
dotnet publish .\ImCompany.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o .\publish
if ($LASTEXITCODE -ne 0) { throw '배포 파일 생성에 실패했습니다.' }
Write-Host 'publish 폴더의 ImCompany.exe를 실행하세요. wwwroot 폴더도 함께 보관하세요.'
