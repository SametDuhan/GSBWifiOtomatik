@echo off
rem Derleme: .NET Framework ile gelen csc.exe kullanilir, ek arac gerekmez.
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
"%CSC%" /nologo /codepage:65001 /target:winexe /win32icon:assets\app.ico /out:GSBWifiOtomatik.exe /r:System.Windows.Forms.dll /r:System.Drawing.dll GSBWifiOtomatik.cs
if %errorlevel%==0 (echo Tamam: GSBWifiOtomatik.exe) else (echo Derleme hatasi)
