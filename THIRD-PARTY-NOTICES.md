# 第三方组件与素材

QLaunch 自身代码以 [MIT](LICENSE) 授权。仓库里分发的以下文件来自第三方，各自适用其原始许可，不受 MIT 约束。

## BergamotTranslatorSharp 0.7.0

- 用途：Bergamot 离线翻译引擎的 .NET 封装，通过 NuGet 引用
- 许可：MPL-2.0
- 来源：<https://github.com/Freeesia/BergamotTranslatorSharp>
- 许可全文：<https://licenses.nuget.org/MPL-2.0>

## Mozilla Firefox Translations 英译中模型

- 用途：`models/enzh/`（`model.enzh.intgemm.alphas.bin`、`lex.50.50.enzh.s2t.bin`、`srcvocab.enzh.spm`、`trgvocab.enzh.spm`、`config.txt`）
- 许可：MPL-2.0
- 来源：<https://github.com/mozilla/firefox-translations-models>（`models/prod/enzh`）
- 许可全文：<https://www.mozilla.org/MPL/2.0/>

这两个 MPL-2.0 组件的源码可按上述地址获取。若要修改模型或封装库，请遵守 MPL-2.0 对修改文件的开源要求。

## Everything SDK（`libs/Everything64.dll`）

- 用途：调用 Everything 的文件搜索 IPC 接口
- 版权：voidtools
- 来源：<https://www.voidtools.com/support/everything/sdk/>
- 说明：voidtools 把 SDK 作为免费组件发布，但未在官网发布明确的再分发条款。本仓库保留该 DLL 只是为了让源码构建开箱即用；**如果你要分发 QLaunch 的二进制产物，请先向 voidtools 确认再分发许可**，或让用户自行从官网下载后放进产物目录。

## 其他

- `app.ico`：QLaunch 自己的图标
