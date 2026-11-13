namespace LocalDocSearch.Core;

/// <summary>Per-query logical resource budgets, measured in UTF-16 code units and item counts.
/// These are not a hard process-memory ceiling. Each query uses a validated immutable snapshot.</summary>
public sealed record SearchResourceLimits
{
    public int MaxSegmentCharacters { get; init; } = 1_048_576;
    public int MaxMatchesPerFile { get; init; } = 50_000;
    public int MaxTotalMatches { get; init; } = 200_000;
    /// <summary>UTF-16 payload retained in Before/Text/After, checked before substring allocation.
    /// Object overhead and third-party parser allocations are outside these budgets.</summary>
    public int MaxResultCharactersPerFile { get; init; } = 16_777_216;
    public int MaxTotalResultCharacters { get; init; } = 33_554_432;
    public int MaxMatchedFiles { get; init; } = 20_000;
    public int MaxScannedFiles { get; init; } = 100_000;
    public int MaxIssueDetails { get; init; } = 1_000;
    public int MaxWarningsPerFile { get; init; } = 100;
    public int MaxPendingDirectories { get; init; } = 20_000;
    /// <summary>Root has depth zero; this is the maximum permitted child depth.</summary>
    public int MaxDirectoryDepth { get; init; } = 128;
    public int MaxTerms { get; init; } = 64;
    public int MaxTermCharacters { get; init; } = 4096;
    public int MaxRegexPatternCharacters { get; init; } = 4096;
    /// <summary>Approximate .NET timeout for each Match/NextMatch operation, from 1 to 1000 ms.</summary>
    public int RegexMatchTimeoutMilliseconds { get; init; } = 100;
    /// <summary>Cumulative matching time per file, excluding extraction and I/O, from 1 to 60000 ms.
    /// Checked between regex operations; this is not a hard wall-clock or cancellation deadline.</summary>
    public int MaxRegexFileMilliseconds { get; init; } = 1000;

    internal SearchResourceLimits Snapshot()
    {
        var snapshot = this with { };
        Positive(snapshot.MaxSegmentCharacters, nameof(MaxSegmentCharacters));
        Positive(snapshot.MaxMatchesPerFile, nameof(MaxMatchesPerFile));
        Positive(snapshot.MaxTotalMatches, nameof(MaxTotalMatches));
        Positive(snapshot.MaxResultCharactersPerFile, nameof(MaxResultCharactersPerFile));
        Positive(snapshot.MaxTotalResultCharacters, nameof(MaxTotalResultCharacters));
        Positive(snapshot.MaxMatchedFiles, nameof(MaxMatchedFiles));
        Positive(snapshot.MaxScannedFiles, nameof(MaxScannedFiles));
        Positive(snapshot.MaxIssueDetails, nameof(MaxIssueDetails));
        Positive(snapshot.MaxWarningsPerFile, nameof(MaxWarningsPerFile));
        Positive(snapshot.MaxPendingDirectories, nameof(MaxPendingDirectories));
        Positive(snapshot.MaxDirectoryDepth, nameof(MaxDirectoryDepth));
        Positive(snapshot.MaxTerms, nameof(MaxTerms));
        Positive(snapshot.MaxTermCharacters, nameof(MaxTermCharacters));
        Positive(snapshot.MaxRegexPatternCharacters, nameof(MaxRegexPatternCharacters));
        Bounded(snapshot.RegexMatchTimeoutMilliseconds, 1000, nameof(RegexMatchTimeoutMilliseconds));
        Bounded(snapshot.MaxRegexFileMilliseconds, 60_000, nameof(MaxRegexFileMilliseconds));
        return snapshot;
    }

    private static void Bounded(int value, int maximum, string name)
    {
        if (value < 1 || value > maximum)
            throw new ArgumentOutOfRangeException(name, value, $"资源限额须为 1 至 {maximum} 的整数。");
    }

    private static void Positive(int value, string name)
    {
        if (value <= 0) throw new ArgumentOutOfRangeException(name, value, "资源限额须为正整数。");
    }
}
