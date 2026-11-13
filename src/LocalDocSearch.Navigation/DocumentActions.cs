namespace LocalDocSearch.Navigation;

/// <summary>Find-field assistance never asserts an exact source match or selection.</summary>
public enum FindAssistStatus { SearchFieldFilled, ManualFindRequired, Failed }

public sealed record FindAssistRequest(string FullPath, string SearchText);
public sealed record FindAssistResult(FindAssistStatus Status, string Message, string SearchText);

public interface IOriginalFileFindAssistant
{
    /// <summary>Explicit user action. Opens the original, then fills only a verified search field.
    /// Unsupported or ambiguous windows require manual Find. Never reads or writes the clipboard.</summary>
    Task<FindAssistResult> FindAsync(FindAssistRequest request, CancellationToken cancellationToken = default);
}

public sealed record FolderRevealResult(bool Succeeded, string Message);

public interface IContainingFolderRevealer
{
    /// <summary>Explicit user action. Opens the containing folder and selects the original via Shell PIDLs.</summary>
    Task<FolderRevealResult> RevealAsync(string fullPath, CancellationToken cancellationToken = default);
}

internal interface IDocumentActionFileProbe
{
    Task<bool> IsExistingRegularFileAsync(string path, CancellationToken cancellationToken);
}

internal sealed class DocumentActionFileProbe : IDocumentActionFileProbe
{
    private readonly Func<string, bool> _exists;
    private readonly Func<string, FileAttributes> _attributes;
    internal DocumentActionFileProbe(Func<string, bool>? exists = null, Func<string, FileAttributes>? attributes = null)
        => (_exists, _attributes) = (exists ?? File.Exists, attributes ?? File.GetAttributes);

    public Task<bool> IsExistingRegularFileAsync(string path, CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_exists(path)) return false;
            // Do not allow a junction/symlink to redirect the requested original or its parent.
            for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var attributes = _attributes(current);
                if ((attributes & FileAttributes.ReparsePoint) != 0) return false;
                if (current == path && (attributes & FileAttributes.Directory) != 0) return false;
            }
            return true;
        }, cancellationToken);
}
