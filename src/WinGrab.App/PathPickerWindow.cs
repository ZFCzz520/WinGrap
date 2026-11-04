using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace WinGrab.App;

/// <summary>In-app directory / ffmpeg.exe picker; never invokes a system dialog.</summary>
public sealed class PathPickerWindow : DialogWindow
{
    readonly bool folder;
    readonly TextBox location = new() { Name = "PathInput", MinWidth = 80 };
    readonly ListBox entries = new() { Name = "PathEntries", BorderThickness = new Thickness(0), Background = System.Windows.Media.Brushes.White };
    readonly TextBlock error = new() { Name = "PathError", TextWrapping = TextWrapping.Wrap, Foreground = InkBrush };
    readonly Border errorFlag;
    readonly ComboBox drives = new() { Name = "DriveSelector", MinWidth = 88 };
    string currentDirectory = "";
    public string SelectedPath { get; private set; } = "";

    public PathPickerWindow(string initialPath, bool folder)
    {
        this.folder = folder;
        Title = folder ? "选择保存目录" : "选择 ffmpeg.exe";
        Eyebrow = folder ? "// SAVE DIRECTORY" : "// FFMPEG";
        Width = 760; Height = 560;MinWidth=600;MinHeight=420;
        var layout = new Grid { Margin = new Thickness(20) };
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition());
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var navigation = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
        var up = MakeButton("上级", "ParentButton"); up.Margin = new Thickness(0, 0, 8, 0);
        up.Click += (_, _) => { try { var parent = Directory.GetParent(currentDirectory); if (parent != null) Navigate(parent.FullName); } catch (Exception ex) when (IsPathError(ex)) { SetError(ex); } };
        DockPanel.SetDock(up, Dock.Left); navigation.Children.Add(up);
        DockPanel.SetDock(drives, Dock.Left); drives.Margin = new Thickness(0, 0, 8, 0); navigation.Children.Add(drives);
        foreach (string drive in Directory.GetLogicalDrives()) drives.Items.Add(drive);
        drives.SelectionChanged += (_, _) => { if (drives.SelectedItem is string drive && !currentDirectory.StartsWith(drive, StringComparison.OrdinalIgnoreCase)) Navigate(drive); };
        var go = MakeButton("定位", "NavigateButton"); go.Click += (_, _) => NavigateInput();
        DockPanel.SetDock(go, Dock.Right); navigation.Children.Add(go); navigation.Children.Add(location);
        location.KeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; NavigateInput(); } };
        layout.Children.Add(navigation);
        var hint = new TextBlock { Text = folder ? "双击进入目录，选择当前目录或列表中的目录。" : "双击进入目录，选择 ffmpeg.exe。", Margin = new Thickness(0, 0, 0, 12), TextWrapping = TextWrapping.Wrap };
        Grid.SetRow(hint, 1); layout.Children.Add(hint);
        var entryText=new FrameworkElementFactory(typeof(TextBlock));
        entryText.SetBinding(TextBlock.TextProperty,new System.Windows.Data.Binding(nameof(Entry.Label)));
        entryText.SetValue(TextBlock.TextTrimmingProperty,TextTrimming.CharacterEllipsis);
        entries.ItemTemplate=new DataTemplate{VisualTree=entryText};
        ScrollViewer.SetHorizontalScrollBarVisibility(entries,ScrollBarVisibility.Disabled);
        entries.MouseDoubleClick += (_, e) =>
        {
            // Only a double click on an actual row navigates; blank list space does not.
            if (ItemsControl.ContainerFromElement(entries, e.OriginalSource as DependencyObject) is ListBoxItem) OpenSelection();
        };
        entries.KeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; OpenSelection(); } };
        var rowStyle = new Style(typeof(ListBoxItem));
        rowStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(12, 10, 12, 10)));
        rowStyle.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        var rowBorder = new FrameworkElementFactory(typeof(Border));
        rowBorder.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        rowBorder.SetBinding(Border.PaddingProperty, new System.Windows.Data.Binding("Padding") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        var rowContent = new FrameworkElementFactory(typeof(ContentPresenter));
        rowContent.SetBinding(ContentPresenter.ContentProperty, new System.Windows.Data.Binding("Content") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        rowContent.SetBinding(ContentPresenter.ContentTemplateProperty, new System.Windows.Data.Binding("ContentTemplate") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        rowBorder.AppendChild(rowContent);
        rowStyle.Setters.Add(new Setter(Control.TemplateProperty, new ControlTemplate(typeof(ListBoxItem)) { VisualTree = rowBorder }));
        var selected = new Trigger { Property = ListBoxItem.IsSelectedProperty, Value = true };
        selected.Setters.Add(new Setter(Control.BackgroundProperty, AccentBrush));
        selected.Setters.Add(new Setter(Control.ForegroundProperty, InkBrush));
        rowStyle.Triggers.Add(selected); entries.ItemContainerStyle = rowStyle;
        Grid.SetRow(entries, 2); layout.Children.Add(entries);
        errorFlag = new Border { Background = AccentBrush, Padding = new Thickness(10), Margin = new Thickness(0, 12, 0, 0), Child = error, Visibility = Visibility.Collapsed };
        Grid.SetRow(errorFlag, 3); layout.Children.Add(errorFlag);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        var cancel = MakeButton("取消", "CancelButton"); cancel.IsCancel = true; cancel.Click += (_, _) => Close(); actions.Children.Add(cancel);
        var choose = MakeButton(folder ? "选择目录" : "选择文件", "SelectPathButton", true);
        choose.Click += (_, _) => Choose(); actions.Children.Add(choose);
        Grid.SetRow(actions, 4); layout.Children.Add(actions); Body = layout;
        string start = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string? initialFile = null;
        if (!string.IsNullOrWhiteSpace(initialPath))
        {
            try
            {
                string candidate = Path.GetFullPath(Environment.ExpandEnvironmentVariables(initialPath.Trim().Trim('"')));
                if (File.Exists(candidate)) { initialFile = candidate; candidate = Path.GetDirectoryName(candidate)!; }
                while (!Directory.Exists(candidate) && Directory.GetParent(candidate) is { } parent) candidate = parent.FullName;
                if (Directory.Exists(candidate)) start = candidate;
            }
            catch (Exception ex) when (IsPathError(ex)) { }
        }
        Navigate(start);
        if (!folder && initialFile != null) entries.SelectedItem = entries.Items.Cast<Entry>().FirstOrDefault(e => string.Equals(e.Path, initialFile, StringComparison.OrdinalIgnoreCase));
        ContentRendered += (_, _) => { location.Focus(); location.SelectAll(); };
    }

    void NavigateInput()
    {
        try
        {
            string path = ResolveInput();
            if (!folder && File.Exists(path))
            {
                Navigate(Path.GetDirectoryName(path)!);
                entries.SelectedItem = entries.Items.Cast<Entry>().FirstOrDefault(e => string.Equals(e.Path, path, StringComparison.OrdinalIgnoreCase));
            }
            else Navigate(path);
        }
        catch (Exception ex) when (IsPathError(ex)) { SetError(ex); }
    }

    string ResolveInput() => Path.GetFullPath(Environment.ExpandEnvironmentVariables(location.Text.Trim().Trim('"')), string.IsNullOrEmpty(currentDirectory) ? Environment.CurrentDirectory : currentDirectory);

    void Navigate(string path)
    {
        try
        {
            string full = Path.GetFullPath(path);
            // Materialize before replacing the visible listing so inaccessible paths preserve navigation.
            var listing = Directory.EnumerateDirectories(full).Select(p => new Entry(p, true))
                .Concat(folder ? Enumerable.Empty<Entry>() : Directory.EnumerateFiles(full, "*").Where(p => string.Equals(Path.GetFileName(p), "ffmpeg.exe", StringComparison.OrdinalIgnoreCase)).Select(p => new Entry(p, false)))
                .OrderByDescending(e => e.Directory).ThenBy(e => e.Label, StringComparer.CurrentCultureIgnoreCase).ToArray();
            currentDirectory = full; location.Text = full; entries.ItemsSource = listing;
            drives.SelectedItem = Path.GetPathRoot(full); ShowError("");
        }
        catch (Exception ex) when (IsPathError(ex)) { SetError(ex); }
    }

    void OpenSelection()
    {
        if (entries.SelectedItem is not Entry entry) return;
        if (entry.Directory) Navigate(entry.Path); else Choose();
    }

    void Choose()
    {
        try
        {
            string input = ResolveInput();
            // A manually edited path takes precedence over an old selected row.
            string candidate = string.Equals(input.TrimEnd(Path.DirectorySeparatorChar), currentDirectory.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)
                ? (entries.SelectedItem as Entry)?.Path ?? currentDirectory : input;
            if (folder ? !Directory.Exists(candidate) : !File.Exists(candidate) || !string.Equals(Path.GetFileName(candidate), "ffmpeg.exe", StringComparison.OrdinalIgnoreCase))
            {
                ShowError(folder ? "请选择存在的目录，或输入目录路径后定位。" : "请选择存在的 ffmpeg.exe 文件。");
                return;
            }
            SelectedPath = Path.GetFullPath(candidate); DialogResult = true;
        }
        catch (Exception ex) when (IsPathError(ex)) { SetError(ex); }
    }

    void ShowError(string message) { error.Text = message; errorFlag.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible; }
    void SetError(Exception ex) => ShowError(ex is UnauthorizedAccessException ? "无法访问此目录，请选择其他位置。" : "无法打开此路径，请检查位置是否存在且可访问。");
    static bool IsPathError(Exception ex) => ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException;
    sealed record Entry(string Path, bool Directory)
    {
        public string Label => (Directory ? "[目录]  " : "[文件]  ") + System.IO.Path.GetFileName(Path.TrimEnd(System.IO.Path.DirectorySeparatorChar));
    }
}
