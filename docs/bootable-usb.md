# Bootable USB Guide

PowerRecover can run from a Windows PE recovery USB with the same graphical UI
as the normal desktop app.

## Why Use Bootable USB

Use bootable USB when:

- The lost files are on the Windows system drive.
- You do not want Windows to keep writing to the affected drive.
- You need Administrator-level disk access without a normal installed session.

## What Goes Into The USB Image

The published PowerRecover files should be copied into the Windows PE image:

```text
X:\PowerRecover\PowerRecover.exe
X:\PowerRecover\PowerRecover.dll
X:\PowerRecover\PowerRecover.Engine.dll
X:\PowerRecover\all required runtime files
X:\Windows\System32\winpeshl.ini
```

`winpeshl.ini` launches the GUI automatically.

## Build The GUI Package

```powershell
dotnet publish .\PowerRecover.App\PowerRecover.App.csproj -c Release -r win-x64 -p:PublishProfile=BootableUsb
```

Output:

```text
PowerRecover.App\bin\Release\net8.0-windows\win-x64\bootable-usb\
```

## Important Licensing Note

Do not publish a Windows PE ISO unless you are sure you have the right to
redistribute every Microsoft component in it. The open-source repo should
provide the app package and instructions. Users can build their own recovery USB
with their own Windows tools.

## Recovery Output

When booted from USB, save recovered files to a second USB drive or external
disk. Do not save recovered files back to the drive being scanned.
