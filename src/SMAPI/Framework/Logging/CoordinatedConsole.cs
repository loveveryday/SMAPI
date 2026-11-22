using System;
using System.Collections.Concurrent;
using System.Text;
using PrettyPrompt.Consoles;

namespace StardewModdingAPI.Framework.Logging;

/// <summary>An <see cref="IConsole"/> wrapper for PrettyPrompt that coordinates concurrent log output with the active prompt line.</summary>
internal class CoordinatedConsole : IConsole
{
    /*********
    ** Fields
    *********/
    /// <summary>The underlying system console.</summary>
    private readonly IConsole Inner = new SystemConsole();

    /// <summary>Synthetic key presses queued internally (e.g. to refresh a stale completion popup after editing).</summary>
    private readonly ConcurrentQueue<ConsoleKeyInfo> SyntheticKeys = new();

    /// <summary>The lock synchronizing console writes between PrettyPrompt and SMAPI's logger.</summary>
    private readonly object ConsoleLock = new();

    /// <summary>The plain prompt text (e.g. <c>"smapi$ "</c>).</summary>
    private readonly string PromptText;

    /// <summary>Returns the ANSI escape sequence for the prompt color, or <c>null</c> when color is disabled.</summary>
    private readonly Func<string?> GetPromptColorCode;

    /// <summary>Returns the ANSI escape sequence for the command highlight color, or <c>null</c> when color is disabled.</summary>
    private readonly Func<string?> GetCommandColorCode;

    /// <summary>Returns the command name span in the given input if it starts with a registered command.</summary>
    private readonly Func<string, (bool Found, int Start, int Length)> TryGetCommandSpan;

    /// <summary>Whether <c>Prompt.ReadLineAsync</c> is currently waiting for user input.</summary>
    private volatile bool IsPromptActive;

    /// <summary>The latest user input snapshot tracked for redrawing after background log writes.</summary>
    private string CurrentInput = string.Empty;

    /// <summary>The latest caret offset tracked for redrawing after background log writes.</summary>
    private int CurrentCaret;

    /// <summary>An optional custom overlay clear callback (e.g. while the Atuin-style history search overlay is open).</summary>
    private Action? CustomOverlayClear;

    /// <summary>An optional custom overlay redraw callback.</summary>
    private Action? CustomOverlayRedraw;


    /*********
    ** Accessors
    *********/
    /// <inheritdoc />
    public int CursorTop => this.Inner.CursorTop;

    /// <inheritdoc />
    public int BufferWidth => this.Inner.BufferWidth;

    /// <inheritdoc />
    public int WindowHeight => this.Inner.WindowHeight;

    /// <inheritdoc />
    public int WindowTop => this.Inner.WindowTop;

    /// <inheritdoc />
    public bool KeyAvailable => this.Inner.KeyAvailable;

    /// <inheritdoc />
    public bool IsErrorRedirected => this.Inner.IsErrorRedirected;

    /// <inheritdoc />
    public bool CaptureControlC
    {
        get => this.Inner.CaptureControlC;
        set => this.Inner.CaptureControlC = value;
    }

    /// <inheritdoc />
    public event ConsoleCancelEventHandler CancelKeyPress
    {
        add => this.Inner.CancelKeyPress += value;
        remove => this.Inner.CancelKeyPress -= value;
    }


    /*********
    ** Public methods
    *********/
    /// <summary>Construct an instance.</summary>
    /// <param name="promptText">The plain prompt text (e.g. <c>"smapi$ "</c>).</param>
    /// <param name="getPromptColorCode">Returns the ANSI foreground escape sequence for the prompt, or <c>null</c> if colors are disabled.</param>
    /// <param name="getCommandColorCode">Returns the ANSI foreground escape sequence for highlighted commands, or <c>null</c> if colors are disabled.</param>
    /// <param name="tryGetCommandSpan">Locates a registered command name at the start of input.</param>
    public CoordinatedConsole(
        string promptText,
        Func<string?> getPromptColorCode,
        Func<string?> getCommandColorCode,
        Func<string, (bool Found, int Start, int Length)> tryGetCommandSpan
    )
    {
        this.PromptText = promptText;
        this.GetPromptColorCode = getPromptColorCode;
        this.GetCommandColorCode = getCommandColorCode;
        this.TryGetCommandSpan = tryGetCommandSpan;
    }

    /// <summary>Mark whether PrettyPrompt is actively reading a line.</summary>
    /// <param name="active">Whether the prompt is active.</param>
    public void SetPromptActive(bool active)
    {
        lock (this.ConsoleLock)
        {
            this.IsPromptActive = active;
            if (!active)
            {
                this.CurrentInput = string.Empty;
                this.CurrentCaret = 0;
            }
        }
    }

    /// <summary>Update the tracked input text and caret position for redrawing after background logs.</summary>
    /// <param name="text">The current input buffer.</param>
    /// <param name="caret">The current caret index, or <c>null</c> to keep the existing caret clamped to <paramref name="text"/>.</param>
    public void UpdateInputState(string text, int? caret = null)
    {
        lock (this.ConsoleLock)
        {
            this.CurrentInput = text;
            this.CurrentCaret = caret.HasValue
                ? Math.Clamp(caret.Value, 0, text.Length)
                : Math.Clamp(this.CurrentCaret, 0, text.Length);
        }
    }

    /// <summary>Ensure there are enough blank lines below the current prompt line to display the completion dropdown.</summary>
    /// <param name="minRows">The minimum number of rows required below the prompt line.</param>
    public void EnsureCompletionSpace(int minRows = 8)
    {
        lock (this.ConsoleLock)
        {
            try
            {
                int windowHeight = this.Inner.WindowHeight;
                if (windowHeight <= 2)
                    return;

                int targetRows = Math.Min(minRows, Math.Max(3, windowHeight - 2));
                int remainingRows = this.Inner.WindowTop + windowHeight - 1 - this.Inner.CursorTop;
                if (remainingRows >= targetRows)
                    return;

                int scrollLines = targetRows - remainingRows;
                int cursorColumn = this.PromptText.Length + Math.Clamp(this.CurrentCaret, 0, this.CurrentInput.Length);

                // scroll terminal up to create room below the prompt, then restore cursor to the exact prompt column
                StringBuilder sb = new();
                for (int i = 0; i < scrollLines; i++)
                    sb.Append("\r\n");
                sb.Append($"\x1b[{scrollLines}A\r");
                if (cursorColumn > 0)
                    sb.Append($"\x1b[{cursorColumn}C");

                Console.Write(sb.ToString());
            }
            catch
            {
                // ignore terminal query errors on unsupported consoles
            }
        }
    }

    /// <summary>Register or clear a custom TUI overlay (such as history search) so background log writes cleanly clear and redraw it.</summary>
    /// <param name="clearOverlay">Clears the overlay from the console (invoked while holding the console lock).</param>
    /// <param name="redrawOverlay">Redraws the overlay to the console (invoked while holding the console lock).</param>
    public void SetCustomOverlay(Action? clearOverlay, Action? redrawOverlay)
    {
        lock (this.ConsoleLock)
        {
            this.CustomOverlayClear = clearOverlay;
            this.CustomOverlayRedraw = redrawOverlay;
        }
    }

    /// <summary>Execute an action while holding the console synchronization lock.</summary>
    /// <param name="action">The console rendering action to run atomically.</param>
    public void RunExclusive(Action action)
    {
        lock (this.ConsoleLock)
            action();
    }

    /// <summary>Execute a log write safely while preserving and redrawing the active prompt line or custom overlay.</summary>
    /// <param name="writeAction">The action which writes one log line to <see cref="Console"/>.</param>
    public void WriteLogLine(Action writeAction)
    {
        lock (this.ConsoleLock)
        {
            if (this.CustomOverlayClear != null && this.CustomOverlayRedraw != null)
            {
                this.CustomOverlayClear();
                writeAction();
                this.CustomOverlayRedraw();
                return;
            }

            if (!this.IsPromptActive)
            {
                writeAction();
                return;
            }

            // clear current prompt line and any completion pane below it
            Console.Write("\r\x1b[0J");

            // write the log line (ends with newline, leaving cursor at column 0 of the next line)
            writeAction();

            // redraw the prompt and current input buffer
            this.RedrawPromptLineUnlocked();
        }
    }

    /// <inheritdoc />
    public void Write(string? value)
    {
        lock (this.ConsoleLock)
            this.Inner.Write(value);
    }

    /// <inheritdoc />
    public void WriteLine(string? value)
    {
        lock (this.ConsoleLock)
            this.Inner.WriteLine(value);
    }

    /// <inheritdoc />
    public void WriteError(string? value)
    {
        lock (this.ConsoleLock)
            this.Inner.WriteError(value);
    }

    /// <inheritdoc />
    public void WriteErrorLine(string? value)
    {
        lock (this.ConsoleLock)
            this.Inner.WriteErrorLine(value);
    }

    /// <inheritdoc />
    public void Write(ReadOnlySpan<char> value)
    {
        lock (this.ConsoleLock)
            this.Inner.Write(value);
    }

    /// <inheritdoc />
    public void WriteLine(ReadOnlySpan<char> value)
    {
        lock (this.ConsoleLock)
            this.Inner.WriteLine(value);
    }

    /// <inheritdoc />
    public void WriteError(ReadOnlySpan<char> value)
    {
        lock (this.ConsoleLock)
            this.Inner.WriteError(value);
    }

    /// <inheritdoc />
    public void WriteErrorLine(ReadOnlySpan<char> value)
    {
        lock (this.ConsoleLock)
            this.Inner.WriteErrorLine(value);
    }

    /// <inheritdoc />
    public void Write(StringBuilder value, bool hideCursor = false)
    {
        lock (this.ConsoleLock)
            this.Inner.Write(value, hideCursor);
    }

    /// <inheritdoc />
    public void WriteLine(StringBuilder value, bool hideCursor = false)
    {
        lock (this.ConsoleLock)
            this.Inner.WriteLine(value, hideCursor);
    }

    /// <inheritdoc />
    public void WriteError(StringBuilder value, bool hideCursor = false)
    {
        lock (this.ConsoleLock)
            this.Inner.WriteError(value, hideCursor);
    }

    /// <inheritdoc />
    public void WriteErrorLine(StringBuilder value, bool hideCursor = false)
    {
        lock (this.ConsoleLock)
            this.Inner.WriteErrorLine(value, hideCursor);
    }

    /// <inheritdoc />
    public void Clear()
    {
        lock (this.ConsoleLock)
            this.Inner.Clear();
    }

    /// <inheritdoc />
    public void ShowCursor()
    {
        lock (this.ConsoleLock)
            this.Inner.ShowCursor();
    }

    /// <inheritdoc />
    public void HideCursor()
    {
        lock (this.ConsoleLock)
            this.Inner.HideCursor();
    }

    /// <summary>Enqueue a synthetic key press to be returned on the next <see cref="ReadKey"/> call.</summary>
    /// <param name="key">The key press to enqueue.</param>
    public void EnqueueSyntheticKey(ConsoleKeyInfo key)
    {
        this.SyntheticKeys.Enqueue(key);
    }

    /// <summary>Enqueue text characters as synthetic key presses to prefill the next prompt line.</summary>
    /// <param name="text">The text to type into the next prompt.</param>
    public void EnqueueSyntheticText(string text)
    {
        foreach (char ch in text)
        {
            ConsoleKey key = ch switch
            {
                >= 'a' and <= 'z' => ConsoleKey.A + (ch - 'a'),
                >= 'A' and <= 'Z' => ConsoleKey.A + (ch - 'A'),
                >= '0' and <= '9' => ConsoleKey.D0 + (ch - '0'),
                ' ' => ConsoleKey.Spacebar,
                _ => ConsoleKey.NoName
            };
            this.SyntheticKeys.Enqueue(new ConsoleKeyInfo(ch, key, shift: char.IsUpper(ch), alt: false, control: false));
        }
    }

    /// <inheritdoc />
    public ConsoleKeyInfo ReadKey(bool intercept)
    {
        if (this.SyntheticKeys.TryDequeue(out ConsoleKeyInfo synthetic))
            return synthetic;

        // never hold ConsoleLock while blocking on key input
        return this.Inner.ReadKey(intercept);
    }

    /// <inheritdoc />
    public void InitVirtualTerminalProcessing()
    {
        lock (this.ConsoleLock)
            this.Inner.InitVirtualTerminalProcessing();
    }


    /*********
    ** Private methods
    *********/
    /// <summary>Redraw the prompt and current single-line input buffer. Caller must hold <see cref="ConsoleLock"/>.</summary>
    private void RedrawPromptLineUnlocked()
    {
        const string resetAnsi = "\x1b[0m";
        string? promptColor = this.GetPromptColorCode();
        string? commandColor = this.GetCommandColorCode();

        StringBuilder sb = new();
        if (promptColor != null)
            sb.Append(promptColor).Append(this.PromptText).Append(resetAnsi);
        else
            sb.Append(this.PromptText);

        string input = this.CurrentInput;
        if (input.Length > 0)
        {
            (bool found, int start, int length) = this.TryGetCommandSpan(input);
            if (found && commandColor != null)
            {
                if (start > 0)
                    sb.Append(input, 0, start);
                sb.Append(commandColor).Append(input, start, length).Append(resetAnsi);
                if (start + length < input.Length)
                    sb.Append(input, start + length, input.Length - (start + length));
            }
            else
            {
                sb.Append(input);
            }
        }

        // reposition cursor back to caret if not at end of line
        int trailingChars = input.Length - Math.Clamp(this.CurrentCaret, 0, input.Length);
        if (trailingChars > 0)
            sb.Append($"\x1b[{trailingChars}D");

        Console.Write(sb.ToString());
    }
}
