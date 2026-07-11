# PowerRecover Test Recovery Kit

This folder creates a safe fake disk image for testing PowerRecover inside a virtual machine.

## Build The Test Image

Open PowerShell in the project folder and run:

```powershell
powershell -ExecutionPolicy Bypass -File .\TestRecoveryKit\Generate-TestImage.ps1
```

This creates:

- `TestRecoveryKit\PowerRecover_TestImage.img`
- `TestRecoveryKit\ExpectedFiles\`
- `TestRecoveryKit\MANIFEST.txt`

## How To Test In PowerRecover

1. Open PowerRecover.
2. Click `Disk image`.
3. Pick `TestRecoveryKit\PowerRecover_TestImage.img`.
4. Choose an empty output folder.
5. Use `Full search - try everything`.
6. Click `Find my files`.

## What Should Happen

The useful files should be recovered:

- PDF
- PNG image
- JPG photo
- Word document
- Excel spreadsheet
- PowerPoint file

The junk files should be skipped:

- fake icon file
- fake log file

This image is mostly for testing the raw file search and filtering behavior. It is not a real NTFS/FAT formatted drive, so it does not test full folder reconstruction.
