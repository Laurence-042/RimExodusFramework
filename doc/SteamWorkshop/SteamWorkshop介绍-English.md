[h1]RimExodus Framework[/h1]

RimExodus Framework (REF) is a shared dependency for the RimExodus series. It provides map rendering and tools for saving and rebuilding structures within maps to RimExodus and RimExodus: Delta-V. The framework itself adds no ships, weapons, world tiles, or standalone gameplay. Install it to run mods that depend on it.

[b]Supports RimWorld 1.6 and 1.7. Requires Harmony. Load order: Harmony → RimExodus Framework → RimExodus / RimExodus: Delta-V.[/b]

[h1]What the framework provides[/h1]

[list]
[*]Tools for consumer mods to draw other maps near the current map, with their own display positions and rotations.
[*]Maps without a world tile, plus tools to save and restore region templates, prefabs, and whole-map archives.
[*]Per-map tick-rate controls, cross-map spatial queries, and a cross-map combat bridge enabled only when requested by a consumer mod.
[/list]

These are building blocks for other mods. Consumer mods decide which maps, rules, and visuals to use. Installing REF alone does not turn ordinary maps into a seamless world or add new gameplay content.

[h1]Source and feedback[/h1]

MIT licensed. See the [url=https://github.com/Laurence-042/RimExodusFramework]GitHub repository and documentation[/url] for feature boundaries, developer APIs, build instructions, and remaining work. Please include a log, game version, and steps to reproduce when reporting an issue on GitHub Issues.
