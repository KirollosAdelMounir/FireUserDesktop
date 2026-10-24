using Avalonia.Controls;
using Avalonia.Input;
using UserIdentityDesktop.ViewModels;
namespace UserIdentityDesktop.Views;
public partial class PeopleSearchBox : UserControl
{
    public PeopleSearchBox()
    {
        InitializeComponent();
        KeyDown+=(_,e)=>{if(DataContext is not ChatViewModel model)return;if(e.Key==Key.Escape){model.ClearIdentitySearch();e.Handled=true;}else if(e.Key==Key.Enter&&model.SearchResults.Count>0){model.StartChatFromSearchCommand.Execute(model.SearchResults[0]);e.Handled=true;}};
        DetachedFromVisualTree+=(_,_)=>ResultsPopup.IsOpen=false;
    }
}
