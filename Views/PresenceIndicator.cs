using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using UserIdentityDesktop.Core.Models;
using UserIdentityDesktop.ViewModels;
namespace UserIdentityDesktop.Views;
/// <summary>The presence sign itself, shared by profile, conversation and availability menus.</summary>
public sealed class PresenceIndicator : Control
{
    public static readonly StyledProperty<UserStatus> StatusProperty=AvaloniaProperty.Register<PresenceIndicator,UserStatus>(nameof(Status),UserStatus.Offline);
    public UserStatus Status {get=>GetValue(StatusProperty);set=>SetValue(StatusProperty,value);}
    static PresenceIndicator()=>AffectsRender<PresenceIndicator>(StatusProperty);
    public PresenceIndicator(){Width=16;Height=16;PropertyChanged+=(_,e)=>{if(e.Property==StatusProperty){ToolTip.SetTip(this,ChatViewModel.GetStatusLabel(Status));Avalonia.Automation.AutomationProperties.SetName(this,ChatViewModel.GetStatusLabel(Status));}};}
    public override void Render(DrawingContext context)
    {
        base.Render(context);using var scale=context.PushTransform(Matrix.CreateScale(Bounds.Width/20,Bounds.Height/20));
        var color=ChatViewModel.GetStatusColor(Status);context.DrawEllipse(Status==UserStatus.Offline?null:color,new Pen(color,2),new Point(10,10),8,8);
        var white=new Pen(Brushes.White,2);
        if(Status==UserStatus.Away){context.DrawLine(white,new Point(10,5),new Point(10,10));context.DrawLine(white,new Point(10,10),new Point(14,12));}
        else if(Status is UserStatus.Busy or UserStatus.DoNotDisturb)context.DrawLine(white,new Point(6,10),new Point(14,10));
        else if(Status is UserStatus.InCall or UserStatus.InMeeting){context.DrawRectangle(Brushes.White,null,new Rect(6,7,7,6),1,1);}
        else if(Status is UserStatus.Presenting or UserStatus.SharingScreen){context.DrawRectangle(null,white,new Rect(5,6,10,7));context.DrawLine(white,new Point(10,13),new Point(10,16));}
    }
}
