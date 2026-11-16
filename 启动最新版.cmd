@echo off
setlocal
set "APP_DIR=%~dp0artifacts\releases-control-colors\LocalDocSearch-1.0.0-rc.13-win-x64\LocalDocSearch-1.0.0-rc.13-win-x64"
if not exist "%APP_DIR%\DocSearch.exe" (
  echo The current package is missing. See README.md.
  exit /b 1
)
start "" /D "%APP_DIR%" "%APP_DIR%\DocSearch.exe"
