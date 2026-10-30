# RimExodus Framework (REF) 1.0.0

RimExodus 系列通用的地图渲染框架以及地图内结构操作工具包。REF 是独立的 RimWorld 前置 Mod，提供公共程序集 `RimExodus.Maps.dll`，不引用 Delta-V 或 RimExodus，也不添加飞船、武器或其他玩法内容。使用 MIT 协议，见 [LICENSE](LICENSE)。

## 背景

RimExodus 与 RimExodus: Delta-V 都需要在当前地图附近显示其他地图，并对地图中的一部分结构进行保存、移走和重建。我们评估了现有方案，但它们的设计边界与需求不同。

Vehicle Map Framework 的地图面向载具设计，并以叠加在 worldtile 地图上的使用方式为前提；Delta-V 需要没有 worldtile 宿主的独立地图，以及任意平移和旋转的显示参考系，因此不适合直接复用其地图模型。

调研 VEF 后，KSCG 的基架处理及其关联机制带来了较高的改造和维护成本。VEF 使用者众多，本项目没有足够精力保证大幅改造之后，仍能维持它与其他内容之间的可靠性。Custom Quest Frame 较重，主要以整张地图为单位提供序列化与反序列化；Real Ruins 的系统对我们的局部结构需求也过重。因此，我们自行实现了地图内结构的序列化与按需重建，并将其与公共地图渲染能力一起抽出为 REF。这些是本项目的选型理由，不是对其他框架适用场景的否定。

## 功能与边界

| 能力 | 1.0.0 状态 |
| --- | --- |
| 多地图绘制 | 原版绘制捕获、局部到显示坐标变换、内容过滤与自定义输出；1.6 使用 CommandBuffer，1.7 使用 Waylith 命令和深度分组 |
| 原版多地图绘制入口 | `MapRuntime.Rendering.SetProvider` 接入原有逐图 Update/Draw 回调；保留队列并隔离常规 shader 参数，可裁切大面积平面覆盖层；特殊相机/GPU 管线仍有边界 |
| 无 worldtile 地图 | 口袋图创建、注册和公共 sourceMap 宿主服务 |
| 区域模板与 prefab | 任意区域选择器，Home Area 是其中一种；逐实例 Scribe、地形、基架、屋顶和重建回调 |
| 整图归档 | 全图选择的原版持久状态层；保留地图身份，支持卸载和恢复 |
| tick 速率 | 精确 0–100% 执行机会控制、逐图错峰、全速区域回调；0% 区外注销/恢复注册，可选移动补偿，消费方提供停图策略 |
| 跨图战斗 | 原版/CE 桥接、逐图显式启用、目标与弹丸状态失效；仅消费方请求时安装战斗补丁，迁入结果待游戏验收 |

区域模板、封存与 prefab 共用内容模型；整图归档是范围覆盖全图并包含地图级状态的特例。区域清空/替换事务、资源池和 Gravship 搬运尚未全部迁入。不要将 1.0.0 解释为所有规划能力均已完成。API、生命周期契约和剩余工作见 [地图模块](doc/地图模块.md)，最初调研见 [迁移备忘录](doc/迁移备忘录.md)。

Delta-V 已切换为唯一 REF 后端，保留船壳选择、宇宙运动、飞船身份、标记展开与战斗策略。RimExodus 已接入可选 REF 后端，默认保留 legacy，开关重启生效；REF 模式的生成、渲染、降频、区域恢复及原版/CE 战斗接线仍需游戏内验收。

## 安装与依赖

需要 RimWorld 1.6 或 1.7 和 Harmony。将本目录作为独立 Mod 安装并启用，加载顺序为 Harmony → RimExodus Framework → Delta-V / RimExodus。REF 的 DLL 只在 REF 的版本化 `Assemblies` 目录加载；消费方不要再把它打包进自己的 `Assemblies`。

## 构建

需要 `just` 和对应 .NET SDK：1.7 使用 SDK 10；1.6 使用可构建 net48 的 SDK。Windows 构建入口使用系统自带的 `powershell.exe`（Windows PowerShell 5.1），无需另装 PowerShell 7。可用 `DOTNET_EXE` 指定 dotnet 可执行文件。本机引用默认位置见 [引用配置](build/RimWorldReferences.props)，可用 MSBuild 的 `RimWorldDir`、`HarmonyPath` 属性或同名环境变量覆盖。1.6 使用编译期 NuGet 引用，不分发游戏/Harmony 程序集。

```powershell
just build       # 1.7 Release
just build 1.6   # 1.6 Release
just test        # 1.7 离线回归
just test-services # SDK 8+，版本无关的调度/生成协调检查
```

输出为 `<游戏版本>/Assemblies/RimExodus.Maps.dll`，不提交构建产物。独立克隆可以直接构建；不要求 Delta-V 或 RimExodus 源码。若同级存在 RimExodus，测试会只读链接其 transpiler，额外检查补丁组合顺序。

Delta-V 与 RimExodus 的 `just build` 会先调用同级 REF 的 `just build`，成功后复制 DLL 到各消费方的 `References/REF/<版本>/` 作为编译输入，再构建消费方。运行时仍由独立 REF Mod 提供程序集。

## 验证

本轮 RE 接入后，1.6 与 1.7 的 REF/RE 标准构建均通过，零警告、零错误；`just test-services` 的 2,135 项检查通过。该检查使用生产算法及最小引擎占位，不覆盖实际 tick 注册、Harmony、Scribe 或 GPU。RE 构建已采用 Delta-V 的 REF 优先构建流程。已修复自动扫描误收 MapPreview 手动兼容钩子导致的初始化失败；1.7 的 `just test` 共 258 项检查通过，覆盖调度、坐标、绑定签名、渲染门禁组合及绘制捕获/提交，其中新增安装回归在实际游戏程序集上执行生产 Harmony 安装循环并成功绑定 34 个方法。1.6 PatchAll 验证器缺少原路径游戏程序集，绑定结果待补。`RimExodus` 的 `doc/REF接入实施与验收.md` 记录完整验收矩阵。用户已确认修正后的非聚焦护盾与导弹架上层显示正常。独立 Mod 的加载顺序、区域存读档、整图归档往返及双版本实机回归仍需游戏内验收；代理不自动启动游戏。

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
