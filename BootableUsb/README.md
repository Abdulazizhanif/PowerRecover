# PowerRecover Bootable USB GUI

PowerRecover should boot into the same graphical app, not a command-only screen.

Use this layout inside the Windows PE image:

```text
X:\PowerRecover\PowerRecover.exe
X:\PowerRecover\PowerRecover.dll
X:\PowerRecover\PowerRecover.Engine.dll
X:\PowerRecover\all other published files
X:\Windows\System32\winpeshl.ini
```

`winpeshl.ini` launches `PowerRecover.exe` automatically after Windows PE starts.

Recommended build for the USB:

```powershell
dotnet publish .\PowerRecover.App\PowerRecover.App.csproj -c Release -r win-x64 -p:PublishProfile=BootableUsb
```

Important notes:

- Use a Windows PE image with graphical shell support.
- The app must be published as `win-x64` and self-contained so it does not depend on an installed .NET runtime.
- Save recovered files to another USB drive or external disk, not back to the drive being scanned.
- Boot mode runs with high permissions, so the app can scan physical disks without showing a normal Windows UAC prompt.
