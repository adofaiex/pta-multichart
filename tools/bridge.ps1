# =============================================================================
# pta-bridge —— DSH 侧 PowerShell 客户端
#
# 用法：
#   . .\tools\bridge.ps1
#   Wait-PtaBridge -TimeoutSec 60          # 等桥起来
#   Get-PtaState | Format-List             # 当前状态
#   Get-PtaFloors | Format-Table seqID,entryTime,speed,isCCW,editorNumText
#
# 协议：POST http://127.0.0.1:59731/cmd  {"cmd":"...","args":{...}}
#        → {"ok":true,"result":...} / {"ok":false,"error":"..."}
#        GET  http://127.0.0.1:59731/ping
#
# 端口：默认 59731，可用环境变量 PTA_BRIDGE_PORT 或 -Port 覆盖。
# =============================================================================

$script:PtaBridgeDefaultPort = 59731
$script:PtaBridgeHost = '127.0.0.1'

function Get-PtaBridgePort {
    [CmdletBinding()]
    param([int]$Port = 0)
    if ($Port -gt 0) { return $Port }

    $envPort = [Environment]::GetEnvironmentVariable('PTA_BRIDGE_PORT')
    if ($envPort) {
        $n = 0
        if ([int]::TryParse($envPort, [ref]$n) -and $n -gt 0) { return $n }
    }
    return $script:PtaBridgeDefaultPort
}

function Get-PtaBridgeBaseUri {
    param([int]$Port = 0)
    $p = Get-PtaBridgePort -Port $Port
    return "http://$($script:PtaBridgeHost):$p"
}

# 相对路径要按 PowerShell 的当前位置解析（.NET 的 CurrentDirectory 不一定同步）
function Get-PtaFullPath {
    param([string]$Path)
    if (-not $Path) { return $null }
    try {
        return $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Path)
    } catch {
        return [System.IO.Path]::GetFullPath($Path)
    }
}

# 把 HTTP 响应体按 UTF-8 取成字符串（PS 5.1 的 .Content 会按错误编码解码）
function ConvertFrom-PtaBridgeRawResponse {
    param($Response)
    if ($null -eq $Response) { return '' }

    $streamProp = $Response.PSObject.Properties['RawContentStream']
    if ($streamProp -and $null -ne $streamProp.Value) {
        $stream = $streamProp.Value
        try {
            if ($stream.CanSeek) { $stream.Position = 0 }
            $ms = New-Object System.IO.MemoryStream
            $stream.CopyTo($ms)
            return [System.Text.Encoding]::UTF8.GetString($ms.ToArray())
        } catch {
            # 落到下面的 .Content
        }
    }

    $contentProp = $Response.PSObject.Properties['Content']
    if ($contentProp -and $null -ne $contentProp.Value) {
        return [string]$contentProp.Value
    }

    return ''
}

function Invoke-PtaBridgeHttp {
    # 底层：POST /cmd 或 GET /ping，返回原始 JSON 文本
    param(
        [string]$Path = '/cmd',
        [byte[]]$Body = $null,
        [int]$TimeoutSec = 20,
        [int]$Port = 0
    )

    $uri = (Get-PtaBridgeBaseUri -Port $Port) + $Path
    $timeout = [Math]::Max(1, $TimeoutSec)
    $canNoProxy = $false
    try { $canNoProxy = (Get-Command Invoke-WebRequest).Parameters.ContainsKey('NoProxy') } catch { }

    $isPost = ($null -ne $Body)
    $resp = $null

    try {
        $common = @{
            Uri             = $uri
            TimeoutSec      = $timeout
            UseBasicParsing = $true
            ErrorAction     = 'Stop'
        }
        if ($isPost) {
            $common['Method'] = 'Post'
            $common['Body'] = $Body
            $common['ContentType'] = 'application/json; charset=utf-8'
        } else {
            $common['Method'] = 'Get'
        }
        if ($canNoProxy) { $common['NoProxy'] = $true }

        $resp = Invoke-WebRequest @common
    } catch {
        $detail = $_.Exception.Message
        $inner = $_.Exception.InnerException
        if ($inner) { $detail = "$detail —— $($inner.Message)" }
        throw [System.InvalidOperationException]::new(
            "PtaBridge: 请求 $uri 失败 —— $detail`n" +
            "  排查顺序：1) 游戏是否在运行（A Dance of Fire and Ice.exe） 2) 模组是否已装到 <游戏>\Mods\pta-bridge\ 并出现在 UMM 里 " +
            "3) 端口 $((Get-PtaBridgePort -Port $Port)) 是否被别的程序占用 4) 看日志 <游戏>\Mods\pta-bridge\bridge.log（桥被关掉时可在 UMM 界面点 Start bridge）。")
    }

    return ConvertFrom-PtaBridgeRawResponse -Response $resp
}

function Invoke-PtaBridge {
    <#
    .SYNOPSIS
        发一条桥命令。成功返回 result（PSCustomObject / 数组 / 标量），失败抛异常。
    .PARAMETER Raw
        返回 { ok, result/error } 原始对象而不抛异常。
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true, Position = 0)]
        [string]$Cmd,
        [hashtable]$Args = @{},
        [int]$TimeoutSec = 20,
        [int]$Port = 0,
        [switch]$Raw
    )

    $payload = @{ cmd = $Cmd; args = $Args }
    $json = $payload | ConvertTo-Json -Depth 10 -Compress
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($json)

    $text = Invoke-PtaBridgeHttp -Path '/cmd' -Body $bytes -TimeoutSec $TimeoutSec -Port $Port

    $obj = $null
    try {
        $obj = $text | ConvertFrom-Json
    } catch {
        throw [System.InvalidOperationException]::new("PtaBridge: 返回不是合法 JSON：$text")
    }

    if ($Raw) { return $obj }

    if (-not $obj.ok) {
        throw [System.InvalidOperationException]::new("PtaBridge: cmd '$Cmd' 失败：$($obj.error)")
    }
    return $obj.result
}

function Test-PtaBridge {
    # 不抛异常的探活：通了返回 $true
    [CmdletBinding()]
    param([int]$TimeoutSec = 3, [int]$Port = 0)
    try {
        $null = Invoke-PtaBridge -Cmd 'ping' -TimeoutSec $TimeoutSec -Port $Port
        return $true
    } catch {
        return $false
    }
}

function Wait-PtaBridge {
    <#
    .SYNOPSIS
        轮询 ping 直到桥就绪，返回 ping 结果；超时抛异常。
    #>
    [CmdletBinding()]
    param(
        [int]$TimeoutSec = 60,
        [int]$Port = 0,
        [int]$IntervalMs = 400
    )

    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    $last = $null
    while ((Get-Date) -lt $deadline) {
        try {
            $ping = Invoke-PtaBridge -Cmd 'ping' -TimeoutSec 5 -Port $Port
            Write-Verbose "pta-bridge 就绪：$((Get-PtaBridgeBaseUri -Port $Port))"
            return $ping
        } catch {
            $last = $_.Exception.Message
            Start-Sleep -Milliseconds $IntervalMs
        }
    }

    throw [System.TimeoutException]::new(
        "Wait-PtaBridge: 等待 $TimeoutSec 秒仍未连上 $((Get-PtaBridgeBaseUri -Port $Port))/ping。最后一次错误：$last")
}

# ---------------------------- 读取 ----------------------------

function Get-PtaState {
    [CmdletBinding()]
    param([int]$Port = 0, [int]$TimeoutSec = 20)
    return Invoke-PtaBridge -Cmd 'state' -TimeoutSec $TimeoutSec -Port $Port
}

function Get-PtaFloors {
    <#
    .SYNOPSIS
        返回砖块数组。Count = 0 表示全部。
    #>
    [CmdletBinding()]
    param(
        [int]$From = 0,
        [int]$Count = 0,
        [int]$Port = 0,
        [int]$TimeoutSec = 30
    )
    $a = @{ from = $From }
    if ($Count -gt 0) { $a['count'] = $Count }
    return Invoke-PtaBridge -Cmd 'floors' -Args $a -TimeoutSec $TimeoutSec -Port $Port
}

function Get-PtaHits {
    [CmdletBinding()]
    param([int]$Port = 0, [int]$TimeoutSec = 20)
    return Invoke-PtaBridge -Cmd 'hits' -TimeoutSec $TimeoutSec -Port $Port
}

function Get-PtaLog {
    [CmdletBinding()]
    param([int]$Port = 0, [int]$TimeoutSec = 20)
    $r = Invoke-PtaBridge -Cmd 'log' -TimeoutSec $TimeoutSec -Port $Port
    return $r
}

function Get-PtaHelp {
    [CmdletBinding()]
    param([int]$Port = 0, [int]$TimeoutSec = 20)
    return Invoke-PtaBridge -Cmd 'help' -TimeoutSec $TimeoutSec -Port $Port
}

# ---------------------------- 编辑器 ----------------------------

function Open-PtaLevel {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true, Position = 0)]
        [string]$Path,
        [int]$Port = 0,
        [int]$TimeoutSec = 20
    )
    $full = Get-PtaFullPath $Path
    if (-not (Test-Path -LiteralPath $full)) {
        throw [System.IO.FileNotFoundException]::new("谱面文件不存在：$full")
    }
    return Invoke-PtaBridge -Cmd 'openlevel' -Args @{ path = $full } -TimeoutSec $TimeoutSec -Port $Port
}

function Open-PtaEditor {
    <#
    .SYNOPSIS
        进编辑器（可选直接打开某个谱面）。返回 {submitted, way, scene, ...}。
    .EXAMPLE
        Open-PtaEditor -Path .\testlevels\a.adofai
    #>
    [CmdletBinding()]
    param(
        [string]$Path,
        [int]$Port = 0,
        [int]$TimeoutSec = 20
    )
    $a = @{}
    if ($Path) {
        $full = Get-PtaFullPath $Path
        if (-not (Test-Path -LiteralPath $full)) {
            throw [System.IO.FileNotFoundException]::new("谱面文件不存在：$full")
        }
        $a['path'] = $full
    }
    return Invoke-PtaBridge -Cmd 'openeditor' -Args $a -TimeoutSec $TimeoutSec -Port $Port
}

function Wait-PtaEditor {
    <#
    .SYNOPSIS
        等到 isLevelEditor == true（可选再等到 floor 数 >= MinFloors）。
    #>
    [CmdletBinding()]
    param(
        [int]$MinFloors = 1,
        [int]$TimeoutSec = 60,
        [int]$Port = 0,
        [int]$IntervalMs = 300
    )
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    $state = $null
    while ((Get-Date) -lt $deadline) {
        try {
            $ping = Invoke-PtaBridge -Cmd 'ping' -TimeoutSec 5 -Port $Port
            if ($ping.isLevelEditor) {
                $state = Invoke-PtaBridge -Cmd 'state' -TimeoutSec 10 -Port $Port
                if ($state.floorCount -ge $MinFloors) { return $state }
            }
        } catch { }
        Start-Sleep -Milliseconds $IntervalMs
    }
    throw [System.TimeoutException]::new(
        "Wait-PtaEditor: 等了 $TimeoutSec 秒还没进入编辑器/floors>=$MinFloors（最后状态：$($state | ConvertTo-Json -Depth 4 -Compress)）")
}

function Save-PtaLevel {
    [CmdletBinding()]
    param([int]$Port = 0, [int]$TimeoutSec = 30)
    return Invoke-PtaBridge -Cmd 'save' -TimeoutSec $TimeoutSec -Port $Port
}

function Set-PtaFloorNums {
    [CmdletBinding()]
    param(
        [bool]$On = $true,
        [int]$Port = 0,
        [int]$TimeoutSec = 40
    )
    return Invoke-PtaBridge -Cmd 'setfloornums' -Args @{ on = $On } -TimeoutSec $TimeoutSec -Port $Port
}

function Select-PtaFloor {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true, Position = 0)]
        [int]$Id,
        [int]$Port = 0,
        [int]$TimeoutSec = 20
    )
    return Invoke-PtaBridge -Cmd 'selectfloor' -Args @{ id = $Id } -TimeoutSec $TimeoutSec -Port $Port
}

# ---------------------------- 播放 ----------------------------

function Start-PtaPlay {
    [CmdletBinding()]
    param([int]$Port = 0, [int]$TimeoutSec = 30)
    return Invoke-PtaBridge -Cmd 'play' -TimeoutSec $TimeoutSec -Port $Port
}

function Stop-PtaPlay {
    <#
    .SYNOPSIS
        退出播放回到编辑器（scnEditor.SwitchToEditMode(false)）。
    #>
    [CmdletBinding()]
    param([int]$Port = 0, [int]$TimeoutSec = 30)
    return Invoke-PtaBridge -Cmd 'exitedit' -TimeoutSec $TimeoutSec -Port $Port
}

function Set-PtaAuto {
    [CmdletBinding()]
    param(
        [bool]$On = $true,
        [int]$Port = 0,
        [int]$TimeoutSec = 20
    )
    return Invoke-PtaBridge -Cmd 'setauto' -Args @{ on = $On } -TimeoutSec $TimeoutSec -Port $Port
}

function Reset-PtaHits {
    [CmdletBinding()]
    param([int]$Port = 0, [int]$TimeoutSec = 20)
    return Invoke-PtaBridge -Cmd 'resethits' -TimeoutSec $TimeoutSec -Port $Port
}

# ---------------------------- 截图 / 反射 / 退出 ----------------------------

function Get-PtaScreenshot {
    <#
    .SYNOPSIS
        游戏内截图（RenderTexture + ReadPixels，不抢焦点）。返回 {path,width,height,bytes}。
    .EXAMPLE
        Get-PtaScreenshot -Path .\out\shot.png
    #>
    [CmdletBinding()]
    param(
        [string]$Path,
        [int]$Width = 0,
        [int]$Height = 0,
        [int]$Port = 0,
        [int]$TimeoutSec = 40
    )
    $a = @{}
    if ($Path) { $a['path'] = Get-PtaFullPath $Path }
    if ($Width -gt 0) { $a['width'] = $Width }
    if ($Height -gt 0) { $a['height'] = $Height }
    return Invoke-PtaBridge -Cmd 'screenshot' -Args $a -TimeoutSec $TimeoutSec -Port $Port
}

function Invoke-PtaReflect {
    <#
    .SYNOPSIS
        反射调试：读/写静态成员，或调用静态方法。临时排查用，不要做正常路径。
    .EXAMPLE
        Invoke-PtaReflect -Type scnEditor -Member instance
        Invoke-PtaReflect -Type GCS -Member difficulty -Value 3
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$Type,
        [Parameter(Mandatory = $true)][string]$Member,
        [object[]]$Arg,
        [int]$Port = 0,
        [int]$TimeoutSec = 20
    )
    $a = @{ type = $Type; member = $Member }
    if ($PSBoundParameters.ContainsKey('Arg')) { $a['args'] = $Arg }
    return Invoke-PtaBridge -Cmd 'invoke' -Args $a -TimeoutSec $TimeoutSec -Port $Port
}

function Go-PtaLevelSelect {
    [CmdletBinding()]
    param([int]$Port = 0, [int]$TimeoutSec = 20)
    return Invoke-PtaBridge -Cmd 'gotolevelselect' -TimeoutSec $TimeoutSec -Port $Port
}

function Quit-PtaGame {
    <#
    .SYNOPSIS
        优雅关闭游戏（Application.Quit()，先 flush 日志）。
    #>
    [CmdletBinding()]
    param([int]$Port = 0, [int]$TimeoutSec = 20)
    return Invoke-PtaBridge -Cmd 'quit' -TimeoutSec $TimeoutSec -Port $Port
}

function Wait-PtaGameExit {
    [CmdletBinding()]
    param([int]$TimeoutSec = 30)
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while ((Get-Date) -lt $deadline) {
        $p = Get-Process -Name 'A Dance of Fire and Ice' -ErrorAction SilentlyContinue
        if (-not $p) { return $true }
        Start-Sleep -Milliseconds 300
    }
    return $false
}
