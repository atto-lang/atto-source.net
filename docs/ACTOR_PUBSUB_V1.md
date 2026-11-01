# Actor Pub/Sub v1 parity target

.NET NativeAOT: implement ActorGroup/Topic/Subscription over ActorRuntime/ActorMailbox without introducing managed-only semantics that cannot survive NativeAOT. Until source lowering exists, reject unsupported guest constructs and keep this contract as the parity target.

Normative contract: `contracts/v1/actor-pubsub-v1.json`.

No `define publisher` or `define subscriber` language forms are introduced. The shared source model is `ActorGroup<T>`, `Topic<T>`, `Subscription<T>`, and `PublishReceipt`; actors may belong to multiple groups simultaneously, and group membership is never lifecycle ownership.
