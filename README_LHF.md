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


## github


 两种做法技术上完全等价（remote 名字只是别名，功能没有任何区别），但如果问哪种更符合社区惯例——你 nvim 项目的做
 法（origin=自己的仓库，upstream=上游）更准确。

 这是 GitHub Fork 工作流的标准约定：

 ┌──────────┬───────────────┬──────────────────────────────────┐
 │ 远端名   │ 惯例指向      │ 含义                             │
 ├──────────┼───────────────┼──────────────────────────────────┤
 │ origin   │ 你自己的 fork │ 你拥有推送权限，日常 push 到这里 │
 ├──────────┼───────────────┼──────────────────────────────────┤
 │ upstream │ 原作者的仓库  │ 只用于 fetch 拉取上游更新        │
 └──────────┴───────────────┴──────────────────────────────────┘

 原因：

 1. 默认行为的便利：git push、git pull、git clone 的默认远端都是 origin。把origin 指向自己的仓库后，日常推送无
    需指定远端名，少打很多字。
 2. 工具链兼容：很多 GitHub CLI、CI 模板、编辑器插件都假设 origin 是你自己的仓库。
 3. 语义清晰：upstream 这个词本身就表达"上游、源头"的含义，一看就知道是只读的参考源。

 建议把 MousePointerReposition 项目也统一成这种风格：

 ```bash
   cd S:/1/repos/MousePointerReposition

   # 把原 origin（rhinterndorfer）改名为 upstream
   git remote rename origin upstream

   # 添加你自己的仓库为 origin
   git remote add origin https://github.com/liuhhaiffeng/MousePointerReposition_available_touchpad.git

   # 推送当前分支
   git push -u origin lhf_dev_trackpad
 ```

 之后日常同步上游的流程：

 ```bash
   git fetch upstream
   git merge upstream/master      # 或 git rebase upstream/master
   git push origin lhf_dev_trackpad
 ```
