using System.Diagnostics;
using System.Runtime.InteropServices;
using DeepSeekTranslator.Interop;

namespace DeepSeekTranslator.Services;

public static class ElevationHelper
{
    public static bool IsCurrentProcessElevated()
    {
        return IsProcessElevated((uint)Environment.ProcessId);
    }

    public static bool IsProcessElevated(uint processId)
    {
        var handle = NativeMethods.OpenProcess(
            NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION,
            false,
            processId);
        if (handle == IntPtr.Zero)
        {
            return false;
        }

        var token = IntPtr.Zero;
        try
        {
            if (!NativeMethods.OpenProcessToken(handle, NativeMethods.TOKEN_QUERY, out token))
            {
                return false;
            }

            var size = (uint)Marshal.SizeOf<NativeMethods.TOKEN_ELEVATION>();
            if (!NativeMethods.GetTokenInformation(
                    token,
                    NativeMethods.TokenElevation,
                    out var elevation,
                    size,
                    out _))
            {
                return false;
            }

            return elevation.TokenIsElevated != 0;
        }
        catch (Exception ex)
        {
            Logger.Error("检测进程提权状态失败", ex);
            return false;
        }
        finally
        {
            if (token != IntPtr.Zero) NativeMethods.CloseHandle(token);
            NativeMethods.CloseHandle(handle);
        }
    }

    public static string GetProcessName(uint processId)
    {
        try
        {
            using var process = Process.GetProcessById((int)processId);
            return process.ProcessName;
        }
        catch
        {
            return "未知应用";
        }
    }
}
