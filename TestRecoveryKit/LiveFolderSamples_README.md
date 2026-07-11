# Live Folder Samples

Use this folder when you want to test PowerRecover in a virtual machine with normal files and folders.

## How To Use

1. Copy `TestRecoveryKit\LiveFolderSamples` into a test drive inside your VM.
2. Example destination:

```text
E:\RecoveryTest\LiveFolderSamples
```

3. For a normal scan test, leave the files there and scan the test drive.
4. For a deleted-file recovery test, delete `E:\RecoveryTest\LiveFolderSamples`, empty Recycle Bin, and then scan the test drive.

## Best VM Test Setup

Use a second virtual disk, not your Windows system drive.

Recommended:

- Add a small second disk to the VM.
- Format it as NTFS.
- Copy `LiveFolderSamples` to that disk.
- Delete the copied folder.
- Do not install or copy anything else to that disk after deleting.
- Run PowerRecover and scan that second disk.

This gives a much cleaner test than scanning the Windows `C:` drive.

## Expected Useful Files

- Word documents
- Excel spreadsheet
- PDF invoice
- PNG photo
- CSV file
- TXT note

## Expected Junk Files

The `JunkToIgnore` folder contains small junk files. PowerRecover should ignore most of these after filtering.
