@echo off
title Sanal Ekran Surucu Kaldirma (SecondScreen)
net session >nul 2>&1
if %errorLevel% neq 0 (
    echo Yonetici haklari ile yeniden baslatiliyor...
    powershell -NoProfile -ExecutionPolicy Bypass -Command "Start-Process cmd.exe -ArgumentList '/c \"\"%~f0\"\"' -Verb RunAs"
    exit /b
)

echo ===================================================
echo  SecondScreen - Sanal Ekran Cihazi Kaldirma
echo ===================================================
echo.

cd /d "C:\VirtualDisplayDriver"
devcon.exe remove Root\MttVDD
pnputil /delete-driver MttVDD.inf /uninstall /force

echo.
echo [TAMAM] Sanal Ekran basariyla kaldirildi.
pause

