using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using System.Diagnostics;
using System.Reflection;
using System.Xml.Linq;

namespace PerfTicker;

internal sealed partial class MarketWindow
{
    private Window _gpuWindow, _gadget;
    private TextBlock _gadgetText;
    private TrayIcon _tray;
    private readonly List<Window> _auxiliaries = new();
    private Window Auxiliary(string title, double width, double height, Control content)
    {
        var window = new Window {
        Title = title, Width = width, Height = height, MinWidth = 300, MinHeight = 180,
        Background = MarketStyle.Background, Foreground = MarketStyle.Text, FontFamily = new FontFamily(MarketStyle.Fonts),
        RequestedThemeVariant = RequestedThemeVariant, WindowStartupLocation = WindowStartupLocation.CenterOwner,
        Content = content
        };
        _auxiliaries.Add(window); window.Closed += (_, _) => _auxiliaries.Remove(window); return window;
    }
    internal void ShowGpus()
    {
        if (_gpuWindow?.IsVisible == true) { _gpuWindow.Activate(); return; }
        _gpuWindow = Auxiliary("显卡总览 · PerfTicker", 660, 600, new ScrollViewer { Margin = new Thickness(16), Content = _cards });
        _gpuWindow.Closed += (_, _) => { _gpuWindow.Content = null; _gpuWindow = null; };
        _gpuWindow.Show(this); RenderCards();
    }
    internal void ToggleGadget()
    {
        if (_gadget?.IsVisible == true) { _gadget.Close(); return; }
        _gadgetText = MarketStyle.Label("正在采集…", 14, MarketStyle.Gold); _gadgetText.TextWrapping = TextWrapping.Wrap;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = {
            MarketStyle.Button("主窗口", () => { Show(); Activate(); }), MarketStyle.Button("暂停/继续", TogglePause),
            MarketStyle.Button("关闭", () => _gadget?.Close()) } };
        var panel = new StackPanel { Margin = new Thickness(12), Spacing = 10, Children = { MarketStyle.Label("▥ PERF · TERMINAL", 16, MarketStyle.Gold), _gadgetText, buttons } };
        _gadget = Auxiliary("PerfTicker 桌面挂件", 365, 200, panel); _gadget.ShowInTaskbar = false; _gadget.Topmost = true;
        _gadget.Closed += (_, _) => { _gadget = null; _gadgetText = null; }; _gadget.Show(); UpdateGadget();
    }
    private void UpdateGadget()
    {
        if (_gadgetText != null) _gadgetText.Text = $"{(_remote ? "SSH" : "本机")} · {_sampler.Status}\nCPU {MarketStyle.N(_snapshot.CpuPct)}%    内存 {MarketStyle.N(_snapshot.MemPct)}%\nGPU {MarketStyle.N(_snapshot.Gpu.UtilPct)}%    ↓ {MarketStyle.N(_snapshot.NetRxMBps)} MB/s";
    }
    private void SetupTray()
    {
        try
        {
            using var iconStream = Assembly.GetExecutingAssembly().GetManifestResourceStream("PerfTicker.app.ico");
            Icon = new WindowIcon(iconStream);
            var menu = new NativeMenu();
            void Add(string title, Action action) { var item = new NativeMenuItem(title); item.Click += (_, _) => action(); menu.Add(item); }
            Add("显示主窗口", () => { Show(); Activate(); }); Add("暂停 / 继续", TogglePause);
            Add("桌面挂件", ToggleGadget); Add("连接设置", async () => await Configure()); Add("退出", Close);
            _tray = new TrayIcon { Icon = Icon, ToolTipText = "PerfTicker · 性能行情终端", Menu = menu };
            _tray.Clicked += (_, _) => { Show(); Activate(); };
            TrayIcon.SetIcons(Application.Current, new TrayIcons { _tray });
        }
        catch (Exception ex) { Diag.Error("TRAY", ex); }
    }
    internal void ShowSettings()
    {
        var theme = new ComboBox { ItemsSource = new[] { "深色", "浅色", "跟随系统" }, SelectedIndex = _settings.ThemeMode };
        theme.SelectionChanged += (_, _) => SetTheme(theme.SelectedIndex);
        var marquee = new CheckBox { Content = "显示底部行情播报", IsChecked = _settings.Marquee };
        marquee.IsCheckedChanged += (_, _) => { _settings.Marquee = marquee.IsChecked == true; _marquee.IsVisible = _settings.Marquee; };
        var notes = MarketStyle.Label("空格 暂停/继续 · 1–5 K线周期 · T 置顶 · C 配色\nE 营收 · ⌘/Ctrl+F 搜索 · F9 挂件 · F11 全屏", 12, MarketStyle.Muted); notes.TextWrapping = TextWrapping.Wrap;
        var autoStart = new CheckBox { Content = "登录此 Mac 时启动", IsChecked = MacAutoStart.Enabled, IsEnabled = OperatingSystem.IsMacOS() };
        var message = MarketStyle.Label("", 11, MarketStyle.Gold); message.TextWrapping = TextWrapping.Wrap;
        autoStart.IsCheckedChanged += (_, _) => { try { MacAutoStart.Set(autoStart.IsChecked == true); message.Text = "登录启动设置已保存"; } catch (Exception ex) { message.Text = ex.Message; } };
        var content = new StackPanel { Margin = new Thickness(22), Spacing = 14, Children = {
            MarketStyle.Label("偏好设置", 22, MarketStyle.Gold), MarketStyle.Label("外观"), theme, autoStart, marquee,
            MarketStyle.Button("打开诊断日志", () => { Directory.CreateDirectory(Diag.LogDir); Process.Start(new ProcessStartInfo(Diag.LogDir) { UseShellExecute = true }); }),
            MarketStyle.Button("显示桌面挂件", ToggleGadget), notes, message,
            MarketStyle.Label("监控为只读，不提供结束本机或服务器进程。", 11, MarketStyle.Muted) } };
        Auxiliary("设置 · PerfTicker", 540, 455, new ScrollViewer { Content = content }).Show(this);
    }
    private void ShowAbout()
    {
        var text = MarketStyle.Label($"性能行情终端 · PerfTicker 1.7\n\n{_snapshot.HostName}\n{_snapshot.OsName} · {_snapshot.Arch}\n{_snapshot.CpuName}\n{_snapshot.CoreMHz.Length} 逻辑核心 · {_snapshot.MemTotalGB:0.0} GB 内存\n{_snapshot.Gpus.Count} 张 GPU\n\n原作者 QiuSui / QiuSui_Studio\nGPL-3.0 · https://github.com/zangly55/PerfTicker-Remote\n\n营收是模拟玩法，不代表真实收益。\nMac 不支持的硬件读数显示 —。", 14);
        text.TextWrapping = TextWrapping.Wrap; text.Margin = new Thickness(24);
        Auxiliary("关于 · PerfTicker", 590, 430, text).Show(this);
    }
}

internal static class MacAutoStart
{
    private static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "LaunchAgents", "io.github.zangly55.perfticker.plist");
    public static bool Enabled => OperatingSystem.IsMacOS() && File.Exists(FilePath);
    internal static string Plist(string executable) => new XDocument(new XDeclaration("1.0", "UTF-8", null),
        new XDocumentType("plist", "-//Apple//DTD PLIST 1.0//EN", "http://www.apple.com/DTDs/PropertyList-1.0.dtd", null),
        new XElement("plist", new XAttribute("version", "1.0"), new XElement("dict",
            new XElement("key", "Label"), new XElement("string", "io.github.zangly55.perfticker"),
            new XElement("key", "ProgramArguments"), new XElement("array", new XElement("string", executable)),
            new XElement("key", "RunAtLoad"), new XElement("true"),
            new XElement("key", "ProcessType"), new XElement("string", "Interactive")))).ToString();
    public static void Set(bool enabled)
    {
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException("登录启动在此处仅支持 macOS。");
        if (!enabled) { if (File.Exists(FilePath)) File.Delete(FilePath); return; }
        string executable = Environment.ProcessPath;
        if (executable == null || !executable.Contains(".app/Contents/MacOS/", StringComparison.Ordinal) || executable.Contains("AppTranslocation"))
            throw new InvalidOperationException("请先将 PerfTicker.app 移入“应用程序”并重新打开，再开启登录启动。");
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)); File.WriteAllText(FilePath, Plist(executable));
    }
}
