using System.Runtime.InteropServices;

namespace Runner.Services;

internal static partial class NativeHardening
{
    private const int PrSetDumpable = 4;

    [LibraryImport("libc", EntryPoint = "prctl", SetLastError = true)]
    private static partial int Prctl(int option, nuint argument2, nuint argument3, nuint argument4, nuint argument5);

    public static void ProtectServiceProcess()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }
        if (Prctl(PrSetDumpable, 0, 0, 0, 0) != 0)
        {
            throw new InvalidOperationException($"Unable to harden runner process (errno {Marshal.GetLastPInvokeError()}).");
        }
    }
}
