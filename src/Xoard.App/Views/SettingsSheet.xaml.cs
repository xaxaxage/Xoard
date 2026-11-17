using Microsoft.UI.Xaml.Controls;
using Xoard.App.ViewModels;

namespace Xoard.App.Views;

/// <summary>Настройки: панель справа поверх главного окна.</summary>
public sealed partial class SettingsSheet : UserControl
{
    public SettingsSheet(MainViewModel vm)
    {
        Vm = vm;
        InitializeComponent();
    }

    public MainViewModel Vm { get; }
}
