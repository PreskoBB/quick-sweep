using System.IO.Enumeration;

namespace QuickSweep.Core.Cleaner;

public sealed class DirNode
{
    public string Name = "";
    public string FullPath = "";
    public DirNode? Parent;
    public long Size;          // everything below, links excluded
    public long OwnBytes;      // files directly in this folder
    public long Files;         // files below
    public int Dirs;           // folders below
    public bool IsLink;
    public DateTime Newest;
    public List<DirNode> Children = new();
    public List<(string name, long size)> BigFiles = new(); // biggest loose files (for the treemap)
    public bool Truncated;     // deeper levels were summed but not kept as nodes
}

public sealed class ScanProgress
{
    long _files, _bytes;
    public long Files => Interlocked.Read(ref _files);
    public long Bytes => Interlocked.Read(ref _bytes);
    public string Current = "";
    public void Add(long bytes) { Interlocked.Increment(ref _files); Interlocked.Add(ref _bytes, bytes); }
}

/// <summary>Walks a folder tree once and keeps a size tree for the explorer and treemap. Never follows links.</summary>
public static class DiskScanner
{
    const int MaxDepth = 14;
    const long BigFile = 48L * 1024 * 1024;
    static readonly EnumerationOptions Opt = new() { IgnoreInaccessible = true, AttributesToSkip = 0, ReturnSpecialDirectories = false };

    readonly record struct Row(bool IsDir, bool IsLink, long Length, DateTime Write, string Name, string? Full);

    static FileSystemEnumerable<Row> List(string dir) => new(dir,
        (ref FileSystemEntry e) => new Row(e.IsDirectory, e.Attributes.HasFlag(FileAttributes.ReparsePoint), e.IsDirectory ? 0 : e.Length,
            e.LastWriteTimeUtc.UtcDateTime, e.FileName.ToString(), e.IsDirectory ? e.ToFullPath() : null), Opt);

    public static DirNode Scan(string root, ScanProgress progress, CancellationToken ct)
    {
        root = root.Length > 3 ? root.TrimEnd('\\') : root;
        var node = new DirNode { Name = root, FullPath = root };
        // top level folders in parallel (2 at a time: kind to spinning disks and to the kernel file-name cache)
        var tops = new List<DirNode>();
        var loose = new List<Row>();
        try
        {
            foreach (var r in List(root))
            {
                if (r.IsDir) { tops.Add(new DirNode { Name = r.Name, FullPath = r.Full!, Parent = node, IsLink = r.IsLink, Newest = r.Write }); }
                else if (!r.IsLink) { loose.Add(r); progress.Add(r.Length); }
            }
        }
        catch { }
        foreach (var l in loose) { node.OwnBytes += l.Length; node.Files++; if (l.Length >= BigFile) node.BigFiles.Add((l.Name, l.Length)); }
        var opts = new ParallelOptions { MaxDegreeOfParallelism = 2, CancellationToken = ct };
        Parallel.ForEach(tops.Where(t => !t.IsLink), opts, t => Walk(t, 1, progress, ct));
        foreach (var t in tops)
        {
            node.Children.Add(t);
            node.Size += t.Size; node.Files += t.Files; node.Dirs += t.Dirs + 1;
            if (t.Newest > node.Newest) node.Newest = t.Newest;
        }
        node.Size += node.OwnBytes;
        node.Children.Sort((a, b) => b.Size.CompareTo(a.Size));
        node.BigFiles.Sort((a, b) => b.size.CompareTo(a.size));
        return node;
    }

    static void Walk(DirNode node, int depth, ScanProgress progress, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        progress.Current = node.FullPath;
        var subs = new List<DirNode>();
        try
        {
            foreach (var r in List(node.FullPath))
            {
                if (r.IsDir)
                {
                    if (r.IsLink) { subs.Add(new DirNode { Name = r.Name, FullPath = r.Full!, Parent = node, IsLink = true, Newest = r.Write }); continue; }
                    subs.Add(new DirNode { Name = r.Name, FullPath = r.Full!, Parent = node, Newest = r.Write });
                }
                else if (!r.IsLink)
                {
                    node.OwnBytes += r.Length; node.Files++;
                    if (r.Write > node.Newest) node.Newest = r.Write;
                    if (r.Length >= BigFile) node.BigFiles.Add((r.Name, r.Length));
                    progress.Add(r.Length);
                }
            }
        }
        catch { }
        node.Size = node.OwnBytes;
        foreach (var s in subs)
        {
            if (!s.IsLink)
            {
                if (depth >= MaxDepth) { SumOnly(s, progress, ct); s.Truncated = true; }
                else Walk(s, depth + 1, progress, ct);
            }
            node.Size += s.Size; node.Files += s.Files; node.Dirs += s.Dirs + 1;
            if (s.Newest > node.Newest) node.Newest = s.Newest;
            if (depth < MaxDepth) node.Children.Add(s);
        }
        node.Children.Sort((a, b) => b.Size.CompareTo(a.Size));
        node.BigFiles.Sort((a, b) => b.size.CompareTo(a.size));
        if (node.BigFiles.Count > 24) node.BigFiles.RemoveRange(24, node.BigFiles.Count - 24);
    }

    /// <summary>Adds up a deep folder without keeping nodes.</summary>
    static void SumOnly(DirNode node, ScanProgress progress, CancellationToken ct)
    {
        var stack = new Stack<string>();
        stack.Push(node.FullPath);
        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var d = stack.Pop();
            try
            {
                foreach (var r in List(d))
                {
                    if (r.IsDir) { if (!r.IsLink) { stack.Push(r.Full!); node.Dirs++; } }
                    else if (!r.IsLink) { node.Size += r.Length; node.Files++; progress.Add(r.Length); if (r.Write > node.Newest) node.Newest = r.Write; }
                }
            }
            catch { }
        }
    }
}
