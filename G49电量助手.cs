// G49-PRO battery tray utility.
// It sends only the receiver's reverse-engineered, read-only BatteryLevel request.
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Media;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;

internal static class Program
{
    [DllImport("user32.dll")]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    [DllImport("user32.dll")]
    private static extern bool SetProcessDPIAware();

    private static readonly IntPtr DpiAwarenessContextPerMonitorAwareV2 = new IntPtr(-4);

    [STAThread]
    private static void Main()
    {
        EnableDpiAwareness();

        bool firstInstance;
        using (System.Threading.Mutex instanceMutex = new System.Threading.Mutex(
            true, @"Local\G49ProBatteryTray", out firstInstance))
        {
            if (!firstInstance)
            {
                MessageBox.Show("红龙G49 PRO鼠标电量助手已经在运行。", "红龙G49 PRO鼠标电量助手",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new G49BatteryTrayContext());
        }
    }

    private static void EnableDpiAwareness()
    {
        try
        {
            if (SetProcessDpiAwarenessContext(DpiAwarenessContextPerMonitorAwareV2))
                return;
        }
        catch (EntryPointNotFoundException)
        {
        }
        catch (DllNotFoundException)
        {
        }

        try
        {
            SetProcessDPIAware();
        }
        catch (Exception)
        {
        }
    }
}

internal sealed class G49BatteryTrayContext : ApplicationContext
{
    // Normal polling follows the requested five-minute cadence. Shorter delays
    // are used only while recovering from an unavailable receiver at logon or
    // after the mouse has gone to sleep. Automatic polls are skipped while the
    // user is idle so we never interfere with the mouse's sleep timer.
    private const int PollIntervalMilliseconds = 5 * 60 * 1000;
    private const uint IdleSkipThresholdMilliseconds = 60 * 1000;
    private const int IdleCheckIntervalMilliseconds = 3 * 1000;
    private const int ResumePollDelayMilliseconds = 1500;
    private static readonly int[] FailedReadRetryIntervalsMilliseconds =
    {
        15 * 1000,
        60 * 1000,
        5 * 60 * 1000
    };
    private const int DefaultLowBatteryPercent = 10;
    private const int LowBatteryReminderMinutes = 30;
    private static readonly int[] PresetLowBatteryThresholds = { 10, 15, 20, 25, 30, 50 };

    private readonly NotifyIcon trayIcon;
    private readonly ContextMenuStrip menu;
    private readonly ToolStripMenuItem statusItem;
    private readonly ToolStripMenuItem refreshItem;
    private readonly ToolStripMenuItem lowBatteryMenuItem;
    private readonly List<ToolStripMenuItem> presetThresholdItems = new List<ToolStripMenuItem>();
    private readonly ToolStripMenuItem customThresholdItem;
    private readonly ToolStripMenuItem disableThresholdItem;
    private readonly ToolStripMenuItem startupItem;
    private readonly ToolStripMenuItem diagnosticItem;
    private readonly TaskbarBatteryWidget taskbarWidget;
    private readonly System.Threading.Timer pollTimer;
    private readonly Timer idleResumeTimer;
    private readonly BackgroundWorker pollWorker;
    private readonly Control pollInvoker;
    private Icon activeIcon;
    private BatteryReading lastReading;
    private bool hasReading;
    private int lowBatteryThresholdPercent;
    private string lastError = "正在读取鼠标电量...";
    private DateTime lastLowBatteryAlertUtc = DateTime.MinValue;
    private DateTime lastPollAttemptLocal = DateTime.MinValue;
    private DateTime nextPollLocal = DateTime.MinValue;
    private bool pollDeferredForIdle;
    private int consecutiveReadFailures;
    private bool disposed;

    public G49BatteryTrayContext()
    {
        lowBatteryThresholdPercent = AlertSettings.GetLowBatteryThreshold(DefaultLowBatteryPercent);

        menu = new ContextMenuStrip();
        statusItem = new ToolStripMenuItem("G49：正在读取...");
        statusItem.Enabled = false;
        refreshItem = new ToolStripMenuItem("立即刷新", null, delegate { StartPoll(true); });
        lowBatteryMenuItem = new ToolStripMenuItem();
        foreach (int preset in PresetLowBatteryThresholds)
        {
            int value = preset;
            string label = preset == DefaultLowBatteryPercent
                ? string.Format("{0}%（默认）", preset)
                : string.Format("{0}%", preset);
            ToolStripMenuItem item = new ToolStripMenuItem(label, null, delegate { UpdateLowBatteryThreshold(value); });
            item.Tag = value;
            presetThresholdItems.Add(item);
            lowBatteryMenuItem.DropDownItems.Add(item);
        }
        lowBatteryMenuItem.DropDownItems.Add(new ToolStripSeparator());
        customThresholdItem = new ToolStripMenuItem("自定义百分比...", null, delegate { PromptCustomThreshold(); });
        disableThresholdItem = new ToolStripMenuItem("关闭低电量提醒", null, delegate { UpdateLowBatteryThreshold(0); });
        lowBatteryMenuItem.DropDownItems.Add(customThresholdItem);
        lowBatteryMenuItem.DropDownItems.Add(disableThresholdItem);
        SyncLowBatteryMenuState();

        startupItem = new ToolStripMenuItem("开机启动");
        startupItem.CheckOnClick = true;
        startupItem.Checked = StartupSettings.IsEnabled();
        startupItem.CheckedChanged += StartupItem_CheckedChanged;
        diagnosticItem = new ToolStripMenuItem("复制诊断信息", null, delegate { CopyDiagnosticInfo(); });
        ToolStripMenuItem exitItem = new ToolStripMenuItem("退出", null, delegate { ExitThread(); });

        menu.Items.Add(statusItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(refreshItem);
        menu.Items.Add(lowBatteryMenuItem);
        menu.Items.Add(startupItem);
        menu.Items.Add(diagnosticItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(exitItem);

        trayIcon = new NotifyIcon();
        trayIcon.ContextMenuStrip = menu;
        trayIcon.Text = "G49 鼠标：正在读取电量";
        trayIcon.DoubleClick += delegate { ShowStatus(); };
        SetTrayIcon(TrayIconFactory.CreateUnknownIcon());
        trayIcon.Visible = true;

        taskbarWidget = new TaskbarBatteryWidget(menu);
        taskbarWidget.SetLowBatteryThreshold(lowBatteryThresholdPercent);
        taskbarWidget.StatusRequested += delegate { ShowStatus(); };
        taskbarWidget.SetUnknown("G49 鼠标：正在读取电量");
        taskbarWidget.StartDocking();

        // Keep a UI-thread message-pump target for timer callbacks.
        pollInvoker = new Control();
        pollInvoker.CreateControl();

        SystemEvents.DisplaySettingsChanged += SystemEvents_DisplaySettingsChanged;
        SystemEvents.UserPreferenceChanged += SystemEvents_UserPreferenceChanged;

        pollWorker = new BackgroundWorker();
        pollWorker.DoWork += PollWorker_DoWork;
        pollWorker.RunWorkerCompleted += PollWorker_RunWorkerCompleted;

        idleResumeTimer = new Timer();
        idleResumeTimer.Interval = IdleCheckIntervalMilliseconds;
        idleResumeTimer.Tick += IdleResumeTimer_Tick;

        pollTimer = new System.Threading.Timer(PollTimerCallback, null,
            System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
        StartPoll(true);
    }

    private void SystemEvents_DisplaySettingsChanged(object sender, EventArgs e)
    {
        if (disposed || pollInvoker.IsDisposed || !pollInvoker.IsHandleCreated)
            return;

        try
        {
            pollInvoker.BeginInvoke(new MethodInvoker(RefreshIconsForEnvironmentChange));
        }
        catch (InvalidOperationException)
        {
        }
    }

    private void SystemEvents_UserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (disposed || pollInvoker.IsDisposed || !pollInvoker.IsHandleCreated)
            return;

        try
        {
            pollInvoker.BeginInvoke(new MethodInvoker(RefreshIconsForEnvironmentChange));
        }
        catch (InvalidOperationException)
        {
        }
    }

    private void RefreshIconsForEnvironmentChange()
    {
        if (disposed)
            return;

        if (hasReading)
            SetTrayIcon(TrayIconFactory.CreateBatteryIcon(lastReading.Percent, lastReading.IsCharging, lowBatteryThresholdPercent));
        else
            SetTrayIcon(TrayIconFactory.CreateUnknownIcon());

        taskbarWidget.RefreshAppearance();
    }

    private void SyncLowBatteryMenuState()
    {
        lowBatteryMenuItem.Text = lowBatteryThresholdPercent > 0
            ? string.Format("低电量提醒（≤ {0}%）", lowBatteryThresholdPercent)
            : "低电量提醒（已关闭）";

        bool matchedPreset = false;
        foreach (ToolStripMenuItem item in presetThresholdItems)
        {
            int preset = (int)item.Tag;
            bool isSelected = lowBatteryThresholdPercent == preset;
            item.Checked = isSelected;
            if (isSelected)
                matchedPreset = true;
        }

        disableThresholdItem.Checked = lowBatteryThresholdPercent <= 0;
        customThresholdItem.Checked = lowBatteryThresholdPercent > 0 && !matchedPreset;
        customThresholdItem.Text = customThresholdItem.Checked
            ? string.Format("自定义百分比（当前 {0}%）...", lowBatteryThresholdPercent)
            : "自定义百分比...";
    }

    private void PromptCustomThreshold()
    {
        int initialValue = lowBatteryThresholdPercent > 0 ? lowBatteryThresholdPercent : DefaultLowBatteryPercent;
        using (LowBatteryThresholdDialog dialog = new LowBatteryThresholdDialog(initialValue))
        {
            if (dialog.ShowDialog() == DialogResult.OK)
                UpdateLowBatteryThreshold(dialog.ThresholdPercent);
        }
    }

    private void UpdateLowBatteryThreshold(int newThreshold)
    {
        int clamped = AlertSettings.ClampThreshold(newThreshold);
        try
        {
            AlertSettings.SetLowBatteryThreshold(clamped);
        }
        catch (Exception ex)
        {
            trayIcon.ShowBalloonTip(3500, "红龙G49 PRO鼠标电量助手",
                "无法保存低电量提醒设置：" + ex.Message, ToolTipIcon.Warning);
            return;
        }

        lowBatteryThresholdPercent = clamped;
        lastLowBatteryAlertUtc = DateTime.MinValue;
        SyncLowBatteryMenuState();
        taskbarWidget.SetLowBatteryThreshold(lowBatteryThresholdPercent);

        if (hasReading)
        {
            SetTrayIcon(TrayIconFactory.CreateBatteryIcon(lastReading.Percent, lastReading.IsCharging, lowBatteryThresholdPercent));
            CheckLowBatteryAlert(lastReading);
        }
    }

    private void PollTimerCallback(object state)
    {
        if (disposed || pollInvoker.IsDisposed || !pollInvoker.IsHandleCreated)
            return;

        try
        {
            pollInvoker.BeginInvoke(new MethodInvoker(delegate { StartPoll(false); }));
        }
        catch (InvalidOperationException)
        {
            // The UI is closing; ExitThreadCore will dispose the timer.
        }
    }

    private void IdleResumeTimer_Tick(object sender, EventArgs e)
    {
        if (disposed || !pollDeferredForIdle)
        {
            idleResumeTimer.Stop();
            return;
        }

        uint idleMs;
        if (UserInputMonitor.TryGetIdleMilliseconds(out idleMs) && idleMs < IdleSkipThresholdMilliseconds)
        {
            // User returned and moved the mouse or pressed a key. Give a sleeping mouse a brief
            // moment (~1.5s) to re-link with the 2.4G receiver before sending the battery query.
            pollDeferredForIdle = false;
            idleResumeTimer.Stop();
            ScheduleNextPoll(ResumePollDelayMilliseconds);
        }
    }

    private void StartupItem_CheckedChanged(object sender, EventArgs e)
    {
        try
        {
            StartupSettings.SetEnabled(startupItem.Checked);
        }
        catch (Exception ex)
        {
            startupItem.CheckedChanged -= StartupItem_CheckedChanged;
            startupItem.Checked = !startupItem.Checked;
            startupItem.CheckedChanged += StartupItem_CheckedChanged;
            trayIcon.ShowBalloonTip(3500, "红龙G49 PRO鼠标电量助手",
                "无法更新开机启动设置：" + ex.Message, ToolTipIcon.Warning);
        }
    }

    private void StartPoll(bool force)
    {
        if (disposed)
            return;

        if (!force)
        {
            uint idleMs;
            if (UserInputMonitor.TryGetIdleMilliseconds(out idleMs) && idleMs >= IdleSkipThresholdMilliseconds)
            {
                // Skip automatic HID polling while the PC is idle so the mouse can sleep undisturbed.
                pollTimer.Change(System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
                nextPollLocal = DateTime.MinValue;
                pollDeferredForIdle = true;
                if (!idleResumeTimer.Enabled)
                    idleResumeTimer.Start();
                return;
            }
        }

        pollDeferredForIdle = false;
        idleResumeTimer.Stop();

        if (pollWorker.IsBusy)
        {
            ScheduleNextPoll(PollIntervalMilliseconds);
            return;
        }

        pollTimer.Change(System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
        nextPollLocal = DateTime.MinValue;
        lastPollAttemptLocal = DateTime.Now;
        refreshItem.Enabled = false;
        pollWorker.RunWorkerAsync();
    }

    private void ScheduleNextPollAfterSuccess()
    {
        consecutiveReadFailures = 0;
        ScheduleNextPoll(PollIntervalMilliseconds);
    }

    private void ScheduleNextPollAfterFailure()
    {
        int retryIndex = Math.Min(consecutiveReadFailures,
            FailedReadRetryIntervalsMilliseconds.Length - 1);
        int interval = FailedReadRetryIntervalsMilliseconds[retryIndex];
        consecutiveReadFailures++;
        ScheduleNextPoll(interval);
    }

    private void ScheduleNextPoll(int intervalMilliseconds)
    {
        if (disposed)
            return;

        nextPollLocal = DateTime.Now.AddMilliseconds(intervalMilliseconds);
        pollTimer.Change(intervalMilliseconds, System.Threading.Timeout.Infinite);
    }

    private string DescribeNextPoll()
    {
        if (nextPollLocal == DateTime.MinValue)
            return "稍后";

        double remainingSeconds = (nextPollLocal - DateTime.Now).TotalSeconds;
        if (remainingSeconds <= 5)
            return "马上";
        if (remainingSeconds < 60)
            return string.Format("约 {0} 秒后", (int)Math.Ceiling(remainingSeconds));
        if (remainingSeconds < 60 * 60)
            return string.Format("约 {0} 分钟后", (int)Math.Ceiling(remainingSeconds / 60.0));
        return "约 1 小时后";
    }

    private static void PollWorker_DoWork(object sender, DoWorkEventArgs e)
    {
        BatteryReading reading;
        string error;
        bool success = G49BatteryReader.TryRead(out reading, out error);
        e.Result = new PollResult(success, reading, error);
    }

    private void PollWorker_RunWorkerCompleted(object sender, RunWorkerCompletedEventArgs e)
    {
        // The HID read can finish after the user chooses Exit from the tray menu.
        if (disposed)
            return;

        refreshItem.Enabled = true;
        if (e.Error != null)
        {
            ScheduleNextPollAfterFailure();
            ApplyReadFailure(e.Error.Message);
            return;
        }

        PollResult result = e.Result as PollResult;
        if (result == null || !result.Success)
        {
            ScheduleNextPollAfterFailure();
            ApplyReadFailure(result == null ? "读取线程没有返回结果" : result.Error);
            return;
        }

        ScheduleNextPollAfterSuccess();
        ApplyReading(result.Reading);
    }

    private void ApplyReading(BatteryReading reading)
    {
        hasReading = true;
        lastReading = reading;
        lastError = "";

        string modeName = reading.Mode == BatteryConnectionMode.Bluetooth ? "蓝牙" : "2.4G";
        string tooltip;
        if (reading.Mode == BatteryConnectionMode.Bluetooth)
        {
            statusItem.Text = string.Format("G49：{0}%（蓝牙模式）", reading.Percent);
            tooltip = string.Format("G49 鼠标：{0}% | 蓝牙模式", reading.Percent);
        }
        else
        {
            string charging = reading.IsCharging ? "充电中" : "未充电";
            statusItem.Text = string.Format("G49：{0}%（{1}，{2}，{3} mV）",
                reading.Percent, modeName, charging, reading.Millivolts);
            tooltip = string.Format("G49 鼠标：{0}% | {1} | {2} mV",
                reading.Percent, charging, reading.Millivolts);
        }
        trayIcon.Text = LimitTooltip(tooltip);
        SetTrayIcon(TrayIconFactory.CreateBatteryIcon(reading.Percent, reading.IsCharging, lowBatteryThresholdPercent));
        taskbarWidget.SetReading(reading.Percent, reading.IsCharging, tooltip);

        CheckLowBatteryAlert(reading);
    }

    private void CheckLowBatteryAlert(BatteryReading reading)
    {
        bool needsCharge = lowBatteryThresholdPercent > 0 &&
                           !reading.IsCharging &&
                           reading.Percent <= lowBatteryThresholdPercent;
        if (!needsCharge)
        {
            lastLowBatteryAlertUtc = DateTime.MinValue;
            return;
        }

        DateTime now = DateTime.UtcNow;
        if (lastLowBatteryAlertUtc == DateTime.MinValue ||
            now - lastLowBatteryAlertUtc >= TimeSpan.FromMinutes(LowBatteryReminderMinutes))
        {
            lastLowBatteryAlertUtc = now;
            SystemSounds.Exclamation.Play();
            trayIcon.ShowBalloonTip(
                10000,
                "G49 鼠标电量低",
                string.Format("当前电量 {0}%（已降至设定阈值 {1}% 及以下），请给鼠标充电。",
                    reading.Percent, lowBatteryThresholdPercent),
                ToolTipIcon.Warning);
        }
    }

    private void ApplyReadFailure(string error)
    {
        lastError = string.IsNullOrEmpty(error) ? "暂时无法读取电量" : error;
        string retryText = DescribeNextPoll();
        bool likelySleeping = IsLikelySleepingFailure(lastError);
        if (hasReading)
        {
            // A transient timeout should not erase a previously verified value.
            string statusDescription = likelySleeping ? "鼠标可能休眠" : "本次读取失败";
            string staleTooltip = string.Format("G49 鼠标：{0}% | {1}，{2}自动重试",
                lastReading.Percent, statusDescription, retryText);
            statusItem.Text = string.Format("G49：{0}%（{1}，{2}自动重试）",
                lastReading.Percent, statusDescription, retryText);
            trayIcon.Text = LimitTooltip(staleTooltip);
            taskbarWidget.SetReading(lastReading.Percent, lastReading.IsCharging, staleTooltip);
            return;
        }

        string unavailableDescription = likelySleeping ? "鼠标可能休眠，请移动鼠标唤醒" : "暂时无法读取";
        string failureTooltip = "G49 鼠标：" + unavailableDescription + "，" + retryText + "自动重试";
        statusItem.Text = string.Format("G49：{0}（{1}自动重试）", unavailableDescription, retryText);
        trayIcon.Text = LimitTooltip(failureTooltip);
        SetTrayIcon(TrayIconFactory.CreateUnknownIcon());
        taskbarWidget.SetUnknown(failureTooltip);
    }

    private static bool IsLikelySleepingFailure(string error)
    {
        if (string.IsNullOrEmpty(error))
            return false;

        return error.IndexOf("超时", StringComparison.OrdinalIgnoreCase) >= 0 ||
               error.IndexOf("没有收到有效电量回包", StringComparison.OrdinalIgnoreCase) >= 0 ||
               error.IndexOf("打开 HID 端点失败", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private void ShowStatus()
    {
        string thresholdHint = lowBatteryThresholdPercent > 0
            ? string.Format("电量降至 {0}% 及以下且未充电时会提醒（可右键修改阈值）。", lowBatteryThresholdPercent)
            : "当前已关闭低电量提醒（可右键重新开启）。";
        if (hasReading)
        {
            string body;
            if (lastReading.Mode == BatteryConnectionMode.Bluetooth)
            {
                body = string.Format(
                    "连接方式：蓝牙模式\r\n电量：{0}%\r\n（蓝牙模式使用系统 BLE 电池服务，不含充电状态与电压）\r\n\r\n{1}",
                    lastReading.Percent, thresholdHint);
            }
            else
            {
                string charging = lastReading.IsCharging ? "是" : "否";
                body = string.Format(
                    "连接方式：2.4G 接收器\r\n电量：{0}%\r\n充电中：{1}\r\n电压：{2} mV\r\n\r\n{3}",
                    lastReading.Percent, charging, lastReading.Millivolts, thresholdHint);
            }
            MessageBox.Show(
                body,
                "红龙G49 PRO鼠标电量助手",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
        else
        {
            string wakeHint = IsLikelySleepingFailure(lastError)
                ? "鼠标可能处于休眠状态，请移动一下鼠标唤醒它"
                : "请确认 2.4G 接收器已插入，或鼠标已通过蓝牙连接";
            MessageBox.Show(
                "暂时无法读取鼠标电量。\r\n\r\n" + wakeHint + "；程序将在" + DescribeNextPoll() +
                "自动重试；也可以右键图标选择“立即刷新”。\r\n\r\n原因：" + lastError,
                "红龙G49 PRO鼠标电量助手",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
    }

    private void CopyDiagnosticInfo()
    {
        string text;
        string thresholdText = lowBatteryThresholdPercent > 0
            ? string.Format("{0}%", lowBatteryThresholdPercent)
            : "已关闭";
        string nextPollText = pollDeferredForIdle
            ? "键鼠空闲已暂缓（恢复活动后查询）"
            : FormatDiagnosticTime(nextPollLocal);
        string schedule = string.Format(
            "\r\n低电量提醒阈值：{0}\r\n上次查询：{1}\r\n下次自动查询：{2}",
            thresholdText,
            FormatDiagnosticTime(lastPollAttemptLocal),
            nextPollText);
        if (hasReading)
        {
            if (lastReading.Mode == BatteryConnectionMode.Bluetooth)
            {
                text = string.Format(
                    "G49 Battery Tray\r\n状态：可读取\r\n连接方式：蓝牙模式\r\n电量：{0}%\r\n协议：BLE GATT Battery Service (0x180F / 0x2A19){1}",
                    lastReading.Percent,
                    schedule);
            }
            else
            {
                text = string.Format(
                    "G49 Battery Tray\r\n状态：可读取\r\n连接方式：2.4G 接收器\r\n电量：{0}%\r\n充电中：{1}\r\n电压：{2} mV\r\n协议：HID report 08 04（只读 BatteryLevel 请求）{3}",
                    lastReading.Percent,
                    lastReading.IsCharging ? "是" : "否",
                    lastReading.Millivolts,
                    schedule);
            }
        }
        else
        {
            text = "G49 Battery Tray\r\n状态：读取失败\r\n原因：" + lastError + schedule;
        }

        try
        {
            Clipboard.SetText(text);
            trayIcon.ShowBalloonTip(2500, "红龙G49 PRO鼠标电量助手", "诊断信息已复制到剪贴板。", ToolTipIcon.Info);
        }
        catch (ExternalException)
        {
            trayIcon.ShowBalloonTip(2500, "红龙G49 PRO鼠标电量助手", "剪贴板当前被其他程序占用。", ToolTipIcon.Warning);
        }
    }

    private static string FormatDiagnosticTime(DateTime value)
    {
        return value == DateTime.MinValue ? "尚未安排" : value.ToString("yyyy-MM-dd HH:mm:ss");
    }

    private void SetTrayIcon(Icon icon)
    {
        Icon oldIcon = activeIcon;
        activeIcon = icon;
        trayIcon.Icon = icon;
        if (oldIcon != null)
            oldIcon.Dispose();
    }

    private static string LimitTooltip(string text)
    {
        // NotifyIcon.Text is limited to 63 characters on older Windows versions.
        return text.Length <= 63 ? text : text.Substring(0, 63);
    }

    protected override void ExitThreadCore()
    {
        if (!disposed)
        {
            disposed = true;
            SystemEvents.DisplaySettingsChanged -= SystemEvents_DisplaySettingsChanged;
            SystemEvents.UserPreferenceChanged -= SystemEvents_UserPreferenceChanged;
            pollTimer.Dispose();
            idleResumeTimer.Stop();
            idleResumeTimer.Dispose();
            pollInvoker.Dispose();
            taskbarWidget.CloseForExit();
            taskbarWidget.Dispose();
            trayIcon.Visible = false;
            trayIcon.Dispose();
            menu.Dispose();
            if (activeIcon != null)
                activeIcon.Dispose();
        }
        base.ExitThreadCore();
    }

    private sealed class PollResult
    {
        public readonly bool Success;
        public readonly BatteryReading Reading;
        public readonly string Error;

        public PollResult(bool success, BatteryReading reading, string error)
        {
            Success = success;
            Reading = reading;
            Error = error;
        }
    }
}

internal enum BatteryConnectionMode
{
    Receiver24G = 0,
    Bluetooth = 1
}

internal struct BatteryReading
{
    public byte Percent;
    public bool IsCharging;
    public ushort Millivolts;
    public BatteryConnectionMode Mode;
}

internal static class G49BatteryReader
{
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint OpenExisting = 3;
    private const uint FileAttributeNormal = 0x00000080;
    private const uint FileFlagOverlapped = 0x40000000;
    private const uint WaitObject0 = 0;
    private const uint WaitTimeout = 258;
    private const int ErrorIoPending = 997;
    private static readonly IntPtr InvalidHandle = new IntPtr(-1);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeOverlapped
    {
        public IntPtr Internal;
        public IntPtr InternalHigh;
        public uint Offset;
        public uint OffsetHigh;
        public IntPtr EventHandle;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateFile(
        string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes,
        uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadFile(IntPtr file, byte[] buffer, uint bytesToRead,
        out uint bytesRead, ref NativeOverlapped overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteFile(IntPtr file, byte[] buffer, uint bytesToWrite,
        out uint bytesWritten, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetOverlappedResult(IntPtr file, ref NativeOverlapped overlapped,
        out uint bytesTransferred, bool wait);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateEvent(IntPtr attributes, bool manualReset,
        bool initialState, string name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CancelIoEx(IntPtr file, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_SetOutputReport(IntPtr device, byte[] reportBuffer,
        uint reportBufferLength);

    public static bool TryRead(out BatteryReading reading, out string error)
    {
        reading = new BatteryReading();
        error = "";

        // 1. Fast check (~2ms when disconnected): if G49 is currently connected via Bluetooth,
        //    read standard BLE GATT Battery Service (0x180F / 0x2A19) directly without waiting
        //    for an idle 2.4G dongle to time out.
        bool bleConnected;
        string bleError;
        if (G49BluetoothBatteryReader.TryReadConnected(out reading, out bleConnected, out bleError))
            return true;

        // 2. Otherwise read from the 2.4G USB receiver HID command endpoint.
        string hidError;
        if (TryReadFrom24GReceiver(out reading, out hidError))
            return true;

        if (bleConnected && !string.IsNullOrEmpty(bleError))
        {
            error = "蓝牙读取失败：" + bleError;
            return false;
        }

        if (hidError == "没有找到 G49 接收器的 HID 命令端点")
        {
            error = "未检测到已连接的 G49 蓝牙鼠标，也未找到 2.4G 接收器";
            return false;
        }

        error = hidError;
        return false;
    }

    private static bool TryReadFrom24GReceiver(out BatteryReading reading, out string error)
    {
        reading = new BatteryReading();
        error = "";
        List<string> paths = FindCommandCollectionPaths();
        if (paths.Count == 0)
        {
            error = "没有找到 G49 接收器的 HID 命令端点";
            return false;
        }

        List<string> reasons = new List<string>();
        foreach (string path in paths)
        {
            string failure;
            if (TryReadFromPath(path, out reading, out failure))
            {
                reading.Mode = BatteryConnectionMode.Receiver24G;
                return true;
            }
            reasons.Add(failure);
        }

        error = reasons.Count == 0 ? "读取电量失败" : string.Join("；", reasons.ToArray());
        return false;
    }

    private static List<string> FindCommandCollectionPaths()
    {
        List<string> result = new List<string>();
        const string keyPath = @"SYSTEM\CurrentControlSet\Control\DeviceClasses\{4d1e55b2-f16f-11cf-88cb-001111000030}";
        try
        {
            using (RegistryKey root = Registry.LocalMachine.OpenSubKey(keyPath))
            {
                if (root == null)
                    return result;

                foreach (string subkey in root.GetSubKeyNames())
                {
                    string lower = subkey.ToLowerInvariant();
                    bool receiver = lower.Contains("vid_3554&pid_f54d") ||
                                    lower.Contains("vid_3554&pid_f54f") ||
                                    lower.Contains("vid_3554&pid_f54c");
                    if (!receiver || !lower.Contains("mi_01&col05"))
                        continue;

                    string path = NormalizeDevicePath(subkey);
                    if (!result.Contains(path))
                        result.Add(path);
                }
            }
        }
        catch (Exception ex)
        {
            // Standard users can normally read DeviceClasses. Return an empty result
            // with no exception propagation if a locked-down system rejects it.
            System.Diagnostics.Debug.WriteLine(ex.Message);
        }
        return result;
    }

    private static string NormalizeDevicePath(string registryName)
    {
        int offset = 0;
        while (offset < registryName.Length)
        {
            char c = registryName[offset];
            if (c != '\\' && c != '?' && c != '#' && c != '/')
                break;
            offset++;
        }
        return @"\\?\" + registryName.Substring(offset);
    }

    private static bool TryReadFromPath(string path, out BatteryReading reading, out string error)
    {
        reading = new BatteryReading();
        error = "";
        IntPtr handle = CreateFile(
            path,
            GenericRead | GenericWrite,
            FileShareRead | FileShareWrite,
            IntPtr.Zero,
            OpenExisting,
            FileAttributeNormal | FileFlagOverlapped,
            IntPtr.Zero);
        if (handle == InvalidHandle)
        {
            error = "打开 HID 端点失败(" + Marshal.GetLastWin32Error() + ")";
            return false;
        }

        try
        {
            // Receiver protocol observed from the official driver: report 08, command 04 (BatteryLevel).
            // The final byte is (0x55 - sum(bytes[0..15])) & 0xff.
            byte[] query = new byte[17];
            query[0] = 0x08;
            query[1] = 0x04;
            query[16] = CalculateChecksum(query);

            byte[] frame;
            if (!TryReadFirstFrameAfterSending(handle, query, 800, out frame, out error))
                return false;
            if (TryParseBatteryFrame(frame, out reading))
                return true;

            long deadline = Environment.TickCount + 1000;
            while (Environment.TickCount < deadline)
            {
                int remaining = (int)Math.Max(1, deadline - Environment.TickCount);
                if (!TryReadFrame(handle, remaining, out frame, out error))
                    return false;
                if (TryParseBatteryFrame(frame, out reading))
                    return true;
            }

            error = "没有收到有效电量回包";
            return false;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private static bool TryReadFirstFrameAfterSending(
        IntPtr handle, byte[] query, int timeoutMilliseconds, out byte[] response, out string error)
    {
        response = null;
        error = "";
        IntPtr signal = CreateEvent(IntPtr.Zero, true, false, null);
        if (signal == IntPtr.Zero || signal == InvalidHandle)
        {
            error = "创建 HID 读取事件失败(" + Marshal.GetLastWin32Error() + ")";
            return false;
        }

        try
        {
            NativeOverlapped overlapped = new NativeOverlapped();
            overlapped.EventHandle = signal;
            byte[] buffer = new byte[17];
            uint read;
            bool completed = ReadFile(handle, buffer, (uint)buffer.Length, out read, ref overlapped);
            if (!completed && Marshal.GetLastWin32Error() != ErrorIoPending)
            {
                error = "启动 HID 读取失败(" + Marshal.GetLastWin32Error() + ")";
                return false;
            }

            // Let the receiver register the pending input read before it receives
            // the request. This matches the timing used by the original protocol probe.
            System.Threading.Thread.Sleep(15);

            uint written;
            bool sent = WriteFile(handle, query, (uint)query.Length, out written, IntPtr.Zero);
            int writeError = sent ? 0 : Marshal.GetLastWin32Error();
            if (!sent || written != query.Length)
            {
                // HIDUsb.dll uses WriteFile for this collection. Some receiver driver
                // revisions only accept the HidD variant, so preserve that fallback.
                sent = HidD_SetOutputReport(handle, query, (uint)query.Length);
                if (!sent)
                {
                    int hidError = Marshal.GetLastWin32Error();
                    CancelAndDrainRead(handle, signal, ref overlapped);
                    error = "发送电量查询失败(WriteFile=" + writeError + "，HidD=" + hidError + ")";
                    return false;
                }
            }

            if (completed)
            {
                response = Trim(buffer, (int)read);
                return true;
            }

            return FinishPendingRead(handle, signal, ref overlapped, buffer, timeoutMilliseconds, out response, out error);
        }
        finally
        {
            CloseHandle(signal);
        }
    }

    private static bool TryReadFrame(IntPtr handle, int timeoutMilliseconds, out byte[] response, out string error)
    {
        response = null;
        error = "";
        IntPtr signal = CreateEvent(IntPtr.Zero, true, false, null);
        if (signal == IntPtr.Zero || signal == InvalidHandle)
        {
            error = "创建 HID 读取事件失败(" + Marshal.GetLastWin32Error() + ")";
            return false;
        }

        try
        {
            NativeOverlapped overlapped = new NativeOverlapped();
            overlapped.EventHandle = signal;
            byte[] buffer = new byte[17];
            uint read;
            bool completed = ReadFile(handle, buffer, (uint)buffer.Length, out read, ref overlapped);
            if (completed)
            {
                response = Trim(buffer, (int)read);
                return true;
            }
            if (Marshal.GetLastWin32Error() != ErrorIoPending)
            {
                error = "启动 HID 读取失败(" + Marshal.GetLastWin32Error() + ")";
                return false;
            }

            return FinishPendingRead(handle, signal, ref overlapped, buffer, timeoutMilliseconds, out response, out error);
        }
        finally
        {
            CloseHandle(signal);
        }
    }

    private static bool FinishPendingRead(
        IntPtr handle, IntPtr signal, ref NativeOverlapped overlapped, byte[] buffer,
        int timeoutMilliseconds, out byte[] response, out string error)
    {
        response = null;
        error = "";
        uint wait = WaitForSingleObject(signal, (uint)timeoutMilliseconds);
        if (wait == WaitTimeout)
        {
            CancelAndDrainRead(handle, signal, ref overlapped);
            error = "等待 HID 电量回包超时";
            return false;
        }
        if (wait != WaitObject0)
        {
            CancelAndDrainRead(handle, signal, ref overlapped);
            error = "等待 HID 回包失败(" + Marshal.GetLastWin32Error() + ")";
            return false;
        }

        uint read;
        if (!GetOverlappedResult(handle, ref overlapped, out read, false))
        {
            error = "读取 HID 回包失败(" + Marshal.GetLastWin32Error() + ")";
            return false;
        }
        response = Trim(buffer, (int)read);
        return true;
    }

    private static void CancelAndDrainRead(IntPtr handle, IntPtr signal, ref NativeOverlapped overlapped)
    {
        CancelIoEx(handle, IntPtr.Zero);
        WaitForSingleObject(signal, 200);
        uint ignored;
        GetOverlappedResult(handle, ref overlapped, out ignored, false);
    }

    private static byte CalculateChecksum(byte[] frame)
    {
        int sum = 0;
        for (int i = 0; i < frame.Length - 1; i++)
            sum += frame[i];
        return (byte)((0x55 - sum) & 0xff);
    }

    private static bool TryParseBatteryFrame(byte[] frame, out BatteryReading reading)
    {
        reading = new BatteryReading();
        // Reply positions were verified against the official G49 driver:
        // 6=level, 7=isCharging, 8=voltage high byte, 9=voltage low byte.
        if (frame == null || frame.Length < 10 || frame[0] != 0x08 || frame[1] != 0x04)
            return false;

        byte percent = frame[6];
        // The observed G49 reply encodes voltage as high-byte followed by low-byte.
        ushort millivolts = (ushort)((frame[8] << 8) | frame[9]);
        if (percent > 100 || millivolts < 2500 || millivolts > 4500)
            return false;

        reading.Percent = percent;
        reading.IsCharging = frame[7] != 0;
        reading.Millivolts = millivolts;
        reading.Mode = BatteryConnectionMode.Receiver24G;
        return true;
    }

    private static byte[] Trim(byte[] input, int length)
    {
        length = Math.Max(0, Math.Min(length, input.Length));
        byte[] result = new byte[length];
        Array.Copy(input, result, length);
        return result;
    }
}

internal static class G49BluetoothBatteryReader
{
    private static readonly Guid GuidBatteryService = new Guid("0000180f-0000-1000-8000-00805f9b34fb");
    private static readonly DEVPROPKEY DevpkeyIsConnected = new DEVPROPKEY
    {
        fmtid = new Guid("83da6326-97a6-4088-9453-a1923f573b29"),
        pid = 15
    };
    private static readonly DEVPROPKEY DevpkeyBluetoothBattery = new DEVPROPKEY
    {
        fmtid = new Guid("104ea319-6ee2-4701-bd47-8ddbf425bbe5"),
        pid = 2
    };
    private static readonly DEVPROPKEY DevpkeyFriendlyName = new DEVPROPKEY
    {
        fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"),
        pid = 14
    };

    private const uint DigcfPresent = 0x00000002;
    private const uint DigcfDeviceInterface = 0x00000010;
    private const uint GenericRead = 0x80000000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint OpenExisting = 3;
    private const uint BluetoothGattFlagNone = 0x00000000;
    private const uint BluetoothGattFlagForceRead = 0x00000004;
    private const ushort BatteryLevelCharacteristicUuid = 0x2A19;
    private static readonly IntPtr InvalidHandle = new IntPtr(-1);

    [StructLayout(LayoutKind.Sequential)]
    private struct DEVPROPKEY
    {
        public Guid fmtid;
        public uint pid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVINFO_DATA
    {
        public uint cbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVICE_INTERFACE_DATA
    {
        public uint cbSize;
        public Guid InterfaceClassGuid;
        public uint Flags;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Explicit, Size = 20)]
    private struct BTH_LE_UUID
    {
        [FieldOffset(0)] public byte IsShortUuid;
        [FieldOffset(4)] public ushort ShortUuid;
        [FieldOffset(4)] public Guid LongUuid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BTH_LE_GATT_CHARACTERISTIC
    {
        public ushort ServiceHandle;
        public BTH_LE_UUID CharacteristicUuid;
        public ushort AttributeHandle;
        public ushort CharacteristicValueHandle;
        public byte IsBroadcastable;
        public byte IsReadable;
        public byte IsWritable;
        public byte IsWritableWithoutResponse;
        public byte IsSignedWritable;
        public byte IsNotifiable;
        public byte IsIndicatable;
        public byte HasExtendedProperties;
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, string enumerator, IntPtr hwndParent, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInterfaces(IntPtr deviceInfoSet, IntPtr deviceInfoData,
        ref Guid interfaceClassGuid, uint memberIndex, ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr deviceInfoSet,
        ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData, IntPtr detailData, uint detailDataSize,
        out uint requiredSize, ref SP_DEVINFO_DATA deviceInfoData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceProperty(IntPtr deviceInfoSet, ref SP_DEVINFO_DATA deviceInfoData,
        ref DEVPROPKEY propertyKey, out uint propertyType, byte[] propertyBuffer, uint propertyBufferSize,
        out uint requiredSize, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern uint CM_Get_Parent(out uint pdnDevInst, uint dnDevInst, uint ulFlags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern uint CM_Get_DevNode_PropertyW(uint dnDevInst, ref DEVPROPKEY propertyKey,
        out uint propertyType, byte[] propertyBuffer, ref uint propertyBufferSize, uint ulFlags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateFile(string fileName, uint desiredAccess, uint shareMode,
        IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("BluetoothAPIs.dll", SetLastError = true)]
    private static extern int BluetoothGATTGetCharacteristics(IntPtr hDevice, IntPtr service,
        ushort characteristicsBufferCount, [Out] BTH_LE_GATT_CHARACTERISTIC[] characteristicsBuffer,
        out ushort characteristicsBufferActual, uint flags);

    [DllImport("BluetoothAPIs.dll", SetLastError = true)]
    private static extern int BluetoothGATTGetCharacteristicValue(IntPtr hDevice,
        ref BTH_LE_GATT_CHARACTERISTIC characteristic, uint valueBufferSize, IntPtr valueBuffer,
        out ushort valueBufferSizeRequired, uint flags);

    private sealed class BleCandidate
    {
        public readonly string Path;
        public readonly uint ParentDevInst;
        public readonly int Priority;

        public BleCandidate(string path, uint parentDevInst, int priority)
        {
            Path = path;
            ParentDevInst = parentDevInst;
            Priority = priority;
        }
    }

    public static bool TryReadConnected(out BatteryReading reading, out bool foundConnected, out string error)
    {
        reading = new BatteryReading();
        foundConnected = false;
        error = "";

        List<BleCandidate> candidates;
        try
        {
            candidates = FindConnectedG49Candidates();
        }
        catch (Exception ex)
        {
            error = "枚举蓝牙电池服务失败(" + ex.Message + ")";
            return false;
        }

        if (candidates.Count == 0)
            return false;

        foundConnected = true;
        List<string> reasons = new List<string>();
        foreach (BleCandidate candidate in candidates)
        {
            byte percent;
            string reason;
            if (TryReadGattBattery(candidate.Path, out percent, out reason) ||
                TryReadParentPnPBattery(candidate.ParentDevInst, out percent))
            {
                reading.Percent = percent;
                reading.IsCharging = false;
                reading.Millivolts = 0;
                reading.Mode = BatteryConnectionMode.Bluetooth;
                return true;
            }
            if (!string.IsNullOrEmpty(reason))
                reasons.Add(reason);
        }

        error = reasons.Count == 0 ? "读取蓝牙电量失败" : string.Join("；", reasons.ToArray());
        return false;
    }

    private static List<BleCandidate> FindConnectedG49Candidates()
    {
        List<BleCandidate> result = new List<BleCandidate>();
        Guid serviceGuid = GuidBatteryService;
        IntPtr deviceInfoSet = SetupDiGetClassDevs(ref serviceGuid, null, IntPtr.Zero,
            DigcfPresent | DigcfDeviceInterface);
        if (deviceInfoSet == InvalidHandle || deviceInfoSet == IntPtr.Zero)
            return result;

        try
        {
            uint index = 0;
            while (true)
            {
                SP_DEVICE_INTERFACE_DATA ifData = new SP_DEVICE_INTERFACE_DATA();
                ifData.cbSize = (uint)Marshal.SizeOf(typeof(SP_DEVICE_INTERFACE_DATA));
                if (!SetupDiEnumDeviceInterfaces(deviceInfoSet, IntPtr.Zero, ref serviceGuid, index, ref ifData))
                    break;
                index++;

                SP_DEVINFO_DATA devInfo = new SP_DEVINFO_DATA();
                devInfo.cbSize = (uint)Marshal.SizeOf(typeof(SP_DEVINFO_DATA));
                uint requiredSize;
                SetupDiGetDeviceInterfaceDetail(deviceInfoSet, ref ifData, IntPtr.Zero, 0,
                    out requiredSize, ref devInfo);
                if (requiredSize <= 4)
                    continue;

                IntPtr detailBuffer = Marshal.AllocHGlobal((int)requiredSize);
                try
                {
                    Marshal.WriteInt32(detailBuffer, IntPtr.Size == 8 ? 8 : 6);
                    if (!SetupDiGetDeviceInterfaceDetail(deviceInfoSet, ref ifData, detailBuffer, requiredSize,
                        out requiredSize, ref devInfo))
                    {
                        continue;
                    }

                    string path = Marshal.PtrToStringUni(new IntPtr(detailBuffer.ToInt64() + 4));
                    if (string.IsNullOrEmpty(path))
                        continue;

                    uint parentDevInst = 0;
                    CM_Get_Parent(out parentDevInst, devInfo.DevInst, 0);

                    int priority = MatchG49CandidatePriority(path, parentDevInst);
                    if (priority <= 0)
                        continue;

                    bool serviceConnected = GetDevInfoBoolProperty(deviceInfoSet, ref devInfo, DevpkeyIsConnected);
                    bool parentConnected = parentDevInst != 0 && GetDevNodeBoolProperty(parentDevInst, DevpkeyIsConnected);
                    if (!serviceConnected && !parentConnected)
                        continue;

                    result.Add(new BleCandidate(path, parentDevInst, priority));
                }
                finally
                {
                    Marshal.FreeHGlobal(detailBuffer);
                }
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(deviceInfoSet);
        }

        result.Sort(delegate(BleCandidate a, BleCandidate b) { return b.Priority.CompareTo(a.Priority); });
        return result;
    }

    private static int MatchG49CandidatePriority(string interfacePath, uint parentDevInst)
    {
        string lower = interfacePath.ToLowerInvariant();
        bool matchesVendor = lower.Contains("vid&023554") ||
                             lower.Contains("vid&013554") ||
                             lower.Contains("vid_3554");
        if (matchesVendor)
            return lower.Contains("pid&f533") ? 100 : 80;

        if (parentDevInst != 0)
        {
            string friendlyName = GetDevNodeStringProperty(parentDevInst, DevpkeyFriendlyName);
            if (!string.IsNullOrEmpty(friendlyName) &&
                friendlyName.IndexOf("g49", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return 60;
            }
        }

        return 0;
    }

    private static bool GetDevInfoBoolProperty(IntPtr deviceInfoSet, ref SP_DEVINFO_DATA devInfo, DEVPROPKEY key)
    {
        byte[] buffer = new byte[4];
        uint propType;
        uint requiredSize;
        if (SetupDiGetDeviceProperty(deviceInfoSet, ref devInfo, ref key, out propType,
            buffer, (uint)buffer.Length, out requiredSize, 0))
        {
            return buffer[0] != 0;
        }
        return false;
    }

    private static bool GetDevNodeBoolProperty(uint devInst, DEVPROPKEY key)
    {
        byte[] buffer = new byte[4];
        uint propType;
        uint size = (uint)buffer.Length;
        if (CM_Get_DevNode_PropertyW(devInst, ref key, out propType, buffer, ref size, 0) == 0 && size >= 1)
            return buffer[0] != 0;
        return false;
    }

    private static string GetDevNodeStringProperty(uint devInst, DEVPROPKEY key)
    {
        uint propType;
        uint size = 0;
        CM_Get_DevNode_PropertyW(devInst, ref key, out propType, null, ref size, 0);
        if (size < 2 || size > 1024)
            return "";

        byte[] buffer = new byte[size];
        if (CM_Get_DevNode_PropertyW(devInst, ref key, out propType, buffer, ref size, 0) != 0)
            return "";

        return Encoding.Unicode.GetString(buffer, 0, (int)size).TrimEnd('\0');
    }

    private static bool TryReadParentPnPBattery(uint parentDevInst, out byte percent)
    {
        percent = 0;
        if (parentDevInst == 0)
            return false;

        byte[] buffer = new byte[4];
        uint propType;
        uint size = (uint)buffer.Length;
        DEVPROPKEY key = DevpkeyBluetoothBattery;
        if (CM_Get_DevNode_PropertyW(parentDevInst, ref key, out propType, buffer, ref size, 0) == 0 && size >= 1)
        {
            byte value = buffer[0];
            if (value <= 100)
            {
                percent = value;
                return true;
            }
        }
        return false;
    }

    private static bool TryReadGattBattery(string path, out byte percent, out string error)
    {
        percent = 0;
        error = "";
        IntPtr handle = CreateFile(path, GenericRead, FileShareRead | FileShareWrite,
            IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
        if (handle == InvalidHandle)
        {
            error = "打开 BLE 电池服务失败(" + Marshal.GetLastWin32Error() + ")";
            return false;
        }

        try
        {
            ushort count = 0;
            int hr = BluetoothGATTGetCharacteristics(handle, IntPtr.Zero, 0, null, out count, BluetoothGattFlagNone);
            if (count == 0)
            {
                error = "未发现 BLE 电池特征(0x" + hr.ToString("X8") + ")";
                return false;
            }

            BTH_LE_GATT_CHARACTERISTIC[] chars = new BTH_LE_GATT_CHARACTERISTIC[count];
            hr = BluetoothGATTGetCharacteristics(handle, IntPtr.Zero, count, chars, out count, BluetoothGattFlagNone);
            if (hr != 0)
            {
                error = "获取 BLE 电池特征失败(0x" + hr.ToString("X8") + ")";
                return false;
            }

            for (int i = 0; i < count; i++)
            {
                BTH_LE_GATT_CHARACTERISTIC ch = chars[i];
                if (ch.CharacteristicUuid.IsShortUuid == 0 ||
                    ch.CharacteristicUuid.ShortUuid != BatteryLevelCharacteristicUuid)
                {
                    continue;
                }

                // First request a fresh value from the connected mouse; fall back to the
                // Windows GATT cache if a transient link hiccup occurs.
                if (TryReadCharacteristicValue(handle, ref ch, BluetoothGattFlagForceRead, out percent, out error))
                    return true;
                if (TryReadCharacteristicValue(handle, ref ch, BluetoothGattFlagNone, out percent, out error))
                    return true;
                return false;
            }

            error = "BLE 电池服务缺少 0x2A19 电量特征";
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            error = "当前系统不支持 BluetoothAPIs BLE GATT 接口";
            return false;
        }
        catch (DllNotFoundException)
        {
            error = "缺少 BluetoothAPIs.dll";
            return false;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private static bool TryReadCharacteristicValue(
        IntPtr handle, ref BTH_LE_GATT_CHARACTERISTIC characteristic, uint flags,
        out byte percent, out string error)
    {
        percent = 0;
        error = "";
        ushort requiredSize = 0;
        int hr = BluetoothGATTGetCharacteristicValue(handle, ref characteristic, 0, IntPtr.Zero,
            out requiredSize, flags);
        if (requiredSize < 5)
        {
            error = "读取 BLE 电量长度失败(0x" + hr.ToString("X8") + ")";
            return false;
        }

        IntPtr buffer = Marshal.AllocHGlobal(requiredSize);
        try
        {
            ushort actualSize = 0;
            hr = BluetoothGATTGetCharacteristicValue(handle, ref characteristic, requiredSize, buffer,
                out actualSize, flags);
            if (hr != 0)
            {
                error = "读取 BLE 电量值失败(0x" + hr.ToString("X8") + ")";
                return false;
            }

            int dataSize = Marshal.ReadInt32(buffer);
            if (dataSize < 1)
            {
                error = "BLE 电量回包为空";
                return false;
            }

            byte value = Marshal.ReadByte(buffer, 4);
            if (value > 100)
            {
                error = "BLE 电量值异常(" + value + ")";
                return false;
            }

            percent = value;
            return true;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
}

internal static class StartupSettings
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "G49ProBatteryTray";

    public static bool IsEnabled()
    {
        using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey, false))
            return key != null && key.GetValue(ValueName) != null;
    }

    public static void SetEnabled(bool enabled)
    {
        using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey, true))
        {
            if (key == null)
                throw new InvalidOperationException("无法打开当前用户的启动项设置");
            if (enabled)
                key.SetValue(ValueName, "\"" + Application.ExecutablePath + "\"");
            else
                key.DeleteValue(ValueName, false);
        }
    }
}

internal static class UserInputMonitor
{
    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

    public static bool TryGetIdleMilliseconds(out uint idleMilliseconds)
    {
        LASTINPUTINFO info = new LASTINPUTINFO();
        info.cbSize = (uint)Marshal.SizeOf(typeof(LASTINPUTINFO));
        if (!GetLastInputInfo(ref info))
        {
            idleMilliseconds = 0;
            return false;
        }

        uint now = unchecked((uint)Environment.TickCount);
        idleMilliseconds = unchecked(now - info.dwTime);
        return true;
    }
}

internal static class AlertSettings
{
    private const string SettingsKey = @"Software\G49ProBatteryTray";
    private const string ValueName = "LowBatteryThresholdPercent";
    public const int MinCustomThreshold = 1;
    public const int MaxCustomThreshold = 95;

    public static int ClampThreshold(int value)
    {
        if (value <= 0)
            return 0;
        if (value < MinCustomThreshold)
            return MinCustomThreshold;
        if (value > MaxCustomThreshold)
            return MaxCustomThreshold;
        return value;
    }

    public static int GetLowBatteryThreshold(int defaultValue)
    {
        try
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(SettingsKey, false))
            {
                if (key == null)
                    return ClampThreshold(defaultValue);

                object value = key.GetValue(ValueName);
                if (value == null)
                    return ClampThreshold(defaultValue);

                return ClampThreshold(Convert.ToInt32(value));
            }
        }
        catch (Exception)
        {
            return ClampThreshold(defaultValue);
        }
    }

    public static void SetLowBatteryThreshold(int percent)
    {
        int clamped = ClampThreshold(percent);
        using (RegistryKey key = Registry.CurrentUser.CreateSubKey(SettingsKey))
        {
            if (key == null)
                throw new InvalidOperationException("无法打开当前用户的提醒阈值设置");
            key.SetValue(ValueName, clamped, RegistryValueKind.DWord);
        }
    }
}

internal sealed class LowBatteryThresholdDialog : Form
{
    private readonly NumericUpDown thresholdInput;

    public int ThresholdPercent
    {
        get { return (int)thresholdInput.Value; }
    }

    public LowBatteryThresholdDialog(int currentThreshold)
    {
        Text = "自定义低电量提醒阈值";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterScreen;
        TopMost = true;
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = SystemFonts.MessageBoxFont;
        ClientSize = new Size(340, 148);

        try
        {
            Icon appIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            if (appIcon != null)
                Icon = appIcon;
        }
        catch (Exception)
        {
        }

        Label promptLabel = new Label();
        promptLabel.AutoSize = true;
        promptLabel.Text = "当鼠标电量降至以下数值及以下且未充电时提醒：";
        promptLabel.Location = new Point(18, 18);

        Label valueLabel = new Label();
        valueLabel.AutoSize = true;
        valueLabel.Text = "提醒阈值：";
        valueLabel.Location = new Point(18, 56);

        thresholdInput = new NumericUpDown();
        thresholdInput.Minimum = AlertSettings.MinCustomThreshold;
        thresholdInput.Maximum = AlertSettings.MaxCustomThreshold;
        thresholdInput.Value = Math.Max(AlertSettings.MinCustomThreshold,
            Math.Min(AlertSettings.MaxCustomThreshold, currentThreshold));
        thresholdInput.Location = new Point(92, 52);
        thresholdInput.Width = 84;
        thresholdInput.TextAlign = HorizontalAlignment.Right;

        Label percentLabel = new Label();
        percentLabel.AutoSize = true;
        percentLabel.Text = "%（可填 1 - 95）";
        percentLabel.Location = new Point(182, 56);

        Button okButton = new Button();
        okButton.Text = "确定";
        okButton.DialogResult = DialogResult.OK;
        okButton.Size = new Size(80, 30);
        okButton.Location = new Point(156, 102);

        Button cancelButton = new Button();
        cancelButton.Text = "取消";
        cancelButton.DialogResult = DialogResult.Cancel;
        cancelButton.Size = new Size(80, 30);
        cancelButton.Location = new Point(244, 102);

        Controls.Add(promptLabel);
        Controls.Add(valueLabel);
        Controls.Add(thresholdInput);
        Controls.Add(percentLabel);
        Controls.Add(okButton);
        Controls.Add(cancelButton);

        AcceptButton = okButton;
        CancelButton = cancelButton;
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Activate();
        thresholdInput.Focus();
        thresholdInput.Select(0, thresholdInput.Text.Length);
    }
}

internal static class TrayIconFactory
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    public static Icon CreateUnknownIcon()
    {
        return CreateIcon(-1, false, 10);
    }

    public static Icon CreateBatteryIcon(int percent, bool charging, int lowBatteryThreshold)
    {
        return CreateIcon(Math.Max(0, Math.Min(100, percent)), charging, lowBatteryThreshold);
    }

    private static int GetTrayIconSize()
    {
        int width = SystemInformation.SmallIconSize.Width;
        if (width < 16)
            return 16;
        if (width > 64)
            return 64;
        return width;
    }

    private static Icon CreateIcon(int percent, bool charging, int lowBatteryThreshold)
    {
        int size = GetTrayIconSize();
        bool light = IsSystemLightTaskbar();
        using (Bitmap bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb))
        using (Graphics graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            graphics.Clear(Color.Transparent);

            float mh = size * 0.84f;
            float mw = mh * 0.66f;
            float mx = (size - mw) / 2f;
            float my = (size - mh) / 2f;
            DrawLiquidMouse(graphics, mx, my, mw, mh, percent, charging, light, lowBatteryThreshold);

            IntPtr handle = bitmap.GetHicon();
            try
            {
                using (Icon temporary = Icon.FromHandle(handle))
                    return (Icon)temporary.Clone();
            }
            finally
            {
                DestroyIcon(handle);
            }
        }
    }

    public static bool IsCriticalLevel(int percent, bool charging, int lowBatteryThreshold)
    {
        if (percent < 0 || charging)
            return false;
        int criticalThreshold = lowBatteryThreshold > 0 ? lowBatteryThreshold : 10;
        return percent <= criticalThreshold;
    }

    public static void DrawLiquidMouse(Graphics graphics, float x, float y, float w, float h,
        int percent, bool charging, bool lightTaskbar, int lowBatteryThreshold)
    {
        Color fg = lightTaskbar ? Color.FromArgb(24, 26, 32) : Color.FromArgb(244, 246, 250);
        Color accent = GetStatusColor(percent, charging, lightTaskbar, lowBatteryThreshold);
        bool critical = IsCriticalLevel(percent, charging, lowBatteryThreshold);
        Color strokeColor = critical
            ? accent
            : Color.FromArgb(lightTaskbar ? 195 : 220, fg);

        RectangleF body = new RectangleF(x, y, w, h);
        using (GraphicsPath mousePath = RoundedRectangle(body, w * 0.48f))
        {
            // Subtle empty-chamber backing so the mouse shape stays crisp on both dark & light taskbars.
            Color emptyBacking = lightTaskbar
                ? Color.FromArgb(30, 15, 23, 42)
                : Color.FromArgb(38, 255, 255, 255);
            using (Brush backingBrush = new SolidBrush(emptyBacking))
                graphics.FillPath(backingBrush, mousePath);

            // Liquid battery fill rising from bottom to top inside the mouse silhouette.
            if (percent > 0)
            {
                GraphicsState saved = graphics.Save();
                graphics.SetClip(mousePath);
                float fillH = Math.Max(h * 0.12f, h * (percent / 100f));
                float fillTop = y + h - fillH;
                RectangleF fillRect = new RectangleF(x, fillTop, w, fillH + 0.5f);
                using (Brush fillBrush = new SolidBrush(Color.FromArgb(228, accent)))
                    graphics.FillRectangle(fillBrush, fillRect);
                graphics.Restore(saved);
            }

            float strokeWidth = Math.Max(1.25f, w * 0.095f);
            using (Pen borderPen = new Pen(strokeColor, strokeWidth))
                graphics.DrawPath(borderPen, mousePath);
        }

        // Crisp vertical scroll wheel; white when submerged in liquid, dark/light contrast when above liquid.
        float wheelW = Math.Max(2.0f, w * 0.17f);
        float wheelH = Math.Max(3.6f, h * 0.22f);
        float wheelX = x + (w - wheelW) / 2f;
        float wheelY = y + h * 0.17f;
        bool wheelSubmerged = percent >= 72;
        Color wheelColor = wheelSubmerged
            ? Color.White
            : (percent < 0 ? Color.FromArgb(160, fg) : (critical ? accent : Color.FromArgb(220, fg)));

        RectangleF wheelRect = new RectangleF(wheelX, wheelY, wheelW, wheelH);
        using (GraphicsPath wheelPath = RoundedRectangle(wheelRect, wheelW / 2f))
        using (Brush wheelBrush = new SolidBrush(wheelColor))
            graphics.FillPath(wheelBrush, wheelPath);

        // Non-overlapping miniature lightning bolt badge at bottom-right while charging.
        if (charging)
        {
            float boltH = Math.Max(7.5f, h * 0.42f);
            Color haloColor = lightTaskbar
                ? Color.FromArgb(220, 242, 244, 248)
                : Color.FromArgb(220, 24, 28, 34);
            DrawMiniBolt(graphics, x + w - 0.5f, y + h - boltH * 0.38f, boltH, haloColor, accent);
        }
    }

    public static void DrawMiniBolt(Graphics graphics, float cx, float cy, float h, Color haloColor, Color boltColor)
    {
        float w = h * 0.65f;
        float x = cx - w / 2f;
        float y = cy - h / 2f;
        PointF[] pts =
        {
            new PointF(x + w * 0.62f, y),
            new PointF(x + w * 0.05f, y + h * 0.55f),
            new PointF(x + w * 0.48f, y + h * 0.55f),
            new PointF(x + w * 0.34f, y + h),
            new PointF(x + w * 0.96f, y + h * 0.44f),
            new PointF(x + w * 0.54f, y + h * 0.44f)
        };
        if (haloColor.A > 0)
        {
            using (Pen haloPen = new Pen(haloColor, Math.Max(1.6f, h * 0.22f)))
            {
                haloPen.LineJoin = LineJoin.Round;
                graphics.DrawPolygon(haloPen, pts);
            }
        }
        using (Brush boltBrush = new SolidBrush(boltColor))
            graphics.FillPolygon(boltBrush, pts);
    }

    public static Color GetStatusColor(int percent, bool charging, bool lightTaskbar, int lowBatteryThreshold)
    {
        if (percent < 0)
            return lightTaskbar ? Color.FromArgb(100, 116, 139) : Color.FromArgb(148, 163, 184);
        if (charging)
            return lightTaskbar ? Color.FromArgb(2, 132, 199) : Color.FromArgb(56, 189, 248);
        if (IsCriticalLevel(percent, charging, lowBatteryThreshold))
            return lightTaskbar ? Color.FromArgb(220, 38, 38) : Color.FromArgb(248, 113, 113);
        if (percent < 30)
            return lightTaskbar ? Color.FromArgb(217, 119, 6) : Color.FromArgb(251, 146, 60);
        if (percent < 50)
            return lightTaskbar ? Color.FromArgb(202, 138, 4) : Color.FromArgb(250, 204, 21);
        return lightTaskbar ? Color.FromArgb(16, 163, 95) : Color.FromArgb(52, 211, 153);
    }

    public static bool IsSystemLightTaskbar()
    {
        try
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", false))
            {
                if (key == null)
                    return false;
                object val = key.GetValue("SystemUsesLightTheme");
                return val != null && Convert.ToInt32(val) != 0;
            }
        }
        catch (Exception)
        {
            return false;
        }
    }

    public static GraphicsPath RoundedRectangle(RectangleF bounds, float radius)
    {
        GraphicsPath path = new GraphicsPath();
        float diameter = radius * 2f;
        if (diameter > bounds.Width)
            diameter = bounds.Width;
        if (diameter > bounds.Height)
            diameter = bounds.Height;
        if (diameter <= 0.1f)
        {
            path.AddRectangle(bounds);
            return path;
        }

        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}

internal sealed class TaskbarBatteryWidget : Form
{
    private const int WsExLayered = 0x00080000;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoActivate = 0x08000000;
    private const int WsExTopMost = 0x00000008;
    private const int WmMouseActivate = 0x0021;
    private const int MaNoActivate = 3;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;
    private const uint GwHwndPrev = 3;
    private const byte AcSrcOver = 0x00;
    private const byte AcSrcAlpha = 0x01;
    private const int UlwAlpha = 0x02;
    private const int QunsBusy = 2;
    private const int QunsRunningD3DFullScreen = 3;
    private const int QunsPresentationMode = 4;
    private static readonly IntPtr HwndTopMost = new IntPtr(-1);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public int Width { get { return Right - Left; } }
        public int Height { get { return Bottom - Top; } }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;

        public POINT(int x, int y)
        {
            X = x;
            Y = y;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE
    {
        public int Width;
        public int Height;

        public SIZE(int width, int height)
        {
            Width = width;
            Height = height;
        }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct BLENDFUNCTION
    {
        public byte BlendOp;
        public byte BlendFlags;
        public byte SourceConstantAlpha;
        public byte AlphaFormat;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string className, string windowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowEx(IntPtr parentHandle, IntPtr childAfter,
        string className, string windowTitle);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder className, int maxCount);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint cmd);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst,
        ref SIZE psize, IntPtr hdcSrc, ref POINT pptSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hDC);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hDC);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hDC, IntPtr hObject);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("shell32.dll")]
    private static extern int SHQueryUserNotificationState(out int pquns);

    private readonly ContextMenuStrip trayMenu;
    private readonly ToolTip hoverTip;
    private readonly Timer dockTimer;
    private int currentPercent = -1;
    private bool currentCharging;
    private int lowBatteryThresholdPercent = 10;
    private bool hovered;
    private bool lightTaskbar;
    private bool closingForExit;
    private bool visuallyDirty = true;
    private Rectangle lastDockedBounds = Rectangle.Empty;

    public event EventHandler StatusRequested;

    public TaskbarBatteryWidget(ContextMenuStrip contextMenu)
    {
        trayMenu = contextMenu;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        Size = new Size(36, 36);
        lightTaskbar = TrayIconFactory.IsSystemLightTaskbar();

        hoverTip = new ToolTip();
        hoverTip.InitialDelay = 300;
        hoverTip.ReshowDelay = 150;
        hoverTip.AutoPopDelay = 8000;
        hoverTip.ShowAlways = true;

        dockTimer = new Timer();
        dockTimer.Interval = 250;
        dockTimer.Tick += delegate { SyncWithTaskbar(); };
    }

    protected override bool ShowWithoutActivation
    {
        get { return true; }
    }

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams cp = base.CreateParams;
            cp.ExStyle |= WsExLayered | WsExToolWindow | WsExNoActivate | WsExTopMost;
            return cp;
        }
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmMouseActivate)
        {
            m.Result = new IntPtr(MaNoActivate);
            return;
        }
        base.WndProc(ref m);
    }

    public void StartDocking()
    {
        CreateHandle();
        SyncWithTaskbar();
        dockTimer.Start();
    }

    public void SetLowBatteryThreshold(int thresholdPercent)
    {
        int clamped = AlertSettings.ClampThreshold(thresholdPercent);
        if (lowBatteryThresholdPercent != clamped)
        {
            lowBatteryThresholdPercent = clamped;
            visuallyDirty = true;
            if (Visible)
                RenderLayeredBitmap();
        }
    }

    public void SetReading(int percent, bool charging, string tooltipText)
    {
        int clamped = Math.Max(0, Math.Min(100, percent));
        if (currentPercent != clamped || currentCharging != charging)
        {
            currentPercent = clamped;
            currentCharging = charging;
            visuallyDirty = true;
            SyncWithTaskbar();
            if (Visible && visuallyDirty)
                RenderLayeredBitmap();
        }
        UpdateTooltip(tooltipText);
    }

    public void SetUnknown(string tooltipText)
    {
        if (currentPercent != -1 || currentCharging)
        {
            currentPercent = -1;
            currentCharging = false;
            visuallyDirty = true;
            SyncWithTaskbar();
            if (Visible && visuallyDirty)
                RenderLayeredBitmap();
        }
        UpdateTooltip(tooltipText);
    }

    public void RefreshAppearance()
    {
        lightTaskbar = TrayIconFactory.IsSystemLightTaskbar();
        visuallyDirty = true;
        SyncWithTaskbar();
        if (Visible)
            RenderLayeredBitmap();
    }

    public void CloseForExit()
    {
        closingForExit = true;
        dockTimer.Stop();
        Close();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            dockTimer.Dispose();
            hoverTip.Dispose();
        }
        base.Dispose(disposing);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!closingForExit && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            return;
        }
        base.OnFormClosing(e);
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        if (!hovered)
        {
            hovered = true;
            visuallyDirty = true;
            RenderLayeredBitmap();
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (hovered)
        {
            hovered = false;
            visuallyDirty = true;
            RenderLayeredBitmap();
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button == MouseButtons.Right && trayMenu != null)
        {
            // Activate our hidden message owner momentarily so clicking outside closes the popup menu.
            SetForegroundWindow(Handle);
            trayMenu.Show(Cursor.Position);
        }
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        if (e.Button == MouseButtons.Left)
        {
            EventHandler handler = StatusRequested;
            if (handler != null)
                handler(this, EventArgs.Empty);
        }
    }

    private void UpdateTooltip(string text)
    {
        if (IsDisposed)
            return;
        hoverTip.SetToolTip(this, string.IsNullOrEmpty(text) ? "红龙G49 PRO鼠标电量助手" : text);
    }

    private void SyncWithTaskbar()
    {
        if (closingForExit || IsDisposed)
            return;

        IntPtr shellTray = FindWindow("Shell_TrayWnd", null);
        if (shellTray == IntPtr.Zero || !IsWindowVisible(shellTray))
        {
            HideWidget();
            return;
        }

        RECT trayRect;
        if (!GetWindowRect(shellTray, out trayRect) || trayRect.Width <= 0 || trayRect.Height <= 0)
        {
            HideWidget();
            return;
        }

        Screen taskbarScreen = Screen.FromRectangle(
            new Rectangle(trayRect.Left, trayRect.Top, trayRect.Width, trayRect.Height));
        if (!IsTaskbarVisibleOnScreen(trayRect, taskbarScreen.Bounds) || IsFullscreenAppActive(shellTray, taskbarScreen.Bounds))
        {
            HideWidget();
            return;
        }

        IntPtr trayNotify = FindWindowEx(shellTray, IntPtr.Zero, "TrayNotifyWnd", null);
        RECT notifyRect = new RECT();
        bool hasNotify = trayNotify != IntPtr.Zero &&
                         GetWindowRect(trayNotify, out notifyRect) &&
                         notifyRect.Width > 0 && notifyRect.Height > 0;

        bool horizontal = trayRect.Width >= trayRect.Height;
        int targetW;
        int targetH;
        int targetX;
        int targetY;

        if (horizontal)
        {
            int barH = trayRect.Height;
            targetH = Math.Max(28, (int)Math.Round(barH * 0.76));
            targetW = MeasureHorizontalWidgetWidth(barH);
            int gap = Math.Max(2, (int)Math.Round(barH * 0.04));
            int anchorRight = hasNotify ? notifyRect.Left : (trayRect.Right - barH * 3);
            targetX = Math.Max(trayRect.Left + 8, anchorRight - targetW - gap);
            targetY = trayRect.Top + (barH - targetH) / 2;
        }
        else
        {
            int barW = trayRect.Width;
            targetW = Math.Max(30, (int)Math.Round(barW * 0.84));
            targetH = Math.Max(38, (int)Math.Round(barW * 0.84));
            int gap = Math.Max(2, (int)Math.Round(barW * 0.06));
            int anchorBottom = hasNotify ? notifyRect.Top : (trayRect.Bottom - barW * 3);
            targetX = trayRect.Left + (barW - targetW) / 2;
            targetY = Math.Max(trayRect.Top + 8, anchorBottom - targetH - gap);
        }

        Rectangle targetBounds = new Rectangle(targetX, targetY, targetW, targetH);
        bool boundsChanged = targetBounds != lastDockedBounds;
        bool sizeChanged = targetW != lastDockedBounds.Width || targetH != lastDockedBounds.Height;

        if (sizeChanged)
            visuallyDirty = true;

        lastDockedBounds = targetBounds;

        if (!Visible)
        {
            Bounds = targetBounds;
            Show();
            visuallyDirty = true;
            RenderLayeredBitmap();
            SetWindowPos(Handle, HwndTopMost, targetX, targetY, targetW, targetH,
                SwpNoActivate | SwpShowWindow);
            return;
        }

        if (visuallyDirty)
        {
            if (sizeChanged)
                Size = new Size(targetW, targetH);
            RenderLayeredBitmap();
        }

        if (boundsChanged)
        {
            SetWindowPos(Handle, HwndTopMost, targetX, targetY, targetW, targetH,
                SwpNoActivate | SwpShowWindow);
        }
        else if (IsWindowAboveUs(shellTray))
        {
            SetWindowPos(Handle, HwndTopMost, 0, 0, 0, 0,
                SwpNoActivate | SwpNoMove | SwpNoSize | SwpShowWindow);
        }
    }

    private void HideWidget()
    {
        if (Visible)
        {
            hovered = false;
            Hide();
        }
    }

    private bool IsWindowAboveUs(IntPtr otherHandle)
    {
        IntPtr cursor = GetWindow(Handle, GwHwndPrev);
        int guard = 0;
        while (cursor != IntPtr.Zero && guard < 25)
        {
            if (cursor == otherHandle)
                return true;
            cursor = GetWindow(cursor, GwHwndPrev);
            guard++;
        }
        return false;
    }

    private static bool IsTaskbarVisibleOnScreen(RECT trayRect, Rectangle screenBounds)
    {
        Rectangle tray = new Rectangle(trayRect.Left, trayRect.Top, trayRect.Width, trayRect.Height);
        Rectangle visiblePart = Rectangle.Intersect(tray, screenBounds);
        return visiblePart.Width >= 16 && visiblePart.Height >= 16;
    }

    private bool IsFullscreenAppActive(IntPtr shellTray, Rectangle screenBounds)
    {
        try
        {
            int state;
            if (SHQueryUserNotificationState(out state) == 0)
            {
                if (state == QunsBusy || state == QunsRunningD3DFullScreen || state == QunsPresentationMode)
                    return true;
            }
        }
        catch (Exception)
        {
        }

        IntPtr foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero || foreground == Handle || foreground == shellTray || !IsWindowVisible(foreground))
            return false;

        string cls = GetWindowClassName(foreground);
        if (string.Equals(cls, "Progman", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(cls, "WorkerW", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(cls, "Shell_TrayWnd", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(cls, "Shell_SecondaryTrayWnd", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(cls, "Windows.UI.Core.CoreWindow", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        RECT fgRect;
        if (!GetWindowRect(foreground, out fgRect))
            return false;

        return fgRect.Left <= screenBounds.Left + 1 &&
               fgRect.Top <= screenBounds.Top + 1 &&
               fgRect.Right >= screenBounds.Right - 1 &&
               fgRect.Bottom >= screenBounds.Bottom - 1;
    }

    private static string GetWindowClassName(IntPtr hWnd)
    {
        StringBuilder sb = new StringBuilder(128);
        int len = GetClassName(hWnd, sb, sb.Capacity);
        return len > 0 ? sb.ToString(0, len) : "";
    }

    private string FormatDisplayText()
    {
        return currentPercent < 0 ? "--" : (currentPercent.ToString() + "%");
    }

    private int MeasureHorizontalWidgetWidth(int barHeight)
    {
        float mouseH = Math.Max(16f, barHeight * 0.365f);
        float mouseW = mouseH * 0.66f;
        float fontPx = Math.Max(11.5f, barHeight * 0.22f);
        float gap = Math.Max(4.5f, barHeight * 0.095f);
        float padX = Math.Max(6.5f, barHeight * 0.145f);
        string text = FormatDisplayText();

        using (Bitmap probe = new Bitmap(1, 1, PixelFormat.Format32bppPArgb))
        using (Graphics g = Graphics.FromImage(probe))
        using (Font font = new Font("Segoe UI", fontPx, FontStyle.Regular, GraphicsUnit.Pixel))
        using (StringFormat fmt = new StringFormat(StringFormat.GenericTypographic))
        {
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            fmt.FormatFlags |= StringFormatFlags.NoClip;
            SizeF textSize = g.MeasureString(text, font, 200, fmt);
            return (int)Math.Ceiling(padX + mouseW + gap + textSize.Width + padX);
        }
    }

    private void RenderLayeredBitmap()
    {
        if (!IsHandleCreated || IsDisposed)
            return;

        int width = Math.Max(24, lastDockedBounds.Width > 0 ? lastDockedBounds.Width : Width);
        int height = Math.Max(24, lastDockedBounds.Height > 0 ? lastDockedBounds.Height : Height);
        bool horizontalLayout = width >= height;

        using (Bitmap bitmap = new Bitmap(width, height, PixelFormat.Format32bppPArgb))
        using (Graphics graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            graphics.Clear(Color.Transparent);

            // Fluent hover highlight matching Windows 11 taskbar buttons; 1/255 alpha keeps hit-testing active.
            RectangleF hitTile = new RectangleF(1f, 1f, width - 2f, height - 2f);
            float tileRadius = Math.Max(4f, Math.Min(width, height) * 0.14f);
            using (GraphicsPath tilePath = TrayIconFactory.RoundedRectangle(hitTile, tileRadius))
            {
                Color hoverBg = hovered
                    ? (lightTaskbar ? Color.FromArgb(22, 0, 0, 0) : Color.FromArgb(24, 255, 255, 255))
                    : Color.FromArgb(1, 0, 0, 0);
                using (Brush hoverBrush = new SolidBrush(hoverBg))
                    graphics.FillPath(hoverBrush, tilePath);

                if (hovered)
                {
                    Color hoverBorder = lightTaskbar
                        ? Color.FromArgb(26, 0, 0, 0)
                        : Color.FromArgb(28, 255, 255, 255);
                    using (Pen hoverPen = new Pen(hoverBorder, 1f))
                        graphics.DrawPath(hoverPen, tilePath);
                }
            }

            Color fg = lightTaskbar ? Color.FromArgb(24, 26, 32) : Color.FromArgb(244, 246, 250);
            Color accent = TrayIconFactory.GetStatusColor(currentPercent, currentCharging, lightTaskbar, lowBatteryThresholdPercent);
            bool critical = TrayIconFactory.IsCriticalLevel(currentPercent, currentCharging, lowBatteryThresholdPercent);
            Color textColor = critical ? accent : fg;
            string text = FormatDisplayText();

            if (horizontalLayout)
            {
                // Reverse-derive taskbar barHeight from tile height (targetH = barH * 0.76).
                float barH = height / 0.76f;
                float mouseH = Math.Max(16f, barH * 0.365f);
                float mouseW = mouseH * 0.66f;
                float fontPx = Math.Max(11.5f, barH * 0.22f);
                float gap = Math.Max(4.5f, barH * 0.095f);

                using (Font font = new Font("Segoe UI", fontPx, FontStyle.Regular, GraphicsUnit.Pixel))
                using (Brush textBrush = new SolidBrush(textColor))
                using (StringFormat fmt = new StringFormat(StringFormat.GenericTypographic))
                {
                    fmt.FormatFlags |= StringFormatFlags.NoClip;
                    SizeF textSize = graphics.MeasureString(text, font, 200, fmt);
                    float contentW = mouseW + gap + textSize.Width;
                    float startX = (width - contentW) / 2f;
                    float centerY = height / 2f;

                    TrayIconFactory.DrawLiquidMouse(graphics,
                        startX, centerY - mouseH / 2f, mouseW, mouseH,
                        currentPercent, currentCharging, lightTaskbar, lowBatteryThresholdPercent);

                    graphics.DrawString(text, font, textBrush,
                        startX + mouseW + gap, centerY - textSize.Height / 2f - 0.3f, fmt);
                }
            }
            else
            {
                // Vertical taskbar fallback: liquid mouse centered on top, percentage underneath.
                float mouseH = Math.Max(14f, height * 0.48f);
                float mouseW = mouseH * 0.66f;
                float mx = (width - mouseW) / 2f;
                float my = height * 0.10f;
                TrayIconFactory.DrawLiquidMouse(graphics, mx, my, mouseW, mouseH,
                    currentPercent, currentCharging, lightTaskbar, lowBatteryThresholdPercent);

                float fontPx = Math.Max(10f, width * 0.26f);
                using (Font font = new Font("Segoe UI", fontPx, FontStyle.Regular, GraphicsUnit.Pixel))
                using (Brush textBrush = new SolidBrush(textColor))
                using (StringFormat fmt = new StringFormat(StringFormat.GenericTypographic))
                {
                    fmt.Alignment = StringAlignment.Center;
                    fmt.LineAlignment = StringAlignment.Center;
                    fmt.FormatFlags |= StringFormatFlags.NoClip;
                    graphics.DrawString(text, font, textBrush,
                        new RectangleF(0f, my + mouseH + 1f, width, height - (my + mouseH + 2f)), fmt);
                }
            }

            ApplyLayeredBitmap(bitmap, width, height);
        }

        visuallyDirty = false;
    }

    private void ApplyLayeredBitmap(Bitmap bitmap, int width, int height)
    {
        IntPtr screenDc = GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero)
            return;

        IntPtr memDc = IntPtr.Zero;
        IntPtr hBitmap = IntPtr.Zero;
        IntPtr oldBitmap = IntPtr.Zero;
        try
        {
            memDc = CreateCompatibleDC(screenDc);
            hBitmap = bitmap.GetHbitmap(Color.FromArgb(0));
            oldBitmap = SelectObject(memDc, hBitmap);

            POINT destPoint = new POINT(
                lastDockedBounds.IsEmpty ? Left : lastDockedBounds.Left,
                lastDockedBounds.IsEmpty ? Top : lastDockedBounds.Top);
            SIZE bitmapSize = new SIZE(width, height);
            POINT srcPoint = new POINT(0, 0);
            BLENDFUNCTION blend = new BLENDFUNCTION
            {
                BlendOp = AcSrcOver,
                BlendFlags = 0,
                SourceConstantAlpha = 255,
                AlphaFormat = AcSrcAlpha
            };

            UpdateLayeredWindow(Handle, screenDc, ref destPoint, ref bitmapSize,
                memDc, ref srcPoint, 0, ref blend, UlwAlpha);
        }
        finally
        {
            if (oldBitmap != IntPtr.Zero && memDc != IntPtr.Zero)
                SelectObject(memDc, oldBitmap);
            if (hBitmap != IntPtr.Zero)
                DeleteObject(hBitmap);
            if (memDc != IntPtr.Zero)
                DeleteDC(memDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }
}
