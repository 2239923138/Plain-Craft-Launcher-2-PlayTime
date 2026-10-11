# PCL 游戏时长统计与显示 实现计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 从存档的统计文件读取游玩时长,在启动页、版本选择列表、版本设置概览三处显示每个版本的游戏总时长。

**Architecture:** 新增一个 `ModPlayTime` 模块负责「解析统计文件 → 按存档汇总 → 按版本聚合 → mtime 缓存」。缓存落在一个 JSON 文件里,以每个存档 stats 目录的最新修改时间做失效判断,避免每次都全量重读。UI 三处都只从内存缓存读值,不在 UI 线程做磁盘 I/O。

**Tech Stack:** VB.NET (.NET Framework 4.8) / WPF / Newtonsoft.Json / 项目自有的 `LoaderTask` 异步框架与 `MeloongCore` 工具类。

## Global Constraints

- 目标框架 `.NET Framework 4.8`,主项目是**旧式非 SDK 的 VB.NET WPF 工程**,只能用 VS 的 MSBuild 构建,不能用 `dotnet build`。
- 新增 `.vb` 文件必须同时加进 `Plain Craft Launcher 2\Plain Craft Launcher 2.vbproj` 的 `<Compile Include="..." />` 条目(VB 项目不自动通配)。
- 文本文件一律 **UTF-8 无 BOM**。**不要新建含中文的 `.bat`** —— 本机代码页 936 会把中文字节吃掉导致命令损坏(仓库已有的 `build.bat` 因此全用 ASCII)。
- 构建与产物验证统一用仓库根目录的 `build.bat`(默认 Release),产物落在 `dist\Release\`。
- **开发时从 `dist\Release\` 运行**,不要从 `bin\` 或 `obj\` 运行 —— PCL 会在 exe 同级创建 `PCL\` 和 `.minecraft\`,在构建输出目录里会被清理掉。
- 沿用项目既有风格:大驼峰公有成员、`Logger.Info/Error/Warn` 记日志、`RunInUi` 回填 UI、`LoaderTask` 做后台任务、`DirectoryUtils`/`FileUtils` 做文件操作。不要引入新依赖。

---

## 领域知识(实现前必读)

这一节是调研得到的、代码里看不出来的事实。**每一条都在用户真实的存档文件上验证过。**

### 统计文件的位置与命名

```
<游戏根目录>\saves\<存档名>\stats\<玩家UUID>.json
```

`<游戏根目录>` 是**版本隔离后**的目录,即 `McInstance.PathIndie`(**不是** `PathVersion`)。这一点是关键:

- 开了版本隔离的版本 → 存档在 `<版本文件夹>\saves\`
- 没开隔离的版本 → 存档在 `.minecraft\saves\`

用 `McInstance.PathIndie & "saves\"` 两种情况都正确。用户机器上**两种都真实存在**:`.minecraft\saves\` 有 12 个存档,`versions\1.21.8\saves\`、`versions\GreedyCraft\saves\` 等还有几十个。

### 文件名 = 玩家 UUID,一个存档可能有多个

单机存档通常只有一个文件(你自己)。联机 / 开过局域网 / 换过离线用户名时会有多个。

### 三种 JSON 格式(都真实存在,必须全部处理)

**格式 A —— 1.17+ 嵌套格式**(用户机器上 **180 个文件**是这种):

```json
{"stats":{"minecraft:custom":{"minecraft:play_time":5146,"minecraft:jump":13}},"DataVersion":3955}
```

**格式 B —— 1.13~1.16 嵌套格式**:结构与 A 相同,但键名是 `minecraft:play_one_minute`。**用户机器上没有样本**,但按文档必须兼容(1.17 只是改名,老存档不会自动加新键)。

**格式 C —— 1.12 及更早的扁平格式**(用户机器上 **5 个文件**是这种):

```json
{"stat.flyOneCm":7980,"stat.playOneMinute":5498,"stat.leaveGame":1}
```

**没有 `stats` 外层,也没有 `minecraft:custom` 嵌套,键名直接是 `stat.playOneMinute`。**

### 值的单位是 tick,不是分钟

三种键名的值**单位都是 tick**(20 tick = 1 秒),名字里的 "minute" 是历史误称。

实测验证:`versions\GreedyCraft\saves\New §6Default Preset §b(BoP)\` 的值是 `1656388`。按 tick 算是 1656388 ÷ 20 ÷ 3600 = **23 小时**(GreedyCraft 是大型整合包,合理);按分钟算是 1150 天,显然不可能。

**换算:`秒 = tick / 20`,`小时 = tick / 72000`。**

### 统计口径:只算当前账号

用户机器上 `versions\Dream\saves\捕梦者：崩坏的梦境\stats\` 有 **71 个**不同玩家的统计文件。实测对比:

| 口径 | 数值 |
|---|---|
| 全部文件求和 | 3597.4 小时 |
| 单个最大文件 | 2164.1 小时 |
| 用户自己的 UUID(`4d594ff4-…`) | **9.6 小时** |

求和是真实值的 **373 倍**。所以**必须只统计当前账号的 UUID**,不能求和。这是用户明确确认的口径。

用户补充过一句关键背景:`捕梦者` 那个存档是**整合包作者提供的**,那 71 个文件是作者测试时生成的,与用户无关。也就是说**整合包确实会自带带统计文件的存档**,不能假设「存档里的统计文件都是自己的」。

那为什么还保留「只有一个文件时直接采用」的兜底规则?**因为实测过误判风险为零。** 扫过用户全部 46 个「stats 目录下只有一个 JSON」的存档:

| 那个文件是谁的 | 数量 |
|---|---|
| 用户自己的在线 UUID `4d594ff4-…` | 45 |
| 无法判定(1.12.2 的旧离线名 `98ba4b36-…`) | 1 |

也就是说,整合包自带的存档要么是用户自己创建并玩过的(带用户 UUID),要么像捕梦者那样带一堆文件 —— **不存在「只有一个文件、且不是用户的」这种组合**。所以兜底规则在真实数据上不会把作者的时长算到用户头上,可以放心保留。

### PCL 的离线 UUID 算法(和原版 Java 不同)

原版 Java 的离线 UUID 是 `UUID.nameUUIDFromBytes("OfflinePlayer:" + name)`。**PCL 用的是自己的一套**,在 `ModLaunch.vb:1173`:

```vb
Public Function McLoginLegacyUuid(Name As String)
    Dim FullUuid As String = Name.Length.ToString("X").EnsureLength("0", 16) & Name.GetStableHashCode().ToString("X").EnsureLength("0", 16)
    Return FullUuid.Substring(0, 12) & "3" & FullUuid.Substring(13, 3) & "9" & FullUuid.Substring(17, 15)
End Function
```

**直接调用它,不要自己实现。** 已用真实文件名反推验证:文件名 `00000000-0000-3001-998f-501bc686b6bb` 中 `3001` 的 `001` 说明当时用的离线用户名是 **1 个字符**,与算法一致。

**但是游戏实际启动时用的不是这个函数。** `ModLaunch.vb:701` 用的是 `McLoginLegacyUuidWithCustomSkin(用户名, SkinType, SkinName)`,它在该结果之上按离线皮肤设置做调整(`ModLaunch.vb:1117-1145`):

- `SkinType` 1/2/4 → 把 UUID **最后 5 位十六进制**递增,直到皮肤性别匹配
- `SkinType` 3 → 可能替换成 `McLoginMojangUuid` 的结果(正版 UUID)

所以存档里的文件名对「同一个离线用户名 + 不同皮肤设置」是不同的。**解法是只比前 27 位**:前 27 位在所有这些变体里都不变,只有最后 5 位会动。这就是 `PlayTimeAccount.Matches` 里离线账号走前缀比较的原因。

### tick 的三种返回值约定

`PlayTimeFromStatsFile` 和它下游的 `PlayTimeGetSave` / `PlayTimeGetVersion` 用同一个三态约定,UI 层靠它区分「没有」和「坏了」:

| 值 | 含义 | 显示 |
|---|---|---|
| `> 0` | 正常时长 | 格式化后的时长 |
| `0` | 没有数据(没存档、没有统计文件、格式正常但没有时长字段) | 什么都不显示 |
| `-1` | 真的读取出错(文件存在但读不动或解析失败;或存档里有多个统计文件却一个都匹不上归属) | 「出错」 |

**不要用 `Math.Max(0, …)` 之类的钳制把 `-1` 抹平** —— 那会让「出错」显示成一个偏低但看起来正常的数字,比不显示更糟。

### 账号 UUID 的来源(PCL 已缓存在 Settings 里)

| 登录方式 | UUID 键 | 用户名键 |
|---|---|---|
| 微软 | `CacheMsV2Uuid` | `CacheMsV2Name` |
| 统一通行证(Nide) | `CacheNideUuid` | `CacheNideName` |
| Authlib-Injector | `CacheAuthUuid` | `CacheAuthName` |
| 离线 | 由用户名推导 | `LoginLegacyName`(**值为 `名称¨皮肤类型`,要 `BeforeFirst("¨")` 截断**) |

### 两个现成的关键锚点

- **`McInstance.ToListItem()` 只有两个调用点**:`Controls\MyCard.vb:140`(版本选择列表)和 `Pages\PageInstance\PageInstanceOverall.xaml.vb:32`(版本设置概览顶部框)。这正好就是需要显示时长的两处,所以在这一个函数里填值可以同时覆盖两个界面,不用分别改。
- **`MyListItem.xaml` 的所有列都按 `x:Name` 引用,没有任何地方按索引引用列**(`ColumnPaddingRight.Width`、`ColumnLogo.Width` 等)。所以在第 3 列之后插入一个新列是安全的,不会破坏现有布局代码。

---

## 文件结构

**新增 1 个文件**

| 文件 | 职责 |
|---|---|
| `Plain Craft Launcher 2\Modules\Minecraft\ModPlayTime.vb` | 全部核心逻辑:解析统计文件、账号 UUID 解析、按存档/按版本汇总、mtime 缓存、格式化、异步加载器与三个触发入口。**不含任何 UI 代码。** |

**修改 9 个文件**

| 文件 | 改动 |
|---|---|
| `Plain Craft Launcher 2\Plain Craft Launcher 2.vbproj` | 注册新文件的 `<Compile Include>` |
| `Plain Craft Launcher 2\Controls\MyListItem.xaml` | 新增一个 `Auto` 列放时长文本 |
| `Plain Craft Launcher 2\Controls\MyListItem.xaml.vb` | 新增 `PlayTime` 属性,悬停时随按钮淡出 |
| `Plain Craft Launcher 2\Modules\Minecraft\ModMinecraft.vb` | `ToListItem` 里填入时长(同时覆盖列表与概览两处) |
| `Plain Craft Launcher 2\Pages\PageSelectRight.xaml.vb` | 触发后台统计,统计完成后刷新列表 |
| `Plain Craft Launcher 2\Pages\PageLaunch\PageLaunchRight.xaml` + `.xaml.vb` | 启动页新增「游戏时长」卡片 + 手动刷新按钮 |
| `Plain Craft Launcher 2\Modules\Minecraft\ModLaunch.vb` | 启动时记录快照、退出时更新当前版本 |
| `Plain Craft Launcher 2\FormMain.xaml.vb` | 启动时补一次漏掉的更新 |

**为什么核心逻辑集中在一个模块文件里:** 仓库既有惯例就是大模块文件(`ModMinecraft.vb` 2500 行、`ModLaunch.vb` 2200 行),`Modules\Minecraft\` 下的模块都是 Module。拆成多个文件会偏离惯例,且这些函数互相调用密集。但**严格把 UI 代码留在各页面里**,模块内一行控件代码都不写。

---

## 关于测试

**这个项目没有任何测试工程** —— 唯一带测试的 `MeloongCore\Tests` 是子模块自己的,且其规则明确禁止它知道 Minecraft 存档格式。给主项目新建 xUnit 工程并接进 MSBuild 是用户没要求的架构改动。

因此本计划用**真实数据对照**代替单元测试:核心解析函数写成无副作用的纯函数,再用用户**真实的存档文件**核对结果。期望值已在「领域知识」一节实测得出,直接写进各任务的验收步骤。

如果想补真正的单元测试工程,应在**所有任务完成后**单独做,不要插进本计划的节奏里。

---

## Task 1: 核心解析模块(格式解析 + 格式化 + UUID 解析)

**Files:**
- Create: `Plain Craft Launcher 2\Modules\Minecraft\ModPlayTime.vb`
- Modify: `Plain Craft Launcher 2\Plain Craft Launcher 2.vbproj:335` 附近

**Interfaces:**
- Consumes: `MeloongCore` 的 `FileUtils.Exists/ReadAsJson`、`McLoginLegacyUuid`(`ModLaunch` 模块里)、`Settings.Get(Of T)`、`Logger`
- Produces:
  - `Public Function PlayTimeFromStatsFile(FilePath As String) As Long` —— 单个统计文件的 tick 数;文件不存在或无时长字段返回 0,读取/解析失败返回 -1
  - `Public Function PlayTimeFormat(Ticks As Long) As String` —— tick 转可读文本;-1 → 「出错」,0 → 「—」
  - `Public Function PlayTimeCurrentAccount() As PlayTimeAccount` —— 当前账号的匹配信息(在线账号的完整 UUID + 离线账号的 27 位前缀)
  - `Public Class PlayTimeAccount`,成员:`Online As List(Of String)`(32 位小写无连字符)、`OfflinePrefix As String`(27 位,空串表示无离线账号)、`Matches(UuidFromFile As String) As Boolean`

- [ ] **Step 1: 创建模块骨架**

新建 `Plain Craft Launcher 2\Modules\Minecraft\ModPlayTime.vb`:

```vb
''' <summary>
''' 统计各版本、各存档的游戏时长。
''' 数据来源是存档 stats 目录下的统计文件。游玩时长记录在
''' minecraft:play_time(1.17+)/ minecraft:play_one_minute(1.13~1.16)/
''' stat.playOneMinute(1.12 及更早)三个键之一,值单位是 tick(20 tick = 1 秒),
''' 键名里的「分钟」是历史误称。
''' </summary>
Public Module ModPlayTime

#Region "解析统计文件"

    Private Const TicksPerSecond As Long = 20

    ''' <summary>
    ''' 读取一个统计文件里的游玩时长,单位 tick。
    ''' 文件不存在、或格式正常但没有可识别的时长字段,返回 0;
    ''' 文件存在但读取或解析失败,返回 -1(调用方据此显示「出错」)。
    ''' </summary>
    Public Function PlayTimeFromStatsFile(FilePath As String) As Long
        If Not FileUtils.Exists(FilePath) Then Return 0
        Dim Json As JObject = Nothing
        Try
            Json = FileUtils.ReadAsJson(Of JObject)(FilePath)
        Catch ex As Exception
            Logger.Warn(ex, "读取存档统计文件失败:" & FilePath)
            Return -1
        End Try
        If Json Is Nothing Then Return -1

        '格式 A / B:1.13+ 的嵌套结构 {"stats":{"minecraft:custom":{"minecraft:play_time":123}}}
        Dim Custom As JObject = Nothing
        Dim StatsNode = Json.Item("stats")
        If StatsNode IsNot Nothing AndAlso StatsNode.Type = JTokenType.Object Then
            Custom = TryCast(CType(StatsNode, JObject).Item("minecraft:custom"), JObject)
        End If
        If Custom IsNot Nothing Then
            Dim Modern = PlayTimeReadTicks(Custom.Item("minecraft:play_time"))
            If Modern >= 0 Then Return Modern
            Dim Legacy = PlayTimeReadTicks(Custom.Item("minecraft:play_one_minute"))
            If Legacy >= 0 Then Return Legacy
        End If

        '格式 C:1.12 及更早的扁平结构 {"stat.playOneMinute":123}
        Dim Flat = PlayTimeReadTicks(Json.Item("stat.playOneMinute"))
        If Flat >= 0 Then Return Flat

        Return 0
    End Function

    ''' <summary>读一个 JSON 节点里的 tick 数。不是整数节点(缺失、字符串、对象)返回 -1。</summary>
    Private Function PlayTimeReadTicks(Token As JToken) As Long
        If Token Is Nothing Then Return -1
        If Token.Type <> JTokenType.Integer Then Return -1
        Return Token.ToObject(Of Long)()
    End Function

#End Region

#Region "格式化"

    ''' <summary>
    ''' 把 tick 格式化为可读文本。-1 显示「出错」,0 显示破折号,
    ''' 1 小时以上显示「x 小时 y 分钟」,1 分钟以上显示「x 分钟」,不足 1 分钟显示「x 秒」。
    ''' </summary>
    Public Function PlayTimeFormat(Ticks As Long) As String
        If Ticks < 0 Then Return "出错"
        If Ticks = 0 Then Return "—"
        Dim TotalSeconds = Ticks \ TicksPerSecond
        Dim Hours = TotalSeconds \ 3600
        Dim Minutes = (TotalSeconds Mod 3600) \ 60
        If Hours > 0 Then Return $"{Hours} 小时 {Minutes} 分钟"
        If Minutes > 0 Then Return $"{Minutes} 分钟"
        Return $"{TotalSeconds} 秒"
    End Function

#End Region

#Region "账号 UUID"

    ''' <summary>
    ''' 当前账号在统计目录里的匹配信息。
    ''' </summary>
    Public Class PlayTimeAccount

        ''' <summary>在线账号的完整 UUID,统一成 32 位小写十六进制(无连字符)。</summary>
        Public ReadOnly Online As New List(Of String)

        ''' <summary>离线账号 UUID 的前 27 位前缀。空串表示没有可用的离线账号。</summary>
        Public OfflinePrefix As String = ""

        ''' <summary>判断一个统计文件名(不含扩展名)是否属于当前账号。</summary>
        Public Function Matches(UuidFromFile As String) As Boolean
            Dim U As String = If(UuidFromFile, "").Replace("-", "").ToLowerInvariant()
            If U.Length <> 32 Then Return False
            If Online.Contains(U) Then Return True
            Return OfflinePrefix <> "" AndAlso U.StartsWith(OfflinePrefix)
        End Function

    End Class

    ''' <summary>
    ''' 当前账号的匹配信息。
    ''' 离线账号只比前 27 位,是因为 PCL 会按离线皮肤设置递增 UUID 的最后 5 位
    ''' (McLoginLegacyUuidWithCustomSkin),前 27 位才是该用户名稳定的部分。
    ''' </summary>
    Public Function PlayTimeCurrentAccount() As PlayTimeAccount
        Dim Result As New PlayTimeAccount
        PlayTimeAddOnline(Result, Settings.Get(Of String)("CacheMsV2Uuid"))
        PlayTimeAddOnline(Result, Settings.Get(Of String)("CacheNideUuid"))
        PlayTimeAddOnline(Result, Settings.Get(Of String)("CacheAuthUuid"))
        '离线登录:LoginLegacyName 是以 ¨ 分隔的玩家名列表,第一个是当前使用的那个
        Dim LegacyName = Settings.Get(Of String)("LoginLegacyName")
        If Not String.IsNullOrWhiteSpace(LegacyName) Then
            Dim Uuid As String = McLoginLegacyUuid(LegacyName.BeforeFirst("¨")).Replace("-", "").ToLowerInvariant()
            If Uuid.Length = 32 Then Result.OfflinePrefix = Uuid.Substring(0, 27)
        End If
        Return Result
    End Function

    Private Sub PlayTimeAddOnline(Target As PlayTimeAccount, Uuid As String)
        If String.IsNullOrWhiteSpace(Uuid) Then Return
        Target.Online.Add(Uuid.Replace("-", "").ToLowerInvariant())
    End Function

#End Region

End Module
```

- [ ] **Step 2: 把新文件注册进工程**

在 `Plain Craft Launcher 2\Plain Craft Launcher 2.vbproj` 第 335 行 `<Compile Include="Modules\Minecraft\ModLaunch.vb" />` 的**前一行**插入:

```xml
    <Compile Include="Modules\Minecraft\ModPlayTime.vb" />
```

- [ ] **Step 3: 构建**

Run:
```bash
build.bat Release
```

Expected: 输出以 `BUILD OK` 结尾,`Output: G:\Workspace\PCE_ACE\dist\Release\Plain Craft Launcher 2.exe`。

若报 `McLoginLegacyUuid` 未定义,说明它不在可访问的 `Module` 里 —— 检查 `ModLaunch.vb:1173` 所在模块的声明,必要时改成 `ModLaunch.McLoginLegacyUuid(...)`。

- [ ] **Step 4: 用真实文件核对解析与换算**

已实测的期望值(列在「领域知识」一节,这里再抄一份便于核对):

| 统计文件 | 格式 | tick 值 | 期望 `PlayTimeFormat` 输出 |
|---|---|---|---|
| `.minecraft\saves\新的世界\stats\4d594ff4-e1a0-4023-886e-c5e6983ed32b.json` | A | 5146 | `4 分钟` |
| `.minecraft\versions\1.21.8\saves\新的世界\stats\4d594ff4-….json` | A | 22534 | `18 分钟` |
| `.minecraft\versions\1.12.2\saves\新的世界\stats\98ba4b36-6db8-975f-3c1f-6254fde7698a.json` | C | 6203 | `5 分钟` |
| `.minecraft\versions\GreedyCraft\saves\New §6Default Preset §b(BoP)\stats\4d594ff4-….json` | C | 1656388 | `23 小时 0 分钟` |

用 PowerShell 独立复核这几个值,确认换算系数没写错:

```bash
MC="F:/Minecraft/.minecraft"
for f in "$MC/saves/新的世界/stats/4d594ff4-e1a0-4023-886e-c5e6983ed32b.json" \
         "$MC/versions/1.21.8/saves/新的世界/stats/4d594ff4-e1a0-4023-886e-c5e6983ed32b.json" \
         "$MC/versions/1.12.2/saves/新的世界/stats/98ba4b36-6db8-975f-3c1f-6254fde7698a.json" ; do
  grep -oE '"(minecraft:play_time|stat\.playOneMinute)":[0-9]+' "$f" | head -1
done
```

- [ ] **Step 5: 提交**

```bash
git add "Plain Craft Launcher 2/Modules/Minecraft/ModPlayTime.vb" "Plain Craft Launcher 2/Plain Craft Launcher 2.vbproj"
git commit -m "feat(playtime): 新增统计文件解析、时长格式化与账号 UUID 解析"
```

---

## Task 2: 按存档/按版本汇总 + mtime 缓存

**Files:**
- Modify: `Plain Craft Launcher 2\Modules\Minecraft\ModPlayTime.vb`

**Interfaces:**
- Consumes: Task 1 的 `PlayTimeFromStatsFile` / `PlayTimeCurrentAccount` / `PlayTimeReadTicks`
- Produces:
  - `Public Function PlayTimeGetSave(SavePath As String, Optional UseCache As Boolean = True) As Long` —— 单个存档中属于当前账号的 tick 数,`SavePath` 以 `\` 结尾;没有数据返回 0,**读取/判定失败返回 -1**
  - `Public Function PlayTimeGetVersion(Instance As McInstance, Optional UseCache As Boolean = True) As Long` —— 该版本全部存档的合计;任意一个存档出错则整体返回 -1
  - `Public Sub PlayTimeClearCache()` / `Public Sub PlayTimeCacheSave()`

- [ ] **Step 1: 追加缓存与汇总逻辑**

在 `ModPlayTime.vb` 的账号 UUID 段 `#End Region` 之后插入:

```vb
#Region "缓存"

    '缓存文件里存每个存档「上次统计时 stats 目录的最新修改时间」,
    '下次统计只要这个时间没变就跳过重读,避免每次都把所有存档的 JSON 解析一遍。
    Private ReadOnly Property PlayTimeCachePath As String
        Get
            Return PathTemp & "Cache\PlayTime.json"
        End Get
    End Property

    Private _PlayTimeCache As JObject = Nothing

    Private Function PlayTimeCacheGet() As JObject
        If _PlayTimeCache Is Nothing Then
            Try
                _PlayTimeCache = FileUtils.ReadAsJson(Of JObject)(PlayTimeCachePath)
            Catch ex As Exception
                Logger.Warn(ex, "读取游戏时长缓存失败,已重置")
            End Try
            If _PlayTimeCache Is Nothing Then _PlayTimeCache = New JObject
            If _PlayTimeCache.Item("Saves") Is Nothing Then _PlayTimeCache("Saves") = New JObject
        End If
        Return _PlayTimeCache
    End Function

    Public Sub PlayTimeCacheSave()
        If _PlayTimeCache Is Nothing Then Return
        Try
            DirectoryUtils.Create(IO.Path.GetDirectoryName(PlayTimeCachePath))
            FileUtils.Write(PlayTimeCachePath, _PlayTimeCache.ToString(Formatting.None))
        Catch ex As Exception
            Logger.Warn(ex, "写入游戏时长缓存失败")
        End Try
    End Sub

    ''' <summary>清空内存与磁盘缓存。手动刷新时调用。</summary>
    Public Sub PlayTimeClearCache()
        _PlayTimeCache = New JObject
        _PlayTimeCache("Saves") = New JObject
        PlayTimeCacheSave()
    End Sub

    ''' <summary>取一个存档 stats 目录下所有 JSON 的最新修改时间(UTC tick)。没有文件返回 0。</summary>
    Private Function PlayTimeStatsMtime(StatsFolder As String) As Long
        Dim Latest As Long = 0
        For Each File In DirectoryUtils.EnumerateFiles(StatsFolder, False, "*.json")
            Dim T = FileUtils.GetInfo(File).LastWriteTimeUtc.Ticks
            If T > Latest Then Latest = T
        Next
        Return Latest
    End Function

#End Region

#Region "扫描存档与版本"

    ''' <summary>
    ''' 单个存档中属于当前账号的游玩时长(tick)。SavePath 必须以 \ 结尾。
    ''' 优先按账号匹配统计文件;一个都匹配不上时,若该存档只有一个统计文件
    ''' (单机存档的常见情况)则直接采用,多个文件时无法判定归属,返回 -1(出错)。
    ''' 被采用的任意一个文件读取失败,也返回 -1。
    ''' </summary>
    Public Function PlayTimeGetSave(SavePath As String, Optional UseCache As Boolean = True) As Long
        Dim StatsFolder As String = SavePath & "stats\"
        If Not DirectoryUtils.Exists(StatsFolder) Then Return 0
        Dim Files = DirectoryUtils.EnumerateFiles(StatsFolder, False, "*.json").ToList
        If Files.Count = 0 Then Return 0

        Dim Mtime = PlayTimeStatsMtime(StatsFolder)
        If UseCache Then
            Dim Saves = CType(PlayTimeCacheGet().Item("Saves"), JObject)
            Dim Cached = TryCast(Saves.Item(SavePath), JObject)
            '两个字段都必须在才算缓存有效。Ticks 缺失时不能当成 -1(那是「出错」的意思),
            '否则一条残缺的缓存会被永久当成出错。
            If Cached IsNot Nothing AndAlso Cached.Item("Ticks") IsNot Nothing AndAlso
               PlayTimeReadTicks(Cached.Item("Mtime")) = Mtime Then
                Return PlayTimeReadTicks(Cached.Item("Ticks"))
            End If
        End If

        Dim Fresh = PlayTimeSumFiles(Files)
        If UseCache Then
            Dim Entry As New JObject
            Entry("Mtime") = Mtime
            Entry("Ticks") = Fresh
            CType(PlayTimeCacheGet().Item("Saves"), JObject)(SavePath) = Entry
            '这里刻意不落盘 —— 一次全量扫描会调用本函数几十次,每个存档写一次文件太重。
            '落盘交给调用方:PlayTimeLoad / PlayTimeUpdateCurrent 在收尾时统一 PlayTimeCacheSave()。
        End If
        Return Fresh
    End Function

    ''' <summary>
    ''' 从一组统计文件里挑出属于当前账号的并求和。
    ''' 匹不上归属时:只有一个文件就直接采用,多个文件无法判定,返回 -1。
    ''' 被采用的文件里任意一个读取失败,整个存档也返回 -1。
    ''' </summary>
    Private Function PlayTimeSumFiles(Files As List(Of String)) As Long
        Dim Account = PlayTimeCurrentAccount()
        Dim Matched = Files.Where(Function(F) Account.Matches(IO.Path.GetFileNameWithoutExtension(F))).ToList
        If Matched.Count = 0 Then
            If Files.Count <> 1 Then Return -1
            Matched = Files
        End If
        Dim Total As Long = 0
        For Each File In Matched
            Dim Ticks = PlayTimeFromStatsFile(File)
            If Ticks < 0 Then Return -1
            Total += Ticks
        Next
        Return Total
    End Function

    ''' <summary>
    ''' 某个版本下全部存档的游玩时长合计(tick)。
    ''' 任意一个存档出错就整体返回 -1,让界面显示「出错」,而不是给出一个偏低但看起来正常的数。
    ''' </summary>
    Public Function PlayTimeGetVersion(Instance As McInstance, Optional UseCache As Boolean = True) As Long
        Dim SavesFolder As String = Instance.PathIndie & "saves\"
        If Not DirectoryUtils.Exists(SavesFolder) Then Return 0
        Dim Total As Long = 0
        For Each Save As IO.DirectoryInfo In DirectoryUtils.GetInfo(SavesFolder).EnumerateDirectories
            Dim Ticks = PlayTimeGetSave(Save.FullName & "\", UseCache)
            If Ticks < 0 Then Return -1
            Total += Ticks
        Next
        Return Total
    End Function

#End Region
```

- [ ] **Step 2: 构建**

Run:
```bash
build.bat Release
```

Expected: `BUILD OK`。若报 `FileUtils.Write` 参数不匹配,查 `MeloongCore\Shared\Utils\FileUtils.cs` 里 `Write` 的重载(文本版应是 `Write(string filePath, string content)`)。

- [ ] **Step 3: 用真实数据核对版本级合计与兜底分支**

`versions\1.12.2\saves\新的世界\` 只有一个存档、一个统计文件,而且它用的是**离线 UUID `98ba4b36-…`,不在当前微软账号的 UUID 集合里**。所以它会走「只有一个文件就直接采用」的兜底分支。

Expected: 该版本合计 = `6203` tick = `5 分钟`。这正好同时验证了「格式 C 解析」和「兜底分支」两条路径。

- [ ] **Step 4: 提交**

```bash
git add "Plain Craft Launcher 2/Modules/Minecraft/ModPlayTime.vb"
git commit -m "feat(playtime): 按存档与版本汇总游玩时长,加入 mtime 缓存"
```

---

## Task 3: 异步加载器与三个刷新触发

**Files:**
- Modify: `Plain Craft Launcher 2\Modules\Minecraft\ModPlayTime.vb`
- Modify: `Plain Craft Launcher 2\Modules\Minecraft\ModLaunch.vb`
- Modify: `Plain Craft Launcher 2\FormMain.xaml.vb`

**Interfaces:**
- Consumes: Task 2 的 `PlayTimeGetVersion` / `PlayTimeCacheSave`
- Produces:
  - `Public ReadOnly PlayTimeVersionCache As ConcurrentDictionary(Of String, Long)` —— 版本路径 → tick 数,UI 直接读这个
  - `Public PlayTimeLoader As LoaderTask(Of Integer, Integer)`
  - `Public Sub PlayTimeUpdateCurrent(Instance As McInstance)`
  - `Public Sub PlayTimeRefreshAll()`
  - `Public Sub PlayTimeMarkLaunch()` / `Public Sub PlayTimeMarkLaunchFinished()` / `Public Sub PlayTimeCatchUp()`

- [ ] **Step 1: 追加加载器与触发入口**

在 `ModPlayTime.vb` 末尾(`#End Region` 与 `End Module` 之间)插入:

```vb
#Region "异步加载与触发"

    ''' <summary>版本文件夹路径 → 该版本的游玩总时长(tick)。由 PlayTimeLoader 在后台填充,UI 只读。</summary>
    Public ReadOnly PlayTimeVersionCache As New ConcurrentDictionary(Of String, Long)

    Public PlayTimeLoader As New LoaderTask(Of Integer, Integer)("游戏时长统计", AddressOf PlayTimeLoad)

    Private Sub PlayTimeLoad(Loader As LoaderTask(Of Integer, Integer))
        Dim Instances = McInstanceList.Values.SelectMany(Function(List) List).ToList
        Dim Done As Integer = 0
        For Each Instance In Instances
            If Loader.IsCanceled Then Return
            PlayTimeVersionCache(Instance.PathVersion) = PlayTimeGetVersion(Instance)
            Done += 1
            Loader.Progress = Done / Math.Max(1.0, Instances.Count)
        Next
        PlayTimeCacheSave()
        Logger.Info($"游戏时长统计完成,共 {Instances.Count} 个版本")
    End Sub

    ''' <summary>只更新一个版本。PlayTimeGetVersion 内部靠 mtime 跳过没变过的存档。</summary>
    Public Sub PlayTimeUpdateCurrent(Instance As McInstance)
        If Instance Is Nothing Then Return
        Try
            PlayTimeVersionCache(Instance.PathVersion) = PlayTimeGetVersion(Instance)
            PlayTimeCacheSave()
        Catch ex As Exception
            Logger.Warn(ex, $"更新版本游戏时长失败:{Instance.Name}")
        End Try
    End Sub

    ''' <summary>游戏启动前记录一次快照,退出时据此知道要更新哪个版本。</summary>
    Public Sub PlayTimeMarkLaunch()
        Try
            Dim Cache = PlayTimeCacheGet()
            Cache("LastLaunchVersion") = If(McInstanceSelected Is Nothing, "", McInstanceSelected.PathVersion)
            Cache("LastLaunchPending") = True
            PlayTimeCacheSave()
        Catch ex As Exception
            Logger.Warn(ex, "记录游戏时长启动快照失败")
        End Try
    End Sub

    ''' <summary>游戏进程退出后调用,清掉待更新标记。</summary>
    Public Sub PlayTimeMarkLaunchFinished()
        Try
            PlayTimeCacheGet()("LastLaunchPending") = False
            PlayTimeCacheSave()
        Catch ex As Exception
            Logger.Warn(ex, "清除游戏时长启动快照失败")
        End Try
    End Sub

    ''' <summary>
    ''' PCL 启动时调用。若上一次游戏启动后 PCL 没等到进程退出就关了,
    ''' 这里补做一次那个版本的更新。
    ''' </summary>
    Public Sub PlayTimeCatchUp()
        Try
            Dim Cache = PlayTimeCacheGet()
            Dim Pending = Cache.Item("LastLaunchPending")
            If Pending Is Nothing OrElse Pending.Type <> JTokenType.Boolean OrElse Not Pending.ToObject(Of Boolean)() Then Return
            Dim Path As String = If(Cache.Item("LastLaunchVersion") Is Nothing, "", Cache.Item("LastLaunchVersion").ToString)
            Cache("LastLaunchPending") = False
            PlayTimeCacheSave()
            If Path = "" Then Return
            Dim Instance = McInstanceList.Values.SelectMany(Function(List) List).FirstOrDefault(Function(I) I.PathVersion = Path)
            If Instance IsNot Nothing Then
                Logger.Info($"检测到上次游戏启动未收尾,补更新游戏时长:{Instance.Name}")
                PlayTimeUpdateCurrent(Instance)
            End If
        Catch ex As Exception
            Logger.Warn(ex, "补更新游戏时长失败")
        End Try
    End Sub

    ''' <summary>手动刷新:清缓存后重扫全部版本。</summary>
    Public Sub PlayTimeRefreshAll()
        PlayTimeClearCache()
        PlayTimeLoader.Start(0, IsForceRestart:=True)
    End Sub

#End Region
```

- [ ] **Step 2: 在启动流程挂钩**

在 `Plain Craft Launcher 2\Modules\Minecraft\ModLaunch.vb` 的 `McLaunchRun`(`Private Sub McLaunchRun`,**第 2120 行**)里,在第 2152 行 `McLaunchProcess = GameProcess` 的**紧接下一行**插入:

```vb
        '记录启动快照,便于退出时定位要更新哪个版本
        PlayTimeMarkLaunch()
```

- [ ] **Step 3: 在退出流程挂钩**

在 `ModLaunch.vb` 的 `McLaunchEnd()` 开头(`McLaunchLog("开始启动结束处理")` 之后)插入:

```vb
        '游戏已退出,更新刚玩过的那个版本,并清掉待更新标记
        PlayTimeUpdateCurrent(McInstanceSelected)
        PlayTimeMarkLaunchFinished()
```

- [ ] **Step 4: 在 PCL 启动时补齐**

在 `FormMain.xaml.vb` 里找到已有的 `LoaderFolderRun(McInstanceListLoader, …)` 调用附近,插入:

```vb
        '若上次游戏启动后 PCL 没等到进程退出,这里补一次统计
        RunInNewThread(Sub()
                           LoaderFolderRun(McInstanceListLoader, McFolderSelected, LoaderFolderRunType.RunOnUpdated, MaxDepth:=1, ExtraPath:="versions\", WaitForExit:=True)
                           PlayTimeCatchUp()
                       End Sub, "PlayTime CatchUp")
```

- [ ] **Step 5: 构建**

Run:
```bash
build.bat Release
```

Expected: `BUILD OK`。这一步最容易出的错是 `McInstanceSelected` 在 `McLaunchRun` 里尚未赋值 —— 若报空引用,把挂钩点后移到 `McLaunchWait` 开头。

- [ ] **Step 6: 实测触发链路**

从 `dist\Release\Plain Craft Launcher 2.exe` 启动 PCL,启动一次 Minecraft,进任意存档玩十几秒后退出游戏。然后:

```bash
grep -o '"LastLaunchPending":[a-z]*' "$TEMP/PCL/Cache/PlayTime.json"
```

Expected: 变成 `"LastLaunchPending":false`(说明 `McLaunchEnd` 里的收尾跑到了),且 `Saves` 里出现刚玩过那个存档的条目。

- [ ] **Step 7: 提交**

```bash
git add "Plain Craft Launcher 2/Modules/Minecraft/ModPlayTime.vb" "Plain Craft Launcher 2/Modules/Minecraft/ModLaunch.vb" "Plain Craft Launcher 2/FormMain.xaml.vb"
git commit -m "feat(playtime): 加入异步统计加载器与启动/退出/启动补齐三个触发点"
```

---

## Task 4: 列表项右侧时长显示(悬停时隐藏)

**Files:**
- Modify: `Plain Craft Launcher 2\Controls\MyListItem.xaml:9-15`(列定义)
- Modify: `Plain Craft Launcher 2\Controls\MyListItem.xaml.vb`(新增属性 + 动画)

**Interfaces:**
- Consumes: 无
- Produces: `Public Property PlayTime As String` —— 设了就在条目右侧显示;设为空串则隐藏,且不占布局空间

- [ ] **Step 1: 新增一个专用的 Auto 列**

把 `MyListItem.xaml` 第 9-15 行的列定义改成(**在第 3 列之后插入 `ColumnPlayTime`**):

```xml
    <Grid.ColumnDefinitions>
        <ColumnDefinition x:Name="ColumnCheck" Width="2"/>
        <ColumnDefinition x:Name="ColumnPaddingLeft" Width="0" />
        <ColumnDefinition x:Name="ColumnLogo" Width="4" />
        <ColumnDefinition Width="1*"/>
        <ColumnDefinition x:Name="ColumnPlayTime" Width="Auto" />
        <ColumnDefinition x:Name="ColumnPaddingRight" Width="4" />
    </Grid.ColumnDefinitions>
```

**为什么必须新开一列,而不是像按钮那样叠加:** 按钮是「悬停才出现」,所以它们直接叠在标题上没关系。时长是**一直显示**的,如果也叠加,标题文字会从时长底下穿过去。开一个 `Auto` 列后,标题所在的 `1*` 列会先收缩、先省略号截断,两者永不重叠。`Auto` 列在没有内容时宽度为 0,所以不显示时长的地方布局完全不变。

**这个改动是安全的:** 该文件所有列都只用 `x:Name` 引用(`ColumnPaddingRight.Width` 等),没有任何地方按数字索引取列,所以插入新列不会破坏现有代码。

- [ ] **Step 2: 新增 PlayTime 属性**

在 `MyListItem.xaml.vb` 的 `LabInfo` 属性块之后(`#End Region` 之前,第 78 行 `End Property` 后面)插入:

```vb
    '右侧游戏时长
    Private _LabPlayTime As TextBlock = Nothing
    Public ReadOnly Property LabPlayTime As TextBlock
        Get
            If _LabPlayTime Is Nothing Then
                Dim Lab As New TextBlock With {
                    .Name = "LabPlayTime",
                    .SnapsToDevicePixels = False,
                    .UseLayoutRounding = False,
                    .HorizontalAlignment = HorizontalAlignment.Right,
                    .VerticalAlignment = VerticalAlignment.Center,
                    .IsHitTestVisible = False,
                    .Visibility = Visibility.Collapsed,
                    .FontSize = 12,
                    .Opacity = 0.6,
                    .Margin = New Thickness(4, 0, 5, 0)
                }
                SetColumn(Lab, 4)
                SetRowSpan(Lab, 4)
                Children.Add(Lab)
                _LabPlayTime = Lab
            End If
            Return _LabPlayTime
        End Get
    End Property

    '游戏时长文本
    Private _PlayTime As String = ""
    Public Property PlayTime As String
        Get
            Return _PlayTime
        End Get
        Set(value As String)
            value = If(value, "")
            If _PlayTime = value Then Return
            _PlayTime = value
            LabPlayTime.Text = value
            LabPlayTime.Visibility = If(value = "", Visibility.Collapsed, Visibility.Visible)
        End Set
    End Property
```

- [ ] **Step 3: 悬停时淡出时长**

在 `RefreshColor` 的**有动画、鼠标移入**分支里,把新语句插到 `If ButtonStack IsNot Nothing` **块内部**(第 577 行 `End If` 之前):

```vb
                    If LabPlayTime IsNot Nothing Then Ani.Add(AaOpacity(LabPlayTime, -LabPlayTime.Opacity, Time * 0.4))
```

**必须放在 `If ButtonStack IsNot Nothing` 里面**,不能放外面。`MyListItem` 是全项目共用的控件,很多地方用它但不给按钮(比如启动页的时长卡片)。只有「确实有右侧按钮要腾位置」时才该把时长藏起来,否则那些没有按钮的列表一悬停文字就没了。

- [ ] **Step 4: 移出时淡入**

在**有动画、鼠标移出**分支里,同样插到 `If ButtonStack IsNot Nothing` **块内部**(第 595 行 `End If` 之前):

```vb
                    If LabPlayTime IsNot Nothing Then Ani.Add(AaOpacity(LabPlayTime, 1 - LabPlayTime.Opacity, Time * 0.7, Time * 0.3))
```

- [ ] **Step 5: 补无动画分支**

第 610-613 行的 `If ButtonStack IsNot Nothing` 块里,在 `ColumnPaddingRight.Width = …` 之后加一行:

```vb
                    If LabPlayTime IsNot Nothing Then LabPlayTime.Opacity = 0
```

第 620-623 行的 `If ButtonStack IsNot Nothing` 块里,在 `ColumnPaddingRight.Width = …` 之后加一行:

```vb
                    If LabPlayTime IsNot Nothing Then LabPlayTime.Opacity = 1
```

- [ ] **Step 6: 构建**

Run:
```bash
build.bat Release
```

Expected: `BUILD OK`。这一步同时改了 `.xaml` 和它的 code-behind,若列定义写错会报 XAML 编译错误。

- [ ] **Step 7: 提交**

```bash
git add "Plain Craft Launcher 2/Controls/MyListItem.xaml" "Plain Craft Launcher 2/Controls/MyListItem.xaml.vb"
git commit -m "feat(playtime): MyListItem 新增右侧时长列,悬停时随按钮淡出"
```

---

## Task 5: 在 ToListItem 里填入时长(同时覆盖列表与概览)

**Files:**
- Modify: `Plain Craft Launcher 2\Modules\Minecraft\ModMinecraft.vb:902-928`

**Interfaces:**
- Consumes: Task 1 的 `PlayTimeFormat`、Task 3 的 `PlayTimeVersionCache`、Task 4 的 `MyListItem.PlayTime`
- Produces: 无(纯消费)

**为什么只改这一处就够:** `McInstance.ToListItem()` 全项目只有两个调用点 —— `Controls\MyCard.vb:140`(版本选择列表)和 `PageInstanceOverall.xaml.vb:32`(版本设置概览顶部框)。这两个正是需要显示时长的位置,所以在这一个函数里填值即可,不必分别去改两个页面。

- [ ] **Step 1: 在 initializer 里填值**

`ModMinecraft.vb` 第 904-908 行是 `MyVirtualizingElement` 的 initializer(滚动进入可视区时才执行),把 PlayTime 的填充加在这里:

```vb
            Function()
                Dim NewItem As New MyListItem With {
                    .Info = Info, .Height = 42, .Tag = Me, .SnapsToDevicePixels = True, .Type = MyListItem.CheckType.Clickable,
                    .ContentHandler = ContentHandler
                }
                '游戏时长。只读后台统计好的内存缓存,这里不做磁盘 I/O。
                Dim Ticks As Long = 0
                If PlayTimeVersionCache.TryGetValue(PathVersion, Ticks) Then NewItem.PlayTime = PlayTimeFormat(Ticks)
                '标题
                NewItem.Inlines.Clear()
```

注意 `PathVersion` 是 `Me.PathVersion`(lambda 捕获实例),不要写成别的。

- [ ] **Step 2: 构建**

Run:
```bash
build.bat Release
```

Expected: `BUILD OK`。

- [ ] **Step 3: 让版本选择页触发统计**

**先**把 `PageSelectRight.xaml.vb` 第 25 行的列表重建方法签名改成可选参数(已确认方法体内从未引用 `Loader`,所以传 `Nothing` 是安全的,而带 `Optional` 的参数仍然能匹配 `PageLoaderInit` 里的 `AddressOf McInstanceListUI` 委托):

```vb
    Private Sub McInstanceListUI(Optional Loader As LoaderTask(Of String, Integer) = Nothing)
```

**再**在 `PageSelectRight_Loaded`(第 4-7 行)里 `LoaderFolderRun(...)` 之后追加:

```vb
        '后台统计各版本游戏时长;统计完成后重建列表以显示时长
        PlayTimeLoader.Start(0)
        RunInNewThread(Sub()
                           PlayTimeLoader.WaitForExit(0)
                           RunInUi(Sub() McInstanceListUI())
                       End Sub, "PlayTime List Refresh")
```

`McInstanceListUI` 会 `PanMain.Children.Clear()` 后按 `McInstanceList` 重新构建所有条目,此时 `PlayTimeVersionCache` 已经填好,重建出来的条目就带上了时长。

- [ ] **Step 4: 构建并目视验证**

Run:
```bash
build.bat Release
```

Expected: `BUILD OK`。

从 `dist\Release\Plain Craft Launcher 2.exe` 启动,进入「版本选择」页,滚动列表。Expected:

- 每个版本条目**右侧**显示时长文本,如 `5 分钟`、`23 小时 0 分钟`
- 鼠标移到条目上时,时长文本淡出,右侧出现收藏/删除/设置按钮;移开后淡入
- 长版本名的条目,标题会在时长之前就省略号截断,**两者不重叠**
- `1.12.2` 显示 `5 分钟`(走的是「只有一个统计文件」兜底分支)
- `Dream` 显示**个位数小时**,而不是上千小时 —— 这验证「只算当前账号」的口径生效

- [ ] **Step 5: 提交**

```bash
git add "Plain Craft Launcher 2/Modules/Minecraft/ModMinecraft.vb" "Plain Craft Launcher 2/Pages/PageSelectRight.xaml.vb"
git commit -m "feat(playtime): 版本条目与概览头部显示游戏时长"
```

---

## Task 6: 启动页新增时长卡片与手动刷新按钮

**Files:**
- Modify: `Plain Craft Launcher 2\Pages\PageLaunch\PageLaunchRight.xaml`
- Modify: `Plain Craft Launcher 2\Pages\PageLaunch\PageLaunchRight.xaml.vb`

**Interfaces:**
- Consumes: `PlayTimeLoader` / `PlayTimeVersionCache` / `PlayTimeRefreshAll`(Task 3)、`PlayTimeFormat`(Task 1)、`MyCard`、`MyExtraTextButton`

- [ ] **Step 1: 新增卡片**

在 `PageLaunchRight.xaml` 的 `<StackPanel Name="PanMain" …>` 里,`PanHint` 之后、`PanCustom` 之前插入:

```xml
        <local:MyCard Margin="0,0,0,15" Title="游戏时长">
            <Grid Margin="20,38,20,18">
                <Grid.RowDefinitions>
                    <RowDefinition Height="Auto" />
                    <RowDefinition Height="Auto" />
                </Grid.RowDefinitions>
                <StackPanel x:Name="PanPlayTimeList" Grid.Row="0" />
                <Grid Grid.Row="1" Margin="0,10,0,0">
                    <TextBlock x:Name="LabPlayTimeHint" HorizontalAlignment="Left" VerticalAlignment="Center"
                               FontSize="12" Foreground="{DynamicResource ColorBrushGray3}"
                               Text="正在统计…" />
                    <local:MyExtraTextButton x:Name="BtnPlayTimeRefresh" HorizontalAlignment="Right"
                                             Text="刷新统计" />
                </Grid>
            </Grid>
        </local:MyCard>
```

**关键:** 卡片放在 `PanMain` 里,不要放进 `PanCustom` —— `PanCustom.Children.Clear()` 会在每次主页刷新时执行(`PageLaunchRight.xaml.vb` 第 227 行),放进去会被清掉。

- [ ] **Step 2: 实现填充逻辑**

在 `PageLaunchRight.xaml.vb` 里追加:

```vb
    '游戏时长卡片:列出每个版本的游玩总时长
    Private Sub RefreshPlayTime()
        PanPlayTimeList.Children.Clear()
        Dim Instances = McInstanceList.Values.SelectMany(Function(List) List).ToList
        If Instances.Count = 0 Then
            LabPlayTimeHint.Text = "还没有读取到任何游戏版本。"
            Return
        End If
        'Ticks < 0 是「读取出错」,要一并列出让用户看见;0 表示没有游玩记录,不列
        Dim Rows = Instances.Select(Function(I)
                                        Dim T As Long = 0
                                        PlayTimeVersionCache.TryGetValue(I.PathVersion, T)
                                        Return (Instance:=I, Ticks:=T)
                                    End Function).Where(Function(R) R.Ticks <> 0).OrderByDescending(Function(R) R.Ticks).ToList
        If Rows.Count = 0 Then
            LabPlayTimeHint.Text = "还没有找到游玩记录。"
            Return
        End If
        Dim Total As Long = 0
        Dim Errored As Integer = 0
        For Each Row In Rows
            If Row.Ticks < 0 Then Errored += 1 Else Total += Row.Ticks
            Dim Item As New MyListItem With {
                .Title = Row.Instance.Name,
                .PlayTime = PlayTimeFormat(Row.Ticks),
                .Height = 42,
                .Type = MyListItem.CheckType.Clickable,
                .IsHitTestVisible = False
            }
            PanPlayTimeList.Children.Add(Item)
        Next
        Dim OkCount = Rows.Count - Errored
        LabPlayTimeHint.Text = If(Errored = 0,
                                  $"共 {OkCount} 个版本,合计 {PlayTimeFormat(Total)}",
                                  $"共 {OkCount} 个版本,合计 {PlayTimeFormat(Total)};另有 {Errored} 个版本读取出错")
    End Sub

    Private Sub BtnPlayTimeRefresh_Click(sender As Object, e As EventArgs)
        LabPlayTimeHint.Text = "正在统计…"
        PlayTimeRefreshAll()
    End Sub
```

- [ ] **Step 3: 挂上按钮与加载回调**

在 `PageLaunchRight.xaml.vb` 的 `Private Sub Init() Handles Me.Loaded`(**第 5 行**)方法体末尾追加:

```vb
        AddHandler BtnPlayTimeRefresh.Click, AddressOf BtnPlayTimeRefresh_Click
        PlayTimeLoader.Start(0)
        RunInNewThread(Sub()
                           PlayTimeLoader.WaitForExit(0)
                           RunInUi(AddressOf RefreshPlayTime)
                       End Sub, "PlayTime Ui Fill")
```

- [ ] **Step 4: 构建并目视验证**

Run:
```bash
build.bat Release
```

Expected: `BUILD OK`。

启动 PCL,停在启动页。Expected:

- 启动页右侧出现「游戏时长」卡片,逐条列出有游玩记录的版本,按时长从多到少排列
- 底部提示行显示「共 N 个版本,合计 X 小时 Y 分钟」
- 点「刷新统计」后,提示先变「正在统计…」,统计完成后重新列出
- 没有配置自定义主页时,这张卡片就是右侧唯一的内容

- [ ] **Step 5: 提交**

```bash
git add "Plain Craft Launcher 2/Pages/PageLaunch/PageLaunchRight.xaml" "Plain Craft Launcher 2/Pages/PageLaunch/PageLaunchRight.xaml.vb"
git commit -m "feat(playtime): 启动页新增游戏时长卡片与手动刷新按钮"
```

---

## Task 7: 整体验收

**Files:** 无改动

- [ ] **Step 1: 干净全量重建**

```bash
build.bat Release
```

Expected: `BUILD OK`,`dist\Release\Plain Craft Launcher 2.exe` 时间戳刷新。

- [ ] **Step 2: 三处显示一致性核对**

启动 `dist\Release\Plain Craft Launcher 2.exe`,对同一个版本(建议用 `1.21.8`,它有一个 `22534` tick 的存档)分别看三处:

| 位置 | 期望 |
|---|---|
| 启动页卡片 | 该版本一行,显示 `18 分钟` |
| 版本选择列表右侧 | `18 分钟` |
| 版本设置概览顶部框右侧 | `18 分钟` |

三处必须一致。不一致说明缓存键或统计口径不一致。

- [ ] **Step 3: 悬停行为核对**

在版本选择列表里把鼠标在几个条目之间快速来回移动。Expected: 时长文本随按钮一起平滑淡入淡出,没有残留、没有闪烁、没有文字重叠。

- [ ] **Step 4: 缓存生效核对**

记下 `PlayTime.json` 的修改时间(在 `%TEMP%\PCL\Cache\` 下)。然后只滚动列表、不玩任何游戏,不该有新的磁盘写入。再进游戏玩一小会儿退出,该文件应被更新,且只多出刚玩过那个存档的条目。

- [ ] **Step 5: 收尾提交**

```bash
git add -A
git commit -m "chore(playtime): 完成游戏时长显示功能"
```

---

## 附:已知局限

这几点是当前设计的固有边界,不是 bug。若用户之后要求,再单独开任务处理。

1. **只认当前账号的 UUID。** 历史上用过的其他离线用户名对应的统计文件不会被算进来 —— 那些 UUID 已经无法从 PCL 设置里读到。兜底规则(存档只有一个统计文件时直接采用)能覆盖纯单机存档,但「换过多个离线名 + 该存档有多个文件」的组合会漏。
2. **联机存档只算自己的部分。** 在服务器上玩时统计归服务器所有,本地 `saves\` 下不会产生记录,这类时长统计不到。
3. **`saves\` 下不是存档的目录会被一起扫。** 遍历不筛选目录内容,一个空的子目录会贡献 0,不影响合计。
4. **缓存文件放在 `PathTemp\Cache\`,系统清理临时文件后会丢。** 丢了只是下次全量重扫一遍,结果仍然正确。
5. **`PlayTime.json` 里的 `Mtime` 用的是 UTC tick。** 不要改成 `LastWriteTime`(本地时间),跨时区/夏时制会导致缓存误判。
