using System.Windows;
using Altong.Client.Services;

namespace Altong.Client;

public partial class App : Application
{
    public FocusModeService FocusModeService { get; } = new();
}
