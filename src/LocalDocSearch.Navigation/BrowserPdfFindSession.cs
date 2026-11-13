using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace LocalDocSearch.Navigation;

/// <summary>BCL COM interop only. Verifies full-path browser addresses or Acrobat's active-document JS path.
/// Captions alone never prove document identity. Uses only named Find controls outside document content.</summary>
internal sealed class BrowserPdfFindSessionFactory : IFindWindowSessionFactory
{
    private readonly Func<string, IFindWindowSession> _create;
    private readonly int _attempts, _delay;
    private readonly bool _windows;
    internal BrowserPdfFindSessionFactory() : this(path => new BrowserPdfFindSession(path), 40, 100, OperatingSystem.IsWindows()) { }
    internal BrowserPdfFindSessionFactory(Func<string, IFindWindowSession> create, int attempts, int delay, bool windows)
        => (_create, _attempts, _delay, _windows) = (create, attempts, delay, windows);

    public IFindWindowSession? TryCreate(string fullPath, CancellationToken token)
    {
        if (!_windows || !Path.GetExtension(fullPath).Equals(".pdf", StringComparison.OrdinalIgnoreCase)) return null;
        for (var attempt = 0; attempt < _attempts; attempt++)
        {
            token.ThrowIfCancellationRequested();
            IFindWindowSession? session = null;
            var accepted = false;
            try
            {
                session = _create(fullPath);
                if (session.ReadTarget() is not null) { accepted = true; return session; }
            }
            catch (NotSupportedException) { }
            catch (COMException) { }
            catch (InvalidOperationException) { }
            finally { if (!accepted) session?.Dispose(); }
            if (token.WaitHandle.WaitOne(_delay)) token.ThrowIfCancellationRequested();
        }
        return null;
    }
}

internal sealed class BrowserPdfFindSession : IFindWindowSession
{
    private readonly string _path;
    private readonly nint _window;
    private readonly int _processId;
    private readonly IUiaAutomation _automation;
    private readonly IUiaElement _root;
    private readonly IUiaTreeWalker _walker;
    private readonly object _edits;
    private readonly object _buttons;
    private readonly object? _acrobat;
    private readonly List<object> _owned = [];
    private IUiaElement? _field;
    private bool _disposed;

    internal BrowserPdfFindSession(string path)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("PDF 查看器辅助仅支持 Windows。");
        _path = path;
        _window = GetForegroundWindow();
        GetWindowThreadProcessId(_window, out _processId);
        using var process = Process.GetProcessById(_processId);
        var name = process.ProcessName.ToLowerInvariant();
        if (name is not ("msedge" or "chrome" or "acrobat" or "acrord32")) throw new NotSupportedException("当前前台窗口不是受支持的 PDF 查看器。");
        try
        {
            if (name is "acrobat" or "acrord32")
            {
                // Attach only after the original has been shell-opened and Acrobat owns the foreground.
                // No preferences, security modes, source document content, or application lifetime changes.
                var acrobatType = Type.GetTypeFromProgID("AcroExch.App", throwOnError: true)!;
                _acrobat = Own(Activator.CreateInstance(acrobatType)!);
            }
            var type = Type.GetTypeFromCLSID(new Guid("ff48dba4-60ef-4201-aa87-54103eef594e"), throwOnError: true)!;
            _automation = Own((IUiaAutomation)Activator.CreateInstance(type)!);
            _root = Own(_automation.ElementFromHandle(_window));
            _walker = Own(_automation.ControlViewWalker);
            _edits = Own(_automation.CreatePropertyCondition(30003, 50004)); // ControlType.Edit
            _buttons = Own(_automation.CreatePropertyCondition(30003, 50000)); // ControlType.Button
        }
        catch { Dispose(); throw; }
    }

    public FindTargetEvidence? ReadTarget()
    {
        if (!CurrentWindow()) return null;
        if (_acrobat is not null)
        {
            dynamic app = _acrobat;
            object? active = app.GetActiveDoc();
            if (active is null) return null;
            dynamic av = Own(active);
            dynamic pd = Own((object)av.GetPDDoc());
            object? javaScript = pd.GetJSObject();
            if (javaScript is null) return null;
            var devicePath = AcrobatDocumentIdentity.ReadPath(Own(javaScript));
            var caption = new StringBuilder(4096);
            if (!AcrobatDocumentIdentity.TryNormalizePath(devicePath, out var filename) ||
                !NavigationValidation.SamePath(_path, filename) || GetWindowText(_window, caption, caption.Capacity) == 0 ||
                !string.Equals((string)av.GetTitle(), caption.ToString(), StringComparison.Ordinal) || !CurrentWindow()) return null;
            return new(_window, _processId, _path, true, true);
        }
        var addresses = Elements(_edits).Where(element =>
            IsAddressName(Text(element, 30005), Text(element, 30011)) && IsEnabled(element) &&
            TryValue(element, out var value) && SameFileAddress(_path, value)).ToArray();
        if (addresses.Length != 1 || !CurrentWindow()) return null;
        return new(_window, _processId, _path, true, true);
    }

    public bool OpenFind(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (ReadTarget() is null) return false;
        var fields = SearchFields();
        if (fields.Length == 0)
        {
            if (_acrobat is not null)
            {
                if (ReadTarget() is null) return false;
                token.ThrowIfCancellationRequested();
                dynamic app = _acrobat;
                if (Convert.ToInt32(app.MenuItemExecute("Find")) == 0) return false;
            }
            else
            {
                var buttons = Elements(_buttons).Where(element => IsSearchName(Text(element, 30005)) && IsEnabled(element)).ToArray();
                if (buttons.Length != 1) return false;
                var invoke = Pattern<IUiaInvokePattern>(buttons[0], 10000);
                if (invoke is null || ReadTarget() is null) return false;
                token.ThrowIfCancellationRequested();
                invoke.Invoke(); // Targeted UIA operation; never dispatches a global Ctrl+F.
            }
            for (var attempt = 0; attempt < 8; attempt++)
            {
                token.ThrowIfCancellationRequested();
                if (ReadTarget() is null) return false;
                fields = SearchFields();
                if (fields.Length != 0) break;
                if (token.WaitHandle.WaitOne(100)) token.ThrowIfCancellationRequested();
            }
        }
        if (fields.Length != 1 || ReadTarget() is null) return false;
        token.ThrowIfCancellationRequested();
        _field = fields[0];
        _field.SetFocus();
        return ReadSearchField() is { IsFocused: true, IsEditable: true, IsEnabled: true };
    }

    public FindSearchFieldEvidence? ReadSearchField()
    {
        if (_field is null || ReadTarget() is null) return null;
        var focused = Own(_automation.GetFocusedElement());
        var runtimeId = RuntimeId(_field);
        var pattern = Pattern<IUiaValuePattern>(_field, 10002);
        if (pattern is null) return null;
        return new(_window, Integer(_field, 30002), runtimeId, IsSearchName(Text(_field, 30005)) && OutsideDocument(_field),
            Boolean(_field, 30008) && runtimeId == RuntimeId(focused), !pattern.CurrentIsReadOnly,
            IsEnabled(_field), pattern.CurrentValue);
    }

    public bool SetSearchValue(FindSearchFieldEvidence expectedField, string text, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (ReadTarget() is null || ReadSearchField() is not { IsSearch: true, IsFocused: true, IsEditable: true, IsEnabled: true } current ||
            current.RuntimeId != expectedField.RuntimeId || current.ProcessId != _processId) return false;
        var pattern = Pattern<IUiaValuePattern>(_field!, 10002);
        if (pattern is null || pattern.CurrentIsReadOnly || !CurrentWindow()) return false;
        token.ThrowIfCancellationRequested();
        pattern.SetValue(text); // Addressed to the verified search element, regardless of a later focus race.
        return true;
    }

    private IUiaElement[] SearchFields() => Elements(_edits).Where(element =>
        IsSearchName(Text(element, 30005)) && Integer(element, 30002) == _processId && OutsideDocument(element) &&
        IsEnabled(element) && !Boolean(element, 30022) && // IsOffscreen
        Pattern<IUiaValuePattern>(element, 10002) is { CurrentIsReadOnly: false }).ToArray();

    private bool OutsideDocument(IUiaElement element)
    {
        // A PDF form field named “Find” is still document content. Reject document/page ancestry,
        // and require the element to lead back to this exact top-level application window.
        var rootId = RuntimeId(_root);
        IUiaElement? current = element;
        for (var depth = 0; depth < 32 && current is not null; depth++)
        {
            if (Integer(current, 30003) == 50030 || Text(current, 30012) is
                "Chrome_RenderWidgetHostHWND" or "AVPageView" || Text(current, 30005) is
                "AVPageView" or "AVScrolledPageView") return false;
            if (RuntimeId(current) == rootId) return true;
            current = _walker.GetParentElement(current);
            if (current is not null) Own(current);
        }
        return false;
    }

    private IEnumerable<IUiaElement> Elements(object condition)
    {
        var array = Own(_root.FindAll(4, condition)); // Descendants of the single verified window only.
        var count = array.Length;
        if (count > 256) yield break; // Ambiguous/oversized tree: fail closed.
        for (var index = 0; index < count; index++) yield return Own(array.GetElement(index));
    }
    private bool CurrentWindow()
    {
        if (_disposed || _window == 0 || GetForegroundWindow() != _window) return false;
        GetWindowThreadProcessId(_window, out var processId);
        return processId == _processId;
    }
    private T Own<T>(T value) where T : class
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!_owned.Any(item => ReferenceEquals(item, value))) _owned.Add(value);
        return value;
    }
    private T? Pattern<T>(IUiaElement element, int id) where T : class
    {
        try { var value = element.GetCurrentPattern(id); return value is T typed ? Own(typed) : null; }
        catch (COMException) { return null; }
    }
    private bool TryValue(IUiaElement element, out string value)
    {
        var pattern = Pattern<IUiaValuePattern>(element, 10002);
        value = pattern?.CurrentValue ?? string.Empty;
        return pattern is not null;
    }
    private static string Text(IUiaElement element, int property) => element.GetCurrentPropertyValue(property) as string ?? string.Empty;
    private static int Integer(IUiaElement element, int property) => element.GetCurrentPropertyValue(property) is int number ? number : 0;
    private static bool Boolean(IUiaElement element, int property) => element.GetCurrentPropertyValue(property) is true;
    private static bool IsEnabled(IUiaElement element) => Boolean(element, 30010);
    private static string RuntimeId(IUiaElement element) => string.Join('.', element.GetRuntimeId());

    internal static bool IsAddressName(string name, string id) => id is "addressEditBox" or "omnibox" || name is
        "Address and search bar" or "Search or enter web address" or "地址和搜索栏" or "搜索或输入 Web 地址" or "地址栏";
    internal static bool IsSearchName(string name)
    {
        name = name.Replace(" (Ctrl+F)", "", StringComparison.OrdinalIgnoreCase).Replace(" (Ctrl + F)", "", StringComparison.OrdinalIgnoreCase).Trim();
        return name is "Find" or "Find in document" or "Find in page" or "Search in document" or "Find in PDF" or
            "Find text" or "Find text in document" or "查找" or "查找文本" or "在文档中查找" or "在页面上查找" or "在 PDF 中查找";
    }
    internal static bool SameFileAddress(string path, string address)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || !uri.IsFile || !string.IsNullOrEmpty(uri.Query)) return false;
        return NavigationValidation.SamePath(path, uri.LocalPath);
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        for (var index = _owned.Count - 1; index >= 0; index--)
            if (OperatingSystem.IsWindows() && Marshal.IsComObject(_owned[index])) Marshal.ReleaseComObject(_owned[index]);
        _owned.Clear();
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowTextW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int GetWindowText(nint window, StringBuilder text, int size);
    [DllImport("user32.dll", ExactSpelling = true)] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll", ExactSpelling = true)] private static extern uint GetWindowThreadProcessId(nint window, out int processId);
}

// Vtable order/GUIDs follow Microsoft's UIAutomationClient.h. Reserved methods are never called.
// Keeping interop here avoids runtime dependencies on WPF or an installed Office version.
[ComImport, Guid("30cbe57d-d9d0-452a-ab13-7ac5ac4825ee"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IUiaAutomation
{
    void Reserved01(); void Reserved02(); void Reserved03();
    IUiaElement ElementFromHandle(nint window);
    void Reserved05();
    IUiaElement GetFocusedElement();
    void Reserved07(); void Reserved08(); void Reserved09(); void Reserved10();
    void Reserved11(); IUiaTreeWalker ControlViewWalker { get; } void Reserved13(); void Reserved14();
    void Reserved15(); void Reserved16(); void Reserved17(); void Reserved18();
    void Reserved19(); void Reserved20();
    [return: MarshalAs(UnmanagedType.IUnknown)] object CreatePropertyCondition(int propertyId, [MarshalAs(UnmanagedType.Struct)] object value);
}
[ComImport, Guid("d22108aa-8ac5-49a5-837b-37bbb3d7591e"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IUiaElement
{
    void SetFocus();
    [return: MarshalAs(UnmanagedType.SafeArray, SafeArraySubType = VarEnum.VT_I4)] int[] GetRuntimeId();
    void Reserved03();
    IUiaElementArray FindAll(int scope, [MarshalAs(UnmanagedType.IUnknown)] object condition);
    void Reserved05(); void Reserved06(); void Reserved07();
    [return: MarshalAs(UnmanagedType.Struct)] object GetCurrentPropertyValue(int propertyId);
    void Reserved09(); void Reserved10(); void Reserved11(); void Reserved12(); void Reserved13();
    [return: MarshalAs(UnmanagedType.IUnknown)] object GetCurrentPattern(int patternId);
}
[ComImport, Guid("4042c624-389c-4afc-a630-9df854a541fc"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IUiaTreeWalker
{
    IUiaElement? GetParentElement(IUiaElement element);
}
[ComImport, Guid("14314595-b4bc-4055-95f2-58f2e42c9855"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IUiaElementArray
{
    int Length { get; }
    IUiaElement GetElement(int index);
}
[ComImport, Guid("a94cd8b1-0844-4cd6-9d2d-640537ab39e9"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IUiaValuePattern
{
    void SetValue([MarshalAs(UnmanagedType.BStr)] string value);
    string CurrentValue { [return: MarshalAs(UnmanagedType.BStr)] get; }
    bool CurrentIsReadOnly { [return: MarshalAs(UnmanagedType.Bool)] get; }
}
[ComImport, Guid("fb377fbe-8ea6-46d5-9c73-6499642d3059"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IUiaInvokePattern { void Invoke(); }
