param([Parameter(Mandatory=$true)][string[]]$Paths)
$ErrorActionPreference = 'Stop'
foreach ($filePath in $Paths) {
    $resolvedPath = (Resolve-Path -LiteralPath $filePath).Path
    $bytes = [IO.File]::ReadAllBytes($resolvedPath)
    $pe = [BitConverter]::ToInt32($bytes, 0x3c)
    $opt = $pe + 24
    $ascii = [Text.Encoding]::ASCII.GetString($bytes)
    $rsds = $ascii.IndexOf('RSDS', [StringComparison]::Ordinal)
    if ($rsds -lt 0) { throw "No RSDS record in $resolvedPath" }
    $guidBytes = [byte[]]::new(16)
    [Array]::Copy($bytes,$rsds+4,$guidBytes,0,16)
    $pdbGuid = [Guid]::new($guidBytes)
    $pdbAge = [BitConverter]::ToUInt32($bytes,$rsds+20)
    $pdbName = $ascii.Substring($rsds+24).Split([char]0)[0]
    $sectionCount = [BitConverter]::ToUInt16($bytes,$pe+6)
    $sectionOffset = $opt + [BitConverter]::ToUInt16($bytes,$pe+20)
    $sections = for ($i=0; $i -lt $sectionCount; $i++) {
        $offset = $sectionOffset + 40*$i
        [pscustomobject]@{
            Name = [Text.Encoding]::ASCII.GetString($bytes,$offset,8).TrimEnd([char]0)
            VirtualSize = [BitConverter]::ToUInt32($bytes,$offset+8)
            Rva = [BitConverter]::ToUInt32($bytes,$offset+12)
            RawSize = [BitConverter]::ToUInt32($bytes,$offset+16)
            RawOffset = [BitConverter]::ToUInt32($bytes,$offset+20)
        }
    }
    [pscustomobject]@{
        Path = $resolvedPath
        Sha256 = (Get-FileHash -LiteralPath $resolvedPath -Algorithm SHA256).Hash
        Version = [Diagnostics.FileVersionInfo]::GetVersionInfo($resolvedPath).FileVersion
        Length = $bytes.Length
        Machine = ('0x{0:X4}' -f [BitConverter]::ToUInt16($bytes,$pe+4))
        Timestamp = [BitConverter]::ToUInt32($bytes,$pe+8)
        ImageBase = [BitConverter]::ToUInt64($bytes,$opt+24)
        ImageSize = [BitConverter]::ToUInt32($bytes,$opt+56)
        PdbGuid = $pdbGuid.ToString().ToUpperInvariant()
        PdbAge = $pdbAge
        PdbName = $pdbName
        PdbUrl = ('https://msdl.microsoft.com/download/symbols/{0}/{1}{2:X}/{0}' -f $pdbName,$pdbGuid.ToString('N').ToUpperInvariant(),$pdbAge)
        Sections = @($sections)
    }
}
