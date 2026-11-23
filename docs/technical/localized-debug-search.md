← [README](../README.md)

# 让 `debug` 命令支持当前语言名查找

本文说明如何在 SMAPI 的 ConsoleCommands 里实现：通过 `debug` 运行游戏调试命令时，物品、NPC、地点除了英文内部名，也能用当前游戏语言的名字，比如 `debug f 防风草`、`debug warpcharacter 阿比盖尔`、`debug warp 鹈鹕镇`。

同样的功能已经用 Harmony 做成了独立 MOD（StardewValleyMods 仓库里的 `LocalizedDebugSearch`）。本文把它改成 SMAPI 内置实现，不需要 Harmony。

## 目录
* [背景](#背景)
* [方案选择](#方案选择)
* [需要翻译的命令](#需要翻译的命令)
* [实现步骤](#实现步骤)
* [边界情况](#边界情况)
* [测试清单](#测试清单)

## 背景

`debug` 命令（`src/SMAPI.Mods.ConsoleCommands/Framework/Commands/Other/DebugCommand.cs`）只是把参数原样交给游戏的 `DebugCommands.TryHandle(string[])`，名字怎么匹配由游戏决定。根据 1.6.15 的 `Stardew Valley.dll`：

| 查找对象 | 游戏用的方法 | 匹配的名字 |
|---|---|---|
| 物品（模糊） | `Utility.fuzzyItemSearch(query, stack_count = 1, useLocalizedNames = false)` | `ParsedItemData.InternalName`。参数 `useLocalizedNames: true` 时改用 `DisplayName`，但没有任何调试命令这样传 |
| 物品（精确） | `ItemNamed`、`Lookup` 自己循环 | 只比较 `(O)` 物品的 `InternalName`，忽略大小写 |
| NPC | `Utility.fuzzyCharacterSearch(query, must_be_villager = true)` | `NPC.Name` |
| 地点 | `Utility.fuzzyLocationSearch(query)` | `GameLocation.NameOrUniqueName` |

所有模糊查找最后都走 `Utility.fuzzySearch(query, terms)`，按"完全相同 → 格式化后相同 → 开头相同 → 包含"的顺序选最佳匹配。格式化只做 `ToLowerInvariant`、去空格和标点，对中文同样适用。

## 方案选择

| 方案 | 做法 | 结论 |
|---|---|---|
| **A. 在 `DebugCommand` 里翻译参数**（推荐） | 调用 `TryHandle` 前，先按命令表把参数里的当前语言名换成内部名 | SMAPI 本身不改游戏代码，对游戏和其他 MOD 没有影响。代价是要维护一张命令表 |
| B. Harmony 补丁 | 和 `LocalizedDebugSearch` MOD 一样，给 `Utility.fuzzyCharacterSearch` 等方法打补丁 | 能自动覆盖所有命令，但 SMAPI 自带的 MOD 一般不修改游戏代码，补丁还会影响所有调用这些方法的代码，不适合放进 SMAPI |

下文按方案 A 实现。

核心规则：**只有原版按英文名找不到时才翻译**。这样英文名、内部名的行为和原版完全一样，翻译只是兜底。

## 需要翻译的命令

键是 `DebugCommands.DefaultHandlers` 里的方法名，也就是命令的正式名称。别名（`f`、`wc` 等）不用写进表里，运行时用反射读 `OtherNamesAttribute` 解析，见[实现步骤](#实现步骤)第 2 步。

| 命令（方法名） | 名字参数位置 | 类型 | 备注 |
|---|---|---|---|
| `FuzzyItemNamed` | 1 | 物品（模糊） | 别名 `f`、`fin` |
| `GetIndex` | 1 | 物品（模糊） | |
| `ItemNamed` | 1 | 物品（精确，`(O)`） | 别名 `in` |
| `Lookup` | 1 到末尾整体 | 物品（精确，`(O)`） | 别名 `lu`。游戏用 `TryGetRemainder` 把剩余参数拼成一个名字 |
| `Warp` | 1 | 地点 | |
| `Event` | 1 | 地点 | |
| `AddDialogue` | 1 | NPC | 别名 `dialogue` |
| `Clone` | 1 | NPC | |
| `Engaged` | 1 | NPC | |
| `FaceDirection` | 1 | NPC | 别名 `fd`、`face` |
| `FacePlayer` | 1 | NPC | |
| `Friendship` | 1 | NPC | 别名 `friend` |
| `Hurry` | 1 | NPC | |
| `InviteMovie` | 1 | NPC | |
| `Jump` | 1 | NPC | |
| `LoadDialogue` | 1 | NPC | |
| `Marry` | 1 | NPC | |
| `ShowTextAboveHead` | 1 | NPC | 别名 `sb` |
| `SpeakTo` | 1 | NPC | 别名 `db`。省略参数时游戏默认用 `Pierre` |
| `Speech` | 1 | NPC | |
| `WarpToCharacter` | 1 | NPC | 别名 `wtc` |
| `WarpCharacterTo` | 1 | NPC | 别名 `wct` |
| `WarpCharacter` | 1 | NPC（含非村民） | 别名 `wc`。游戏传 `must_be_villager: false` |
| `WarpCharacterToMe` | 1 | NPC（含非村民） | 别名 `wctm`。游戏传 `must_be_villager: false` |
| `Movie` | 2 | NPC | 参数 1 是电影 ID，参数 2 是受邀 NPC，可选 |
| `Emote` | 1、3、5… | NPC（含非村民） | 参数成对出现：`NPC名 表情 NPC名 表情…`，游戏传 `must_be_villager: false` |

不需要翻译的：按物品 ID 的命令（`item`、`furniture`、`tv` 等），玩家自己起的名字（`warpanimaltome` 的动物名、`changepet` 的宠物名），`shop`（按商店 ID 匹配），`showplurals`（只列出内部名）。

> 位置和别名来自反编译源码，`Emote` 来自 1.6.15 DLL 的 IL（反编译源码里 `Emote` 不走 NPC 搜索，和 DLL 不一致，以 DLL 为准）。游戏更新后要重新核对，见[边界情况](#边界情况)里的启动自检。

## 实现步骤

所有改动都在 `src/SMAPI.Mods.ConsoleCommands/Framework/Commands/Other/DebugCommand.cs`。

### 1. 命令表

```csharp
/// <summary>A name argument that may need translating from the current language to the game's internal name.</summary>
private enum NameKind { FuzzyItem, ExactObject, ExactObjectRemainder, Npc, AnyNpc, Location }

/// <summary>The debug commands which look up names, indexed by method name in <see cref="DebugCommands.DefaultHandlers"/>.</summary>
private static readonly Dictionary<string, (NameKind Kind, int[] Indexes)> NameArgs = new(StringComparer.OrdinalIgnoreCase)
{
    ["FuzzyItemNamed"] = (NameKind.FuzzyItem, [1]),
    ["GetIndex"] = (NameKind.FuzzyItem, [1]),
    ["ItemNamed"] = (NameKind.ExactObject, [1]),
    ["Lookup"] = (NameKind.ExactObjectRemainder, [1]),
    ["Warp"] = (NameKind.Location, [1]),
    ["Event"] = (NameKind.Location, [1]),
    ["WarpCharacter"] = (NameKind.AnyNpc, [1]),
    ["WarpCharacterToMe"] = (NameKind.AnyNpc, [1]),
    ["Movie"] = (NameKind.Npc, [2]),
    ["Emote"] = (NameKind.AnyNpc, [1, 3, 5, 7, 9]), // pairs of <npc> <emote>; indexes past the end are skipped
    // the rest are all (NameKind.Npc, [1]):
    ["AddDialogue"] = (NameKind.Npc, [1]),
    // ... Clone, Engaged, FaceDirection, FacePlayer, Friendship, Hurry, InviteMovie, Jump,
    //     LoadDialogue, Marry, ShowTextAboveHead, SpeakTo, Speech, WarpToCharacter, WarpCharacterTo
};
```

### 2. 别名解析

`DebugCommands.Aliases` 是私有字段，所以用和游戏相同的方式，自己从 `OtherNamesAttribute`（`StardewValley.Internal`，公开类）构建一次：

```csharp
/// <summary>Maps each debug command alias to its method name.</summary>
private static readonly Lazy<Dictionary<string, string>> Aliases = new(() =>
{
    Dictionary<string, string> aliases = new(StringComparer.OrdinalIgnoreCase);
    foreach (MethodInfo method in typeof(DebugCommands.DefaultHandlers).GetMethods(BindingFlags.Public | BindingFlags.Static))
    {
        foreach (string alias in method.GetCustomAttribute<OtherNamesAttribute>()?.Aliases ?? [])
            aliases.TryAdd(alias, method.Name);
    }
    return aliases;
});

private static string GetCommandName(string name)
{
    return Aliases.Value.TryGetValue(name, out string? method) ? method : name;
}
```

### 3. 翻译参数

在 `Handle` 里复制一份参数数组，翻译后再交给游戏。不要直接修改 `args.Values`：

```csharp
public override void Handle(IMonitor monitor, string command, ArgumentParser args)
{
    string[] gameArgs = this.TranslateNames(monitor, args.Values.ToArray());

    string oldOutput = Game1.debugOutput;
    if (DebugCommands.TryHandle(gameArgs))
    {
        // (unchanged)
    }
}

private string[] TranslateNames(IMonitor monitor, string[] args)
{
    if (args.Length < 2 || !NameArgs.TryGetValue(GetCommandName(args[0]), out var entry))
        return args;

    try
    {
        // lu: the whole remainder is one name
        if (entry.Kind == NameKind.ExactObjectRemainder)
        {
            string query = string.Join(" ", args.Skip(1));
            return this.TranslateOne(monitor, NameKind.ExactObject, query) is string name
                ? [args[0], name]
                : args;
        }

        foreach (int i in entry.Indexes.Where(i => i < args.Length))
        {
            if (this.TranslateOne(monitor, entry.Kind, args[i]) is string name)
                args[i] = name;
        }
    }
    catch (Exception ex)
    {
        // never block the original command because of a translation error
        monitor.Log($"Couldn't translate names for debug command '{args[0]}', sending it unchanged.\n{ex}", LogLevel.Trace);
    }
    return args;
}
```

### 4. 每种类型的翻译

每种都是：原版能找到就返回 `null`（不改参数），否则按当前语言名查找，找到后返回游戏认识的内部名。

```csharp
/// <summary>Get the internal name for a localized name, or <c>null</c> if the game already matches it or nothing matches.</summary>
private string? TranslateOne(IMonitor monitor, NameKind kind, string query)
{
    string? name = kind switch
    {
        NameKind.FuzzyItem => Utility.fuzzyItemSearch(query) is null && Utility.fuzzyItemSearch(query, useLocalizedNames: true) is Item item
            ? ItemRegistry.GetData(item.QualifiedItemId)?.InternalName
            : null,

        NameKind.ExactObject => GetObjectInternalName(query),

        NameKind.Npc or NameKind.AnyNpc => Context.IsWorldReady && Utility.fuzzyCharacterSearch(query, kind == NameKind.Npc) is null
            ? FuzzySearchBy(query, GetNpcs(villagersOnly: kind == NameKind.Npc), npc => npc.displayName)?.Name
            : null,

        NameKind.Location => Context.IsWorldReady && Utility.fuzzyLocationSearch(query) is null
            ? FuzzySearchBy(query, GetLocations(), loc => loc.DisplayName)?.NameOrUniqueName
            : null,

        _ => null
    };

    if (name != null)
        monitor.Log($"Interpreted '{query}' as '{name}'.", LogLevel.Info);
    return name;
}

/// <summary>Get the internal name of the (O) item whose display name exactly matches, unless the query is already an internal name.</summary>
private static string? GetObjectInternalName(string query)
{
    IItemDataDefinition type = ItemRegistry.GetObjectTypeDefinition();
    ParsedItemData[] all = type.GetAllIds().Select(type.GetData).Where(p => p != null).ToArray();
    if (all.Any(p => string.Equals(p.InternalName, query, StringComparison.OrdinalIgnoreCase)))
        return null;
    return all.FirstOrDefault(p => string.Equals(p.DisplayName, query, StringComparison.OrdinalIgnoreCase))?.InternalName;
}

/// <summary>Find the best match using the game's own fuzzy rules, comparing against a different name field.</summary>
private static T? FuzzySearchBy<T>(string query, IEnumerable<T> values, Func<T, string?> getName) where T : class
{
    Dictionary<string, T> byName = new();
    foreach (T value in values)
    {
        string? name = getName(value);
        if (!string.IsNullOrEmpty(name))
            byName.TryAdd(name, value); // first one wins, same as the game
    }
    return Utility.fuzzySearch(query, byName.Keys) is string match ? byName[match] : null;
}

private static IEnumerable<NPC> GetNpcs(bool villagersOnly)
{
    List<NPC> npcs = [];
    Utility.ForEachCharacter(npc =>
    {
        if (!villagersOnly || npc.IsVillager)
            npcs.Add(npc);
        return true;
    });
    return npcs;
}

private static IEnumerable<GameLocation> GetLocations()
{
    List<GameLocation> locations = [];
    Utility.ForEachLocation(loc =>
    {
        locations.Add(loc);
        return true;
    });
    return locations;
}
```

说明：

* NPC 翻译后返回 `npc.Name`，游戏会按"完全相同"（优先级 0）重新找到同一个 NPC。
* 地点返回 `NameOrUniqueName`，和游戏建索引用的字段一致，室内建筑这类有唯一名的地点也能找回来。
* 物品返回 `InternalName`。

### 5. Tab 补全

`GetCompletions` 现在只补全命令名和 `warp` 的地点名。可以复用同一张命令表，给名字参数补全"当前语言名 + 内部名"，和 `player_add name` 现有的做法一致：

```csharp
public override IEnumerable<string> GetCompletions(string[] args, int argIndex)
{
    if (argIndex == 0)
        return /* (unchanged) */;

    if (args.Length == 0 || !NameArgs.TryGetValue(GetCommandName(args[0]), out var entry) || !Context.IsWorldReady)
        return [];
    if (entry.Kind != NameKind.ExactObjectRemainder && !entry.Indexes.Contains(argIndex))
        return [];

    try
    {
        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
        switch (entry.Kind)
        {
            case NameKind.FuzzyItem:
                foreach (IItemDataDefinition type in ItemRegistry.ItemTypes)
                    AddNames(names, type.GetAllIds().Select(type.GetData), p => p?.DisplayName, p => p?.InternalName);
                break;

            case NameKind.ExactObject:
            case NameKind.ExactObjectRemainder:
                IItemDataDefinition objects = ItemRegistry.GetObjectTypeDefinition();
                AddNames(names, objects.GetAllIds().Select(objects.GetData), p => p?.DisplayName, p => p?.InternalName);
                break;

            case NameKind.Npc:
            case NameKind.AnyNpc:
                AddNames(names, GetNpcs(villagersOnly: entry.Kind == NameKind.Npc), npc => npc.displayName, npc => npc.Name);
                break;

            case NameKind.Location:
                AddNames(names, GetLocations(), loc => loc.DisplayName, loc => loc.NameOrUniqueName);
                break;
        }
        return names;
    }
    catch
    {
        return []; // completions run on a background thread, so game state may change mid-read
    }
}

private static void AddNames<T>(HashSet<string> names, IEnumerable<T> values, params Func<T, string?>[] getNames)
{
    foreach (T value in values)
    {
        foreach (var getName in getNames)
        {
            string? name = getName(value);
            if (!string.IsNullOrWhiteSpace(name))
                names.Add(name);
        }
    }
}
```

原来 `warp` / `w` 的特殊分支可以删掉，`Warp` 已经在命令表里。注意原分支只补全 `loc.Name`，新写法同时补全显示名。

## 边界情况

* **存档未加载**：`fuzzyCharacterSearch` 和 `fuzzyLocationSearch` 要遍历当前世界，只在 `Context.IsWorldReady` 时翻译。物品数据在游戏启动后就可用，但翻译代码整体包在 `try/catch` 里，出错就原样交给游戏，不影响原命令。
* **补全在后台线程运行**：`player_add` 的补全代码已经说明了这一点。补全里读游戏状态必须包 `try/catch`，并且只读不写。`Handle` 在游戏线程运行，不受影响。
* **内部名重复**：不同类型的物品可能有相同的内部名。`f`/`getindex` 换成内部名后，游戏可能选中同名的另一个物品。要彻底解决，可以把这两个命令改成直接按翻译出的 `QualifiedItemId` 生成物品，但那样等于在 SMAPI 里重写命令逻辑（数量、品质、武器附魔），不建议。
* **显示名重复**：室内地点没有自己的显示名时会沿用所在地点的显示名；NPC 也可能同名。`FuzzySearchBy` 取先遍历到的那个，和游戏处理内部名重复的方式一致。
* **参数里的空格**：SMAPI 按空格切分参数。名字带空格时要加引号，比如 `debug fin "Galaxy Sword"`。`lu` 例外，游戏自己把剩余参数拼起来。
* **游戏更新导致命令表过期**：建议在 ConsoleCommands 的 `Entry` 或第一次执行 `debug` 时做一次自检：检查命令表里的每个键在 `DebugCommands.DefaultHandlers` 里都有同名公开静态方法，缺失的记一条 `LogLevel.Trace` 日志。参数位置没法自动校验，游戏大版本更新时要按本文的方法重新核对。
* **和 `LocalizedDebugSearch` MOD 共存**：SMAPI 先把参数翻译成内部名，MOD 的 Harmony 补丁看到的是能直接匹配的英文名，不会再触发回退。两者同时装不会冲突，但 SMAPI 实现后这个 MOD 就不需要了。

## 测试清单

进入存档后在 SMAPI 控制台运行（中文游戏）：

| 命令 | 预期 |
|---|---|
| `debug f 防风草 5` | 背包多 5 个防风草，控制台显示 `Interpreted '防风草' as 'Parsnip'.` |
| `debug f parsnip` | 和原版一样，不显示翻译日志 |
| `debug getindex 防风草` | 输出 `防风草's qualified ID is (O)24` |
| `debug in 防风草 3` | 背包多 3 个防风草 |
| `debug lu 防风草` | 输出 `Parsnip 24` |
| `debug wctm 阿比盖尔` | 阿比盖尔被传送到玩家身边 |
| `debug movie <电影ID> 阿比盖尔` | 只翻译第 2 个参数 |
| `debug emote 阿比盖尔 20 塞巴斯蒂安 16` | 阿比盖尔头上显示爱心，塞巴斯蒂安显示感叹号，控制台有两条 `Interpreted` 日志。两个 NPC 要和你在同一地点，可先用 `wctm` 传过来 |
| `debug warp <地点中文名>` | 传送到该地点，地点名以游戏地图上显示的为准 |
| `debug sb 不存在的名字` | 不翻译，游戏按原样报错 |
| 输入 `debug wc 阿` 后按 Tab | 补全列表里有中文名和英文名 |
| 在标题画面运行 `debug f 防风草` | 物品照常翻译；NPC/地点命令不翻译，也不报错 |

`emote` 的表情参数是 `Character` 上的常量（1.6.15 DLL）：

| 值 | 表情 | 值 | 表情 |
|---|---|---|---|
| 4 | 空罐子 `emptyCanEmote` | 32 | 开心 `happyEmote` |
| 8 | 问号 `questionMarkEmote` | 36 | 叉号 `xEmote` |
| 12 | 生气 `angryEmote` | 40 | 省略号 `pauseEmote` |
| 16 | 感叹号 `exclamationEmote` | 52 | 打游戏 `videoGameEmote` |
| 20 | 爱心 `heartEmote` | 56 | 音符 `musicNoteEmote` |
| 24 | 睡觉 `sleepEmote` | 60 | 脸红 `blushEmote` |
| 28 | 伤心 `sadEmote` | | |
