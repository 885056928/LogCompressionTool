# 日志工作台交互约定

业务依据为当前用户要求的两个 .NET 10 项目、压缩/查看/修改/保存接口；仅处理用户明确选择的本地目录或 ZIP。

| 能力 | 统一所有者 | 约定 | 验证 |
|---|---|---|---|
| 文件列表与单选 | MainViewModel.FileView / WPF DataGrid | 本地筛选、排序、虚拟化、Enter/双击查看；打开新来源重置筛选 | VerifyUi |
| 单选下拉 | Theme.xaml / WPF ComboBox | 自定义统一模板，固定非编辑选项；弹出层与字段等宽；原生方向键、Escape 和选项选择逻辑 | VerifyUi 键盘、选项选择、几何及绑定检查 |
| 日期多选 | DaySelectionViewModel / WPF Popup、CheckBox | 实际日期排序、即时勾选、全选/清空；未选日期时压缩不可用；弹窗不挤占编辑区 | VerifyDays / VerifyUi |
| 压缩方式 | MainViewModel.Compression / LogService | 整个目录、每日独立 ZIP、每日 ZIP 合成总 ZIP；仅 ZIP，日期模式仅包含勾选日期 | VerifyDays / VerifyUi / VerifySample |
| 解压 ZIP | MainViewModel.Extraction / LogService.Extraction | 选择 ZIP 和目标目录，保留相对目录；暂存完成后提交，不覆盖同名目标；拒绝重复、绝对或越界路径；取消清理暂存内容 | Verify / VerifyUi |
| 表单/日志编辑 | MainViewModel / WPF TextBox | 预览只读，全文最大 32 MiB；Ctrl+S；原始文本保存 | Verify / VerifyUi |
| 滚动条 | Theme.xaml / WPF ScrollBar | 由系统控件提供拖动、键盘及高对比度支持，面板内滚动 | WPF 渲染 |
| 通知 | MainViewModel.Status | 同一状态行显示加载/成功/取消/错误；失败保留编辑内容 | VerifyUi |
| 未保存提示 | IDialogService / DialogService | 原生 Yes/No/Cancel 分别保存/放弃/取消；默认取消 | VerifyUi |
| 压缩覆盖 | SaveFileDialog | 命名对象并确认覆盖已有 ZIP；源日志保留 | Verify |
| 生命周期 | LogService 临时文件替换 | 在提交前取消，成功替换后不再取消；检测外部改动 | Verify |
| 每日批次 | MainViewModel.Compression | 每日串行独立提交；取消/后续错误保留已完成 ZIP，并显示完成天数；批量覆盖需确认 | VerifyUi |
| 总包编辑 | LogService 嵌套读取与重写 | 支持一层每日 ZIP；保存保留其他日期内容；内部压缩包缓存自动删除，日志不展开到磁盘 | VerifyDays / VerifyUi |

不提供删除、云服务、账户或后台定时操作。界面简体中文，文化 zh-CN，文件时间保留源时间；ZIP 时间受格式范围限制。高对比度属于 Windows 原生控件语义，未承诺完整自定义高对比度主题。
