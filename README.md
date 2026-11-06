# RimExodus Framework (REF) 1.0.3

RimExodus 系列通用的地图渲染框架以及地图内结构操作工具包。REF 是独立的 RimWorld 前置 Mod，提供公共程序集 `RimExodus.Maps.dll`，不引用 Delta-V 或 RimExodus，也不添加飞船、武器或其他玩法内容。使用 MIT 协议，见 [LICENSE](LICENSE)。

## 背景

RimExodus 与 RimExodus: Delta-V 都需要在当前地图附近显示其他地图，并对地图中的一部分结构进行保存、移走和重建。我们评估了现有方案，但它们的设计边界与需求不同。

Vehicle Map Framework 的地图面向载具设计，并以叠加在 worldtile 地图上的使用方式为前提；Delta-V 需要没有 worldtile 宿主的独立地图，以及任意平移和旋转的显示参考系，因此不适合直接复用其地图模型。

调研 VEF 后，KSCG 的基架处理及其关联机制带来了较高的改造和维护成本。VEF 使用者众多，本项目没有足够精力保证大幅改造之后，仍能维持它与其他内容之间的可靠性。Custom Quest Frame 较重，主要以整张地图为单位提供序列化与反序列化；Real Ruins 的系统对我们的局部结构需求也过重。因此，我们自行实现了地图内结构的序列化与按需重建，并将其与公共地图渲染能力一起抽出为 REF。这些是本项目的选型理由，不是对其他框架适用场景的否定。

## 功能与边界

| 能力 | 1.0.3 状态 |
| --- | --- |
| 多地图绘制 | 原版绘制捕获、局部到显示坐标变换、内容过滤与自定义输出；1.6 使用 CommandBuffer，1.7 使用 Waylith 命令和深度分组 |
| 原版多地图绘制入口 | `MapRuntime.Rendering.SetProvider` 接入原有逐图 Update/Draw 回调；保留队列并隔离常规 shader 参数，可裁切大面积平面覆盖层；特殊相机/GPU 管线仍有边界 |
| 无 worldtile 地图 | 口袋图创建、注册和公共 sourceMap 宿主服务 |
| 区域模板与 prefab | 任意区域选择器，Home Area 是其中一种；逐实例 Scribe、地形、基架、屋顶、原版禁止建屋顶区域、室内温度／真空度和重建回调 |
| 整图归档 | 全图选择的原版持久状态层；保留地图身份，支持卸载和恢复 |
| tick 速率 | 精确 0–100% 执行机会控制、逐图错峰、全速区域回调；0% 区外注销/恢复注册，可选移动补偿，消费方提供停图策略 |
| 跨图战斗 | 原版/CE 桥接、逐图显式启用、目标与弹丸状态失效；仅消费方请求时安装战斗补丁，迁入结果待游戏验收 |
| 跨图格子查询 | `MapRuntime.Spatial` 提供明确参考系、整数平移、真实格归属和有界地址表；与渲染/战斗开关独立，无自动 tick |
| SS 地图背景兼容 | 对任意来源的地图尊重 `MapGeneratorDef.disableMapClippers`：明确禁用时阻止 SS 强制开启遮片，其余地图不改结果；不判断消费方或地图类别 |
| IsoRim 1.7 可见性兼容 | 原版回调窗口保留源图局部视区，私有包围盒/模型剔除使用显示坐标；适用于任意消费方的绘制计划；用户已在 RE 中确认修复有效。矩形土层侧面与首次显示重建尚未适配 |

区域模板、封存与 prefab 共用内容模型；整图归档是范围覆盖全图并包含地图级状态的特例。区域清空/替换事务、资源池和 Gravship 搬运尚未全部迁入。不要将 1.0.3 解释为所有规划能力均已完成。API、生命周期契约和剩余工作见 [地图模块](doc/地图模块.md)，最初调研见 [迁移备忘录](doc/迁移备忘录.md)。

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

2026-11-06 prefab 环境层补齐新地图生成收尾：仅对生成期间导入环境的地图，在原版全量房间重建前后经公共环境层保留实际温度／真空度，普通地图与读档不重复套用模板。两版构建、环境专项与实际 Harmony 绑定通过，完整游戏新档待验收，见[环境契约](doc/地图模块.md#prefab-环境层2026-11-05)。

整图捕获已修复保存后调用压缩判定引发的 `no maps with compressor` 红字：必存对象清单在有效的原版上下文中生成，保存后核对 XML，保留对被引用对象缺失的检查。新增 12 项实际 1.7 压缩规则检查通过；完整地图往返仍需游戏内复测，契约见 [地图模块](doc/地图模块.md)。

2026-11-01 整图上下文审计另补齐快照版本头隔离、风格戒律／Pawn 政策／艺术故事等外部引用、parent 加载收尾，以及部分地图和注册通知异常的清理；新增主线程、存读档重入、生成／长事件和地图容量门禁。1.7 离线套件 586 项通过，两版 REF／Delta-V 标准构建通过；完整地图实机往返仍待验收。无版本头的旧开发快照不迁移，复测使用新档或新生成归档。逐阶段证据及测试边界见[上下文审计](doc/地图模块.md#整图原版上下文审计2026-11-01)。

归档外部引用补充 BattleLog／PlayLog，并统一保存检查与恢复登记清单；未知引用仍告警。新增 16 项实际 1.7 Scribe 检查覆盖重复枚举、未知引用告警、身份冲突及战斗／交互记录引用的重复 XML 往返；测试使用最小游戏上下文，不覆盖完整地图卸载／恢复。契约见 [地图模块](doc/地图模块.md)。

本轮 RE 接入后，1.6 与 1.7 的 REF/RE 标准构建均通过，零警告、零错误；`just test-services` 的 2,135 项检查通过。该检查使用生产算法及最小引擎占位，不覆盖实际 tick 注册、Harmony、Scribe 或 GPU。RE 构建已采用 Delta-V 的 REF 优先构建流程。已修复自动扫描误收 MapPreview 手动兼容钩子导致的初始化失败；1.7 的 `just test` 共 526 项检查通过，覆盖调度、坐标、绑定签名、渲染门禁组合及绘制捕获/提交，其中新增安装回归在实际游戏程序集上执行生产 Harmony 安装循环并成功绑定 34 个方法。1.6 PatchAll 验证器缺少原路径游戏程序集，绑定结果待补。`RimExodus` 的 `doc/REF接入实施与验收.md` 记录完整验收矩阵。用户已确认修正后的非聚焦护盾与导弹架上层显示正常。独立 Mod 的加载顺序、区域存读档、整图归档往返及双版本实机回归仍需游戏内验收；代理不自动启动游戏。

## 目录

兼容层注册边界与 RE 两后端组合审计见同级 `RimExodus/doc/兼容层注册审计.md`；消费方 `just test-compat` 包含真实 Harmony 绑定及缺失、延迟加载和签名异常的模拟回归。

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
