// G49-PRO battery tray utility.
// It sends only the receiver's reverse-engineered, read-only BatteryLevel request.
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Media;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
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
}

internal sealed class G49BatteryTrayContext : ApplicationContext
{
    // Normal polling follows the requested ten-minute cadence. Shorter delays
    // are used only while recovering from an unavailable receiver at logon or
    // after the mouse has gone to sleep.
    private const int PollIntervalMilliseconds = 10 * 60 * 1000;
    private static readonly int[] FailedReadRetryIntervalsMilliseconds =
    {
        15 * 1000,
        60 * 1000,
        5 * 60 * 1000,
        15 * 60 * 1000
    };
    private const int LowBatteryPercent = 15;
    private const int LowBatteryReminderMinutes = 30;

    private readonly NotifyIcon trayIcon;
    private readonly ContextMenuStrip menu;
    private readonly ToolStripMenuItem statusItem;
    private readonly ToolStripMenuItem refreshItem;
    private readonly ToolStripMenuItem startupItem;
    private readonly ToolStripMenuItem desktopBadgeItem;
    private readonly ToolStripMenuItem diagnosticItem;
    private readonly System.Threading.Timer pollTimer;
    private readonly BackgroundWorker pollWorker;
    private readonly BatteryBadgeForm desktopBadge;
    private readonly Control pollInvoker;
    private Icon activeIcon;
    private BatteryReading lastReading;
    private bool hasReading;
    private string lastError = "正在读取鼠标电量...";
    private DateTime lastLowBatteryAlertUtc = DateTime.MinValue;
    private DateTime lastPollAttemptLocal = DateTime.MinValue;
    private DateTime nextPollLocal = DateTime.MinValue;
    private int consecutiveReadFailures;
    private bool disposed;

    public G49BatteryTrayContext()
    {
        menu = new ContextMenuStrip();
        statusItem = new ToolStripMenuItem("G49：正在读取...");
        statusItem.Enabled = false;
        refreshItem = new ToolStripMenuItem("立即刷新", null, delegate { StartPoll(); });
        startupItem = new ToolStripMenuItem("开机启动");
        startupItem.CheckOnClick = true;
        startupItem.Checked = StartupSettings.IsEnabled();
        startupItem.CheckedChanged += StartupItem_CheckedChanged;
        desktopBadgeItem = new ToolStripMenuItem("显示桌面电量标签（推荐）");
        desktopBadgeItem.CheckOnClick = true;
        desktopBadgeItem.Checked = DesktopBadgeSettings.IsVisible();
        desktopBadgeItem.CheckedChanged += DesktopBadgeItem_CheckedChanged;
        diagnosticItem = new ToolStripMenuItem("复制诊断信息", null, delegate { CopyDiagnosticInfo(); });
        ToolStripMenuItem exitItem = new ToolStripMenuItem("退出", null, delegate { ExitThread(); });

        menu.Items.Add(statusItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(refreshItem);
        menu.Items.Add(startupItem);
        menu.Items.Add(desktopBadgeItem);
        menu.Items.Add(diagnosticItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(exitItem);

        trayIcon = new NotifyIcon();
        trayIcon.ContextMenuStrip = menu;
        trayIcon.Text = "G49 鼠标：正在读取电量";
        trayIcon.DoubleClick += delegate { ShowStatus(); };
        SetTrayIcon(TrayIconFactory.CreateUnknownIcon());
        trayIcon.Visible = true;

        desktopBadge = new BatteryBadgeForm();
        desktopBadge.ContextMenuStrip = menu;
        desktopBadge.DismissRequested += DesktopBadge_DismissRequested;
        SetDesktopBadgeVisible(desktopBadgeItem.Checked, false);

        // Keep a message-pump target even when the desktop badge is hidden.
        pollInvoker = new Control();
        pollInvoker.CreateControl();

        pollWorker = new BackgroundWorker();
        pollWorker.DoWork += PollWorker_DoWork;
        pollWorker.RunWorkerCompleted += PollWorker_RunWorkerCompleted;

        pollTimer = new System.Threading.Timer(PollTimerCallback, null,
            System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
        StartPoll();
    }

    private void PollTimerCallback(object state)
    {
        if (disposed || pollInvoker.IsDisposed || !pollInvoker.IsHandleCreated)
            return;

        try
        {
            pollInvoker.BeginInvoke(new MethodInvoker(StartPoll));
        }
        catch (InvalidOperationException)
        {
            // The UI is closing; ExitThreadCore will dispose the timer.
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

    private void DesktopBadgeItem_CheckedChanged(object sender, EventArgs e)
    {
        SetDesktopBadgeVisible(desktopBadgeItem.Checked, true);
    }

    private void DesktopBadge_DismissRequested(object sender, EventArgs e)
    {
        if (disposed)
            return;

        desktopBadgeItem.CheckedChanged -= DesktopBadgeItem_CheckedChanged;
        desktopBadgeItem.Checked = false;
        desktopBadgeItem.CheckedChanged += DesktopBadgeItem_CheckedChanged;
        SetDesktopBadgeVisible(false, true);
    }

    private void SetDesktopBadgeVisible(bool visible, bool persist)
    {
        if (persist)
        {
            try
            {
                DesktopBadgeSettings.SetVisible(visible);
            }
            catch (Exception ex)
            {
                trayIcon.ShowBalloonTip(3500, "红龙G49 PRO鼠标电量助手",
                    "无法保存桌面标签设置：" + ex.Message, ToolTipIcon.Warning);
            }
        }

        if (visible)
        {
            if (!desktopBadge.Visible)
                desktopBadge.Show();
            desktopBadge.BringToFront();
        }
        else
        {
            desktopBadge.Hide();
        }
    }

    private void StartPoll()
    {
        if (disposed)
            return;

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

        string charging = reading.IsCharging ? "充电中" : "未充电";
        statusItem.Text = string.Format("G49：{0}%（{1}，{2} mV）", reading.Percent, charging, reading.Millivolts);
        trayIcon.Text = LimitTooltip(string.Format("G49 鼠标：{0}% | {1} | {2} mV", reading.Percent, charging, reading.Millivolts));
        SetTrayIcon(TrayIconFactory.CreateBatteryIcon(reading.Percent, reading.IsCharging));
        desktopBadge.SetReading(reading);

        bool needsCharge = !reading.IsCharging && reading.Percent < LowBatteryPercent;
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
                string.Format("当前仅剩 {0}%，请给鼠标充电。", reading.Percent),
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
            string staleStatus = likelySleeping ? "等待鼠标唤醒" : "读取重试中";
            string statusDescription = likelySleeping ? "鼠标可能休眠" : "本次读取失败";
            statusItem.Text = string.Format("G49：{0}%（{1}，{2}自动重试）",
                lastReading.Percent, statusDescription, retryText);
            trayIcon.Text = LimitTooltip(string.Format("G49 鼠标：{0}% | {1}，{2}自动重试",
                lastReading.Percent, statusDescription, retryText));
            desktopBadge.SetStale(lastReading, staleStatus);
            return;
        }

        string unavailableDescription = likelySleeping ? "鼠标可能休眠，请移动鼠标唤醒" : "暂时无法读取";
        statusItem.Text = string.Format("G49：{0}（{1}自动重试）", unavailableDescription, retryText);
        trayIcon.Text = LimitTooltip("G49 鼠标：" + unavailableDescription + "，" + retryText + "自动重试");
        SetTrayIcon(TrayIconFactory.CreateUnknownIcon());
        desktopBadge.SetFailure(lastError);
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
        if (hasReading)
        {
            string charging = lastReading.IsCharging ? "是" : "否";
            MessageBox.Show(
                string.Format("电量：{0}%\r\n充电中：{1}\r\n电压：{2} mV\r\n\r\n低于 15% 且未充电时会提醒。",
                    lastReading.Percent, charging, lastReading.Millivolts),
                "红龙G49 PRO鼠标电量助手",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
        else
        {
            string wakeHint = IsLikelySleepingFailure(lastError)
                ? "鼠标可能处于休眠状态，请移动一下鼠标唤醒它"
                : "请确认接收器已连接";
            MessageBox.Show(
                "暂时无法读取鼠标电量。\r\n\r\n" + wakeHint + "；程序将在" + DescribeNextPoll() +
                "自动重试；也可以右键托盘图标选择“立即刷新”。\r\n\r\n原因：" + lastError,
                "红龙G49 PRO鼠标电量助手",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
    }

    private void CopyDiagnosticInfo()
    {
        string text;
        string schedule = string.Format(
            "\r\n上次查询：{0}\r\n下次自动查询：{1}",
            FormatDiagnosticTime(lastPollAttemptLocal),
            FormatDiagnosticTime(nextPollLocal));
        if (hasReading)
        {
            text = string.Format(
                "G49 Battery Tray\r\n状态：可读取\r\n电量：{0}%\r\n充电中：{1}\r\n电压：{2} mV\r\n协议：HID report 08 04（只读 BatteryLevel 请求）{3}",
                lastReading.Percent,
                lastReading.IsCharging ? "是" : "否",
                lastReading.Millivolts,
                schedule);
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
            pollTimer.Dispose();
            pollInvoker.Dispose();
            trayIcon.Visible = false;
            trayIcon.Dispose();
            desktopBadge.CloseForExit();
            desktopBadge.Dispose();
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

internal struct BatteryReading
{
    public byte Percent;
    public bool IsCharging;
    public ushort Millivolts;
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
                return true;
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

internal static class DesktopBadgeSettings
{
    private const string SettingsKey = @"Software\G49ProBatteryTray";
    private const string ValueName = "ShowDesktopBadge";

    public static bool IsVisible()
    {
        using (RegistryKey key = Registry.CurrentUser.OpenSubKey(SettingsKey, false))
        {
            if (key == null)
                return true;

            object value = key.GetValue(ValueName);
            if (value == null)
                return true;

            try
            {
                return Convert.ToInt32(value) != 0;
            }
            catch (Exception)
            {
                return true;
            }
        }
    }

    public static void SetVisible(bool visible)
    {
        using (RegistryKey key = Registry.CurrentUser.CreateSubKey(SettingsKey))
        {
            if (key == null)
                throw new InvalidOperationException("无法打开桌面标签设置");
            key.SetValue(ValueName, visible ? 1 : 0, RegistryValueKind.DWord);
        }
    }
}

internal sealed class BatteryBadgeForm : Form
{
    private int percent = -1;
    private bool charging;
    private string detail = "正在读取";
    private bool dragging;
    private Point dragOrigin;
    private Point windowOrigin;
    private bool closingForExit;

    public event EventHandler DismissRequested;

    public BatteryBadgeForm()
    {
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(122, 52);
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        DoubleBuffered = true;
        BackColor = Color.FromArgb(31, 38, 48);
        Location = DefaultLocation();
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
    }

    protected override bool ShowWithoutActivation
    {
        get { return true; }
    }

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams parameters = base.CreateParams;
            parameters.ExStyle |= 0x08000000; // WS_EX_NOACTIVATE
            return parameters;
        }
    }

    public void SetReading(BatteryReading reading)
    {
        percent = reading.Percent;
        charging = reading.IsCharging;
        detail = charging ? "充电中" : "G49 鼠标";
        Invalidate();
    }

    public void SetFailure(string failure)
    {
        percent = -1;
        charging = false;
        detail = "暂时无法读取";
        Invalidate();
    }

    public void SetStale(BatteryReading reading, string status)
    {
        percent = reading.Percent;
        charging = reading.IsCharging;
        detail = status;
        Invalidate();
    }

    public void CloseForExit()
    {
        closingForExit = true;
        Close();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!closingForExit && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            EventHandler handler = DismissRequested;
            if (handler != null)
                handler(this, EventArgs.Empty);
        }
        base.OnFormClosing(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        Rectangle bounds = new Rectangle(0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
        Color accent = AccentColor(percent, charging);

        using (GraphicsPath outline = RoundedRectangle(bounds, 11))
        using (Brush background = new SolidBrush(Color.FromArgb(31, 38, 48)))
        using (Pen border = new Pen(Color.FromArgb(185, 206, 218, 232), 1.1f))
        using (Brush accentBrush = new SolidBrush(accent))
        using (Font percentFont = new Font("Segoe UI", 19f, FontStyle.Bold, GraphicsUnit.Pixel))
        using (Font detailFont = new Font("Segoe UI", 10f, FontStyle.Regular, GraphicsUnit.Pixel))
        {
            e.Graphics.FillPath(background, outline);
            e.Graphics.DrawPath(border, outline);
            e.Graphics.FillEllipse(accentBrush, 8, 9, 34, 34);

            string symbol = percent < 0 ? "?" : (charging ? "⚡" : "%");
            using (Font symbolFont = new Font("Segoe UI Symbol", charging ? 18f : 15f,
                       FontStyle.Bold, GraphicsUnit.Pixel))
            {
                TextRenderer.DrawText(e.Graphics, symbol, symbolFont, new Rectangle(8, 8, 34, 35),
                    Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                    TextFormatFlags.NoPadding);
            }

            string value = percent < 0 ? "--" : percent.ToString() + "%";
            TextRenderer.DrawText(e.Graphics, value, percentFont, new Rectangle(47, 5, 70, 25), Color.White,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            TextRenderer.DrawText(e.Graphics, detail, detailFont, new Rectangle(48, 29, 68, 16),
                Color.FromArgb(207, 221, 233),
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding |
                TextFormatFlags.EndEllipsis);
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left)
        {
            dragging = true;
            dragOrigin = Cursor.Position;
            windowOrigin = Location;
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (dragging)
        {
            Point point = Cursor.Position;
            Location = new Point(windowOrigin.X + point.X - dragOrigin.X,
                windowOrigin.Y + point.Y - dragOrigin.Y);
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        dragging = false;
        base.OnMouseUp(e);
    }

    private static Point DefaultLocation()
    {
        Rectangle workingArea = Screen.PrimaryScreen.WorkingArea;
        return new Point(workingArea.Right - 142, workingArea.Bottom - 88);
    }

    private static Color AccentColor(int value, bool isCharging)
    {
        if (value < 0)
            return Color.FromArgb(105, 112, 122);
        if (isCharging)
            return Color.FromArgb(42, 145, 214);
        if (value < 10)
            return Color.FromArgb(215, 61, 55);
        if (value < 30)
            return Color.FromArgb(223, 172, 0);
        if (value < 50)
            return Color.FromArgb(239, 137, 0);
        return Color.FromArgb(24, 166, 88);
    }

    private static GraphicsPath RoundedRectangle(Rectangle bounds, int radius)
    {
        GraphicsPath path = new GraphicsPath();
        int diameter = radius * 2;
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}

internal static class TrayIconFactory
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    public static Icon CreateUnknownIcon()
    {
        return CreateIcon(-1, false);
    }

    public static Icon CreateBatteryIcon(int percent, bool charging)
    {
        return CreateIcon(Math.Max(0, Math.Min(100, percent)), charging);
    }

    private static Icon CreateIcon(int percent, bool charging)
    {
        using (Bitmap bitmap = new Bitmap(32, 32))
        using (Graphics graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.Clear(Color.Transparent);

            if (percent < 0)
            {
                DrawUnknown(graphics);
            }
            else
            {
                DrawBattery(graphics, percent, charging);
            }

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

    private static void DrawUnknown(Graphics graphics)
    {
        using (Brush fill = new SolidBrush(Color.FromArgb(105, 112, 122)))
        using (Pen border = new Pen(Color.White, 2f))
        using (Font font = new Font("Segoe UI", 17f, FontStyle.Bold, GraphicsUnit.Pixel))
        {
            graphics.FillEllipse(fill, 3, 3, 26, 26);
            graphics.DrawEllipse(border, 3, 3, 26, 26);
            TextRenderer.DrawText(graphics, "?", font, new Rectangle(4, 3, 24, 26), Color.White,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
    }

    private static void DrawBattery(Graphics graphics, int percent, bool charging)
    {
        Color fillColor;
        if (charging)
            fillColor = Color.FromArgb(42, 145, 214);
        else if (percent < 10)
            fillColor = Color.FromArgb(215, 61, 55);
        else if (percent < 30)
            fillColor = Color.FromArgb(223, 172, 0);
        else if (percent < 50)
            fillColor = Color.FromArgb(239, 137, 0);
        else
            fillColor = Color.FromArgb(24, 166, 88);

        using (Brush fill = new SolidBrush(fillColor))
        using (Pen outline = new Pen(Color.White, 2f))
        using (Font font = new Font("Segoe UI", percent >= 100 ? 12f : 16f,
                   FontStyle.Bold, GraphicsUnit.Pixel))
        {
            // At notification-area sizes a classic battery outline is too small to
            // identify. Use a high-contrast percentage bubble instead.
            graphics.FillEllipse(fill, 2, 2, 28, 28);
            graphics.DrawEllipse(outline, 2, 2, 28, 28);
            string text = percent >= 100 ? "99" : percent.ToString();
            TextRenderer.DrawText(graphics, text, font, new Rectangle(3, 3, 26, 26), Color.White,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }

        if (charging)
        {
            Point[] bolt =
            {
                new Point(19, 3), new Point(13, 15), new Point(18, 15),
                new Point(15, 29), new Point(25, 12), new Point(20, 12)
            };
            using (Brush boltBrush = new SolidBrush(Color.White))
                graphics.FillPolygon(boltBrush, bolt);
        }
        else if (percent < 10)
        {
            using (Font font = new Font("Segoe UI", 17f, FontStyle.Bold, GraphicsUnit.Pixel))
                TextRenderer.DrawText(graphics, "!", font, new Rectangle(9, 4, 12, 24), Color.White,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
    }

    private static GraphicsPath RoundedRectangle(Rectangle bounds, int radius)
    {
        GraphicsPath path = new GraphicsPath();
        int diameter = radius * 2;
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}
