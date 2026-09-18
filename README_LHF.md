 项目情况

 这是一个 .NET Framework 4.7.2 WPF 桌面应用（经典旧式 csproj + PackageReference)，解决方案里还有一个
 PackageProject.wapproj (MSIX 打包工程，可选）。

 如何编译

 核心项目用 MSBuild 即可，无需打开 Visual Studio:

 ```powershell
   # 自动定位 VS2022 的 MSBuild
   .\build.ps1                    # Debug + Release 都编
   .\build.ps1 -Configuration Debug
   .\build.ps1 -Configuration Release
 ```

 若还要生成 MSIX 安装包（需要 VS 安装"通用 Windows 平台开发"工作负载）:

 ```powershell
   .\build.ps1 -Package
 ```

 脚本说明（已创建 build.ps1)

 - 通过 vswhere.exe 自动定位 MSBuild
 - 先 Restore 还原 NuGet 包，再 Build
 - 注意坑：csproj 里平台名是 AnyCPU（无空格），而 sln 里是 Any CPU，脚本已自动处理

 编译结果

 ┌─────────┬────────────────────────────────────────────────────────────────────────┐
 │ 配置    │ 输出                                                                   │
 ├─────────┼────────────────────────────────────────────────────────────────────────┤
 │ Debug   │ MousePointerReposition\bin\Debug\MousePointerReposition.exe (313 KB)   │
 ├─────────┼────────────────────────────────────────────────────────────────────────┤
 │ Release │ MousePointerReposition\bin\Release\MousePointerReposition.exe (309 KB) │
 └─────────┴────────────────────────────────────────────────────────────────────────┘

 两个配置均一次编译通过，无错误警告（minimal 输出下）。如需 x86/x64，加 -Platform x86 或 -Platform x64 即可。
