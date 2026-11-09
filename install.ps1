<#
  Install from source: builds the installer and runs it.
  Most people should download VlcCommentarySetup.exe from the Releases page instead.

  Usage:  powershell -ExecutionPolicy Bypass -File install.ps1
#>
$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'build.ps1')
Start-Process (Join-Path $PSScriptRoot 'dist\VlcCommentarySetup.exe')
