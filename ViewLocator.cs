using System;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using NotchPeninsula.ViewModels;

// 项目同时引用了 System.Windows.Forms 与 WPF，Control 存在二义性，这里显式钉死 Avalonia 版本
using Control = Avalonia.Controls.Control;

namespace NotchPeninsula;

/// <summary>
/// 约定式视图定位：NotchPeninsula.ViewModels.XxxViewModel → NotchPeninsula.Views.XxxView
/// </summary>
public class ViewLocator : IDataTemplate
{
    public Control? Build(object? data)
    {
        if (data is null) return null;

        var vmName = data.GetType().FullName ?? string.Empty;
        var viewName = vmName.Replace("ViewModels.", "Views.").Replace("ViewModel", "View");
        var type = data.GetType().Assembly.GetType(viewName);

        if (type != null && Activator.CreateInstance(type) is Control view)
            return view;

        return new TextBlock { Text = $"未找到视图：{viewName}" };
    }

    public bool Match(object? data) => data is ViewModelBase;
}
