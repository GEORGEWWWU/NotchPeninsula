using Avalonia.Controls;

// 与 System.Windows.Forms.UserControl / System.Windows.Controls.UserControl 消歧
using UserControl = Avalonia.Controls.UserControl;

namespace NotchPeninsula.Views;

public partial class AboutView : UserControl
{
    public AboutView() => InitializeComponent();
}
