using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading;

namespace StardewModdingAPI.Framework;

/// <summary>A thread-safe command queue optimized for infrequent changes.</summary>
internal class CommandQueue
{
    /********
    ** Fields
    ********/
    /// <summary>The underlying list of queued commands to parse and execute.</summary>
    private readonly List<QueuedRawCommand> RawCommandQueue = [];


    /********
    ** Public methods
    ********/
    /// <summary>Add a command to the queue.</summary>
    /// <param name="command">The command to add.</param>
    public void Add(string command)
    {
        lock (this.RawCommandQueue)
            this.RawCommandQueue.Add(new QueuedRawCommand(command, Completed: null));
    }

    /// <summary>Add a command to the queue and wait briefly for the game loop to finish executing it, so its console output appears before the next prompt.</summary>
    /// <param name="command">The command to add.</param>
    /// <param name="timeoutMs">The maximum milliseconds to wait before returning anyway.</param>
    public void AddAndWait(string command, int timeoutMs = 300)
    {
        using ManualResetEventSlim completed = new(false);

        lock (this.RawCommandQueue)
            this.RawCommandQueue.Add(new QueuedRawCommand(command, completed));

        completed.Wait(timeoutMs);
    }

    /// <summary>Remove and return all queued commands, if any.</summary>
    /// <param name="queued">The commands that were dequeued, in the order they were originally queued.</param>
    /// <returns>Returns whether any values were dequeued.</returns>
    [SuppressMessage("ReSharper", "InconsistentlySynchronizedField", Justification = "Deliberately check if it's empty before locking unnecessarily.")]
    public bool TryDequeue([NotNullWhen(true)] out QueuedRawCommand[]? queued)
    {
        if (this.RawCommandQueue.Count is 0)
        {
            queued = null;
            return false;
        }

        lock (this.RawCommandQueue)
        {
            queued = this.RawCommandQueue.ToArray();
            this.RawCommandQueue.Clear();
            return queued.Length > 0;
        }
    }
}

/// <summary>A raw console command queued for parsing and execution.</summary>
/// <param name="Input">The raw command line entered by the user.</param>
/// <param name="Completed">An optional signal set after the command finishes executing (or fails to parse).</param>
internal readonly record struct QueuedRawCommand(string Input, ManualResetEventSlim? Completed);
