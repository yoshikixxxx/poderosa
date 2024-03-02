@echo off
setlocal

set ILMERGE=ILMerge.exe

if not exist "%ILMERGE%" (
  echo ILMerge.exe is reuired to make a Monolithic-Poderosa.
  pause
  goto end
)

set CONFIG=Release
set PROJDIR=%~dp0..

rem Poderosa
set ASSYS=
call :addfile "%PROJDIR%\bin\%CONFIG%\Poderosa.exe"
for %%D in (Core Granados Macro Pipe Plugin PortForwardingCommand Protocols SerialPort SFTP TerminalEmulator TerminalSession UI Usability XZModem Benchmark) do (
  if exist "%PROJDIR%\bin\%CONFIG%\%%D.dll" (
    call :addfile "%PROJDIR%\bin\%CONFIG%\%%D.dll"
  ) else if exist "%PROJDIR%\bin\%CONFIG%\Poderosa.%%D.dll" (
    call :addfile "%PROJDIR%\bin\%CONFIG%\Poderosa.%%D.dll"
  )
)
call :addfile "%PROJDIR%\bin\%CONFIG%\Poderosa.ExtendPaste.dll"
call :addfile "%PROJDIR%\bin\%CONFIG%\Contrib.BroadcastCommand.dll"
call :addfile "%PROJDIR%\bin\%CONFIG%\Contrib.ConnectProfile.dll"
"%ILMERGE%" /targetplatform:"v4,C:\Program Files (x86)\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.8" /target:winexe /copyattrs /allowMultiple /out:poderosa.monolithic.exe %ASSYS%

rem Portforwarding
set ASSYS=
call :addfile "%PROJDIR%\bin\%CONFIG%\Portforwarding.exe"
call :addfile "%PROJDIR%\bin\%CONFIG%\Granados.dll"
"%ILMERGE%" /targetplatform:"v4,C:\Program Files (x86)\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.8" /target:winexe /copyattrs /allowMultiple /out:Portforwarding.monolithic.exe %ASSYS%

rem CreateMonolithic (ILMerge)
mkdir "%PROJDIR%\bin\monolithic"
copy /Y poderosa.monolithic.exe "%PROJDIR%\bin\monolithic\Poderosa.exe"
copy /Y Portforwarding.monolithic.exe "%PROJDIR%\bin\monolithic\Portforwarding.exe"
copy /Y "%PROJDIR%\bin\%CONFIG%\charfont" "%PROJDIR%\bin\monolithic"
copy /Y "%PROJDIR%\bin\%CONFIG%\charwidth" "%PROJDIR%\bin\monolithic"
copy /Y "%PROJDIR%\bin\%CONFIG%\cygwin-bridge32.exe" "%PROJDIR%\bin\monolithic"
copy /Y "%PROJDIR%\bin\%CONFIG%\cygwin-bridge64.exe" "%PROJDIR%\bin\monolithic"
pause

goto end

:addfile
set ASSYS=%ASSYS% %1
goto end

:end
