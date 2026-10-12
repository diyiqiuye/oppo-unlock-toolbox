# 项目结构

工具箱分两层：`OppoUnlockToolbox.Core` 只放逻辑（不依赖 WPF），`OppoUnlockToolbox` 只放界面。
两边的命名空间都按目录分层，各自的 `GlobalUsings.cs` 统一引入，所以文件里不用逐个写 using。

## Core：逻辑层

```
OppoUnlockToolbox.Core/
  Foundation/    进程与文本的底座
    Cmd.cs            命令行参数转义
    ProcessRunner.cs  起进程、收输出、超时与取消（RunResult / RunOptions）
    FileTools.cs      md5、体积格式化
  Platform/      常量与路径
    AppPaths.cs       程序目录、工作目录、日志/备份目录、随包资源
    AppMeta.cs        应用名、版本、作者、对外链接
    ToolLocator.cs    找 adb / fastboot
    DevicePaths.cs    设备上的目录、分区名、镜像名
    DeviceCatalog.cs  已适配机型、内核、SU 管理器识别规则
    ExploitProfiles.cs 内核↔KMI 映射、bundle 文件别名、DFRoot 常量
  Android/
    Adb.cs            adb / fastboot 的全部封装（shell、push/pull、md5、属性）
    AdbDefault.cs     默认实例
  Devices/
    DeviceMonitor.cs  监听插拔（adb track-devices）
    DeviceProbe.cs    一次只读探测
    DeviceSnapshot.cs 探测结果
    ToolInfo.cs       单个外部工具的落点
  State/
    StateStore.cs     账本：设备信息、分区备份/写入/还原记录
  Downloads/
    ResourceCatalog.cs resources.json 的读写与默认条目
    Downloader.cs      多镜像源下载、断流保护、解压
    ResourceFill.cs    从下载结果里认出 bundle / abl / ocdt
  Diagnostics/
    VioletGuard.cs    检测紫罗兰工具箱是否在运行
  Workflow/      七阶段工作流
    Engine/           与具体阶段无关的引擎
      Field.cs          参数说明
      Stage.cs          一个阶段
      Step.cs           一步
      Ctx.cs            执行上下文：日志、跑命令、读参数
      TaskEvent.cs      向界面抛的事件
      TaskRunner.cs     按顺序跑 Step，收集事件
      TaskFailures.cs   失败与中止异常
    Shared/           多个阶段共用的动手逻辑
      WorkflowClock.cs  取当前时间、可取消的 sleep、adb 实例
      DeviceGuard.cs    设备在线检查、root 校验、槽位判定
      RemoteLayout.cs   设备端目录、推送二进制
      BundleFiles.cs    内核版本/文件名归一化、找 profile、认 bundle 文件
      ImageLedger.cs    判断某个镜像是不是「写进去过的那张」
      RestorePlanner.cs 还原来源的挑选与核对
      PartitionWriter.cs 分区读写（setrw/写回）
    Stages/           一个阶段一个文件
      TemprootStage.cs  阶段 1 临时 Root（GPU / DFRoot 两条链路）
      PushStage.cs      阶段 2 推送解锁文件
      BackupStage.cs    阶段 3 备份原分区
      WriteStage.cs     阶段 4 写入解锁文件
      FastbootStage.cs  阶段 5 fastboot 解锁
      VerifyStage.cs    阶段 6 验证解锁状态
      RestoreStage.cs   阶段 7 还原原分区
    StageRegistry.cs    把七个阶段登记成列表（界面和其它地方都从这里取）
```

## App：界面层（MVVM）

```
OppoUnlockToolbox/
  App.xaml(.cs)      组装根：服务 → ViewModel → 主窗口
  Controls/          自绘控件与图标
    IconBox.cs         SVG 路径图标
    Icons.cs           图标数据表
  Mvvm/               ObservableObject、两种 RelayCommand
  Models/             纯数据：日志行、级别→画笔
  Services/           界面要用的能力，和窗口解耦
    StageSession.cs     当前阶段、每阶段参数、完成状态
    LogBook.cs          日志行 + 落盘
    StageRunner.cs      把正式任务放后台跑，事件回到 UI 线程
    DeviceWatcher.cs    插拔监听 + 防抖 + 触发探测
    ResourceInstaller.cs 下载结果填进参数 / 装 apk
    DialogService.cs    所有弹窗、文件对话框、剪贴板、开外部程序
    ThemeService.cs     明暗主题切换
    UserSettings.cs     用户偏好（目前是主题）
    FirstRunNotice.cs   「使用须知」只弹一次
  ViewModels/         每个界面区域的 VM
    MainViewModel.cs        总装与命令
    StagePanelViewModel.cs  阶段卡片：标题、警告、参数表单、进度
    Fields/                 每种参数字段一个 VM（bool/choice/multi/dir/file/text/note）
    StageRailItemViewModel.cs 左侧导航的一行
    DevicePanelViewModel.cs / DeviceTileViewModel.cs  右栏设备环境
    LogPanelViewModel.cs    实时输出
    DownloadViewModel.cs / DownloadRowViewModel.cs    资源下载窗口
    StageVisuals.cs         阶段 → 图标/按钮文案的映射
  Views/              只有窗口与对话框的 XAML 与很薄的 code-behind
    MainWindow.xaml(.cs)    四段式外壳 + 自绘标题栏
    AboutWindow / ConfirmWindow / FirstRunWindow / DownloadWindow
  Themes/
    Tokens.xaml       字体、字号、圆角、外壳尺寸（与主题无关）
    Light.xaml        浅色配色
    Dark.xaml         深色配色
    Controls.xaml     控件样式（取色一律 DynamicResource）
```

## 几个约定

**主题**：`App.xaml` 里合并字典的顺序固定为 `Tokens → 主题配色(索引 1) → Controls`。
`ThemeService` 换的就是索引 1 那个字典，所以控件样式必须用 `DynamicResource` 取色，
用 `StaticResource` 取色会锁死在启动时的配色。

**参数字段**：`FieldViewModel` 的子类只把值写进 `StageSession` 里那份按阶段分的字典，
阶段的 `Build()` 读的也是同一份，所以界面换了写法不影响 `Workflow` 的逻辑。

**引擎边界**：`Ctx` 负责日志与跑命令，`Step` 是纯函数式的「一步」，
`TaskRunner` 只按顺序跑并抛事件，界面通过 `StageRunner` 订阅。

**版本号**：`AppMeta.AppVersion`、`OppoUnlockToolbox.csproj` 的 `<Version>`/`<FileVersion>`、
`installer.iss` 的 `MyAppVersion` 四处要一起改。
