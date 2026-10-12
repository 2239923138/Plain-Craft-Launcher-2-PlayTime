# Plain Craft Launcher 2 PlayTime

[![哔哩哔哩](https://img.shields.io/badge/动态-BiliBili-00A4DB?style=flat&labelColor=444444&logoSize=auto)](https://space.bilibili.com/11343203/dynamic)
[![爱发电](https://img.shields.io/badge/赞助-爱发电-946ce6?style=flat&labelColor=444444&logoSize=auto)](https://meloong.com/afd/a/LTCat)

> **这是第三方基于 PCL 独立进行的二次创作，不是官方版本，与龙腾猫跃及 PCL 官方无关。**
> 原项目：[Meloong-Git/PCL](https://github.com/Meloong-Git/PCL)

本仓库基于 PCL 的开源代码，增加了一个功能：**显示游戏时长**。

<br/>

## 增加了什么

从 Minecraft 存档的统计文件里读取游玩时长，在三个位置显示每个版本的游戏总时长：

| 位置 | 说明 |
|---|---|
| **启动页卡片** | 顶部一行大字显示所有版本加起来的总时长，下面按版本列出明细，底部显示版本数 |
| **版本选择列表** | 每个版本条目右侧显示该版本的时长；鼠标移上去时淡出，让位给收藏/删除/设置按钮 |
| **版本设置 → 概览** | 顶部显示版本名和类型的那一栏，右侧显示该版本的时长 |

### 数据来源

读取 `saves/<存档名>/stats/<玩家UUID>.json`。游玩时长记录在以下三个键之一：

- `minecraft:play_time`（1.17 及以上）
- `minecraft:play_one_minute`（1.13 ~ 1.16）
- `stat.playOneMinute`（1.12 及更早的扁平格式）

**这三个键的值单位都是 tick（20 tick = 1 秒）**，名字里的 "minute" 是 Minecraft 的历史误称，不是分钟。

### 只统计你自己的时长

一个存档里可能同时存在多个玩家的统计文件（联机过、开过局域网、或换过离线用户名）。本功能**只累加属于当前账号的文件**，不会把别人的时长算进来——实测中一个整合包自带的存档里有 71 个玩家的统计文件，全部相加会得到真实值的 370 多倍。

匹配依据是 PCL 当前账号的 UUID：在线账号用完整 UUID，离线账号用 PCL 自己那套算法推导出的 UUID 的**前 27 位**（后 5 位会随离线皮肤设置变化）。

### 手动填写玩家 UUID

**开源版本没有 OAuth Client ID，无法使用微软登录**，所以自动拿不到正版 UUID，某些存档会显示「出错」。

遇到这种情况，可以在 **设置 → 启动设置 → 游戏时长统计** 里手动填入你的玩家 UUID。找法：打开该版本的 `saves` 文件夹 → 任意存档 → `stats` 文件夹，里面文件名去掉 `.json` 后缀的那一段就是。

填完回到启动页点一次「刷新统计」重新统计。

### 更新时机

- **手动**：启动页卡片上的「刷新统计」按钮，重扫全部版本
- **自动**：游戏进程退出时，只更新刚玩过的那个版本
- **补齐**：如果 PCL 在游戏退出前就被关掉了，下次启动时会补做一次

统计结果按存档的修改时间做缓存，没变过的存档不会重复读取。

<br/>

## 与原版的差别

本仓库是 PCL 的**开源分支**，官方在开源代码中剥离了部分功能，因此本版本：

- 无法进行微软正版登录
- 无法使用 CurseForge API
- 没有自动更新与联网通知
- 没有主题切换、赞助解锁等需要在线校验的功能
- 注册表位置与正式版隔离（`PCLDebug`），不会与已安装的正式版 PCL 冲突

**要使用完整功能的 PCL，请前往 [官方下载](https://meloong.com/pcl)。**

<br/>

## 构建

主项目是**旧式的非 SDK 风格 VB.NET WPF 工程**，只能用 Visual Studio 的 MSBuild 构建，`dotnet build` 不可用。

需要：

- Visual Studio（勾选「.NET 桌面开发」工作负载）
- .NET SDK（用于还原 `PCLCS` 的 NuGet 包）

构建：

```bash
build.bat
```

```bash
build.bat Debug
```

不传参数默认 Release。脚本会依次还原 NuGet、用 MSBuild 构建、把产物拷到 `dist\<配置>\PCL2_ACE.exe`。

产物文件名是 `PCL2_ACE.exe`，但**程序集名仍是 `Plain Craft Launcher 2`**——WPF 的 pack URI 与 XAML 命名空间解析的是程序集元数据名而非磁盘文件名，所以源码里那些 `/Plain Craft Launcher 2;component/...` 形式的引用无需改动。

运行时会在 exe 同级目录创建 `PCL\` 和 `.minecraft\`，所以**从 `dist\` 里运行，不要从 `bin\` 或 `obj\` 里运行**。

<br/>

## 许可与署名

本项目基于 [Meloong-Git/PCL](https://github.com/Meloong-Git/PCL) 修改，遵循其《PCL 分发有限许可》。

- **原作者**：龙腾猫跃（[BiliBili](https://space.bilibili.com/11343203/dynamic) / [爱发电](https://meloong.com/afd/a/LTCat)）
- 本仓库为第三方修改版，与原项目及其作者无关
- 源码公开，继续沿用 [LICENCE](LICENCE) 作为使用指南

<br/>

### 相关资源

- [PCL 下载](https://meloong.com/pcl)：下载可免费使用的正式版 PCL
- [PCL 功能投票](https://github.com/Meloong-Git/PCL/discussions/2)：开发者会优先处理票数较高的项目
- [帮助文档库](https://github.com/LTCatt/PCL2Help)：PCL 内置帮助文档的存储库
