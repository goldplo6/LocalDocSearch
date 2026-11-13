using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace LocalDocSearch.Navigation;

internal static class AcrobatDocumentIdentity
{
    // Acrobat's JavaScript dispatch intentionally has no usable ITypeInfo. Do not use dynamic
    // binding for its properties: read IDispatch directly without changing document or settings.
    internal static string ReadPath(object javaScriptDocument)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var dispatch = (IAcrobatDispatch)javaScriptDocument;
        var iid = Guid.Empty;
        var ids = new int[1];
        Marshal.ThrowExceptionForHR(dispatch.GetIDsOfNames(ref iid, ["path"], 1, 0x409, ids));
        var parameters = new DISPPARAMS();
        var exception = new EXCEPINFO();
        Marshal.ThrowExceptionForHR(dispatch.Invoke(ids[0], ref iid, 0x409, 2, ref parameters, out var result, ref exception, out _));
        return result as string ?? string.Empty;
    }

    internal static bool TryNormalizePath(string deviceIndependentPath, out string path)
    {
        path = string.Empty;
        if (string.IsNullOrWhiteSpace(deviceIndependentPath)) return false;
        string candidate;
        if (deviceIndependentPath.Length >= 3 && deviceIndependentPath[0] == '/' &&
            char.IsAsciiLetter(deviceIndependentPath[1]) && deviceIndependentPath[2] == '/')
            candidate = deviceIndependentPath[1] + ":\\" + deviceIndependentPath[3..].Replace('/', '\\');
        else if (deviceIndependentPath.StartsWith("//", StringComparison.Ordinal))
            candidate = deviceIndependentPath.Replace('/', '\\');
        else return false;
        return NavigationValidation.TryPath(candidate, out path, out _);
    }
}

[ComImport, Guid("00020400-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAcrobatDispatch
{
    [PreserveSig] int GetTypeInfoCount(out uint count);
    [PreserveSig] int GetTypeInfo(uint index, uint locale, out nint information);
    [PreserveSig] int GetIDsOfNames(ref Guid iid,
        [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPWStr, SizeParamIndex = 2)] string[] names,
        uint count, uint locale, [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 2)] int[] identifiers);
    [PreserveSig] int Invoke(int identifier, ref Guid iid, uint locale, ushort flags, ref DISPPARAMS parameters,
        [MarshalAs(UnmanagedType.Struct)] out object result, ref EXCEPINFO exception, out uint argumentError);
}
