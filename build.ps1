$ErrorActionPreference = "Stop"

$project = Join-Path $PSScriptRoot "DockWebCalendar\DockWebCalendar.csproj"

dotnet restore $project
if ($LASTEXITCODE -ne 0) { throw "dotnet restore failed" }

dotnet build $project -c Release --no-restore
if ($LASTEXITCODE -ne 0) { throw "dotnet build failed" }

Write-Host "Build completed successfully."