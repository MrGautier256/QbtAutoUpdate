using QbtAutoUpdate.Core;
using Windows.Networking.Connectivity;

namespace QbtAutoUpdate.Platform;

public static class SystemInfo
{
    /// <summary>
    /// The machine's native architecture. IsWow64Process2 tells the truth even when this process
    /// runs as x64 under emulation on an ARM64 machine.
    /// </summary>
    public static CpuArch NativeArchitecture()
    {
        if (NativeMethods.IsWow64Process2(NativeMethods.GetCurrentProcess(), out _, out ushort native)
            && native == NativeMethods.IMAGE_FILE_MACHINE_ARM64)
        {
            return CpuArch.Arm64;
        }
        return CpuArch.X64;
    }

    /// <summary>
    /// True when Windows reports the internet connection as metered (or roaming, or over its data
    /// limit). Any failure of the WinRT API counts as "not metered": the setting only ever delays updates.
    /// </summary>
    public static bool IsMeteredConnection(ILog log)
    {
        try
        {
            ConnectionProfile? profile = NetworkInformation.GetInternetConnectionProfile();
            if (profile is null)
                return false;

            ConnectionCost cost = profile.GetConnectionCost();
            return cost.NetworkCostType is NetworkCostType.Fixed or NetworkCostType.Variable
                   || cost.Roaming || cost.OverDataLimit;
        }
        catch (Exception ex)
        {
            log.Warn($"Could not read the connection cost: {ex.Message}");
            return false;
        }
    }
}
