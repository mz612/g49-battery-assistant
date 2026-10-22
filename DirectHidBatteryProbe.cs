// Read-only protocol verifier for the G49-PRO receiver.
// The request is the reverse-engineered BatteryLevel command: 08 04 00 ... 49.
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Microsoft.Win32;

internal static class DirectHidBatteryProbe
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

    private static void Main()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        List<string> paths = FindCommandCollectionPaths();
        Console.WriteLine("候选命令端点: " + paths.Count);
        foreach (string path in paths)
        {
            Console.WriteLine(path);
            BatteryReading reading;
            string reason;
            if (TryReadBattery(path, out reading, out reason))
            {
                Console.WriteLine("SUCCESS: {0}% charging={1} voltage={2}mV", reading.Percent,
                    reading.IsCharging ? "yes" : "no", reading.Millivolts);
                return;
            }
            Console.WriteLine("FAILED: " + reason);
        }
        Environment.ExitCode = 1;
    }

    private static List<string> FindCommandCollectionPaths()
    {
        List<string> result = new List<string>();
        const string keyPath = @"SYSTEM\CurrentControlSet\Control\DeviceClasses\{4d1e55b2-f16f-11cf-88cb-001111000030}";
        using (RegistryKey root = Registry.LocalMachine.OpenSubKey(keyPath))
        {
            if (root == null)
                return result;

            foreach (string subkey in root.GetSubKeyNames())
            {
                string lower = subkey.ToLowerInvariant();
                bool matchingReceiver = lower.Contains("vid_3554&pid_f54d") ||
                                        lower.Contains("vid_3554&pid_f54f") ||
                                        lower.Contains("vid_3554&pid_f54c");
                if (!matchingReceiver || !lower.Contains("mi_01&col05"))
                    continue;

                string path = NormalizeDevicePath(subkey);
                if (!result.Contains(path))
                    result.Add(path);
            }
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

    private static bool TryReadBattery(string path, out BatteryReading reading, out string reason)
    {
        reading = new BatteryReading();
        reason = "";
        IntPtr handle = CreateFile(path, GenericRead | GenericWrite, FileShareRead | FileShareWrite,
            IntPtr.Zero, OpenExisting, FileAttributeNormal | FileFlagOverlapped, IntPtr.Zero);
        if (handle == InvalidHandle)
        {
            reason = "打开失败 Win32=" + Marshal.GetLastWin32Error();
            return false;
        }

        try
        {
            // Report 0x08, command 0x04. The final byte is the driver's checksum:
            // (0x55 - sum(bytes 0..15)) & 0xff = 0x49.
            byte[] query = new byte[17];
            query[0] = 0x08;
            query[1] = 0x04;
            query[16] = CalculateChecksum(query);

            long deadline = Environment.TickCount + 1800;
            byte[] response;
            if (!TryReadFirstFrameAfterSending(handle, query, 800, out response, out reason))
                return false;
            if (TryParseBatteryFrame(response, out reading))
                return true;

            while (Environment.TickCount < deadline)
            {
                int waitMs = (int)Math.Max(1, deadline - Environment.TickCount);
                if (!TryReadFrame(handle, waitMs, out response, out reason))
                    return false;

                if (TryParseBatteryFrame(response, out reading))
                    return true;
            }

            reason = "未收到有效电量回包";
            return false;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    // Register the input read before writing. Some receivers reply quickly enough that
    // sending first can lose the response to another HID consumer.
    private static bool TryReadFirstFrameAfterSending(IntPtr handle, byte[] query, int timeoutMs,
        out byte[] response, out string reason)
    {
        response = null;
        reason = "";
        IntPtr signal = CreateEvent(IntPtr.Zero, true, false, null);
        if (signal == IntPtr.Zero || signal == InvalidHandle)
        {
            reason = "创建读取事件失败 Win32=" + Marshal.GetLastWin32Error();
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
                reason = "启动读取失败 Win32=" + Marshal.GetLastWin32Error();
                return false;
            }

            // The receiver needs a short interval to register its pending read.
            System.Threading.Thread.Sleep(15);

            uint written;
            bool sent = WriteFile(handle, query, (uint)query.Length, out written, IntPtr.Zero);
            int writeError = sent ? 0 : Marshal.GetLastWin32Error();
            if (!sent || written != query.Length)
            {
                sent = HidD_SetOutputReport(handle, query, (uint)query.Length);
                if (!sent)
                {
                    CancelIoEx(handle, IntPtr.Zero);
                    reason = "发送查询失败 WriteFile=" + writeError + " HidD=" + Marshal.GetLastWin32Error();
                    return false;
                }
            }

            if (completed)
            {
                response = Trim(buffer, (int)read);
                return true;
            }

            uint wait = WaitForSingleObject(signal, (uint)timeoutMs);
            if (wait == WaitTimeout)
            {
                CancelIoEx(handle, IntPtr.Zero);
                reason = "等待回包超时";
                return false;
            }
            if (wait != WaitObject0)
            {
                reason = "等待回包失败 Win32=" + Marshal.GetLastWin32Error();
                return false;
            }
            if (!GetOverlappedResult(handle, ref overlapped, out read, false))
            {
                reason = "读取回包失败 Win32=" + Marshal.GetLastWin32Error();
                return false;
            }
            response = Trim(buffer, (int)read);
            return true;
        }
        finally
        {
            CloseHandle(signal);
        }
    }

    private static bool TryReadFrame(IntPtr handle, int timeoutMs, out byte[] response, out string reason)
    {
        response = null;
        reason = "";
        IntPtr signal = CreateEvent(IntPtr.Zero, true, false, null);
        if (signal == IntPtr.Zero || signal == InvalidHandle)
        {
            reason = "创建读取事件失败 Win32=" + Marshal.GetLastWin32Error();
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

            int error = Marshal.GetLastWin32Error();
            if (error != ErrorIoPending)
            {
                reason = "启动读取失败 Win32=" + error;
                return false;
            }

            uint wait = WaitForSingleObject(signal, (uint)timeoutMs);
            if (wait == WaitTimeout)
            {
                CancelIoEx(handle, IntPtr.Zero);
                reason = "等待回包超时";
                return false;
            }
            if (wait != WaitObject0)
            {
                reason = "等待回包失败 Win32=" + Marshal.GetLastWin32Error();
                return false;
            }
            if (!GetOverlappedResult(handle, ref overlapped, out read, false))
            {
                reason = "读取回包失败 Win32=" + Marshal.GetLastWin32Error();
                return false;
            }
            response = Trim(buffer, (int)read);
            return true;
        }
        finally
        {
            CloseHandle(signal);
        }
    }

    private static byte[] Trim(byte[] input, int length)
    {
        length = Math.Max(0, Math.Min(length, input.Length));
        byte[] result = new byte[length];
        Array.Copy(input, result, length);
        return result;
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
        if (frame == null || frame.Length < 10 || frame[0] != 0x08 || frame[1] != 0x04)
            return false;

        Console.WriteLine("回包: " + BitConverter.ToString(frame));

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

    private struct BatteryReading
    {
        public byte Percent;
        public bool IsCharging;
        public ushort Millivolts;
    }
}
