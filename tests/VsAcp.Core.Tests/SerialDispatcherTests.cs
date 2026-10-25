using VsAcp.Core.Services;

namespace VsAcp.Core.Tests;

public class SerialDispatcherTests
{
    /// <summary>A host dispatch that holds work until the test releases it, like a busy UI thread.</summary>
    private sealed class HeldDispatch
    {
        private readonly Queue<Action> _held = new();
        public int Hops { get; private set; }
        public void Dispatch(Action action) { Hops++; _held.Enqueue(action); }
        public void RunAll() { while (_held.Count > 0) _held.Dequeue()(); }
    }

    [Fact]
    public void Runs_at_once_through_the_host_when_nothing_is_queued()
    {
        var ran = new List<int>();
        var queue = new SerialDispatcher(action => action());

        queue.Post(() => ran.Add(1));
        queue.Post(() => ran.Add(2));

        Assert.Equal(new[] { 1, 2 }, ran);
    }

    [Fact]
    public void A_later_post_waits_behind_queued_work_even_when_the_host_would_run_it_inline()
    {
        // the bug this exists for: the turn's end ran inline on the UI thread ahead of queued chunk updates
        var held = new HeldDispatch();
        var onUiThread = false;
        var ran = new List<string>();
        var queue = new SerialDispatcher(action =>
        {
            if (onUiThread) action();
            else held.Dispatch(action);
        });

        queue.Post(() => ran.Add("chunk 1"));
        queue.Post(() => ran.Add("chunk 2"));
        onUiThread = true;
        queue.Post(() => ran.Add("complete"));
        Assert.Empty(ran);

        held.RunAll();
        Assert.Equal(new[] { "chunk 1", "chunk 2", "complete" }, ran);
    }

    [Fact]
    public void A_burst_costs_one_hop()
    {
        var held = new HeldDispatch();
        var queue = new SerialDispatcher(held.Dispatch);

        for (var i = 0; i < 50; i++) queue.Post(() => { });
        held.RunAll();
        queue.Post(() => { });

        Assert.Equal(2, held.Hops);
    }

    [Fact]
    public void Posting_from_inside_an_action_queues_behind_it_instead_of_nesting()
    {
        var ran = new List<string>();
        SerialDispatcher queue = null!;
        queue = new SerialDispatcher(action => action());

        queue.Post(() =>
        {
            ran.Add("outer start");
            queue.Post(() => ran.Add("inner"));
            ran.Add("outer end");
        });

        Assert.Equal(new[] { "outer start", "outer end", "inner" }, ran);
    }

    [Fact]
    public void A_failing_action_does_not_strand_the_ones_behind_it()
    {
        var held = new HeldDispatch();
        var ran = new List<int>();
        var queue = new SerialDispatcher(held.Dispatch);

        queue.Post(() => ran.Add(1));
        queue.Post(() => throw new InvalidOperationException("boom"));
        queue.Post(() => ran.Add(3));
        held.RunAll();

        Assert.Equal(new[] { 1, 3 }, ran);
        queue.Post(() => ran.Add(4));
        held.RunAll();
        Assert.Equal(new[] { 1, 3, 4 }, ran);
    }

    [Fact]
    public async Task Posts_from_many_threads_all_run_and_keep_each_threads_order()
    {
        var gate = new object();
        var seen = new List<(int Thread, int Seq)>();
        var queue = new SerialDispatcher(action => Task.Run(() => { lock (gate) action(); }));

        await Task.WhenAll(Enumerable.Range(0, 8).Select(t => Task.Run(() =>
        {
            for (var i = 0; i < 500; i++)
            {
                var seq = i;
                queue.Post(() => seen.Add((t, seq)));
            }
        })));

        var done = new TaskCompletionSource<bool>();
        queue.Post(() => done.TrySetResult(true));
        Assert.Same(done.Task, await Task.WhenAny(done.Task, Task.Delay(10000)));

        Assert.Equal(8 * 500, seen.Count);
        foreach (var perThread in seen.GroupBy(s => s.Thread))
            Assert.Equal(Enumerable.Range(0, 500), perThread.Select(s => s.Seq));
    }
}
