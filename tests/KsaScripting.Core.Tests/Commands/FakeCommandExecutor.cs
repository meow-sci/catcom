using KsaScripting.Core.Commands;

namespace KsaScripting.Core.Tests.Commands;

/// <summary>A drainable executor double: records calls and can be made to throw or return a result.</summary>
internal sealed class FakeCommandExecutor : ICommandExecutor
{
    public CommandResult Result { get; set; } = CommandResult.Ok;
    public bool Throw { get; set; }
    public SimCommand? Last { get; private set; }
    public int Count { get; private set; }

    /// <summary>Every executed command, in order (for batch ordering asserts).</summary>
    public List<SimCommand> All { get; } = [];

    /// <summary>Optional per-command result override; falls back to <see cref="Result"/>.</summary>
    public Func<SimCommand, CommandResult>? OnExecute { get; set; }

    public CommandResult Execute(SimCommand command)
    {
        Last = command;
        All.Add(command);
        Count++;
        if (Throw)
            throw new InvalidOperationException("boom");
        return OnExecute?.Invoke(command) ?? Result;
    }
}
