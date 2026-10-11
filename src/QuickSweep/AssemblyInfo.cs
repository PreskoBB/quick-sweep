using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

// Every P/Invoke resolves from System32 only (this app runs elevated).
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
[assembly: InternalsVisibleTo("QuickSweep.Tests")]
