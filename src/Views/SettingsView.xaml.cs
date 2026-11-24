using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace EndfieldMonitor.Views;

public partial class SettingsView : UserControl
{
    public event EventHandler? CloseRequested;

    public SettingsView()
    {
        InitializeComponent();
        PreviewKeyDown += OnPreviewKeyDown;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            // 下拉框展开时 Escape 先收起下拉框。
            if (Keyboard.FocusedElement is ComboBoxItem || (e.OriginalSource as DependencyObject) is ComboBox { IsDropDownOpen: true })
                return;
            e.Handled = true;
            CloseRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>面板打开后把键盘焦点交给当前页的第一个可聚焦控件。</summary>
    public void FocusFirst()
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (Tabs.SelectedItem is TabItem tab)
            {
                tab.Focus();
                tab.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
            }
        }, System.Windows.Threading.DispatcherPriority.Input);
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);
}
