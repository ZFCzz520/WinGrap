using System.IO;
using System.Windows;
using System.Windows.Controls;
using WinGrab.Contracts;
using WinGrab.Core;
namespace WinGrab.App;
public partial class MainWindow
{
    internal Func<BilibiliLoginWindow>? LoginWindowFactory {get;set;}
    void PopulateQualities()
    {
        if(preview==null)return;
        suppressQualityEvents=true;
        try
        {
            var choices=preview.Qualities.Count>0?preview.Qualities:preview.Videos.Select((v,i)=>new QualityChoice(-1-i,v.Label,v.Id)).ToList();
            QualityBox.ItemsSource=choices;
            QualityBox.SelectedItem=choices.FirstOrDefault(q=>q.Available&&q.Quality==80)??choices.FirstOrDefault(q=>q.Available);
            QualityBox.IsEnabled=preview.Kind==MediaKind.Video&&FormatBox.SelectedIndex==0;
            FormatBox.IsEnabled=preview.Kind==MediaKind.Video;
            FormatBox.Visibility=preview.Kind==MediaKind.Gallery?Visibility.Collapsed:Visibility.Visible;
            QualityBox.Visibility=preview.Kind==MediaKind.Gallery?Visibility.Collapsed:Visibility.Visible;
        }
        finally{suppressQualityEvents=false;}
    }
    async void Download_Click(object sender,RoutedEventArgs e)
    {
        if(preview==null||shuttingDown||parsing!=null||loginOpen)return;
        string format=SelectedFormat(preview);
        var choice=QualityBox.SelectedItem as QualityChoice;
        if(format=="mp4"&&preview.Kind==MediaKind.Video&&(choice==null||!choice.Available)){if(choice!=null)await UnlockQuality(choice);return;}
        string trackId=format=="mp4"?choice?.StreamId??"":"";
        if(!QueueMedia(preview with{Collection=null},Canonical(preview),format,trackId))
        {InputStatus.Visibility=Visibility.Visible;InputStatus.Text="相同格式和画质的任务已在队列中，请继续已有任务。";return;}
        if(pending.Count>0)ShowNext();
        InputStatus.Visibility=Visibility.Visible;InputStatus.Text="已加入下载，可继续选择封面、音频或其他画质";Refresh();Pump();
    }
    void Format_Changed(object sender,SelectionChangedEventArgs e)
    {if(preview!=null)QualityBox.IsEnabled=preview.Kind==MediaKind.Video&&FormatBox.SelectedIndex==0;}
    async void Quality_Changed(object sender,SelectionChangedEventArgs e)
    {
        if(suppressQualityEvents||preview==null||qualityLoginInProgress||QualityBox.SelectedItem is not QualityChoice choice||choice.Available)return;
        await UnlockQuality(choice);
    }
    async Task UnlockQuality(QualityChoice choice)
    {
        if(qualityLoginInProgress||loginOpen||parsing!=null)return;
        InputStatus.Visibility=Visibility.Visible;
        if(!choice.RequiresLogin&&!choice.RequiresMembership){InputStatus.Text="该档位当前没有可用下载地址，请尝试其他清晰度";return;}
        qualityLoginInProgress=true;loginOpen=true;
        string? target=preview==null?null:Canonical(preview);
        try
        {
            InputStatus.Text=choice.RequiresMembership?"此画质需要登录及相应会员权限，请在手机上确认自己的账号":"此画质需要登录，请使用手机 B站扫码确认";
            var dialog=LoginWindowFactory?.Invoke()??new BilibiliLoginWindow(store){Owner=this};
            dialog.ShowDialog();
            if(!dialog.CredentialsChanged){InputStatus.Text="未更改登录状态，请选择可用清晰度或再次扫码";return;}
            if(target!=null)LinkInput.Text=target;
            await RefreshCredentials();
            if(preview==null)return;
            var refreshed=preview.Qualities.FirstOrDefault(q=>q.Quality==choice.Quality);
            if(refreshed!=null)
            {
                suppressQualityEvents=true;try{QualityBox.SelectedItem=refreshed;}finally{suppressQualityEvents=false;}
                InputStatus.Text=refreshed.Available?"已获取 "+refreshed.Name+"，确认后加入下载":refreshed.RequiresMembership?"该账号暂未获得此会员档位，或来源未返回地址；请选择可用画质":"该档位仍未返回下载地址，请确认手机已授权登录或尝试其他画质";
            }
        }
        finally{loginOpen=false;qualityLoginInProgress=false;}
    }
    void ClearHistory_Click(object sender,RoutedEventArgs e)=>DeleteHistory(jobs.Where(j=>j.State is JobState.Completed or JobState.Failed or JobState.Cancelled).ToArray());
    void DeleteHistory(IReadOnlyList<Job> targets,bool confirm=true)
    {
        var finished=targets.Where(j=>!running.ContainsKey(j.Id)&&j.State is JobState.Completed or JobState.Failed or JobState.Cancelled).ToArray();
        if(finished.Length==0)return;
        if(confirm&&ThemedDialog.Confirm(this,$"删除 {finished.Length} 条历史记录？\n已下载文件将保留，正在下载或暂停的任务不会删除。","删除历史记录","删除记录") == false)return;
        try{foreach(var job in finished)if(store.DeleteHistory(job.Id))jobs.Remove(job);}
        catch(IOException){ThemedDialog.Info(this,"部分记录未能删除，请检查数据目录权限后重试。","WinGrab");}
        finally{Refresh();}
    }
}
