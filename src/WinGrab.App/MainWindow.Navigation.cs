using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
namespace WinGrab.App;
public partial class MainWindow
{
    enum MainPage { Workbench,History,Settings }
    MainPage currentPage;
    SettingsPage? settingsPage;
    bool credentialsNeedRefresh;
    bool confirmingSettings;
    bool ConfirmLeaveSettings()
    {
        if(settingsPage?.HasUnsavedChanges!=true)return true;
        if(confirmingSettings)return false;
        confirmingSettings=true;
        try
        {
            switch(ThemedDialog.AskSaveChanges(this))
            {
                case SaveChangesChoice.Save:return settingsPage.TrySave();
                case SaveChangesChoice.Discard:settingsPage.DiscardChanges();return true;
                default:return false;
            }
        }
        finally{confirmingSettings=false;UpdateSettingsSaveReminder();}
    }
    void UpdateSettingsSaveReminder()=>SettingsSaveReminder.Visibility=currentPage==MainPage.Settings&&settingsPage?.HasUnsavedChanges==true?Visibility.Visible:Visibility.Collapsed;
    bool Navigate(MainPage page)
    {
        if(currentPage==MainPage.Settings&&page!=MainPage.Settings&&!ConfirmLeaveSettings())return false;
        bool changed=page!=currentPage;
        currentPage=page;history=page==MainPage.History;
        InputPanel.Visibility=page==MainPage.Workbench?Visibility.Visible:Visibility.Collapsed;
        DownloadsPanel.Visibility=page==MainPage.Settings?Visibility.Collapsed:Visibility.Visible;
        SettingsHost.Visibility=page==MainPage.Settings?Visibility.Visible:Visibility.Collapsed;
        SaveSettingsButton.Visibility=page==MainPage.Settings?Visibility.Visible:Visibility.Collapsed;
        PageTitle.Text=page switch{MainPage.Workbench=>"下载工作台",MainPage.History=>"下载历史",_=>"偏好设置"};
        PageEyebrow.Text=page switch{MainPage.Workbench=>"// DOWNLOAD WORKSPACE",MainPage.History=>"// DOWNLOAD HISTORY",_=>"// PREFERENCES"};
        PageSubtitle.Text=page switch{MainPage.Workbench=>"复制链接，收藏每一份灵感。",MainPage.History=>"管理最近的下载记录与本地文件。",_=>"按你的习惯设置下载、命名与账号。"};
        QueueTitle.Text=history?"最近 90 天":"下载队列";
        EmptyTitle.Text=history?"暂无下载历史":"还没有下载任务";
        EmptyHint.Text=history?"下载完成后的记录会显示在这里。":"粘贴分享链接，解析后即可保存视频、音频或封面。";
        foreach(var pair in new[]{(WorkbenchNav,MainPage.Workbench),(HistoryNav,MainPage.History),(SettingsNav,MainPage.Settings)})
        {
            bool selected=pair.Item2==page;
            pair.Item1.Background=selected?(Brush)FindResource("Accent"):Brushes.Transparent;
            pair.Item1.BorderBrush=Brushes.Transparent;
            pair.Item1.Foreground=selected?(Brush)FindResource("Ink"):Brushes.White;
            pair.Item1.FontWeight=selected?FontWeights.SemiBold:FontWeights.Normal;
            System.Windows.Automation.AutomationProperties.SetItemStatus(pair.Item1,selected?"当前页面":"");
        }
        if(page==MainPage.Settings&&settingsPage==null)
        {
            settingsPage=new SettingsPage(store);
            settingsPage.UnsavedChangesChanged+=(_,_)=>UpdateSettingsSaveReminder();
            settingsPage.CredentialsUpdated+=(_,_)=>
            {
                parser.ClearCache();pending.Clear();preview=null;PreviewPanel.Visibility=Visibility.Collapsed;PopulateCollection();credentialsNeedRefresh=true;
            };
            SettingsHost.Content=settingsPage;
        }
        UpdateSettingsSaveReminder();Refresh();
        if(changed&&SystemParameters.ClientAreaAnimation)
        {
            var selected=page switch{MainPage.Workbench=>WorkbenchNav,MainPage.History=>HistoryNav,_=>SettingsNav};
            selected.BeginAnimation(OpacityProperty,new System.Windows.Media.Animation.DoubleAnimation(.65,1,TimeSpan.FromMilliseconds(150)));
            var content=page==MainPage.Settings?(UIElement)SettingsHost:DownloadsPanel;
            content.BeginAnimation(OpacityProperty,new System.Windows.Media.Animation.DoubleAnimation(.7,1,TimeSpan.FromMilliseconds(150)));
        }
        return true;
    }
}
