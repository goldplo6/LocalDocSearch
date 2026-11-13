using System.Runtime.InteropServices;

namespace LocalDocSearch.Navigation;

/// <summary>Uses Windows Shell PIDLs; never resolves or starts explorer.exe from PATH.</summary>
public sealed class WindowsContainingFolderRevealer : IContainingFolderRevealer
{
    private readonly IDocumentActionFileProbe _files;
    private readonly IShellPidlApi _shell;
    private readonly bool _isWindows;

    public WindowsContainingFolderRevealer() : this(new DocumentActionFileProbe(), new WindowsShellPidlApi(), OperatingSystem.IsWindows()) { }
    internal WindowsContainingFolderRevealer(IDocumentActionFileProbe files, IShellPidlApi shell, bool isWindows = true)
        => (_files, _shell, _isWindows) = (files, shell, isWindows);

    public async Task<FolderRevealResult> RevealAsync(string fullPath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!NavigationValidation.TryPath(fullPath, out var path, out var error)) return new(false, error);
        if (!_isWindows) return new(false, "打开所在文件夹仅支持 Windows。");
        try
        {
            if (!await _files.IsExistingRegularFileAsync(path, cancellationToken).ConfigureAwait(false))
                return new(false, "目标文件不存在、不可访问或路径包含重解析点；请检查原文件路径。");
            await Task.Run(() => Reveal(path, cancellationToken), cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return new(true, "已通过 Windows Shell 请求打开所在文件夹并选中原文件。");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) { return new(false, $"打开所在文件夹未完成：{exception.Message}"); }
    }

    private void Reveal(string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var initialized = _shell.Initialize();
        try
        {
            var parent = Path.GetDirectoryName(path) ?? throw new IOException("原文件没有可打开的父文件夹。");
            var folder = _shell.Parse(parent);
            using var folderOwner = new PidlOwner(_shell, folder.Pointer);
            Marshal.ThrowExceptionForHR(folder.HResult);
            if (folder.Pointer == 0) throw new IOException("Windows Shell 未返回父文件夹标识。");
            token.ThrowIfCancellationRequested();
            var item = _shell.Parse(path);
            using var itemOwner = new PidlOwner(_shell, item.Pointer);
            Marshal.ThrowExceptionForHR(item.HResult);
            if (item.Pointer == 0) throw new IOException("Windows Shell 未返回原文件标识。");
            var child = _shell.LastId(item.Pointer);
            if (child == 0) throw new IOException("Windows Shell 未返回原文件相对标识。");
            token.ThrowIfCancellationRequested();
            Marshal.ThrowExceptionForHR(_shell.Open(folder.Pointer, child));
        }
        finally { if (initialized) _shell.Uninitialize(); }
    }

    private sealed class PidlOwner(IShellPidlApi api, nint pointer) : IDisposable
    {
        public void Dispose() { if (pointer != 0) api.Free(pointer); }
    }
}

internal readonly record struct PidlParseResult(int HResult, nint Pointer);
internal interface IShellPidlApi
{
    bool Initialize();
    void Uninitialize();
    PidlParseResult Parse(string path);
    nint LastId(nint absolute);
    int Open(nint parent, nint child);
    void Free(nint pointer);
}

internal sealed class WindowsShellPidlApi : IShellPidlApi
{
    public bool Initialize()
    {
        var result = CoInitializeEx(0, 0); // MTA worker; balance both S_OK and S_FALSE.
        Marshal.ThrowExceptionForHR(result);
        return true;
    }
    public void Uninitialize() => CoUninitialize();
    public PidlParseResult Parse(string path)
    {
        var hr = SHParseDisplayName(path, 0, out var pointer, 0, out _);
        return new(hr, pointer);
    }
    public nint LastId(nint absolute) => ILFindLastID(absolute);
    public int Open(nint parent, nint child) => SHOpenFolderAndSelectItems(parent, 1, [child], 0);
    public void Free(nint pointer) => ILFree(pointer);

    [DllImport("ole32.dll", ExactSpelling = true)] private static extern int CoInitializeEx(nint reserved, uint flags);
    [DllImport("ole32.dll", ExactSpelling = true)] private static extern void CoUninitialize();
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHParseDisplayName(string name, nint bindContext, out nint pidl, uint attributesIn, out uint attributesOut);
    [DllImport("shell32.dll", ExactSpelling = true)] private static extern nint ILFindLastID(nint pidl);
    [DllImport("shell32.dll", ExactSpelling = true)] private static extern int SHOpenFolderAndSelectItems(nint folder, uint count, [In] nint[] children, uint flags);
    [DllImport("shell32.dll", ExactSpelling = true)] private static extern void ILFree(nint pidl);
}
