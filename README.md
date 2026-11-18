# Tralys.BehaviorTree

A behavior tree library for C# game servers that run thousands of agents. The tree definition is immutable and
shared by every agent of its type. Each agent's state is one compact array. **A tick allocates nothing.**

It replaces the deprecated `FlatTree` package.

```sh
dotnet add package Tralys.BehaviorTree
```

## Allocation profile

| Scope | Allocation |
|---|---|
| Per archetype | One tree, built once at startup and shared by every agent of that type |
| Per agent | One `NodeState[]`, 16 B per node |
| Per tick | 0 B. Random orderings draw into `stackalloc`. |

Node construction runs once per archetype, at build time. Leaf delegates must capture nothing (use `static` lambdas
or method groups). That rule is about correctness as well as allocation: a node is shared by every agent, so a
closure over one agent's state would leak it to all of them.

## Authoring

```csharp
BtFactory<Ctx> n = Bt.For<Ctx>();

BehaviorTree<Ctx> tree = n.Build(
    n.PrioritySelector("root",
        n.Sequence("enrage",
            n.Condition("hp<20%", static (in Ctx c) => c.Hp < c.MaxHp / 5),
            n.Cooldown("enrage-cd", TimeSpan.FromSeconds(30),
                n.Do("cast-enrage", Actions.CastEnrage))),
        n.Sequence("attack",
            n.Condition("has-target", Actions.HasTarget),
            n.Wait("wind-up", TimeSpan.FromMilliseconds(500)),
            n.Do("strike", Actions.Strike)),
        n.Do("idle", Actions.Idle)));

NodeState[] state = tree.NewState();                        // per agent
TickResult result = tree.Tick(state, new Ctx(board, nowMs)); // per tick, 0 B
```

### The context

- `Ctx` is any struct or class that implements `IClock` (`long NowMs { get; }`).
- Time comes only from the context, so a tree is deterministic under test.

### Leaves and node arguments

- `Do` returns `TickResult` and `Condition` returns `bool`.
- Both take the context by `in`, so a lambda spells out `static (in c) => ...`.
- Per-site data goes in a `state` argument, not a closure: `n.Condition("armed", Gate.Ready, static (in c, in g) => c.Is(g))`.
  Every agent shares that state, so keep it immutable.
- The `Do` overload taking `(in Ctx, ref int cursor, ref long stamp)` exposes per-agent scratch for multi-tick
  actions. A reset clears that scratch.
- Names are optional and only for debugging.
- Durations are `TimeSpan`.
- `Bt.For<Ctx>(new SeededRandomProvider(seed))` makes random orderings and `Chance` rolls reproducible on every
  platform.

### Reuse and custom nodes

- A reusable subtree is a method that returns `BtNode<Ctx>`.
- For a custom node, subclass `LeafNode<Ctx>`, `CompositeNode<Ctx>` or `DecoratorNode<Ctx>`.
- `PollingLeaf<Ctx>` starts work once and polls it to completion, with an optional deadline.

## Nodes

| Kind | Nodes |
|---|---|
| Composites | `Selector`, `Sequence`, `PrioritySelector`, `PrioritySequence`, `RandomSelector`, `RandomSequence`, `WeightedSelector`, `SimpleParallel` (up to 16 children), `If`, `ReactiveIf` |
| Decorators | `Inverter`, `AlwaysSucceed`, `AlwaysFail`, `AutoReset`, `UntilSuccess`, `UntilFailed`, `Repeat`, `Retry`, `Forever`, `While`, `ForEach`, `Catch`, `OnComplete`, `Cooldown`, `RateLimiter`, `TimeLimit`, `Chance` |
| Leaves | `Do`, `Act`, `Condition`, `Wait`, `WaitUntil` |

## Reactive vs resuming composites

How a composite behaves while one of its children is `Running`:

| Composite | While a child is `Running` |
|---|---|
| `Selector` / `Sequence` | Resumes at its cursor. Earlier guards are **not** re-evaluated. |
| `PrioritySelector` / `PrioritySequence` | Re-ticks from child 0 every tick, and resets lower-priority children when a higher one takes over. |
| `If` / `ReactiveIf` | `If` reads its condition once. `ReactiveIf` reads it every tick and resets the running branch when the answer changes. |

Use a priority composite where a guard must interrupt a running branch.

Don't put a self-re-arming leaf (`Wait`, or a scratch `Do`) as a non-final child of a priority composite. It reports
Success, then restarts on the next tick, which resets everything behind it, so multi-tick work behind it never
finishes. Wrap the tail in a `Sequence`:

```csharp
n.PrioritySequence("attack",
    n.Condition("has-target", ...),
    n.Sequence("swing", n.Wait("wind-up", ...), n.Do("channel", ...)))
```

## Reset, abort and recovery

### Reset

- `Reset` is the only abort notification. A preempted node hears it through `DoReset(state, in ctx)` and releases
  what it acquired.
- `Reset` skips `Fresh` nodes, so an untouched branch costs nothing.
- `Reset` rewinds traversal, not history:

| State | On reset |
|---|---|
| Cursors, started flags, `Do` scratch, `Wait` and `TimeLimit` timers | Cleared |
| `Cooldown` timer, `RateLimiter` interval and cached verdict | Kept, so preemption can't refund a cooldown |

### Uninterruptible subtrees

- `n.Uninterruptible(...)` marks a subtree that must never be torn down mid-flight.
- `Build` throws if such a subtree sits under anything that preempts a running child: a priority composite,
  `SimpleParallel`, `TimeLimit`, `ReactiveIf`, or a custom node that overrides `PreemptsRunningChildren`.

### After an exception

- A node whose `Update` throws keeps the status it had before that tick, so an ordinary `Reset` can miss the dirty
  subtree.
- `tree.TickOrRecover(state, ctx)` runs `ResetAll` and then rethrows. `ResetAll` sweeps every node and calls
  `DoReset` even on nodes that never ticked, so cleanup hooks must be idempotent.

## Pooling and tracing

### `BehaviorTreePool<Ctx>`

- It holds many agents' state in one slab.
- `Rent` gives you a slot. `Tick`, `TickOrRecover`, `Reset`, `ResetAll` and `StatusOf` all take that slot.
- `Return(slot, ctx)` runs cleanup before it frees the slot.
- `Return(slot)` only frees the slot. Use it only for agents that hold nothing.

### Tracing

- `tree.Tracer = tracer` attaches an `IBtTracer<Ctx>`, which hears every tick result and every reset of a node that
  had ticked.
- The tree is shared, so one tracer sees every agent; tell them apart through the context.
- Unset, the tracer costs one null check per node tick.
- `node.Describe(state)` and `ToDebugString(state)` print one agent's scratch for a node.

## Constraints

- Targets `net11.0`, with no dependencies.
- Single-threaded: a tree and its state take no locks.

## Building and releasing

```sh
dotnet build Tralys.BehaviorTree.slnx
dotnet run --project tests/Tralys.BehaviorTree.Tests -c Release
dotnet run --project bench/Tralys.BehaviorTree.Bench -c Release
```

To release, push a `v*` tag from `main` once `CHANGELOG.md` has a section for that version. The
[`release` workflow](https://github.com/tralys/Tralys.BehaviorTree/blob/main/.github/workflows/release.yml) builds,
tests, packs and publishes to nuget.org, then cuts a GitHub Release from that section. A tag containing `-` publishes
a prerelease.

## License

MIT.
