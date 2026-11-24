using System.Collections.Concurrent;
using System.Net;
using Ores.Net.Runtime;
using Ores.Net.Runtime.Http;
using Oreslang.Generated;

using var actors = new ActorRuntime(new ActorRuntimeOptions
{
    MaxActors = 48,
    ActorPersistentBytes = 4096,
    ActorScratchBytes = 1024,
    NativeBudgetBytes = 4 * 1024 * 1024,
    Dispatcher = new ActorDispatcherOptions
    {
        SharedWorkers = 2, PrivateWorkers = 2, UntrustedWorkers = 1,
        QueueCapacityPerDomain = 64
    }
});
var resumed = new ConcurrentDictionary<ActorRef, ActorKind>();
ActorHttpEndpoint Resolve(string method, string path)
{
    var endpoint = HttpActors.Bind((method, path) switch
    {
        ("GET", "/hello") => "SharedHello",
        ("POST", "/echo") => "IsolatedEcho",
        _ => throw new InvalidOperationException("Unexpected verification route")
    });
    return endpoint with
    {
        AfterResponse = (context, runtime) =>
        {
            // Actor memory access authenticates the new physical turn.
            var scratch = context.AllocateScratch(1);
            context.WriteScratch(scratch, 0, [0x2a]);
            if (!resumed.TryAdd(context.Self, context.Kind))
                throw new InvalidOperationException("Response continuation resumed more than once");
            endpoint.AfterResponse!(context, runtime);
        }
    };
}

await using var server = await ActorHttpServer.StartAsync(actors, Resolve,
    new ActorHttpServerOptions { MaxRequests = 32, MaxBodyBytes = 1024, RequestTimeout = TimeSpan.FromSeconds(4) });
using var client = new HttpClient { BaseAddress = server.Address, Timeout = TimeSpan.FromSeconds(6) };
await Task.WhenAll(Enumerable.Range(0, 32).Select(async index =>
{
    if (index % 2 == 0)
    {
        if (await client.GetStringAsync("/hello") != "hello from shared actor")
            throw new InvalidOperationException("Compiled shared handler response mismatch");
    }
    else
    {
        string body = $"echo payload {index}";
        using var response = await client.PostAsync("/echo", new StringContent(body));
        if (response.StatusCode != HttpStatusCode.Created || await response.Content.ReadAsStringAsync() != body)
            throw new InvalidOperationException("Compiled isolated handler did not echo the owned body");
    }
}));

using var quiescence = new CancellationTokenSource(TimeSpan.FromSeconds(6));
while (server.ActiveRequests != 0 || actors.LiveActors != 0 || actors.NativeReservedBytes != 0)
    await Task.Delay(10, quiescence.Token);
if (resumed.Values.Count(kind => kind == ActorKind.Shared) != 16 ||
    resumed.Values.Count(kind => kind == ActorKind.Private) != 16)
    throw new InvalidOperationException("Every request must resume once on an actor of the original kind");
Console.WriteLine("PASS .NET compiled Atto HTTP handlers: 32 responses and scheduler continuations, zero live exchanges or actor allocations");
