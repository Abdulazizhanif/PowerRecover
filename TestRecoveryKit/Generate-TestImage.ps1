param(
    [string]$OutputDir = $PSScriptRoot,
    [int]$ImageSizeMb = 64
)

$ErrorActionPreference = "Stop"

$expectedDir = Join-Path $OutputDir "ExpectedFiles"
$tempDir = Join-Path $OutputDir "_temp"
$imagePath = Join-Path $OutputDir "PowerRecover_TestImage.img"

Remove-Item -LiteralPath $tempDir -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $expectedDir | Out-Null
New-Item -ItemType Directory -Force -Path $tempDir | Out-Null
Get-ChildItem -LiteralPath $expectedDir -File -ErrorAction SilentlyContinue |
    Remove-Item -Force -ErrorAction SilentlyContinue

function Write-Bytes {
    param([string]$Path, [byte[]]$Bytes)
    [System.IO.File]::WriteAllBytes($Path, $Bytes)
}

function Write-Ascii {
    param([string]$Path, [string]$Text)
    [System.IO.File]::WriteAllBytes($Path, [System.Text.Encoding]::ASCII.GetBytes($Text))
}

function New-OfficeZip {
    param(
        [string]$Path,
        [string]$Kind
    )

    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem

    $fs = [System.IO.File]::Open($Path, [System.IO.FileMode]::Create, [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::None)
    $zip = [System.IO.Compression.ZipArchive]::new($fs, [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        $contentTypes = $zip.CreateEntry("[Content_Types].xml")
        $writer = New-Object System.IO.StreamWriter($contentTypes.Open())
        $writer.Write('<?xml version="1.0" encoding="UTF-8"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"></Types>')
        $writer.Dispose()

        if ($Kind -eq "docx") {
            $entry = $zip.CreateEntry("word/document.xml")
            $writer = New-Object System.IO.StreamWriter($entry.Open())
            $writer.Write('<w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:body><w:p><w:r><w:t>PowerRecover Word test file. This padded text makes the document large enough for recovery quality filtering. Recovery test content. Recovery test content. Recovery test content. Recovery test content. Recovery test content. Recovery test content. Recovery test content. Recovery test content. Recovery test content. Recovery test content. Recovery test content. Recovery test content. Recovery test content. Recovery test content. Recovery test content. Recovery test content. Recovery test content. Recovery test content.</w:t></w:r></w:p></w:body></w:document>')
            $writer.Dispose()
        }
        elseif ($Kind -eq "xlsx") {
            $entry = $zip.CreateEntry("xl/workbook.xml")
            $writer = New-Object System.IO.StreamWriter($entry.Open())
            $writer.Write('<workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheets><sheet name="RecoveryTestDataRecoveryTestDataRecoveryTestData" sheetId="1" r:id="rId1"/></sheets><definedNames><definedName name="PowerRecover_Test">Recovery test content repeated repeated repeated repeated repeated repeated repeated repeated repeated repeated repeated repeated repeated repeated repeated repeated repeated repeated repeated repeated repeated repeated repeated repeated.</definedName></definedNames></workbook>')
            $writer.Dispose()
        }
        elseif ($Kind -eq "pptx") {
            $entry = $zip.CreateEntry("ppt/presentation.xml")
            $writer = New-Object System.IO.StreamWriter($entry.Open())
            $writer.Write('<p:presentation xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main"><p:sldMasterIdLst><p:sldMasterId id="2147483648" r:id="rId1" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"/></p:sldMasterIdLst><p:notesSz cx="6858000" cy="9144000"/><p:defaultTextStyle>PowerRecover recovery test presentation content repeated repeated repeated repeated repeated repeated repeated repeated repeated repeated repeated repeated repeated repeated repeated repeated.</p:defaultTextStyle></p:presentation>')
            $writer.Dispose()
        }
    }
    finally {
        $zip.Dispose()
        $fs.Dispose()
    }
}

$pdfPath = Join-Path $expectedDir "client_invoice_test.pdf"
$pdf = @"
%PDF-1.4
% PowerRecover recovery test PDF with padding so it is not treated as a tiny placeholder file.
% Padding line 01: recovered document content recovered document content recovered document content.
% Padding line 02: recovered document content recovered document content recovered document content.
% Padding line 03: recovered document content recovered document content recovered document content.
1 0 obj
<< /Type /Catalog /Pages 2 0 R >>
endobj
2 0 obj
<< /Type /Pages /Kids [3 0 R] /Count 1 >>
endobj
3 0 obj
<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 144] /Contents 4 0 R >>
endobj
4 0 obj
<< /Length 58 >>
stream
BT /F1 18 Tf 40 90 Td (PowerRecover PDF test file) Tj ET
endstream
endobj
xref
0 5
0000000000 65535 f
0000000009 00000 n
0000000058 00000 n
0000000115 00000 n
0000000202 00000 n
trailer
<< /Root 1 0 R /Size 5 >>
startxref
310
%%EOF
"@
Write-Ascii $pdfPath $pdf

$pngPath = Join-Path $expectedDir "family_photo_test.png"
Add-Type -AssemblyName System.Drawing
$bitmap = [System.Drawing.Bitmap]::new(64, 64)
try {
    for ($x = 0; $x -lt 64; $x++) {
        for ($y = 0; $y -lt 64; $y++) {
            $color = if (($x + $y) % 2 -eq 0) {
                [System.Drawing.Color]::FromArgb(30, 144, 255)
            } else {
                [System.Drawing.Color]::FromArgb(255, 255, 255)
            }
            $bitmap.SetPixel($x, $y, $color)
        }
    }
    $bitmap.Save($pngPath, [System.Drawing.Imaging.ImageFormat]::Png)
}
finally {
    $bitmap.Dispose()
}

$jpgPath = Join-Path $expectedDir "holiday_photo_test.jpg"
$jpgBase64 = "/9j/4AAQSkZJRgABAQAAAQABAAD/2wBDAP//////////////////////////////////////////////////////////////////////////////////////2wBDAf//////////////////////////////////////////////////////////////////////////////////////wAARCAABAAEDASIAAhEBAxEB/8QAFQABAQAAAAAAAAAAAAAAAAAAAAX/xAAUEAEAAAAAAAAAAAAAAAAAAAAA/9oADAMBAAIQAxAAAAH/xAAUEAEAAAAAAAAAAAAAAAAAAAAA/9oACAEBAAEFAqf/xAAUEQEAAAAAAAAAAAAAAAAAAAAA/9oACAEDAQE/ASP/xAAUEQEAAAAAAAAAAAAAAAAAAAAA/9oACAECAQE/ASP/xAAUEAEAAAAAAAAAAAAAAAAAAAAA/9oACAEBAAY/Al//xAAUEAEAAAAAAAAAAAAAAAAAAAAA/9oACAEBAAE/IV//2gAMAwEAAgADAAAAEP/EABQRAQAAAAAAAAAAAAAAAAAAABD/2gAIAQMBAT8QH//EABQRAQAAAAAAAAAAAAAAAAAAABD/2gAIAQIBAT8QH//EABQQAQAAAAAAAAAAAAAAAAAAABD/2gAIAQEAAT8QH//Z"
Write-Bytes $jpgPath ([Convert]::FromBase64String($jpgBase64))

New-OfficeZip -Path (Join-Path $expectedDir "customer_contract_test.docx") -Kind "docx"
New-OfficeZip -Path (Join-Path $expectedDir "accounts_test.xlsx") -Kind "xlsx"
New-OfficeZip -Path (Join-Path $expectedDir "sales_deck_test.pptx") -Kind "pptx"

Write-Ascii (Join-Path $expectedDir "windows_icon_junk.ico") "THIS_IS_FAKE_ICON_JUNK"
Write-Ascii (Join-Path $expectedDir "debug_log_junk.log") "debug log junk that should be skipped by professional filtering"

$imageBytes = New-Object byte[] ($ImageSizeMb * 1024 * 1024)
$rng = [System.Random]::new(20260711)
$rng.NextBytes($imageBytes)

$placements = @(
    @{ File = "client_invoice_test.pdf"; Offset = 1MB },
    @{ File = "family_photo_test.png"; Offset = 6MB },
    @{ File = "holiday_photo_test.jpg"; Offset = 10MB },
    @{ File = "customer_contract_test.docx"; Offset = 16MB },
    @{ File = "accounts_test.xlsx"; Offset = 24MB },
    @{ File = "sales_deck_test.pptx"; Offset = 32MB },
    @{ File = "windows_icon_junk.ico"; Offset = 40MB },
    @{ File = "debug_log_junk.log"; Offset = 42MB }
)

foreach ($placement in $placements) {
    $filePath = Join-Path $expectedDir $placement.File
    $bytes = [System.IO.File]::ReadAllBytes($filePath)
    [Array]::Copy($bytes, 0, $imageBytes, [int]$placement.Offset, $bytes.Length)
}

[System.IO.File]::WriteAllBytes($imagePath, $imageBytes)

$manifest = @"
PowerRecover test image
Image: $imagePath
Size: $ImageSizeMb MB

Expected useful files:
- PDF: client_invoice_test.pdf
- PNG: family_photo_test.png
- JPG: holiday_photo_test.jpg
- Word: customer_contract_test.docx
- Excel: accounts_test.xlsx
- PowerPoint: sales_deck_test.pptx

Expected junk files that should be skipped:
- windows_icon_junk.ico
- debug_log_junk.log

Recommended app test:
1. Open PowerRecover.
2. Choose Disk image.
3. Select PowerRecover_TestImage.img.
4. Choose an empty output folder.
5. Use Full search - try everything, or Raw search - find files without names.
6. Click Find my files.
"@

Set-Content -Encoding UTF8 -Path (Join-Path $OutputDir "MANIFEST.txt") -Value $manifest

Write-Host "Created test image:"
Write-Host "  $imagePath"
Write-Host ""
Write-Host "Expected source files:"
Get-ChildItem $expectedDir | Select-Object Name, Length
