using Replica.Core.Models;

namespace Replica.Core.Services;

public interface IWindowsCompatibilityService
{
    WindowsCompatibilityInfo GetCompatibility();
}
