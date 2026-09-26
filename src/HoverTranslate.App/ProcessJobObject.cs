using System.Diagnostics;
using System.Runtime.InteropServices;

namespace HoverTranslate.App;

// Windows KHÔNG tự giết process con khi process cha bị Kill()/Task Manager
// "End Task"/crash - chỉ graceful exit (app.Exit event) mới chạy được code
// dọn dẹp của .NET. Gặp thực tế: ~16 tiến trình translate_server.py mồ côi
// tích tụ sau nhiều lần force-kill app trong lúc dev, mỗi cái vẫn chiếm RAM.
// Người dùng thật cũng có thể gặp y hệt nếu tắt app qua Task Manager thay vì
// tray menu, hoặc app tự crash. Windows Job Object với cờ
// JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE là cách đúng để Windows TỰ giết mọi
// process con bất kể cha chết kiểu gì - không phụ thuộc code .NET có kịp chạy
// dọn dẹp hay không.
internal static class ProcessJobObject
{
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x2000;

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll")]
    private static extern bool SetInformationJobObject(IntPtr hJob, int infoType, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION lpJobObjectInfo, uint cbJobObjectInfoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    // Giữ 1 job DUY NHẤT cho suốt vòng đời app - KHÔNG được đóng handle này
    // sớm (đóng job trước khi app thoát sẽ giết con NGAY LẬP TỨC do cờ
    // kill-on-close). OS tự đóng mọi handle (kể cả job) khi process của mình
    // kết thúc theo bất kỳ cách nào, đúng lúc cần kích hoạt kill-on-close.
    private static readonly IntPtr JobHandle = CreateJob();

    private static IntPtr CreateJob()
    {
        IntPtr job = CreateJobObject(IntPtr.Zero, null);
        if (job == IntPtr.Zero) return IntPtr.Zero;

        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION { LimitFlags = JobObjectLimitKillOnJobClose },
        };
        SetInformationJobObject(job, JobObjectExtendedLimitInformation, ref info, (uint)Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>());
        return job;
    }

    // Gọi ngay sau Process.Start() cho mọi sidecar Python (translate_server.py,
    // stt_server.py) - lỗi ở đây không được phép làm app crash, tính năng
    // chính vẫn phải chạy dù không gán job được (máy cũ/hạn chế quyền...).
    public static void AttachToLifetimeOfThisApp(Process process)
    {
        if (JobHandle == IntPtr.Zero) return;
        try { AssignProcessToJobObject(JobHandle, process.Handle); }
        catch { /* best-effort - không quan trọng bằng việc sidecar vẫn chạy được */ }
    }
}
