using System.Windows;
using WinGrab.Contracts;
using WinGrab.Core;

namespace WinGrab.App;
public sealed class EpisodeSelection(MediaEpisode episode,bool selected,Action changed):Observable
{
    bool selected=selected;
    public MediaEpisode Episode{get;}=episode;
    public string Label=>Episode.Label+(Available?"":"（受限内容）");
    public string Duration=>Episode.DurationLabel;
    public bool Available=>Episode.Available;
    public bool IsSelected{get=>selected;set{if(selected==value)return;selected=value;Changed();changed();}}
}
public partial class MainWindow
{
    readonly List<EpisodeSelection> episodeOptions=[];
    void PopulateCollection()
    {
        episodeOptions.Clear();
        var collection=preview?.Collection;
        CollectionPanel.Visibility=collection is {Episodes.Count:>1}?Visibility.Visible:Visibility.Collapsed;
        bool grouped=CollectionPanel.Visibility==Visibility.Visible;
        SourceEntry.Visibility=SourceHint.Visibility=grouped?Visibility.Collapsed:Visibility.Visible;
        ChangeSourceButton.Visibility=grouped?Visibility.Visible:Visibility.Collapsed;
        if(collection==null){EpisodeList.ItemsSource=null;return;}
        foreach(var episode in collection.Episodes)
            episodeOptions.Add(new(episode,episode.Available&&episode.Id==preview!.ContentId,UpdateEpisodeSelection));
        CollectionTitle.Text=collection.Title;
        EpisodeList.ItemsSource=episodeOptions;UpdateEpisodeSelection();
    }
    void UpdateEpisodeSelection()
    {
        int selected=episodeOptions.Count(e=>e.IsSelected&&e.Available);
        CollectionCount.Text=$"{episodeOptions.Count} 集 / 已选 {selected} 集";
        DownloadSelectedButton.Content=$"下载所选 {selected} 集";
        DownloadSelectedButton.IsEnabled=selected>0&&!shuttingDown;
        DownloadAllButton.Content=$"全部下载（{episodeOptions.Count(e=>e.Available)} 集）";
    }
    void SelectEpisodes_Click(object sender,RoutedEventArgs e)
    {foreach(var option in episodeOptions)option.IsSelected=option.Available;}
    void ClearEpisodes_Click(object sender,RoutedEventArgs e)
    {foreach(var option in episodeOptions)option.IsSelected=false;}
    void ChangeSource_Click(object sender,RoutedEventArgs e)
    {
        SourceEntry.Visibility=SourceHint.Visibility=Visibility.Visible;ChangeSourceButton.Visibility=Visibility.Collapsed;
        CollectionPanel.IsExpanded=false;InputScroll.ScrollToTop();LinkInput.Focus();LinkInput.SelectAll();
    }
    async void DownloadSelected_Click(object sender,RoutedEventArgs e)=>await QueueEpisodes(false);
    async void DownloadAll_Click(object sender,RoutedEventArgs e)=>await QueueEpisodes(true);
    async Task QueueEpisodes(bool all)
    {
        if(preview?.Collection==null||shuttingDown||parsing!=null||loginOpen)return;
        var media=preview;
        string format=SelectedFormat(media);
        var quality=QualityBox.SelectedItem as QualityChoice;
        if(format=="mp4"&&(quality==null||!quality.Available)){if(quality!=null)await UnlockQuality(quality);return;}
        string track=format=="mp4"?quality!.StreamId!:"";
        var targets=episodeOptions.Where(e=>e.Available&&(all||e.IsSelected)).Select(e=>e.Episode).ToArray();
        int added=0,duplicates=0;
        foreach(var episode in targets)
        {
            bool current=episode.Id==media.ContentId;
            var part=current?media with{Collection=null}:media with{ContentId=episode.Id,ArchiveId=episode.ArchiveId,Title=media.Collection.Title+" · "+episode.Label,Duration=episode.Duration,Videos=[],Audio=null,Images=[],Qualities=[],Collection=null};
            if(QueueMedia(part,episode.Url,format,track))added++;else duplicates++;
        }
        InputStatus.Visibility=Visibility.Visible;
        InputStatus.Text=$"已加入 {added} 集"+(duplicates>0?$"，跳过 {duplicates} 个已有任务":"")+"。按队列逐集获取所选清晰度，不可用时会提示，不会自动降级。";
        Refresh();Pump();
        if(added>0){CollectionPanel.IsExpanded=false;InputScroll.ScrollToTop();}
    }
    string SelectedFormat(ParsedMedia media)=>media.Kind==MediaKind.Gallery?"zip":FormatBox.SelectedIndex switch{1=>"m4a",2=>"mp3",3=>"cover",_=>"mp4"};
    bool QueueMedia(ParsedMedia media,string url,string format,string track)
    {
        bool Same(Job j)=>j.Media?.Platform==media.Platform&&(j.Media.ContentId==media.ContentId||j.Url==url||media.ArchiveId>0&&j.Media.ArchiveId==media.ArchiveId&&Page(j.Media.ContentId)==Page(media.ContentId));
        if(jobs.Any(j=>Same(j)&&j.Format==format&&j.TrackId==track&&j.State is JobState.Queued or JobState.Downloading or JobState.Paused or JobState.Merging))return false;
        var job=new Job{Url=url,Media=media,TrackIndex=Math.Max(0,media.Videos.FindIndex(v=>v.Id==track)),TrackId=track,Format=format};
        job.Transition(JobState.Queued);store.Save(job);jobs.Add(job);return true;
    }
    static int Page(string id)=>int.TryParse(id.Split("-p").LastOrDefault(),out int part)?part:1;
}
