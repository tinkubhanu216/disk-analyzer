using CommunityToolkit.Mvvm.ComponentModel;
using DiskAnalyzer.App.Services;

namespace DiskAnalyzer.App.ViewModels;

/// <summary>Base for pages shown in the main window.</summary>
public abstract partial class PageViewModel : ObservableObject
{
    protected PageViewModel(ScanSessionService session)
    {
        Session = session;
        session.ResultChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasResult));
            OnResultChanged();
        };
    }

    public ScanSessionService Session { get; }

    public abstract string Title { get; }

    public abstract PageKind Kind { get; }

    public bool HasResult => Session.Current is not null;

    /// <summary>Called each time the page is shown, with an optional parameter (e.g. a node index).</summary>
    public virtual void OnNavigatedTo(object? parameter)
    {
    }

    protected virtual void OnResultChanged()
    {
    }
}
