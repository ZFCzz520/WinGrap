using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Automation;
using WinGrab.Core;

namespace WinGrab.App;
public partial class MainWindow
{
    string selectedPlatform="bilibili";
    readonly Dictionary<string,string> platformDrafts=[];
    bool changingPlatform;
    void SelectPlatform(string platform)
    {
        bool changed=selectedPlatform!=platform;
        selectedPlatform=platform;
        foreach(var button in new[]{BiliPlatformButton,DouyinPlatformButton})
        {
            bool selected=(string)button.Tag==platform;
            button.Background=selected?(Brush)FindResource("Ink"):(Brush)FindResource("Chip");
            button.Foreground=selected?Brushes.White:(Brush)FindResource("Ink");
            button.FontWeight=selected?FontWeights.SemiBold:FontWeights.Normal;
            AutomationProperties.SetItemStatus(button,selected?"当前平台":"");
            if(changed&&selected&&SystemParameters.ClientAreaAnimation)
                button.BeginAnimation(OpacityProperty,new System.Windows.Media.Animation.DoubleAnimation(.65,1,TimeSpan.FromMilliseconds(150)));
        }
        SourceHint.Text=platform=="douyin"?"支持抖音单条公开视频或图文分享链接，也可直接粘贴分享文字。":"支持哔哩哔哩分享文字、BV / av 号及多行链接，可解析分P和视频合集。";
        LinkInput.ToolTip=platform=="douyin"?"粘贴抖音分享文字或作品链接，多条请换行":"粘贴 B站分享链接或 BV / av 号，多条请换行";
    }
    void Platform_Click(object sender,RoutedEventArgs e)
    {
        if(sender is not Button{Tag:string platform}||platform==selectedPlatform||parsing!=null)return;
        platformDrafts[selectedPlatform]=LinkInput.Text;
        changingPlatform=true;
        try
        {
            SelectPlatform(platform);
            LinkInput.Text=platformDrafts.GetValueOrDefault(platform,"");
            pending.Clear();preview=null;PreviewPanel.Visibility=Visibility.Collapsed;PopulateCollection();
            InputStatus.Visibility=Visibility.Collapsed;InputScroll.ScrollToTop();LinkInput.Focus();
        }
        finally{changingPlatform=false;}
    }
    void LinkInput_TextChanged(object sender,TextChangedEventArgs e)
    {
        if(changingPlatform||SourceHint==null)return;
        string? raw=Links.Extract(LinkInput.Text);
        if(raw!=null&&Uri.TryCreate(raw,UriKind.Absolute,out var url)&&Links.Platform(url) is string platform)
            SelectPlatform(platform);
        platformDrafts[selectedPlatform]=LinkInput.Text;
    }
}
