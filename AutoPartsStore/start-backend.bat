@echo off
echo ========================================
echo FitmentOps - Backend Başlatılıyor
echo ========================================
echo.

cd Backend\AutoPartsStore.API
echo Backend dizinine geçildi...
echo.

echo Veritabanı oluşturuluyor ve API başlatılıyor...
echo Backend: http://localhost:5167
echo Swagger UI: http://localhost:5167/swagger
echo.

dotnet run
