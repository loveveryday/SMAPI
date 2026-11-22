using System;
using System.Collections.Generic;

namespace StardewModdingAPI;

/// <summary>Provides an API for managing console commands.</summary>
public interface ICommandHelper : IModLinked
{
    /*********
    ** Public methods
    *********/
    /// <summary>Add a console command.</summary>
    /// <param name="name">The command name, which the user must type to trigger it.</param>
    /// <param name="documentation">The human-readable documentation shown when the player runs the built-in 'help' command.</param>
    /// <param name="callback">The method to invoke when the command is triggered. This method is passed the command name and arguments submitted by the user.</param>
    /// <exception cref="ArgumentNullException">The <paramref name="name"/> or <paramref name="callback"/> is null or empty.</exception>
    /// <exception cref="FormatException">The <paramref name="name"/> is not a valid format.</exception>
    /// <exception cref="ArgumentException">There's already a command with that name.</exception>
    ICommandHelper Add(string name, string documentation, Action<string, string[]> callback);

    /// <summary>Add a console command with optional tab-completion support for its arguments.</summary>
    /// <param name="name">The command name, which the user must type to trigger it.</param>
    /// <param name="documentation">The human-readable documentation shown when the player runs the built-in 'help' command.</param>
    /// <param name="callback">The method to invoke when the command is triggered. This method is passed the command name and arguments submitted by the user.</param>
    /// <param name="getCompletions">An optional callback which returns suggested argument values for tab-completion. It receives the command name, the arguments entered so far (including the current partial argument), and the zero-based index of the argument being completed. Note that this may run on a background console thread.</param>
    /// <exception cref="ArgumentNullException">The <paramref name="name"/> or <paramref name="callback"/> is null or empty.</exception>
    /// <exception cref="FormatException">The <paramref name="name"/> is not a valid format.</exception>
    /// <exception cref="ArgumentException">There's already a command with that name.</exception>
    ICommandHelper Add(string name, string documentation, Action<string, string[]> callback, Func<string, string[], int, IEnumerable<string>>? getCompletions);
}
