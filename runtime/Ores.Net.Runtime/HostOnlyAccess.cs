using System.Runtime.CompilerServices;

// Only the trusted executable and dedicated verification assembly can call
// internal host socket connectors. Never expose raw Socket to generated guest
// assemblies; future guest networking must use revocable host-owned handles.
[assembly: InternalsVisibleTo("Ores.Net.NativeHost")]
[assembly: InternalsVisibleTo("Ores.Net.Tests")]
