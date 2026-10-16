using KsaScripting.Core.Commands;
using KsaScripting.Core.Hosting;
using KsaScripting.Core.Values;

namespace KsaScripting.Core.Tests.Support;

/// <summary>An <see cref="IGameBridge"/> for tests: records commands, lets tests deliver Solver results and script the reads.</summary>
internal sealed class FakeGameBridge : IGameBridge
{
    private readonly Dictionary<(string Handle, string Path), ScriptValue> _reads = [];
    private readonly Dictionary<string, ScriptValue> _snapshots = [];
    private readonly Dictionary<string, IReadOnlyList<string>> _lists = [];

    /// <summary>Every command passed to <see cref="Execute"/>.</summary>
    public List<SimCommand> Executed { get; } = [];

    /// <summary>Every command passed to <see cref="QueueSolver"/>, with the result callback.</summary>
    public List<(SimCommand Command, Action<CommandResult> OnResult)> QueuedSolvers { get; } = [];

    /// <summary>Every console command.</summary>
    public List<string> ConsoleCommands { get; } = [];

    /// <summary>Every <c>(handle, path)</c> pair passed to <see cref="Read"/>.</summary>
    public List<(string Handle, string Path)> ReadCalls { get; } = [];

    /// <summary>The result of <see cref="Execute"/> (default: <see cref="CommandResult.Ok"/>).</summary>
    public Func<SimCommand, CommandResult> ExecuteResult { get; set; } = _ => CommandResult.Ok;

    /// <summary>Makes <see cref="Execute"/> throw.</summary>
    public Exception? ThrowOnExecute { get; set; }

    /// <summary>Makes <see cref="QueueSolver"/> throw.</summary>
    public Exception? ThrowOnQueueSolver { get; set; }

    /// <summary>Makes <see cref="Read"/> throw.</summary>
    public Exception? ThrowOnRead { get; set; }

    /// <inheritdoc />
    public double UtSeconds { get; set; }

    /// <inheritdoc />
    public double SimDt { get; set; } = 0.02;

    /// <inheritdoc />
    public double Warp { get; set; } = 1;

    /// <inheritdoc />
    public bool Paused { get; set; }

    /// <inheritdoc />
    public ulong FrameNumber { get; set; }

    /// <inheritdoc />
    public IReadOnlyList<EventDescriptor> EventCatalog { get; set; } =
    [
        new EventDescriptor("engine.ignite", "An engine ignited.", ["engine"], "differ"),
        new EventDescriptor("stage", "The vessel staged.", [], "harmony"),
    ];

    /// <summary>Scripts the value <see cref="Read"/> returns for a handle/path.</summary>
    public void SetRead(string handle, string path, ScriptValue value) => _reads[(handle, path)] = value;

    /// <summary>Sets the active vessel id (<c>Read("time", "activeVesselId")</c>).</summary>
    public void SetActiveVessel(string? id)
        => SetRead("time", "activeVesselId", id is null ? ScriptValue.Null : ScriptValue.From(id));

    /// <summary>Scripts <see cref="Snapshot"/>.</summary>
    public void SetSnapshot(string handle, ScriptValue value) => _snapshots[handle] = value;

    /// <summary>Scripts <see cref="List"/>.</summary>
    public void SetList(string kind, params string[] items) => _lists[kind] = items;

    /// <summary>Delivers the result of the n-th queued Solver command.</summary>
    public void CompleteSolver(int index, CommandResult result) => QueuedSolvers[index].OnResult(result);

    /// <inheritdoc />
    public ScriptValue Read(string handle, string path)
    {
        ReadCalls.Add((handle, path));
        if (ThrowOnRead is not null)
            throw ThrowOnRead;
        return _reads.TryGetValue((handle, path), out var value) ? value : ScriptValue.Null;
    }

    /// <inheritdoc />
    public ScriptValue Snapshot(string handle)
        => _snapshots.TryGetValue(handle, out var value) ? value : ScriptValue.Null;

    /// <inheritdoc />
    public IReadOnlyList<string> List(string kind)
        => _lists.TryGetValue(kind, out var items) ? items : [];

    /// <inheritdoc />
    public CommandResult Execute(SimCommand command)
    {
        Executed.Add(command);
        if (ThrowOnExecute is not null)
            throw ThrowOnExecute;
        return ExecuteResult(command);
    }

    /// <inheritdoc />
    public void QueueSolver(SimCommand command, Action<CommandResult> onResult)
    {
        if (ThrowOnQueueSolver is not null)
            throw ThrowOnQueueSolver;
        QueuedSolvers.Add((command, onResult));
    }

    /// <inheritdoc />
    public void ConsoleExec(string command) => ConsoleCommands.Add(command);
}
