# 设置内容区设计语言

右侧内容区沿用 Windows 系统浅色和深色外观。亚克力保留在窗口底层，内容卡片一律半透明并使用很低的不透明度（浅色约 22%、深色约 10%，且不随亚克力是否可用而退回纯色），卡片的层次来自背后的模糊底与留白，卡片本身只叠一层极薄的明度。同级双栏继续使用相同间距。卡片采用 8 DIP 圆角，卡片之间通过留白和明度区分。[Microsoft Acrylic](https://learn.microsoft.com/en-us/windows/apps/design/style/acrylic)

交互控件采用 4 DIP 圆角；按钮统一为 6 DIP 圆角、低不透明度填充、无描边，主要 / 危险 / 选中状态只用更淡的色层与彩色文字区分，不再整块铺蓝。下拉框和快捷键输入保留中性色填充与细描边，按钮不参与。开关、滑杆、复选框沿用各自的形状规则。选中、开启及主要操作用蓝色，危险操作文字用红色。禁用状态降低对比且保留默认光标，普通可点击按钮使用手形光标。[WinUI 控件](https://learn.microsoft.com/windows/uwp/design/controls-and-patterns/) · [WinUI ComboBox](https://learn.microsoft.com/windows/apps/design/controls/combo-box) · [Google Material 色彩角色](https://m3.material.io/styles/color/the-color-system/)

标题、正文、辅助文字和禁用文字使用各自的颜色角色。辅助文字在浅色模式用深灰，在深色模式用浅灰，通过颜色和间距建立信息层级。下拉菜单和对话框使用不透明表面，选中状态同时提供填充与文字反馈。通用状态由共享绘制方法实现，内置设置和插件设置使用同一套规则。[Meta 无障碍设计](https://www.meta.com/design-at-meta/blog/acessibility-and-design-systems/) · [Adobe Spectrum 设计令牌](https://spectrum.adobe.com/page/design-tokens/)

卡片高度一律由卡内内容决定：最后一行内容（文字用基线加 4 DIP 的字形下沿，控件用其下沿）再加 10 DIP 内边距就是卡片底边（`CARD_BOTTOM_PAD`）。左右两列各排各的，同排两张卡允许高度不同、第二排的卡也允许顶边不齐，不要为了让两列底边看齐而给某张卡拉出空白。整页的滚动列表（我的插件、插件市场）仍占满到内容区底边，但按条数收高的清单（显示内容、重要信息、实况窗插件）只包住真实条目，条目多到装不下时才排到底边并改为内部滚动。
