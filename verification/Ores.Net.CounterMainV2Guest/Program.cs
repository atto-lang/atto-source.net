using Oreslang.Generated;

if (System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported)
    throw new InvalidOperationException("Counter main native guest requires fully static NativeAOT");
string result = await CounterMain_V2.RunAsync();
Console.Out.Write(result);
