[h1]RimExodus Framework[/h1]

RimExodus Framework（REF）是 RimExodus 系列共用的前置框架。它为 RimExodus 与 RimExodus: Delta-V 提供地图绘制、地图内结构保存与重建等基础能力。框架本身不添加飞船、武器、世界地块或独立玩法；安装它是为了运行依赖它的 Mod。

[b]支持 RimWorld 1.6 和 1.7。需要 Harmony。加载顺序：Harmony → RimExodus Framework → RimExodus / RimExodus: Delta-V。[/b]

[h1]框架提供什么[/h1]

[list]
[*]让消费方在一张地图附近绘制其他地图，并指定显示位置与角度。
[*]提供无 worldtile 的独立地图，以及区域模板、prefab 和整图归档所需的保存与恢复工具。
[*]提供逐图 tick 速率控制、跨图空间查询，以及由消费方按需启用的跨图战斗桥接。
[/list]

这些是供其他 Mod 调用的基础设施，具体地图、规则与画面由消费方决定。安装 REF 后，普通地图不会自动变成无缝世界，也不会单独出现新的游戏内容。

[h1]源码与反馈[/h1]

MIT 开源。功能边界、开发接口、构建方法和待完成事项请看 [url=https://github.com/Laurence-042/RimExodusFramework]GitHub 仓库与文档[/url]。遇到问题请附日志、游戏版本和复现步骤，在 GitHub Issues 反馈。
