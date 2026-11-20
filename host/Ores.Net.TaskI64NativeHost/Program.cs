using Ores.Net.Runtime;
using System.Globalization;

try
{
    var receipt = await RootTaskI64.RunAsync(Oreslang.Generated.OresProgram.RunTaskI64Producer);
    if (receipt.ProducerExecutions != 1 || !receipt.ObserverDistinct || !receipt.ProducerBodyRetired)
        throw new InvalidOperationException("Task-i64 root lifecycle receipt failed");
    await Console.Out.WriteLineAsync(receipt.Value.ToString(CultureInfo.InvariantCulture));
    await Console.Error.WriteLineAsync($"TASK_I64_RECEIPT; producers={receipt.ProducerExecutions}; observer_distinct={receipt.ObserverDistinct}; producer_body_retired={receipt.ProducerBodyRetired}");
    return 0;
}
catch (OverflowException)
{
    await Console.Error.WriteLineAsync("Task-i64 producer FAILED: OverflowException");
    return 70;
}
