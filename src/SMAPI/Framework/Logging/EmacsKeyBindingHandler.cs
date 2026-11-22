using System;
using System.Collections.Generic;
using PrettyPrompt.Consoles;

namespace StardewModdingAPI.Framework.Logging;

/// <summary>Implements standard Emacs line-editing key bindings, a kill ring, and Ctrl+R reverse history search for PrettyPrompt.</summary>
internal class EmacsKeyBindingHandler
{
    /*********
    ** Fields
    *********/
    /// <summary>Returns command history ordered from oldest to newest, if available.</summary>
    private readonly Func<IReadOnlyList<string>>? GetHistory;

    /// <summary>The last killed (cut) text for <c>Ctrl+Y</c> yanking.</summary>
    private string KillRing = string.Empty;

    /// <summary>The pending edit action queued during <see cref="TransformKeyPress"/> to be applied in <see cref="ApplyPendingEdit"/>.</summary>
    private PendingEmacsEdit PendingEdit = PendingEmacsEdit.None;

    /// <summary>The active <c>Ctrl+R</c> reverse history search query.</summary>
    private string? ReverseSearchQuery;

    /// <summary>The index in history of the last <c>Ctrl+R</c> match.</summary>
    private int ReverseSearchIndex = -1;

    /// <summary>The last command text populated by <c>Ctrl+R</c>, used to detect repeated <c>Ctrl+R</c> presses.</summary>
    private string? LastReverseSearchResult;


    /*********
    ** Accessors
    *********/
    /// <summary>The last text stored in the kill ring.</summary>
    public string LastKilledText => this.KillRing;


    /*********
    ** Public methods
    *********/
    /// <summary>Construct an instance.</summary>
    /// <param name="getHistory">Returns command history ordered from oldest to newest for <c>Ctrl+R</c> reverse search.</param>
    public EmacsKeyBindingHandler(Func<IReadOnlyList<string>>? getHistory = null)
    {
        this.GetHistory = getHistory;
    }

    /// <summary>Reset per-line reverse search state before a new prompt line begins.</summary>
    public void ResetLineState()
    {
        this.PendingEdit = PendingEmacsEdit.None;
        this.ResetReverseSearch();
    }

    /// <summary>Transform Emacs key presses into PrettyPrompt navigation keys or queue kill/yank/search edits.</summary>
    /// <param name="keyPress">The incoming key press.</param>
    public KeyPress TransformKeyPress(KeyPress keyPress)
    {
        ConsoleKeyInfo info = keyPress.ConsoleKeyInfo;
        ConsoleModifiers mods = info.Modifiers;
        bool ctrlOnly = mods == ConsoleModifiers.Control;
        bool altOnly = mods == ConsoleModifiers.Alt;

        // any key other than Ctrl+R exits the active reverse-search cycle
        if (!(ctrlOnly && info.Key == ConsoleKey.R))
            this.ResetReverseSearch();

        if (ctrlOnly)
        {
            switch (info.Key)
            {
                // movement
                case ConsoleKey.A:
                    return new KeyPress(new ConsoleKeyInfo('\0', ConsoleKey.Home, shift: false, alt: false, control: false));
                case ConsoleKey.E:
                    return new KeyPress(new ConsoleKeyInfo('\0', ConsoleKey.End, shift: false, alt: false, control: false));
                case ConsoleKey.B:
                    return new KeyPress(new ConsoleKeyInfo('\0', ConsoleKey.LeftArrow, shift: false, alt: false, control: false));
                case ConsoleKey.F:
                    return new KeyPress(new ConsoleKeyInfo('\0', ConsoleKey.RightArrow, shift: false, alt: false, control: false));
                case ConsoleKey.P:
                    return new KeyPress(new ConsoleKeyInfo('\0', ConsoleKey.UpArrow, shift: false, alt: false, control: false));
                case ConsoleKey.N:
                    return new KeyPress(new ConsoleKeyInfo('\0', ConsoleKey.DownArrow, shift: false, alt: false, control: false));

                // single-char delete (never exits game on empty line)
                case ConsoleKey.D:
                    return new KeyPress(new ConsoleKeyInfo('\0', ConsoleKey.Delete, shift: false, alt: false, control: false));
                case ConsoleKey.H:
                    return new KeyPress(new ConsoleKeyInfo('\b', ConsoleKey.Backspace, shift: false, alt: false, control: false));

                // cancel / abort completion
                case ConsoleKey.G:
                    return new KeyPress(new ConsoleKeyInfo('\x1b', ConsoleKey.Escape, shift: false, alt: false, control: false));

                // kill / yank / reverse-search actions handled in FormatInput
                case ConsoleKey.K:
                    this.PendingEdit = PendingEmacsEdit.KillToEnd;
                    return EmacsKeyBindingHandler.CreateNoOpKeyPress();
                case ConsoleKey.U:
                    this.PendingEdit = PendingEmacsEdit.KillToStart;
                    return EmacsKeyBindingHandler.CreateNoOpKeyPress();
                case ConsoleKey.W:
                    this.PendingEdit = PendingEmacsEdit.KillPreviousWord;
                    return EmacsKeyBindingHandler.CreateNoOpKeyPress();
                case ConsoleKey.Y:
                    this.PendingEdit = PendingEmacsEdit.Yank;
                    return EmacsKeyBindingHandler.CreateNoOpKeyPress();
                case ConsoleKey.R:
                    this.PendingEdit = PendingEmacsEdit.ReverseHistorySearch;
                    return EmacsKeyBindingHandler.CreateNoOpKeyPress();
            }
        }
        else if (altOnly)
        {
            switch (info.Key)
            {
                // word movement
                case ConsoleKey.B:
                    return new KeyPress(new ConsoleKeyInfo('\0', ConsoleKey.LeftArrow, shift: false, alt: false, control: true));
                case ConsoleKey.F:
                    return new KeyPress(new ConsoleKeyInfo('\0', ConsoleKey.RightArrow, shift: false, alt: false, control: true));

                // kill next word
                case ConsoleKey.D:
                    this.PendingEdit = PendingEmacsEdit.KillNextWord;
                    return EmacsKeyBindingHandler.CreateNoOpKeyPress();
            }
        }

        return keyPress;
    }

    /// <summary>Apply any queued Emacs kill/yank/search edit to the input buffer.</summary>
    /// <param name="text">The current input text.</param>
    /// <param name="caret">The current caret index.</param>
    /// <returns>The updated text and caret index.</returns>
    public (string Text, int Caret) ApplyPendingEdit(string text, int caret)
    {
        PendingEmacsEdit edit = this.PendingEdit;
        this.PendingEdit = PendingEmacsEdit.None;

        if (edit == PendingEmacsEdit.None)
            return (text, caret);

        caret = Math.Clamp(caret, 0, text.Length);

        switch (edit)
        {
            case PendingEmacsEdit.KillToEnd:
                if (caret < text.Length)
                {
                    this.KillRing = text.Substring(caret);
                    return (text.Substring(0, caret), caret);
                }
                return (text, caret);

            case PendingEmacsEdit.KillToStart:
                if (caret > 0)
                {
                    this.KillRing = text.Substring(0, caret);
                    return (text.Substring(caret), 0);
                }
                return (text, caret);

            case PendingEmacsEdit.KillPreviousWord:
                if (caret > 0)
                {
                    int wordStart = EmacsKeyBindingHandler.FindPreviousWordBoundary(text, caret);
                    this.KillRing = text.Substring(wordStart, caret - wordStart);
                    string updated = text.Remove(wordStart, caret - wordStart);
                    return (updated, wordStart);
                }
                return (text, caret);

            case PendingEmacsEdit.KillNextWord:
                if (caret < text.Length)
                {
                    int wordEnd = EmacsKeyBindingHandler.FindNextWordBoundary(text, caret);
                    this.KillRing = text.Substring(caret, wordEnd - caret);
                    string updated = text.Remove(caret, wordEnd - caret);
                    return (updated, caret);
                }
                return (text, caret);

            case PendingEmacsEdit.Yank:
                if (!string.IsNullOrEmpty(this.KillRing))
                {
                    string updated = text.Insert(caret, this.KillRing);
                    return (updated, caret + this.KillRing.Length);
                }
                return (text, caret);

            case PendingEmacsEdit.ReverseHistorySearch:
                return this.ApplyReverseHistorySearch(text, caret);

            default:
                return (text, caret);
        }
    }


    /*********
    ** Private methods
    *********/
    /// <summary>Search command history backwards for the current query (or continue to the previous match on repeated <c>Ctrl+R</c>).</summary>
    /// <param name="text">The current input text.</param>
    /// <param name="caret">The current caret index.</param>
    private (string Text, int Caret) ApplyReverseHistorySearch(string text, int caret)
    {
        IReadOnlyList<string>? history = this.GetHistory?.Invoke();
        if (history is null || history.Count == 0)
            return (text, caret);

        bool continuing = this.ReverseSearchQuery != null && string.Equals(text, this.LastReverseSearchResult, StringComparison.Ordinal);
        string query = continuing ? this.ReverseSearchQuery! : text;
        int startIndex = continuing ? this.ReverseSearchIndex - 1 : history.Count - 1;

        // search backwards from startIndex down to 0, then wrap around
        for (int offset = 0; offset < history.Count; offset++)
        {
            int idx = startIndex - offset;
            if (idx < 0)
                idx += history.Count;

            string entry = history[idx];
            if (string.IsNullOrEmpty(query) || entry.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                this.ReverseSearchQuery = query;
                this.ReverseSearchIndex = idx;
                this.LastReverseSearchResult = entry;
                return (entry, entry.Length);
            }
        }

        return (text, caret);
    }

    /// <summary>Reset reverse history search tracking.</summary>
    private void ResetReverseSearch()
    {
        this.ReverseSearchQuery = null;
        this.ReverseSearchIndex = -1;
        this.LastReverseSearchResult = null;
    }

    /// <summary>Create a key press that PrettyPrompt ignores as text input so <see cref="ApplyPendingEdit"/> can mutate the buffer cleanly.</summary>
    private static KeyPress CreateNoOpKeyPress()
    {
        return new KeyPress(new ConsoleKeyInfo('\0', ConsoleKey.NoName, shift: false, alt: false, control: false));
    }

    /// <summary>Find the start index of the word immediately preceding <paramref name="caret"/> (GNU Readline <c>Ctrl+W</c> behavior).</summary>
    /// <param name="text">The input text.</param>
    /// <param name="caret">The current caret index.</param>
    private static int FindPreviousWordBoundary(string text, int caret)
    {
        int i = caret;
        while (i > 0 && char.IsWhiteSpace(text[i - 1]))
            i--;
        while (i > 0 && !char.IsWhiteSpace(text[i - 1]))
            i--;
        return i;
    }

    /// <summary>Find the end index of the word immediately following <paramref name="caret"/> (GNU Readline <c>Alt+D</c> behavior).</summary>
    /// <param name="text">The input text.</param>
    /// <param name="caret">The current caret index.</param>
    private static int FindNextWordBoundary(string text, int caret)
    {
        int i = caret;
        while (i < text.Length && !char.IsLetterOrDigit(text[i]) && text[i] != '_')
            i++;
        while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_'))
            i++;
        return i;
    }

    /// <summary>An Emacs buffer edit queued for execution.</summary>
    private enum PendingEmacsEdit
    {
        None,
        KillToEnd,
        KillToStart,
        KillPreviousWord,
        KillNextWord,
        Yank,
        ReverseHistorySearch
    }
}
