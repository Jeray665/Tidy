using System.Runtime.InteropServices;
using System.Windows;

[assembly: ThemeInfo(ResourceDictionaryLocation.None, ResourceDictionaryLocation.SourceAssembly)]

// Native DLLs (nvml, ntdll, wintrust, powrprof...) only ever load from System32: no DLL hijacking
// from the app folder or the current directory.
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
