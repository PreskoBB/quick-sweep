// Usage: FakeGame.exe [seconds]   (default 90). Opens an off-screen 900x500 window and holds ~200 MB.
using System.Windows.Forms;

int seconds = args.Length > 0 && int.TryParse(args[0], out var s) ? s : 90;
var block = new byte[200 * 1024 * 1024];
for (int i = 0; i < block.Length; i += 4096) block[i] = 1;   // touch every page so it is really resident

ApplicationConfiguration.Initialize();
var form = new NoActivateForm { Text = "FakeGame (Quick Sweep test)", StartPosition = FormStartPosition.Manual, Location = new System.Drawing.Point(-20000, 0), Size = new System.Drawing.Size(900, 500), ShowInTaskbar = false };
var timer = new System.Windows.Forms.Timer { Interval = seconds * 1000 };
timer.Tick += (_, _) => { GC.KeepAlive(block); form.Close(); };
timer.Start();
Application.Run(form);

sealed class NoActivateForm : Form { protected override bool ShowWithoutActivation => true; }
