# Privacy Notice / 隐私说明

**DeepSeek 划词翻译 (DeepSeek Translator)** is a local Windows tray application. This notice explains what data leaves your device and what is stored locally.

Last updated: 2026-11-11

---

## English

### 1. Data sent to DeepSeek

The app contacts the DeepSeek API **only when the user explicitly triggers a translation, word explanation, connection test, or—after opting in—selects translated text for source alignment**.

When translating, it sends over HTTPS to `https://api.deepseek.com/chat/completions`:

- the selected text;
- the translation direction and a system prompt;
- glossary entries that match the text (if the user configured a glossary).

For word explanation, it also sends the selected word and the displayed original context.

**Optional source alignment is off by default.** After the user enables it and finishes selecting translated text, an extra request sends the complete displayed original and translation, selected text, UTF-16 selection position and surrounding translation context. This may incur extra API charges. At most 64 alignment results and their text/position/model cache keys are retained in memory only; no alignment text or credentials are written to logs or disk. Turning the feature off, clearing caches or exiting clears that memory cache. Alignment is an AI estimate and may be incorrect.

The DeepSeek API key is sent in the `Authorization` header. It is stored locally with Windows DPAPI and is never written to settings, logs, or any webpage.

The app does **not** send: your name, email address, device identifiers, clipboard history, screenshots, window titles, browsing history, or usage analytics.

DeepSeek's processing of API requests is governed by DeepSeek's privacy policy:
<https://cdn.deepseek.com/policies/zh-CN/deepseek-privacy-policy.html>

### 2. Local data

All application data stays under `%LOCALAPPDATA%\DeepSeekTranslator`:

| File | Contents | Notes |
| --- | --- | --- |
| `settings.json` | model, hotkeys, overlay size/font, glossary, compatibility list | no API key |
| `secret.dat` | API key encrypted with Windows DPAPI | current Windows user only |
| `translation-cache.json` | up to 200 translations, max 30 days: translated text, model, timestamps; the source text is stored only as a SHA-256 key | clearable in Diagnostics |
| `logs\app.log` | local run log: app name, error codes, character counts; may include the foreground window title/path for troubleshooting | no selected text; local only |
| `translation-cache` / glossary | user content | removable with the folder |

Screenshots used by OCR are processed in memory by the built-in Windows OCR engine. They are never written to disk or uploaded; only the recognized text may be sent for translation if the user asks for it.

### 3. Network connections

- `api.deepseek.com` — translation / word explanation / optional source alignment (only on user action, alignment requires opt-in).
- `gitee.com` — update check (`latest.json`); the request contains no user data.
- Read-aloud uses the built-in Windows OneCore speech engine. The text is synthesized on-device and no audio is stored or uploaded.
- No telemetry, analytics, advertising, or crash-reporting SDK.

### 4. Hooks and clipboard

Global hotkeys can remain registered while the app runs unless paused. Low-level mouse/keyboard hooks may run to detect selection gestures, configured double-tap hotkeys and overlay dismissal. They do not record or transmit keystrokes. The clipboard is read or written only when the user triggers capture or copy; source alignment does not use the clipboard.

### 5. Third-party components

- .NET 8 / WPF (Microsoft).
- `System.Security.Cryptography.ProtectedData` 8.0.0 (Microsoft, for DPAPI).
- No other NuGet packages or third-party SDKs.

### 6. Retention and deletion

Exit the app and delete `%LOCALAPPDATA%\DeepSeekTranslator` to remove settings, glossary, cache, logs, and the encrypted API key. The translation cache can also be cleared from the Diagnostics window.

### 7. Contact

- Issues: <https://github.com/hxxhxhxx/deepseek-translator/issues>
- Email: <mihexin@outlook.com>

---

## 中文

### 1. 发送给 DeepSeek 的数据

只有在你**主动触发**翻译、单词详解、连接测试，或在明确开启「选词对照原文」后选择译文时，应用才会访问 DeepSeek API。

翻译时会通过 HTTPS 发送到 `https://api.deepseek.com/chat/completions`：

- 选中的文本；
- 翻译方向和系统提示词；
- 匹配到的术语表条目（如果你配置了术语表）。

单词详解还会发送选中的单词和浮层显示的原文上下文。

**选词对照原文默认关闭。** 你开启后，在译文中选词并停顿，会额外发送当前完整原文、完整译文、选中文字、UTF-16 选区位置及附近译文上下文，可能产生额外 API 费用。最多 64 条结果及文本/位置/模型缓存键仅保留在内存中，不把对齐文本或凭据写入日志、磁盘；关闭该功能、清理缓存或退出应用时清除。对应关系属于 AI 估计，可能出错。

API Key 放在 `Authorization` 请求头里，本地用 Windows DPAPI 加密保存，不写入设置、日志或任何网页。

应用**不会**发送：姓名、邮箱、设备标识、剪贴板历史、截图、窗口标题、浏览历史或使用统计。

DeepSeek 对 API 请求的处理受其隐私政策约束：
<https://cdn.deepseek.com/policies/zh-CN/deepseek-privacy-policy.html>

### 2. 本地数据

所有数据都保存在 `%LOCALAPPDATA%\DeepSeekTranslator`：

| 文件 | 内容 | 说明 |
| --- | --- | --- |
| `settings.json` | 模型、快捷键、浮层尺寸/字号、术语表、兼容列表 | 不含 API Key |
| `secret.dat` | 用 Windows DPAPI 加密的 API Key | 仅当前 Windows 用户可解密 |
| `translation-cache.json` | 最多 200 条、30 天内的译文、模型、时间；原文只以 SHA-256 键形式存在 | 可在诊断面板清除 |
| `logs\app.log` | 本地运行日志：应用名、错误码、字符数；排障时可能包含前台窗口标题/路径 | 不含选中文本，仅本地 |

OCR 用的截图仅在内存中由 Windows OCR 处理，不落盘、不上传；只有识别出的文字在用户要求翻译时才可能发送。

### 3. 网络连接

- `api.deepseek.com`：翻译/单词详解/可选原文对照，仅在用户操作时访问；对照需先明确开启；
- `gitee.com`：检查更新 `latest.json`，不包含用户数据；
- 朗读使用 Windows 内置 OneCore 语音，在本机合成，不保存、不上传音频。
- 无遥测、分析、广告或崩溃上报 SDK。

### 4. 钩子与剪贴板

全局快捷键在应用运行期间可持续注册（暂停时除外）。低级鼠标/键盘钩子可能用于识别划词手势、配置的双击式快捷键，以及关闭浮层的点击/按键；不记录、不上传按键内容。剪贴板只在用户触发取词或复制时读写，原文对照不使用剪贴板。

### 5. 第三方组件

- .NET 8 / WPF（Microsoft）；
- `System.Security.Cryptography.ProtectedData` 8.0.0（Microsoft，DPAPI）；
- 没有其他 NuGet 包或第三方 SDK。

### 6. 保留与删除

退出应用后删除 `%LOCALAPPDATA%\DeepSeekTranslator`，即可清除设置、术语表、缓存、日志和加密的 API Key；缓存也可以在诊断面板里清除。

### 7. 联系方式

- Issues：<https://github.com/hxxhxhxx/deepseek-translator/issues>
- 邮箱：<mihexin@outlook.com>
