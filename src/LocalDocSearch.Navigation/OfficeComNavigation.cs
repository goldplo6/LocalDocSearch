using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace LocalDocSearch.Navigation;

/// <summary>Late-bound COM only; extraction and construction never instantiate Office.</summary>
[SupportedOSPlatform("windows")]
internal static partial class OfficeComNavigation
{
    internal static OfficeLocationEvidence Locate(OfficeTarget target, CancellationToken token)
    {
        using var scope = new ComScope();
        var opened = false;
        string? documentPath = null;
        var note = string.Empty;
        try
        {
            token.ThrowIfCancellationRequested();
            var excel = target.Kind == OfficeTargetKind.ExcelCell;
            var word = target.Kind == OfficeTargetKind.WordText;
            var progId = word ? "Word.Application" : excel ? "Excel.Application" : "PowerPoint.Application";
            var type = Type.GetTypeFromProgID(progId)
                ?? throw new COMException($"未检测到 {progId} COM 注册。");
            dynamic? application = GetRunningApplication(type.GUID, scope);
            object? document = null;
            object? protectedWindow = null;
            if (application is not null)
                (document, protectedWindow) = FindOpenDocument((object)application, target.FullPath, target.Kind, scope, token);

            if (document is null)
            {
                token.ThrowIfCancellationRequested();
                // Request an automation instance; some Office servers may share a process.
                // Never change security settings, even if activation returns an existing instance.
                // Protected view is intrinsically read-only, with VBA/data connections restricted.
                application = scope.Take(Activator.CreateInstance(type)
                    ?? throw new COMException("Office 实例创建未完成。"));
                note = "已取得 Office 自动化实例并保持打开，由用户关闭；Office 可能复用已有进程。";
                token.ThrowIfCancellationRequested();
                application.Visible = excel || word ? (object)true : -1;
                token.ThrowIfCancellationRequested();
                dynamic protectedWindows = scope.Take((object)application.ProtectedViewWindows);
                token.ThrowIfCancellationRequested();
                protectedWindow = scope.Take((object)protectedWindows.Open(target.FullPath));
                opened = true;
                note += "使用只读保护视图；未启用编辑或修改宏设置。";
                token.ThrowIfCancellationRequested();
                dynamic view = protectedWindow;
                document = scope.Take(word ? (object)view.Document : excel ? (object)view.Workbook : (object)view.Presentation);
            }
            else
            {
                opened = true;
                note = "复用已打开的同一路径文件；保留当前只读/可写状态，未重开或保存。";
            }

            token.ThrowIfCancellationRequested();
            dynamic openedDocument = document;
            documentPath = Convert.ToString(openedDocument.FullName, CultureInfo.InvariantCulture);
            if (!NavigationValidation.SamePath(target.FullPath, documentPath))
                return new OfficeLocationEvidence(true, note + "Office 文档路径与请求不一致，已停止导航。", documentPath);

            if (word) return LocateWord((object)application!, (object)openedDocument, protectedWindow, target, scope, note, token);
            if (excel) return LocateExcel((object)application!, (object)openedDocument, protectedWindow, target, scope, note, token);
            return LocateSlide((object)openedDocument, protectedWindow, target, scope, note, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            // After opening, never open again merely because selection/Protected View is restricted.
            return new OfficeLocationEvidence(opened, $"{note}Office 定位未完成：{exception.Message}", documentPath);
        }
    }

    private static OfficeLocationEvidence LocateExcel(dynamic application, dynamic workbook, object? protectedWindow,
        OfficeTarget target, ComScope scope, string note, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (protectedWindow is not null)
        {
            dynamic view = protectedWindow;
            view.Activate();
        }
        else workbook.Activate();

        dynamic sheets = scope.Take((object)workbook.Worksheets);
        var count = Convert.ToInt32(sheets.Count, CultureInfo.InvariantCulture);
        for (var index = 1; index <= count; index++)
        {
            token.ThrowIfCancellationRequested();
            dynamic sheet = scope.Take((object)sheets.Item[index]);
            // COM name lookup is case-insensitive; enumerate to enforce the extracted exact name.
            if (!string.Equals((string)sheet.Name, target.Sheet, StringComparison.Ordinal)) continue;
            if (Convert.ToInt32(sheet.Visible, CultureInfo.InvariantCulture) != -1)
                return new OfficeLocationEvidence(true, note + "目标工作表当前隐藏；未更改可见性。", (string)workbook.FullName);
            token.ThrowIfCancellationRequested();
            sheet.Activate();
            token.ThrowIfCancellationRequested();
            dynamic range = scope.Take((object)sheet.Range[target.Address!]);
            token.ThrowIfCancellationRequested();
            application.Goto(range, true);
            token.ThrowIfCancellationRequested();
            dynamic selection = scope.Take((object)application.Selection);
            // Read the actual selection's parents rather than trusting the requested range.
            var selectedAddress = (string)selection.Address;
            dynamic selectedSheet = scope.Take((object)selection.Worksheet);
            var selectedSheetName = (string)selectedSheet.Name;
            dynamic selectedWorkbook = scope.Take((object)selectedSheet.Parent);
            var selectedPath = (string)selectedWorkbook.FullName;
            token.ThrowIfCancellationRequested();
            return new OfficeLocationEvidence(true, note, selectedPath, selectedSheetName, selectedAddress);
        }
        return new OfficeLocationEvidence(true, note + "未找到名称完全一致的工作表。", (string)workbook.FullName);
    }

    private static OfficeLocationEvidence LocateSlide(dynamic presentation, object? protectedWindow,
        OfficeTarget target, ComScope scope, string note, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var documentPath = (string)presentation.FullName;
        dynamic slides = scope.Take((object)presentation.Slides);
        var count = Convert.ToInt32(slides.Count, CultureInfo.InvariantCulture);
        var slideIndex = target.Slide.GetValueOrDefault();
        if (slideIndex < 1 || slideIndex > count)
            return new OfficeLocationEvidence(true, note + "目标幻灯片序号超出当前文稿范围。", documentPath);
        if (protectedWindow is not null)
        {
            dynamic protectedView = protectedWindow;
            protectedView.Activate();
        }
        token.ThrowIfCancellationRequested();
        dynamic windows = scope.Take((object)presentation.Windows);
        if (Convert.ToInt32(windows.Count, CultureInfo.InvariantCulture) < 1)
            return new OfficeLocationEvidence(true, note + "当前保护视图未提供可导航窗口。", documentPath);
        dynamic window = scope.Take((object)windows.Item[1]);
        token.ThrowIfCancellationRequested();
        window.Activate();
        dynamic view = scope.Take((object)window.View);
        token.ThrowIfCancellationRequested();
        view.GotoSlide(slideIndex);
        token.ThrowIfCancellationRequested();
        dynamic shownSlide = scope.Take((object)view.Slide);
        var actualSlide = Convert.ToInt32(shownSlide.SlideIndex, CultureInfo.InvariantCulture);
        dynamic shownPresentation = scope.Take((object)window.Presentation);
        var actualPath = (string)shownPresentation.FullName;
        token.ThrowIfCancellationRequested();
        var evidence = new OfficeLocationEvidence(true, note, actualPath, Slide: actualSlide);
        if (actualSlide != slideIndex || !NavigationValidation.SamePath(target.FullPath, actualPath) || target.TextTarget is null)
            return evidence;
        try
        {
            return SelectSlideText((object)window, (object)shownSlide, target, scope, evidence, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            // A restricted text selection must not discard an already verified slide or open it again.
            return evidence with { Message = note + $"文字选区未完成：{exception.Message}" };
        }
    }

    private static (object? Document, object? ProtectedWindow) FindOpenDocument(dynamic application, string path,
        OfficeTargetKind kind, ComScope scope, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var excel = kind == OfficeTargetKind.ExcelCell;
        var word = kind == OfficeTargetKind.WordText;
        dynamic documents = scope.Take(word ? (object)application.Documents : excel ? (object)application.Workbooks : (object)application.Presentations);
        var count = Convert.ToInt32(documents.Count, CultureInfo.InvariantCulture);
        for (var index = 1; index <= count; index++)
        {
            token.ThrowIfCancellationRequested();
            dynamic document = scope.Take((object)documents.Item[index]);
            if (NavigationValidation.SamePath(path, (string?)document.FullName)) return (document, null);
        }
        token.ThrowIfCancellationRequested();
        dynamic views = scope.Take((object)application.ProtectedViewWindows);
        count = Convert.ToInt32(views.Count, CultureInfo.InvariantCulture);
        for (var index = 1; index <= count; index++)
        {
            token.ThrowIfCancellationRequested();
            dynamic view = scope.Take((object)views.Item[index]);
            dynamic document = scope.Take(word ? (object)view.Document : excel ? (object)view.Workbook : (object)view.Presentation);
            if (NavigationValidation.SamePath(path, (string?)document.FullName)) return (document, view);
        }
        return (null, null);
    }

    private static object? GetRunningApplication(Guid classId, ComScope scope)
    {
        try
        {
            GetActiveObject(ref classId, IntPtr.Zero, out var application);
            return scope.Take(application);
        }
        catch (COMException exception) when (exception.HResult is unchecked((int)0x800401E3) or unchecked((int)0x80040154))
        {
            // ROT exposes one registered instance, not every instance. If no matching document
            // is found there, use a separate protected-view instance; never guess by filename.
            return null;
        }
    }

    [DllImport("oleaut32.dll", PreserveSig = false)]
    private static extern void GetActiveObject(ref Guid classId, IntPtr reserved,
        [MarshalAs(UnmanagedType.IUnknown)] out object application);

    private sealed class ComScope : IDisposable
    {
        private readonly List<object> _objects = [];
        internal object Take(object value)
        {
            _objects.Add(value);
            return value;
        }

        public void Dispose()
        {
            for (var index = _objects.Count - 1; index >= 0; index--)
            {
                try
                {
                    if (Marshal.IsComObject(_objects[index])) Marshal.ReleaseComObject(_objects[index]);
                }
                catch (Exception) { /* Releasing our RCW must not close or terminate user Office. */ }
            }
        }
    }
}
