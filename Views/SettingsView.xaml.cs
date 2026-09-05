using System.Windows;
using System.Windows.Controls;
using HanguanBox.ViewModels;

namespace HanguanBox.Views;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();

        DataContextChanged += (_, _) =>
        {
            if (DataContext is MainViewModel vm)
            {
                vm.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(MainViewModel.BackgroundPath))
                        TxtBgPath.Text = vm.BackgroundPath ?? "未设置（使用默认深色背景）";
                };
                TxtBgPath.Text = vm.BackgroundPath ?? "未设置（使用默认深色背景）";
            }
        };
    }

    private void PickBg_Click(object sender, RoutedEventArgs e)
        => (DataContext as MainViewModel)?.PickBackgroundCommand.Execute(null);

    private void ClearBg_Click(object sender, RoutedEventArgs e)
        => (DataContext as MainViewModel)?.ClearBackgroundCommand.Execute(null);

    // 滑条拖动实时改模糊半径；松手后同步回 ViewModel（避免拖动期间反复触发背景计算）
    private void SldBlur_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (DataContext is MainViewModel vm)
            vm.BackgroundBlurRadius = e.NewValue;
    }
}
