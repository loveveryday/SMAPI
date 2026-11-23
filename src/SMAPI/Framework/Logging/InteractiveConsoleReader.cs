using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using PrettyPrompt;
using PrettyPrompt.Consoles;
using PrettyPrompt.Highlighting;
using StardewModdingAPI.Internal.ConsoleWriting;

namespace StardewModdingAPI.Framework.Logging;

/// <summary>Reads console command input using PrettyPrompt (with bash-like prompt, cyan command highlighting, Emacs key bindings, and tab-completion) or falls back to <see cref="Console.ReadLine"/>.</summary>
internal class InteractiveConsoleReader
{
    /*********
    ** Fields
    *********/
    /// <summary>The bash-like prompt prefix shown before user commands.</summary>
    private const string PromptText = "smapi$ ";

    /// <summary>Manages registered console commands.</summary>
    private readonly CommandManager CommandManager;

    /// <summary>The console writer used for SMAPI log output.</summary>
    private readonly ColorfulConsoleWriter ConsoleWriter;

    /// <summary>The SMAPI monitor for logging diagnostics.</summary>
    private readonly Monitor Monitor;

    /// <summary>Whether the interactive console is enabled in SMAPI's config.</summary>
    private readonly bool EnableInteractive;

    /// <summary>Whether console output is enabled (false when <c>--no-terminal</c> is used).</summary>
    private readonly bool WriteToConsole;


    /*********
    ** Public methods
    *********/
    /// <summary>Construct an instance.</summary>
    /// <param name="commandManager">Manages registered console commands.</param>
    /// <param name="consoleWriter">The console writer used for SMAPI log output.</param>
    /// <param name="monitor">The SMAPI monitor for logging diagnostics.</param>
    /// <param name="enableInteractive">Whether the enhanced interactive console is enabled in config.</param>
    /// <param name="writeToConsole">Whether writing to the console is enabled.</param>
    public InteractiveConsoleReader(CommandManager commandManager, ColorfulConsoleWriter consoleWriter, Monitor monitor, bool enableInteractive, bool writeToConsole)
    {
        this.CommandManager = commandManager;
        this.ConsoleWriter = consoleWriter;
        this.Monitor = monitor;
        this.EnableInteractive = enableInteractive;
        this.WriteToConsole = writeToConsole;
    }

    /// <summary>Run the blocking console input loop, automatically falling back to <see cref="Console.ReadLine"/> if interactive mode is disabled or unsupported.</summary>
    /// <param name="onInput">The callback invoked with each non-blank submitted command line.</param>
    public void RunInputLoop(Action<string> onInput)
    {
        if (!this.CanUseInteractiveConsole())
        {
            this.RunBasicInputLoop(onInput);
            return;
        }

        CoordinatedConsole? coordinatedConsole = null;
        try
        {
            coordinatedConsole = new CoordinatedConsole(
                promptText: InteractiveConsoleReader.PromptText,
                getPromptColorCode: () => this.GetPromptColor()?.GetEscapeSequence(),
                getCommandColorCode: () => this.GetCommandColor()?.GetEscapeSequence(),
                tryGetCommandSpan: input => this.CommandManager.TryGetCommandNameSpan(input, out int start, out int length)
                    ? (true, start, length)
                    : (false, 0, 0)
            );

            // attach log interceptor so background logs cleanly preserve and redraw the prompt line
            this.ConsoleWriter.WriteInterceptor = coordinatedConsole.WriteLogLine;

            string? historyPath = this.GetPersistentHistoryPath();
            List<string> history = InteractiveConsoleReader.LoadHistoryEntries(historyPath);
            string? prettyPromptHistoryCachePath = InteractiveConsoleReader.WritePrettyPromptHistoryCache(history);

            var emacsHandler = new EmacsKeyBindingHandler(() => history);
            var callbacks = new SmapiPromptCallbacks(
                commandManager: this.CommandManager,
                coordinatedConsole: coordinatedConsole,
                emacsHandler: emacsHandler,
                getCommandHighlightColor: this.GetCommandColor,
                useInteractiveHistorySearch: true
            );

            var historyOverlay = new HistorySearchOverlay(
                console: coordinatedConsole,
                useColor: this.ConsoleWriter.IsColorEnabled,
                isLightBackground: this.ConsoleWriter.ActiveColorScheme == MonitorColorScheme.LightBackground
            );

            AnsiColor? promptColor = this.GetPromptColor();
            FormattedString formattedPrompt = promptColor is not null
                ? new FormattedString(InteractiveConsoleReader.PromptText, new FormatSpan(0, InteractiveConsoleReader.PromptText.Length, promptColor.Value))
                : new FormattedString(InteractiveConsoleReader.PromptText);

            var keyBindings = new KeyBindings(
                commitCompletion: new KeyPressPatterns(
                    new KeyPressPattern(ConsoleKey.Enter),
                    new KeyPressPattern(ConsoleKey.Tab)
                ),
                triggerCompletionList: new KeyPressPatterns(
                    new KeyPressPattern(ConsoleModifiers.Control, ConsoleKey.Spacebar)
                ),
                newLine: new KeyPressPatterns(),
                submitPrompt: new KeyPressPatterns(
                    new KeyPressPattern(ConsoleKey.Enter)
                ),
                historyPrevious: new KeyPressPatterns(
                    new KeyPressPattern(ConsoleKey.UpArrow)
                ),
                historyNext: new KeyPressPatterns(
                    new KeyPressPattern(ConsoleKey.DownArrow)
                )
            );

            var configuration = new PromptConfiguration(
                keyBindings: keyBindings,
                prompt: formattedPrompt,
                minCompletionItemsCount: 5,
                maxCompletionItemsCount: 9
            );

            Prompt prompt = new(
                persistentHistoryFilepath: prettyPromptHistoryCachePath,
                callbacks: callbacks,
                console: coordinatedConsole,
                configuration: configuration
            );

            while (true)
            {
                // reserve vertical space before ReadLineAsync initializes CodePane.TopCoordinate so the completion box is never squished
                coordinatedConsole.EnsureCompletionSpace(minRows: 8);
                callbacks.ResetLineState();
                coordinatedConsole.SetPromptActive(true);
                PromptResult result;
                try
                {
                    result = prompt.ReadLineAsync().GetAwaiter().GetResult();
                }
                finally
                {
                    coordinatedConsole.SetPromptActive(false);
                }

                // Handle Ctrl+R interactive Atuin-style history search overlay
                if (!result.IsSuccess && callbacks.HistorySearchRequested)
                {
                    // clear the cancelled prompt line cleanly before drawing the overlay
                    coordinatedConsole.RunExclusive(() => Console.Write("\x1b[1A\r\x1b[0J"));

                    HistorySearchResult searchResult = historyOverlay.Show(history, callbacks.SavedInputBeforeHistorySearch);
                    switch (searchResult.Action)
                    {
                        case HistorySearchAction.Execute when !string.IsNullOrWhiteSpace(searchResult.Command):
                            string cmd = searchResult.Command;
                            this.WriteSubmittedPromptLine(coordinatedConsole, cmd);
                            InteractiveConsoleReader.RecordHistoryEntry(history, cmd);
                            InteractiveConsoleReader.SaveHistoryEntries(historyPath, history);
                            onInput(cmd);
                            break;

                        case HistorySearchAction.Edit when !string.IsNullOrEmpty(searchResult.Command):
                            coordinatedConsole.EnqueueSyntheticText(searchResult.Command);
                            break;

                        case HistorySearchAction.Cancel:
                            if (!string.IsNullOrEmpty(callbacks.SavedInputBeforeHistorySearch))
                                coordinatedConsole.EnqueueSyntheticText(callbacks.SavedInputBeforeHistorySearch);
                            break;
                    }
                    continue;
                }

                // Ctrl+C cancels current line without exiting the game
                if (!result.IsSuccess)
                    continue;

                string input = result.Text;
                if (string.IsNullOrWhiteSpace(input))
                    continue;

                InteractiveConsoleReader.RecordHistoryEntry(history, input);
                InteractiveConsoleReader.SaveHistoryEntries(historyPath, history);
                onInput(input);
            }
        }
        catch (Exception ex)
        {
            if (coordinatedConsole != null)
            {
                coordinatedConsole.SetPromptActive(false);
                this.ConsoleWriter.WriteInterceptor = null;
            }

            this.Monitor.Log($"Interactive console failed to initialize; falling back to basic input. Technical details: {ex.Message}", LogLevel.Trace);
            this.RunBasicInputLoop(onInput);
        }
    }


    /*********
    ** Private methods
    *********/
    /// <summary>Get whether the current environment supports the interactive console.</summary>
    private bool CanUseInteractiveConsole()
    {
        return
            this.EnableInteractive
            && this.WriteToConsole
            && !Console.IsInputRedirected
            && !Console.IsOutputRedirected;
    }

    /// <summary>Run the fallback basic <see cref="Console.ReadLine"/> input loop.</summary>
    /// <param name="onInput">The callback invoked with each non-blank submitted command line.</param>
    private void RunBasicInputLoop(Action<string> onInput)
    {
        while (true)
        {
            string? input = Console.ReadLine();
            if (string.IsNullOrWhiteSpace(input))
                continue;

            onInput(input);
        }
    }

    /// <summary>Get the theme-aware ANSI color for the bash-like prompt.</summary>
    private AnsiColor? GetPromptColor()
    {
        if (!this.ConsoleWriter.IsColorEnabled)
            return null;

        return this.ConsoleWriter.ActiveColorScheme switch
        {
            MonitorColorScheme.LightBackground => AnsiColor.Green,
            MonitorColorScheme.DarkBackground => AnsiColor.BrightGreen,
            _ => null
        };
    }

    /// <summary>Get the theme-aware ANSI color for highlighted command names (cyan on dark terminals, dark cyan on light terminals).</summary>
    private AnsiColor? GetCommandColor()
    {
        if (!this.ConsoleWriter.IsColorEnabled)
            return null;

        return this.ConsoleWriter.ActiveColorScheme switch
        {
            MonitorColorScheme.LightBackground => AnsiColor.Cyan,
            MonitorColorScheme.DarkBackground => AnsiColor.BrightCyan,
            _ => null
        };
    }

    /// <summary>Get the file path for persistent console command history, creating its directory if needed.</summary>
    /// <remarks>Stored in <see cref="Constants.DataPath"/> rather than <see cref="Constants.LogDir"/> because <c>SCore.PurgeNormalLogs</c> deletes <c>SMAPI-*</c> files in <see cref="Constants.LogDir"/> on every launch.</remarks>
    private string? GetPersistentHistoryPath()
    {
        try
        {
            Directory.CreateDirectory(Constants.DataPath);
            string path = Path.Combine(Constants.DataPath, "SMAPI-console-history.txt");

            // migrate any legacy file from ErrorLogs if present
            string legacyPath = Path.Combine(Constants.LogDir, "SMAPI-console-history.txt");
            if (!File.Exists(path) && File.Exists(legacyPath))
                File.Move(legacyPath, path);

            return path;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Load persisted history entries from disk (supports both plain text lines and legacy PrettyPrompt Base64 lines).</summary>
    /// <param name="historyPath">The persistent history file path, if any.</param>
    internal static List<string> LoadHistoryEntries(string? historyPath)
    {
        List<string> entries = [];
        if (string.IsNullOrWhiteSpace(historyPath) || !File.Exists(historyPath))
            return entries;

        try
        {
            foreach (string rawLine in File.ReadLines(historyPath))
            {
                string trimmed = rawLine.Trim();
                if (trimmed.Length == 0)
                    continue;

                string decoded = InteractiveConsoleReader.TryDecodeLegacyBase64Line(trimmed) ?? trimmed;
                InteractiveConsoleReader.RecordHistoryEntry(entries, decoded);
            }
        }
        catch
        {
            // ignore unreadable history file
        }

        return entries;
    }

    /// <summary>Persist all history entries immediately to <paramref name="historyPath"/> as readable UTF-8 lines (up to the most recent 1000 entries).</summary>
    /// <param name="historyPath">The persistent history file path, if any.</param>
    /// <param name="history">The in-memory history list ordered from oldest to newest.</param>
    internal static void SaveHistoryEntries(string? historyPath, List<string> history)
    {
        if (string.IsNullOrWhiteSpace(historyPath))
            return;

        const int maxEntries = 1000;
        if (history.Count > maxEntries)
            history.RemoveRange(0, history.Count - maxEntries);

        try
        {
            File.WriteAllLines(historyPath, history, Encoding.UTF8);
        }
        catch
        {
            // ignore persistence errors
        }
    }

    /// <summary>Write a Base64-encoded history seed file for PrettyPrompt's own Up/Down history navigation so PrettyPrompt never overwrites our main history file.</summary>
    /// <param name="history">The loaded history entries.</param>
    private static string? WritePrettyPromptHistoryCache(List<string> history)
    {
        try
        {
            string cachePath = Path.Combine(Path.GetTempPath(), "SMAPI-console-history.cache");
            List<string> encodedLines = new(history.Count);
            foreach (string entry in history)
                encodedLines.Add(Convert.ToBase64String(Encoding.UTF8.GetBytes(entry)));

            File.WriteAllLines(cachePath, encodedLines, Encoding.UTF8);
            return cachePath;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Try to decode a legacy PrettyPrompt Base64-encoded history line; returns <c>null</c> if the line is already plain text.</summary>
    /// <param name="line">The trimmed line from disk.</param>
    private static string? TryDecodeLegacyBase64Line(string line)
    {
        if (line.Length < 4 || line.Length % 4 != 0 || line.Contains(' ') || line.Contains('_'))
            return null;

        try
        {
            byte[] bytes = Convert.FromBase64String(line);
            string decoded = Encoding.UTF8.GetString(bytes);

            if (decoded.Length == 0 || Convert.ToBase64String(bytes) != line)
                return null;

            foreach (char ch in decoded)
            {
                if (char.IsControl(ch))
                    return null;
            }

            return decoded;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Write a submitted command line to the console with prompt and cyan command highlighting (used when executing directly from <c>Ctrl+R</c> history search).</summary>
    /// <param name="coordinatedConsole">The coordinated console.</param>
    /// <param name="command">The command line being executed.</param>
    private void WriteSubmittedPromptLine(CoordinatedConsole coordinatedConsole, string command)
    {
        const string resetAnsi = "\x1b[0m";
        string? promptColor = this.GetPromptColor()?.GetEscapeSequence();
        string? commandColor = this.GetCommandColor()?.GetEscapeSequence();

        StringBuilder sb = new();
        if (promptColor != null)
            sb.Append(promptColor).Append(InteractiveConsoleReader.PromptText).Append(resetAnsi);
        else
            sb.Append(InteractiveConsoleReader.PromptText);

        if (commandColor != null && this.CommandManager.TryGetCommandNameSpan(command, out int start, out int length))
        {
            if (start > 0)
                sb.Append(command, 0, start);
            sb.Append(commandColor).Append(command, start, length).Append(resetAnsi);
            if (start + length < command.Length)
                sb.Append(command, start + length, command.Length - (start + length));
        }
        else
        {
            sb.Append(command);
        }

        sb.Append("\r\n");
        coordinatedConsole.RunExclusive(() => Console.Write(sb.ToString()));
    }

    /// <summary>Record a submitted command in the in-memory history list (deduplicating consecutive/older duplicates so the most recent stays at the end).</summary>
    /// <param name="history">The in-memory history list.</param>
    /// <param name="command">The submitted command line.</param>
    private static void RecordHistoryEntry(List<string> history, string command)
    {
        string trimmed = command.Trim();
        if (trimmed.Length == 0)
            return;

        history.RemoveAll(p => string.Equals(p, trimmed, StringComparison.Ordinal));
        history.Add(trimmed);
    }
}
