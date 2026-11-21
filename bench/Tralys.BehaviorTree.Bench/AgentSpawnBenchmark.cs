namespace Tralys.BehaviorTree.Bench;

/// <summary>
/// The cost of bringing one agent into existence. Tralys.BehaviorTree shares the node graph across agents, so
/// spawning is a single <c>NodeState[]</c>; the classic layout must build the whole node graph per
/// agent. The <c>Allocated</c> column is the point of this benchmark — it is the per-agent half of
/// the allocation profile, which <see cref="TickThroughputBenchmark"/> cannot see because it spawns
/// in <c>[GlobalSetup]</c>.
/// </summary>
[MemoryDiagnoser]
public class AgentSpawnBenchmark
{
    private readonly BehaviorTree<BenchContext> _tree = BenchTrees.Boss();

    [Benchmark(Baseline = true)]
    public NodeState[] FlatNewState() => _tree.NewState();

    [Benchmark]
    public NaiveNode NaiveNodeGraph() => NaiveTrees.Boss();
}
