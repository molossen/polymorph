@echo off
rem Polymorph release: tests, self-contained build (nothing to install for users), zip.
setlocal
cd /d "%~dp0"
rem the embedded Python (scripts and add-ons run inside Polymorph)
if not exist pyembed\python312.dll powershell -NoProfile -ExecutionPolicy Bypass -File tools\get_python.ps1 || goto :fail
set VER=1.7.0
set NAME=Polymorph-%VER%-win-x64
set DST=release\%NAME%

echo == Tests of the geometry core
dotnet run --project MoloMax.Core.Tests -c Release || goto :fail
echo == Building %NAME% (self-contained)
if exist "%DST%" rmdir /s /q "%DST%"
dotnet publish MoloMax\MoloMax.csproj -c Release -r win-x64 --self-contained true -p:DebugType=none -o "%DST%" || goto :fail
if exist "%DST%\python\tests" rmdir /s /q "%DST%\python\tests"
echo == Zip
if exist "release\%NAME%.zip" del "release\%NAME%.zip"
powershell -NoProfile -Command "Compress-Archive -Path '%DST%' -DestinationPath 'release\%NAME%.zip' -CompressionLevel Optimal" || goto :fail
echo.
echo Done: release\%NAME%.zip
exit /b 0
:fail
echo.
echo Release failed, see the messages above.
exit /b 1
