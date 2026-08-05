Write-Host "========================================"
Write-Host "FitmentOps - Backend Baslatiliyor"
Write-Host "========================================"
Write-Host ""

Set-Location Backend\AutoPartsStore.API
Write-Host "Backend dizinine gecildi..."
Write-Host ""

Write-Host "Veritabani olusturuluyor ve API baslatiliyor..."
Write-Host "Backend: http://localhost:5167"
Write-Host "Swagger UI: http://localhost:5167/swagger"
Write-Host ""

dotnet run
