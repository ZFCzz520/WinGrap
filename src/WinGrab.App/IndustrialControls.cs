using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
namespace WinGrab.App;
public sealed class TextLengthVisibilityConverter:System.Windows.Data.IValueConverter
{
    public object Convert(object value,Type targetType,object parameter,System.Globalization.CultureInfo culture)=>value is int n&&n>0?Visibility.Visible:Visibility.Collapsed;
    public object ConvertBack(object value,Type targetType,object parameter,System.Globalization.CultureInfo culture)=>throw new NotSupportedException();
}

public sealed class InputHeightConverter:System.Windows.Data.IValueConverter
{
    public object Convert(object value,Type targetType,object parameter,System.Globalization.CultureInfo culture)=>Math.Max(220,(value is double height?height:860)-350);
    public object ConvertBack(object value,Type targetType,object parameter,System.Globalization.CultureInfo culture)=>throw new NotSupportedException();
}

public sealed class CutBorder:Border
{
    protected override void OnRender(DrawingContext dc)
    {
        double w=ActualWidth,h=ActualHeight,c=Math.Min(8,Math.Min(w,h)/3);
        var geometry=new StreamGeometry();
        using(var g=geometry.Open()){g.BeginFigure(new Point(.5,.5),true,true);g.PolyLineTo(new[]{new Point(w-.5,.5),new Point(w-.5,h-c),new Point(w-c,h-.5),new Point(.5,h-.5)},true,false);}
        geometry.Freeze();
        dc.DrawGeometry(Background,BorderBrush==null?null:new Pen(BorderBrush,Math.Max(1,BorderThickness.Left)),geometry);
    }
}
