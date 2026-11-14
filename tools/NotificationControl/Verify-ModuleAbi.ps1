# Developer-only offline verifier. Reads Microsoft DLL/PDB files without loading the target DLL.
param(
    [Parameter(Mandatory=$true)][string]$InputRoot,
    [Parameter(Mandatory=$true)][string]$SymbolReader,
    [string]$DiaLibrary = 'C:\Program Files\Microsoft Visual Studio\2022\Community\DIA SDK\bin\amd64\msdia140.dll',
    [string]$Dumpbin = 'C:\Program Files\Microsoft Visual Studio\2022\Community\VC\Tools\MSVC\14.44.35207\bin\Hostx64\x64\dumpbin.exe',
    [string]$CatalogPath = (Join-Path $PSScriptRoot '..\..\src\Altong.Client\Services\NotificationControl\NotificationControllerModules.json'),
    [string]$EvidencePath = (Join-Path $InputRoot 'abi-verification.json')
)
$ErrorActionPreference='Stop'
$modules=@(Get-Content -LiteralPath $CatalogPath -Raw|ConvertFrom-Json)
$senderSlots=@{
    5='?get_Enabled@AumidNotificationSettings@@UEAAJPEAH@Z'
    7='?get_ShowBanner@AumidNotificationSettings@@UEAAJPEAH@Z'
    8='?put_ShowBanner@AumidNotificationSettings@@UEAAJH@Z'
    9='?get_ShowInActionCenter@AumidNotificationSettings@@UEAAJPEAH@Z'
    33='?get_SoundFile@AumidNotificationSettings@@UEAAJPEAPEAG@Z'
    34='?put_SoundFile@AumidNotificationSettings@@UEAAJPEBG@Z'
    37='?get_Title@AumidNotificationSettings@@UEAAJPEAPEAG@Z'
    43='?get_HasValidAppInfo@AumidNotificationSettings@@UEAAJPEAH@Z'
}
function Get-RawOffset($Info,[uint32]$Rva) {
    foreach($section in $Info.Sections) {
        if($Rva -ge $section.Rva -and $Rva -lt ($section.Rva+[Math]::Max($section.VirtualSize,$section.RawSize))) {
            return [int]($Rva-$section.Rva+$section.RawOffset)
        }
    }
    throw "Unmapped RVA: $Rva"
}
$results=foreach($module in $modules) {
    $directory=Join-Path $InputRoot $module.Sha256
    $dll=Join-Path $directory 'NotificationController.dll'
    $pdb=Join-Path $directory 'NotificationController.pdb'
    $info=& (Join-Path $PSScriptRoot 'Get-ModuleMetadata.ps1') -Paths @($dll)
    if($info.Sha256 -ne $module.Sha256) {throw "Module hash mismatch: $($module.Sha256)"}
    $expectedMachine=if($module.Architecture -eq 'x64'){'0x8664'}else{'0xAA64'}
    if($info.Machine -ne $expectedMachine) {throw 'Module architecture mismatch'}
    $lines=& $SymbolReader $DiaLibrary $pdb $info.PdbGuid "$($info.PdbAge)"
    if($LASTEXITCODE -ne 0) {throw "Cannot read matching PDB: $pdb"}
    $symbols=@($lines|ConvertFrom-Csv -Delimiter "`t" -Header Tag,Rva,Name,Signature)
    $symbolsByRva=@{}
    foreach($symbol in $symbols) {
        if(!$symbolsByRva.ContainsKey($symbol.Rva)) {$symbolsByRva[$symbol.Rva]=[Collections.Generic.List[string]]::new()}
        $symbolsByRva[$symbol.Rva].Add($symbol.Name.Replace('$$h',''))
    }
    $bytes=[IO.File]::ReadAllBytes($dll)
    $calls=[Collections.Generic.List[object]]::new()
    foreach($tableSpec in @(
        @{Name='??_7AumidNotificationSettings@@6B@';Slots=$senderSlots},
        @{Name='??_7NotificationSettingsImpl@@6BINotificationSettings@@@';Slots=@{
            3='?GetSettingsForAumid@NotificationSettingsImpl@@UEAAJPEBGPEAPEAUIAumidNotificationSettings@@@Z'}}
    )) {
        $tables=@($symbols|Where-Object Name -eq $tableSpec.Name)
        if($tables.Count -ne 1) {throw 'Missing or ambiguous vtable symbol'}
        $tableOffset=Get-RawOffset $info ([Convert]::ToUInt32($tables[0].Rva,16))
        foreach($slot in @($tableSpec.Slots.Keys|Sort-Object)) {
            $pointer=[BitConverter]::ToUInt64($bytes,$tableOffset+[int]$slot*8)
            if($pointer -lt $info.ImageBase -or $pointer -ge ($info.ImageBase+$info.ImageSize)) {throw 'Vtable pointer out of image'}
            $rva=[uint32]($pointer-$info.ImageBase)
            $names=@($symbolsByRva[$rva.ToString('X8')])
            if($names -notcontains $tableSpec.Slots[$slot]) {throw "ABI mismatch at slot $slot in $($module.Sha256)"}
            $calls.Add([pscustomobject]@{Slot=$slot;Rva=$rva;Symbol=$tableSpec.Slots[$slot]})
        }
    }
    $hex=[Convert]::ToHexString($bytes)
    foreach($guid in @('D18705BE-FC2F-44C8-AEFF-1CD49AEA8FC1','443B1739-3779-4ABA-953B-9E4FF84DD4B6','9DCA8F00-CB98-434B-9473-5A0691200DC4')) {
        if($hex.Contains([Convert]::ToHexString(([Guid]$guid).ToByteArray()))) {continue}
        if($guid -ne '9DCA8F00-CB98-434B-9473-5A0691200DC4' -or $module.Architecture -ne 'x64') {throw "Missing GUID $guid"}
        $qi=@($symbols|Where-Object {$_.Name -match '^\?QueryInterface@.*UIAumidNotificationSettings@@' -and $_.Name -match '@@UEAAJAEBU_GUID@@PEAPEAX@Z$'})
        if($qi.Count -ne 1){throw 'Ambiguous QueryInterface for inlined IID check'}
        $start=$info.ImageBase+[Convert]::ToUInt32($qi[0].Rva,16)
        $range='/range:0x{0:X},0x{1:X}' -f $start,($start+0x200)
        $code=(& $Dumpbin /disasm:bytes $range $dll) -join "`n"
        if($LASTEXITCODE -ne 0) {throw 'Cannot inspect inlined IID'}
        foreach($part in @('9DCA8F00h','434BCB98h','65A7394h','0C40D2091h')) {
            if(!$code.Contains($part)) {throw "Missing inlined IID comparison $part"}
        }
    }
    Write-Host "ABI PASS $($module.Architecture) $($module.FileVersion)"
    [pscustomobject]@{Sha256=$module.Sha256;Architecture=$module.Architecture;FileVersion=$info.Version;
        PdbGuid=$info.PdbGuid;PdbAge=$info.PdbAge;PdbUrl=$info.PdbUrl;Calls=@($calls);Passed=$true}
}
$results|ConvertTo-Json -Depth 8|Set-Content -LiteralPath $EvidencePath -Encoding utf8
Write-Output "PASS: $($results.Count) modules, nine ABI slots and three GUIDs per module. Evidence: $EvidencePath"
