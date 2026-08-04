using Replica.Core.Navigation;

namespace Replica.Core.Services;

public interface INavigationService
{
    event EventHandler<NavigationChangedEventArgs>? Navigated;

    NavigationDestination CurrentDestination { get; }

    void Navigate(NavigationDestination destination);
}
