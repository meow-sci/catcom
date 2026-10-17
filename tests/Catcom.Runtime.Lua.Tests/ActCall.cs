using Catcom.Core.Hosting;
using Catcom.Core.Values;

namespace Catcom.Runtime.Lua.Tests;

/// <summary>One recorded <see cref="IHostFacade.Act"/> call.</summary>
public sealed record ActCall(string Action, string? VesselId, int Ordinal, double Value, double[]? Values, string? Token, string? Aux);
