using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using QuickSweep.Native;
using Xunit;

namespace QuickSweep.Tests;

/// <summary>
/// The elevated process must not load a DLL from the current directory or PATH (a normal user can write both).
/// LibreHardwareMonitor asks for vendor DLLs by bare name; these tests pin the two layers that stop that.
/// </summary>
public class DllHardeningTests
{
    static string Repo
    {
        get
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !File.Exists(Path.Combine(d.FullName, "QuickSweep.slnx"))) d = d.Parent;
            return d?.FullName ?? throw new InvalidOperationException("repo root not found");
        }
    }

    [Fact]
    public void ApplyRunsFromAModuleInitializerSoItIsBeforeMainAndWpf()
    {
        var m = typeof(DllHardening).GetMethod("Init", BindingFlags.NonPublic | BindingFlags.Static)!;
        Assert.NotNull(m.GetCustomAttribute<ModuleInitializerAttribute>());
        Assert.True(DllHardening.Applied);
        Assert.StartsWith("ok", DllHardening.Status);
    }

    [Fact]
    public void OnStartupCallsTheHardeningBeforeAnythingElse()
    {
        string src = File.ReadAllText(Path.Combine(Repo, "src", "QuickSweep", "App.xaml.cs"));
        int start = src.IndexOf("protected override void OnStartup", StringComparison.Ordinal);
        Assert.True(start > 0);
        int body = src.IndexOf('{', start) + 1;
        string first = src[body..].Split('\n').Select(l => l.Trim()).First(l => l.Length > 0 && !l.StartsWith("//"));
        Assert.Equal("DllHardening.Apply();", first);
    }

    [Fact]
    public void NoCodeOutsideTheHardeningCallsLoadLibraryOrSetsADllDirectory()
    {
        foreach (var f in Directory.EnumerateFiles(Path.Combine(Repo, "src", "QuickSweep"), "*.cs", SearchOption.AllDirectories))
        {
            if (f.Contains(@"\obj\") || f.EndsWith("DllHardening.cs")) continue;
            string t = File.ReadAllText(f);
            Assert.DoesNotContain("LoadLibrary", t);
            Assert.DoesNotContain("SetDllDirectory", t);
            Assert.DoesNotContain("NativeLibrary.Load", t);
        }
    }

    [Fact]
    public void EveryDllImportOfTheAppIsPinnedToSystem32ByTheAssemblyAttribute()
    {
        var a = typeof(DllHardening).Assembly;
        var attr = a.GetCustomAttribute<DefaultDllImportSearchPathsAttribute>();
        Assert.NotNull(attr);
        Assert.Equal(DllImportSearchPath.System32, attr!.Paths);
        // and no single import weakens it
        foreach (var t in a.GetTypes())
            foreach (var m in t.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                var own = m.GetCustomAttribute<DefaultDllImportSearchPathsAttribute>();
                if (own != null) Assert.Equal(DllImportSearchPath.System32, own.Paths);
            }
    }

    static readonly string[] Roots = { @"C:\Windows", @"C:\Program Files", @"C:\Program Files\QuickSweep" };

    [Fact]
    public void ABareNameResolvesFromSystem32ThenTheAppFolderAndNothingElse()
    {
        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"C:\Windows\System32\nvml.dll", @"C:\Program Files\QuickSweep\MonoPosixHelper.dll", @"C:\Evil\atiadlxx.dll" };
        bool Ex(string p) => present.Contains(p);
        var sys = DllHardening.Decide("nvml.dll", @"C:\Program Files\QuickSweep", @"C:\Windows\System32", Roots, Ex, _ => false);
        Assert.Equal(@"C:\Windows\System32\nvml.dll", sys.Path);
        Assert.Equal(@"C:\Program Files\QuickSweep\MonoPosixHelper.dll", DllHardening.Decide("MonoPosixHelper", @"C:\Program Files\QuickSweep", @"C:\Windows\System32", Roots, Ex, _ => false).Path);
        // a file that exists only on PATH / in the current directory is never found
        Assert.Null(DllHardening.Decide("atiadlxx.dll", @"C:\Program Files\QuickSweep", @"C:\Windows\System32", Roots, Ex, _ => false).Path);
        Assert.Null(DllHardening.Decide("atiadlxx", @"C:\Program Files\QuickSweep", @"C:\Windows\System32", Roots, Ex, _ => false).Path);
    }

    [Theory]
    [InlineData(@"C:\Evil\atiadlxx.dll")]
    [InlineData(@"..\atiadlxx.dll")]
    [InlineData(@"runtimes\win-x64\native\x.dll")]
    [InlineData(@"\\server\share\x.dll")]
    [InlineData(@"C:\Users\someone\AppData\x.dll")]
    public void PathsOutsideProtectedFoldersAreRefused(string name)
    {
        var d = DllHardening.Decide(name, @"C:\Program Files\QuickSweep", @"C:\Windows\System32", Roots, _ => true, _ => true);
        Assert.Null(d.Path);
    }

    [Fact]
    public void AFullPathBelowProgramFilesIsAccepted()
        => Assert.Equal(@"C:\Program Files\NVIDIA Corporation\NVSMI\nvml.dll",
            DllHardening.Decide(@"C:\Program Files\NVIDIA Corporation\NVSMI\nvml.dll", @"C:\Program Files\QuickSweep", @"C:\Windows\System32", Roots, _ => true, _ => false).Path);

    [Fact]
    public void NvmlMayComeFromTheDriverFolderOnlyWhenTheFileIsVouchedFor()
    {
        string nv = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NVIDIA Corporation", "NVSMI", "nvml.dll");
        bool Ex(string p) => p.Equals(nv, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(nv, DllHardening.Decide("nvml.dll", @"C:\app", @"C:\Windows\System32", Roots, Ex, _ => true).Path);
        Assert.Null(DllHardening.Decide("nvml.dll", @"C:\app", @"C:\Windows\System32", Roots, Ex, _ => false).Path);
    }

    [Fact]
    public void AFileInATempFolderIsNotAProtectedFile()
    {
        string f = Path.Combine(Path.GetTempPath(), "qs-prot-" + Guid.NewGuid().ToString("N")[..8] + ".dll");
        File.WriteAllText(f, "x");
        try { Assert.False(ProtectedFile.IsProtected(f, out _)); }
        finally { File.Delete(f); }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern nint LoadLibraryW(string name);
    [DllImport("kernel32.dll")] static extern bool FreeLibrary(nint h);

    /// <summary>A real (harmless) system DLL copied under a vendor name into a folder that is put on PATH: if the search reaches PATH it loads.</summary>
    static string Plant(out string name)
    {
        string dir = Path.Combine(Path.GetTempPath(), "qs-plant-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        name = "qsplanted" + Guid.NewGuid().ToString("N")[..6] + ".dll";
        File.Copy(Path.Combine(Environment.SystemDirectory, "version.dll"), Path.Combine(dir, name));
        return dir;
    }

    [Fact]
    public void ALibraryOnPathIsNotLoadedByABareLoadLibrary()
    {
        string dir = Plant(out var name);
        string oldPath = Environment.GetEnvironmentVariable("PATH") ?? "";
        try
        {
            Environment.SetEnvironmentVariable("PATH", dir + ";" + oldPath);
            nint h = LoadLibraryW(name);
            if (h != 0) FreeLibrary(h);
            Assert.Equal(0, h);
        }
        finally { Environment.SetEnvironmentVariable("PATH", oldPath); try { Directory.Delete(dir, true); } catch { } }
    }

    /// <summary>The control: the same planted file DOES load in a process that has no hardening (a PowerShell child), so the test above proves something.</summary>
    [Fact]
    public void TheSamePlantedLibraryLoadsInAnUnhardenedProcess()
    {
        string dir = Plant(out var name);
        try
        {
            string script = "$ErrorActionPreference='Stop'; Add-Type -MemberDefinition '[DllImport(\"kernel32.dll\", CharSet=CharSet.Unicode)] public static extern System.IntPtr LoadLibraryW(string n);' -Name K -Namespace QsT; "
                          + "$h=[QsT.K]::LoadLibraryW('" + name + "'); if ($h -ne [System.IntPtr]::Zero) { 'LOADED' } else { 'NOT' }";
            var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"),
                "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"" + script.Replace("\"", "\\\"") + "\"")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, WorkingDirectory = dir };
            psi.Environment["PATH"] = dir + ";" + Environment.GetEnvironmentVariable("PATH");
            using var p = Process.Start(psi)!;
            string o = p.StandardOutput.ReadToEnd();
            Assert.True(p.WaitForExit(60000));
            Assert.Contains("LOADED", o);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}

/// <summary>The real app, started non-elevated with --dev --gpu-probe and a planted ADL/Intel/FTDI DLL on PATH and as the working directory.</summary>
[Trait("Needs", "NonAdmin")]
public class DllHijackEndToEndTests
{
    static readonly string[] VendorNames = { "atiadlxx.dll", "atiadlxy.dll", "ControlLib.dll", "Ftd2xx.dll", "nvidia-ml.dll", "nvapi64x.dll" };

    static (string output, string dir) Run()
    {
        string exe = Path.Combine(AppContext.BaseDirectory, "QuickSweep.exe");
        Assert.True(File.Exists(exe), exe);
        string root = Path.Combine(Path.GetTempPath(), "qs-hijack-" + Guid.NewGuid().ToString("N")[..8]);
        string plant = Path.Combine(root, "plant"), data = Path.Combine(root, "data");
        Directory.CreateDirectory(plant); Directory.CreateDirectory(data);
        foreach (var n in VendorNames) File.Copy(Path.Combine(Environment.SystemDirectory, "version.dll"), Path.Combine(plant, n));
        string outFile = Path.Combine(root, "probe.txt");
        var psi = new ProcessStartInfo(exe, $"--dev --data=\"{data}\" --gpu-probe=\"{outFile}\"")
        { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = plant };
        psi.Environment["PATH"] = plant + ";" + Environment.GetEnvironmentVariable("PATH");
        using var p = Process.Start(psi)!;
        Assert.True(p.WaitForExit(120000), "probe did not finish");
        Assert.True(File.Exists(outFile), "no probe output, exit " + p.ExitCode);
        return (File.ReadAllText(outFile), root);
    }

    [Fact]
    public void ThePlantedDllsAreNeverLoadedByTheApp()
    {
        var (o, root) = Run();
        try
        {
            Assert.Contains("DLL search hardening: ok", o);
            // on a PC without AMD's ADL in System32 the library asks for it by name: the resolver must have refused it, not found the planted one
            if (!File.Exists(Path.Combine(Environment.SystemDirectory, "atiadlxx.dll"))) Assert.Contains("refused 'atiadlxx.dll'", o);
            Assert.DoesNotContain(@"qs-hijack-", o.Substring(o.IndexOf("Loaded DLLs outside", StringComparison.Ordinal)));
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

}
