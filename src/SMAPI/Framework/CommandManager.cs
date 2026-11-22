using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using StardewModdingAPI.Framework.Commands;
using StardewValley;

namespace StardewModdingAPI.Framework;

/// <summary>Manages console commands.</summary>
internal class CommandManager
{
    /*********
    ** Fields
    *********/
    /// <summary>The commands registered with SMAPI.</summary>
    private readonly IDictionary<string, Command> Commands = new Dictionary<string, Command>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Writes messages to the console.</summary>
    private readonly IMonitor Monitor;


    /*********
    ** Public methods
    *********/
    /// <summary>Construct an instance.</summary>
    /// <param name="monitor">Writes messages to the console.</param>
    public CommandManager(IMonitor monitor)
    {
        this.Monitor = monitor;
    }

    /// <summary>Add a console command.</summary>
    /// <param name="mod">The mod adding the command (or <c>null</c> for a SMAPI command).</param>
    /// <param name="name">The command name, which the user must type to trigger it.</param>
    /// <param name="documentation">The human-readable documentation shown when the player runs the built-in 'help' command.</param>
    /// <param name="callback">The method to invoke when the command is triggered. This method is passed the command name and arguments submitted by the user.</param>
    /// <param name="getCompletions">An optional callback which returns suggested argument values for tab-completion.</param>
    /// <exception cref="ArgumentNullException">The <paramref name="name"/> or <paramref name="callback"/> is null or empty.</exception>
    /// <exception cref="FormatException">The <paramref name="name"/> is not a valid format.</exception>
    /// <exception cref="ArgumentException">There's already a command with that name.</exception>
    public CommandManager Add(IModMetadata? mod, string name, string documentation, Action<string, string[]> callback, Func<string, string[], int, IEnumerable<string>>? getCompletions = null)
    {
        name = this.GetNormalizedName(name)!; // null-checked below

        // validate format
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentNullException(nameof(name), "Can't register a command with no name.");
        if (name.Any(char.IsWhiteSpace))
            throw new FormatException($"Can't register the '{name}' command because the name can't contain whitespace.");
        if (callback == null)
            throw new ArgumentNullException(nameof(callback), $"Can't register the '{name}' command because without a callback.");

        // ensure uniqueness
        if (this.Commands.ContainsKey(name))
            throw new ArgumentException(nameof(callback), $"Can't register the '{name}' command because there's already a command with that name.");

        // add command
        this.Commands.Add(name, new Command(mod, name, documentation, callback, getCompletions));
        return this;
    }

    /// <summary>Add a console command.</summary>
    /// <param name="command">the SMAPI console command to add.</param>
    /// <param name="monitor">Writes messages to the console.</param>
    /// <exception cref="ArgumentException">There's already a command with that name.</exception>
    public CommandManager Add(IInternalCommand command, IMonitor monitor)
    {
        return this.Add(
            mod: null,
            name: command.Name,
            documentation: command.Description,
            callback: (_, args) => command.HandleCommand(args, monitor),
            getCompletions: (_, args, argIndex) => command.GetCompletions(args, argIndex)
        );
    }

    /// <summary>Get a command by its unique name.</summary>
    /// <param name="name">The command name.</param>
    /// <returns>Returns the matching command, or <c>null</c> if not found.</returns>
    public Command? Get(string? name)
    {
        name = this.GetNormalizedName(name)!;
        if (string.IsNullOrWhiteSpace(name))
            return null;

        this.Commands.TryGetValue(name, out Command? command);
        return command;
    }

    /// <summary>Get all registered commands.</summary>
    public IEnumerable<Command> GetAll()
    {
        return this.Commands
            .Values
            .OrderBy(p => p.Name);
    }

    /// <summary>Try to parse a raw line of user input into an executable command.</summary>
    /// <param name="input">The raw user input.</param>
    /// <param name="name">The parsed command name.</param>
    /// <param name="args">The parsed command arguments.</param>
    /// <param name="command">The command which can handle the input.</param>
    /// <param name="screenId">The screen ID on which to run the command.</param>
    /// <returns>Returns true if the input was successfully parsed and matched to a command; else false.</returns>
    public bool TryParse(string? input, [NotNullWhen(true)] out string? name, [NotNullWhen(true)] out string[]? args, [NotNullWhen(true)] out Command? command, out int screenId)
    {
        // ignore if blank
        if (string.IsNullOrWhiteSpace(input))
        {
            name = null;
            args = null;
            command = null;
            screenId = 0;
            return false;
        }

        // parse input
        args = ArgUtility.SplitBySpaceQuoteAware(input);
        name = this.GetNormalizedName(args[0])!;
        args = ArgUtility.GetSubsetOf(args, startAt: 1);

        // get screen ID argument
        screenId = 0;
        for (int i = 0; i < args.Length; i++)
        {
            // consume arg & set screen ID
            if (this.TryParseScreenId(args[i], out int rawScreenId, out string? error))
            {
                args = args.Take(i).Concat(args.Skip(i + 1)).ToArray();
                screenId = rawScreenId;
                continue;
            }

            // invalid screen arg
            if (error != null)
            {
                this.Monitor.Log(error, LogLevel.Error);
                command = null;
                return false;
            }
        }

        // get command
        return this.Commands.TryGetValue(name, out command);
    }

    /// <summary>Try to locate the span of a registered command name at the start of raw input.</summary>
    /// <param name="input">The raw user input.</param>
    /// <param name="start">The zero-based start index of the command name.</param>
    /// <param name="length">The length of the command name.</param>
    /// <returns>Returns whether the input starts with a registered command name.</returns>
    public bool TryGetCommandNameSpan(string? input, out int start, out int length)
    {
        start = 0;
        length = 0;

        if (string.IsNullOrWhiteSpace(input))
            return false;

        int i = 0;
        while (i < input.Length && char.IsWhiteSpace(input[i]))
            i++;

        int tokenStart = i;
        while (i < input.Length && !char.IsWhiteSpace(input[i]))
            i++;

        int tokenLength = i - tokenStart;
        if (tokenLength <= 0)
            return false;

        string candidate = input.Substring(tokenStart, tokenLength);
        if (!this.Commands.ContainsKey(candidate))
            return false;

        start = tokenStart;
        length = tokenLength;
        return true;
    }

    /// <summary>Get the span to replace when triggering tab-completion at the given caret position.</summary>
    /// <param name="input">The raw user input.</param>
    /// <param name="caret">The zero-based caret position in <paramref name="input"/>.</param>
    public (int Start, int Length) GetCompletionSpan(string input, int caret)
    {
        caret = Math.Clamp(caret, 0, input.Length);
        List<InputToken> tokens = CommandManager.TokenizeForCompletion(input);

        foreach (InputToken token in tokens)
        {
            if (caret >= token.Start && caret <= token.End)
                return (token.Start, token.End - token.Start);
        }

        return (caret, 0);
    }

    /// <summary>Get whether there is at least one fuzzy-matching completion for the given input and caret position.</summary>
    /// <param name="input">The raw user input.</param>
    /// <param name="caret">The zero-based caret position in <paramref name="input"/>.</param>
    public bool HasMatchingCompletions(string input, int caret)
    {
        return this.GetCompletions(input, caret, filterByPrefix: true).Count > 0;
    }

    /// <summary>Get tab-completion suggestions for the given input and caret position.</summary>
    /// <param name="input">The raw user input.</param>
    /// <param name="caret">The zero-based caret position in <paramref name="input"/>.</param>
    /// <param name="filterByPrefix">Whether to filter suggestions by fuzzy-matching the current token. Set to <c>false</c> when the caller (such as PrettyPrompt) caches and filters the full candidate list itself.</param>
    public IReadOnlyList<CommandCompletionItem> GetCompletions(string input, int caret, bool filterByPrefix = true)
    {
        caret = Math.Clamp(caret, 0, input.Length);
        List<InputToken> tokens = CommandManager.TokenizeForCompletion(input);

        // find which token the caret is on (or if starting a new token after whitespace)
        int activeTokenIndex = -1;
        for (int i = 0; i < tokens.Count; i++)
        {
            if (caret >= tokens[i].Start && caret <= tokens[i].End)
            {
                activeTokenIndex = i;
                break;
            }
        }

        bool isNewToken = activeTokenIndex == -1;
        if (isNewToken)
        {
            // count how many tokens appear strictly before caret
            activeTokenIndex = tokens.Count(p => p.End < caret);
        }

        string currentPrefix = isNewToken
            ? string.Empty
            : CommandManager.Unquote(tokens[activeTokenIndex].RawValue);

        // completing command name
        if (activeTokenIndex == 0)
        {
            var matchedCommands = this.Commands.Values
                .Select(cmd => (Command: cmd, Score: filterByPrefix ? CommandManager.GetFuzzyMatchScore(cmd.Name, currentPrefix) : 0))
                .Where(p => p.Score != int.MinValue)
                .OrderByDescending(p => p.Score)
                .ThenBy(p => p.Command.Name, StringComparer.OrdinalIgnoreCase);

            return matchedCommands
                .Select(p => new CommandCompletionItem(
                    ReplacementText: p.Command.Name,
                    DisplayText: p.Command.Name,
                    Description: CommandManager.GetFirstLine(p.Command.Documentation)
                ))
                .ToArray();
        }

        // completing command arguments
        string commandName = CommandManager.Unquote(tokens[0].RawValue);
        if (!this.Commands.TryGetValue(commandName, out Command? command))
            return [];

        List<(CommandCompletionItem Item, int Score)> results = [];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

        // screen=X completion
        int[] activeScreenIds = Context.ActiveScreenIds.ToArray();
        if (currentPrefix.StartsWith("screen=", StringComparison.OrdinalIgnoreCase) || activeScreenIds.Length > 1)
        {
            foreach (int screenId in activeScreenIds)
            {
                string suggestion = $"screen={screenId}";
                int score = filterByPrefix ? CommandManager.GetFuzzyMatchScore(suggestion, currentPrefix) : 0;
                if (score != int.MinValue && seen.Add(suggestion))
                    results.Add((new CommandCompletionItem(suggestion, suggestion, $"Run this command on split-screen instance {screenId}."), score));
            }
        }

        // command-specific argument completions
        if (command.GetCompletions != null)
        {
            // build args excluding screen=X tokens prior to the active token
            List<string> argList = [];
            for (int i = 1; i < activeTokenIndex; i++)
            {
                string val = CommandManager.Unquote(tokens[i].RawValue);
                if (!val.StartsWith("screen=", StringComparison.OrdinalIgnoreCase))
                    argList.Add(val);
            }

            int argIndex = argList.Count;
            argList.Add(currentPrefix);

            try
            {
                IEnumerable<string>? rawSuggestions = command.GetCompletions(command.Name, argList.ToArray(), argIndex);
                if (rawSuggestions != null)
                {
                    foreach (string raw in rawSuggestions)
                    {
                        if (string.IsNullOrWhiteSpace(raw))
                            continue;

                        int score = filterByPrefix ? CommandManager.GetFuzzyMatchScore(raw, currentPrefix) : 0;
                        if (score == int.MinValue)
                            continue;

                        if (!seen.Add(raw))
                            continue;

                        string replacement = raw.Any(char.IsWhiteSpace)
                            ? $"\"{raw}\""
                            : raw;

                        results.Add((new CommandCompletionItem(replacement, raw, Description: null), score));
                    }
                }
            }
            catch
            {
                // never crash the console thread due to a mod's completion callback
            }
        }

        return results
            .OrderByDescending(p => p.Score)
            .ThenBy(p => p.Item.DisplayText, StringComparer.OrdinalIgnoreCase)
            .Select(p => p.Item)
            .ToArray();
    }


    /*********
    ** Private methods
    *********/
    /// <summary>Try to parse a 'screen=X' command argument, which specifies the screen that should receive the command.</summary>
    /// <param name="arg">The raw argument to parse.</param>
    /// <param name="screen">The parsed screen ID, if any.</param>
    /// <param name="error">The error which indicates an invalid screen ID, if applicable.</param>
    /// <returns>Returns whether the screen ID was parsed successfully.</returns>
    private bool TryParseScreenId(string arg, out int screen, out string? error)
    {
        screen = -1;
        error = null;

        // skip non-screen arg
        if (!arg.StartsWith("screen="))
            return false;

        // get screen ID
        string rawScreen = arg.Substring("screen=".Length);
        if (!int.TryParse(rawScreen, out screen))
        {
            error = $"invalid screen ID format: {rawScreen}";
            return false;
        }

        // validate ID
        if (!Context.HasScreenId(screen))
        {
            error = $"there's no active screen with ID {screen}. Active screen IDs: {string.Join(", ", Context.ActiveScreenIds)}.";
            return false;
        }

        return true;
    }

    /// <summary>Get a normalized command name.</summary>
    /// <param name="name">The command name.</param>
    private string? GetNormalizedName(string? name)
    {
        name = name?.Trim().ToLower();
        return !string.IsNullOrWhiteSpace(name)
            ? name
            : null;
    }

    /// <summary>Split a raw input line into quote-aware tokens with character offsets.</summary>
    /// <param name="input">The raw user input.</param>
    private static List<InputToken> TokenizeForCompletion(string input)
    {
        List<InputToken> tokens = [];
        int i = 0;

        while (i < input.Length)
        {
            while (i < input.Length && char.IsWhiteSpace(input[i]))
                i++;

            if (i >= input.Length)
                break;

            int start = i;
            bool inQuotes = false;
            while (i < input.Length)
            {
                char ch = input[i];
                if (ch == '"')
                    inQuotes = !inQuotes;
                else if (char.IsWhiteSpace(ch) && !inQuotes)
                    break;

                i++;
            }

            tokens.Add(new InputToken(start, i, input.Substring(start, i - start)));
        }

        return tokens;
    }

    /// <summary>Compute a fuzzy match score for a completion candidate against the user's query.</summary>
    /// <param name="candidate">The candidate command or argument value.</param>
    /// <param name="query">The user's partial input.</param>
    /// <returns>Returns <see cref="int.MinValue"/> if <paramref name="candidate"/> does not fuzzy-match <paramref name="query"/>; <c>0</c> if <paramref name="query"/> is empty; or a positive score where higher values indicate better matches.</returns>
    public static int GetFuzzyMatchScore(string candidate, string query)
    {
        if (string.IsNullOrEmpty(query))
            return 0;

        if (string.IsNullOrEmpty(candidate) || query.Length > candidate.Length)
            return int.MinValue;

        // exact / prefix / substring bonuses
        int score = 100;
        if (candidate.Equals(query, StringComparison.OrdinalIgnoreCase))
            score += 10000;
        else if (candidate.StartsWith(query, StringComparison.OrdinalIgnoreCase))
            score += 5000;
        else if (candidate.Contains(query, StringComparison.OrdinalIgnoreCase))
            score += 2000;

        // greedy subsequence match with bonuses for consecutive characters and word boundaries
        int queryIndex = 0;
        int lastMatchIndex = -2;
        for (int i = 0; i < candidate.Length && queryIndex < query.Length; i++)
        {
            if (char.ToLowerInvariant(candidate[i]) != char.ToLowerInvariant(query[queryIndex]))
                continue;

            if (i == lastMatchIndex + 1)
                score += 15; // consecutive match bonus

            if (i == 0 || candidate[i - 1] is '_' or '-' or ' ' or '/' or '=')
                score += 25; // word boundary bonus

            lastMatchIndex = i;
            queryIndex++;
        }

        if (queryIndex < query.Length)
            return int.MinValue;

        // prefer shorter/tighter matches
        score -= candidate.Length - query.Length;
        return Math.Max(1, score);
    }

    /// <summary>Remove surrounding quotes from a token for prefix matching.</summary>
    /// <param name="rawToken">The raw token text.</param>
    internal static string Unquote(string rawToken)
    {
        if (rawToken.Length >= 2 && rawToken[0] == '"' && rawToken[^1] == '"')
            return rawToken.Substring(1, rawToken.Length - 2);
        if (rawToken.StartsWith('"'))
            return rawToken.Substring(1);
        return rawToken;
    }

    /// <summary>Get the first non-empty line of a documentation string.</summary>
    /// <param name="documentation">The full documentation text.</param>
    private static string? GetFirstLine(string? documentation)
    {
        if (string.IsNullOrWhiteSpace(documentation))
            return null;

        foreach (string line in documentation.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            string trimmed = line.Trim();
            if (trimmed.Length > 0)
                return trimmed;
        }

        return null;
    }

    /// <summary>A parsed token within raw console input.</summary>
    /// <param name="Start">The inclusive start offset in the input string.</param>
    /// <param name="End">The exclusive end offset in the input string.</param>
    /// <param name="RawValue">The raw substring in the input.</param>
    private readonly record struct InputToken(int Start, int End, string RawValue);
}

/// <summary>A tab-completion suggestion for a console command or argument.</summary>
/// <param name="ReplacementText">The text to insert when selected (quoted if it contains whitespace).</param>
/// <param name="DisplayText">The human-readable text shown in the completion list.</param>
/// <param name="Description">An optional short description shown alongside the completion item.</param>
internal readonly record struct CommandCompletionItem(string ReplacementText, string DisplayText, string? Description);
