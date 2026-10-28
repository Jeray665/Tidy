# Tidy

**English** · [Español](README.es.md)

**Make Windows 11 faster, cleaner and more private, without breaking anything.** Tidy shows you exactly what it will change before it does it, and every change can be undone with one click.

![Tidy: your PC in top shape. Without breaking anything.](docs/img/hero.jpg)

**[Download the latest version](../../releases/latest)** · Free and open source · Available in English, Spanish, Portuguese, French, German, Russian, Chinese and Japanese · Join the [Discord](https://discord.gg/34RZad5Uas)

## Why Tidy

- **Everything can be undone.** Tidy backs up every value it changes. Any tweak, service, startup app or DNS change goes back with one click from History.
- **Nothing happens behind your back.** Before deleting anything you see the exact list of files, and what you remove from Storage goes to the Recycle Bin.
- **It never lowers your security.** No "optimization" turns off Defender, the firewall, UAC, SmartScreen or Windows Update.
- **Free, with no ads, no account and no telemetry.** Tidy only goes online for what you ask it to do, and to check GitHub for updates (you can turn that off).

## What it does

**One click to start.** Tidy checks your PC (memory, junk files, startup and drivers), tells you what's worth improving, and "Optimize now" frees RAM and shows you what to clean. Leave it in the tray and it looks after your PC by itself, using very few resources.

### A faster PC
- **87 reversible tweaks** with profiles: Recommended, Gaming, Privacy and Maximum performance. Each one explains what it does.
- **Windows startup:** see which apps Windows says are slowing your boot and by how many seconds, take them out, and compare your boot time before and after Tidy.
- **Memory:** frees standby RAM by itself when it runs low.
- **Background check:** measures what the programs with no window are using and lets you close them or take them out of startup.
- **Power plans,** plus when the screen turns off and when the PC sleeps or hibernates.
- **Services and scheduled tasks** that run for nothing (telemetry, error reports, features almost nobody uses), with the reason for each one.

### More free space
- **Cleanup:** temp files, caches, Windows Update leftovers, browser and GPU shader caches, and the Recycle Bin.
- **Disk analysis:** which folders and files take the most space, with filters by type and size.
- **Downloads and duplicates,** and **move to another drive** to free up C: (and put it all back from History).
- **Scheduled cleanup:** Tidy can remind you or clean by itself, only the categories you choose.

### Gaming
- **Automatic game mode:** when you start a game, Tidy switches to the maximum performance plan, frees RAM and raises the game's priority. When you close it, everything goes back to how it was.

### Privacy and network
- **Block ads, trackers and malware across the whole PC** through the hosts file, and protect it so nothing else can change it.
- **Change your DNS** and measure which one is faster, test latency, and repair the connection.

### Apps and drivers
- **Deep uninstaller:** removes the program and its leftovers, which go to a quarantine you can recover.
- **Update or install apps** with winget, from a catalog.
- **Drivers** from Windows Update and nvidia.com, with the digital signature checked.

### Fix problems
- **Repair Windows** with its own tools, and check your **disks' health**.
- **"Who's using this file?"** for when Windows won't let you delete or move something.
- **Security check-up** (read-only): antivirus, firewall, UAC, Secure Boot, TPM…
- **Hardware:** your PC's components at a glance.

### Utilities
- **Images:** convert between JPG, PNG, BMP, TIFF, GIF and ICO (it also opens HEIC, WebP and AVIF), compress, resize and remove hidden data like GPS location.
- **Video and audio:** compress videos to MP4, or keep only the sound as MP3 or M4A.
- **Check a download:** compare its fingerprint (SHA-256 and others) with the one on the website, and see if the program has a valid digital signature.
- Originals are never touched: every result is a new copy.

### Always in control
- **History** of everything Tidy does, with undo.
- **Restore point** before system changes.
- **Export your settings** and take your tweaks to another PC.
- **Tray mini app** with a summary, performance and cleanup, light and dark themes, and updates from inside the app with SHA-256 verification.

![Cleanup, tweaks and performance](docs/img/funciones.jpg)

![Tidy's sections: services, storage, Windows startup and hardware](docs/img/secciones.jpg)

![Light and dark theme](docs/img/temas.jpg)

## What Tidy deliberately doesn't do

It doesn't turn off Defender, the firewall, UAC, SmartScreen, Windows Update or the Spectre/Meltdown mitigations. Many "optimizers" do, and it's the classic way to get a PC compromised.

![Optimize without fear: everything can be undone](docs/img/seguro.jpg)

## Download

- **Tidy-Setup.exe** installs Tidy in Program Files, with a Start menu entry and uninstall from Settings > Apps. It updates itself from inside the app.
- **Tidy-Portable.exe** runs without installing. It keeps everything in a `TidyData` folder next to the .exe and leaves nothing else on the PC.

Requires Windows 11 (64-bit) and the [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0).

### "Windows protected your PC"

Tidy isn't signed with a paid certificate yet, so Windows shows it as an "Unknown publisher". To open it: **More info** → **Run anyway**. To check that your download is the real one, compare its SHA-256 with the `.sha256` file on the release page:

```
certutil -hashfile Tidy-Setup.exe SHA256
```

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
