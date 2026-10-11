using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using QuickSweep.Core;

namespace QuickSweep.Ui;

public sealed class DeviceRow
{
    public AudioDevice Device { get; init; } = null!;
    public string Name => Device.Name;
    public Brush Dot => Device.IsDefault ? Palette.Good : Palette.Track;
    public FontWeight Weight => Device.IsDefault ? FontWeights.SemiBold : FontWeights.Normal;
    public Visibility DefaultVis => Device.IsDefault ? Visibility.Visible : Visibility.Collapsed;
    public Visibility SwitchVis => Device.IsDefault ? Visibility.Collapsed : Visibility.Visible;
}

public sealed class AudioRow : Observable
{
    public AudioSession Session { get; set; } = null!;
    double _percent;
    public string Name => Session.Name;
    public ImageSource? Icon => IconCache.Get(Session.ExePath);
    public string State => Session.Muted ? "muted" : Session.Active ? "playing now" : "idle";
    public double Percent { get => _percent; set { if (Set(ref _percent, value)) { Raise(nameof(PercentText)); Raise(nameof(PercentBrush)); Raise(nameof(FixVis)); } } }
    public string PercentText => $"{_percent:0}%";
    public bool Low => Session.Muted || _percent < 90;
    public Brush PercentBrush => Low ? Palette.Warn : Palette.Text;
    public Visibility FixVis => Low ? Visibility.Visible : Visibility.Collapsed;
    public void Reload() { Raise(nameof(State)); Raise(nameof(Name)); Raise(nameof(Low)); Raise(nameof(PercentBrush)); Raise(nameof(FixVis)); }
}

public partial class AudioPage : UserControl, IPage
{
    readonly AudioManager _audio = new();
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };
    List<AudioRow> _rows = new();
    bool _loadingMaster;

    public AudioPage(SystemMonitor mon)
    {
        InitializeComponent();
        _timer.Tick += (_, _) => Refresh();
    }

    public void OnShown() { Refresh(); _timer.Start(); }
    public void OnHidden() => _timer.Stop();

    void Refresh()
    {
        try
        {
            var devs = _audio.Devices();
            DeviceList.ItemsSource = devs.Select(d => new DeviceRow { Device = d }).ToList();
            NoDevices.Visibility = devs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            if (_audio.MasterVolume() is { } mv && !MasterSlider.IsMouseCaptureWithin)
            {
                _loadingMaster = true;
                MasterSlider.Value = mv.level * 100; MasterText.Text = $"{mv.level * 100:0}%";
                _loadingMaster = false;
            }

            var sessions = _audio.Sessions();
            // update in place so sliders do not jump under the mouse
            bool same = sessions.Count == _rows.Count && sessions.Select(s => s.Name).SequenceEqual(_rows.Select(r => r.Name));
            if (!same)
            {
                _rows = sessions.Select(s => new AudioRow { Session = s, Percent = s.Volume * 100 }).ToList();
                SessionList.ItemsSource = _rows;
            }
            else
            {
                for (int i = 0; i < sessions.Count; i++)
                {
                    _rows[i].Session = sessions[i];
                    if (!SliderBusy()) _rows[i].Percent = sessions[i].Volume * 100;
                    _rows[i].Reload();
                }
            }
            NoSessions.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            var low = _rows.Where(r => r.Low && !r.Session.IsSystemSounds).ToList();
            if (low.Count > 0)
            {
                FlagCard.Visibility = Visibility.Visible;
                FlagText.Text = string.Join("  ", low.Select(r => r.Session.Muted ? $"{r.Name} is muted in the Windows mixer." : $"{r.Name} is at {r.Percent:0}% in the Windows mixer.")) +
                                " Everything it plays is quieter than it needs to be; this is separate from the volume of your speakers or headphones.";
            }
            else FlagCard.Visibility = Visibility.Collapsed;
        }
        catch (Exception e) { Log.Write("audio page: " + e.Message); }
    }

    bool SliderBusy() => SessionList.IsMouseCaptureWithin || SessionList.IsKeyboardFocusWithin;

    void OnMakeDefault(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is DeviceRow d) { _audio.SetDefault(d.Device.Id); Refresh(); }
    }

    void OnMasterChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loadingMaster) return;
        MasterText.Text = $"{e.NewValue:0}%";
        if (MasterSlider.IsMouseCaptureWithin || MasterSlider.IsKeyboardFocusWithin) _audio.SetMasterVolume((float)(e.NewValue / 100));
    }

    void OnSessionChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        var s = (Slider)sender;
        if (s.Tag is AudioRow r && (s.IsMouseCaptureWithin || s.IsKeyboardFocusWithin))
        {
            _audio.SetSessionVolume(r.Session, (float)(e.NewValue / 100));
            r.Reload();
        }
    }

    void OnFix(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is AudioRow r)
        {
            _audio.SetSessionVolume(r.Session, 1f, unmute: true);
            r.Percent = 100; r.Reload();
            Refresh();
        }
    }
}
