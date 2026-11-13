using System.Runtime.InteropServices;

namespace SRM.Core.Native;

internal static class SetupApi
{
    internal static readonly Guid NetworkClassGuid = new("4d36e972-e325-11ce-bfc1-08002be10318");

    internal const uint DigcfPresent = 0x00000002;
    internal const uint SpdrpDeviceDesc = 0x00000000;
    internal const uint SpdrpFriendlyName = 0x0000000C;
    internal const uint DicsEnable = 0x00000001;
    internal const uint DicsDisable = 0x00000002;
    internal const uint DicsFlagGlobal = 0x00000001;
    internal const uint DiregDev = 0x00000001;
    internal const uint DiregDrv = 0x00000002;
    internal const uint KeyRead = 0x00020019;
    internal const uint DnHasProblem = 0x00000400;
    internal const uint CmProbDisabled = 22;
    internal const uint NcfHidden = 0x00000008;

    [StructLayout(LayoutKind.Sequential)]
    internal struct SpDevInfoData
    {
        public uint cbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public IntPtr Reserved;
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, IntPtr enumerator, IntPtr hwndParent, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    internal static extern bool SetupDiEnumDeviceInfo(IntPtr deviceInfoSet, uint memberIndex, ref SpDevInfoData deviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true)]
    internal static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern bool SetupDiGetDeviceRegistryProperty(IntPtr deviceInfoSet, ref SpDevInfoData deviceInfoData, uint property, out uint propertyRegDataType, byte[]? propertyBuffer, uint propertyBufferSize, out uint requiredSize);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern bool SetupDiGetDeviceInstanceId(IntPtr deviceInfoSet, ref SpDevInfoData deviceInfoData, System.Text.StringBuilder deviceInstanceId, uint deviceInstanceIdSize, out uint requiredSize);

    [DllImport("setupapi.dll", SetLastError = true)]
    internal static extern bool SetupDiChangeState(IntPtr deviceInfoSet, ref SpDevInfoData deviceInfoData, uint state);

    [DllImport("setupapi.dll", SetLastError = true)]
    internal static extern IntPtr SetupDiOpenDevRegKey(IntPtr deviceInfoSet, ref SpDevInfoData deviceInfoData, uint scope, uint hwProfile, uint keyType, uint samDesired);

    [DllImport("cfgmgr32.dll")]
    internal static extern uint CM_Get_DevNode_Status(out uint status, out uint problemNumber, uint devInst, uint flags);

    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    internal static extern uint ConvertInterfaceGuidToLuid(ref Guid interfaceGuid, out ulong luid);

    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    internal static extern uint ConvertInterfaceLuidToIndex(ref ulong luid, out uint interfaceIndex);
}
