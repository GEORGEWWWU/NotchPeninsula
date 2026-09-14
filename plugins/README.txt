NotchPeninsula 插件目录
========================

把从插件市场下载（或自己编译）的插件 DLL 放到这个目录，然后在
「设置 → 插件中心」里点「导入 DLL」或直接用「打开目录」按钮管理。

支持两种布局：

1) 单文件型（插件没有任何额外依赖时最简单）
   plugins/
     HelloPlugin.dll

2) 目录型（插件带自己的依赖 DLL / 资源文件时用这种）
   plugins/
     MyPlugin/
       plugin.json          ← 可选，用来指定入口 DLL
       MyPlugin.dll
       某个依赖.dll

   plugin.json 内容示例：
     { "dll": "MyPlugin.dll" }

说明：
- 以 "_" 开头（如 _recycle 回收站）或 "." 开头的目录会被忽略。
- 在插件中心里「移除」插件不会真正删除文件，而是移动到 plugins/_recycle 下，可手动找回。
- 加载采用「影子拷贝」，因此插件 DLL 不会被进程占用，可以随时覆盖升级后点「重载」。
