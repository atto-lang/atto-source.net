using Ores.Net.Runtime;

// Diagnostic host only. The source compiler generated every imported method;
// Native AOT statically links them. There is no dynamic assembly load or JIT.
return await RuntimeHost.ExecuteAsync(Oreslang.Generated.OresProgram.Run);
