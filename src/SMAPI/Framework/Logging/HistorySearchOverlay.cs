using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace StardewModdingAPI.Framework.Logging;

/// <summary>An Atuin-style inline fuzzy history search TUI overlay for the SMAPI console.</summary>
internal class HistorySearchOverlay
{
    /*********
    ** Fields
    *********/
    /// <summary>The maximum number of history items displayed at once.</summary>
    private const int MaxVisibleItems = 7;

    /// <summary>The coordinated console used for synchronized rendering and key input.</summary>
    private readonly CoordinatedConsole Console;

    /// <summary>Whether ANSI color formatting is enabled.</summary>
    private readonly bool UseColor;

    /// <summary>Whether the terminal uses a light background theme.</summary>
    private readonly bool IsLightBackground;

    /// <summary>The number of lines currently rendered by the overlay (from the anchor top line downward).</summary>
    private int RenderedHeight;

    /// <summary>The row offset from the overlay top line where the cursor currently rests (the Search input row).</summary>
    private int CursorRowOffset;


    /*********
    ** Public methods
    *********/
    /// <summary>Construct an instance.</summary>
    /// <param name="console">The coordinated console.</param>
    /// <param name="useColor">Whether ANSI color output is enabled.</param>
    /// <param name="isLightBackground">Whether the terminal uses a light background theme.</param>
    public HistorySearchOverlay(CoordinatedConsole console, bool useColor, bool isLightBackground)
    {
        this.Console = console;
        this.UseColor = useColor;
        this.IsLightBackground = isLightBackground;
    }

    /// <summary>Display the interactive history search overlay until the user runs, edits, or cancels.</summary>
    /// <param name="history">The command history ordered from oldest to newest.</param>
    /// <param name="initialQuery">The initial search query (e.g. text already typed at the prompt).</param>
    public HistorySearchResult Show(IReadOnlyList<string> history, string initialQuery)
    {
        var state = new HistorySearchState(history, initialQuery, HistorySearchOverlay.MaxVisibleItems);

        // reserve vertical space for the overlay (header + items + divider + search + bottom border)
        int overlayHeight = HistorySearchOverlay.MaxVisibleItems + 4;
        this.Console.EnsureCompletionSpace(minRows: overlayHeight);

        this.Console.SetCustomOverlay(
            clearOverlay: this.ClearOverlayUnlocked,
            redrawOverlay: () => this.RenderOverlayUnlocked(state)
        );

        try
        {
            this.Console.RunExclusive(() => this.RenderOverlayUnlocked(state));

            while (true)
            {
                ConsoleKeyInfo key = this.Console.ReadKey(intercept: true);
                HistorySearchResult? outcome = state.HandleKey(key);
                if (outcome != null)
                    return outcome.Value;

                this.Console.RunExclusive(() =>
                {
                    this.ClearOverlayUnlocked();
                    this.RenderOverlayUnlocked(state);
                });
            }
        }
        finally
        {
            this.Console.RunExclusive(this.ClearOverlayUnlocked);
            this.Console.SetCustomOverlay(null, null);
        }
    }


    /*********
    ** Private methods
    *********/
    /// <summary>Clear the rendered overlay and move the cursor back to its anchor line. Caller must hold console lock.</summary>
    private void ClearOverlayUnlocked()
    {
        if (this.RenderedHeight <= 0)
            return;

        StringBuilder sb = new();
        if (this.CursorRowOffset > 0)
            sb.Append($"\x1b[{this.CursorRowOffset}A");
        sb.Append("\r\x1b[0J");

        System.Console.Write(sb.ToString());
        this.RenderedHeight = 0;
        this.CursorRowOffset = 0;
    }

    /// <summary>Render the Atuin-style inline history search box. Caller must hold console lock.</summary>
    /// <param name="state">The current search state.</param>
    private void RenderOverlayUnlocked(HistorySearchState state)
    {
        int width = Math.Clamp(this.Console.BufferWidth - 2, 44, 96);
        int innerWidth = width - 2;

        string borderColor = this.UseColor ? (this.IsLightBackground ? "\x1b[34m" : "\x1b[90m") : "";
        string accentColor = this.UseColor ? (this.IsLightBackground ? "\x1b[36m" : "\x1b[96m") : "";
        string selectedBg = this.UseColor ? (this.IsLightBackground ? "\x1b[47;30m" : "\x1b[48;5;237;97m") : "";
        string matchHighlight = this.UseColor ? (this.IsLightBackground ? "\x1b[1;34m" : "\x1b[1;93m") : "";
        string dimColor = this.UseColor ? "\x1b[90m" : "";
        string reset = this.UseColor ? "\x1b[0m" : "";

        StringBuilder sb = new();

        // 1. Top border with title and match counter
        string title = " SMAPI History ";
        string counter = $" {state.Matches.Count}/{state.TotalCount} ";
        int dashCount = Math.Max(1, innerWidth - title.Length - counter.Length - 2);
        sb.Append(borderColor)
            .Append("╭─")
            .Append(accentColor).Append(title).Append(borderColor)
            .Append(new string('─', dashCount))
            .Append(dimColor).Append(counter).Append(borderColor)
            .Append("─╮")
            .Append(reset)
            .Append("\r\n");

        // 2. Match list rows (fixed height for flicker-free rendering)
        int visibleCount = HistorySearchOverlay.MaxVisibleItems;
        for (int row = 0; row < visibleCount; row++)
        {
            int itemIndex = state.ScrollOffset + row;
            sb.Append(borderColor).Append('│').Append(reset);

            if (itemIndex < state.Matches.Count)
            {
                string entry = state.Matches[itemIndex];
                bool isSelected = itemIndex == state.SelectedIndex;
                string prefix = isSelected ? " > " : "   ";
                int maxTextWidth = Math.Max(1, innerWidth - prefix.Length - 1);
                string display = HistorySearchOverlay.Truncate(entry, maxTextWidth);
                int padRight = Math.Max(0, innerWidth - prefix.Length - display.Length);

                if (isSelected)
                {
                    sb.Append(selectedBg)
                        .Append(accentColor).Append(prefix).Append(reset).Append(selectedBg);
                    HistorySearchOverlay.AppendHighlightedText(sb, display, state.Query, matchHighlight, selectedBg);
                    sb.Append(new string(' ', padRight)).Append(reset);
                }
                else
                {
                    sb.Append(prefix);
                    HistorySearchOverlay.AppendHighlightedText(sb, display, state.Query, matchHighlight, reset);
                    sb.Append(new string(' ', padRight));
                }
            }
            else if (row == 0 && state.Matches.Count == 0)
            {
                string emptyMsg = state.TotalCount == 0 ? "   (no command history yet)" : "   (no matching history)";
                string clipped = HistorySearchOverlay.Truncate(emptyMsg, innerWidth);
                sb.Append(dimColor).Append(clipped).Append(new string(' ', Math.Max(0, innerWidth - clipped.Length))).Append(reset);
            }
            else
            {
                sb.Append(new string(' ', innerWidth));
            }

            sb.Append(borderColor).Append('│').Append(reset).Append("\r\n");
        }

        // 3. Middle divider with keybind hints
        string hints = " [Enter] run  [Tab] edit  [Ctrl+P/N] move  [Esc] cancel ";
        if (hints.Length > innerWidth - 2)
            hints = " [Enter] run  [Tab] edit  [Esc] cancel ";
        int hintDash = Math.Max(0, innerWidth - hints.Length - 1);
        sb.Append(borderColor)
            .Append("├─")
            .Append(dimColor).Append(hints).Append(borderColor)
            .Append(new string('─', hintDash))
            .Append("┤")
            .Append(reset)
            .Append("\r\n");

        // 4. Search input row
        string searchPrompt = " Search> ";
        int maxQueryWidth = Math.Max(1, innerWidth - searchPrompt.Length - 1);
        string visibleQuery = state.Query.Length > maxQueryWidth
            ? state.Query.Substring(state.Query.Length - maxQueryWidth)
            : state.Query;
        int queryPad = Math.Max(0, innerWidth - searchPrompt.Length - visibleQuery.Length);

        sb.Append(borderColor).Append('│').Append(reset)
            .Append(accentColor).Append(searchPrompt).Append(reset)
            .Append(visibleQuery)
            .Append(new string(' ', queryPad))
            .Append(borderColor).Append('│').Append(reset)
            .Append("\r\n");

        // 5. Bottom border
        sb.Append(borderColor)
            .Append('╰')
            .Append(new string('─', innerWidth))
            .Append('╯')
            .Append(reset);

        // Move cursor up 1 line (from bottom border) onto the Search> input line right after the query
        int cursorCol = 1 + searchPrompt.Length + visibleQuery.Length;
        sb.Append($"\x1b[1A\r\x1b[{cursorCol}C");

        System.Console.Write(sb.ToString());
        this.RenderedHeight = visibleCount + 4;
        this.CursorRowOffset = visibleCount + 2; // 0=top border, 1..N=items, N+1=divider, N+2=search row
    }

    /// <summary>Append <paramref name="text"/> while highlighting subsequence characters matched by <paramref name="query"/>.</summary>
    private static void AppendHighlightedText(StringBuilder sb, string text, string query, string highlightAnsi, string baseAnsi)
    {
        if (string.IsNullOrEmpty(query) || string.IsNullOrEmpty(highlightAnsi))
        {
            sb.Append(text);
            return;
        }

        int qIdx = 0;
        for (int i = 0; i < text.Length; i++)
        {
            char ch = text[i];
            if (qIdx < query.Length && char.ToLowerInvariant(ch) == char.ToLowerInvariant(query[qIdx]))
            {
                sb.Append(highlightAnsi).Append(ch).Append(baseAnsi);
                qIdx++;
            }
            else
            {
                sb.Append(ch);
            }
        }
    }

    /// <summary>Truncate a string to fit within <paramref name="maxLength"/> columns.</summary>
    private static string Truncate(string text, int maxLength)
    {
        if (text.Length <= maxLength)
            return text;
        return maxLength <= 1
            ? text.Substring(0, maxLength)
            : text.Substring(0, maxLength - 1) + "…";
    }
}

/// <summary>The state machine and fuzzy matcher for <see cref="HistorySearchOverlay"/>.</summary>
internal class HistorySearchState
{
    /*********
    ** Fields
    *********/
    /// <summary>The history entries ordered from newest to oldest.</summary>
    private readonly string[] HistoryNewestFirst;

    /// <summary>The maximum number of visible rows in the list.</summary>
    private readonly int MaxVisibleItems;


    /*********
    ** Accessors
    *********/
    /// <summary>The total number of unique history entries available.</summary>
    public int TotalCount => this.HistoryNewestFirst.Length;

    /// <summary>The current search query typed by the user.</summary>
    public string Query { get; private set; }

    /// <summary>The currently matching history entries, ranked by fuzzy score then recency.</summary>
    public IReadOnlyList<string> Matches { get; private set; } = [];

    /// <summary>The zero-based index of the selected match in <see cref="Matches"/>.</summary>
    public int SelectedIndex { get; private set; }

    /// <summary>The top visible index in <see cref="Matches"/>.</summary>
    public int ScrollOffset { get; private set; }

    /// <summary>The currently selected history command, or <c>null</c> if there are no matches.</summary>
    public string? SelectedItem => this.SelectedIndex >= 0 && this.SelectedIndex < this.Matches.Count
        ? this.Matches[this.SelectedIndex]
        : null;


    /*********
    ** Public methods
    *********/
    /// <summary>Construct an instance.</summary>
    /// <param name="historyOldestFirst">The command history ordered from oldest to newest.</param>
    /// <param name="initialQuery">The initial search query.</param>
    /// <param name="maxVisibleItems">The maximum visible items in the viewport.</param>
    public HistorySearchState(IReadOnlyList<string> historyOldestFirst, string initialQuery, int maxVisibleItems = 7)
    {
        this.HistoryNewestFirst = historyOldestFirst.Reverse().ToArray();
        this.MaxVisibleItems = Math.Max(1, maxVisibleItems);
        this.Query = initialQuery ?? string.Empty;
        this.RefreshMatches();
    }

    /// <summary>Process a single key press in the history search overlay.</summary>
    /// <param name="key">The key press.</param>
    /// <returns>Returns a <see cref="HistorySearchResult"/> if the overlay should close; otherwise <c>null</c> to stay open.</returns>
    public HistorySearchResult? HandleKey(ConsoleKeyInfo key)
    {
        bool ctrl = key.Modifiers == ConsoleModifiers.Control;

        // cancel
        if (key.Key == ConsoleKey.Escape || (ctrl && key.Key is ConsoleKey.C or ConsoleKey.G))
            return new HistorySearchResult(HistorySearchAction.Cancel, null);

        // execute selected command immediately
        if (key.Key == ConsoleKey.Enter)
        {
            return this.SelectedItem != null
                ? new HistorySearchResult(HistorySearchAction.Execute, this.SelectedItem)
                : new HistorySearchResult(HistorySearchAction.Cancel, null);
        }

        // insert selected command into prompt for editing
        if (key.Key is ConsoleKey.Tab or ConsoleKey.RightArrow)
        {
            return this.SelectedItem != null
                ? new HistorySearchResult(HistorySearchAction.Edit, this.SelectedItem)
                : new HistorySearchResult(HistorySearchAction.Cancel, null);
        }

        // navigate up (previous / newer)
        if (key.Key == ConsoleKey.UpArrow || (ctrl && key.Key == ConsoleKey.P))
        {
            this.MoveSelection(-1);
            return null;
        }

        // navigate down (next / older, including repeated Ctrl+R)
        if (key.Key == ConsoleKey.DownArrow || (ctrl && key.Key is ConsoleKey.N or ConsoleKey.R))
        {
            this.MoveSelection(1);
            return null;
        }

        // backspace / Ctrl+H
        if (key.Key == ConsoleKey.Backspace || (ctrl && key.Key == ConsoleKey.H))
        {
            if (this.Query.Length > 0)
            {
                this.Query = this.Query.Substring(0, this.Query.Length - 1);
                this.RefreshMatches();
            }
            return null;
        }

        // Ctrl+U: clear query
        if (ctrl && key.Key == ConsoleKey.U)
        {
            if (this.Query.Length > 0)
            {
                this.Query = string.Empty;
                this.RefreshMatches();
            }
            return null;
        }

        // Ctrl+W: delete previous word
        if (ctrl && key.Key == ConsoleKey.W)
        {
            if (this.Query.Length > 0)
            {
                int i = this.Query.Length;
                while (i > 0 && char.IsWhiteSpace(this.Query[i - 1]))
                    i--;
                while (i > 0 && !char.IsWhiteSpace(this.Query[i - 1]))
                    i--;
                this.Query = this.Query.Substring(0, i);
                this.RefreshMatches();
            }
            return null;
        }

        // printable character
        if (!char.IsControl(key.KeyChar))
        {
            this.Query += key.KeyChar;
            this.RefreshMatches();
        }

        return null;
    }


    /*********
    ** Private methods
    *********/
    /// <summary>Recompute fuzzy matches for the current <see cref="Query"/>.</summary>
    private void RefreshMatches()
    {
        if (string.IsNullOrEmpty(this.Query))
        {
            this.Matches = this.HistoryNewestFirst;
        }
        else
        {
            this.Matches = this.HistoryNewestFirst
                .Select((entry, recencyIndex) => (
                    Entry: entry,
                    RecencyIndex: recencyIndex,
                    Tier: HistorySearchState.GetMatchTier(entry, this.Query)
                ))
                .Where(p => p.Tier >= 0)
                .OrderByDescending(p => p.Tier)
                .ThenBy(p => p.RecencyIndex) // most recent first within the same match tier (Atuin-like recency)
                .Select(p => p.Entry)
                .ToArray();
        }

        this.SelectedIndex = this.Matches.Count > 0 ? 0 : -1;
        this.ScrollOffset = 0;
    }

    /// <summary>Classify how strongly a history entry matches the query (<c>-1</c> if no match).</summary>
    private static int GetMatchTier(string entry, string query)
    {
        if (entry.StartsWith(query, StringComparison.OrdinalIgnoreCase))
            return 2;
        if (entry.Contains(query, StringComparison.OrdinalIgnoreCase))
            return 1;
        return CommandManager.GetFuzzyMatchScore(entry, query) != int.MinValue
            ? 0
            : -1;
    }

    /// <summary>Move the selection by <paramref name="delta"/> with wrap-around and viewport scrolling.</summary>
    private void MoveSelection(int delta)
    {
        if (this.Matches.Count == 0)
            return;

        this.SelectedIndex = (this.SelectedIndex + delta + this.Matches.Count) % this.Matches.Count;

        if (this.SelectedIndex < this.ScrollOffset)
            this.ScrollOffset = this.SelectedIndex;
        else if (this.SelectedIndex >= this.ScrollOffset + this.MaxVisibleItems)
            this.ScrollOffset = this.SelectedIndex - this.MaxVisibleItems + 1;
    }
}

/// <summary>The action chosen when closing <see cref="HistorySearchOverlay"/>.</summary>
internal enum HistorySearchAction
{
    /// <summary>Cancel search and restore the previous prompt text.</summary>
    Cancel,

    /// <summary>Execute the selected command immediately.</summary>
    Execute,

    /// <summary>Insert the selected command into the prompt for further editing.</summary>
    Edit
}

/// <summary>The result returned by <see cref="HistorySearchOverlay"/>.</summary>
/// <param name="Action">The user's chosen action.</param>
/// <param name="Command">The selected command, if any.</param>
internal readonly record struct HistorySearchResult(HistorySearchAction Action, string? Command);
