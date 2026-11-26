using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Ores.Net.Runtime;
using Ores.Net.Runtime.Http;

static void Require(bool value, string label)
{ if (!value) throw new Exception("HTTP probe failed: " + label); }
static bool Denied(Action action)
{ try { action(); return false; } catch (InvalidOperationException) { return true; } }
static bool BadHeader(Action action)
{ try { action(); return false; } catch (ArgumentException) { return true; } }
static async Task Until(Func<bool> condition, Func<string>? describe = null)
{
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    try { while (!condition()) await Task.Delay(5, deadline.Token); }
    catch (OperationCanceledException) when (deadline.IsCancellationRequested)
    {
        throw new TimeoutException("HTTP probe did not quiesce " + describe?.Invoke());
    }
}

using var actors = new ActorRuntime(new ActorRuntimeOptions
{
    MaxActors = 32, ActorPersistentBytes = 4096, ActorScratchBytes = 1024,
    NativeBudgetBytes = 1024 * 1024,
    Dispatcher = new ActorDispatcherOptions
    { PrivateWorkers = 1, SharedWorkers = 1, UntrustedWorkers = 1, QueueCapacityPerDomain = 32 }
});
var ids = new ConcurrentDictionary<ulong, bool>();
ActorHttpExchange? retained = null;
ActorContext? retainedContext = null;
int denials = 0;
int responseContinuations = 0;
void Resume(ActorContext context, ActorRuntime runtime)
{
    Interlocked.Increment(ref responseContinuations);
    runtime.Stop(context.Self);
}
ActorHttpEndpoint Resolve(string method, string path) => new(
    path == "/private" ? ActorKind.Private : ActorKind.Shared,
    (context, exchange) =>
    {
        Require(ids.TryAdd(context.Self.Id, true), "fresh actor per request");
        if (path == "/fault") throw new Exception("deliberate handler fault");
        if (path == "/timeout") return;
        if (path == "/move")
        {
            retained = exchange; retainedContext = context;
            HttpExchangeTicket? ticket = null;
            var target = actors.Spawn(ActorKind.Private, (receiver, _) =>
            {
                var moved = ActorHttpExchange.Claim(receiver, ticket!);
                Require(Denied(() => ActorHttpExchange.Claim(receiver, ticket!)), "double claim denied");
                Interlocked.Increment(ref denials);
                byte[] body = new byte[moved.BodyLength(receiver)];
                moved.CopyBody(receiver, body);
                Require(Encoding.UTF8.GetString(body) == "owned-body", "body transferred to private arena");
                moved.Respond(receiver, 200, "text/plain", "moved");
                actors.Stop(receiver.Self);
            });
            ticket = exchange.Move(context, target);
            Require(Denied(() => exchange.Path(context)), "sender alias revoked before delivery");
            Require(Denied(() => exchange.SetResponseHeader(context, "X-Former-Owner", "stale")),
                "former exchange owner cannot alter headers");
            Interlocked.Increment(ref denials);
            var wrong = actors.Spawn(ActorKind.Shared, (intruder, _) =>
            {
                Require(Denied(() => ActorHttpExchange.Claim(intruder, ticket)), "wrong owner rejected without consuming claim");
                Interlocked.Increment(ref denials);
                actors.Send(target, []);
                actors.Stop(intruder.Self);
            });
            actors.Send(wrong, []);
            actors.Stop(context.Self);
            return;
        }
        string response = path == "/private" ? "private" : "shared";
        if (path == "/echo")
        {
            byte[] body = new byte[exchange.BodyLength(context)];
            exchange.CopyBody(context, body);
            response = Encoding.UTF8.GetString(body);
        }
        if (path == "/oversized-response") response = new string('x', 2048);
        if (path == "/metadata")
        {
            Require(BadHeader(() => exchange.SetResponseHeader(context, "cOnTeNt-LeNgTh", "0")),
                "actor cannot set message framing");
            Require(BadHeader(() => exchange.SetResponseHeader(context, "X-Probe", "before\r\nafter")),
                "multiline header value denied");
            for (int i = 0; i < 31; i++)
                exchange.SetResponseHeader(context, $"X-Probe-{i}", new string('x', 240));
            Require(BadHeader(() => exchange.SetResponseHeader(context, "X-Over-Budget", new string('x', 1024))),
                "aggregate header bytes bounded");
            exchange.SetResponseHeader(context, "X-Request-Id", exchange.RequestId(context));
            Require(BadHeader(() => exchange.SetResponseHeader(context, "X-Extra-Header", "no")),
                "header count bounded");
        }
        exchange.Respond(context, 200, "text/plain", response);
        if (path == "/metadata")
            Require(Denied(() => exchange.SetResponseHeader(context, "X-Probe-0", "late")),
                "headers cannot change after response admission");
        if (path == "/duplicate")
        {
            Require(Denied(() => exchange.Respond(context, 200, "text/plain", "duplicate")), "one writer");
            Interlocked.Increment(ref denials);
        }
        if (path is not ("/linger" or "/resume")) actors.Stop(context.Self);
    })
    {
        AfterResponse = path is "/resume" or "/fault" or "/oversized-response" or "/timeout"
            ? Resume : null
    };

await using var server = await ActorHttpServer.StartAsync(actors, Resolve,
    new ActorHttpServerOptions { MaxRequests = 16, MaxBodyBytes = 1024, MaxResponseBytes = 1024, RequestTimeout = TimeSpan.FromSeconds(2) });
using var client = new HttpClient { BaseAddress = server.Address, Timeout = TimeSpan.FromSeconds(6) };
Require(await client.GetStringAsync("/shared") == "shared", "shared HTTP actor");
Require(await client.GetStringAsync("/private") == "private", "isolated HTTP actor");
using (var echo = await client.PostAsync("/echo", new StringContent("body-roundtrip")))
    Require(await echo.Content.ReadAsStringAsync() == "body-roundtrip", "actor-owned body");
using (var moved = await client.PostAsync("/move", new StringContent("owned-body")))
    Require(await moved.Content.ReadAsStringAsync() == "moved", "exchange transfer over real HTTP");
Require(Denied(() => retained!.Path(retainedContext!)), "retained physical turn denied");
Require(Denied(() => retained!.SetResponseHeader(retainedContext!, "X-Stale-Turn", "rejected")),
    "retained turn cannot edit headers");
Interlocked.Increment(ref denials);
Require(await client.GetStringAsync("/duplicate") == "shared", "duplicate response rejected");
Require(await client.GetStringAsync("/resume") == "shared", "response precedes continuation");
using (var metadata = await client.GetAsync("/metadata"))
    Require(metadata.IsSuccessStatusCode &&
        Guid.TryParseExact(metadata.Headers.GetValues("X-Request-Id").Single(), "D", out _) &&
        metadata.Headers.GetValues("X-Probe-0").Single() == new string('x', 240),
        "bounded staged headers commit with the response");
using (var head = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, "/shared")))
    Require((await head.Content.ReadAsByteArrayAsync()).Length == 0 && head.Content.Headers.ContentLength == 6, "HEAD body suppression");
using (var tooLarge = await client.PostAsync("/echo", new ByteArrayContent(new byte[1025])))
    Require(tooLarge.StatusCode == HttpStatusCode.RequestEntityTooLarge, "body admission limit");
using (var fault = await client.GetAsync("/fault")) Require(fault.StatusCode == HttpStatusCode.InternalServerError, "handler fault containment");
using (var bad = await client.GetAsync("/oversized-response")) Require(bad.StatusCode == HttpStatusCode.InternalServerError, "response bound");
var requests = Enumerable.Range(0, 12).Select(i => client.GetStringAsync(i % 2 == 0 ? "/private" : "/shared"));
Require((await Task.WhenAll(requests)).Length == 12, "concurrent single-carrier progress");
using (var timeout = await client.GetAsync("/timeout")) Require(timeout.StatusCode == HttpStatusCode.GatewayTimeout, "deadline retires nonresponding actor");
string DescribeMainServer() =>
    $"alive={actors.LiveActors}, native bytes={actors.NativeReservedBytes}, admitted={server.ActiveRequests}";
await Until(() => actors.LiveActors == 0 && actors.NativeReservedBytes == 0 && server.ActiveRequests == 0,
    DescribeMainServer);
using (var canceled = new CancellationTokenSource(TimeSpan.FromMilliseconds(80)))
{
    try { await client.GetAsync("/timeout", canceled.Token); throw new Exception("client cancellation did not interrupt request"); }
    catch (OperationCanceledException) when (canceled.IsCancellationRequested) { }
}
await Until(() => actors.LiveActors == 0 && actors.NativeReservedBytes == 0 && server.ActiveRequests == 0,
    DescribeMainServer);
Require(await client.GetStringAsync("/shared") == "shared", "disconnect recovery");
Require(responseContinuations == 1, "only a completed response resumes its actor");

await using (var bounded = await ActorHttpServer.StartAsync(actors, Resolve,
    new ActorHttpServerOptions { MaxRequests = 1, MaxBodyBytes = 1024, RequestTimeout = TimeSpan.FromMilliseconds(350) }))
{
    using var boundedClient = new HttpClient { BaseAddress = bounded.Address, Timeout = TimeSpan.FromSeconds(5) };
    Require(await boundedClient.GetStringAsync("/linger") == "shared", "response before actor completion");
    using var overload = await boundedClient.GetAsync("/shared");
    Require(overload.StatusCode == HttpStatusCode.ServiceUnavailable, "admission retained until actor finalization");
    await Until(() => bounded.ActiveRequests == 0);
    Require(await boundedClient.GetStringAsync("/private") == "private", "admission recovers after deadline");
}
await Until(() => actors.LiveActors == 0 && actors.NativeReservedBytes == 0);
var shutdown = await ActorHttpServer.StartAsync(actors, Resolve,
    new ActorHttpServerOptions { MaxRequests = 1, RequestTimeout = TimeSpan.FromSeconds(30) });
using (var shutdownClient = new HttpClient { BaseAddress = shutdown.Address })
{
    Require(await shutdownClient.GetStringAsync("/linger") == "shared", "live actor before shutdown");
    await shutdown.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
}
await Until(() => actors.LiveActors == 0 && actors.NativeReservedBytes == 0);
Require(denials == 5, "all ownership/write denials exercised");
Console.WriteLine("PASS .NET HTTP real sockets: fresh shared/private actors, transfer/claim revocation, arena body, one writer, HEAD, limits, concurrency, faults, deadlines, finalization and zero native leaks");
