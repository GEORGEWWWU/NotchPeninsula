# NotchPeninsula 项目协作规则

- 每次写代码前，先读 `.workbuddy/PROGRESS.md` 和 `.workbuddy/memory/` 下最新的日期记录。涉及旧功能时，再读对应的历史记录。
- 每次项目操作后，在 `.workbuddy/memory/YYYY-MM-DD.md` 追加记录。按北京时间记日期，写明需求、执行动作、结果和待办。遇到失败也要记录。保留其他代理已有的记录。
- 每次交付前更新 `.workbuddy/PROGRESS.md`，记录当前状态、已验证范围和下一步。
- 每次交付代码改动时，用中文写 Git 提交说明，主动提交并推送，确认推送结果。记忆和进度文件保存在本地 `.workbuddy/`，由 Git 忽略。
- 每次交付代码改动都附上对应的 Windows x64 单文件 exe。直接输出并覆盖 `publish/NotchPeninsula.exe`。按用户要求只打包，除非用户明确要求启动或测试。
- 涉及材质改动时保留 Windows 10 兼容性，优先考虑性能和内存占用。
