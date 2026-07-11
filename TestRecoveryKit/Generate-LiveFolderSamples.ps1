param(
    [string]$OutputDir = (Join-Path $PSScriptRoot "LiveFolderSamples")
)

$ErrorActionPreference = "Stop"

$folders = @(
    "Documents",
    "Photos",
    "Finance",
    "Work",
    "JunkToIgnore"
)

foreach ($folder in $folders) {
    New-Item -ItemType Directory -Force -Path (Join-Path $OutputDir $folder) | Out-Null
}

function Write-Ascii {
    param([string]$Path, [string]$Text)
    [System.IO.File]::WriteAllBytes($Path, [System.Text.Encoding]::ASCII.GetBytes($Text))
}

function New-TestDocx {
    param([string]$Path, [string]$Title)

    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem

    $fs = [System.IO.File]::Open($Path, [System.IO.FileMode]::Create, [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::None)
    $zip = [System.IO.Compression.ZipArchive]::new($fs, [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        $entry = $zip.CreateEntry("[Content_Types].xml")
        $writer = [System.IO.StreamWriter]::new($entry.Open())
        $writer.Write('<?xml version="1.0" encoding="UTF-8"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/></Types>')
        $writer.Dispose()

        $entry = $zip.CreateEntry("_rels/.rels")
        $writer = [System.IO.StreamWriter]::new($entry.Open())
        $writer.Write('<?xml version="1.0" encoding="UTF-8"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="word/document.xml"/></Relationships>')
        $writer.Dispose()

        $entry = $zip.CreateEntry("word/document.xml")
        $writer = [System.IO.StreamWriter]::new($entry.Open())
        $writer.Write('<w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:body><w:p><w:r><w:t>' + $Title + '</w:t></w:r></w:p><w:p><w:r><w:t>This is a PowerRecover test Word document. If recovery works, this file should keep its name and folder.</w:t></w:r></w:p></w:body></w:document>')
        $writer.Dispose()
    }
    finally {
        $zip.Dispose()
        $fs.Dispose()
    }
}

function New-TestXlsx {
    param([string]$Path)

    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem

    $fs = [System.IO.File]::Open($Path, [System.IO.FileMode]::Create, [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::None)
    $zip = [System.IO.Compression.ZipArchive]::new($fs, [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        $entry = $zip.CreateEntry("[Content_Types].xml")
        $writer = [System.IO.StreamWriter]::new($entry.Open())
        $writer.Write('<?xml version="1.0" encoding="UTF-8"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/><Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/></Types>')
        $writer.Dispose()

        $entry = $zip.CreateEntry("_rels/.rels")
        $writer = [System.IO.StreamWriter]::new($entry.Open())
        $writer.Write('<?xml version="1.0" encoding="UTF-8"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/></Relationships>')
        $writer.Dispose()

        $entry = $zip.CreateEntry("xl/workbook.xml")
        $writer = [System.IO.StreamWriter]::new($entry.Open())
        $writer.Write('<workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets><sheet name="Invoices" sheetId="1" r:id="rId1"/></sheets></workbook>')
        $writer.Dispose()

        $entry = $zip.CreateEntry("xl/_rels/workbook.xml.rels")
        $writer = [System.IO.StreamWriter]::new($entry.Open())
        $writer.Write('<?xml version="1.0" encoding="UTF-8"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/></Relationships>')
        $writer.Dispose()

        $entry = $zip.CreateEntry("xl/worksheets/sheet1.xml")
        $writer = [System.IO.StreamWriter]::new($entry.Open())
        $writer.Write('<worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData><row r="1"><c r="A1" t="inlineStr"><is><t>Invoice</t></is></c><c r="B1" t="inlineStr"><is><t>Amount</t></is></c></row><row r="2"><c r="A2" t="inlineStr"><is><t>INV-1001</t></is></c><c r="B2"><v>450</v></c></row></sheetData></worksheet>')
        $writer.Dispose()
    }
    finally {
        $zip.Dispose()
        $fs.Dispose()
    }
}

Write-Ascii (Join-Path $OutputDir "Documents\notes_about_project.txt") @"
PowerRecover test note

This is a normal text file inside a Documents folder.
Expected behavior: the software should find it with a readable preview.
"@

New-TestDocx -Path (Join-Path $OutputDir "Documents\client_contract_recovery_test.docx") -Title "Client Contract Recovery Test"
New-TestDocx -Path (Join-Path $OutputDir "Work\meeting_notes_recovery_test.docx") -Title "Meeting Notes Recovery Test"
New-TestXlsx -Path (Join-Path $OutputDir "Finance\invoice_register_recovery_test.xlsx")

Write-Ascii (Join-Path $OutputDir "Finance\monthly_accounts.csv") @"
Month,Income,Expense
January,1200,450
February,1400,500
March,1600,620
"@

$pdf = @"
%PDF-1.4
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
<< /Length 70 >>
stream
BT /F1 16 Tf 40 90 Td (PowerRecover invoice PDF test file) Tj ET
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
330
%%EOF
"@
Write-Ascii (Join-Path $OutputDir "Finance\paid_invoice_recovery_test.pdf") $pdf

Add-Type -AssemblyName System.Drawing
$pngPath = Join-Path $OutputDir "Photos\family_photo_recovery_test.png"
$bitmap = [System.Drawing.Bitmap]::new(120, 80)
try {
    for ($x = 0; $x -lt 120; $x++) {
        for ($y = 0; $y -lt 80; $y++) {
            $r = [Math]::Min(255, 40 + $x)
            $g = [Math]::Min(255, 80 + $y)
            $b = 180
            $bitmap.SetPixel($x, $y, [System.Drawing.Color]::FromArgb($r, $g, $b))
        }
    }
    $bitmap.Save($pngPath, [System.Drawing.Imaging.ImageFormat]::Png)
}
finally {
    $bitmap.Dispose()
}

Write-Ascii (Join-Path $OutputDir "JunkToIgnore\debug_junk.log") "Debug log junk. This is not user data."
Write-Ascii (Join-Path $OutputDir "JunkToIgnore\desktop.ini") "[.ShellClassInfo]"
Write-Ascii (Join-Path $OutputDir "JunkToIgnore\fake_icon.ico") "fake icon junk"

Write-Host "Created live folder test files in:"
Write-Host "  $OutputDir"
Get-ChildItem -Recurse $OutputDir -File | Select-Object FullName, Length
