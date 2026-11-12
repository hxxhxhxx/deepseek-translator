# 分发与签名

当前状态（2026-11-12）：

| 渠道 | 状态 |
| --- | --- |
| Gitee Releases + `latest.json` 自动更新 | 已上线，1.9.1（安装包 SHA256 `666d5ffe…`） |
| GitHub 镜像 | 已同步 1.9.1（源码 commit `e45453d`），Release v1.9.1 已上传安装包 |
| winget | PR [#470707](https://github.com/microsoft/winget-pkgs/pull/470707) 已提交（1.7.1）；CLA 已确认，等待 moderator |
| Microsoft Store | 未提交；MSIX 清单、图标和说明已准备，需要 Partner Center 账号 |
| 代码签名 | 仍是内部自签证书；SignPath Foundation 免费 OSS 申请因表单 reCAPTCHA 无法提交，邮件已备好待发 |

---

## 1. winget

清单在：

```text
packaging\winget\manifests\h\Hexin\DeepSeekTranslator\1.6.2\
  Hexin.DeepSeekTranslator.yaml
  Hexin.DeepSeekTranslator.installer.yaml
  Hexin.DeepSeekTranslator.locale.en-US.yaml
  Hexin.DeepSeekTranslator.locale.zh-CN.yaml
```

本地校验：

```powershell
winget validate --manifest .\packaging\winget\manifests\h\Hexin\DeepSeekTranslator\1.6.2
```

本地试装（会覆盖已安装版本）：

```powershell
winget install --manifest .\packaging\winget\manifests\h\Hexin\DeepSeekTranslator\1.6.2
```

当前 PR：[#470707](https://github.com/microsoft/winget-pkgs/pull/470707)（1.7.1）

- CLA 已确认（`@microsoft-github-policy-service agree`）；
- winget-pkgs 自动校验进行中（URL、清单、策略、扫描、安装、元数据等）；
- 现在只等社区 moderator 人工审核，通常几小时到几天。

以后每个版本：复制目录改版本号，把 `InstallerUrl` 指向新安装包、`InstallerSha256` 换成新值
（`releases\v<版本>\latest.json` 里的 `sha256`），再发 PR。

> 安装包同时放在 Gitee 和 GitHub Release；清单里的 URL 已改成 GitHub Release
> `https://github.com/hxxhxhxx/deepseek-translator/releases/download/v1.6.2/...`，海外校验机下载更稳。

---

## 2. GitHub / Gitee 双源

GitHub 仓库：<https://github.com/hxxhxhxx/deepseek-translator>（已同步到 1.9.1，源码 commit `e45453d`）。Gitee 源码 commit `bf5cd3c`；另有发布文档/更新清单同步提交。

Release：<https://github.com/hxxhxhxx/deepseek-translator/releases/tag/v1.9.1>（附安装包与译文选词对照说明）。Gitee：<https://gitee.com/hexin1225/deepseek-translator/releases/tag/v1.9.1>。

同步脚本：`scripts/github-sync.ps1`

- 只导出可公开的目录：`src`、`tests`、`scripts`、`installer`、`docs`、`packaging`、`.github`；
- 自动排除 `bin`、`obj`、`releases`、`tools`、缓存与临时文件；
- 用 GitHub Git Data API 一次提交（不依赖本地 git），并用公开 README 覆盖仓库根 `README.md`；
- 公开 README / LICENSE / CI 模板分别在 `packaging/public-README.md`、`packaging/public/LICENSE`、`packaging/github/build.yml`，同步时会映射到仓库根和 `.github/workflows`；
- `-DryRun` 只列文件，不联网。

准备 token（需要 `repo` 权限；要更新 `.github/workflows` 还需要 `workflow` 权限）：

```powershell
setx GITHUB_TOKEN "<github_pat_...>"
# 新开终端后
powershell -File .\scripts\github-sync.ps1            # 真正推送
powershell -File .\scripts\github-sync.ps1 -DryRun    # 先看会推哪些文件
```

推送后 GitHub 上的源码、README、LICENSE、winget 清单都会与本地一致；Gitee 仍是发布与自动更新源。

---

## 3. 正式代码签名（消除“未知发布者”）

### 为什么现在会提示

安装包目前用内部自签证书 `CN=DeepSeek Translator (Internal)` 签名。自签证书不被 Windows 信任，
SmartScreen 就会显示“未知发布者”。**自签证书不能对外分发**，只适合内部测试。

### 可选方案

| 方案 | 大致费用 | SmartScreen | 适合 |
| --- | --- | --- | --- |
| OV 代码签名证书 | 约 $200–400/年 | 需要下载量积累信誉，前期仍可能提示 | 个人/小团队 |
| EV 代码签名证书 | 约 $300–600/年 + 硬件令牌 | 通常立即获得信誉 | 正式对外分发 |
| Azure Trusted Signing | 约 $9.99/月 | 与 OV 类似，需积累 | 有 3 年可验证经营历史的组织 |
| Certum Open Source Developer | 约 €25–50/年 | 需积累 | 开源项目（本项目 MIT，符合） |
| Microsoft Store | Partner Center 账号费 | Store 安装不触发 SmartScreen | 想彻底绕过签名问题 |

> 价格为公开报价的大致范围，以 CA 实际报价为准。按 CA/B Forum 要求，OV/EV 私钥必须放在
> 硬件令牌或云 HSM 中，CA 会寄 USB Key 或提供云签名服务。

### 购买与配置流程

1. 选 CA（DigiCert / Sectigo / SSL.com / Certum / Azure Trusted Signing）；
2. 提交身份验证（个人护照/身份证，或营业执照 + 电话回访）；
3. 收到证书（USB 令牌或 PFX/云 HSM）；
4. 让证书可用后，二选一配置：

```powershell
# 方式 A：证书在“当前用户\个人”证书库（USB 令牌常见）
setx DSH_SIGN_THUMBPRINT "<证书指纹>"

# 方式 B：PFX 文件
setx DSH_SIGN_PFX "D:\certs\codesign.pfx"
setx DSH_SIGN_PFX_PASSWORD "<密码>"
```

5. 重新打包（`build-installer.ps1` 会调用 `sign.ps1` 自动签名 exe/dll/安装包）：

```powershell
powershell -File .\scripts\build-installer.ps1 -Notes "1.6.2"
```

6. 验证：

```powershell
Get-AuthenticodeSignature .\releases\v1.6.2\DeepSeekTranslator-1.6.2-setup.exe
```

### 关于 SmartScreen 的预期

- **EV 证书**：一般签名后很快不再提示；
- **OV 证书**：新证书仍可能提示“未知发布者”，需要真实下载量和时间积累信誉；
- **Microsoft Store**：走 Store 安装由微软重新签名，不触发 SmartScreen；
- 任何证书都不要把私钥提交到仓库；CI 里用 Secrets 保存 PFX 的 base64 和密码。

---

## 4. Microsoft Store

Store 不是“上传一个 exe”就行，需要 **MSIX 包 + Partner Center 账号**，并且有两处应用行为要改：

1. **关闭应用内自动更新**：Store 政策不允许应用绕过 Store 自更新；Store 版应把
   `AutoCheckUpdate` 设为 `false` 并隐藏“检查更新”；
2. **开机自启改用 StartupTask**：MSIX 应用写 `HKCU\...\Run` 会被虚拟化/不生效，需要在
   `Package.appxmanifest` 里声明 `uap5:StartupTask`，由用户在系统设置里开启。

准备步骤：

1. 注册 [Partner Center](https://partner.microsoft.com/)（个人约 $19 一次性，公司约 $99，以页面为准）；
2. 预留应用名称 `DeepSeek 划词翻译`；
3. 用 Windows SDK 的 `MakeAppx.exe` 打包自包含发布目录（模板见 `packaging\msix\Package.appxmanifest`）；
4. 上传 MSIX，填写年龄分级、隐私政策 URL、截图、支持信息；
5. 提交认证（通常 1–3 个工作日）。

MSIX 模板里的 `Identity Name` / `Publisher` 必须与 Partner Center 分配的值一致；
`Publisher` 还要与签名证书主题一致（Store 会重新签名，但本地测试需要自签）。

> Store 版和 Gitee 版可以共存，但建议二选一作为主渠道，避免自动更新逻辑冲突。


---

## 5. 2026-11-12 当前进度

| 事项 | 结果 |
| --- | --- |
| Gitee 发布 | v1.9.1，安装包 53,516,440 字节，SHA256 `666d5ffe…`，`latest.json` 已更新 |
| GitHub 同步 | 已同步 1.9.1，源码 commit `e45453d`；文档与更新清单另有同步提交 |
| GitHub Release | v1.9.1，安装包 53,516,440 字节，SHA256 `666d5ffe…` |
| winget PR | #470707（1.7.1），CLA 已确认，等待 moderator；1.9.1 尚未提交 |
| MSIX 准备 | `packaging/msix/Package.appxmanifest` + `Assets/`（6 张图标，由 `scripts/make-msix-assets.ps1` 生成） |
| SignPath 申请 | **未提交**：表单的 reCAPTCHA 依赖 `www.gstatic.com`，浏览器加载失败；邮件草稿已备好，待发到 support@signpath.io |
| 正式证书 | 未购买 |

> 分发相关的公开文件：`packaging/public-README.md`（GitHub 首页）、`packaging/public/LICENSE`、
> `packaging/github/build.yml`（CI）。旧的 `github-export/` 快照已删除，避免再推旧代码。
>
> 本次已同步两边源码、公开 README、隐私说明、发行说明与分发文档。
> Gitee 为自动更新主源，GitHub 同时提供源码和安装包镜像；1.9.1 源码和安装包均已上传。

---

## 6. SignPath Foundation（免费 OSS 签名，进行中）

SignPath Foundation 给开源项目免费签发代码签名证书（证书主体是 SignPath Foundation）。
本项目 MIT、有公开仓库和 Release，符合基本条件，但需要人工审核“可验证声誉”。

已准备：

- 申请材料：项目名、仓库、Release、Privacy、Category=Productivity、Build=GitHub Actions、Maintainer=Individual；
- Release v1.6.2 的说明里已加入 SignPath 要求的声明与隐私说明。

当前阻塞：申请表单的 reCAPTCHA 脚本来自 `www.gstatic.com`，在本机浏览器里 `ERR_CONNECTION_CLOSED`，
点 Submit 不会发出请求。备选是发邮件到 `support@signpath.io`（正文见会话记录）。

> SignPath 的证书发布者显示为 “SignPath Foundation”，不是应用名；它能消除“未知发布者”，
> 但仍需遵守其 Code of Conduct、MFA、每次发布人工批准等要求。


### 邮件模板（备用）

```text
To: support@signpath.io
Subject: OSS code signing application - DeepSeek Translator (MIT)

Hello SignPath Foundation,

I would like to apply for the free OSS code signing subscription. The web form
at https://signpath.org/apply.html does not submit in my browser (reCAPTCHA
from gstatic fails to load), so no request is sent.

Project: DeepSeek 划词翻译 (DeepSeek Translator)
Repo: https://github.com/hxxhxhxx/deepseek-translator
License: MIT
Release: https://github.com/hxxhxhxx/deepseek-translator/releases/tag/v1.6.2
Category: Productivity
Description: Windows 11 tray app translating selected text in Word, browsers,
PDFs, WeChat and PowerPoint via the DeepSeek API.
Build system: GitHub Actions
Maintainer type: Individual
Reputation: GitHub repository with public releases; winget PR #467817 passed
all 10 winget-pkgs automated checks.
Contact: Xin He <mihexin@outlook.com>

Could you create the application manually, or advise an alternative route?

Thank you,
Xin He
```
