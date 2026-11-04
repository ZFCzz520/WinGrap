using System.Windows;
namespace WinGrab.App;
public partial class MainWindow
{
    void Minimize_Click(object sender,RoutedEventArgs e)=>SystemCommands.MinimizeWindow(this);
    void Maximize_Click(object sender,RoutedEventArgs e)
    {if(WindowState==WindowState.Maximized)SystemCommands.RestoreWindow(this);else SystemCommands.MaximizeWindow(this);}
    void Close_Click(object sender,RoutedEventArgs e)=>Close();
    void UpdateChrome()
    {
        ShellRoot.Margin=WindowState==WindowState.Maximized?new Thickness(8):new Thickness(0);
        MaximizeButton.Content=WindowState==WindowState.Maximized?"▣":"□";
    }
}
