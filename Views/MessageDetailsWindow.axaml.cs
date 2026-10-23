using Avalonia.Controls;
using Avalonia.Input;
using UserIdentityDesktop.ViewModels;
namespace UserIdentityDesktop.Views;
public partial class MessageDetailsWindow : Window
{
    public MessageDetailsWindow()
    {
        InitializeComponent();var motion=new WindowMotion(this);
        KeyDown+=(_,e)=>{if(e.Key==Key.Escape){e.Handled=true;_=motion.CloseAsync(false);}};
        Closed+=(_,_)=>{if(DataContext is MessageDetailsViewModel model)model.Dispose();};
    }
    public MessageDetailsWindow(MessageDetailsViewModel model):this()=>DataContext=model;
}
