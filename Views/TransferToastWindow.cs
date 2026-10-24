using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using UserIdentityDesktop.Core;
namespace UserIdentityDesktop.Views;
/// <summary>Persistent, non-activating transfer surface. Never consumes the ordinary toast limit.</summary>
public sealed class TransferToastWindow : Window
{
    private readonly StackPanel list = new() { Spacing = 12 };
    private readonly Dictionary<Guid, Row> rows = new();
    private readonly ScrollViewer scroll;
    private readonly bool reducedMotion;
    internal Border Card { get; }
    private bool presenterClosing;
    internal void CloseForPresenter() { presenterClosing=true; Close(); }
    protected override void OnClosing(WindowClosingEventArgs args)
    {
        if(!presenterClosing && args.CloseReason is WindowCloseReason.WindowClosing or WindowCloseReason.Undefined) args.Cancel=true;
        base.OnClosing(args);
    }
    public TransferToastWindow(bool reducedMotion = false)
    {
        this.reducedMotion = reducedMotion;
        scroll = new ScrollViewer { MaxHeight=420, Content=list };
        Title="Attachment transfers • Fire User Connect"; Width=396; SizeToContent=SizeToContent.Height;
        CanResize=false; ShowInTaskbar=false; ShowActivated=false; Topmost=true;
        WindowDecorations=WindowDecorations.None; Background=Brushes.Transparent;
        TransparencyLevelHint=new[]{WindowTransparencyLevel.Transparent};
        Card=new Border { Margin=new Thickness(8), CornerRadius=new CornerRadius(14), BorderThickness=new Thickness(1), BoxShadow=BoxShadows.Parse("0 4 16 0 #30000000"), ClipToBounds=true };
        Card.Bind(Border.BackgroundProperty, this.GetResourceObservable("ForgeSurfaceCard"));
        Card.Bind(Border.BorderBrushProperty, this.GetResourceObservable("ForgeBorder"));
        var edge=new Border { BorderThickness=new Thickness(4,0,0,0), CornerRadius=new CornerRadius(14), Padding=new Thickness(14), Child=scroll };
        edge.Bind(Border.BorderBrushProperty,this.GetResourceObservable("ForgePrimary")); Card.Child=edge; Content=Card;
    }
    internal void FitWorkingArea(double height) => scroll.MaxHeight=Math.Max(80, Math.Min(420, height-60));
    public void Update(IReadOnlyList<TransferSnapshot> snapshots)
    {
        foreach(var id in rows.Keys.Where(id=>!snapshots.Any(s=>s.Id==id)).ToArray()) { list.Children.Remove(rows[id].Panel); rows.Remove(id); }
        foreach(var state in snapshots)
        {
            if(!rows.TryGetValue(state.Id,out var row)) { row=new Row(this,state.Direction); rows.Add(state.Id,row); list.Children.Add(row.Panel); }
            row.Update(state);
        }
    }
    internal static string FormatBytes(double bytes)
    {
        string[] units={"B","KB","MB","GB","TB","PB","EB"}; var i=0;
        while(bytes>=1024 && i<units.Length-1){bytes/=1024;i++;}
        return bytes.ToString(i==0?"0":"0.0",CultureInfo.CurrentCulture)+" "+units[i];
    }
    private sealed class Row
    {
        public StackPanel Panel {get;} = new(){Spacing=7};
        private readonly TextBlock title, count, stats, phase, percentage;
        private readonly bool ownerReducedMotion;
        private readonly ProgressBar bar=new(){Minimum=0,Maximum=100,Height=5,CornerRadius=new CornerRadius(3)};
        private TextBlock Label(TransferToastWindow owner,string resource,double size=12)
        { var text=new TextBlock {FontSize=size,TextWrapping=TextWrapping.Wrap};text.Bind(TextBlock.ForegroundProperty,owner.GetResourceObservable(resource));return text; }
        public Row(TransferToastWindow owner,TransferDirection direction)
        {
            ownerReducedMotion=owner.reducedMotion;
            title=Label(owner,"ForgeTextPrimary",14.7); title.FontWeight=FontWeight.Bold;
            count=Label(owner,"ForgeTextPrimary",13);stats=Label(owner,"ForgeTextSecondary");phase=Label(owner,"ForgeTextMuted");percentage=Label(owner,"ForgeTextPrimary");
            var icon=new PathIcon{Width=20,Height=20,Data=Geometry.Parse(direction==TransferDirection.Upload ? "M12,2 L5,9 7,11 11,7 11,18 13,18 13,7 17,11 19,9 Z M4,20 L20,20 20,22 4,22 Z" : "M11,2 L13,2 13,13 17,9 19,11 12,18 5,11 7,9 11,13 Z M4,20 L20,20 20,22 4,22 Z")};
            icon.Bind(PathIcon.ForegroundProperty,owner.GetResourceObservable("ForgePrimary"));
            var heading=new StackPanel{Orientation=Avalonia.Layout.Orientation.Horizontal,Spacing=10};heading.Children.Add(icon);heading.Children.Add(title);
            var progress=new Grid{ColumnDefinitions=new ColumnDefinitions("*,Auto"),ColumnSpacing=10};progress.Children.Add(bar);Grid.SetColumn(percentage,1);progress.Children.Add(percentage);
            bar.Bind(ProgressBar.ForegroundProperty,owner.GetResourceObservable("ForgePrimary"));
            Panel.Children.Add(heading);Panel.Children.Add(count);Panel.Children.Add(progress);Panel.Children.Add(stats);Panel.Children.Add(phase);
            Avalonia.Automation.AutomationProperties.SetLiveSetting(Panel,Avalonia.Automation.AutomationLiveSetting.Polite);
        }
        public void Update(TransferSnapshot s)
        {
            var upload=s.Direction==TransferDirection.Upload; title.Text=s.Finished ? s.Succeeded ? upload?"Upload complete":"Download complete" : "Transfer failed" : upload?"Uploading attachments":"Downloading attachments";
            count.Text=$"{s.CompletedFiles} of {s.TotalFiles} attachment{(s.TotalFiles==1?"":"s")} {(upload?"uploaded":"downloaded")}";
            bar.IsIndeterminate=!s.Finished && s.TotalBytes is null && !ownerReducedMotion;bar.Value=s.Percentage;
            percentage.Text=s.TotalBytes is null && !s.Succeeded ? "—" : $"{s.Percentage:0}%";
            stats.Text=$"{FormatBytes(s.Bytes)}{(s.TotalBytes is {} total ? " / "+FormatBytes(total):"")}  •  {(upload?"Upload":"Download")} {FormatBytes(s.BytesPerSecond)}/s";
            phase.Text=s.Phase;
        }
    }
}
