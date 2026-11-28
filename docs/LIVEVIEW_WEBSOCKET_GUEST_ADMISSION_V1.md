# .NET Native AOT WebSocket/LiveView guest admission: fail closed

The native .NET HTTP actor provider currently uses slim ASP.NET/Kestrel
HTTP/1 request handling but **does not** provide an Oreslang-guest owned
WebSocket upgrade, connection transfer or source WebSocket API. Setting
`ActorHttpServerOptions.EnableWebSocketUpgrade` or
`EnableLiveViewUpdates` now rejects before the server binds.
This avoids conflating Kestrel platform capability with Oreslang's
generation-fenced actor authority.

`verification/Ores.Net.HttpProbe/Program.cs` adds three explicit
rejection probes (upgrade only, LiveView only, both) while retaining
existing HTTP success tests with default disabled flags. These are
AUTHORED; .NET SDK, Linux/Windows Native AOT and Oreslang guest tests
were NOT_EXECUTED in this change.

The canonical 41 WebSocket client frame vectors are pinned at
`contracts/v1/ores-liveview-wire-v1.json`, Git blob
`931f58c1acd40e4c7e1b0b061f1711a0afe3c9b8`. This is a
future positive target, **not evidence** that the native .NET backend
parses those frames. Full parity requires per-tenant isolates, exact
message/close-code mapping, actor session delivery and lifecycle,
Native AOT, cross-backend replay/DOM/security tests and authorized CI.

No private compiler/source copied to another organization.
