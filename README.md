# Tidy

**English** · [Español](README.es.md)

A safe optimizer for Windows 11: cleanup, tweaks, drivers, apps, network, security and game mode. Free and open source.

![Tidy: your PC in top shape. Without breaking anything.](docs/img/hero.jpg)

**Download:** the latest version is on [Releases](../../releases/latest).

- **Tidy-Setup.exe** installs it in Program Files, with a Start menu entry and uninstall from Settings > Apps. It updates itself from inside the app (checking the SHA-256 fingerprint).
- **Tidy-Portable.exe** runs without installing. It keeps everything in a `TidyData` folder next to the .exe and leaves nothing else on the PC.

Available in English, Spanish, Portuguese, French, German, Russian, Chinese and Japanese.

## What it does

- **Cleanup and storage:** temp files, caches, large files, downloads and duplicates. Before deleting anything it shows you the exact list of files; anything removed from Storage goes to the Recycle Bin.
- **Tweaks:** more than 80, with profiles (Recommended, Gaming, Privacy, Maximum performance). Each one saves the original value and is undone with its own switch.
- **Apps:** deep uninstaller (leftovers go to a recoverable quarantine), processes, and updating or installing apps with winget.
- **Performance:** automatic RAM release, power plans and automatic game mode.
- **Drivers:** from Windows Update and nvidia.com (the digital signature is checked).
- **Network, services, scheduled tasks and Windows startup**, with a backup of everything.
- **Security:** a read-only check-up (antivirus, firewall, UAC, Secure Boot, TPM, disks…).
- **Utilities:** convert and compress images, compress videos or extract their audio, and check that a download is the real one (hash and digital signature). Originals are never touched.
- **History:** everything Tidy does, with an undo button, and a before/after comparison.

![Cleanup, tweaks and performance](docs/img/funciones.jpg)

![Tidy's sections: services, storage, Windows startup and hardware](docs/img/secciones.jpg)

![Light and dark theme](docs/img/temas.jpg)

## What Tidy deliberately doesn't do

It doesn't turn off Defender, the firewall, UAC, SmartScreen, Windows Update or the Spectre/Meltdown mitigations. No tweak lowers your PC's security.

![Optimize without fear: everything can be undone](docs/img/seguro.jpg)

## "Windows protected your PC"

Tidy isn't signed with a paid certificate yet, so Windows shows it as an "Unknown publisher". To open it: **More info** → **Run anyway**. To check that your download is the real one, compare its SHA-256 with the `.sha256` file on the release page:

```
certutil -hashfile Tidy-Setup.exe SHA256
```

## Requirements

Windows 11 (64-bit) and the [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0).

## Build

```
dotnet publish src/Tidy -c Release -p:PublishSingleFile=true -p:DebugType=none -o dist
```

Safety tests (they run in an isolated folder):

```
dotnet run --project tests/Tidy.Tests
```

## License

Tidy is free software under the [GNU GPL v3](LICENSE): you can use, study, modify and share it, and any modified version that is published must stay free, with its source code.
