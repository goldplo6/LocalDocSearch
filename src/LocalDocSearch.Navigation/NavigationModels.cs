using LocalDocSearch.Core;

namespace LocalDocSearch.Navigation;

public enum LocateLevel { Exact, Section, OpenedOnly, Failed }

/// <summary>A user-initiated open request. Office automation is opt-in for each action.</summary>
public sealed record LocateRequest(string FullPath, MatchItem? Match = null, bool AllowOfficeAutomation = false);

/// <summary>Exact means a read-back verified cell or source-matched text selection; Section means a verified slide.</summary>
public sealed record LocateResult(LocateLevel Level, string Message);

public interface IFileNavigator
{
    /// <summary>
    /// Open or locate only in response to an explicit user action. Cancellation stops waiting
    /// and cancels subsequent steps, but never terminates Office or an in-flight COM/shell call;
    /// a call already dispatched to Office or Windows may finish after cancellation.
    /// </summary>
    Task<LocateResult> LocateAsync(LocateRequest request, CancellationToken cancellationToken = default);
}

internal enum OfficeTargetKind { ExcelCell, PowerPointSlide, WordText }

internal sealed record OfficeTarget(string FullPath, OfficeTargetKind Kind,
    string? Sheet = null, string? Address = null, int? Slide = null)
{
    internal OfficeTextTarget? TextTarget { get; init; }
}

internal sealed record OfficeTextTarget(MatchItem Match, int? ObjectIndex = null);

// Evidence contains read-back values, never a trusted success level. The navigator
// independently compares the full path and requested location before assigning a grade.
internal sealed record OfficeLocationEvidence(bool Opened, string Message, string? DocumentPath = null,
    string? Sheet = null, string? Address = null, int? Slide = null)
{
    internal OfficeTextSelectionEvidence? TextSelection { get; init; }
}

internal sealed record OfficeTextSelectionEvidence(string ParagraphText, string SelectedText,
    int ParagraphStart, int SelectionStart, int SelectionLength, int Paragraph,
    int? ObjectIndex = null, string? ObjectName = null);

internal interface INavigationBackend
{
    bool IsWindows { get; }
    Task<bool> FileExistsAsync(string fullPath, CancellationToken cancellationToken);
    Task OpenShellAsync(string fullPath, CancellationToken cancellationToken);
    Task<OfficeLocationEvidence> LocateOfficeAsync(OfficeTarget target, CancellationToken cancellationToken);
}

internal sealed class OfficeTimeoutException(string message) : TimeoutException(message);
internal sealed class OfficeBusyException(string message) : InvalidOperationException(message);
