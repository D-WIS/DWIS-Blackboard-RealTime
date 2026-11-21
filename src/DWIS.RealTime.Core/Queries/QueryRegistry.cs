using DWIS.Blackboard.Contracts.Grpc;
using DWIS.RealTime.Core.Adapters;

namespace DWIS.RealTime.Core.Queries;

/// <summary>
/// The local table from registered query to the sessions that hold it (specification 3.1), with the query text,
/// the rows of its current answer, and the id the service gave it. The local id names the nodes
/// (<c>QueryClient{id}…</c>) and never changes; the service's id changes after a restart of the service, when
/// Core registers every query again (T-QR-4), so the two are kept apart.
/// </summary>
public sealed class QueryRegistry
{
    private readonly Dictionary<int, Entry> _byLocal = [];
    private readonly Dictionary<string, int> _byText = new(StringComparer.Ordinal);
    private readonly Dictionary<int, int> _byService = [];
    private readonly Lock _gate = new();
    private int _nextLocal;

    /// <summary>A registered query.</summary>
    public sealed record Entry(int LocalId, int ServiceId, string Text, IReadOnlyList<string> Variables, IReadOnlyList<Row> Rows, IReadOnlySet<SessionId> Sessions);

    /// <summary>Records a session's hold on a query (new, or already held by others); returns the entry and whether the query is new here.</summary>
    public (Entry Entry, bool IsNew) Add(string text, int serviceId, IEnumerable<string> variables, IEnumerable<Row> rows, SessionId session)
    {
        lock (_gate)
        {
            if (_byText.TryGetValue(text, out var localId) && _byLocal.TryGetValue(localId, out var existing))
            {
                var sessions = new HashSet<SessionId>(existing.Sessions) { session };
                var updated = existing with { ServiceId = serviceId, Variables = [.. variables], Rows = [.. rows], Sessions = sessions };
                Put(updated);
                return (updated, false);
            }
            var entry = new Entry(++_nextLocal, serviceId, text, [.. variables], [.. rows], new HashSet<SessionId> { session });
            Put(entry);
            return (entry, true);
        }
    }

    /// <summary>Takes a session's hold off a query by its service id; returns the entry as it was and whether the query is gone here.</summary>
    public (Entry? Entry, bool Gone) RemoveSession(int serviceId, SessionId session)
    {
        lock (_gate)
        {
            if (!_byService.TryGetValue(serviceId, out var localId) || !_byLocal.TryGetValue(localId, out var entry))
            {
                return (null, false);
            }
            return RemoveSessionCore(entry, session);
        }
    }

    /// <summary>Takes a closing session off every query it holds; returns each entry and whether the query is gone here.</summary>
    public IReadOnlyList<(Entry Entry, bool Gone)> RemoveSessionEverywhere(SessionId session)
    {
        lock (_gate)
        {
            var result = new List<(Entry, bool)>();
            foreach (var entry in _byLocal.Values.Where(e => e.Sessions.Contains(session)).ToList())
            {
                var (removed, gone) = RemoveSessionCore(entry, session);
                result.Add((removed!, gone));
            }
            return result;
        }
    }

    private (Entry Entry, bool Gone) RemoveSessionCore(Entry entry, SessionId session)
    {
        var sessions = new HashSet<SessionId>(entry.Sessions);
        sessions.Remove(session);
        if (sessions.Count == 0)
        {
            _byLocal.Remove(entry.LocalId);
            _byText.Remove(entry.Text);
            _byService.Remove(entry.ServiceId);
            return (entry, true);
        }
        Put(entry with { Sessions = sessions });
        return (entry, false);
    }

    /// <summary>Applies a diff from the stream to the query's rows; returns the entry after it, or null for a query not held here.</summary>
    public Entry? Apply(int serviceId, IEnumerable<Row> added, IEnumerable<Row> removed)
    {
        lock (_gate)
        {
            if (!_byService.TryGetValue(serviceId, out var localId) || !_byLocal.TryGetValue(localId, out var entry))
            {
                return null;
            }
            var rows = entry.Rows.Where(r => !removed.Any(x => Same(x, r))).Concat(added).ToList();
            var updated = entry with { Rows = rows };
            Put(updated);
            return updated;
        }
    }

    /// <summary>After the service was registered again: the new service id and rows; returns the entry before the change (for the diff) and after.</summary>
    public (Entry Before, Entry After)? Rebind(int localId, int serviceId, IEnumerable<string> variables, IEnumerable<Row> rows)
    {
        lock (_gate)
        {
            if (!_byLocal.TryGetValue(localId, out var before))
            {
                return null;
            }
            _byService.Remove(before.ServiceId);
            var after = before with { ServiceId = serviceId, Variables = [.. variables], Rows = [.. rows] };
            Put(after);
            return (before, after);
        }
    }

    /// <summary>The entries, a copy.</summary>
    public IReadOnlyList<Entry> All
    {
        get { lock (_gate) { return [.. _byLocal.Values]; } }
    }

    /// <summary>How many distinct queries are held here.</summary>
    public int QueryCount
    {
        get { lock (_gate) { return _byLocal.Count; } }
    }

    /// <summary>How many (query, session) pairs are held here: the query nodes.</summary>
    public int SubscriptionCount
    {
        get { lock (_gate) { return _byLocal.Values.Sum(e => e.Sessions.Count); } }
    }

    private void Put(Entry entry)
    {
        _byLocal[entry.LocalId] = entry;
        _byText[entry.Text] = entry.LocalId;
        _byService[entry.ServiceId] = entry.LocalId;
    }

    /// <summary>Two rows are the same when their values are, in order.</summary>
    public static bool Same(Row a, Row b) => a.Values.Count == b.Values.Count && a.Values.Zip(b.Values).All(p => p.First == p.Second);

    /// <summary>The rows in <paramref name="after"/> that are not in <paramref name="before"/>.</summary>
    public static IReadOnlyList<Row> Except(IEnumerable<Row> after, IReadOnlyList<Row> before) => after.Where(r => !before.Any(b => Same(b, r))).ToList();
}
