@echo off
rem Register the .litematic preview handler for the current user (no admin needed).
set CLSID={8E5B1A47-3F2E-4C6D-9A1B-7C2D5E8F0A31}
set DLL=%~dp0ShellPreview\LitematicaViewer.ShellPreview.dll
if not exist "%DLL%" (echo ERROR: %DLL% not found & pause & exit /b 1)

reg add "HKCU\Software\Classes\.litematic\shellex\{8895b1c6-b41f-4c1c-a562-0d564250836f}" /ve /d "%CLSID%" /f
reg add "HKCU\Software\Classes\CLSID\%CLSID%" /ve /d "Litematica Preview Handler" /f
reg add "HKCU\Software\Classes\CLSID\%CLSID%\InprocServer32" /ve /d "%DLL%" /f
reg add "HKCU\Software\Classes\CLSID\%CLSID%\InprocServer32" /v ThreadingModel /d "Apartment" /f
reg add "HKCU\Software\Classes\CLSID\%CLSID%\AppID" /ve /d "%CLSID%" /f
reg add "HKCU\Software\Classes\AppID\%CLSID%" /ve /d "prevhost.exe" /f
reg add "HKCU\Software\Classes\AppID\%CLSID%" /v DllSurrogate /d "prevhost.exe" /f

echo Registered. If the preview pane was open, close and reopen it.
pause
