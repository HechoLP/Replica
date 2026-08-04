using Replica.Core.Navigation;
using Replica.Core.Services;

namespace Replica.App.Services;

public sealed class NavigationService : INavigationService
{
    public event EventHandler<NavigationChangedEventArgs>? Navigated;

    public NavigationDestination CurrentDestination { get; private set; } = NavigationDestination.Home;

    public void Navigate(NavigationDestination destination)
    {
        if (destination == CurrentDestination)
        {
            return;
        }

        CurrentDestination = destination;
        Navigated?.Invoke(this, new NavigationChangedEventArgs(destination));
    }
}
