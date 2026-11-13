using System.Runtime.InteropServices;
using Xunit;

namespace LocalDocSearch.Navigation.Tests;

public sealed class DocumentActionTests
{
    private static string Document(string extension = ".pdf") => Path.Combine(Path.GetTempPath(), "导航 中文 ' & $ (x),; find" + extension);

    [Fact]
    public async Task Find_fills_only_verified_search_value_and_preserves_original_navigation_contract()
    {
        var session = new Session();
        var navigator = new Navigator();
        var result = await Assistant(session, navigator).FindAsync(new(Document(), "查找 ' 😀 & text"));
        Assert.Equal(FindAssistStatus.SearchFieldFilled, result.Status);
        Assert.Equal("查找 ' 😀 & text", session.Value);
        Assert.Equal(1, session.SetCalls);
        Assert.Equal(1, session.OpenFindCalls);
        Assert.True(session.Disposed);
        Assert.False(navigator.Request!.AllowOfficeAutomation);
        Assert.Null(navigator.Request.Match);
        Assert.Contains("未确认", result.Message);
    }

    [Fact]
    public async Task Unsupported_viewer_returns_explicit_manual_find_and_original_query()
    {
        var result = await new WindowsOriginalFileFindAssistant(new Navigator(), new Files(), new Sessions(null))
            .FindAsync(new(Document(".docx"), "词"));
        Assert.Equal(FindAssistStatus.ManualFindRequired, result.Status);
        Assert.Equal("词", result.SearchText);
        Assert.Contains("Ctrl+F", result.Message);
        Assert.Contains("剪贴板", result.Message);
    }

    [Theory]
    [InlineData("wrongPath")]
    [InlineData("noForeground")]
    [InlineData("unsupported")]
    [InlineData("noWindow")]
    [InlineData("wrongProcess")]
    public async Task Unverified_target_never_opens_find_or_inserts_text(string defect)
    {
        var session = new Session();
        session.Target = defect switch
        {
            "wrongPath" => session.Target with { FullPath = Document(".txt") },
            "noForeground" => session.Target with { IsForeground = false },
            "unsupported" => session.Target with { SupportedViewer = false },
            "noWindow" => session.Target with { Window = 0 },
            _ => session.Target with { ProcessId = 0 }
        };
        var result = await Assistant(session).FindAsync(new(Document(), "词"));
        Assert.Equal(FindAssistStatus.ManualFindRequired, result.Status);
        Assert.Equal(0, session.OpenFindCalls);
        Assert.Equal(0, session.SetCalls);
        Assert.True(session.Disposed);
    }

    [Fact]
    public async Task Window_change_between_target_checks_prevents_find()
    {
        var session = new Session();
        session.ReadTargetHook = count => count == 1 ? session.Target : session.Target with { Window = 500 };
        Assert.Equal(FindAssistStatus.ManualFindRequired, (await Assistant(session).FindAsync(new(Document(), "词"))).Status);
        Assert.Equal(0, session.OpenFindCalls);
        Assert.Equal(0, session.SetCalls);
    }

    [Theory]
    [InlineData("documentBody")]
    [InlineData("notFocused")]
    [InlineData("readOnly")]
    [InlineData("disabled")]
    [InlineData("wrongWindow")]
    [InlineData("wrongProcess")]
    [InlineData("noIdentity")]
    public async Task Ambiguous_or_document_body_control_never_receives_text(string defect)
    {
        var session = new Session();
        session.Field = defect switch
        {
            "documentBody" => session.Field with { IsSearch = false },
            "notFocused" => session.Field with { IsFocused = false },
            "readOnly" => session.Field with { IsEditable = false },
            "disabled" => session.Field with { IsEnabled = false },
            "wrongWindow" => session.Field with { Window = 2 },
            "wrongProcess" => session.Field with { ProcessId = 555 },
            _ => session.Field with { RuntimeId = "" }
        };
        Assert.Equal(FindAssistStatus.ManualFindRequired, (await Assistant(session).FindAsync(new(Document(), "词"))).Status);
        Assert.Equal(0, session.SetCalls);
    }

    [Fact]
    public async Task Adapter_rejects_changed_field_before_mutation()
    {
        var session = new Session { Setter = (_, _) => false };
        Assert.Equal(FindAssistStatus.ManualFindRequired, (await Assistant(session).FindAsync(new(Document(), "词"))).Status);
        Assert.Empty(session.Value);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Wrong_value_or_changed_control_readback_never_reports_filled(bool changedIdentity)
    {
        var session = new Session();
        session.Setter = (_, text) =>
        {
            session.Value = changedIdentity ? text : "wrong";
            if (changedIdentity) session.Field = session.Field with { RuntimeId = "other" };
            return true;
        };
        Assert.Equal(FindAssistStatus.ManualFindRequired, (await Assistant(session).FindAsync(new(Document(), "词"))).Status);
    }

    [Theory]
    [InlineData("beforeOpenFind")]
    [InlineData("beforeSet")]
    [InlineData("afterSet")]
    public async Task Cancellation_stops_following_steps_and_disposes_session(string stage)
    {
        using var stop = new CancellationTokenSource();
        var session = new Session();
        if (stage == "beforeOpenFind") session.ReadTargetHook = _ => { stop.Cancel(); return session.Target; };
        if (stage == "beforeSet") session.ReadFieldHook = () => { stop.Cancel(); return session.Field; };
        if (stage == "afterSet") session.Setter = (_, _) => { stop.Cancel(); return true; };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Assistant(session).FindAsync(new(Document(), "词"), stop.Token));
        Assert.True(session.Disposed);
        if (stage != "afterSet") Assert.Equal(0, session.SetCalls);
    }

    [Fact]
    public async Task Cancellation_before_start_has_no_open()
    {
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        var navigator = new Navigator();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Assistant(new Session(), navigator).FindAsync(new(Document(), "词"), stop.Token));
        Assert.Null(navigator.Request);
    }

    [Fact]
    public async Task Cancelled_open_does_not_start_automation()
    {
        using var stop = new CancellationTokenSource();
        var sessions = new Sessions(new Session());
        var navigator = new Navigator { OnOpen = () => stop.Cancel() };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new WindowsOriginalFileFindAssistant(navigator, new Files(), sessions).FindAsync(new(Document(), "词"), stop.Token));
        Assert.Equal(0, sessions.Calls);
    }

    [Fact]
    public async Task Failed_open_and_missing_or_reparse_file_have_no_automation()
    {
        var sessions = new Sessions(new Session());
        var result = await new WindowsOriginalFileFindAssistant(new Navigator { Level = LocateLevel.Failed }, new Files(), sessions)
            .FindAsync(new(Document(), "词"));
        Assert.Equal(FindAssistStatus.Failed, result.Status);
        var navigator = new Navigator();
        result = await new WindowsOriginalFileFindAssistant(navigator, new Files { Exists = false }, sessions).FindAsync(new(Document(), "词"));
        Assert.Equal(FindAssistStatus.Failed, result.Status);
        Assert.Null(navigator.Request);
        Assert.Equal(0, sessions.Calls);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("word\nnext")]
    [InlineData("word\0")]
    [InlineData("word\t")]
    public async Task Invalid_find_words_do_not_open(string text)
    {
        var navigator = new Navigator();
        Assert.Equal(FindAssistStatus.Failed, (await Assistant(new Session(), navigator).FindAsync(new(Document(), text))).Status);
        Assert.Null(navigator.Request);
    }

    [Fact]
    public async Task Oversized_word_does_not_open()
    {
        var navigator = new Navigator();
        Assert.Equal(FindAssistStatus.Failed, (await Assistant(new Session(), navigator).FindAsync(new(Document(), new string('x', 2049)))).Status);
        Assert.Null(navigator.Request);
    }

    [Theory]
    [InlineData("https://example.invalid/file.pdf")]
    [InlineData("shell:AppsFolder")]
    [InlineData("relative.pdf")]
    [InlineData("C:\\file.pdf:stream")]
    [InlineData("C:\\file.exe")]
    [InlineData("C:\\file.pdf\" /select,")]
    public async Task Invalid_path_never_reaches_shell_or_find(string path)
    {
        var shell = new Pidls();
        Assert.False((await new WindowsContainingFolderRevealer(new Files(), shell).RevealAsync(path)).Succeeded);
        var navigator = new Navigator();
        Assert.Equal(FindAssistStatus.Failed, (await Assistant(new Session(), navigator).FindAsync(new(path, "词"))).Status);
        Assert.Empty(shell.Parsed);
        Assert.Null(navigator.Request);
    }

    [Fact]
    public async Task Folder_reveal_passes_unicode_punctuation_as_pidl_paths_and_releases_both()
    {
        var shell = new Pidls();
        var result = await new WindowsContainingFolderRevealer(new Files(), shell).RevealAsync(Document());
        Assert.True(result.Succeeded);
        Assert.Equal([Path.GetDirectoryName(Document())!, Document()], shell.Parsed);
        Assert.Equal((nint)100, shell.OpenParent);
        Assert.Equal((nint)201, shell.OpenChild);
        Assert.Equal([(nint)200, (nint)100], shell.Freed);
        Assert.Equal(1, shell.Uninitializations);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task Pidl_failures_release_allocated_resources_and_never_claim_success(int failAt)
    {
        var shell = new Pidls { FailAt = failAt };
        Assert.False((await new WindowsContainingFolderRevealer(new Files(), shell).RevealAsync(Document())).Succeeded);
        Assert.Equal(failAt == 1 ? [(nint)100] : new[] { (nint)200, (nint)100 }, shell.Freed);
        Assert.Equal(1, shell.Uninitializations);
        if (failAt < 3) Assert.Equal((nint)0, shell.OpenParent);
    }

    [Fact]
    public async Task Cancel_after_parse_frees_pidl_without_opening_folder()
    {
        using var stop = new CancellationTokenSource();
        var shell = new Pidls { OnParse = () => stop.Cancel() };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new WindowsContainingFolderRevealer(new Files(), shell).RevealAsync(Document(), stop.Token));
        Assert.Equal([(nint)100], shell.Freed);
        Assert.Equal((nint)0, shell.OpenParent);
        Assert.Equal(1, shell.Uninitializations);
    }

    [Fact]
    public async Task Folder_missing_file_and_non_windows_do_not_call_shell()
    {
        var shell = new Pidls();
        Assert.False((await new WindowsContainingFolderRevealer(new Files { Exists = false }, shell).RevealAsync(Document())).Succeeded);
        Assert.False((await new WindowsContainingFolderRevealer(new Files(), shell, false).RevealAsync(Document())).Succeeded);
        Assert.Empty(shell.Parsed);
    }

    [Theory]
    [InlineData("Find", true)]
    [InlineData("Find (Ctrl+F)", true)]
    [InlineData("在 PDF 中查找", true)]
    [InlineData("Document content", false)]
    [InlineData("Address and search bar", false)]
    [InlineData("Search", false)]
    [InlineData("查找与替换", false)]
    public void Only_explicit_find_control_names_are_accepted(string name, bool expected) =>
        Assert.Equal(expected, BrowserPdfFindSession.IsSearchName(name));

    [Fact]
    public void Browser_address_must_be_full_original_file_uri_not_title_or_remote_site()
    {
        var path = Document();
        Assert.True(BrowserPdfFindSession.SameFileAddress(path, new Uri(path).AbsoluteUri));
        Assert.True(BrowserPdfFindSession.SameFileAddress(path, new Uri(path).AbsoluteUri + "#page=3"));
        Assert.False(BrowserPdfFindSession.SameFileAddress(path, Path.GetFileName(path)));
        Assert.False(BrowserPdfFindSession.SameFileAddress(path, "https://example.invalid/" + Path.GetFileName(path)));
        Assert.False(BrowserPdfFindSession.SameFileAddress(path, new Uri(Document(".txt")).AbsoluteUri));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Reparse_file_parent_and_ancestor_are_rejected_before_shell(int depth)
    {
        var path = Document();
        var bad = path;
        for (var index = 0; index < depth; index++) bad = Path.GetDirectoryName(bad)!;
        var probe = new DocumentActionFileProbe(_ => true, current => current == bad ? FileAttributes.ReparsePoint : FileAttributes.Normal);
        Assert.False(await probe.IsExistingRegularFileAsync(path, default));
        var shell = new Pidls();
        Assert.False((await new WindowsContainingFolderRevealer(probe, shell).RevealAsync(path)).Succeeded);
        Assert.Empty(shell.Parsed);
    }

    [Fact]
    public async Task Missing_path_and_directory_are_not_original_files()
    {
        Assert.False(await new DocumentActionFileProbe(_ => false, _ => throw new Exception("Must not inspect missing file"))
            .IsExistingRegularFileAsync(Document(), default));
        Assert.False(await new DocumentActionFileProbe(_ => true, _ => FileAttributes.Directory)
            .IsExistingRegularFileAsync(Document(), default));
    }

    [Fact]
    public async Task File_probe_io_failure_becomes_actionable_failure_without_shell()
    {
        var shell = new Pidls();
        var probe = new DocumentActionFileProbe(_ => true, _ => throw new IOException("path changed"));
        var result = await new WindowsContainingFolderRevealer(probe, shell).RevealAsync(Document());
        Assert.False(result.Succeeded);
        Assert.Contains("path changed", result.Message);
        Assert.Empty(shell.Parsed);
    }

    [Theory]
    [InlineData("/C/中文路径/a ' & $ (x),;.pdf", "C:\\中文路径\\a ' & $ (x),;.pdf")]
    [InlineData("/c/temp/a.pdf", "c:\\temp\\a.pdf")]
    [InlineData("//server/share/文件.pdf", "\\\\server\\share\\文件.pdf")]
    public void Acrobat_device_independent_full_path_is_preserved(string input, string expected)
    {
        Assert.True(AcrobatDocumentIdentity.TryNormalizePath(input, out var actual));
        Assert.Equal(expected, actual, ignoreCase: true);
    }

    [Theory]
    [InlineData("a.pdf")]
    [InlineData("C:/temp/a.pdf")]
    [InlineData("https://example.invalid/a.pdf")]
    [InlineData("/C/temp/a.pdf:stream")]
    [InlineData("/C/temp/a.pdf\"")]
    [InlineData("/C/temp/a.exe")]
    public void Acrobat_basename_remote_and_malformed_paths_are_not_identity(string input) =>
        Assert.False(AcrobatDocumentIdentity.TryNormalizePath(input, out _));

    [Fact]
    public void Viewer_factory_retries_shell_window_transition_and_releases_rejected_sessions()
    {
        var rejected = new Session { ReadTargetHook = _ => null };
        var accepted = new Session();
        var count = 0;
        var factory = new BrowserPdfFindSessionFactory(_ => ++count switch
        {
            1 => throw new NotSupportedException(),
            2 => rejected,
            _ => accepted
        }, 4, 0, true);
        Assert.Same(accepted, factory.TryCreate(Document(), default));
        Assert.True(rejected.Disposed);
        Assert.False(accepted.Disposed);
        Assert.Equal(3, count);
    }

    [Fact]
    public void Viewer_factory_cancellation_during_open_releases_session_before_returning()
    {
        using var stop = new CancellationTokenSource();
        var rejected = new Session { ReadTargetHook = _ => { stop.Cancel(); return null; } };
        var factory = new BrowserPdfFindSessionFactory(_ => rejected, 4, 0, true);
        Assert.ThrowsAny<OperationCanceledException>(() => factory.TryCreate(Document(), stop.Token));
        Assert.True(rejected.Disposed);
        Assert.Equal(0, rejected.OpenFindCalls);
    }

    [Fact]
    public void Viewer_factory_unsupported_types_never_probe_foreground()
    {
        var count = 0;
        var factory = new BrowserPdfFindSessionFactory(_ => { count++; return new Session(); }, 4, 0, true);
        Assert.Null(factory.TryCreate(Document(".docx"), default));
        Assert.Equal(0, count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Timed_out_or_cancelled_worker_retains_single_flight_until_real_exit(bool cancel)
    {
        using var gate = new SemaphoreSlim(1, 1);
        using var stop = new CancellationTokenSource();
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = new Session();
        session.ReadTargetHook = _ => { entered.TrySetResult(); release.Wait(); return session.Target; };
        var assistant = new WindowsOriginalFileFindAssistant(new Navigator(), new Files(), new Sessions(session),
            assistanceTimeout: TimeSpan.FromMilliseconds(60), workerGate: gate);
        var first = assistant.FindAsync(new(Document(), "词"), stop.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            if (cancel)
            {
                stop.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
            }
            else Assert.Equal(FindAssistStatus.ManualFindRequired, (await first).Status);
            var secondSessions = new Sessions(new Session());
            var secondNavigator = new Navigator();
            var second = new WindowsOriginalFileFindAssistant(secondNavigator, new Files(), secondSessions, workerGate: gate);
            var result = await second.FindAsync(new(Document(), "第二词"));
            Assert.Equal(FindAssistStatus.ManualFindRequired, result.Status);
            Assert.Contains("上一次", result.Message);
            Assert.NotNull(secondNavigator.Request);
            Assert.Equal(0, secondSessions.Calls);
            Assert.Equal(0, gate.CurrentCount);
            Assert.Equal(0, session.SetCalls);
        }
        finally
        {
            release.Set();
            Assert.True(await gate.WaitAsync(TimeSpan.FromSeconds(3)));
            gate.Release();
        }
        Assert.True(session.Disposed);
        Assert.Equal(0, session.SetCalls);
        Assert.Equal(FindAssistStatus.SearchFieldFilled, (await new WindowsOriginalFileFindAssistant(
            new Navigator(), new Files(), new Sessions(new Session()), workerGate: gate).FindAsync(new(Document(), "新词"))).Status);
    }

    private static WindowsOriginalFileFindAssistant Assistant(Session session, Navigator? navigator = null) =>
        new(navigator ?? new Navigator(), new Files(), new Sessions(session));
    private sealed class Files : IDocumentActionFileProbe
    {
        internal bool Exists { get; init; } = true;
        public Task<bool> IsExistingRegularFileAsync(string path, CancellationToken cancellationToken) => Task.FromResult(Exists);
    }
    private sealed class Navigator : IFileNavigator
    {
        internal LocateRequest? Request;
        internal LocateLevel Level = LocateLevel.OpenedOnly;
        internal Action? OnOpen;
        public Task<LocateResult> LocateAsync(LocateRequest request, CancellationToken cancellationToken = default)
        { Request = request; OnOpen?.Invoke(); return Task.FromResult(new LocateResult(Level, "打开结果")); }
    }
    private sealed class Sessions(IFindWindowSession? session) : IFindWindowSessionFactory
    {
        internal int Calls;
        public IFindWindowSession? TryCreate(string fullPath, CancellationToken token) { Calls++; return session; }
    }
    private sealed class Session : IFindWindowSession
    {
        internal FindTargetEvidence Target = new(100, 42, Document(), true, true);
        internal FindSearchFieldEvidence Field = new(100, 42, "runtime-search", true, true, true, true, "");
        internal string Value = "";
        internal int SetCalls, OpenFindCalls, TargetReads;
        internal bool Disposed;
        internal Func<int, FindTargetEvidence?>? ReadTargetHook;
        internal Func<FindSearchFieldEvidence?>? ReadFieldHook;
        internal Func<FindSearchFieldEvidence, string, bool>? Setter;
        public FindTargetEvidence? ReadTarget() => ReadTargetHook is not null ? ReadTargetHook(++TargetReads) : Target;
        public bool OpenFind(CancellationToken token) { OpenFindCalls++; return true; }
        public FindSearchFieldEvidence? ReadSearchField() => ReadFieldHook is not null ? ReadFieldHook() : Field with { Value = Value };
        public bool SetSearchValue(FindSearchFieldEvidence expectedField, string text, CancellationToken token)
        { SetCalls++; if (Setter is not null) return Setter(expectedField, text); Value = text; return true; }
        public void Dispose() => Disposed = true;
    }
    private sealed class Pidls : IShellPidlApi
    {
        internal List<string> Parsed = [];
        internal List<nint> Freed = [];
        internal nint OpenParent, OpenChild;
        internal int Uninitializations, FailAt;
        internal Action? OnParse;
        public bool Initialize() => true;
        public void Uninitialize() => Uninitializations++;
        public PidlParseResult Parse(string path)
        {
            Parsed.Add(path); OnParse?.Invoke();
            return new(Parsed.Count == FailAt ? unchecked((int)0x80004005) : 0, Parsed.Count * 100);
        }
        public nint LastId(nint absolute) => absolute + 1;
        public int Open(nint parent, nint child) { OpenParent = parent; OpenChild = child; return FailAt == 3 ? unchecked((int)0x80004005) : 0; }
        public void Free(nint pointer) => Freed.Add(pointer);
    }
}
