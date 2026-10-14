---
version: alpha
name: "日志工作台"
description: "面向 Windows 日志排查的双栏文件与文本工作台"
colors:
  primary: "#245BCC"
  background: "#EDF1F7"
  surface: "#FFFFFF"
  text: "#182B49"
  muted: "#586980"
  border: "#D2DBE8"
  hover: "#E3ECFC"
  danger: "#AC2430"
  console: "#14253D"
  consoleText: "#E0E8F4"
typography:
  sans:
    fontFamily: "Microsoft YaHei UI, Segoe UI"
  mono:
    fontFamily: "Consolas, Microsoft YaHei UI"
rounded:
  DEFAULT: "5px"
  panel: "7px"
  badge: "4px"
  item: "3px"
spacing:
  page: "24px"
  panel: "16px"
  action: "8px"
components:
  button: {}
  input: {}
  table: {}
  editor: {}
  dialog: {}
  select: {}
---

# 日志工作台 Design System

## Overview

### Creative North Star
参考设备诊断工作台：左侧索引、右侧深蓝日志纸面，让长日志文本成为视觉中心。

### Product context and register
- 使用者与目的：Windows 桌面日志排查与归档，来自当前用户需求。
- 市场：未限定地区；界面简体中文，日期本机时间，数字 zh-CN。
- 环境：鼠标与键盘，常驻双栏工具；无需互联网。
- Register：product/tool。
- 特征：浅色文件索引搭配深蓝等宽日志区域。
- 克制：原生窗口、系统文件选择和系统确认，不增加装饰动画。
- 避免：营销首页、统计卡片、隐式删除原日志。
- 所有权：Main/Themes/Theme.xaml 为运行时规范来源，本文记录对应值。WPF px 对应设备无关单位 DIP。

## Colors
primary 用于主要动作及焦点；background 为工作区；surface 为文件列表；text/muted 分别为主要/辅助文本；border 为边线；hover 为选择；danger 用于错误文字；console/consoleText 为日志编辑区域。系统滚动条在高对比度环境保留操作系统机制。

## Typography
sans 用于中文界面，正文 13 DIP，标题 27/17 DIP；mono 用于原始日志，13 DIP。技术路径可以选择复制，不强制换行；日志保留原始换行。

## Layout
初始 1320×850，最小 1000×650。24 DIP 外边距，16 DIP 面板间距，8 DIP 按钮间距。左侧文件列表 365 DIP，可通过分隔条调整。列表与日志分别拥有内部滚动，不包裹于额外 ScrollViewer。日期选择使用宽 440 DIP 的 WPF Popup，不挤占窄窗口的日志区域；内部日期列表最多 220 DIP，超出可滚动。

## Elevation & Depth
使用背景/白色面板/深蓝文本区域区分层级，1 DIP 边框，无阴影。

## Shapes
控件 5 DIP 圆角、面板 7 DIP、状态标签 4 DIP、下拉选项高亮 3 DIP。按钮最小高度 36 DIP；压缩操作区统一 40 DIP 高度与 8 DIP 间距，主要压缩按钮固定宽度 126 DIP，切换模式时不改变动作区宽度。

## Components

### Foundational visual states
默认、悬停/按下透明度、键盘焦点边线、禁用状态均由 Theme.xaml 统一。文件选中使用 hover；错误状态通过明确文字及 danger 色同时表达。加载使用原生 ProgressBar，不改变面板位置。

### Buttons and actions
普通操作白底边框，压缩/保存蓝底白字，取消为普通按钮。忙碌时禁用重复操作，取消保持可用。

解压 ZIP 使用与打开/压缩并列的普通操作按钮；选择源 ZIP 与目标目录后按原路径写入，不覆盖现有文件，进度与取消复用状态栏工作区。

### Navigation and data display
文件列表使用 WPF DataGrid，单选、排序、虚拟化、文件名筛选及清空。双击和 Enter 与“查看所选”对应。

日志编辑区上方放置轻量搜索栏，使用大小写不敏感的当前内容查找；上一个/下一个循环定位，命中项在原始文本区选中并按行列滚动到可视区域。搜索状态显示“第 2 / 共 10 处”，包含重叠匹配，关键词或内容修改后重新统计。搜索后保留搜索框或按钮焦点，编辑器失焦时仍显示选区高亮。搜索仅浏览当前已加载文本，预览计数带“预览”前缀，不隐式读取全文。

### Forms and overlays
TextBox 保留原生键盘行为。固定选项 ComboBox 使用 Theme.xaml 统一模板：白底、细边框、5 DIP 圆角、自绘箭头、蓝色悬停/焦点；弹出列表与字段等宽，圆角白底，选中项有浅蓝底和勾选标记。保留原生 ComboBox 的键盘、选项选择与无障碍逻辑，不使用动画。文件选择、未保存提示仍使用系统对话框。日期 Popup 内为原生 CheckBox，多选即时生效；支持全选、清空、完成、点击外部关闭与 Escape，完成/Escape 返回选择按钮焦点。日志预览只读，全文可编辑。状态行承载成功、取消、部分完成和错误信息。

### Iconography
以中文文字标识操作，不使用图标代替动作名称。应用图标使用主色圆角底、日志纸张与 ZIP 拉链的组合，沿用主界面的蓝色和深蓝日志区域配色；ICO 提供 16–256 px 多尺寸帧，供任务栏和窗口按 DPI 显示。

### Motion
不添加装饰动画，进度反馈是唯一动态状态。

### Content and data visualization
统一使用“打开目录”“打开 ZIP”“保存修改”；显示真实日志文件数量及压缩结果，不显示模拟数据。

### Runtime mapping
颜色依次对应 PrimaryBrush、BackgroundBrush、SurfaceBrush、TextBrush、MutedBrush、BorderBrush、HoverBrush、ErrorBrush、ConsoleBrush、ConsoleTextBrush。字体对应 BodyFont/CodeFont。所有颜色定义仅在 Theme.xaml；MainWindow 使用资源引用。Button 与日期 ToggleButton 共用 ActionButton 样式。

## Do's and Don'ts
- 保留原日志，压缩与保存有明确完成结果。
- 忙碌、错误、无结果、未保存状态遵循相同语义。
- 不将预览内容写回完整文件。
- 不因主题设计隐藏滚动条或键盘焦点。
