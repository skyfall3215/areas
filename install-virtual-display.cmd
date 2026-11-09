@echo off
title Sanal Ekran Surucu Kurulumu (SecondScreen)
:: Yonetici haklari kontrolu
net session >nul 2>&1
if %errorLevel% neq 0 (
    echo Yonetici haklari ile yeniden baslatiliyor...
    powershell -NoProfile -ExecutionPolicy Bypass -Command "Start-Process cmd.exe -ArgumentList '/c \"\"%~f0\"\"' -Verb RunAs"
    exit /b
)

echo ===================================================
echo  SecondScreen - Sanal Ekran Surucusu Kurulumu (IddCx)
echo ===================================================
echo.

cd /d "C:\VirtualDisplayDriver"

echo [1/2] Surucu paketi sisteme tanitiliyor (pnputil)...
pnputil /add-driver MttVDD.inf /install

echo.
echo [2/2] Sanal Ekran Cihazi olusturuluyor (devcon)...
devcon.exe install MttVDD.inf Root\MttVDD

echo.
echo ===================================================
echo Kurulum tamamlandi!
echo Windows Görüntü Ayarlari (Ayarlar ^> Sistem ^> Monitör)
echo menusunde 2. Ekranin geldigini dogrulayin.
echo ===================================================
echo.
pause

