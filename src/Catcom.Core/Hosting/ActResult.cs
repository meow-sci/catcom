using Catcom.Core.Commands;

namespace Catcom.Core.Hosting;

/// <summary>
///     The result of <see cref="IHostFacade.Act"/>: either an immediate outcome (Frame-phase actions) or a
///     pending wait (Solver-phase actions, resolved in a later Step slot).
/// </summary>
/// <param name="Immediate">The outcome when the command executed immediately; otherwise <c>null</c>.</param>
/// <param name="Pending">The wait that completes with the outcome map; otherwise <c>null</c>.</param>
public sealed record ActResult(CommandResult? Immediate, HostWait? Pending);
