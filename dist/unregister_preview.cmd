@echo off
set CLSID={8E5B1A47-3F2E-4C6D-9A1B-7C2D5E8F0A31}
reg delete "HKCU\Software\Classes\.litematic\shellex\{8895b1c6-b41f-4c1c-a562-0d564250836f}" /f
reg delete "HKCU\Software\Classes\CLSID\%CLSID%" /f
reg delete "HKCU\Software\Classes\AppID\%CLSID%" /f
echo Unregistered.
pause
