# 第三方声明 / Third-Party Notices

本文列出已核对的 `win-x64` 自包含构建中包含的第三方组件。各组件保留各自的版权与许可证，不受 EndfieldMonitor 自身的 MIT 许可证替代。本项目使用上游提供的二进制文件，未修改这些组件的源代码。

重新构建时依赖或运行时版本可能变化，公开分发前请用实际产物的 `EndfieldMonitor.deps.json` 核对清单。

## Mozilla Public License 2.0

下表提供与随附二进制对应的源码。依据 [MPL 2.0 第 3.2 节](https://www.mozilla.org/en-US/MPL/2.0/)，接收者可以通过这些链接获取适用 MPL 2.0 的源码。

| 组件 | 版本 | 对应源码 |
| --- | --- | --- |
| LibreHardwareMonitorLib | 0.9.6 | [LibreHardwareMonitor 对应提交](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/tree/3d331e3370efb858411f19511373eff65a218701) |
| DiskInfoToolkit | 1.1.2 | [DiskInfoToolkit 对应提交](https://github.com/Blacktempel/DiskInfoToolkit/tree/25319eae5781e75bcf141e844ceab2afe94d40ea) |
| RAMSPDToolkit-NDD | 1.4.2 | [RAMSPDToolkit 对应提交](https://github.com/Blacktempel/RAMSPDToolkit/tree/3b47b960e0830fef344624ad5e389675d5f0a1ce) |
| BlackSharp.Core | 1.0.7 | [BlackSharp 对应提交](https://github.com/Blacktempel/BlackSharp/tree/c70b735c6cec123ee8a046ac4a0bc6c606f52cf0) |

## PawnIO.Modules（LGPL 2.1）

`LibreHardwareMonitorLib.dll` 内嵌了 **PawnIO.Modules 0.1.6** 的签名模块。[对应源码](https://github.com/namazso/PawnIO.Modules/tree/4aa792beb020a14c8261072e9786d2dfb38489d9)可从 PawnIO.Modules 项目获取；[LibreHardwareMonitor 上游声明](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/blob/3d331e3370efb858411f19511373eff65a218701/THIRD-PARTY-NOTICES.txt)收录了模块适用的 [LGPL 2.1 许可证全文](https://www.gnu.org/licenses/old-licenses/lgpl-2.1.html)。

这些模块不同于 PawnIO 内核驱动。EndfieldMonitor **不随附或安装驱动本身**。如需读取部分底层传感器，可从 [PawnIO 官方渠道](https://pawnio.eu/)单独获取驱动。

## Apache License 2.0（HidSharp）

> HIDSharp — Copyright 2010-2025 James F. Bellinger <http://software.seekye.com/hidsharp>

随附版本为 [HidSharp 2.6.4](https://www.nuget.org/packages/HidSharp/2.6.4)。分发时应随包提供该 NuGet 包内的原始 `LICENSE.txt`，其中包含版权声明和 [Apache License 2.0](https://www.apache.org/licenses/LICENSE-2.0) 全文。

## Mono 和 .NET

| 组件 | 已核对版本 | 许可证和声明来源 |
| --- | --- | --- |
| Mono.Posix.NETStandard | 1.0.0 | MIT；参见 [NuGet 包指向的 Mono 许可说明](https://go.microsoft.com/fwlink/?linkid=869050)和 [Mono.Posix 源码](https://go.microsoft.com/fwlink/?linkid=869051) |
| System.IO.Ports | 10.0.3 | MIT；参见 [对应 NuGet 包](https://www.nuget.org/packages/System.IO.Ports/10.0.3) |
| System.Management | 10.0.2 | MIT；参见 [对应 NuGet 包](https://www.nuget.org/packages/System.Management/10.0.2) |
| .NET 运行时（`Microsoft.NETCore.App.Runtime.win-x64`） | 10.0.15 | MIT 及随包附带的第三方声明；参见 [运行时包](https://www.nuget.org/packages/Microsoft.NETCore.App.Runtime.win-x64/10.0.15) |
| Windows Desktop Runtime（含 WPF） | 10.0.15 | WPF 自身采用 MIT；随附其他组件保留各自条款，参见 [运行时包](https://www.nuget.org/packages/Microsoft.WindowsDesktop.App.Runtime.win-x64/10.0.15)和 [WPF 上游](https://github.com/dotnet/wpf) |

当前自包含产物中的 `System.IO.FileSystem.AccessControl.dll` 由 .NET 运行时包提供，不是单独分发的 5.0.0 NuGet 包。分发时应保留各组件原有的版权信息、许可证及适用的第三方声明，不能用一份 .NET 版权声明替代其他项目的声明。

## 分发前检查

本文件是组件清单和源码索引，**不替代随发布包提供的许可证全文或上游声明**。公开上传二进制包前，请一并提供 EndfieldMonitor 的 `LICENSE`、相关依赖的原始许可证和上游声明，并确保接收者能获得与包内 MPL 和 LGPL 二进制相对应的源码。对于内嵌的 PawnIO 模块，还应核对 LGPL 2.1 对分发方式的要求，不能仅靠列出链接来替代合规检查。
