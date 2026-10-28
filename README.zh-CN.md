# TaskHarbor

**让待办有处安放，让重要的事持续向前。**

[GitHub 开源仓库](https://github.com/Crazyer-Flove/TaskHarbor) · [构建状态与下载产物](https://github.com/Crazyer-Flove/TaskHarbor/actions/workflows/ci.yml) · [反馈问题](https://github.com/Crazyer-Flove/TaskHarbor/issues)

[English](README.md) · [使用说明](docs/USER_GUIDE.md) · [验证记录](docs/VERIFICATION.md)

TaskHarbor 是面向个人的 Windows 桌面四象限任务工具。保留中文操作界面，统一采用英文产品名、目录和源码命名。

## 已实现

- 四象限、拖动归类、搜索、列表与看板切换。
- 日／周／月计划与可选截止时间分开；不会将本周自动设置为周日截止。
- 完整工作台、小面板、侧边编辑、子任务和批量操作。
- 保存失败回滚、重试、撤销、回收站和导入前备份。
- 完成后生成下一次的日／周／月重复任务，处理闰年和月末日期。
- 托盘、Ctrl+Shift+T 唤出、固定、贴边收起及可配置延迟。
- 任务提醒、静默时段、延后提醒；程序退出后停止提醒。
- 可选 AI 文本／图片整理，预览修改后才加入任务。本地回环接口可不填 Key。
- 本地 JSON 数据、当前 Windows 用户加密 Key、旧版数据复制迁移。

## 使用与构建

```powershell
git clone https://github.com/Crazyer-Flove/TaskHarbor.git
cd TaskHarbor
```

运行发布包中的 `TaskHarbor.exe`。要求 Windows 10/11 x64、.NET Framework 4.6.2+、WebView2 Runtime。旧版正在运行时请先退出。

源码目录双击 `Build.cmd`，或执行：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/build.ps1
```

首次构建会从官方 NuGet 恢复固定版本 SDK。运行测试需要 Node.js 22+：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/test.ps1
```

发布文件在 `dist/`。源码包不含个人任务、API Key、运行缓存或编译依赖 DLL；依赖由构建脚本恢复。

默认数据位置为 `%APPDATA%\TaskHarbor`。首次正常运行复制旧版 DeskTodo 默认数据，保留原文件；旧版自定义任务路径继续沿用。回收站不会自动清空。

目前自动化测试与二进制自检已通过，真实桌面视觉与多屏验收状态见[验证记录](docs/VERIFICATION.md)，不能把单元测试通过等同于全部实机验证。

使用 MIT 开源许可；第三方 SDK 使用其原有许可。推送 GitHub 的操作见[发布指南](docs/PUBLISHING.md)。
