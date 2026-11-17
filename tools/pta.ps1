# DSH 侧 pta-bridge 客户端（Lead 用）
# usage: . .\tools\pta.ps1; Get-PtaState; Invoke-Pta play
$script:PtaBase = 'http://127.0.0.1:59731'

function Invoke-Pta {
    param(
        [Parameter(Mandatory = $true)][string]$Cmd,
        [hashtable]$A = @{},
        [int]$TimeoutSec = 30
    )
    $payload = @{ cmd = $Cmd; args = $A } | ConvertTo-Json -Depth 12 -Compress
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($payload)
    $resp = Invoke-RestMethod -Uri "$script:PtaBase/cmd" -Method Post `
        -ContentType 'application/json; charset=utf-8' -Body $bytes -TimeoutSec $TimeoutSec
    if (-not $resp.ok) { throw ("bridge error: " + $resp.error) }
    return $resp.result
}

function Get-PtaState { Invoke-Pta state }
function Get-PtaFloors { param([int]$From = 0, [int]$To = -1) Invoke-Pta floors @{ from = $From; to = $To } }
function Start-PtaPlay { Invoke-Pta play }
function Stop-PtaPlay { Invoke-Pta exitedit }
function Set-PtaAuto { param([bool]$On = $true) Invoke-Pta setauto @{ on = $On } }
function Get-PtaHits { Invoke-Pta hits }
function Reset-PtaHits { Invoke-Pta resethits }
function Open-PtaLevel { param([string]$Path) Invoke-Pta openlevel @{ path = $Path } }
function Enter-PtaEditor { param([string]$Path) Invoke-Pta openeditor @{ path = $Path } }
function Save-PtaLevel { Invoke-Pta save }
function Set-PtaFloorNums { param([bool]$On = $true) Invoke-Pta setfloornums @{ on = $On } }
function Select-PtaFloor { param([int]$Id) Invoke-Pta selectfloor @{ id = $Id } }

function Wait-PtaEditor {
    param([int]$TimeoutSec = 60, [int]$MinFloors = 2)
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while ($sw.Elapsed.TotalSeconds -lt $TimeoutSec) {
        try {
            $s = Get-PtaState
            if ($s.isLevelEditor -and $s.floorCount -ge $MinFloors) { return $s }
        } catch { }
        Start-Sleep -Milliseconds 500
    }
    throw "等待编辑器超时（${TimeoutSec}s）"
}
