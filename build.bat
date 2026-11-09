@echo off
rem Builds a single DesktopPet.exe (with icon) into the "publish" folder.
dotnet publish -c Release -o publish
echo.
echo Done: publish\DesktopPet.exe
pause
