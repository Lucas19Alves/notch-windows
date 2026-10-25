# Gera o instalador em artifacts\NotchSetup-<versão>.exe
# Requer: .NET 10 SDK e Inno Setup 6 (winget install JRSoftware.InnoSetup)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

# um único .exe com o .NET embutido: quem instala não precisa instalar mais nada
dotnet publish NotchApp.csproj -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
    -o artifacts\publish

$iscc = @("$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe", "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe") |
    Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw 'Inno Setup 6 não encontrado. Instale com: winget install JRSoftware.InnoSetup' }
& $iscc installer\Notch.iss
