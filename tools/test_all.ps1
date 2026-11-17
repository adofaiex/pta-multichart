# 端到端验收脚本（Lead 用）。前提：游戏已启动、pta-bridge 在 59731 监听、编辑器已进入关卡。
# 用法: pwsh -NoProfile -File .\tools\test_all.ps1
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\pta.ps1"

$base = Join-Path (Split-Path -Parent $PSScriptRoot) 'testlevels'
$script:results = @()

function Wait-Until {
    param([scriptblock]$Cond, [int]$TimeoutMs = 15000, [string]$What = 'condition')
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while ($sw.Elapsed.TotalMilliseconds -lt $TimeoutMs) {
        try { if (& $Cond) { return $true } } catch { }
        Start-Sleep -Milliseconds 250
    }
    Write-Host ("  [warn] timeout waiting for " + $What)
    return $false
}

function Test-Level {
    param(
        [string]$Name,
        [int]$ExpectLogicalFloors,
        [int]$ExpectPlayableFloors,
        [int]$ExpectHits,
        [string]$Note = ''
    )
    $path = Join-Path $base ($Name + '.adofai')
    Open-PtaLevel $path | Out-Null

    $loaded = Wait-Until -TimeoutMs 20000 -What "$Name loaded" -Cond {
        $s = Get-PtaState
        ($s.levelPath -eq $path) -and ($s.floorCount -eq $ExpectLogicalFloors) -and (-not $s.playMode)
    }
    $state = Get-PtaState
    $logicalOk = ($state.floorCount -eq $ExpectLogicalFloors)
    $nums = (Set-PtaFloorNums $true).floors | ForEach-Object { $_.text }

    Reset-PtaHits | Out-Null
    Set-PtaAuto $true | Out-Null
    Start-PtaPlay | Out-Null
    $started = Wait-Until -TimeoutMs 10000 -What "$Name play started" -Cond {
        $s = Get-PtaState
        $s.playMode -eq $true
    }
    $floors = Get-PtaFloors
    $playableOk = ($floors.Count -eq $ExpectPlayableFloors)
    $invisibleInserts = ($floors | Where-Object { $_.opacityVal -eq 0 }).Count

    $h = $null
    for ($i = 0; $i -lt 60; $i++) {
        Start-Sleep -Milliseconds 250
        $h = Get-PtaHits
        if ($h.percentComplete -ge 1) { break }
    }
    $realHits = ($h.hitMargins | Where-Object { $_ -ne 'Midspin' }).Count
    $hitsOk = ($realHits -eq $ExpectHits -and $h.deaths -eq 0)

    Stop-PtaPlay | Out-Null
    $back = Wait-Until -TimeoutMs 15000 -What "$Name back to edit" -Cond {
        $s = Get-PtaState
        (-not $s.playMode) -and ($s.floorCount -eq $ExpectLogicalFloors)
    }
    $after = Get-PtaState

    $row = [ordered]@{
        level          = $Name
        logicalFloors  = $state.floorCount
        playableFloors = $floors.Count
        invisible      = $invisibleInserts
        hits           = $realHits
        margins        = ($h.hitMargins -join ',')
        deaths         = $h.deaths
        logicalOk      = ($logicalOk -and $loaded)
        playableOk     = ($playableOk -and $started)
        hitsOk         = $hitsOk
        restoredFloors = $after.floorCount
        nums           = ($nums -join ',')
        note           = $Note
    }
    $script:results += [pscustomobject]$row
}

Test-Level -Name 't1_insert' -ExpectLogicalFloors 3 -ExpectPlayableFloors 4 -ExpectHits 3 -Note 'sub [180,270] @anchor1'
Test-Level -Name 't2_repeat_const' -ExpectLogicalFloors 5 -ExpectPlayableFloors 8 -ExpectHits 7 -Note 'repeat gap=1 -> gapLength=2'
Test-Level -Name 't3_repeat_var' -ExpectLogicalFloors 5 -ExpectPlayableFloors 6 -ExpectHits 5 -Note 'repeat expanded to unrolled'
Test-Level -Name 't6_two_subs' -ExpectLogicalFloors 4 -ExpectPlayableFloors 6 -ExpectHits 5 -Note 'two subs, anchors 1 and 2'
Test-Level -Name 't4_midspin' -ExpectLogicalFloors 5 -ExpectPlayableFloors 6 -ExpectHits 4 -Note 'anchor after midspin'

Write-Host ''
Write-Host '==== summary ===='
$script:results | Format-Table level, logicalFloors, playableFloors, invisible, hits, deaths, logicalOk, playableOk, hitsOk, restoredFloors, nums -AutoSize | Out-String | Write-Host
Write-Host ''
Write-Host '==== margins ===='
$script:results | Format-Table level, margins, note -AutoSize | Out-String | Write-Host
$failed = $script:results | Where-Object { -not ($_.logicalOk -and $_.playableOk -and $_.hitsOk) }
if ($failed) {
    Write-Host ("FAILED: " + (($failed | ForEach-Object { $_.level }) -join ', '))
    exit 1
}
Write-Host 'ALL E2E CHECKS PASSED'
