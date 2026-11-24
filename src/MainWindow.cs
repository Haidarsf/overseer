// Claude Monitor - WPF UI (built in code, no XAML files).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using Shapes = System.Windows.Shapes;

namespace ClaudeMonitor
{
    public partial class MainWindow : Window
    {
        // ---------- palette ----------
        static Color C(string hex) { return (Color)ColorConverter.ConvertFromString(hex); }
        static Brush B(string hex) { var b = new SolidColorBrush(C(hex)); b.Freeze(); return b; }
        static Brush Tint(Brush b, byte alpha)
        {
            var c = ((SolidColorBrush)b).Color;
            var r = new SolidColorBrush(Color.FromArgb(alpha, c.R, c.G, c.B)); r.Freeze(); return r;
        }
        static Brush Grad(Color a, Color b, double angleDeg)
        {
            var g = new LinearGradientBrush(a, b, angleDeg); g.Freeze(); return g;
        }
        static Color Col(Brush b) { return ((SolidColorBrush)b).Color; }
        static Color WithA(Color c, byte a) { return Color.FromArgb(a, c.R, c.G, c.B); }

        static readonly Brush BgApp = B("#111110"), BgSide = B("#161514"), BgCard = B("#1C1B19"), BgCard2 = B("#181715"), BgHover = B("#25231F"),
            Line = B("#2C2A26"), LineSoft = B("#23211E"), Fg = B("#F1EEE9"), Fg2 = B("#CFCAC2"), Muted = B("#948F86"), Faint = B("#615D56"),
            Accent = B("#E07B53"), Accent2 = B("#F2A65A"), Green = B("#62C98F"), Blue = B("#78A8F0"), Red = B("#EE7D7D"),
            Yellow = B("#E8C15C"), Purple = B("#B391F2"), Teal = B("#5EC4B6"), Pink = B("#E58BBE");
        static readonly Brush[] AvatarColors = { Accent, Blue, Green, Purple, Yellow, Teal, Pink };
        static readonly FontFamily Mono = new FontFamily("Cascadia Mono, Consolas");
        static readonly FontFamily Display = new FontFamily("Segoe UI Variable Display, Segoe UI");
        static readonly FontFamily IconFont = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        // Segoe Fluent Icons glyphs
        const string IcTerminal = "", IcDoc = "", IcEdit = "", IcSearch = "", IcGlobe = "", IcRobot = "",
            IcBolt = "", IcPuzzle = "", IcList = "", IcTool = "", IcPerson = "", IcChat = "",
            IcError = "", IcClock = "", IcPulse = "", IcCoins = "", IcBrain = "", IcSettings = "",
            IcHistory = "", IcChart = "", IcPlug = "", IcCheck = "", IcPause = "", IcStop = "";

        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int val, int size);

        readonly Store store = new Store();
        readonly object gate = new object();
        readonly StackPanel listPanel = new StackPanel();
        readonly ScrollViewer mainScroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        readonly TextBlock statusText = new TextBlock();
        readonly Shapes.Ellipse statusDot = new Shapes.Ellipse { Width = 8, Height = 8 };
        Border tabSession, tabOverview, tabControl;
        string selected;
        bool overview, control, controlDirty, busy, dirty = true, resetScroll;
        DateTime lastRender = DateTime.MinValue;

        public MainWindow()
        {
            Title = "Claude Monitor";
            try
            {
                var ico = typeof(MainWindow).Assembly.GetManifestResourceStream("ClaudeMonitor.app.ico");
                if (ico != null) Icon = System.Windows.Media.Imaging.BitmapFrame.Create(ico);
            }
            catch { }
            Width = 1400; Height = 900; MinWidth = 1000; MinHeight = 640;
            Background = BgApp;
            Foreground = Fg;
            FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
            FontSize = 13;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
            UseLayoutRounding = true;
            SourceInitialized += delegate { DarkTitleBar(); };

            var root = new Grid();
            // soft accent glow behind everything
            var glow = new RadialGradientBrush { Center = new Point(0.92, -0.05), GradientOrigin = new Point(0.92, -0.05), RadiusX = 0.55, RadiusY = 0.75 };
            glow.GradientStops.Add(new GradientStop(WithA(Col(Accent), 0x26), 0));
            glow.GradientStops.Add(new GradientStop(WithA(Col(Accent), 0x00), 1));
            var glowLayer = new Border { Background = glow, IsHitTestVisible = false };
            Grid.SetRowSpan(glowLayer, 2);
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(64) });
            root.RowDefinitions.Add(new RowDefinition());
            root.Children.Add(glowLayer);
            root.Children.Add(Header());

            var body = new Grid();
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(320) });
            body.ColumnDefinitions.Add(new ColumnDefinition());
            Grid.SetRow(body, 1);
            root.Children.Add(body);

            var side = new Border { Background = BgSide, BorderBrush = LineSoft, BorderThickness = new Thickness(0, 1, 1, 0), CornerRadius = new CornerRadius(0, 16, 0, 0) };
            var sideScroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = listPanel };
            listPanel.Margin = new Thickness(10, 12, 10, 16);
            side.Child = sideScroll;
            body.Children.Add(side);

            Grid.SetColumn(mainScroll, 1);
            mainScroll.Content = Empty("Reading Claude session logs…");
            body.Children.Add(mainScroll);
            Grid.SetRowSpan(toastHost, 2);
            root.Children.Add(toastHost);
            InitControls();
            Content = root;

            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            timer.Tick += delegate { Refresh(); };
            timer.Start();
            Loaded += delegate { Refresh(); };
        }

        void DarkTitleBar()
        {
            try
            {
                var h = new WindowInteropHelper(this).Handle;
                int on = 1; DwmSetWindowAttribute(h, 20, ref on, 4);           // immersive dark mode
                int cap = 0x00101111; DwmSetWindowAttribute(h, 35, ref cap, 4);  // caption color (BGR)
                int txt = 0x00E9EEF1; DwmSetWindowAttribute(h, 36, ref txt, 4);  // caption text
                int corner = 2; DwmSetWindowAttribute(h, 33, ref corner, 4);     // rounded corners
            }
            catch { }
        }

        public static ResourceDictionary Theme()
        {
            const string xaml = @"
<ResourceDictionary xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'>
  <Style TargetType='ScrollBar'>
    <Setter Property='Width' Value='12'/>
    <Setter Property='MinWidth' Value='12'/>
    <Setter Property='Background' Value='Transparent'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='ScrollBar'>
          <Grid Background='Transparent'>
            <Track x:Name='PART_Track' IsDirectionReversed='True'>
              <Track.Thumb>
                <Thumb>
                  <Thumb.Template>
                    <ControlTemplate TargetType='Thumb'>
                      <Border x:Name='b' CornerRadius='3' Background='#34312D' Margin='4,2,4,2'/>
                      <ControlTemplate.Triggers>
                        <Trigger Property='IsMouseOver' Value='True'><Setter TargetName='b' Property='Background' Value='#4A4640'/></Trigger>
                        <Trigger Property='IsDragging' Value='True'><Setter TargetName='b' Property='Background' Value='#E07B53'/></Trigger>
                      </ControlTemplate.Triggers>
                    </ControlTemplate>
                  </Thumb.Template>
                </Thumb>
              </Track.Thumb>
            </Track>
          </Grid>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>
  <Style TargetType='ToolTip'>
    <Setter Property='Foreground' Value='#F1EEE9'/>
    <Setter Property='FontSize' Value='12'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='ToolTip'>
          <Border Background='#26241F' BorderBrush='#3A3732' BorderThickness='1' CornerRadius='8' Padding='10,7' MaxWidth='460'>
            <ContentPresenter>
              <ContentPresenter.Resources>
                <Style TargetType='TextBlock'><Setter Property='TextWrapping' Value='Wrap'/></Style>
              </ContentPresenter.Resources>
            </ContentPresenter>
          </Border>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>
  <Style TargetType='TextBox'>
    <Setter Property='Foreground' Value='#F1EEE9'/>
    <Setter Property='CaretBrush' Value='#F2A65A'/>
    <Setter Property='SelectionBrush' Value='#E07B53'/>
    <Setter Property='FontSize' Value='12.5'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='TextBox'>
          <Border x:Name='bd' Background='#141312' BorderBrush='#2C2A26' BorderThickness='1' CornerRadius='9' Padding='10,6,10,7'>
            <Grid>
              <TextBlock x:Name='ph' Text='{TemplateBinding Tag}' Foreground='#615D56' IsHitTestVisible='False' Visibility='Collapsed' Margin='2,0,0,0'/>
              <ScrollViewer x:Name='PART_ContentHost' VerticalAlignment='Center'/>
            </Grid>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property='Text' Value=''><Setter TargetName='ph' Property='Visibility' Value='Visible'/></Trigger>
            <Trigger Property='IsKeyboardFocused' Value='True'><Setter TargetName='bd' Property='BorderBrush' Value='#E07B53'/></Trigger>
            <Trigger Property='IsMouseOver' Value='True'><Setter TargetName='bd' Property='BorderBrush' Value='#4A4640'/></Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>
</ResourceDictionary>";
            return (ResourceDictionary)XamlReader.Parse(xaml);
        }

        // ---------- header ----------
        UIElement Header()
        {
            var g = new Grid { Background = Brushes.Transparent };
            var left = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(20, 0, 0, 0) };
            var logoBox = new Border
            {
                Width = 36, Height = 36, CornerRadius = new CornerRadius(10), Margin = new Thickness(0, 0, 12, 0),
                Background = Grad(Col(Accent2), Col(Accent), 135),
                Effect = new DropShadowEffect { Color = Col(Accent), BlurRadius = 18, ShadowDepth = 0, Opacity = 0.45 }
            };
            logoBox.Child = new Shapes.Path
            {
                Data = Geometry.Parse("M8,0 L9.6,6.4 L16,8 L9.6,9.6 L8,16 L6.4,9.6 L0,8 L6.4,6.4 Z"),
                Fill = Brushes.White, Width = 16, Height = 16, Stretch = Stretch.Uniform
            };
            left.Children.Add(logoBox);
            var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var t1 = Txt("Claude Monitor", 16, Fg, FontWeights.SemiBold); t1.FontFamily = Display;
            titles.Children.Add(t1);
            titles.Children.Add(Txt("Live view of your Claude sessions", 11.5, Muted, FontWeights.Normal));
            left.Children.Add(titles);
            g.Children.Add(left);

            var right = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 20, 0) };
            var status = new Border { CornerRadius = new CornerRadius(14), Background = Tint(Fg, 0x0A), BorderBrush = Line, BorderThickness = new Thickness(1), Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(0, 0, 14, 0) };
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            statusDot.Fill = Green; statusDot.Margin = new Thickness(0, 0, 8, 0); statusDot.VerticalAlignment = VerticalAlignment.Center;
            sp.Children.Add(statusDot);
            statusText.Foreground = Fg2; statusText.FontSize = 12; statusText.VerticalAlignment = VerticalAlignment.Center;
            sp.Children.Add(statusText);
            status.Child = sp;
            right.Children.Add(status);

            var seg = new Border { CornerRadius = new CornerRadius(10), Background = Tint(Fg, 0x08), BorderBrush = Line, BorderThickness = new Thickness(1), Padding = new Thickness(3) };
            var segSp = new StackPanel { Orientation = Orientation.Horizontal };
            tabSession = TabButton("Session", IcPulse, 0);
            tabOverview = TabButton("Overview", IcChart, 1);
            tabControl = TabButton("Control", IcSettings, 2);
            segSp.Children.Add(tabSession);
            segSp.Children.Add(tabOverview);
            segSp.Children.Add(tabControl);
            seg.Child = segSp;
            right.Children.Add(seg);
            g.Children.Add(right);
            StyleTabs();
            return g;
        }

        Border TabButton(string text, string icon, int mode)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            var ic = Glyph(icon, 12, Fg); ic.Margin = new Thickness(0, 0, 7, 0);
            sp.Children.Add(ic);
            sp.Children.Add(Txt(text, 12.5, Fg, FontWeights.SemiBold));
            var b = new Border { Child = sp, Padding = new Thickness(14, 6, 14, 7), CornerRadius = new CornerRadius(7), Cursor = System.Windows.Input.Cursors.Hand };
            b.MouseLeftButtonUp += delegate { overview = mode == 1; control = mode == 2; controlDirty = true; StyleTabs(); dirty = true; resetScroll = true; Render(true); };
            return b;
        }

        void StyleTabs()
        {
            foreach (var pair in new[] { Tuple.Create(tabSession, !overview && !control), Tuple.Create(tabOverview, overview), Tuple.Create(tabControl, control) })
            {
                var on = pair.Item2;
                pair.Item1.Background = on ? Grad(Col(Accent2), Col(Accent), 90) : Brushes.Transparent;
                pair.Item1.Effect = on ? new DropShadowEffect { Color = Col(Accent), BlurRadius = 12, ShadowDepth = 0, Opacity = 0.35 } : null;
                foreach (var tb in ((StackPanel)pair.Item1.Child).Children.OfType<TextBlock>())
                    tb.Foreground = on ? Brushes.White : Muted;
            }
        }

        // ---------- refresh loop ----------
        void Refresh()
        {
            if (busy) return;
            busy = true;
            Task.Factory.StartNew(() => { lock (gate) store.Scan(); })
                .ContinueWith(_ => Dispatcher.BeginInvoke(new Action(() =>
                {
                    busy = false;
                    Render((DateTime.Now - lastRender).TotalSeconds > 10);
                })));
        }

        void Render(bool force)
        {
            lock (gate)
            {
                if (!store.Changed && !dirty && !force) return;
                store.Changed = false; dirty = false; lastRender = DateTime.Now;
                var top = store.Sessions.Values.Where(s => !s.IsSub && s.Calls + s.Prompts > 0)
                    .OrderByDescending(s => s.Live).ThenByDescending(s => s.Last).ToList();
                if (selected == null || !top.Any(s => s.File == selected))
                {
                    var pick = top.FirstOrDefault(s => s.Live && s.LiveStatus == "busy") ?? top.FirstOrDefault();
                    selected = pick != null ? pick.File : null;
                }
                int live = top.Count(s => s.Live), working = top.Count(s => s.Live && s.LiveStatus == "busy");
                statusText.Text = string.Format("{0} live  ·  {1} working  ·  {2:HH:mm:ss}", live, working, DateTime.Now);
                statusDot.Fill = working > 0 ? Accent : live > 0 ? Green : Faint;
                RenderList(top.Take(60).ToList());
                double off = resetScroll ? 0 : mainScroll.VerticalOffset; resetScroll = false;
                Sess sel;
                store.Sessions.TryGetValue(selected ?? "", out sel);
                if (control) { if (controlDirty) { controlDirty = false; mainScroll.Content = RenderControl(top); } }
                else mainScroll.Content = overview ? RenderOverview(top) : (sel != null ? RenderSession(sel) : Empty("No Claude sessions found under ~/.claude/projects yet."));
                mainScroll.UpdateLayout();
                mainScroll.ScrollToVerticalOffset(off);
            }
        }

        // ---------- session list ----------
        void RenderList(List<Sess> items)
        {
            listPanel.Children.Clear();
            var liveItems = items.Where(s => s.Live).ToList();
            var rest = items.Where(s => !s.Live).ToList();
            if (liveItems.Count > 0) { listPanel.Children.Add(GroupHeader("LIVE", liveItems.Count)); foreach (var s in liveItems) listPanel.Children.Add(ListRow(s)); }
            if (rest.Count > 0) { listPanel.Children.Add(GroupHeader("RECENT", rest.Count)); foreach (var s in rest) listPanel.Children.Add(ListRow(s)); }
        }

        static UIElement GroupHeader(string text, int count)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(10, 10, 0, 8) };
            sp.Children.Add(Txt(text, 10.5, Muted, FontWeights.Bold));
            var c = Txt(count.ToString(Inv), 10.5, Faint, FontWeights.SemiBold); c.Margin = new Thickness(6, 0, 0, 0);
            sp.Children.Add(c);
            return sp;
        }

        UIElement ListRow(Sess s)
        {
            var sess = s;
            bool isSel = s.File == selected && !overview && !control;
            var row = new Border
            {
                Padding = new Thickness(10, 9, 10, 9), Margin = new Thickness(0, 0, 0, 3), CornerRadius = new CornerRadius(12),
                Background = isSel ? Grad(WithA(Col(Accent), 0x2A), WithA(Col(Accent), 0x08), 0) : Brushes.Transparent,
                BorderBrush = isSel ? Tint(Accent, 0x55) : Brushes.Transparent, BorderThickness = new Thickness(1),
                Cursor = System.Windows.Input.Cursors.Hand
            };
            if (!isSel)
            {
                row.MouseEnter += delegate { row.Background = BgHover; };
                row.MouseLeave += delegate { row.Background = Brushes.Transparent; };
            }
            row.MouseLeftButtonUp += delegate { selected = sess.File; overview = false; control = false; StyleTabs(); dirty = true; resetScroll = true; Render(true); };
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.Children.Add(Avatar(s));
            var sp = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var t = Txt(s.Title, 13, isSel ? Fg : Fg2, FontWeights.SemiBold);
            t.TextTrimming = TextTrimming.CharacterEllipsis;
            sp.Children.Add(t);
            var sub = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 0) };
            sub.Children.Add(Txt(ShortModel(s), 11.5, Muted, FontWeights.Normal));
            sub.Children.Add(Txt("  ·  " + Ago(s.Last) + "  ·  " + K(s.In + s.Out + s.CC + s.CR), 11.5, Faint, FontWeights.Normal));
            sp.Children.Add(sub);
            Grid.SetColumn(sp, 1);
            g.Children.Add(sp);
            row.Child = g;
            row.ToolTip = s.Title + (s.Cwd != null ? "\n" + s.Cwd : "");
            return row;
        }

        UIElement Avatar(Sess s)
        {
            var c = AvatarColors[Math.Abs(s.Id.GetHashCode()) % AvatarColors.Length];
            var g = new Grid { Width = 34, Height = 34, HorizontalAlignment = HorizontalAlignment.Left };
            var box = new Border { CornerRadius = new CornerRadius(10), Background = Grad(WithA(Col(c), 0x40), WithA(Col(c), 0x18), 135), BorderBrush = Tint(c, 0x40), BorderThickness = new Thickness(1) };
            var letter = (s.Title ?? "?").Trim();
            var tb = Txt(letter.Length > 0 ? char.ToUpper(letter[0]).ToString() : "?", 14, c, FontWeights.Bold);
            tb.HorizontalAlignment = HorizontalAlignment.Center; tb.VerticalAlignment = VerticalAlignment.Center; tb.FontFamily = Display;
            box.Child = tb;
            g.Children.Add(box);
            if (s.State != "Ended")
            {
                var dotWrap = new Border { Width = 13, Height = 13, CornerRadius = new CornerRadius(7), Background = BgSide, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, -3, -3) };
                var d = Dot(StateColor(s.State), s.State == "Working", 8);
                d.HorizontalAlignment = HorizontalAlignment.Center;
                dotWrap.Child = d;
                g.Children.Add(dotWrap);
            }
            return g;
        }

        // ---------- session view ----------
        UIElement RenderSession(Sess s)
        {
            var p = new StackPanel { Margin = new Thickness(30, 24, 30, 30) };

            var title = Txt(s.Title, 26, Fg, FontWeights.SemiBold);
            title.FontFamily = Display; title.TextTrimming = TextTrimming.CharacterEllipsis;
            p.Children.Add(title);
            var meta = new WrapPanel { Margin = new Thickness(0, 10, 0, 18) };
            if (s.Version != null) meta.Children.Add(Chip(IcSettings, "Claude Code " + s.Version, Fg2));
            if (s.Entry != null) meta.Children.Add(Chip(IcPlug, s.Entry, Fg2));
            if (s.Mode != null) meta.Children.Add(Chip(IcCheck, "permissions: " + s.Mode, Yellow));
            if (s.Live) meta.Children.Add(Chip(IcPulse, "pid " + s.Pid, Fg2));
            if (s.Cwd != null)
            {
                var cwdChip = Chip(IcDoc, ShortPath(s.Cwd), Fg2);
                cwdChip.ToolTip = s.Cwd;
                meta.Children.Add(cwdChip);
            }
            p.Children.Add(meta);
            p.Children.Add(SessionActions(s));

            p.Children.Add(Hero(s));

            var subs = store.Sessions.Values.Where(x => x.IsSub && x.ParentId == s.Id).ToList();
            long subTok = subs.Sum(x => x.In + x.Out + x.CC + x.CR);

            var cards = new UniformGrid { Columns = 4, Margin = new Thickness(-7, 14, -7, 0) };
            cards.Children.Add(ContextCard(s));
            cards.Children.Add(TokensCard(s));
            cards.Children.Add(Card("Activity", IcPulse, Blue, QuadStats(new[] {
                Tuple.Create(s.ToolCalls.ToString(Inv), "tool calls", Blue), Tuple.Create(s.Calls.ToString(Inv), "model calls", Fg),
                Tuple.Create(s.Prompts.ToString(Inv), "your prompts", Accent), Tuple.Create(s.Errors.ToString(Inv), "tool errors", s.Errors > 0 ? Red : Faint) }), null));
            var timing = new StackPanel();
            var span = Txt(Dur(s.Last - s.First), 28, Fg, FontWeights.SemiBold); span.FontFamily = Display;
            timing.Children.Add(span);
            timing.Children.Add(Txt("session length", 11.5, Muted, FontWeights.Normal));
            var tl = KvList(new[] {
                KV("Started", s.First == DateTime.MinValue ? "—" : s.First.ToString("MMM d, HH:mm", Inv), Fg2),
                KV("Last activity", Ago(s.Last), Fg2),
                KV("Subagents", subs.Count == 0 ? "none" : subs.Count + " · " + K(subTok), subs.Count > 0 ? Purple : Faint) });
            ((FrameworkElement)tl).Margin = new Thickness(0, 12, 0, 0);
            timing.Children.Add(tl);
            cards.Children.Add(Card("Timing", IcClock, Teal, timing, null));
            p.Children.Add(cards);

            var cols = new Grid { Margin = new Thickness(-7, 0, -7, 0) };
            cols.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.5, GridUnitType.Star) });
            cols.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            cols.Children.Add(Card("Activity timeline", IcHistory, Accent, Timeline(s), "Newest first. Hover a row to see the full text."));

            var right = new StackPanel();
            right.Children.Add(Card("Tools used", IcTool, Blue, Bars(s.Tools.Select(kv => new KeyValuePair<string, long>(kv.Key, kv.Value)), Blue, 12, "No tool calls yet.", true), null));
            right.Children.Add(Card("MCP servers & connectors", IcPuzzle, Purple, Bars(s.Mcp.Select(kv => new KeyValuePair<string, long>(kv.Key, kv.Value)), Purple, 8, "No MCP tools used.", false), null));
            right.Children.Add(Card("Skills invoked", IcBolt, Accent, ChipWrap(s.Skills.OrderByDescending(kv => kv.Value).Select(kv => kv.Key + "  ×" + kv.Value), Accent, "No skills invoked in this session."), null));
            right.Children.Add(Card("Skills available  ·  " + s.SkillList.Count, IcBrain, Yellow, ChipWrap(s.SkillList, Fg2, "Not reported for this session."), "Skills listed to the model at the start of this session."));
            Grid.SetColumn(right, 1);
            cols.Children.Add(right);
            p.Children.Add(cols);
            return p;
        }

        UIElement Hero(Sess s)
        {
            var last = s.Events.LastOrDefault();
            var lastTool = s.Events.LastOrDefault(e => e.Kind == "tool");
            string head, detail, icon;
            Brush c = StateColor(s.State);
            if (s.State == "Working")
            {
                if (last != null && last.Kind == "tool") { head = "Running " + last.Name; detail = string.IsNullOrEmpty(last.Detail) ? "Waiting for the tool to finish…" : last.Detail; icon = ToolIcon(last.Name); }
                else if (last != null && last.Kind == "prompt") { head = "Thinking"; detail = "Reading your message and planning the next step…"; icon = IcBrain; }
                else if (last != null && last.Kind == "error") { head = "Recovering"; detail = "A tool returned an error: " + last.Detail; icon = IcError; }
                else { head = "Writing"; detail = last != null ? last.Detail : "Composing a reply…"; icon = IcChat; }
            }
            else if (s.Live)
            {
                head = "Waiting for you"; icon = IcPause;
                detail = lastTool != null ? "Last used " + lastTool.Name + " " + Ago(lastTool.T) : "Session is open and idle";
            }
            else
            {
                head = s.State == "Active" ? "Recently active" : "Session ended"; icon = s.State == "Active" ? IcPulse : IcStop;
                detail = "Last activity " + Ago(s.Last) + (lastTool != null ? "  ·  last tool " + lastTool.Name : "");
            }

            var bd = new Border
            {
                CornerRadius = new CornerRadius(18), Padding = new Thickness(22, 18, 22, 18), Margin = new Thickness(0, 0, 0, 0),
                Background = Grad(WithA(Col(c), 0x30), WithA(Col(BgCard), 0xFF), 0),
                BorderBrush = Grad(WithA(Col(c), 0x70), WithA(Col(c), 0x10), 0), BorderThickness = new Thickness(1),
                Effect = new DropShadowEffect { Color = Colors.Black, BlurRadius = 24, ShadowDepth = 6, Opacity = 0.35 }
            };
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(72) });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // status orb
            var orb = new Grid { Width = 54, Height = 54, HorizontalAlignment = HorizontalAlignment.Left };
            if (s.State == "Working")
            {
                var ring = new Shapes.Ellipse { Stroke = c, StrokeThickness = 2, RenderTransformOrigin = new Point(0.5, 0.5) };
                var st = new ScaleTransform(1, 1);
                ring.RenderTransform = st;
                var dur = TimeSpan.FromSeconds(1.6);
                st.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, 1.55, dur) { RepeatBehavior = RepeatBehavior.Forever });
                st.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, 1.55, dur) { RepeatBehavior = RepeatBehavior.Forever });
                ring.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0.7, 0, dur) { RepeatBehavior = RepeatBehavior.Forever });
                orb.Children.Add(ring);
            }
            orb.Children.Add(new Shapes.Ellipse { Fill = Grad(WithA(Col(c), 0xFF), WithA(Col(c), 0xB0), 135), Effect = new DropShadowEffect { Color = Col(c), BlurRadius = 20, ShadowDepth = 0, Opacity = 0.55 } });
            var oi = Glyph(icon, 22, B("#1A1917"));
            oi.HorizontalAlignment = HorizontalAlignment.Center; oi.VerticalAlignment = VerticalAlignment.Center;
            orb.Children.Add(oi);
            g.Children.Add(orb);

            var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var lbl = Txt("RIGHT NOW  ·  " + s.State.ToUpperInvariant(), 10.5, c, FontWeights.Bold);
            texts.Children.Add(lbl);
            var h = Txt(head, 21, Fg, FontWeights.SemiBold);
            h.FontFamily = Display; h.TextTrimming = TextTrimming.CharacterEllipsis; h.Margin = new Thickness(0, 3, 0, 2);
            texts.Children.Add(h);
            var d = Txt(detail, 13, Fg2, FontWeights.Normal);
            d.TextTrimming = TextTrimming.CharacterEllipsis; d.ToolTip = detail;
            texts.Children.Add(d);
            Grid.SetColumn(texts, 1);
            g.Children.Add(texts);

            var model = new Border { CornerRadius = new CornerRadius(14), Background = Tint(Fg, 0x08), BorderBrush = Line, BorderThickness = new Thickness(1), Padding = new Thickness(16, 10, 16, 11), Margin = new Thickness(18, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            var ms = new StackPanel();
            ms.Children.Add(Txt("MODEL", 10, Muted, FontWeights.Bold));
            var mn = Txt(s.ModelName ?? PrettyModel(s.Model), 18, Accent2, FontWeights.SemiBold); mn.FontFamily = Display; mn.Margin = new Thickness(0, 2, 0, 1);
            ms.Children.Add(mn);
            var mid = Txt(s.Model ?? "", 11, Faint, FontWeights.Normal); mid.FontFamily = Mono;
            ms.Children.Add(mid);
            model.Child = ms;
            Grid.SetColumn(model, 2);
            g.Children.Add(model);
            bd.Child = g;
            return bd;
        }

        Border ContextCard(Sess s)
        {
            double frac = s.ContextWindow > 0 ? (double)s.LastCtx / s.ContextWindow : 0;
            var ringColor = frac > 0.85 ? Red : frac > 0.6 ? Yellow : Accent;
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.Children.Add(Ring(frac, ringColor, 96));
            var t = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 0, 0) };
            var big = Txt(K(s.LastCtx), 24, Fg, FontWeights.SemiBold); big.FontFamily = Display;
            t.Children.Add(big);
            t.Children.Add(Txt("of " + K(s.ContextWindow) + " tokens", 12, Muted, FontWeights.Normal));
            var left = Txt(K(Math.Max(0, s.ContextWindow - s.LastCtx)) + " left", 11.5, ringColor, FontWeights.SemiBold); left.Margin = new Thickness(0, 8, 0, 0);
            t.Children.Add(left);
            Grid.SetColumn(t, 1);
            g.Children.Add(t);
            return Card("Context window", IcBrain, ringColor, g, "Tokens sent on the latest model call (input + cache). Window size is inferred: 1M if the model id has [1m] or usage went past 200k, otherwise 200k.");
        }

        Border TokensCard(Sess s)
        {
            var sp = new StackPanel();
            long total = s.In + s.Out + s.CC + s.CR;
            var big = Txt(K(total), 28, Fg, FontWeights.SemiBold); big.FontFamily = Display;
            sp.Children.Add(big);
            sp.Children.Add(Txt("tokens processed", 11.5, Muted, FontWeights.Normal));
            var parts = new[] { Tuple.Create("Output", s.Out, Accent), Tuple.Create("Input", s.In, Fg2), Tuple.Create("Cache write", s.CC, Blue), Tuple.Create("Cache read", s.CR, Purple) };
            var bar = new Grid { Height = 8, Margin = new Thickness(0, 12, 0, 10), ClipToBounds = true };
            var clip = new Border { CornerRadius = new CornerRadius(4), Background = LineSoft, Child = bar, Margin = new Thickness(0, 12, 0, 10) };
            bar.Margin = new Thickness(0);
            int col = 0;
            foreach (var pt in parts)
            {
                if (pt.Item2 <= 0) continue;
                // sqrt scale so small parts stay visible next to huge cache reads
                bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Sqrt(pt.Item2), GridUnitType.Star) });
                var seg = new Border { Background = pt.Item3, Margin = new Thickness(0, 0, 2, 0), CornerRadius = new CornerRadius(2) };
                Grid.SetColumn(seg, col++);
                bar.Children.Add(seg);
            }
            sp.Children.Add(clip);
            var legend = new StackPanel();
            foreach (var pt in parts)
            {
                var row = new DockPanel { Margin = new Thickness(0, 1, 0, 1) };
                row.Children.Add(new Border { Width = 8, Height = 8, CornerRadius = new CornerRadius(2), Background = pt.Item3, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center });
                var v = Txt(K(pt.Item2), 12, Fg, FontWeights.SemiBold);
                DockPanel.SetDock(v, Dock.Right);
                row.Children.Add(v);
                row.Children.Add(Txt(pt.Item1, 12, Muted, FontWeights.Normal));
                legend.Children.Add(row);
            }
            sp.Children.Add(legend);
            return Card("Tokens", IcCoins, Accent2, sp, "Bar segments use a square-root scale so small values stay visible next to cache reads.");
        }

        UIElement Timeline(Sess s)
        {
            var sp = new StackPanel();
            var evs = s.Events.AsEnumerable().Reverse().Take(50).ToList();
            if (evs.Count == 0) { sp.Children.Add(Txt("No activity yet.", 12, Muted, FontWeights.Normal)); return sp; }
            DateTime? lastDay = null;
            for (int i = 0; i < evs.Count; i++)
            {
                var e = evs[i];
                if (lastDay != null && lastDay.Value.Date != e.T.Date)
                {
                    var dh = Txt(e.T.ToString("dddd, MMM d", Inv), 11, Muted, FontWeights.SemiBold);
                    dh.Margin = new Thickness(38, 6, 0, 10);
                    sp.Children.Add(dh);
                }
                lastDay = e.T;
                sp.Children.Add(EventRow(e, i == evs.Count - 1));
            }
            return sp;
        }

        UIElement EventRow(Ev e, bool isLast)
        {
            Brush c = e.Kind == "tool" ? Blue : e.Kind == "prompt" ? Accent : e.Kind == "error" ? Red : Green;
            string ic = e.Kind == "tool" ? ToolIcon(e.Name) : e.Kind == "prompt" ? IcPerson : e.Kind == "error" ? IcError : IcChat;
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(38) });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            if (!isLast)
            {
                var line = new Border { Width = 2, Background = LineSoft, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(13, 26, 0, 0) };
                g.Children.Add(line);
            }
            var node = new Border { Width = 28, Height = 28, CornerRadius = new CornerRadius(14), Background = Tint(c, 0x22), BorderBrush = Tint(c, 0x55), BorderThickness = new Thickness(1), VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Left };
            var ni = Glyph(ic, 12, c); ni.HorizontalAlignment = HorizontalAlignment.Center; ni.VerticalAlignment = VerticalAlignment.Center;
            node.Child = ni;
            g.Children.Add(node);

            var body = new Border { Padding = new Thickness(8, 3, 8, 12), CornerRadius = new CornerRadius(8) };
            var bg = new Grid();
            bg.ColumnDefinitions.Add(new ColumnDefinition());
            bg.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            bg.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            bg.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var nm = Txt(e.Name, 13, Fg, FontWeights.SemiBold); nm.TextTrimming = TextTrimming.CharacterEllipsis;
            bg.Children.Add(nm);
            var tm = Txt(e.T.ToString("HH:mm:ss", Inv), 11, Faint, FontWeights.Normal); tm.FontFamily = Mono; tm.Margin = new Thickness(10, 1, 0, 0);
            Grid.SetColumn(tm, 1);
            bg.Children.Add(tm);
            if (!string.IsNullOrEmpty(e.Detail))
            {
                var d = Txt(e.Detail, 12, e.Kind == "error" ? Red : Muted, FontWeights.Normal);
                d.TextTrimming = TextTrimming.CharacterEllipsis; d.Margin = new Thickness(0, 2, 0, 0);
                if (e.Kind == "tool" && LooksLikePath(e.Detail)) { d.FontFamily = Mono; d.FontSize = 11.5; }
                Grid.SetRow(d, 1); Grid.SetColumnSpan(d, 2);
                bg.Children.Add(d);
                body.ToolTip = e.Detail;
            }
            body.Child = bg;
            body.MouseEnter += delegate { body.Background = Tint(Fg, 0x07); };
            body.MouseLeave += delegate { body.Background = Brushes.Transparent; };
            Grid.SetColumn(body, 1);
            g.Children.Add(body);
            return g;
        }

        static bool LooksLikePath(string s) { return s.Contains(":\\") || s.StartsWith("/"); }

        // ---------- overview ----------
        UIElement RenderOverview(List<Sess> top)
        {
            var p = new StackPanel { Margin = new Thickness(30, 24, 30, 30) };
            var h = Txt("Usage across all sessions", 26, Fg, FontWeights.SemiBold); h.FontFamily = Display;
            p.Children.Add(h);

            var note = new Border { CornerRadius = new CornerRadius(10), Background = Tint(Yellow, 0x12), BorderBrush = Tint(Yellow, 0x30), BorderThickness = new Thickness(1), Padding = new Thickness(12, 8, 12, 9), Margin = new Thickness(0, 12, 0, 16) };
            var nsp = new DockPanel();
            var nic = Glyph("", 13, Yellow); nic.Margin = new Thickness(0, 1, 10, 0); nic.VerticalAlignment = VerticalAlignment.Top;
            nsp.Children.Add(nic);
            var nt = Txt("Plan rate limits aren't stored in Claude's local files, so these are rolling-window totals worked out from your session logs (subagents included). Your exact limits are in claude.ai → Settings → Usage.", 12, Fg2, FontWeights.Normal);
            nt.TextWrapping = TextWrapping.Wrap;
            nsp.Children.Add(nt);
            note.Child = nsp;
            p.Children.Add(note);

            var now = DateTime.Now;
            var all = store.Sessions.Values.SelectMany(s => s.Usages).Where(u => u.T != DateTime.MinValue).ToList();
            var h5 = all.Where(u => u.T > now.AddHours(-5)).ToList();
            var d7 = all.Where(u => u.T > now.AddDays(-7)).ToList();
            var today = all.Where(u => u.T.Date == now.Date).ToList();

            var cards = new UniformGrid { Columns = 4, Margin = new Thickness(-7, 0, -7, 0) };
            string ages = h5.Count > 0 ? "oldest call ages out in " + Dur(h5.Min(u => u.T).AddHours(5) - now) : "no calls in this window";
            cards.Children.Add(Card("Last 5 hours", IcClock, Accent, BigStat(K(h5.Sum(u => u.Billable)), "tokens · excl. cache reads", h5.Count + " model calls", ages, Spark(Buckets(all, now.AddHours(-5), TimeSpan.FromMinutes(30), 10), Accent)), "Rolling 5-hour window, the span Claude's session limits use. Sparkline = 30-minute buckets."));
            cards.Children.Add(Card("Today", "", Blue, BigStat(K(today.Sum(u => u.Billable)), "tokens · excl. cache reads", today.Count + " model calls", K(today.Sum(u => u.Out)) + " output tokens", Spark(Buckets(all, now.Date, TimeSpan.FromHours(1), Math.Max(1, now.Hour + 1)), Blue)), "Sparkline = hourly since midnight."));
            cards.Children.Add(Card("Last 7 days", IcChart, Purple, BigStat(K(d7.Sum(u => u.Billable)), "tokens · excl. cache reads", d7.Count + " model calls", d7.Select(u => u.Sid).Distinct().Count() + " sessions", Spark(Buckets(all, now.Date.AddDays(-6), TimeSpan.FromDays(1), 7), Purple)), "Rolling 7-day window, the span Claude's weekly limits use. Sparkline = per day."));
            int live = top.Count(s => s.Live), working = top.Count(s => s.Live && s.LiveStatus == "busy");
            var dots = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
            foreach (var s in top.Where(x => x.Live))
            {
                var d = Dot(StateColor(s.State), s.State == "Working", 12);
                d.Margin = new Thickness(0, 0, 6, 0);
                d.ToolTip = s.Title + " — " + s.State;
                dots.Children.Add(d);
            }
            cards.Children.Add(Card("Right now", IcPulse, Green, BigStat(live.ToString(Inv), "live sessions", working + " working  ·  " + (live - working) + " idle", top.Count + " sessions on disk", dots), null));
            p.Children.Add(cards);

            var chart = Card("Tokens per hour  ·  last 24 hours", IcChart, Accent, HourChart(all, now), "Input + output + cache-write tokens per hour. Hover a bar for details.");
            chart.Margin = new Thickness(0, 7, 0, 7);
            p.Children.Add(chart);

            var tools = new Dictionary<string, long>();
            var mcp = new Dictionary<string, long>();
            var skillsUsed = new Dictionary<string, long>();
            foreach (var s in store.Sessions.Values.Where(x => x.Last > now.AddDays(-7)))
            {
                foreach (var kv in s.Tools) Add(tools, kv.Key, kv.Value);
                foreach (var kv in s.Mcp) Add(mcp, kv.Key, kv.Value);
                foreach (var kv in s.Skills) Add(skillsUsed, kv.Key, kv.Value);
            }

            var cols = new Grid { Margin = new Thickness(-7, 0, -7, 0) };
            cols.ColumnDefinitions.Add(new ColumnDefinition());
            cols.ColumnDefinitions.Add(new ColumnDefinition());
            var left = new StackPanel();
            left.Children.Add(Card("Models  ·  7 days", IcBrain, Accent2,
                Bars(d7.GroupBy(u => PrettyModel(u.Model)).Select(g => new KeyValuePair<string, long>(g.Key, g.Count())), Accent, 8, "No model calls in the last 7 days.", false), "Counted by model calls."));
            left.Children.Add(Card("MCP servers  ·  7 days", IcPuzzle, Purple, Bars(mcp, Purple, 10, "No MCP tools used.", false), null));
            left.Children.Add(Card("Skills invoked  ·  7 days", IcBolt, Accent, Bars(skillsUsed, Accent, 10, "No skills invoked in the last 7 days.", false), null));
            left.Children.Add(Card("Settings", IcSettings, Muted, KvList(store.Settings.Select(kv => KV(kv.Key, kv.Value, Fg2)).ToArray()), "~/.claude/settings.json"));
            cols.Children.Add(left);

            var right = new StackPanel();
            right.Children.Add(Card("Top tools  ·  7 days", IcTool, Blue, Bars(tools, Blue, 14, "No tool calls in the last 7 days.", true), null));
            var sk = new WrapPanel();
            foreach (var si in store.InstalledSkills)
            {
                var c = Chip(IcBolt, si.Name, si.Source == "personal" ? Green : Fg2);
                c.ToolTip = string.IsNullOrEmpty(si.Description) ? si.Name : si.Description;
                sk.Children.Add(c);
            }
            if (store.InstalledSkills.Count == 0) sk.Children.Add(Txt("None found in ~/.claude/skills.", 12, Muted, FontWeights.Normal));
            right.Children.Add(Card("Installed skills  ·  " + store.InstalledSkills.Count, IcBolt, Yellow, sk, "Skills in ~/.claude/skills (green = your own). Hover for descriptions."));
            var pl = new StackPanel();
            foreach (var pi in store.Plugins)
            {
                var row = new Grid { Margin = new Thickness(0, 0, 0, 10) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
                row.ColumnDefinitions.Add(new ColumnDefinition());
                var ib = new Border { Width = 30, Height = 30, CornerRadius = new CornerRadius(8), Background = Tint(Purple, 0x22), VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Left };
                var ii = Glyph(IcPuzzle, 13, Purple); ii.HorizontalAlignment = HorizontalAlignment.Center; ii.VerticalAlignment = VerticalAlignment.Center;
                ib.Child = ii;
                row.Children.Add(ib);
                var tx = new StackPanel();
                tx.Children.Add(Txt(pi.Name + (pi.Version != null ? "   v" + pi.Version : ""), 13, Fg, FontWeights.SemiBold));
                if (!string.IsNullOrEmpty(pi.Description))
                {
                    var d = Txt(pi.Description, 12, Muted, FontWeights.Normal);
                    d.TextWrapping = TextWrapping.Wrap; d.MaxHeight = 34; d.TextTrimming = TextTrimming.CharacterEllipsis; d.ToolTip = pi.Description;
                    tx.Children.Add(d);
                }
                Grid.SetColumn(tx, 1);
                row.Children.Add(tx);
                pl.Children.Add(row);
            }
            if (store.Plugins.Count == 0) pl.Children.Add(Txt("No plugins found.", 12, Muted, FontWeights.Normal));
            right.Children.Add(Card("Plugins  ·  " + store.Plugins.Count, IcPuzzle, Purple, pl, null));
            Grid.SetColumn(right, 1);
            cols.Children.Add(right);
            p.Children.Add(cols);
            return p;
        }

        static void Add(Dictionary<string, long> d, string k, long v) { long o; d.TryGetValue(k, out o); d[k] = o + v; }

        static long[] Buckets(List<Usage> all, DateTime start, TimeSpan step, int n)
        {
            var r = new long[n];
            foreach (var u in all)
            {
                if (u.T < start) continue;
                int i = (int)((u.T - start).Ticks / step.Ticks);
                if (i >= 0 && i < n) r[i] += u.Billable;
            }
            return r;
        }

        static UIElement Spark(long[] v, Brush c)
        {
            long max = Math.Max(1, v.Max());
            var g = new Grid { Height = 32, Margin = new Thickness(0, 14, 0, 0) };
            for (int i = 0; i < v.Length; i++)
            {
                g.ColumnDefinitions.Add(new ColumnDefinition());
                var h = v[i] == 0 ? 3 : Math.Max(5, 32.0 * v[i] / max);
                var b = new Border
                {
                    Height = h, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(1.5, 0, 1.5, 0), CornerRadius = new CornerRadius(2),
                    Background = v[i] == 0 ? LineSoft : (i == v.Length - 1 ? c : Tint(c, 0x80)), ToolTip = K(v[i]) + " tokens"
                };
                Grid.SetColumn(b, i);
                g.Children.Add(b);
            }
            return g;
        }

        UIElement HourChart(List<Usage> all, DateTime now)
        {
            var start = new DateTime(now.Year, now.Month, now.Day, now.Hour, 0, 0).AddHours(-23);
            var buckets = new long[24];
            var calls = new int[24];
            foreach (var u in all)
            {
                if (u.T < start) continue;
                int i = (int)(u.T - start).TotalHours;
                if (i < 0 || i > 23) continue;
                buckets[i] += u.Billable; calls[i]++;
            }
            long max = Math.Max(1, buckets.Max());
            const double H = 150;
            var outer = new StackPanel();
            var head = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
            var tot = Txt(K(buckets.Sum()), 22, Fg, FontWeights.SemiBold); tot.FontFamily = Display;
            head.Children.Add(tot);
            var tl = Txt("  tokens in 24h", 12, Muted, FontWeights.Normal); tl.VerticalAlignment = VerticalAlignment.Bottom; tl.Margin = new Thickness(0, 0, 0, 4);
            head.Children.Add(tl);
            var peak = Txt("peak " + K(max) + "/h", 11.5, Faint, FontWeights.Normal); peak.HorizontalAlignment = HorizontalAlignment.Right; peak.VerticalAlignment = VerticalAlignment.Bottom;
            head.Children.Add(peak);
            outer.Children.Add(head);

            var area = new Grid { Height = H };
            for (int k = 0; k <= 3; k++)
            {
                var gl = new Border { Height = 1, Background = LineSoft, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, H * k / 3) };
                area.Children.Add(gl);
            }
            var g = new Grid();
            var lbl = new Grid { Margin = new Thickness(0, 8, 0, 0) };
            var barBrush = Grad(Col(Accent2), WithA(Col(Accent), 0x90), 90);
            for (int i = 0; i < 24; i++)
            {
                g.ColumnDefinitions.Add(new ColumnDefinition());
                lbl.ColumnDefinitions.Add(new ColumnDefinition());
                var h = buckets[i] == 0 ? 3 : Math.Max(6, H * buckets[i] / max);
                var bar = new Border
                {
                    Height = h, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(3, 0, 3, 0),
                    CornerRadius = new CornerRadius(5, 5, 1, 1), Background = buckets[i] == 0 ? Line : barBrush,
                    ToolTip = string.Format("{0:HH}:00 – {1:HH}:00\n{2} tokens  ·  {3} model calls", start.AddHours(i), start.AddHours(i + 1), K(buckets[i]), calls[i])
                };
                if (i == 23 && buckets[i] > 0) bar.Effect = new DropShadowEffect { Color = Col(Accent), BlurRadius = 16, ShadowDepth = 0, Opacity = 0.6 };
                Grid.SetColumn(bar, i);
                g.Children.Add(bar);
                if (i % 3 == 2 || i == 23)
                {
                    var t = Txt(i == 23 ? "now" : start.AddHours(i).ToString("HH:00", Inv), 10.5, i == 23 ? Accent : Faint, FontWeights.Normal);
                    t.HorizontalAlignment = HorizontalAlignment.Center;
                    Grid.SetColumn(t, i);
                    lbl.Children.Add(t);
                }
            }
            area.Children.Add(g);
            outer.Children.Add(area);
            outer.Children.Add(lbl);
            return outer;
        }

        // ---------- widgets ----------
        static TextBlock Txt(string s, double size, Brush c, FontWeight w)
        {
            return new TextBlock { Text = s ?? "", FontSize = size, Foreground = c, FontWeight = w };
        }

        static TextBlock Glyph(string glyph, double size, Brush c)
        {
            return new TextBlock { Text = glyph, FontFamily = IconFont, FontSize = size, Foreground = c };
        }

        static Border Card(string title, string icon, Brush accent, UIElement content, string tip)
        {
            var sp = new StackPanel();
            var head = new DockPanel { Margin = new Thickness(0, 0, 0, 14) };
            var ib = new Border { Width = 26, Height = 26, CornerRadius = new CornerRadius(8), Background = Tint(accent, 0x22), Margin = new Thickness(0, 0, 10, 0) };
            var ic = Glyph(icon, 12, accent); ic.HorizontalAlignment = HorizontalAlignment.Center; ic.VerticalAlignment = VerticalAlignment.Center;
            ib.Child = ic;
            head.Children.Add(ib);
            var h = Txt(title, 13, Fg2, FontWeights.SemiBold); h.VerticalAlignment = VerticalAlignment.Center;
            head.Children.Add(h);
            if (tip != null)
            {
                var info = Glyph("", 12, Faint);
                info.HorizontalAlignment = HorizontalAlignment.Right; info.VerticalAlignment = VerticalAlignment.Center; info.ToolTip = tip; info.Cursor = System.Windows.Input.Cursors.Help;
                head.Children.Add(info);
            }
            sp.Children.Add(head);
            sp.Children.Add(content);
            return new Border
            {
                Background = Grad(Col(BgCard), Col(BgCard2), 90), CornerRadius = new CornerRadius(16), Padding = new Thickness(18, 16, 18, 18), Margin = new Thickness(7),
                Child = sp, BorderBrush = Grad(C("#34312C"), C("#1F1E1B"), 90), BorderThickness = new Thickness(1)
            };
        }

        static Tuple<string, string, Brush> KV(string k, string v, Brush c) { return Tuple.Create(k, v, c); }

        static UIElement KvList(Tuple<string, string, Brush>[] rows)
        {
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            for (int i = 0; i < rows.Length; i++)
            {
                g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var k = Txt(rows[i].Item1, 12.5, Muted, FontWeights.Normal);
                k.Margin = new Thickness(0, 3, 14, 3);
                Grid.SetRow(k, i);
                g.Children.Add(k);
                var v = Txt(rows[i].Item2, 12.5, rows[i].Item3, FontWeights.SemiBold);
                v.HorizontalAlignment = HorizontalAlignment.Right; v.Margin = new Thickness(0, 3, 0, 3);
                v.TextTrimming = TextTrimming.CharacterEllipsis; v.ToolTip = rows[i].Item2;
                Grid.SetRow(v, i); Grid.SetColumn(v, 1);
                g.Children.Add(v);
            }
            return g;
        }

        static UIElement QuadStats(Tuple<string, string, Brush>[] items)
        {
            var g = new UniformGrid { Columns = 2 };
            foreach (var it in items)
            {
                var sp = new StackPanel { Margin = new Thickness(0, 0, 8, 12) };
                var n = Txt(it.Item1, 24, it.Item3, FontWeights.SemiBold); n.FontFamily = Display;
                sp.Children.Add(n);
                sp.Children.Add(Txt(it.Item2, 11.5, Muted, FontWeights.Normal));
                g.Children.Add(sp);
            }
            return g;
        }

        static UIElement BigStat(string big, string unit, string line1, string line2, UIElement extra)
        {
            var sp = new StackPanel();
            var b = Txt(big, 30, Fg, FontWeights.SemiBold); b.FontFamily = Display;
            sp.Children.Add(b);
            sp.Children.Add(Txt(unit, 11.5, Muted, FontWeights.Normal));
            if (extra != null) sp.Children.Add(extra);
            var a = Txt(line1, 12, Fg2, FontWeights.SemiBold); a.Margin = new Thickness(0, 12, 0, 1); a.TextTrimming = TextTrimming.CharacterEllipsis;
            sp.Children.Add(a);
            var c = Txt(line2, 11.5, Faint, FontWeights.Normal); c.TextTrimming = TextTrimming.CharacterEllipsis;
            sp.Children.Add(c);
            return sp;
        }

        static UIElement Bars(IEnumerable<KeyValuePair<string, long>> data, Brush color, int maxRows, string empty, bool toolNames)
        {
            var list = data.OrderByDescending(kv => kv.Value).ToList();
            var sp = new StackPanel();
            if (list.Count == 0) { sp.Children.Add(Txt(empty, 12, Muted, FontWeights.Normal)); return sp; }
            long max = list[0].Value;
            var fill = Grad(WithA(Col(color), 0xFF), WithA(Col(color), 0x99), 0);
            foreach (var kv in list.Take(maxRows))
            {
                var label = toolNames ? Store.ToolLabel(kv.Key) : kv.Key;
                var g = new Grid { Margin = new Thickness(0, 0, 0, 10), ToolTip = label + ": " + kv.Value };
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                g.ColumnDefinitions.Add(new ColumnDefinition());
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                if (toolNames)
                {
                    var ic = Glyph(ToolIcon(label), 11, color); ic.Margin = new Thickness(0, 1, 7, 0); ic.VerticalAlignment = VerticalAlignment.Center;
                    g.Children.Add(ic);
                }
                var lbl = Txt(label, 12.5, Fg2, FontWeights.Normal);
                lbl.TextTrimming = TextTrimming.CharacterEllipsis;
                Grid.SetColumn(lbl, 1);
                g.Children.Add(lbl);
                var val = Txt(K(kv.Value), 12.5, Fg, FontWeights.SemiBold);
                val.Margin = new Thickness(8, 0, 0, 0);
                Grid.SetColumn(val, 2);
                g.Children.Add(val);
                var track = new Grid { Height = 6, Margin = new Thickness(0, 6, 0, 0) };
                Grid.SetRow(track, 1); Grid.SetColumnSpan(track, 3);
                double f = (double)kv.Value / max;
                track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0.02, f), GridUnitType.Star) });
                track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0.0001, 1 - f), GridUnitType.Star) });
                var restB = new Border { Background = LineSoft, CornerRadius = new CornerRadius(3) };
                Grid.SetColumnSpan(restB, 2);
                track.Children.Add(restB);
                track.Children.Add(new Border { Background = fill, CornerRadius = new CornerRadius(3) });
                g.Children.Add(track);
                sp.Children.Add(g);
            }
            if (list.Count > maxRows) sp.Children.Add(Txt("+ " + (list.Count - maxRows) + " more", 11.5, Faint, FontWeights.Normal));
            return sp;
        }

        static Border Chip(string icon, string text, Brush c)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            if (icon != null)
            {
                var ic = Glyph(icon, 11, c == Fg2 ? Muted : c); ic.Margin = new Thickness(0, 1, 7, 0); ic.VerticalAlignment = VerticalAlignment.Center;
                sp.Children.Add(ic);
            }
            sp.Children.Add(Txt(text, 12, c, FontWeights.Normal));
            return new Border
            {
                Child = sp, Padding = new Thickness(10, 4, 11, 5), Margin = new Thickness(0, 0, 6, 6), CornerRadius = new CornerRadius(14),
                Background = c == Fg2 ? Tint(Fg, 0x08) : Tint(c, 0x18), BorderBrush = c == Fg2 ? Line : Tint(c, 0x40), BorderThickness = new Thickness(1)
            };
        }

        static UIElement ChipWrap(IEnumerable<string> items, Brush c, string empty)
        {
            var w = new WrapPanel();
            foreach (var i in items) w.Children.Add(Chip(null, i, c));
            if (w.Children.Count == 0) w.Children.Add(Txt(empty, 12, Muted, FontWeights.Normal));
            return w;
        }

        static FrameworkElement Dot(Brush c, bool pulse, double size)
        {
            var e = new Shapes.Ellipse { Width = size, Height = size, Fill = c, VerticalAlignment = VerticalAlignment.Center };
            if (pulse)
            {
                e.Effect = new DropShadowEffect { Color = Col(c), BlurRadius = 10, ShadowDepth = 0, Opacity = 0.9 };
                e.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, 0.35, TimeSpan.FromSeconds(0.8)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
            }
            return e;
        }

        static UIElement Ring(double frac, Brush c, double size)
        {
            frac = Math.Max(0, Math.Min(0.9999, frac));
            double th = 10, r = (size - th) / 2, cx = size / 2;
            var g = new Grid { Width = size, Height = size };
            g.Children.Add(new Shapes.Ellipse { Stroke = LineSoft, StrokeThickness = th });
            if (frac > 0.001)
            {
                double a = frac * 2 * Math.PI;
                var fig = new PathFigure { StartPoint = new Point(cx, cx - r), IsClosed = false };
                fig.Segments.Add(new ArcSegment(new Point(cx + r * Math.Sin(a), cx - r * Math.Cos(a)), new Size(r, r), 0, frac > 0.5, SweepDirection.Clockwise, true));
                var geo = new PathGeometry();
                geo.Figures.Add(fig);
                var col = Col(c);
                g.Children.Add(new Shapes.Path
                {
                    Data = geo, Stroke = Grad(Color.FromRgb((byte)Math.Min(255, col.R + 30), (byte)Math.Min(255, col.G + 40), (byte)Math.Min(255, col.B + 20)), col, 45),
                    StrokeThickness = th, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
                    Effect = new DropShadowEffect { Color = col, BlurRadius = 12, ShadowDepth = 0, Opacity = 0.45 }
                });
            }
            var t = Txt(Math.Round(frac * 100).ToString(Inv) + "%", 19, Fg, FontWeights.SemiBold);
            t.FontFamily = Display; t.HorizontalAlignment = HorizontalAlignment.Center; t.VerticalAlignment = VerticalAlignment.Center;
            g.Children.Add(t);
            return g;
        }

        static UIElement Empty(string msg)
        {
            var t = Txt(msg, 14, Muted, FontWeights.Normal);
            t.Margin = new Thickness(30);
            return t;
        }

        // ---------- formatting ----------
        static string ToolIcon(string name)
        {
            var n = (name ?? "").ToLowerInvariant();
            if (n.Contains("bash") || n.Contains("powershell") || n.Contains("terminal")) return IcTerminal;
            if (n == "read" || n.Contains("read_page") || n.Contains("get_page_text") || n.Contains(" read")) return IcDoc;
            if (n == "edit" || n == "write" || n.Contains("notebookedit")) return IcEdit;
            if (n.Contains("grep") || n.Contains("glob") || n.Contains("search") || n.Contains("find")) return IcSearch;
            if (n.Contains("web") || n.Contains("browser") || n.Contains("chrome") || n.Contains("navigate")) return IcGlobe;
            if (n == "agent" || n == "task" || n.Contains("session")) return IcRobot;
            if (n == "skill") return IcBolt;
            if (n.Contains("plan") || n.Contains("wrap_up") || n.Contains("todo")) return IcList;
            if (n.Contains("computer") || n.Contains("screenshot")) return "";
            if (n.Contains(" · ")) return IcPuzzle;
            return IcTool;
        }
        static Brush StateColor(string st)
        {
            switch (st)
            {
                case "Working": return Accent;
                case "Idle": case "Open": return Green;
                case "Active": return Blue;
                case "Ended": return Faint;
                default: return Yellow;
            }
        }
        static string ShortPath(string p)
        {
            if (p.Length <= 48) return p;
            var parts = p.Split('\\', '/');
            return parts.Length > 2 ? parts[0] + "\\…\\" + parts[parts.Length - 1] : p.Substring(0, 45) + "…";
        }
        static string PrettyModel(string m)
        {
            if (string.IsNullOrEmpty(m)) return "Unknown model";
            var parts = m.Replace("claude-", "").Split('-');
            if (parts.Length >= 3)
            {
                int a, b;
                if (int.TryParse(parts[1], out a) && int.TryParse(parts[2], out b))
                    return char.ToUpper(parts[0][0]) + parts[0].Substring(1) + " " + a + "." + b;
            }
            return m;
        }
        static string ShortModel(Sess s) { return s.ModelName ?? PrettyModel(s.Model); }
        static string K(long n)
        {
            if (n >= 1000000000) return (n / 1e9).ToString("0.0#", Inv) + "B";
            if (n >= 1000000) return (n / 1e6).ToString("0.0#", Inv) + "M";
            if (n >= 1000) return (n / 1e3).ToString("0.#", Inv) + "k";
            return n.ToString(Inv);
        }
        static string Ago(DateTime t)
        {
            if (t == DateTime.MinValue) return "never";
            var d = DateTime.Now - t;
            if (d.TotalSeconds < 60) return Math.Max(0, (int)d.TotalSeconds) + "s ago";
            if (d.TotalMinutes < 60) return (int)d.TotalMinutes + "m ago";
            if (d.TotalHours < 24) return (int)d.TotalHours + "h ago";
            return (int)d.TotalDays + "d ago";
        }
        static string Dur(TimeSpan d)
        {
            if (d.TotalSeconds < 0) d = TimeSpan.Zero;
            if (d.TotalMinutes < 1) return (int)d.TotalSeconds + "s";
            if (d.TotalHours < 1) return (int)d.TotalMinutes + "m";
            if (d.TotalDays < 1) return (int)d.TotalHours + "h " + d.Minutes + "m";
            return (int)d.TotalDays + "d " + d.Hours + "h";
        }
    }

    public static class Program
    {
        [STAThread]
        public static void Main()
        {
            var app = new Application();
            try { app.Resources.MergedDictionaries.Add(MainWindow.Theme()); } catch { }
            app.DispatcherUnhandledException += (s, e) =>
            {
                MessageBox.Show(e.Exception.ToString(), "Claude Monitor error");
                e.Handled = true;
            };
            app.Run(new MainWindow());
        }
    }
}
