param(
    [Parameter(Mandatory = $true, Position = 0)]
    [string] $Path
)

$ErrorActionPreference = 'Stop'

try {
    $Path = [System.IO.Path]::GetFullPath($Path)
}
catch {
    Write-Error "Invalid file path: $($_.Exception.Message)"
    exit 2
}

if (-not [System.IO.File]::Exists($Path)) {
    Write-Error "File does not exist: $Path"
    exit 2
}

$source = @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

public sealed class FileLockProcessInfo
{
    public int ProcessId { get; set; }
    public string ApplicationName { get; set; }
}

public static class FileLockRestartManager
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

        public int ApplicationType;
        public uint AppStatus;
        public uint TSSessionId;

        [MarshalAs(UnmanagedType.Bool)]
        public bool Restartable;
    }

    public static FileLockProcessInfo[] GetProcesses(string path)
    {
        uint sessionHandle;
        StringBuilder sessionKey = new StringBuilder(33);

        int result = RmStartSession(out sessionHandle, 0, sessionKey);
        if (result != 0)
            throw new Win32Exception(result, "RmStartSession failed.");

        try
        {
            result = RmRegisterResources(
                sessionHandle,
                1,
                new string[] { path },
                0,
                null,
                0,
                null);

            if (result != 0)
                throw new Win32Exception(result, "RmRegisterResources failed.");

            for (int attempt = 0; attempt < 5; attempt++)
            {
                uint needed;
                uint count = 0;
                uint rebootReasons = 0;

                result = RmGetList(
                    sessionHandle,
                    out needed,
                    ref count,
                    null,
                    ref rebootReasons);

                if (result != ErrorMoreData && result != 0)
                    throw new Win32Exception(result, "RmGetList failed.");

                if (needed == 0)
                    return new FileLockProcessInfo[0];

                count = needed;
                RM_PROCESS_INFO[] nativeItems =
                    new RM_PROCESS_INFO[(int)count];

                result = RmGetList(
                    sessionHandle,
                    out needed,
                    ref count,
                    nativeItems,
                    ref rebootReasons);

                if (result == 0)
                {
                    FileLockProcessInfo[] answer =
                        new FileLockProcessInfo[(int)count];

                    for (int i = 0; i < (int)count; i++)
                    {
                        answer[i] = new FileLockProcessInfo
                        {
                            ProcessId = nativeItems[i].Process.ProcessId,
                            ApplicationName = nativeItems[i].AppName
                        };
                    }

                    return answer;
                }

                if (result != ErrorMoreData)
                    throw new Win32Exception(result, "RmGetList failed.");
            }

            throw new InvalidOperationException(
                "The process list changed repeatedly while it was being read.");
        }
        finally
        {
            RmEndSession(sessionHandle);
        }
    }
}
'@

if (-not ('FileLockRestartManager' -as [type])) {
    Add-Type -TypeDefinition $source -Language CSharp
}

try {
    $processes = [FileLockRestartManager]::GetProcesses($Path)

    if ($processes.Count -eq 0) {
        Write-Output 'Restart Manager reported no processes using the file.'
        exit 0
    }

    foreach ($entry in $processes) {
        $processId = $entry.ProcessId

        try {
            $process = Get-CimInstance `
                -ClassName Win32_Process `
                -Filter "ProcessId = $processId"

            if ($null -ne $process -and
                -not [string]::IsNullOrWhiteSpace($process.CommandLine)) {
                Write-Output (
                    "PID {0}: {1}" -f $processId, $process.CommandLine
                )
            }
            elseif ($null -ne $process -and $process.ExecutablePath) {
                Write-Output (
                    "PID {0}: {1} (command line unavailable)" -f
                    $processId, $process.ExecutablePath
                )
            }
            else {
                Write-Output "PID ${processId}: process details unavailable."
            }
        }
        catch {
            Write-Output (
                "PID {0}: command line unavailable ({1})" -f
                $processId, $_.Exception.Message
            )
        }
    }
}
catch {
    Write-Error $_
    exit 1
}
