# pta-multichart

一个用于 **冰与火之舞（ADOFAI）** 的 UnityModManager 多轨道制谱模组：在编辑器中支持**多条轨道同屏编辑与点击切换**，保存与试玩时自动编译为**原版可直接游玩的单轨 `.adofai` 谱面**——不注册新的 `LevelEventType`，不写入 `requiredMods`，未安装模组也可正常游玩。

[English](README.md)

---

## 核心原理

ADOFAI 原版引擎只沿一条主轨做按键判定。`pta-multichart` 将**编辑态（逻辑谱）**与**存盘/游玩态（播放谱）**分离：

1. **逻辑谱（编辑态内存与元数据）**
   - `Track 0` 为主轨；`Track 1..N` 为副轨，创建时锚定到主轨某块砖（`seqID`），自动继承锚点处的出射角、旋转方向（`Twirl`）与有效 BPM（`SetSpeed`）。
   - 编辑时所有轨道同时渲染在视口中（`TrackOverlay` 将非当前轨道绘制为可点击的幽灵砖），直接点击任意轨道的砖块即可切换编辑对象，全局关卡 `settings` 保持共享。
   - 完整逻辑谱 JSON 保存在主轨 `0` 号砖一条锁定的 `EditorComment` 中（前缀 `[[PTA-MC:v1]]\n`）。

2. **播放谱（在 `SaveLevel` / `Play` 时自动编译）**
   - **副轨点击嵌入**：按各自时间线换算歌曲时刻并嵌入主轨（`seq 1` 对齐锚点砖且不重复插入；`seq 0` 为幽灵起点，不占拍、不绘制、不参与判定）。
   - **中途点击切分**：在宿主主轨砖的时间窗内按比例切分，插入透明中旋（`999`）并挂载 `PositionTrack(opacity = 0, justThisTile = true)`，同时用 `Floor` 装饰（`pta-mc-mask`）补回原主轨视觉形状。
   - **伪双押处理**：与主轨点击时间差小于半个判定窗（`MinClickGapSeconds`，默认 `0.05s`）的副轨点击，按 `1°` 扫角 + `999` 中旋写入并从当前砖扣回 `1°`，保持后续主轨点击时间不变。
   - **副轨装饰与星球动画**：副轨砖块与双星导出为原生 `AddObject`（`Floor` / `Planet`）装饰，携带旋转/变速/长按/检查点等地表图标，并通过 `MoveDecorations` 沿副轨点击区间线性折线驱动星球公转。
   - **事件映射与重复展开**：主轨事件的 `floor` 与 `TileRelativeTo`（`ThisTile` / `Start` / `End`）经 `FloorMap` 映射；`RepeatEvents(Floor)` 在步长恒定时改写 `gapLength`，否则展开副本并打上 `pta-mc-unrolled`（若与作者 `SetConditionalEvents` 桶标签冲突则自动退让并报警）。
   - **拍网格说明**：受 ADOFAI 砖模型（`entry[i] = exit[i-1] + π`）限制，每向主轨插入 1 块点击砖会使后续主轨顺延 1 拍；面板内置 6 条只读编译预检（按 `F8` 显隐）。

---

## 目录结构

```text
pta-multichart/
├── pta-multichart.slnx
├── src/
│   ├── Core/                         # 纯 C# 核心层（不依赖 Unity，模组与测试共用）
│   │   ├── Model/                    # LogicalChart / Track / ChartEvent / MiniJson
│   │   ├── Timing/                   # TimelineBuilder（对齐 r150 节拍/位置数学）
│   │   └── Compile/                  # ChartCompiler、EventMapper、RepeatEventProcessor、
│   │                                 # DecorationBuilder、PreflightBuilder、PlayableChart
│   └── Mod/                          # UMM 模组层（net481，引用游戏与 Unity DLL）
│       ├── Main.cs                   # UMM 入口（PtaMultichart.Main.Load）
│       ├── Info.json                 # UMM 清单
│       ├── Api.cs                    # 供自动化桥调用的静态入口
│       ├── Game/                     # LevelAdapter、EventFactory
│       ├── Session/                  # MultitrackSession（切轨、换入/换出播放谱）
│       └── Editor/                   # Harmony 补丁、MultitrackPanel（IMGUI）、TrackOverlay、PatchLoadGuard
├── mods/
│   └── pta-bridge/                   # 可选进程内 HTTP 测试桥模组（127.0.0.1:59731）
├── tests/
│   ├── TimingTests/                  # 无头单元测试（47 条断言）
│   ├── CompileTests/                 # 无头编译测试（192 条断言）
│   ├── LevelGen/                     # testlevels 夹具生成器
│   └── shots/                        # 编辑器验证截图
├── testlevels/                       # 回归测试谱面（t0..t6、probe_b、template）
├── tools/                            # PowerShell 桥客户端与端到端回归脚本（test_all.ps1）
├── scripts/                          # 仓库检查脚本（check-file-size.mjs）
└── docs/archive/                     # 历史设计方案（v1/v2）、实测报告与决策记录
```

---

## 环境要求

- **游戏版本**：ADOFAI r150 / 3.4.0（Unity `6000.3.25f1`）+ **UnityModManager**（`0.27.0+`）
- **.NET SDK**：.NET 9.0 SDK+（同时构建 `net9.0` 单元测试与 `net481` 模组）
- **Node.js**（可选）：用于运行 `scripts/check-file-size.mjs`

---

## 构建、测试与使用

### 1. 运行纯逻辑单元测试（无需安装游戏）

`src/Core` 不引用任何 Unity 程序集，修改核心逻辑后可直接无头验证：

```powershell
dotnet run --project tests/TimingTests/TimingTests.csproj -c Release
dotnet run --project tests/CompileTests/CompileTests.csproj -c Release
```

### 2. 构建与部署模组

如果游戏不在默认路径，请通过 `-p:GameDir="..."` 指定游戏根目录：

```powershell
# 构建并自动部署到 <GameDir>\Mods\pta-multichart\
dotnet build src/Mod/pta-multichart.csproj -c Release -p:GameDir="C:\Program Files (x86)\Steam\steamapps\common\A Dance of Fire and Ice"

# 仅构建到 out\pta-multichart\（不部署到游戏目录）
dotnet build src/Mod/pta-multichart.csproj -c Release -p:Deploy=false
```

### 3. 游戏内操作

1. 在 ADOFAI **关卡编辑器** 中打开任意关卡。
2. 面板默认显示（按 **`F8`** 可显隐 **PTA Multichart** 面板）。
3. 在主轨选中一块砖，点击 **`Create sub track @ selected tile`** 创建副轨。
4. 在面板轨道列表或直接点击视口中的幽灵砖即可切换当前编辑轨道，正常使用原版方向键与事件面板编辑。
5. 点击 **`Recompile preview (log)`** 可查看 6 条编译预检；保存（`Ctrl+S`）或试玩（`Play`）会自动编译并无缝恢复编辑态。

### 4. 自动化端到端测试（可选，依赖 `pta-bridge`）

```powershell
# 构建并部署 HTTP 测试桥（监听 127.0.0.1:59731）
dotnet build mods/pta-bridge/pta-bridge.csproj -c Release

# 游戏启动并进入编辑器后运行：
pwsh -NoProfile -File tools/test_all.ps1
```

桥命令协议详见 [mods/pta-bridge/README.md](mods/pta-bridge/README.md)。

---

## 贡献指南

1. **保持 `src/Core/` 纯净**：不要在 `src/Core/` 中引入 `UnityEngine` 或 `Assembly-CSharp` 依赖，确保无头测试始终可运行。
2. **兼容性红线**：
   - 不要注册新的 `LevelEventType`。
   - 不要在导出的 `.adofai` 或 `Info.json` 中写入 `requiredMods` / `Requirements`。
   - 生成物标签统一使用 `pta-mc*` 命名空间，发生条件事件标签交集时保留作者标签。
3. **单文件大小控制**：提交前检查单文件行数（警告 > 400 行，失败 > 800 行）：
   ```bash
   node scripts/check-file-size.mjs --fail
   ```
4. **提交前校验**：确保 `TimingTests`（47 checks）、`CompileTests`（192 checks）以及 `pta-multichart.csproj` / `pta-bridge.csproj` 均为 0 错误、0 警告通过。
