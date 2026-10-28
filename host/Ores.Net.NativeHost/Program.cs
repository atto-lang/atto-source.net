using Ores.Net.Runtime;

// Direct static call to generated CIL; Native AOT sees this reference at publish.
// Host-visible output is asynchronous; guest/core runtime code must not block on stdio.
return await RuntimeHost.ExecuteAsync(Oreslang.Generated.OresProgram.Run);
