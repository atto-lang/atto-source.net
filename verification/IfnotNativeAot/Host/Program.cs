// AOT must link the compiler-emitted OresGenerated assembly. No dynamic
// loading, reflection, interpreter, or generated-C# stand-in is permitted.
return checked((int)Oreslang.Generated.OresProgram.Run());
