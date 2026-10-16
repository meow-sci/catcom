using KsaScripting.Core.Hosting;
using KsaScripting.Core.Values;

namespace KsaScripting.Runtime.Lua.Tests;

/// <summary>One recorded <see cref="IHostFacade.Act"/> call.</summary>
public sealed record ActCall(string Action, string? VesselId, int Ordinal, double Value, double[]? Values, string? Token, string? Aux);
