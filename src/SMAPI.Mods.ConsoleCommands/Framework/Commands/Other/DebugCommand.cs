using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using StardewValley;

namespace StardewModdingAPI.Mods.ConsoleCommands.Framework.Commands.Other;

/// <summary>A command which sends a debug command to the game.</summary>
[SuppressMessage("ReSharper", "UnusedMember.Global", Justification = "Loaded using reflection")]
internal class DebugCommand : ConsoleCommand
{
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
        string oldOutput = Game1.debugOutput;
        if (DebugCommands.TryHandle(args.Values)) // if it returns false, the game will log an error itself
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

        if (argIndex == 1 && args.Length > 0 && (args[0].Equals("warp", StringComparison.OrdinalIgnoreCase) || args[0].Equals("w", StringComparison.OrdinalIgnoreCase)))
        {
            if (Context.IsWorldReady && Game1.locations != null)
            {
                try
                {
                    return Game1.locations
                        .ToArray()
                        .Where(loc => !string.IsNullOrWhiteSpace(loc?.Name))
                        .Select(loc => loc.Name);
                }
                catch
                {
                    return [];
                }
            }
        }

        return [];
    }
}
