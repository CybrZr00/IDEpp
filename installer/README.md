# IDE++ installer

A per-user MSI built with [WiX Toolset](https://wixtoolset.org/) v7. Deliberately not part of
`IdePlusPlus.slnx` or the regular `dotnet build`/`dotnet test` loop - building it publishes a full
self-contained `win-x64` copy of the app first, which takes real time and isn't something an
ordinary dev-loop build should pay for.

## Build

```
dotnet build installer\IdePlusPlus.Installer\IdePlusPlus.Installer.wixproj -c Release
```

One command: it publishes `IdePlusPlus.App` fresh (self-contained, `win-x64`) and then builds the
MSI from that output. Nothing to run by hand first. Output lands at
`installer\IdePlusPlus.Installer\bin\x64\Release\IdePlusPlus-Setup.msi`.

## What it does

- Installs per-user under `%LOCALAPPDATA%\Programs\IDE++` - no admin/UAC prompt.
- Adds a Start Menu shortcut.
- Self-contained: the target machine needs nothing preinstalled.

## Notes for future changes

- **UpgradeCode** (`Package.wxs`) must never change - it's the only thing that lets Windows
  Installer recognize a new build as an upgrade of this same product rather than a different one.
- **ProductVersion** (`IdePlusPlus.Installer.wixproj`) should go up with each release you intend
  people to actually install over a previous one.
- ICE38/ICE64/ICE91 are suppressed (`SuppressIces` in the `.wixproj`) - the standard, expected thing
  for a perUser install using harvested file components; see the comment there for why.
