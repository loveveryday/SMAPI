using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using PrettyPrompt;
using PrettyPrompt.Consoles;
using StardewModdingAPI;
using StardewModdingAPI.Framework;
using StardewModdingAPI.Framework.Commands;
using StardewModdingAPI.Framework.Logging;

namespace SMAPI.Tests.Core;

/// <summary>Unit tests for console command highlighting, tab-completion, and Emacs key bindings.</summary>
[TestFixture]
internal class ConsoleCommandTests
{
    /*********
    ** Unit tests
    *********/
    /****
    ** CommandManager highlighting
    ****/
    [TestCase("help", true, 0, 4)]
    [TestCase("HELP world_setseason", true, 0, 4)]
    [TestCase("  world_setseason spring", true, 2, 15)]
    [TestCase("unknown_cmd arg1", false, 0, 0)]
    [TestCase("   ", false, 0, 0)]
    [TestCase("", false, 0, 0)]
    public void TryGetCommandNameSpan_MatchesRegisteredCommands(string input, bool expectedFound, int expectedStart, int expectedLength)
    {
        // arrange
        CommandManager manager = this.CreateCommandManager();

        // act
        bool found = manager.TryGetCommandNameSpan(input, out int start, out int length);

        // assert
        found.Should().Be(expectedFound);
        start.Should().Be(expectedStart);
        length.Should().Be(expectedLength);
    }

    /****
    ** CommandManager tab-completion
    ****/
    [Test]
    public void GetCompletions_CommandNamePrefix_ReturnsMatchingCommandsWithDescriptions()
    {
        // arrange
        CommandManager manager = this.CreateCommandManager();

        // act
        IReadOnlyList<CommandCompletionItem> items = manager.GetCompletions("world_", caret: 6);

        // assert (shorter tighter match ranks before longer match: world_clear(11), world_setseason(15), world_freezetime(16))
        items.Select(p => p.DisplayText).Should().Equal("world_clear", "world_setseason", "world_freezetime");
        items[0].Description.Should().Be("Clears entities.");
        items[1].Description.Should().Be("Sets the season.");
        items[2].Description.Should().Be("Freezes time.");
    }

    [Test]
    public void GetCompletions_Unfiltered_ReturnsAllCandidatesForPrettyPromptCaching()
    {
        // arrange
        CommandManager manager = this.CreateCommandManager();

        // act: even when typing "hel", filterByPrefix: false returns all commands so PrettyPrompt can re-filter after editing to "world"
        IReadOnlyList<CommandCompletionItem> commandItems = manager.GetCompletions("hel", caret: 3, filterByPrefix: false);
        IReadOnlyList<CommandCompletionItem> argItems = manager.GetCompletions("world_setseason s", caret: 17, filterByPrefix: false);

        // assert
        commandItems.Select(p => p.DisplayText).Should().Equal("help", "world_clear", "world_freezetime", "world_setseason");
        argItems.Select(p => p.DisplayText).Should().Equal("fall", "spring", "summer", "winter");
    }

    [Test]
    public void GetFuzzyMatchScore_MatchesSubsequenceAndRejectsNonMatches()
    {
        CommandManager.GetFuzzyMatchScore("world_freezetime", "worldfrz").Should().BeGreaterThan(0);
        CommandManager.GetFuzzyMatchScore("world_setseason", "wss").Should().BeGreaterThan(0);
        CommandManager.GetFuzzyMatchScore("help", "zzzzz").Should().Be(int.MinValue);

        // prefix match ranks higher than subsequence match
        int prefixScore = CommandManager.GetFuzzyMatchScore("world_setseason", "world");
        int fuzzyScore = CommandManager.GetFuzzyMatchScore("world_setseason", "wss");
        prefixScore.Should().BeGreaterThan(fuzzyScore);
    }

    [Test]
    public async Task Prompt_FuzzyMatchAndSecondTabCommitsSelection()
    {
        // arrange: type "worldfrz", Tab (open), Tab (commit), Enter
        PromptResult result = await this.RunPromptSessionAsync(
            ConsoleCommandTests.TextKeys("worldfrz")
                .Concat([ConsoleCommandTests.Key(ConsoleKey.Tab), ConsoleCommandTests.Key(ConsoleKey.Tab), ConsoleCommandTests.Key(ConsoleKey.Enter)])
        );

        // assert
        result.IsSuccess.Should().BeTrue();
        result.Text.Should().Be("world_freezetime");
    }

    [Test]
    public async Task Prompt_NoMatchTabDoesNotShowAllCommandsOrInsertSpaces()
    {
        // arrange: type "zzzzz", Tab, Tab, Enter
        PromptResult result = await this.RunPromptSessionAsync(
            ConsoleCommandTests.TextKeys("zzzzz")
                .Concat([ConsoleCommandTests.Key(ConsoleKey.Tab), ConsoleCommandTests.Key(ConsoleKey.Tab), ConsoleCommandTests.Key(ConsoleKey.Enter)])
        );

        // assert
        result.IsSuccess.Should().BeTrue();
        result.Text.Should().Be("zzzzz");
    }

    [Test]
    public async Task Prompt_CompletionListSupportsCtrlNAndCtrlPNavigation()
    {
        // arrange: type "world_", Tab (opens [world_clear, world_freezetime, world_setseason]),
        // Ctrl+N (-> world_freezetime), Ctrl+N (-> world_setseason), Ctrl+P (-> world_freezetime), Tab (commit), Enter
        PromptResult result = await this.RunPromptSessionAsync(
            ConsoleCommandTests.TextKeys("world_")
                .Concat([
                    ConsoleCommandTests.Key(ConsoleKey.Tab),
                    ConsoleCommandTests.Ctrl(ConsoleKey.N).ConsoleKeyInfo,
                    ConsoleCommandTests.Ctrl(ConsoleKey.N).ConsoleKeyInfo,
                    ConsoleCommandTests.Ctrl(ConsoleKey.P).ConsoleKeyInfo,
                    ConsoleCommandTests.Key(ConsoleKey.Tab),
                    ConsoleCommandTests.Key(ConsoleKey.Enter)
                ])
        );

        // assert (fuzzy order for "world_": world_clear, world_setseason, world_freezetime;
        // Down -> world_setseason, Down -> world_freezetime, Up -> world_setseason)
        result.IsSuccess.Should().BeTrue();
        result.Text.Should().Be("world_setseason");
    }

    [Test]
    public async Task Prompt_EditWhileCompletionOpenUpdatesAndTabCommits()
    {
        // arrange: type "hel", Tab (opens menu for "hel"), Backspace x3, type "worldfrz",
        // Tab (input changed -> refreshes menu to [world_freezetime]), Tab (commits), Enter
        PromptResult result = await this.RunPromptSessionAsync(
            ConsoleCommandTests.TextKeys("hel")
                .Concat([
                    ConsoleCommandTests.Key(ConsoleKey.Tab),
                    ConsoleCommandTests.Key(ConsoleKey.Backspace),
                    ConsoleCommandTests.Key(ConsoleKey.Backspace),
                    ConsoleCommandTests.Key(ConsoleKey.Backspace)
                ])
                .Concat(ConsoleCommandTests.TextKeys("worldfrz"))
                .Concat([
                    ConsoleCommandTests.Key(ConsoleKey.Tab),
                    ConsoleCommandTests.Key(ConsoleKey.Tab),
                    ConsoleCommandTests.Key(ConsoleKey.Enter)
                ])
        );

        // assert
        result.IsSuccess.Should().BeTrue();
        result.Text.Should().Be("world_freezetime");
    }

    [Test]
    public async Task Prompt_CtrlRReverseHistorySearchCyclesMatches()
    {
        // arrange: history has two mine commands; typing "mine" + Ctrl+R -> newest match, Ctrl+R again -> older match
        string[] history = ["world_setminelevel 40", "help", "world_setminelevel 120"];
        PromptResult result = await this.RunPromptSessionAsync(
            ConsoleCommandTests.TextKeys("mine")
                .Concat([
                    ConsoleCommandTests.Ctrl(ConsoleKey.R).ConsoleKeyInfo,
                    ConsoleCommandTests.Ctrl(ConsoleKey.R).ConsoleKeyInfo,
                    ConsoleCommandTests.Key(ConsoleKey.Enter)
                ]),
            history: history
        );

        // assert
        result.IsSuccess.Should().BeTrue();
        result.Text.Should().Be("world_setminelevel 40");
    }

    [Test]
    public void GetCompletions_HelpWithFuzzyArg_ExcludesNonMatchingCommands()
    {
        // arrange
        CommandManager manager = this.CreateCommandManager();

        // act
        IReadOnlyList<CommandCompletionItem> items = manager.GetCompletions("help wfrz", caret: 9, filterByPrefix: true);

        // assert: only matching command is returned, no non-matching commands like 'help' or 'world_clear'
        items.Select(p => p.DisplayText).Should().Equal("world_freezetime");
    }

    [Test]
    public void HistorySearchState_FuzzyFiltersNavigatesAndReturnsActions()
    {
        // arrange
        string[] history = ["help", "world_setminelevel 40", "world_setseason spring", "world_setminelevel 120"];
        var state = new HistorySearchState(history, initialQuery: "", maxVisibleItems: 2);

        // newest first when query is empty
        state.Matches.Should().Equal("world_setminelevel 120", "world_setseason spring", "world_setminelevel 40", "help");
        state.SelectedItem.Should().Be("world_setminelevel 120");

        // fuzzy typing "wsm" filters to both mine level commands, newest first
        foreach (ConsoleKeyInfo k in ConsoleCommandTests.TextKeys("wsm"))
            state.HandleKey(k).Should().BeNull();
        state.Matches.Should().Equal("world_setminelevel 120", "world_setminelevel 40");

        // Ctrl+N / Ctrl+R moves to next match; Ctrl+P moves back
        state.HandleKey(ConsoleCommandTests.Ctrl(ConsoleKey.N).ConsoleKeyInfo).Should().BeNull();
        state.SelectedItem.Should().Be("world_setminelevel 40");
        state.HandleKey(ConsoleCommandTests.Ctrl(ConsoleKey.P).ConsoleKeyInfo).Should().BeNull();
        state.SelectedItem.Should().Be("world_setminelevel 120");

        // Tab returns Edit action with selected command
        HistorySearchResult? editResult = state.HandleKey(ConsoleCommandTests.Key(ConsoleKey.Tab));
        editResult.Should().Be(new HistorySearchResult(HistorySearchAction.Edit, "world_setminelevel 120"));

        // Enter returns Execute action
        HistorySearchResult? execResult = state.HandleKey(ConsoleCommandTests.Key(ConsoleKey.Enter));
        execResult.Should().Be(new HistorySearchResult(HistorySearchAction.Execute, "world_setminelevel 120"));

        // Ctrl+U clears query, Esc cancels
        state.HandleKey(ConsoleCommandTests.Ctrl(ConsoleKey.U).ConsoleKeyInfo).Should().BeNull();
        state.Query.Should().BeEmpty();
        state.Matches.Count.Should().Be(4);
        state.HandleKey(ConsoleCommandTests.Key(ConsoleKey.Escape)).Should().Be(new HistorySearchResult(HistorySearchAction.Cancel, null));
    }

    [Test]
    public async Task Prompt_EscapeThenEditAndTabUpdatesCompletionList()
    {
        // arrange: type "hel", Tab (opens menu), Escape (closes menu), Backspace x3, type "worldfrz", Tab (opens), Tab (commits), Enter
        PromptResult result = await this.RunPromptSessionAsync(
            ConsoleCommandTests.TextKeys("hel")
                .Concat([
                    ConsoleCommandTests.Key(ConsoleKey.Tab),
                    ConsoleCommandTests.Key(ConsoleKey.Escape),
                    ConsoleCommandTests.Key(ConsoleKey.Backspace),
                    ConsoleCommandTests.Key(ConsoleKey.Backspace),
                    ConsoleCommandTests.Key(ConsoleKey.Backspace)
                ])
                .Concat(ConsoleCommandTests.TextKeys("worldfrz"))
                .Concat([
                    ConsoleCommandTests.Key(ConsoleKey.Tab),
                    ConsoleCommandTests.Key(ConsoleKey.Tab),
                    ConsoleCommandTests.Key(ConsoleKey.Enter)
                ])
        );

        // assert
        result.IsSuccess.Should().BeTrue();
        result.Text.Should().Be("world_freezetime");
    }

    [Test]
    public void GetCompletions_CommandArguments_FiltersAndQuotesSpaces()
    {
        // arrange
        CommandManager manager = this.CreateCommandManager();

        // act: first argument with prefix
        IReadOnlyList<CommandCompletionItem> seasonItems = manager.GetCompletions("world_setseason s", caret: 17);

        // assert
        seasonItems.Select(p => p.ReplacementText).Should().Equal("spring", "summer");

        // act: argument containing spaces gets quoted
        IReadOnlyList<CommandCompletionItem> clearItems = manager.GetCompletions("world_clear ", caret: 12);
        CommandCompletionItem? spacedLocation = clearItems.FirstOrDefault(p => p.DisplayText == "Custom Farm");
        spacedLocation.Should().NotBeNull();
        spacedLocation!.Value.ReplacementText.Should().Be("\"Custom Farm\"");
    }

    [Test]
    public void GetCompletions_ScreenArgument_DoesNotShiftArgumentIndex()
    {
        // arrange
        CommandManager manager = this.CreateCommandManager();

        // act: screen=0 is ignored when computing argIndex for the command's own completion callback
        IReadOnlyList<CommandCompletionItem> items = manager.GetCompletions("world_setseason screen=0 f", caret: 26);

        // assert
        items.Select(p => p.ReplacementText).Should().Equal("fall");
    }

    [Test]
    public void GetCompletions_HelpCommand_SuggestsAllRegisteredCommands()
    {
        // arrange
        CommandManager manager = this.CreateCommandManager();

        // act
        IReadOnlyList<CommandCompletionItem> items = manager.GetCompletions("help w", caret: 6);

        // assert
        items.Select(p => p.ReplacementText).Should().Equal("world_clear", "world_setseason", "world_freezetime");
    }

    [Test]
    public void GetCompletions_ThrowingCallback_ReturnsEmptySafely()
    {
        // arrange
        CommandManager manager = this.CreateCommandManager();
        manager.Add(
            mod: null,
            name: "broken_cmd",
            documentation: "Throws during completion.",
            callback: (_, _) => { },
            getCompletions: (_, _, _) => throw new InvalidOperationException("boom")
        );

        // act
        IReadOnlyList<CommandCompletionItem> items = manager.GetCompletions("broken_cmd ", caret: 11);

        // assert
        items.Should().BeEmpty();
    }

    /****
    ** EmacsKeyBindingHandler
    ****/
    [TestCase(ConsoleKey.A, ConsoleModifiers.Control, ConsoleKey.Home, (ConsoleModifiers)0)]
    [TestCase(ConsoleKey.E, ConsoleModifiers.Control, ConsoleKey.End, (ConsoleModifiers)0)]
    [TestCase(ConsoleKey.B, ConsoleModifiers.Control, ConsoleKey.LeftArrow, (ConsoleModifiers)0)]
    [TestCase(ConsoleKey.F, ConsoleModifiers.Control, ConsoleKey.RightArrow, (ConsoleModifiers)0)]
    [TestCase(ConsoleKey.P, ConsoleModifiers.Control, ConsoleKey.UpArrow, (ConsoleModifiers)0)]
    [TestCase(ConsoleKey.N, ConsoleModifiers.Control, ConsoleKey.DownArrow, (ConsoleModifiers)0)]
    [TestCase(ConsoleKey.D, ConsoleModifiers.Control, ConsoleKey.Delete, (ConsoleModifiers)0)]
    [TestCase(ConsoleKey.H, ConsoleModifiers.Control, ConsoleKey.Backspace, (ConsoleModifiers)0)]
    [TestCase(ConsoleKey.G, ConsoleModifiers.Control, ConsoleKey.Escape, (ConsoleModifiers)0)]
    [TestCase(ConsoleKey.B, ConsoleModifiers.Alt, ConsoleKey.LeftArrow, ConsoleModifiers.Control)]
    [TestCase(ConsoleKey.F, ConsoleModifiers.Alt, ConsoleKey.RightArrow, ConsoleModifiers.Control)]
    public void EmacsHandler_NavigationAndDeleteMappings(ConsoleKey inputKey, ConsoleModifiers inputMods, ConsoleKey expectedKey, ConsoleModifiers expectedMods)
    {
        // arrange
        var handler = new EmacsKeyBindingHandler();
        var keyPress = new KeyPress(new ConsoleKeyInfo('\0', inputKey, shift: false, alt: inputMods.HasFlag(ConsoleModifiers.Alt), control: inputMods.HasFlag(ConsoleModifiers.Control)));

        // act
        KeyPress result = handler.TransformKeyPress(keyPress);

        // assert
        result.ConsoleKeyInfo.Key.Should().Be(expectedKey);
        result.ConsoleKeyInfo.Modifiers.Should().Be(expectedMods);
    }

    [Test]
    public void EmacsHandler_KillAndYank_UpdatesBufferAndKillRing()
    {
        // arrange
        var handler = new EmacsKeyBindingHandler();

        // Ctrl+K: kill to end
        handler.TransformKeyPress(ConsoleCommandTests.Ctrl(ConsoleKey.K));
        (string text, int caret) = handler.ApplyPendingEdit("world_setseason spring", 15);
        text.Should().Be("world_setseason");
        caret.Should().Be(15);
        handler.LastKilledText.Should().Be(" spring");

        // Ctrl+Y: yank back
        handler.TransformKeyPress(ConsoleCommandTests.Ctrl(ConsoleKey.Y));
        (text, caret) = handler.ApplyPendingEdit(text, caret);
        text.Should().Be("world_setseason spring");
        caret.Should().Be(22);

        // Ctrl+W: kill previous word
        handler.TransformKeyPress(ConsoleCommandTests.Ctrl(ConsoleKey.W));
        (text, caret) = handler.ApplyPendingEdit(text, caret);
        text.Should().Be("world_setseason ");
        caret.Should().Be(16);
        handler.LastKilledText.Should().Be("spring");

        // Ctrl+U: kill to start
        handler.TransformKeyPress(ConsoleCommandTests.Ctrl(ConsoleKey.U));
        (text, caret) = handler.ApplyPendingEdit("help world_setseason", 5);
        text.Should().Be("world_setseason");
        caret.Should().Be(0);
        handler.LastKilledText.Should().Be("help ");

        // Alt+D: kill next word
        handler.TransformKeyPress(ConsoleCommandTests.Alt(ConsoleKey.D));
        (text, caret) = handler.ApplyPendingEdit("world_setseason spring", 0);
        text.Should().Be(" spring");
        caret.Should().Be(0);
        handler.LastKilledText.Should().Be("world_setseason");
    }


    /*********
    ** Private methods
    *********/
    /// <summary>Create a <see cref="CommandManager"/> populated with sample commands.</summary>
    private CommandManager CreateCommandManager()
    {
        IMonitor monitor = Mock.Of<IMonitor>();
        var manager = new CommandManager(monitor);

        manager.Add(new HelpCommand(manager), monitor);
        manager.Add(
            mod: null,
            name: "world_setseason",
            documentation: "Sets the season.\n\nUsage: world_setseason <season>",
            callback: (_, _) => { },
            getCompletions: (_, _, argIndex) => argIndex == 0 ? ["spring", "summer", "fall", "winter"] : []
        );
        manager.Add(
            mod: null,
            name: "world_freezetime",
            documentation: "Freezes time.\n\nUsage: world_freezetime [0|1]",
            callback: (_, _) => { },
            getCompletions: (_, _, argIndex) => argIndex == 0 ? ["0", "1"] : []
        );
        manager.Add(
            mod: null,
            name: "world_clear",
            documentation: "Clears entities.\n\nUsage: world_clear <location> <type>",
            callback: (_, _) => { },
            getCompletions: (_, _, argIndex) => argIndex switch
            {
                0 => ["current", "Farm", "Custom Farm"],
                1 => ["crops", "debris", "trees"],
                _ => []
            }
        );

        return manager;
    }

    /// <summary>Create a Ctrl+key press.</summary>
    private static KeyPress Ctrl(ConsoleKey key)
    {
        return new KeyPress(new ConsoleKeyInfo('\0', key, shift: false, alt: false, control: true));
    }

    /// <summary>Create an Alt+key press.</summary>
    private static KeyPress Alt(ConsoleKey key)
    {
        return new KeyPress(new ConsoleKeyInfo('\0', key, shift: false, alt: true, control: false));
    }

    /// <summary>Create a plain key info.</summary>
    private static ConsoleKeyInfo Key(ConsoleKey key)
    {
        char ch = key switch
        {
            ConsoleKey.Tab => '\t',
            ConsoleKey.Enter => '\r',
            ConsoleKey.Backspace => '\b',
            _ => '\0'
        };
        return new ConsoleKeyInfo(ch, key, shift: false, alt: false, control: false);
    }

    /// <summary>Convert ASCII text into a sequence of <see cref="ConsoleKeyInfo"/> key presses.</summary>
    private static IEnumerable<ConsoleKeyInfo> TextKeys(string text)
    {
        foreach (char ch in text)
        {
            ConsoleKey key = ch switch
            {
                >= 'a' and <= 'z' => ConsoleKey.A + (ch - 'a'),
                >= 'A' and <= 'Z' => ConsoleKey.A + (ch - 'A'),
                '_' => ConsoleKey.OemMinus,
                ' ' => ConsoleKey.Spacebar,
                _ => ConsoleKey.NoName
            };
            yield return new ConsoleKeyInfo(ch, key, shift: char.IsUpper(ch) || ch == '_', alt: false, control: false);
        }
    }

    /// <summary>Run an end-to-end <see cref="Prompt"/> session with a scripted sequence of keys.</summary>
    private async Task<PromptResult> RunPromptSessionAsync(IEnumerable<ConsoleKeyInfo> keys, IReadOnlyList<string>? history = null)
    {
        CommandManager manager = this.CreateCommandManager();
        var console = new FakePromptConsole(keys);
        var callbacks = new SmapiPromptCallbacks(manager, coordinatedConsole: null, new EmacsKeyBindingHandler(() => history ?? []), () => null, console.EnqueuePriorityKey);

        var keyBindings = new KeyBindings(
            commitCompletion: new KeyPressPatterns(new KeyPressPattern(ConsoleKey.Enter), new KeyPressPattern(ConsoleKey.Tab)),
            triggerCompletionList: new KeyPressPatterns(new KeyPressPattern(ConsoleModifiers.Control, ConsoleKey.Spacebar)),
            newLine: new KeyPressPatterns(),
            submitPrompt: new KeyPressPatterns(new KeyPressPattern(ConsoleKey.Enter)),
            historyPrevious: new KeyPressPatterns(new KeyPressPattern(ConsoleKey.UpArrow)),
            historyNext: new KeyPressPatterns(new KeyPressPattern(ConsoleKey.DownArrow))
        );

        var prompt = new Prompt(
            persistentHistoryFilepath: null,
            callbacks: callbacks,
            console: console,
            configuration: new PromptConfiguration(keyBindings: keyBindings, prompt: "smapi$ ")
        );

        callbacks.ResetLineState();
        return await prompt.ReadLineAsync();
    }

    /// <summary>A scripted in-memory <see cref="IConsole"/> for testing <see cref="Prompt"/>.</summary>
    private sealed class FakePromptConsole : IConsole
    {
        private readonly Queue<ConsoleKeyInfo> PriorityKeys = new();
        private readonly Queue<ConsoleKeyInfo> Keys;

        public int CursorTop => 0;
        public int BufferWidth => 120;
        public int WindowHeight => 40;
        public int WindowTop => 0;
        public bool KeyAvailable => false; // simulate interactive keystrokes rather than paste burst
        public bool IsErrorRedirected => false;
        public bool CaptureControlC { get; set; }
        public event ConsoleCancelEventHandler? CancelKeyPress { add { } remove { } }

        public FakePromptConsole(IEnumerable<ConsoleKeyInfo> keys)
        {
            this.Keys = new Queue<ConsoleKeyInfo>(keys);
        }

        public void EnqueuePriorityKey(ConsoleKeyInfo key) => this.PriorityKeys.Enqueue(key);

        public ConsoleKeyInfo ReadKey(bool intercept) => this.PriorityKeys.Count > 0 ? this.PriorityKeys.Dequeue() : this.Keys.Dequeue();
        public void Write(string? value) { }
        public void WriteLine(string? value) { }
        public void WriteError(string? value) { }
        public void WriteErrorLine(string? value) { }
        public void Write(ReadOnlySpan<char> value) { }
        public void WriteLine(ReadOnlySpan<char> value) { }
        public void WriteError(ReadOnlySpan<char> value) { }
        public void WriteErrorLine(ReadOnlySpan<char> value) { }
        public void Write(StringBuilder value, bool hideCursor = false) { }
        public void WriteLine(StringBuilder value, bool hideCursor = false) { }
        public void WriteError(StringBuilder value, bool hideCursor = false) { }
        public void WriteErrorLine(StringBuilder value, bool hideCursor = false) { }
        public void Clear() { }
        public void ShowCursor() { }
        public void HideCursor() { }
        public void InitVirtualTerminalProcessing() { }
    }
}
