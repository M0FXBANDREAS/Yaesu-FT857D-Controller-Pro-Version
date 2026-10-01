using System.IO;
using System.Runtime.InteropServices;
using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using System.Windows.Shapes;
using NAudio.Wave;
using FT857DControl.Models;
using FT857DControl.Services;

namespace FT857DControl;

public partial class MainWindow : Window
{
    private bool _fullScreen;
    private WindowStyle _savedWindowStyle;
    private ResizeMode _savedResizeMode;
    private WindowState _savedWindowState;

    private readonly RadioConnection _radio = new();
    private readonly DispatcherTimer _pollTimer;
    private readonly DispatcherTimer _lampTimer;
    private WaveInEvent? _audioInput;
    private readonly object _scopeLock = new();
    private float[] _scopeSamples = new float[256];
    private bool _lampPhase;
    private bool _rxActive;
    private bool _pcSquelchEnabled;
    private int _pcSquelchLevel = 3;
    private bool _pcSquelchMuted;
    private long _frequencyHz = 145_500_000;
    private RadioMode _mode = RadioMode.FM;
    private bool _split;
    private bool _vfoB;
    private bool _transmitting;
    private bool _polling;
    private enum MeterType { Signal, Power, Swr, Alc }
    private MeterType _meterType = MeterType.Signal;
    private int _lastSignalLevel;
    private int _lastPowerLevel;
    private bool _lastHighSwr;
    private bool _sessionConnected;
    private bool _clarifier;
    private int _ritHz;
    private bool _scanning;
    private bool _memoryMode;
    private long _memoryHz = 145_500_000;
    private RadioMode _memoryModeValue = RadioMode.FM;
    private long _homeHz = 145_500_000;
    private RadioMode _homeMode = RadioMode.FM;
    private int _functionStepIndex;
    private readonly long[] _steps = [10, 100, 1000, 5000, 10000, 100000];
    private bool _dialDragging;
    private double _lastDialAngle;
    private double _dialVisualAngle;
    private bool _dialBusy;
    private readonly decimal[] _ctcssTones = [67.0m,69.3m,71.9m,74.4m,77.0m,79.7m,82.5m,85.4m,88.5m,91.5m,94.8m,97.4m,100.0m,103.5m,107.2m,110.9m,114.8m,118.8m,123.0m,127.3m,131.8m,136.5m,141.3m,146.2m,151.4m,156.7m,159.8m,162.2m,165.5m,167.9m,171.3m,173.8m,177.3m,179.9m,183.5m,186.2m,189.9m,192.8m,196.6m,199.5m,203.5m,206.5m,210.7m,218.1m,225.7m,229.1m,233.6m,241.8m,250.3m,254.1m];
    private decimal _ctcssTone = 88.5m;
    private bool _ctcssEnabled;
    private int _repeaterShift;
    private long _repeaterOffsetHz = 600_000;
    private readonly MemoryChannel?[] _memories = new MemoryChannel?[200];
    private static readonly string MemoryFile = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HamTech M0FXB FT857D Controller", "memories.json");
    private sealed record MemoryChannel(string Name, long FrequencyHz, RadioMode Mode, bool CtcssEnabled, decimal CtcssTone, int Shift, long OffsetHz);

    private readonly long[] _bandHomes = [1_900_000, 3_650_000, 5_354_000, 7_100_000, 10_125_000, 14_200_000, 18_100_000, 21_200_000, 24_940_000, 28_500_000, 50_150_000, 145_500_000, 433_500_000];

    public MainWindow()
    {
        InitializeComponent();
        // Baud choices are declared in XAML so they are always visible in the drop-down.
        BaudBox.Text = "9600";
        PhotoBaudBox.Text = "9600";
        CtcssBox.ItemsSource = _ctcssTones.Select(t => t.ToString("0.0", CultureInfo.InvariantCulture));
        CtcssBox.SelectedItem = "88.5";
        MemoryChannelBox.ItemsSource = Enumerable.Range(1, 200).Select(i => $"M-{i:000}").ToArray();
        MemoryChannelBox.SelectedIndex = 0;
        RadioMemoryBox.ItemsSource = Enumerable.Range(1, 200).Select(i => $"M-{i:000}").ToArray();
        RadioMemoryBox.SelectedIndex = 0;
        LoadMemories();
        RefreshPorts();
        _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
        _pollTimer.Tick += PollTimer_Tick;
        _lampTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _lampTimer.Tick += (_, _) => UpdateActivityLamps();
        _lampTimer.Start();
        StartAudioScope();
        Closed += async (_, _) => { StopAudioScope(); try { if (_radio.IsOpen && _transmitting) await _radio.SendAsync(CatProtocol.SetPtt(false)); } catch { } _radio.Dispose(); };
        UpdateDisplay();
    }


    private void StartAudioScope()
    {
        StopAudioScope();
        try
        {
            _audioInput = new WaveInEvent
            {
                DeviceNumber = 0,
                WaveFormat = new WaveFormat(16000, 16, 1),
                BufferMilliseconds = 45,
                NumberOfBuffers = 3
            };
            _audioInput.DataAvailable += AudioInput_DataAvailable;
            _audioInput.RecordingStopped += (_, e) => Dispatcher.Invoke(() =>
            {
                if (e.Exception != null) AudioScopeStatus.Text = "AUDIO INPUT ERROR";
            });
            _audioInput.StartRecording();
            AudioScopeStatus.Text = "DEFAULT AUDIO INPUT • LIVE";
        }
        catch
        {
            AudioScopeStatus.Text = "NO AUDIO INPUT — CLICK RESTART";
        }
    }

    private void StopAudioScope()
    {
        try { _audioInput?.StopRecording(); } catch { }
        _audioInput?.Dispose();
        _audioInput = null;
    }

    private void RestartAudioScope_Click(object sender, RoutedEventArgs e) => StartAudioScope();

    private void AudioInput_DataAvailable(object? sender, WaveInEventArgs e)
    {
        int count = e.BytesRecorded / 2;
        if (count <= 0) return;
        var next = new float[256];
        int stride = Math.Max(1, count / next.Length);
        for (int i = 0; i < next.Length; i++)
        {
            int sampleIndex = Math.Min(count - 1, i * stride);
            short sample = BitConverter.ToInt16(e.Buffer, sampleIndex * 2);
            next[i] = sample / 32768f;
        }
        lock (_scopeLock) _scopeSamples = next;
        Dispatcher.BeginInvoke(DrawAudioScope);
    }

    private void AudioScopeCanvas_SizeChanged(object sender, SizeChangedEventArgs e) => DrawAudioScope();

    private void DrawAudioScope()
    {
        if (AudioScopeCanvas == null || AudioScopeTrace == null) return;
        double w = AudioScopeCanvas.ActualWidth, h = AudioScopeCanvas.ActualHeight;
        if (w < 2 || h < 2) return;
        ScopeMidLine.X1 = 0; ScopeMidLine.X2 = w; ScopeMidLine.Y1 = ScopeMidLine.Y2 = h / 2;
        float[] samples;
        lock (_scopeLock) samples = (float[])_scopeSamples.Clone();
        var points = new PointCollection(samples.Length);
        for (int i = 0; i < samples.Length; i++)
        {
            double x = i * (w / (samples.Length - 1));
            double y = h / 2 - samples[i] * (h * 0.44);
            points.Add(new Point(x, y));
        }
        AudioScopeTrace.Points = points;
        if (PhotoAudioScopeCanvas != null && PhotoAudioScopeTrace != null)
        {
            double pw = PhotoAudioScopeCanvas.ActualWidth, ph = PhotoAudioScopeCanvas.ActualHeight;
            if (pw > 2 && ph > 2)
            {
                PhotoScopeMidLine.X1 = 0; PhotoScopeMidLine.X2 = pw; PhotoScopeMidLine.Y1 = PhotoScopeMidLine.Y2 = ph / 2;
                var pp = new PointCollection(samples.Length);
                for (int i = 0; i < samples.Length; i++) pp.Add(new Point(i * (pw / (samples.Length - 1)), ph / 2 - samples[i] * (ph * 0.44)));
                PhotoAudioScopeTrace.Points = pp;
            }
        }
    }

    private bool Connected => _sessionConnected;

    // Windows multimedia keys control the current default playback endpoint.
    private const byte VkVolumeMute = 0xAD;
    private const byte VkVolumeDown = 0xAE;
    private const byte VkVolumeUp = 0xAF;
    private const uint KeyeventfKeyup = 0x0002;

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    private static void SendMediaKey(byte key)
    {
        keybd_event(key, 0, 0, UIntPtr.Zero);
        keybd_event(key, 0, KeyeventfKeyup, UIntPtr.Zero);
    }

    private void VolumeDown_Click(object sender, RoutedEventArgs e)
    {
        SendMediaKey(VkVolumeDown);
        StatusText.Text = "Windows volume down";
    }

    private void VolumeUp_Click(object sender, RoutedEventArgs e)
    {
        SendMediaKey(VkVolumeUp);
        StatusText.Text = "Windows volume up";
    }

    private void Mute_Click(object sender, RoutedEventArgs e)
    {
        SendMediaKey(VkVolumeMute);
        StatusText.Text = "Windows mute toggled";
    }


    // The FT-857D standard CAT set does not provide AF squelch level control.
    // This PC-side squelch uses the radio's real CAT S-meter reading and mutes/unmutes
    // Windows audio when the received level crosses the selected threshold.
    private void SqlToggle_Click(object sender, RoutedEventArgs e)
    {
        _pcSquelchEnabled = !_pcSquelchEnabled;
        SqlButton.Content = _pcSquelchEnabled ? "PC SQL ON" : "PC SQL OFF";
        SqlButton.Foreground = new SolidColorBrush(_pcSquelchEnabled ? Color.FromRgb(130, 230, 90) : Colors.White);
        if (!_pcSquelchEnabled && _pcSquelchMuted) SetPcSquelchMute(false);
        StatusText.Text = _pcSquelchEnabled ? $"PC squelch enabled at S{_pcSquelchLevel}" : "PC squelch disabled";
    }

    private void SqlDown_Click(object sender, RoutedEventArgs e)
    {
        _pcSquelchLevel = Math.Max(0, _pcSquelchLevel - 1);
        SqlLevelText.Text = $"S{_pcSquelchLevel}";
    }

    private void SqlUp_Click(object sender, RoutedEventArgs e)
    {
        _pcSquelchLevel = Math.Min(15, _pcSquelchLevel + 1);
        SqlLevelText.Text = _pcSquelchLevel <= 9 ? $"S{_pcSquelchLevel}" : $"+{(_pcSquelchLevel - 9) * 10}";
    }

    private void ApplyPcSquelch(int level)
    {
        if (!_pcSquelchEnabled || _transmitting) return;
        SetPcSquelchMute(level < _pcSquelchLevel);
    }

    private void SetPcSquelchMute(bool shouldMute)
    {
        if (_pcSquelchMuted == shouldMute) return;
        SendMediaKey(VkVolumeMute);
        _pcSquelchMuted = shouldMute;
        MuteButton.Content = shouldMute ? "SQL MUTED" : "MUTE";
    }

    private void RfGainInfo_Click(object sender, RoutedEventArgs e)
    {
        StatusText.Text = "RF Gain: use the radio SQL/RF knob (standard FT-857D CAT has no RF Gain level command)";
        MessageBox.Show("The FT-857D standard CAT command set does not provide a command to change the physical RF Gain level. Use the radio's SQL/RF knob for true RF Gain. I have deliberately not used undocumented EEPROM writes, because those can alter persistent radio data.", "RF Gain", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void MenuExit_Click(object sender, RoutedEventArgs e) => Close();

    private void FillScreen_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Maximized;

    private void FullScreen_Click(object sender, RoutedEventArgs e) => ToggleFullScreen();

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F11) { ToggleFullScreen(); e.Handled = true; }
        else if (e.Key == Key.Escape && _fullScreen) { ToggleFullScreen(); e.Handled = true; }
    }

    private void ToggleFullScreen()
    {
        if (!_fullScreen)
        {
            _savedWindowStyle = WindowStyle;
            _savedResizeMode = ResizeMode;
            _savedWindowState = WindowState;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            WindowState = WindowState.Maximized;
            _fullScreen = true;
            StatusText.Text = "Full screen — F11 or Esc to restore";
        }
        else
        {
            WindowStyle = _savedWindowStyle;
            ResizeMode = _savedResizeMode;
            WindowState = _savedWindowState == WindowState.Minimized ? WindowState.Normal : _savedWindowState;
            _fullScreen = false;
            StatusText.Text = "Windowed mode";
        }
    }

    private void RestoreWindow_Click(object sender, RoutedEventArgs e)
    {
        if (_fullScreen) ToggleFullScreen();
        WindowState = WindowState.Normal;
    }

    private void Tuner_Click(object sender, RoutedEventArgs e)
    {
        var connected = Connected ? "CAT IS CURRENTLY CONNECTED. Disconnect it first.\n\n" : "";
        MessageBox.Show(connected +
            "FT-857D FC-30 / ATAS TUNER SETUP\n\n" +
            "The rear CAT/LINEAR socket is shared with the tuner control, so the controller cannot keep CAT active while an FC-30 occupies that socket.\n\n" +
            "For FC-30: set Menu 020 CAT/LIN/TUN = TUNER, power-cycle after connecting the FC-30, then set Menu 085 TUNER/ATAS = TUNER. On the radio select MF-k [TUNE, DOWN, UP]. Press TUNE to enable; hold TUNE about one second to start automatic tuning.\n\n" +
            "This orange button is intentionally a safe tuner/setup control and does not transmit an undocumented CAT command.",
            "Orange TUNER — FT-857D", MessageBoxButton.OK, MessageBoxImage.Information);
        StatusText.Text = Connected ? "Tuner requires CAT disconnect / Menu 020 = TUNER" : "Tuner setup: Menu 020 = TUNER, Menu 085 = TUNER";
    }

    private void AlwaysOnTop_Click(object sender, RoutedEventArgs e)
    {
        Topmost = AlwaysOnTopMenu.IsChecked;
        StatusText.Text = Topmost ? "Always on top enabled" : "Always on top disabled";
    }

    private void TuningStepMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem item || item.Tag is not string tag || !long.TryParse(tag, out var step)) return;
        var index = Array.IndexOf(_steps, step);
        if (index >= 0) { _functionStepIndex = index; PhotoStepButton.Content = FormatStep(_steps[_functionStepIndex]); }
        StatusText.Text = $"VFO tuning step: {step:N0} Hz";
    }

    private async void MenuPttOn_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_transmitting) return;
            await SendAsync(CatProtocol.SetPtt(true), "TRANSMITTING");
            _transmitting = true;
            PttButton.Content = "TX";
            PhotoPttButton.Content = "TX";
            PttButton.Background = new SolidColorBrush(Color.FromRgb(194, 50, 44));
            PhotoPttButton.Background = new SolidColorBrush(Color.FromRgb(238, 20, 28));
        }
        catch (Exception ex) { ShowError("PTT failed", ex); }
    }

    private async void MenuPttOff_Click(object sender, RoutedEventArgs e) => await ReleasePttAsync();

    private void Instructions_Click(object sender, RoutedEventArgs e)
    {
        var help = new Window
        {
            Title = "Instructions — HamTech M0FXB Yaesu FT857D Controller",
            Width = 820,
            Height = 720,
            MinWidth = 520,
            MinHeight = 420,
            Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.CanResizeWithGrip,
            Background = new SolidColorBrush(Color.FromRgb(20, 22, 23)),
            Foreground = Brushes.White
        };

        var panel = new StackPanel { Margin = new Thickness(18) };
        void Heading(string text)
        {
            panel.Children.Add(new TextBlock
            {
                Text = text,
                Foreground = new SolidColorBrush(Color.FromRgb(255, 211, 90)),
                FontSize = 18,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 14, 0, 5)
            });
        }
        void Body(string text)
        {
            panel.Children.Add(new TextBlock
            {
                Text = text,
                Foreground = new SolidColorBrush(Color.FromRgb(230, 230, 230)),
                FontSize = 14,
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 21,
                Margin = new Thickness(0, 0, 0, 5)
            });
        }

        panel.Children.Add(new TextBlock
        {
            Text = "HamTech M0FXB Yaesu FT857D Controller",
            Foreground = Brushes.White,
            FontSize = 24,
            FontWeight = FontWeights.Bold
        });
        panel.Children.Add(new TextBlock
        {
            Text = "by M0FXB Andreas — Operating Instructions",
            Foreground = new SolidColorBrush(Color.FromRgb(255, 211, 90)),
            FontSize = 16,
            Margin = new Thickness(0, 2, 0, 10)
        });

        Heading("1. Connect to the radio");
        Body("Connect the FT-857D CAT cable. Choose the COM port and the same baud rate configured in the radio (4800, 9600 or 38400), then press Connect. Use Refresh if a newly attached COM port is not shown. Demo mode lets you operate the interface without a radio.");

        Heading("2. Frequency and VFO knob");
        Body("Turn the large VFO dial with the mouse wheel, or hold the left mouse button and drag around the dial. The frequency buttons provide fixed +/- steps. FUNC cycles the tuning step; Settings > Tuning step selects 10 Hz, 100 Hz, 1 kHz, 5 kHz, 10 kHz or 100 kHz. UP/DWN tune by 1 kHz. BAND cycles amateur-band starting frequencies.");

        Heading("3. Modes, VFO, split and RIT");
        Body("LSB, USB, CW, CWR, AM, FM, DIG and PKT select the operating mode. A/B switches VFO. SPL toggles split operation. CLAR enables/disables clarifier/RIT; use the RIT -, CLR and + buttons to change or clear the offset.");

        Heading("4. PTT, TX/RX lamps and signal meter");
        Body("The large red PTT button keys and releases the transmitter. The TX lamp flashes while transmitting. The RX lamp flashes when receive activity is detected. The bar meter and analog S-meter are driven from the radio receiver-status/S-meter CAT reading.");

        Heading("5. Repeater shift and CTCSS");
        Body("Select a CTCSS tone, then use Tone ON or Tone OFF. For a repeater choose SIMPLEX, - or +, enter the shift amount in MHz (for example 0.600), and press APPLY. The controller sends the repeater offset, shift direction and tone settings to the radio.");

        Heading("6. PC memory channels — SAVE");
        Body("There are 50 application memory channels, CH 01 to CH 50. First tune the radio and select the mode, CTCSS tone/state, repeater direction and shift amount you want to store. Select a red memory channel number, type an optional name such as GB3WR, then press SAVE. The memory stores name, receive frequency, mode, CTCSS state/tone, shift direction and offset. Memories are saved on the PC and survive program restarts.");

        Heading("7. PC memory channels — RECALL");
        Body("Select the required CH number and press RECALL. The saved frequency, mode, CTCSS tone/state and repeater shift are sent back to the connected FT-857D. These are controller/application memories; they do not overwrite the radio's internal EEPROM memory channels.");

        Heading("8. RADIO memories — READ RADIO");
        Body("The red M-001 to M-200 selector addresses the FT-857D's actual internal regular memory records. Connect the radio, select a channel, then press READ RADIO. The controller uses the undocumented EEPROM READ command only and displays the radio memory tag, frequency, mode, repeater direction and CTCSS tone where present. This feature is deliberately READ ONLY: the program does not issue the EEPROM write command, so READ RADIO cannot alter calibration data or radio memories.");

        Heading("8. HOME, V/M, MW and SCAN");
        Body("HOME recalls the controller home frequency/mode. V/M switches the controller's VFO/memory behavior. MW stores the current controller memory state used by that V/M function. SCAN starts/stops the controller scan function. The dedicated 50-channel SAVE/RECALL bank is the recommended way to keep named repeater and operating memories.");

        Heading("9. PC audio volume, mute and squelch");
        Body("VOL - and VOL + change the Windows master playback volume. MUTE toggles Windows playback mute. SQL ON/OFF enables the controller's PC-audio squelch; SQL - and SQL + change the S-meter threshold. When enabled, Windows audio is muted below the selected signal threshold and opened above it.");

        Heading("10. RF Gain");
        Body("The RF - / RF + controls display information only. Standard FT-857D CAT does not provide a safe command for the physical RF Gain setting, so true RF Gain remains controlled by the radio's SQL/RF knob.");

        Heading("11. Audio scope");
        Body("The green audio scope uses the Windows default recording input. Feed your radio/interface receive audio into a Windows recording device to see the waveform. If you change audio devices, press RESTART on the scope.");

        Heading("12. Top menus");
        Body("File closes the program. View refreshes COM ports, reads the radio and controls Always on top. Radio provides connection, read, VFO, split, clarifier and PTT commands. Settings selects tuning step and refreshes COM ports. Instructions opens this guide. Help contains VFO knob help and About.");

        Heading("13. Resizing");
        Body("Drag any window edge or the lower-right resize grip. The radio panel scales to fit the available window while keeping the controls together.");

        Heading("Safety note");
        Body("Before transmitting, confirm the correct frequency, mode, repeater shift, CTCSS tone and antenna. PC volume/squelch controls affect Windows audio, not the radio's physical AF volume knob.");

        var close = new Button
        {
            Content = "CLOSE INSTRUCTIONS",
            Height = 38,
            Margin = new Thickness(0, 18, 0, 12),
            FontWeight = FontWeights.Bold,
            Background = new SolidColorBrush(Color.FromRgb(51, 56, 58)),
            Foreground = Brushes.White
        };
        close.Click += (_, _) => help.Close();
        panel.Children.Add(close);

        help.Content = new ScrollViewer
        {
            Content = panel,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        help.ShowDialog();
    }

    private void KnobHelp_Click(object sender, RoutedEventArgs e) =>
        MessageBox.Show("VFO KNOB\n\n• Roll the mouse wheel over the large dial to tune.\n• Or hold the left mouse button and drag around the dial.\n• FUNC cycles the tuning step.\n• Settings → Tuning step selects an exact step.\n\nEach movement sends a real CAT Set Frequency command to the FT-857D.", "VFO knob", MessageBoxButton.OK, MessageBoxImage.Information);

    private void About_Click(object sender, RoutedEventArgs e) =>
        MessageBox.Show("FT-857D CAT Control\nRadio-style desktop controller\n\nCAT functions include frequency, mode, VFO A/B, split, clarifier/RIT, PTT and receiver status.", "About FT-857D Control", MessageBoxButton.OK, MessageBoxImage.Information);

    private void Dial_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!Connected) { ShowError("VFO dial", new InvalidOperationException("Connect to the radio or enable Demo mode first.")); return; }
        _dialDragging = true;
        var dial = sender as FrameworkElement ?? VfoDial;
        _lastDialAngle = GetDialAngle(e.GetPosition(dial), dial);
        dial.CaptureMouse();
        e.Handled = true;
    }

    private void Dial_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _dialDragging = false;
        if (sender is FrameworkElement dial) dial.ReleaseMouseCapture();
        else VfoDial.ReleaseMouseCapture();
        e.Handled = true;
    }

    private void Dial_LostMouseCapture(object sender, MouseEventArgs e) => _dialDragging = false;

    private async void Dial_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_dialDragging || e.LeftButton != MouseButtonState.Pressed || _dialBusy) return;
        var dial = sender as FrameworkElement ?? VfoDial;
        var angle = GetDialAngle(e.GetPosition(dial), dial);
        var delta = angle - _lastDialAngle;
        if (delta > 180) delta -= 360;
        if (delta < -180) delta += 360;
        if (Math.Abs(delta) < 4.0) return;

        var ticks = Math.Clamp((int)(delta / 4.0), -12, 12);
        if (ticks == 0) ticks = Math.Sign(delta);
        _lastDialAngle = angle;
        await TuneDialAsync(ticks);
    }

    private static double GetDialAngle(Point point, FrameworkElement dial)
    {
        var cx = dial.ActualWidth / 2.0;
        var cy = dial.ActualHeight / 2.0;
        return Math.Atan2(point.Y - cy, point.X - cx) * 180.0 / Math.PI;
    }

    private async Task TuneDialAsync(int ticks)
    {
        if (_dialBusy) return;
        _dialBusy = true;
        try
        {
            var step = _steps[_functionStepIndex];
            var delta = ticks * step;
            var target = Math.Clamp(_frequencyHz + delta, 100_000, 470_000_000);
            await SendAsync(CatProtocol.SetFrequency(target), $"VFO dial · {delta:+#;-#} Hz · step {step:N0} Hz");
            _frequencyHz = target;
            _dialVisualAngle = (_dialVisualAngle + ticks * 7.5) % 360.0;
            DialIndicatorTransform.Angle = _dialVisualAngle;
            PhotoDialTransform.Angle = _dialVisualAngle;
            PhotoDialMarkerTransform.Angle = _dialVisualAngle;
            UpdateDisplay();
        }
        catch (Exception ex) { ShowError("Dial tune failed", ex); }
        finally { _dialBusy = false; }
    }

    private void RefreshPorts_Click(object sender, RoutedEventArgs e) => RefreshPorts();

    private void PortBox_DropDownOpened(object sender, EventArgs e) => RefreshPorts();

    private void PhotoPortBox_DropDownOpened(object sender, EventArgs e) => RefreshPorts();

    private void RefreshPorts()
    {
        var selected = (PortBox.Text ?? "").Trim();
        var ports = RadioConnection.AvailablePorts();
        PortBox.ItemsSource = ports;
        PhotoPortBox.ItemsSource = ports;
        if (!string.IsNullOrWhiteSpace(selected) && ports.Contains(selected, StringComparer.OrdinalIgnoreCase))
            PortBox.Text = ports.First(p => string.Equals(p, selected, StringComparison.OrdinalIgnoreCase));
        else if (ports.Length > 0)
            PortBox.Text = ports[0];
        if (ports.Length > 0 && string.IsNullOrWhiteSpace(PhotoPortBox.Text))
            PhotoPortBox.Text = PortBox.Text;
        StatusText.Text = ports.Length == 0 ? "No serial ports found" : $"Found {ports.Length} serial port(s)";
    }

    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        if (Connected)
        {
            if (_transmitting) await ReleasePttAsync();
            _pollTimer.Stop();
            _radio.Close();
            _sessionConnected = false;
            SetConnectedUi(false, "Disconnected");
            return;
        }

        try
        {
            if (DemoCheck.IsChecked == true)
            {
                SetConnectedUi(true, "Demo mode — no radio commands are being sent");
            }
            else
            {
                var port = (!string.IsNullOrWhiteSpace(PhotoPortBox.Text) ? PhotoPortBox.Text : PortBox.Text).Trim().ToUpperInvariant();
                PortBox.Text = port;
                if (string.IsNullOrWhiteSpace(port)) throw new InvalidOperationException("Select a COM port, or type one such as COM3.");

                var baudText = (!string.IsNullOrWhiteSpace(PhotoBaudBox.Text) ? PhotoBaudBox.Text : BaudBox.Text).Trim();
                BaudBox.Text = baudText;
                if (!int.TryParse(baudText, out var baud) || (baud != 4800 && baud != 9600 && baud != 38400))
                    throw new InvalidOperationException("Choose CAT baud rate 4800, 9600, or 38400. It must match the FT-857D CAT RATE menu setting.");

                StatusText.Text = $"Opening {port} at {baud} baud…";
                _radio.Open(port, baud);

                // Do a real CAT query before claiming the radio is connected.
                // FT-817 and FT-857D use the same 5-byte CAT frequency/mode command here.
                RadioState? state = null;
                Exception? lastCatError = null;
                for (var attempt = 1; attempt <= 3; attempt++)
                {
                    try
                    {
                        state = await _radio.ReadStateAsync();
                        break;
                    }
                    catch (Exception ex)
                    {
                        lastCatError = ex;
                        if (attempt < 3) await Task.Delay(250);
                    }
                }
                if (state is null)
                    throw new IOException("COM port opened, but the FT-857D did not answer the CAT frequency query after 3 attempts.", lastCatError);

                _frequencyHz = state.Value.FrequencyHz;
                _mode = state.Value.Mode;
                UpdateDisplay();
                SetConnectedUi(true, $"CAT OK · {port} · {baud} baud");
            }
            _sessionConnected = true;
            // Start every real CAT session with CLAR explicitly OFF.  The FT-857D
            // CAT readback does not report clarifier state, so keeping a stale local
            // flag can otherwise make the UI appear permanently enabled.
            _clarifier = false;
            if (DemoCheck.IsChecked != true)
            {
                await _radio.SendAsync(CatProtocol.SetClarifier(false));
            }
            UpdateDisplay();
            _pollTimer.Start();
        }
        catch (Exception ex)
        {
            _pollTimer.Stop();
            _radio.Close();
            _sessionConnected = false;
            SetConnectedUi(false, $"CAT connection failed: {ex.Message}");
            ShowError("Could not connect to FT-857D CAT", new Exception(
                ex.Message + "\n\nCheck: correct COM port; radio CAT RATE matches the selected baud; no other program is using the COM port; FT-857D Menu 020 [CAT/LIN/TUN] is set to CAT; CAT cable is connected to the rear CAT/LINEAR socket. If your USB CAT interface requires RTS/DTR power, tell me the cable/chipset before enabling those lines."));
        }
    }

    private void SetConnectedUi(bool connected, string message)
    {
        ConnectButton.Content = connected ? "DISCONNECT" : "CONNECT";
        PhotoConnectButton.Content = connected ? "DISCONNECT" : "CONNECT";
        PhotoConnectButton.Background = new SolidColorBrush(connected ? Color.FromRgb(115, 35, 35) : Color.FromRgb(35, 95, 55));
        PhotoConnectionText.Text = connected ? "Connected" : "Disconnected";
        ConnectButton.Background = new SolidColorBrush(connected ? Color.FromRgb(110, 57, 42) : Color.FromRgb(39, 100, 73));
        LinkLamp.Fill = new SolidColorBrush(connected ? Color.FromRgb(71, 220, 112) : Color.FromRgb(102, 102, 102));
        PortBox.IsEnabled = !connected;
        BaudBox.IsEnabled = !connected;
        PhotoPortBox.IsEnabled = !connected;
        PhotoBaudBox.IsEnabled = !connected;
        DemoCheck.IsEnabled = !connected;
        RefreshButton.IsEnabled = !connected;
        StatusText.Text = message;
    }

    private async void PollTimer_Tick(object? sender, EventArgs e)
    {
        if (_polling || !Connected || DemoCheck.IsChecked == true) return;
        _polling = true;
        try
        {
            var state = await _radio.ReadStateAsync();
            _frequencyHz = state.FrequencyHz;
            _mode = state.Mode;
            UpdateDisplay();

            // F7 is the FT-857D's documented TX-status query. It reports PO meter,
            // SPLIT, HI-SWR and PTT, so this also notices TX keyed from the microphone.
            var tx = await _radio.ReadTxStatusAsync();
            // FT-857D status byte layout (manual Notes 3/4):
            // TX: bits 3..1 PO, bit 5 SPLIT, bit 6 HI-SWR, bit 7 PTT.
            bool radioTx = (tx & 0x80) == 0;
            bool splitOn = (tx & 0x20) == 0;
            bool highSwr = (tx & 0x40) != 0;
            SplitText.Text = splitOn ? "SPLIT ON" : "SPLIT OFF";
            _transmitting = radioTx;

            if (radioTx)
            {
                UpdateTxMeter(ScaleCatMeter((tx >> 1) & 0x07), highSwr);
                _rxActive = false;
                StatusText.Text = highSwr ? "TRANSMITTING · HIGH SWR" : "TRANSMITTING · PO meter active";
            }
            else
            {
                var rx = await _radio.ReadRxStatusAsync();
                UpdateSignalMeter(ScaleCatMeter((rx >> 1) & 0x07));
                _rxActive = (rx & 0x80) == 0;
                StatusText.Text = $"Connected · Last update {DateTime.Now:T}";
            }
        }
        catch (Exception ex)
        {
            // A single missed CAT reply must not kill the live meters.
            // Leave the timer running so frequency/mode and E7/F7 meters recover
            // automatically on the next poll.
            StatusText.Text = $"CAT poll retrying: {ex.Message}";
        }
        finally { _polling = false; }
    }

    private void UpdateActivityLamps()
    {
        _lampPhase = !_lampPhase;
        TxLamp.Fill = new SolidColorBrush(_transmitting && _lampPhase ? Color.FromRgb(255, 55, 45) : Color.FromRgb(72, 18, 15));
        RxLamp.Fill = new SolidColorBrush(!_transmitting && _rxActive && _lampPhase ? Color.FromRgb(174, 232, 35) : Color.FromRgb(38, 50, 13));
    }

    private static int ScaleCatMeter(int raw)
    {
        raw = Math.Clamp(raw, 0, 7);
        return (int)Math.Round(raw * 15.0 / 7.0);
    }

    private void UpdateSignalMeter(int level)
    {
        level = Math.Clamp(level, 0, 15);
        _lastSignalLevel = level;
        SignalText.Text = level <= 9 ? $"S{level}" : $"+{(level - 9) * 10}";

        var segments = new Border[] { SignalSeg0, SignalSeg1, SignalSeg2, SignalSeg3, SignalSeg4, SignalSeg5, SignalSeg6, SignalSeg7, SignalSeg8, SignalSeg9, SignalSeg10, SignalSeg11, SignalSeg12, SignalSeg13, SignalSeg14, SignalSeg15 };
        for (int i = 0; i < segments.Length; i++)
        {
            bool lit = i <= level;
            // S0-S9 are green. S9+10 through +60 are red for an immediate strong-signal warning.
            Color on = i <= 9 ? Color.FromRgb(42, 225, 74) : Color.FromRgb(245, 48, 42);
            Color off = i <= 9 ? Color.FromRgb(28, 62, 34) : Color.FromRgb(70, 28, 26);
            segments[i].Background = new SolidColorBrush(lit ? on : off);
            segments[i].BorderBrush = new SolidColorBrush(lit ? on : Color.FromRgb(55, 70, 55));
        }

        var angle = -62.0 + (level / 15.0) * 124.0;
        AnalogNeedleTransform.Angle = angle;
        AnalogMeterText.Text = SignalText.Text;
        var lcdSegments = new Border[] { LcdMeterSeg0, LcdMeterSeg1, LcdMeterSeg2, LcdMeterSeg3, LcdMeterSeg4, LcdMeterSeg5, LcdMeterSeg6, LcdMeterSeg7, LcdMeterSeg8, LcdMeterSeg9, LcdMeterSeg10, LcdMeterSeg11, LcdMeterSeg12, LcdMeterSeg13, LcdMeterSeg14, LcdMeterSeg15 };
        for (int i = 0; i < lcdSegments.Length; i++)
        {
            bool lit = i <= level;
            bool strong = i >= 10;
            Color on = strong ? Color.FromRgb(235, 38, 32) : Color.FromRgb(30, 190, 72);
            Color off = strong ? Color.FromRgb(92, 42, 30) : Color.FromRgb(37, 90, 46);
            lcdSegments[i].Background = new SolidColorBrush(lit ? on : off);
            lcdSegments[i].BorderBrush = new SolidColorBrush(Color.FromRgb(28, 48, 28));
        }
        UpdateSelectedPhotoMeter();
        ApplyPcSquelch(level);
    }

    private void UpdateTxMeter(int level, bool highSwr)
    {
        level = Math.Clamp(level, 0, 15);
        _lastPowerLevel = level;
        _lastHighSwr = highSwr;
        SignalText.Text = highSwr ? "SWR!" : $"PO {level:00}";
        var segments = new Border[] { SignalSeg0, SignalSeg1, SignalSeg2, SignalSeg3, SignalSeg4, SignalSeg5, SignalSeg6, SignalSeg7, SignalSeg8, SignalSeg9, SignalSeg10, SignalSeg11, SignalSeg12, SignalSeg13, SignalSeg14, SignalSeg15 };
        for (int i = 0; i < segments.Length; i++)
        {
            bool lit = i <= level;
            Color on = highSwr ? Color.FromRgb(255, 45, 35) : (i < 11 ? Color.FromRgb(42, 225, 74) : Color.FromRgb(245, 48, 42));
            Color off = highSwr ? Color.FromRgb(75, 22, 20) : (i < 11 ? Color.FromRgb(28, 62, 34) : Color.FromRgb(70, 28, 26));
            segments[i].Background = new SolidColorBrush(lit ? on : off);
            segments[i].BorderBrush = new SolidColorBrush(lit ? on : Color.FromRgb(55, 70, 55));
        }
        AnalogNeedleTransform.Angle = -62.0 + (level / 15.0) * 124.0;
        AnalogMeterText.Text = highSwr ? "HI SWR" : "TX PO";
        UpdateSelectedPhotoMeter();
    }

    private void MeterType_Click(object sender, RoutedEventArgs e)
    {
        _meterType = (MeterType)(((int)_meterType + 1) % 4);
        PhotoMeterButton.Content = $"METER: {_meterType.ToString().ToUpperInvariant()}";
        UpdateSelectedPhotoMeter();
    }

    private void UpdateSelectedPhotoMeter()
    {
        // Bottom-left analogue meter is permanently tied to the same live RX S-meter
        // as the LCD, so those two displays can never drift apart.
        double signalValue = Math.Clamp(_lastSignalLevel / 15.0, 0, 1);
        PhotoMeterNeedle1.Angle = -58 + signalValue * 116;
        PhotoSignalBar.Width = 286 * signalValue;
        PhotoSignalMeterText.Text = _lastSignalLevel <= 9
            ? $"SIGNAL S{_lastSignalLevel}"
            : $"SIGNAL +{(_lastSignalLevel - 9) * 10}";

        // Bottom-right analogue meter follows the green METER selector.
        double value; string text;
        switch (_meterType)
        {
            case MeterType.Signal:
                value = signalValue;
                text = PhotoSignalMeterText.Text;
                break;
            case MeterType.Power:
                value = _lastPowerLevel / 15.0;
                text = $"POWER {_lastPowerLevel:00}/15";
                break;
            case MeterType.Swr:
                value = _lastHighSwr ? 1.0 : 0.0;
                text = _lastHighSwr ? "SWR HIGH" : "SWR OK";
                break;
            default:
                value = 0;
                text = "ALC N/A VIA CAT";
                break;
        }
        value = Math.Clamp(value, 0, 1);
        PhotoMeterNeedle2.Angle = -58 + value * 116;
        PhotoPowerBar.Width = 200 * value;
        PhotoPowerMeterText.Text = text;
    }

    private async Task SendAsync(byte[] command, string status)
    {
        if (!Connected) throw new InvalidOperationException("Connect to the radio or enable Demo mode first.");
        if (DemoCheck.IsChecked != true) await _radio.SendAsync(command);
        StatusText.Text = DemoCheck.IsChecked == true ? $"Demo · {status}" : status;
    }

    private async void SetFrequency_Click(object sender, RoutedEventArgs e) => await SetFrequencyFromBoxAsync();

    private async void FrequencyBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { e.Handled = true; await SetFrequencyFromBoxAsync(); }
    }

    private async Task SetFrequencyFromBoxAsync()
    {
        try
        {
            var parsed = ParseFrequency(FrequencyBox.Text);
            await SendAsync(CatProtocol.SetFrequency(parsed), $"Frequency set to {FormatFrequency(parsed)} MHz");
            _frequencyHz = parsed;
            UpdateDisplay();
        }
        catch (Exception ex) { ShowError("Could not set frequency", ex); UpdateDisplay(); }
    }

    private void PhotoFrequencyBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        PhotoFrequencyBox.SelectAll();
    }

    private void PhotoFrequencyBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        PhotoFrequencyBox.Text = FormatFrequency(_frequencyHz);
    }

    private async void PhotoFrequencyBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            PhotoFrequencyBox.Text = FormatFrequency(_frequencyHz);
            Keyboard.ClearFocus();
            return;
        }
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        try
        {
            var parsed = ParseFrequency(PhotoFrequencyBox.Text);
            await SendAsync(CatProtocol.SetFrequency(parsed), $"Frequency set to {FormatFrequency(parsed)} MHz");
            _frequencyHz = parsed;
            UpdateDisplay();
            Keyboard.ClearFocus();
        }
        catch (Exception ex) { ShowError("Could not set frequency", ex); PhotoFrequencyBox.Text = FormatFrequency(_frequencyHz); }
    }

    private async void PhotoFrequencyBox_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true;
        if (PhotoFrequencyBox.IsKeyboardFocusWithin) Keyboard.ClearFocus();
        await TuneDialAsync(e.Delta > 0 ? 1 : -1);
    }

    private async void Tune_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var delta = long.Parse((string)((Button)sender).Tag, CultureInfo.InvariantCulture);
            var target = _frequencyHz + delta;
            await SendAsync(CatProtocol.SetFrequency(target), $"Tuned {delta / 1000:+0;-0} kHz");
            _frequencyHz = target;
            UpdateDisplay();
        }
        catch (Exception ex) { ShowError("Could not tune", ex); }
    }

    private async void Mode_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var button = (Button)sender;
            RadioMode mode;
            if (button.Tag is string tag && !string.IsNullOrWhiteSpace(tag))
                mode = Enum.Parse<RadioMode>(tag);
            else
            {
                RadioMode[] order = [RadioMode.LSB, RadioMode.USB, RadioMode.CW, RadioMode.CWR, RadioMode.AM, RadioMode.FM, RadioMode.DIG, RadioMode.PKT];
                var i = Array.IndexOf(order, _mode);
                mode = order[(i + 1 + order.Length) % order.Length];
            }
            await SendAsync(CatProtocol.SetMode(mode), $"Mode set to {mode}");
            _mode = mode;
            UpdateDisplay();
        }
        catch (Exception ex) { ShowError("Could not set mode", ex); }
    }

    private void UnsupportedCat_Click(object sender, RoutedEventArgs e)
    {
        var name = (sender as Button)?.Tag?.ToString() ?? "This control";
        StatusText.Text = $"{name}: not exposed by the FT-857D documented CAT command set";
        MessageBox.Show(this,
            $"{name} is a real front-panel function, but Yaesu does not provide a documented FT-857D CAT opcode for it.\n\nThe controller will not send guessed/undocumented bytes to the radio.",
            "FT-857D CAT limitation", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private async void Vfo_Click(object sender, RoutedEventArgs e)
    {
        try { await SendAsync(CatProtocol.ToggleVfo(), "VFO changed"); _vfoB = !_vfoB; UpdateDisplay(); }
        catch (Exception ex) { ShowError("Could not change VFO", ex); }
    }

    private async void Split_Click(object sender, RoutedEventArgs e)
    {
        try { var next = !_split; await SendAsync(CatProtocol.SetSplit(next), $"Split {(next ? "on" : "off")}"); _split = next; UpdateDisplay(); }
        catch (Exception ex) { ShowError("Could not change split", ex); }
    }

    private async void Read_Click(object sender, RoutedEventArgs e)
    {
        if (DemoCheck.IsChecked == true) { StatusText.Text = "Demo · simulated radio state refreshed"; UpdateDisplay(); return; }
        try
        {
            if (!_radio.IsOpen) throw new InvalidOperationException("Connect to the radio first.");
            var state = await _radio.ReadStateAsync();
            _frequencyHz = state.FrequencyHz; _mode = state.Mode; UpdateDisplay();
            StatusText.Text = "Radio state read successfully";
        }
        catch (Exception ex) { ShowError("Could not read radio", ex); }
    }

    private async void Ptt_Down(object sender, MouseButtonEventArgs e)
    {
        try
        {
            if (sender is Button pressed) pressed.CaptureMouse();
            else PttButton.CaptureMouse();
            await SendAsync(CatProtocol.SetPtt(true), "TRANSMITTING");
            _transmitting = true;
            PttButton.Content = "TX";
            PhotoPttButton.Content = "TX";
            PttButton.Background = new SolidColorBrush(Color.FromRgb(194, 50, 44));
            PhotoPttButton.Background = new SolidColorBrush(Color.FromRgb(238, 20, 28));
        }
        catch (Exception ex) { ShowError("PTT failed", ex); }
    }

    private async void Ptt_Up(object sender, MouseButtonEventArgs e) => await ReleasePttAsync();
    private async void Ptt_LostCapture(object sender, MouseEventArgs e) { if (_transmitting) await ReleasePttAsync(); }

    private async Task ReleasePttAsync()
    {
        try { if (_transmitting) await SendAsync(CatProtocol.SetPtt(false), "Receive"); }
        catch (Exception ex) { ShowError("Could not release PTT", ex); }
        finally
        {
            _transmitting = false;
            PttButton.ReleaseMouseCapture();
            PhotoPttButton.ReleaseMouseCapture();
            PttButton.Content = "PTT";
            PhotoPttButton.Content = "PTT";
            PttButton.Background = new SolidColorBrush(Color.FromRgb(128, 46, 46));
            PhotoPttButton.Background = new SolidColorBrush(Color.FromRgb(196, 21, 28));
        }
    }

    private async void Home_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await SendAsync(CatProtocol.SetFrequency(_homeHz), $"HOME · {FormatFrequency(_homeHz)} MHz");
            await SendAsync(CatProtocol.SetMode(_homeMode), $"HOME · {_homeMode}");
            _frequencyHz = _homeHz; _mode = _homeMode; _memoryMode = false; UpdateDisplay();
        }
        catch (Exception ex) { ShowError("HOME failed", ex); }
    }

    private async void Clar_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _clarifier = !_clarifier;
            await SendAsync(CatProtocol.SetClarifier(_clarifier), $"Clarifier {(_clarifier ? "ON" : "OFF")}");
            UpdateDisplay();
        }
        catch (Exception ex) { _clarifier = !_clarifier; ShowError("Clarifier failed", ex); }
    }

    private static string FormatStep(long hz) => hz >= 1000 ? $"STEP {hz / 1000.0:0.#} kHz" : $"STEP {hz} Hz";

    private void Func_Click(object sender, RoutedEventArgs e)
    {
        _functionStepIndex = (_functionStepIndex + 1) % _steps.Length;
        PhotoStepButton.Content = FormatStep(_steps[_functionStepIndex]);
        StatusText.Text = $"Tuning step: {_steps[_functionStepIndex]} Hz · mouse wheel on tuning dial uses this step";
    }

    private async void Vm_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!_memoryMode) { _memoryMode = true; await SetRadioStateAsync(_memoryHz, _memoryModeValue, "Memory recalled"); }
            else { _memoryMode = false; StatusText.Text = "VFO mode"; }
            UpdateDisplay();
        }
        catch (Exception ex) { ShowError("V/M failed", ex); }
    }

    private void MemoryWrite_Click(object sender, RoutedEventArgs e)
    {
        _memoryHz = _frequencyHz; _memoryModeValue = _mode;
        StatusText.Text = $"MW · saved app memory: {FormatFrequency(_memoryHz)} {_memoryModeValue}";
    }

    private async void Scan_Click(object sender, RoutedEventArgs e)
    {
        if (_scanning) { _scanning = false; ScanButton.Content = "SCAN"; StatusText.Text = "Scan stopped"; return; }
        if (!Connected) { ShowError("Scan", new InvalidOperationException("Connect to the radio first.")); return; }
        _scanning = true; ScanButton.Content = "STOP";
        try
        {
            while (_scanning && Connected)
            {
                var target = _frequencyHz + Math.Max(1000, _steps[_functionStepIndex]);
                if (target > 470_000_000) target = 100_000;
                await SendAsync(CatProtocol.SetFrequency(target), $"Scanning · {FormatFrequency(target)}");
                _frequencyHz = target; UpdateDisplay();
                if (DemoCheck.IsChecked != true)
                {
                    await Task.Delay(180);
                    var rx = await _radio.ReadRxStatusAsync();
                    UpdateSignalMeter(ScaleCatMeter((rx >> 1) & 0x07));
                    // FT-857D RX status bit 0 is squelch status: 0 means signal present.
                    if ((rx & 0x80) == 0) { _scanning = false; StatusText.Text = $"Scan stopped on signal · {FormatFrequency(_frequencyHz)}"; break; }
                }
                await Task.Delay(220);
            }
        }
        catch (Exception ex) { ShowError("Scan failed", ex); }
        finally { _scanning = false; ScanButton.Content = "SCAN"; }
    }

    private async void Power_Click(object sender, RoutedEventArgs e)
    {
        // CAT cannot safely power an FT-857D on/off. Use this faceplate button as Connect/Disconnect.
        Connect_Click(sender, e);
        await Task.CompletedTask;
    }

    private async void BandGrid_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is null) return;
        try
        {
            var target = long.Parse(button.Tag.ToString()!, CultureInfo.InvariantCulture);
            await SendAsync(CatProtocol.SetFrequency(target), $"Band {button.Content} · {FormatFrequency(target)}");
            _frequencyHz = target;
            _memoryMode = false;
            UpdateDisplay();
        }
        catch (Exception ex) { ShowError("Band selection failed", ex); }
    }

    private async void Band_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var next = _bandHomes.FirstOrDefault(f => f > _frequencyHz + 100_000);
            if (next == 0) next = _bandHomes[0];
            await SendAsync(CatProtocol.SetFrequency(next), $"Band up · {FormatFrequency(next)}");
            _frequencyHz = next; UpdateDisplay();
        }
        catch (Exception ex) { ShowError("Band change failed", ex); }
    }

    private async void Dial_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true;
        await TuneDialAsync(e.Delta > 0 ? 1 : -1);
    }

    private async void RitStep_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _ritHz = Math.Clamp(_ritHz + int.Parse((string)((Button)sender).Tag, CultureInfo.InvariantCulture), -9990, 9990);
            await SendAsync(CatProtocol.SetClarifierOffset(_ritHz), $"RIT {_ritHz / 1000.0:+0.00;-0.00;0.00} kHz");
            // Changing the stored RIT offset must not silently switch CLAR on.
            // CLAR is controlled only by its explicit toggle/button.
            UpdateDisplay();
        }
        catch (Exception ex) { ShowError("RIT failed", ex); }
    }

    private async void RitClear_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _ritHz = 0;
            await SendAsync(CatProtocol.SetClarifierOffset(0), "RIT cleared");
            UpdateDisplay();
        }
        catch (Exception ex) { ShowError("RIT clear failed", ex); }
    }

    private async void ClarifierKnob_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true;
        try
        {
            int step = e.Delta > 0 ? 100 : -100;
            _ritHz = Math.Clamp(_ritHz + step, -9990, 9990);
            await SendAsync(CatProtocol.SetClarifierOffset(_ritHz), $"Clarifier {_ritHz / 1000.0:+0.00;-0.00;0.00} kHz");
            // Changing the stored RIT offset must not silently switch CLAR on.
            // CLAR is controlled only by its explicit toggle/button.
            UpdateDisplay();
        }
        catch (Exception ex) { ShowError("Clarifier tuning failed", ex); }
    }

    private async void ClarifierToggle_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        try
        {
            _clarifier = !_clarifier;
            await SendAsync(CatProtocol.SetClarifier(_clarifier), $"Clarifier {(_clarifier ? "ON" : "OFF")}");
            UpdateDisplay();
        }
        catch (Exception ex) { _clarifier = !_clarifier; ShowError("Clarifier failed", ex); }
    }

    private void XitInfo_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        StatusText.Text = "XIT: the documented FT-857D CAT set does not expose an independent XIT control";
    }

    private decimal SelectedCtcssTone()
    {
        var text = CtcssBox.SelectedItem?.ToString() ?? CtcssBox.Text;
        if (!decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var tone)) throw new FormatException("Select a CTCSS tone.");
        return tone;
    }

    private long SelectedShiftHz()
    {
        if (!decimal.TryParse(ShiftAmountBox.Text.Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture, out var mhz) || mhz < 0 || mhz > 99.99999m)
            throw new FormatException("Enter repeater shift in MHz, for example 0.600 or 1.600.");
        return decimal.ToInt64(decimal.Round(mhz * 1_000_000m / 10m) * 10m);
    }

    private async void CtcssOn_Click(object sender, RoutedEventArgs e)
    {
        try { _ctcssTone = SelectedCtcssTone(); await SendAsync(CatProtocol.SetCtcssTone(_ctcssTone), $"CTCSS {_ctcssTone:0.0} Hz"); await SendAsync(CatProtocol.SetCtcssMode(true), "CTCSS encoder ON"); _ctcssEnabled = true; UpdateRepeaterInfo(); }
        catch (Exception ex) { ShowError("CTCSS failed", ex); }
    }

    private async void CtcssOff_Click(object sender, RoutedEventArgs e)
    {
        try { await SendAsync(CatProtocol.SetCtcssMode(false), "CTCSS OFF"); _ctcssEnabled = false; UpdateRepeaterInfo(); }
        catch (Exception ex) { ShowError("CTCSS failed", ex); }
    }

    private async void Shift_Click(object sender, RoutedEventArgs e)
    {
        try { _repeaterShift = int.Parse((string)((Button)sender).Tag, CultureInfo.InvariantCulture); await SendAsync(CatProtocol.SetRepeaterShift(_repeaterShift), _repeaterShift < 0 ? "Repeater − shift" : _repeaterShift > 0 ? "Repeater + shift" : "Simplex"); UpdateRepeaterInfo(); }
        catch (Exception ex) { ShowError("Repeater shift failed", ex); }
    }

    private async void ApplyRepeater_Click(object sender, RoutedEventArgs e)
    {
        try { _repeaterOffsetHz = SelectedShiftHz(); await SendAsync(CatProtocol.SetRepeaterOffset(_repeaterOffsetHz), $"Repeater offset {_repeaterOffsetHz / 1_000_000.0:0.000} MHz"); await SendAsync(CatProtocol.SetRepeaterShift(_repeaterShift), "Repeater settings applied"); if (_ctcssEnabled) { _ctcssTone = SelectedCtcssTone(); await SendAsync(CatProtocol.SetCtcssTone(_ctcssTone), $"CTCSS {_ctcssTone:0.0} Hz"); await SendAsync(CatProtocol.SetCtcssMode(true), "CTCSS encoder ON"); } UpdateRepeaterInfo(); }
        catch (Exception ex) { ShowError("Repeater setup failed", ex); }
    }


    private void ReadRadioMemory_Click(object sender, RoutedEventArgs e)
    {
        MessageBox.Show(this,
            "The FT-857D standard CAT command set does not include a documented command for reading the radio's stored memory alpha tags.\n\n" +
            "This controller therefore keeps its own 200 named memories and does not use undocumented EEPROM addresses.",
            "FT-857D memory names", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void SaveMemory_Click(object sender, RoutedEventArgs e)
    {
        try { var i = MemoryChannelBox.SelectedIndex; if (i < 0) return; _ctcssTone = SelectedCtcssTone(); _repeaterOffsetHz = SelectedShiftHz(); var name = string.IsNullOrWhiteSpace(MemoryNameBox.Text) ? $"CH {i + 1:00}" : MemoryNameBox.Text.Trim(); _memories[i] = new(name, _frequencyHz, _mode, _ctcssEnabled, _ctcssTone, _repeaterShift, _repeaterOffsetHz); SaveMemories(); MemoryInfoText.Text = $"Saved M-{i + 1:000} {name} · {FormatFrequency(_frequencyHz)} {_mode}"; MemoryDisplayText.Text = $"M-{i + 1:000} {name}"; }
        catch (Exception ex) { ShowError("Memory save failed", ex); }
    }

    private async void RecallMemory_Click(object sender, RoutedEventArgs e)
    {
        try { var i = MemoryChannelBox.SelectedIndex; if (i < 0 || _memories[i] is not { } m) throw new InvalidOperationException("That memory channel is empty."); MemoryNameBox.Text = m.Name; _frequencyHz = m.FrequencyHz; _mode = m.Mode; _ctcssEnabled = m.CtcssEnabled; _ctcssTone = m.CtcssTone; _repeaterShift = m.Shift; _repeaterOffsetHz = m.OffsetHz; CtcssBox.SelectedItem = m.CtcssTone.ToString("0.0", CultureInfo.InvariantCulture); ShiftAmountBox.Text = (m.OffsetHz / 1_000_000m).ToString("0.00000", CultureInfo.InvariantCulture).TrimEnd('0').TrimEnd('.'); await SendAsync(CatProtocol.SetFrequency(m.FrequencyHz), $"Memory {i + 1:00} {m.Name}"); await SendAsync(CatProtocol.SetMode(m.Mode), $"Mode {m.Mode}"); await SendAsync(CatProtocol.SetRepeaterOffset(m.OffsetHz), "Repeater offset restored"); await SendAsync(CatProtocol.SetRepeaterShift(m.Shift), "Repeater shift restored"); await SendAsync(CatProtocol.SetCtcssTone(m.CtcssTone), $"CTCSS {m.CtcssTone:0.0} Hz"); await SendAsync(CatProtocol.SetCtcssMode(m.CtcssEnabled), m.CtcssEnabled ? "CTCSS ON" : "CTCSS OFF"); UpdateDisplay(); UpdateRepeaterInfo(); MemoryInfoText.Text = $"Recalled M-{i + 1:000} {m.Name}"; MemoryDisplayText.Text = $"M-{i + 1:000} {m.Name}"; }
        catch (Exception ex) { ShowError("Memory recall failed", ex); }
    }

    private void UpdateRepeaterInfo() => MemoryInfoText.ToolTip = $"CTCSS {(_ctcssEnabled ? _ctcssTone.ToString("0.0") + " Hz" : "OFF")} · Shift {(_repeaterShift < 0 ? "−" : _repeaterShift > 0 ? "+" : "simplex")} {_repeaterOffsetHz / 1_000_000.0:0.000} MHz";

    private void LoadMemories()
    {
        try { if (!File.Exists(MemoryFile)) return; var loaded = JsonSerializer.Deserialize<MemoryChannel?[]>(File.ReadAllText(MemoryFile)); if (loaded is null) return; Array.Copy(loaded, _memories, Math.Min(loaded.Length, _memories.Length)); }
        catch { }
    }

    private void SaveMemories()
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(MemoryFile)!);
        File.WriteAllText(MemoryFile, JsonSerializer.Serialize(_memories, new JsonSerializerOptions { WriteIndented = true }));
    }

    private async Task SetRadioStateAsync(long frequency, RadioMode mode, string status)
    {
        await SendAsync(CatProtocol.SetFrequency(frequency), status);
        await SendAsync(CatProtocol.SetMode(mode), status);
        _frequencyHz = frequency; _mode = mode;
    }

    private void UpdateDisplay()
    {
        FrequencyBox.Text = FormatFrequency(_frequencyHz);
        if (!PhotoFrequencyBox.IsKeyboardFocusWithin) PhotoFrequencyBox.Text = FormatFrequency(_frequencyHz);
        FrequencyReadout.Text = FormatFrequency(_frequencyHz);
        ModeText.Text = _mode.ToString();
        UpdatePhotoModeIndicators();
        VfoText.Text = _vfoB ? "VFO B" : "VFO A";
        SplitText.Text = _split ? "SPLIT ON" : "SPLIT OFF";
        SplitButton.Background = new SolidColorBrush(_split ? Color.FromRgb(164, 105, 30) : Color.FromRgb(69, 74, 79));
        ClarButton.Background = new SolidColorBrush(_clarifier ? Color.FromRgb(164, 105, 30) : Color.FromRgb(23, 25, 26));
        RitText.Text = $"{(_clarifier ? "ON" : "OFF")} {_ritHz / 1000.0:+0.00;-0.00;0.00}";
        PhotoClarText.Text = $"CLAR {(_clarifier ? "ON" : "OFF")} {_ritHz / 1000.0:+0.00;-0.00;0.00} kHz";
        VfoText.Text = _memoryMode ? "MEM" : (_vfoB ? "VFO B" : "VFO A");
    }


    private void UpdatePhotoModeIndicators()
    {
        // FT-857D CAT readback: 00 LSB, 01 USB, 04 AM, 06 WFM, 08 FM,
        // 88 NFM, 0A DIG.  The photo panel has one FM lamp for FM/WFM/NFM.
        PhotoModeLsb.Visibility = _mode == RadioMode.LSB ? Visibility.Visible : Visibility.Collapsed;
        PhotoModeUsb.Visibility = _mode == RadioMode.USB ? Visibility.Visible : Visibility.Collapsed;
        PhotoModeAm.Visibility = _mode == RadioMode.AM ? Visibility.Visible : Visibility.Collapsed;
        PhotoModeFm.Visibility = (_mode == RadioMode.FM || _mode == RadioMode.WFM) ? Visibility.Visible : Visibility.Collapsed;
        PhotoModeDig.Visibility = _mode == RadioMode.DIG ? Visibility.Visible : Visibility.Collapsed;
    }

    private static string FormatFrequency(long hz)
    {
        var mhz = hz / 1_000_000;
        var fraction10Hz = (hz % 1_000_000) / 10;
        var fraction = fraction10Hz.ToString("D5");
        return $"{mhz:000}.{fraction[..3]}.{fraction[3..]}";
    }

    private static long ParseFrequency(string text)
    {
        var trimmed = text.Trim().Replace(',', '.');
        if (trimmed.Count(c => c == '.') > 1)
        {
            var digits = trimmed.Replace(".", "");
            if (!long.TryParse(digits, out var units10Hz)) throw new FormatException("Enter a frequency such as 145.500 or 145.500.00.");
            return units10Hz * 10;
        }
        if (!decimal.TryParse(trimmed, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var mhz))
            throw new FormatException("Enter a frequency in MHz, such as 145.500.");
        return decimal.ToInt64(decimal.Round(mhz * 1_000_000m / 10m) * 10m);
    }

    private static void ShowError(string title, Exception ex) =>
        MessageBox.Show(ex.Message, title, MessageBoxButton.OK, MessageBoxImage.Warning);
}
