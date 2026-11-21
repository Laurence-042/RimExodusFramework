# RimExodus Framework (REF) 1.0.5

RimExodus 系列通用的地图渲染框架以及地图内结构操作工具包。REF 是独立的 RimWorld 前置 Mod，提供公共程序集 `RimExodus.Maps.dll`，不引用 Delta-V 或 RimExodus，也不添加飞船、武器或其他玩法内容。使用 MIT 协议，见 [LICENSE](LICENSE)。

## 背景

RimExodus 与 RimExodus: Delta-V 都需要在当前地图附近显示其他地图，并对地图中的一部分结构进行保存、移走和重建。我们评估了现有方案，但它们的设计边界与需求不同。

Vehicle Map Framework 的地图面向载具设计，并以叠加在 worldtile 地图上的使用方式为前提；Delta-V 需要没有 worldtile 宿主的独立地图，以及任意平移和旋转的显示参考系，因此不适合直接复用其地图模型。

调研 VEF 后，KSCG 的基架处理及其关联机制带来了较高的改造和维护成本。VEF 使用者众多，本项目没有足够精力保证大幅改造之后，仍能维持它与其他内容之间的可靠性。Custom Quest Frame 较重，主要以整张地图为单位提供序列化与反序列化；Real Ruins 的系统对我们的局部结构需求也过重。因此，我们自行实现了地图内结构的序列化与按需重建，并将其与公共地图渲染能力一起抽出为 REF。这些是本项目的选型理由，不是对其他框架适用场景的否定。

## 功能与边界

| 能力 | 1.0.5 状态 |
| --- | --- |
| 多地图绘制 | 原版绘制捕获、局部到显示坐标变换、内容过滤与自定义输出；1.6 使用 CommandBuffer，1.7 使用 Waylith 命令和深度分组 |
| 静态区块绘制 | 按表面层和材质合批静态平面，按脏区重建和释放，已接入 Delta-V 船壳 |
| 原版多地图绘制入口 | `MapRuntime.Rendering.SetProvider` 接入原有逐图 Update/Draw 回调，保留队列并隔离常规 shader 参数。特殊相机和 GPU 管线仍有边界 |
| 分区覆盖层裁切 | 默认按网格大小判断哪些平面覆盖层应裁切。覆盖层由多个小网格绘制时，可用 `OverlayClipSelector` 指定哪些提交需要裁切。 [接口契约](doc/地图模块.md#原版回调绘制契约) |
| 无 worldtile 地图 | 口袋图创建、注册和公共 sourceMap 宿主服务 |
| 区域模板与 prefab | 任意区域选择器，Home Area 是其中一种；逐实例 Scribe、地形、基架、屋顶、原版禁止建屋顶区域、室内温度／真空度和重建回调 |
| 整图归档 | 全图选择的原版持久状态层；保留地图身份，支持卸载和恢复 |
| tick 速率 | 精确 0–100% 执行机会控制、逐图错峰、全速区域回调；0% 区外注销/恢复注册，可选移动补偿，消费方提供停图策略 |
| 跨图战斗 | 原版/CE 桥接、逐图显式启用、目标与弹丸状态失效；仅消费方请求时安装战斗补丁，迁入结果待游戏验收 |
| 跨图格子查询 | `MapRuntime.Spatial` 提供明确参考系、整数平移、真实格归属和有界地址表；与渲染/战斗开关独立，无自动 tick |
| SS 地图背景兼容 | 对任意来源的地图尊重 `MapGeneratorDef.disableMapClippers`：明确禁用时阻止 SS 强制开启遮片，其余地图不改结果；不判断消费方或地图类别 |
| IsoRim 1.7 可见性兼容 | 原版回调窗口保留源图局部视区，私有包围盒/模型剔除使用显示坐标；适用于任意消费方的绘制计划；用户已在 RE 中确认修复有效。矩形土层侧面与首次显示重建尚未适配 |

区域模板、封存与 prefab 共用内容模型；整图归档是范围覆盖全图并包含地图级状态的特例。区域清空/替换事务、资源池和 Gravship 搬运尚未全部迁入。不要将 1.0.5 解释为所有规划能力均已完成。API、生命周期契约和剩余工作见 [地图模块](doc/地图模块.md)，最初调研见 [迁移备忘录](doc/迁移备忘录.md)。

Delta-V 已切换为唯一 REF 后端，保留船壳选择、宇宙运动、飞船身份、标记展开与战斗策略。RimExodus 已接入可选 REF 后端，默认保留 legacy，开关重启生效；REF 模式的生成、渲染、降频、区域恢复及原版/CE 战斗接线仍需游戏内验收。

## 安装与依赖

跨图坐标与第三方算法的接入方法见 [跨图空间查询](doc/跨图空间查询.md)。显示投影、模拟格子归属和算法结果发布分别有明确契约；这套接口不自动把未知 Mod 的视野、寻路等逻辑跨图化。

需要 RimWorld 1.6 或 1.7 和 Harmony。将本目录作为独立 Mod 安装并启用，加载顺序为 Harmony → RimExodus Framework → Delta-V / RimExodus。REF 的 DLL 只在 REF 的版本化 `Assemblies` 目录加载；消费方不要再把它打包进自己的 `Assemblies`。

开发目录的名称为 `RimExodus Framework (Dev)`、packageId 为 `RimExodus.Framework.dev`；RimExodus 与 Delta-V 的开发目录依赖该 ID。正式发布包名称不带 `(Dev)`，packageId 为 `RimExodus.Framework`，供两者正式发布包依赖。

## 构建

需要 `just` 和对应 .NET SDK：1.7 使用 SDK 10；1.6 使用可构建 net48 的 SDK。Windows 构建入口使用系统自带的 `powershell.exe`（Windows PowerShell 5.1），无需另装 PowerShell 7。可用 `DOTNET_EXE` 指定 dotnet 可执行文件。本机默认游戏目录为 `D:\SteamLibrary\steamapps\common\RimWorld_1.6`（1.6）与 `D:\SteamLibrary\steamapps\common\RimWorld`（1.7）；[引用配置](build/RimWorldReferences.props) 可用 `RimWorld16Dir`、`RimWorld17Dir` 或通用 `RimWorldDir` 及 `HarmonyPath` 属性/环境变量覆盖。1.6 使用编译期 NuGet 引用，不分发游戏/Harmony 程序集。

```powershell
just build       # 1.7 Release
just build 1.6   # 1.6 Release
just release     # 同时构建并打包 1.6 与 1.7 正式版
just test        # 1.7 离线回归
just test-services # SDK 8+，版本无关的调度/生成协调检查
```

输出为 `<游戏版本>/Assemblies/RimExodus.Maps.dll`，不提交构建产物。独立克隆可以直接构建；不要求 Delta-V 或 RimExodus 源码。若同级存在 RimExodus，测试会只读链接其 transpiler，额外检查补丁组合顺序。

`just release` 分别尝试构建 1.6 与 1.7；缺少某版构建环境时只复用未过时的已有 DLL，若 DLL 缺失或早于源码/构建脚本则警告并中止打包。成功后将两版 DLL、正式 `About.xml`、`PublishedFileId.txt`、`LoadFolders.xml` 和许可证一起打包到仓库外的 `../RimExodusFramework.release`；正式元数据使用 `RimExodus.Framework`，同时声明 1.6 与 1.7。缺少创意工坊 ID 文件时中止打包；再次执行会替换该发布目录，并检查两套游戏 `Mods` 目录下的 `RimExodusFramework.release` 软链接，缺失时补建；若游戏目录不存在则警告。发布包不含源码、文档、PDB 或游戏/Harmony 程序集。若设置了 `DOTNET_EXE`，发布构建使用指定 SDK；本机还可复用同级 Delta-V 的本地 .NET 10 SDK。两套游戏的 `Mods` 目录分别软链接开发目录与正式目录，按所需组合启用。

Delta-V 与 RimExodus 的 `just build` 会先调用同级 REF 的 `just build`，成功后复制 DLL 到各消费方的 `References/REF/<版本>/` 作为编译输入，再构建消费方。运行时仍由独立 REF Mod 提供程序集。

## 验证

- `just test` 使用 1.7 游戏程序集离线验证公共与战斗模块的绑定、绘制提交与裁切、地图生命周期及序列化。部分场景使用夹具，测试不会启动游戏或 GPU。
- `just test-services` 验证版本无关的调度和生成协调。消费方可用自己的回归验证框架与其补丁的组合。
- 游戏内验收使用配套构建的 DLL，检查实例化与逐项绑定日志。区域存读档、地图卸载与恢复，以及实际画面均需要使用对应 Mod 组合实测。

## 目录

```text
About/                  Mod 元数据
1.6/Assemblies/          1.6 构建输出（忽略）
1.7/Assemblies/          1.7 构建输出（忽略）
Source/                 公共 API、服务与版本后端
build/                  引用配置和构建入口
scripts/MapCoreRegression/ 离线回归
doc/                    契约、迁移记录与待完成事项
```

创意工坊介绍文案（中英分离的 BBCode）见 [中文](doc/SteamWorkshop/SteamWorkshop介绍-中文.md) / [English](doc/SteamWorkshop/SteamWorkshop介绍-English.md)。
