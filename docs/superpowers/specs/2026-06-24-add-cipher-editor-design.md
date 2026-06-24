# 新增密码库项目编辑器设计

日期: 2026-06-24

## 背景

WinVaultWarden 当前已有保险库列表、详情展示、五种 Cipher 类型的只读详情模板，以及 mock 数据服务。`VaultViewModel.AddCommand` 仍是占位。用户希望新增一个参考 Bitwarden 原版的“新增密码”界面，支持登录、支付卡、身份、笔记、SSH 密钥，以及文件夹入口。

这轮实现范围限定为前端可用闭环:

- 新增 UI、编辑草稿模型、校验和 mock 保存。
- 保存后新增项目进入当前列表并选中，详情页可回显。
- 不接真实 `/api/ciphers`，不做 EncString 加密写入。
- 不做附件、组织集合分享、归档、密码历史和真实文件夹创建。

## 已确认方案

采用右侧编辑面板，而不是弹窗或独立编辑页。

原因:

- 与当前 `VaultPage` 的列表 + 详情结构一致，新增时不丢列表上下文。
- 比 `ContentDialog` 更适合身份、银行卡等长表单。
- 比独立编辑页更少导航状态和页面生命周期复杂度。

字段覆盖采用“完整字段 + 分组折叠”。

原因:

- 草稿模型按 Bitwarden / Vaultwarden 类型准备完整字段，后续接 API 时不用推翻结构。
- UI 默认展示高频字段，低频字段收进 `Expander`，避免右侧面板过长。
- 保留原版的分区感: 项目详细信息、类型字段、自动填充、附加选项。

## Vaultwarden 契约依据

创建个人条目走:

```http
POST /api/ciphers
```

请求结构参考 `D:\Code\vaultwarden\src\api\core\ciphers.rs` 的 `CipherData`:

- `type`: `Login=1`、`SecureNote=2`、`Card=3`、`Identity=4`、`SshKey=5`
- `name`
- `notes`
- `folderId`
- `organizationId`
- `key`
- `fields`
- `favorite`
- `reprompt`
- `passwordHistory`
- `login` / `secureNote` / `card` / `identity` / `sshKey`: 按 `type` 仅填一个

Vaultwarden 对类型专属对象使用 JSON 原样存储，再在 `Cipher::to_json` 中按类型返回到匹配字段。服务端只做少量兼容修正:

- Login 会从 `uris[0].uri` 兼容生成 `uri`。
- SecureNote 无效时会修正为 `{ "type": 0 }`。
- SSH 返回时要求 `keyFingerprint`、`privateKey`、`publicKey` 三项非空，否则 `sshKey` 会被置为 `null`。

因此这一轮 UI 草稿保存为明文 mock 模型；后续真实 API 任务再由加密层把字段统一转为 EncString。

## UI 结构

### 新增入口

`VaultPage` 列表工具栏的“新增”按钮改为 WinUI `Button` + `MenuFlyout`。

菜单项:

- 登录
- 支付卡
- 身份
- 笔记
- SSH 密钥
- 分隔线
- 文件夹

选择五种 Cipher 类型时进入右侧编辑态。文件夹入口本轮保留为占位命令或禁用状态，避免把文件夹创建混入 Cipher 编辑器任务。

### 右侧编辑态

`VaultPage` 右侧区域根据 ViewModel 状态切换:

- 未选择项目: 当前空状态。
- 选择项目: 当前详情模板。
- 正在新增: 编辑表单。

编辑态顶部:

- 标题: `新增登录` / `新增支付卡` / `新增身份` / `新增笔记` / `新增 SSH 密钥`
- 类型选择 `ComboBox`
- 收藏星标按钮
- `取消` 按钮
- `保存` 按钮

类型切换时:

- 保留通用字段: `Name`、`FolderId`、`Favorite`、`Reprompt`、`Notes`、`CustomFields`。
- 切换类型专属草稿。
- 如果用户已经填写类型专属字段，本轮不做复杂迁移提示；切换后保留原类型草稿在内存中，切回时仍可看到。

### 分区

通用分区:

- 项目详细信息: 项目名称、文件夹
- 附加选项: 备注、主密码重新提示、自定义字段

类型分区:

- 登录凭据
- 支付卡信息
- 身份信息
- 安全笔记
- SSH 密钥

登录额外分区:

- 自动填充选项: URI 列表、URI 匹配规则

## 字段设计

### 通用字段

- `Name`: 必填
- `FolderId`: 可空
- `Favorite`: 默认 `false`
- `Reprompt`: `0` 或 `1`
- `Notes`: 可空
- `CustomFields`: 支持 text、hidden、boolean 三类

自定义字段 UI:

- “添加字段”追加一行。
- 每行包含名称、类型、值、删除按钮。
- hidden 字段在详情回显时复用隐藏/显示行为。

### Login

默认展开:

- `username`
- `password`
- `totp`
- `uris[0].uri`

高级/重复项:

- `uris[].match`
- 添加多个 URI
- `passwordRevisionDate` 后续真实密码生成器接入时再写

校验:

- `Name` 必填。
- 用户名、密码、URI、TOTP 不强制必填，贴近 Bitwarden 可保存空登录的行为。

### Card

默认展开:

- `cardholderName`
- `number`
- `expMonth`
- `expYear`
- `code`

高级:

- `brand`

校验:

- `Name` 必填。
- 本轮不强制卡号、CVV 格式，避免 UI 层引入地区和品牌规则。

### Identity

默认展开:

- `title`
- `firstName`
- `middleName`
- `lastName`
- `email`
- `phone`
- `address1`
- `city`
- `country`

高级:

- `username`
- `company`
- `ssn`
- `passportNumber`
- `licenseNumber`
- `address2`
- `address3`
- `state`
- `postalCode`

回显:

- 详情页 `FullName` 由 `firstName/middleName/lastName` 组合。
- 没有姓名时，列表主标题仍用 `Name`。

### SecureNote

字段:

- `secureNote.type = 0`
- 主要内容使用通用 `Notes`

校验:

- `Name` 必填。

### SshKey

字段:

- `privateKey`
- `publicKey`
- `keyFingerprint`

校验:

- `Name` 必填。
- `privateKey`、`publicKey`、`keyFingerprint` 三项必填，因为 Vaultwarden 返回时要求这三项非空。

## ViewModel 与模型

新增 `CipherEditorDraft`:

- 继承 `ObservableObject`
- 提供 `CreateDefault(VaultItemKind type)`
- 通用属性: `Type`、`Name`、`FolderId`、`Favorite`、`Reprompt`、`Notes`、`CustomFields`
- 类型专属属性: `Login`、`Card`、`Identity`、`SecureNote`、`SshKey`
- 派生属性: `IsLogin`、`IsCard`、`IsIdentity`、`IsSecureNote`、`IsSshKey`
- 方法: `Validate()` 或 `HasRequiredData()`

新增类型草稿:

- `LoginEditorDraft`
- `CardEditorDraft`
- `IdentityEditorDraft`
- `SecureNoteEditorDraft`
- `SshKeyEditorDraft`
- `CustomFieldEditorDraft`
- `LoginUriEditorDraft`

扩展 `VaultViewModel`:

- `IsEditing`
- `EditorDraft`
- `EditorError`
- `BeginAdd(VaultItemKind type)`
- `CancelEdit()`
- `SaveDraft()`
- `ChangeEditorType(VaultItemKind type)`

保存 mock 时:

- 生成新的 `CipherDetail` 派生对象。
- 生成对应 `CipherListItem`。
- 加入 `Items`。
- 调用 `ApplyFilter()` 刷新当前过滤结果。
- 将 `SelectedItem` 指向新项目。
- 退出编辑态并显示详情。

## XAML 实现边界

`VaultPage.xaml`:

- 新增按钮改为 `Flyout` 或 `MenuFlyout`。
- 右侧区域增加编辑态 `ContentControl` 或独立编辑 `StackPanel`。
- 类型专属表单用 DataTemplate 或显隐绑定，优先沿用当前详情模板的运行时类型切换模式。
- 使用 WinUI 原生控件: `TextBox`、`PasswordBox` 或带显示按钮的 `TextBox`、`ComboBox`、`CheckBox`、`Expander`、`NumberBox`、`AppBarButton` / `Button`。
- 输入控件横向 `Stretch`，避免固定宽度在窄面板中溢出。
- 不在卡片内套卡片；每个分区是单层 8px 左右圆角容器。

`VaultPage.xaml.cs`:

- 增加编辑模板切换。
- 保持详情模板切换逻辑清晰，不把所有状态写入 XAML 事件。

## 测试策略

遵循测试先行。

ViewModel / 模型测试:

- 默认创建 Login 草稿，通用字段默认值正确。
- 类型切换保留通用字段。
- `Name` 为空时保存失败。
- SSH 三项为空时保存失败。
- 保存 Login 草稿后，列表新增项目并选中新项。
- 保存 Card / Identity / SecureNote / SshKey 后生成对应详情类型。
- 当前过滤器为类型过滤时，新项目只有类型匹配才出现在 `FilteredItems`。

XAML 结构测试:

- 新增菜单包含登录、支付卡、身份、笔记、SSH 密钥、文件夹。
- 编辑表单主要输入控件 `HorizontalAlignment="Stretch"`。
- 编辑表单不引入自定义滚动条或嵌套 `ScrollViewer`。
- 保存/取消按钮存在于编辑态顶部。

验证命令:

```powershell
dotnet test
```

如果 WinUI 构建受本机 Windows App SDK 或 Visual Studio 组件影响，则至少运行可执行的 `App.Tests`，并在结果中说明限制。

## 非目标

本轮不实现:

- EncString 加密。
- `POST /api/ciphers` 真实提交。
- 组织条目和 collection 分享。
- 附件。
- 密码历史。
- 真实文件夹创建/编辑。
- SSH 密钥生成器。
- 密码生成器弹窗联动。

## 后续接 API 的预留

真实 API 接入时增加 `CipherCreateRequest` DTO，映射方式:

- 通用字段先加密为 EncString。
- `fields` 每个自定义字段的 `name`、`value` 加密。
- 按 `Type` 填充 `login` / `secureNote` / `card` / `identity` / `sshKey` 中的一个。
- `favorite`、`folderId`、`reprompt` 仍保持明文元数据。
- `passwordHistory`、`lastKnownRevisionDate` 创建时不传。

SSH 创建前仍需本地校验三项非空，避免服务端保存后同步返回 `sshKey = null`。
