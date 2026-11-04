using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace WinGrab.App;
public enum SaveChangesChoice{Save,Discard,Cancel}

public static class ThemedDialog
{
    public static SaveChangesChoice AskSaveChanges(Window owner)
    {
        var result=SaveChangesChoice.Cancel;
        var window=new DialogWindow{Title="保存设置",Eyebrow="// UNSAVED SETTINGS",Width=560,Height=280,ResizeMode=ResizeMode.NoResize};
        if(owner.IsVisible)window.Owner=owner;else window.WindowStartupLocation=WindowStartupLocation.CenterScreen;
        var layout=new Grid{Margin=new Thickness(24)};layout.RowDefinitions.Add(new RowDefinition());layout.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});
        layout.Children.Add(new TextBlock{Text="设置有未保存的修改。\n是否保存后再离开？",FontSize=15,TextWrapping=TextWrapping.Wrap});
        var actions=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right};Grid.SetRow(actions,1);layout.Children.Add(actions);
        var cancel=window.MakeButton("继续编辑","ContinueEditingButton");cancel.IsCancel=true;cancel.Click+=(_,_)=>window.DialogResult=false;
        var discard=window.MakeButton("不保存","DiscardChangesButton");discard.Click+=(_,_)=>{result=SaveChangesChoice.Discard;window.DialogResult=true;};
        var save=window.MakeButton("保存并离开","SaveChangesButton",true);save.IsDefault=true;save.Click+=(_,_)=>{result=SaveChangesChoice.Save;window.DialogResult=true;};
        actions.Children.Add(cancel);actions.Children.Add(discard);actions.Children.Add(save);window.Body=layout;
        window.ContentRendered+=(_,_)=>cancel.Focus();window.ShowDialog();return result;
    }
    public static bool Confirm(Window? owner, string message, string title, string confirmLabel = "确认") =>
        Show(owner, message, title, confirmLabel, true);

    public static void Info(Window? owner, string message, string title = "WinGrab") =>
        Show(owner, message, title, "知道了", false);

    static bool Show(Window? owner, string message, string title, string confirmLabel, bool cancellable)
    {
        var window = new DialogWindow { Title = title, Eyebrow = cancellable ? "// CONFIRM" : "// NOTICE", Width = 540, Height = 330,ResizeMode=ResizeMode.NoResize };
        if (owner?.IsVisible == true) window.Owner = owner;
        else window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var layout = new Grid { Margin = new Thickness(24) };
        layout.RowDefinitions.Add(new RowDefinition());
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, FontSize = 14, Padding = new Thickness(0, 0, 12, 20) } });
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        Grid.SetRow(actions, 1); layout.Children.Add(actions);
        var cancel = window.MakeButton("取消", "CancelButton"); cancel.IsCancel = true;
        cancel.Click += (_, _) => window.DialogResult = false;
        if (cancellable) actions.Children.Add(cancel);
        var confirm = window.MakeButton(confirmLabel, "ConfirmButton", true); confirm.IsDefault = true;
        confirm.Click += (_, _) => window.DialogResult = true;
        actions.Children.Add(confirm); window.Body = layout;
        window.ContentRendered += (_, _) => (cancellable ? cancel : confirm).Focus();
        // Cancellation is the initial focus and close result. Explicit Enter accepts.
        window.PreviewKeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; window.DialogResult = true; } };
        return window.ShowDialog() == true;
    }
}
