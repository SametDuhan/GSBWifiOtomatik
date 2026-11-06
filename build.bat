@echo off
rem Derleme: .NET Framework ile gelen csc.exe kullanilir, ek arac gerekmez.
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
set GAC=%WINDIR%\Microsoft.NET\assembly\GAC_MSIL
"%CSC%" /nologo /codepage:65001 /target:winexe /win32icon:assets\app.ico /win32manifest:app.manifest /out:GSBWifiOtomatik.exe /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Web.Extensions.dll /r:System.Security.dll /r:System.Management.dll /r:"%GAC%\UIAutomationClient\v4.0_4.0.0.0__31bf3856ad364e35\UIAutomationClient.dll" /r:"%GAC%\UIAutomationTypes\v4.0_4.0.0.0__31bf3856ad364e35\UIAutomationTypes.dll" /r:"%GAC%\WindowsBase\v4.0_4.0.0.0__31bf3856ad364e35\WindowsBase.dll" GSBWifiOtomatik.cs
if %errorlevel%==0 (echo Tamam: GSBWifiOtomatik.exe) else (echo Derleme hatasi)
