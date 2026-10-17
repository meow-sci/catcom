using System.Runtime.CompilerServices;

// KsaScripting.PurrTty (the only assembly that references purrTTY types) is loaded by file path at run time and
// keeps using this assembly's internal types (ScriptingHost members, ReplFlavor, ModInfo, ...).
[assembly: InternalsVisibleTo("KsaScripting.PurrTty")]
