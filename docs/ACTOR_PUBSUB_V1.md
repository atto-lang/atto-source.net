# Actor Pub/Sub v1 parity target

.NET NativeAOT: implement ActorGroup/Topic/Subscription over ActorRuntime/ActorMailbox without introducing managed-only semantics that cannot survive NativeAOT. Until source lowering exists, reject unsupported guest constructs and keep this contract as the parity target.

Normative contract: `contracts/v1/actor-pubsub-v1.json`.

No `define publisher` or `define subscriber` language forms are introduced. The shared source model is `ActorGroup`, `Topic<T>`, `Subscription<T>`, and `PublishReceipt`; actors may belong to multiple groups simultaneously, and group membership is never lifecycle ownership.

Golden source parity fixture: `contracts/v1/fixtures/actor-pubsub-v1.ores`.
The backend must either execute this fixture with the normative output or reject the unsupported source surface before lowering; host-only scaffolding is not parity evidence.
