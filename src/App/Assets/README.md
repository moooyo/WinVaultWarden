# App 资源图标

本目录的占位图标由脚本生成(深蓝底 + 白色 "V" 字),覆盖 MSIX 打包所需的全部尺寸:

| 文件 | 尺寸 | 用途 |
| --- | --- | --- |
| StoreLogo.png | 50×50 | 商店标识 |
| Square150x150Logo.png | 150×150 | 中磁贴 |
| Square44x44Logo.png | 44×44 | 应用列表/任务栏 |
| Wide310x150Logo.png | 310×150 | 宽磁贴 |
| SplashScreen.png | 620×300 | 启动画面 |
| LockScreenLogo.png | 44×44 | 锁屏(透明底) |

> 说明:这是**占位图标**,非 Bitwarden 官方 logo。配色接近但带自有 "V" 标记,
> 避免与官方品牌混淆(商标考虑)。后续可替换为正式设计。
> 重新生成:见 git 历史中的 PowerShell 脚本(System.Drawing 绘制)。
