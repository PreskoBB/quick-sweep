// Usage: <name>.exe [seconds] [threads]   Burns CPU on N threads for the given time, then exits. No window.
int seconds = args.Length > 0 && int.TryParse(args[0], out var s) ? s : 60;
int threads = args.Length > 1 && int.TryParse(args[1], out var t) ? t : Math.Max(1, Environment.ProcessorCount / 2);
var end = DateTime.UtcNow.AddSeconds(seconds);
var workers = Enumerable.Range(0, threads).Select(_ => new Thread(() =>
{
    double x = 1.0001;
    while (DateTime.UtcNow < end) { for (int i = 0; i < 200_000; i++) x = x * 1.0000001 + 1e-9; if (x > 1e300) x = 1; }
}) { IsBackground = true }).ToList();
workers.ForEach(w => w.Start());
workers.ForEach(w => w.Join());
