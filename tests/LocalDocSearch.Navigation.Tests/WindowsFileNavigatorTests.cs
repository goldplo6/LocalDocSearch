using System.Runtime.InteropServices;
using LocalDocSearch.Core;
using Xunit;

namespace LocalDocSearch.Navigation.Tests;

public sealed class WindowsFileNavigatorTests
{
    private static string Document(string extension = ".xlsx") =>
        Path.Combine(Path.GetTempPath(), "Navigation 中文 ' quote & $ data" + extension);

    private static MatchItem Excel(string? sheet = "数据 ' 1", string? address = "B12") =>
        new("词", "前", "词", "后", new SourceLocator(SourceKind.ExcelCell, Sheet: sheet, Address: address));

    private static MatchItem Slide(int? slide = 3, SourceKind kind = SourceKind.SlideText) =>
        new("词", "前", "词", "后", new SourceLocator(kind, Slide: slide));

    [Fact]
    public async Task Default_is_shell_only_even_with_an_exact_office_match()
    {
        var backend = new FakeBackend();
        var request = new LocateRequest(Document(), Excel());
        Assert.False(request.AllowOfficeAutomation);
        var result = await new WindowsFileNavigator(backend).LocateAsync(request);
        Assert.Equal(LocateLevel.OpenedOnly, result.Level);
        Assert.Equal(0, backend.OfficeCalls);
        Assert.Equal([Path.GetFullPath(request.FullPath)], backend.ShellPaths);
        Assert.Contains("未启用", result.Message);
    }

    [Theory]
    [InlineData(".txt")]
    [InlineData(".md")]
    [InlineData(".log")]
    [InlineData(".csv")]
    [InlineData(".pdf")]
    [InlineData(".docx")]
    public async Task Non_supported_office_locations_only_open_the_document(string extension)
    {
        var backend = new FakeBackend();
        var match = new MatchItem("same", "", "same", "",
            new SourceLocator(SourceKind.WordParagraph, Paragraph: 100));
        var result = await new WindowsFileNavigator(backend).LocateAsync(new(Document(extension), match, true));
        Assert.Equal(LocateLevel.OpenedOnly, result.Level);
        Assert.Equal(0, backend.OfficeCalls);
        Assert.Single(backend.ShellPaths);
        if (extension == ".docx") Assert.Contains("结构提示", result.Message);
    }

    [Fact]
    public async Task Opt_in_without_a_match_is_still_shell_only()
    {
        var backend = new FakeBackend();
        var result = await new WindowsFileNavigator(backend).LocateAsync(new(Document(), AllowOfficeAutomation: true));
        Assert.Equal(LocateLevel.OpenedOnly, result.Level);
        Assert.Equal(0, backend.OfficeCalls);
        Assert.Single(backend.ShellPaths);
    }

    [Fact]
    public async Task Verified_excel_file_sheet_and_single_cell_is_exact()
    {
        var path = Document(".XLSX");
        var backend = new FakeBackend
        {
            Office = (_, _) => Task.FromResult(new OfficeLocationEvidence(true, "fake", path.ToUpperInvariant(),
                "数据 ' 1", "$B$12"))
        };
        var result = await new WindowsFileNavigator(backend).LocateAsync(new(path, Excel(address: "b12"), true));
        Assert.Equal(LocateLevel.Exact, result.Level);
        Assert.Contains("B12", result.Message);
        Assert.Equal(1, backend.OfficeCalls);
        Assert.Empty(backend.ShellPaths);
        Assert.Equal("B12", backend.LastTarget?.Address);
    }

    [Theory]
    [InlineData("数据 ' 1", "B13")]
    [InlineData("Other", "B12")]
    [InlineData(null, "B12")]
    [InlineData("数据 ' 1", null)]
    [InlineData("数据 ' 1", "B12:C12")]
    [InlineData("数据 ' 1", "B12,B14")]
    [InlineData("数据 ' 1", "R12C2")]
    public async Task Unverified_excel_selection_is_opened_only_without_duplicate_open(string? sheet, string? address)
    {
        var path = Document();
        var backend = new FakeBackend
        {
            Office = (_, _) => Task.FromResult(new OfficeLocationEvidence(true, "protected", path, sheet, address))
        };
        var result = await new WindowsFileNavigator(backend).LocateAsync(new(path, Excel(), true));
        Assert.Equal(LocateLevel.OpenedOnly, result.Level);
        Assert.Empty(backend.ShellPaths);
    }

    [Fact]
    public async Task Sheet_name_must_match_ordinal_not_case_insensitively()
    {
        var path = Document();
        var backend = new FakeBackend
        {
            Office = (_, _) => Task.FromResult(new OfficeLocationEvidence(true, "", path, "sheet", "A1"))
        };
        var result = await new WindowsFileNavigator(backend).LocateAsync(new(path, Excel("Sheet", "A1"), true));
        Assert.Equal(LocateLevel.OpenedOnly, result.Level);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("different.xlsx")]
    public async Task An_open_document_without_verified_fullpath_is_failed(string? otherName)
    {
        var backend = new FakeBackend
        {
            Office = (_, _) => Task.FromResult(new OfficeLocationEvidence(true, "wrong document",
                otherName is null ? null : Path.Combine(Path.GetTempPath(), otherName), "数据 ' 1", "B12"))
        };
        var result = await new WindowsFileNavigator(backend).LocateAsync(new(Document(), Excel(), true));
        Assert.Equal(LocateLevel.Failed, result.Level);
        Assert.Contains("完整路径", result.Message);
        Assert.Empty(backend.ShellPaths);
    }

    [Theory]
    [InlineData("")]
    [InlineData("A0")]
    [InlineData("A01")]
    [InlineData("XFE1")]
    [InlineData("XFD1048577")]
    [InlineData("AAAA1")]
    [InlineData("A1:B2")]
    [InlineData("A1,B2")]
    [InlineData("Sheet1!A1")]
    [InlineData("=HYPERLINK(1)")]
    [InlineData("A1;cmd")]
    [InlineData(" A1")]
    [InlineData("A１")]
    public async Task Invalid_a1_never_reaches_com(string address)
    {
        var backend = new FakeBackend();
        var result = await new WindowsFileNavigator(backend).LocateAsync(new(Document(), Excel(address: address), true));
        Assert.Equal(LocateLevel.OpenedOnly, result.Level);
        Assert.Equal(0, backend.OfficeCalls);
        Assert.Single(backend.ShellPaths);
    }

    [Theory]
    [InlineData("A1", "A1")]
    [InlineData("z99", "Z99")]
    [InlineData("$b$12", "B12")]
    [InlineData("XFD1048576", "XFD1048576")]
    [InlineData("$XFD$1048576", "XFD1048576")]
    public void Legal_a1_is_canonicalized_without_evaluating_formulas(string address, string expected)
    {
        Assert.True(NavigationValidation.TryAddress(address, out var actual));
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("bad/name")]
    [InlineData("bad!not-sheet-with-more-than-thirty-one-characters")]
    public async Task Missing_or_invalid_sheet_skips_com(string? sheet)
    {
        var backend = new FakeBackend();
        var result = await new WindowsFileNavigator(backend).LocateAsync(new(Document(), Excel(sheet), true));
        Assert.Equal(LocateLevel.OpenedOnly, result.Level);
        Assert.Equal(0, backend.OfficeCalls);
    }

    [Theory]
    [InlineData(SourceKind.SlideText)]
    [InlineData(SourceKind.SlideTableCell)]
    public async Task Powerpoint_verified_slide_is_section_never_text_exact(SourceKind kind)
    {
        var path = Document(".pptx");
        var backend = new FakeBackend
        {
            Office = (_, _) => Task.FromResult(new OfficeLocationEvidence(true, "", path, "Sheet", "A1", 3))
        };
        var result = await new WindowsFileNavigator(backend).LocateAsync(new(path, Slide(kind: kind), true));
        Assert.Equal(LocateLevel.Section, result.Level);
        Assert.Contains("未定位或验证具体文字", result.Message);
        Assert.Empty(backend.ShellPaths);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(1)]
    [InlineData(4)]
    public async Task Wrong_or_missing_slide_readback_is_opened_only(int? actualSlide)
    {
        var path = Document(".pptx");
        var backend = new FakeBackend
        {
            Office = (_, _) => Task.FromResult(new OfficeLocationEvidence(true, "", path, Slide: actualSlide))
        };
        var result = await new WindowsFileNavigator(backend).LocateAsync(new(path, Slide(), true));
        Assert.Equal(LocateLevel.OpenedOnly, result.Level);
        Assert.Empty(backend.ShellPaths);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Invalid_slide_request_never_reaches_com(int? slide)
    {
        var backend = new FakeBackend();
        var result = await new WindowsFileNavigator(backend).LocateAsync(new(Document(".pptx"), Slide(slide), true));
        Assert.Equal(LocateLevel.OpenedOnly, result.Level);
        Assert.Equal(0, backend.OfficeCalls);
    }

    [Fact]
    public async Task Extension_and_source_kind_must_agree()
    {
        var backend = new FakeBackend();
        await new WindowsFileNavigator(backend).LocateAsync(new(Document(), Slide(), true));
        await new WindowsFileNavigator(backend).LocateAsync(new(Document(".pptx"), Excel(), true));
        Assert.Equal(0, backend.OfficeCalls);
        Assert.Equal(2, backend.ShellPaths.Count);
    }

    [Fact]
    public async Task No_office_registration_falls_back_to_shell_with_reason()
    {
        var backend = new FakeBackend
        {
            Office = (_, _) => throw new COMException("class not registered", unchecked((int)0x80040154))
        };
        var result = await new WindowsFileNavigator(backend).LocateAsync(new(Document(), Excel(), true));
        Assert.Equal(LocateLevel.OpenedOnly, result.Level);
        Assert.Contains("class not registered", result.Message);
        Assert.Contains("回退", result.Message);
        Assert.Single(backend.ShellPaths);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Office_open_failure_falls_back_but_never_trusts_unopened_evidence(bool throwError)
    {
        var path = Document();
        var backend = new FakeBackend
        {
            Office = (_, _) => throwError ? throw new PlatformNotSupportedException("COM unavailable")
                : Task.FromResult(new OfficeLocationEvidence(false, "open failed", path, "数据 ' 1", "B12"))
        };
        var result = await new WindowsFileNavigator(backend).LocateAsync(new(path, Excel(), true));
        Assert.Equal(LocateLevel.OpenedOnly, result.Level);
        Assert.Single(backend.ShellPaths);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task In_flight_timeout_or_busy_never_dispatches_a_duplicate_open(bool timeout)
    {
        var backend = new FakeBackend
        {
            Office = (_, _) => timeout ? throw new OfficeTimeoutException("Office pending")
                : throw new OfficeBusyException("Office busy")
        };
        var result = await new WindowsFileNavigator(backend).LocateAsync(new(Document(), Excel(), true));
        Assert.Equal(LocateLevel.Failed, result.Level);
        Assert.Empty(backend.ShellPaths);
    }

    [Fact]
    public async Task Missing_file_is_failed_without_shell_or_office()
    {
        var backend = new FakeBackend { Exists = false };
        var result = await new WindowsFileNavigator(backend).LocateAsync(new(Document(), Excel(), true));
        Assert.Equal(LocateLevel.Failed, result.Level);
        Assert.Equal(0, backend.OfficeCalls);
        Assert.Empty(backend.ShellPaths);
    }

    [Fact]
    public async Task Shell_failure_after_com_failure_is_failed_not_opened_only()
    {
        var backend = new FakeBackend { Shell = (_, _) => throw new InvalidOperationException("No association") };
        var result = await new WindowsFileNavigator(backend).LocateAsync(new(Document(), Excel(), true));
        Assert.Equal(LocateLevel.Failed, result.Level);
        Assert.Contains("No association", result.Message);
    }

    [Fact]
    public async Task File_check_failure_is_a_failed_result()
    {
        var backend = new FakeBackend { Check = (_, _) => throw new IOException("network unavailable") };
        var result = await new WindowsFileNavigator(backend).LocateAsync(new(Document()));
        Assert.Equal(LocateLevel.Failed, result.Level);
        Assert.Contains("network unavailable", result.Message);
        Assert.Empty(backend.ShellPaths);
    }

    [Fact]
    public async Task Non_windows_reports_platform_failure_without_any_open()
    {
        var backend = new FakeBackend { IsWindows = false };
        var result = await new WindowsFileNavigator(backend).LocateAsync(new(Document(), Excel(), true));
        Assert.Equal(LocateLevel.Failed, result.Level);
        Assert.Equal(0, backend.FileChecks);
        Assert.Equal(0, backend.OfficeCalls);
        Assert.Empty(backend.ShellPaths);
    }

    public static IEnumerable<object?[]> InvalidPaths()
    {
        foreach (var path in new string?[] { null, "", "relative.xlsx", "https://example.invalid/a.xlsx",
            "shell:AppsFolder", Document(".exe"), Document(".cmd"), Document() + ":stream",
            Document() + "\n", Document() + "\"", @"\\?\C:\test.xlsx", @"\\.\test.xlsx" })
            yield return [path];
    }

    [Theory]
    [MemberData(nameof(InvalidPaths))]
    public async Task Invalid_or_command_paths_never_reach_any_backend(string? path)
    {
        var backend = new FakeBackend();
        var result = await new WindowsFileNavigator(backend).LocateAsync(new(path!));
        Assert.Equal(LocateLevel.Failed, result.Level);
        Assert.Equal(0, backend.FileChecks);
        Assert.Equal(0, backend.OfficeCalls);
        Assert.Empty(backend.ShellPaths);
    }

    [Fact]
    public async Task Null_request_is_a_failed_result()
    {
        var result = await new WindowsFileNavigator(new FakeBackend()).LocateAsync(null!);
        Assert.Equal(LocateLevel.Failed, result.Level);
    }

    [Fact]
    public async Task Cancellation_before_start_has_no_side_effects()
    {
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        var backend = new FakeBackend();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new WindowsFileNavigator(backend).LocateAsync(new(Document(), Excel(), true), stop.Token));
        Assert.Equal(0, backend.FileChecks);
        Assert.Equal(0, backend.OfficeCalls);
        Assert.Empty(backend.ShellPaths);
    }

    [Fact]
    public async Task Cancellation_in_office_does_not_fall_back_or_terminate_office()
    {
        using var stop = new CancellationTokenSource();
        var backend = new FakeBackend
        {
            Office = (_, token) =>
            {
                stop.Cancel();
                return Task.FromCanceled<OfficeLocationEvidence>(token);
            }
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new WindowsFileNavigator(backend).LocateAsync(new(Document(), Excel(), true), stop.Token));
        Assert.Equal(1, backend.OfficeCalls);
        Assert.Empty(backend.ShellPaths);
    }

    [Fact]
    public async Task Cancellation_after_office_return_prevents_fallback()
    {
        using var stop = new CancellationTokenSource();
        var backend = new FakeBackend
        {
            Office = (_, _) =>
            {
                stop.Cancel();
                return Task.FromResult(new OfficeLocationEvidence(false, "cancelled"));
            }
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new WindowsFileNavigator(backend).LocateAsync(new(Document(), Excel(), true), stop.Token));
        Assert.Empty(backend.ShellPaths);
    }

    [Fact]
    public async Task Cancellation_in_file_check_prevents_opening()
    {
        using var stop = new CancellationTokenSource();
        var backend = new FakeBackend
        {
            Check = (_, _) => { stop.Cancel(); return Task.FromResult(true); }
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new WindowsFileNavigator(backend).LocateAsync(new(Document(), Excel(), true), stop.Token));
        Assert.Equal(0, backend.OfficeCalls);
        Assert.Empty(backend.ShellPaths);
    }

    private sealed class FakeBackend : INavigationBackend
    {
        public bool IsWindows { get; init; } = true;
        internal bool Exists { get; init; } = true;
        internal int FileChecks { get; private set; }
        internal int OfficeCalls { get; private set; }
        internal OfficeTarget? LastTarget { get; private set; }
        internal List<string> ShellPaths { get; } = [];
        internal Func<string, CancellationToken, Task<bool>>? Check { get; init; }
        internal Func<string, CancellationToken, Task>? Shell { get; init; }
        internal Func<OfficeTarget, CancellationToken, Task<OfficeLocationEvidence>>? Office { get; init; }

        public Task<bool> FileExistsAsync(string fullPath, CancellationToken cancellationToken)
        {
            FileChecks++;
            return Check?.Invoke(fullPath, cancellationToken) ?? Task.FromResult(Exists);
        }

        public Task OpenShellAsync(string fullPath, CancellationToken cancellationToken)
        {
            ShellPaths.Add(fullPath);
            return Shell?.Invoke(fullPath, cancellationToken) ?? Task.CompletedTask;
        }

        public Task<OfficeLocationEvidence> LocateOfficeAsync(OfficeTarget target, CancellationToken cancellationToken)
        {
            OfficeCalls++;
            LastTarget = target;
            return Office?.Invoke(target, cancellationToken)
                ?? Task.FromResult(new OfficeLocationEvidence(false, "Office not installed"));
        }
    }
}
