using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;

internal static class Program
{
    private const int ErrorMoreData = 234;
    private const int MaxAppName = 255;
    private const int MaxServiceName = 63;

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmStartSession(
        out uint sessionHandle,
        int sessionFlags,
        StringBuilder sessionKey);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmEndSession(uint sessionHandle);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmRegisterResources(
        uint sessionHandle,
        uint fileCount,
        string[] fileNames,
        uint applicationCount,
        RM_UNIQUE_PROCESS[] applications,
        uint serviceCount,
        string[] serviceNames);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmGetList(
        uint sessionHandle,
        out uint processInfoNeeded,
        ref uint processInfoCount,
        [In, Out] RM_PROCESS_INFO[] affectedApps,
        ref uint rebootReasons);

    [StructLayout(LayoutKind.Sequential)]
    private struct RM_UNIQUE_PROCESS
    {
        public int ProcessId;
        public System.Runtime.InteropServices.ComTypes.FILETIME ProcessStartTime;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct RM_PROCESS_INFO
    {
        public RM_UNIQUE_PROCESS Process;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = MaxAppName + 1)]
        public string AppName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = MaxServiceName + 1)]
        public string ServiceShortName;

        public RM_APP_TYPE ApplicationType;
        public uint AppStatus;
        public uint TSSessionId;

        [MarshalAs(UnmanagedType.Bool)]
        public bool Restartable;
    }

    private enum RM_APP_TYPE
    {
        Unknown = 0,
        MainWindow = 1,
        OtherWindow = 2,
        Service = 3,
        Explorer = 4,
        Console = 5,
        Critical = 1000
    }

    private static int Main(string[] args)
    {
        if (args.Length != 1)
        {
            Console.Error.WriteLine("Usage: FileLockCheck.exe <file-path>");
            return 2;
        }

        string path;

        try
        {
            path = Path.GetFullPath(args[0]);

            if (!File.Exists(path))
            {
                Console.Error.WriteLine("File does not exist: " + path);
                return 2;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Invalid file path: " + ex.Message);
            return 2;
        }

        uint sessionHandle;
        var sessionKey = new StringBuilder(33);

        int result = RmStartSession(out sessionHandle, 0, sessionKey);

        if (result != 0)
        {
            Console.Error.WriteLine(
                "RmStartSession failed: " + new Win32Exception(result).Message);
            return 1;
        }

        try
        {
            result = RmRegisterResources(
                sessionHandle,
                1,
                new[] { path },
                0,
                null,
                0,
                null);

            if (result != 0)
            {
                Console.Error.WriteLine(
                    "RmRegisterResources failed: " +
                    new Win32Exception(result).Message);
                return 1;
            }

            RM_PROCESS_INFO[] processes;

            try
            {
                processes = GetProcesses(sessionHandle);
            }
            catch (Win32Exception ex)
            {
                Console.Error.WriteLine(ex.Message);
                return 1;
            }

            if (processes.Length == 0)
            {
                Console.WriteLine(
                    "Restart Manager reported no processes using the file.");
                return 0;
            }

            foreach (RM_PROCESS_INFO info in processes)
            {
                int pid = info.Process.ProcessId;
                Console.Write("PID {0}: ", pid);

                try
                {
                    string commandLine = GetCommandLine(pid);

                    if (!String.IsNullOrEmpty(commandLine))
                    {
                        Console.WriteLine(commandLine);
                    }
                    else
                    {
                        Console.WriteLine(
                            "command line unavailable; " + GetExecutablePath(pid));
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine(
                        "process details unavailable ({0})", ex.Message);
                }
            }

            return 0;
        }
        finally
        {
            RmEndSession(sessionHandle);
        }
    }

    private static RM_PROCESS_INFO[] GetProcesses(uint sessionHandle)
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            uint needed;
            uint count = 0;
            uint rebootReasons = 0;

            int result = RmGetList(
                sessionHandle,
                out needed,
                ref count,
                null,
                ref rebootReasons);

            if (result != ErrorMoreData && result != 0)
            {
                throw new Win32Exception(result, "RmGetList failed.");
            }

            if (needed == 0)
            {
                return new RM_PROCESS_INFO[0];
            }

            count = needed;
            var items = new RM_PROCESS_INFO[(int)count];

            result = RmGetList(
                sessionHandle,
                out needed,
                ref count,
                items,
                ref rebootReasons);

            if (result == 0)
            {
                if (count != (uint)items.Length)
                {
                    Array.Resize(ref items, (int)count);
                }

                return items;
            }

            if (result != ErrorMoreData)
            {
                throw new Win32Exception(result, "RmGetList failed.");
            }
        }

        throw new InvalidOperationException(
            "The process list changed repeatedly while it was being read.");
    }

    private static string GetCommandLine(int pid)
    {
        string query =
            "SELECT CommandLine FROM Win32_Process WHERE ProcessId = " + pid;

        using (var searcher = new ManagementObjectSearcher(
            @"\\.\root\CIMV2",
            query))
        using (ManagementObjectCollection results = searcher.Get())
        {
            foreach (ManagementObject item in results)
            {
                using (item)
                {
                    return item["CommandLine"] as string;
                }
            }
        }

        return null;
    }

    private static string GetExecutablePath(int pid)
    {
        using (Process process = Process.GetProcessById(pid))
        {
            return process.MainModule.FileName;
        }
    }
}
