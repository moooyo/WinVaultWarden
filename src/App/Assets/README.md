# App 资源占位说明

MSIX 打包需要 `Assets/` 下的图标文件,这些是二进制 PNG,无法在此手写生成。
首次在 Visual Studio 打开本解决方案、或用 `dotnet new winui3` 生成一个临时项目后,
将其 `Assets/` 目录下的以下文件复制到 `src/App/Assets/`:

- StoreLogo.png
- Square150x150Logo.png
- Square44x44Logo.png
- Wide310x150Logo.png
- SplashScreen.png
- LockScreenLogo.png(可选)

`Package.appxmanifest` 已引用上述路径。缺图标时 MSIX 构建会报资源缺失错误,
补齐即可。纯调试(非打包)运行通常不强制要求全部图标。
