# Release Guide

Use GitHub Releases for downloadable builds. Do not commit compiled EXE files or
ZIP packages to the source tree.

## Create Release Files

```powershell
.\scripts\package-release.ps1
```

This creates:

```text
release\PowerRecover-win-x64.zip
release\PowerRecover-bootable-usb-package.zip
release\SHA256SUMS.txt
```

## Suggested Version Names

Start with:

```text
v0.1.0-beta
```

Use beta until the app has been tested on several drives and virtual machines.

## Release Notes Template

```markdown
## PowerRecover v0.1.0-beta

First public beta release.

### Included
- Windows GUI recovery app
- Command-line app
- Bootable USB package files
- Test recovery kit

### Safety
- Read-only source scanning
- Recover to a separate output folder or drive

### Known Limits
- Recovery is not guaranteed if data was overwritten
- Bootable USB ISO is not distributed; build instructions are provided
```
