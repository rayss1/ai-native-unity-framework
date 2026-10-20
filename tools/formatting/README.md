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
