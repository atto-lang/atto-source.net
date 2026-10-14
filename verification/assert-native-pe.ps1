param(
    [Parameter(Mandatory = $true)][string]$Binary,
    [ValidateSet('win-x64', 'win-arm64')][string]$Rid = 'win-x64'
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Reject PE files containing a CLR/COM descriptor. A managed .NET apphost is
# a native launcher, but can still depend on a managed DLL and installed runtime.
# Verify the standalone Native AOT image, not just its .exe extension.
$resolved = (Resolve-Path -LiteralPath $Binary).Path
$stream = [System.IO.File]::Open($resolved, [System.IO.FileMode]::Open,
    [System.IO.FileAccess]::Read, [System.IO.FileShare]::Read)
$reader = [System.IO.BinaryReader]::new($stream)
try {
    if ($stream.Length -lt 512) { throw 'Truncated PE image' }
    $stream.Position = 0x3C
    $pe = [long]$reader.ReadUInt32()
    if ($pe -lt 0x40 -or $pe -gt ($stream.Length - 256)) {
        throw 'Invalid PE header offset'
    }
    $stream.Position = $pe
    if ($reader.ReadUInt32() -ne 0x00004550) { throw 'Missing PE signature' }
    $coffStart = $stream.Position
    $machine = $reader.ReadUInt16()
    $expected = if ($Rid -eq 'win-x64') { 0x8664 } else { 0xAA64 }
    if ($machine -ne $expected) {
        throw ("Unexpected PE machine 0x{0:X4}; expected 0x{1:X4}" -f $machine, $expected)
    }
    $stream.Position = $coffStart + 16
    $optionalSize = $reader.ReadUInt16()
    $optionalStart = $coffStart + 20
    if ($optionalSize -lt 232 -or ($optionalStart + $optionalSize) -gt $stream.Length) {
        throw 'Invalid PE32+ optional header'
    }
    $stream.Position = $optionalStart
    if ($reader.ReadUInt16() -ne 0x20B) {
        throw 'Expected 64-bit PE32+ executable'
    }
    # PE32+ data directories start 112 bytes into the optional header.
    # Directory index 14 is IMAGE_DIRECTORY_ENTRY_COM_DESCRIPTOR.
    $stream.Position = $optionalStart + 112 + (14 * 8)
    $clrRva = $reader.ReadUInt32()
    $clrSize = $reader.ReadUInt32()
    if ($clrRva -ne 0 -or $clrSize -ne 0) {
        throw 'Found CLR metadata header: not a strict Native AOT image'
    }
}
finally {
    $reader.Dispose()
    $stream.Dispose()
}
$sidecar = [System.IO.Path]::ChangeExtension($resolved, '.runtimeconfig.json')
if (Test-Path -LiteralPath $sidecar) {
    throw "Managed runtime configuration present: $sidecar"
}
Write-Host "Validated Native AOT PE ($Rid): $resolved"
