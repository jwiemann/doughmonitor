$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

Get-CimInstance Win32_Process |
    Where-Object { $_.Name -eq 'SourdoughMonitor.exe' } |
    ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }

dotnet build SourdoughMonitor.csproj -nologo -v q
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

dotnet --no-build --nologo bin/Debug/net8.0/SourdoughMonitor.dll
