# Build instructions

- 用户要求：每次完成一轮代码修改，立即运行 `dotnet build TANGERINE-VideoEditor/TANGERINE-VideoEditor.csproj --configuration Debug --artifacts-path D:\Dev\TVE\Debug-test`。
- 仅 Codex 验证构建使用上述测试目录；主程序在 `D:\Dev\TVE\Debug-test\bin\TANGERINE-VideoEditor\debug\`，其他项目在同一测试目录的独立子目录。不要改变项目本身的默认 Debug 输出配置，也不要用 Release 代替 Debug。
- 本地 `data/voices`、`.tvevcv` 音色包和 `.tvep` 工程文件包含私人内容，不要加入 Git。
- 保留用户的其他未提交修改和文件移动。遇到正在运行的程序锁定输出文件时，不擅自结束用户进程。
