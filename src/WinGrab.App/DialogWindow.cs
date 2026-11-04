using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;

namespace WinGrab.App;

/// <summary>Shared, resizable dialog shell. Safe before Application resources have loaded.</summary>
public class DialogWindow : Window
{
    internal static readonly Brush InkBrush = new SolidColorBrush(Color.FromRgb(22, 22, 22));
    internal static readonly Brush PaperBrush = new SolidColorBrush(Color.FromRgb(244, 244, 242));
    internal static readonly Brush AccentBrush = new SolidColorBrush(Color.FromRgb(255, 250, 0));
    readonly ContentControl bodyHost = new();
    readonly TextBlock eyebrowLabel;
    public UIElement? Body { get => bodyHost.Content as UIElement; set => bodyHost.Content = value; }
    public string Eyebrow { get => eyebrowLabel.Text; set => eyebrowLabel.Text = value; }
    public ContentControl ContentHost => bodyHost;

    public DialogWindow()
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.CanResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Background = PaperBrush; Foreground = InkBrush;
        FontFamily = new FontFamily("Microsoft YaHei UI"); FontSize = 13;
        Width = 600; Height = 400; MinWidth = 320; MinHeight = 230;
        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = 64, ResizeBorderThickness = new Thickness(6),
            GlassFrameThickness = new Thickness(0), CornerRadius = new CornerRadius(0),
            UseAeroCaptionButtons = false
        });
        // A local fallback also handles errors raised before App.InitializeComponent.
        if(Application.Current?.TryFindResource(typeof(Button)) is not System.Windows.Style)
            Resources[typeof(Button)] = CreateFallbackButtonStyle();
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(65) });
        root.RowDefinitions.Add(new RowDefinition());
        var header = new Grid { Margin = new Thickness(18, 0, 0, 0) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var icon = new Border { Background = InkBrush, Width = 28, Height = 28,
            Child = new TextBlock { Text = "B", Foreground = Brushes.White, FontSize = 22,
                FontWeight = FontWeights.Black, HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center } };
        try
        {
            icon.Background = Brushes.Transparent;
            icon.Child = new Image { Source = BitmapFrame.Create(new Uri("pack://application:,,,/WinGrab;component/Assets/WinGrab.png")), Stretch = Stretch.Uniform };
            Icon = BitmapFrame.Create(new Uri("pack://application:,,,/WinGrab;component/Assets/WinGrab.ico"));
        }
        catch (Exception ex) when (ex is System.IO.IOException or NotSupportedException or System.IO.FileFormatException)
        {
            icon.Background = InkBrush;
            icon.Child = new TextBlock { Text = "B", Foreground = Brushes.White, FontSize = 22, FontWeight = FontWeights.Black, HorizontalAlignment = HorizontalAlignment.Center };
        }
        header.Children.Add(icon);
        var labels = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
        eyebrowLabel = new TextBlock { Text = "// WINGRAB", FontSize = 10, Foreground = InkBrush,
            FontFamily = Application.Current?.TryFindResource("WideFont") as FontFamily ?? new FontFamily("Segoe UI") };
        labels.Children.Add(eyebrowLabel);
        var title = new TextBlock { FontSize = 18, FontWeight = FontWeights.Bold, TextTrimming = TextTrimming.CharacterEllipsis };
        title.SetBinding(TextBlock.TextProperty, new Binding(nameof(Title)) { Source = this });
        labels.Children.Add(title);
        var titleBar=new Grid{Width=160,Height=3,HorizontalAlignment=HorizontalAlignment.Left,Margin=new Thickness(0,3,0,0)};
        for(int i=0;i<3;i++)titleBar.ColumnDefinitions.Add(new ColumnDefinition());
        var colors=new[]{Color.FromRgb(217,45,140),Color.FromRgb(255,250,0),Color.FromRgb(54,167,136)};
        for(int i=0;i<colors.Length;i++){var stripe=new Border{Background=new SolidColorBrush(colors[i])};Grid.SetColumn(stripe,i);titleBar.Children.Add(stripe);}
        labels.Children.Add(titleBar); Grid.SetColumn(labels, 1); header.Children.Add(labels);
        var captions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top };
        var maximize = MakeButton("□", "MaximizeButton");
        maximize.SetBinding(VisibilityProperty, new Binding(nameof(ResizeMode)) { Source = this, Converter = new ResizeVisibilityConverter() });
        maximize.ToolTip = "最大化 / 还原";
        maximize.Click += (_, _) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        var close = MakeButton("×", "CloseButton"); close.ToolTip = "关闭"; close.Click += (_, _) => Close();
        foreach (var button in new[] { maximize, close })
        {
            if (Application.Current?.TryFindResource("CaptionButton") is Style captionStyle) button.Style = captionStyle;
            button.Width = 42; button.Height = 36; button.Padding = new Thickness(0);
            button.Margin = new Thickness(0); button.Background = Brushes.Transparent;
            button.BorderBrush = Brushes.Transparent;
            System.Windows.Automation.AutomationProperties.SetName(button, button == close ? "关闭" : "最大化或还原");
            WindowChrome.SetIsHitTestVisibleInChrome(button, true); captions.Children.Add(button);
        }
        Grid.SetColumn(captions, 2); header.Children.Add(captions);
        var headerRule = new Border { BorderBrush = InkBrush, BorderThickness = new Thickness(0, 0, 0, 1), Child = header };
        root.Children.Add(headerRule);
        Grid.SetRow(bodyHost, 1); root.Children.Add(bodyHost);
        Content = new Border { BorderBrush = InkBrush, BorderThickness = new Thickness(1), Child = root };
        KeyboardNavigation.SetTabNavigation(this, KeyboardNavigationMode.Cycle);
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; Close(); } };
        SourceInitialized += (_, _) =>
        {
            var area = SystemParameters.WorkArea;
            MinWidth = Math.Min(MinWidth, area.Width); MinHeight = Math.Min(MinHeight, area.Height);
            MaxWidth = area.Width; MaxHeight = area.Height;
            Width = Math.Min(Width, MaxWidth); Height = Math.Min(Height, MaxHeight);
        };
    }

    sealed class ResizeVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) =>
            value is ResizeMode.CanResize or ResizeMode.CanResizeWithGrip ? Visibility.Visible : Visibility.Collapsed;
        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) => Binding.DoNothing;
    }

    public Button MakeButton(string label, string? name = null, bool primary = false)
    {
        var button = new Button { Content = label, Name = name ?? "", Padding = new Thickness(16, 10, 16, 10),
            MinHeight = 38, Margin = new Thickness(8, 0, 0, 0), Foreground = primary ? Brushes.White : InkBrush,
            Background = primary ? InkBrush : new SolidColorBrush(Color.FromRgb(228, 228, 224)) };
        var appStyle = Application.Current?.TryFindResource(primary ? "Primary" : typeof(Button)) as Style;
        if (appStyle != null) button.Style = appStyle;
        return button;
    }

    static Style CreateFallbackButtonStyle()
    {
        var style = new Style(typeof(Button));
        style.Setters.Add(new Setter(Control.ForegroundProperty, InkBrush));
        style.Setters.Add(new Setter(Control.BackgroundProperty, PaperBrush));
        style.Setters.Add(new Setter(Control.BorderBrushProperty, InkBrush));
        style.Setters.Add(new Setter(Control.CursorProperty, Cursors.Hand));
        var border = new FrameworkElementFactory(typeof(CutBorder), "Frame");
        border.SetBinding(Border.BackgroundProperty, new Binding(nameof(Control.Background)) { RelativeSource = RelativeSource.TemplatedParent });
        border.SetBinding(Border.BorderBrushProperty, new Binding(nameof(Control.BorderBrush)) { RelativeSource = RelativeSource.TemplatedParent });
        border.SetBinding(Border.PaddingProperty, new Binding(nameof(Control.Padding)) { RelativeSource = RelativeSource.TemplatedParent });
        border.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        var content = new FrameworkElementFactory(typeof(ContentPresenter), "Label");
        content.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        content.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        content.SetBinding(ContentPresenter.ContentProperty, new Binding(nameof(ContentControl.Content)) { RelativeSource = RelativeSource.TemplatedParent });
        border.AppendChild(content);
        var template = new ControlTemplate(typeof(Button)) { VisualTree = border };
        foreach (var property in new[] { UIElement.IsMouseOverProperty, UIElement.IsKeyboardFocusedProperty })
        {
            var trigger = new Trigger { Property = property, Value = true };
            trigger.Setters.Add(new Setter(Border.BackgroundProperty, AccentBrush, "Frame"));
            trigger.Setters.Add(new Setter(TextElement.ForegroundProperty, InkBrush, "Label"));
            template.Triggers.Add(trigger);
        }
        style.Setters.Add(new Setter(Control.TemplateProperty, template));
        return style;
    }
}
