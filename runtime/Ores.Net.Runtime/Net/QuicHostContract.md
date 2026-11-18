# Native QUIC host primitive — policy gated, not guest supported

`NativeQuicClient` contains a private `System.Net.Quic` / MsQuic TLS 1.3 connection implementation, but the **only accessible HTTP/3 connection entrypoint is fail-closed**: `ConnectHttp3Async` rejects all otherwise valid attempts with `SecurityException` because no unforgeable Oreslang source/actor egress policy permit has been implemented. This is intentional security hardening. `IsSupported` describes machine provider availability, **not Oreslang guest support**.

The rejected path validates a concrete IPv4 or IPv6 IP, original lowercase canonical DNS hostname, ALPN `h3`, cancellable token and no multicast/wildcard destination. Rejecting naked endpoint+SNI inputs prevents the provider from accidentally bypassing the required complete DNS-set check. A mere `CancellationToken.CanBeCanceled` does NOT guarantee a finite deadline: a future permit-gated connection must enforce a verified bounded deadline independently.

The future implementation must issue an opaque, single-use connection permit under `OutboundEgressPolicy`-equivalent DNS-set authorization, bind actor/tenant/generation, check the pinned IP/SNI at send and path-migration time, bound all connection/stream/packet queues and native memory loans, and prove physical disposal/retirement after cancellation. Then reconnect `ConnectUncheckedAfterPolicyAsync` **only** through the verified permit.

**Not yet implemented or tested:** guest `std/net/quic`, QUIC listener/stream APIs, HTTP/3/QPACK, actor ownership, production egress authorization, complete native AOT publish and independent wire interoperability. A source-free/zero-step runner is not source test evidence.

The standalone admission executable includes negative cases for naked connect, multicast, uppercase/trailing-dot SNI, wildcard endpoints, absent ALPN and pre-cancelled token. The .NET 10 SDK is not present in the local testing environment; exact-head compile and execution still require a genuine, authorized runner.
