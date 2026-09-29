/// Windows Job Objects, so that ending a process also ends every process it
/// started. `Process.Kill(entireProcessTree = true)` misses the children of a
/// Git for Windows (MSYS2) `sh`: they are not in the process tree .NET walks.
module internal FsSemanticTagger.JobObject

open System
open System.ComponentModel
open System.Diagnostics
open System.Runtime.InteropServices
open System.Runtime.Versioning
open Microsoft.Win32.SafeHandles

[<SupportedOSPlatform "windows">]
module private Native =
    /// JOBOBJECT_EXTENDED_LIMIT_INFORMATION, fields in declaration order.
    [<Struct; StructLayout(LayoutKind.Sequential)>]
    type ExtendedLimitInformation =
        val mutable PerProcessUserTimeLimit: int64
        val mutable PerJobUserTimeLimit: int64
        val mutable LimitFlags: uint32
        val mutable MinimumWorkingSetSize: unativeint
        val mutable MaximumWorkingSetSize: unativeint
        val mutable ActiveProcessLimit: uint32
        val mutable Affinity: unativeint
        val mutable PriorityClass: uint32
        val mutable SchedulingClass: uint32
        val mutable ReadOperationCount: uint64
        val mutable WriteOperationCount: uint64
        val mutable OtherOperationCount: uint64
        val mutable ReadTransferCount: uint64
        val mutable WriteTransferCount: uint64
        val mutable OtherTransferCount: uint64
        val mutable ProcessMemoryLimit: unativeint
        val mutable JobMemoryLimit: unativeint
        val mutable PeakProcessMemoryUsed: unativeint
        val mutable PeakJobMemoryUsed: unativeint

    [<Literal>]
    let JobObjectExtendedLimitInformation = 9

    [<Literal>]
    let JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000u

    [<DllImport("kernel32.dll", SetLastError = true)>]
    extern bool CloseHandle(nativeint handle)

    type SafeJobHandle() =
        inherit SafeHandleZeroOrMinusOneIsInvalid(true)
        override this.ReleaseHandle() = CloseHandle this.handle

    [<DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)>]
    extern SafeJobHandle CreateJobObjectW(nativeint attributes, string name)

    [<DllImport("kernel32.dll", SetLastError = true)>]
    extern bool SetInformationJobObject(SafeJobHandle job, int infoClass, ExtendedLimitInformation& info, uint32 length)

    [<DllImport("kernel32.dll", SetLastError = true)>]
    extern bool AssignProcessToJobObject(SafeJobHandle job, SafeProcessHandle proc)

    let check (ok: bool) =
        if not ok then
            raise (Win32Exception(Marshal.GetLastPInvokeError()))

    /// A job that ends every process in it when its last handle closes, with
    /// `p` in it. Nested jobs (Windows 8+) let this work under a CI runner's job.
    let enclose (p: Process) : IDisposable =
        let job = CreateJobObjectW(0n, null)

        try
            check (not job.IsInvalid)
            let mutable info = ExtendedLimitInformation()
            info.LimitFlags <- JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE

            check (
                SetInformationJobObject(
                    job,
                    JobObjectExtendedLimitInformation,
                    &info,
                    uint32 (Marshal.SizeOf<ExtendedLimitInformation>())
                )
            )

            check (AssignProcessToJobObject(job, p.SafeHandle))
            job :> IDisposable
        with _ ->
            job.Dispose()
            p.Kill(entireProcessTree = true)
            reraise ()

/// On Windows, puts the just-started `p` in a job: disposing the result ends `p`
/// and every process it started. A process `p` starts before the join escapes;
/// MSYS2 `sh` spends milliseconds starting up, the join microseconds. Elsewhere
/// a no-op: `Process.Kill(entireProcessTree = true)` reaches every child there.
let enclose (p: Process) : IDisposable =
    if OperatingSystem.IsWindows() then
        Native.enclose p
    else
        { new IDisposable with
            member _.Dispose() = ()
        }
