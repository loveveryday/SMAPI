using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;
using StardewValley;
using StardewValley.Internal;
using StardewValley.ItemTypeDefinitions;

namespace StardewModdingAPI.Mods.ConsoleCommands.Framework.Commands.Other;

/// <summary>A command which sends a debug command to the game.</summary>
[SuppressMessage("ReSharper", "UnusedMember.Global", Justification = "Loaded using reflection")]
internal class DebugCommand : ConsoleCommand
{
    /*********
    ** Fields
    *********/
    /// <summary>A name argument that may need translating from the current language to the game's internal name.</summary>
    private enum NameKind { FuzzyItem, ExactObject, ExactObjectRemainder, Npc, AnyNpc, Location }

    /// <summary>The debug commands which look up names, indexed by method name in <see cref="DebugCommands.DefaultHandlers"/>.</summary>
    /// <remarks>Argument positions were checked against Stardew Valley 1.6.15; see docs/technical/localized-debug-search.md.</remarks>
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
        ["AddDialogue"] = (NameKind.Npc, [1]),
        ["Clone"] = (NameKind.Npc, [1]),
        ["Engaged"] = (NameKind.Npc, [1]),
        ["FaceDirection"] = (NameKind.Npc, [1]),
        ["FacePlayer"] = (NameKind.Npc, [1]),
        ["Friendship"] = (NameKind.Npc, [1]),
        ["Hurry"] = (NameKind.Npc, [1]),
        ["InviteMovie"] = (NameKind.Npc, [1]),
        ["Jump"] = (NameKind.Npc, [1]),
        ["LoadDialogue"] = (NameKind.Npc, [1]),
        ["Marry"] = (NameKind.Npc, [1]),
        ["ShowTextAboveHead"] = (NameKind.Npc, [1]),
        ["SpeakTo"] = (NameKind.Npc, [1]),
        ["Speech"] = (NameKind.Npc, [1]),
        ["WarpToCharacter"] = (NameKind.Npc, [1]),
        ["WarpCharacterTo"] = (NameKind.Npc, [1])
    };

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

    /// <summary>Whether the command table has been checked against the game's debug commands.</summary>
    private bool CheckedNameArgs;


    /*********
    ** Public methods
    *********/
    /// <summary>Construct an instance.</summary>
    public DebugCommand()
        : base("debug", "Run one of the game's debug commands; for example, 'debug warp FarmHouse 1 1' warps the player to the farmhouse.") { }

    /// <summary>Handle the command.</summary>
    /// <param name="monitor">Writes messages to the console and log file.</param>
    /// <param name="command">The command name.</param>
    /// <param name="args">The command arguments.</param>
    public override void Handle(IMonitor monitor, string command, ArgumentParser args)
    {
        this.CheckNameArgsOnce(monitor);

        string[] gameArgs = this.TranslateNames(monitor, args.Values.ToArray());

        string oldOutput = Game1.debugOutput;
        if (DebugCommands.TryHandle(gameArgs)) // if it returns false, the game will log an error itself
        {
            monitor.Log(Game1.debugOutput != oldOutput
                ? $"> {Game1.debugOutput}"
                : "Sent debug command to the game, but there was no output.", LogLevel.Info);
        }
    }

    /// <inheritdoc />
    public override IEnumerable<string> GetCompletions(string[] args, int argIndex)
    {
        if (argIndex == 0)
        {
            try
            {
                return DebugCommands.SearchCommandNames("", displayAliases: false);
            }
            catch
            {
                return [];
            }
        }

        if (args.Length == 0 || !Context.IsWorldReady || !NameArgs.TryGetValue(GetCommandName(args[0]), out var entry))
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


    /*********
    ** Private methods
    *********/
    /// <summary>Get the method name for a debug command name or alias.</summary>
    /// <param name="name">The command name or alias.</param>
    private static string GetCommandName(string name)
    {
        return Aliases.Value.TryGetValue(name, out string? method) ? method : name;
    }

    /// <summary>Log any entries in <see cref="NameArgs"/> which no longer match a game debug command, e.g. after a game update.</summary>
    /// <param name="monitor">Writes messages to the console and log file.</param>
    private void CheckNameArgsOnce(IMonitor monitor)
    {
        if (this.CheckedNameArgs)
            return;
        this.CheckedNameArgs = true;

        try
        {
            HashSet<string> methods = new(
                typeof(DebugCommands.DefaultHandlers).GetMethods(BindingFlags.Public | BindingFlags.Static).Select(p => p.Name),
                StringComparer.OrdinalIgnoreCase
            );
            string[] missing = NameArgs.Keys.Where(key => !methods.Contains(key)).ToArray();
            if (missing.Any())
                monitor.Log($"Localized name lookup is configured for debug commands the game no longer has: {string.Join(", ", missing)}.", LogLevel.Trace);
        }
        catch (Exception ex)
        {
            monitor.Log($"Couldn't check the debug commands for localized name lookup.\n{ex}", LogLevel.Trace);
        }
    }

    /// <summary>Replace localized names in the debug command arguments with the internal names the game expects, if the game wouldn't match them as-is.</summary>
    /// <param name="monitor">Writes messages to the console and log file.</param>
    /// <param name="args">A copy of the command arguments, which may be edited in place.</param>
    private string[] TranslateNames(IMonitor monitor, string[] args)
    {
        if (args.Length < 2 || !NameArgs.TryGetValue(GetCommandName(args[0]), out var entry))
            return args;

        try
        {
            // the game joins the whole remainder into one name
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

    /// <summary>Get the internal name for a localized name, or <c>null</c> if the game already matches it or nothing matches.</summary>
    /// <param name="monitor">Writes messages to the console and log file.</param>
    /// <param name="kind">The kind of name to translate.</param>
    /// <param name="query">The name entered by the player.</param>
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
    /// <param name="query">The name entered by the player.</param>
    private static string? GetObjectInternalName(string query)
    {
        IItemDataDefinition type = ItemRegistry.GetObjectTypeDefinition();
        ParsedItemData[] all = type.GetAllIds().Select(type.GetData).Where(p => p != null).ToArray();
        if (all.Any(p => string.Equals(p.InternalName, query, StringComparison.OrdinalIgnoreCase)))
            return null;
        return all.FirstOrDefault(p => string.Equals(p.DisplayName, query, StringComparison.OrdinalIgnoreCase))?.InternalName;
    }

    /// <summary>Find the best match using the game's own fuzzy rules, comparing against a different name field.</summary>
    /// <typeparam name="T">The value type.</typeparam>
    /// <param name="query">The name entered by the player.</param>
    /// <param name="values">The values to search.</param>
    /// <param name="getName">Get the name to compare for a value.</param>
    private static T? FuzzySearchBy<T>(string query, IEnumerable<T> values, Func<T, string?> getName)
        where T : class
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

    /// <summary>Get all NPCs in the world.</summary>
    /// <param name="villagersOnly">Whether to only include villagers.</param>
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

    /// <summary>Get all locations in the world, including building interiors.</summary>
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

    /// <summary>Add every non-empty name for the given values.</summary>
    /// <typeparam name="T">The value type.</typeparam>
    /// <param name="names">The set to add names to.</param>
    /// <param name="values">The values whose names to add.</param>
    /// <param name="getNames">Get each name to add for a value.</param>
    private static void AddNames<T>(HashSet<string> names, IEnumerable<T> values, params Func<T, string?>[] getNames)
    {
        foreach (T value in values)
        {
            foreach (Func<T, string?> getName in getNames)
            {
                string? name = getName(value);
                if (!string.IsNullOrWhiteSpace(name))
                    names.Add(name);
            }
        }
    }
}
