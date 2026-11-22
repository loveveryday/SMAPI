using System.Collections.Generic;

namespace StardewModdingAPI.Framework.Commands;

/// <summary>A core SMAPI console command.</summary>
interface IInternalCommand
{
    /*********
    ** Accessors
    *********/
    /// <summary>The command name, which the user must type to trigger it.</summary>
    string Name { get; }

    /// <summary>The human-readable documentation shown when the player runs the built-in 'help' command.</summary>
    string Description { get; }


    /*********
    ** Methods
    *********/
    /// <summary>Handle the console command when it's entered by the user.</summary>
    /// <param name="args">The command arguments.</param>
    /// <param name="monitor">Writes messages to the console.</param>
    void HandleCommand(string[] args, IMonitor monitor);

    /// <summary>Get suggested argument values for tab-completion.</summary>
    /// <param name="args">The arguments entered so far (including the current partial argument).</param>
    /// <param name="argIndex">The zero-based index of the argument being completed.</param>
    IEnumerable<string> GetCompletions(string[] args, int argIndex) => [];
}
