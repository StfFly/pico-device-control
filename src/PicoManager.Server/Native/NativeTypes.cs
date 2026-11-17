using System;
using System.Runtime.InteropServices;

namespace PicoManager.Server.Native
{
    public enum PXREAClientCallbackType : uint
    {
        PXREAFrameCaptureStart = 1,
        PXREAFrameCaptureStop = 1 << 1,
        PXREAServerConnect = 1 << 2,
        PXREAServerDisconnect = 1 << 3,
        PXREADeviceFind = 1 << 4,
        PXREADeviceMissing = 1 << 5,
        PXREADeviceBattery = 1 << 6,
        PXREADeviceLock = 1 << 7,
        PXREADeviceUnlock = 1 << 8,
        PXREADeviceConnect = 1 << 9,
        PXREADeviceSensor = 1 << 10,
        PXREADeviceShutdown = 1 << 11,
        PXREADeviceResume = 1 << 12,
        PXREADeviceMonitorParameter = 1 << 13,
        PXREADeviceMonitor = 1 << 14,
        PXREADeviceModel = 1 << 15,
        PXREACurrentApplication = 1 << 16,
        PXREAControllerBattery = 1 << 17,
        PXREAScreenState = 1 << 18,
        PXREAVideoControlResult = 1 << 19,
        PXREADeviceScreenMonitorParameter = 1 << 20,
        PXREADeviceScreenMonitor = 1 << 21,
        PXREADeviceStreamMonitorParameter = 1 << 22,
        PXREADeviceStreamMonitor = 1 << 23,
        PXREADeviceAlias = 1 << 24,
        PXREADeviceStateJson = 1 << 25,
        PXREADeviceCustomMessage = 1 << 26,
        PXREAFullMask = 0xffffffff
    }

    public enum PXREAMonitorQuality
    {
        Quality480P = 0,
        Quality720P = 1,
        Quality1080P = 2,
        Quality2K = 3,
        Quality360p = 4,
        QualityUserConfig = 1000
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    public struct PXREADevBattery
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string devID;
        public uint battery;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    public struct PXREACurrentApp
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string devID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string appName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    public struct PXREADevModel
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string devID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string devModel;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    public struct PXREACtrllerBattery
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string devID;
        public int controllerID; // 0: 左手柄, 1: 右手柄
        public uint battery;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    public struct PXREAFrameBlob
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string devID;
        public IntPtr addr;
        public uint size;
        public uint width;
        public uint height;
        public uint bytesPerLine;
        public int pixFormat; // 18: Format_YUV420P, 4: Format_BGRA8888
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8, CharSet = CharSet.Ansi)]
    public struct PXREAFrameBlobMultiplyMonitor
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string devID;
        public int isScreen;
        public IntPtr addr;
        public uint size;
        public uint width;
        public uint height;
        public uint bytesPerLine;
        public int pixFormat; // 13: YUV420P, 4: BGRA8888
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    public struct PXREAMonitorPara
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string devID;
        public uint width;
        public uint height;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    public struct PXREADevAlias
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string devID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string alias;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    public struct PXREADevStateJson
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string devID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string stateJson;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    public struct PXREAVideoCtrlResult
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string devID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string action;
        public int result;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string detail;
        public int errorcode;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    public struct PXREADevCustomMessage
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string devID;
        public ulong dataSize;
        public IntPtr dataPtr;
    }
}
