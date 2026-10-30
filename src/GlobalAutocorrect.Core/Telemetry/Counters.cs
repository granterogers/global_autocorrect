using System.Text.Json;

namespace GlobalAutocorrect.Core.Telemetry;

/// <summary>
/// Counts only, never text (6.5, 8.6): lets real-world false-edit rate be estimated from undos ÷ corrections.
/// </summary>
public sealed class Counters
{
    private long _instant, _ai, _undos, _desyncs, _aiRequests, _aiFailures, _rejectedEdits, _droppedWrites, _fieldMismatches;

    public long InstantCorrections => Interlocked.Read(ref _instant);
    public long AiCorrections => Interlocked.Read(ref _ai);
    public long Undos => Interlocked.Read(ref _undos);
    public long Desyncs => Interlocked.Read(ref _desyncs);
    public long AiRequests => Interlocked.Read(ref _aiRequests);
    public long AiFailures => Interlocked.Read(ref _aiFailures);
    public long RejectedEdits => Interlocked.Read(ref _rejectedEdits);
    public long DroppedWrites => Interlocked.Read(ref _droppedWrites);
    /// <summary>The field's text didn't match what was typed, so the session was dropped (diagnostics).</summary>
    public long FieldMismatches => Interlocked.Read(ref _fieldMismatches);
    public long Corrections => InstantCorrections + AiCorrections;

    /// <summary>D64: how long each stage of a correction takes (durations only).</summary>
    public Timings Timings { get; } = new();

    public void Instant() => Interlocked.Increment(ref _instant);
    public void Ai() => Interlocked.Increment(ref _ai);
    public void Undo() => Interlocked.Increment(ref _undos);
    public void Desync() => Interlocked.Increment(ref _desyncs);
    public void AiRequest() => Interlocked.Increment(ref _aiRequests);
    public void AiFailure() => Interlocked.Increment(ref _aiFailures);
    public void Rejected(int n) => Interlocked.Add(ref _rejectedEdits, n);
    public void Dropped() => Interlocked.Increment(ref _droppedWrites);
    public void FieldMismatch() => Interlocked.Increment(ref _fieldMismatches);

    public Dictionary<string, long> Snapshot() => new()
    {
        ["instantCorrections"] = InstantCorrections, ["aiCorrections"] = AiCorrections, ["undos"] = Undos,
        ["desyncs"] = Desyncs, ["aiRequests"] = AiRequests, ["aiFailures"] = AiFailures,
        ["rejectedEdits"] = RejectedEdits, ["droppedWrites"] = DroppedWrites, ["fieldMismatches"] = FieldMismatches,
    };

    public string ToJson() => JsonSerializer.Serialize(Snapshot());
}
