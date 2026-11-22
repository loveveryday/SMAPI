using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PrettyPrompt;
using PrettyPrompt.Completion;
using PrettyPrompt.Consoles;
using PrettyPrompt.Documents;
using PrettyPrompt.Highlighting;

namespace StardewModdingAPI.Framework.Logging;

/// <summary>PrettyPrompt callbacks providing SMAPI command highlighting, fuzzy tab-completion, and Emacs key bindings.</summary>
internal class SmapiPromptCallbacks : PromptCallbacks
{
    /*********
    ** Fields
    *********/
    /// <summary>Manages registered SMAPI and mod commands.</summary>
    private readonly CommandManager CommandManager;

    /// <summary>The coordinated console used to track input state for background log redraws.</summary>
    private readonly CoordinatedConsole? CoordinatedConsole;

    /// <summary>Enqueues a synthetic key press into the active console, used to refresh a stale completion menu in a single Tab press.</summary>
    private readonly Action<ConsoleKeyInfo>? EnqueueSyntheticKey;

    /// <summary>Handles Emacs key bindings, kill ring, and reverse history search.</summary>
    private readonly EmacsKeyBindingHandler EmacsHandler;

    /// <summary>Returns the ANSI color to use for highlighted command names, or <c>null</c> when color is disabled.</summary>
    private readonly Func<AnsiColor?> GetCommandHighlightColor;

    /// <summary>Tracks whether the completion dropdown is currently open so a second <c>Tab</c> commits the selection.</summary>
    private bool IsCompletionMenuOpen;

    /// <summary>The input text snapshot when the completion list was last populated, so editing the token triggers a fresh completion query on Tab.</summary>
    private string? LastCompletionInput;

    /// <summary>Whether the caller supports launching the interactive Atuin-style <c>Ctrl+R</c> history search overlay.</summary>
    private readonly bool UseInteractiveHistorySearch;


    /*********
    ** Accessors
    *********/
    /// <summary>Whether the user pressed <c>Ctrl+R</c> to open the interactive history search overlay.</summary>
    public bool HistorySearchRequested { get; private set; }

    /// <summary>The prompt text that was in the buffer when <c>Ctrl+R</c> was pressed.</summary>
    public string SavedInputBeforeHistorySearch { get; private set; } = string.Empty;


    /*********
    ** Public methods
    *********/
    /// <summary>Construct an instance.</summary>
    /// <param name="commandManager">Manages registered SMAPI and mod commands.</param>
    /// <param name="coordinatedConsole">The coordinated console used to track input state, if any.</param>
    /// <param name="emacsHandler">Handles Emacs key bindings and kill ring state.</param>
    /// <param name="getCommandHighlightColor">Returns the ANSI color for command names, or <c>null</c> when disabled.</param>
    /// <param name="enqueueSyntheticKey">An optional callback to enqueue a synthetic key press into the console.</param>
    /// <param name="useInteractiveHistorySearch">Whether pressing <c>Ctrl+R</c> hands off to the interactive Atuin-style history search overlay.</param>
    public SmapiPromptCallbacks(
        CommandManager commandManager,
        CoordinatedConsole? coordinatedConsole,
        EmacsKeyBindingHandler emacsHandler,
        Func<AnsiColor?> getCommandHighlightColor,
        Action<ConsoleKeyInfo>? enqueueSyntheticKey = null,
        bool useInteractiveHistorySearch = false
    )
    {
        this.CommandManager = commandManager;
        this.CoordinatedConsole = coordinatedConsole;
        this.EmacsHandler = emacsHandler;
        this.GetCommandHighlightColor = getCommandHighlightColor;
        this.EnqueueSyntheticKey = enqueueSyntheticKey ?? (coordinatedConsole != null ? coordinatedConsole.EnqueueSyntheticKey : null);
        this.UseInteractiveHistorySearch = useInteractiveHistorySearch;
    }

    /// <summary>Reset per-line transient completion and Emacs state before starting a new <c>ReadLineAsync</c>.</summary>
    public void ResetLineState()
    {
        this.IsCompletionMenuOpen = false;
        this.LastCompletionInput = null;
        this.HistorySearchRequested = false;
        this.SavedInputBeforeHistorySearch = string.Empty;
        this.EmacsHandler.ResetLineState();
    }

    /// <inheritdoc />
    protected override Task<IReadOnlyCollection<FormatSpan>> HighlightCallbackAsync(string text, CancellationToken cancellationToken)
    {
        this.CoordinatedConsole?.UpdateInputState(text);

        AnsiColor? commandColor = this.GetCommandHighlightColor();
        if (commandColor is not null && this.CommandManager.TryGetCommandNameSpan(text, out int start, out int length))
        {
            IReadOnlyCollection<FormatSpan> spans = [new FormatSpan(start, length, commandColor.Value)];
            return Task.FromResult(spans);
        }

        return Task.FromResult<IReadOnlyCollection<FormatSpan>>(Array.Empty<FormatSpan>());
    }

    /// <inheritdoc />
    protected override Task<bool> ShouldOpenCompletionWindowAsync(string text, int caret, KeyPress keyPress, CancellationToken cancellationToken)
    {
        // Only open completion via explicit trigger (Tab mapped to Ctrl+Space, or Ctrl+Space directly).
        return Task.FromResult(false);
    }

    /// <inheritdoc />
    protected override Task<TextSpan> GetSpanToReplaceByCompletionAsync(string text, int caret, CancellationToken cancellationToken)
    {
        (int start, int length) = this.CommandManager.GetCompletionSpan(text, caret);
        return Task.FromResult(new TextSpan(start, length));
    }

    /// <inheritdoc />
    protected override Task<IReadOnlyList<CompletionItem>> GetCompletionItemsAsync(string text, int caret, TextSpan spanToBeReplaced, CancellationToken cancellationToken)
    {
        // Only return actual fuzzy-matching items so PrettyPrompt's SlidingArrayWindow never pads the dropdown with non-matching commands.
        this.LastCompletionInput = text;
        IReadOnlyList<CommandCompletionItem> matches = this.CommandManager.GetCompletions(text, caret, filterByPrefix: true);
        if (matches.Count == 0)
            return Task.FromResult<IReadOnlyList<CompletionItem>>(Array.Empty<CompletionItem>());

        List<CompletionItem> items = new(matches.Count);
        foreach (CommandCompletionItem match in matches)
        {
            string? description = match.Description;
            items.Add(new FuzzyCompletionItem(
                replacementText: match.ReplacementText,
                displayText: new FormattedString(match.DisplayText),
                filterText: match.DisplayText,
                getExtendedDescription: description != null
                    ? _ => Task.FromResult(new FormattedString(description))
                    : null
            ));
        }

        return Task.FromResult<IReadOnlyList<CompletionItem>>(items);
    }

    /// <inheritdoc />
    protected override Task<bool> ConfirmCompletionCommit(string text, int caret, KeyPress keyPress, CancellationToken cancellationToken)
    {
        this.IsCompletionMenuOpen = false;
        this.LastCompletionInput = null;
        return Task.FromResult(true);
    }

    /// <inheritdoc />
    protected override Task<KeyPress> TransformKeyPressAsync(string text, int caret, KeyPress keyPress, CancellationToken cancellationToken)
    {
        this.CoordinatedConsole?.UpdateInputState(text, caret);

        ConsoleKeyInfo info = keyPress.ConsoleKeyInfo;

        // Tab:
        // - If the menu is open for the current input, leave as Tab so KeyBindings.CommitCompletion accepts the selected item.
        // - If the menu is closed OR the user edited the input since the menu opened, re-query fuzzy matches via Ctrl+Space.
        // - If there are zero fuzzy matches, close any stale menu (Escape) or do nothing (NoName).
        if (info.Key == ConsoleKey.Tab && info.Modifiers == 0)
        {
            bool inputChangedSinceOpen = !string.Equals(text, this.LastCompletionInput, StringComparison.Ordinal);

            if (this.IsCompletionMenuOpen && !inputChangedSinceOpen)
            {
                this.IsCompletionMenuOpen = false;
                this.LastCompletionInput = null;
                return Task.FromResult(keyPress);
            }

            if (this.CommandManager.HasMatchingCompletions(text, caret))
            {
                ConsoleKeyInfo ctrlSpace = new(' ', ConsoleKey.Spacebar, shift: false, alt: false, control: true);

                // If the completion dropdown was already open for a different input, PrettyPrompt treats Ctrl+Space as a toggle (closing it).
                // Close the stale menu with Escape first, then immediately enqueue Ctrl+Space so PrettyPrompt re-opens with fresh fuzzy matches.
                if (this.IsCompletionMenuOpen && inputChangedSinceOpen && this.EnqueueSyntheticKey != null)
                {
                    this.IsCompletionMenuOpen = false;
                    this.LastCompletionInput = null;
                    this.EnqueueSyntheticKey(ctrlSpace);
                    return Task.FromResult(new KeyPress(new ConsoleKeyInfo('\x1b', ConsoleKey.Escape, shift: false, alt: false, control: false)));
                }

                this.IsCompletionMenuOpen = true;
                this.LastCompletionInput = text;
                return Task.FromResult(new KeyPress(ctrlSpace));
            }

            bool wasOpen = this.IsCompletionMenuOpen;
            this.IsCompletionMenuOpen = false;
            this.LastCompletionInput = null;
            return Task.FromResult(wasOpen
                ? new KeyPress(new ConsoleKeyInfo('\x1b', ConsoleKey.Escape, shift: false, alt: false, control: false))
                : new KeyPress(new ConsoleKeyInfo('\0', ConsoleKey.NoName, shift: false, alt: false, control: false)));
        }

        // Ctrl+Space: only trigger completion menu if matches exist
        if (info.Key == ConsoleKey.Spacebar && info.Modifiers == ConsoleModifiers.Control)
        {
            if (this.CommandManager.HasMatchingCompletions(text, caret))
            {
                this.IsCompletionMenuOpen = true;
                this.LastCompletionInput = text;
                return Task.FromResult(keyPress);
            }

            this.IsCompletionMenuOpen = false;
            this.LastCompletionInput = null;
            return Task.FromResult(new KeyPress(new ConsoleKeyInfo('\0', ConsoleKey.NoName, shift: false, alt: false, control: false)));
        }

        // Ctrl+R: hand off to the Atuin-style interactive history search overlay when enabled
        if (this.UseInteractiveHistorySearch && info.Key == ConsoleKey.R && info.Modifiers == ConsoleModifiers.Control)
        {
            this.HistorySearchRequested = true;
            this.SavedInputBeforeHistorySearch = text;
            this.IsCompletionMenuOpen = false;
            this.LastCompletionInput = null;
            return Task.FromResult(new KeyPress(new ConsoleKeyInfo('\x03', ConsoleKey.C, shift: false, alt: false, control: true)));
        }

        // Apply Emacs key mappings (including Ctrl+P -> UpArrow, Ctrl+N -> DownArrow, Ctrl+R -> reverse search)
        KeyPress transformed = this.EmacsHandler.TransformKeyPress(keyPress);
        ConsoleKeyInfo transformedInfo = transformed.ConsoleKeyInfo;

        // Track keys that close the completion dropdown
        if (this.IsCompletionMenuOpen)
        {
            bool closesMenu =
                transformedInfo.Key is ConsoleKey.Enter or ConsoleKey.Escape or ConsoleKey.LeftArrow or ConsoleKey.RightArrow or ConsoleKey.Home or ConsoleKey.End or ConsoleKey.NoName
                || transformedInfo.KeyChar == ' '
                || (transformedInfo.Key == ConsoleKey.C && transformedInfo.Modifiers == ConsoleModifiers.Control);

            if (closesMenu)
            {
                this.IsCompletionMenuOpen = false;
                this.LastCompletionInput = null;
            }
        }

        return Task.FromResult(transformed);
    }

    /// <inheritdoc />
    protected override Task<(string Text, int Caret)> FormatInput(string text, int caret, KeyPress keyPress, CancellationToken cancellationToken)
    {
        (string updatedText, int updatedCaret) = this.EmacsHandler.ApplyPendingEdit(text, caret);
        this.CoordinatedConsole?.UpdateInputState(updatedText, updatedCaret);

        return Task.FromResult((updatedText, updatedCaret));
    }


    /*********
    ** Private types
    *********/
    /// <summary>A <see cref="CompletionItem"/> that uses fuzzy subsequence matching for ranking.</summary>
    private sealed class FuzzyCompletionItem : CompletionItem
    {
        /// <inheritdoc />
        public FuzzyCompletionItem(string replacementText, FormattedString displayText, string filterText, GetExtendedDescriptionHandler? getExtendedDescription)
            : base(replacementText, displayText, filterText, getExtendedDescription) { }

        /// <inheritdoc />
        public override int GetCompletionItemPriority(string text, int caret, TextSpan spanToBeReplaced)
        {
            int start = Math.Clamp(spanToBeReplaced.Start, 0, text.Length);
            int length = Math.Clamp(spanToBeReplaced.Length, 0, text.Length - start);
            string query = CommandManager.Unquote(text.Substring(start, length));

            return CommandManager.GetFuzzyMatchScore(this.FilterText, query);
        }
    }
}
