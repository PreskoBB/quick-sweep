using System.ComponentModel;
using System.IO.Enumeration;
using System.Runtime.CompilerServices;

namespace QuickSweep.Core.Cleaner;

public enum DeleteMode { Permanent, Recycle }

public sealed class CleanItem : INotifyPropertyChanged
{
    public string Category = "";
    public string Path = "";
    public string Display = "";
    public bool IsDirectory;
    public long Bytes, Files;
    public DateTime Newest;
    public bool IsLink;               // the item itself is a junction/symlink: only the link would be removed
    public int LinksInside;           // junctions/symlinks inside (never followed)
    public string? Denied;            // set when the guard refuses it
    public DeleteMode Mode;
    public string? Note;

    bool _selected;
    public bool Selected
    {
        get => _selected;
        set { if (_selected != value) { _selected = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Selected))); } }
    }

    public bool Recent => Newest > DateTime.UtcNow.AddDays(-7);
    public bool Selectable => Denied == null;
    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class CleanCategory
{
    public string Id = "", Title = "", Group = "";
    public string What = "", WhySafe = "", Regen = "";
    public DeleteMode Mode = DeleteMode.Permanent;
    /// <summary>Windows owns this data: we only report its size and point to Windows' own cleanup.</summary>
    public bool ManagedByWindows;
    public bool NeedsAdmin;
    public Func<CleanContext, IEnumerable<RootSpec>> Roots = _ => Array.Empty<RootSpec>();
    /// <summary>Optional filter on measured items (for example "untouched for 30 days").</summary>
    public Func<CleanContext, CleanItem, bool>? Keep;
}

/// <param name="ChildrenAreItems">true: every entry inside the folder is its own item (the folder stays)</param>
public readonly record struct RootSpec(string Path, string Display, bool ChildrenAreItems);

public sealed class CleanContext
{
    public AppSettings Settings { get; }
    public PathGuard Guard { get; }
    // all from Windows' own records, never from environment variables (an unprivileged program can change those)
    public string Local = TrustedPaths.Local;
    public string Roaming = TrustedPaths.Roaming;
    public string Profile = TrustedPaths.Profile;
    public string ProgramData = TrustedPaths.ProgramData;
    public string Windows = TrustedPaths.Windows;
    public string Temp = TrustedPaths.Temp;
    public string SystemDrive;
    public CleanContext(AppSettings s, PathGuard? guard = null)
    {
        Settings = s; Guard = guard ?? new PathGuard(s);
        SystemDrive = TrustedPaths.SystemDrive;
    }
}

public sealed class CategoryResult
{
    public CleanCategory Category = null!;
    public List<CleanItem> Items = new();
    public long TotalBytes => Items.Sum(i => i.Bytes);
    public string? Error;
}

public sealed record Measurement(long Bytes, long Files, DateTime Newest, int Links, bool IsLink);

public static class Measurer
{
    static readonly EnumerationOptions Opt = new()
    {
        IgnoreInaccessible = true, AttributesToSkip = 0, ReturnSpecialDirectories = false, RecurseSubdirectories = false
    };

    readonly record struct Entry(bool IsDir, bool IsLink, long Length, DateTime Write, string? Full);

    static FileSystemEnumerable<Entry> List(string dir) => new(dir,
        (ref FileSystemEntry e) => new Entry(e.IsDirectory, e.Attributes.HasFlag(FileAttributes.ReparsePoint), e.IsDirectory ? 0 : e.Length,
            e.LastWriteTimeUtc.UtcDateTime, e.IsDirectory ? e.ToFullPath() : null), Opt);

    /// <summary>Size, file count and newest write time of a file or folder. Never follows links.</summary>
    public static Measurement Measure(string path, CancellationToken ct = default)
    {
        try
        {
            var attr = File.GetAttributes(path);
            bool isLink = attr.HasFlag(FileAttributes.ReparsePoint);
            if (!attr.HasFlag(FileAttributes.Directory))
            {
                var fi = new FileInfo(path);
                return new Measurement(isLink ? 0 : fi.Length, 1, fi.LastWriteTimeUtc, 0, isLink);
            }
            if (isLink) return new Measurement(0, 0, Directory.GetLastWriteTimeUtc(path), 0, true);
            long bytes = 0, files = 0; int links = 0;
            DateTime newest = Directory.GetLastWriteTimeUtc(path);
            var stack = new Stack<string>();
            stack.Push(path);
            while (stack.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                var dir = stack.Pop();
                foreach (var e in List(dir))
                {
                    if (e.IsDir)
                    {
                        if (e.IsLink) { links++; continue; }
                        stack.Push(e.Full!);
                    }
                    else if (e.IsLink) links++;
                    else { bytes += e.Length; files++; if (e.Write > newest) newest = e.Write; }
                }
            }
            return new Measurement(bytes, files, newest, links, false);
        }
        catch (OperationCanceledException) { throw; }
        catch { return new Measurement(0, 0, DateTime.MinValue, 0, false); }
    }

    /// <summary>Immediate children (files and folders) of a folder, links included but flagged.</summary>
    public static List<(string full, bool isDir, bool isLink)> Children(string dir)
    {
        var list = new List<(string, bool, bool)>();
        try
        {
            foreach (var e in new FileSystemEnumerable<(string, bool, bool)>(dir,
                (ref FileSystemEntry fe) => (fe.ToFullPath(), fe.IsDirectory, fe.Attributes.HasFlag(FileAttributes.ReparsePoint)), Opt))
                list.Add(e);
        }
        catch { }
        return list;
    }
}
