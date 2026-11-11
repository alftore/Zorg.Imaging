using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Zorg.Imaging.OsCodecs.Windows;

/// <summary>The COM entry points in ole32.</summary>
[SupportedOSPlatform("windows")]
internal static unsafe partial class NativeMethods
{
    [LibraryImport("ole32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static partial int CoInitializeEx(void* reserved, int concurrency);

    [LibraryImport("ole32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static partial void CoUninitialize();

    [LibraryImport("ole32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static partial int CoCreateInstance(Guid* classId, void* outer, int context, Guid* interfaceId, void** instance);
}
