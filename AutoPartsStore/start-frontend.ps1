Write-Host "========================================"
Write-Host "FitmentOps - Frontend Baslatiliyor"
Write-Host "========================================"
Write-Host ""

Set-Location Frontend\client
Write-Host "Frontend dizinine gecildi..."
Write-Host ""

Write-Host "Development server baslatiliyor..."
Write-Host "Frontend: http://localhost:5173"
Write-Host ""

npm run dev
