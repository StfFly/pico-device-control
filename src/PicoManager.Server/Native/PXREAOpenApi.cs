using System;
using System.Runtime.InteropServices;

namespace PicoManager.Server.Native
{
    public static class PXREAOpenApi
    {
        private const string DllName = "PXREAClientSDK.dll";

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void SDKCallBack(IntPtr context, PXREAClientCallbackType type, int status, IntPtr userData);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int Init(IntPtr context, SDKCallBack callBack, uint mask);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void Deinit();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int GetBattery([MarshalAs(UnmanagedType.LPStr)] string devID);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int GetCurrentApp([MarshalAs(UnmanagedType.LPStr)] string devID);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int GetDeviceModel([MarshalAs(UnmanagedType.LPStr)] string devID);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int GetControllerBattery([MarshalAs(UnmanagedType.LPStr)] string devID, int controllerID);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int GetScreenState([MarshalAs(UnmanagedType.LPStr)] string devID);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int SetScreenState([MarshalAs(UnmanagedType.LPStr)] string devID, int screenState);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int GetDeviceAlias([MarshalAs(UnmanagedType.LPStr)] string devID);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int SetDeviceAlias([MarshalAs(UnmanagedType.LPStr)] string devID, [MarshalAs(UnmanagedType.LPStr)] string alias);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int LockDevice([MarshalAs(UnmanagedType.LPStr)] string devID);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int UnlockDevice([MarshalAs(UnmanagedType.LPStr)] string devID);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int StartControlDevice([MarshalAs(UnmanagedType.LPStr)] string devID);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int StopControlDevice([MarshalAs(UnmanagedType.LPStr)] string devID);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int StartPlayApp([MarshalAs(UnmanagedType.LPStr)] string devID, [MarshalAs(UnmanagedType.LPStr)] string appName);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int StopPlayApp([MarshalAs(UnmanagedType.LPStr)] string devID, [MarshalAs(UnmanagedType.LPStr)] string appName);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int StartPlayVideo([MarshalAs(UnmanagedType.LPStr)] string devID, [MarshalAs(UnmanagedType.LPStr)] string videoPath);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int StopPlayVideo([MarshalAs(UnmanagedType.LPStr)] string devID);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int PauseVideo([MarshalAs(UnmanagedType.LPStr)] string devID);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int ContinueVideo([MarshalAs(UnmanagedType.LPStr)] string devID);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int SeekVideoTime([MarshalAs(UnmanagedType.LPStr)] string devID, int millseconds);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int SetDeviceVolumn([MarshalAs(UnmanagedType.LPStr)] string devID, int vol);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int ShutDownDevice([MarshalAs(UnmanagedType.LPStr)] string devID);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int RestartDevice([MarshalAs(UnmanagedType.LPStr)] string devID);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int RecenterView([MarshalAs(UnmanagedType.LPStr)] string devID);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int StartMonitor(IntPtr devID);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int StopMonitor(IntPtr devID);

        public static int StartMonitor(string devID)
        {
            if (string.IsNullOrEmpty(devID)) return -1;
            IntPtr ptr = Marshal.StringToHGlobalAnsi(devID);
            try { return StartMonitor(ptr); }
            finally { Marshal.FreeHGlobal(ptr); }
        }

        public static int StopMonitor(string devID)
        {
            if (string.IsNullOrEmpty(devID)) return -1;
            IntPtr ptr = Marshal.StringToHGlobalAnsi(devID);
            try { return StopMonitor(ptr); }
            finally { Marshal.FreeHGlobal(ptr); }
        }

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int StartMonitorScreen([MarshalAs(UnmanagedType.LPStr)] string devID, PXREAMonitorQuality quality);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int SetMonitorScreenQuality([MarshalAs(UnmanagedType.LPStr)] string devID, PXREAMonitorQuality quality);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int StopMonitorScreen([MarshalAs(UnmanagedType.LPStr)] string devID);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int StartMonitorStream([MarshalAs(UnmanagedType.LPStr)] string devID);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int StopMonitorStream([MarshalAs(UnmanagedType.LPStr)] string devID);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int DeviceControlJson([MarshalAs(UnmanagedType.LPStr)] string devID, [MarshalAs(UnmanagedType.LPStr)] string parameterJson);
    }
}
