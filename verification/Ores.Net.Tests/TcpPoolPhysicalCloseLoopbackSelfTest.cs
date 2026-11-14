using System.Net;
using System.Net.Sockets;
using Ores.Net.Runtime.Net;

// Host-only real TCP proof: lease release/completion cannot recycle a
// physical connection before the owner actually closes it. This does NOT
// expose network I/O to compiled Oreslang or implement socket pooling.
internal static class TcpPoolPhysicalCloseLoopbackSelfTest
{
    private const string Partition = "tenant-a|plain|127.0.0.1";
    private static readonly TcpPoolLeaseLedger.ActorIdentity ActorA = new(101, 1);
    private static readonly TcpPoolLeaseLedger.ActorIdentity ActorB = new(102, 1);

    private static void Require(bool valid, string why)
    {
        if (!valid) throw new Exception("TCP host loopback: " + why);
    }

    internal static async Task RunAsync()
    {
        var pool = new TcpPoolLeaseLedger(1, Partition, 7);
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var serverTask = Task.Run(async () =>
            {
                for (int round = 0; round < 2; round++)
                {
                    using var accepted = await listener.AcceptTcpClientAsync()
                        .WaitAsync(TimeSpan.FromSeconds(4));
                    using var input = accepted.GetStream();
                    var request = new byte[1];
                    var length = await input.ReadAsync(request)
                        .AsTask().WaitAsync(TimeSpan.FromSeconds(4));
                    Require(length == 1 && request[0] == 41 + round,
                        "wrong request byte");
                    await input.WriteAsync(new byte[] { (byte)(42 + round) })
                        .AsTask().WaitAsync(TimeSpan.FromSeconds(4));
                }
            });
            for (int round = 0; round < 2; round++)
            {
                var actor = round == 0 ? ActorA : ActorB;
                Require(pool.TryAcquire(0, actor, Partition, 7, out var lease)
                    && lease is not null, "fresh physical slot not admitted");
                Require(pool.TryBegin(lease, actor, 7), "exclusive I/O admission");
                Require(!pool.TryAcquire(0, ActorB, Partition, 7, out _),
                    "overlapping actor acquired socket");
                using (var client = new TcpClient())
                {
                    await client.ConnectAsync(IPAddress.Loopback, port)
                        .WaitAsync(TimeSpan.FromSeconds(4));
                    var transport = client.GetStream();
                    await transport.WriteAsync(new byte[] { (byte)(41 + round) })
                        .AsTask().WaitAsync(TimeSpan.FromSeconds(4));
                    var response = new byte[1];
                    var count = await transport.ReadAsync(response)
                        .AsTask().WaitAsync(TimeSpan.FromSeconds(4));
                    Require(count == 1 && response[0] == 42 + round,
                        "wrong response");
                    Require(pool.TryRelease(lease, actor, 7), "revoke actor");
                    Require(!pool.TryConfirmPhysicalClose(0, lease!.Generation + 1),
                        "physical close acknowledged with active I/O");
                    Require(pool.TryComplete(lease), "physical completion");
                    Require(!pool.TryAcquire(0, ActorB, Partition, 7, out _),
                        "completion incorrectly recycled open socket");
                }
                // Only a trusted owner calls this after the actual socket
                // disposal; actor release and Future settlement are not proof.
                Require(!pool.TryConfirmPhysicalClose(0, lease!.Generation),
                    "old generation unexpectedly accepted");
                Require(pool.TryConfirmPhysicalClose(0, lease.Generation + 1),
                    "closed physical slot did not rearm");
                Require(!pool.TryComplete(lease), "old completion replay");
            }
            await serverTask.WaitAsync(TimeSpan.FromSeconds(8));
            Require(pool.TryStop(), "terminal shutdown");
            Require(!pool.TryAcquire(0, ActorA, Partition, 7, out _),
                "STOP resurrected socket");
        }
        finally
        {
            listener.Stop();
        }
    }
}
