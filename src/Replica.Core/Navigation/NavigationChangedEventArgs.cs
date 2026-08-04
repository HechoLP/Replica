namespace Replica.Core.Navigation;

public sealed class NavigationChangedEventArgs : EventArgs
{
    public NavigationChangedEventArgs(NavigationDestination destination)
    {
        Destination = destination;
    }

    public NavigationDestination Destination { get; }
}
