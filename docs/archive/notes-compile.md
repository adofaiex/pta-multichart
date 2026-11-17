# 编译层笔记（compile-dev）— 待 Lead 汇总进 `结果_v1.md`

范围：`src/Core/Compile/**`、`tests/CompileTests/**`。纯 C#，不引用 Unity。
测试：`dotnet run --project tests/CompileTests` → **ALL TESTS PASSED (118 checks)**；
`tests/TimingTests` 47 条仍全过；`dotnet build src/Mod/... -c Release -p:Deploy=false` 0 警告 0 错误。

## 1. 文件与职责

| 文件 | 内容 |
|---|---|
| `Compile/PlayableChart.cs` | Lead 的 `FloorMap` / `InsertedClick` / `PlayableChart` / `CompileError`（未改） |
| `Compile/ChartCompiler.cs` | 主流程：副轨点击落砖 → 切分 → PositionTrack（透明度 + 位置补偿）→ 砖号/事件映射 → `RepeatEvents` → 记号 |
| `Compile/DecorationBuilder.cs` | `AddObject(Floor/Planet)` / `MoveDecorations` 字段拼装、副轨作者装饰烤 Global、枚举白名单、颜色校验 |

## 2. 位置补偿（本轮新增，已按 Lead 要求实现）

r150 的砖是**链式**摆放（`InstantiateFloatFloors`：`pos[i+1] = pos[i] + v(exit_i)`，`v = (sin, cos)` 单位向量），
所以插一块砖会让**其后所有砖整体位移一整块**。补偿分两处：

1. **插入砖**：把它拉回原弧上按 sweep 比例该在的点。
   `positionOffset = ideal - natural`，与 `opacity = 0` **合成同一条** `PositionTrack`，`justThisTile = true`
   （opacity 与位置都不能漏到后面）。
   `ideal` 由 `ChartCompiler.IdealArcPoint` 给出：弦长恒为 1 砖长，圆心在中垂线上，半径 `0.5 / sin(sweep/2)`，
   起始半径向量 `P_k - 圆心`，按 `s * sweep * f` 旋转（`s = isCCW ? +1 : -1`）。
   `sweep = π` 时它精确退化成 Lead 给的形状（圆心 = 弦中点、半径 0.5），非 180° 砖也成立。
   `sweep → 2π`（回头弯）圆模型退化，回落到直线插值。
2. **被切砖的后继砖**（逻辑 k+1 对应的播放砖）：一条 `PositionTrack`，`positionOffset = v(angleData[k]) - Σ v(exit_i)`，
   **不带** `justThisTile`（默认 false）→ 补偿会传递给后面所有砖，把整条轨拉回原位。这条事件也不带 `opacity`。

要点：游戏侧 `floor.position = startPos + vector2`（`vector2` = 上砖传下来的 carry + 本砖所有 `positionOffset`），
且只有本条砖上出现**非** `justThisTile` 的 `PositionTrack` 时 `carry` 才更新（`scnGame.ApplyCoreEventsToFloors`）。
所以编译时按「相对 carry 的增量」发事件，并在同一条砖上只发**一条**补偿事件（不叠加、不漏传）。
`positionOffset` 单位是**砖长**（游戏里 `Get<Vector2>("positionOffset") * tileSize`），本实现全程用砖长。

实测断言（`tests/CompileTests` 的 `TestPositionCompensation` / `TestRepeatConstant`）：
- ① 模拟累加后，每块逻辑砖在播放谱里的位置 == 原时间线位置，误差 **< 1e-6**（实测 ~2e-8）；
- ② 插入砖到圆心距离 = 0.5，落在 `(-1.5, -0.5)`（不是链式的 `(-1,-1)`）；
- 主轨 `[180,180]` 一例：插入砖 `positionOffset = (-0.5, +0.5)`，后继砖 `(0, +1)`。
- 残留 ~2e-8 的来源：Timing 里 `PiF`（float π）/`TwoPi`（float 2π）与 `Math.PI` 混用，角度链有 1e-7 量级抖动，可忽略。

## 3. 副轨口径（本轮按 Lead 要求改）

- **副轨 1 号砖 = 锚点砖的世界坐标**，0 号砖是它后面一块的幽灵起点（`anchor - v(exit_0)`），**不画装饰**。
  实现：`shift = anchorPos - Positions[1]`，`world[i] = shift + Positions[i]`。
- 装饰只画 `1 .. N-1` 号砖；终点砖没有 `angleData`（`Angle = NaN`）所以跳过，实际画 `1 .. N-2`。
- 两颗 Planet 起点 = `world[1]` = 锚点砖坐标。
- 副轨作者装饰（`Track.Decorations`）按方案 §5 烤成 Global：`position = world[floor] + 原 position`（砖长），
  `relativeTo = "Global"`，`floor = 0`。
- 主轨作者装饰保留原 `relativeTo`，但 `floor` 走砖号映射（有断言：逻辑 2 → 播放 3）。
- 插入砖的 `InsertedClick.PlayableSeq` / `SongTime` 已填好，`Map.ToLogical(插入砖) = 它前面那块逻辑砖`
  （`[180,180]` 一例播放→逻辑 = **0,1,1,2**，测试里把它打印出来了；Lead 在游戏里看到 `0,1,2,2`，
  应是那版补丁/构建的问题，请对照这串数复核）。

## 4. 已确认的方案问题（Lead 已用游戏实测复核）

### 4.1 插入一块砖必然给主轨加 1 拍（不是副轨间隙）

r150 每块砖的时长 = `mod((exit-entry)*dir, 2π)/π` 拍，而 `entry = 上一块 exit + π`。
一串砖的总旋转 = 方向变化 + 砖数×π，**多一块砖就多 π（1 拍）**，与弧长无关：

| 谱 | floor1 | floor2 | floor3 | 每段 |
|---|---|---|---|---|
| `[180,180]` | 0.6s | — | — | 1 拍 |
| `[180,270,180]`（插一刀） | 0.3s | 1.2s | — | 0.5 拍 + 1.5 拍 = 2 拍 |

Lead 用桥读 `scrFloor.entryTime` 实测一致。结论：**「多一次点击」与「主轨总时长不变」在 r150 不可兼得**，
每次插入的固有代价是奇数个 π（实测 1 拍）。方案 §7.2「各段 angleLength 之和不变」不成立；§7.4「往后推」方向对、量不对。
（本轮只做位置补偿，不动时长——Lead 说这条他写进 `结果_v1.md`。）

### 4.2 §8 的「每 1/4 拍取样」做不了 → 已按折线实现

`MoveDecorations` 只能挂在砖上、按砖的拍触发；1/4 拍在播放谱里没有宿主砖。
现在：**锚点砖 → 第 1 次点击**一条、**每次点击 → 下一次点击**一条（红蓝各一条），线性插到下一次点击的轨道点。
`duration = 秒 × 主轨BPM / 60`（单位是播放谱的拍），`positionOffset = 目标 - 装饰 startPos`（绝对量，砖长），
所以星球走折线不是圆弧；最后一次点击之后没有尾巴（没有可挂的砖）。

### 4.3 其它小项

- `AddObject` 没有 `trackGlowIntensity` 这个 key（只有 `trackGlowEnabled` + `trackGlowColor`），强度是关卡级设置。
  现在用 `TrackGlowIntensity > 0` 决定 enabled、颜色取 `TrackColor`。
- `AddObject.trackColorType` 的枚举是 `FloorDecorationColorType`（Single/Glow/Blink/Switch/Rainbow/Volume），
  **没有 Stripes**；关卡 `TrackColorType` 若是 Stripes 直接抄会让 `LevelEvent.Decode` 的 `Enum.Parse` 抛异常 → 已加白名单回落 `Single`；
  `trackStyle` 同样白名单。
- 颜色必须是不带 `#` 的 6/8 位十六进制（`RDUtils.HexToColor` 用 `Substring(0,2)` 直接 parse），已校验 + 去 `#`。
- 装饰 `depth` 直接写 `CompileSettings.DecorationDepth`（默认 -1 → `SetDepth` 落到 "Default" 层、order 1）。
  遮盖装饰要盖住接头缺口又不能盖住星球，**层序仍需游戏里看一眼**。
- 副轨 `TileShape` 无法逐装饰设置（`AddObject` 没有这个 key），只能靠关卡全局设置。
- `RepeatEvents` 展开时按方案摘掉匹配 tag。若同一 tag 同时被 `SetConditionalEvents` 的桶名引用，摘掉会让条件桶对不上
  （方案内部冲突）；已按方案实现，风险记在这里。
- `RepeatEvents(Floor)` 若 `gapLength = 0`，逻辑目标全落在同一块砖上 → 步长恒为 0，走「只改数字」路径，不会展开成空循环。
- 主轨砖上若**作者自己**已经有 `PositionTrack`，位置补偿会与它叠加（游戏是累加的）；此时 carry 的记账不再精确，
  补偿只保证「至少不比原来差」。这种混用属于语义冲突，建议面板层提示。
