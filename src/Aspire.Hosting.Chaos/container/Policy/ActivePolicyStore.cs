// <copyright file="ActivePolicyStore.cs" company="Microsoft Corporation">
// Copyright (c) Microsoft Corporation. All rights reserved.
// </copyright>

using System.Collections.Concurrent;
using System.Collections.Immutable;

namespace ChaosProxy.Container.Policy;

/// <summary>
/// Thread-safe mutable store of <see cref="ActivePolicy"/> instances. Singleton
/// registered in DI. Holds the bootstrap policy loaded from env vars at startup plus
/// any policies installed at runtime via <c>POST /chaos/policies</c>. Per D6, state is
/// in-memory only - lost on container restart.
/// </summary>
internal sealed class ActivePolicyStore
{
    // ImmutableList for snapshot reads; lock for writes. List preserves install order
    // which is required by D12 (first-installed-wins on matcher overlap per transform type).
    private readonly object _lock = new();
    private ImmutableList<ActivePolicy> _policies = ImmutableList<ActivePolicy>.Empty;

    // Per-policy per-request-key failFirst counters. Composite key keeps buckets isolated
    // even within the same policy/request (e.g., latency vs error vs replay).
    private readonly ConcurrentDictionary<string, int> _failFirstCounters = new();

    // Per-policy seeded RNG for resource-aware random chaos. Created lazily from the
    // policy's seed on first use and reset when the policy is (re)installed/removed, so a
    // fixed seed yields a reproducible fault sequence (D21). Random is not thread-safe;
    // WithPolicyRandom locks the instance so the roll+sample sequence is atomic per draw.
    private readonly ConcurrentDictionary<string, Random> _policyRandoms = new();

    // Bounded log of the faults random chaos actually fired, in order. Drives /chaos/freeze,
    // which converts it into a deterministic chaos_policies[] block that reproduces what broke.
    private const int FrozenFaultsCap = 2000;
    private readonly ConcurrentQueue<FrozenFault> _frozenFaults = new();

    // Global pause flag - flipped via /chaos/pause and /chaos/resume endpoints (or the
    // dashboard pause-faults/resume-faults commands). When true, all middlewares skip
    // their transforms but the proxy keeps forwarding traffic. Volatile so flips are
    // visible across threads without locking.
    private volatile bool _isPaused;

    /// <summary>
    /// True when chaos transforms are paused (proxy still forwards but no faults fire).
    /// </summary>
    public bool IsPaused => _isPaused;

    /// <summary>
    /// Pause all chaos transforms. Proxy continues forwarding requests; faults stop firing.
    /// Idempotent - calling Pause when already paused is a no-op.
    /// </summary>
    public void Pause() => _isPaused = true;

    /// <summary>
    /// Resume all chaos transforms after a prior Pause(). Idempotent.
    /// </summary>
    public void Resume() => _isPaused = false;

    // Fire-once triggers: per-transform-bucket booleans that, when set, cause the
    // corresponding middleware to fire on its NEXT matching request regardless of
    // probability / failFirst, then atomically clear themselves. Useful for one-shot
    // dashboard "trigger chaos now" buttons.
    private readonly ConcurrentDictionary<string, byte> _fireOnceTriggers = new();

    /// <summary>
    /// Arm a fire-once trigger for a transform bucket (e.g., <c>"latency"</c>,
    /// <c>"error"</c>, <c>"replay-duplicate"</c>). The next matching request that reaches
    /// that middleware will fire the transform regardless of normal probability /
    /// failFirst gates. Idempotent: arming an already-armed trigger is a no-op.
    /// </summary>
    public void SetFireOnce(string bucket)
    {
        ArgumentException.ThrowIfNullOrEmpty(bucket);
        _fireOnceTriggers[bucket] = 1;
    }

    /// <summary>
    /// Atomically test-and-clear a fire-once trigger. Returns true exactly once between
    /// successive <see cref="SetFireOnce"/> calls for the same bucket.
    /// </summary>
    public bool ConsumeFireOnce(string bucket)
    {
        return _fireOnceTriggers.TryRemove(bucket, out _);
    }

    /// <summary>
    /// Arm a fire-once trigger scoped to a specific policy + transform. Middleware
    /// consumes via <see cref="ConsumeFireOnceForPolicy"/> and falls back to the global
    /// <see cref="ConsumeFireOnce"/> trigger when no per-policy trigger is armed. Lets
    /// the harness target a specific policy on multi-policy proxies without firing
    /// every matching policy of that transform.
    /// </summary>
    public void SetFireOnceForPolicy(string policyId, string transform)
    {
        ArgumentException.ThrowIfNullOrEmpty(policyId);
        ArgumentException.ThrowIfNullOrEmpty(transform);
        _fireOnceTriggers[$"{policyId}:{transform}"] = 1;
    }

    /// <summary>
    /// Atomically test-and-clear a per-policy fire-once trigger.
    /// </summary>
    public bool ConsumeFireOnceForPolicy(string policyId, string transform)
    {
        return _fireOnceTriggers.TryRemove($"{policyId}:{transform}", out _);
    }

    public ActivePolicy Add(ActivePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);

        // Reset this policy's derived per-policy state (seeded RNG + fire counters/paths) BEFORE
        // publishing the new policy snapshot. Otherwise a request that observes the newly installed
        // policy in the window between publish and reset could record a fire that the reset then wipes.

        // Reset the seeded RNG so reinstalling a policy (same id) restarts its random
        // fault sequence from the configured seed rather than continuing a stale stream.
        _policyRandoms.TryRemove(policy.Id, out _);

        // Reset this policy's fire counters/paths so a (re)install starts a fresh tally.
        // Fire counters persist independently of the policy entry (they survive
        // SweepExpired, so a long-running test can still read them after the policy's TTL
        // lapses); without this, a re-armed policy id would accumulate counts across arms.
        // Note: for a reinstall (same id), a fire from the outgoing policy in the brief
        // window before the snapshot is published lands on the fresh tally, which is the
        // intended "reinstall means start fresh" semantics.
        ResetFireCounts(policy.Id);

        // Also reset this policy's per-request GATING state (failFirst budgets + rate-limit
        // sliding windows), so a re-armed policy id actually starts with a fresh gate — not
        // just fresh observability counters. Without this, a re-install silently inherits the
        // outgoing policy's EXHAUSTED failFirst budget: e.g. a failFirst:1 policy that already
        // spent its single fire in a prior arm would never fire again after re-arm on a
        // long-lived proxy (the proxy container outlives the arm). This is the "(re)install
        // starts a fresh tally" contract above, applied to the gating state it previously
        // missed. (Surfaced by run-to-green mesh repros once the chaos proxy started surviving
        // targeted resource rebuilds instead of being recreated per fix-loop iteration.)
        ResetGatingState(policy.Id);

        lock (_lock)
        {
            _policies = _policies.RemoveAll(p => p.Id == policy.Id).Add(policy);
        }

        return policy;
    }

    public bool Remove(string id)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);

        lock (_lock)
        {
            var updated = _policies.RemoveAll(p => p.Id == id);
            if (updated.Count == _policies.Count)
            {
                return false;
            }
            _policies = updated;
            _policyRandoms.TryRemove(id, out _);
            // Fire counts intentionally survive explicit removal so long-running tests can
            // assert counts after teardown; re-arming the policy resets them via ResetFireCounts.
            return true;
        }
    }

    /// <summary>Snapshot of currently active (non-expired) policies in install order.</summary>
    public ImmutableList<ActivePolicy> GetActive()
    {
        var now = DateTimeOffset.UtcNow;
        return _policies.RemoveAll(p => p.ExpiresAt.HasValue && p.ExpiresAt.Value <= now);
    }

    /// <summary>
    /// Returns the active policy with the given id, or null if not found / expired.
    /// Faster than <c>GetActive().FirstOrDefault(...)</c> because it avoids the full
    /// snapshot materialization for the common single-lookup case.
    /// </summary>
    public ActivePolicy? TryGet(string id)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        var now = DateTimeOffset.UtcNow;
        foreach (var policy in _policies)
        {
            if (string.Equals(policy.Id, id, StringComparison.Ordinal)
                && (!policy.ExpiresAt.HasValue || policy.ExpiresAt.Value > now))
            {
                return policy;
            }
        }
        return null;
    }

    /// <summary>
    /// Sets a fresh expiry of <paramref name="ttl"/> from now on the policy with the
    /// given id. Returns true if the policy was found AND not already expired (the
    /// new expiry was applied); false otherwise. Lets long-running tests keep their
    /// chaos policy alive past the 5-minute install default without removing and
    /// reinstalling. Passing <see cref="TimeSpan.Zero"/> clears the expiry entirely
    /// (policy lives until explicitly removed).
    /// </summary>
    public bool ExtendTtl(string id, TimeSpan ttl)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);

        var now = DateTimeOffset.UtcNow;
        var newExpiresAt = ttl == TimeSpan.Zero ? (DateTimeOffset?)null : now.Add(ttl);

        lock (_lock)
        {
            for (var i = 0; i < _policies.Count; i++)
            {
                var existing = _policies[i];
                if (!string.Equals(existing.Id, id, StringComparison.Ordinal))
                {
                    continue;
                }
                if (existing.ExpiresAt.HasValue && existing.ExpiresAt.Value <= now)
                {
                    // Already expired - bail out so harnesses get a clear 'expired'
                    // signal (treat as 404 at the endpoint layer) rather than
                    // accidentally resurrecting a dead policy.
                    return false;
                }
                _policies = _policies.SetItem(i, existing with { ExpiresAt = newExpiresAt });
                return true;
            }
            return false;
        }
    }

    /// <summary>Removes expired policies from the underlying store. Called by <see cref="PolicyExpirationService"/>.</summary>
    public int SweepExpired()
    {
        var now = DateTimeOffset.UtcNow;
        lock (_lock)
        {
            var before = _policies.Count;
            _policies = _policies.RemoveAll(p => p.ExpiresAt.HasValue && p.ExpiresAt.Value <= now);
            return before - _policies.Count;
        }
    }

    /// <summary>
    /// Per-policy per-request-key failFirst counter. Returns true the first N times the
    /// composite key (bucket + policyId + requestKey) is seen, false thereafter.
    /// </summary>
    public bool ConsumeFailFirstSlot(string bucket, string policyId, string requestKey, int budget)
    {
        var compositeKey = $"{bucket}:{policyId}:{requestKey}";
        var newCount = _failFirstCounters.AddOrUpdate(compositeKey, 1, (_, current) => current + 1);
        return newCount <= budget;
    }

    // Sliding-window timestamp queues for rate-limit gating. Composite key matches
    // failFirst semantics (per-(bucket,policyId,requestKey)) so the same request stream
    // can be rate-limited independently from any error/latency policy in play.
    private readonly ConcurrentDictionary<string, Queue<long>> _rateLimitWindows = new();

    /// <summary>
    /// Sliding-window admission check. Each call records the current timestamp;
    /// returns true if fewer than <paramref name="requestsPerWindow"/> have been
    /// recorded within the past <paramref name="windowMs"/> milliseconds (admit),
    /// false otherwise (rate-limited - middleware should short-circuit). The window
    /// slides forward on every call.
    /// </summary>
    public bool TryAdmitRateLimitedRequest(string bucket, string policyId, string requestKey, int requestsPerWindow, int windowMs)
    {
        var compositeKey = $"{bucket}:{policyId}:{requestKey}";
        var queue = _rateLimitWindows.GetOrAdd(compositeKey, _ => new Queue<long>());
        var now = Environment.TickCount64;
        var cutoff = now - windowMs;

        lock (queue)
        {
            while (queue.TryPeek(out var oldest) && oldest <= cutoff)
            {
                queue.Dequeue();
            }

            if (queue.Count >= requestsPerWindow)
            {
                return false;
            }

            queue.Enqueue(now);
            return true;
        }
    }

    // Idempotency-key dedupe state - per-policy, holds the last-seen timestamp for
    // each key. We use a flat ConcurrentDictionary; keys are evicted lazily when a
    // collision check sees the timestamp has aged past the window.
    private readonly ConcurrentDictionary<string, long> _idempotencyKeys = new();

    /// <summary>
    /// Records the supplied idempotency key. Returns true if the key was NOT seen
    /// within the past <paramref name="windowMs"/> ms (first sight, request should be
    /// forwarded); returns false if it WAS seen (collision, middleware should
    /// short-circuit). Sliding-window behavior: the latest sighting resets the
    /// timestamp so a busy key never times out.
    /// </summary>
    public bool TryRecordIdempotencyKey(string policyId, string key, int windowMs)
    {
        var compositeKey = $"{policyId}:{key}";
        var now = Environment.TickCount64;
        var firstSeen = true;

        _idempotencyKeys.AddOrUpdate(
            compositeKey,
            _ =>
            {
                firstSeen = true;
                return now;
            },
            (_, existing) =>
            {
                if (now - existing <= windowMs)
                {
                    firstSeen = false;
                    return existing; // keep original "first seen" timestamp so the
                                      // window slides off the FIRST request, not the latest
                }
                firstSeen = true;
                return now; // expired - record as fresh sight
            });

        return firstSeen;
    }

    /// <summary>
    /// Records that <paramref name="transform"/> fired against <paramref name="policyId"/>.
    /// Per-policy per-transform counters are exposed via <see cref="GetFireCounts"/> and
    /// included in <c>GET /chaos/policies</c> responses so harnesses can assert that the
    /// chaos actually happened (not just that the policy was installed).
    /// </summary>
    public void RecordFire(string policyId, string transform, string? requestPath = null)
    {
        var compositeKey = $"{policyId}:{transform}";
        _fireCounters.AddOrUpdate(compositeKey, 1, (_, current) => current + 1);

        // Capture the distinct request path the fault actually fired on, for
        // repro-fidelity assertions ("did the fault hit the code path the bug is
        // about?"). Set semantics; bounded per policy so a high-cardinality path
        // space (e.g. per-document Cosmos URIs) can't grow this without limit.
        if (!string.IsNullOrEmpty(requestPath))
        {
            var pathKey = $"{policyId}\u0001{requestPath}";
            if (_firedPaths.ContainsKey(pathKey) || CountFiredPaths(policyId) < FiredPathsCapPerPolicy)
            {
                _firedPaths.TryAdd(pathKey, 0);
            }
        }
    }

    private int CountFiredPaths(string policyId)
    {
        var prefix = $"{policyId}\u0001";
        var n = 0;
        foreach (var key in _firedPaths.Keys)
        {
            if (key.StartsWith(prefix, StringComparison.Ordinal))
            {
                n++;
            }
        }

        return n;
    }

    /// <summary>
    /// Atomically reserves a fire slot if the current fire count is below
    /// <paramref name="maxFires"/>. Use when the work between the "should I fire?"
    /// decision and the actual fire-recording is long enough that a check-then-record
    /// race would let MaxFires be exceeded — e.g. <c>forwardThenFail</c>, whose
    /// upstream call takes hundreds of ms and would otherwise let many concurrent
    /// requests all pass the cap check.
    /// </summary>
    /// <remarks>
    /// Implemented as a CAS loop over the same composite key
    /// <see cref="RecordFire"/> writes to (via <see cref="ConcurrentDictionary{TKey,TValue}.TryUpdate"/>),
    /// so reserved slots are visible to subsequent <see cref="GetFireCount"/> calls.
    /// The "reservation" IS the fire — there's no separate "commit" or "rollback"
    /// step; once reserved, the slot counts even if the middleware later throws.
    /// </remarks>
    /// <returns>true if the slot was reserved (caller should proceed and fire); false if the cap was already hit.</returns>
    public bool TryReserveFire(string policyId, string transform, long maxFires)
    {
        if (maxFires <= 0)
        {
            return false;
        }

        var compositeKey = $"{policyId}:{transform}";

        while (true)
        {
            if (_fireCounters.TryGetValue(compositeKey, out var current))
            {
                if (current >= maxFires)
                {
                    return false;
                }
                // TryUpdate is a true CAS: succeeds only if the stored value still
                // equals `current`. Concurrent updaters lose and re-loop.
                if (_fireCounters.TryUpdate(compositeKey, current + 1, current))
                {
                    return true;
                }
                // Lost the race; another thread incremented. Re-evaluate.
                continue;
            }

            // No entry yet. TryAdd is atomic — succeeds only if key still absent.
            if (_fireCounters.TryAdd(compositeKey, 1))
            {
                return true;
            }
            // Another thread added first; re-loop and treat it as the TryUpdate path.
        }
    }

    /// <summary>
    /// Returns the current fire count for the given policy + transform, or 0 if no
    /// fires have been recorded. Use for global MaxFires caps that complement the
    /// per-request-key <see cref="ConsumeFailFirstSlot"/> semantics — middlewares
    /// can peek this value before firing and bail when a global ceiling is hit.
    /// </summary>
    /// <remarks>
    /// Check-then-fire is racy (another thread can increment between peek + commit),
    /// but the race window is small and the cost of an extra fire under contention
    /// is acceptable for chaos injection. If strict counting is ever needed, switch
    /// to a CAS loop wrapping the peek + RecordFire pair.
    /// </remarks>
    public long GetFireCount(string policyId, string transform)
    {
        var compositeKey = $"{policyId}:{transform}";
        return _fireCounters.TryGetValue(compositeKey, out var count) ? count : 0;
    }

    /// <summary>
    /// Returns a snapshot of fire counts for the given policy keyed by transform name
    /// (<c>"latency"</c>, <c>"error"</c>, <c>"replay-duplicate"</c>, <c>"drop-response"</c>,
    /// <c>"rate-limit"</c>, <c>"header-tamper"</c>, <c>"partial-response"</c>,
    /// <c>"idempotency-collision"</c>). Transforms with zero fires are omitted.
    /// </summary>
    public IReadOnlyDictionary<string, long> GetFireCounts(string policyId)
    {
        var prefix = $"{policyId}:";
        var result = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var kvp in _fireCounters)
        {
            if (kvp.Key.StartsWith(prefix, StringComparison.Ordinal))
            {
                result[kvp.Key[prefix.Length..]] = kvp.Value;
            }
        }
        return result;
    }

    /// <summary>
    /// Returns the distinct request paths (as <c>"{method} {path}"</c>) that the
    /// given policy actually fired on. Lets harnesses assert REPRO FIDELITY — that
    /// the injected fault hit the code path the bug under test is about, not some
    /// unrelated request that merely matched a broad matcher. A broad matcher
    /// (e.g. <c>POST /docs</c>) can fire on the wrong operation (a Cosmos query on
    /// the operations collection) instead of the intended one (a metadata upsert
    /// on the resource-metadata collection), producing a FALSE repro; comparing
    /// these paths against the bug's intended target catches that.
    /// </summary>
    public IReadOnlyList<string> GetFiredPaths(string policyId)
    {
        var prefix = $"{policyId}\u0001";
        var result = new List<string>();
        foreach (var key in _firedPaths.Keys)
        {
            if (key.StartsWith(prefix, StringComparison.Ordinal))
            {
                result.Add(key[prefix.Length..]);
            }
        }

        result.Sort(StringComparer.Ordinal);
        return result;
    }

    /// <summary>
    /// Resets the fire counters for a single policy without affecting other policies'
    /// counters or any other chaos state. Useful for harnesses running multiple
    /// sub-tests under one chaos policy that want per-sub-test assertions.
    /// </summary>
    public void ResetFireCounts(string policyId)
    {
        ArgumentException.ThrowIfNullOrEmpty(policyId);
        var prefix = $"{policyId}:";
        foreach (var key in _fireCounters.Keys)
        {
            if (key.StartsWith(prefix, StringComparison.Ordinal))
            {
                _fireCounters.TryRemove(key, out _);
            }
        }

        var pathPrefix = $"{policyId}\u0001";
        foreach (var key in _firedPaths.Keys)
        {
            if (key.StartsWith(pathPrefix, StringComparison.Ordinal))
            {
                _firedPaths.TryRemove(key, out _);
            }
        }
    }

    /// <summary>
    /// Clears the per-request GATING state — failFirst budgets (<see cref="_failFirstCounters"/>)
    /// and rate-limit sliding windows (<see cref="_rateLimitWindows"/>) — for a single policy id
    /// across all transform buckets. Called from <see cref="Add"/> on (re)install so a re-armed
    /// policy starts with a fresh gate (unlike <see cref="ResetFireCounts"/>, which only resets the
    /// observability tally). The composite keys are <c>{bucket}:{policyId}:{requestKey}</c> and the
    /// requestKey segment can itself contain <c>':'</c> (e.g. <c>anon:POST:/path</c>), so match the
    /// bounded <c>:{policyId}:</c> token rather than a prefix. A theoretical over-match (a policy id
    /// literally appearing as a <c>:{id}:</c> segment inside another policy's requestKey) is
    /// astronomically unlikely for real ids and harmless if it occurred (it would merely reset an
    /// unrelated gate to fresh, the same effect as re-arming that policy).
    /// </summary>
    private void ResetGatingState(string policyId)
    {
        var token = $":{policyId}:";

        foreach (var key in _failFirstCounters.Keys)
        {
            if (key.Contains(token, StringComparison.Ordinal))
            {
                _failFirstCounters.TryRemove(key, out _);
            }
        }

        foreach (var key in _rateLimitWindows.Keys)
        {
            if (key.Contains(token, StringComparison.Ordinal))
            {
                _rateLimitWindows.TryRemove(key, out _);
            }
        }
    }

    /// <summary>
    /// True if this policy id has at least one recorded fire. Fire counters survive
    /// <see cref="SweepExpired"/>, so this lets a fire-count query return the retained
    /// tally even after the policy's TTL has lapsed and it has been swept from the active
    /// set — the case a long-running test hits when it asserts fire counts after a wait
    /// that outlives a short-TTL fault.
    /// </summary>
    public bool HasFireRecord(string policyId)
    {
        ArgumentException.ThrowIfNullOrEmpty(policyId);
        var prefix = $"{policyId}:";
        foreach (var key in _fireCounters.Keys)
        {
            if (key.StartsWith(prefix, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Returns a snapshot of total fire counts across ALL policies, keyed by
    /// transform name. Useful for at-a-glance dashboard view + harness assertions
    /// like "chaos fired at all during this test run".
    /// </summary>
    public IReadOnlyDictionary<string, long> GetAllFireCounts()
    {
        var result = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var kvp in _fireCounters)
        {
            var sep = kvp.Key.IndexOf(':', StringComparison.Ordinal);
            if (sep < 0)
            {
                continue;
            }
            var transform = kvp.Key[(sep + 1)..];
            result[transform] = result.GetValueOrDefault(transform) + kvp.Value;
        }
        return result;
    }

    /// <summary>
    /// Returns the keys of currently armed fire-once triggers (both global per-transform
    /// triggers like <c>"latency"</c> and per-policy triggers like <c>"policy-a:error"</c>).
    /// Useful for harness debugging when a test fails to reproduce - "did we forget to
    /// arm the trigger? did a previous test leak one?"
    /// </summary>
    public IReadOnlyList<string> GetArmedFireOnceTriggers()
    {
        return _fireOnceTriggers.Keys.OrderBy(k => k).ToList();
    }

    /// <summary>
    /// Runs <paramref name="draw"/> against this policy's seeded RNG under a lock so the
    /// draw sequence is deterministic for a given seed and atomic under concurrency. The
    /// RNG is created from <paramref name="seed"/> on first use for the policy and reset
    /// when the policy is (re)installed or removed.
    /// </summary>
    public T WithPolicyRandom<T>(string policyId, int seed, Func<Random, T> draw)
    {
        ArgumentException.ThrowIfNullOrEmpty(policyId);
        ArgumentNullException.ThrowIfNull(draw);

        var rng = _policyRandoms.GetOrAdd(policyId, _ => new Random(seed));
        lock (rng)
        {
            return draw(rng);
        }
    }

    /// <summary>Records a fault that random chaos fired (bounded) for later <c>/chaos/freeze</c>.</summary>
    public void RecordFrozenFault(FrozenFault fault)
    {
        ArgumentNullException.ThrowIfNull(fault);
        if (_frozenFaults.Count >= FrozenFaultsCap)
        {
            return;
        }

        _frozenFaults.Enqueue(fault);
    }

    /// <summary>Snapshot of the random-chaos fired-fault log in fire order.</summary>
    public IReadOnlyList<FrozenFault> GetFrozenFaults() => _frozenFaults.ToArray();

    /// <summary>
    /// Removes ALL policies (regardless of expiry) and clears all fire counters.
    /// Returns the number of policies that were removed. Designed for harness teardown
    /// between tests so each run starts from a clean slate.
    /// </summary>
    public int Clear()
    {
        int removed;
        lock (_lock)
        {
            removed = _policies.Count;
            _policies = ImmutableList<ActivePolicy>.Empty;
        }

        _fireCounters.Clear();
        _firedPaths.Clear();
        _failFirstCounters.Clear();
        _rateLimitWindows.Clear();
        _idempotencyKeys.Clear();
        _fireOnceTriggers.Clear();
        _dtfxCorrelations.Clear();
        _policyRandoms.Clear();
        _frozenFaults.Clear();
        return removed;
    }

    private readonly ConcurrentDictionary<string, long> _fireCounters = new();

    // Distinct request paths each policy actually fired on (set semantics), for
    // repro-fidelity assertions. Key is "{policyId}\u0001{method} {path}". Bounded
    // per policy via FiredPathsCapPerPolicy so a high-cardinality path space can't
    // grow this without limit.
    private readonly ConcurrentDictionary<string, byte> _firedPaths = new();
    private const int FiredPathsCapPerPolicy = 50;

    // -- DTFx activity correlation ---------------------------------------------
    //
    // DTFx wire shape: a TaskScheduledEvent message (orchestrator -> activity worker)
    // carries (InstanceId, EventId, ActivityName). A TaskCompletedEvent message
    // (activity worker -> orchestrator) carries (InstanceId, TaskScheduledId) where
    // TaskScheduledId == the schedule event's EventId. The completion event body
    // does NOT carry the activity name.
    //
    // To support matchers like "drop TaskCompletedEvent for activity 'X'", we observe
    // every scheduled event flowing through the proxy and remember its name keyed by
    // (InstanceId, ScheduledEventId). The buffering middleware records correlations
    // unconditionally when any policy in the store has DtfxActivityName set. The
    // matcher looks up by (InstanceId, TaskScheduledId) at match time.
    //
    // Capacity bound: a simple cap on the dictionary's entry count. When the cap is
    // exceeded we drop arbitrary old entries (ConcurrentDictionary doesn't support
    // LRU natively; for chaos correlation, dropping random old entries is acceptable
    // — a missed correlation means the matcher doesn't fire on that specific
    // completion event, which degrades chaos coverage but doesn't break anything).
    private const int DtfxCorrelationCap = 10_000;

    private readonly ConcurrentDictionary<string, string> _dtfxCorrelations = new(StringComparer.Ordinal);

    /// <summary>
    /// Records that the DTFx event with the given (orchestration instance id,
    /// task-scheduled event id) corresponds to the given activity name. Called
    /// from the buffering middleware when it observes a TaskScheduledEvent.
    /// </summary>
    public void RecordDtfxActivityName(string instanceId, int taskScheduledId, string activityName)
    {
        if (string.IsNullOrEmpty(instanceId) || string.IsNullOrEmpty(activityName))
        {
            return;
        }
        var key = $"{instanceId}:{taskScheduledId}";
        _dtfxCorrelations[key] = activityName;

        // Best-effort cap. If we exceed the cap, drop a handful of arbitrary entries.
        // We accept the race — chaos doesn't need strict guarantees, just bounded memory.
        if (_dtfxCorrelations.Count > DtfxCorrelationCap)
        {
            int toRemove = _dtfxCorrelations.Count - (DtfxCorrelationCap * 9 / 10);
            foreach (var existingKey in _dtfxCorrelations.Keys)
            {
                if (toRemove-- <= 0) { break; }
                _dtfxCorrelations.TryRemove(existingKey, out _);
            }
        }
    }

    /// <summary>
    /// Looks up the activity name (if any) recorded by an earlier
    /// <see cref="RecordDtfxActivityName"/> call for this (instance, scheduled-id)
    /// pair. Returns null if no correlation was recorded.
    /// </summary>
    public string? LookupDtfxActivityName(string instanceId, int taskScheduledId)
    {
        if (string.IsNullOrEmpty(instanceId))
        {
            return null;
        }
        var key = $"{instanceId}:{taskScheduledId}";
        return _dtfxCorrelations.TryGetValue(key, out var name) ? name : null;
    }
}
