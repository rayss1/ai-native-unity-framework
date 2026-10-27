# 代码排版

在仓库根目录运行：

```powershell
dotnet tool restore
python tools/formatting/format_csharp.py
python tools/formatting/format_csharp.py --check
```

CSharpier 版本固定为 1.3.0，使用 MIT 许可证；来源为 NuGet 的
[CSharpier 包](https://www.nuget.org/packages/CSharpier/1.3.0)。
版本在 `dotnet-tools.json` 中锁定，格式在 `.csharpierrc.json` 中定义。
升级时单独检查格式差异；回滚时恢复工具清单和配置后重新执行 `dotnet tool restore`。
它只用于开发，不加入 Unity 或服务器运行时依赖。

- C# 使用 4 个空格缩进，目标行宽为 100 字符。长参数列表和调用自动换行，
  方法体、枚举项和多条语句展开；短的自动属性保留一行。
- 字符串内容和长 URL 不强制拆开，避免改变实际内容。
- 脚本只处理 Git 已跟踪的项目自有 C#。新文件先加入 Git 后才能被脚本检查。
  第三方代码、协议生成代码和 Unity Input System 生成代码不手工格式化。
- `.proto` 使用 2 个空格缩进，每个字段或枚举项独占一行，消息之间空一行。
  格式调整不得改变字段名称、类型、编号、顺序或 `reserved` 范围。
- 修改协议源文件后，使用现有生成入口更新生成物和来源哈希：

```powershell
dotnet build tools/ProtocolCodegen/ProtocolCodegen.csproj -p:GenerateProtocol=true
python tools/ProtocolCodegen/package_unity.py
python tools/ProtocolCodegen/package_unity.py --check
```

格式化后检查 diff，并运行对应测试。CSharpier 自带语法树等价校验，不使用
`--skip-validation` 跳过它。

## Tools 脚本排版

脚本统一使用 4 空格缩进。展开压缩的控制块、多条语句和非空字典；用空行区分
参数、前置检查、主要步骤与清理。保留字符串、命令参数、退出码、here-document
和 PowerShell here-string 的内容。较长的字符串或不能安全断行的命令可以超过
100 列，不为满足行宽加入脆弱的续行符。

| 类型 | 固定工具 | 格式约定 |
| --- | --- | --- |
| Python | Black 24.8.0（MIT） | `black.toml`：100 列，保留默认语法树等价检查 |
| PowerShell | PSScriptAnalyzer 1.24.0（MIT） | `powershell.psd1` 和 `format_powershell.ps1`：先按解析 token 展开压缩代码，再排版；解析失败或非布局 token 改变时拒绝写入 |
| Shell | shfmt 3.7.0（BSD-3-Clause） | `-i 4 -ci`，保留原有 Bash/POSIX 方言，使用 LF 换行 |
| CMD | 人工整理 | 4 空格缩进、CRLF 换行；不改写 `%*`、`%ERRORLEVEL%` 或括号内的变量展开 |

这些依赖仅用于开发。使用隔离的 Python 环境安装 `black==24.8.0`；shfmt 可通过
`go install mvdan.cc/sh/v3/cmd/shfmt@v3.7.0` 安装，或使用该版本的官方二进制。
源码和许可证分别来自 [Black](https://github.com/psf/black/tree/24.8.0)、
[PSScriptAnalyzer](https://github.com/PowerShell/PSScriptAnalyzer/tree/1.24.0) 和
[shfmt](https://github.com/mvdan/sh/tree/v3.7.0)。

PowerShell 模块可通过配置好的 PowerShell Gallery 下载到本地缓存：

```powershell
Save-Module PSScriptAnalyzer -RequiredVersion 1.24.0 `
    -Path ./artifacts/tool-formatting/modules
```

若未配置 Gallery，可下载
[固定版本 nupkg](https://cdn.powershellgallery.com/packages/psscriptanalyzer.1.24.0.nupkg)，
校验 SHA-256 为 `e86c97d44bb1bc8a1de35e753b85ea1d938f6f9f881639a181507e079bca4556`，
然后将内容解压至 `artifacts/tool-formatting/modules/PSScriptAnalyzer/1.24.0/`。
格式器默认读取该目录，也可用 `-ModulePath` 指定同版本模块清单。

从仓库根目录检查 Python 和 PowerShell：

```powershell
python -m black --config tools/formatting/black.toml --check tools
pwsh -NoProfile -File tools/formatting/format_powershell.ps1 -Check
```

去掉 `--check` / `-Check` 才会修改文件。在 Bash 中检查所有 Shell 脚本：

```bash
find tools -name '*.sh' -exec shfmt -d -i 4 -ci {} +
find tools -name '*.sh' -exec bash -n {} \;
```

将 `shfmt -d` 换成 `shfmt -w` 才会写回。格式器升级必须单独审查差异，运行
对应工具回归和语法检查；回滚时恢复格式配置及上述固定工具版本。排版协议打包
脚本也会改变其来源哈希，需重新运行上面的 `package_unity.py` 与 `--check`，
不能只手工修改依赖锁。
