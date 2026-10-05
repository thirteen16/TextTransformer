# 文本转换助手

Windows 文本转换托盘程序。

| 快捷键 | 功能 |
| --- | --- |
| 双击 CapsLock | 英文字母全大写；已全大写时转为全小写 |
| 双击左 Shift | 只保留英文字母 A–Z、a–z，删除其他字符 |
| 双击右 Shift | 句首大写，其余英文字母小写，同时将中文标点转为英文标点 |

选中文字后使用快捷键；没有选区时，处理当前可编辑输入框的全文。

右键托盘图标可打开使用说明、勾选开机自启、启用或停用快捷键，以及退出程序。关闭说明窗口后，程序继续在托盘运行。

## 构建与运行

在 Windows PowerShell 中运行：

```powershell
.\build.ps1
```

需要 Windows 的 .NET Framework 4.x 编译器。构建后双击 `TextTransformer.exe` 即可运行，无需安装。图标已嵌入 EXE。

## 项目文件

- `TextTransformer.cs`：程序源码。
- `TextTransformer.ico`：程序图标。
- `build.ps1`：构建脚本。
- `README.md`：项目说明。
- `.gitignore`：排除本地编译产物。

生成的 EXE 可作为 GitHub Release 附件发布。
