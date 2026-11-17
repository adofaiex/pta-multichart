# pta-bridge — 进程内 HTTP 测试桥

独立的 UMM 模组：让 DSH（PowerShell）在**进程内**读/写游戏，不需要 computer use、不抢焦点。

- 程序集 / 文件夹 / Info.json Id：`pta-bridge`
- 入口：`PtaBridge.Main.Load`（`TargetFramework=net481`）
- 端点：`http://127.0.0.1:59731/`（端口常量 59731；`PTA_BRIDGE_PORT` 环境变量或命令行 `-port <n>` 可覆盖）
- 日志：`<游戏>\Mods\pta-bridge\bridge.log`（同时进 UMM `Log.txt`，前缀 `[pta-bridge]`）

## 构建 / 部署

```powershell
dotnet build .\pta-bridge.csproj -c Release     # → ..\..\out\pta-bridge\ + <游戏>\Mods\pta-bridge\
dotnet build .\pta-bridge.csproj -c Release -p:Deploy=false   # 只构建不部署
```

## 线程模型

请求线程只做 HTTP + JSON；真正的 Unity 访问全部塞进 `ConcurrentQueue`，由
`BridgeRunner.Update`（`DontDestroyOnLoad` 的 `pta-bridge` 对象）在主线程取出执行，
结果经 `TaskCompletionSource` 回给请求线程。单条命令超时 15s → 返回
`{"ok":false,"error":"timeout ..."}`。所有异常都捕获在 Unity 主循环之外。

HTTP 服务优先用 `HttpListener`；若本机 URL ACL 不允许（非管理员常见），自动退回
`TcpListener` 手写的最小 HTTP/1.1 服务（同样端口、同样协议）。`ping.server` 会说明当前是哪种。

## 协议

```
POST /cmd   {"cmd":"state","args":{}}
         →  {"ok":true,"result":...}   或   {"ok":false,"error":"..."}
GET  /ping  → 同 ping 命令
```

## 命令一览

| cmd | args | 说明 / 返回 |
|---|---|---|
| `ping` | — | `version/unityVersion/gameVersion/scene/isLevelEditor/isPlaying/editorPlayMode/levelPath/acceptedAgreement/port/server/pid/frameCount/uptimeSec` |
| `state` | — | `scene/isLevelEditor/mode("editor"\|"play"\|"none")/levelPath/floorCount/floors(同 floorCount)/angleCount/currentSeqID/paused/isPlaying/playMode/pausedInPlayMode/isLoading/showFloorNums/selectedFloors/selectedFloorCached/songPosition/songPositionV/gameworld[/song/bpm/levelVersion]` |
| `floors` | `from?`, `count?` | **数组**：`seqID, entryBeat, entryTime, speed, isCCW, midSpin, angleLength, opacityVal, holdLength, isFake, position{x,y}, editorNumText, editorNumVisible` |
| `openlevel` | `path` | `scnEditor.instance.OpenLevel(path)`（协程，立即返回）→ `{submitted,path,floorsBefore}`；之后轮询 `state` |
| `openeditor` | `path?` | 已在编辑器：有 path 就 `OpenLevel`，否则什么都不做；不在编辑器：`scnEditor.levelToOpenOnLoad = path` → `scrController.PortalTravelAction(Portal.EditorScene)`（兜底 `ADOBase.loader.LoadScene("scnEditor")` → `SceneManager.LoadScene(...,Additive)`）→ `{submitted,way,path,scene}` |
| `save` | — | `scnEditor.instance.SaveLevel()` → `{saved,path,size,mtimeUtc}` |
| `play` | — | `scnEditor.instance.Play()` |
| `exitedit` | — | `scnEditor.instance.SwitchToEditMode(false)`（异常转错误字符串） |
| `setauto` | `on` | `RDC.auto = on`（= `RDConstants.data.auto`），并清 `editor.autoFailed` |
| `hits` | — | `scrMistakesManager.marginTrackers[0]`：`hitMargins`(枚举名数组)/`hitMarginsLength`/`hitMarginsCount`(按 HitMargin 下标)/`hitMarginsCountByName`/`playerHitMarginCount`/`deaths`/`xScore`/`hardestDifficulty`/`percentComplete`/`percentAcc`/`currentSeqID`/`gameworld` |
| `resethits` | — | `playerManager.mistakesManager.Reset()`，返回重置后的 `hits` |
| `setfloornums` | `on` | 设 `showFloorNums` + `RemakePath()` → `{showFloorNums,count,floors:[{seqID,text,visible}]}` |
| `selectfloor` | `id` | `scnEditor.instance.SelectFloor(floors[id], false)` → `{selected,seqID,selectedFloors}` |
| `invoke` | `type`,`member`,`args?` | 反射读写**静态**成员：无 args=读，1 个值=写，数组=调方法。支持 int/long/float/double/bool/string/enum，返回原生类型或 `ToString()`；找不到成员会列出可用静态成员 |
| `screenshot` | `path?`,`width?`,`height?` | `Camera`（`scrCamera.instance.camobj` → `Camera.main`）+ `RenderTexture` + `ReadPixels` + `EncodeToPNG` → `{path,width,height,bytes}` |
| `log` | — | `{file,count,lines:[最近 200 行]}` |
| `quit` | — | `Application.Quit()`（延迟 ~400ms，先让响应写回；日志已 flush） |
| `gotolevelselect` | — | `ADOBase.GoToLevelSelect()`（附属工具，用于复位） |
| `help` | — | 命令清单 |

## DSH 侧客户端

`..\..\tools\bridge.ps1`：`. .\tools\bridge.ps1` 后可用
`Invoke-PtaBridge / Wait-PtaBridge / Get-PtaState / Get-PtaFloors / Get-PtaHits /
Open-PtaLevel / Open-PtaEditor / Wait-PtaEditor / Save-PtaLevel / Start-PtaPlay / Stop-PtaPlay /
Set-PtaAuto / Reset-PtaHits / Set-PtaFloorNums / Select-PtaFloor / Get-PtaScreenshot /
Get-PtaLog / Invoke-PtaReflect / Go-PtaLevelSelect / Quit-PtaGame / Wait-PtaGameExit`。

## 已知限制

1. **`hits` 要在播放中读**：`exitedit`（`SwitchToEditMode`）会把 `marginTracker` 清空，
   退出后再读就是空数组；`play` 之前也建议先 `resethits`。实测编辑器自动播放
   （`setauto on` + `play`）能正常记录判定（`gameworld=true`，全是 `XPerfect`），
   所以判定结论应「轮询到播放结束前/播放中」取 `hits`。
   `hits.gameworld` 会告诉你当前是否处于记录判定的状态。
2. `screenshot` 会把 `camera.targetTexture` 临时指向 RenderTexture 再 `Render()`，
   那一帧主屏可能闪一下；分辨率默认取 `Screen.width/height`，返回前会恢复原 `targetTexture`。
3. `invoke` 只支持静态成员（够调试用）；返回对象一律 `ToString()`。
4. `openlevel` / `openeditor` 都是异步提交，必须自己轮询 `state`（`floorCount`）。
5. 主线程被长操作占住（例如超大谱面 `RemakePath`）时，命令可能 15s 超时；
   超时的命令仍会在主线程继续执行完。
6. 端口固定 59731：同时只允许一个游戏进程。
