namespace CopyMaster.Engine.Scheduling;

public enum QueueState
{
    /// <summary>In the queue, waiting for its disks.</summary>
    Waiting,
    /// <summary>Paused before it started: it won't start, and doesn't hold up jobs behind it.</summary>
    Held,
    Running,
    Done,
}

/// <summary>One job's place in a <see cref="Scheduler{T}"/>.</summary>
public sealed class QueueEntry<T>
{
    internal QueueEntry(T item, IReadOnlySet<string> resources, QueueState state)
    {
        Item = item;
        Resources = resources;
        State = state;
    }

    public T Item { get; }

    /// <summary>The disks (and servers) the job keeps busy: <see cref="Disks.ResourcesFor"/>.</summary>
    public IReadOnlySet<string> Resources { get; }

    public QueueState State { get; internal set; }

    /// <summary>
    /// For a waiting job: the job it waits for, one running on a disk it needs or queued
    /// ahead of it for one. Null when it isn't waiting.
    /// </summary>
    public QueueEntry<T>? WaitingFor { get; internal set; }

    /// <summary>"Start now": run at the next <see cref="Scheduler{T}.Start"/> whatever's busy.</summary>
    internal bool StartNow { get; set; }
}

/// <summary>
/// Decides when queued jobs start: a queue per disk. A job starts when none of its disks
/// is busy and no job queued ahead of it wants one of them, so a job is only ever overtaken
/// by jobs that share no disk with it, and nothing waits forever. Held jobs are left out
/// (they neither start nor hold anyone up); "Start now" starts a job whatever's busy.
///
/// Not thread-safe: the owner (<see cref="JobQueue"/>) locks around it.
/// </summary>
public sealed class Scheduler<T>
{
    private readonly List<QueueEntry<T>> _entries = [];

    /// <summary>All jobs in queue order, running and finished ones included.</summary>
    public IReadOnlyList<QueueEntry<T>> Entries => _entries;

    public QueueEntry<T> Add(T item, IEnumerable<string> resources, bool held = false)
    {
        var entry = new QueueEntry<T>(item, resources.ToHashSet(StringComparer.OrdinalIgnoreCase),
            held ? QueueState.Held : QueueState.Waiting);
        _entries.Add(entry);
        return entry;
    }

    /// <summary>Moves a job up (negative) or down the queue. False if it was already at that end.</summary>
    public bool Move(QueueEntry<T> entry, int places)
    {
        int from = IndexOf(entry);
        int to = Math.Clamp(from + places, 0, _entries.Count - 1);
        if (to == from) return false;
        _entries.RemoveAt(from);
        _entries.Insert(to, entry);
        return true;
    }

    public void Hold(QueueEntry<T> entry)
    {
        if (entry.State != QueueState.Waiting) return;
        entry.State = QueueState.Held;
        entry.StartNow = false;
        entry.WaitingFor = null;
    }

    public void Release(QueueEntry<T> entry)
    {
        if (entry.State == QueueState.Held) entry.State = QueueState.Waiting;
    }

    /// <summary>Starts a waiting or held job at the next <see cref="Start"/>, even if its disks are busy.</summary>
    public void StartNow(QueueEntry<T> entry)
    {
        if (entry.State is not (QueueState.Waiting or QueueState.Held)) return;
        entry.State = QueueState.Waiting;
        entry.StartNow = true;
    }

    /// <summary>Takes a job out of the queue. A running job can't be removed (cancel it first).</summary>
    public bool Remove(QueueEntry<T> entry)
    {
        if (entry.State == QueueState.Running) return false;
        return _entries.Remove(entry);
    }

    /// <summary>A running job has ended.</summary>
    public void Finish(QueueEntry<T> entry)
    {
        if (entry.State != QueueState.Running) throw new InvalidOperationException("That job isn't running.");
        entry.State = QueueState.Done;
    }

    /// <summary>Puts a finished job back in the queue where it stands (to retry its failures).</summary>
    public void Requeue(QueueEntry<T> entry)
    {
        if (entry.State != QueueState.Done) throw new InvalidOperationException("That job hasn't finished.");
        entry.State = QueueState.Waiting;
    }

    /// <summary>Marks the jobs that can start now as running and returns them.</summary>
    public List<QueueEntry<T>> Start()
    {
        // Each disk is claimed by the job running on it, else by the first job queued for it.
        var claimed = new Dictionary<string, QueueEntry<T>>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in _entries.Where(e => e.State == QueueState.Running))
        {
            foreach (string resource in entry.Resources) claimed.TryAdd(resource, entry);
        }

        var started = new List<QueueEntry<T>>();
        foreach (var entry in _entries)
        {
            if (entry.State != QueueState.Waiting) continue;

            QueueEntry<T>? blocker = null;
            if (!entry.StartNow)
            {
                foreach (string resource in entry.Resources)
                {
                    if (claimed.TryGetValue(resource, out blocker)) break;
                }
            }

            if (blocker is null)
            {
                entry.State = QueueState.Running;
                entry.StartNow = false;
                entry.WaitingFor = null;
                started.Add(entry);
            }
            else
            {
                entry.WaitingFor = blocker;
            }

            // Started or not, it has first call on its disks over everything behind it.
            foreach (string resource in entry.Resources) claimed.TryAdd(resource, entry);
        }
        return started;
    }

    private int IndexOf(QueueEntry<T> entry)
    {
        int index = _entries.IndexOf(entry);
        return index >= 0 ? index : throw new ArgumentException("That job isn't in this queue.", nameof(entry));
    }
}
